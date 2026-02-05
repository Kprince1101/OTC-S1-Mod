using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Quests;
using MelonLoader;
using S1API.GameTime;
using S1API.Items;
using S1API.Products;
using System;
using System.Collections.Generic;
using System.Linq;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using UnityEngine;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// The "Director" system that manages Drifter NPCs.
    /// Spawns random NPCs at hotspots during the day who text the player for one-time deals.
    /// </summary>
    public class DrifterManager
    {
        private readonly MelonLogger.Instance _logger;

        // Active drifter tracking: DrifterId -> DrifterEvent
        private readonly Dictionary<string, DrifterEvent> _activeEvents = new();

        // Daily/hourly tracking
        private int _lastHourChecked = -1;
        private int _drifterIdCounter = 0;

        public static DrifterManager Instance { get; private set; }

        public DrifterManager(MelonLogger.Instance logger)
        {
            _logger = logger;
            Instance = this;

            TimeManager.OnTick += OnTimeTick;
            TimeManager.OnDayPass += OnDayPass;
        }

        private void OnTimeTick()
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                int currentTime = TimeManager.CurrentTime;
                int currentHour = currentTime / 100;

                // Hourly spawn roll during day phase
                if (currentHour != _lastHourChecked && IsDayPhase(currentTime))
                {
                    _lastHourChecked = currentHour;
                    TrySpawnDrifter();
                }

                // Process lifecycle for all active drifters
                ProcessDrifterLifecycles();
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] OnTimeTick error: {ex.Message}");
            }
        }

        private void OnDayPass()
        {
            if (!NetworkHelper.IsHost) return;
            _lastHourChecked = -1;
        }

        private bool IsDayPhase(int time24h)
        {
            return time24h >= Config.DrifterDayStartHour.Value && time24h < Config.DrifterDayEndHour.Value;
        }

        private void TrySpawnDrifter()
        {
            if (_activeEvents.Count >= Config.MaxActiveDrifters.Value)
                return;

            float roll = UnityEngine.Random.value;
            if (roll > Config.DrifterSpawnChancePerHour.Value)
                return;

            SpawnDrifter();
        }

        /// <summary>
        /// Spawns a new drifter NPC at a random hotspot.
        /// </summary>
        public void SpawnDrifter(DrifterType? forceType = null)
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                // Generate unique ID
                string drifterId = $"drifter_{TimeManager.ElapsedDays}_{_drifterIdCounter++}";

                // Select type
                DrifterType type = forceType ?? DrifterTypeWeights.GetRandomType();

                // Select hotspot (avoid occupied ones)
                var hotspot = GetAvailableHotspot();
                if (hotspot == null)
                {
                    _logger.Warning("[DrifterManager] No available hotspots for drifter spawn");
                    return;
                }

                // Generate seed from drifter ID for deterministic appearance
                int seed = drifterId.GetHashCode();
                int spawnTime = GetCurrentElapsedMinutes();

                // Calculate offer deadline
                int offerWindow = UnityEngine.Random.Range(
                    Config.DrifterOfferWindowMinMin.Value,
                    Config.DrifterOfferWindowMaxMin.Value + 1);
                int offerDeadline = spawnTime + offerWindow;

                // Create the event
                var evt = new DrifterEvent
                {
                    DrifterId = drifterId,
                    Type = type,
                    HotspotName = hotspot.Name,
                    Seed = seed,
                    SpawnTime = spawnTime,
                    OfferDeadline = offerDeadline,
                    DeliveryDeadline = 0, // Set on accept
                    LingerDeadline = 0,   // Set on completion/expiry
                    State = DrifterEventState.Spawned
                };

                _activeEvents[drifterId] = evt;

                // Create the NPC
                var drifter = DrifterInstance.Create(drifterId, type, hotspot, seed);
                if (drifter != null)
                {
                    // Schedule intro text after brief delay
                    evt.State = DrifterEventState.OfferPending;
                    evt.TextSendTime = spawnTime + 1; // 1 minute delay before text

                    _logger.Msg($"[DrifterManager] Spawned drifter {drifterId}: Type={type}, Hotspot={hotspot.Name}, OfferDeadline={offerWindow}min");

                    // Sync to clients
                    ConfigSyncData.Instance?.PublishGameState();
                }
                else
                {
                    _activeEvents.Remove(drifterId);
                    _logger.Error($"[DrifterManager] Failed to create drifter NPC {drifterId}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] SpawnDrifter failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private DrifterHotspots.Hotspot GetAvailableHotspot()
        {
            var occupiedHotspots = new HashSet<string>();
            foreach (var evt in _activeEvents.Values)
                occupiedHotspots.Add(evt.HotspotName);

            var availableHotspots = DrifterHotspots.AllHotspots
                .Where(h => !occupiedHotspots.Contains(h.Name))
                .ToList();

            if (availableHotspots.Count == 0)
                return null;

            return availableHotspots[UnityEngine.Random.Range(0, availableHotspots.Count)];
        }

        private void ProcessDrifterLifecycles()
        {
            int currentMinutes = GetCurrentElapsedMinutes();
            var toRemove = new List<string>();

            foreach (var kvp in _activeEvents)
            {
                var evt = kvp.Value;
                var drifter = DrifterInstance.Active.GetValueOrDefault(evt.DrifterId);

                switch (evt.State)
                {
                    case DrifterEventState.Spawned:
                    case DrifterEventState.OfferPending:
                        // Check if we should send intro text
                        if (evt.TextSendTime > 0 && currentMinutes >= evt.TextSendTime && !evt.TextSent)
                        {
                            SendIntroText(evt, drifter);
                            evt.TextSent = true;
                            evt.State = DrifterEventState.OfferSent;
                        }
                        // Check offer deadline
                        if (currentMinutes >= evt.OfferDeadline)
                        {
                            ExpireDrifterOffer(evt, drifter);
                        }
                        break;

                    case DrifterEventState.OfferSent:
                        // Check offer deadline
                        if (currentMinutes >= evt.OfferDeadline)
                        {
                            ExpireDrifterOffer(evt, drifter);
                        }
                        break;

                    case DrifterEventState.DealAccepted:
                        // Check delivery deadline
                        if (currentMinutes >= evt.DeliveryDeadline)
                        {
                            FailDrifterDelivery(evt, drifter);
                        }
                        break;

                    case DrifterEventState.DealCompleted:
                    case DrifterEventState.Lingering:
                        // Check linger deadline
                        if (currentMinutes >= evt.LingerDeadline)
                        {
                            DespawnDrifter(evt, drifter);
                            toRemove.Add(evt.DrifterId);
                        }
                        break;

                    case DrifterEventState.Despawning:
                        toRemove.Add(evt.DrifterId);
                        break;
                }
            }

            // Remove despawned drifters
            foreach (var id in toRemove)
                _activeEvents.Remove(id);

            if (toRemove.Count > 0)
                ConfigSyncData.Instance?.PublishGameState();
        }

        private void SendIntroText(DrifterEvent evt, DrifterInstance drifter)
        {
            if (drifter == null) return;

            try
            {
                string message = drifter.GetIntroTextMessage();
                drifter.SendTextMessage(message);
                _logger.Msg($"[DrifterManager] Sent intro text for drifter {evt.DrifterId}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DrifterManager] Failed to send intro text: {ex.Message}");
            }
        }

        private void ExpireDrifterOffer(DrifterEvent evt, DrifterInstance drifter)
        {
            _logger.Msg($"[DrifterManager] Offer expired for drifter {evt.DrifterId}");

            // Send expiry text
            if (drifter != null)
            {
                try
                {
                    drifter.SendTextMessage(drifter.GetExpiryTextMessage());
                }
                catch { }
            }

            // Start linger timer
            int lingerTime = UnityEngine.Random.Range(
                Config.DrifterLingerMinMin.Value,
                Config.DrifterLingerMaxMin.Value + 1);
            evt.LingerDeadline = GetCurrentElapsedMinutes() + lingerTime;
            evt.State = DrifterEventState.Lingering;

            if (drifter != null)
            {
                drifter.State = DrifterState.Lingering;
            }
        }

        private void FailDrifterDelivery(DrifterEvent evt, DrifterInstance drifter)
        {
            _logger.Msg($"[DrifterManager] Delivery failed for drifter {evt.DrifterId}");

            // Send failure text
            if (drifter != null)
            {
                try
                {
                    drifter.SendTextMessage("You're too slow. Deal's off.");
                }
                catch { }
            }

            // Start linger timer
            int lingerTime = UnityEngine.Random.Range(
                Config.DrifterLingerMinMin.Value,
                Config.DrifterLingerMaxMin.Value + 1);
            evt.LingerDeadline = GetCurrentElapsedMinutes() + lingerTime;
            evt.State = DrifterEventState.Lingering;

            if (drifter != null)
            {
                drifter.State = DrifterState.Lingering;
            }
        }

        private void DespawnDrifter(DrifterEvent evt, DrifterInstance drifter)
        {
            _logger.Msg($"[DrifterManager] Despawning drifter {evt.DrifterId}");
            evt.State = DrifterEventState.Despawning;

            if (drifter != null)
            {
                try
                {
                    drifter.Despawn();
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[DrifterManager] Error despawning drifter: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Called when a player accepts a drifter's deal.
        /// </summary>
        public void OnDealAccepted(string drifterId)
        {
            if (!NetworkHelper.IsHost) return;

            if (!_activeEvents.TryGetValue(drifterId, out var evt))
            {
                _logger.Warning($"[DrifterManager] OnDealAccepted: unknown drifter {drifterId}");
                return;
            }

            evt.State = DrifterEventState.DealAccepted;
            evt.DeliveryDeadline = GetCurrentElapsedMinutes() + Config.DrifterDeliveryDeadlineMin.Value;

            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
            if (drifter != null)
            {
                drifter.DealAccepted = true;
                drifter.State = DrifterState.DealAccepted;
            }

            _logger.Msg($"[DrifterManager] Deal accepted for drifter {drifterId}. Delivery deadline: {Config.DrifterDeliveryDeadlineMin.Value} min");
            ConfigSyncData.Instance?.PublishGameState();
        }

        /// <summary>
        /// Called when a drifter deal is completed (handover success).
        /// Returns true if it was a narc (sting triggered).
        /// </summary>
        public bool OnDealCompleted(string drifterId)
        {
            if (!_activeEvents.TryGetValue(drifterId, out var evt))
                return false;

            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
            bool isNarc = evt.Type == DrifterType.Narc;

            evt.State = DrifterEventState.DealCompleted;

            // Start linger timer
            int lingerTime = UnityEngine.Random.Range(
                Config.DrifterLingerMinMin.Value,
                Config.DrifterLingerMaxMin.Value + 1);
            evt.LingerDeadline = GetCurrentElapsedMinutes() + lingerTime;

            if (drifter != null)
            {
                drifter.DealCompleted = true;
                drifter.State = DrifterState.DealCompleted;
            }

            _logger.Msg($"[DrifterManager] Deal completed for drifter {drifterId}. IsNarc={isNarc}");
            ConfigSyncData.Instance?.PublishGameState();

            return isNarc;
        }

        /// <summary>
        /// Gets a drifter event by ID.
        /// </summary>
        public DrifterEvent GetEvent(string drifterId)
        {
            return _activeEvents.GetValueOrDefault(drifterId);
        }

        /// <summary>
        /// Gets all active drifter events that have accepted deals (for handover detection).
        /// </summary>
        public IEnumerable<DrifterEvent> GetAcceptedDeals()
        {
            return _activeEvents.Values.Where(e => e.State == DrifterEventState.DealAccepted);
        }

        /// <summary>
        /// Serializes drifter state for network sync.
        /// Format: id:type:seed:hotspot:state;id:type:seed:hotspot:state
        /// </summary>
        public string SerializeDrifterState()
        {
            if (_activeEvents.Count == 0)
                return "";

            var parts = new List<string>();
            foreach (var evt in _activeEvents.Values)
            {
                parts.Add($"{evt.DrifterId}:{(int)evt.Type}:{evt.Seed}:{evt.HotspotName}:{(int)evt.State}");
            }
            return string.Join(";", parts);
        }

        /// <summary>
        /// Applies drifter state from network sync (client-side).
        /// Creates missing drifters, removes stale ones.
        /// </summary>
        public void ApplyDrifterState(string stateString)
        {
            if (NetworkHelper.IsHost) return;

            var hostDrifters = new HashSet<string>();

            if (!string.IsNullOrEmpty(stateString))
            {
                var entries = stateString.Split(';');
                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry)) continue;

                    var parts = entry.Split(':');
                    if (parts.Length < 5) continue;

                    string drifterId = parts[0];
                    hostDrifters.Add(drifterId);

                    if (!int.TryParse(parts[1], out int typeInt)) continue;
                    if (!int.TryParse(parts[2], out int seed)) continue;
                    string hotspotName = parts[3];
                    if (!int.TryParse(parts[4], out int stateInt)) continue;

                    var type = (DrifterType)typeInt;
                    var state = (DrifterEventState)stateInt;

                    // Create drifter if missing
                    if (!DrifterInstance.Active.ContainsKey(drifterId))
                    {
                        var hotspot = DrifterHotspots.GetHotspotByName(hotspotName);
                        if (hotspot != null)
                        {
                            var drifter = DrifterInstance.Create(drifterId, type, hotspot, seed);
                            if (drifter != null)
                            {
                                drifter.State = state switch
                                {
                                    DrifterEventState.DealAccepted => DrifterState.DealAccepted,
                                    DrifterEventState.DealCompleted => DrifterState.DealCompleted,
                                    DrifterEventState.Lingering => DrifterState.Lingering,
                                    _ => DrifterState.Spawned
                                };
                                drifter.DealAccepted = state >= DrifterEventState.DealAccepted;
                                drifter.DealCompleted = state >= DrifterEventState.DealCompleted;
                                            }
                        }
                    }
                    else
                    {
                        // Update existing drifter state
                        var drifter = DrifterInstance.Active[drifterId];
                        drifter.State = state switch
                        {
                            DrifterEventState.DealAccepted => DrifterState.DealAccepted,
                            DrifterEventState.DealCompleted => DrifterState.DealCompleted,
                            DrifterEventState.Lingering => DrifterState.Lingering,
                            _ => DrifterState.Spawned
                        };
                        drifter.DealAccepted = state >= DrifterEventState.DealAccepted;
                        drifter.DealCompleted = state >= DrifterEventState.DealCompleted;
                            }
                }
            }

            // Remove drifters not on host
            var localDrifterIds = DrifterInstance.Active.Keys.ToList();
            foreach (var id in localDrifterIds)
            {
                if (!hostDrifters.Contains(id))
                {
                    try
                    {
                        DrifterInstance.Active[id].Despawn();
                    }
                    catch { }
                }
            }
        }

        private int GetCurrentElapsedMinutes()
        {
            int days = TimeManager.ElapsedDays;
            int time24h = TimeManager.CurrentTime;
            int hours = time24h / 100;
            int minutes = time24h % 100;
            return (days * 1440) + (hours * 60) + minutes;
        }

        public void Cleanup()
        {
            TimeManager.OnTick -= OnTimeTick;
            TimeManager.OnDayPass -= OnDayPass;
            _activeEvents.Clear();
            DrifterInstance.CleanupAll();
            Instance = null;
            _logger.Msg("[DrifterManager] Cleaned up and unsubscribed from events.");
        }

        // Debug methods
        public static bool DebugSpawnDrifter(DrifterType type)
        {
            if (Instance == null)
            {
                MelonLoader.MelonLogger.Msg("[DrifterManager] DEBUG: Instance is null!");
                return false;
            }

            // Use player-nearby hotspot for debug spawns
            var debugHotspot = DrifterHotspots.CreateHotspotNearPlayer(8f);
            if (debugHotspot == null)
            {
                MelonLoader.MelonLogger.Msg("[DrifterManager] DEBUG: Could not create hotspot near player, using random");
                debugHotspot = DrifterHotspots.GetRandomHotspot();
            }
            else
            {
                MelonLoader.MelonLogger.Msg($"[DrifterManager] DEBUG: Created hotspot near player at {debugHotspot.Position}");
            }

            Instance.SpawnDrifterAtHotspot(type, debugHotspot);

            MelonLoader.MelonLogger.Msg($"[DrifterManager] DEBUG: After spawn - ActiveEvents={Instance._activeEvents.Count}, ActiveDrifters={DrifterInstance.Active.Count}");

            // Immediately send intro text for debug spawns
            foreach (var evt in Instance._activeEvents.Values)
            {
                MelonLoader.MelonLogger.Msg($"[DrifterManager] DEBUG: Event {evt.DrifterId} - TextSent={evt.TextSent}, State={evt.State}");

                if (!evt.TextSent)
                {
                    if (DrifterInstance.Active.TryGetValue(evt.DrifterId, out var drifter))
                    {
                        try
                        {
                            string message = drifter.GetIntroTextMessage();
                            MelonLoader.MelonLogger.Msg($"[DrifterManager] DEBUG: Attempting to send text: \"{message}\"");
                            drifter.SendTextMessage(message);
                            evt.TextSent = true;
                            evt.State = DrifterEventState.OfferSent;
                            MelonLoader.MelonLogger.Msg($"[DrifterManager] DEBUG: Sent immediate intro text for drifter {evt.DrifterId}");
                        }
                        catch (Exception ex)
                        {
                            MelonLoader.MelonLogger.Warning($"[DrifterManager] DEBUG: Failed to send intro text: {ex.Message}");
                        }
                    }
                    else
                    {
                        MelonLoader.MelonLogger.Warning($"[DrifterManager] DEBUG: Drifter {evt.DrifterId} not found in ActiveDrifters!");
                        MelonLoader.MelonLogger.Msg($"[DrifterManager] DEBUG: ActiveDrifters keys: {string.Join(", ", DrifterInstance.Active.Keys)}");
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Spawns a drifter at a specific hotspot (used for debug spawning near player).
        /// </summary>
        private void SpawnDrifterAtHotspot(DrifterType type, DrifterHotspots.Hotspot hotspot)
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                // Generate unique ID
                string drifterId = $"drifter_{TimeManager.ElapsedDays}_{_drifterIdCounter++}";

                // Generate seed from drifter ID for deterministic appearance
                int seed = drifterId.GetHashCode();
                int spawnTime = GetCurrentElapsedMinutes();

                // Calculate offer deadline
                int offerWindow = UnityEngine.Random.Range(
                    Config.DrifterOfferWindowMinMin.Value,
                    Config.DrifterOfferWindowMaxMin.Value + 1);
                int offerDeadline = spawnTime + offerWindow;

                // Create the event
                var evt = new DrifterEvent
                {
                    DrifterId = drifterId,
                    Type = type,
                    HotspotName = hotspot.Name,
                    Seed = seed,
                    SpawnTime = spawnTime,
                    OfferDeadline = offerDeadline,
                    DeliveryDeadline = 0,
                    LingerDeadline = 0,
                    State = DrifterEventState.Spawned
                };

                _activeEvents[drifterId] = evt;

                _logger.Msg($"[DrifterManager] Spawning drifter {drifterId} at {hotspot.Position}");

                // Create the NPC
                var drifter = DrifterInstance.Create(drifterId, type, hotspot, seed);
                if (drifter != null)
                {
                    evt.State = DrifterEventState.OfferPending;
                    evt.TextSendTime = spawnTime + 1;

                    _logger.Msg($"[DrifterManager] Spawned drifter {drifterId}: Type={type}, Hotspot={hotspot.Name}, Position={hotspot.Position}");

                    ConfigSyncData.Instance?.PublishGameState();
                }
                else
                {
                    _activeEvents.Remove(drifterId);
                    _logger.Error($"[DrifterManager] Failed to create drifter NPC {drifterId}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] SpawnDrifterAtHotspot failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public static string DebugGetStatus()
        {
            if (Instance == null) return "DrifterManager not initialized";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Drifter Manager Status ===");
            sb.AppendLine($"Active Drifters: {Instance._activeEvents.Count}/{Config.MaxActiveDrifters.Value}");

            foreach (var kvp in Instance._activeEvents)
            {
                var evt = kvp.Value;
                sb.AppendLine($"  - {evt.DrifterId}: Type={evt.Type}, State={evt.State}, Hotspot={evt.HotspotName}");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Tracks the state of a drifter event on the host.
    /// </summary>
    public class DrifterEvent
    {
        public string DrifterId { get; set; }
        public DrifterType Type { get; set; }
        public string HotspotName { get; set; }
        public int Seed { get; set; }
        public int SpawnTime { get; set; }
        public int OfferDeadline { get; set; }
        public int DeliveryDeadline { get; set; }
        public int LingerDeadline { get; set; }
        public DrifterEventState State { get; set; }
        public int TextSendTime { get; set; }
        public bool TextSent { get; set; }
    }

    public enum DrifterEventState
    {
        Spawned,
        OfferPending,
        OfferSent,
        DealAccepted,
        DealCompleted,
        Lingering,
        Despawning
    }
}
