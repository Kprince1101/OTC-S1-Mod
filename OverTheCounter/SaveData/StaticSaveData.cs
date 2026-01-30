using MelonLoader;
using OverTheCounter.NPCs;
using OverTheCounter.Quests;
using S1API.Internal.Abstraction;
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

        private int _tickCounter;
        private const int TICK_INTERVAL = 300;

        // Runtime-only: guards against double quest creation per session.
        private bool _questCreated;

        // Runtime-only: intro text deferred until Static NPC spawns.
        private bool _needsIntroText;

        public static StaticSaveData Instance { get; private set; }

        public bool IntroCompleted => _introCompleted;
        public bool QuestTriggered => _questTriggered;

        public StaticSaveData()
        {
            Instance = this;
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
            if (StaticNPC.Instance != null && StaticNPC.Instance.DialogueReady)
                StaticNPC.Instance.RefreshDialogue();
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
                    staticNpc.SendTextMessage("Your digital footprint is sloppy. You're moving that much volume on an unencrypted line? You're begging to get caught.\n\nMeet me at the Casino. Top floor. Come alone, or don't come at all.");
                    _needsIntroText = false;
                }
                else
                {
                    Logger.Warning("Static NPC not found — deferring intro text until spawn.");
                    _needsIntroText = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"TrySendIntroText failed: {ex.Message}");
                _needsIntroText = true;
            }
        }

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
        }
    }
}
