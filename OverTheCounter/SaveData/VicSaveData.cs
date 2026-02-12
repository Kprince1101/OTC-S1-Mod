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
using OverTheCounter.Utilities;
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

        [SaveableField("vic_quest_accepted")]
        private bool _questAccepted;

        /// <summary>
        /// The in-game day when the Clean Cash quest was first detected.
        /// -1 means not yet detected. Set to 0 once detected; intro fires on next sleep.
        /// </summary>
        [SaveableField("vic_trigger_pending_day")]
        private int _triggerPendingDay = -1;

        [SaveableField("vic_last_deposit_day")]
        private int _lastDepositDay = -1;

        [SaveableField("vic_last_trust_increment_day")]
        private int _lastTrustIncrementDay = -1;

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

        // Runtime-only: deferred re-publish after save/load so the client
        // receives correct state even if the initial SyncVar push missed us.
        private bool _needsStatePublish;

        private int _tickCounter;
        private const int TICK_INTERVAL = 300; // ~5 seconds at 60fps
        private bool _positionFixed;

        private static bool _sleepEndSubscribed;
        public static VicSaveData Instance { get; private set; }

        internal static void ResetInstance() => Instance = null;

        public bool Unlocked => _unlocked;
        public bool QuestAccepted => _questAccepted;
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
                ConfigSyncData.Instance?.PublishGameState();
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

            if (_questAccepted)
                _questCreated = true;

            if (_hasBeenTexted)
            {
                _questCreated = true;
                if (NetworkHelper.IsHost)
                    _needsStatePublish = true;
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
            // Reset so the next TICK_INTERVAL fires a position fix
            // (deferred ~5s to let NavMeshAgent fully initialize).
            if (NetworkHelper.IsHost)
                _positionFixed = false;

            if (!_needsIntroText) return;
            _needsIntroText = false;
            TrySendIntroText();
        }

        private void OnPlayerWokeUp()
        {
            // Host-only: re-warp after sleep so the NPC snaps to the correct
            // NavMesh position. The Warp RPC syncs to clients via FishNet.
            if (!NetworkHelper.IsHost) return;

            _positionFixed = false;

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
            // Re-publish game state after save/load so the client receives
            // the correct vic fields (initial push may have missed them).
            if (_needsStatePublish && NetworkHelper.IsHost)
            {
                _needsStatePublish = false;
                ConfigSyncData.Instance?.PublishGameState();
            }

            // Stale data check: host-only since client state comes from ApplyHostState.
            if (NetworkHelper.IsHost && !_staleCheckDone && _hasBeenTexted)
            {
                _staleCheckDone = true;
                if (!_unlocked && !IsCleanCashQuestStarted())
                {
                    _hasBeenTexted = false;
                    _triggerPendingDay = -1;
                    _questCreated = false;
                    _needsIntroText = false;
                    _fireOnNextTick = false;
                }
                return;
            }

            // Deferred trigger from OnSleepEnd (host-only path).
            if (_fireOnNextTick)
            {
                _fireOnNextTick = false;
                if (!_hasBeenTexted)
                    HasBeenTexted = true;
                return;
            }

            if (++_tickCounter < TICK_INTERVAL) return;
            _tickCounter = 0;

            // Deferred position fix: runs once after TICK_INTERVAL (~5s) to
            // ensure the NavMeshAgent is fully initialized before warping.
            if (NetworkHelper.IsHost && !_positionFixed && VicNPC.Instance != null)
            {
                _positionFixed = true;
                try { VicNPC.Instance.WarpToSpawn(); }
                catch (Exception) { }
            }

            // Keep Vic's dialogue fresh so players see current values.
            if (_hasBeenTexted)
            {
                try
                {
                    if (VicNPC.Instance != null && VicNPC.Instance.DialogueReady)
                        VicNPC.Instance.RefreshDialogue();
                }
                catch (Exception) { }
                return;
            }

            // Quest polling: host-only since client state comes from ApplyHostState.
            if (!NetworkHelper.IsHost) return;

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

        /// <summary>
        /// Called by ConfigSyncData when the client receives game state from the host.
        /// Syncs all flags, advances quest objectives, and refreshes dialogue.
        /// </summary>
        public void ApplyHostState(bool hasBeenTexted = false, bool questAccepted = false, bool unlocked = false, int trustLevel = -1)
        {
            bool changed = false;

            // Determine if the intro quest is already fully completed in the
            // incoming state so we can skip quest creation / objective noise.
            bool questAlreadyDone = unlocked;

            if (hasBeenTexted && !_hasBeenTexted)
            {
                _hasBeenTexted = true;
                if (!questAlreadyDone)
                    CreateOrResumeQuest();
                changed = true;
            }

            if (questAccepted && !_questAccepted)
            {
                _questAccepted = true;
                _questCreated = true;
                if (!questAlreadyDone)
                {
                    try { VicIntroQuest.Instance?.CompleteObj1(); }
                    catch (Exception ex) { Logger.Warning($"Client VicIntroQuest CompleteObj1 failed: {ex.Message}"); }
                }
                changed = true;
            }

            if (unlocked && !_unlocked)
            {
                _unlocked = true;
                try { VicIntroQuest.Instance?.CompleteObj2(); }
                catch (Exception ex) { Logger.Warning($"Client VicIntroQuest CompleteObj2 failed: {ex.Message}"); }
                changed = true;
            }

            if (trustLevel >= 0 && trustLevel != _trustLevel)
            {
                _trustLevel = trustLevel;
                changed = true;
            }

            if (changed && VicNPC.Instance != null && VicNPC.Instance.DialogueReady)
                VicNPC.Instance.RefreshDialogue();
        }

        public void OnQuestComplete()
        {
            _unlocked = true;
            ConfigSyncData.Instance?.PublishGameState();
        }

        public void OnLaunderComplete(int currentDay)
        {
            _lastDepositDay = currentDay;
            if (_lastTrustIncrementDay < currentDay)
            {
                _lastTrustIncrementDay = currentDay;
                _trustLevel++;
            }
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
                case "VIC_QUEST_ACCEPTED":
                    _questAccepted = true;
                    _questCreated = true;
                    try { VicIntroQuest.Instance?.CompleteObj1(); }
                    catch (Exception ex) { Logger.Warning($"Remote VicIntroQuest CompleteObj1 failed: {ex.Message}"); }
                    break;

                case "VIC_QUEST_COMPLETE":
                    _unlocked = true;
                    try { VicIntroQuest.Instance?.CompleteObj2(); }
                    catch (Exception ex) { Logger.Warning($"Remote VicIntroQuest CompleteObj2 failed: {ex.Message}"); }
                    break;

                case "VIC_LAUNDER":
                    int launderDay = TimeManager.ElapsedDays;
                    if (_lastTrustIncrementDay < launderDay)
                    {
                        _lastTrustIncrementDay = launderDay;
                        _trustLevel++;
                    }
                    break;

                default:
                    Logger.Warning($"VicSaveData: unknown remote action '{action}'");
                    return;
            }

            ConfigSyncData.Instance?.PublishGameState();
        }

        public void MarkTier2IntroShown()
        {
            _tier2IntroShown = true;
        }

        private void TrySendIntroText()
        {
            if (!NetworkHelper.IsHost) return;

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
