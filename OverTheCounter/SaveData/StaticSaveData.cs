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

namespace OverTheCounter.SaveData
{
    public class StaticSaveData : Saveable
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:StaticSaveData");

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

        [SaveableField("static_early_visit_seen")]
        private bool _earlyVisitSeen;


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
        public bool EarlyVisitSeen => _earlyVisitSeen;

        public StaticSaveData()
        {
            Instance = this;

            if (!_dayPassSubscribed)
            {
                TimeManager.OnDayPass += () => Instance?.OnDayPass();
                _dayPassSubscribed = true;
            }
        }

        protected override void OnLoaded()
        {
            Instance = this;

            if (_questTriggered)
            {
                _questCreated = true;
                if (NetworkHelper.IsHost)
                    _needsStatePublish = true;
            }

            ConfigSyncData.ApplyPendingGameState();
            ReconcileQuest();
        }

        /// <summary>
        /// Advances StaticIntroQuest to match this SaveData's effective stage.
        /// Covers the case where the quest loaded before this Saveable received host state.
        /// </summary>
        private void ReconcileQuest()
        {
            if (StaticIntroQuest.Instance == null) return;
            int effectiveStage = _crmTier >= 1 ? 3 : _introCompleted ? 2 : _questTriggered ? 1 : 0;
            if (effectiveStage <= StaticIntroQuest.Instance.Stage) return;

            if (effectiveStage >= 2 && StaticIntroQuest.Instance.Stage < 2)
                StaticIntroQuest.Instance.CompleteObj1();
            if (effectiveStage >= 3 && StaticIntroQuest.Instance.Stage < 3)
                StaticIntroQuest.Instance.CompleteObj2();
        }

        /// <summary>
        /// Called every frame from Core.OnLateUpdate(). Throttled internally.
        /// Polls ATM deposits and keeps Static's dialogue fresh.
        /// </summary>
        public void Tick()
        {
            // Safety net: reconcile quest once after everything is loaded.
            if (!_questReconciled && _questTriggered
                && StaticIntroQuest.Instance != null)
            {
                _questReconciled = true;
                int effectiveStage = _crmTier >= 1 ? 3 : _introCompleted ? 2 : 1;
                if (StaticIntroQuest.Instance.Stage < effectiveStage)
                {
                    if (Config.VerboseLogging.Value)
                        Logger.Msg($"Tick reconciliation: quest stage {StaticIntroQuest.Instance.Stage} → {effectiveStage}");
                    ReconcileQuest();
                }
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

            // Check ATM trigger (either peer can hit the deposit threshold)
            if (!_questTriggered)
            {
                try
                {
                    if (Il2CppScheduleOne.Money.ATM.WeeklyDepositSum >= Config.AtmDepositTrigger.Value)
                    {
                        _questTriggered = true;
                        CreateOrResumeQuest();

                        if (NetworkHelper.IsHost)
                        {
                            TrySendIntroText();
                            ConfigSyncData.Instance?.PublishGameState();
                        }
                        else
                        {
                            ConfigSyncData.SendQuestAction("STATIC_ATM_TRIGGERED");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"ATM check failed: {ex.Message}");
                }
                return;
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
                    Logger.Error("QuestManager.CreateQuest<StaticIntroQuest> returned null.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"CreateOrResumeQuest failed: {ex.Message}");
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
            int dayPassCount = -1)
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
                    catch (Exception ex) { Logger.Warning($"Client CompleteObj1 failed: {ex.Message}"); }
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
                    catch (Exception ex) { Logger.Warning($"Client CompleteObj2 failed: {ex.Message}"); }
                }
                if (previousTier == 1 && crmTier == 2)
                {
                    try { StaticUpgrade1Quest.Instance?.CompleteObj1(); }
                    catch (Exception ex) { Logger.Warning($"Client Upgrade1 CompleteObj1 failed: {ex.Message}"); }
                }
                if (previousTier == 2 && crmTier == 3)
                {
                    try { StaticUpgrade2Quest.Instance?.CompleteObj1(); }
                    catch (Exception ex) { Logger.Warning($"Client Upgrade2 CompleteObj1 failed: {ex.Message}"); }
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
                if (_upgradeAvailable)
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
                    staticNpc.SendTextMessage("[0x4E72] d1g1t4l f00tpr1nt fl4gg3d. unencrypt3d ch4nn3l. vuln: CR1T1C4L.\n\nc4s1n0. t0p fl00r. 4ft3r 4. c0m3 4l0n3.\n\n\u2014 ST4T1C");
                    _needsIntroText = false;
                }
                else
                {
                    Logger.Warning("Static NPC not found \u2014 deferring intro text until spawn.");
                    _needsIntroText = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"TrySendIntroText failed: {ex.Message}");
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
                Logger.Error($"OnIntroCompleted quest completion failed: {ex.Message}");
            }

            SendStaticText($"[0x7A3F] s0ftw4r3 p4ck4g3 r34dy. c0st: ${Config.StaticTier1BankCost.Value:N0} + {Config.StaticTier1WeedGrams.Value}g w33d.\n\nbr1ng t0 c4s1n0.\n\n\u2014 ST4T1C_SYS");
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
                StaticIntroQuest.Instance?.CompleteObj2();
            }
            catch (Exception ex)
            {
                Logger.Error($"PurchaseInitial quest completion failed: {ex.Message}");
            }

            _dialogueStale = true;
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

            try
            {
                if (previousTier == 1)
                    StaticUpgrade1Quest.Instance?.CompleteObj1();
                else if (previousTier == 2)
                    StaticUpgrade2Quest.Instance?.CompleteObj1();
            }
            catch (Exception ex)
            {
                Logger.Error($"PurchaseUpgrade quest completion failed: {ex.Message}");
            }

            _dialogueStale = true;
            ConfigSyncData.Instance?.PublishGameState();
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
                Logger.Error($"ReactivateSubscription failed: {ex.Message}");
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
                case "STATIC_ATM_TRIGGERED":
                    if (!_questTriggered)
                    {
                        _questTriggered = true;
                        TrySendIntroText();
                        CreateOrResumeQuest();
                    }
                    break;

                case "STATIC_INTRO_COMPLETED":
                    _introCompleted = true;
                    try { StaticIntroQuest.Instance?.CompleteObj1(); }
                    catch (Exception ex) { Logger.Warning($"Remote CompleteObj1 failed: {ex.Message}"); }
                    break;

                case "STATIC_PURCHASE_INITIAL":
                    _crmTier = 1;
                    _saasActive = true;
                    _saasNextPaymentDay = _dayPassCount + Config.SaasCycleDays.Value;
                    try { StaticIntroQuest.Instance?.CompleteObj2(); }
                    catch (Exception ex) { Logger.Warning($"Remote CompleteObj2 failed: {ex.Message}"); }
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
                    catch (Exception ex) { Logger.Warning($"Remote PurchaseUpgrade quest failed: {ex.Message}"); }
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
                    Logger.Warning($"StaticSaveData: unknown remote action '{action}'");
                    return;
            }

            ConfigSyncData.Instance?.PublishGameState();
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
                        SendStaticText("[0x52E1] server r3nt cleared. nod3s onl1ne.\n\n\u2014 ST4T1C_SYS");

                        if (_crmTier < 3 && !_upgradeAvailable)
                        {
                            _upgradeAvailable = true;
                            SendUpgradeOfferText();
                            CreateUpgradeQuest();
                        }
                    }
                    else
                    {
                        _saasActive = false;
                        SendStaticText("[0xDEAD] paym3nt fa1led. serv1ce suspended. r3store in p3rson.\n\n\u2014 ST4T1C_SYS");
                    }

                    ConfigSyncData.Instance?.PublishGameState();
                }
                catch (Exception ex)
                {
                    Logger.Error($"CheckSubscriptionStatus failed: {ex.Message}");
                }
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
                        Logger.Error("CreateQuest<StaticUpgrade1Quest> returned null.");
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
                        Logger.Error("CreateQuest<StaticUpgrade2Quest> returned null.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"CreateUpgradeQuest failed: {ex.Message}");
            }
        }

        private void SendUpgradeOfferText()
        {
            if (_crmTier == 1)
            {
                SendStaticText($"[0x9FA1] pr1v4t3 s3rv3r r34dy. c0st: ${Config.StaticTier2BankCost.Value:N0} + {Config.StaticTier2MethGrams.Value}g m3th.\nfull r3g10n c0v3r4g3. s4m3 sp0t.\n\n\u2014 ST4T1C_SYS");
            }
            else if (_crmTier == 2)
            {
                SendStaticText($"[0xB2D8] 3nt3rpr1s3 t13r. GPS tr4ck1ng. c0st: ${Config.StaticTier3BankCost.Value:N0} + {Config.StaticTier3PremiumMethGrams.Value}g pr3m1um m3th.\nl4st upgr4d3. c4s1n0.\n\n\u2014 ST4T1C_SYS");
            }
        }

        private void SendStaticText(string message)
        {
            try
            {
                var staticNpc = S1API.Entities.NPC.All?.FirstOrDefault(n => n.ID == "static_casino_fixer");
                staticNpc?.SendTextMessage(message);
            }
            catch (Exception ex)
            {
                Logger.Error($"SendStaticText failed: {ex.Message}");
            }
        }
    }
}
