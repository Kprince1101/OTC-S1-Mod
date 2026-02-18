using Il2CppSteamworks;
using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.Utilities;
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

    /// <summary>
    /// Encapsulates all SteamNetworkLib interactions. This class is ONLY
    /// loaded/JIT'd when the SteamNetworkLib assembly is present at runtime.
    /// ConfigSyncData calls into this via guarded [NoInlining] methods to
    /// enforce the type isolation boundary.
    /// </summary>
    internal static class NetworkSyncBridge
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:NetworkSync");

        private static bool _networkInitialized;
        private static bool _initialSyncDone;

        private static SteamNetworkClient _netClient;
        private static HostSyncVar<string> _configVar;
        private static HostSyncVar<string> _stateVar;
        private static HostSyncVar<string> _drifterVar;
        private static HostSyncVar<string> _mgrMsgVar;
        private static HostSyncVar<string> _drifterMsgVar;
        private static ClientSyncVar<string> _actionVar;

        internal const int ManagerSlotCount = 8;
        private static readonly HostSyncVar<string>[] _mgrSlots = new HostSyncVar<string>[ManagerSlotCount];

        private static readonly NetworkSyncOptions _syncOptions = new NetworkSyncOptions
        {
            KeyPrefix = "OTC_",
            Serializer = new RawStringSerializer()
        };

        private static readonly Dictionary<ulong, string> _processedActions = new Dictionary<ulong, string>();
        private static long _lastActionPollTick;
        private const long ACTION_POLL_INTERVAL_MS = 1000;

        // ==================================================================
        // Lifecycle
        // ==================================================================

        /// <summary>
        /// Initializes SteamNetworkClient and SyncVars. Called from Core.OnLateUpdate()
        /// so it runs on BOTH host and client regardless of whether the
        /// Saveable lifecycle fires.
        /// </summary>
        internal static void EnsureNetworkReady()
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
                _mgrMsgVar = _netClient.CreateHostSyncVar("mgrmsg", "", _syncOptions);
                _drifterMsgVar = _netClient.CreateHostSyncVar("driftermsg", "", _syncOptions);
                _actionVar = _netClient.CreateClientSyncVar("action", "", _syncOptions);

                for (int i = 0; i < ManagerSlotCount; i++)
                {
                    _mgrSlots[i] = _netClient.CreateHostSyncVar($"m{i}", "", _syncOptions);
                    int slot = i; // capture for closure
                    _mgrSlots[i].OnSyncError += (ex) => Logger.Warning($"Manager slot {slot} SyncVar error: {ex.Message}");
                    _mgrSlots[i].OnValueChanged += (oldVal, newVal) => OnManagerSlotChanged(slot, oldVal, newVal);
                }

                // Diagnostic error handlers — surface silent SyncVar failures.
                _configVar.OnSyncError += (ex) => Logger.Warning($"Config SyncVar error: {ex.Message}");
                _stateVar.OnSyncError += (ex) => Logger.Warning($"State SyncVar error: {ex.Message}");
                _drifterVar.OnSyncError += (ex) => Logger.Warning($"Drifter SyncVar error: {ex.Message}");
                _mgrMsgVar.OnSyncError += (ex) => Logger.Warning($"MgrMsg SyncVar error: {ex.Message}");
                _drifterMsgVar.OnSyncError += (ex) => Logger.Warning($"DrifterMsg SyncVar error: {ex.Message}");
                _actionVar.OnSyncError += (ex) => Logger.Warning($"Action SyncVar error: {ex.Message}");
                _configVar.OnWriteIgnored += (_) => { if (Config.VerboseLogging.Value) Logger.Msg("Config SyncVar write ignored (not lobby owner)."); };
                _stateVar.OnWriteIgnored += (_) => { if (Config.VerboseLogging.Value) Logger.Msg("State SyncVar write ignored (not lobby owner)."); };

                // Client callbacks: receive config and state from host.
                _configVar.OnValueChanged += OnConfigChanged;
                _stateVar.OnValueChanged += OnStateChanged;
                _drifterVar.OnValueChanged += OnDrifterStateChanged;
                _mgrMsgVar.OnValueChanged += OnManagerMessageChanged;
                _drifterMsgVar.OnValueChanged += OnDrifterMessageChanged;

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
        internal static void ProcessMessages()
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
                        ConfigSyncData.ClearOverridesForHost();

                    string cfg = ConfigSyncData.GetSerializedConfig();
                    if (!string.IsNullOrEmpty(cfg) && _configVar != null)
                        _configVar.Value = cfg;

                    string state = ConfigSyncData.GetSerializedGameState();
                    if (!string.IsNullOrEmpty(state) && _stateVar != null)
                        _stateVar.Value = state;

                    string drifterState = ConfigSyncData.GetSerializedDrifterState();
                    if (_drifterVar != null)
                        _drifterVar.Value = drifterState;

                    // Push per-manager slots
                    ConfigSyncData.WriteInitialManagerSlots();

                    // Refresh — picks up values already in lobby data (covers
                    // client reading host values, and host reading its own on rejoin).
                    _configVar?.Refresh();
                    _stateVar?.Refresh();
                    _drifterVar?.Refresh();
                    for (int i = 0; i < ManagerSlotCount; i++)
                        _mgrSlots[i]?.Refresh();
                    _mgrMsgVar?.Refresh();
                    _drifterMsgVar?.Refresh();
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
                            ConfigSyncData.HandleActionReceived(action);
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
        internal static void Cleanup()
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
            for (int i = 0; i < ManagerSlotCount; i++)
                _mgrSlots[i] = null;
            _mgrMsgVar = null;
            _drifterMsgVar = null;
            _actionVar = null;
            _processedActions.Clear();
            _networkInitialized = false;
            _initialSyncDone = false;
        }

        // ==================================================================
        // Publish methods (ConfigSyncData → SyncVars)
        // ==================================================================

        internal static void PushConfig(string payload)
        {
            if (_configVar != null)
                _configVar.Value = payload;
        }

        internal static void PushState(string payload)
        {
            if (_stateVar != null)
                _stateVar.Value = payload;
        }

        internal static void PushDrifterState(string payload)
        {
            if (_drifterVar != null)
                _drifterVar.Value = payload;
        }

        internal static void PushManagerSlot(int slot, string payload)
        {
            if (slot >= 0 && slot < ManagerSlotCount && _mgrSlots[slot] != null)
                _mgrSlots[slot].Value = payload;
        }

        internal static void ClearManagerSlot(int slot)
        {
            if (slot >= 0 && slot < ManagerSlotCount && _mgrSlots[slot] != null)
                _mgrSlots[slot].Value = "";
        }

        internal static void PushManagerMessages(string payload)
        {
            if (_mgrMsgVar != null)
                _mgrMsgVar.Value = payload;
        }

        internal static void PushDrifterMessages(string payload)
        {
            if (_drifterMsgVar != null)
                _drifterMsgVar.Value = payload;
        }

        internal static void SendAction(string value)
        {
            try
            {
                if (_actionVar == null)
                {
                    Logger.Warning($"SendAction: SyncVar not initialized yet.");
                    return;
                }
                _actionVar.Value = value;
                if (Config.VerboseLogging.Value)
                    Logger.Msg($"Sent quest action via SyncVar: {value}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"SendAction failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Push initial values on save load (called from ConfigSyncData.OnLoaded).
        /// Manager slots are pushed separately via WriteInitialManagerSlots().
        /// </summary>
        internal static void PushOnLoaded(string configPayload, string statePayload,
            string drifterPayload)
        {
            if (_configVar == null) return;
            _configVar.Value = configPayload;
            _stateVar.Value = statePayload;
            if (_drifterVar != null)
                _drifterVar.Value = drifterPayload;
            if (Config.VerboseLogging.Value)
                Logger.Msg("Pushed config, game state, and drifter state to SyncVars.");
        }

        // ==================================================================
        // SyncVar callbacks (route to ConfigSyncData logic)
        // ==================================================================

        private static void OnConfigChanged(string oldValue, string newValue)
        {
            if (_netClient?.IsHost == true) return;
            if (string.IsNullOrEmpty(newValue)) return;
            ConfigSyncData.HandleConfigChanged(newValue);
        }

        private static void OnStateChanged(string oldValue, string newValue)
        {
            if (_netClient?.IsHost == true) return;
            if (string.IsNullOrEmpty(newValue)) return;
            ConfigSyncData.HandleStateChanged(newValue);
        }

        private static void OnDrifterStateChanged(string oldValue, string newValue)
        {
            if (_netClient?.IsHost == true) return;
            if (string.IsNullOrEmpty(newValue)) return;
            ConfigSyncData.HandleDrifterStateChanged(newValue);
        }

        private static void OnManagerSlotChanged(int slot, string oldValue, string newValue)
        {
            if (_netClient?.IsHost == true) return;
            ConfigSyncData.HandleManagerSlotChanged(slot, newValue ?? "");
        }

        private static void OnManagerMessageChanged(string oldValue, string newValue)
        {
            if (_netClient?.IsHost == true) return;
            if (string.IsNullOrEmpty(newValue)) return;
            ConfigSyncData.HandleManagerMessageChanged(newValue);
        }

        private static void OnDrifterMessageChanged(string oldValue, string newValue)
        {
            if (_netClient?.IsHost == true) return;
            if (string.IsNullOrEmpty(newValue)) return;
            ConfigSyncData.HandleDrifterMessageChanged(newValue);
        }

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
                ConfigSyncData.HandleActionReceived(action);
            }
            catch (Exception ex)
            {
                Logger.Warning($"OnActionChanged failed: {ex.Message}");
            }
        }
    }
}
