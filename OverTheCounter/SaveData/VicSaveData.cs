using MelonLoader;
using OverTheCounter.NPCs;
using OverTheCounter.Quests;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using S1API.GameTime;
using S1API.Quests;
using S1API.Quests.Constants;
using S1API.Quests.Identifiers;
using System;
using System.Linq;

namespace OverTheCounter.SaveData
{
    public class VicSaveData : Saveable
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("VicSaveData");

        [SaveableField("vic_unlocked")]
        private bool _unlocked;

        [SaveableField("vic_has_been_texted")]
        private bool _hasBeenTexted;

        [SaveableField("vic_trust_level")]
        private int _trustLevel;

        /// <summary>
        /// The in-game day when the Clean Cash quest was first detected.
        /// -1 means not yet detected. Set to 0 once detected; intro fires on next sleep.
        /// </summary>
        [SaveableField("vic_trigger_pending_day")]
        private int _triggerPendingDay = -1;

        [SaveableField("vic_last_deposit_day")]
        private int _lastDepositDay = -1;

        [SaveableField("vic_tier2_intro_shown")]
        private bool _tier2IntroShown;

        // Runtime-only: intro text deferred until Vic spawns.
        private bool _needsIntroText;

        // Runtime-only: guards against double quest creation per session.
        private bool _questCreated;

        // Runtime-only: defers trigger from OnSleepEnd to next Tick()
        // because NPCs aren't accessible during the sleep transition.
        private bool _fireOnNextTick;

        // Runtime-only: ensures we do the stale-data check exactly once per session.
        private bool _staleCheckDone;

        private int _tickCounter;
        private const int TICK_INTERVAL = 300; // ~5 seconds at 60fps

        private static bool _sleepEndSubscribed;

        public static VicSaveData Instance { get; private set; }

        public bool Unlocked => _unlocked;
        public int TrustLevel => _trustLevel;
        public int LastDepositDay => _lastDepositDay;
        public bool Tier2IntroShown => _tier2IntroShown;

        public bool HasBeenTexted
        {
            get => _hasBeenTexted;
            set
            {
                if (_hasBeenTexted) return;
                if (!value) return;

                _hasBeenTexted = true;
                TrySendIntroText();
                CreateOrResumeQuest();
                if (VicNPC.Instance != null && VicNPC.Instance.DialogueReady)
                    VicNPC.Instance.RefreshDialogue();
            }
        }

        public VicSaveData()
        {
            Instance = this;

            if (!_sleepEndSubscribed)
            {
                TimeManager.OnSleepEnd += (_) => Instance?.OnPlayerWokeUp();
                _sleepEndSubscribed = true;
            }
        }

        protected override void OnLoaded()
        {
            Instance = this;

            if (_hasBeenTexted)
            {
                _questCreated = true;
                return;
            }

            if (_triggerPendingDay >= 0)
                return;

            // Clean Cash was active before this session but we never recorded
            // a pending day. Flag it so the next sleep triggers the intro.
            if (IsCleanCashQuestStarted())
                _triggerPendingDay = 0;
        }

        /// <summary>
        /// Called by VicNPC.OnCreated() to retry a deferred intro text once Vic exists.
        /// </summary>
        public void OnVicSpawned()
        {
            if (!_needsIntroText) return;
            _needsIntroText = false;
            TrySendIntroText();
        }

        private void OnPlayerWokeUp()
        {
            if (_hasBeenTexted) return;

            if (_triggerPendingDay >= 0 || IsCleanCashQuestStarted())
            {
                _triggerPendingDay = 0;
                _fireOnNextTick = true;
            }
        }

        /// <summary>
        /// Called every frame from Core.OnLateUpdate(). Throttled internally.
        /// Polls for the Clean Cash quest and keeps Vic's dialogue up to date.
        /// </summary>
        public void Tick()
        {
            // If HasBeenTexted was loaded as true but Clean Cash isn't actually
            // started, this is stale data carried over from a prior save. Reset.
            if (!_staleCheckDone && _hasBeenTexted)
            {
                _staleCheckDone = true;
                if (!IsCleanCashQuestStarted())
                {
                    _hasBeenTexted = false;
                    _triggerPendingDay = -1;
                    _questCreated = false;
                    _needsIntroText = false;
                    _fireOnNextTick = false;
                }
                return;
            }

            // Deferred trigger from OnSleepEnd.
            if (_fireOnNextTick)
            {
                _fireOnNextTick = false;
                if (!_hasBeenTexted)
                    HasBeenTexted = true;
                return;
            }

            if (++_tickCounter < TICK_INTERVAL) return;
            _tickCounter = 0;

            // Keep Vic's dialogue fresh so players see current values.
            if (_hasBeenTexted)
            {
                try
                {
                    if (VicNPC.Instance != null && VicNPC.Instance.DialogueReady)
                        VicNPC.Instance.RefreshDialogue();
                }
                catch (System.Exception)
                {
                    // NPC's underlying Il2Cpp GameObject was destroyed (e.g. save reload).
                    // OnDestroyed will clear the stale Instance on next spawn.
                }
                return;
            }

            if (_triggerPendingDay >= 0) return;

            if (IsCleanCashQuestStarted())
                _triggerPendingDay = 0;
        }

        /// <summary>
        /// Creates a new VicIntroQuest or resumes one already loaded from save.
        /// </summary>
        public void CreateOrResumeQuest()
        {
            if (_questCreated) return;
            _questCreated = true;

            try
            {
                if (VicIntroQuest.Instance != null) return;

                var quest = (VicIntroQuest)QuestManager.CreateQuest<VicIntroQuest>();
                if (quest != null)
                {
                    quest.Initialize();
                    quest.StartQuest();
                }
                else
                {
                    Logger.Error("QuestManager.CreateQuest<VicIntroQuest> returned null.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"CreateOrResumeQuest failed: {ex.Message}");
            }
        }

        public void OnQuestComplete()
        {
            _unlocked = true;
        }

        public void OnLaunderComplete(int currentDay)
        {
            _lastDepositDay = currentDay;
            _trustLevel++;
        }

        public void MarkTier2IntroShown()
        {
            _tier2IntroShown = true;
        }

        private void TrySendIntroText()
        {
            try
            {
                var vic = S1API.Entities.NPC.All?.FirstOrDefault(n => n.ID == "vic_bank_teller");
                if (vic != null)
                {
                    vic.SendTextMessage("Hey, I noticed you've been hitting your weekly deposit limits. Meet me in the alley behind the bank. I might be able to help.");
                    _needsIntroText = false;
                }
                else
                {
                    Logger.Warning("Vic NPC not found — deferring intro text until spawn.");
                    _needsIntroText = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"TrySendIntroText failed: {ex.Message}");
                _needsIntroText = true;
            }
        }

        private bool IsCleanCashQuestStarted()
        {
            try
            {
                var quest = QuestManager.Get<CleanCash>();
                if (quest == null) return false;

                var entries = quest.QuestEntries;
                if (entries == null || entries.Count == 0) return false;

                foreach (var entry in entries)
                {
                    if (entry.State == QuestState.Active || entry.State == QuestState.Completed)
                        return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not check Clean Cash quest: {ex.Message}");
            }
            return false;
        }
    }
}
