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
using UnityEngine;

namespace OverTheCounter.SaveData
{
    public class ConfigSyncData : Saveable
    {
        [SaveableField("config_sync_payload")]
        private string _payload = "";

        // Cached so SaveData instances created later can pull pending state.
        // Static so it persists even if ConfigSyncData.Instance hasn't been created yet.
        private static Dictionary<string, string> _pendingGameState;

        // Sequence counters for SyncVar dedup (pure ints, no SteamNetworkLib dependency)
        private static int _msgSeq;
        private static int _actionSeq;

        // Dirty flags — set by MarkXxxDirty(), flushed once per frame in FlushDirtyState()
        private static bool _gameStateDirty;
        private static bool _drifterStateDirty;
        private static bool _pricingStateDirty;

        public static ConfigSyncData Instance { get; private set; }

        // ==================================================================
        // Runtime guard — SteamNetworkLib optional dependency
        // ==================================================================

        private static bool? _networkLibAvailable;

        // The exact AssemblyVersion OTC was compiled against.
        // If the installed DLL has a different version, the JIT will throw FileNotFoundException.
        private static readonly Version RequiredSteamNetworkLibVersion =
            typeof(ConfigSyncData).Assembly
                .GetReferencedAssemblies()
                .FirstOrDefault(r => r.Name != null && r.Name.Contains("SteamNetworkLib"))
                ?.Version;

        /// <summary>
        /// True when SteamNetworkLib is loaded with a compatible version.
        /// Cached on first access. Gates all NetworkSyncBridge calls.
        /// </summary>
        internal static bool IsNetworkLibAvailable
        {
            get
            {
                if (_networkLibAvailable.HasValue) return _networkLibAvailable.Value;

                var loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name != null && a.GetName().Name.Contains("SteamNetworkLib"));

                if (loaded == null)
                {
                    _networkLibAvailable = false;
                    return false;
                }

                // If version doesn't match what we compiled against, the JIT will throw
                // FileNotFoundException when our code tries to use SteamNetworkLib types.
                if (RequiredSteamNetworkLibVersion != null && loaded.GetName().Version != RequiredSteamNetworkLibVersion)
                {
                    OTCLog.Warning(OTCLog.Systems.Network,
                        $"SteamNetworkLib version mismatch: installed={loaded.GetName().Version}, required={RequiredSteamNetworkLibVersion}. " +
                        "Multiplayer sync disabled. Update SteamNetworkLib to the correct version.");
                    _networkLibAvailable = false;
                    return false;
                }

                _networkLibAvailable = true;
                return true;
            }
        }

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
            _gameStateDirty = false;
            _drifterStateDirty = false;
            _pricingStateDirty = false;

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
            try
            {
                EnsureNetworkReadyImpl();
            }
            catch (Exception ex) when (IsAssemblyLoadFailure(ex))
            {
                _networkLibAvailable = false;
                OTCLog.Warning(OTCLog.Systems.Network, $"SteamNetworkLib incompatible (version mismatch or wrong branch) — multiplayer sync disabled. ({ex.Message})");
            }
        }

        /// <summary>
        /// Returns true for exceptions caused by failing to load/resolve the SteamNetworkLib
        /// assembly — covers both wrong-branch TypeLoadExceptions and version-mismatch
        /// FileNotFoundExceptions (e.g., assembly compiled against 1.2.3.0 but 1.2.2.0 is installed).
        /// </summary>
        private static bool IsAssemblyLoadFailure(Exception ex) =>
            ex is TypeLoadException || ex is System.IO.FileNotFoundException || ex is System.IO.FileLoadException
            || ex.InnerException is TypeLoadException || ex.InnerException is System.IO.FileNotFoundException
            || ex.Message.Contains("type load") || ex.Message.Contains("Could not load file or assembly");


        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EnsureNetworkReadyImpl() => NetworkSyncBridge.EnsureNetworkReady();

        /// <summary>
        /// Processes incoming SyncVar messages (both host and client).
        /// Called from Core.OnLateUpdate() every frame.
        /// </summary>
        public static void ProcessMessages()
        {
            if (!IsNetworkLibAvailable) return;
            try
            {
                ProcessMessagesImpl();
            }
            catch (Exception ex) when (IsAssemblyLoadFailure(ex))
            {
                _networkLibAvailable = false;
                OTCLog.Warning(OTCLog.Systems.Network, $"SteamNetworkLib version mismatch — multiplayer sync disabled. ({ex.Message})");
            }
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
        /// Called by host after config changes (see <see cref="OverTheCounter.Config.SubscribeToChanges"/>).
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
                if (IsNetworkLibAvailable)
                    PublishGameStateImpl(statePayload);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"PublishGameState failed: {ex.Message}");
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
                OTCLog.Warning(OTCLog.Systems.Network, $"PublishDrifterState failed: {ex.Message}");
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
                OTCLog.Warning(OTCLog.Systems.Network, $"PublishCustomerState failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PublishCustomerStateImpl(string payload) => NetworkSyncBridge.PushCustomerState(payload);

        /// <summary>
        /// Publishes pricing state to the dedicated pricing SyncVar.
        /// </summary>
        public void PublishPricingState()
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                string pricingState = PricingSaveData.Instance?.Serialize() ?? "";
                if (IsNetworkLibAvailable)
                    PublishPricingStateImpl(pricingState);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"PublishPricingState failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PublishPricingStateImpl(string payload) => NetworkSyncBridge.PushPricingState(payload);

        // ==================================================================
        // Dirty-flag batched publishing
        // ==================================================================

        /// <summary>Marks game state for publish on next flush (end of frame).</summary>
        internal static void MarkGameStateDirty() => _gameStateDirty = true;

        /// <summary>Marks drifter state for publish on next flush (end of frame).</summary>
        internal static void MarkDrifterStateDirty() => _drifterStateDirty = true;

        /// <summary>Marks pricing state for publish on next flush (end of frame).</summary>
        internal static void MarkPricingStateDirty() => _pricingStateDirty = true;

        /// <summary>
        /// Flushes all dirty SyncVar channels. Called once per frame from
        /// Core.OnLateUpdate's NetworkPublish block. Coalesces multiple
        /// mutations in the same frame into a single SyncVar write per channel.
        /// </summary>
        internal static void FlushDirtyState()
        {
            if (_gameStateDirty)
            {
                _gameStateDirty = false;
                Instance?.PublishGameState();
            }
            if (_drifterStateDirty)
            {
                _drifterStateDirty = false;
                Instance?.PublishDrifterState();
            }
            if (_pricingStateDirty)
            {
                _pricingStateDirty = false;
                Instance?.PublishPricingState();
            }
        }

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
                OTCLog.Warning(OTCLog.Systems.Network, $"PublishManagerState failed: {ex.Message}");
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
                    OTCLog.Msg(OTCLog.Systems.Network, $"PublishManagerMessages: {payload.Length} chars, {msgParts.Count - 1} messages");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"PublishManagerMessages failed: {ex.Message}");
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
                    OTCLog.Msg(OTCLog.Systems.Network, $"PublishDrifterMessages: {payload.Length} chars, {msgParts.Count - 1} messages");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"PublishDrifterMessages failed: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PublishDrifterMessagesImpl(string payload) => NetworkSyncBridge.PushDrifterMessages(payload);

        // Checkout lock sequence counter (prevents SyncVar dedup on repeated lock/unlock)
        private static int _checkoutSeq;

        /// <summary>
        /// Publishes checkout lock state and total register balance to clients.
        /// Format: "seq|lockHolder|customerId|registerBal"
        /// </summary>
        public void PublishCheckoutState(string lockHolder, string customerId)
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                float registerBal = 0f;
                foreach (var counter in Logic.Placement.CheckoutCounter.AllCounters)
                    registerBal += counter.RegisterBalance;

                string payload = $"{++_checkoutSeq}|{lockHolder}|{customerId}|{registerBal.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                if (IsNetworkLibAvailable)
                    PublishCheckoutStateImpl(payload);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"PublishCheckoutState failed: {ex.Message}");
            }
        }

        /// <summary>Publishes with empty lock (no checkout active).</summary>
        public void PublishCheckoutClear()
        {
            PublishCheckoutState("", "");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PublishCheckoutStateImpl(string payload) => NetworkSyncBridge.PushCheckoutState(payload);

        /// <summary>
        /// Returns the local player's Steam ID as a string, or empty if not available.
        /// </summary>
        public static string LocalPlayerId
        {
            get
            {
                if (!IsNetworkLibAvailable) return "";
                return GetLocalPlayerIdImpl();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string GetLocalPlayerIdImpl() => NetworkSyncBridge.GetLocalPlayerId();

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
                OTCLog.Msg(OTCLog.Systems.Network, $"Client applied {cfgData.Count} config overrides from SyncVar.");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"HandleConfigChanged failed: {ex.Message}");
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
                OTCLog.Msg(OTCLog.Systems.Network, $"Client applied {state.Count} game state values from SyncVar.");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"HandleStateChanged failed: {ex.Message}");
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
                OTCLog.Msg(OTCLog.Systems.Network, $"Client applied drifter state from SyncVar ({newValue.Length} chars).");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"HandleDrifterStateChanged failed: {ex.Message}");
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
                OTCLog.Warning(OTCLog.Systems.Network, $"HandleCustomerStateChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client callback: host checkout SyncVar changed.
        /// Format: "seq|lockHolder|customerId|registerBal"
        /// </summary>
        internal static void HandleCheckoutStateChanged(string newValue)
        {
            try
            {
                if (string.IsNullOrEmpty(newValue))
                {
                    Logic.CheckoutProcess.OnLockStateChanged("", "");
                    return;
                }

                var parts = newValue.Split('|');
                if (parts.Length < 3) return;

                string lockHolder = parts[1];
                string customerId = parts[2];

                Logic.CheckoutProcess.OnLockStateChanged(lockHolder, customerId);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"HandleCheckoutStateChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client callback: host pricing SyncVar changed — apply to PricingSaveData.
        /// </summary>
        internal static void HandlePricingChanged(string newValue)
        {
            try
            {
                if (PricingSaveData.Instance == null) return;
                PricingSaveData.Instance.Deserialize(newValue);
                OTCLog.Msg(OTCLog.Systems.Network, $"Client applied pricing state from SyncVar ({newValue?.Length ?? 0} chars).");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"HandlePricingChanged failed: {ex.Message}");
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
                OTCLog.Msg(OTCLog.Systems.Network, $"Client applied manager slot {slot} from SyncVar ({newValue?.Length ?? 0} chars).");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"HandleManagerSlotChanged[{slot}] failed: {ex.Message}");
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
                        OTCLog.Msg(OTCLog.Systems.Network, $"Client delivered synced text from manager {id}");
                    }
                    else
                    {
                        OTCLog.Warning(OTCLog.Systems.Network, $"Client received message for unknown manager {id}");
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"HandleManagerMessageChanged failed: {ex.Message}");
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
                        OTCLog.Msg(OTCLog.Systems.Network, $"Client delivered synced text from drifter {id}");
                    }
                    else
                    {
                        OTCLog.Warning(OTCLog.Systems.Network, $"Client received message for unknown drifter {id}");
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"HandleDrifterMessageChanged failed: {ex.Message}");
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
                case "STATIC_INTRO_COMPLETED":
                case "STATIC_PURCHASE_INITIAL":
                case "STATIC_PURCHASE_UPGRADE":
                case "STATIC_REACTIVATE":
                case "STATIC_CANCEL":
                    StaticSaveData.Instance?.HandleRemoteAction(action);
                    break;

                case "PURCHASE_WESTVILLE_SHACK":
                    PropertySaveData.Instance?.PurchaseProperty(PropertySaveData.ShackId);
                    break;
                case "PURCHASE_DISPENSARY":
                    PropertySaveData.Instance?.PurchaseProperty(PropertySaveData.DispensaryId);
                    break;
                case "PURCHASE_WAREHOUSE":
                    PropertySaveData.Instance?.PurchaseProperty(PropertySaveData.WarehouseId);
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
                    else if (action.StartsWith("BUDTENDER_HIRE:"))
                    {
                        string idxStr = action.Substring("BUDTENDER_HIRE:".Length);
                        if (int.TryParse(idxStr, out int idx))
                        {
                            var counter = Logic.Placement.CheckoutCounter.GetCounterByIndex(idx);
                            if (counter != null) Logic.BudtenderController.Hire(counter);
                        }
                    }
                    else if (action.StartsWith("BUDTENDER_FIRE:"))
                    {
                        string budtenderId = action.Substring("BUDTENDER_FIRE:".Length);
                        Logic.BudtenderController.Fire(budtenderId);
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
                    else if (action.StartsWith("CHECKOUT_REQUEST:"))
                    {
                        // SyncVar fallback (Debug builds / P2P unavailable)
                        Logic.CheckoutProcess.HandleCheckoutRequest(action.Substring("CHECKOUT_REQUEST:".Length));
                    }
                    else if (action.StartsWith("CHECKOUT_DONE:"))
                    {
                        Logic.CheckoutProcess.HandleCheckoutDone(action.Substring("CHECKOUT_DONE:".Length));
                    }
                    else if (action == "CHECKOUT_ABORT")
                    {
                        Logic.CheckoutProcess.HandleCheckoutAbort();
                    }
                    else if (action.StartsWith("REGISTER_COLLECT:"))
                    {
                        Logic.CheckoutProcess.HandleRegisterCollect(action.Substring("REGISTER_COLLECT:".Length));
                    }
                    else if (action.StartsWith("SHACK_LIGHTS:"))
                    {
                        bool on = action.Substring("SHACK_LIGHTS:".Length) == "1";
                        Logic.Placement.WestvilleShack.SetLightsFromSync(on);
                        MarkGameStateDirty();
                    }
                    else if (action.StartsWith("SHACK_STORE:"))
                    {
                        bool open = action.Substring("SHACK_STORE:".Length) == "1";
                        Logic.Placement.WestvilleShack.SetStoreOpen(open);
                        MarkGameStateDirty();
                    }
                    else if (action.StartsWith("DISP_LIGHTS:"))
                    {
                        bool on = action.Substring("DISP_LIGHTS:".Length) == "1";
                        Logic.Placement.Dispensary.SetLightsFromSync(on);
                        MarkGameStateDirty();
                    }
                    else if (action.StartsWith("DISP_STORE:"))
                    {
                        bool open = action.Substring("DISP_STORE:".Length) == "1";
                        Logic.Placement.Dispensary.SetStoreOpen(open);
                        MarkGameStateDirty();
                    }
                    else if (action.StartsWith("WH_LIGHTS:"))
                    {
                        bool on = action.Substring("WH_LIGHTS:".Length) == "1";
                        Logic.Placement.OTCWarehouse.SetLightsFromSync(on);
                        MarkGameStateDirty();
                    }
                    else if (action.StartsWith("STYLE:"))
                    {
                        // Format: STYLE:buildingId:type:styleId
                        var parts = action.Substring("STYLE:".Length).Split(':');
                        if (parts.Length == 3)
                            ApplyRemoteStyleChange(parts[0], parts[1], parts[2]);
                    }
                    else if (action.StartsWith("DESK_STYLE:"))
                    {
                        // Format: DESK_STYLE:buildingId:styleId
                        var parts = action.Substring("DESK_STYLE:".Length).Split(':');
                        if (parts.Length == 2)
                            ApplyRemoteDeskStyleChange(parts[0], parts[1]);
                    }
                    else if (action.StartsWith("DISP_RENAME:"))
                    {
                        if (PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.DispensaryId) != true) break;
                        string name = action.Substring("DISP_RENAME:".Length).Trim();
                        if (string.IsNullOrEmpty(name)) name = "Dispensary";
                        if (name.Length > 20) name = name.Substring(0, 20);
                        name = name.Replace("|", "").Replace("=", ""); // prevent game-state payload corruption
                        if (PropertySaveData.Instance != null)
                            PropertySaveData.Instance.DispensaryDisplayName = name;
                        Logic.Placement.Dispensary.UpdateSignText(name);
                        MarkGameStateDirty();
                    }
                    else if (action.StartsWith("PRICING_STATE:"))
                    {
                        string payload = action.Substring("PRICING_STATE:".Length);
                        if (PricingSaveData.Instance != null)
                        {
                            PricingSaveData.Instance.Deserialize(payload);
                            // Re-assign through the setter to enforce clamping — Deserialize writes the backing
                            // field directly, which bypasses the Math.Max(0.01f) guard on PricingMultiplier.
                            PricingSaveData.Instance.PricingMultiplier = PricingSaveData.Instance.PricingMultiplier;
                        }
                        MarkPricingStateDirty();
                    }
                    else if (action.StartsWith("SIGN_COLORS:"))
                    {
                        if (PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.DispensaryId) != true) break;
                        // Format: SIGN_COLORS:textHex:backHex
                        var parts = action.Substring("SIGN_COLORS:".Length).Split(':');
                        if (parts.Length == 2 && PropertySaveData.Instance != null)
                        {
                            if (ColorUtility.TryParseHtmlString("#" + parts[0], out var tc))
                                PropertySaveData.Instance.SignTextColor = tc;
                            if (ColorUtility.TryParseHtmlString("#" + parts[1], out var bc))
                                PropertySaveData.Instance.SignBackColor = bc;
                            Logic.Placement.Dispensary.UpdateSignColors(
                                PropertySaveData.Instance.SignTextColor,
                                PropertySaveData.Instance.SignBackColor);
                            MarkGameStateDirty();
                        }
                    }
                    else
                    {
                        OTCLog.Warning(OTCLog.Systems.Network, $"Unknown quest action: {action}");
                    }
                    break;
            }
        }

        /// <summary>
        /// Host-side handler for client style change requests.
        /// Applies the style locally and publishes updated game state.
        /// </summary>
        private static void ApplyRemoteStyleChange(string buildingId, string styleType, string styleId)
        {
            bool isShack = buildingId == PropertySaveData.ShackId;
            bool isDisp = buildingId == PropertySaveData.DispensaryId;
            if (!isShack && !isDisp) return;

            switch (styleType)
            {
                case "lighting":
                    var lightStyle = Logic.Placement.LightingStyle.Get(styleId);
                    if (lightStyle == null) return;
                    if (isShack) Logic.Placement.WestvilleShack.ApplyLightingStyle(lightStyle);
                    else Logic.Placement.Dispensary.ApplyLightingStyle(lightStyle);
                    break;
                case "ext_wall":
                    var extWall = Logic.Placement.WallStyle.GetExterior(styleId);
                    var extMat = S1MAPI.S1.Materials.Find(extWall.MaterialName);
                    if (extMat == null) return;
                    if (isShack)
                    {
                        Logic.Placement.WestvilleShack.CurrentExteriorWallStyleId = styleId;
                        Logic.Placement.WestvilleShack.SwapExteriorWallMaterial(extMat);
                    }
                    else
                    {
                        Logic.Placement.Dispensary.CurrentExteriorWallStyleId = styleId;
                        Logic.Placement.Dispensary.SwapExteriorWallMaterial(extMat);
                    }
                    break;
                case "int_wall":
                    var intWall = Logic.Placement.WallStyle.GetInterior(styleId);
                    var intMat = S1MAPI.S1.Materials.Find(intWall.MaterialName);
                    if (intMat == null) return;
                    if (isShack)
                    {
                        Logic.Placement.WestvilleShack.CurrentInteriorWallStyleId = styleId;
                        Logic.Placement.WestvilleShack.SwapInteriorWallMaterial(intMat);
                    }
                    else
                    {
                        Logic.Placement.Dispensary.CurrentInteriorWallStyleId = styleId;
                        Logic.Placement.Dispensary.SwapInteriorWallMaterial(intMat);
                    }
                    break;
                case "floor":
                    var floorStyle = Logic.Placement.FloorStyle.Get(styleId);
                    var floorMat = S1MAPI.S1.Materials.Find(floorStyle.MaterialName);
                    if (floorMat == null) return;
                    if (isShack)
                    {
                        Logic.Placement.WestvilleShack.CurrentFloorStyleId = styleId;
                        Logic.Placement.WestvilleShack.SwapFloorMaterial(floorMat);
                    }
                    else
                    {
                        Logic.Placement.Dispensary.CurrentFloorStyleId = styleId;
                        Logic.Placement.Dispensary.SwapFloorMaterial(floorMat);
                    }
                    break;
                default:
                    OTCLog.Warning(OTCLog.Systems.Network, $"Unknown style type: {styleType}");
                    return;
            }

            MarkGameStateDirty();
        }

        /// <summary>
        /// Host-side handler for client desk style change requests.
        /// Applies the new desk style to all counters in the building and publishes updated game state.
        /// </summary>
        private static void ApplyRemoteDeskStyleChange(string buildingId, string styleId)
        {
            bool isShack = buildingId == PropertySaveData.ShackId;
            bool isDisp = buildingId == PropertySaveData.DispensaryId;
            if (isShack && PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.ShackId) != true) return;
            if (isDisp && PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.DispensaryId) != true) return;
            if (!isShack && !isDisp) return;
            var style = Logic.Placement.DeskStyle.Get(styleId);
            if (style == null) return;
            foreach (var c in Logic.Placement.CheckoutCounter.AllCounters)
                if (c.BuildingId == buildingId) c.SwapDesk(style);
            MarkGameStateDirty();
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
            OTCLog.Msg(OTCLog.Systems.Network, "Applied pending game state to newly created SaveData.");
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
                OTCLog.Warning(OTCLog.Systems.Network, $"RefreshQuestText failed: {ex.Message}");
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
                parts.Add($"static_upg_accepted={BoolToStr(StaticSaveData.Instance.UpgradeAccepted)}");
                parts.Add($"static_next_payment={StaticSaveData.Instance.SaasNextPaymentDay}");
                parts.Add($"static_day_pass={StaticSaveData.Instance.DayPassCount}");
                parts.Add($"static_t1_money={BoolToStr(StaticSaveData.Instance.Tier1MoneyPaid)}");
                parts.Add($"static_t1_product={BoolToStr(StaticSaveData.Instance.Tier1ProductDelivered)}");
                parts.Add($"static_upg_money={BoolToStr(StaticSaveData.Instance.UpgradeMoneyPaid)}");
                parts.Add($"static_upg_product={BoolToStr(StaticSaveData.Instance.UpgradeProductDelivered)}");
                if (!string.IsNullOrEmpty(StaticSaveData.Instance.ThreadOrder))
                    parts.Add($"static_thread_order={StaticSaveData.Instance.ThreadOrder}");
                if (!string.IsNullOrEmpty(StaticSaveData.Instance.IntroQuestGuid))
                    parts.Add($"static_intro_qg={StaticSaveData.Instance.IntroQuestGuid}");
                if (!string.IsNullOrEmpty(StaticSaveData.Instance.Upgrade1QuestGuid))
                    parts.Add($"static_upg1_qg={StaticSaveData.Instance.Upgrade1QuestGuid}");
                if (!string.IsNullOrEmpty(StaticSaveData.Instance.Upgrade2QuestGuid))
                    parts.Add($"static_upg2_qg={StaticSaveData.Instance.Upgrade2QuestGuid}");
            }

            if (PropertySaveData.Instance != null)
            {
                // Listed == property record exists (Static has offered it in the OTC
                // message thread). Owned == the player has purchased it. Clients need
                // both states so the Static message thread shows warehouse/dispensary
                // listing cards before purchase — without the "_listed" keys below
                // the client's PropertySaveData has no record for listing-only
                // properties and ReconcileHostThread hides those threads entirely.
                parts.Add($"prop_shack_listed={BoolToStr(PropertySaveData.Instance.GetProperty(PropertySaveData.ShackId) != null)}");
                parts.Add($"prop_shack={BoolToStr(PropertySaveData.Instance.IsPropertyOwned(PropertySaveData.ShackId))}");
                parts.Add($"prop_warehouse_listed={BoolToStr(PropertySaveData.Instance.GetProperty(PropertySaveData.WarehouseId) != null)}");
                parts.Add($"prop_warehouse={BoolToStr(PropertySaveData.Instance.IsPropertyOwned(PropertySaveData.WarehouseId))}");
                parts.Add($"prop_dispensary_listed={BoolToStr(PropertySaveData.Instance.GetProperty(PropertySaveData.DispensaryId) != null)}");
                parts.Add($"prop_dispensary={BoolToStr(PropertySaveData.Instance.IsPropertyOwned(PropertySaveData.DispensaryId))}");
            }

            if (VicSaveData.Instance != null)
            {
                parts.Add($"vic_texted={BoolToStr(VicSaveData.Instance.HasBeenTexted)}");
                parts.Add($"vic_accepted={BoolToStr(VicSaveData.Instance.QuestAccepted)}");
                parts.Add($"vic_unlocked={BoolToStr(VicSaveData.Instance.Unlocked)}");
                parts.Add($"vic_trust={VicSaveData.Instance.TrustLevel}");
                if (!string.IsNullOrEmpty(VicSaveData.Instance.QuestGuid))
                    parts.Add($"vic_qg={VicSaveData.Instance.QuestGuid}");
            }

            if (BellaSaveData.Instance != null)
            {
                parts.Add($"bella_stage={BellaSaveData.Instance.Stage}");
                parts.Add($"bella_unlocked={BoolToStr(BellaSaveData.Instance.NightMarketUnlocked)}");
                if (!string.IsNullOrEmpty(BellaSaveData.Instance.QuestGuid))
                    parts.Add($"bella_qg={BellaSaveData.Instance.QuestGuid}");
            }

            string despIds = DesperationManager.GetDesperateIdsForSync();
            if (!string.IsNullOrEmpty(despIds))
                parts.Add($"desp_ids={despIds}");

            // Shack switch states
            parts.Add($"shack_lights={BoolToStr(Logic.Placement.WestvilleShack.AreLightsOn)}");
            parts.Add($"shack_open={BoolToStr(Logic.Placement.WestvilleShack.IsStoreOpen)}");

            // Shack styles
            if (!string.IsNullOrEmpty(Logic.Placement.WestvilleShack.CurrentLightingStyleId))
                parts.Add($"shack_lighting={Logic.Placement.WestvilleShack.CurrentLightingStyleId}");
            if (!string.IsNullOrEmpty(Logic.Placement.WestvilleShack.CurrentExteriorWallStyleId))
                parts.Add($"shack_ext_wall={Logic.Placement.WestvilleShack.CurrentExteriorWallStyleId}");
            if (!string.IsNullOrEmpty(Logic.Placement.WestvilleShack.CurrentInteriorWallStyleId))
                parts.Add($"shack_int_wall={Logic.Placement.WestvilleShack.CurrentInteriorWallStyleId}");
            if (!string.IsNullOrEmpty(Logic.Placement.WestvilleShack.CurrentFloorStyleId))
                parts.Add($"shack_floor={Logic.Placement.WestvilleShack.CurrentFloorStyleId}");

            // Dispensary switch states
            parts.Add($"disp_lights={BoolToStr(Logic.Placement.Dispensary.AreLightsOn)}");
            parts.Add($"disp_open={BoolToStr(Logic.Placement.Dispensary.IsStoreOpen)}");

            // Dispensary custom display name
            var dispName = PropertySaveData.Instance?.DispensaryDisplayName;
            if (!string.IsNullOrEmpty(dispName) && dispName != "Dispensary" && dispName != "Big Dispensary")
                parts.Add($"disp_name={dispName}");

            // Dispensary sign colors
            if (PropertySaveData.Instance != null)
            {
                var stc = UnityEngine.ColorUtility.ToHtmlStringRGB(PropertySaveData.Instance.SignTextColor);
                parts.Add($"disp_stc={stc}");
                var sbc = UnityEngine.ColorUtility.ToHtmlStringRGB(PropertySaveData.Instance.SignBackColor);
                parts.Add($"disp_sbc={sbc}");
            }

            // Warehouse switch states
            parts.Add($"wh_lights={BoolToStr(Logic.Placement.OTCWarehouse.AreLightsOn)}");

            // Dispensary lighting style
            if (!string.IsNullOrEmpty(Logic.Placement.Dispensary.CurrentLightingStyleId))
                parts.Add($"disp_lighting={Logic.Placement.Dispensary.CurrentLightingStyleId}");

            // Dispensary wall/floor styles
            if (!string.IsNullOrEmpty(Logic.Placement.Dispensary.CurrentExteriorWallStyleId))
                parts.Add($"disp_ext_wall={Logic.Placement.Dispensary.CurrentExteriorWallStyleId}");
            if (!string.IsNullOrEmpty(Logic.Placement.Dispensary.CurrentInteriorWallStyleId))
                parts.Add($"disp_int_wall={Logic.Placement.Dispensary.CurrentInteriorWallStyleId}");
            if (!string.IsNullOrEmpty(Logic.Placement.Dispensary.CurrentFloorStyleId))
                parts.Add($"disp_floor={Logic.Placement.Dispensary.CurrentFloorStyleId}");

            // Checkout desk styles (per counter)
            var counters = Logic.Placement.CheckoutCounter.AllCounters;
            for (int i = 0; i < counters.Count; i++)
            {
                parts.Add($"desk_style_{i}={counters[i].CurrentDeskStyleId}");
            }

            // Manager data is on per-manager SyncVar slots (_mgrSlots) to avoid lobby data truncation.

            // Budtender assignments (tiny payload, max ~50 chars)
            string btState = Logic.BudtenderController.Serialize();
            if (!string.IsNullOrEmpty(btState))
                parts.Add($"budtenders={btState}");

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
                bool? upgAccepted = state.TryGetValue("static_upg_accepted", out var ua) ? StrToBool(ua) : (bool?)null;
                int nextPayment = state.TryGetValue("static_next_payment", out var npStr) && int.TryParse(npStr, out var npVal) ? npVal : -1;
                int dayPass = state.TryGetValue("static_day_pass", out var dpStr) && int.TryParse(dpStr, out var dpVal) ? dpVal : -1;

                bool? t1Money = state.TryGetValue("static_t1_money", out var t1m) ? StrToBool(t1m) : (bool?)null;
                bool? t1Product = state.TryGetValue("static_t1_product", out var t1p) ? StrToBool(t1p) : (bool?)null;
                bool? upgMoney = state.TryGetValue("static_upg_money", out var um) ? StrToBool(um) : (bool?)null;
                bool? upgProduct = state.TryGetValue("static_upg_product", out var up) ? StrToBool(up) : (bool?)null;

                string staticIntroQg = state.TryGetValue("static_intro_qg", out var siqg) ? siqg : null;
                string staticUpg1Qg = state.TryGetValue("static_upg1_qg", out var su1qg) ? su1qg : null;
                string staticUpg2Qg = state.TryGetValue("static_upg2_qg", out var su2qg) ? su2qg : null;
                string staticThreadOrder = state.TryGetValue("static_thread_order", out var sto) ? sto : null;

                StaticSaveData.Instance.ApplyHostState(
                    questTriggered: triggered,
                    introCompleted: intro,
                    crmTier: tier,
                    saasActive: saas,
                    upgradeAvailable: upgrade,
                    saasNextPaymentDay: nextPayment,
                    dayPassCount: dayPass,
                    tier1MoneyPaid: t1Money,
                    tier1ProductDelivered: t1Product,
                    upgradeAccepted: upgAccepted,
                    upgradeMoneyPaid: upgMoney,
                    upgradeProductDelivered: upgProduct,
                    hostIntroQuestGuid: staticIntroQg,
                    hostUpgrade1QuestGuid: staticUpg1Qg,
                    hostUpgrade2QuestGuid: staticUpg2Qg,
                    threadOrder: staticThreadOrder);
            }

            // Apply property listing + ownership state from host. Both flags
            // matter for the OTC Static message thread: "listed" makes the
            // listing card appear, "owned" marks it as purchased. Previously
            // clients only received ownership, so warehouse/dispensary
            // listing cards were invisible until purchase.
            if (PropertySaveData.Instance != null)
            {
                bool shackListed = state.TryGetValue("prop_shack_listed", out var psl) && StrToBool(psl);
                bool shackOwned = state.TryGetValue("prop_shack", out var pso) && StrToBool(pso);
                PropertySaveData.Instance.ApplyHostPropertyState(PropertySaveData.ShackId, shackListed, shackOwned);

                bool warehouseListed = state.TryGetValue("prop_warehouse_listed", out var pwl) && StrToBool(pwl);
                bool warehouseOwned = state.TryGetValue("prop_warehouse", out var pwo) && StrToBool(pwo);
                PropertySaveData.Instance.ApplyHostPropertyState(PropertySaveData.WarehouseId, warehouseListed, warehouseOwned);

                bool dispensaryListed = state.TryGetValue("prop_dispensary_listed", out var pdl) && StrToBool(pdl);
                bool dispensaryOwned = state.TryGetValue("prop_dispensary", out var pdo) && StrToBool(pdo);
                PropertySaveData.Instance.ApplyHostPropertyState(PropertySaveData.DispensaryId, dispensaryListed, dispensaryOwned);
            }

            // Rebuild the OTC Static message thread from the now-authoritative
            // local state. This used to call ReconstructClientThread directly
            // with a long parameter list duplicated from the sync blob, but
            // the list was missing warehouse/dispensary listed+owned flags so
            // the client's thread was incomplete. Calling ReconcileHostThread
            // is safe on clients because StaticSaveData + PropertySaveData
            // have just been synced above, making it a single authoritative
            // rebuild path shared with the host.
            //
            // When Instance is null we silently skip: on initial lobby join
            // the SyncVar can fire before StaticThreadSaveData has loaded.
            // All three relevant Saveables (this one, PropertySaveData,
            // StaticSaveData) call ApplyPendingGameState in their OnLoaded,
            // so the state we cached in HandleStateChanged (_pendingGameState)
            // will be re-applied once the last Saveable is ready and the
            // reconcile will run successfully then.
            StaticThreadSaveData.Instance?.ReconcileHostThread();

            if (VicSaveData.Instance != null)
            {
                bool texted = state.TryGetValue("vic_texted", out var vt) && StrToBool(vt);
                bool questAccepted = state.TryGetValue("vic_accepted", out var va) && StrToBool(va);
                bool unlocked = state.TryGetValue("vic_unlocked", out var vu) && StrToBool(vu);
                int trust = state.TryGetValue("vic_trust", out var vtrust) && int.TryParse(vtrust, out var trustVal) ? trustVal : -1;
                string vicQg = state.TryGetValue("vic_qg", out var vqg) ? vqg : null;

                VicSaveData.Instance.ApplyHostState(
                    hasBeenTexted: texted,
                    questAccepted: questAccepted,
                    unlocked: unlocked,
                    trustLevel: trust,
                    hostQuestGuid: vicQg);
            }

            if (BellaSaveData.Instance != null)
            {
                int bellaStage = state.TryGetValue("bella_stage", out var bs) && int.TryParse(bs, out var bsVal) ? bsVal : 0;
                bool bellaUnlocked = state.TryGetValue("bella_unlocked", out var bu) && StrToBool(bu);
                string bellaQg = state.TryGetValue("bella_qg", out var bqg) ? bqg : null;
                BellaSaveData.Instance.ApplyHostState(bellaStage, bellaUnlocked, bellaQg);
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

            // Shack switch states
            if (state.TryGetValue("shack_lights", out var sl))
                Logic.Placement.WestvilleShack.SetLightsFromSync(StrToBool(sl));
            if (state.TryGetValue("shack_open", out var so))
                Logic.Placement.WestvilleShack.SetStoreOpen(StrToBool(so));

            // Shack styles
            if (state.TryGetValue("shack_lighting", out var shackLighting)
                && !string.IsNullOrEmpty(shackLighting)
                && shackLighting != Logic.Placement.WestvilleShack.CurrentLightingStyleId)
            {
                var lightStyle = Logic.Placement.LightingStyle.Get(shackLighting);
                Logic.Placement.WestvilleShack.ApplyLightingStyle(lightStyle);
            }

            if (state.TryGetValue("shack_ext_wall", out var shackExtWall)
                && !string.IsNullOrEmpty(shackExtWall)
                && shackExtWall != Logic.Placement.WestvilleShack.CurrentExteriorWallStyleId)
            {
                var style = Logic.Placement.WallStyle.GetExterior(shackExtWall);
                var mat = S1MAPI.S1.Materials.Find(style.MaterialName);
                if (mat != null)
                {
                    Logic.Placement.WestvilleShack.SwapExteriorWallMaterial(mat);
                    Logic.Placement.WestvilleShack.CurrentExteriorWallStyleId = shackExtWall;
                }
            }

            if (state.TryGetValue("shack_int_wall", out var shackIntWall)
                && !string.IsNullOrEmpty(shackIntWall)
                && shackIntWall != Logic.Placement.WestvilleShack.CurrentInteriorWallStyleId)
            {
                var style = Logic.Placement.WallStyle.GetInterior(shackIntWall);
                var mat = S1MAPI.S1.Materials.Find(style.MaterialName);
                if (mat != null)
                {
                    Logic.Placement.WestvilleShack.SwapInteriorWallMaterial(mat);
                    Logic.Placement.WestvilleShack.CurrentInteriorWallStyleId = shackIntWall;
                }
            }

            if (state.TryGetValue("shack_floor", out var shackFloor)
                && !string.IsNullOrEmpty(shackFloor)
                && shackFloor != Logic.Placement.WestvilleShack.CurrentFloorStyleId)
            {
                var style = Logic.Placement.FloorStyle.Get(shackFloor);
                var mat = S1MAPI.S1.Materials.Find(style.MaterialName);
                if (mat != null)
                {
                    Logic.Placement.WestvilleShack.SwapFloorMaterial(mat);
                    Logic.Placement.WestvilleShack.CurrentFloorStyleId = shackFloor;
                }
            }

            // Dispensary switch states
            if (state.TryGetValue("disp_lights", out var dl))
                Logic.Placement.Dispensary.SetLightsFromSync(StrToBool(dl));
            if (state.TryGetValue("disp_open", out var dso))
                Logic.Placement.Dispensary.SetStoreOpen(StrToBool(dso));

            // Dispensary custom display name
            if (state.TryGetValue("disp_name", out var syncDispName))
            {
                if (PropertySaveData.Instance != null)
                    PropertySaveData.Instance.DispensaryDisplayName = syncDispName;
                Logic.Placement.Dispensary.UpdateSignText(syncDispName);
            }

            // Dispensary sign colors
            if (state.TryGetValue("disp_stc", out var syncStc) && PropertySaveData.Instance != null)
            {
                if (UnityEngine.ColorUtility.TryParseHtmlString("#" + syncStc, out var tc))
                {
                    PropertySaveData.Instance.SignTextColor = tc;
                    Logic.Placement.Dispensary.UpdateSignColors(tc, null);
                }
            }
            if (state.TryGetValue("disp_sbc", out var syncSbc) && PropertySaveData.Instance != null)
            {
                if (UnityEngine.ColorUtility.TryParseHtmlString("#" + syncSbc, out var bc))
                {
                    PropertySaveData.Instance.SignBackColor = bc;
                    Logic.Placement.Dispensary.UpdateSignColors(null, bc);
                }
            }

            // Warehouse switch states
            if (state.TryGetValue("wh_lights", out var wl))
                Logic.Placement.OTCWarehouse.SetLightsFromSync(StrToBool(wl));

            // Dispensary lighting style
            if (state.TryGetValue("disp_lighting", out var dispLighting)
                && !string.IsNullOrEmpty(dispLighting)
                && dispLighting != Logic.Placement.Dispensary.CurrentLightingStyleId)
            {
                var lightStyle = Logic.Placement.LightingStyle.Get(dispLighting);
                Logic.Placement.Dispensary.ApplyLightingStyle(lightStyle);
            }

            // Dispensary wall/floor styles
            if (state.TryGetValue("disp_ext_wall", out var extWall)
                && !string.IsNullOrEmpty(extWall)
                && extWall != Logic.Placement.Dispensary.CurrentExteriorWallStyleId)
            {
                var style = Logic.Placement.WallStyle.GetExterior(extWall);
                var mat = S1MAPI.S1.Materials.Find(style.MaterialName);
                if (mat != null)
                {
                    Logic.Placement.Dispensary.SwapExteriorWallMaterial(mat);
                    Logic.Placement.Dispensary.CurrentExteriorWallStyleId = extWall;
                }
            }

            if (state.TryGetValue("disp_int_wall", out var intWall)
                && !string.IsNullOrEmpty(intWall)
                && intWall != Logic.Placement.Dispensary.CurrentInteriorWallStyleId)
            {
                var style = Logic.Placement.WallStyle.GetInterior(intWall);
                var mat = S1MAPI.S1.Materials.Find(style.MaterialName);
                if (mat != null)
                {
                    Logic.Placement.Dispensary.SwapInteriorWallMaterial(mat);
                    Logic.Placement.Dispensary.CurrentInteriorWallStyleId = intWall;
                }
            }

            if (state.TryGetValue("disp_floor", out var floorStyle)
                && !string.IsNullOrEmpty(floorStyle)
                && floorStyle != Logic.Placement.Dispensary.CurrentFloorStyleId)
            {
                var style = Logic.Placement.FloorStyle.Get(floorStyle);
                var mat = S1MAPI.S1.Materials.Find(style.MaterialName);
                if (mat != null)
                {
                    Logic.Placement.Dispensary.SwapFloorMaterial(mat);
                    Logic.Placement.Dispensary.CurrentFloorStyleId = floorStyle;
                }
            }

            // Checkout desk styles (per counter)
            {
                var deskCounters = Logic.Placement.CheckoutCounter.AllCounters;
                for (int i = 0; i < deskCounters.Count; i++)
                {
                    if (!state.TryGetValue($"desk_style_{i}", out var deskStyle) || string.IsNullOrEmpty(deskStyle))
                        continue;

                    if (deskCounters[i].CurrentDeskStyleId != deskStyle)
                    {
                        var style = Logic.Placement.DeskStyle.Get(deskStyle);
                        deskCounters[i].SwapDesk(style);
                    }
                }
            }

            // Manager data is on per-manager SyncVar slots — handled in HandleManagerSlotChanged.

            // Budtender state (client-side: spawn/despawn to match host)
            if (state.TryGetValue("budtenders", out var btData))
                Logic.BudtenderController.Deserialize(btData ?? "");
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
