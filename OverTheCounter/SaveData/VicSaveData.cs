using MelonLoader;
using OverTheCounter.NPCs;
using OverTheCounter.Quests;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using S1API.GameTime;
using S1API.Quests;
using System;
using OverTheCounter.Utilities;
using System.Linq;

#if IL2CPP
using Il2CppScheduleOne.Money;
#else
using ScheduleOne.Money;
#endif

namespace OverTheCounter.SaveData
{
    public class VicSaveData : Saveable
    {
        [SaveableField("vic_unlocked")]
        private bool _unlocked;

        [SaveableField("vic_has_been_texted")]
        private bool _hasBeenTexted;

        [SaveableField("vic_trust_level")]
        private int _trustLevel;

        [SaveableField("vic_quest_accepted")]
        private bool _questAccepted;

        // Legacy: kept for backwards-compatible deserialization of old saves.
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

        // Runtime-only: deferred re-publish after save/load so the client
        // receives correct state even if the initial SyncVar push missed us.
        private bool _needsStatePublish;

        // Runtime-only: deferred quest reconciliation guard (fires once per session in Tick).
        private bool _questReconciled;

        private int _tickCounter;
        private const int TICK_INTERVAL = 300; // ~5 seconds at 60fps
        private bool _positionFixed;

        // Set when a state change requires a dialogue rebuild but dialogue is in progress.
        // Tick() checks this every frame and refreshes the moment dialogue closes.
        internal bool _dialogueStale;

        // Tracks dialogue open/close to detect the closing edge and force a rebuild.
        private bool _wasInDialogue;

        public static VicSaveData Instance { get; private set; }

        internal static void ResetInstance() => Instance = null;

        public bool Unlocked => _unlocked;
        public bool QuestAccepted => _questAccepted;
        public int TrustLevel => _trustLevel;
        public int LastDepositDay => _lastDepositDay;
        public bool Tier2IntroShown => _tier2IntroShown;

        public bool HasBeenTexted => _hasBeenTexted;

        public VicSaveData()
        {
            Instance = this;
        }

        protected override void OnLoaded()
        {
            Instance = this;

            // Heal stale flags: derive earlier state from later progression.
            if (!_questAccepted && _unlocked)
                _questAccepted = true;
            if (!_hasBeenTexted && (_questAccepted || _unlocked))
                _hasBeenTexted = true;

            // Backwards compat: old saves may have _triggerPendingDay > 0
            // without _hasBeenTexted. Promote to triggered state.
            if (!_hasBeenTexted && _triggerPendingDay > 0)
            {
                _hasBeenTexted = true;
                _needsIntroText = true;
            }

            if (_hasBeenTexted)
            {
                _questCreated = true;
                if (NetworkHelper.IsHost)
                    _needsStatePublish = true;
            }

            _dialogueStale = true;
            ConfigSyncData.ApplyPendingGameState();
            // ReconcileQuest deferred to Tick safety net — OnLoaded fires before
            // the game finishes loading its own quest persistence (Quests.json),
            // so creating quests here races with the game's own quest loading.
        }

        /// <summary>
        /// Reconciles quest state to match SaveData.
        /// Force-creates missing quest instance and advances objectives.
        /// Returns true if a quest was created (caller should defer advancement
        /// to the next frame so Unity's Start() can initialize entry components).
        /// </summary>
        private bool ReconcileQuest()
        {
            int effectiveStage = _unlocked ? 3 : _questAccepted ? 2 : _hasBeenTexted ? 1 : 0;

            bool created = false;

            // Don't create quests that are already fully complete — no UI to show.
            if (effectiveStage > 0 && effectiveStage < 3 && VicIntroQuest.Instance == null)
            {
                try
                {
                    var quest = (VicIntroQuest)QuestManager.CreateQuest<VicIntroQuest>();
                    if (quest != null) { quest.Initialize(); quest.StartQuest(); created = true; }
                }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"ReconcileQuest: quest creation failed: {ex.Message}"); }
            }

            // Skip advancement on the same frame as creation — Unity's Start()
            // hasn't run on the entry components yet, so state changes don't stick.
            if (!created && VicIntroQuest.Instance != null && effectiveStage > VicIntroQuest.Instance.Stage)
            {
                if (effectiveStage >= 2 && VicIntroQuest.Instance.Stage < 2)
                    VicIntroQuest.Instance.CompleteObj1();
                if (effectiveStage >= 3 && VicIntroQuest.Instance.Stage < 3)
                    VicIntroQuest.Instance.CompleteObj2();
            }

            return created;
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

        /// <summary>
        /// Called every frame from Core.OnLateUpdate(). Throttled internally.
        /// Polls ATM deposits and keeps Vic's dialogue up to date.
        /// </summary>
        public void Tick()
        {
            // Safety net: reconcile quest state after everything is loaded.
            // If ReconcileQuest creates a quest, it returns true and we re-run
            // next frame so Unity's Start() has initialized entry components.
            if (!_questReconciled && _hasBeenTexted)
            {
                if (!ReconcileQuest())
                    _questReconciled = true;
            }

            // Detect dialogue closing edge — forces a rebuild so weed count
            // (and any other dynamic data) is always fresh on next interaction.
            bool inDialogue = VicNPC.Instance != null && VicNPC.Instance.IsInDialogue;
            if (_wasInDialogue && !inDialogue)
                _dialogueStale = true;
            _wasInDialogue = inDialogue;

            if (_dialogueStale && VicNPC.Instance != null
                && VicNPC.Instance.DialogueReady && !inDialogue)
            {
                _dialogueStale = false;
                try { VicNPC.Instance.RefreshDialogue(); }
                catch (Exception) { }
            }

            // Re-publish game state after save/load so the client receives
            // the correct vic fields (initial push may have missed them).
            if (_needsStatePublish && NetworkHelper.IsHost)
            {
                _needsStatePublish = false;
                ConfigSyncData.Instance?.PublishGameState();
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

            // Check ATM trigger (either peer can hit the deposit threshold)
            if (!_hasBeenTexted)
            {
                try
                {
                    if (ATM.WeeklyDepositSum >= Config.VicDepositTrigger.Value)
                    {
                        _hasBeenTexted = true;
                        CreateOrResumeQuest();

                        if (NetworkHelper.IsHost)
                        {
                            TrySendIntroText();
                            ConfigSyncData.Instance?.PublishGameState();
                        }
                        else
                        {
                            ConfigSyncData.SendQuestAction("VIC_TRIGGER");
                        }

                        _dialogueStale = true;
                    }
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.NPC, $"ATM check failed: {ex.Message}");
                }
                return;
            }

            // Keep Vic's dialogue fresh once triggered
            try
            {
                if (!inDialogue && VicNPC.Instance != null && VicNPC.Instance.DialogueReady)
                    VicNPC.Instance.RefreshDialogue();
            }
            catch (Exception) { }
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
                    OTCLog.Error(OTCLog.Systems.NPC, "QuestManager.CreateQuest<VicIntroQuest> returned null.");
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
                    catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Client VicIntroQuest CompleteObj1 failed: {ex.Message}"); }
                }
                changed = true;
            }

            if (unlocked && !_unlocked)
            {
                _unlocked = true;
                try { VicIntroQuest.Instance?.CompleteObj2(); }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Client VicIntroQuest CompleteObj2 failed: {ex.Message}"); }
                changed = true;
            }

            if (trustLevel >= 0 && trustLevel != _trustLevel)
            {
                _trustLevel = trustLevel;
                changed = true;
            }

            if (changed)
                _dialogueStale = true;
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
                    catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Remote VicIntroQuest CompleteObj1 failed: {ex.Message}"); }
                    break;

                case "VIC_QUEST_COMPLETE":
                    _unlocked = true;
                    try { VicIntroQuest.Instance?.CompleteObj2(); }
                    catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Remote VicIntroQuest CompleteObj2 failed: {ex.Message}"); }
                    break;

                case "VIC_TRIGGER":
                    if (!_hasBeenTexted)
                    {
                        _hasBeenTexted = true;
                        TrySendIntroText();
                        CreateOrResumeQuest();
                    }
                    break;

                case "VIC_LAUNDER":
                    int launderDay = TimeManager.ElapsedDays;
                    _lastDepositDay = launderDay;
                    if (_lastTrustIncrementDay < launderDay)
                    {
                        _lastTrustIncrementDay = launderDay;
                        _trustLevel++;
                    }
                    break;

                default:
                    OTCLog.Warning(OTCLog.Systems.NPC, $"VicSaveData: unknown remote action '{action}'");
                    return;
            }

            _dialogueStale = true;
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
                    OTCLog.Warning(OTCLog.Systems.NPC, "Vic NPC not found — deferring intro text until spawn.");
                    _needsIntroText = true;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"TrySendIntroText failed: {ex.Message}");
                _needsIntroText = true;
            }
        }

    }
}
