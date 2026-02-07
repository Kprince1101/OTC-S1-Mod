using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Police;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Handover;
using Il2CppScheduleOne.VoiceOver;
using MelonLoader;
using S1API.GameTime;
using S1API.Items;
using S1API.Products;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using OverTheCounter.Quests;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using UnityEngine;
using UnityEngine.Events;

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
            CleanupStaleConversations();

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

        private static readonly float[] RegionSpawnWeights = { 0.30f, 0.40f, 0.55f, 0.70f, 0.85f, 1.0f };

        private void TrySpawnDrifter()
        {
            if (_activeEvents.Count >= Config.MaxActiveDrifters.Value)
                return;

            int regions = GetUnlockedRegionCount();
            float weight = RegionSpawnWeights[Math.Min(regions, 6) - 1];
            float effectiveChance = Config.DrifterSpawnChancePerHour.Value * weight;

            float roll = UnityEngine.Random.value;
            if (roll > effectiveChance)
                return;

            SpawnDrifter();
        }

        private int GetUnlockedRegionCount()
        {
            try
            {
                var map = Singleton<Il2CppScheduleOne.Map.Map>.Instance;
                if (map == null) return 1;
                var regions = map.GetUnlockedRegions();
                return regions != null ? Math.Max(regions.Count, 1) : 1;
            }
            catch { return 1; }
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
                int seed = DeterministicHash(drifterId);
                int spawnTime = GetCurrentElapsedMinutes();

                // Calculate offer deadline
                int offerDeadline = spawnTime + Config.DrifterOfferWindowMin.Value;

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

                // Generate deal request — abort if no listed products
                if (!GenerateDealRequest(evt))
                    return;

                _activeEvents[drifterId] = evt;

                // Create the NPC (spawns at SpawnPosition)
                var drifter = DrifterInstance.Create(drifterId, type, hotspot, seed);
                if (drifter != null)
                {
                    // Capture FishNet ObjectId for client-side lookup
                    try { evt.NetworkObjectId = drifter.GameNpc.NetworkObject.ObjectId; }
                    catch { _logger.Warning($"[DrifterManager] Could not get NetworkObjectId for {drifterId}"); }

                    // Walk from spawn point to destination
                    drifter.WalkToDestination();

                    // Schedule intro text after brief delay
                    evt.State = DrifterEventState.OfferPending;
                    evt.TextSendTime = spawnTime + 1; // 1 minute delay before text

                    _logger.Msg($"[DrifterManager] Spawned drifter {drifterId}: Type={type}, Hotspot={hotspot.Name}, NetObjId={evt.NetworkObjectId}, Spawn={hotspot.SpawnPosition}, Dest={hotspot.Position}");

                    // Sync to clients
                    ConfigSyncData.Instance?.PublishDrifterState();

                    // FishNet may assign ObjectId asynchronously. If still 0,
                    // schedule a delayed re-capture + re-publish.
                    if (evt.NetworkObjectId == 0)
                        MelonCoroutines.Start(DelayedObjectIdCapture(evt, drifter));
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

            return DrifterHotspots.GetSafeHotspot(occupiedHotspots);
        }

        /// <summary>
        /// Generates a deal request (product/quantity/price) for a drifter based on type.
        /// Returns false if no listed products are available (drifter should not spawn).
        /// </summary>
        private bool GenerateDealRequest(DrifterEvent evt)
        {
            try
            {
                var listedProducts = Il2CppScheduleOne.Product.ProductManager.ListedProducts;
                if (listedProducts == null || listedProducts.Count == 0)
                {
                    _logger.Warning("[DrifterManager] No listed products - cannot generate deal");
                    return false;
                }

                // Select product based on type
                Il2CppScheduleOne.Product.ProductDefinition selectedProduct = null;

                if (evt.Type == DrifterType.Fiend)
                {
                    // Fiend prefers meth or coke
                    var preferred = new List<Il2CppScheduleOne.Product.ProductDefinition>();
                    for (int i = 0; i < listedProducts.Count; i++)
                    {
                        var p = listedProducts[i];
                        if (p == null) continue;
                        string id = p.ID?.ToLower() ?? "";
                        if (id.Contains("meth") || id.Contains("coke") || id.Contains("cocaine"))
                            preferred.Add(p);
                    }

                    if (preferred.Count > 0)
                        selectedProduct = preferred[UnityEngine.Random.Range(0, preferred.Count)];
                }

                // Fallback to random listed product
                if (selectedProduct == null)
                {
                    int idx = UnityEngine.Random.Range(0, listedProducts.Count);
                    selectedProduct = listedProducts[idx];
                }

                if (selectedProduct == null)
                {
                    _logger.Warning("[DrifterManager] Could not select product");
                    return false;
                }

                // Get quantity range and price multiplier based on type
                int minQty, maxQty;
                float priceMultiplier;

                switch (evt.Type)
                {
                    case DrifterType.Whale:
                        minQty = 3;
                        maxQty = 6;
                        priceMultiplier = 1.3f;
                        break;
                    case DrifterType.Fiend:
                        minQty = 1;
                        maxQty = 2;
                        priceMultiplier = 1.5f;
                        break;
                    case DrifterType.Narc:
                    case DrifterType.Normal:
                    default:
                        minQty = 1;
                        maxQty = 3;
                        priceMultiplier = 1.0f;
                        break;
                }

                int quantity = UnityEngine.Random.Range(minQty, maxQty + 1);
                float basePrice = selectedProduct.Price;
                float payment = Mathf.Round(basePrice * quantity * priceMultiplier);

                // Soft minimum: if deal value is too low, bump quantity until it's worth the trip
                float minDealValue = Config.DrifterMinDealValue.Value;
                if (payment < minDealValue && basePrice > 0)
                {
                    int needed = Mathf.CeilToInt(minDealValue / (basePrice * priceMultiplier));
                    if (needed > quantity)
                    {
                        quantity = needed;
                        payment = Mathf.Round(basePrice * quantity * priceMultiplier);
                    }
                }

                evt.ProductId = selectedProduct.ID;
                evt.ProductName = selectedProduct.Name ?? selectedProduct.ID;
                evt.Quantity = quantity;
                evt.Payment = payment;

                _logger.Msg($"[DrifterManager] Generated deal for {evt.DrifterId}: {quantity}x {evt.ProductName} @ ${payment} (type={evt.Type}, mult={priceMultiplier})");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] GenerateDealRequest failed: {ex.Message}");
                return false;
            }
        }

        private void ProcessDrifterLifecycles()
        {
            int currentMinutes = GetCurrentElapsedMinutes();
            var toRemove = new List<string>();

            foreach (var kvp in _activeEvents)
            {
                var evt = kvp.Value;
                var drifter = DrifterInstance.Active.GetValueOrDefault(evt.DrifterId);

                // Skip movement checks while player is interacting with a drifter
                if (!IsDrifterPaused(drifter))
                {
                    drifter?.CheckStuck();
                    drifter?.EnsureMoving();
                }

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
                            ConfigSyncData.Instance?.PublishDrifterState();
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
                        // Update quest timing subtitle
                        if (DrifterDealQuest.ActiveQuests.TryGetValue(evt.DrifterId, out var activeQuest))
                            activeQuest.UpdateTiming();

                        // Check delivery deadline
                        if (currentMinutes >= evt.DeliveryDeadline)
                        {
                            FailDrifterDelivery(evt, drifter);
                        }
                        break;

                    case DrifterEventState.DealCompleted:
                    case DrifterEventState.Lingering:
                        // Robber KO detection: freeze the body in place for looting
                        if (drifter != null && drifter.IsAttacking && drifter.IsValid)
                        {
                            try
                            {
                                if (!drifter.GameNpc.IsConscious)
                                {
                                    drifter.IsAttacking = false;
                                    drifter.IsKnockedOut = true;
                                    evt.LingerDeadline = currentMinutes + 180;
                                    _logger.Msg($"[DrifterManager] Robber {evt.DrifterId} knocked out, lingering 180min for looting");
                                }
                            }
                            catch { /* NPC destroyed — will despawn below */ }
                        }

                        // Skip despawn for attacking robbers still in combat
                        if (drifter != null && drifter.IsAttacking)
                            break;

                        // After deal completion: narcs run away, others consume then walk back
                        // Knocked-out robbers skip all movement/consume logic
                        if (drifter != null && !drifter.IsWalkingBack && !drifter.IsConsuming && !drifter.IsAttacking && !drifter.IsKnockedOut)
                        {
                            if (evt.Type == DrifterType.Narc)
                            {
                                // Narc: sprint to spawn immediately (no consume)
                                drifter.SetRunSpeed();
                                drifter.WalkToSpawn();
                            }
                            else if (evt.State == DrifterEventState.DealCompleted && !string.IsNullOrEmpty(evt.ProductId))
                                drifter.PlayConsumeAnimation(evt.ProductId);
                            else
                                drifter.WalkToSpawn();
                        }

                        // Despawn when no players are nearby (or linger deadline as hard fallback)
                        // Knocked-out robbers only despawn on deadline
                        bool noPlayersNearby = !(drifter?.IsKnockedOut == true) && drifter?.Position != null &&
                            !DrifterHotspots.IsAnyPlayerNearby(drifter.Position.Value);
                        bool pastDeadline = evt.LingerDeadline > 0 && currentMinutes >= evt.LingerDeadline;

                        if (noPlayersNearby || pastDeadline)
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
                ConfigSyncData.Instance?.PublishDrifterState();
        }

        private void SendIntroText(DrifterEvent evt, DrifterInstance drifter)
        {
            if (drifter == null) return;

            try
            {
                // Get location hint for the intro text
                string locationHint = GetLocationHint(evt.HotspotName);

                string message = drifter.GetIntroTextMessage(evt.ProductName, evt.Quantity, evt.Payment, locationHint);

                // Deterministic messageId enables client-side dedup
                var conversation = drifter.GameNpc?.MSGConversation;
                if (conversation != null)
                {
                    var msg = new Il2CppScheduleOne.Messaging.Message(message, Il2CppScheduleOne.Messaging.Message.ESenderType.Other, true);
                    msg.messageId = DeterministicHash(evt.DrifterId + "_intro");
                    conversation.SendMessage(msg, true, true); // networked — delivers to all players
                }
                _logger.Msg($"[DrifterManager] Sent intro text for drifter {evt.DrifterId}: {evt.Quantity}x {evt.ProductName} @ ${evt.Payment}");

                ShowDealResponses(evt, drifter);
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DrifterManager] Failed to send intro text: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows Accept/Decline response buttons for a drifter deal.
        /// </summary>
        private void ShowDealResponses(DrifterEvent evt, DrifterInstance drifter)
        {
            if (drifter?.GameNpc?.MSGConversation == null)
            {
                _logger.Warning($"[DrifterManager] Cannot show responses - MSGConversation is null for {evt.DrifterId}");
                return;
            }

            try
            {
                var conversation = drifter.GameNpc.MSGConversation;
                var responses = new Il2CppSystem.Collections.Generic.List<Response>();

                // disableDefaultResponseBehaviour=true so the vanilla SendResponse ServerRpc doesn't
                // propagate the callback to ALL players (which would fire OnAcceptResponse on both sides)
                string drifterId = evt.DrifterId;
                var acceptCallback = (Il2CppSystem.Action)new System.Action(() => OnAcceptResponse(drifterId));
                var acceptResponse = new Response("I'm on my way", "accept", acceptCallback, true);
                responses.Add(acceptResponse);

                var declineCallback = (Il2CppSystem.Action)new System.Action(() => OnDeclineResponse(drifterId));
                var declineResponse = new Response("Not interested", "decline", declineCallback, true);
                responses.Add(declineResponse);

                // network=false: each side keeps its own callbacks (networked responses lose lambdas during serialization)
                conversation.ShowResponses(responses, 0.5f, false);
                _logger.Msg($"[DrifterManager] Showing deal responses for drifter {evt.DrifterId}");
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] ShowDealResponses failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void OnAcceptResponse(string drifterId)
        {
            _logger.Msg($"[DrifterManager] Accept response clicked for drifter {drifterId}");

            if (!_activeEvents.TryGetValue(drifterId, out var evt))
            {
                _logger.Warning($"[DrifterManager] OnAcceptResponse: unknown drifter {drifterId}");
                return;
            }

            // Other player may have already accepted — ignore stale button click
            if (evt.State >= DrifterEventState.DealAccepted)
            {
                _logger.Msg($"[DrifterManager] OnAcceptResponse: drifter {drifterId} already in state {evt.State}, ignoring");
                return;
            }

            // Manually clear responses + render player message (disableDefaultResponseBehaviour skips vanilla handling)
            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
            var conversation = drifter?.GameNpc?.MSGConversation;
            if (conversation != null)
            {
                conversation.ClearResponses(false);
                var playerMsg = new Il2CppScheduleOne.Messaging.Message(
                    "I'm on my way", Il2CppScheduleOne.Messaging.Message.ESenderType.Player, true);
                conversation.SendMessage(playerMsg, false, false);
            }

            if (NetworkHelper.IsHost)
            {
                OnDealAccepted(drifterId);
            }
            else
            {
                ConfigSyncData.SendQuestAction($"DRIFTER_ACCEPT:{drifterId}");
            }

            if (drifter != null)
            {
                string confirmMessage = evt.Type switch
                {
                    DrifterType.Whale => "Good. Come alone. Don't keep me waiting.",
                    DrifterType.Fiend => "THANK GOD. HURRY UP.",
                    DrifterType.Narc => "Perfect. See you soon.",
                    _ => "Cool. Don't keep me waiting."
                };
                drifter.SendTextMessage(confirmMessage);
            }
        }

        private void OnDeclineResponse(string drifterId)
        {
            _logger.Msg($"[DrifterManager] Decline response clicked for drifter {drifterId}");

            if (!_activeEvents.TryGetValue(drifterId, out var evt))
            {
                _logger.Warning($"[DrifterManager] OnDeclineResponse: unknown drifter {drifterId}");
                return;
            }

            if (evt.State >= DrifterEventState.DealAccepted)
            {
                _logger.Msg($"[DrifterManager] OnDeclineResponse: drifter {drifterId} already in state {evt.State}, ignoring");
                return;
            }

            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
            var conversation = drifter?.GameNpc?.MSGConversation;
            if (conversation != null)
            {
                conversation.ClearResponses(false);
                var playerMsg = new Il2CppScheduleOne.Messaging.Message(
                    "Not interested", Il2CppScheduleOne.Messaging.Message.ESenderType.Player, true);
                conversation.SendMessage(playerMsg, false, false);
            }

            if (drifter != null)
            {
                string declineMessage = evt.Type switch
                {
                    DrifterType.Whale => "Your loss. Big money walking away.",
                    DrifterType.Fiend => "NO NO NO. Fine. FINE.",
                    DrifterType.Narc => "Hmm. Okay then.",
                    _ => "Whatever."
                };
                drifter.SendTextMessage(declineMessage);
                drifter.PlayDismissalSound();
            }

            // Start linger timer (host only)
            if (NetworkHelper.IsHost)
            {
                int lingerTime = UnityEngine.Random.Range(
                    Config.DrifterLingerMinMin.Value,
                    Config.DrifterLingerMaxMin.Value + 1);
                evt.LingerDeadline = GetCurrentElapsedMinutes() + lingerTime;
                evt.State = DrifterEventState.Lingering;

                if (drifter != null)
                    drifter.State = DrifterState.Lingering;

                ConfigSyncData.Instance?.PublishDrifterState();
            }
        }

        /// <summary>
        /// Returns true if this drifter should not have its movement resumed.
        /// Prevents EnsureMoving from fighting against HandoverScreen or PickpocketScreen pauses.
        /// </summary>
        private bool IsDrifterPaused(DrifterInstance drifter)
        {
            if (drifter?.GameNpc == null) return false;

            // Attacking or knocked-out robbers: don't interfere with movement
            if (drifter.IsAttacking || drifter.IsKnockedOut) return true;

            try
            {
                // Pause ALL drifters while HandoverScreen is open (it's a modal fullscreen UI)
                var handover = Singleton<HandoverScreen>.Instance;
                if (handover != null && handover.IsOpen)
                    return true;

                // Pause only the drifter being pickpocketed
                var pickpocket = Singleton<PickpocketScreen>.Instance;
                if (pickpocket != null && pickpocket.IsOpen && pickpocket.npc == drifter.GameNpc)
                    return true;
            }
            catch { }

            return false;
        }

        // =====================================================
        // HANDOVER SCREEN INTEGRATION
        // =====================================================

        // Track dialogue choices per drifter so we can clean them up
        // Pending adoptions: drifters that need FishNet NPC found on client (retried periodically)
        private readonly Dictionary<string, PendingAdoption> _pendingAdoptions = new();
        private float _lastAdoptionRetry;
        private float _lastClientQuestTick;

        private class PendingAdoption
        {
            public string DrifterId;
            public DrifterType Type;
            public DrifterHotspots.Hotspot Hotspot;
            public int Seed;
            public DrifterEventState State;
            public int NetworkObjectId;
            public float CreatedTime;
        }

        private readonly Dictionary<string, DialogueController.DialogueChoice> _dealChoices = new();

        private static Il2CppScheduleOne.ItemFramework.EQuality GetExpectedQuality(DrifterType type, int seed)
        {
            var rng = new System.Random(seed + 5555);
            return type switch
            {
                DrifterType.Whale => rng.Next(2) == 0
                    ? Il2CppScheduleOne.ItemFramework.EQuality.Premium
                    : Il2CppScheduleOne.ItemFramework.EQuality.Heavenly,
                DrifterType.Fiend => rng.Next(2) == 0
                    ? Il2CppScheduleOne.ItemFramework.EQuality.Trash
                    : Il2CppScheduleOne.ItemFramework.EQuality.Poor,
                DrifterType.Normal => (Il2CppScheduleOne.ItemFramework.EQuality)(rng.Next(1, 4)),  // Poor(1), Standard(2), Premium(3)
                // Robber/Narc: random Trash-Premium (never Heavenly)
                _ => (Il2CppScheduleOne.ItemFramework.EQuality)(rng.Next(0, 4)),  // Trash(0)..Premium(3)
            };
        }

        // Inverse of StandardsMethod.GetCorrespondingQuality
        private static ECustomerStandard QualityToStandard(Il2CppScheduleOne.ItemFramework.EQuality quality)
        {
            return quality switch
            {
                Il2CppScheduleOne.ItemFramework.EQuality.Trash    => ECustomerStandard.VeryLow,
                Il2CppScheduleOne.ItemFramework.EQuality.Poor     => ECustomerStandard.Low,
                Il2CppScheduleOne.ItemFramework.EQuality.Standard => ECustomerStandard.Moderate,
                Il2CppScheduleOne.ItemFramework.EQuality.Premium  => ECustomerStandard.High,
                Il2CppScheduleOne.ItemFramework.EQuality.Heavenly => ECustomerStandard.VeryHigh,
                _ => ECustomerStandard.Moderate,
            };
        }

        /// <summary>
        /// Opens the vanilla HandoverScreen for a drifter deal.
        /// Called when player interacts with drifter after accepting the deal.
        /// </summary>
        public void OpenDrifterHandover(string drifterId)
        {
            if (!_activeEvents.TryGetValue(drifterId, out var evt))
            {
                _logger.Warning($"[DrifterManager] OpenDrifterHandover: unknown drifter {drifterId}");
                return;
            }

            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
            if (drifter?.GameNpc == null)
            {
                _logger.Warning($"[DrifterManager] OpenDrifterHandover: drifter NPC not found {drifterId}");
                return;
            }

            // Get the Customer component from the drifter
            var customer = DrifterSpawner.GetCustomerComponent(drifter.GameNpc);
            if (customer == null)
            {
                _logger.Error($"[DrifterManager] OpenDrifterHandover: Customer component missing for {drifterId}");
                return;
            }

            try
            {
                // Create ProductList for the expected products
                var productList = new Il2CppScheduleOne.Product.ProductList();
                productList.entries = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Product.ProductList.Entry>();

                // Clone shared CustomerData so per-drifter quality standards don't leak
                var expectedQuality = GetExpectedQuality(evt.Type, evt.Seed);
                if (customer.customerData != null)
                {
                    var perDrifterData = ScriptableObject.Instantiate(customer.customerData);
                    perDrifterData.Standards = QualityToStandard(expectedQuality);
                    customer.customerData = perDrifterData;
                }

                var productEntry = new Il2CppScheduleOne.Product.ProductList.Entry(
                    evt.ProductId,
                    expectedQuality,
                    evt.Quantity
                );
                productList.entries.Add(productEntry);

                // Create the Contract for this deal
                var contract = CreateDrifterContract(evt, customer, productList);
                if (contract == null)
                {
                    _logger.Error($"[DrifterManager] Failed to create contract for drifter {drifterId}");
                    return;
                }

                // Create callback for handover completion
                var drifterIdCapture = drifterId;
                Il2CppSystem.Action<HandoverScreen.EHandoverOutcome, Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.ItemFramework.ItemInstance>, float> callback =
                    (Il2CppSystem.Action<HandoverScreen.EHandoverOutcome, Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.ItemFramework.ItemInstance>, float>)
                    new Action<HandoverScreen.EHandoverOutcome, Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.ItemFramework.ItemInstance>, float>(
                        (outcome, items, price) => OnDrifterHandoverClosed(drifterIdCapture, outcome, items, price));

                // Success chance is 100% for pre-agreed drifter deals
                Il2CppSystem.Func<Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.ItemFramework.ItemInstance>, float, float> successChance =
                    (Il2CppSystem.Func<Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.ItemFramework.ItemInstance>, float, float>)
                    new Func<Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.ItemFramework.ItemInstance>, float, float>(
                        (items, askingPrice) => 1.0f);

                // Open HandoverScreen in Contract mode (shows expected products/payment)
                var handoverScreen = Singleton<HandoverScreen>.Instance;
                if (handoverScreen == null)
                {
                    _logger.Error("[DrifterManager] HandoverScreen singleton not found");
                    return;
                }

                // Stop drifter movement while the handover screen is open
                try { drifter.GameNpc.Movement?.Stop(); } catch { }

                handoverScreen.Open(contract, customer, HandoverScreen.EMode.Contract, callback, successChance, false);
                _logger.Msg($"[DrifterManager] Opened HandoverScreen for drifter {drifterId}: {evt.Quantity}x {evt.ProductName} @ ${evt.Payment}");
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] OpenDrifterHandover failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // Cache for drifter contracts
        private readonly Dictionary<string, Il2CppScheduleOne.Quests.Contract> _drifterContracts = new();

        /// <summary>
        /// Creates a Contract object for a drifter deal.
        /// </summary>
        private Il2CppScheduleOne.Quests.Contract CreateDrifterContract(DrifterEvent evt, Customer customer, Il2CppScheduleOne.Product.ProductList productList)
        {
            try
            {
                // Check if we already have a contract for this drifter
                if (_drifterContracts.TryGetValue(evt.DrifterId, out var existing) && existing != null)
                {
                    return existing;
                }

                // Create a new Contract - it's a NetworkBehaviour so we need to create it as a component
                var contractGo = new GameObject($"DrifterContract_{evt.DrifterId}");
                var contract = contractGo.AddComponent<Il2CppScheduleOne.Quests.Contract>();

                // Initialize the contract silently (doesn't notify quest system)
                var timeManager = NetworkSingleton<Il2CppScheduleOne.GameTime.TimeManager>.Instance;
                var currentTime = timeManager?.GetDateTime() ?? default;

                contract.SilentlyInitializeContract(
                    $"Deal with {customer.NPC?.FirstName ?? "Drifter"}", // title
                    string.Empty, // description
                    null, // quest entries
                    string.Empty, // guid
                    customer, // customer
                    evt.Payment, // payment
                    productList, // expected products
                    string.Empty, // delivery location
                    new Il2CppScheduleOne.Quests.QuestWindowConfig(), // delivery window
                    0, // pickup schedule index
                    currentTime // accept time
                );

                _drifterContracts[evt.DrifterId] = contract;
                _logger.Msg($"[DrifterManager] Created contract for drifter {evt.DrifterId}");

                return contract;
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] CreateDrifterContract failed: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Callback when HandoverScreen is closed for a drifter deal.
        /// </summary>
        private void OnDrifterHandoverClosed(string drifterId, HandoverScreen.EHandoverOutcome outcome, Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.ItemFramework.ItemInstance> items, float askingPrice)
        {
            _logger.Msg($"[DrifterManager] Handover closed for {drifterId}: outcome={outcome}");

            if (outcome == HandoverScreen.EHandoverOutcome.Cancelled)
            {
                // Player cancelled - deal still pending, resume drifter movement
                _logger.Msg($"[DrifterManager] Handover cancelled for {drifterId}, deal still pending");
                var cancelledDrifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
                cancelledDrifter?.EnsureMoving();
                return;
            }

            // outcome == Finalize - deal completed
            if (!_activeEvents.TryGetValue(drifterId, out var evt))
                return;

            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);

            // Stock the drifter's inventory with the actual handed-over items (preserves packaging)
            if (drifter != null && items != null)
                drifter.StockInventory(items);

            // Robber branch: no payment, stock cash as loot, equip weapon, attack
            if (evt.Type == DrifterType.Robber)
            {
                // Stock cash in NPC inventory for body search recovery (deal value + 20% bonus)
                float lootCash = evt.Payment * 1.2f;
                drifter?.StockCash(lootCash);

                // Mark attacking immediately so lifecycle doesn't trigger consume/walk
                if (drifter != null)
                {
                    drifter.IsAttacking = true;
                    SelectRobberWeapon(drifter, evt.Seed);
                }

                // Threatening dialogue
                string[] robberLines = {
                    "Yeah, I'm not paying for that. Thanks though.",
                    "Payment? Nah. I think I'll keep it.",
                    "Appreciate the delivery. Now get lost.",
                    "You really thought I was gonna pay? That's cute."
                };
                var rng = new System.Random(evt.Seed + 7000);
                drifter?.GameNpc?.SendWorldSpaceDialogue(robberLines[rng.Next(robberLines.Length)], 4f);

                // Complete the deal state (host-authoritative)
                if (NetworkHelper.IsHost)
                {
                    OnDealCompleted(drifterId);
                    if (drifter != null)
                        MelonCoroutines.Start(DelayedRobberAttack(drifterId, 0.5f, Player.Local));
                }
                else
                {
                    ConfigSyncData.SendQuestAction($"DRIFTER_COMPLETE:{drifterId}:{Player.Local?.PlayerCode ?? ""}");
                }

                _logger.Msg($"[DrifterManager] Robber {drifterId}: stocked ${lootCash:F0} loot, attack incoming");
                return;
            }

            var contract = _drifterContracts.GetValueOrDefault(drifterId);
            var customer = drifter != null ? DrifterSpawner.GetCustomerComponent(drifter.GameNpc) : null;

            if (contract != null && items != null)
            {
                try
                {
                    int matchedCount;
                    float matchScore = contract.GetProductListMatch(items, out matchedCount);
                    bool accepted = UnityEngine.Random.Range(0f, 1f) < matchScore;

                    if (!accepted)
                    {
                        _logger.Msg($"[DrifterManager] Drifter {drifterId} REJECTED deal (matchScore={matchScore:F2})");

                        try { Singleton<HandoverScreen>.Instance?.ClearCustomerSlots(true); } catch { }

                        string[] rejectionLines = {
                            "This isn't what I asked for.",
                            "Nah, that's not right. I'll pass.",
                            "Are you serious? That's not what we agreed on.",
                            "I don't want this. We're done."
                        };
                        var rng = new System.Random(evt.Seed + 8000);
                        drifter?.GameNpc?.SendWorldSpaceDialogue(rejectionLines[rng.Next(rejectionLines.Length)], 5f);

                        CleanupDealDialogueChoice(drifterId, drifter);

                        if (NetworkHelper.IsHost)
                        {
                            int lingerTime = UnityEngine.Random.Range(
                                Config.DrifterLingerMinMin.Value,
                                Config.DrifterLingerMaxMin.Value + 1);
                            evt.LingerDeadline = GetCurrentElapsedMinutes() + lingerTime;
                            evt.State = DrifterEventState.Lingering;
                            if (drifter != null)
                                drifter.State = DrifterState.Lingering;
                            ConfigSyncData.Instance?.PublishDrifterState();
                        }

                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[DrifterManager] Rejection check failed for {drifterId}, proceeding with deal: {ex.Message}");
                }
            }

            float satisfaction = 1f;
            float qualityDifference = 0f;
            int matchedProductCount = 0;
            var bonuses = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Quests.Contract.BonusPayment>();

            if (customer != null && contract != null && items != null)
            {
                try
                {
                    float highestAddiction;
                    EDrugType mainType;
                    satisfaction = Mathf.Clamp01(customer.EvaluateDelivery(
                        contract, items,
                        out highestAddiction, out mainType,
                        out matchedProductCount, out qualityDifference));
                    _logger.Msg($"[DrifterManager] EvaluateDelivery: satisfaction={satisfaction:F2} qualityDiff={qualityDifference:F2} matchedCount={matchedProductCount}");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[DrifterManager] EvaluateDelivery failed for {drifterId}: {ex.Message}");
                }

                // Replicates vanilla Customer.ProcessHandover bonus logic
                try
                {
                    var curfewMgr = NetworkSingleton<Il2CppScheduleOne.Law.CurfewManager>.Instance;
                    if (curfewMgr != null && curfewMgr.IsCurrentlyActive)
                    {
                        bonuses.Add(new Il2CppScheduleOne.Quests.Contract.BonusPayment("Curfew Bonus", contract.Payment * 0.2f));
                    }

                    int totalQuantity = contract.ProductList.GetTotalQuantity();
                    if (matchedProductCount > totalQuantity && satisfaction >= 0.99f)
                    {
                        bonuses.Add(new Il2CppScheduleOne.Quests.Contract.BonusPayment(
                            "Generosity Bonus", 10f * (matchedProductCount - totalQuantity)));
                    }

                    if (qualityDifference >= 0.2f)
                    {
                        bonuses.Add(new Il2CppScheduleOne.Quests.Contract.BonusPayment(
                            "Exceeded Quality Bonus", contract.Payment * 0.15f * qualityDifference));
                    }

                    var timeManager = NetworkSingleton<Il2CppScheduleOne.GameTime.TimeManager>.Instance;
                    if (timeManager != null)
                    {
                        var acceptTime = contract.AcceptTime;
                        var endTime = new Il2CppScheduleOne.GameTime.GameDateTime(
                            acceptTime.elapsedDays,
                            Il2CppScheduleOne.GameTime.TimeManager.AddMinutesTo24HourTime(
                                contract.DeliveryWindow.WindowStartTime, 60));
                        if (timeManager.IsCurrentDateWithinRange(acceptTime, endTime))
                        {
                            bonuses.Add(new Il2CppScheduleOne.Quests.Contract.BonusPayment(
                                "Quick Delivery Bonus", contract.Payment * 0.1f));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[DrifterManager] Bonus calculation failed for {drifterId}: {ex.Message}");
                }
            }

            float bonusTotal = 0f;
            for (int i = 0; i < bonuses.Count; i++)
            {
                bonusTotal += bonuses[i].Amount;
                _logger.Msg($"[DrifterManager] Bonus: {bonuses[i].Title} +${bonuses[i].Amount:F0}");
            }

            try
            {
                Singleton<HandoverScreen>.Instance?.ClearCustomerSlots(false);
                if (contract != null)
                {
                    contract.SubmitPayment(bonusTotal);
                    _logger.Msg($"[DrifterManager] SubmitPayment: base=${contract.Payment:F0} + bonus=${bonusTotal:F0}");
                }
                else
                {
                    NetworkSingleton<MoneyManager>.Instance?.ChangeCashBalance(evt.Payment, true, true);
                    _logger.Warning($"[DrifterManager] Fallback payment ${evt.Payment} (no contract)");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] Payment failed for {drifterId}: {ex.Message}");
            }

            try
            {
                if (customer != null)
                {
                    float relDelta = customer.NPC?.RelationData?.RelationDelta ?? 0f;
                    float basePayment = contract?.Payment ?? evt.Payment;
                    Singleton<DealCompletionPopup>.Instance?.PlayPopup(
                        customer, satisfaction, relDelta, basePayment, bonuses);
                    _logger.Msg($"[DrifterManager] Playing DealCompletionPopup for {drifterId}");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DrifterManager] DealCompletionPopup failed for {drifterId}: {ex.Message}");
            }

            // Thank the player in person via worldspace dialogue bubble
            if (drifter?.GameNpc != null)
            {
                string completionMessage = evt.Type switch
                {
                    DrifterType.Whale => "Pleasure doing business. Quality stuff.",
                    DrifterType.Fiend => "FINALLY. Thank you thank you thank you.",
                    DrifterType.Narc => "Thanks for the... wait, is that the police?",
                    _ => "Thanks. Good doing business with you."
                };
                drifter.GameNpc.SendWorldSpaceDialogue(completionMessage, 5f);
            }

            // State mutation is host-authoritative; client forwards via network
            if (NetworkHelper.IsHost)
            {
                var completedType = OnDealCompleted(drifterId);
                if (completedType == DrifterType.Narc)
                    TriggerNarcSting(evt, Player.Local);
            }
            else
            {
                var playerCode = Player.Local?.PlayerCode ?? "";
                ConfigSyncData.SendQuestAction($"DRIFTER_COMPLETE:{drifterId}:{playerCode}");
            }
        }

        /// <summary>
        /// Sets up a dialogue choice on the drifter NPC for completing the deal.
        /// Called when deal is accepted.
        /// </summary>
        private void SetupDealDialogueChoice(DrifterEvent evt, DrifterInstance drifter)
        {
            if (drifter?.GameNpc?.DialogueHandler == null)
            {
                _logger.Warning($"[DrifterManager] Cannot setup dialogue choice - DialogueHandler null for {evt.DrifterId}");
                return;
            }

            try
            {
                var dialogueController = drifter.GameNpc.DialogueHandler.GetComponent<DialogueController>();
                if (dialogueController == null)
                {
                    _logger.Warning($"[DrifterManager] DialogueController not found for {evt.DrifterId}");
                    return;
                }

                // Create the dialogue choice
                var choice = new DialogueController.DialogueChoice();
                choice.ChoiceText = "[Complete Deal]";
                choice.Enabled = true;
                choice.Conversation = null;

                // Set up callback using UnityEvent
                choice.onChoosen = new UnityEvent();
                var drifterId = evt.DrifterId;
                choice.onChoosen.AddListener((UnityAction)(() => OpenDrifterHandover(drifterId)));

                // Only show when this drifter's deal is accepted
                choice.shouldShowCheck = (Func<bool, bool>)((bool enabled) =>
                {
                    if (!_activeEvents.TryGetValue(drifterId, out var e))
                        return false;
                    return e.State == DrifterEventState.DealAccepted;
                });

                // Add the choice to the dialogue controller
                dialogueController.AddDialogueChoice(choice);
                _dealChoices[evt.DrifterId] = choice;

                _logger.Msg($"[DrifterManager] Added deal dialogue choice for {evt.DrifterId}");
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] SetupDealDialogueChoice failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Cleans up the dialogue choice for a drifter (on despawn or deal complete).
        /// Since there's no RemoveDialogueChoice, we disable it and rely on shouldShowCheck.
        /// </summary>
        private void CleanupDealDialogueChoice(string drifterId, DrifterInstance drifter)
        {
            if (!_dealChoices.TryGetValue(drifterId, out var choice))
                return;

            try
            {
                // Disable the choice - shouldShowCheck will also return false
                choice.Enabled = false;
            }
            catch { }

            _dealChoices.Remove(drifterId);
        }

        private void CleanupNarcDialogueChoice(string drifterId)
        {
            if (!_narcPostStingChoices.TryGetValue(drifterId, out var choice))
                return;
            try { choice.Enabled = false; } catch { }
            _narcPostStingChoices.Remove(drifterId);
        }

        /// <summary>
        /// Triggers a narc sting: sets active wanted status, warps officers nearby
        /// for immediate response, and dispatches backup from the station.
        /// </summary>
        private void TriggerNarcSting(DrifterEvent evt, Player targetPlayer = null)
        {
            try
            {
                var player = targetPlayer ?? Player.Local;
                _logger.Msg($"[DrifterManager] NARC STING for {evt.DrifterId}! target={player?.PlayerCode} IsHost={NetworkHelper.IsHost}");

                if (player?.CrimeData == null)
                {
                    _logger.Warning("[DrifterManager] TriggerNarcSting: target player or CrimeData is null");
                    return;
                }

                _logger.Msg($"[DrifterManager] TriggerNarcSting: player={player.PlayerCode}, pos={player.transform?.position}, currentPursuit={player.CrimeData.CurrentPursuitLevel}");

                // Record player position and set active wanted status
                player.CrimeData.RecordLastKnownPosition(true);
                player.CrimeData.AddCrime(new DrugTrafficking());
                player.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Arresting);
                _logger.Msg($"[DrifterManager] TriggerNarcSting: pursuit set to Arresting, now={player.CrimeData.CurrentPursuitLevel}");

                // Warp officers near the player for fast response, then start foot pursuit.
                // Also redirect any nearby on-duty officers for immediate backup.
                MelonCoroutines.Start(WarpOfficersNearPlayer(2, player));
                RedirectNearbyOfficers(2, player);

                // Set up narc post-sting dialogue
                var drifter = DrifterInstance.Active.GetValueOrDefault(evt.DrifterId);
                if (drifter != null)
                    SetupNarcPostStingDialogue(evt, drifter);

                _logger.Msg("[DrifterManager] Narc sting complete");
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] TriggerNarcSting failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Pulls officers from the station pool, waits for activation, then warps them
        /// near the player and starts foot pursuit. Much faster than vanilla Dispatch
        /// which makes officers walk/drive from the station.
        /// </summary>
        private IEnumerator WarpOfficersNearPlayer(int count, Player player)
        {
            var station = PoliceStation.GetClosestPoliceStation(player.transform.position);
            if (station == null)
            {
                _logger.Warning("[DrifterManager] WarpOfficersNearPlayer: No police station found");
                yield break;
            }

            string playerCode = player.PlayerCode;
            if (string.IsNullOrEmpty(playerCode))
            {
                _logger.Warning("[DrifterManager] WarpOfficersNearPlayer: PlayerCode is null");
                yield break;
            }

            var officers = new List<PoliceOfficer>();
            for (int i = 0; i < count && station.OfficerPool?.Count > 0; i++)
            {
                var officer = station.PullOfficer();
                if (officer != null)
                    officers.Add(officer);
            }

            if (officers.Count == 0)
            {
                _logger.Msg("[DrifterManager] WarpOfficersNearPlayer: pool empty, falling back to redirect");
                RedirectNearbyOfficers(count, player);
                yield break;
            }

            // Wait for officers to fully activate (exit building animation)
            yield return new WaitForSeconds(0.5f);

            var playerPos = player.transform.position;
            for (int i = 0; i < officers.Count; i++)
            {
                try
                {
                    // Warp each officer to a different offset ~10m from the player
                    float angle = (360f / officers.Count) * i + 180f; // behind the player
                    float rad = angle * Mathf.Deg2Rad;
                    var offset = new Vector3(Mathf.Sin(rad) * 10f, 0f, Mathf.Cos(rad) * 10f);
                    var warpPos = playerPos + offset;

                    officers[i].Movement.Warp(warpPos);
                    officers[i].BeginFootPursuit_Networked(playerCode, false);
                    _logger.Msg($"[DrifterManager] Warped officer {i} ~10m from player, pursuing");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[DrifterManager] WarpOfficersNearPlayer: officer {i} failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Dispatches officers using vanilla PoliceStation.Dispatch, falling back to
        /// redirecting nearby on-duty officers when the station pool is empty.
        /// </summary>
        private void DispatchOfficers(int count, Player player)
        {
            try
            {
                var station = PoliceStation.GetClosestPoliceStation(player.transform.position);
                if (station == null)
                {
                    _logger.Warning("[DrifterManager] DispatchOfficers: No police station found");
                    return;
                }

                int poolCount = station.OfficerPool?.Count ?? 0;
                int fromPool = Math.Min(count, poolCount);
                _logger.Msg($"[DrifterManager] DispatchOfficers: station={station.name}, pool={poolCount}, fromPool={fromPool}");

                if (fromPool > 0)
                {
                    station.Dispatch(fromPool, player);
                    count -= fromPool;
                    _logger.Msg($"[DrifterManager] DispatchOfficers: dispatched {fromPool} from pool, remaining need={count}");
                }

                // Fallback: redirect nearby on-duty officers when pool is empty/insufficient
                if (count > 0)
                {
                    _logger.Msg($"[DrifterManager] DispatchOfficers: pool insufficient, redirecting {count} nearby officers");
                    RedirectNearbyOfficers(count, player);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DrifterManager] DispatchOfficers failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Redirects the nearest available officers to pursue the player.
        /// Used as fallback when the station pool is empty (officers on patrol/checkpoint/sentry).
        /// </summary>
        private void RedirectNearbyOfficers(int count, Player player)
        {
            try
            {
                var officers = PoliceOfficer.Officers;
                if (officers == null || officers.Count == 0)
                {
                    _logger.Warning("[DrifterManager] RedirectNearbyOfficers: No officers in world");
                    return;
                }

                string playerCode = player.PlayerCode;
                if (string.IsNullOrEmpty(playerCode))
                {
                    _logger.Warning("[DrifterManager] RedirectNearbyOfficers: PlayerCode is null");
                    return;
                }

                var playerPos = player.transform.position;

                // Build list of available officers sorted by distance
                var candidates = new List<(PoliceOfficer officer, float dist)>();
                for (int i = 0; i < officers.Count; i++)
                {
                    var off = officers[i];
                    if (off == null) continue;

                    try
                    {
                        if (!off.IsConscious) continue;
                        if (off.PursuitBehaviour != null && off.PursuitBehaviour.Active) continue;
                    }
                    catch { continue; }

                    float dist = Vector3.Distance(off.transform.position, playerPos);
                    candidates.Add((off, dist));
                }

                candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

                int redirected = 0;
                for (int i = 0; i < candidates.Count && redirected < count; i++)
                {
                    try
                    {
                        candidates[i].officer.BeginFootPursuit_Networked(playerCode, true);
                        redirected++;
                        _logger.Msg($"[DrifterManager] Redirected officer at dist={candidates[i].dist:F0}m to pursue player");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"[DrifterManager] Failed to redirect officer: {ex.Message}");
                    }
                }

                if (redirected == 0)
                    _logger.Warning("[DrifterManager] RedirectNearbyOfficers: No available officers found");
                else
                    _logger.Msg($"[DrifterManager] Redirected {redirected}/{count} officers to pursue");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DrifterManager] RedirectNearbyOfficers failed: {ex.Message}");
            }
        }

        // Track narc post-sting dialogue choices and whether backup was already called
        private readonly Dictionary<string, DialogueController.DialogueChoice> _narcPostStingChoices = new();
        private readonly HashSet<string> _narcBackupDispatched = new();

        /// <summary>
        /// Adds a dialogue choice to the narc drifter after the sting.
        /// If the player talks to them, the narc calls police again.
        /// </summary>
        private void SetupNarcPostStingDialogue(DrifterEvent evt, DrifterInstance drifter)
        {
            if (drifter?.GameNpc?.DialogueHandler == null) return;

            try
            {
                var dialogueController = drifter.GameNpc.DialogueHandler.GetComponent<DialogueController>();
                if (dialogueController == null) return;

                var choice = new DialogueController.DialogueChoice();
                choice.ChoiceText = "[Talk]";
                choice.Enabled = true;
                choice.Conversation = null;
                choice.onChoosen = new UnityEvent();

                var drifterId = evt.DrifterId;
                choice.onChoosen.AddListener((UnityAction)(() => OnNarcPostStingTalk(drifterId)));

                choice.shouldShowCheck = (Func<bool, bool>)((bool enabled) =>
                {
                    if (!_activeEvents.TryGetValue(drifterId, out var e)) return false;
                    return e.Type == DrifterType.Narc && e.State >= DrifterEventState.DealCompleted;
                });

                dialogueController.AddDialogueChoice(choice);
                _narcPostStingChoices[drifterId] = choice;
                _logger.Msg($"[DrifterManager] Added narc post-sting dialogue for {drifterId}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DrifterManager] SetupNarcPostStingDialogue failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when player talks to a narc after the sting.
        /// Shows confused dialogue and dispatches more police.
        /// </summary>
        private void OnNarcPostStingTalk(string drifterId)
        {
            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
            if (drifter?.GameNpc == null) return;

            try
            {
                // Confused dialogue
                string[] lines = {
                    "What the— How are you not in cuffs right now?!",
                    "You again?! How did you get past them?!",
                    "No way... They should've had you!",
                    "Are you kidding me?! POLICE!"
                };
                string line = lines[UnityEngine.Random.Range(0, lines.Length)];
                drifter.GameNpc.SendWorldSpaceDialogue(line, 5f);
                drifter.GameNpc.PlayVO(EVOLineType.Alerted);

                var player = Player.Local;
                if (player == null) return;

                // Set wanted status if player escaped
                if (player.CrimeData.CurrentPursuitLevel == PlayerCrimeData.EPursuitLevel.None)
                {
                    player.CrimeData.RecordLastKnownPosition(true);
                    player.CrimeData.AddCrime(new DrugTrafficking());
                    player.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Arresting);
                }

                // Dispatch backup officers only once per narc
                if (_narcBackupDispatched.Add(drifterId))
                {
                    DispatchOfficers(2, player);
                    _logger.Msg($"[DrifterManager] Narc {drifterId} called backup");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DrifterManager] OnNarcPostStingTalk failed: {ex.Message}");
            }
        }

        // Weapon pools for robber type
        private static readonly string[] RobberMeleeWeapons =
        {
            "avatar/equippables/Knife",
            "avatar/equippables/BrokenBottle",
            "avatar/equippables/Baton",
            "avatar/equippables/Hammer"
        };

        private static readonly string[] RobberGunWeapons =
        {
            "avatar/equippables/M1911",
            "avatar/equippables/Revolver"
        };

        /// <summary>
        /// Selects and equips a weapon for a robber drifter.
        /// Seed-based for deterministic multiplayer sync.
        /// Distribution: ~40% fists, ~45% melee, ~15% gun.
        /// </summary>
        private void SelectRobberWeapon(DrifterInstance drifter, int seed)
        {
            try
            {
                // Use seed for deterministic weapon selection (multiplayer sync)
                var state = UnityEngine.Random.state;
                UnityEngine.Random.InitState(seed + 5000); // Offset to avoid collision with appearance seed

                float roll = UnityEngine.Random.value;
                string weaponPath = null;

                if (roll < 0.40f)
                {
                    // Fists - no weapon
                    weaponPath = null;
                }
                else if (roll < 0.85f)
                {
                    // Random melee weapon
                    weaponPath = RobberMeleeWeapons[UnityEngine.Random.Range(0, RobberMeleeWeapons.Length)];
                }
                else
                {
                    // Random gun
                    weaponPath = RobberGunWeapons[UnityEngine.Random.Range(0, RobberGunWeapons.Length)];
                }

                UnityEngine.Random.state = state;

                drifter.EquipWeapon(weaponPath);
                _logger.Msg($"[DrifterManager] Robber {drifter.Id}: weapon={weaponPath ?? "fists"} (roll={roll:F2})");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DrifterManager] SelectRobberWeapon failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Coroutine that delays the robber's attack after the handover screen closes.
        /// Gives the player a moment to read the threatening dialogue.
        /// </summary>
        private IEnumerator DelayedRobberAttack(string drifterId, float delay, Player targetPlayer = null)
        {
            yield return new WaitForSeconds(delay);

            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
            if (drifter != null && drifter.IsValid)
            {
                drifter.AttackPlayer(targetPlayer);
                _logger.Msg($"[DrifterManager] Robber {drifterId}: attack initiated after {delay}s delay (target={targetPlayer?.PlayerCode ?? "local"})");
            }
        }

        /// <summary>
        /// Retries ObjectId capture if FishNet hadn't assigned it synchronously.
        /// Polls every second for up to 60 seconds, re-publishes on success.
        /// </summary>
        private IEnumerator DelayedObjectIdCapture(DrifterEvent evt, DrifterInstance drifter)
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                yield return new WaitForSeconds(1f);

                if (drifter?.GameNpc?.NetworkObject == null) yield break;
                if (evt.NetworkObjectId > 0) yield break; // Already captured elsewhere

                try
                {
                    int objId = drifter.GameNpc.NetworkObject.ObjectId;
                    if (objId > 0)
                    {
                        evt.NetworkObjectId = objId;
                        _logger.Msg($"[DrifterManager] Delayed ObjectId capture: {evt.DrifterId} → {objId} (attempt {attempt + 1})");
                        ConfigSyncData.Instance?.PublishDrifterState();
                        yield break;
                    }
                }
                catch { yield break; }
            }

            _logger.Warning($"[DrifterManager] ObjectId capture failed after 60s for {evt.DrifterId}");
        }

        private string GetLocationHint(string hotspotName)
        {
            return DrifterHotspots.GetHotspotByName(hotspotName)?.Description;
        }

        private void ExpireDrifterOffer(DrifterEvent evt, DrifterInstance drifter)
        {
            _logger.Msg($"[DrifterManager] Offer expired for drifter {evt.DrifterId}");

            // Send expiry text and clear response buttons so player can't accept after timeout
            if (drifter != null)
            {
                try
                {
                    drifter.SendTextMessage(drifter.GetExpiryTextMessage());
                    drifter.GameNpc?.MSGConversation?.ClearResponses(true);
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

            // Fail the quest
            if (DrifterDealQuest.ActiveQuests.TryGetValue(evt.DrifterId, out var quest))
                quest.FailDeal();

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

            // Clean up quest if still active
            if (DrifterDealQuest.ActiveQuests.TryGetValue(evt.DrifterId, out var quest))
                quest.CancelDeal();

            // Clean up dialogue choices
            CleanupDealDialogueChoice(evt.DrifterId, drifter);
            CleanupNarcDialogueChoice(evt.DrifterId);

            HideDrifterConversation(evt.DrifterId);

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

            if (evt.State >= DrifterEventState.DealAccepted)
            {
                _logger.Msg($"[DrifterManager] OnDealAccepted: drifter {drifterId} already in state {evt.State}, ignoring");
                return;
            }

            evt.State = DrifterEventState.DealAccepted;
            evt.DeliveryDeadline = GetCurrentElapsedMinutes() + Config.DrifterDeliveryDeadlineMin.Value;

            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
            if (drifter != null)
            {
                drifter.DealAccepted = true;
                drifter.State = DrifterState.DealAccepted;

                try { drifter.GameNpc?.MSGConversation?.ClearResponses(false); } catch { }

                // Set up the dialogue choice for completing the deal via HandoverScreen
                SetupDealDialogueChoice(evt, drifter);
            }

            // Create quest with map marker and timing
            try
            {
                var quest = (DrifterDealQuest)S1API.Quests.QuestManager.CreateQuest<DrifterDealQuest>();
                if (quest != null)
                {
                    var hotspot = DrifterHotspots.GetHotspotByName(evt.HotspotName);
                    quest.Initialize(drifterId, evt.ProductName, evt.Quantity, evt.Payment,
                                    hotspot.Position, hotspot.Description, evt.DeliveryDeadline);
                    quest.StartQuest();
                }
            }
            catch (Exception ex) { _logger.Warning($"[DrifterManager] Quest creation failed: {ex.Message}"); }

            _logger.Msg($"[DrifterManager] Deal accepted for drifter {drifterId}. Delivery deadline: {Config.DrifterDeliveryDeadlineMin.Value} min");
            ConfigSyncData.Instance?.PublishDrifterState();
        }

        /// <summary>
        /// Called when a drifter deal is completed (handover success).
        /// Returns the drifter type so callers can branch on Narc/Robber/etc.
        /// </summary>
        public DrifterType OnDealCompleted(string drifterId)
        {
            if (!NetworkHelper.IsHost) return DrifterType.Normal;

            if (!_activeEvents.TryGetValue(drifterId, out var evt))
                return DrifterType.Normal;

            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);

            evt.State = DrifterEventState.DealCompleted;

            // Complete the quest
            if (DrifterDealQuest.ActiveQuests.TryGetValue(drifterId, out var quest))
                quest.CompleteDeal();

            // Clean up dialogue choice since deal is done
            CleanupDealDialogueChoice(drifterId, drifter);

            // Start linger timer
            int lingerTime = UnityEngine.Random.Range(
                Config.DrifterLingerMinMin.Value,
                Config.DrifterLingerMaxMin.Value + 1);
            evt.LingerDeadline = GetCurrentElapsedMinutes() + lingerTime;

            if (drifter != null)
            {
                drifter.DealCompleted = true;
                drifter.ArrivedAtDestination = true; // Prevent EnsureMoving from walking to destination
                drifter.State = DrifterState.DealCompleted;
            }

            _logger.Msg($"[DrifterManager] Deal completed for drifter {drifterId}. Type={evt.Type}");
            ConfigSyncData.Instance?.PublishDrifterState();

            return evt.Type;
        }

        /// <summary>
        /// Called by the host when a remote client sends DRIFTER_COMPLETE.
        /// Completes the deal and triggers narc/robber actions against the client player.
        /// </summary>
        public void OnRemoteDealCompleted(string drifterId, string playerCode = "")
        {
            var completedType = OnDealCompleted(drifterId);
            var evt = _activeEvents.GetValueOrDefault(drifterId);

            // Resolve the client player who triggered the deal
            Player clientPlayer = FindPlayerByCode(playerCode);
            _logger.Msg($"[DrifterManager] Remote DRIFTER_COMPLETE: {drifterId} type={completedType} playerCode={playerCode} playerFound={clientPlayer != null}");

            if (completedType == DrifterType.Narc && evt != null)
            {
                TriggerNarcSting(evt, clientPlayer);
            }
            else if (completedType == DrifterType.Robber)
            {
                // Host must initiate robber attack (requires server authority)
                var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
                if (drifter != null)
                {
                    drifter.IsAttacking = true;
                    SelectRobberWeapon(drifter, evt?.Seed ?? 0);
                    MelonCoroutines.Start(DelayedRobberAttack(drifterId, 0.5f, clientPlayer));
                }
            }
        }

        /// <summary>
        /// Gets a drifter event by ID.
        /// </summary>
        public DrifterEvent GetEvent(string drifterId)
        {
            return _activeEvents.GetValueOrDefault(drifterId);
        }

        /// <summary>
        /// Finds a Player by their PlayerCode. Falls back to Player.Local if not found.
        /// </summary>
        private static Player FindPlayerByCode(string playerCode)
        {
            if (string.IsNullOrEmpty(playerCode))
                return Player.Local;

            try
            {
                var playerList = Player.PlayerList;
                if (playerList != null)
                {
                    for (int i = 0; i < playerList.Count; i++)
                    {
                        if (playerList[i]?.PlayerCode == playerCode)
                            return playerList[i];
                    }
                }
            }
            catch { }

            return Player.Local;
        }

        /// <summary>
        /// Gets all active drifter events that have accepted deals (for handover detection).
        /// </summary>
        public IEnumerable<DrifterEvent> GetAcceptedDeals()
        {
            return _activeEvents.Values.Where(e => e.State == DrifterEventState.DealAccepted);
        }

        /// <summary>
        /// Serializes drifter state for network sync via dedicated SyncVar.
        /// Format: id:type:hotspot:state:productId:qty:pay:deadline:netObjId;...
        /// </summary>
        public string SerializeDrifterState()
        {
            if (_activeEvents.Count == 0)
                return "";

            var parts = new List<string>();
            foreach (var evt in _activeEvents.Values)
            {
                string productId = evt.ProductId ?? "unknown";
                string entry = $"{evt.DrifterId}:{(int)evt.Type}:{evt.HotspotName}:{(int)evt.State}:{productId}:{evt.Quantity}:{evt.Payment:F0}:{evt.DeliveryDeadline}:{evt.NetworkObjectId}";
                parts.Add(entry);
            }
            return string.Join(";", parts);
        }

        /// <summary>
        /// Finds a FishNet-replicated NPC by its NetworkObject.ObjectId.
        /// This is the authoritative way to identify which NPC on the client
        /// corresponds to which drifter on the host — ObjectIds are deterministic
        /// across host and client in FishNet.
        /// Falls back to position-based matching near the hotspot when ObjectId is unavailable.
        /// </summary>
        private NPC FindNetworkDrifter(int objectId, DrifterHotspots.Hotspot hotspot = null)
        {
            var registry = NPCManager.NPCRegistry;
            if (registry == null) return null;

            // Build set of already-tracked NPCs once
            var trackedNpcs = new HashSet<NPC>();
            foreach (var d in DrifterInstance.Active.Values)
            {
                if (d.GameNpc != null) trackedNpcs.Add(d.GameNpc);
            }

            // Phase 1: Try exact ObjectId match (fast, deterministic)
            if (objectId > 0)
            {
                for (int i = 0; i < registry.Count; i++)
                {
                    var npc = registry[i];
                    if (npc == null || npc.gameObject == null) continue;
                    if (trackedNpcs.Contains(npc)) continue;

                    try
                    {
                        var netObj = npc.NetworkObject;
                        if (netObj != null && netObj.ObjectId == objectId)
                        {
                            _logger.Msg($"[DrifterManager] Found NPC by ObjectId={objectId}: {npc.gameObject.name}");
                            return npc;
                        }
                    }
                    catch { }
                }
            }

            // Phase 2: Position-based fallback near hotspot (when ObjectId is 0 or not found)
            // Only matches NPCs with empty ID — FishNet-spawned drifter clones have the prefab's
            // default empty ID, while regular game NPCs have IDs set in the scene (e.g. "stan").
            if (hotspot == null) return null;

            const float maxDistance = 50f;
            NPC bestMatch = null;
            float bestDist = maxDistance;

            for (int i = 0; i < registry.Count; i++)
            {
                var npc = registry[i];
                if (npc == null || npc.gameObject == null) continue;
                if (trackedNpcs.Contains(npc)) continue;

                try
                {
                    // Skip named scene NPCs — only match bare prefab clones
                    if (!string.IsNullOrEmpty(npc.ID)) continue;

                    var pos = npc.transform.position;
                    float distToSpawn = Vector3.Distance(pos, hotspot.SpawnPosition);
                    float distToDest = Vector3.Distance(pos, hotspot.Position);
                    float dist = Mathf.Min(distToSpawn, distToDest);

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestMatch = npc;
                    }
                }
                catch { }
            }

            if (bestMatch != null)
                _logger.Msg($"[DrifterManager] Found NPC by position fallback (dist={bestDist:F1}m): {bestMatch.gameObject.name}");

            return bestMatch;
        }

        /// <summary>
        /// Retries pending adoptions for drifters whose FishNet NPC hadn't arrived yet.
        /// Called from the main update loop.
        /// </summary>
        public void RetryPendingAdoptions()
        {
            if (_pendingAdoptions.Count == 0) return;

            float now = Time.time;
            if (now - _lastAdoptionRetry < 0.5f) return; // retry every 0.5s
            _lastAdoptionRetry = now;

            var completed = new List<string>();
            foreach (var kv in _pendingAdoptions)
            {
                var pa = kv.Value;

                // Give up after 120 seconds
                if (now - pa.CreatedTime > 120f)
                {
                    _logger.Warning($"[DrifterManager] Giving up adoption for {pa.DrifterId} (timeout)");
                    completed.Add(kv.Key);
                    continue;
                }

                var npc = FindNetworkDrifter(pa.NetworkObjectId, pa.Hotspot);
                if (npc == null) continue;

                var drifter = DrifterInstance.Adopt(pa.DrifterId, pa.Type, pa.Hotspot, pa.Seed, npc);
                if (drifter != null)
                {
                    SetupAdoptedDrifter(drifter, pa);
                    completed.Add(kv.Key);
                }
            }

            foreach (var id in completed)
                _pendingAdoptions.Remove(id);
        }

        /// <summary>
        /// Client-side tick: updates quest timers for active drifter quests.
        /// Called from Core.OnLateUpdate because OnTimeTick is host-only.
        /// </summary>
        public void ClientQuestTick()
        {
            if (NetworkHelper.IsHost) return;
            if (DrifterDealQuest.ActiveQuests.Count == 0) return;

            float now = Time.time;
            if (now - _lastClientQuestTick < 1f) return; // update every second
            _lastClientQuestTick = now;

            foreach (var quest in DrifterDealQuest.ActiveQuests.Values)
            {
                try { quest.UpdateTiming(); }
                catch { }
            }
        }

        /// <summary>
        /// Sets up an adopted drifter with state, messaging, dialogue, and movement.
        /// </summary>
        private void SetupAdoptedDrifter(DrifterInstance drifter, PendingAdoption pa)
        {
            var state = pa.State;
            drifter.State = state switch
            {
                DrifterEventState.DealAccepted => DrifterState.DealAccepted,
                DrifterEventState.DealCompleted => DrifterState.DealCompleted,
                DrifterEventState.Lingering => DrifterState.Lingering,
                _ => DrifterState.Spawned
            };
            drifter.DealAccepted = state >= DrifterEventState.DealAccepted;
            drifter.DealCompleted = state >= DrifterEventState.DealCompleted;

            // Adopted NPCs are already active — add Customer component for handover support
            if (drifter.GameNpc != null)
                DrifterSpawner.AddCustomerComponentToActive(drifter.GameNpc);

            // Replay intro text locally (network=false) with deterministic messageId for dedup
            if (_activeEvents.TryGetValue(pa.DrifterId, out var evt) && state >= DrifterEventState.OfferSent)
            {
                try
                {
                    var conversation = drifter.GameNpc?.MSGConversation;
                    if (conversation != null)
                    {
                        string locationHint = pa.Hotspot.Description;
                        string introMsg = drifter.GetIntroTextMessage(evt.ProductName, evt.Quantity, evt.Payment, locationHint);

                        var msg = new Il2CppScheduleOne.Messaging.Message(introMsg, Il2CppScheduleOne.Messaging.Message.ESenderType.Other, true);
                        msg.messageId = DeterministicHash(pa.DrifterId + "_intro");
                        conversation.SendMessage(msg, true, false); // local only
                    }

                    if (state == DrifterEventState.OfferSent)
                        ShowDealResponses(evt, drifter);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[DrifterManager] Messaging replay failed for {pa.DrifterId}: {ex.Message}");
                }
            }

            if (state == DrifterEventState.DealAccepted && evt != null)
            {
                try
                {
                    SetupDealDialogueChoice(evt, drifter);
                    CreateClientQuest(evt, pa.Hotspot);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[DrifterManager] Quest/dialogue setup failed for {pa.DrifterId}: {ex.Message}");
                }
            }

            _logger.Msg($"[DrifterManager] Client adopted drifter {pa.DrifterId}: state={state}, type={pa.Type}");
        }

        /// <summary>
        /// Creates a quest with map marker on the client for an accepted drifter deal.
        /// </summary>
        private void CreateClientQuest(DrifterEvent evt, DrifterHotspots.Hotspot hotspot)
        {
            if (DrifterDealQuest.ActiveQuests.ContainsKey(evt.DrifterId)) return;
            try
            {
                var quest = (DrifterDealQuest)S1API.Quests.QuestManager.CreateQuest<DrifterDealQuest>();
                if (quest != null)
                {
                    quest.Initialize(evt.DrifterId, evt.ProductName, evt.Quantity, evt.Payment,
                                    hotspot.Position, hotspot.Description, evt.DeliveryDeadline);
                    quest.StartQuest();
                    _logger.Msg($"[DrifterManager] Client quest created for {evt.DrifterId}");
                }
            }
            catch (Exception ex) { _logger.Warning($"[DrifterManager] Client quest creation failed: {ex.Message}"); }
        }

        /// <summary>
        /// Applies drifter state from network sync (client-side).
        /// Finds FishNet-replicated NPCs by position and adopts them, applying
        /// appearance, messaging, and dialogue choices.
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
                    // Format: id:type:hotspot:state:productId:qty:pay:deadline:netObjId (9 fields)
                    if (parts.Length < 5) continue;

                    string drifterId = parts[0];
                    hostDrifters.Add(drifterId);

                    if (!int.TryParse(parts[1], out int typeInt)) continue;
                    string hotspotName = parts[2];
                    if (!int.TryParse(parts[3], out int stateInt)) continue;

                    string productId = parts.Length > 4 ? parts[4] : "unknown";
                    int quantity = parts.Length > 5 && int.TryParse(parts[5], out int q) ? q : 1;
                    float payment = parts.Length > 6 && float.TryParse(parts[6], out float p) ? p : 50f;
                    int deadline = parts.Length > 7 && int.TryParse(parts[7], out int dl) ? dl : 0;
                    int netObjId = parts.Length > 8 && int.TryParse(parts[8], out int nid) ? nid : 0;

                    // Derive seed deterministically from drifterId — no need to sync it
                    int seed = DeterministicHash(drifterId);

                    var type = (DrifterType)typeInt;
                    var state = (DrifterEventState)stateInt;

                    // Create/update client-side DrifterEvent so handover flow works
                    if (!_activeEvents.TryGetValue(drifterId, out var evt))
                    {
                        evt = new DrifterEvent
                        {
                            DrifterId = drifterId,
                            Type = type,
                            HotspotName = hotspotName,
                            Seed = seed,
                            ProductId = productId,
                            ProductName = LookupProductName(productId),
                            Quantity = quantity,
                            Payment = payment,
                            State = state,
                            DeliveryDeadline = deadline,
                        };
                        _activeEvents[drifterId] = evt;
                    }
                    else
                    {
                        evt.State = state;
                        if (deadline > 0) evt.DeliveryDeadline = deadline;
                    }

                    var hotspot = DrifterHotspots.GetHotspotByName(hotspotName);
                    if (hotspot == null)
                    {
                        _logger.Warning($"[DrifterManager] Hotspot '{hotspotName}' not found for {drifterId}, skipping");
                        continue;
                    }

                    // Try to adopt FishNet-replicated NPC on client
                    if (!DrifterInstance.Active.ContainsKey(drifterId))
                    {
                        // Skip if already pending adoption (but keep state/ObjectId current)
                        if (_pendingAdoptions.TryGetValue(drifterId, out var existingPa))
                        {
                            existingPa.State = state;
                            if (netObjId > 0)
                                existingPa.NetworkObjectId = netObjId;
                            continue;
                        }

                        var npc = FindNetworkDrifter(netObjId, hotspot);
                        if (npc != null)
                        {
                            var drifter = DrifterInstance.Adopt(drifterId, type, hotspot, seed, npc);
                            if (drifter != null)
                            {
                                var pa = new PendingAdoption
                                {
                                    DrifterId = drifterId, Type = type, Hotspot = hotspot,
                                    Seed = seed, State = state, NetworkObjectId = netObjId
                                };
                                SetupAdoptedDrifter(drifter, pa);
                            }
                        }
                        else
                        {
                            // FishNet NPC hasn't arrived yet — queue for retry
                            _pendingAdoptions[drifterId] = new PendingAdoption
                            {
                                DrifterId = drifterId, Type = type, Hotspot = hotspot,
                                Seed = seed, State = state, NetworkObjectId = netObjId,
                                CreatedTime = Time.time
                            };
                            _logger.Msg($"[DrifterManager] Queued adoption for {drifterId} (netObjId={netObjId}, not in registry yet)");
                        }
                    }
                    else
                    {
                        // Update existing drifter state
                        var drifter = DrifterInstance.Active[drifterId];
                        var prevState = drifter.State;
                        drifter.State = state switch
                        {
                            DrifterEventState.DealAccepted => DrifterState.DealAccepted,
                            DrifterEventState.DealCompleted => DrifterState.DealCompleted,
                            DrifterEventState.Lingering => DrifterState.Lingering,
                            _ => DrifterState.Spawned
                        };
                        drifter.DealAccepted = state >= DrifterEventState.DealAccepted;
                        drifter.DealCompleted = state >= DrifterEventState.DealCompleted;

                        if (state == DrifterEventState.OfferSent && prevState == DrifterState.Spawned)
                        {
                            try
                            {
                                var conversation = drifter.GameNpc?.MSGConversation;
                                if (conversation != null)
                                {
                                    string locationHint = hotspot?.Description ?? "";
                                    string introMsg = drifter.GetIntroTextMessage(evt.ProductName, evt.Quantity, evt.Payment, locationHint);
                                    var msg = new Il2CppScheduleOne.Messaging.Message(introMsg, Il2CppScheduleOne.Messaging.Message.ESenderType.Other, true);
                                    msg.messageId = DeterministicHash(drifterId + "_intro");
                                    conversation.SendMessage(msg, true, false);
                                }
                                ShowDealResponses(evt, drifter);
                            }
                            catch (Exception ex)
                            {
                                _logger.Warning($"[DrifterManager] OfferSent replay failed for {drifterId}: {ex.Message}");
                            }
                        }

                        // Set up dialogue choice and quest on transition to DealAccepted
                        if (state == DrifterEventState.DealAccepted && prevState != DrifterState.DealAccepted)
                        {
                            try { drifter.GameNpc?.MSGConversation?.ClearResponses(false); } catch { }
                            SetupDealDialogueChoice(evt, drifter);
                            CreateClientQuest(evt, hotspot);
                        }

                        if (state == DrifterEventState.Lingering && prevState == DrifterState.Spawned)
                        {
                            try { drifter.GameNpc?.MSGConversation?.ClearResponses(false); } catch { }
                        }

                        // Clean up quest and dialogue on deal completion
                        if (state >= DrifterEventState.DealCompleted && prevState < DrifterState.DealCompleted)
                        {
                            if (DrifterDealQuest.ActiveQuests.TryGetValue(drifterId, out var clientQuest))
                                clientQuest.CompleteDeal();
                            CleanupDealDialogueChoice(drifterId, drifter);
                        }

                        // Consume animation or walk-back on deal completion
                        if (!drifter.IsWalkingBack && !drifter.IsConsuming &&
                            (state == DrifterEventState.DealCompleted || state == DrifterEventState.Lingering))
                        {
                            if (state == DrifterEventState.DealCompleted && evt.Type != DrifterType.Narc && !string.IsNullOrEmpty(evt.ProductId))
                                drifter.PlayConsumeAnimation(evt.ProductId);
                            else
                                drifter.WalkToSpawn();
                        }
                    }
                }
            }

            // Remove drifters not on host — also clean up pending adoptions
            var localDrifterIds = DrifterInstance.Active.Keys.ToList();
            foreach (var id in localDrifterIds)
            {
                if (!hostDrifters.Contains(id))
                {
                    try { DrifterInstance.Active[id].Despawn(); }
                    catch { }
                }
            }
            foreach (var id in _pendingAdoptions.Keys.ToList())
            {
                if (!hostDrifters.Contains(id))
                    _pendingAdoptions.Remove(id);
            }

            // Clean up stale client-side events, dialogue choices, and message threads
            var staleEvents = _activeEvents.Keys.Where(k => !hostDrifters.Contains(k)).ToList();
            foreach (var id in staleEvents)
            {
                CleanupDealDialogueChoice(id, null);
                HideDrifterConversation(id);
                _activeEvents.Remove(id);
            }
        }

        private static void HideDrifterConversation(string drifterId)
        {
            if (!DrifterSpawner.DrifterConversations.TryGetValue(drifterId, out var conv))
                return;
            try { conv?.entry?.gameObject?.SetActive(false); } catch { }
            DrifterSpawner.DrifterConversations.Remove(drifterId);
        }

        private static void CleanupStaleConversations()
        {
            var conversations = DrifterSpawner.DrifterConversations;
            if (conversations.Count == 0) return;

            var stale = conversations.Where(kvp => kvp.Value == null || kvp.Value.sender == null).Select(kvp => kvp.Key).ToList();
            foreach (var id in stale)
                HideDrifterConversation(id);
        }

        /// <summary>
        /// Looks up the display name for a product ID from ProductManager.
        /// Falls back to the raw ID if not found.
        /// </summary>
        private string LookupProductName(string productId)
        {
            try
            {
                var listed = Il2CppScheduleOne.Product.ProductManager.ListedProducts;
                if (listed != null)
                {
                    for (int i = 0; i < listed.Count; i++)
                    {
                        if (listed[i]?.ID == productId)
                            return listed[i].Name ?? productId;
                    }
                }
            }
            catch { }
            return productId;
        }

        /// <summary>
        /// Deterministic string hash (DJB2) — unlike string.GetHashCode(), this produces
        /// identical results across different .NET processes (host vs client).
        /// </summary>
        private static int DeterministicHash(string s)
        {
            unchecked
            {
                int hash = 5381;
                foreach (char c in s)
                    hash = ((hash << 5) + hash) + c;
                return hash;
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

            // Clean up all active drifter quests
            foreach (var quest in DrifterDealQuest.ActiveQuests.Values.ToList())
            {
                try { quest.CancelDeal(); } catch { }
            }

            _activeEvents.Clear();
            _dealChoices.Clear();
            _narcPostStingChoices.Clear();
            _narcBackupDispatched.Clear();

            // Clean up drifter contracts
            foreach (var contract in _drifterContracts.Values)
            {
                try
                {
                    if (contract?.gameObject != null)
                        UnityEngine.Object.Destroy(contract.gameObject);
                }
                catch { }
            }
            _drifterContracts.Clear();

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

            // Use the main SpawnDrifter path — ensures ObjectId capture, lifecycle, and state sync
            Instance.SpawnDrifter(type);

            return true;
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

        // Deal request info (Phase 2)
        public string ProductId { get; set; }
        public string ProductName { get; set; }
        public int Quantity { get; set; }
        public float Payment { get; set; }

        // FishNet ObjectId for reliable client-side NPC lookup
        public int NetworkObjectId { get; set; }
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
