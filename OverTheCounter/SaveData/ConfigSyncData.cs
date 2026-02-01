using Il2CppSteamworks;
using MelonLoader;
using OverTheCounter.Utilities;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using SteamNetworkLib;
using SteamNetworkLib.Sync;
using System;
using System.Collections.Generic;

namespace OverTheCounter.SaveData
{
    public class ConfigSyncData : Saveable
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("ConfigSync");

        [SaveableField("config_sync_payload")]
        private string _payload = "";

        // Cached so SaveData instances created later can pull pending state.
        // Static so it persists even if ConfigSyncData.Instance hasn't been created yet.
        private static Dictionary<string, string> _pendingGameState;

        private static bool _networkInitialized;

        // Lobby member data for client→host quest action sync.
        private static CSteamID _lobbyId;
        private static Callback<LobbyDataUpdate_t> _lobbyDataCallback;
        private static int _actionSeq;
        private static readonly Dictionary<ulong, string> _processedActions = new Dictionary<ulong, string>();

        public static ConfigSyncData Instance { get; private set; }
        public static SteamNetworkClient NetworkClient { get; set; }
        public static HostSyncVar<string> ConfigVar { get; private set; }
        public static HostSyncVar<string> StateVar { get; private set; }

        public ConfigSyncData()
        {
            Instance = this;
        }

        protected override void OnLoaded()
        {
            Instance = this;

            if (NetworkHelper.IsHost)
            {
                _payload = Config.SerializeAll();
                if (ConfigVar != null)
                    ConfigVar.Value = _payload;
                if (StateVar != null)
                    StateVar.Value = SerializeGameState();
                Logger.Msg("Host config and game state published via SyncVars.");
            }
            else if (!string.IsNullOrEmpty(_payload))
            {
                // Saveable fallback for config (SyncVar may have applied already)
                var data = ParsePayload(_payload);
                Config.ApplyOverrides(data);
                Logger.Msg($"Client applied {data.Count} config overrides from saveable fallback.");
            }
        }

        /// <summary>
        /// Initializes SteamNetworkClient and creates SyncVars. Called from
        /// Core.OnLateUpdate() so it runs on BOTH host and client regardless
        /// of whether the Saveable lifecycle fires.
        /// </summary>
        public static void EnsureNetworkReady()
        {
            if (_networkInitialized) return;
            _networkInitialized = true;

            if (NetworkClient == null)
            {
                try
                {
                    var client = new SteamNetworkClient();
                    if (client.Initialize())
                    {
                        NetworkClient = client;
                        Logger.Msg("SteamNetworkClient initialized.");
                    }
                    else
                    {
                        Logger.Warning("SteamNetworkClient.Initialize() returned false.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"SteamNetworkClient init failed: {ex.Message}");
                    return;
                }
            }

            try
            {
                ConfigVar = NetworkClient.CreateHostSyncVar<string>("otc_cfg", "");
                StateVar = NetworkClient.CreateHostSyncVar<string>("otc_state", "");

                if (!NetworkHelper.IsHost)
                {
                    ConfigVar.OnValueChanged += OnConfigVarChanged;
                    StateVar.OnValueChanged += OnStateVarChanged;

                    // Read initial state immediately if the host already published
                    string statePayload = StateVar.Value;
                    if (!string.IsNullOrEmpty(statePayload))
                    {
                        var state = ParsePayload(statePayload);
                        _pendingGameState = state;
                        ApplyGameState(state);
                        Logger.Msg($"Client applied {state.Count} game state values on network init.");
                    }

                    string cfgPayload = ConfigVar.Value;
                    if (!string.IsNullOrEmpty(cfgPayload))
                    {
                        var data = ParsePayload(cfgPayload);
                        Config.ApplyOverrides(data);
                        Logger.Msg($"Client applied {data.Count} config overrides on network init.");
                    }
                }

                _lobbyDataCallback = Callback<LobbyDataUpdate_t>.Create(
                    new System.Action<LobbyDataUpdate_t>(OnLobbyDataUpdate));

                ConfigVar.OnSyncError += ex =>
                    Logger.Warning($"Config SyncVar error: {ex.Message}");
                StateVar.OnSyncError += ex =>
                    Logger.Warning($"State SyncVar error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to create SyncVars: {ex.Message}");
            }
        }

        private static void OnConfigVarChanged(string oldVal, string newVal)
        {
            if (string.IsNullOrEmpty(newVal)) return;
            try
            {
                var data = ParsePayload(newVal);
                Config.ApplyOverrides(data);
                Logger.Msg($"Applied {data.Count} config overrides (live sync).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to apply synced config: {ex.Message}");
            }
        }

        private static void OnStateVarChanged(string oldVal, string newVal)
        {
            if (string.IsNullOrEmpty(newVal)) return;
            try
            {
                var state = ParsePayload(newVal);
                _pendingGameState = state;
                ApplyGameState(state);
                Logger.Msg($"Applied {state.Count} game state values from host.");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to apply game state: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by host after config changes (e.g. ModsApp Apply postfix).
        /// Updates the saveable payload and pushes to SyncVar for connected clients.
        /// </summary>
        public void RefreshFromConfig()
        {
            if (!NetworkHelper.IsHost) return;
            _payload = Config.SerializeAll();

            if (ConfigVar != null)
                ConfigVar.Value = _payload;
        }

        /// <summary>
        /// Called by host when quest/game state changes (e.g. ATM threshold met,
        /// Vic intro triggered). Serializes key flags and pushes to clients.
        /// </summary>
        public void PublishGameState()
        {
            if (!NetworkHelper.IsHost) return;
            if (StateVar == null) return;

            try
            {
                StateVar.Value = SerializeGameState();
            }
            catch (Exception ex)
            {
                Logger.Warning($"PublishGameState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a quest action to the host via Steam lobby member data.
        /// No-ops on the host (host executes state changes directly).
        /// Uses a sequence counter so repeated actions (e.g. VIC_LAUNDER)
        /// are not deduplicated away.
        /// </summary>
        public static void SendQuestAction(string action)
        {
            if (NetworkHelper.IsHost) return;

            try
            {
                if (!_lobbyId.IsValid())
                {
                    Logger.Warning($"SendQuestAction({action}): No lobby ID captured yet.");
                    return;
                }

                string value = $"{++_actionSeq}:{action}";
                SteamMatchmaking.SetLobbyMemberData(_lobbyId, "otc_action", value);
                Logger.Msg($"Sent quest action via lobby member data: {value}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"SendQuestAction({action}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Steam callback for lobby data updates. Captures the lobby ID on first
        /// fire, and on the host, reads client member data for quest actions.
        /// </summary>
        private static void OnLobbyDataUpdate(LobbyDataUpdate_t data)
        {
            // Capture lobby ID from any lobby data update.
            if (!_lobbyId.IsValid() && data.m_ulSteamIDLobby != 0)
            {
                _lobbyId = new CSteamID(data.m_ulSteamIDLobby);
                Logger.Msg($"Captured lobby ID: {_lobbyId}");
            }

            // Only the host processes quest actions from clients.
            if (!NetworkHelper.IsHost) return;

            // m_ulSteamIDMember == m_ulSteamIDLobby means lobby-level data changed, not member data.
            if (data.m_ulSteamIDMember == data.m_ulSteamIDLobby) return;

            // Ignore our own member data changes.
            ulong senderId = data.m_ulSteamIDMember;
            CSteamID senderSteamId = new CSteamID(senderId);
            CSteamID localId = SteamUser.GetSteamID();
            if (senderId == localId.m_SteamID) return;

            try
            {
                string raw = SteamMatchmaking.GetLobbyMemberData(
                    new CSteamID(data.m_ulSteamIDLobby), senderSteamId, "otc_action");

                if (string.IsNullOrEmpty(raw)) return;

                // Deduplicate: track last processed value per sender.
                if (_processedActions.TryGetValue(senderId, out string last) && last == raw)
                    return;
                _processedActions[senderId] = raw;

                // Parse "seq:ACTION_NAME"
                int colonIdx = raw.IndexOf(':');
                if (colonIdx <= 0 || colonIdx >= raw.Length - 1) return;
                string action = raw.Substring(colonIdx + 1);

                Logger.Msg($"Host received quest action '{action}' from {senderSteamId}");
                ProcessQuestAction(action);
            }
            catch (Exception ex)
            {
                Logger.Warning($"OnLobbyDataUpdate quest action failed: {ex.Message}");
            }
        }

        private static void ProcessQuestAction(string action)
        {
            switch (action)
            {
                case "STATIC_ATM_TRIGGERED":
                case "STATIC_INTRO_COMPLETED":
                case "STATIC_PURCHASE_INITIAL":
                case "STATIC_PURCHASE_UPGRADE":
                case "STATIC_REACTIVATE":
                case "STATIC_CANCEL":
                    StaticSaveData.Instance?.HandleRemoteAction(action);
                    break;

                case "VIC_QUEST_ACCEPTED":
                case "VIC_QUEST_COMPLETE":
                case "VIC_LAUNDER":
                    VicSaveData.Instance?.HandleRemoteAction(action);
                    break;

                default:
                    Logger.Warning($"Unknown quest action: {action}");
                    break;
            }
        }

        /// <summary>
        /// Called by SaveData instances when they're created on the client
        /// (e.g. fallback creation in NPC.OnCreated). Applies any cached
        /// host game state that arrived before the SaveData existed.
        /// </summary>
        public static void ApplyPendingGameState()
        {
            if (_pendingGameState == null || _pendingGameState.Count == 0) return;
            ApplyGameState(_pendingGameState);
            Logger.Msg("Applied pending game state to newly created SaveData.");
        }

        private static string SerializeGameState()
        {
            var parts = new List<string>();

            if (StaticSaveData.Instance != null)
            {
                parts.Add($"static_triggered={BoolToStr(StaticSaveData.Instance.QuestTriggered)}");
                parts.Add($"static_intro={BoolToStr(StaticSaveData.Instance.IntroCompleted)}");
                parts.Add($"static_tier={StaticSaveData.Instance.CrmTier}");
                parts.Add($"static_saas={BoolToStr(StaticSaveData.Instance.SaasActive)}");
                parts.Add($"static_upgrade={BoolToStr(StaticSaveData.Instance.UpgradeAvailable)}");
                parts.Add($"static_next_payment={StaticSaveData.Instance.SaasNextPaymentDay}");
                parts.Add($"static_day_pass={StaticSaveData.Instance.DayPassCount}");
            }

            if (VicSaveData.Instance != null)
            {
                parts.Add($"vic_texted={BoolToStr(VicSaveData.Instance.HasBeenTexted)}");
                parts.Add($"vic_accepted={BoolToStr(VicSaveData.Instance.QuestAccepted)}");
                parts.Add($"vic_unlocked={BoolToStr(VicSaveData.Instance.Unlocked)}");
                parts.Add($"vic_trust={VicSaveData.Instance.TrustLevel}");
            }

            return string.Join("|", parts);
        }

        private static void ApplyGameState(Dictionary<string, string> state)
        {
            if (StaticSaveData.Instance != null)
            {
                bool triggered = state.TryGetValue("static_triggered", out var t) && StrToBool(t);
                bool intro = state.TryGetValue("static_intro", out var i) && StrToBool(i);
                int tier = state.TryGetValue("static_tier", out var tierStr) && int.TryParse(tierStr, out var tierVal) ? tierVal : -1;
                bool? saas = state.TryGetValue("static_saas", out var s) ? StrToBool(s) : (bool?)null;
                bool? upgrade = state.TryGetValue("static_upgrade", out var u) ? StrToBool(u) : (bool?)null;
                int nextPayment = state.TryGetValue("static_next_payment", out var npStr) && int.TryParse(npStr, out var npVal) ? npVal : -1;
                int dayPass = state.TryGetValue("static_day_pass", out var dpStr) && int.TryParse(dpStr, out var dpVal) ? dpVal : -1;

                StaticSaveData.Instance.ApplyHostState(
                    questTriggered: triggered,
                    introCompleted: intro,
                    crmTier: tier,
                    saasActive: saas,
                    upgradeAvailable: upgrade,
                    saasNextPaymentDay: nextPayment,
                    dayPassCount: dayPass);
            }

            if (VicSaveData.Instance != null)
            {
                bool texted = state.TryGetValue("vic_texted", out var vt) && StrToBool(vt);
                bool questAccepted = state.TryGetValue("vic_accepted", out var va) && StrToBool(va);
                bool unlocked = state.TryGetValue("vic_unlocked", out var vu) && StrToBool(vu);
                int trust = state.TryGetValue("vic_trust", out var vtrust) && int.TryParse(vtrust, out var trustVal) ? trustVal : -1;

                VicSaveData.Instance.ApplyHostState(
                    hasBeenTexted: texted,
                    questAccepted: questAccepted,
                    unlocked: unlocked,
                    trustLevel: trust);
            }
        }

        private static string BoolToStr(bool v) => v ? "1" : "0";
        private static bool StrToBool(string v) => v == "1";

        private static Dictionary<string, string> ParsePayload(string payload)
        {
            var result = new Dictionary<string, string>();
            var pairs = payload.Split('|');

            foreach (var pair in pairs)
            {
                if (string.IsNullOrEmpty(pair)) continue;

                int eqIndex = pair.IndexOf('=');
                if (eqIndex <= 0 || eqIndex >= pair.Length - 1) continue;

                string key = pair.Substring(0, eqIndex);
                string value = pair.Substring(eqIndex + 1);
                result[key] = value;
            }

            return result;
        }
    }
}
