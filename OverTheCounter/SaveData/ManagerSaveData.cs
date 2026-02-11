using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Property;
using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.Utilities;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace OverTheCounter.SaveData
{
    public class ManagerSaveData : Saveable
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("ManagerSaveData");

        [SaveableField("manager_state")]
        private string _managerState;

        [SaveableField("manager_id_counter")]
        private int _idCounter;

        [SaveableField("manager_paid_today")]
        private string _paidToday;

        [SaveableField("manager_npc_inventories")]
        private string _npcInventories;

        public static ManagerSaveData Instance { get; private set; }

        internal static void ResetInstance() => Instance = null;

        private bool _needsRespawn;
        private float _respawnStartTime;

        // Managers whose config GUID resolution failed — retry on subsequent ticks
        private readonly List<(ManagerInstance manager, string configStr)> _pendingConfigs = new();
        private float _pendingConfigStartTime;

        // Pending NPC inventory restoration — deferred until containers resolve
        private class PendingNpcRestore
        {
            public string ManagerId;
            public float Cash;
            public readonly List<(string itemId, int qty)> Items = new();
        }
        private readonly List<PendingNpcRestore> _pendingNpcRestores = new();
        private float _pendingNpcRestoreStartTime;

        public ManagerSaveData()
        {
            Instance = this;
        }

        protected override void OnLoaded()
        {
            Instance = this;

            if (!string.IsNullOrEmpty(_managerState))
            {
                Logger.Msg($"OnLoaded: manager state found ({_managerState.Length} chars), deferring respawn");
                _needsRespawn = true;
                _respawnStartTime = UnityEngine.Time.time;
            }
        }

        public void Tick()
        {
            if (_needsRespawn)
                TryRespawn();

            if (_pendingConfigs.Count > 0)
                RetryPendingConfigs();

            if (_pendingNpcRestores.Count > 0)
                RetryPendingNpcRestores();

            // Keep paid-today fresh so mid-day saves don't lose data (trivially cheap)
            if (!_needsRespawn && NetworkHelper.IsHost && ManagerInstance.Active.Count > 0)
            {
                var paidIds = new List<string>();
                foreach (var mgr in ManagerInstance.Active.Values)
                    if (mgr.PaidForToday) paidIds.Add(mgr.Id);
                _paidToday = paidIds.Count > 0 ? string.Join(",", paidIds) : "";
            }
        }

        private void TryRespawn()
        {
            // Only host respawns — clients adopt via SyncVar
            if (!NetworkHelper.IsHost)
            {
                _needsRespawn = false;
                return;
            }

            // Wait for game to be fully loaded
            try
            {
                var lm = Il2CppScheduleOne.Persistence.LoadManager.Instance;
                if (lm == null || !lm.IsGameLoaded)
                {
                    if (UnityEngine.Time.time - _respawnStartTime > 30f)
                    {
                        Logger.Warning("TryRespawn: timed out waiting for game load");
                        _needsRespawn = false;
                    }
                    return;
                }
            }
            catch
            {
                if (UnityEngine.Time.time - _respawnStartTime > 30f)
                {
                    Logger.Warning("TryRespawn: timed out (LoadManager unavailable)");
                    _needsRespawn = false;
                }
                return;
            }

            // Wait for businesses to exist
            if (Business.OwnedBusinesses == null || Business.OwnedBusinesses.Count == 0)
            {
                if (UnityEngine.Time.time - _respawnStartTime > 30f)
                {
                    Logger.Warning("TryRespawn: timed out waiting for businesses");
                    _needsRespawn = false;
                }
                return;
            }

            _needsRespawn = false;

            // Restore ID counter
            if (ManagerController.Instance != null)
                ManagerController.Instance.RestoreIdCounter(_idCounter);

            Logger.Msg($"TryRespawn: restoring managers from saved state");

            var entries = _managerState.Split(';');
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry)) continue;

                try
                {
                    RespawnManager(entry);
                }
                catch (Exception ex)
                {
                    Logger.Error($"TryRespawn: failed to restore entry '{entry}': {ex.Message}");
                }
            }

            // Restore paid-for-today status
            if (!string.IsNullOrEmpty(_paidToday))
            {
                var paidIds = new HashSet<string>(_paidToday.Split(','));
                foreach (var mgr in ManagerInstance.Active.Values)
                {
                    if (paidIds.Contains(mgr.Id))
                    {
                        mgr.PaidForToday = true;
                        mgr.State = ManagerState.Idle;
                    }
                }
            }

            // Queue NPC inventory restoration — deferred until containers resolve
            if (!string.IsNullOrEmpty(_npcInventories))
            {
                Logger.Msg($"TryRespawn: queuing NPC inventory restore ({_npcInventories.Length} chars): {_npcInventories}");
                ParsePendingNpcInventories(_npcInventories);
            }

            // Sync to clients
            ConfigSyncData.Instance?.PublishManagerState();
        }

        private void RespawnManager(string entry)
        {
            // Format: id:seed:biz:netObjId:configData
            var parts = entry.Split(':');
            if (parts.Length < 4)
            {
                Logger.Warning($"RespawnManager: malformed entry '{entry}'");
                return;
            }

            string id = parts[0];
            if (!int.TryParse(parts[1], out int seed))
            {
                Logger.Warning($"RespawnManager: bad seed in '{entry}'");
                return;
            }
            string bizCode = parts[2];
            // parts[3] = netObjId (irrelevant for respawn — gets new one from FishNet)

            // Rejoin config data from index 4 (config contains ':' separators for item thresholds)
            string encodedConfig = parts.Length > 4
                ? string.Join(":", parts, 4, parts.Length - 4)
                : "";

            // Skip if already active
            if (ManagerInstance.Active.ContainsKey(id))
            {
                Logger.Warning($"RespawnManager: {id} already active, skipping");
                return;
            }

            // Find business by property code
            Business business = null;
            foreach (var biz in Business.OwnedBusinesses)
            {
                if (biz != null && string.Equals(biz.PropertyCode, bizCode, StringComparison.OrdinalIgnoreCase))
                {
                    business = biz;
                    break;
                }
            }

            if (business == null)
            {
                Logger.Warning($"RespawnManager: business '{bizCode}' not found for {id}");
                return;
            }

            var instance = ManagerInstance.Create(id, seed, business);
            if (instance == null)
            {
                Logger.Error($"RespawnManager: Create failed for {id}");
                return;
            }

            // Don't re-send greeting text
            instance.GreetingSent = true;

            // Apply config
            if (!string.IsNullOrEmpty(encodedConfig))
            {
                string decoded = ManagerInstance.DecodeConfig(encodedConfig);
                instance.Configuration.Deserialize(decoded);
                instance.ReconcileLockerFromConfig();

                // Check if locker GUID was present but didn't resolve (GUIDManager not ready)
                bool hadLockerGuid = HasLockerGuid(decoded);
                if (hadLockerGuid && !instance.HasLocker)
                {
                    _pendingConfigs.Add((instance, decoded));
                    if (_pendingConfigs.Count == 1)
                        _pendingConfigStartTime = UnityEngine.Time.time;
                }
            }

            Logger.Msg($"RespawnManager: restored {id} at {bizCode} (seed={seed})");
        }

        /// <summary>
        /// Checks if a decoded config string contains a non-empty locker GUID.
        /// Config format: lockerGUID;supplyGUID|...
        /// </summary>
        private static bool HasLockerGuid(string decoded)
        {
            if (string.IsNullOrEmpty(decoded)) return false;
            int pipeIdx = decoded.IndexOf('|');
            string firstSegment = pipeIdx >= 0 ? decoded.Substring(0, pipeIdx) : decoded;
            int semiIdx = firstSegment.IndexOf(';');
            string lockerGuid = semiIdx >= 0 ? firstSegment.Substring(0, semiIdx) : firstSegment;
            return !string.IsNullOrEmpty(lockerGuid);
        }

        private void RetryPendingConfigs()
        {
            if (UnityEngine.Time.time - _pendingConfigStartTime > 30f)
            {
                Logger.Warning($"RetryPendingConfigs: timed out, clearing {_pendingConfigs.Count} pending");
                _pendingConfigs.Clear();
                return;
            }

            for (int i = _pendingConfigs.Count - 1; i >= 0; i--)
            {
                var (mgr, configStr) = _pendingConfigs[i];
                try
                {
                    mgr.Configuration.Deserialize(configStr);
                    mgr.ReconcileLockerFromConfig();

                    if (mgr.HasLocker)
                    {
                        Logger.Msg($"RetryPendingConfigs: {mgr.Id} locker resolved");
                        _pendingConfigs.RemoveAt(i);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"RetryPendingConfigs: {mgr.Id} retry failed: {ex.Message}");
                }
            }
        }

        // ==================================================================
        // NPC Inventory serialization — saves items/cash the manager is carrying
        // Format: mgrId=cash:itemId*qty+itemId*qty;mgrId=cash:itemId*qty
        // ==================================================================

        /// <summary>
        /// Serializes all managers' NPC inventory contents (items + cash).
        /// </summary>
        private static string SerializeNpcInventories()
        {
            var parts = new List<string>();

            foreach (var mgr in ManagerInstance.Active.Values)
            {
                try
                {
                    // Skip fired managers — they're walking away and shouldn't persist
                    if (mgr.State == ManagerState.Fired) continue;

                    if (mgr.GameNpc == null)
                    {
                        Logger.Warning($"SerializeNpcInv: {mgr.Id} GameNpc is null");
                        continue;
                    }

                    var npcInv = mgr.GameNpc.GetComponent<Il2CppScheduleOne.NPCs.NPCInventory>();
                    if (npcInv == null)
                    {
                        Logger.Warning($"SerializeNpcInv: {mgr.Id} NPCInventory component not found");
                        continue;
                    }

                    if (npcInv.ItemSlots == null)
                    {
                        Logger.Warning($"SerializeNpcInv: {mgr.Id} ItemSlots is null");
                        continue;
                    }

                    float cash = npcInv.GetCashInInventory();

                    // Aggregate items by ID (skip cash slots)
                    var items = new Dictionary<string, int>();
                    for (int i = 0; i < npcInv.ItemSlots.Count; i++)
                    {
                        var slot = npcInv.ItemSlots[i];
                        if (slot?.ItemInstance == null) continue;
                        if (slot.ItemInstance.TryCast<CashInstance>() != null) continue;

                        var def = slot.ItemInstance.Definition;
                        if (def == null) continue;

                        string itemId = def.ID;
                        if (string.IsNullOrEmpty(itemId)) continue;

                        if (items.ContainsKey(itemId))
                            items[itemId] += slot.Quantity;
                        else
                            items[itemId] = slot.Quantity;
                    }

                    // Only save if NPC is carrying something
                    if (cash <= 0f && items.Count == 0) continue;

                    string cashStr = cash.ToString("F0", CultureInfo.InvariantCulture);
                    var itemParts = new List<string>();
                    foreach (var kv in items)
                        itemParts.Add($"{kv.Key}*{kv.Value}");

                    parts.Add($"{mgr.Id}={cashStr}:{string.Join("+", itemParts)}");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"SerializeNpcInv: {mgr.Id} failed: {ex.Message}");
                }
            }

            return parts.Count > 0 ? string.Join(";", parts) : "";
        }

        /// <summary>
        /// Parses saved NPC inventory string into structured pending entries for deferred restoration.
        /// </summary>
        private void ParsePendingNpcInventories(string data)
        {
            _pendingNpcRestores.Clear();
            var entries = data.Split(';');

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry)) continue;

                try
                {
                    int eqIdx = entry.IndexOf('=');
                    if (eqIdx < 0) continue;

                    string mgrId = entry.Substring(0, eqIdx);
                    string rest = entry.Substring(eqIdx + 1);

                    int colonIdx = rest.IndexOf(':');
                    string cashStr = colonIdx >= 0 ? rest.Substring(0, colonIdx) : rest;
                    string itemStr = colonIdx >= 0 ? rest.Substring(colonIdx + 1) : "";

                    float cash = 0f;
                    if (!string.IsNullOrEmpty(cashStr))
                        float.TryParse(cashStr, NumberStyles.Float, CultureInfo.InvariantCulture, out cash);

                    var pending = new PendingNpcRestore { ManagerId = mgrId, Cash = cash };

                    if (!string.IsNullOrEmpty(itemStr))
                    {
                        foreach (var itemEntry in itemStr.Split('+'))
                        {
                            if (string.IsNullOrEmpty(itemEntry)) continue;
                            int starIdx = itemEntry.IndexOf('*');
                            if (starIdx < 0) continue;

                            string itemId = itemEntry.Substring(0, starIdx);
                            if (int.TryParse(itemEntry.Substring(starIdx + 1), out int qty) && qty > 0)
                                pending.Items.Add((itemId, qty));
                        }
                    }

                    if (cash > 0f || pending.Items.Count > 0)
                    {
                        _pendingNpcRestores.Add(pending);
                        Logger.Msg($"ParsePendingNpcInv: {mgrId} queued cash=${cash:F0}, {pending.Items.Count} item types");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"ParsePendingNpcInv: failed on '{entry}': {ex.Message}");
                }
            }

            if (_pendingNpcRestores.Count > 0)
                _pendingNpcRestoreStartTime = UnityEngine.Time.time;
        }

        /// <summary>
        /// Retries restoring saved items/cash into the NPC's inventory.
        /// Retries each tick until NPC inventory is available or 30s timeout.
        /// </summary>
        private void RetryPendingNpcRestores()
        {
            if (UnityEngine.Time.time - _pendingNpcRestoreStartTime > 30f)
            {
                Logger.Warning($"RetryPendingNpcRestores: timed out, {_pendingNpcRestores.Count} entries lost");
                _pendingNpcRestores.Clear();
                return;
            }

            for (int i = _pendingNpcRestores.Count - 1; i >= 0; i--)
            {
                var p = _pendingNpcRestores[i];

                if (!ManagerInstance.Active.TryGetValue(p.ManagerId, out var mgr))
                    continue; // manager not spawned yet, retry

                if (mgr.GameNpc == null)
                    continue; // NPC not ready yet, retry

                var npcInv = mgr.GameNpc.GetComponent<Il2CppScheduleOne.NPCs.NPCInventory>();
                if (npcInv == null)
                    continue; // NPCInventory component not ready yet, retry

                try
                {
                    // Restore cash into NPC inventory
                    if (p.Cash > 0f)
                    {
                        npcInv.AddCash(p.Cash);
                        Logger.Msg($"RestoreNpcInv: {p.ManagerId} restored ${p.Cash:F0} cash to NPC");
                        p.Cash = 0f;
                    }

                    // Restore items into NPC inventory
                    for (int j = p.Items.Count - 1; j >= 0; j--)
                    {
                        var (itemId, qty) = p.Items[j];
                        try
                        {
                            ManagerSupplyBehaviour.AddToNpcInventory(npcInv, itemId, qty, p.ManagerId);
                            Logger.Msg($"RestoreNpcInv: {p.ManagerId} restored {qty}x {itemId} to NPC");
                            p.Items.RemoveAt(j);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"RestoreNpcInv: {p.ManagerId} item '{itemId}' failed: {ex.Message}");
                            p.Items.RemoveAt(j);
                        }
                    }

                    // Entry fully handled
                    if (p.Cash <= 0f && p.Items.Count == 0)
                    {
                        _pendingNpcRestores.RemoveAt(i);
                        Logger.Msg($"RestoreNpcInv: {p.ManagerId} fully restored");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"RetryPendingNpcRestores: {p.ManagerId} error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Captures current manager state into saveable fields. Host-only.
        /// Called from PublishManagerState() and the SaveManager prefix patch.
        /// </summary>
        public void CaptureState()
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                _managerState = ManagerInstance.SerializeManagerState();
                _idCounter = ManagerController.Instance?.GetIdCounter() ?? 0;

                var paidIds = new List<string>();
                foreach (var mgr in ManagerInstance.Active.Values)
                    if (mgr.PaidForToday) paidIds.Add(mgr.Id);
                _paidToday = paidIds.Count > 0 ? string.Join(",", paidIds) : "";

                // Don't overwrite NPC inventories while deferred restoration is pending
                if (_pendingNpcRestores.Count == 0)
                    _npcInventories = SerializeNpcInventories();
            }
            catch (Exception ex)
            {
                Logger.Warning($"CaptureState failed: {ex.Message}");
            }
        }
    }
}
