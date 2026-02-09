using Il2CppSteamworks;
using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.Quests;
using OverTheCounter.Utilities;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using SteamNetworkLib;
using SteamNetworkLib.Sync;
using System;
using System.Collections.Generic;

namespace OverTheCounter.SaveData
{
    /// <summary>
    /// Pass-through serializer that avoids JSON encoding. SteamNetworkLib's
    /// default JsonSyncSerializer wraps strings in quotes which get lost in
    /// the Steam lobby data round-trip (IL2CPP marshaling), causing
    /// "Invalid JSON string format" on deserialization. Since our SyncVars
    /// only carry pipe-delimited strings we serialize ourselves, JSON adds
    /// no value — raw pass-through is correct.
    /// </summary>
    internal class RawStringSerializer : ISyncSerializer
    {
        public string Serialize<T>(T value) => value?.ToString() ?? "";
        public T Deserialize<T>(string data) => (T)(object)(data ?? "");
        public bool CanSerialize(Type type) => type == typeof(string);
    }

    public class ConfigSyncData : Saveable
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("ConfigSync");

        [SaveableField("config_sync_payload")]
        private string _payload = "";

        // Cached so SaveData instances created later can pull pending state.
        // Static so it persists even if ConfigSyncData.Instance hasn't been created yet.
        private static Dictionary<string, string> _pendingGameState;

        private static bool _networkInitialized;
        private static bool _initialSyncDone;

        // SteamNetworkLib client and SyncVars.
        private static SteamNetworkClient _netClient;
        private static HostSyncVar<string> _configVar;    // Host → Client: pipe-delimited config
        private static HostSyncVar<string> _stateVar;     // Host → Client: pipe-delimited state
        private static HostSyncVar<string> _drifterVar;   // Host → Client: drifter state (separate to avoid lobby data truncation)
        private static HostSyncVar<string> _managerVar;  // Host → Client: manager state (separate to avoid lobby data truncation)
        private static ClientSyncVar<string> _actionVar;  // Client → Host: "seq:ACTION"

        private static readonly NetworkSyncOptions _syncOptions = new NetworkSyncOptions
        {
            KeyPrefix = "OTC_",
            Serializer = new RawStringSerializer()
        };

        // Lobby member data for client→host quest action sync.
        private static int _actionSeq;
        private static readonly Dictionary<ulong, string> _processedActions = new Dictionary<ulong, string>();

        // Fallback polling for ClientSyncVar: LobbyDataUpdate_t may not fire for
        // member data changes in IL2CPP. Host polls Refresh() periodically instead.
        private static long _lastActionPollTick;
        private const long ACTION_POLL_INTERVAL_MS = 1000;

        public static ConfigSyncData Instance { get; private set; }

        public ConfigSyncData()
        {
            Instance = this;
        }

        protected override void OnLoaded()
        {
            Instance = this;
            _pendingGameState = null; // Prevent stale state from previous save

            // The save file payload contains config from the last save. Don't
            // apply it as overrides here — the host's authority is its current
            // MelonPreferences, and applying stale save data would mask any
            // config changes the user made since then. Clients receive the
            // host's live config via the SyncVar callback (OnConfigChanged).
            Config.ClearAllOverrides();

            // Push current config + state via SyncVar. On the host this
            // publishes the fresh config to clients. On the client the
            // HostSyncVar silently ignores the write (not lobby owner).
            if (_configVar != null)
            {
                _configVar.Value = Config.SerializeAll();
                _stateVar.Value = SerializeGameState();
                if (_drifterVar != null)
                    _drifterVar.Value = DrifterManager.Instance?.SerializeDrifterState() ?? "";
                if (_managerVar != null)
                    _managerVar.Value = SerializeManagerSyncVar();
                Logger.Msg("Pushed config, game state, drifter state, and manager state to SyncVars.");
            }
        }

        /// <summary>
        /// Initializes SteamNetworkClient and SyncVars. Called from Core.OnLateUpdate()
        /// so it runs on BOTH host and client regardless of whether the
        /// Saveable lifecycle fires.
        /// </summary>
        public static void EnsureNetworkReady()
        {
            if (_networkInitialized) return;
            _networkInitialized = true;

            try
            {
                _netClient = new SteamNetworkClient();
                if (!_netClient.Initialize())
                {
                    Logger.Warning("SteamNetworkClient.Initialize() returned false (single-player?).");
                    _netClient = null;
                    return;
                }

                _configVar = _netClient.CreateHostSyncVar("cfg", "", _syncOptions);
                _stateVar = _netClient.CreateHostSyncVar("state", "", _syncOptions);
                _drifterVar = _netClient.CreateHostSyncVar("drifters", "", _syncOptions);
                _managerVar = _netClient.CreateHostSyncVar("managers", "", _syncOptions);
                _actionVar = _netClient.CreateClientSyncVar("action", "", _syncOptions);

                // Diagnostic error handlers — surface silent SyncVar failures.
                _configVar.OnSyncError += (ex) => Logger.Warning($"Config SyncVar error: {ex.Message}");
                _stateVar.OnSyncError += (ex) => Logger.Warning($"State SyncVar error: {ex.Message}");
                _drifterVar.OnSyncError += (ex) => Logger.Warning($"Drifter SyncVar error: {ex.Message}");
                _managerVar.OnSyncError += (ex) => Logger.Warning($"Manager SyncVar error: {ex.Message}");
                _actionVar.OnSyncError += (ex) => Logger.Warning($"Action SyncVar error: {ex.Message}");
                _configVar.OnWriteIgnored += (_) => Logger.Warning("Config SyncVar write ignored (not lobby owner).");
                _stateVar.OnWriteIgnored += (_) => Logger.Warning("State SyncVar write ignored (not lobby owner).");

                // Client callbacks: receive config and state from host.
                _configVar.OnValueChanged += OnConfigChanged;
                _stateVar.OnValueChanged += OnStateChanged;
                _drifterVar.OnValueChanged += OnDrifterStateChanged;
                _managerVar.OnValueChanged += OnManagerStateChanged;

                // Host callback: receive quest actions from clients.
                _actionVar.OnValueChanged += OnActionChanged;

                // NOTE: Initial value push is deferred to ProcessMessages() after lobby
                // discovery. SyncVar writes before _currentLobby is set fail silently.

                Logger.Msg($"SteamNetworkLib SyncVars initialized (inLobby={_netClient.IsInLobby}).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to initialize SteamNetworkLib: {ex.Message}");
                _netClient = null;
            }
        }

        /// <summary>
        /// Processes incoming SyncVar messages (both host and client).
        /// Called from Core.OnLateUpdate() every frame.
        ///
        /// On the first frame where IsInLobby becomes true (after LobbyEnter_t
        /// is delivered by ProcessIncomingMessages), pushes initial values AND
        /// refreshes from lobby data. We do BOTH unconditionally because
        /// NetworkHelper.IsHost (FishNet) is not yet reliable this early —
        /// HostSyncVar silently ignores non-host writes internally, so the
        /// push is safe on clients (no-op), and refresh is safe on the host
        /// (reads own values).
        /// </summary>
        public static void ProcessMessages()
        {
            if (_netClient == null) return;
            _netClient.ProcessIncomingMessages();

            if (!_initialSyncDone && _netClient.IsInLobby)
            {
                _initialSyncDone = true;
                try
                {
                    // OnLoaded() applies save-file config as overrides before we know
                    // the network role. On the host those overrides are stale — the
                    // host's authority is its own MelonPreferences, not the save file.
                    if (_netClient.IsHost)
                        Config.ClearAllOverrides();

                    string cfg = Config.SerializeAll();
                    if (!string.IsNullOrEmpty(cfg) && _configVar != null)
                        _configVar.Value = cfg;

                    string state = SerializeGameState();
                    if (!string.IsNullOrEmpty(state) && _stateVar != null)
                        _stateVar.Value = state;

                    string drifterState = DrifterManager.Instance?.SerializeDrifterState() ?? "";
                    if (_drifterVar != null)
                        _drifterVar.Value = drifterState;

                    string managerState = SerializeManagerSyncVar();
                    if (_managerVar != null)
                        _managerVar.Value = managerState;

                    // Refresh — picks up values already in lobby data (covers
                    // client reading host values, and host reading its own on rejoin).
                    _configVar?.Refresh();
                    _stateVar?.Refresh();
                    _drifterVar?.Refresh();
                    _managerVar?.Refresh();
                    _actionVar?.Refresh();

                    Logger.Msg($"Initial SyncVar sync after lobby discovery (lobbyHost={_netClient.IsHost}).");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Post-lobby-discovery sync failed: {ex.Message}");
                }
            }

            // Fallback polling for ClientSyncVar on the host. LobbyDataUpdate_t
            // callbacks fire reliably for lobby data (HostSyncVar) in IL2CPP but
            // may not fire for member data (ClientSyncVar). Refresh() clears the
            // internal cache; GetAllValues() then re-reads fresh member data from
            // Steam for each lobby member. We process the values explicitly here
            // since Refresh() does NOT fire OnValueChanged callbacks.
            if (_netClient.IsHost && _actionVar != null && _initialSyncDone)
            {
                long now = Environment.TickCount64;
                if (now - _lastActionPollTick >= ACTION_POLL_INTERVAL_MS)
                {
                    _lastActionPollTick = now;
                    try
                    {
                        _actionVar.Refresh();
                        var allValues = _actionVar.GetAllValues();
                        foreach (var kvp in allValues)
                        {
                            if (kvp.Key.m_SteamID == _netClient.LocalPlayerId.m_SteamID) continue;
                            string val = kvp.Value;
                            if (string.IsNullOrEmpty(val)) continue;

                            // Deduplicate: skip if we already processed this exact value from this sender.
                            if (_processedActions.TryGetValue(kvp.Key.m_SteamID, out string last) && last == val)
                                continue;
                            _processedActions[kvp.Key.m_SteamID] = val;

                            // Parse "seq:ACTION_NAME"
                            int colonIdx = val.IndexOf(':');
                            if (colonIdx <= 0 || colonIdx >= val.Length - 1) continue;
                            string action = val.Substring(colonIdx + 1);

                            Logger.Msg($"Host received quest action '{action}' from {kvp.Key} (polled)");
                            ProcessQuestAction(action);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Action poll failed: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Disposes the SteamNetworkClient and resets all SyncVar references.
        /// Called from Core.OnDeinitializeMelon().
        /// </summary>
        public static void Cleanup()
        {
            try
            {
                _netClient?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Cleanup failed: {ex.Message}");
            }

            _netClient = null;
            _configVar = null;
            _stateVar = null;
            _drifterVar = null;
            _managerVar = null;
            _actionVar = null;
            _pendingGameState = null;
            _processedActions.Clear();
            _networkInitialized = false;
            _initialSyncDone = false;
        }

        /// <summary>
        /// Client callback when host config SyncVar changes.
        /// </summary>
        private static void OnConfigChanged(string oldValue, string newValue)
        {
            if (_netClient?.IsHost == true) return;
            if (string.IsNullOrEmpty(newValue)) return;

            try
            {
                var cfgData = ParsePayload(newValue);
                Config.ApplyOverrides(cfgData);
                RefreshQuestText();
                Logger.Msg($"Client applied {cfgData.Count} config overrides from SyncVar.");
            }
            catch (Exception ex)
            {
                Logger.Warning($"OnConfigChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client callback when host state SyncVar changes.
        /// </summary>
        private static void OnStateChanged(string oldValue, string newValue)
        {
            if (_netClient?.IsHost == true) return;
            if (string.IsNullOrEmpty(newValue)) return;

            try
            {
                var state = ParsePayload(newValue);
                _pendingGameState = state;
                ApplyGameState(state);
                Logger.Msg($"Client applied {state.Count} game state values from SyncVar.");
            }
            catch (Exception ex)
            {
                Logger.Warning($"OnStateChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client callback when host drifter state SyncVar changes.
        /// </summary>
        private static void OnDrifterStateChanged(string oldValue, string newValue)
        {
            if (_netClient?.IsHost == true) return;
            if (string.IsNullOrEmpty(newValue)) return;

            try
            {
                DrifterManager.Instance?.ApplyDrifterState(newValue);
                Logger.Msg($"Client applied drifter state from SyncVar ({newValue.Length} chars).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"OnDrifterStateChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client callback when host manager state SyncVar changes.
        /// </summary>
        private static void OnManagerStateChanged(string oldValue, string newValue)
        {
            if (_netClient?.IsHost == true) return;
            if (string.IsNullOrEmpty(newValue)) return;

            try
            {
                var state = ParsePayload(newValue);

                // Sync business assignments
                ManagerInstance.SyncedManagerBusinesses.Clear();
                if (state.TryGetValue("biz", out var bizStr) && !string.IsNullOrEmpty(bizStr))
                {
                    foreach (var code in bizStr.Split(','))
                    {
                        if (!string.IsNullOrEmpty(code))
                            ManagerInstance.SyncedManagerBusinesses.Add(code);
                    }
                }

                // Adopt manager NPCs
                if (state.TryGetValue("data", out var mgrData) && !string.IsNullOrEmpty(mgrData))
                {
                    ManagerInstance.ApplyManagerState(mgrData);
                }

                Logger.Msg($"Client applied manager state from SyncVar ({newValue.Length} chars).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"OnManagerStateChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Host callback when any client's action SyncVar changes.
        /// </summary>
        private static void OnActionChanged(CSteamID sender, string oldValue, string newValue)
        {
            if (_netClient?.IsHost != true) return;
            if (string.IsNullOrEmpty(newValue)) return;

            ulong senderId = sender.m_SteamID;

            // Ignore own actions (host doesn't send actions to itself).
            if (_netClient != null && senderId == _netClient.LocalPlayerId.m_SteamID) return;

            try
            {
                // Deduplicate: track last processed value per sender.
                if (_processedActions.TryGetValue(senderId, out string last) && last == newValue)
                    return;
                _processedActions[senderId] = newValue;

                // Parse "seq:ACTION_NAME"
                int colonIdx = newValue.IndexOf(':');
                if (colonIdx <= 0 || colonIdx >= newValue.Length - 1) return;
                string action = newValue.Substring(colonIdx + 1);

                Logger.Msg($"Host received quest action '{action}' from {sender}");
                ProcessQuestAction(action);
            }
            catch (Exception ex)
            {
                Logger.Warning($"OnActionChanged failed: {ex.Message}");
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

            if (_configVar != null)
                _configVar.Value = _payload;

            RefreshQuestText();
        }

        /// <summary>
        /// Updates quest entry text on all active quests to reflect current Config values.
        /// Called after config changes on both host and client.
        /// </summary>
        private static void RefreshQuestText()
        {
            try
            {
                VicIntroQuest.Instance?.RefreshEntryText();
                StaticIntroQuest.Instance?.RefreshEntryText();
                StaticUpgrade1Quest.Instance?.RefreshEntryText();
                StaticUpgrade2Quest.Instance?.RefreshEntryText();
            }
            catch (System.Exception ex)
            {
                Logger.Warning($"RefreshQuestText failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by host when quest/game state changes (e.g. ATM threshold met,
        /// Vic intro triggered). Serializes key flags and pushes to clients.
        /// </summary>
        public void PublishGameState()
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                string statePayload = SerializeGameState();
                Logger.Msg($"PublishGameState: payload length={statePayload?.Length ?? 0}");
                if (_stateVar != null)
                    _stateVar.Value = statePayload;
            }
            catch (Exception ex)
            {
                Logger.Warning($"PublishGameState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Publishes drifter state to the dedicated drifter SyncVar.
        /// Separate from PublishGameState to avoid lobby data truncation.
        /// </summary>
        public void PublishDrifterState()
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                string drifterState = DrifterManager.Instance?.SerializeDrifterState() ?? "";
                if (_drifterVar != null)
                    _drifterVar.Value = drifterState;
            }
            catch (Exception ex)
            {
                Logger.Warning($"PublishDrifterState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Publishes manager state to the dedicated manager SyncVar.
        /// Separate from PublishGameState to avoid lobby data truncation.
        /// </summary>
        public void PublishManagerState()
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                string managerState = SerializeManagerSyncVar();
                Logger.Msg($"PublishManagerState: {managerState.Length} chars");
                if (_managerVar != null)
                    _managerVar.Value = managerState;
            }
            catch (Exception ex)
            {
                Logger.Warning($"PublishManagerState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Serializes manager business assignments + NPC data for the dedicated SyncVar.
        /// Format: biz=code1,code2|data=id:seed:biz:netObjId;id:seed:biz:netObjId
        /// </summary>
        private static string SerializeManagerSyncVar()
        {
            var parts = new List<string>();

            string mgrBiz = ManagerInstance.GetManagedBusinessCodes();
            if (!string.IsNullOrEmpty(mgrBiz))
                parts.Add($"biz={mgrBiz}");

            string mgrData = ManagerInstance.SerializeManagerState();
            if (!string.IsNullOrEmpty(mgrData))
                parts.Add($"data={mgrData}");

            return string.Join("|", parts);
        }

        /// <summary>
        /// Sends a quest action to the host via ClientSyncVar.
        /// No-ops on the host (host executes state changes directly).
        /// Uses a sequence counter so repeated actions (e.g. VIC_LAUNDER)
        /// are not deduplicated away.
        /// </summary>
        public static void SendQuestAction(string action)
        {
            if (NetworkHelper.IsHost) return;

            try
            {
                if (_actionVar == null)
                {
                    Logger.Warning($"SendQuestAction({action}): SyncVar not initialized yet.");
                    return;
                }

                string value = $"{++_actionSeq}:{action}";
                _actionVar.Value = value;
                Logger.Msg($"Sent quest action via SyncVar: {value}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"SendQuestAction({action}) failed: {ex.Message}");
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
                    if (action.StartsWith("DESP_RESOLVE:"))
                    {
                        string custId = action.Substring("DESP_RESOLVE:".Length);
                        DesperationManager.ResolveEvent(custId);
                    }
                    else if (action.StartsWith("DESP_ACCEPT:"))
                    {
                        string payload = action.Substring("DESP_ACCEPT:".Length);
                        int sep = payload.IndexOf(':');
                        if (sep > 0 && sep < payload.Length - 1)
                        {
                            string custId = payload.Substring(0, sep);
                            string locGuid = payload.Substring(sep + 1);
                            Patches.AcceptContractClickedPatch.FinalizeDesperationDealRemote(custId, locGuid);
                        }
                    }
                    else if (action.StartsWith("DRIFTER_ACCEPT:"))
                    {
                        string drifterId = action.Substring("DRIFTER_ACCEPT:".Length);
                        DrifterManager.Instance?.OnDealAccepted(drifterId);
                    }
                    else if (action.StartsWith("MANAGER_HIRE:"))
                    {
                        string propertyCode = action.Substring("MANAGER_HIRE:".Length);
                        ManagerController.Instance?.HireManagerRemote(propertyCode);
                    }
                    else if (action.StartsWith("MANAGER_FIRE:"))
                    {
                        string managerId = action.Substring("MANAGER_FIRE:".Length);
                        ManagerController.Instance?.FireManagerRemote(managerId);
                    }
                    else if (action.StartsWith("MANAGER_TRANSFER:"))
                    {
                        // Format: MANAGER_TRANSFER:managerId:targetPropertyCode
                        string payload = action.Substring("MANAGER_TRANSFER:".Length);
                        int sep = payload.IndexOf(':');
                        if (sep > 0 && sep < payload.Length - 1)
                        {
                            string managerId = payload.Substring(0, sep);
                            string targetCode = payload.Substring(sep + 1);
                            ManagerController.Instance?.TransferManagerRemote(managerId, targetCode);
                        }
                    }
                    else if (action.StartsWith("MANAGER_CONFIG:"))
                    {
                        // Format: MANAGER_CONFIG:managerId:configData (~ instead of |)
                        string payload = action.Substring("MANAGER_CONFIG:".Length);
                        int sep = payload.IndexOf(':');
                        if (sep > 0 && sep < payload.Length - 1)
                        {
                            string managerId = payload.Substring(0, sep);
                            string configStr = ManagerInstance.DecodeConfig(payload.Substring(sep + 1));
                            if (ManagerInstance.Active.TryGetValue(managerId, out var instance))
                            {
                                instance.Configuration.Deserialize(configStr);
                                instance.ReconcileLockerFromConfig();
                                Instance?.PublishManagerState();
                            }
                        }
                    }
                    else if (action.StartsWith("DRIFTER_COMPLETE:"))
                    {
                        // Format: DRIFTER_COMPLETE:drifterId:playerCode
                        string payload = action.Substring("DRIFTER_COMPLETE:".Length);
                        int sep = payload.IndexOf(':');
                        string drifterId = sep > 0 ? payload.Substring(0, sep) : payload;
                        string playerCode = sep > 0 ? payload.Substring(sep + 1) : "";
                        DrifterManager.Instance?.OnRemoteDealCompleted(drifterId, playerCode);
                    }
                    else
                    {
                        Logger.Warning($"Unknown quest action: {action}");
                    }
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

            string despIds = DesperationManager.GetDesperateIdsForSync();
            if (!string.IsNullOrEmpty(despIds))
                parts.Add($"desp_ids={despIds}");

            // Manager data is now on its own SyncVar (_managerVar) to avoid lobby data truncation.

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

            // Sync desperation customer IDs to client
            var despIds = new HashSet<string>();
            if (state.TryGetValue("desp_ids", out var despStr) && !string.IsNullOrEmpty(despStr))
            {
                foreach (var id in despStr.Split(','))
                {
                    if (!string.IsNullOrEmpty(id))
                        despIds.Add(id);
                }
            }
            DesperationManager.UpdateClientDesperateIds(despIds);

            // Manager data is now on its own SyncVar (_managerVar) — handled in OnManagerStateChanged.
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
