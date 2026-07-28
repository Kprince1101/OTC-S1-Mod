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
using ItemInstance = Il2CppScheduleOne.ItemFramework.ItemInstance;
using QualityItemInstance = Il2CppScheduleOne.ItemFramework.QualityItemInstance;
using EQuality = Il2CppScheduleOne.ItemFramework.EQuality;
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
using ItemInstance = ScheduleOne.ItemFramework.ItemInstance;
using QualityItemInstance = ScheduleOne.ItemFramework.QualityItemInstance;
using EQuality = ScheduleOne.ItemFramework.EQuality;
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

            // --- Breakdown fragment tracking (deferred commit) ---
            // Fragments are "virtual" units pledged against a larger source package
            // (brick/jar) that has NOT been touched yet. The source is only decremented
            // and repackaged at sale commit (CommitBreakdownGroups), so right-click
            // returns during placement don't lose the virtual units — the source
            // remains intact and SearchAndShowAvailable can re-pledge against it.
            public bool IsBreakdownFragment;
            public ItemSlot BreakdownSourceSlot;
            public int BreakdownSourceHotbarIdx;

            // --- Ownership (multiplayer dupe guard) ---
            // Steam ID of the player who placed this product. Protects the
            // back-out scenario where Player 1 places items, pauses, and
            // Player 2 then locks the checkout: Player 2 must not be able to
            // right-click-return Player 1's items into their own inventory.
            // Empty string means "unknown owner" (e.g. single-player or a
            // networking lookup failure); in that case the guard is bypassed
            // so we never deadlock SP players out of their own products.
            public string OwnerSteamId;
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

            // --- Breakdown fragment tracking (brick/jar → baggie breakdown) ---
            // When IsBreakdownFragment is true, this row is a "virtual" pledge
            // against a larger package (brick/jar) that is still intact in its
            // source slot. The source stays untouched through placement — clicks
            // don't decrement anything. At sale commit (CommitBreakdownGroups),
            // fragments carried over to CounterProduct are grouped by source slot
            // and the slot is decremented then, with any remainder repackaged
            // via the fallback chain.
            public bool IsBreakdownFragment;
            public ItemSlot BreakdownSourceSlot;
            public int BreakdownSourceHotbarIdx;
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
        // Skill check zone multipliers on the enjoy premium tip.
        // Miss/timeout = 50%, green = 110%, gold = 140%.
        private const float MissTipMult = 0.50f;
        private const float GreenTipMult = 1.10f;
        private const float GoldTipMult = 1.40f;
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

        /// <summary>
        /// Counter-indexed variant used by the R-key flow on clients. The client
        /// only knows which counter it is looking at — it does not have a
        /// reliable <c>counter.Queue</c> so it cannot pick the front customer
        /// itself. Host resolves the front customer and replies with the normal
        /// <c>GRANT:CHECKOUT:{custId}</c> or <c>DENY:{reason}</c>.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SendP2PLockRequestForCounter(int counterIdx, string myId)
        {
            SaveData.NetworkP2PBridge.SendToHost(P2P_LOCK_REQ, $"CHECKOUT_COUNTER:{counterIdx}:{myId}");
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
        /// Formats:
        ///   "CHECKOUT:custId:steamId"            — E-key on a specific customer
        ///   "CHECKOUT_COUNTER:counterIdx:steamId" — R-key on a counter (host picks front customer)
        /// Responds immediately with GRANT or DENY.
        /// </summary>
        private static void OnP2PLockRequest(ulong senderSteamId, string value)
        {
            if (!NetworkHelper.IsHost) return;
            if (string.IsNullOrEmpty(value)) return;

            var senderId = new CSteamID(senderSteamId);
            string clientSteamStr = senderSteamId.ToString();

            if (value.StartsWith("CHECKOUT_COUNTER:"))
            {
                string rest = value.Substring("CHECKOUT_COUNTER:".Length);
                int sep = rest.IndexOf(':');
                string idxStr = sep > 0 ? rest.Substring(0, sep) : rest;
                if (!int.TryParse(idxStr, out int counterIdx))
                {
                    SaveData.NetworkP2PBridge.SendTo(senderId, P2P_LOCK_RES, "DENY:BAD_INDEX");
                    return;
                }

                // Same lock-holder guard as the CHECKOUT: branch below.
                if (Instance != null || !string.IsNullOrEmpty(CurrentLockHolder))
                {
                    SaveData.NetworkP2PBridge.SendTo(senderId, P2P_LOCK_RES, "DENY:IN_USE");
                    return;
                }

                var counter = CheckoutCounter.GetCounterByIndex(counterIdx);
                if (counter == null)
                {
                    SaveData.NetworkP2PBridge.SendTo(senderId, P2P_LOCK_RES, "DENY:NO_COUNTER");
                    return;
                }
                if (counter.IsStaffed)
                {
                    SaveData.NetworkP2PBridge.SendTo(senderId, P2P_LOCK_RES, "DENY:STAFFED");
                    return;
                }
                if (counter.Queue.Count == 0)
                {
                    SaveData.NetworkP2PBridge.SendTo(senderId, P2P_LOCK_RES, "DENY:NO_CUSTOMER");
                    return;
                }

                string frontId = counter.Queue[0];
                if (!CustomerInstance.Active.TryGetValue(frontId, out var front) ||
                    front.State != CustomerState.CheckingOut ||
                    !front.ArrivedAtDestination ||
                    front.CheckoutArrivalTime <= 0f)
                {
                    SaveData.NetworkP2PBridge.SendTo(senderId, P2P_LOCK_RES, "DENY:NOT_READY");
                    return;
                }

                SaveData.ConfigSyncData.Instance?.PublishCheckoutState(clientSteamStr, frontId);
                SaveData.NetworkP2PBridge.SendTo(senderId, P2P_LOCK_RES, $"GRANT:CHECKOUT:{frontId}");

                if (Config.VerboseLogging.Value)
                    OTCLog.Msg(OTCLog.Systems.Customer,
                        $"P2P lock GRANTED (counter {counterIdx}) to {clientSteamStr} for {frontId}");
                return;
            }

            if (value.StartsWith("CHECKOUT:"))
            {
                string rest = value.Substring("CHECKOUT:".Length);
                int sep = rest.IndexOf(':');
                string custId = sep > 0 ? rest.Substring(0, sep) : rest;

                // Deny if EITHER the host has a local checkout running OR
                // another client already holds the lock. Relying only on
                // `Instance != null` would miss the second case because when
                // a client owns the checkout the host's local Instance stays
                // null — the lock lives in CurrentLockHolder (pushed via
                // PublishCheckoutState). Without this check, Player 2 could
                // grab the lock mid-pause and reach Player 1's counter state.
                if (Instance != null || !string.IsNullOrEmpty(CurrentLockHolder))
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

            if (NetworkHelper.IsHost)
            {
                // Host has authoritative queue + arrival state and can pick
                // the front customer locally.
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

                StartCheckoutDirect(waitingCustomer, counter);
            }
            else
            {
                // Client can't evaluate readiness — counter.Queue,
                // ArrivedAtDestination and CheckoutArrivalTime are all host-only
                // state. Fire a counter-indexed P2P request immediately and let
                // host authoritatively resolve the front customer. If the host
                // rejects (NO_CUSTOMER / NOT_READY), it's a cheap round-trip
                // and the next keypress will try again — self-healing by design.
                if (!string.IsNullOrEmpty(CurrentLockHolder)) return;

                int counterIdx = CheckoutCounter.GetCounterIndex(counter);
                if (counterIdx < 0) return;

                _pendingLockType = PendingLockType.Checkout;
                _pendingCustomerId = null; // Host fills this in via the GRANT response
                _pendingCounter = counter;
                _lockRequestTime = Time.time;
                string myId = SaveData.ConfigSyncData.LocalPlayerId;

                if (_p2pSubscribed)
                {
                    SendP2PLockRequestForCounter(counterIdx, myId);
                }
                else
                {
                    // SyncVar fallback can't drive counter-indexed requests
                    // because the handler only knows how to look up a customer
                    // by id. Without P2P the R-key flow isn't available to
                    // clients — they can still use the E-key (AssignedCounter)
                    // path below.
                    _pendingLockType = PendingLockType.None;
                    _pendingCounter = null;
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
                    totalTip = dealTip + customer.EnjoyPremium * skillBonus;
                }

                // Record sales if product data provided
                if (parts.Length > 2 && !string.IsNullOrEmpty(parts[2]))
                {
                    var saveData = SaveData.PropertySaveData.Instance;
                    if (saveData != null)
                    {
                        int gameDay = S1API.GameTime.TimeManager.ElapsedDays;
                        int gameHour = S1API.GameTime.TimeManager.CurrentTime;
                        string custName = customer?.GameNpc?.FullName ?? custId;
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

                // Multiplayer dupe guard: if this product was placed by a
                // different player (Player 1 backed out, Player 2 locked the
                // paused checkout), silently refuse the right-click so they
                // can't return stolen items into their own inventory. Empty
                // OwnerSteamId means single-player or unresolved network ID
                // so we bypass the check to avoid locking SP out.
                string localId = SaveData.ConfigSyncData.LocalPlayerId ?? "";
                if (!string.IsNullOrEmpty(product.OwnerSteamId) &&
                    !string.IsNullOrEmpty(localId) &&
                    product.OwnerSteamId != localId)
                    return;

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
                            try { _customer.GameNpc?.SendAnimationTrigger("ThumbsUp"); }
                            catch (Exception animEx)
                            {
                                OTCLog.Warning(OTCLog.Systems.Customer,
                                    $"ThumbsUp animation trigger failed: {animEx.Message}");
                            }
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
                try { _customer.GameNpc?.SendAnimationTrigger("ConversationGesture1"); }
                catch (Exception animEx)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"ConversationGesture1 animation trigger failed: {animEx.Message}");
                }
            }
            if (!_skillCheckPlayedAcknowledge && elapsed >= VO_Acknowledge)
            {
                _skillCheckPlayedAcknowledge = true;
                PlayCustomerVoice(EVOLineType.Acknowledge);
                try { _customer.GameNpc?.SendAnimationTrigger("Nod"); }
                catch (Exception animEx)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"Nod animation trigger failed: {animEx.Message}");
                }
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

                // Determine zone hit — sets the multiplier on the enjoy premium tip
                if (t >= GoldStart && t <= GoldEnd)
                {
                    _skillCheckTipBonus = GoldTipMult;
                    if (_skillCheckLabel != null) _skillCheckLabel.text = "Perfect!";
                    if (_skillCheckCursor != null) _skillCheckCursor.GetComponent<Image>().color = new Color(1f, 0.84f, 0f, 1f);
                    if (_goldZoneImg != null) _goldZoneImg.color = new Color(1f, 0.84f, 0f, 0.8f);
                }
                else if (t >= GreenStart && t <= GreenEnd)
                {
                    _skillCheckTipBonus = GreenTipMult;
                    if (_skillCheckLabel != null) _skillCheckLabel.text = "Nice!";
                    if (_skillCheckCursor != null) _skillCheckCursor.GetComponent<Image>().color = new Color(0.30f, 0.85f, 0.31f, 1f);
                    if (_greenZoneImg != null) _greenZoneImg.color = new Color(0.30f, 0.68f, 0.31f, 0.8f);
                }
                else
                {
                    _skillCheckTipBonus = MissTipMult;
                    if (_skillCheckLabel != null) _skillCheckLabel.text = "";
                    if (_skillCheckCursor != null) _skillCheckCursor.GetComponent<Image>().color = new Color(0.6f, 0.2f, 0.2f, 1f);
                }
                return;
            }

            // Time expired without pressing — miss (still gets partial tip)
            if (elapsed >= SkillCheckDuration)
            {
                _skillCheckTipBonus = MissTipMult;
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
                ProductDef = product.ProductDef,
                // Propagate breakdown metadata so CommitBreakdownGroups can
                // group by source slot at sale completion.
                IsBreakdownFragment = product.IsBreakdownFragment,
                BreakdownSourceSlot = product.BreakdownSourceSlot,
                BreakdownSourceHotbarIdx = product.BreakdownSourceHotbarIdx,
                // Stamp placement with the local player's Steam ID so a
                // different player can't right-click-return this item during
                // a paused/back-out scenario (multiplayer dupe guard).
                OwnerSteamId = SaveData.ConfigSyncData.LocalPlayerId ?? ""
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

        /// <summary>
        /// Pauses an in-progress checkout so the player can walk away (R / Tab).
        /// Called from both the pause hotkey path and the checkout-state machine
        /// when focus is lost. On entry this method invokes
        /// <see cref="CommitBreakdownGroups"/> to force-materialise any outstanding
        /// breakdown fragments before the player backs out: fragments are
        /// "virtual" pledges against a large source package (brick/jar) that has
        /// not been touched yet, so without an early commit the player could
        /// pause, physically relocate the source, and then complete the sale,
        /// shipping phantom jars to the customer while the source still sat in
        /// another property's storage. Committing here decrements the slot,
        /// repackages the remainder through the placement fallback chain, and
        /// promotes successful groups to real counter products inline so
        /// resume / sale completion / right-click all see materialised items.
        /// </summary>
        private void PauseCheckout()
        {
            CommitBreakdownGroups();

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
            // Commit any deferred breakdown fragments NOW — the sale is locked in,
            // so it's safe to decrement the source brick/jar slots and repackage
            // the remainder into storage. After this point, nothing else can return
            // fragments to the counter.
            CommitBreakdownGroups();

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

                    // Track per-candidate consumption so the breakdown pass knows what stock
                    // is still available after the greedy fill.
                    var takenPerCandidate = new int[candidates.Count];

                    // Greedy fill: largest packaging first, floor division (never overshoot)
                    int startIdx = _availableProducts.Count;
                    int unitsFound = 0;
                    for (int cIdx = 0; cIdx < candidates.Count; cIdx++)
                    {
                        if (unitsFound >= unitsRemaining) break;
                        var c = candidates[cIdx];

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
                        takenPerCandidate[cIdx] = pkgsToTake;
                    }

                    // Auto-package: consolidate small packages into largest valid packaging
                    if (_availableProducts.Count - startIdx > 1)
                        ConsolidateAvailableEntries(startIdx, selection.Price);

                    // Breakdown pass: if the greedy fill left a shortfall smaller than the
                    // smallest remaining candidate, split a larger package and emit rows
                    // in the largest-fit valid packagings (e.g. 2× 5g jars + 3× 1g baggies
                    // for a 13g split). See EmitBreakdownFragments for details.
                    if (unitsFound < unitsRemaining)
                    {
                        int added = EmitBreakdownFragments(
                            selection, candidates, takenPerCandidate,
                            unitsRemaining - unitsFound);
                        unitsFound += added;
                    }

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
        /// Splits larger packages to cover a shortfall left by the greedy fill. Each
        /// split emits multiple <see cref="AvailableProduct"/> rows whose packaging is
        /// the largest-fit partition of the taken units (e.g. a 13g split from a 20g
        /// brick produces 2× 5g jars + 3× 1g baggies, not 13× 1g baggies). Emitted
        /// rows do NOT decrement the source slot — they are "virtual" pledges that
        /// carry over to <see cref="CounterProduct"/> on click. The source stays
        /// intact through placement and is only split at sale commit by
        /// <see cref="CommitBreakdownGroups"/>, which groups pledged fragments by
        /// source slot and consumes ceil(totalPledged / sourceMult) packages.
        ///
        /// Split-source selection runs as a loop so multiple sources can be consumed in sequence:
        ///   1. Prefer the smallest candidate whose mult covers the remaining shortfall
        ///      in one shot (minimises waste — e.g. for a 3g shortfall, split a 5g jar
        ///      instead of a 20g brick so the repackaged remainder is only 2g).
        ///   2. Fall back to the largest candidate with stock when nothing covers the
        ///      remainder alone; consume that package fully and loop with the reduced
        ///      shortfall. Example: shortfall=8 with only 5g jars left (no brick
        ///      stock) — Pass 1 skips every jar (5 &lt; 8), Pass 2 picks a 5g jar,
        ///      the loop consumes it and continues with shortfall=3, which Pass 1
        ///      then covers by splitting another 5g jar (remainder=2g). Same mechanism
        ///      gracefully emits partial coverage when the ask is impossible.
        ///   3. If neither pass finds a splittable candidate, stop — whatever units we
        ///      couldn't satisfy flow back to SearchAndShowAvailable and become a
        ///      "missing product" entry. The customer completes the sale early with
        ///      an annoyed voice line, giving the player a chance to restock.
        /// Returns the total number of units satisfied across all emitted groups.
        /// </summary>
        private int EmitBreakdownFragments(
            CustomerInstance.SelectedProduct selection,
            List<(ItemSlot slot, int hotbarIdx, string pkgId, int mult, int available,
                  ProductDefinition prodDef, GameObject visual)> candidates,
            int[] takenPerCandidate,
            int shortfall)
        {
            if (shortfall <= 0 || candidates == null || candidates.Count == 0)
                return 0;

            // Gather all valid packagings sorted LARGEST→SMALLEST so split emission
            // can walk them greedily. Also track the smallest for divisibility checks.
            var prodDef0 = candidates[0].prodDef;
            if (prodDef0?.ValidPackaging == null || prodDef0.ValidPackaging.Length == 0)
                return 0;

            var sortedPackagings = new List<PackagingDefinition>();
            for (int p = 0; p < prodDef0.ValidPackaging.Length; p++)
            {
                var pk = prodDef0.ValidPackaging[p];
                if (pk != null && pk.Quantity > 0) sortedPackagings.Add(pk);
            }
            if (sortedPackagings.Count == 0) return 0;
            sortedPackagings.Sort((a, b) => b.Quantity.CompareTo(a.Quantity));

            PackagingDefinition smallestPack = sortedPackagings[sortedPackagings.Count - 1];
            int fragmentMult = smallestPack.Quantity;
            if (fragmentMult <= 0) return 0;

            // Cache per-packaging visuals so we don't hit StoredItem_Filled once per row.
            var pkgVisuals = new Dictionary<string, GameObject>();
            foreach (var pk in sortedPackagings)
            {
                try
                {
                    var si = pk.StoredItem_Filled;
                    if (si != null) pkgVisuals[pk.ID] = si.gameObject;
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.General,
                        $"EmitBreakdownFragments: failed to get visual for pkg '{pk.ID}': {ex.Message}");
                }
            }

            int totalSatisfied = 0;

            // Split packages until the shortfall is met or no splittable candidates remain.
            while (shortfall > 0)
            {
                // Shortfall must be representable in whole fragments — otherwise we
                // can't emit valid packaging (e.g. 3g shortfall on a product whose
                // smallest packaging is 5g). Bail gracefully; caller marks missing.
                if (shortfall % fragmentMult != 0) break;

                // Pass 1: smallest candidate whose whole package covers the shortfall
                // without waste (current best — most efficient use of packaging).
                int bestIdx = -1;
                int bestMult = int.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    int remainingStock = candidates[i].available - takenPerCandidate[i];
                    if (remainingStock <= 0) continue;
                    if (candidates[i].mult <= fragmentMult) continue; // same size as fragment, nothing to split
                    if (candidates[i].mult < shortfall) continue;     // can't cover in one shot
                    if (candidates[i].mult < bestMult)
                    {
                        bestMult = candidates[i].mult;
                        bestIdx = i;
                    }
                }

                // Pass 2: nothing covers the full shortfall — pick the LARGEST
                // remaining candidate, consume it fully, and loop with the
                // reduced shortfall. This handles multi-source splitting.
                if (bestIdx < 0)
                {
                    int largestMult = 0;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        int remainingStock = candidates[i].available - takenPerCandidate[i];
                        if (remainingStock <= 0) continue;
                        if (candidates[i].mult <= fragmentMult) continue;
                        if (candidates[i].mult > largestMult)
                        {
                            largestMult = candidates[i].mult;
                            bestIdx = i;
                        }
                    }
                }

                if (bestIdx < 0) break; // no splittable candidates left

                var c = candidates[bestIdx];
                // Take only as much as we still need from this package. The remainder
                // (c.mult - take) is not tracked per-row — CommitBreakdownGroups sums
                // pledged units at commit time and computes the leftover from the
                // live source slot's actual packaging size.
                int take = Math.Min(shortfall, c.mult);
                if (take % fragmentMult != 0)
                {
                    // Partial take doesn't divide into whole fragments — skip this
                    // candidate by marking it "taken" so we don't loop forever on it.
                    takenPerCandidate[bestIdx]++;
                    continue;
                }

                // Split `take` into the largest valid packagings first so the customer
                // sees e.g. 2× 5g jars + 3× 1g baggies for a 13g split, not 13 baggies.
                int takeRemaining = take;
                for (int pi = 0; pi < sortedPackagings.Count && takeRemaining > 0; pi++)
                {
                    var pkg = sortedPackagings[pi];
                    int pkgCount = takeRemaining / pkg.Quantity;
                    if (pkgCount <= 0) continue;
                    takeRemaining -= pkgCount * pkg.Quantity;

                    pkgVisuals.TryGetValue(pkg.ID, out GameObject pkgVisual);
                    if (pkgVisual == null) pkgVisual = c.visual;

                    for (int u = 0; u < pkgCount; u++)
                    {
                        _availableProducts.Add(new AvailableProduct
                        {
                            ProductId = selection.ProductId,
                            PackagingId = pkg.ID,
                            ProductName = selection.ProductName,
                            Price = selection.Price * pkg.Quantity,
                            QualityLevel = selection.QualityLevel,
                            UnitCount = pkg.Quantity,
                            VisualPrefab = pkgVisual,
                            ProductDef = c.prodDef,
                            SourceSlot = null,
                            HotbarIndex = -1,
                            IsBreakdownFragment = true,
                            BreakdownSourceSlot = c.slot,
                            BreakdownSourceHotbarIdx = c.hotbarIdx
                        });
                    }
                }

                // Mark one package of this candidate as consumed — subsequent
                // iterations see the reduced stock via remainingStock checks.
                takenPerCandidate[bestIdx]++;
                shortfall -= take;
                totalSatisfied += take;
            }

            return totalSatisfied;
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
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"CollectCandidatesFromStorage: Definition cast failed: {ex.Message}");
                }

                if (prodDef?.ID != productId) continue;

                GameObject visualPrefab = null;
                try
                {
                    var stored = productItem.StoredItem;
                    if (stored != null) visualPrefab = stored.gameObject;
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"CollectCandidatesFromStorage: StoredItem read failed for '{productId}': {ex.Message}");
                }

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
                    catch (Exception ex)
                    {
                        OTCLog.Warning(OTCLog.Systems.Customer,
                            $"CollectCandidatesFromInventory: Definition cast failed: {ex.Message}");
                    }

                    if (prodDef?.ID != productId) continue;

                    GameObject visualPrefab = null;
                    try
                    {
                        var stored = productItem.StoredItem;
                        if (stored != null) visualPrefab = stored.gameObject;
                    }
                    catch (Exception ex)
                    {
                        OTCLog.Warning(OTCLog.Systems.Customer,
                            $"CollectCandidatesFromInventory: StoredItem read failed for '{productId}': {ex.Message}");
                    }

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

        private void ConsumeFromSource(AvailableProduct product)
        {
            // Breakdown fragments: defer. We do NOT touch the source slot here —
            // the fragments are "virtual" pledges against an intact brick/jar that
            // only gets split at sale completion (CommitBreakdownGroups). This lets
            // the player right-click fragments off the counter without losing the
            // virtual units or mutating inventory mid-placement.
            if (product.IsBreakdownFragment)
                return;

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
        /// Commits all deferred breakdown fragments currently on the counter. Called
        /// once at sale completion (TransitionToCustomerPickup), before the customer
        /// walks away with their goods.
        ///
        /// Fragments are grouped by their <i>source slot</i> so multiple search
        /// rebuilds against the same brick collapse into a single
        /// consume + single remainder repackaging. Without slot-grouping, right-clicks
        /// that trigger a re-search would split a second brick even though the player
        /// only took enough product to fit one — wasteful on packaging.
        ///
        /// For each unique source slot:
        ///   1. Sum pledged units across its fragments.
        ///   2. Consume ceil(total / sourceMult) packages from the slot.
        ///   3. Repackage (packagesConsumed*sourceMult − total) leftover units into
        ///      the largest-fit packaging and place via TryPlaceItemAnywhere.
        /// </summary>
        private void CommitBreakdownGroups()
        {
            if (_counterProducts.Count == 0) return;

            // Group pending fragments by source slot. Each group stores the
            // _counterProducts indices of its members so successful commits can
            // flip the fragment flags in place (and failed groups are left alone,
            // preserving the virtual pledge so a retry or abort can still act on
            // them instead of silently promoting them to real items).
            //
            // Parallel lists are used instead of ValueTuple-in-dict so struct
            // CounterProducts living on a MonoBehaviour stay IL2CPP-friendly.
            var slotKeys = new List<ItemSlot>();
            var hotbarKeys = new List<int>();
            var fragmentIndexLists = new List<List<int>>();

            for (int i = 0; i < _counterProducts.Count; i++)
            {
                var cp = _counterProducts[i];
                if (!cp.IsBreakdownFragment) continue;

                int keyIdx = -1;
                for (int k = 0; k < slotKeys.Count; k++)
                {
                    // Match on slot reference first, then on hotbar index for player-inventory sources.
                    bool slotMatch = cp.BreakdownSourceSlot != null
                                     && ReferenceEquals(slotKeys[k], cp.BreakdownSourceSlot);
                    bool hotbarMatch = cp.BreakdownSourceSlot == null
                                       && slotKeys[k] == null
                                       && hotbarKeys[k] == cp.BreakdownSourceHotbarIdx;
                    if (slotMatch || hotbarMatch)
                    {
                        keyIdx = k;
                        break;
                    }
                }

                if (keyIdx < 0)
                {
                    slotKeys.Add(cp.BreakdownSourceSlot);
                    hotbarKeys.Add(cp.BreakdownSourceHotbarIdx);
                    fragmentIndexLists.Add(new List<int> { i });
                }
                else
                {
                    fragmentIndexLists[keyIdx].Add(i);
                }
            }

            for (int g = 0; g < slotKeys.Count; g++)
            {
                if (SplitBreakdownSourceGroup(slotKeys[g], hotbarKeys[g], fragmentIndexLists[g]))
                    PromoteFragmentsToRealProducts(fragmentIndexLists[g]);
            }
        }

        /// <summary>
        /// Flips a set of committed fragment rows (identified by their index in
        /// <see cref="_counterProducts"/>) into regular counter products. Called
        /// only after <see cref="SplitBreakdownSourceGroup"/> reports success, so
        /// the source was actually decremented and the remainder placed — the
        /// fragments are now real items and must no longer be touched by a
        /// subsequent commit pass or the fragment short-circuit in
        /// <see cref="ReturnProduct(CounterProduct)"/>.
        /// </summary>
        private void PromoteFragmentsToRealProducts(List<int> indices)
        {
            if (indices == null) return;
            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (idx < 0 || idx >= _counterProducts.Count) continue;
                var cp = _counterProducts[idx];
                cp.IsBreakdownFragment = false;
                cp.BreakdownSourceSlot = null;
                cp.BreakdownSourceHotbarIdx = -1;
                _counterProducts[idx] = cp;
            }
        }

        /// <summary>
        /// Performs the actual split for one (source slot, hotbar index) group of
        /// fragments. Consumes ceil(totalPledged / sourceMult) packages from the
        /// slot and repackages the remainder via the placement fallback chain.
        /// Returns true on full success, false if the source couldn't be resolved
        /// or any repackaged remainder failed to place — a false return leaves
        /// the fragments untouched so the caller won't promote them to real items.
        /// </summary>
        private bool SplitBreakdownSourceGroup(
            ItemSlot srcSlot,
            int srcHotbarIdx,
            List<int> fragmentIndices)
        {
            if (fragmentIndices == null || fragmentIndices.Count == 0) return false;

            // Tracks whether we decremented the source slot. If we did, the
            // fragments MUST be promoted even on a later exception — otherwise a
            // retry pass would double-consume an already-empty slot.
            bool sourceConsumed = false;

            try
            {
                // Sum pledged units and pick up shared metadata from the fragments.
                // Fragments in a single group all came from the same source, so
                // the first one's quality is canonical — no need to scan.
                int totalPledged = 0;
                string productName = "";
                ProductDefinition prodDef = null;
                int qualityLevel = 0;
                bool metadataPicked = false;
                for (int i = 0; i < fragmentIndices.Count; i++)
                {
                    int idx = fragmentIndices[i];
                    if (idx < 0 || idx >= _counterProducts.Count) continue;
                    var f = _counterProducts[idx];
                    totalPledged += f.UnitCount;
                    if (!metadataPicked)
                    {
                        productName = f.ProductName;
                        prodDef = f.ProductDef;
                        qualityLevel = f.QualityLevel;
                        metadataPicked = true;
                    }
                }
                if (totalPledged <= 0 || prodDef == null) return false;

                // Resolve the live slot once up front. For player-inventory
                // fragments the slot ref is null, so walk the hotbar index
                // instead, then pass the resolved slot directly to the consume
                // helper so it doesn't re-walk the hotbar.
                ItemSlot resolvedSlot = srcSlot;
                if (resolvedSlot == null && srcHotbarIdx >= 0)
                {
                    var inv = PlayerSingleton<PlayerInventory>.Instance;
                    if (inv?.hotbarSlots != null && srcHotbarIdx < inv.hotbarSlots.Count)
                        resolvedSlot = inv.hotbarSlots[srcHotbarIdx];
                }

                if (resolvedSlot?.ItemInstance == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"CommitBreakdownGroups: source slot for '{productName}' is empty, leaving {totalPledged} pledged units as virtual fragments");
                    return false;
                }

                // Read the source packaging BEFORE any consume — the slot may end
                // up cleared after the first decrement (e.g. a 1-quantity brick).
                int sourceMult = 1;
#if IL2CPP
                var srcProductItem = resolvedSlot.ItemInstance.TryCast<ProductItemInstance>();
#else
                var srcProductItem = resolvedSlot.ItemInstance as ProductItemInstance;
#endif
                if (srcProductItem != null)
                {
                    if (srcProductItem.AppliedPackaging != null && srcProductItem.AppliedPackaging.Quantity > 0)
                        sourceMult = srcProductItem.AppliedPackaging.Quantity;
                    qualityLevel = (int)srcProductItem.Quality;
                }

                // Consume enough whole packages from the slot to cover the pledge.
                // ceil((totalPledged) / sourceMult); this may be more than one
                // package if a re-search pledged fragments beyond a single brick's
                // capacity (rare but possible when multiple bricks live in one slot).
                int packagesToConsume = (totalPledged + sourceMult - 1) / sourceMult;
                int remainderUnits = packagesToConsume * sourceMult - totalPledged;

                // Flip sourceConsumed inside the loop so that a throw mid-way
                // through a multi-package consume still leaves the flag set
                // for earlier iterations. Without this, a second commit pass
                // would over-consume: the partial decrement from the first
                // iteration is already physical, but fragments would stay
                // "virtual" and a retry would try to decrement again.
                for (int i = 0; i < packagesToConsume; i++)
                {
                    ConsumeFromSource(resolvedSlot, -1, productName);
                    sourceConsumed = true;
                }

                if (remainderUnits <= 0) return true;

                var validPack = prodDef.ValidPackaging;
                if (validPack == null || validPack.Length == 0)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"CommitBreakdownGroups: no valid packaging defs for '{productName}', {remainderUnits} units lost");
                    return false;
                }

                // Largest-first repackaging (mirrors ConsolidateAvailableEntries layout).
                for (int p = validPack.Length - 1; p >= 0 && remainderUnits > 0; p--)
                {
                    var pkg = validPack[p];
                    if (pkg == null || pkg.Quantity <= 0) continue;
                    int count = remainderUnits / pkg.Quantity;
                    if (count <= 0) continue;
                    remainderUnits -= count * pkg.Quantity;

                    for (int i = 0; i < count; i++)
                    {
                        var instance = prodDef.GetDefaultInstance(1);
#if IL2CPP
                        var prodInstance = instance?.TryCast<ProductItemInstance>();
#else
                        var prodInstance = instance as ProductItemInstance;
#endif
                        if (prodInstance == null) continue;
                        prodInstance.SetPackaging(pkg);

                        // Preserve quality from the split source package.
#if IL2CPP
                        var qInst = instance?.TryCast<QualityItemInstance>();
#else
                        var qInst = instance as QualityItemInstance;
#endif
                        if (qInst != null)
                        {
                            try { qInst.SetQuality((EQuality)qualityLevel); }
                            catch (Exception ex)
                            {
                                OTCLog.Warning(OTCLog.Systems.Customer,
                                    $"CommitBreakdownGroups: SetQuality failed: {ex.Message}");
                            }
                        }

                        if (!TryPlaceItemAnywhere(prodInstance, resolvedSlot))
                        {
                            OTCLog.Warning(OTCLog.Systems.Customer,
                                $"CommitBreakdownGroups: no room for {productName} {pkg.ID} remainder, destroying");
                        }
                    }
                }

                if (remainderUnits > 0)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"CommitBreakdownGroups: {remainderUnits} units of {productName} could not be repackaged (no valid small packaging)");
                }

                // Partial remainder placement failures are treated as a
                // logged warning but still a successful commit: the source
                // has already been decremented (see sourceConsumed) and the
                // pledged product now exists as real counter products. A
                // false return here would trigger a second commit pass that
                // tries to decrement the already-empty source slot again.
                return true;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"CommitBreakdownGroups failed: {ex.Message}");
                // If the source was already decremented before the exception,
                // the fragments MUST be promoted so a retry doesn't over-consume.
                return sourceConsumed;
            }
        }

        /// <summary>
        /// Places an ItemInstance via the fallback chain:
        ///  1) preferredSlot (if it can still accept the item — e.g. the source slot
        ///     of a split brick, which is now empty)
        ///  2) player inventory (any open slot)
        ///  3) checkout counter storage
        ///  4) any other storage entity on the property grid
        /// Returns false only if no container accepts the item (caller should warn/destroy).
        /// </summary>
        private static bool TryPlaceItemAnywhere(ItemInstance productInstance, ItemSlot preferredSlot)
        {
            if (productInstance == null) return false;
            try
            {
                // 0. Prefer the source slot if it's now empty — keeps split remainder
                //    "where it was found" per the design intent.
                if (preferredSlot != null && preferredSlot.ItemInstance == null)
                {
                    preferredSlot.InsertItem(productInstance);
                    return true;
                }

                // 1. Player inventory
                var inventory = PlayerSingleton<PlayerInventory>.Instance;
                if (inventory != null && inventory.CanItemFitInInventory(productInstance, 1))
                {
                    inventory.AddItemToInventory(productInstance);
                    return true;
                }

                // 2. Checkout counter storage
                var counterStorage = Instance?._counter?.CounterStorageEntity;
                if (counterStorage != null && counterStorage.CanItemFit(productInstance, 1))
                {
                    counterStorage.InsertItem(productInstance, true);
                    return true;
                }

                // 3. Any other storage entity on the active counter's property.
                var propertyGrid = Instance?._counter?.ParentGrid;
                if (propertyGrid != null && BuildingGridFactory.GridContainers.TryGetValue(propertyGrid, out var buildingRoot))
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
                        if (s == counterStorage) continue;
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
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"TryPlaceItemAnywhere failed: {ex.Message}");
                return false;
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
            // Deferred breakdown fragments never touched the source slot — the
            // brick/jar is still intact in its original slot. Removing the fragment
            // from the counter is enough; creating a fresh baggie here would
            // duplicate product. Caller drops the CounterProduct from the list.
            if (product.IsBreakdownFragment)
                return true;

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

                // 3. Try any other storage entity on the active counter's property
                var propertyGrid = Instance?._counter?.ParentGrid;
                if (propertyGrid != null && BuildingGridFactory.GridContainers.TryGetValue(propertyGrid, out var buildingRoot))
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
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"PlayPopSound failed: {ex.Message}");
            }
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
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"CacheScanClip failed: {ex.Message}");
            }
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
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"PlayCashSound failed: {ex.Message}");
            }
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
                        string custName = _customer?.GameNpc?.FullName ?? "Unknown";
                        string txId = saveData.NextTransactionId();
                        float tip = DispensaryDealManager.GetTipAmount(_customer, _totalPlacedPrice);
                        tip += _customer.EnjoyPremium * _skillCheckTipBonus;
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
                float skillTip = _customer.EnjoyPremium * _skillCheckTipBonus;
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
                    $"Budtending rewards for {vc.NPC.FullName}: rel={relChange:+0.000;-0.000} satisfaction={satisfaction:P0}");
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
            Instance?.Cleanup(); // tear down HUD, visuals, and coroutines before clearing statics
            Instance = null;
            CurrentLockHolder = "";
            _pendingLockType = PendingLockType.None;
            _pendingCustomerId = null;
            _pendingCounter = null;
        }
    }
}
