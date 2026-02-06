using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI.Handover;
using MelonLoader;
using S1API.GameTime;
using S1API.Items;
using S1API.Products;
using System;
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
                    // Walk from spawn point to destination
                    drifter.WalkToDestination();

                    // Schedule intro text after brief delay
                    evt.State = DrifterEventState.OfferPending;
                    evt.TextSendTime = spawnTime + 1; // 1 minute delay before text

                    _logger.Msg($"[DrifterManager] Spawned drifter {drifterId}: Type={type}, Hotspot={hotspot.Name}, Spawn={hotspot.SpawnPosition}, Dest={hotspot.Position}, OfferDeadline={Config.DrifterOfferWindowMin.Value}min");

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
                        // Player needs to approach and interact with drifter to open HandoverScreen
                        // The dialogue choice handles opening the handover screen
                        // Check delivery deadline
                        if (currentMinutes >= evt.DeliveryDeadline)
                        {
                            FailDrifterDelivery(evt, drifter);
                        }
                        break;

                    case DrifterEventState.DealCompleted:
                    case DrifterEventState.Lingering:
                        // Start walking back if not already
                        if (drifter != null && !drifter.IsWalkingBack)
                        {
                            drifter.WalkToSpawn();
                        }

                        // Despawn when no players are nearby (or linger deadline as hard fallback)
                        bool noPlayersNearby = drifter?.Position != null &&
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
                ConfigSyncData.Instance?.PublishGameState();
        }

        private void SendIntroText(DrifterEvent evt, DrifterInstance drifter)
        {
            if (drifter == null) return;

            try
            {
                // Get location hint for the intro text
                string locationHint = GetLocationHint(evt.HotspotName);

                string message = drifter.GetIntroTextMessage(evt.ProductName, evt.Quantity, evt.Payment, locationHint);

                drifter.SendTextMessage(message);
                _logger.Msg($"[DrifterManager] Sent intro text for drifter {evt.DrifterId}: {evt.Quantity}x {evt.ProductName} @ ${evt.Payment}");

                // Show response buttons after intro text
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

                // Create Accept response
                string drifterId = evt.DrifterId;
                var acceptCallback = (Il2CppSystem.Action)new System.Action(() => OnAcceptResponse(drifterId));
                var acceptResponse = new Response("I'm on my way", "accept", acceptCallback, false);
                responses.Add(acceptResponse);

                // Create Decline response
                var declineCallback = (Il2CppSystem.Action)new System.Action(() => OnDeclineResponse(drifterId));
                var declineResponse = new Response("Not interested", "decline", declineCallback, false);
                responses.Add(declineResponse);

                // Show responses with slight delay for natural feel
                conversation.ShowResponses(responses, 0.5f, true);
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

            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
            if (drifter == null) return;

            // Send confirmation text from drifter (location already in intro)
            string confirmMessage = evt.Type switch
            {
                DrifterType.Whale => "Good. Come alone. Don't keep me waiting.",
                DrifterType.Fiend => "THANK GOD. HURRY UP.",
                DrifterType.Narc => "Perfect. See you soon.",
                _ => "Cool. Don't keep me waiting."
            };
            drifter.SendTextMessage(confirmMessage);

            // Trigger the deal accepted flow (on host or via network)
            if (NetworkHelper.IsHost)
            {
                OnDealAccepted(drifterId);
            }
            else
            {
                ConfigSyncData.SendQuestAction($"DRIFTER_ACCEPT:{drifterId}");
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

            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);
            if (drifter != null)
            {
                // Send disappointed text
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

                ConfigSyncData.Instance?.PublishGameState();
            }
        }

        // =====================================================
        // HANDOVER SCREEN INTEGRATION
        // =====================================================

        // Track dialogue choices per drifter so we can clean them up
        private readonly Dictionary<string, DialogueController.DialogueChoice> _dealChoices = new();

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

                // Add the expected product entry
                var productEntry = new Il2CppScheduleOne.Product.ProductList.Entry(
                    evt.ProductId,
                    Il2CppScheduleOne.ItemFramework.EQuality.Standard, // Accept any quality
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
                // Player cancelled - deal still pending
                _logger.Msg($"[DrifterManager] Handover cancelled for {drifterId}, deal still pending");
                return;
            }

            // outcome == Finalize - deal completed
            if (!_activeEvents.TryGetValue(drifterId, out var evt))
                return;

            var drifter = DrifterInstance.Active.GetValueOrDefault(drifterId);

            // Give player money
            try
            {
                var moneyManager = NetworkSingleton<MoneyManager>.Instance;
                if (moneyManager != null)
                {
                    moneyManager.ChangeCashBalance(evt.Payment, true, true);
                    _logger.Msg($"[DrifterManager] Gave player ${evt.Payment} for drifter deal");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] Failed to give payment: {ex.Message}");
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

            // Complete the deal
            bool isNarc = OnDealCompleted(drifterId);

            // Trigger narc sting if applicable
            if (isNarc)
            {
                TriggerNarcSting(evt);
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

        /// <summary>
        /// Triggers a narc sting (police response) using reflection.
        /// </summary>
        private void TriggerNarcSting(DrifterEvent evt)
        {
            try
            {
                _logger.Msg("[DrifterManager] NARC STING! Triggering police response.");

                var lawManager = Singleton<LawManager>.Instance;
                if (lawManager == null)
                {
                    _logger.Warning("[DrifterManager] LawManager singleton not found");
                    return;
                }

                // Set wanted level using reflection
                try
                {
                    var setWantedMethod = typeof(LawManager).GetMethod("SetWantedLevel",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                    if (setWantedMethod != null)
                    {
                        var ewantedType = typeof(LawManager).Assembly.GetType("Il2CppScheduleOne.Law.EWantedLevel");
                        if (ewantedType != null)
                        {
                            var arrestingValue = Enum.ToObject(ewantedType, 3);
                            setWantedMethod.Invoke(lawManager, new object[] { arrestingValue });
                            _logger.Msg("[DrifterManager] Set wanted level to Arresting");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[DrifterManager] SetWantedLevel failed: {ex.Message}");
                }

                // Call police using reflection
                var playerMovement = PlayerSingleton<PlayerMovement>.Instance;
                if (playerMovement != null)
                {
                    try
                    {
                        var callPoliceMethod = typeof(LawManager).GetMethod("CallPolice",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                        if (callPoliceMethod != null)
                        {
                            var parameters = callPoliceMethod.GetParameters();
                            if (parameters.Length >= 1)
                            {
                                if (parameters.Length == 1)
                                    callPoliceMethod.Invoke(lawManager, new object[] { playerMovement.transform.position });
                                else
                                    callPoliceMethod.Invoke(lawManager, new object[] { playerMovement.transform.position, 3 });

                                _logger.Msg("[DrifterManager] Called police to player location");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"[DrifterManager] CallPolice failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[DrifterManager] TriggerNarcSting failed: {ex.Message}");
            }
        }

        private string GetLocationHint(string hotspotName)
        {
            return DrifterHotspots.GetHotspotByName(hotspotName)?.Description;
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

            // Clean up dialogue choice
            CleanupDealDialogueChoice(evt.DrifterId, drifter);

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

                // Set up the dialogue choice for completing the deal via HandoverScreen
                SetupDealDialogueChoice(evt, drifter);
            }

            // Create quest with map marker
            try
            {
                var quest = (DrifterDealQuest)S1API.Quests.QuestManager.CreateQuest<DrifterDealQuest>();
                if (quest != null)
                {
                    var hotspot = DrifterHotspots.GetHotspotByName(evt.HotspotName);
                    quest.Initialize(drifterId, evt.ProductName, evt.Quantity, evt.Payment,
                                    hotspot.Position, hotspot.Description);
                    quest.StartQuest();
                }
            }
            catch (Exception ex) { _logger.Warning($"[DrifterManager] Quest creation failed: {ex.Message}"); }

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
        /// Format: id:type:seed:hotspot:state:productId:quantity:payment;...
        /// </summary>
        public string SerializeDrifterState()
        {
            if (_activeEvents.Count == 0)
                return "";

            var parts = new List<string>();
            foreach (var evt in _activeEvents.Values)
            {
                // Include deal info for client sync
                string productId = evt.ProductId ?? "unknown";
                parts.Add($"{evt.DrifterId}:{(int)evt.Type}:{evt.Seed}:{evt.HotspotName}:{(int)evt.State}:{productId}:{evt.Quantity}:{evt.Payment:F0}");
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

                    // Parse deal info (new fields)
                    string productId = parts.Length > 5 ? parts[5] : "unknown";
                    int quantity = parts.Length > 6 && int.TryParse(parts[6], out int q) ? q : 1;
                    float payment = parts.Length > 7 && float.TryParse(parts[7], out float p) ? p : 50f;

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

                                // Command movement based on current state
                                if (state >= DrifterEventState.DealCompleted || state == DrifterEventState.Lingering)
                                    drifter.WalkToSpawn();
                                else
                                    drifter.WalkToDestination();
                            }
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

                        // Start walk-back when transitioning to completed/lingering
                        if (!drifter.IsWalkingBack &&
                            (state == DrifterEventState.DealCompleted || state == DrifterEventState.Lingering))
                        {
                            drifter.WalkToSpawn();
                        }
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

            // Clean up all active drifter quests
            foreach (var quest in DrifterDealQuest.ActiveQuests.Values.ToList())
            {
                try { quest.CancelDeal(); } catch { }
            }

            _activeEvents.Clear();
            _dealChoices.Clear();

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

            // Use a real hotspot so debug spawns mirror actual gameplay (location hints, travel, etc.)
            var debugHotspot = Instance.GetAvailableHotspot();
            if (debugHotspot == null)
            {
                MelonLoader.MelonLogger.Msg("[DrifterManager] DEBUG: No available hotspots, all occupied");
                return false;
            }

            MelonLoader.MelonLogger.Msg($"[DrifterManager] DEBUG: Using hotspot '{debugHotspot.Name}' at {debugHotspot.Position}");

            Instance.SpawnDrifterAtHotspot(type, debugHotspot);

            MelonLoader.MelonLogger.Msg($"[DrifterManager] DEBUG: After spawn - ActiveEvents={Instance._activeEvents.Count}, ActiveDrifters={DrifterInstance.Active.Count}");

            // Immediately send intro text for debug spawns (using the full SendIntroText flow)
            foreach (var evt in Instance._activeEvents.Values)
            {
                MelonLoader.MelonLogger.Msg($"[DrifterManager] DEBUG: Event {evt.DrifterId} - TextSent={evt.TextSent}, State={evt.State}, Product={evt.ProductName}, Qty={evt.Quantity}, Price=${evt.Payment}");

                if (!evt.TextSent)
                {
                    if (DrifterInstance.Active.TryGetValue(evt.DrifterId, out var drifter))
                    {
                        // Use the full SendIntroText which includes deal info and response buttons
                        Instance.SendIntroText(evt, drifter);
                        evt.TextSent = true;
                        evt.State = DrifterEventState.OfferSent;
                        MelonLoader.MelonLogger.Msg($"[DrifterManager] DEBUG: Sent intro text with deal info for drifter {evt.DrifterId}");
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
                    DeliveryDeadline = 0,
                    LingerDeadline = 0,
                    State = DrifterEventState.Spawned
                };

                // Generate deal request — abort if no listed products
                if (!GenerateDealRequest(evt))
                    return;

                _activeEvents[drifterId] = evt;

                _logger.Msg($"[DrifterManager] Spawning drifter {drifterId} at spawn={hotspot.SpawnPosition}, dest={hotspot.Position}");

                // Create the NPC (spawns at SpawnPosition)
                var drifter = DrifterInstance.Create(drifterId, type, hotspot, seed);
                if (drifter != null)
                {
                    // Walk from spawn point to destination
                    drifter.WalkToDestination();

                    evt.State = DrifterEventState.OfferPending;
                    evt.TextSendTime = spawnTime + 1;

                    _logger.Msg($"[DrifterManager] Spawned drifter {drifterId}: Type={type}, Hotspot={hotspot.Name}");

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

        // Deal request info (Phase 2)
        public string ProductId { get; set; }
        public string ProductName { get; set; }
        public int Quantity { get; set; }
        public float Payment { get; set; }
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
