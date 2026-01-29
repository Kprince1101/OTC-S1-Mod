using MelonLoader;
using OverTheCounter.NPCs;
using OverTheCounter.Quests;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using S1API.GameTime;
using S1API.Quests;
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
        /// The in-game day when the Clean Cash quest was first detected during gameplay.
        /// -1 means not yet detected. Used to implement the ~24-hour text delay.
        /// </summary>
        [SaveableField("vic_trigger_pending_day")]
        private int _triggerPendingDay = -1;

        /// <summary>
        /// Runtime-only flag: true when the intro text needs to be sent but Vic hasn't spawned yet.
        /// </summary>
        private bool _needsIntroText;

        /// <summary>
        /// Runtime-only flag: true once CreateOrResumeQuest has run this session.
        /// </summary>
        private bool _questCreated;

        /// <summary>
        /// Whether the retroactive check already ran this session.
        /// Distinguishes a load-time detection (immediate trigger) from a
        /// real-time detection during gameplay (delayed trigger).
        /// </summary>
        private bool _retroactiveCheckDone;

        private int _tickCounter;
        private const int TICK_INTERVAL = 300; // ~5 seconds at 60fps

        public static VicSaveData Instance { get; private set; }

        public bool Unlocked => _unlocked;
        public int TrustLevel => _trustLevel;

        public bool HasBeenTexted
        {
            get => _hasBeenTexted;
            set
            {
                if (_hasBeenTexted) return;
                if (!value) return;

                _hasBeenTexted = true;
                Logger.Msg("HasBeenTexted flipped to true — attempting intro text.");
                TrySendIntroText();
                CreateOrResumeQuest();
                VicNPC.Instance?.RefreshDialogue();
            }
        }

        public VicSaveData()
        {
            Instance = this;
        }

        protected override void OnLoaded()
        {
            Instance = this;
            _retroactiveCheckDone = false;
            Logger.Msg("VicSaveData loaded.");

            if (_hasBeenTexted)
            {
                _questCreated = true; // Quest auto-loads from save via QuestPatches
                return;
            }

            // If a pending trigger was saved from a prior session, Tick() will handle firing it.
            if (_triggerPendingDay >= 0)
            {
                return;
            }

            // Attempt retroactive trigger — quest systems may not be ready yet,
            // so Tick() retries if this returns false.
            if (IsCleanCashQuestStarted())
            {
                Logger.Msg("Retroactive: Clean Cash quest detected on load — triggering immediately.");
                _retroactiveCheckDone = true;
                HasBeenTexted = true;
            }
        }

        /// <summary>
        /// Called by VicNPC.OnCreated() to retry a deferred intro text once Vic exists.
        /// </summary>
        public void OnVicSpawned()
        {
            if (!_needsIntroText) return;

            Logger.Msg("Vic spawned — retrying deferred intro text.");
            _needsIntroText = false;
            TrySendIntroText();
        }

        /// <summary>
        /// Called every frame from Core.OnLateUpdate(). Throttled internally.
        /// Handles both retroactive detection (if OnLoaded missed it due to timing)
        /// and real-time detection with a ~24-hour in-game delay.
        /// </summary>
        public void Tick()
        {
            if (_hasBeenTexted) return;

            if (++_tickCounter < TICK_INTERVAL) return;
            _tickCounter = 0;

            // If a trigger day is pending, check if enough time has passed (next in-game day).
            if (_triggerPendingDay >= 0)
            {
                try
                {
                    int today = TimeManager.ElapsedDays;
                    if (today > _triggerPendingDay)
                    {
                        Logger.Msg($"Trigger delay elapsed (pending day {_triggerPendingDay}, now day {today}). Sending Vic intro.");
                        HasBeenTexted = true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Tick: Could not read ElapsedDays: {ex.Message}");
                }
                return;
            }

            // Poll for Clean Cash quest.
            if (IsCleanCashQuestStarted())
            {
                if (!_retroactiveCheckDone)
                {
                    // This is the retroactive path — OnLoaded() missed it (timing issue).
                    // Trigger immediately since the quest was already active before this session.
                    Logger.Msg("Retroactive (deferred): Clean Cash quest detected in Tick — triggering immediately.");
                    _retroactiveCheckDone = true;
                    HasBeenTexted = true;
                }
                else
                {
                    // Real-time: quest just became active during gameplay.
                    // Schedule the text for the next in-game day (~24 hours).
                    try
                    {
                        _triggerPendingDay = TimeManager.ElapsedDays;
                        Logger.Msg($"Clean Cash quest detected on day {_triggerPendingDay}. Vic will text next day.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Tick: Could not read ElapsedDays for pending: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Creates a new VicIntroQuest or resumes one already loaded from save.
        /// Safe to call multiple times — guards against double-creation.
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

        /// <summary>
        /// Called by VicNPC when the player completes the quest handover.
        /// </summary>
        public void OnQuestComplete()
        {
            _unlocked = true;
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
                    Logger.Msg("Intro text sent from Vic.");
                }
                else
                {
                    Logger.Warning("Vic NPC not found in NPC.All — deferring intro text until spawn.");
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
                if (quest != null)
                    return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not check Clean Cash quest: {ex.Message}");
            }
            return false;
        }
    }
}
