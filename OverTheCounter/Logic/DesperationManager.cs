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

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Quests;
#else
using ScheduleOne.Economy;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Product;
using ScheduleOne.Quests;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// The "Director" system that manages Desperation events.
    /// Randomly triggers high-addiction customers to demand immediate product during the day.
    /// </summary>
    public class DesperationManager
    {
        private readonly MelonLogger.Instance _logger;

        // Active desperation events: CustomerID -> Deadline (in elapsed minutes)
        private readonly Dictionary<string, DesperationEvent> _activeEvents = new();

        // Customers on cooldown: CustomerID -> Cooldown end time (elapsed minutes)
        private readonly Dictionary<string, int> _customerCooldowns = new();

        // Daily tracking
        private int _dailyEventsTriggered = 0;
        private int _lastDayTracked = -1;
        private int _lastHourChecked = -1;

        // Static instance for access from Harmony patches
        public static DesperationManager Instance { get; private set; }

        // Debug override: when set, ForceCustomerDealOffer uses this product ID
        // instead of looking up purchase history. Cleared after use.
        internal static string DebugProductId;

        public DesperationManager(MelonLogger.Instance logger)
        {
            _logger = logger;
            Instance = this;

            // Subscribe to time events
            TimeManager.OnTick += OnTimeTick;
            TimeManager.OnDayPass += OnDayPass;

        }

        /// <summary>
        /// Called every game tick to check for hourly updates and deadline expirations.
        /// </summary>
        private void OnTimeTick()
        {
            if (!Config.DesperationEnabled.Value) return;
            if (!NetworkHelper.IsHost) return;

            try
            {
                int currentTime = TimeManager.CurrentTime;
                int currentHour = currentTime / 100;  // Extract hour from 24-hour format

                // Check for hourly director roll (only during day phase)
                if (currentHour != _lastHourChecked && IsDayPhase(currentTime))
                {
                    _lastHourChecked = currentHour;
                    TryTriggerDesperationEvent();
                }

                // Check for expired deadlines
                CheckExpiredDeadlines();
            }
            catch (Exception ex)
            {
                _logger.Error($"[DesperationManager] OnTimeTick error: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when a new day starts - reset daily counters.
        /// </summary>
        private void OnDayPass()
        {
            if (!NetworkHelper.IsHost) return;

            int currentDay = TimeManager.ElapsedDays;
            if (currentDay != _lastDayTracked)
            {
                _lastDayTracked = currentDay;
                _dailyEventsTriggered = 0;
                _lastHourChecked = -1;
            }
        }

        /// <summary>
        /// Checks if the current time is within the day phase (8 AM to 9 PM).
        /// </summary>
        private bool IsDayPhase(int time24h)
        {
            return time24h >= Config.DayStartHour.Value && time24h < Config.DayEndHour.Value;
        }

        /// <summary>
        /// The Director's hourly roll - attempts to trigger a desperation event.
        /// </summary>
        private void TryTriggerDesperationEvent()
        {
            if (_dailyEventsTriggered >= Config.MaxEventsPerDay.Value)
                return;

            float roll = UnityEngine.Random.value;
            if (roll > Config.TriggerChancePerHour.Value)
                return;

            var eligibleCustomers = GetEligibleFiends();
            if (eligibleCustomers.Count == 0)
                return;

            int index = UnityEngine.Random.Range(0, eligibleCustomers.Count);
            var customer = eligibleCustomers[index];

            TriggerDesperationEvent(customer);
        }

        /// <summary>
        /// Gets all customers eligible for a desperation event.
        /// Criteria: Fiend addiction level, idle (no contract), not already desperate, not on cooldown.
        /// </summary>
        private List<Customer> GetEligibleFiends()
        {
            var eligible = new List<Customer>();
            var unlocked = Customer.UnlockedCustomers;

            if (unlocked == null) return eligible;

            int currentMinutes = GetCurrentElapsedMinutes();

            for (int i = 0; i < unlocked.Count; i++)
            {
                var customer = unlocked[i];
                if (customer == null || customer.NPC == null) continue;

                string customerId = customer.NPC.ID;

                if (customer.CurrentAddiction < Config.FiendAddictionThreshold.Value)
                    continue;

                if (customer.CurrentContract != null)
                    continue;

                if (customer.GetOfferedContractInfo() != null)
                    continue;

                if (_activeEvents.ContainsKey(customerId))
                    continue;

                if (_customerCooldowns.TryGetValue(customerId, out int cooldownEnd))
                {
                    if (currentMinutes < cooldownEnd)
                        continue;
                    else
                        _customerCooldowns.Remove(customerId);
                }

                if (!customer.NPC.IsConscious)
                    continue;

                eligible.Add(customer);
            }

            return eligible;
        }

        /// <summary>
        /// Triggers a desperation event for the specified customer.
        /// </summary>
        private void TriggerDesperationEvent(Customer customer)
        {
            string customerId = customer.NPC.ID;
            int responseDeadline = GetCurrentElapsedMinutes() + Config.ResponseDeadlineMinutes.Value;

            // Create the event with response deadline (delivery deadline set on accept)
            var evt = new DesperationEvent
            {
                Customer = customer,
                DeadlineMinutes = responseDeadline,
                TriggerTime = TimeManager.CurrentTime,
                IsAccepted = false
            };

            _activeEvents[customerId] = evt;
            _dailyEventsTriggered++;

            // Force the customer to create a deal/contract request
            ForceCustomerDealOffer(customer);

            // Sync desperate IDs to clients
            ConfigSyncData.Instance?.PublishGameState();

            _logger.Msg($"[DesperationManager] Desperation event triggered for {customer.NPC.fullName}. " +
                       $"Response deadline: {Config.ResponseDeadlineMinutes.Value} mins. Daily count: {_dailyEventsTriggered}/{Config.MaxEventsPerDay.Value}");
        }

        /// <summary>
        /// Forces the customer to create a deal offer/contract.
        /// Creates a custom contract based on customer's purchase history.
        /// </summary>
        private void ForceCustomerDealOffer(Customer customer)
        {
            try
            {
                // Reset timing fields to allow contract generation
                customer.SetTimeSinceLastDealCompleted(9999);
                customer.SetTimeSinceLastDealOffered(9999);

                // Priority: DebugProductId → actively listed → customer preference
                string productId = DebugProductId;
                DebugProductId = null;

                if (string.IsNullOrEmpty(productId))
                    productId = GetActivelyListedProduct();

                if (string.IsNullOrEmpty(productId))
                    productId = GetCustomerPreferredProduct(customer);

                if (string.IsNullOrEmpty(productId))
                {
                    _logger.Warning($"[DesperationManager] No purchase history for {customer.NPC.fullName}. " +
                                   "Using fallback contract generation.");
                    FallbackContractGeneration(customer);
                    return;
                }

                CreateDesperationContract(customer, productId);
            }
            catch (Exception ex)
            {
                _logger.Error($"[DesperationManager] ForceCustomerDealOffer failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Gets a random product ID from the player's listed products (ProductManager.ListedProducts).
        /// Ensures desperation events request something the player is actually selling.
        /// </summary>
        private string GetActivelyListedProduct()
        {
            try
            {
                var listedProducts = ScheduleOne.Product.ProductManager.ListedProducts;
                if (listedProducts == null || listedProducts.Count == 0)
                {
                    _logger.Warning("[DesperationManager] GetActivelyListedProduct: no listed products found");
                    return null;
                }

                // Pick a random listed product
                int idx = UnityEngine.Random.Range(0, listedProducts.Count);
                var product = listedProducts[idx];
                if (product == null) return null;

                string productId = product.ID;
                if (!string.IsNullOrEmpty(productId))
                {
                    _logger.Msg($"[DesperationManager] GetActivelyListedProduct: picked '{productId}' from {listedProducts.Count} listed product(s)");
                    return productId;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DesperationManager] GetActivelyListedProduct failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets the customer's most purchased product from their history.
        /// </summary>
        private string GetCustomerPreferredProduct(Customer customer)
        {
            try
            {
                // Call CalculateTopWeeklyPurchases via reflection
                var customerType = customer.GetType();

                // Find the method - it has out parameters
                var calcMethod = customerType.GetMethod("CalculateTopWeeklyPurchases",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                if (calcMethod == null)
                {
                    _logger.Warning("[DesperationManager] Could not find CalculateTopWeeklyPurchases method");
                    return null;
                }

                // Invoke with out parameters
                object[] args = new object[] { null, 0f };
                calcMethod.Invoke(customer, args);

                // args[0] should now contain List<StringIntPair> mostPurchasedProducts
                object mostPurchased = args[0];

                if (mostPurchased == null)
                {
                    return null;
                }

                int count = (int)mostPurchased.GetType().GetProperty("Count").GetValue(mostPurchased);
                if (count == 0)
                {
                    return null;
                }

                // Return the most purchased product ID
                // StringIntPair has .String and .Int properties
                var indexer = mostPurchased.GetType().GetProperty("Item");
                object topItem = indexer.GetValue(mostPurchased, new object[] { 0 });
                string topProduct = (string)topItem.GetType().GetProperty("String").GetValue(topItem);
                return topProduct;
            }
            catch (Exception ex)
            {
                _logger.Error($"[DesperationManager] GetCustomerPreferredProduct failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Creates a desperation contract directly using game classes.
        /// </summary>
        private void CreateDesperationContract(Customer customer, string productId)
        {
            try
            {
                // Calculate quantity based on addiction level (higher addiction = wants more)
                int quantity = Mathf.RoundToInt(Mathf.Lerp(1f, 5f, customer.CurrentAddiction));
                quantity = Mathf.Max(1, quantity);

                // Step 1: Get the product definition to calculate price
                var itemDef = ItemManager.GetItemDefinition(productId);
                var productDef = itemDef as S1API.Products.ProductDefinition;

                float price = 20f; // Default fallback price
                if (productDef != null)
                {
                    price = productDef.Price;
                }
                else
                {
                    _logger.Warning($"[DesperationManager] Could not get price for {productId}, using default");
                }

                float payment = price * quantity;

                // Step 2: Get a delivery location from the customer's region
                string deliveryLocationGuid = GetRandomDeliveryLocation(customer);
                if (string.IsNullOrEmpty(deliveryLocationGuid))
                {
                    _logger.Warning("[DesperationManager] Could not find delivery location");
                    FallbackContractGeneration(customer);
                    return;
                }

                // Step 3: Create ProductList with the entry
                var productList = new ProductList();
                var entry = new ProductList.Entry(productId, EQuality.Standard, quantity);
                productList.entries.Add(entry);

                // Step 4: Create delivery window config (2-hour window starting now)
                // Use ImmediateQuestWindowConfig to mark this as a desperation contract
                int currentTime = TimeManager.CurrentTime;
                int endTime = TimeManager.Get24HourTimeFromMinutes(
                    TimeManager.GetMinutesFrom24HourTime(currentTime) + Config.DeadlineMinutes.Value);

                var deliveryWindow = new ImmediateQuestWindowConfig
                {
                    IsEnabled = true,
                    WindowStartTime = 0,
                    WindowEndTime = endTime
                };

                // Step 5: Create ContractInfo
                // Constructor: ContractInfo(payment, productList, deliveryLocationGuid, deliveryWindow, expires, expiresAfter, pickupScheduleGroup, isCounterOffer)
                // ExpiresAfter = 0 means same day expiry, adjusted to window end
                var contractInfo = new ContractInfo(
                    payment,
                    productList,
                    deliveryLocationGuid,
                    deliveryWindow,
                    true,   // expires
                    0,      // expiresAfter (0 = same day, expiry adjusted to window end)
                    0,      // pickupScheduleGroup
                    false   // isCounterOffer
                );

                // Step 6: Offer the contract
                OfferContractToCustomer(customer, contractInfo);
            }
            catch (Exception ex)
            {
                _logger.Error($"[DesperationManager] CreateDesperationContract failed: {ex.Message}\n{ex.StackTrace}");
                FallbackContractGeneration(customer);
            }
        }

        /// <summary>
        /// Gets a random delivery location GUID from the customer's region.
        /// </summary>
        private string GetRandomDeliveryLocation(Customer customer)
        {
            try
            {
                // Get the Map singleton
#if IL2CPP
                var mapType = Type.GetType("Il2CppScheduleOne.Map.Map, Assembly-CSharp");
#else
                var mapType = Type.GetType("ScheduleOne.Map.Map, Assembly-CSharp");
#endif
                if (mapType == null)
                {
                    _logger.Warning("[DesperationManager] Could not find Map type");
                    return null;
                }

                // Get Singleton<Map>.Instance
                var singletonType = typeof(ScheduleOne.DevUtilities.Singleton<>).MakeGenericType(mapType);
                var instanceProp = singletonType.GetProperty("Instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                var mapInstance = instanceProp?.GetValue(null);

                if (mapInstance == null)
                {
                    _logger.Warning("[DesperationManager] Map instance is null");
                    return null;
                }

                // Get region data for customer's region
                var getRegionDataMethod = mapInstance.GetType().GetMethod("GetRegionData");
                if (getRegionDataMethod == null)
                {
                    _logger.Warning("[DesperationManager] Could not find GetRegionData method");
                    return null;
                }

                var regionData = getRegionDataMethod.Invoke(mapInstance, new object[] { customer.NPC.Region });
                if (regionData == null)
                {
                    _logger.Warning($"[DesperationManager] No region data for {customer.NPC.Region}");
                    return null;
                }

                // Get random unscheduled delivery location
                var getLocationMethod = regionData.GetType().GetMethod("GetRandomUnscheduledDeliveryLocation");
                if (getLocationMethod == null)
                {
                    _logger.Warning("[DesperationManager] Could not find GetRandomUnscheduledDeliveryLocation method");
                    return null;
                }

                object deliveryLocation = getLocationMethod.Invoke(regionData, null);
                if (deliveryLocation == null)
                {
                    _logger.Warning($"[DesperationManager] No delivery locations in {customer.NPC.Region}");
                    return null;
                }

                string guid = deliveryLocation.GetType().GetProperty("GUID").GetValue(deliveryLocation).ToString();
                return guid;
            }
            catch (Exception ex)
            {
                _logger.Error($"[DesperationManager] GetRandomDeliveryLocation failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Offers a contract to the customer, bypassing the deal window selection.
        /// </summary>
        private void OfferContractToCustomer(Customer customer, object contractInfo)
        {
            try
            {
                var customerType = customer.GetType();

                // Find OfferContract method
                foreach (var method in customerType.GetMethods(System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (method.Name == "OfferContract")
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 1)
                        {
                            method.Invoke(customer, new object[] { contractInfo });
                            _logger.Msg($"[DesperationManager] Contract offered to player from {customer.NPC.fullName}");
                            return;
                        }
                    }
                }

                _logger.Warning("[DesperationManager] Could not find OfferContract method");
            }
            catch (Exception ex)
            {
                _logger.Error($"[DesperationManager] OfferContractToCustomer failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback: Use the game's built-in contract generation.
        /// </summary>
        private void FallbackContractGeneration(Customer customer)
        {
            try
            {
                var customerType = customer.GetType();

                // Try TryGenerateContract
                foreach (var method in customerType.GetMethods(System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (method.Name == "TryGenerateContract")
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 1)
                        {
                            var contractInfo = method.Invoke(customer, new object[] { null });
                            if (contractInfo != null)
                            {
                                OfferContractToCustomer(customer, contractInfo);
                                return;
                            }
                            else
                            {
                                _logger.Warning($"[DesperationManager] Fallback TryGenerateContract returned null for {customer.NPC.fullName}");
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"[DesperationManager] FallbackContractGeneration failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends the "desperate fiend" text message to the player.
        /// </summary>
        private void SendDesperationMessage(Customer customer)
        {
            string message = GetRandomDesperationMessage();

            try
            {
                // Use the game's MSGConversation system to send messages
                SendNPCTextMessage(customer.NPC, message);
            }
            catch (Exception ex)
            {
                _logger.Error($"[DesperationManager] Failed to send message: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a text message from an NPC using the IL2CPP NPC directly.
        /// Game NPCs (customers) aren't in S1API.Entities.NPC.All, so we
        /// call the IL2CPP SendTextMessage method instead.
        /// </summary>
        private void SendNPCTextMessage(ScheduleOne.NPCs.NPC ilNpc, string message)
        {
            if (ilNpc == null) return;

            try
            {
                ilNpc.SendTextMessage(message);
            }
            catch (Exception ex)
            {
                _logger.Error($"[DesperationManager] SendNPCTextMessage failed for '{ilNpc.fullName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Returns a random desperate message for variety.
        /// </summary>
        private string GetRandomDesperationMessage()
        {
            string[] messages = new[]
            {
                "I need a fix NOW. I'll pay extra. Don't make me call someone else.",
                "Yo I'm DESPERATE here. Got cash ready. Need you ASAP or I'm going elsewhere.",
                "Can't wait any longer. Premium pay if you come through RIGHT NOW.",
                "I'm losing it here. Need product immediately. Name your price.",
                "This is urgent. I'll make it worth your while. Don't leave me hanging."
            };

            return messages[UnityEngine.Random.Range(0, messages.Length)];
        }

        /// <summary>
        /// Checks for expired deadlines and applies penalties.
        /// </summary>
        private void CheckExpiredDeadlines()
        {
            int currentMinutes = GetCurrentElapsedMinutes();
            var expiredIds = new List<string>();

            foreach (var kvp in _activeEvents)
            {
                if (currentMinutes >= kvp.Value.DeadlineMinutes)
                {
                    expiredIds.Add(kvp.Key);
                }
            }

            foreach (var customerId in expiredIds)
            {
                var evt = _activeEvents[customerId];
                FailEvent(evt, evt.IsAccepted ? "delivery" : "response");
                _activeEvents.Remove(customerId);
            }

            if (expiredIds.Count > 0)
                ConfigSyncData.Instance?.PublishGameState();
        }

        /// <summary>
        /// Handles a failed desperation event (deadline expired).
        /// </summary>
        private void FailEvent(DesperationEvent evt, string failureType)
        {
            if (!NetworkHelper.IsHost) return;

            var customer = evt.Customer;
            if (customer == null || customer.NPC == null) return;

            string customerId = customer.NPC.ID;

            // Clear the pending contract offer and response buttons so player can't accept after timeout
            try
            {
                customer.SetOfferedContractInfo(null);
                customer.NPC.GetMSGConversation()?.ClearResponses(true);
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DesperationManager] Failed to clear contract offer/responses: {ex.Message}");
            }

            try { customer.NPC.Movement?.SpeedController?.RemoveSpeedControl("desperation"); } catch { }

            // Apply relationship penalty
            try
            {
                customer.NPC.RelationData.ChangeRelationship(Config.RelationshipPenalty.Value, true);
            }
            catch (Exception ex)
            {
                _logger.Error($"[DesperationManager] Failed to apply relationship penalty: {ex.Message}");
            }

            // Send appropriate failure message
            SendFailureMessage(customer, failureType);

            // Put customer on cooldown (24 hours)
            int cooldownEnd = GetCurrentElapsedMinutes() + Config.CooldownMinutes.Value;
            _customerCooldowns[customerId] = cooldownEnd;

            _logger.Msg($"[DesperationManager] Desperation event FAILED ({failureType}) for {customer.NPC.fullName}. " +
                       $"Relationship {Config.RelationshipPenalty.Value}. Cooldown until minute {cooldownEnd}.");
        }

        /// <summary>
        /// Sends the failure message when the player misses the deadline.
        /// </summary>
        private void SendFailureMessage(Customer customer, string failureType)
        {
            string[] messages;

            if (failureType == "response")
            {
                // Player didn't respond in time
                messages = new[]
                {
                    "Too slow to respond. Found someone else.",
                    "You snooze, you lose. Already got my fix.",
                    "Couldn't wait forever. The Cartel was faster.",
                    "Forget it. Called someone who actually picks up."
                };
            }
            else
            {
                // Player accepted but didn't deliver in time
                messages = new[]
                {
                    "Too slow. The Cartel boys hooked me up. Don't bother coming.",
                    "You said you'd come through. Liar. Found someone else.",
                    "Waited at the spot for nothing. Never trusting you again.",
                    "Can't rely on you. Had to go to the competition."
                };
            }

            string message = messages[UnityEngine.Random.Range(0, messages.Length)];

            try
            {
                SendNPCTextMessage(customer.NPC, message);
            }
            catch (Exception ex)
            {
                _logger.Error($"[DesperationManager] Failed to send failure message: {ex.Message}");
            }
        }

        // Client-side set of desperate customer IDs, populated via StateVar sync.
        private static readonly HashSet<string> _clientDesperateIds = new();

        /// <summary>
        /// Checks if a customer is currently in a desperation state.
        /// Works on both host (checks _activeEvents) and client (checks synced IDs).
        /// </summary>
        public static bool IsDesperate(string customerId)
        {
            if (Instance?._activeEvents.ContainsKey(customerId) == true)
                return true;
            return _clientDesperateIds.Contains(customerId);
        }

        /// <summary>
        /// Called by ConfigSyncData on clients to update the set of desperate customer IDs.
        /// </summary>
        public static void UpdateClientDesperateIds(HashSet<string> ids)
        {
            _clientDesperateIds.Clear();
            if (ids != null)
            {
                foreach (var id in ids)
                    _clientDesperateIds.Add(id);
            }
        }

        /// <summary>
        /// Returns a comma-separated string of desperate customer IDs for state sync.
        /// Called by ConfigSyncData.SerializeGameState() on the host.
        /// </summary>
        public static string GetDesperateIdsForSync()
        {
            if (Instance == null || Instance._activeEvents.Count == 0)
                return "";
            return string.Join(",", Instance._activeEvents.Keys);
        }

        /// <summary>
        /// Gets the bonus multiplier for desperation events.
        /// </summary>
        public static float GetBonusMultiplier()
        {
            return Config.BonusMultiplier.Value;
        }

        /// <summary>
        /// Resolves a desperation event after successful delivery.
        /// Called by the ProcessHandover transpiler.
        /// </summary>
        public static void ResolveEvent(string customerId)
        {
            if (Instance == null) return;
            if (!NetworkHelper.IsHost) return;

            if (Instance._activeEvents.TryGetValue(customerId, out var evt))
            {
                try { evt.Customer?.NPC?.Movement?.SpeedController?.RemoveSpeedControl("desperation"); } catch { }

                Instance._activeEvents.Remove(customerId);

                // Put customer on cooldown so they don't immediately trigger again
                int cooldownEnd = Instance.GetCurrentElapsedMinutes() + Config.CooldownMinutes.Value;
                Instance._customerCooldowns[customerId] = cooldownEnd;

                ConfigSyncData.Instance?.PublishGameState();
                Instance._logger.Msg($"[DesperationManager] Desperation event RESOLVED for customer {customerId}. " +
                                    $"Bonus applied: {Config.BonusMultiplier.Value * 100}%. Cooldown until minute {cooldownEnd}.");
            }
        }

        /// <summary>
        /// Gets a list of all customers currently in desperation state.
        /// Used for UI indicators.
        /// </summary>
        public static List<string> GetDesperateCustomerIds()
        {
            return Instance?._activeEvents.Keys.ToList() ?? new List<string>();
        }

        /// <summary>
        /// DEBUG: Force triggers a desperation event for a specific customer.
        /// Returns true if successful, false if customer is not eligible.
        /// </summary>
        public static bool DebugForceTrigger(Customer customer)
        {
            if (Instance == null) return false;
            if (customer?.NPC == null) return false;
            if (Instance._activeEvents.ContainsKey(customer.NPC.ID)) return false;

            Instance.TriggerDesperationEvent(customer);
            return true;
        }

        /// <summary>
        /// DEBUG: Force triggers desperation on a random eligible customer.
        /// If DebugProductId is not set, auto-finds a meth product to use.
        /// </summary>
        public static bool DebugForceRandomTrigger()
        {
            if (Instance == null) return false;

            var eligible = Instance.GetEligibleFiends();
            if (eligible.Count == 0)
            {
                // If no eligible fiends, try any unlocked customer
                var unlocked = Customer.UnlockedCustomers;
                if (unlocked == null || unlocked.Count == 0)
                {
                    DebugProductId = null;
                    return false;
                }

                int index = UnityEngine.Random.Range(0, unlocked.Count);
                return DebugForceTrigger(unlocked[index]);
            }

            int idx = UnityEngine.Random.Range(0, eligible.Count);
            return DebugForceTrigger(eligible[idx]);
        }

        /// <summary>
        /// DEBUG: Gets current status info for logging.
        /// </summary>
        public static string DebugGetStatus()
        {
            if (Instance == null) return "DesperationManager not initialized";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Desperation Manager Status ===");
            sb.AppendLine($"Daily Events: {Instance._dailyEventsTriggered}/{Config.MaxEventsPerDay.Value}");
            sb.AppendLine($"Active Events: {Instance._activeEvents.Count}");

            foreach (var kvp in Instance._activeEvents)
            {
                var evt = kvp.Value;
                int remaining = evt.DeadlineMinutes - Instance.GetCurrentElapsedMinutes();
                sb.AppendLine($"  - {evt.Customer?.NPC?.fullName ?? "Unknown"}: {remaining} mins remaining");
            }

            sb.AppendLine($"Customers on Cooldown: {Instance._customerCooldowns.Count}");
            return sb.ToString();
        }

        /// <summary>
        /// Calculates elapsed minutes from game start for deadline tracking.
        /// </summary>
        private int GetCurrentElapsedMinutes()
        {
            int days = TimeManager.ElapsedDays;
            int time24h = TimeManager.CurrentTime;
            int hours = time24h / 100;
            int minutes = time24h % 100;

            return (days * 1440) + (hours * 60) + minutes;
        }

        /// <summary>
        /// Cleanup when mod unloads.
        /// </summary>
        public void Cleanup()
        {
            TimeManager.OnTick -= OnTimeTick;
            TimeManager.OnDayPass -= OnDayPass;
            _activeEvents.Clear();
            _customerCooldowns.Clear();
            Instance = null;
            _logger.Msg("[DesperationManager] Cleaned up and unsubscribed from events.");
        }

        /// <summary>
        /// Called when a desperation contract is accepted. Updates the deadline for delivery.
        /// </summary>
        public static void OnContractAccepted(string customerId)
        {
            if (Instance == null) return;
            if (!NetworkHelper.IsHost) return;

            if (Instance._activeEvents.TryGetValue(customerId, out var evt))
            {
                // Update deadline: 120 minutes from NOW (acceptance time)
                evt.DeadlineMinutes = Instance.GetCurrentElapsedMinutes() + Config.DeadlineMinutes.Value;
                evt.IsAccepted = true;

                // Make the NPC run to the deal location (matches RequestProductBehaviour speed)
                try
                {
                    evt.Customer.NPC.Movement.SpeedController.AddSpeedControl(
                        new ScheduleOne.NPCs.NPCSpeedController.SpeedControl("desperation", 10, 0.9f));
                }
                catch (Exception ex)
                {
                    Instance._logger.Warning($"[DesperationManager] Failed to set run speed: {ex.Message}");
                }

                Instance._logger.Msg($"[DesperationManager] Contract accepted for {evt.Customer?.NPC?.fullName}. New deadline: {Config.DeadlineMinutes.Value} minutes from now.");
            }
        }

        /// <summary>
        /// Called when a desperation contract is declined. Cancels the event cleanly with no penalty.
        /// </summary>
        public static void OnContractRejected(string customerId)
        {
            if (Instance == null) return;
            if (!NetworkHelper.IsHost) return;

            if (Instance._activeEvents.TryGetValue(customerId, out var evt))
            {
                Instance._activeEvents.Remove(customerId);

                // 12-hour cooldown so they don't immediately re-trigger
                int cooldownEnd = Instance.GetCurrentElapsedMinutes() + (12 * 60);
                Instance._customerCooldowns[customerId] = cooldownEnd;

                ConfigSyncData.Instance?.PublishGameState();
                Instance._logger.Msg($"[DesperationManager] Desperation event DECLINED (no penalty) for customer {customerId}. 12hr cooldown until minute {cooldownEnd}.");
            }
        }

        /// <summary>
        /// Internal class to track desperation event data.
        /// </summary>
        private class DesperationEvent
        {
            public Customer Customer { get; set; }
            public int DeadlineMinutes { get; set; }  // Response deadline initially, then delivery deadline
            public int TriggerTime { get; set; }
            public bool IsAccepted { get; set; } = false;
        }
    }
}
