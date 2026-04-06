using MelonLoader;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using static OverTheCounter.Logic.CustomerInstance;
using S1API.Money;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using CSteamID = Il2CppSteamworks.CSteamID;
#else
using CSteamID = Steamworks.CSteamID;
#endif

#if IL2CPP
using Il2CppTMPro;
using GameCanvasScaler = Il2CppScheduleOne.UI.CanvasScaler;
using Il2CppScheduleOne;
using Il2CppScheduleOne.Audio;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.ObjectScripts.Cash;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.VoiceOver;
using DealCompletionPopup = Il2CppScheduleOne.UI.DealCompletionPopup;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using PackagingDefinition = Il2CppScheduleOne.Product.Packaging.PackagingDefinition;
using NativeMoneyManager = Il2CppScheduleOne.Money.MoneyManager;
using NativeStorableItemDef = Il2CppScheduleOne.ItemFramework.StorableItemDefinition;
using ItemSlot = Il2CppScheduleOne.ItemFramework.ItemSlot;
using Customer = Il2CppScheduleOne.Economy.Customer;
#else
using TMPro;
using GameCanvasScaler = ScheduleOne.UI.CanvasScaler;
using ScheduleOne;
using ScheduleOne.Audio;
using ScheduleOne.DevUtilities;
using ScheduleOne.Interaction;
using ScheduleOne.ObjectScripts.Cash;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Product;
using ScheduleOne.Storage;
using ScheduleOne.Quests;
using ScheduleOne.UI;
using ScheduleOne.VoiceOver;
using DealCompletionPopup = ScheduleOne.UI.DealCompletionPopup;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
using PackagingDefinition = ScheduleOne.Product.Packaging.PackagingDefinition;
using NativeMoneyManager = ScheduleOne.Money.MoneyManager;
using NativeStorableItemDef = ScheduleOne.ItemFramework.StorableItemDefinition;
using ItemSlot = ScheduleOne.ItemFramework.ItemSlot;
using Customer = ScheduleOne.Economy.Customer;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Manages the interactive budtending checkout flow:
    /// camera pan → sprite selection → product placement → customer pickup → cash to register.
    /// One checkout at a time — <see cref="Instance"/> is non-null while active or paused.
    /// </summary>
    public class CheckoutProcess
    {
        /// <summary>Active checkout instance, or null when idle.</summary>
        public static CheckoutProcess Instance { get; private set; }

        /// <summary>ID of the customer being checked out.</summary>
        public string CustomerId => _customer?.Id;

        /// <summary>Whether the checkout is in a paused (backed-out) state.</summary>
        public bool IsPaused => _state == State.Paused;

        private enum State
        {
            CameraPanning,
            SkillCheck,
            WaitingForPlacement,
            CustomerPickup,
            PaymentAppearing,
            WaitingForPayment,
            CashFlying,
            CameraReturning,
            Paused
        }

        /// <summary>A product that has been placed on the counter during budtending.</summary>
        private struct CounterProduct
        {
            public string ProductId;
            public string PackagingId;
            public string ProductName;
            public float Price;         // per-package price
            public int QualityLevel;
            public int UnitCount;       // product units in this package (baggie=1, jar=5, brick=20)
            public ItemSlot SourceSlot;     // storage slot to consume from (null if from player inventory)
            public int HotbarIndex;         // player hotbar index (-1 if from storage)
            public GameObject VisualPrefab;
            public ProductDefinition ProductDef;
        }

        /// <summary>A product found in storage/inventory, ready to be placed via sprite click.</summary>
        private struct AvailableProduct
        {
            public string ProductId;
            public string PackagingId;
            public string ProductName;
            public float Price;         // per-package price
            public int QualityLevel;
            public int UnitCount;       // product units in this package (baggie=1, jar=5, brick=20)
            public GameObject VisualPrefab;
            public ProductDefinition ProductDef;
            public ItemSlot SourceSlot;     // storage slot to consume from (null if from player inventory)
            public int HotbarIndex;         // player hotbar index (-1 if from storage)

            /// <summary>
            /// Multi-source refs for auto-packaged entries. When non-null, ConsumeFromSource
            /// iterates these instead of using SourceSlot/HotbarIndex.
            /// Each tuple: (slot, hotbarIdx, count) — decrement 'count' times from source.
            /// </summary>
            public List<(ItemSlot slot, int hotbarIdx, int count)> SourceRefs;
        }

        // Constants
        private static AudioClip _scanClip;
        private static GameObject _cashPrefab;
        private const float CameraLerpTime = 0.25f;
        private const float DefaultFOV = 40f;
        private const float CameraPanWait = 0.6f;
        private const float PostPickupDelay = 0.5f;
        private const float PostScanDelay = 0.3f;
        private const float CameraReturnWait = 0.6f;
        private const float CashFlyDuration = 0.8f;
        private const int MaxCounterVisuals = 6;

        // Skill check timing (player-as-budtender consultation)
        private const float SkillCheckDuration = 4.0f;
        private const float VO_Question = 1.5f;
        private const float VO_Acknowledge = 3.0f;

        // Skill check zones (fraction of bar width, near the right end)
        private const float GreenStart = 0.72f;
        private const float GreenEnd = 0.90f;
        private const float GoldStart = 0.79f;
        private const float GoldEnd = 0.85f;
        private const float GreenTipPercent = 0.15f;
        private const float GoldTipPercent = 0.30f;
        private const float SkillCheckFreezeDelay = 0.5f;

        // Overhead camera offsets (finalized via runtime editor)
        private const float CamRightOffset = -0.05f;
        private const float CamUpOffset = 0.6f;
        private const float CamHeight = 1.1f;
        private const float CamForwardOffset = 1.15f;
        private const float CamFOV = DefaultFOV;

        // Product spawn offsets (finalized via runtime editor)
        private const float ProductRightOffset = -0.20f;
        private const float ProductUpOffset = -0.14f;
        private const float ProductForwardOffset = 0.0f;
        private const float ProductRotX = 0f;

        // State
        private readonly CustomerInstance _customer;
        private readonly CheckoutCounterInstance _counter;
        private State _state;
        private float _stateTimer;
        private float _totalPlacedPrice;
        private GameObject _paymentObject;

        // Products on the counter (placed by player)
        private readonly List<CounterProduct> _counterProducts = new();

        // 3D visuals on the counter grid (rebuilt proportionally after each placement)
        private readonly List<GameObject> _counterVisuals = new();

        // Products available for placement (from storage/inventory search)
        private readonly List<AvailableProduct> _availableProducts = new();

        // Tracking which requested products are missing (not found anywhere), keyed by ProductId
        private readonly HashSet<string> _missingProductKeys = new();

        // Tracking placed product units on counter, keyed by ProductId → units placed
        private readonly Dictionary<string, int> _placedUnitCounts = new();

        // Whether the "Complete Sale" button has been shown for a partial order
        private bool _completeBtnShown;

        // Skill check UI
        private GameObject _skillCheckRoot;
        private RectTransform _skillCheckCursor;
        private Image _greenZoneImg;
        private Image _goldZoneImg;
        private TextMeshProUGUI _skillCheckLabel;
        private bool _skillCheckLocked;
        private float _skillCheckLockTime;
        private float _skillCheckTipBonus;
        private bool _skillCheckPlayedQuestion;
        private bool _skillCheckPlayedAcknowledge;

        // Animation coroutines
        private object _cashFlyCoroutine;
        private object _pickupAnimCoroutine;

        // Client lock request state
        private enum PendingLockType { None, Checkout }
        private static PendingLockType _pendingLockType;
        private static string _pendingCustomerId;
        private static CheckoutCounterInstance _pendingCounter;
        private static float _lockRequestTime;

        /// <summary>Current checkout lock holder Steam ID (updated from SyncVar on client).</summary>
        internal static string CurrentLockHolder { get; private set; } = "";

        // P2P channels — instant lock acquisition + register collection
        private const string P2P_LOCK_REQ = "lock_req";
        private const string P2P_LOCK_RES = "lock_res";
        private const string P2P_REG_REQ = "reg_req";
        private const string P2P_REG_RES = "reg_res";
        private const string P2P_SALES_REQ = "sales_req";
        private const string P2P_SALES_RES = "sales_res";
        private static bool _p2pSubscribed;

        /// <summary>The counter this checkout is operating on.</summary>
        public CheckoutCounterInstance Counter => _counter;

        private CheckoutProcess(CustomerInstance customer, CheckoutCounterInstance counter)
        {
            _customer = customer;
            _counter = counter;
        }

        // =================================================================
        //  P2P lock system — replaces SyncVar-based lock polling
        // =================================================================

        /// <summary>
        /// Subscribes P2P handlers for instant lock request/response.
        /// Called from Core.OnGameLoaded after network is ready.
        /// </summary>
        public static void InitP2P()
        {
            if (_p2pSubscribed) return;
            if (!SaveData.ConfigSyncData.IsNetworkLibAvailable) return;

            try
            {
                InitP2PImpl();
                _p2pSubscribed = true;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"CheckoutProcess P2P init failed: {ex.Message}");
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void InitP2PImpl()
        {
            // Host listens for lock requests, register collection, and sales log requests
            SaveData.NetworkP2PBridge.Subscribe(P2P_LOCK_REQ, OnP2PLockRequest);
            SaveData.NetworkP2PBridge.Subscribe(P2P_REG_REQ, OnP2PRegisterRequest);
            SaveData.NetworkP2PBridge.Subscribe(P2P_SALES_REQ, OnP2PSalesRequest);
            // Client listens for lock grant/deny, register grant, and sales log response
            SaveData.NetworkP2PBridge.Subscribe(P2P_LOCK_RES, OnP2PLockResponse);
            SaveData.NetworkP2PBridge.Subscribe(P2P_REG_RES, OnP2PRegisterResponse);
            SaveData.NetworkP2PBridge.Subscribe(P2P_SALES_RES, OnP2PSalesResponse);
            // Host pushes updated sales log to all clients on each new sale
            SaveData.PropertySaveData.OnSaleRecorded += OnSaleRecordedBroadcast;
        }

        private static void CleanupP2P()
        {
            if (!_p2pSubscribed) return;
            try { CleanupP2PImpl(); }
            catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.Network, $"P2P cleanup failed: {ex.Message}"); }
            _p2pSubscribed = false;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SendP2PLockRequest(string custId, string myId)
        {
            SaveData.NetworkP2PBridge.SendToHost(P2P_LOCK_REQ, $"CHECKOUT:{custId}:{myId}");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void CleanupP2PImpl()
        {
            SaveData.NetworkP2PBridge.Unsubscribe(P2P_LOCK_REQ);
            SaveData.NetworkP2PBridge.Unsubscribe(P2P_LOCK_RES);
            SaveData.NetworkP2PBridge.Unsubscribe(P2P_REG_REQ);
            SaveData.NetworkP2PBridge.Unsubscribe(P2P_REG_RES);
            SaveData.NetworkP2PBridge.Unsubscribe(P2P_SALES_REQ);
            SaveData.NetworkP2PBridge.Unsubscribe(P2P_SALES_RES);
            SaveData.PropertySaveData.OnSaleRecorded -= OnSaleRecordedBroadcast;
        }

        /// <summary>
        /// Host: receives lock request from client via P2P.
        /// Format: "CHECKOUT:custId:steamId"
        /// Responds immediately with GRANT or DENY.
        /// </summary>
        private static void OnP2PLockRequest(ulong senderSteamId, string value)
        {
            if (!NetworkHelper.IsHost) return;
            if (string.IsNullOrEmpty(value)) return;

            var senderId = new CSteamID(senderSteamId);
            string clientSteamStr = senderSteamId.ToString();

            if (value.StartsWith("CHECKOUT:"))
            {
                string rest = value.Substring("CHECKOUT:".Length);
                int sep = rest.IndexOf(':');
                string custId = sep > 0 ? rest.Substring(0, sep) : rest;

                if (Instance != null)
                {
                    SaveData.NetworkP2PBridge.SendTo(senderId, P2P_LOCK_RES, "DENY:IN_USE");
                    return;
                }

                if (!CustomerInstance.Active.TryGetValue(custId, out var customer) ||
                    customer.State != CustomerState.CheckingOut)
                {
                    SaveData.NetworkP2PBridge.SendTo(senderId, P2P_LOCK_RES, "DENY:INVALID_CUSTOMER");
                    return;
                }

                SaveData.ConfigSyncData.Instance?.PublishCheckoutState(clientSteamStr, custId);
                SaveData.NetworkP2PBridge.SendTo(senderId, P2P_LOCK_RES, $"GRANT:CHECKOUT:{custId}");

                if (Config.VerboseLogging.Value)
                    OTCLog.Msg(OTCLog.Systems.Customer, $"P2P lock GRANTED (checkout) to {clientSteamStr} for {custId}");
            }
        }

        /// <summary>
        /// Client: receives lock grant/deny from host via P2P.
        /// Format: "GRANT:CHECKOUT:custId" or "DENY:reason"
        /// </summary>
        private static void OnP2PLockResponse(ulong senderSteamId, string value)
        {
            if (NetworkHelper.IsHost) return;
            if (_pendingLockType == PendingLockType.None) return;
            if (string.IsNullOrEmpty(value)) return;

            if (value.StartsWith("GRANT:"))
            {
                string rest = value.Substring("GRANT:".Length);
                string myId = SaveData.ConfigSyncData.LocalPlayerId;

                if (rest.StartsWith("CHECKOUT:") && _pendingLockType == PendingLockType.Checkout)
                {
                    string custId = rest.Substring("CHECKOUT:".Length);
                    _pendingLockType = PendingLockType.None;
                    CurrentLockHolder = myId;

                    if (!string.IsNullOrEmpty(custId) && _pendingCounter != null &&
                        CustomerInstance.Active.TryGetValue(custId, out var customer))
                    {
                        var counter = _pendingCounter;
                        _pendingCustomerId = null;
                        _pendingCounter = null;
                        StartCheckoutDirect(customer, counter);
                    }
                    else
                    {
                        OTCLog.Warning(OTCLog.Systems.Customer, $"P2P lock granted but customer {custId} not found");
                        _pendingCustomerId = null;
                        _pendingCounter = null;
                    }
                }
            }
            else if (value.StartsWith("DENY:"))
            {
                string reason = value.Substring("DENY:".Length);
                OTCLog.Msg(OTCLog.Systems.Customer, $"P2P lock denied: {reason}");
                _pendingLockType = PendingLockType.None;
                _pendingCustomerId = null;
                _pendingCounter = null;
            }
        }

        // =================================================================
        //  P2P register collection — host-authoritative cash withdrawal
        // =================================================================

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void SendP2PRegisterCollect(int counterIndex)
        {
            SaveData.NetworkP2PBridge.SendToHost(P2P_REG_REQ, counterIndex.ToString());
        }

        /// <summary>
        /// Host: receives register collection request from client.
        /// Validates balance, zeroes register, responds with granted amount.
        /// </summary>
        private static void OnP2PRegisterRequest(ulong senderSteamId, string value)
        {
            if (!NetworkHelper.IsHost) return;
            if (!int.TryParse(value, out int counterIdx)) return;

            var counter = Placement.CheckoutCounter.GetCounterByIndex(counterIdx);
            if (counter == null || counter.RegisterBalance <= 0f)
            {
                SaveData.NetworkP2PBridge.SendTo(new CSteamID(senderSteamId), P2P_REG_RES, "0");
                return;
            }

            float amount = counter.RegisterBalance;
            counter.CollectRegister();
            SaveData.ConfigSyncData.Instance?.PublishCheckoutClear();
            SaveData.NetworkP2PBridge.SendTo(new CSteamID(senderSteamId), P2P_REG_RES,
                amount.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Client: receives register collection grant from host.
        /// Awards cash locally only if host confirmed a non-zero amount.
        /// </summary>
        private static void OnP2PRegisterResponse(ulong senderSteamId, string value)
        {
            if (NetworkHelper.IsHost) return;
            if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float amount) || amount <= 0f) return;
            S1API.Money.Money.ChangeCashBalance(amount, true, true);
        }

        /// <summary>Host: responds to a client's sales log request with the full log.</summary>
        private static void OnP2PSalesRequest(ulong senderSteamId, string value)
        {
            if (!NetworkHelper.IsHost) return;
            var psd = SaveData.PropertySaveData.Instance;
            if (psd == null) return;
            string payload = psd.SerializeSalesLog();
            SaveData.NetworkP2PBridge.SendTo(new CSteamID(senderSteamId), P2P_SALES_RES, payload);
            OTCLog.Msg(OTCLog.Systems.Network, $"Sent sales log to {senderSteamId} ({payload?.Length ?? 0} chars)");
        }

        /// <summary>Client: receives sales log response from host.</summary>
        private static void OnP2PSalesResponse(ulong senderSteamId, string value)
        {
            if (NetworkHelper.IsHost) return;
            var psd = SaveData.PropertySaveData.Instance;
            if (psd == null) return;
            psd.ApplySalesLog(value);
            OTCLog.Msg(OTCLog.Systems.Network, $"Applied sales log from host ({value?.Length ?? 0} chars)");
        }

        /// <summary>Host: broadcasts the full sales log to all clients when a new sale is recorded.</summary>
        private static void OnSaleRecordedBroadcast(string _)
        {
            if (!NetworkHelper.IsHost || !_p2pSubscribed) return;
            var psd = SaveData.PropertySaveData.Instance;
            if (psd == null) return;
            string payload = psd.SerializeSalesLog();
            SaveData.NetworkP2PBridge.Broadcast(P2P_SALES_RES, payload);
        }

        /// <summary>
        /// Client requests the sales log from host after P2P init.
        /// Host ignores this call (already has the data).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void RequestSalesLog()
        {
            if (NetworkHelper.IsHost || !_p2pSubscribed) return;
            SaveData.NetworkP2PBridge.SendToHost(P2P_SALES_REQ, "");
            OTCLog.Msg(OTCLog.Systems.Network, "Requested sales log from host");
        }

        // =================================================================
        //  SyncVar fallback handlers (Debug builds / P2P unavailable)
        // =================================================================

        /// <summary>
        /// Host: handles CHECKOUT_REQUEST quest action from client (SyncVar path).
        /// Same validation as <see cref="OnP2PLockRequest"/> but grants via SyncVar
        /// instead of P2P response — client polls <see cref="CurrentLockHolder"/>.
        /// Format: "custId:steamId"
        /// </summary>
        public static void HandleCheckoutRequest(string payload)
        {
            if (!NetworkHelper.IsHost) return;
            var parts = payload.Split(':');
            if (parts.Length < 2) return;

            string custId = parts[0];
            string clientSteamStr = parts[1];

            if (Instance != null) return;

            if (!CustomerInstance.Active.TryGetValue(custId, out var customer) ||
                customer.State != CustomerState.CheckingOut)
                return;

            SaveData.ConfigSyncData.Instance?.PublishCheckoutState(clientSteamStr, custId);
            OTCLog.Msg(OTCLog.Systems.Customer, $"SyncVar lock GRANTED (checkout) to {clientSteamStr} for {custId}");
        }

        // =================================================================
        //  Public API
        // =================================================================

        /// <summary>
        /// Checks if the player pressed R while looking at the checkout counter
        /// with a waiting customer. Handles both fresh start and resume from pause.
        /// Called from Core.OnLateUpdate (both host and client).
        /// </summary>
        public static void TryStartCheckout()
        {
            if (GameInput.IsTyping) return;
            if (!Input.GetKeyDown(KeyCode.R)) return;
            if (_pendingLockType != PendingLockType.None) return;

            // Resume from pause
            if (Instance != null && Instance._state == State.Paused)
            {
                Instance.ResumeFromPause();
                return;
            }

            if (Instance != null) return;

            // Player must be looking at a checkout counter
            var hovered = Singleton<InteractionManager>.Instance?.HoveredInteractableObject;
            if (hovered == null) return;
            var counter = CheckoutCounter.GetCounterByInteractable(hovered);
            if (counter == null) return;

            // Staffed counters are budtender-only — no player checkout
            if (counter.IsStaffed) return;

            // Check if another player is already using this counter
            if (!string.IsNullOrEmpty(counter.LockHolder)) return;

            // Find the front customer waiting at THIS counter
            CustomerInstance waitingCustomer = null;
            if (counter.Queue.Count > 0 &&
                CustomerInstance.Active.TryGetValue(counter.Queue[0], out var front) &&
                front.State == CustomerState.CheckingOut &&
                front.ArrivedAtDestination &&
                front.CheckoutArrivalTime > 0f)
            {
                waitingCustomer = front;
            }

            if (waitingCustomer == null) return;

            if (NetworkHelper.IsHost)
            {
                StartCheckoutDirect(waitingCustomer, counter);
            }
            else
            {
                if (!string.IsNullOrEmpty(CurrentLockHolder))
                    return;
                _pendingLockType = PendingLockType.Checkout;
                _pendingCustomerId = waitingCustomer.Id;
                _pendingCounter = counter;
                _lockRequestTime = Time.time;
                string myId = SaveData.ConfigSyncData.LocalPlayerId;

                if (_p2pSubscribed)
                {
                    SendP2PLockRequest(waitingCustomer.Id, myId);
                }
                else
                {
                    SaveData.ConfigSyncData.SendQuestAction(
                        $"CHECKOUT_REQUEST:{waitingCustomer.Id}:{myId}");
                }
            }
        }

        /// <summary>
        /// Starts checkout for a specific customer (E-key on NPC).
        /// Bypasses R-key and hover checks — the customer is passed directly.
        /// </summary>
        internal static void TryStartCheckoutForCustomer(CustomerInstance customer)
        {
            if (Instance != null) return;
            if (_pendingLockType != PendingLockType.None) return;

            var counter = customer.AssignedCounter;
            if (counter == null || counter.IsStaffed) return;
            if (!string.IsNullOrEmpty(counter.LockHolder)) return;
            if (NetworkHelper.IsHost)
            {
                StartCheckoutDirect(customer, counter);
            }
            else
            {
                if (!string.IsNullOrEmpty(CurrentLockHolder)) return;
                _pendingLockType = PendingLockType.Checkout;
                _pendingCustomerId = customer.Id;
                _pendingCounter = counter;
                _lockRequestTime = Time.time;
                string myId = SaveData.ConfigSyncData.LocalPlayerId;

                if (_p2pSubscribed)
                    SendP2PLockRequest(customer.Id, myId);
                else
                    SaveData.ConfigSyncData.SendQuestAction(
                        $"CHECKOUT_REQUEST:{customer.Id}:{myId}");
            }
        }

        /// <summary>
        /// Host: creates the checkout instance and publishes the lock.
        /// Also used when granting a client's lock request.
        /// </summary>
        private static void StartCheckoutDirect(CustomerInstance customer, CheckoutCounterInstance counter)
        {
            var process = new CheckoutProcess(customer, counter);
            Instance = process;

            process.SearchAndShowAvailable();

            process._state = State.CameraPanning;
            process._stateTimer = Time.time;
            counter.Screen?.HideCheckoutInfo();
            process.LockPlayerInput();

            // Publish lock with host's Steam ID (or "host" fallback if LocalPlayerId isn't ready).
            // The non-empty customerId is what signals "in use" to other clients.
            if (NetworkHelper.IsHost)
            {
                string hostId = SaveData.ConfigSyncData.LocalPlayerId;
                SaveData.ConfigSyncData.Instance?.PublishCheckoutState(
                    string.IsNullOrEmpty(hostId) ? "host" : hostId, customer.Id);
            }
        }

        /// <summary>
        /// Client: timeout fallback for P2P lock requests.
        /// The actual grant/deny comes via <see cref="OnP2PLockResponse"/>.
        /// This just catches the edge case where the P2P message is lost in transit.
        /// Called from Core.OnLateUpdate.
        /// </summary>
        public static void PollLockGrant()
        {
            if (_pendingLockType == PendingLockType.None) return;
            if (NetworkHelper.IsHost) { _pendingLockType = PendingLockType.None; return; }

            string myId = SaveData.ConfigSyncData.LocalPlayerId;

            // SyncVar fallback: host sets CurrentLockHolder via PublishCheckoutState.
            // Catches grants even when the P2P response is lost in transit.
            if (!string.IsNullOrEmpty(myId) && CurrentLockHolder == myId)
            {
                _pendingLockType = PendingLockType.None;

                if (_pendingCustomerId != null)
                {
                    if (CustomerInstance.Active.TryGetValue(_pendingCustomerId, out var customer))
                    {
                        StartCheckoutDirect(customer, _pendingCounter);
                    }
                    _pendingCustomerId = null;
                    _pendingCounter = null;
                }
                return;
            }

            // Timeout — 3s for P2P, 5s for SyncVar (lobby metadata is slower)
            float timeout = _p2pSubscribed ? 3f : 5f;
            if (Time.time - _lockRequestTime > timeout)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    _p2pSubscribed ? "P2P lock request timed out (no response from host)"
                                  : "SyncVar lock request timed out (no response from host)");
                _pendingLockType = PendingLockType.None;
                _pendingCustomerId = null;
                _pendingCounter = null;
            }
        }

        // =================================================================
        //  Multiplayer sync (lock state + host-side action handlers)
        // =================================================================

        /// <summary>
        /// Called on client when the checkout SyncVar changes.
        /// Updates local lock state and register balance.
        /// </summary>
        public static void OnLockStateChanged(string lockHolder, string customerId)
        {
            CurrentLockHolder = lockHolder ?? "";

            // If lock cleared and we had an active checkout that already completed locally, clean up
            if (string.IsNullOrEmpty(lockHolder) && Instance != null &&
                Instance._state == State.CameraReturning)
            {
                // Host confirmed completion — nothing extra needed, Cleanup already called locally
            }
        }

        /// <summary>
        /// Host: handles CHECKOUT_DONE action from client.
        /// Format: "customerId:totalPrice:prodId,name,price,quality~prod2,...:skillBonus"
        /// </summary>
        public static void HandleCheckoutDone(string payload)
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                var parts = payload.Split(new[] { ':' }, 6);
                if (parts.Length < 2) return;

                string custId = parts[0];
                if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float totalPrice))
                    return;

                // Parse skill check bonus (4th segment) and highest addictiveness (5th segment)
                float skillBonus = 0f;
                float highestAddiction = 0f;
                if (parts.Length > 3)
                    float.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out skillBonus);
                if (parts.Length > 4)
                    float.TryParse(parts[4], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out highestAddiction);

                // Resolve customer and compute tip once
                CustomerInstance.Active.TryGetValue(custId, out var customer);
                float totalTip = 0f;
                if (customer != null)
                {
                    float dealTip = DispensaryDealManager.GetTipAmount(customer, totalPrice);
                    totalTip = dealTip + totalPrice * skillBonus;
                }

                // Record sales if product data provided
                if (parts.Length > 2 && !string.IsNullOrEmpty(parts[2]))
                {
                    var saveData = SaveData.PropertySaveData.Instance;
                    if (saveData != null)
                    {
                        int gameDay = S1API.GameTime.TimeManager.ElapsedDays;
                        int gameHour = S1API.GameTime.TimeManager.CurrentTime;
                        string custName = customer?.GameNpc?.fullName ?? custId;
                        string txId = saveData.NextTransactionId();
                        string buildingId = customer?.AssignedCounter?.BuildingId;
                        var items = parts[2].Split('~');
                        bool tipRecorded = false;
                        foreach (var item in items)
                        {
                            var fields = item.Split(',');
                            if (fields.Length < 4) continue;
                            float.TryParse(fields[2], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float price);
                            int.TryParse(fields[3], out int quality);
                            saveData.RecordSale(fields[0], fields[1], 1, price, quality, gameDay,
                                custName, gameHour, txId, tipRecorded ? 0f : totalTip, buildingId);
                            tipRecorded = true;
                        }
                    }
                }

                // Signal customer to exit and deposit to their assigned counter's register
                if (customer != null)
                {
                    var counter = customer.AssignedCounter;
                    if (counter == null && CheckoutCounter.AllCounters.Count > 0)
                        counter = CheckoutCounter.AllCounters[0];

                    // Deposit sale total
                    counter?.DepositToRegister(totalPrice);

                    // Deposit tip (deal + skill bonus)
                    if (totalTip > 0f)
                        counter?.DepositToRegister(totalTip);

                    // Floating notification above register
                    UI.RegisterFloatingText.Show(counter, totalPrice, totalTip);

                    // Apply non-monetary deal rewards (XP, cooldown)
                    if (customer.IsDealCustomer)
                        DispensaryDealManager.ApplyDealRewardsNonMonetary(customer, totalPrice);

                    // Budtending relationship (host-authoritative for client requests).
                    // The client already applied locally in CompleteCheckout for popup accuracy;
                    // this ensures the host's save data reflects the change.
                    if (customer.VanillaCustomer?.NPC?.RelationData != null)
                    {
                        float sat = DispensaryDealManager.CalculateSatisfaction(customer);
                        float rel = sat * 0.25f;
                        customer.VanillaCustomer.NPC.RelationData.ChangeRelationship(rel);
                    }

                    customer.CheckoutArrivalTime = 0f;
                    customer.ArrivedAtDestination = false;
                    customer.State = CustomerState.ExitingStore;
                    customer.SetAvoidancePriority(10);
                    customer.RecallFromBuilding();

                    // Addiction applied after release — highest addictiveness / 5 (same as vanilla)
                    if (highestAddiction > 0f && customer.VanillaCustomer != null)
                        customer.VanillaCustomer.ChangeAddiction(highestAddiction / 5f);

                    CustomerManager.Instance?.OnCheckoutComplete(custId);
                }

                // Clear lock
                SaveData.ConfigSyncData.Instance?.PublishCheckoutClear();
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"HandleCheckoutDone failed: {ex.Message}");
                SaveData.ConfigSyncData.Instance?.PublishCheckoutClear();
            }
        }

        /// <summary>
        /// Host: handles CHECKOUT_ABORT action from client.
        /// </summary>
        public static void HandleCheckoutAbort()
        {
            if (!NetworkHelper.IsHost) return;

            // Clear the lock. The client that aborted handles its own customer state locally.
            SaveData.ConfigSyncData.Instance?.PublishCheckoutClear();
        }

        /// <summary>
        /// Client: requests register collection from host.
        /// Uses P2P when available (host-authoritative, cash on response),
        /// falls back to quest action + local cash in Debug builds.
        /// </summary>
        internal static void RequestRegisterCollect(int counterIndex)
        {
            if (_p2pSubscribed)
            {
                SendP2PRegisterCollect(counterIndex);
            }
            else
            {
                // SyncVar fallback (Debug/LocalLobby): award cash locally, host zeroes register
                var counter = CheckoutCounter.GetCounterByIndex(counterIndex);
                if (counter != null && counter.RegisterBalance > 0f)
                    S1API.Money.Money.ChangeCashBalance(counter.RegisterBalance, true, true);
                SaveData.ConfigSyncData.SendQuestAction($"REGISTER_COLLECT:{counterIndex}");
            }
        }

        /// <summary>
        /// Host: handles REGISTER_COLLECT quest action from client (SyncVar fallback).
        /// Client already awarded itself cash locally in this path.
        /// </summary>
        public static void HandleRegisterCollect(string payload)
        {
            if (!NetworkHelper.IsHost) return;

            if (!int.TryParse(payload, out int counterIdx)) return;
            var counter = CheckoutCounter.GetCounterByIndex(counterIdx);
            if (counter == null || counter.RegisterBalance <= 0f) return;

            counter.CollectRegister();
            SaveData.ConfigSyncData.Instance?.PublishCheckoutClear();
        }

        /// <summary>
        /// Checks for right-click on counter products while checkout is paused.
        /// Returns product to player inventory. Called from Core.OnLateUpdate.
        /// </summary>
        public static void TryPickupCounterProduct()
        {
            if (Instance == null || Instance._state != State.Paused) return;
            if (!Input.GetMouseButtonDown(1)) return;

            var cam = Camera.main;
            if (cam == null) return;
            var ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f));

            // RaycastAll so we can hit products sitting on top of the desk
            var hits = Physics.RaycastAll(ray, 5f);
            if (hits.Length == 0) return;

            if (Instance._counterProducts.Count == 0) return;

            // Check if any counter visual was hit
            bool hitCounterVisual = false;
            for (int h = 0; h < hits.Length && !hitCounterVisual; h++)
            {
                var hitGo = hits[h].collider.gameObject;
                for (int v = 0; v < Instance._counterVisuals.Count; v++)
                {
                    if (Instance._counterVisuals[v] == null) continue;
                    if (hitGo == Instance._counterVisuals[v] ||
                        hits[h].collider.transform.IsChildOf(Instance._counterVisuals[v].transform))
                    {
                        hitCounterVisual = true;
                        break;
                    }
                }
            }

            if (hitCounterVisual)
            {
                int lastIdx = Instance._counterProducts.Count - 1;
                var product = Instance._counterProducts[lastIdx];

                if (!ReturnProduct(product))
                    return;

                Instance._counterProducts.RemoveAt(lastIdx);
                Instance._totalPlacedPrice -= product.Price;
                if (Instance._placedUnitCounts.TryGetValue(product.ProductId, out int pu))
                    Instance._placedUnitCounts[product.ProductId] = Math.Max(0, pu - product.UnitCount);

                Instance.RebuildCounterVisuals();
                Instance._counter.Screen?.ShowBudtendingStatus(
                    Instance._customer.SelectedProducts,
                    Instance._missingProductKeys,
                    Instance._placedUnitCounts,
                    Instance._totalPlacedPrice);
            }
        }

        /// <summary>
        /// Ticks the checkout state machine. Called every frame from Core.OnLateUpdate.
        /// </summary>
        public void Tick()
        {
            if (_state == State.Paused) return;

            // Safety: customer or counter gone → abort
            if (!_customer.IsValid || _counter?.CounterTransform == null)
            {
                Abort();
                return;
            }

            float elapsed = Time.time - _stateTimer;

            switch (_state)
            {
                case State.CameraPanning:
                    if (elapsed >= CameraPanWait)
                    {
                        if (_customer.SelectedProducts.Count == 0)
                        {
                            // No products yet — player consults with customer (skill check)
                            _state = State.SkillCheck;
                            _stateTimer = Time.time;
                            _skillCheckPlayedQuestion = false;
                            _skillCheckPlayedAcknowledge = false;
                            _skillCheckLocked = false;
                            _skillCheckTipBonus = 0f;
                            CreateSkillCheckBar();
                            try { _customer.GameNpc?.SendAnimationTrigger("ThumbsUp"); } catch { }
                        }
                        else
                        {
                            _state = State.WaitingForPlacement;
                            ShowSpriteHUD();
                            UpdatePOSForBudtending();
                        }
                    }
                    break;

                case State.SkillCheck:
                    TickSkillCheck(elapsed);
                    break;

                case State.WaitingForPlacement:
                    TickPlacement();
                    break;

                case State.CustomerPickup:
                    // Coroutine handles shrink+fly animation → transitions to PaymentAppearing
                    break;

                case State.PaymentAppearing:
                    if (elapsed >= PostScanDelay)
                    {
                        SpawnPaymentObject();
                        _state = State.WaitingForPayment;
                    }
                    break;

                case State.WaitingForPayment:
                    TickPaymentClick();
                    break;

                case State.CashFlying:
                    // Coroutine handles transition to CameraReturning
                    break;

                case State.CameraReturning:
                    if (elapsed >= CameraReturnWait)
                    {
                        CompleteCheckout();
                    }
                    break;
            }
        }

        // =================================================================
        //  Skill check state (player-as-budtender consultation)
        // =================================================================

        private void TickSkillCheck(float elapsed)
        {
            // Voice lines + animations on schedule
            if (!_skillCheckPlayedQuestion && elapsed >= VO_Question)
            {
                _skillCheckPlayedQuestion = true;
                PlayCustomerVoice(EVOLineType.Question);
                try { _customer.GameNpc?.SendAnimationTrigger("ConversationGesture1"); } catch { }
            }
            if (!_skillCheckPlayedAcknowledge && elapsed >= VO_Acknowledge)
            {
                _skillCheckPlayedAcknowledge = true;
                PlayCustomerVoice(EVOLineType.Acknowledge);
                try { _customer.GameNpc?.SendAnimationTrigger("Nod"); } catch { }
            }

            if (_skillCheckLocked)
            {
                // Cursor already locked — wait for freeze delay then proceed
                if (Time.time - _skillCheckLockTime >= SkillCheckFreezeDelay)
                    FinishSkillCheck();
                return;
            }

            // Bounce-back cursor: 0→1 over first half, 1→0 over second half
            float halfDur = SkillCheckDuration * 0.5f;
            float t;
            if (elapsed <= halfDur)
                t = elapsed / halfDur;
            else
                t = 1f - (elapsed - halfDur) / halfDur;
            t = Mathf.Clamp01(t);

            // Update cursor position
            if (_skillCheckCursor != null)
            {
                _skillCheckCursor.anchorMin = new Vector2(t - 0.005f, 0f);
                _skillCheckCursor.anchorMax = new Vector2(t + 0.005f, 1f);
            }

            // Check for SPACE press
            if (Input.GetKeyDown(KeyCode.Space))
            {
                _skillCheckLocked = true;
                _skillCheckLockTime = Time.time;

                // Determine zone hit
                if (t >= GoldStart && t <= GoldEnd)
                {
                    _skillCheckTipBonus = GoldTipPercent;
                    if (_skillCheckLabel != null) _skillCheckLabel.text = "Perfect!";
                    if (_skillCheckCursor != null) _skillCheckCursor.GetComponent<Image>().color = new Color(1f, 0.84f, 0f, 1f);
                    if (_goldZoneImg != null) _goldZoneImg.color = new Color(1f, 0.84f, 0f, 0.8f);
                }
                else if (t >= GreenStart && t <= GreenEnd)
                {
                    _skillCheckTipBonus = GreenTipPercent;
                    if (_skillCheckLabel != null) _skillCheckLabel.text = "Nice!";
                    if (_skillCheckCursor != null) _skillCheckCursor.GetComponent<Image>().color = new Color(0.30f, 0.85f, 0.31f, 1f);
                    if (_greenZoneImg != null) _greenZoneImg.color = new Color(0.30f, 0.68f, 0.31f, 0.8f);
                }
                else
                {
                    _skillCheckTipBonus = 0f;
                    if (_skillCheckLabel != null) _skillCheckLabel.text = "";
                    if (_skillCheckCursor != null) _skillCheckCursor.GetComponent<Image>().color = new Color(0.6f, 0.2f, 0.2f, 1f);
                }
                return;
            }

            // Time expired without pressing — miss
            if (elapsed >= SkillCheckDuration)
            {
                _skillCheckTipBonus = 0f;
                FinishSkillCheck();
            }
        }

        private void FinishSkillCheck()
        {
            DestroySkillCheckBar();

            // Scan all accessible storage + player inventory, then recommend via familiarity filter
            var storages = BudtenderStorageSearch.GetAllAccessibleStorages(_counter);
            _customer.ObserveFromStorageList(storages);
            _customer.ObservePlayerInventory();
            _customer.FilterByFamiliarity();
            _customer.DecidePurchases();

            if (_customer.SelectedProducts.Count > 0)
            {
                SearchAndShowAvailable();
                PanCameraToDesk();
                _state = State.CameraPanning;
                _stateTimer = Time.time;
            }
            else
            {
                _customer.ShowDisappointed();
                _state = State.CameraReturning;
                _stateTimer = Time.time;
                UnlockPlayerInput();
            }
        }

        private void CreateSkillCheckBar()
        {
            try
            {
                var rootGo = new GameObject("OTC_SkillCheckBar");
                var canvas = rootGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 100;
                var scaler = rootGo.AddComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
                rootGo.AddComponent<GameCanvasScaler>();
                rootGo.AddComponent<GraphicRaycaster>();

                // Bar background (bottom-center of screen)
                var bgGo = new GameObject("BarBg");
                bgGo.transform.SetParent(rootGo.transform, false);
                var bgImg = bgGo.AddComponent<Image>();
                bgImg.color = new Color(0.12f, 0.12f, 0.12f, 0.85f);
                var bgRect = bgGo.GetComponent<RectTransform>();
                bgRect.anchorMin = new Vector2(0.3f, 0.06f);
                bgRect.anchorMax = new Vector2(0.7f, 0.09f);
                bgRect.offsetMin = Vector2.zero;
                bgRect.offsetMax = Vector2.zero;

                // Green zone overlay
                var greenGo = new GameObject("GreenZone");
                greenGo.transform.SetParent(bgGo.transform, false);
                _greenZoneImg = greenGo.AddComponent<Image>();
                _greenZoneImg.color = new Color(0.30f, 0.68f, 0.31f, 0.35f);
                var greenRect = greenGo.GetComponent<RectTransform>();
                greenRect.anchorMin = new Vector2(GreenStart, 0f);
                greenRect.anchorMax = new Vector2(GreenEnd, 1f);
                greenRect.offsetMin = Vector2.zero;
                greenRect.offsetMax = Vector2.zero;

                // Gold zone overlay (inside green)
                var goldGo = new GameObject("GoldZone");
                goldGo.transform.SetParent(bgGo.transform, false);
                _goldZoneImg = goldGo.AddComponent<Image>();
                _goldZoneImg.color = new Color(1f, 0.84f, 0f, 0.45f);
                var goldRect = goldGo.GetComponent<RectTransform>();
                goldRect.anchorMin = new Vector2(GoldStart, 0f);
                goldRect.anchorMax = new Vector2(GoldEnd, 1f);
                goldRect.offsetMin = Vector2.zero;
                goldRect.offsetMax = Vector2.zero;

                // Cursor indicator (narrow white bar)
                var cursorGo = new GameObject("Cursor");
                cursorGo.transform.SetParent(bgGo.transform, false);
                var cursorImg = cursorGo.AddComponent<Image>();
                cursorImg.color = Color.white;
                _skillCheckCursor = cursorGo.GetComponent<RectTransform>();
                _skillCheckCursor.anchorMin = new Vector2(0f, 0f);
                _skillCheckCursor.anchorMax = new Vector2(0.01f, 1f);
                _skillCheckCursor.offsetMin = Vector2.zero;
                _skillCheckCursor.offsetMax = Vector2.zero;

                // "Press SPACE" label (above the bar)
                var labelGo = new GameObject("SkillCheckLabel");
                labelGo.transform.SetParent(rootGo.transform, false);
                var labelRect = labelGo.AddComponent<RectTransform>();
                labelRect.anchorMin = new Vector2(0.3f, 0.09f);
                labelRect.anchorMax = new Vector2(0.7f, 0.13f);
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
                _skillCheckLabel = UI.TMPFactory.Text("Label", "Press SPACE", labelGo.transform, 16,
                    TextAlignmentOptions.Center, FontStyles.Bold);
                _skillCheckLabel.color = Color.white;
                _skillCheckLabel.outlineWidth = 0.2f;
                _skillCheckLabel.outlineColor = new Color32(0, 0, 0, 180);

                _skillCheckRoot = rootGo;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"CreateSkillCheckBar failed: {ex.Message}");
            }
        }

        private void DestroySkillCheckBar()
        {
            if (_skillCheckRoot != null)
            {
                UnityEngine.Object.Destroy(_skillCheckRoot);
                _skillCheckRoot = null;
                _skillCheckCursor = null;
                _greenZoneImg = null;
                _goldZoneImg = null;
                _skillCheckLabel = null;
            }
        }

        // =================================================================
        //  Placement state
        // =================================================================

        private void TickPlacement()
        {
            // Check for back-out keys (R, Tab)
            if (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.Tab))
            {
                PauseCheckout();
                return;
            }

            // Right-click to remove a product already placed on the counter
            if (Input.GetMouseButtonDown(1))
            {
                TryRemovePlacedProduct();
                return;
            }

            // Delegate click detection to the HUD
            BudtenderHUD.TickClickDetection();

            // Check if all sprites have been placed
            bool allPlaced = BudtenderHUD.AllPlaced() || _availableProducts.Count == 0;

            if (allPlaced)
            {
                if (_missingProductKeys.Count == 0)
                {
                    // Full order, everything placed — auto-transition
                    BudtenderHUD.Hide();
                    TransitionToCustomerPickup();
                }
                else if (_completeBtnShown == false)
                {
                    // Partial order — show "Complete Sale Early"
                    BudtenderHUD.HideCards();
                    _completeBtnShown = true;
                    BudtenderHUD.ShowCompleteButton(OnCompleteSaleClicked);
                }
            }
        }

        /// <summary>Called when the player clicks "Complete Sale" for a partial or empty order.</summary>
        private void OnCompleteSaleClicked()
        {
            BudtenderHUD.Hide();

            // If nothing was placed at all, skip pickup/payment — just return camera and let customer leave
            if (_counterProducts.Count == 0)
            {
                PlayCustomerVoice(EVOLineType.Angry);
                _state = State.CameraReturning;
                _stateTimer = Time.time;
                UnlockPlayerInput();
                return;
            }

            TransitionToCustomerPickup();
        }

        /// <summary>
        /// Removes a placed product from the counter via right-click during WaitingForPlacement.
        /// Uses mouse position raycast (overhead camera).
        /// </summary>
        private void TryRemovePlacedProduct()
        {
            if (_counterProducts.Count == 0) return;
            var cam = Camera.main;
            if (cam == null) return;
            var ray = cam.ScreenPointToRay(Input.mousePosition);

            var hits = Physics.RaycastAll(ray, 20f);
            if (hits.Length == 0) return;

            // Check if any counter visual was hit
            bool hitCounterVisual = false;
            for (int h = 0; h < hits.Length && !hitCounterVisual; h++)
            {
                var hitGo = hits[h].collider.gameObject;
                for (int v = 0; v < _counterVisuals.Count; v++)
                {
                    if (_counterVisuals[v] == null) continue;
                    if (hitGo == _counterVisuals[v] ||
                        hits[h].collider.transform.IsChildOf(_counterVisuals[v].transform))
                    {
                        hitCounterVisual = true;
                        break;
                    }
                }
            }

            if (hitCounterVisual)
            {
                // Remove most recently placed product
                int lastIdx = _counterProducts.Count - 1;
                var product = _counterProducts[lastIdx];

                if (!ReturnProduct(product))
                    return;

                _counterProducts.RemoveAt(lastIdx);
                _totalPlacedPrice -= product.Price;
                if (_placedUnitCounts.TryGetValue(product.ProductId, out int pu2))
                    _placedUnitCounts[product.ProductId] = Math.Max(0, pu2 - product.UnitCount);

                // Rebuild counter display and refresh HUD
                RebuildCounterVisuals();
                SearchAndShowAvailable();
                ShowSpriteHUD();
                UpdatePOSForBudtending();
            }
        }

        /// <summary>Called by BudtenderHUD when the player clicks a sprite slot.</summary>
        private void OnSpriteClicked(int index)
        {
            if (index < 0 || index >= _availableProducts.Count) return;
            var available = _availableProducts[index];

            // Track product and update counter display
            TrackPlacedProduct(available);
            ConsumeFromSource(available);

            // Track placement (unit-based)
            _totalPlacedPrice += available.Price;
            _placedUnitCounts.TryGetValue(available.ProductId, out int prev);
            _placedUnitCounts[available.ProductId] = prev + available.UnitCount;

            // Remove sprite from HUD
            BudtenderHUD.RemoveItem(index);
            PlayPopSound();

            // Update POS display
            UpdatePOSForBudtending();
        }

        private void TrackPlacedProduct(AvailableProduct product)
        {
            _counterProducts.Add(new CounterProduct
            {
                ProductId = product.ProductId,
                PackagingId = product.PackagingId,
                ProductName = product.ProductName,
                Price = product.Price,
                QualityLevel = product.QualityLevel,
                UnitCount = product.UnitCount,
                SourceSlot = product.SourceSlot,
                HotbarIndex = product.HotbarIndex,
                VisualPrefab = product.VisualPrefab,
                ProductDef = product.ProductDef
            });
            RebuildCounterVisuals();
        }

        /// <summary>
        /// Rebuilds the 3x2 counter grid to proportionally represent all placed products.
        /// Uses largest-remainder allocation to distribute MaxCounterVisuals slots.
        /// </summary>
        private void RebuildCounterVisuals()
        {
            // Destroy existing visuals
            foreach (var v in _counterVisuals)
            {
                if (v != null)
                {
                    DisableRenderers(v);
                    UnityEngine.Object.Destroy(v);
                }
            }
            _counterVisuals.Clear();

            if (_counterProducts.Count == 0) return;

            // Group by product+packaging key, pick a representative from each group
            var groups = new Dictionary<string, (int count, int representativeIdx)>();
            for (int i = 0; i < _counterProducts.Count; i++)
            {
                string key = _counterProducts[i].ProductId + "|" + _counterProducts[i].PackagingId;
                if (!groups.ContainsKey(key))
                    groups[key] = (1, i);
                else
                {
                    var g = groups[key];
                    groups[key] = (g.count + 1, g.representativeIdx);
                }
            }

            // Largest-remainder method to distribute slots proportionally
            int total = _counterProducts.Count;
            int slots = Math.Min(MaxCounterVisuals, total);
            var keys = new List<string>(groups.Keys);
            var allocated = new int[keys.Count];
            var remainders = new float[keys.Count];
            int assigned = 0;

            for (int i = 0; i < keys.Count; i++)
            {
                float exact = (float)groups[keys[i]].count / total * slots;
                allocated[i] = (int)exact; // floor
                remainders[i] = exact - allocated[i];
                assigned += allocated[i];
            }

            // Distribute remaining slots to groups with largest remainders
            while (assigned < slots)
            {
                int bestIdx = 0;
                float bestRem = -1f;
                for (int i = 0; i < remainders.Length; i++)
                {
                    if (remainders[i] > bestRem)
                    {
                        bestRem = remainders[i];
                        bestIdx = i;
                    }
                }
                allocated[bestIdx]++;
                remainders[bestIdx] = -1f; // used up
                assigned++;
            }

            // Spawn visuals in 3x2 grid
            var counterTransform = _counter.CounterTransform;
            var surfacePos = _counter.SurfacePosition.Value;
            int gridIdx = 0;

            for (int g = 0; g < keys.Count; g++)
            {
                var (_, repIdx) = groups[keys[g]];
                var rep = _counterProducts[repIdx];
                for (int v = 0; v < allocated[g] && gridIdx < MaxCounterVisuals; v++)
                {
                    int col = gridIdx % 3;
                    int row = gridIdx / 3;
                    float xOffset = (col - 1.0f) * 0.18f;
                    float zOffset = (row - 0.5f) * 0.18f;
                    var spawnPos = surfacePos
                        + counterTransform.right * ProductRightOffset
                        + counterTransform.right * xOffset
                        + counterTransform.forward * ProductForwardOffset
                        + counterTransform.forward * zOffset
                        + Vector3.up * ProductUpOffset;

                    var go = SpawnSingleVisual(rep, spawnPos, counterTransform, gridIdx);
                    _counterVisuals.Add(go);
                    gridIdx++;
                }
            }
        }

        private GameObject SpawnSingleVisual(CounterProduct product, Vector3 pos, Transform counterTransform, int idx)
        {
            GameObject go;
            if (product.VisualPrefab != null)
            {
                go = UnityEngine.Object.Instantiate(product.VisualPrefab);
                go.name = $"OTC_CounterProduct_{idx}";
                go.transform.position = pos;
                go.transform.rotation = counterTransform.rotation;
                go.transform.localScale = Vector3.one;

                try
                {
                    var multiVisuals = go.GetComponentInChildren<MultiTypeVisualsSetter>();
                    if (multiVisuals != null && product.ProductDef != null)
                        multiVisuals.ApplyVisuals(product.ProductDef);
                    else
                    {
                        var visualsSetter = go.GetComponentInChildren<ProductVisualsSetter>();
                        if (visualsSetter != null && product.ProductDef != null)
                            visualsSetter.ApplyVisuals(product.ProductDef);
                    }
                    StripVisualSetters(go);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer, $"ApplyVisuals failed for {product.ProductName}: {ex.Message}");
                }

                go.transform.rotation = counterTransform.rotation * Quaternion.Euler(ProductRotX, 0f, 0f);
                foreach (var c in go.GetComponentsInChildren<Collider>())
                    c.enabled = false;
                go.layer = 0;
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(0.3f, 0.3f, 0.3f);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"OTC_CounterProduct_{idx}";
                go.transform.position = pos;
                go.transform.localScale = GetProductScale(product.PackagingId);
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.material.color = GetProductColor(product.PackagingId);
            }
            return go;
        }

        // =================================================================
        //  Pause / Resume
        // =================================================================

        private void PauseCheckout()
        {
            _state = State.Paused;
            BudtenderHUD.Hide();
            UnlockPlayerInput();

            // Show POS with current status (resume prompt)
            _counter.Screen?.ShowBudtendingStatus(
                _customer.SelectedProducts,
                _missingProductKeys,
                _placedUnitCounts,
                _totalPlacedPrice);
        }

        private void ResumeFromPause()
        {
            // Player must be looking at this counter
            var hovered = Singleton<InteractionManager>.Instance?.HoveredInteractableObject;
            if (hovered != _counter?.CheckoutInteractable) return;

            // Re-scan sources for remaining unfulfilled items
            SearchAndShowAvailable();

            _state = State.CameraPanning;
            _stateTimer = Time.time;
            _counter.Screen?.HideCheckoutInfo();
            LockPlayerInput();
        }

        // =================================================================
        //  Customer pickup → payment → cash fly
        // =================================================================

        private void TransitionToCustomerPickup()
        {
            // Customer plays GrabItem animation
            try
            {
                _customer.GameNpc?.SendAnimationTrigger("GrabItem");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"GrabItem animation failed: {ex.Message}");
            }

            _state = State.CustomerPickup;
            _stateTimer = Time.time;

            // Start shrink+travel animation toward NPC
            _pickupAnimCoroutine = MelonCoroutines.Start(ProductPickupAnimation());
        }

        /// <summary>
        /// Animates all counter products shrinking and traveling toward the customer,
        /// then transitions to PaymentAppearing.
        /// </summary>
        private IEnumerator ProductPickupAnimation()
        {
            const float duration = 0.6f;

            // Target: customer chest area
            var customerTransform = _customer.GameNpc?.transform;
            var targetPos = customerTransform != null
                ? customerTransform.position + Vector3.up * 0.8f
                : _counter?.SurfacePosition ?? Vector3.zero;

            // Capture starting state of each counter visual
            var startPositions = new Vector3[_counterVisuals.Count];
            var startScales = new Vector3[_counterVisuals.Count];
            for (int i = 0; i < _counterVisuals.Count; i++)
            {
                var visual = _counterVisuals[i];
                if (visual != null)
                {
                    startPositions[i] = visual.transform.position;
                    startScales[i] = visual.transform.localScale;
                }
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float smoothT = t * t * (3f - 2f * t); // smoothstep

                // Update target each frame in case NPC moves
                if (customerTransform != null)
                    targetPos = customerTransform.position + Vector3.up * 0.8f;

                for (int i = 0; i < _counterVisuals.Count; i++)
                {
                    var visual = _counterVisuals[i];
                    if (visual == null) continue;
                    visual.transform.position = Vector3.Lerp(startPositions[i], targetPos, smoothT);
                    visual.transform.localScale = Vector3.Lerp(startScales[i], Vector3.zero, smoothT);
                }

                yield return null;
            }

            // Disable renderers before destroying
            foreach (var v in _counterVisuals)
            {
                if (v != null)
                {
                    DisableRenderers(v);
                    UnityEngine.Object.Destroy(v);
                }
            }
            _counterVisuals.Clear();

            _pickupAnimCoroutine = null;
            _state = State.PaymentAppearing;
            _stateTimer = Time.time;
        }

        private void TickPaymentClick()
        {
            if (!Input.GetMouseButtonDown(0)) return;
            if (_paymentObject == null) return;

            // Any click collects the cash — there's nothing else to interact with
            _state = State.CashFlying;
            _cashFlyCoroutine = MelonCoroutines.Start(CashFlyCoroutine());
        }

        private IEnumerator CashFlyCoroutine()
        {
            var registerPos = _counter?.RegisterPosition;
            if (_paymentObject == null || !registerPos.HasValue)
            {
                // No register — fall back to direct deposit
                _counter?.DepositToRegister(_totalPlacedPrice);
                if (_paymentObject != null)
                    UnityEngine.Object.Destroy(_paymentObject);
                _paymentObject = null;
                _state = State.CameraReturning;
                _stateTimer = Time.time;
                UnlockPlayerInput();
                yield break;
            }

            var startPos = _paymentObject.transform.position;
            var endPos = registerPos.Value + Vector3.up * 0.15f;
            float elapsed = 0f;

            while (elapsed < CashFlyDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / CashFlyDuration);
                float smoothT = t * t * (3f - 2f * t); // smoothstep

                // Arc upward
                float arc = Mathf.Sin(t * Mathf.PI) * 0.4f;
                var pos = Vector3.Lerp(startPos, endPos, smoothT) + Vector3.up * arc;

                // Spin
                float spin = t * 720f;

                if (_paymentObject != null)
                {
                    _paymentObject.transform.position = pos;
                    _paymentObject.transform.rotation = Quaternion.Euler(0f, spin, 0f);
                }
                yield return null;
            }

            // Deposit and cleanup
            _counter?.DepositToRegister(_totalPlacedPrice);
            PlayCashSound();

            if (_paymentObject != null)
                UnityEngine.Object.Destroy(_paymentObject);
            _paymentObject = null;

            _state = State.CameraReturning;
            _stateTimer = Time.time;
            UnlockPlayerInput();
        }

        // =================================================================
        //  Product source search
        // =================================================================

        /// <summary>
        /// Searches counter storage then player inventory for requested products.
        /// Populates _availableProducts and _missingProductKeys.
        /// Uses greedy packaging: prefers largest package that fits (jar before baggies).
        /// SelectedProduct.Quantity is in raw product UNITS, not packages.
        /// </summary>
        private void SearchAndShowAvailable()
        {
            _availableProducts.Clear();
            _missingProductKeys.Clear();

            var requested = _customer.SelectedProducts;
            if (requested == null || requested.Count == 0)
                return;

            try
            {
                StorageEntity counterStorage = _counter?.CounterStorageEntity;

                foreach (var selection in requested)
                {
                    int unitsNeeded = selection.Quantity > 0 ? selection.Quantity : 1;

                    // Subtract already-placed units
                    _placedUnitCounts.TryGetValue(selection.ProductId, out int alreadyPlaced);
                    int unitsRemaining = unitsNeeded - alreadyPlaced;
                    if (unitsRemaining <= 0) continue;

                    // Collect all candidate packages matching this ProductId (any packaging)
                    var candidates = new List<(ItemSlot slot, int hotbarIdx, string pkgId, int mult,
                        int available, ProductDefinition prodDef, GameObject visual)>();

                    if (counterStorage?.ItemSlots != null)
                        CollectCandidatesFromStorage(counterStorage, selection.ProductId, candidates);
                    CollectCandidatesFromInventory(selection.ProductId, candidates);

                    // Sort by packaging multiplier descending (brick=20 > jar=5 > baggie=1)
                    candidates.Sort((a, b) => b.mult.CompareTo(a.mult));

                    // Greedy fill: largest packaging first, floor division (never overshoot)
                    int startIdx = _availableProducts.Count;
                    int unitsFound = 0;
                    foreach (var c in candidates)
                    {
                        if (unitsFound >= unitsRemaining) break;

                        int pkgsNeeded = (unitsRemaining - unitsFound) / c.mult;
                        int pkgsToTake = Math.Min(pkgsNeeded, c.available);
                        if (pkgsToTake <= 0) continue;

                        for (int u = 0; u < pkgsToTake; u++)
                        {
                            _availableProducts.Add(new AvailableProduct
                            {
                                ProductId = selection.ProductId,
                                PackagingId = c.pkgId,
                                ProductName = selection.ProductName,
                                Price = selection.Price * c.mult, // per-package price
                                QualityLevel = selection.QualityLevel,
                                UnitCount = c.mult,
                                VisualPrefab = c.visual,
                                ProductDef = c.prodDef,
                                SourceSlot = c.hotbarIdx < 0 ? c.slot : null,
                                HotbarIndex = c.hotbarIdx
                            });
                            unitsFound += c.mult;
                        }
                    }

                    // Auto-package: consolidate small packages into largest valid packaging
                    if (_availableProducts.Count - startIdx > 1)
                        ConsolidateAvailableEntries(startIdx, selection.Price);

                    if (unitsFound < unitsRemaining)
                        _missingProductKeys.Add(selection.ProductId);
                }

            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"SearchAndShowAvailable failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Auto-packaging: consolidates entries [startIdx..end) into the largest valid packaging.
        /// E.g., 5 baggies (1 unit each) → 1 jar (5 units). The budtender repackages at the counter.
        /// pricePerUnit is the per-unit selling price from the customer selection.
        /// </summary>
        private void ConsolidateAvailableEntries(int startIdx, float pricePerUnit)
        {
            int count = _availableProducts.Count - startIdx;
            if (count <= 1) return;

            var prodDef = _availableProducts[startIdx].ProductDef;
            if (prodDef?.ValidPackaging == null || prodDef.ValidPackaging.Length <= 1)
                return; // no alternative packaging to consolidate into

            // Sum total units and find the largest packaging already present
            int totalUnits = 0;
            int largestCurrentMult = 0;
            for (int i = startIdx; i < _availableProducts.Count; i++)
            {
                totalUnits += _availableProducts[i].UnitCount;
                if (_availableProducts[i].UnitCount > largestCurrentMult)
                    largestCurrentMult = _availableProducts[i].UnitCount;
            }

            if (totalUnits <= 0) return;

            // Find the largest valid packaging we could use
            int largestValidMult = 0;
            for (int p = prodDef.ValidPackaging.Length - 1; p >= 0; p--)
            {
                var pkg = prodDef.ValidPackaging[p];
                if (pkg != null && pkg.Quantity > 0 && pkg.Quantity <= totalUnits)
                {
                    largestValidMult = pkg.Quantity;
                    break;
                }
            }

            // If already at optimal packaging, nothing to do
            if (largestCurrentMult >= largestValidMult) return;

            // Snapshot original entries for source mapping
            var originals = new List<AvailableProduct>();
            for (int i = startIdx; i < _availableProducts.Count; i++)
                originals.Add(_availableProducts[i]);

            // Remove old entries
            _availableProducts.RemoveRange(startIdx, count);

            // Redistribute from largest packaging to smallest
            int remaining = totalUnits;
            int origIdx = 0;      // cursor into originals for source assignment
            int origUnitDebt = 0; // units consumed from originals[origIdx] so far

            for (int p = prodDef.ValidPackaging.Length - 1; p >= 0 && remaining > 0; p--)
            {
                var pkg = prodDef.ValidPackaging[p];
                if (pkg == null || pkg.Quantity <= 0) continue;

                int pkgCount = remaining / pkg.Quantity;
                if (pkgCount <= 0) continue;
                remaining -= pkgCount * pkg.Quantity;

                // Get visual from this packaging's StoredItem_Filled
                GameObject visual = null;
                try
                {
                    var storedItem = pkg.StoredItem_Filled;
                    if (storedItem != null) visual = storedItem.gameObject;
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.General,
                        $"ConsolidateAvailableEntries: failed to get StoredItem_Filled visual: {ex.Message}");
                }

                // Fall back to any original's visual if packaging visual unavailable
                if (visual == null && originals.Count > 0)
                    visual = originals[0].VisualPrefab;

                for (int i = 0; i < pkgCount; i++)
                {
                    // Build SourceRefs by consuming original entries' units
                    var sourceRefs = new List<(ItemSlot slot, int hotbarIdx, int count)>();
                    int unitsNeeded = pkg.Quantity;

                    while (unitsNeeded > 0 && origIdx < originals.Count)
                    {
                        var orig = originals[origIdx];
                        int origUnitsLeft = orig.UnitCount - origUnitDebt;

                        if (origUnitsLeft <= 0)
                        {
                            origIdx++;
                            origUnitDebt = 0;
                            continue;
                        }

                        // How many source decrements does this original entry represent?
                        // Each original entry = 1 package in its source slot
                        // We consume whole original entries (each = 1 slot decrement)
                        // Track by units: take min(unitsNeeded, origUnitsLeft)
                        int unitsFromThis = Math.Min(unitsNeeded, origUnitsLeft);
                        origUnitDebt += unitsFromThis;
                        unitsNeeded -= unitsFromThis;

                        // If we fully consumed this original entry, it means 1 decrement from source
                        if (origUnitDebt >= orig.UnitCount)
                        {
                            // Find existing ref for this source or add new
                            AddSourceRef(sourceRefs, orig.SourceSlot, orig.HotbarIndex, 1);
                            origIdx++;
                            origUnitDebt = 0;
                        }
                        // If partially consumed (shouldn't happen for integer packaging),
                        // we'll consume the rest on next iteration
                    }

                    _availableProducts.Add(new AvailableProduct
                    {
                        ProductId = originals[0].ProductId,
                        PackagingId = pkg.ID,
                        ProductName = originals[0].ProductName,
                        Price = pricePerUnit * pkg.Quantity,
                        QualityLevel = originals[0].QualityLevel,
                        UnitCount = pkg.Quantity,
                        VisualPrefab = visual,
                        ProductDef = prodDef,
                        SourceSlot = null,
                        HotbarIndex = -1,
                        SourceRefs = sourceRefs
                    });
                }
            }

            // Any entries with packaging matching originals (mult == 1 leftover) that weren't
            // consolidated keep their original single-source tracking
        }

        private static void AddSourceRef(List<(ItemSlot slot, int hotbarIdx, int count)> refs,
            ItemSlot slot, int hotbarIdx, int addCount)
        {
            for (int i = 0; i < refs.Count; i++)
            {
                if (refs[i].slot == slot && refs[i].hotbarIdx == hotbarIdx)
                {
                    refs[i] = (slot, hotbarIdx, refs[i].count + addCount);
                    return;
                }
            }
            refs.Add((slot, hotbarIdx, addCount));
        }

        /// <summary>Collects candidate packages from a StorageEntity matching productId.</summary>
        private static void CollectCandidatesFromStorage(StorageEntity storage, string productId,
            List<(ItemSlot slot, int hotbarIdx, string pkgId, int mult, int available,
                ProductDefinition prodDef, GameObject visual)> candidates)
        {
            if (storage?.ItemSlots == null) return;

            for (int j = 0; j < storage.ItemSlots.Count; j++)
            {
                var slot = storage.ItemSlots[j];
                if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

#if IL2CPP
                var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                var productItem = slot.ItemInstance as ProductItemInstance;
#endif
                if (productItem?.AppliedPackaging == null) continue;

                ProductDefinition prodDef = null;
                try
                {
#if IL2CPP
                    prodDef = productItem.Definition?.TryCast<ProductDefinition>();
#else
                    prodDef = productItem.Definition as ProductDefinition;
#endif
                }
                catch { }

                if (prodDef?.ID != productId) continue;

                GameObject visualPrefab = null;
                try
                {
                    var stored = productItem.StoredItem;
                    if (stored != null) visualPrefab = stored.gameObject;
                }
                catch { }

                candidates.Add((slot, -1, productItem.AppliedPackaging.ID,
                    productItem.AppliedPackaging.Quantity, slot.Quantity, prodDef, visualPrefab));
            }
        }

        /// <summary>Collects candidate packages from player hotbar matching productId.</summary>
        private static void CollectCandidatesFromInventory(string productId,
            List<(ItemSlot slot, int hotbarIdx, string pkgId, int mult, int available,
                ProductDefinition prodDef, GameObject visual)> candidates)
        {
            try
            {
                var inventory = PlayerSingleton<PlayerInventory>.Instance;
                if (inventory?.hotbarSlots == null) return;

                for (int i = 0; i < inventory.hotbarSlots.Count; i++)
                {
                    var slot = inventory.hotbarSlots[i];
                    if (slot?.ItemInstance == null) continue;

#if IL2CPP
                    var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                    var productItem = slot.ItemInstance as ProductItemInstance;
#endif
                    if (productItem?.AppliedPackaging == null) continue;

                    ProductDefinition prodDef = null;
                    try
                    {
#if IL2CPP
                        prodDef = productItem.Definition?.TryCast<ProductDefinition>();
#else
                        prodDef = productItem.Definition as ProductDefinition;
#endif
                    }
                    catch { }

                    if (prodDef?.ID != productId) continue;

                    GameObject visualPrefab = null;
                    try
                    {
                        var stored = productItem.StoredItem;
                        if (stored != null) visualPrefab = stored.gameObject;
                    }
                    catch { }

                    candidates.Add((slot, i, productItem.AppliedPackaging.ID,
                        productItem.AppliedPackaging.Quantity, slot.Quantity, prodDef, visualPrefab));
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"SearchPlayerInventory failed: {ex.Message}");
            }
        }

        /// <summary>Consumes one unit from the product's source (storage slot or player hotbar).</summary>
        private static void ConsumeFromSource(ItemSlot sourceSlot, int hotbarIndex, string productName)
        {
            try
            {
                if (sourceSlot != null)
                {
                    if (sourceSlot.Quantity <= 1)
                        sourceSlot.ClearStoredInstance(false);
                    else
                        sourceSlot.ChangeQuantity(-1);
                }
                else if (hotbarIndex >= 0)
                {
                    // From player inventory
                    var inventory = PlayerSingleton<PlayerInventory>.Instance;
                    if (inventory?.hotbarSlots != null && hotbarIndex < inventory.hotbarSlots.Count)
                    {
                        var slot = inventory.hotbarSlots[hotbarIndex];
                        if (slot?.Quantity <= 1)
                            slot?.ClearStoredInstance(false);
                        else
                            slot?.ChangeQuantity(-1);
                    }
                    else
                    {
                        OTCLog.Warning(OTCLog.Systems.Customer,$"ConsumeFromSource '{productName}': hotbar[{hotbarIndex}] out of range");
                    }
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,$"ConsumeFromSource '{productName}': no source");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"ConsumeFromSource failed for {productName}: {ex.Message}");
            }
        }

        private static void ConsumeFromSource(CounterProduct product)
            => ConsumeFromSource(product.SourceSlot, product.HotbarIndex, product.ProductName);

        private static void ConsumeFromSource(AvailableProduct product)
        {
            if (product.SourceRefs != null && product.SourceRefs.Count > 0)
            {
                foreach (var (slot, hotbarIdx, count) in product.SourceRefs)
                    for (int i = 0; i < count; i++)
                        ConsumeFromSource(slot, hotbarIdx, product.ProductName);
            }
            else
            {
                ConsumeFromSource(product.SourceSlot, product.HotbarIndex, product.ProductName);
            }
        }

        /// <summary>
        /// Removes all visual setter components from a cloned product GameObject.
        /// After ApplyVisuals sets the correct materials, these components are no longer
        /// needed and keeping them alive risks native Renderer crashes if re-triggered.
        /// </summary>
        internal static void StripVisualSetters(GameObject go)
        {
            if (go == null) return;

            // MultiTypeVisualsSetter : MonoBehaviour (separate hierarchy from ProductVisualsSetter)
            foreach (var multi in go.GetComponentsInChildren<MultiTypeVisualsSetter>(true))
                UnityEngine.Object.Destroy(multi);

            // ProductVisualsSetter and all subclasses (WeedVisualsSetter, MethVisualsSetter, etc.)
            foreach (var setter in go.GetComponentsInChildren<ProductVisualsSetter>(true))
                UnityEngine.Object.Destroy(setter);
        }

        /// <summary>
        /// Disables all renderers on a GameObject hierarchy. Called before destroying
        /// product visuals to prevent GPU access to zero-scale or about-to-die geometry.
        /// </summary>
        private static void DisableRenderers(GameObject go)
        {
            if (go == null) return;

            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                r.enabled = false;
        }

        /// <summary>
        /// Returns a product from the counter back to storage.
        /// Priority: player inventory → checkout counter storage → any property storage.
        /// Returns false only if no storage accepts the item.
        /// </summary>
        private static bool ReturnProduct(CounterProduct product)
        {
            try
            {
                // Create item instance to return
#if IL2CPP
                var prodDef = Registry.GetItem(product.ProductId)?.TryCast<ProductDefinition>();
#else
                var prodDef = GetRegistryItem(product.ProductId) as ProductDefinition;
#endif
                if (prodDef == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,$"ReturnProduct: could not find ProductDefinition for '{product.ProductId}'");
                    return false;
                }

                var instance = prodDef.GetDefaultInstance(1);
#if IL2CPP
                var productInstance = instance?.TryCast<ProductItemInstance>();
#else
                var productInstance = instance as ProductItemInstance;
#endif
                if (productInstance == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,$"ReturnProduct: GetDefaultInstance returned null for '{product.ProductId}'");
                    return false;
                }

                // Apply packaging
#if IL2CPP
                var packDef = Registry.GetItem(product.PackagingId)?.TryCast<PackagingDefinition>();
#else
                var packDef = GetRegistryItem(product.PackagingId) as PackagingDefinition;
#endif
                if (packDef != null)
                    productInstance.SetPackaging(packDef);

                // 1. Try player inventory (any open slot)
                var inventory = PlayerSingleton<PlayerInventory>.Instance;
                if (inventory != null && inventory.CanItemFitInInventory(productInstance, 1))
                {
                    inventory.AddItemToInventory(productInstance);
                    return true;
                }

                // 2. Try checkout counter storage
                var counterStorage = Instance?._counter?.CounterStorageEntity;
                if (counterStorage != null && counterStorage.CanItemFit(productInstance, 1))
                {
                    counterStorage.InsertItem(productInstance, true);
                    return true;
                }

                // 3. Try any other storage entity on the property grid
                var shackGrid = WestvilleShack.ShackGrid;
                if (shackGrid != null && BuildingGridFactory.GridContainers.TryGetValue(shackGrid, out var buildingRoot))
                {
#if IL2CPP
                    var storages = buildingRoot.GetComponentsInChildren<StorageEntity>(true);
                    for (int i = 0; i < storages.Count; i++)
                    {
                        var s = storages[i];
#else
                    var storages = buildingRoot.GetComponentsInChildren<StorageEntity>(true);
                    foreach (var s in storages)
                    {
#endif
                        if (s == counterStorage) continue; // already tried
                        if (s.CanItemFit(productInstance, 1))
                        {
                            s.InsertItem(productInstance, true);
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"ReturnProduct failed for '{product.ProductName}': {ex.Message}");
                return false;
            }
        }

        // =================================================================
        //  HUD & POS helpers
        // =================================================================

        private void ShowSpriteHUD()
        {
            _completeBtnShown = false;
            var spriteItems = new List<BudtenderHUD.SpriteItem>();
            for (int i = 0; i < _availableProducts.Count; i++)
            {
                var ap = _availableProducts[i];
                spriteItems.Add(new BudtenderHUD.SpriteItem
                {
                    ProductId = ap.ProductId,
                    PackagingId = ap.PackagingId,
                    ProductName = ap.ProductName,
                    QualityLevel = ap.QualityLevel,
                    UnitCount = ap.UnitCount
                });
            }
            BudtenderHUD.Show(spriteItems, OnSpriteClicked);
        }

        private void UpdatePOSForBudtending()
        {
            _counter.Screen?.ShowBudtendingStatus(
                _customer.SelectedProducts,
                _missingProductKeys,
                _placedUnitCounts,
                _totalPlacedPrice);
        }

        // =================================================================
        //  Payment visual
        // =================================================================

        private void SpawnPaymentObject()
        {
            var surfacePos = _counter.SurfacePosition.Value;
            var counterTransform = _counter.CounterTransform;

            // Position on the left side of desk
            var payPos = surfacePos - counterTransform.right * 0.3f;

            CacheCashPrefab();

            GameObject go;
            if (_cashPrefab != null)
            {
                go = UnityEngine.Object.Instantiate(_cashPrefab);
                go.name = "OTC_CheckoutCash";
                go.transform.position = payPos;
                go.transform.localScale = Vector3.one;

                var cashVisuals = go.GetComponentInChildren<CashStackVisuals>();
                if (cashVisuals != null)
                    cashVisuals.ShowAmount(_totalPlacedPrice);

                foreach (var col in go.GetComponentsInChildren<Collider>())
                    col.enabled = false;
                go.layer = 0; // Default layer for raycast
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(1f, 1f, 1f);
                box.center = new Vector3(0f, 0.1f, 0f); // align with visual center
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "OTC_CheckoutCash";
                go.transform.position = payPos;
                go.transform.localScale = new Vector3(0.25f, 0.08f, 0.15f);
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    var unlitShader = Shader.Find("Unlit/Color");
                    if (unlitShader != null)
                        renderer.material = new Material(unlitShader);
                    renderer.material.color = new Color(0.1f, 0.9f, 0.1f);
                }
            }

            _paymentObject = go;
        }

        private static void CacheCashPrefab()
        {
            if (_cashPrefab != null) return;
            try
            {
#if IL2CPP
                var def = Registry.GetItem("cash")?.TryCast<NativeStorableItemDef>();
#else
                var def = GetRegistryItem("cash") as NativeStorableItemDef;
#endif
                if (def?.StoredItem != null)
                    _cashPrefab = def.StoredItem.gameObject;
                else
                    OTCLog.Warning(OTCLog.Systems.Customer,"Cash definition or StoredItem not found in Registry");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"CacheCashPrefab failed: {ex.Message}");
            }
        }

#if !IL2CPP
        private static object GetRegistryItem(string itemId)
        {
            var registryType = typeof(NativeStorableItemDef).Assembly.GetType("ScheduleOne.Registry");
            if (registryType == null) return null;
            foreach (var m in registryType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (m.Name != "GetItem" || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    return m.Invoke(null, new object[] { itemId });
            }
            return null;
        }
#endif

        private static Vector3 GetProductScale(string packagingId)
        {
            if (packagingId == null) return new Vector3(0.2f, 0.15f, 0.2f);
            return packagingId.ToLower() switch
            {
                "baggie" => new Vector3(0.25f, 0.06f, 0.18f),
                "jar" => new Vector3(0.15f, 0.25f, 0.15f),
                "brick" => new Vector3(0.35f, 0.15f, 0.25f),
                _ => new Vector3(0.2f, 0.15f, 0.2f)
            };
        }

        private static Color GetProductColor(string packagingId)
        {
            if (packagingId == null) return new Color(0.8f, 0.8f, 0.75f);
            return packagingId.ToLower() switch
            {
                "baggie" => new Color(0.95f, 0.95f, 0.9f),
                "jar" => new Color(0.6f, 0.85f, 0.6f),
                "brick" => new Color(0.7f, 0.55f, 0.35f),
                _ => new Color(0.8f, 0.8f, 0.75f)
            };
        }

        // =================================================================
        //  Camera & input control
        // =================================================================

        private void LockPlayerInput()
        {
            try
            {
                var counterTransform = _counter.CounterTransform;
                var counterPos = _counter.CounterPosition.Value;

                var cam = PlayerSingleton<PlayerCamera>.Instance;
                cam.AddActiveUIElement("OTC_Checkout");

                bool chitChat = _customer.SelectedProducts.Count == 0;
                if (chitChat && _customer.GameNpc != null)
                {
                    // Chit-chat: camera faces the customer across the counter
                    var custPos = _customer.GameNpc.transform.position;
                    var camPos = counterPos + counterTransform.forward * 0.4f + Vector3.up * 1.5f;
                    var lookTarget = custPos + Vector3.up * 1.2f;
                    var lookDir = (lookTarget - camPos).normalized;
                    cam.OverrideTransform(camPos, Quaternion.LookRotation(lookDir), CameraLerpTime, false);
                    cam.OverrideFOV(DefaultFOV, CameraLerpTime);
                }
                else
                {
                    // Normal checkout: overhead desk view
                    PanCameraToDesk();
                }

                cam.FreeMouse();
                PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
                Singleton<HUD>.Instance.canvas.enabled = false;
                UI.HUDOverlay.Suppressed = true;
                UI.StoreAlertOverlay.Suppressed = true;

                PlayCustomerVoice(EVOLineType.Greeting);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"LockPlayerInput failed: {ex.Message}");
            }
        }

        private void PanCameraToDesk()
        {
            var counterTransform = _counter.CounterTransform;
            var counterPos = _counter.CounterPosition.Value;

            var surfaceCenter = counterPos + Vector3.up * CamUpOffset + counterTransform.right * CamRightOffset;
            var overheadPos = surfaceCenter + Vector3.up * CamHeight + counterTransform.forward * CamForwardOffset;
            var lookDir = (surfaceCenter - overheadPos).normalized;
            var overheadRot = Quaternion.LookRotation(lookDir);

            var cam = PlayerSingleton<PlayerCamera>.Instance;
            cam.OverrideTransform(overheadPos, overheadRot, CameraLerpTime, false);
            cam.OverrideFOV(CamFOV, CameraLerpTime);
        }

        private static void UnlockPlayerInput()
        {
            try
            {
                var cam = PlayerSingleton<PlayerCamera>.Instance;
                cam.RemoveActiveUIElement("OTC_Checkout");
                cam.StopFOVOverride(CameraLerpTime);
                cam.StopTransformOverride(CameraLerpTime, true, true);
                cam.LockMouse();

                PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
                Singleton<HUD>.Instance.canvas.enabled = true;
                UI.HUDOverlay.Suppressed = false;
                UI.StoreAlertOverlay.Suppressed = false;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"UnlockPlayerInput failed: {ex.Message}");
            }
        }

        // =================================================================
        //  Audio
        // =================================================================

        private GameObject _popAudioGo;
        private AudioSource _popAudioSource;

        private void PlayPopSound()
        {
            try
            {
                if (_scanClip == null) CacheScanClip();
                if (_scanClip == null) return;
                var counterPos = _counter?.CounterPosition;
                if (!counterPos.HasValue) return;

                if (_popAudioSource == null)
                {
                    _popAudioGo = new GameObject("OTC_PopSound");
                    _popAudioSource = _popAudioGo.AddComponent<AudioSource>();
                    _popAudioSource.spatialBlend = 1f;
                }

                // Rising pitch per placement for a satisfying ascending pop
                _popAudioGo.transform.position = counterPos.Value;
                _popAudioSource.clip = _scanClip;
                _popAudioSource.volume = 0.6f;
                _popAudioSource.pitch = 0.9f + _counterProducts.Count * 0.08f;
                _popAudioSource.Play();
            }
            catch { }
        }

        private static void CacheScanClip()
        {
            try
            {
                foreach (var src in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                {
                    if (src?.clip != null && src.clip.name == "Very quick click")
                    {
                        _scanClip = src.clip;
                        return;
                    }
                }
            }
            catch { }
        }

        private static void PlayCashSound()
        {
            try
            {
#if IL2CPP
                Il2CppScheduleOne.DevUtilities.NetworkSingleton<NativeMoneyManager>.Instance?.PlayCashSound();
#else
                ScheduleOne.DevUtilities.NetworkSingleton<NativeMoneyManager>.Instance?.PlayCashSound();
#endif
            }
            catch { }
        }

        private void PlayCustomerVoice(EVOLineType lineType)
        {
            try
            {
                var emitter = _customer.GameNpc?.VoiceOverEmitter;
                if (emitter == null) return;
                emitter.Play(lineType);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,$"PlayCustomerVoice({lineType}) failed: {ex.Message}");
            }
        }

        // =================================================================
        //  Completion & cleanup
        // =================================================================

        private void CompleteCheckout()
        {
            // Voice line depends on fulfillment (local audio — plays on whoever did the checkout)
            if (_counterProducts.Count == 0)
                PlayCustomerVoice(EVOLineType.Angry);
            else if (_missingProductKeys.Count > 0)
                PlayCustomerVoice(EVOLineType.Annoyed);
            else
                PlayCustomerVoice(EVOLineType.Thanks);

            // Budtending rewards: relationship increase + popup (local player)
            ApplyBudtendingRewards();

            // Products already consumed at sprite-click time (OnSpriteClicked)

            if (NetworkHelper.IsHost)
            {
                // Host path: record sales, signal customer, clear lock directly
                try
                {
                    var saveData = SaveData.PropertySaveData.Instance;
                    if (saveData != null)
                    {
                        int gameDay = S1API.GameTime.TimeManager.ElapsedDays;
                        int gameHour = S1API.GameTime.TimeManager.CurrentTime;
                        string custName = _customer?.GameNpc?.fullName ?? "Unknown";
                        string txId = saveData.NextTransactionId();
                        float tip = DispensaryDealManager.GetTipAmount(_customer, _totalPlacedPrice);
                        tip += _totalPlacedPrice * _skillCheckTipBonus;
                        string buildingId = _counter?.BuildingId;
                        bool tipRecorded = false;
                        foreach (var product in _counterProducts)
                        {
                            saveData.RecordSale(
                                product.ProductId,
                                product.ProductName,
                                1,
                                product.Price,
                                product.QualityLevel,
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
                    OTCLog.Warning(OTCLog.Systems.Customer,$"Failed to record sale: {ex.Message}");
                }

                // Deposit tip to register (sale total was deposited during CashFly)
                float dealTip = DispensaryDealManager.GetTipAmount(_customer, _totalPlacedPrice);
                float skillTip = _totalPlacedPrice * _skillCheckTipBonus;
                float totalTip = dealTip + skillTip;
                if (totalTip > 0f)
                    _counter?.DepositToRegister(totalTip);

                // Floating notification above register
                UI.RegisterFloatingText.Show(_counter, _totalPlacedPrice, totalTip);

                // Apply non-monetary deal rewards (XP, relationship, cooldown)
                if (_customer.IsDealCustomer)
                    DispensaryDealManager.ApplyDealRewardsNonMonetary(_customer, _totalPlacedPrice);

                // Signal customer to exit
                _customer.CheckoutArrivalTime = 0f;
                _customer.ArrivedAtDestination = false;
                _customer.State = CustomerState.ExitingStore;
                _customer.SetAvoidancePriority(10);
                _customer.RecallFromBuilding();

                // Addiction applied after release — highest addictiveness / 5 (same as vanilla)
                if (_customer.VanillaCustomer != null)
                {
                    float highestAddiction = 0f;
                    foreach (var p in _counterProducts)
                    {
                        if (p.ProductDef == null) continue;
                        float a = p.ProductDef.GetAddictiveness();
                        if (a > highestAddiction) highestAddiction = a;
                    }
                    if (highestAddiction > 0f)
                        _customer.VanillaCustomer.ChangeAddiction(highestAddiction / 5f);
                }

                CustomerManager.Instance?.OnCheckoutComplete(_customer.Id);
                SaveData.ConfigSyncData.Instance?.PublishCheckoutClear();
            }
            else
            {
                // Client path: send completion to host with sale data
                var saleParts = new List<string>();
                float highestAddiction = 0f;
                foreach (var product in _counterProducts)
                {
                    saleParts.Add($"{product.ProductId},{product.ProductName}," +
                        $"{product.Price.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                        $"{product.QualityLevel}");
                    if (product.ProductDef != null)
                    {
                        float a = product.ProductDef.GetAddictiveness();
                        if (a > highestAddiction) highestAddiction = a;
                    }
                }
                string saleData = string.Join("~", saleParts);
                string totalStr = _totalPlacedPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string bonusStr = _skillCheckTipBonus.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string addictStr = highestAddiction.ToString(System.Globalization.CultureInfo.InvariantCulture);
                SaveData.ConfigSyncData.SendQuestAction($"CHECKOUT_DONE:{_customer.Id}:{totalStr}:{saleData}:{bonusStr}:{addictStr}");
            }

            Cleanup();
        }

        /// <summary>
        /// Applies budtending rewards for deal customers: relationship (50% of vanilla deal value)
        /// and the vanilla deal completion popup. Addiction is applied separately after customer
        /// release in the host-side exit path. Runs on the local player who performed the checkout.
        /// </summary>
        private void ApplyBudtendingRewards()
        {
            var vc = _customer?.VanillaCustomer;
            if (vc?.NPC?.RelationData == null) return;
            if (_counterProducts.Count == 0) return;

            try
            {
                float satisfaction = DispensaryDealManager.CalculateSatisfaction(_customer);

                // 50% of vanilla deal value (vanilla max = +0.5, budtending max = +0.25)
                // Always positive — budtending never penalises relationship
                float relChange = satisfaction * 0.25f;
                float originalDelta = vc.NPC.RelationData.RelationDelta;
                vc.NPC.RelationData.ChangeRelationship(relChange);

                // Show vanilla popup if relationship gained
                if (relChange > 0f)
                    ShowBudtendingPopup(vc, satisfaction, originalDelta);

                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"Budtending rewards for {vc.NPC.fullName}: rel={relChange:+0.000;-0.000} satisfaction={satisfaction:P0}");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"ApplyBudtendingRewards: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows the vanilla <see cref="DealCompletionPopup"/> with an empty bonus list
        /// and the current sale total as payment. Platform-specific generic list required.
        /// </summary>
        private void ShowBudtendingPopup(Customer vc, float satisfaction, float originalDelta)
        {
            try
            {
                var popup = Singleton<DealCompletionPopup>.Instance;
                if (popup == null) return;

#if IL2CPP
                var bonuses = new Il2CppSystem.Collections.Generic.List<Contract.BonusPayment>();
#else
                var bonuses = new System.Collections.Generic.List<Contract.BonusPayment>();
#endif
                popup.PlayPopup(vc, satisfaction, originalDelta, _totalPlacedPrice, bonuses);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"Budtending popup: {ex.Message}");
            }
        }

        private void Abort()
        {
            OTCLog.Warning(OTCLog.Systems.Customer,"Checkout aborted — customer or counter invalid");
            BudtenderHUD.Hide();
            UnlockPlayerInput();

            // Return counter products to storage before cleanup destroys them
            foreach (var product in _counterProducts)
            {
                if (!ReturnProduct(product))
                    OTCLog.Warning(OTCLog.Systems.Customer,$"Abort: no storage for '{product.ProductName}' — item lost");
            }

            if (NetworkHelper.IsHost)
            {
                if (_customer.IsValid)
                {
                    _customer.CheckoutArrivalTime = 0f;
                    _customer.ArrivedAtDestination = false;
                    _customer.State = CustomerState.ExitingStore;
                    _customer.SetAvoidancePriority(10);
                    _customer.RecallFromBuilding();
                }

                CustomerManager.Instance?.OnCheckoutComplete(_customer.Id);
                SaveData.ConfigSyncData.Instance?.PublishCheckoutClear();
            }
            else
            {
                SaveData.ConfigSyncData.SendQuestAction("CHECKOUT_ABORT");
            }

            Cleanup();
        }

        private void Cleanup()
        {
            foreach (var v in _counterVisuals)
            {
                if (v != null)
                {
                    DisableRenderers(v);
                    UnityEngine.Object.Destroy(v);
                }
            }
            _counterVisuals.Clear();
            _counterProducts.Clear();

            if (_popAudioGo != null)
            {
                UnityEngine.Object.Destroy(_popAudioGo);
                _popAudioGo = null;
                _popAudioSource = null;
            }

            if (_paymentObject != null)
            {
                UnityEngine.Object.Destroy(_paymentObject);
                _paymentObject = null;
            }

            if (_pickupAnimCoroutine != null)
            {
                MelonCoroutines.Stop(_pickupAnimCoroutine);
                _pickupAnimCoroutine = null;
            }

            if (_cashFlyCoroutine != null)
            {
                MelonCoroutines.Stop(_cashFlyCoroutine);
                _cashFlyCoroutine = null;
            }

            DestroySkillCheckBar();
            BudtenderHUD.Hide();
            Instance = null;
            _pendingLockType = PendingLockType.None;
            _pendingCustomerId = null;
            _pendingCounter = null;
        }

        /// <summary>
        /// Resets all static checkout state. Called on scene transitions.
        /// </summary>
        public static void ResetStatic()
        {
            CleanupP2P();
            Instance = null;
            CurrentLockHolder = "";
            _pendingLockType = PendingLockType.None;
            _pendingCustomerId = null;
            _pendingCounter = null;
        }
    }
}
