using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1MAPI.Building;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

#if IL2CPP
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.VoiceOver;
#else
using ScheduleOne.NPCs;
using ScheduleOne.DevUtilities;
using ScheduleOne.Storage;
using ScheduleOne.ItemFramework;
using ScheduleOne.VoiceOver;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Budtender lifecycle states.
    /// </summary>
    public enum BudtenderState
    {
        /// <summary>Walking from exterior spawn point to the counter.</summary>
        WalkingToCounter,
        /// <summary>Standing at counter, waiting for a customer in the queue.</summary>
        Idle,
        /// <summary>Chit-chat with customer + recommending products.</summary>
        Consulting,
        /// <summary>Walking to a storage entity to pick up product.</summary>
        FetchingProduct,
        /// <summary>Walking back to the counter with product.</summary>
        ReturningToCounter,
        /// <summary>At counter, processing the transaction.</summary>
        CompletingSale,
        /// <summary>Walking out of the building before being despawned.</summary>
        LeavingBuilding,
        /// <summary>Not hired or outside operating hours.</summary>
        Off
    }

    /// <summary>
    /// Represents a single hired budtender NPC assigned to a checkout counter.
    /// Manages the NPC reference, state machine, and autonomous checkout behavior.
    /// Host-only AI execution — clients see NPC movement replicated via FishNet.
    /// </summary>
    public class BudtenderInstance
    {
        /// <summary>All active budtender instances keyed by ID.</summary>
        public static readonly Dictionary<string, BudtenderInstance> Active = new();

        // Identity
        public string Id { get; }
        public int Seed { get; }

        // References
        public NPC GameNpc { get; private set; }
        public CheckoutCounterInstance AssignedCounter { get; }
        private BuildingTarget _buildingTarget;

            // State
        public BudtenderState State { get; set; } = BudtenderState.Off;
        public bool PaidForToday { get; set; }
        public int PaidOnDay { get; set; } = -1; // game day number when last paid

        // Checkout state (Phase 2)
        internal CustomerInstance CurrentCustomer { get; set; }
        internal List<FetchTask> FetchQueue { get; set; }
        internal int FetchIndex { get; set; }
        internal float SaleTotal { get; set; }
        internal float StateTimer { get; set; }
        private int _animationsPlayed;
        private int _animationsTotal;
        private StorageEntity _lastVisitedStorage;
        private Action _pendingArrival;
        private Vector3 _lastFramePos;
        private float _stoppedTime;
        private float _leaveTimeout;

        /// <summary>
        /// Fired budtenders that are walking out but already removed from Active.
        /// Ticked separately until they exit the building and get despawned.
        /// </summary>
        private static readonly List<BudtenderInstance> _leaving = new();

        /// <summary>Product fetch task for the budtender's checkout loop.</summary>
        internal struct FetchTask
        {
            public StorageEntity Storage;
            public ItemSlot SourceSlot;
            public Vector3 WorldPosition;
            public string ProductId;
            public string PackagingId;
            public string ProductName;
            public float Price;      // per-package price
            public int QualityLevel;
            public int UnitCount;    // packaging multiplier
            public bool Consumed;    // set to true when product was actually taken from storage
        }

        private BudtenderInstance(string id, int seed, CheckoutCounterInstance counter)
        {
            Id = id;
            Seed = seed;
            AssignedCounter = counter;
        }

        /// <summary>
        /// Creates a new budtender instance, spawns the NPC, and registers in Active dictionary.
        /// Host-only — clients receive state via SyncVar and adopt.
        /// </summary>
        public static BudtenderInstance Create(string id, int seed, CheckoutCounterInstance counter)
        {
            if (counter == null)
            {
                OTCLog.Error(OTCLog.Systems.Customer, $"BudtenderInstance.Create: null counter for {id}");
                return null;
            }

            var instance = new BudtenderInstance(id, seed, counter);

            // Get building target for exterior spawn position
            var target = GetBuildingTarget(counter.BuildingId);
            Vector3 spawnPos;
            Quaternion spawnRot = Quaternion.identity;

            if (target != null)
            {
                // Spawn at exterior approach (outside the building, on ground NavMesh)
                spawnPos = target.ExteriorApproachPosition;
                // Face toward the building
                var toBuilding = target.BuildingPosition - spawnPos;
                toBuilding.y = 0;
                if (toBuilding.sqrMagnitude > 0.01f)
                    spawnRot = Quaternion.LookRotation(toBuilding);
            }
            else
            {
                // Fallback: spawn near counter (shouldn't happen, but safe)
                var counterPos = counter.CounterPosition;
                if (!counterPos.HasValue)
                {
                    OTCLog.Error(OTCLog.Systems.Customer, $"BudtenderInstance.Create: no spawn position for {id}");
                    return null;
                }
                spawnPos = counterPos.Value;
            }

            var npc = BudtenderSpawner.Spawn(id, seed, spawnPos, spawnRot);
            if (npc == null)
            {
                OTCLog.Error(OTCLog.Systems.Customer, $"BudtenderInstance.Create: spawn failed for {id}");
                return null;
            }

            instance.GameNpc = npc;
            instance._buildingTarget = target;

            // Mark counter as staffed
            counter.AssignedBudtenderId = id;

            Active[id] = instance;
            OTCLog.Msg(OTCLog.Systems.Customer,
                $"Budtender {id} ({npc.FirstName} {npc.LastName}) spawned outside, walking to counter {CheckoutCounter.GetCounterIndex(counter)}");

            // Start walking to counter position
            instance.State = BudtenderState.WalkingToCounter;
            instance.WalkToCounterPosition();

            return instance;
        }

        /// <summary>Returns the BuildingTarget for a given building ID.</summary>
        private static Placement.BuildingTarget GetBuildingTarget(string buildingId)
        {
            if (buildingId == SaveData.PropertySaveData.ShackId)
                return Placement.WestvilleShack.Target;
            if (buildingId == Placement.Dispensary.DispensaryId)
                return Placement.Dispensary.Target;
            return null;
        }

        /// <summary>
        /// Despawns the NPC and removes from the Active dictionary.
        /// </summary>
        public void Despawn()
        {
            OTCLog.Msg(OTCLog.Systems.Customer, $"Budtender {Id} despawning");

            State = BudtenderState.Off;

            // Unmark counter
            if (AssignedCounter != null)
                AssignedCounter.AssignedBudtenderId = null;

            // Despawn NPC
            if (GameNpc != null)
            {
                NpcSpawner.Despawn(GameNpc);
                GameNpc = null;
            }

            Active.Remove(Id);
        }

        /// <summary>
        /// Sends the budtender home (off-duty). The NPC walks out through the door
        /// before being despawned. Instance remains in Active for CallIn() later.
        /// </summary>
        public void SendHome()
        {
            if (State == BudtenderState.Off || State == BudtenderState.LeavingBuilding) return;

            OTCLog.Msg(OTCLog.Systems.Customer, $"Budtender {Id} going off-duty");

            // Reset checkout state
            CurrentCustomer = null;
            FetchQueue = null;
            FetchIndex = 0;
            SaleTotal = 0f;
            StateTimer = 0f;
            _pendingArrival = null;

            if (GameNpc == null)
            {
                State = BudtenderState.Off;
                return;
            }

            // Walk out through the door
            StartLeavingBuilding();
        }

        /// <summary>
        /// Fires the budtender — NPC walks out through the door, then is fully removed.
        /// The counter is unassigned immediately. Instance is removed from Active and
        /// tracked in a separate leaving list until the NPC exits the building.
        /// </summary>
        public void GracefulDespawn()
        {
            if (GameNpc == null || State == BudtenderState.Off)
            {
                Despawn();
                return;
            }

            OTCLog.Msg(OTCLog.Systems.Customer, $"Budtender {Id} fired, walking out");

            // Reset checkout state
            CurrentCustomer = null;
            FetchQueue = null;
            FetchIndex = 0;
            SaleTotal = 0f;
            StateTimer = 0f;
            _pendingArrival = null;

            // Unmark counter immediately (available for re-hire)
            if (AssignedCounter != null)
                AssignedCounter.AssignedBudtenderId = null;

            // Remove from Active — logically gone
            Active.Remove(Id);

            // Track in leaving list for continued ticking
            _leaving.Add(this);

            // Walk out through the door
            StartLeavingBuilding();
        }

        /// <summary>Starts the exit walk via S1MAPI RecallNPC.</summary>
        private void StartLeavingBuilding()
        {
            State = BudtenderState.LeavingBuilding;
            _leaveTimeout = UnityEngine.Time.time + 10f;
            _pendingArrival = null;

            var nav = _buildingTarget?.NavBuilder;
            if (nav == null || GameNpc?.Movement == null)
            {
                FinishLeaving();
                return;
            }

            // Re-enable NavMeshAgent rotation + off-mesh link traversal for exit
            var agent = GameNpc.gameObject.GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                agent.updateRotation = true;
                agent.autoTraverseOffMeshLink = true;
            }

            nav.RecallNPC(GameNpc.Movement);
        }

        private void TickLeavingBuilding()
        {
            if (GameNpc == null)
            {
                FinishLeaving();
                return;
            }

            var nav = _buildingTarget?.NavBuilder;
            bool outside = nav == null || !nav.IsNPCInside(GameNpc.Movement);

            if (outside || UnityEngine.Time.time > _leaveTimeout)
                FinishLeaving();
        }

        private void FinishLeaving()
        {
            if (GameNpc != null)
            {
                NpcSpawner.Despawn(GameNpc);
                GameNpc = null;
            }
            State = BudtenderState.Off;
            _leaving.Remove(this);
        }

        /// <summary>Ticks fired budtenders that are walking out. Called from BudtenderController.</summary>
        internal static void TickLeaving()
        {
            for (int i = _leaving.Count - 1; i >= 0; i--)
            {
                try { _leaving[i].TickLeavingBuilding(); }
                catch { _leaving.RemoveAt(i); }
            }
        }

        /// <summary>
        /// Re-spawns the budtender NPC after being off-duty. Returns true on success.
        /// </summary>
        public bool CallIn()
        {
            if (State != BudtenderState.Off || GameNpc != null) return false;

            var target = GetBuildingTarget(AssignedCounter?.BuildingId);
            Vector3 spawnPos;
            Quaternion spawnRot = Quaternion.identity;

            if (target != null)
            {
                spawnPos = target.ExteriorApproachPosition;
                var toBuilding = target.BuildingPosition - spawnPos;
                toBuilding.y = 0;
                if (toBuilding.sqrMagnitude > 0.01f)
                    spawnRot = Quaternion.LookRotation(toBuilding);
            }
            else
            {
                var counterPos = AssignedCounter?.CounterPosition;
                if (!counterPos.HasValue) return false;
                spawnPos = counterPos.Value;
            }

            var npc = BudtenderSpawner.Spawn(Id, Seed, spawnPos, spawnRot);
            if (npc == null) return false;

            GameNpc = npc;
            _buildingTarget = target;
            State = BudtenderState.WalkingToCounter;
            WalkToCounterPosition();

            OTCLog.Msg(OTCLog.Systems.Customer, $"Budtender {Id} called in for duty");
            return true;
        }

        /// <summary>Cleans up all active budtender instances and any that are leaving.</summary>
        public static void CleanupAll()
        {
            var ids = new List<string>(Active.Keys);
            foreach (var id in ids)
            {
                if (Active.TryGetValue(id, out var bt))
                    bt.Despawn();
            }
            Active.Clear();

            // Also clean up any fired budtenders still walking out
            foreach (var bt in _leaving)
            {
                if (bt.GameNpc != null)
                {
                    NpcSpawner.Despawn(bt.GameNpc);
                    bt.GameNpc = null;
                }
            }
            _leaving.Clear();
        }

        // =================================================================
        //  Tick — called from BudtenderController.Tick() (host-only)
        // =================================================================

        /// <summary>
        /// Main tick for the budtender AI. Called every frame from BudtenderController.
        /// </summary>
        public void Tick()
        {
            if (GameNpc == null || AssignedCounter == null)
            {
                State = BudtenderState.Off;
                return;
            }

            // S1MAPI sometimes drops OnArrival — detect when NPC stops moving
            CheckStoppedMovement();

            switch (State)
            {
                case BudtenderState.WalkingToCounter:
                    TickWalkingToCounter();
                    break;
                case BudtenderState.Idle:
                    TickIdle();
                    break;
                case BudtenderState.Consulting:
                    TickConsulting();
                    break;
                case BudtenderState.FetchingProduct:
                    TickFetchingProduct();
                    break;
                case BudtenderState.ReturningToCounter:
                    TickReturningToCounter();
                    break;
                case BudtenderState.CompletingSale:
                    TickCompletingSale();
                    break;
                case BudtenderState.LeavingBuilding:
                    TickLeavingBuilding();
                    break;
            }
        }

        // =================================================================
        //  State: WalkingToCounter — stuck recovery
        // =================================================================

        private void TickWalkingToCounter()
        {
            if (StateTimer > 0f && Time.time > StateTimer)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"Budtender {Id}: walk to counter stuck, forcing position");
                _pendingArrival = null;
                StateTimer = 0f;

                FaceCounter();
                State = BudtenderState.Idle;
            }
        }

        // =================================================================
        //  State: Idle — wait for a customer at the front of our queue
        // =================================================================

        private void TickIdle()
        {
            var queue = AssignedCounter.Queue;
            if (queue.Count == 0) return;

            string frontId = queue[0];
            if (!CustomerInstance.Active.TryGetValue(frontId, out var customer))
                return;

            if (customer.State != CustomerState.CheckingOut) return;
            if (!customer.ArrivedAtDestination) return;
            if (customer.CheckoutArrivalTime <= 0f) return;

            // Customer arrived without products — start consultation (recommendation flow)
            if (customer.SelectedProducts.Count == 0)
            {
                BeginConsulting(customer);
                return;
            }

            // Customer already has products (save/reload edge case) — skip to checkout
            BeginCheckout(customer);
        }

        // =================================================================
        //  State: Consulting — chit-chat + product recommendation
        // =================================================================

        // Voice line schedule (seconds into consultation)
        private const float ConsultDuration = 3.5f;
        private const float VoBudtenderGreeting = 1.0f;
        private const float VoCustomerQuestion = 2.0f;
        private const float VoBudtenderAcknowledge = 3.0f;

        private float _consultTimer;
        private int _consultVoiceStep;

        private void BeginConsulting(CustomerInstance customer)
        {
            CurrentCustomer = customer;
            State = BudtenderState.Consulting;
            _consultTimer = 0f;
            _consultVoiceStep = 0;

            OTCLog.Msg(OTCLog.Systems.Customer,
                $"Budtender {Id}: consulting with {customer.Id} (familiarity={customer.Familiarity:F2})");

            // First voice line: customer greeting
            PlayCustomerVO(customer, EVOLineType.Greeting);
            _consultVoiceStep = 1;
        }

        private void TickConsulting()
        {
            if (CurrentCustomer == null || !CurrentCustomer.IsValid)
            {
                // Customer disappeared — abort
                CurrentCustomer = null;
                State = BudtenderState.Idle;
                return;
            }

            _consultTimer += Time.deltaTime;

            // Play voice lines on schedule
            if (_consultVoiceStep == 1 && _consultTimer >= VoBudtenderGreeting)
            {
                PlayBudtenderVO(EVOLineType.Greeting);
                _consultVoiceStep = 2;
            }
            else if (_consultVoiceStep == 2 && _consultTimer >= VoCustomerQuestion)
            {
                PlayCustomerVO(CurrentCustomer, EVOLineType.Question);
                _consultVoiceStep = 3;
            }
            else if (_consultVoiceStep == 3 && _consultTimer >= VoBudtenderAcknowledge)
            {
                PlayBudtenderVO(EVOLineType.Acknowledge);
                _consultVoiceStep = 4;
            }

            if (_consultTimer < ConsultDuration) return;

            // Consultation complete — scan storage and recommend products
            ScanAndRecommend(CurrentCustomer);
            CurrentCustomer.DecidePurchases();

            if (CurrentCustomer.SelectedProducts.Count > 0)
            {
                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"Budtender {Id}: recommended {CurrentCustomer.SelectedProducts.Count} products to {CurrentCustomer.Id}");
                BeginCheckout(CurrentCustomer);
            }
            else
            {
                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"Budtender {Id}: {CurrentCustomer.Id} didn't like any recommendations");
                CurrentCustomer.ShowDisappointed();

                // Remove from queue and signal exit
                AssignedCounter.Queue.Remove(CurrentCustomer.Id);
                CurrentCustomer.State = CustomerState.ExitingStore;
                CurrentCustomer.RecallFromBuilding();
                CustomerManager.Instance?.AdvanceQueue(AssignedCounter);

                CurrentCustomer = null;
                State = BudtenderState.Idle;
            }
        }

        private void ScanAndRecommend(CustomerInstance customer)
        {
            var storages = BudtenderStorageSearch.GetAllAccessibleStorages(AssignedCounter);
            customer.ObserveFromStorageList(storages);
            customer.FilterByFamiliarity();
        }

        private void PlayCustomerVO(CustomerInstance customer, EVOLineType lineType)
        {
            try { customer.GameNpc?.VoiceOverEmitter?.Play(lineType); }
            catch { }
        }

        private void PlayBudtenderVO(EVOLineType lineType)
        {
            try { GameNpc?.VoiceOverEmitter?.Play(lineType); }
            catch { }
        }

        // =================================================================
        //  Checkout initiation
        // =================================================================

        private void BeginCheckout(CustomerInstance customer)
        {
            CurrentCustomer = customer;
            FetchQueue = BudtenderStorageSearch.FindProducts(AssignedCounter, customer.SelectedProducts);
            FetchIndex = 0;
            SaleTotal = 0f;

            _lastVisitedStorage = null;

            // Update POS screen to show what the budtender found
            UpdatePOSScreen(customer);

            if (FetchQueue.Count == 0)
            {
                // Nothing in stock — skip to sale completion (customer leaves unhappy)
                OTCLog.Msg(OTCLog.Systems.Customer, $"Budtender {Id}: no products found for {customer.Id}");
                EnterCompletingSale();
                return;
            }

            OTCLog.Msg(OTCLog.Systems.Customer,
                $"Budtender {Id}: starting checkout for {customer.Id} — {FetchQueue.Count} items to fetch");
            State = BudtenderState.FetchingProduct;
            StartNextFetch();
        }

        /// <summary>
        /// Updates the POS screen to reflect what the budtender found in storage,
        /// rather than what the player has in inventory.
        /// </summary>
        internal void UpdatePOSScreen(CustomerInstance customer)
        {
            if (AssignedCounter?.Screen == null || customer == null) return;

            var placedUnitCounts = new Dictionary<string, int>();
            float placedTotal = 0f;

            if (FetchQueue != null)
            {
                foreach (var task in FetchQueue)
                {
                    if (placedUnitCounts.ContainsKey(task.ProductId))
                        placedUnitCounts[task.ProductId] += task.UnitCount;
                    else
                        placedUnitCounts[task.ProductId] = task.UnitCount;
                    placedTotal += task.Price;
                }
            }

            var missingKeys = new HashSet<string>();
            foreach (var sel in customer.SelectedProducts)
            {
                int needed = sel.Quantity > 0 ? sel.Quantity : 1;
                if (!placedUnitCounts.TryGetValue(sel.ProductId, out int found) || found < needed)
                    missingKeys.Add(sel.ProductId);
            }

            AssignedCounter.Screen.ShowBudtendingStatus(
                customer.SelectedProducts, missingKeys, placedUnitCounts, placedTotal);
        }

        // =================================================================
        //  State: FetchingProduct — walk to storage, consume item
        // =================================================================

        private void StartNextFetch()
        {
            if (FetchIndex >= FetchQueue.Count)
            {
                // All items fetched — return to counter
                State = BudtenderState.ReturningToCounter;
    
                WalkToCounter();
                return;
            }

            var task = FetchQueue[FetchIndex];

            // If the storage is the counter's own storage, skip walking
            if (task.Storage == AssignedCounter.CounterStorageEntity)
            {
                ConsumeProduct(FetchIndex);
                FetchIndex++;
                StartNextFetch();
                return;
            }

            // If we're already at this storage (consecutive items from same shelf), skip walking
            if (task.Storage == _lastVisitedStorage)
            {
                ConsumeProduct(FetchIndex);
                FetchIndex++;
                StartNextFetch();
                return;
            }

            // Walk to the storage via interior navigation
            StateTimer = Time.time + 10f; // stuck timeout
            SendToInterior(task.WorldPosition, () =>
            {
                StateTimer = 0f;
                _lastVisitedStorage = FetchQueue[FetchIndex].Storage;
                // Arrived at storage — consume
                ConsumeProduct(FetchIndex);
                FetchIndex++;
                StartNextFetch();
            });
        }

        private void TickFetchingProduct()
        {
            // Stuck recovery — if S1MAPI dropped the OnArrival callback,
            // the NPC is physically at the shelf but the callback never fired.
            // Consume the item and advance.
            if (StateTimer > 0f && Time.time > StateTimer)
            {
                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"Budtender {Id}: arrival callback missed (item {FetchIndex}/{FetchQueue?.Count}), recovering");
                _pendingArrival = null;
                StateTimer = 0f;

                if (FetchQueue != null && FetchIndex < FetchQueue.Count)
                {
                    _lastVisitedStorage = FetchQueue[FetchIndex].Storage;
                    ConsumeProduct(FetchIndex);
                    FetchIndex++;
                }
                StartNextFetch();
            }
        }

        // =================================================================
        //  State: ReturningToCounter — walk back to counter
        // =================================================================

        private void WalkToCounter()
        {
            var counterPos = AssignedCounter.CounterPosition;
            if (!counterPos.HasValue)
            {
                State = BudtenderState.CompletingSale;
                StateTimer = Time.time + 0.5f;
                return;
            }

            Vector3 behindCounter = AssignedCounter.BudtenderStandPosition ?? counterPos.Value;

            StateTimer = Time.time + 10f; // stuck timeout
            SendToInterior(behindCounter, () =>
            {
                FaceCounter();
                EnterCompletingSale();
            });
        }

        private void TickReturningToCounter()
        {
            if (StateTimer > 0f && Time.time > StateTimer)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"Budtender {Id}: return to counter stuck, forcing completion");
                FaceCounter();
                EnterCompletingSale();
            }
        }

        // =================================================================
        //  State: CompletingSale — finalize transaction
        // =================================================================

        private void EnterCompletingSale()
        {
            State = BudtenderState.CompletingSale;
            _animationsPlayed = 0;
            int consumed = 0;
            if (FetchQueue != null)
                foreach (var t in FetchQueue) if (t.Consumed) consumed++;
            _animationsTotal = Math.Min(consumed, 5);
            StateTimer = Time.time + 1f;

        }

        private void TickCompletingSale()
        {
            if (Time.time < StateTimer) return;

            // Play GrabItem animations (one per consumed product, max 5)
            if (_animationsPlayed < _animationsTotal)
            {
                try { GameNpc?.SetAnimationTrigger_Networked(null, "GrabItem"); }
                catch { }
                _animationsPlayed++;
                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"Budtender {Id}: animation {_animationsPlayed}/{_animationsTotal}, customer={CurrentCustomer?.Id} state={CurrentCustomer?.State}");
                StateTimer = Time.time + 0.8f;
                return;
            }

            CompleteCheckout();
        }

        private void CompleteCheckout()
        {
            if (CurrentCustomer == null || !CurrentCustomer.IsValid)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"Budtender {Id}: customer invalid at completion");
                ResetToIdle();
                return;
            }

            // Record only consumed sales
            try
            {
                var saveData = SaveData.PropertySaveData.Instance;
                if (saveData != null && FetchQueue != null)
                {
                    int gameDay = S1API.GameTime.TimeManager.ElapsedDays;
                    int gameHour = S1API.GameTime.TimeManager.CurrentTime;
                    string custName = CurrentCustomer.GameNpc?.fullName ?? "Unknown";
                    string txId = saveData.NextTransactionId();
                    float tip = DispensaryDealManager.GetTipAmount(CurrentCustomer, SaleTotal);
                    string buildingId = AssignedCounter.BuildingId;
                    bool tipRecorded = false;

                    OTCLog.Msg(OTCLog.Systems.Customer,
                        $"Budtender {Id}: recording sale — day={gameDay} hour={gameHour} building={buildingId} txId={txId}");

                    foreach (var task in FetchQueue)
                    {
                        if (!task.Consumed) continue;
                        saveData.RecordSale(
                            task.ProductId,
                            task.ProductName,
                            1,
                            task.Price,
                            task.QualityLevel,
                            gameDay,
                            custName,
                            gameHour,
                            txId,
                            tipRecorded ? 0f : tip,
                            buildingId);
                        tipRecorded = true;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"Budtender {Id}: failed to record sale: {ex.Message}");
            }

            // Deposit to register
            if (SaleTotal > 0f)
                AssignedCounter.DepositToRegister(SaleTotal);

            // Apply deal rewards
            float budtenderTip = 0f;
            if (CurrentCustomer.IsDealCustomer)
            {
                budtenderTip = DispensaryDealManager.GetTipAmount(CurrentCustomer, SaleTotal);
                DispensaryDealManager.ApplyDealRewards(CurrentCustomer, SaleTotal);
            }

            // Floating notification above register
            UI.RegisterFloatingText.Show(AssignedCounter, SaleTotal, budtenderTip);

            // Signal customer exit
            CurrentCustomer.CheckoutArrivalTime = 0f;
            CurrentCustomer.ArrivedAtDestination = false;
            CurrentCustomer.State = CustomerState.ExitingStore;
            CurrentCustomer.SetAvoidancePriority(10);
            CurrentCustomer.RecallFromBuilding();

            CustomerManager.Instance?.OnCheckoutComplete(CurrentCustomer.Id);

            int consumed = 0;
            if (FetchQueue != null)
                foreach (var t in FetchQueue) if (t.Consumed) consumed++;
            OTCLog.Msg(OTCLog.Systems.Customer,
                $"Budtender {Id}: completed sale — {consumed} items, ${SaleTotal:F2}");

            ResetToIdle();
        }

        private void ResetToIdle()
        {
            CurrentCustomer = null;
            FetchQueue = null;
            _lastVisitedStorage = null;
            _pendingArrival = null;
            FetchIndex = 0;
            SaleTotal = 0f;

            State = BudtenderState.Idle;
        }

        // =================================================================
        //  Walk-in from exterior spawn
        // =================================================================

        private void WalkToCounterPosition()
        {
            var counterPos = AssignedCounter.CounterPosition;
            if (!counterPos.HasValue)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"Budtender {Id}: no counter position, going idle");
                State = BudtenderState.Idle;
                return;
            }

            // Target: behind the counter (budtender side)
            Vector3 behindCounter = AssignedCounter.BudtenderStandPosition ?? counterPos.Value;

            StateTimer = Time.time + 12f; // stuck timeout (walking from exterior)
            SendToInterior(behindCounter, () =>
            {
                StateTimer = 0f;
                // Face toward the counter (toward customers)
                FaceCounter();
                OTCLog.Msg(OTCLog.Systems.Customer, $"Budtender {Id}: arrived at counter, going idle");
                State = BudtenderState.Idle;
            });
        }

        /// <summary>Centers the NPC on the desk (XZ only) and faces toward the customer side.</summary>
        private void FaceCounter()
        {
            var counterTransform = AssignedCounter?.CounterTransform;
            if (counterTransform == null || GameNpc == null) return;

            // Nudge XZ to exact stand position for centering; keep current Y (navmesh floor)
            var exactPos = AssignedCounter.BudtenderStandPosition;
            var agent = GameNpc.gameObject.GetComponent<NavMeshAgent>();
            if (exactPos.HasValue)
            {
                var cur = GameNpc.transform.position;
                var centered = new Vector3(exactPos.Value.x, cur.y, exactPos.Value.z);
                GameNpc.transform.position = centered;
                // Sync agent so it knows the new position (no updatePosition toggling)
                if (agent != null)
                    agent.nextPosition = centered;
            }

            var faceDir = -counterTransform.forward;
            faceDir.y = 0;
            if (faceDir.sqrMagnitude > 0.01f)
                GameNpc.transform.rotation = Quaternion.LookRotation(faceDir);

            // Stop NavMeshAgent from overriding our rotation
            if (agent != null)
                agent.updateRotation = false;
        }

        // =================================================================
        //  Movement helpers — S1MAPI interior navigation
        // =================================================================

        /// <summary>
        /// Sends the budtender to a world position using S1MAPI's interior NavigationBuilder.
        /// Converts world coords to building-local coords automatically.
        /// If the NPC is already at the target (within threshold), invokes callback directly.
        /// </summary>
        private void SendToInterior(Vector3 worldPos, Action onArrival)
        {
            _pendingArrival = null;
            _stoppedTime = 0f;

            var nav = _buildingTarget?.NavBuilder;
            if (nav == null || GameNpc?.Movement == null)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"Budtender {Id}: no NavBuilder, warping to position");
                try { GameNpc?.Movement?.Warp(worldPos); } catch { }
                onArrival?.Invoke();
                return;
            }

            // If NPC is already at (or very near) the target, skip pathfinding
            // S1MAPI's SendNPCToPosition won't fire OnArrival for zero-distance moves
            float dist = Vector3.Distance(GameNpc.transform.position, worldPos);
            if (dist < 1.5f)
            {
                onArrival?.Invoke();
                return;
            }

            // Store callback — checked by stopped-movement detection each frame
            _pendingArrival = onArrival;
            _lastFramePos = GameNpc.transform.position;

            // Re-enable rotation for walking
            var agent = GameNpc.gameObject.GetComponent<NavMeshAgent>();
            if (agent != null)
                agent.updateRotation = true;

            var localTarget = nav.WorldToLocal(worldPos);
            nav.SendNPCToPosition(GameNpc.Movement, localTarget, () =>
            {
                if (_pendingArrival == null) return; // already fired by stopped detection
                var cb = _pendingArrival;
                _pendingArrival = null;
                cb.Invoke();
            });
        }

        /// <summary>
        /// Re-sends the budtender to a world position after a nav rebuild.
        /// Called by SafeRebuildNavigation to restore NPC position after S1MAPI's
        /// Rebuild() releases all tracked NPCs.
        /// </summary>
        internal void ResendToPosition(Vector3 worldPos)
        {
            var nav = _buildingTarget?.NavBuilder;
            if (nav == null || GameNpc?.Movement == null) return;

            var localTarget = nav.WorldToLocal(worldPos);
            nav.SendNPCToPosition(GameNpc.Movement, localTarget, null);
        }

        /// <summary>
        /// Detects when the NPC has stopped moving but S1MAPI didn't fire OnArrival.
        /// Tracks position delta between frames — if NPC hasn't moved for 0.5s and
        /// we have a pending callback, fire it. Only active during FetchingProduct.
        /// </summary>
        private void CheckStoppedMovement()
        {
            if (_pendingArrival == null || GameNpc == null) return;
            if (State != BudtenderState.FetchingProduct &&
                State != BudtenderState.WalkingToCounter &&
                State != BudtenderState.ReturningToCounter) return;

            Vector3 currentPos = GameNpc.transform.position;
            float delta = (currentPos - _lastFramePos).sqrMagnitude;
            _lastFramePos = currentPos;

            if (delta < 0.0001f) // essentially not moving
            {
                _stoppedTime += Time.deltaTime;
                // Shorter threshold for fetching (near shelf), longer for navigation-heavy states
                float threshold = State == BudtenderState.FetchingProduct ? 0.5f : 2f;
                if (_stoppedTime > threshold)
                {
                    var cb = _pendingArrival;
                    _pendingArrival = null;
                    StateTimer = 0f;
                    _stoppedTime = 0f;
                    cb.Invoke();
                }
            }
            else
            {
                _stoppedTime = 0f; // still moving, reset
            }
        }

        // =================================================================
        //  Product consumption
        // =================================================================

        private void ConsumeProduct(int taskIndex)
        {
            var task = FetchQueue[taskIndex];

            // Remove from storage (best-effort — slot may already be empty if player or another customer took it)
            try
            {
                if (task.SourceSlot != null)
                {
                    if (task.SourceSlot.Quantity <= 1)
                        task.SourceSlot.ClearStoredInstance(false);
                    else
                        task.SourceSlot.ChangeQuantity(-1);
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"Budtender {Id}: slot removal failed for {task.ProductName}: {ex.Message}");
            }

            // Always mark consumed and tally price — the product was claimed when the fetch queue was built
            task.Consumed = true;
            FetchQueue[taskIndex] = task;
            SaleTotal += task.Price;
        }
    }
}
