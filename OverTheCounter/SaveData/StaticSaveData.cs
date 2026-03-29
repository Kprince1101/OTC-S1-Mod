using MelonLoader;
using OverTheCounter.NPCs;
using OverTheCounter.Quests;
using S1API.GameTime;
using S1API.Internal.Abstraction;
using S1API.Money;
using S1API.Saveables;
using S1API.Quests;
using System;
using OverTheCounter.Utilities;
using System.Linq;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
#endif

namespace OverTheCounter.SaveData
{
    public class StaticSaveData : Saveable
    {
        [SaveableField("static_intro_completed")]
        private bool _introCompleted;

        [SaveableField("static_quest_triggered")]
        private bool _questTriggered;

        [SaveableField("static_crm_tier")]
        private int _crmTier;           // 0=Locked, 1=Lite, 2=Pro, 3=Enterprise

        [SaveableField("static_saas_active")]
        private bool _saasActive;

        [SaveableField("static_saas_next_payment_day")]
        private int _saasNextPaymentDay; // _dayPassCount value when next payment is due

        [SaveableField("static_saas_day_pass_count")]
        private int _dayPassCount;       // Incremented every OnDayPass while subscription is active

        [SaveableField("static_upgrade_available")]
        private bool _upgradeAvailable;

        [SaveableField("static_upgrade_accepted")]
        private bool _upgradeAccepted;

        [SaveableField("static_early_visit_seen")]
        private bool _earlyVisitSeen;

        [SaveableField("static_tier1_money_paid")]
        private bool _tier1MoneyPaid;

        [SaveableField("static_tier1_product_delivered")]
        private bool _tier1ProductDelivered;

        [SaveableField("static_upgrade_money_paid")]
        private bool _upgradeMoneyPaid;

        [SaveableField("static_upgrade_product_delivered")]
        private bool _upgradeProductDelivered;

        [SaveableField("static_thread_order")]
        private string _threadOrder = "";

        private int _tickCounter;
        private const int TICK_INTERVAL = 300;
        private bool _positionFixed;

        // Set when a state change requires a dialogue rebuild but dialogue is in progress.
        // Tick() checks this every frame and refreshes the moment dialogue closes.
        private bool _dialogueStale;

        // Tracks dialogue open/close to detect the closing edge and force a rebuild.
        private bool _wasInDialogue;

        // Runtime-only: guards against double quest creation per session.
        private bool _questCreated;

        // Runtime-only: deferred re-publish after save/load so the client
        // receives correct state even if the initial SyncVar push missed us.
        private bool _needsStatePublish;

        // Runtime-only: deferred quest reconciliation guard (fires once per session in Tick).
        private bool _questReconciled;

        // Runtime-only: post-load quest reconciliation (fires once after IsGameLoaded).
        private bool _postLoadReconciled;

        // Runtime-only: intro text deferred until Static NPC spawns.
        private bool _needsIntroText;

        // Runtime-only: prevents double subscription to OnDayPass.
        private static bool _dayPassSubscribed;

        public static StaticSaveData Instance { get; private set; }

        internal static void ResetInstance() => Instance = null;

        public bool IntroCompleted => _introCompleted;
        public bool QuestTriggered => _questTriggered;
        public int CrmTier => _crmTier;
        public bool SaasActive => _saasActive;
        public int SaasNextPaymentDay => _saasNextPaymentDay;
        public int DayPassCount => _dayPassCount;
        public bool UpgradeAvailable => _upgradeAvailable;
        public bool UpgradeAccepted => _upgradeAccepted;
        public bool EarlyVisitSeen => _earlyVisitSeen;
        public bool Tier1MoneyPaid => _tier1MoneyPaid;
        public bool Tier1ProductDelivered => _tier1ProductDelivered;
        public bool UpgradeMoneyPaid => _upgradeMoneyPaid;
        public bool UpgradeProductDelivered => _upgradeProductDelivered;
        public string ThreadOrder => _threadOrder ?? "";

        /// <summary>
        /// Appends a thread ID to the activation order if not already present.
        /// </summary>
        public void ActivateThread(string threadId)
        {
            if (string.IsNullOrEmpty(threadId)) return;
            var parts = (_threadOrder ?? "").Split(',');
            foreach (var p in parts)
                if (p == threadId) return;
            _threadOrder = string.IsNullOrEmpty(_threadOrder) ? threadId : _threadOrder + "," + threadId;
        }

        public StaticSaveData()
        {
            Instance = this;

            if (!_dayPassSubscribed)
            {
                TimeManager.OnDayPass += () => Instance?.OnDayPass();
                TimeManager.OnTick += () => Instance?.OnTimeTick();
                _dayPassSubscribed = true;
            }
        }

        protected override void OnLoaded()
        {
            Instance = this;

            // Heal stale flags: derive earlier state from later progression.
            if (!_introCompleted && _crmTier >= 1)
                _introCompleted = true;
            if (!_questTriggered && (_introCompleted || _crmTier >= 1))
                _questTriggered = true;

            if (_questTriggered)
            {
                _questCreated = true;
                if (NetworkHelper.IsHost)
                {
                    _needsStatePublish = true;
                    // Re-send intro text on load if player hasn't visited Static yet
                    if (!_introCompleted)
                        _needsIntroText = true;
                }
            }

            _dialogueStale = true;

            ConfigSyncData.ApplyPendingGameState();
            // ReconcileQuest deferred to Tick safety net — OnLoaded fires before
            // the game finishes loading its own quest persistence (Quests.json),
            // so creating quests here races with the game's own quest loading.
        }

        /// <summary>
        /// Reconciles all quest states to match SaveData.
        /// Covers intro quest, upgrade quests, and force-creates missing quest instances.
        /// Returns true if any quest was created (caller should defer advancement
        /// to the next frame so Unity's Start() can initialize entry components).
        /// </summary>
        private bool ReconcileQuest()
        {
            bool created = false;

            // ── Intro quest ──
            int effectiveStage = _crmTier >= 1 ? 3 : _introCompleted ? 2 : _questTriggered ? 1 : 0;

            // Don't create quests that are already fully complete — no UI to show.
            // Also skip if _questCreated is set - OnCreated (which sets Instance) runs
            // on the next frame via Unity Start(), so Instance may still be null.
            if (effectiveStage > 0 && effectiveStage < 3 && !_questCreated && StaticIntroQuest.Instance == null)
            {
                try
                {
                    var quest = (StaticIntroQuest)QuestManager.CreateQuest<StaticIntroQuest>();
                    if (quest != null) { quest.Initialize(); quest.StartQuest(); created = true; }
                }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"ReconcileQuest: intro quest creation failed: {ex.Message}"); }
            }

            // Skip advancement on the same frame as creation — Unity's Start()
            // hasn't run on the entry components yet, so state changes don't stick.
            if (!created && StaticIntroQuest.Instance != null && effectiveStage > StaticIntroQuest.Instance.Stage)
            {
                if (effectiveStage >= 2 && StaticIntroQuest.Instance.Stage < 2)
                    StaticIntroQuest.Instance.CompleteObj1();
                if (effectiveStage >= 3 && StaticIntroQuest.Instance.Stage < 3)
                    StaticIntroQuest.Instance.CompleteObj2();
            }

            // ── Upgrade 1 quest (tier 1→2) ──
            if (_crmTier == 1 && _saasActive && _upgradeAccepted && StaticUpgrade1Quest.Instance == null)
            {
                try { CreateUpgradeQuest(); created = true; }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"ReconcileQuest: upgrade1 quest creation failed: {ex.Message}"); }
            }
            else if (!created && _crmTier >= 2 && StaticUpgrade1Quest.Instance != null && StaticUpgrade1Quest.Instance.Stage < 1)
            {
                try { StaticUpgrade1Quest.Instance.CompleteObj1(); }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"ReconcileQuest: upgrade1 completion failed: {ex.Message}"); }
            }

            // ── Upgrade 2 quest (tier 2→3) ──
            if (_crmTier == 2 && _saasActive && _upgradeAccepted && StaticUpgrade2Quest.Instance == null)
            {
                try { CreateUpgradeQuest(); created = true; }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"ReconcileQuest: upgrade2 quest creation failed: {ex.Message}"); }
            }
            else if (!created && _crmTier >= 3 && StaticUpgrade2Quest.Instance != null && StaticUpgrade2Quest.Instance.Stage < 1)
            {
                try { StaticUpgrade2Quest.Instance.CompleteObj1(); }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"ReconcileQuest: upgrade2 completion failed: {ex.Message}"); }
            }

            return created;
        }

        /// <summary>
        /// Called once from Tick() after LoadManager.IsGameLoaded becomes true.
        /// At this point Quests.json is fully loaded, so any persisted quest
        /// will have its Instance set. Completes stale quests that the early
        /// ReconcileQuest missed because the Instance was still null.
        /// </summary>
        private void ReconcileQuestsPostLoad()
        {
            int effectiveStage = _crmTier >= 1 ? 3 : _introCompleted ? 2 : _questTriggered ? 1 : 0;

            if (StaticIntroQuest.Instance != null && effectiveStage > StaticIntroQuest.Instance.Stage)
            {
                if (effectiveStage >= 2 && StaticIntroQuest.Instance.Stage < 2)
                    StaticIntroQuest.Instance.CompleteObj1();
                if (effectiveStage >= 3 && StaticIntroQuest.Instance.Stage < 3)
                    StaticIntroQuest.Instance.CompleteObj2();
                OTCLog.Msg(OTCLog.Systems.NPC, $"Post-load: advanced intro quest to stage {StaticIntroQuest.Instance.Stage}");
            }

            if (_crmTier >= 2 && StaticUpgrade1Quest.Instance != null && StaticUpgrade1Quest.Instance.Stage < 1)
            {
                StaticUpgrade1Quest.Instance.CompleteObj1();
                OTCLog.Msg(OTCLog.Systems.NPC, "Post-load: completed upgrade1 quest");
            }

            if (_crmTier >= 3 && StaticUpgrade2Quest.Instance != null && StaticUpgrade2Quest.Instance.Stage < 1)
            {
                StaticUpgrade2Quest.Instance.CompleteObj1();
                OTCLog.Msg(OTCLog.Systems.NPC, "Post-load: completed upgrade2 quest");
            }
        }

        /// <summary>
        /// Called every frame from Core.OnLateUpdate(). Throttled internally.
        /// Polls ATM deposits and keeps Static's dialogue fresh.
        /// </summary>
        public void Tick()
        {
            // Safety net: reconcile quest state after everything is loaded.
            // If ReconcileQuest creates a quest, it returns true and we re-run
            // next frame so Unity's Start() has initialized entry components.
            if (!_questReconciled && _questTriggered)
            {
                if (!ReconcileQuest())
                    _questReconciled = true;
            }

            // Second pass: after the game finishes loading Quests.json, any
            // persisted quest Instance is now set. Advance stale quests that
            // ReconcileQuest skipped because Instance was still null.
            if (!_postLoadReconciled && _questTriggered)
            {
                try
                {
                    var lm = ScheduleOne.Persistence.LoadManager.Instance;
                    if (lm != null && lm.IsGameLoaded)
                    {
                        _postLoadReconciled = true;
                        ReconcileQuestsPostLoad();
                    }
                }
                catch { _postLoadReconciled = true; }
            }

            // Detect dialogue closing edge — forces a rebuild so weed count,
            // bank balance, and other dynamic data is fresh on next interaction.
            bool inDialogue = StaticNPC.Instance != null && StaticNPC.Instance.IsInDialogue;
            if (_wasInDialogue && !inDialogue)
                _dialogueStale = true;
            _wasInDialogue = inDialogue;

            // Re-publish game state after save/load so the client receives
            // the correct static fields (initial push may have missed them).
            if (_needsStatePublish && NetworkHelper.IsHost)
            {
                _needsStatePublish = false;
                ConfigSyncData.Instance?.PublishGameState();
            }

            if (_dialogueStale && StaticNPC.Instance != null
                && StaticNPC.Instance.DialogueReady && !inDialogue)
            {
                _dialogueStale = false;
                try { StaticNPC.Instance.RefreshDialogue(); }
                catch (Exception) { }
            }

            if (++_tickCounter < TICK_INTERVAL) return;
            _tickCounter = 0;

            // Deferred position fix: runs once after TICK_INTERVAL (~5s) to
            // ensure the NavMeshAgent is fully initialized before warping.
            if (NetworkHelper.IsHost && !_positionFixed && StaticNPC.Instance != null)
            {
                _positionFixed = true;
                try { StaticNPC.Instance.WarpToSpawn(); }
                catch (Exception) { }
            }

            // Keep dialogue fresh once triggered
            try
            {
                if (StaticNPC.Instance != null && StaticNPC.Instance.DialogueReady)
                    StaticNPC.Instance.RefreshDialogue();
            }
            catch (Exception)
            {
                // NPC's underlying GameObject was destroyed (e.g. save reload).
                // Silently ignore — OnDestroyed will clear the stale Instance,
                // and OnCreated will reinitialize on next spawn.
            }
        }

        public void CreateOrResumeQuest()
        {
            if (_questCreated) return;
            _questCreated = true;

            try
            {
                if (StaticIntroQuest.Instance != null) return;

                var quest = (StaticIntroQuest)QuestManager.CreateQuest<StaticIntroQuest>();
                if (quest != null)
                {
                    quest.Initialize();
                    quest.StartQuest();
                }
                else
                {
                    OTCLog.Error(OTCLog.Systems.NPC, "QuestManager.CreateQuest<StaticIntroQuest> returned null.");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"CreateOrResumeQuest failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by ConfigSyncData when the client receives game state from the host.
        /// Syncs all flags, advances quest objectives, and refreshes dialogue.
        /// </summary>
        public void ApplyHostState(
            bool questTriggered = false,
            bool introCompleted = false,
            int crmTier = -1,
            bool? saasActive = null,
            bool? upgradeAvailable = null,
            int saasNextPaymentDay = -1,
            int dayPassCount = -1,
            bool? tier1MoneyPaid = null,
            bool? tier1ProductDelivered = null,
            bool? upgradeAccepted = null,
            bool? upgradeMoneyPaid = null,
            bool? upgradeProductDelivered = null)
        {
            bool changed = false;

            // Determine if the intro quest is already fully completed in the
            // incoming state so we can skip quest creation / objective noise.
            bool questAlreadyDone = crmTier >= 1;

            if (questTriggered && !_questTriggered)
            {
                _questTriggered = true;
                if (!questAlreadyDone)
                    CreateOrResumeQuest();
                changed = true;
            }

            if (introCompleted && !_introCompleted)
            {
                _introCompleted = true;
                if (!questAlreadyDone)
                {
                    try { StaticIntroQuest.Instance?.CompleteObj1(); }
                    catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Client CompleteObj1 failed: {ex.Message}"); }
                }
                changed = true;
            }

            if (crmTier >= 0 && crmTier != _crmTier)
            {
                int previousTier = _crmTier;
                _crmTier = crmTier;

                // Only advance quest objectives for live tier transitions,
                // not when catching up to an already-completed state.
                if (previousTier == 0 && crmTier == 1)
                {
                    try { StaticIntroQuest.Instance?.CompleteObj2(); }
                    catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Client CompleteObj2 failed: {ex.Message}"); }
                }
                if (previousTier == 1 && crmTier == 2)
                {
                    try { StaticUpgrade1Quest.Instance?.CompleteObj1(); }
                    catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Client Upgrade1 CompleteObj1 failed: {ex.Message}"); }
                }
                if (previousTier == 2 && crmTier == 3)
                {
                    try { StaticUpgrade2Quest.Instance?.CompleteObj1(); }
                    catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Client Upgrade2 CompleteObj1 failed: {ex.Message}"); }
                }

                changed = true;
            }

            if (saasActive.HasValue && saasActive.Value != _saasActive)
            {
                _saasActive = saasActive.Value;
                changed = true;
            }

            if (upgradeAvailable.HasValue && upgradeAvailable.Value != _upgradeAvailable)
            {
                _upgradeAvailable = upgradeAvailable.Value;
                changed = true;
            }

            if (upgradeAccepted.HasValue && upgradeAccepted.Value != _upgradeAccepted)
            {
                _upgradeAccepted = upgradeAccepted.Value;
                if (_upgradeAccepted)
                    CreateUpgradeQuest();
                changed = true;
            }

            if (saasNextPaymentDay >= 0 && saasNextPaymentDay != _saasNextPaymentDay)
            {
                _saasNextPaymentDay = saasNextPaymentDay;
                changed = true;
            }

            if (dayPassCount >= 0 && dayPassCount != _dayPassCount)
            {
                _dayPassCount = dayPassCount;
                changed = true;
            }

            if (tier1MoneyPaid.HasValue && tier1MoneyPaid.Value != _tier1MoneyPaid)
            {
                _tier1MoneyPaid = tier1MoneyPaid.Value;
                changed = true;
            }
            if (tier1ProductDelivered.HasValue && tier1ProductDelivered.Value != _tier1ProductDelivered)
            {
                _tier1ProductDelivered = tier1ProductDelivered.Value;
                changed = true;
            }
            if (upgradeMoneyPaid.HasValue && upgradeMoneyPaid.Value != _upgradeMoneyPaid)
            {
                _upgradeMoneyPaid = upgradeMoneyPaid.Value;
                changed = true;
            }
            if (upgradeProductDelivered.HasValue && upgradeProductDelivered.Value != _upgradeProductDelivered)
            {
                _upgradeProductDelivered = upgradeProductDelivered.Value;
                changed = true;
            }

            if (changed && StaticNPC.Instance != null && StaticNPC.Instance.DialogueReady)
                StaticNPC.Instance.RefreshDialogue();
        }

        /// <summary>
        /// Called by StaticNPC.OnCreated() to retry a deferred intro text once Static exists.
        /// </summary>
        public void OnStaticSpawned()
        {
            // Reset so the next TICK_INTERVAL fires a position fix
            // (deferred ~5s to let NavMeshAgent fully initialize).
            if (NetworkHelper.IsHost)
                _positionFixed = false;

            if (!_needsIntroText) return;
            _needsIntroText = false;
            TrySendIntroText();
        }

        private void TrySendIntroText()
        {
            if (!NetworkHelper.IsHost) return;

            try
            {
                var staticNpc = S1API.Entities.NPC.All?.FirstOrDefault(n => n.ID == "static_casino_fixer");
                if (staticNpc != null)
                {
                    staticNpc.SendTextMessage("y0ur comm channels are unencrypted. that's a liability.\n\nopen the 0TC app. i left you a message.\n\n\u2014 ST4T1C");
                    _needsIntroText = false;
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.NPC, "Static NPC not found \u2014 deferring intro text until spawn.");
                    _needsIntroText = true;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"TrySendIntroText failed: {ex.Message}");
                _needsIntroText = true;
            }
        }

        public void OnEarlyVisitSeen()
        {
            _earlyVisitSeen = true;
        }

        /// <summary>
        /// Called when player accepts intro dialogue ("Deal.").
        /// Marks intro complete, completes quest objective, and sends the first purchase offer via text.
        /// </summary>
        public void OnIntroCompleted()
        {
            if (_introCompleted) return;
            _introCompleted = true;

            try
            {
                StaticIntroQuest.Instance?.CompleteObj1();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"OnIntroCompleted quest completion failed: {ex.Message}");
            }

            // Thread is rebuilt from state flags - no manual embed add needed.
            _dialogueStale = true;
            ConfigSyncData.Instance?.PublishGameState();
        }

        /// <summary>
        /// Called when the player accepts the upgrade offer via the "What's the catch?" response.
        /// </summary>
        public void AcceptUpgrade()
        {
            if (_upgradeAccepted || !_upgradeAvailable) return;
            _upgradeAccepted = true;
            CreateUpgradeQuest();
            _dialogueStale = true;
            ConfigSyncData.Instance?.PublishGameState();
        }

        /// <summary>
        /// Called when the player purchases the initial software package ($3000 + 20g weed).
        /// Activates tier 1 subscription and completes the intro quest.
        /// </summary>
        public void PurchaseInitial()
        {
            if (_crmTier >= 1) return;
            _crmTier = 1;
            _saasActive = true;
            _saasNextPaymentDay = _dayPassCount + Config.SaasCycleDays.Value;

            try
            {
                StaticIntroQuest.Instance?.CompleteObj3();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"PurchaseInitial quest completion failed: {ex.Message}");
            }

            // Thread is rebuilt from state flags - embed status set automatically.
            _dialogueStale = true;

            // Offer the next upgrade immediately (player must accept before paying)
            if (_crmTier < 3 && !_upgradeAvailable)
            {
                _upgradeAvailable = true;
                _upgradeAccepted = false;
                ActivateThread("upgrade1");
                SendUpgradeOfferText();
            }

            ConfigSyncData.Instance?.PublishGameState();
        }

        /// <summary>
        /// Called when the player purchases an upgrade.
        /// Advances CRM tier, clears the upgrade-available flag, and completes the upgrade quest.
        /// </summary>
        public void PurchaseUpgrade()
        {
            if (_crmTier >= 3) return;

            int previousTier = _crmTier;
            _crmTier++;
            _upgradeAvailable = false;
            _upgradeAccepted = false;

            try
            {
                if (previousTier == 1)
                    StaticUpgrade1Quest.Instance?.CompleteObj1();
                else if (previousTier == 2)
                    StaticUpgrade2Quest.Instance?.CompleteObj1();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"PurchaseUpgrade quest completion failed: {ex.Message}");
            }

            // Thread is rebuilt from state flags - embed status set automatically.
            _dialogueStale = true;

            // Offer the next upgrade immediately (player must accept before paying)
            if (_crmTier < 3 && !_upgradeAvailable)
            {
                _upgradeAvailable = true;
                _upgradeAccepted = false;
                ActivateThread("upgrade2");
                SendUpgradeOfferText();
            }

            ConfigSyncData.Instance?.PublishGameState();
        }

        // ── Split purchase: tier 1 (money + weed) ────────────────────────

        /// <summary>
        /// Marks the money portion of the tier 1 purchase as paid.
        /// If product was already delivered, completes the purchase automatically.
        /// </summary>
        public void PayTier1Money()
        {
            if (_tier1MoneyPaid || _crmTier >= 1) return;
            _tier1MoneyPaid = true;

            try { StaticIntroQuest.Instance?.CompletePay(); }
            catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"CompletePay failed: {ex.Message}"); }

            ConfigSyncData.Instance?.PublishGameState();

            if (_tier1ProductDelivered)
                CompleteTier1Purchase();
        }

        /// <summary>
        /// Marks the product portion of the tier 1 purchase as delivered.
        /// If money was already paid, completes the purchase automatically.
        /// </summary>
        public void ConfirmTier1Product()
        {
            if (_tier1ProductDelivered || _crmTier >= 1) return;
            _tier1ProductDelivered = true;

            try { StaticIntroQuest.Instance?.CompleteDropOff(); }
            catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"CompleteDropOff failed: {ex.Message}"); }

            ConfigSyncData.Instance?.PublishGameState();

            if (_tier1MoneyPaid)
                CompleteTier1Purchase();
        }

        private void CompleteTier1Purchase()
        {
            PurchaseInitial();
        }

        // ── Split purchase: upgrade (money + meth) ───────────────────────

        /// <summary>
        /// Marks the money portion of the upgrade purchase as paid.
        /// If product was already delivered, completes the upgrade automatically.
        /// </summary>
        public void PayUpgradeMoney()
        {
            if (_upgradeMoneyPaid || _crmTier >= 3 || !_upgradeAvailable) return;
            _upgradeMoneyPaid = true;

            try
            {
                if (_crmTier == 1) StaticUpgrade1Quest.Instance?.CompletePay();
                else if (_crmTier == 2) StaticUpgrade2Quest.Instance?.CompletePay();
            }
            catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Upgrade CompletePay failed: {ex.Message}"); }

            ConfigSyncData.Instance?.PublishGameState();

            if (_upgradeProductDelivered)
                CompleteUpgradePurchase();
        }

        /// <summary>
        /// Marks the product portion of the upgrade purchase as delivered.
        /// If money was already paid, completes the upgrade automatically.
        /// </summary>
        public void ConfirmUpgradeProduct()
        {
            if (_upgradeProductDelivered || _crmTier >= 3 || !_upgradeAvailable) return;
            _upgradeProductDelivered = true;

            try
            {
                if (_crmTier == 1) StaticUpgrade1Quest.Instance?.CompleteDropOff();
                else if (_crmTier == 2) StaticUpgrade2Quest.Instance?.CompleteDropOff();
            }
            catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Upgrade CompleteDropOff failed: {ex.Message}"); }

            ConfigSyncData.Instance?.PublishGameState();

            if (_upgradeMoneyPaid)
                CompleteUpgradePurchase();
        }

        private void CompleteUpgradePurchase()
        {
            PurchaseUpgrade();
            // Reset flags for the next tier's upgrade cycle
            _upgradeMoneyPaid = false;
            _upgradeProductDelivered = false;
        }

        public bool ReactivateSubscription()
        {
            try
            {
                if (Money.GetOnlineBalance() < Config.SaasWeeklyCost.Value)
                    return false;

                Money.CreateOnlineTransaction("OTC Back-Rent", -Config.SaasWeeklyCost.Value, 1f, "Static Services");
                _saasActive = true;
                _saasNextPaymentDay = _dayPassCount + Config.SaasCycleDays.Value;
                _dialogueStale = true;
                ConfigSyncData.Instance?.PublishGameState();
                return true;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"ReactivateSubscription failed: {ex.Message}");
                return false;
            }
        }

        public void CancelSubscription()
        {
            _saasActive = false;
            _upgradeAvailable = false;
            _dialogueStale = true;
            ConfigSyncData.Instance?.PublishGameState();
        }

        /// <summary>
        /// Called on the host when a client sends a quest action via P2P.
        /// Applies state-only transitions (no text messages, no player-local
        /// money/inventory ops — those already ran on the client).
        /// </summary>
        public void HandleRemoteAction(string action)
        {
            switch (action)
            {
                case "STATIC_INTRO_COMPLETED":
                    _introCompleted = true;
                    try { StaticIntroQuest.Instance?.CompleteObj1(); }
                    catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Remote CompleteObj1 failed: {ex.Message}"); }
                    break;

                case "STATIC_PURCHASE_INITIAL":
                    _crmTier = 1;
                    _saasActive = true;
                    _saasNextPaymentDay = _dayPassCount + Config.SaasCycleDays.Value;
                    try { StaticIntroQuest.Instance?.CompleteObj2(); }
                    catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Remote CompleteObj2 failed: {ex.Message}"); }
                    break;

                case "STATIC_PURCHASE_UPGRADE":
                    if (_crmTier >= 3) return;
                    int previousTier = _crmTier;
                    _crmTier++;
                    _upgradeAvailable = false;
                    try
                    {
                        if (previousTier == 1)
                            StaticUpgrade1Quest.Instance?.CompleteObj1();
                        else if (previousTier == 2)
                            StaticUpgrade2Quest.Instance?.CompleteObj1();
                    }
                    catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Remote PurchaseUpgrade quest failed: {ex.Message}"); }
                    break;

                case "STATIC_ACCEPT_UPGRADE":
                    _upgradeAccepted = true;
                    CreateUpgradeQuest();
                    break;

                case "STATIC_PAY_TIER1":
                    PayTier1Money();
                    break;

                case "STATIC_PAY_UPGRADE":
                    PayUpgradeMoney();
                    break;

                case "STATIC_REACTIVATE":
                    _saasActive = true;
                    _saasNextPaymentDay = _dayPassCount + Config.SaasCycleDays.Value;
                    break;

                case "STATIC_CANCEL":
                    _saasActive = false;
                    _upgradeAvailable = false;
                    break;

                default:
                    OTCLog.Warning(OTCLog.Systems.NPC, $"StaticSaveData: unknown remote action '{action}'");
                    return;
            }

            ConfigSyncData.Instance?.PublishGameState();
        }

        /// <summary>
        /// Called every game tick. Triggers intro quest at 1:00 PM
        /// and lists the shack once the player has $5k in bank.
        /// </summary>
        private void OnTimeTick()
        {
            if (!NetworkHelper.IsHost) return;

            // 1:00 PM CRM intro trigger - Static texts about encrypted comms
            if (!_questTriggered && TimeManager.CurrentTime >= 1300)
            {
                _questTriggered = true;
                ActivateThread("crm");
                CreateOrResumeQuest();
                TrySendIntroText();
                StaticThreadSaveData.Instance?.ReconcileHostThread();
                ConfigSyncData.Instance?.PublishGameState();
            }

            // Shack listing - independent of CRM quest, just needs 1 PM + enough money
            if (TimeManager.CurrentTime >= 1300
                && PropertySaveData.Instance != null
                && PropertySaveData.Instance.GetProperty(PropertySaveData.ShackId) == null
                && Money.GetOnlineBalance() >= Config.ShackPurchasePrice.Value)
            {
                ActivateThread("shack");
                PropertySaveData.Instance.EnsureShackListing();
                StaticThreadSaveData.Instance?.ReconcileHostThread();
                ConfigSyncData.Instance?.PublishGameState();
            }

            // Dead drop product detection - auto-consume when enough product is dropped off
            CheckDeadDropProduct();
        }

        /// <summary>
        /// Called every OnDayPass (each sleep). Increments our own day counter
        /// and checks whether the subscription payment is due.
        /// </summary>
        private void OnDayPass()
        {
            if (!NetworkHelper.IsHost) return;

            // Reset so the next TICK_INTERVAL fires a position fix after sleep.
            _positionFixed = false;

            _dayPassCount++;
            CheckSubscriptionStatus();
        }

        public void CheckSubscriptionStatus()
        {
            if (!_saasActive) return;

            if (_dayPassCount >= _saasNextPaymentDay)
            {
                try
                {
                    if (Money.GetOnlineBalance() >= Config.SaasWeeklyCost.Value)
                    {
                        Money.CreateOnlineTransaction("OTC Server Rent", -Config.SaasWeeklyCost.Value, 1f, "Static Services");
                        _saasNextPaymentDay += Config.SaasCycleDays.Value;
                        SendOtcToast("Server rent cleared. All nodes online.");

                        if (_crmTier < 3 && !_upgradeAvailable)
                        {
                            _upgradeAvailable = true;
                            _upgradeAccepted = false;
                            ActivateThread(_crmTier == 1 ? "upgrade1" : "upgrade2");
                            SendUpgradeOfferText();
                        }
                    }
                    else
                    {
                        _saasActive = false;
                        StaticThreadSaveData.Instance?.AddOrUpdateMessage("sub_failed",
                            "Payment failed. Service suspended. Come see me to restore it.");
                    }

                    ConfigSyncData.Instance?.PublishGameState();
                }
                catch (Exception ex)
                {
                    OTCLog.Error(OTCLog.Systems.NPC, $"CheckSubscriptionStatus failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Checks the casino dead drop for required product and auto-consumes it.
        /// </summary>
        private void CheckDeadDropProduct()
        {
            try
            {
                // Tier 1: packaged weed
                if (_introCompleted && _crmTier == 0 && !_tier1ProductDelivered)
                {
                    int weedGrams = Logic.Placement.CasinoDeadDrop.CountPackagedWeed();
                    if (weedGrams >= Config.StaticTier1WeedGrams.Value)
                    {
                        Logic.Placement.CasinoDeadDrop.ClearWeed(Config.StaticTier1WeedGrams.Value);
                        ConfirmTier1Product();
                        SendOtcToast("Product received. Package verified.");
                    }
                }

                // Upgrade: packaged meth (or premium meth for tier 3)
                if (_upgradeAvailable && !_upgradeProductDelivered)
                {
                    int targetTier = _crmTier + 1;
                    if (targetTier == 2)
                    {
                        int methGrams = Logic.Placement.CasinoDeadDrop.CountPackagedMeth();
                        if (methGrams >= Config.StaticTier2MethGrams.Value)
                        {
                            Logic.Placement.CasinoDeadDrop.ClearMeth(Config.StaticTier2MethGrams.Value);
                            ConfirmUpgradeProduct();
                            SendOtcToast("Product received. Package verified.");
                        }
                    }
                    else if (targetTier == 3)
                    {
#if IL2CPP
                        int methGrams = Logic.Placement.CasinoDeadDrop.CountPackagedMeth(Il2CppScheduleOne.ItemFramework.EQuality.Premium);
#else
                        int methGrams = Logic.Placement.CasinoDeadDrop.CountPackagedMeth(ScheduleOne.ItemFramework.EQuality.Premium);
#endif
                        if (methGrams >= Config.StaticTier3PremiumMethGrams.Value)
                        {
#if IL2CPP
                            Logic.Placement.CasinoDeadDrop.ClearMeth(Config.StaticTier3PremiumMethGrams.Value, Il2CppScheduleOne.ItemFramework.EQuality.Premium);
#else
                            Logic.Placement.CasinoDeadDrop.ClearMeth(Config.StaticTier3PremiumMethGrams.Value, ScheduleOne.ItemFramework.EQuality.Premium);
#endif
                            ConfirmUpgradeProduct();
                            SendOtcToast("Product received. Package verified.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"CheckDeadDropProduct failed: {ex.Message}");
            }
        }

        private void CreateUpgradeQuest()
        {
            try
            {
                if (_crmTier == 1)
                {
                    if (StaticUpgrade1Quest.Instance != null) return;

                    var quest = (StaticUpgrade1Quest)QuestManager.CreateQuest<StaticUpgrade1Quest>();
                    if (quest != null)
                    {
                        quest.Initialize();
                        quest.StartQuest();
                    }
                    else
                    {
                        OTCLog.Error(OTCLog.Systems.NPC, "CreateQuest<StaticUpgrade1Quest> returned null.");
                    }
                }
                else if (_crmTier == 2)
                {
                    if (StaticUpgrade2Quest.Instance != null) return;

                    var quest = (StaticUpgrade2Quest)QuestManager.CreateQuest<StaticUpgrade2Quest>();
                    if (quest != null)
                    {
                        quest.Initialize();
                        quest.StartQuest();
                    }
                    else
                    {
                        OTCLog.Error(OTCLog.Systems.NPC, "CreateQuest<StaticUpgrade2Quest> returned null.");
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"CreateUpgradeQuest failed: {ex.Message}");
            }
        }

        private void SendUpgradeOfferText()
        {
            // Thread is rebuilt from state flags - publish triggers reconstruction.
            ConfigSyncData.Instance?.PublishGameState();
        }

        private void SendOtcToast(string subtitle)
        {
            try
            {
                Singleton<NotificationsManager>.Instance?.SendNotification(
                    "OTC App", subtitle,
                    Apps.CustomersApp.GetAppIcon(), 5f, true);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"SendStaticText failed: {ex.Message}");
            }
        }
    }
}
