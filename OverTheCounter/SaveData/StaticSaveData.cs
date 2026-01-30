using MelonLoader;
using OverTheCounter.NPCs;
using OverTheCounter.Quests;
using S1API.GameTime;
using S1API.Internal.Abstraction;
using S1API.Money;
using S1API.Saveables;
using S1API.Quests;
using System;
using System.Linq;

namespace OverTheCounter.SaveData
{
    public class StaticSaveData : Saveable
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("StaticSaveData");

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

        private const float SAAS_WEEKLY_COST = 1000f;
        private const int SAAS_CYCLE_DAYS = 7;

        private int _tickCounter;
        private const int TICK_INTERVAL = 300;

        // Runtime-only: guards against double quest creation per session.
        private bool _questCreated;

        // Runtime-only: intro text deferred until Static NPC spawns.
        private bool _needsIntroText;

        // Runtime-only: prevents double subscription to OnDayPass.
        private static bool _dayPassSubscribed;

        public static StaticSaveData Instance { get; private set; }

        public bool IntroCompleted => _introCompleted;
        public bool QuestTriggered => _questTriggered;
        public int CrmTier => _crmTier;
        public bool SaasActive => _saasActive;
        public int SaasNextPaymentDay => _saasNextPaymentDay;
        public int DayPassCount => _dayPassCount;
        public bool UpgradeAvailable => _upgradeAvailable;

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
                _questCreated = true;
        }

        /// <summary>
        /// Called every frame from Core.OnLateUpdate(). Throttled internally.
        /// Polls ATM deposits and keeps Static's dialogue fresh.
        /// </summary>
        public void Tick()
        {
            if (++_tickCounter < TICK_INTERVAL) return;
            _tickCounter = 0;

            // Check ATM trigger
            if (!_questTriggered)
            {
                try
                {
                    if (Il2CppScheduleOne.Money.ATM.WeeklyDepositSum >= 5000f)
                    {
                        _questTriggered = true;
                        TrySendIntroText();
                        CreateOrResumeQuest();
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
        /// Called by StaticNPC.OnCreated() to retry a deferred intro text once Static exists.
        /// </summary>
        public void OnStaticSpawned()
        {
            if (!_needsIntroText) return;
            _needsIntroText = false;
            TrySendIntroText();
        }

        private void TrySendIntroText()
        {
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

        /// <summary>
        /// Called when player accepts intro dialogue ("I'm listening").
        /// Marks intro complete, completes quest objective, and sends the first purchase offer via text.
        /// </summary>
        public void OnIntroCompleted()
        {
            _introCompleted = true;

            try
            {
                StaticIntroQuest.Instance?.CompleteObj1();
            }
            catch (Exception ex)
            {
                Logger.Error($"OnIntroCompleted quest completion failed: {ex.Message}");
            }

            SendStaticText("[0x7A3F] s0ftw4r3 p4ck4g3 r34dy. c0st: $3,000 + 20g w33d.\n\nbr1ng t0 c4s1n0.\n\n\u2014 ST4T1C_SYS");
        }

        /// <summary>
        /// Called when the player purchases the initial software package ($3000 + 20g weed).
        /// Activates tier 1 subscription and completes the intro quest.
        /// </summary>
        public void PurchaseInitial()
        {
            _crmTier = 1;
            _saasActive = true;
            _saasNextPaymentDay = _dayPassCount + SAAS_CYCLE_DAYS;

            try
            {
                StaticIntroQuest.Instance?.CompleteObj2();
            }
            catch (Exception ex)
            {
                Logger.Error($"PurchaseInitial quest completion failed: {ex.Message}");
            }
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
        }

        public bool ReactivateSubscription()
        {
            try
            {
                if (Money.GetOnlineBalance() < SAAS_WEEKLY_COST)
                    return false;

                Money.CreateOnlineTransaction("OTC Back-Rent", -SAAS_WEEKLY_COST, 1f, "Static Services");
                _saasActive = true;
                _saasNextPaymentDay = _dayPassCount + SAAS_CYCLE_DAYS;
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
        }

        /// <summary>
        /// Called every OnDayPass (each sleep). Increments our own day counter
        /// and checks whether the subscription payment is due.
        /// </summary>
        private void OnDayPass()
        {
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
                    if (Money.GetOnlineBalance() >= SAAS_WEEKLY_COST)
                    {
                        Money.CreateOnlineTransaction("OTC Server Rent", -SAAS_WEEKLY_COST, 1f, "Static Services");
                        _saasNextPaymentDay += SAAS_CYCLE_DAYS;
                        SendStaticText("Server rent paid. We're live.");

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
                        SendStaticText("Payment failed. Service suspended.");
                    }
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
                SendStaticText("[0x9FA1] pr3m1um t13r unl0ck3d. c0st: $6,000 + 5g m3th.\n\ns4m3 sp0t.\n\n\u2014 ST4T1C_SYS");
            }
            else if (_crmTier == 2)
            {
                SendStaticText("[0xB2D8] f1n4l upgr4d3. t13r-3 c0st: $12,000 + 10g pr3m1um m3th.\n\nl4st ch4nc3. full sc4l3.\n\n\u2014 ST4T1C_SYS");
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
