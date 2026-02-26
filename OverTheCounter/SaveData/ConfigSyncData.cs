using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.Quests;
using OverTheCounter.Utilities;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace OverTheCounter.SaveData
{
    public class ConfigSyncData : Saveable
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:ConfigSync");

        [SaveableField("config_sync_payload")]
        private string _payload = "";

        // Cached so SaveData instances created later can pull pending state.
        // Static so it persists even if ConfigSyncData.Instance hasn't been created yet.
        private static Dictionary<string, string> _pendingGameState;

        // Sequence counters for SyncVar dedup (pure ints, no SteamNetworkLib dependency)
        private static int _msgSeq;
        private static int _actionSeq;

        public static ConfigSyncData Instance { get; private set; }

        // ==================================================================
        // Runtime guard — SteamNetworkLib optional dependency
        // ==================================================================

        private static bool? _networkLibAvailable;

        /// <summary>
        /// True when the SteamNetworkLib assembly is loaded. Cached on first access.
        /// All calls to NetworkSyncBridge are gated behind this check to prevent
        /// TypeLoadException when the DLL is absent.
        /// </summary>
        internal static bool IsNetworkLibAvailable =>
            _networkLibAvailable ??= AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name.Contains("SteamNetworkLib"));

        // ==================================================================
        // Lifecycle
        // ==================================================================

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
            if (IsNetworkLibAvailable)
                OnLoadedNetworkPush();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void OnLoadedNetworkPush()
        {
            NetworkSyncBridge.PushOnLoaded(
                Config.SerializeAll(),
                SerializeGameState(),
                DrifterManager.Instance?.SerializeDrifterState() ?? "",
                CustomerManager.Instance?.SerializeCustomerState() ?? "");
            // Manager slots are pushed via WriteInitialManagerSlots() in ProcessMessages,
            // or via PublishManagerState() once managers are restored from save.
        }

        // ==================================================================
        // Public API — guarded delegates to NetworkSyncBridge
        // ==================================================================

        /// <summary>
        /// Initializes SteamNetworkClient and SyncVars. Called from Core.OnLateUpdate()
        /// so it runs on BOTH host and client regardless of whether the
        /// Saveable lifecycle fires.
        /// </summary>
        public static void EnsureNetworkReady()
        {
            if (!IsNetworkLibAvailable) return;
            EnsureNetworkReadyImpl();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EnsureNetworkReadyImpl() => NetworkSyncBridge.EnsureNetworkReady();

        /// <summary>
        /// Processes incoming SyncVar messages (both host and client).
        /// Called from Core.OnLateUpdate() every frame.
        /// </summary>
        public static void ProcessMessages()
        {
            if (!IsNetworkLibAvailable) return;
            ProcessMessagesImpl();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProcessMessagesImpl() => NetworkSyncBridge.ProcessMessages();

        /// <summary>
        /// Disposes the SteamNetworkClient and resets all SyncVar references.
        /// Called from Core.OnDeinitializeMelon().
        /// </summary>
        public static void Cleanup()
        {
            _pendingGameState = null;
            if (!IsNetworkLibAvailable) return;
            CleanupImpl();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CleanupImpl() => NetworkSyncBridge.Cleanup();

        /// <summary>
        /// Called by host after config changes (e.g. ModsApp Apply postfix).
        /// Updates the saveable payload and pushes to SyncVar for connected clients.
        /// </summary>
        public void RefreshFromConfig()
        {
            if (!NetworkHelper.IsHost) return;
            _payload = Config.SerializeAll();

            if (IsNetworkLibAvailable)
                RefreshFromConfigImpl(_payload);

            RefreshQuestText();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RefreshFromConfigImpl(string payload) => NetworkSyncBridge.PushConfig(payload);

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
                if (Config.VerboseLogging.Value)
                    Logger.Msg($"PublishGameState: payload length={statePayload?.Length ?? 0}");
                if (IsNetworkLibAvailable)
                    PublishGameStateImpl(statePayload);
            }
            catch (Exception ex)
            {
                Logger.Warning($"PublishGameState failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PublishGameStateImpl(string payload) => NetworkSyncBridge.PushState(payload);

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
                if (IsNetworkLibAvailable)
                    PublishDrifterStateImpl(drifterState);
            }
            catch (Exception ex)
            {
                Logger.Warning($"PublishDrifterState failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PublishDrifterStateImpl(string payload) => NetworkSyncBridge.PushDrifterState(payload);

        /// <summary>
        /// Publishes customer state to the dedicated customer SyncVar.
        /// </summary>
        public void PublishCustomerState()
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                string customerState = CustomerManager.Instance?.SerializeCustomerState() ?? "";
                if (IsNetworkLibAvailable)
                    PublishCustomerStateImpl(customerState);
            }
            catch (Exception ex)
            {
                Logger.Warning($"PublishCustomerState failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PublishCustomerStateImpl(string payload) => NetworkSyncBridge.PushCustomerState(payload);

        /// <summary>
        /// Publishes each active manager to its own SyncVar slot.
        /// Separate from PublishGameState to avoid lobby data truncation.
        /// </summary>
        public void PublishManagerState()
        {
            if (!NetworkHelper.IsHost) return;

            ManagerSaveData.Instance?.CaptureState();

            try
            {
                if (IsNetworkLibAvailable)
                    PublishManagerStateImpl();
            }
            catch (Exception ex)
            {
                Logger.Warning($"PublishManagerState failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PublishManagerStateImpl() => ManagerInstance.PublishAllSlots();

        /// <summary>
        /// Writes manager data to per-manager SyncVar slots during initial lobby sync.
        /// Called from NetworkSyncBridge.ProcessMessages after lobby discovery.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void WriteInitialManagerSlots() => ManagerInstance.PublishAllSlots();

        /// <summary>
        /// Publishes pending manager text messages to the dedicated message SyncVar.
        /// Called from Core.OnLateUpdate when HasPendingMessages is true.
        /// Format: "seq;managerId|messageText;managerId2|messageText2"
        /// </summary>
        public void PublishManagerMessages()
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                var msgParts = new List<string>();
                msgParts.Add((_msgSeq++).ToString()); // Sequence counter prevents SyncVar dedup

                foreach (var mgr in ManagerInstance.Active.Values)
                {
                    if (!string.IsNullOrEmpty(mgr.PendingClientMessage))
                    {
                        msgParts.Add($"{mgr.Id}|{mgr.PendingClientMessage}");
                        mgr.PendingClientMessage = null;
                    }
                }
                ManagerInstance.HasPendingMessages = false;

                if (msgParts.Count > 1 && IsNetworkLibAvailable) // > 1 because first entry is seq
                {
                    string payload = string.Join(";", msgParts);
                    PublishManagerMessagesImpl(payload);
                    if (Config.VerboseLogging.Value)
                        Logger.Msg($"PublishManagerMessages: {payload.Length} chars, {msgParts.Count - 1} messages");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"PublishManagerMessages failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PublishManagerMessagesImpl(string payload) => NetworkSyncBridge.PushManagerMessages(payload);

        /// <summary>
        /// Publishes pending drifter text messages to the dedicated message SyncVar.
        /// Called from Core.OnLateUpdate when HasPendingDrifterMessages is true.
        /// Format: "seq;drifterId|messageText;drifterId2|messageText2"
        /// </summary>
        public void PublishDrifterMessages()
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                var msgParts = new List<string>();
                msgParts.Add((_msgSeq++).ToString());

                var dm = DrifterManager.Instance;
                if (dm != null)
                {
                    foreach (var evt in dm.ActiveEvents.Values)
                    {
                        if (!string.IsNullOrEmpty(evt.PendingClientMessage))
                        {
                            msgParts.Add($"{evt.DrifterId}|{evt.PendingClientMessage}");
                            evt.PendingClientMessage = null;
                        }
                    }
                }
                DrifterManager.HasPendingDrifterMessages = false;

                if (msgParts.Count > 1 && IsNetworkLibAvailable)
                {
                    string payload = string.Join(";", msgParts);
                    PublishDrifterMessagesImpl(payload);
                    if (Config.VerboseLogging.Value)
                        Logger.Msg($"PublishDrifterMessages: {payload.Length} chars, {msgParts.Count - 1} messages");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"PublishDrifterMessages failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PublishDrifterMessagesImpl(string payload) => NetworkSyncBridge.PushDrifterMessages(payload);

        /// <summary>
        /// Sends a quest action to the host via ClientSyncVar.
        /// No-ops on the host (host executes state changes directly).
        /// Uses a sequence counter so repeated actions (e.g. VIC_LAUNDER)
        /// are not deduplicated away.
        /// </summary>
        public static void SendQuestAction(string action)
        {
            if (NetworkHelper.IsHost) return;
            if (!IsNetworkLibAvailable) return;
            SendQuestActionImpl(action);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendQuestActionImpl(string action)
        {
            string value = $"{++_actionSeq}:{action}";
            NetworkSyncBridge.SendAction(value);
        }

        // ==================================================================
        // Internal API — called by NetworkSyncBridge callbacks
        // ==================================================================

        /// <summary>
        /// Provides serialized config for the bridge's initial sync push.
        /// </summary>
        internal static string GetSerializedConfig() => Config.SerializeAll();

        /// <summary>
        /// Provides serialized game state for the bridge's initial sync push.
        /// </summary>
        internal static string GetSerializedGameState() => SerializeGameState();

        /// <summary>
        /// Provides serialized drifter state for the bridge's initial sync push.
        /// </summary>
        internal static string GetSerializedDrifterState() =>
            DrifterManager.Instance?.SerializeDrifterState() ?? "";

        /// <summary>
        /// Provides serialized customer state for the bridge's initial sync push.
        /// </summary>
        internal static string GetSerializedCustomerState() =>
            CustomerManager.Instance?.SerializeCustomerState() ?? "";

        /// <summary>
        /// Clears config overrides on the host during initial lobby sync.
        /// </summary>
        internal static void ClearOverridesForHost() => Config.ClearAllOverrides();

        /// <summary>
        /// Client callback: host config SyncVar changed — apply overrides.
        /// </summary>
        internal static void HandleConfigChanged(string newValue)
        {
            try
            {
                var cfgData = ParsePayload(newValue);
                Config.ApplyOverrides(cfgData);
                RefreshQuestText();
                Logger.Msg($"Client applied {cfgData.Count} config overrides from SyncVar.");
            }
            catch (Exception ex)
            {
                Logger.Warning($"HandleConfigChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client callback: host state SyncVar changed — cache and apply game state.
        /// </summary>
        internal static void HandleStateChanged(string newValue)
        {
            try
            {
                var state = ParsePayload(newValue);
                _pendingGameState = state;
                ApplyGameState(state);
                Logger.Msg($"Client applied {state.Count} game state values from SyncVar.");
            }
            catch (Exception ex)
            {
                Logger.Warning($"HandleStateChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client callback: host drifter state SyncVar changed.
        /// </summary>
        internal static void HandleDrifterStateChanged(string newValue)
        {
            try
            {
                DrifterManager.Instance?.ApplyDrifterState(newValue);
                Logger.Msg($"Client applied drifter state from SyncVar ({newValue.Length} chars).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"HandleDrifterStateChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client callback: host customer state SyncVar changed.
        /// </summary>
        internal static void HandleCustomerStateChanged(string newValue)
        {
            try
            {
                CustomerManager.Instance?.ApplyHostCustomerState(newValue);
            }
            catch (Exception ex)
            {
                Logger.Warning($"HandleCustomerStateChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client callback: a single manager SyncVar slot changed.
        /// </summary>
        internal static void HandleManagerSlotChanged(int slot, string newValue)
        {
            try
            {
                ManagerInstance.ApplyManagerSlot(slot, newValue);
                Logger.Msg($"Client applied manager slot {slot} from SyncVar ({newValue?.Length ?? 0} chars).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"HandleManagerSlotChanged[{slot}] failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client callback: host manager text message SyncVar changed.
        /// Format: "seq;managerId|messageText" or "seq;id1|msg1;id2|msg2" for multiple.
        /// </summary>
        internal static void HandleManagerMessageChanged(string newValue)
        {
            try
            {
                var entries = newValue.Split(';');
                // First entry is the sequence counter — skip it
                for (int i = 1; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    int pipeIdx = entry.IndexOf('|');
                    if (pipeIdx <= 0) continue;

                    var id = entry.Substring(0, pipeIdx);
                    var text = entry.Substring(pipeIdx + 1);

                    if (ManagerInstance.Active.TryGetValue(id, out var mgr))
                    {
                        mgr.SendTextMessage(text, queueForClient: false);
                        if (Config.VerboseLogging.Value)
                            Logger.Msg($"Client delivered synced text from manager {id}");
                    }
                    else
                    {
                        Logger.Warning($"Client received message for unknown manager {id}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"HandleManagerMessageChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client callback: host drifter text message SyncVar changed.
        /// Format: "seq;drifterId|messageText" or "seq;id1|msg1;id2|msg2" for multiple.
        /// </summary>
        internal static void HandleDrifterMessageChanged(string newValue)
        {
            try
            {
                var entries = newValue.Split(';');
                // First entry is the sequence counter — skip it
                for (int i = 1; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    int pipeIdx = entry.IndexOf('|');
                    if (pipeIdx <= 0) continue;

                    var id = entry.Substring(0, pipeIdx);
                    var text = entry.Substring(pipeIdx + 1);

                    if (DrifterInstance.Active.TryGetValue(id, out var drifter))
                    {
                        drifter.DeliverTextLocally(text);
                        if (Config.VerboseLogging.Value)
                            Logger.Msg($"Client delivered synced text from drifter {id}");
                    }
                    else
                    {
                        Logger.Warning($"Client received message for unknown drifter {id}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"HandleDrifterMessageChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Host callback: quest action received from a client or polled.
        /// </summary>
        internal static void HandleActionReceived(string action)
        {
            ProcessQuestAction(action);
        }

        // ==================================================================
        // Quest action routing (pure game logic)
        // ==================================================================

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

                case "BELLA_INTRO":
                case "BELLA_ADVANCE":
                    BellaSaveData.Instance?.HandleRemoteAction(action);
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
                    else if (action.StartsWith("MANAGER_UPGRADE_SPEED:"))
                    {
                        string managerId = action.Substring("MANAGER_UPGRADE_SPEED:".Length);
                        if (ManagerInstance.Active.TryGetValue(managerId, out var mgr))
                        {
                            mgr.TryPurchaseSpeedUpgrade();
                            Instance?.PublishManagerState();
                        }
                    }
                    else if (action.StartsWith("MANAGER_UPGRADE_INV:"))
                    {
                        string managerId = action.Substring("MANAGER_UPGRADE_INV:".Length);
                        if (ManagerInstance.Active.TryGetValue(managerId, out var mgr))
                        {
                            mgr.TryPurchaseInventoryUpgrade();
                            Instance?.PublishManagerState();
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

        // ==================================================================
        // Pending state for late-created SaveData instances
        // ==================================================================

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

        // ==================================================================
        // Serialization / deserialization (pure game logic)
        // ==================================================================

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
                BellaProtocolQuest.Instance?.RefreshEntryText();
            }
            catch (System.Exception ex)
            {
                Logger.Warning($"RefreshQuestText failed: {ex.Message}");
            }
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
                // TODO: Move to property sync when property save class exists (see tools/saveable_buildings.md).
                parts.Add($"static_shack={BoolToStr(StaticSaveData.Instance.ShackPurchased)}");
            }

            if (VicSaveData.Instance != null)
            {
                parts.Add($"vic_texted={BoolToStr(VicSaveData.Instance.HasBeenTexted)}");
                parts.Add($"vic_accepted={BoolToStr(VicSaveData.Instance.QuestAccepted)}");
                parts.Add($"vic_unlocked={BoolToStr(VicSaveData.Instance.Unlocked)}");
                parts.Add($"vic_trust={VicSaveData.Instance.TrustLevel}");
            }

            if (BellaSaveData.Instance != null)
            {
                parts.Add($"bella_stage={BellaSaveData.Instance.Stage}");
                parts.Add($"bella_unlocked={BoolToStr(BellaSaveData.Instance.NightMarketUnlocked)}");
            }

            string despIds = DesperationManager.GetDesperateIdsForSync();
            if (!string.IsNullOrEmpty(despIds))
                parts.Add($"desp_ids={despIds}");

            // Manager data is on per-manager SyncVar slots (_mgrSlots) to avoid lobby data truncation.

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
                bool? shack = state.TryGetValue("static_shack", out var sh) ? StrToBool(sh) : (bool?)null;

                StaticSaveData.Instance.ApplyHostState(
                    questTriggered: triggered,
                    introCompleted: intro,
                    crmTier: tier,
                    saasActive: saas,
                    upgradeAvailable: upgrade,
                    saasNextPaymentDay: nextPayment,
                    dayPassCount: dayPass,
                    shackPurchased: shack);
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

            if (BellaSaveData.Instance != null)
            {
                int bellaStage = state.TryGetValue("bella_stage", out var bs) && int.TryParse(bs, out var bsVal) ? bsVal : 0;
                bool bellaUnlocked = state.TryGetValue("bella_unlocked", out var bu) && StrToBool(bu);
                BellaSaveData.Instance.ApplyHostState(bellaStage, bellaUnlocked);
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

            // Manager data is on per-manager SyncVar slots — handled in HandleManagerSlotChanged.
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
