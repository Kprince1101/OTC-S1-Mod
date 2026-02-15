using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Map;
using MelonLoader;
using OverTheCounter.NPCs;
using OverTheCounter.Quests;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using S1API.Quests;
using OverTheCounter.Utilities;
using System;

namespace OverTheCounter.SaveData
{
    public class BellaSaveData : Saveable
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:BellaSaveData");

        [SaveableField("bella_stage")]
        private int _stage;

        [SaveableField("bella_night_market_unlocked")]
        private bool _nightMarketUnlocked;

        private bool _questCreated;
        private bool _needsSpawn;
        private bool _needsStatePublish;
        private bool _positionFixed;
        private bool _warehouseHoursApplied;
        private bool _questReconciled;
        private int _tickCounter;
        private const int TICK_INTERVAL = 300;

        public static BellaSaveData Instance { get; private set; }

        internal static void ResetInstance() => Instance = null;

        public int Stage => _stage;
        public bool NightMarketUnlocked => _nightMarketUnlocked;

        public static bool IsNightMarketUnlocked =>
            Instance?._nightMarketUnlocked ?? false;

        public BellaSaveData()
        {
            Instance = this;
        }

        protected override void OnLoaded()
        {
            Instance = this;

            if (Config.VerboseLogging.Value)
                Logger.Msg($"OnLoaded: _stage={_stage} (from save), quest Instance={(BellaProtocolQuest.Instance != null ? $"exists (stage={BellaProtocolQuest.Instance.Stage})" : "null")}");

            if (_stage > 0)
                _questCreated = true;

            // If quest was in progress when saved, defer Bella respawn
            if (_stage >= 1 && _stage < 5)
                _needsSpawn = true;

            // Re-publish game state on next Tick so the client receives
            // bella_stage. The initial SyncVar push in ProcessMessages may
            // run before S1API loads this Saveable, causing the client to
            // receive bella_stage=0 and lose quest progress.
            if (_stage > 0)
                _needsStatePublish = true;

            // Re-apply any host state that arrived before this Saveable loaded.
            // Also reconciles the quest if it loaded before us with a stale stage.
            ConfigSyncData.ApplyPendingGameState();

            if (Config.VerboseLogging.Value)
                Logger.Msg($"OnLoaded after pending apply: _stage={_stage}, quest Instance={(BellaProtocolQuest.Instance != null ? $"exists (stage={BellaProtocolQuest.Instance.Stage})" : "null")}");

            ReconcileQuest();
        }

        /// <summary>
        /// If the quest was already loaded with a stale stage, advance it to match.
        /// Covers the case where BellaProtocolQuest.OnLoaded ran before this Saveable.
        /// </summary>
        private void ReconcileQuest()
        {
            if (_stage < 2 || BellaProtocolQuest.Instance == null) return;
            if (BellaProtocolQuest.Instance.Stage >= _stage) return;

            if (_stage >= 2 && BellaProtocolQuest.Instance.Stage < 2)
                BellaProtocolQuest.Instance.AdvanceToWeedRequest();
            if (_stage >= 3 && BellaProtocolQuest.Instance.Stage < 3)
                BellaProtocolQuest.Instance.AdvanceToMethRequest();
            if (_stage >= 4 && BellaProtocolQuest.Instance.Stage < 4)
                BellaProtocolQuest.Instance.AdvanceToCocaineRequest();
            if (_stage >= 5 && BellaProtocolQuest.Instance.Stage < 5)
                BellaProtocolQuest.Instance.CompleteQuest();
        }

        public void Tick()
        {
            // Safety net: reconcile quest once after everything is loaded.
            // Covers all load-order timing edge cases between SaveData, Quest,
            // and SyncVar callbacks that OnLoaded reconciliation may miss.
            if (!_questReconciled && _stage >= 2
                && BellaProtocolQuest.Instance != null)
            {
                _questReconciled = true;
                if (BellaProtocolQuest.Instance.Stage < _stage)
                {
                    if (Config.VerboseLogging.Value)
                        Logger.Msg($"Tick reconciliation: quest stage {BellaProtocolQuest.Instance.Stage} → {_stage}");
                    ReconcileQuest();
                }
            }

            if (_needsSpawn)
            {
                _needsSpawn = false;
                try
                {
                    EnableBella();
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Deferred Bella spawn failed: {ex.Message}");
                    _needsSpawn = true; // retry next tick
                }
            }

            // Re-publish game state after save/load so the client receives
            // the correct bella_stage (initial push may have missed it).
            if (_needsStatePublish && NetworkHelper.IsHost)
            {
                _needsStatePublish = false;
                ConfigSyncData.Instance?.PublishGameState();
            }

            // Host-only: NavMesh-snap Bella's position so the Warp RPC sends
            // correct ground-level Y to clients (prevents floating).
            if (NetworkHelper.IsHost && !_positionFixed && BellaNPC.Instance != null)
            {
                _positionFixed = true;
                try { BellaNPC.Instance.WarpToSpawn(); }
                catch (Exception) { }
            }

            // Apply 24/7 warehouse hours once DarkMarket singleton is available
            if (_nightMarketUnlocked && !_warehouseHoursApplied)
                ApplyWarehouseHours();

            if (++_tickCounter < TICK_INTERVAL) return;
            _tickCounter = 0;

            // Keep Bella's dialogue fresh
            if (_stage > 0 && BellaNPC.Instance != null && BellaNPC.Instance.DialogueReady)
            {
                try { BellaNPC.Instance.RefreshDialogue(); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Called from FixerDialoguePatch when player selects "Warehouse hours?".
        /// Spawns Bella and creates the quest with POI to the building entrance.
        /// </summary>
        public void TriggerQuest()
        {
            if (_stage > 0) return;

            _stage = 1;
            _questCreated = true;

            EnableBella();
            CreateQuest();

            ConfigSyncData.Instance?.PublishGameState();
        }

        /// <summary>
        /// Enables Bella's summoning and refreshes her dialogue.
        /// Bella is auto-created by S1API; this just flips her to summonable.
        /// </summary>
        private void EnableBella()
        {
            if (BellaNPC.Instance == null || BellaNPC.Instance.GameNpc == null)
            {
                _needsSpawn = true;
                return;
            }

            BellaNPC.Instance.EnableSummoning();
            if (BellaNPC.Instance.DialogueReady)
                BellaNPC.Instance.RefreshDialogue();
            Logger.Msg("Bella enabled for summoning.");
        }

        /// <summary>
        /// Sets the Dark Market / Warehouse access zone to 24/7 hours.
        /// Both DarkMarket.ShouldBeOpen() and DarkMarketAccessZone.GetIsOpen()
        /// read OpenTime/CloseTime from the same AccessZone, so this single
        /// change unlocks doors for both the player and NPC store visits.
        /// </summary>
        private void ApplyWarehouseHours()
        {
            try
            {
                var darkMarket = NetworkSingleton<DarkMarket>.Instance;
                if (darkMarket?.AccessZone == null) return;

                darkMarket.AccessZone.OpenTime = 0;
                darkMarket.AccessZone.CloseTime = 2400;
                _warehouseHoursApplied = true;
                Logger.Msg("Warehouse hours set to 24/7 (Bella quest complete).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"ApplyWarehouseHours failed (will retry): {ex.Message}");
            }
        }

        private void CreateQuest()
        {
            if (BellaProtocolQuest.Instance != null) return;

            try
            {
                var quest = (BellaProtocolQuest)QuestManager.CreateQuest<BellaProtocolQuest>();
                if (quest != null)
                {
                    quest.Initialize();
                    quest.StartQuest();
                }
                else
                {
                    Logger.Error("QuestManager.CreateQuest<BellaProtocolQuest> returned null.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"CreateQuest failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by BellaNPC dialogue when the player completes the current stage.
        /// Host path — advances stage, updates quest, publishes state.
        /// </summary>
        public void CompleteCurrentStage()
        {
            switch (_stage)
            {
                case 1:
                    _stage = 2;
                    BellaProtocolQuest.Instance?.AdvanceToWeedRequest();
                    break;
                case 2:
                    _stage = 3;
                    BellaProtocolQuest.Instance?.AdvanceToMethRequest();
                    break;
                case 3:
                    _stage = 4;
                    BellaProtocolQuest.Instance?.AdvanceToCocaineRequest();
                    break;
                case 4:
                    _stage = 5;
                    _nightMarketUnlocked = true;
                    BellaProtocolQuest.Instance?.CompleteQuest();
                    break;
            }

            ConfigSyncData.Instance?.PublishGameState();
        }

        /// <summary>
        /// Called on the host when a client sends a quest action via P2P.
        /// </summary>
        public void HandleRemoteAction(string action)
        {
            switch (action)
            {
                case "BELLA_INTRO":
                    TriggerQuest();
                    break;

                case "BELLA_ADVANCE":
                    CompleteCurrentStage();
                    break;

                default:
                    Logger.Warning($"BellaSaveData: unknown remote action '{action}'");
                    return;
            }
        }

        /// <summary>
        /// Called by ConfigSyncData when the client receives game state from the host.
        /// </summary>
        public void ApplyHostState(int stage, bool unlocked)
        {
            bool changed = false;

            if (Config.VerboseLogging.Value)
                Logger.Msg($"ApplyHostState: host stage={stage}, local _stage={_stage}, quest Instance={(BellaProtocolQuest.Instance != null ? $"exists (stage={BellaProtocolQuest.Instance.Stage})" : "null")}");

            if (stage > _stage)
            {
                int oldStage = _stage;
                _stage = stage;

                // If quest not created yet, create it and enable summoning
                if (oldStage == 0 && stage >= 1 && !_questCreated)
                {
                    _questCreated = true;
                    EnableBella();
                    CreateQuest();
                }

                // Advance quest entries to match
                if (stage >= 2 && oldStage < 2)
                    BellaProtocolQuest.Instance?.AdvanceToWeedRequest();
                if (stage >= 3 && oldStage < 3)
                    BellaProtocolQuest.Instance?.AdvanceToMethRequest();
                if (stage >= 4 && oldStage < 4)
                    BellaProtocolQuest.Instance?.AdvanceToCocaineRequest();
                if (stage >= 5 && oldStage < 5)
                    BellaProtocolQuest.Instance?.CompleteQuest();

                changed = true;
            }

            if (unlocked && !_nightMarketUnlocked)
            {
                _nightMarketUnlocked = true;
                changed = true;
            }

            if (changed && BellaNPC.Instance != null && BellaNPC.Instance.DialogueReady)
                BellaNPC.Instance.RefreshDialogue();
        }
    }
}
