using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.NPCs;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Wraps a game NPC as a store customer with a browse-and-leave lifecycle.
    /// </summary>
    public class CustomerInstance
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:CustomerInstance");

        /// <summary>All active customer instances keyed by ID.</summary>
        public static readonly Dictionary<string, CustomerInstance> Active = new();

        // =====================================================================
        //  Identity
        // =====================================================================

        public string Id { get; }
        public int SpawnSeed { get; }
        public CustomerSpawnPoints.SpawnPoint SpawnPoint { get; }

        // =====================================================================
        //  Game reference
        // =====================================================================

        public NPC GameNpc { get; private set; }

        /// <summary>FishNet NetworkObject.ObjectId for client-side adoption.</summary>
        public int NetworkObjectId { get; set; }

        /// <summary>True if this instance was adopted from a FishNet-replicated NPC (client-side).</summary>
        public bool IsAdopted { get; private set; }

        /// <summary>Whether the underlying NPC is still alive and valid.</summary>
        public bool IsValid => GameNpc != null && GameNpc.gameObject != null;

        /// <summary>Current world position of the NPC, or null if invalid.</summary>
        public Vector3? Position => IsValid ? GameNpc.transform.position : null;

        // =====================================================================
        //  State machine
        // =====================================================================

        public CustomerState State { get; set; }
        public bool ArrivedAtDestination { get; set; }

        // Browse tracking
        private List<Vector3> _browsePositions;
        private int _browseTargetIndex;
        private float _browsePauseEndTime;
        private Vector3? _currentWalkTarget;

        private const float BrowsePauseDuration = 5f;

        // Look-around tracking (when no storage found)
        public float LookAroundEndTime { get; set; }

        // =====================================================================
        //  Movement (GC-pinned callbacks)
        // =====================================================================

        private GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult> _walkCallback;

        // Stuck detection
        private Vector3? _lastStuckCheckPos;
        private float _lastStuckCheckTime;
        private int _stuckCount;
        private const float StuckCheckInterval = 8f;
        private const float StuckThreshold = 1.5f;

        // =====================================================================
        //  Constructor + Factory
        // =====================================================================

        private CustomerInstance(string id, int seed, CustomerSpawnPoints.SpawnPoint spawnPoint, NPC npc)
        {
            Id = id;
            SpawnSeed = seed;
            SpawnPoint = spawnPoint;
            GameNpc = npc;
            State = CustomerState.WalkingToStore;
        }

        /// <summary>
        /// Spawns a new customer NPC and registers it in the Active dictionary.
        /// </summary>
        public static CustomerInstance Create(string id, int seed, CustomerSpawnPoints.SpawnPoint spawnPoint)
        {
            try
            {
                var (firstName, lastName) = DrifterInstance.GetDrifterName(seed);

                var npc = NpcSpawner.SpawnCivilianNpc(
                    id, $"Customer_{id}",
                    firstName, lastName,
                    spawnPoint.Position, spawnPoint.Rotation);

                if (npc == null)
                {
                    Logger.Error($"Failed to spawn customer NPC for {id}");
                    return null;
                }

                NpcSpawner.GenerateRandomAppearance(npc, seed);

                var instance = new CustomerInstance(id, seed, spawnPoint, npc);

                // Capture FishNet ObjectId for client-side adoption
                try
                {
                    var nob = npc.GetComponent<FishNet.Object.NetworkObject>();
                    if (nob != null)
                        instance.NetworkObjectId = (int)nob.ObjectId;
                }
                catch { }

                Active[id] = instance;
                return instance;
            }
            catch (Exception ex)
            {
                Logger.Error($"CustomerInstance.Create failed for {id}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Adopts an existing FishNet-replicated NPC as a customer on the client.
        /// Applies appearance without spawning a new clone.
        /// </summary>
        public static CustomerInstance Adopt(string id, int seed, CustomerSpawnPoints.SpawnPoint spawnPoint, CustomerState state, NPC existingNpc)
        {
            if (Active.ContainsKey(id))
                return Active[id];

            var (firstName, lastName) = DrifterInstance.GetDrifterName(seed);
            existingNpc.ID = id;
            existingNpc.FirstName = firstName;
            existingNpc.LastName = lastName;

            NpcSpawner.GenerateRandomAppearance(existingNpc, seed);

            var instance = new CustomerInstance(id, seed, spawnPoint, existingNpc)
            {
                IsAdopted = true,
                State = state
            };

            Active[id] = instance;
            if (Config.VerboseLogging.Value)
                Logger.Msg($"Adopted customer {id} (state={state})");
            return instance;
        }

        // =====================================================================
        //  Movement
        // =====================================================================

        /// <summary>
        /// Commands the NPC to walk to the given position.
        /// </summary>
        public void WalkTo(Vector3 target)
        {
            if (!IsValid) return;

            // Ensure off-mesh link traversal stays enabled (game may reset it after spawn)
            try
            {
                var agent = GameNpc.gameObject.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null && !agent.autoTraverseOffMeshLink)
                    agent.autoTraverseOffMeshLink = true;
            }
            catch { }

            ArrivedAtDestination = false;
            _currentWalkTarget = target;

            _walkCallback = (GameSystem.Action<ScheduleOne.NPCs.NPCMovement.WalkResult>)
                new Action<ScheduleOne.NPCs.NPCMovement.WalkResult>(result =>
                {
                    if (result == ScheduleOne.NPCs.NPCMovement.WalkResult.Success ||
                        result == ScheduleOne.NPCs.NPCMovement.WalkResult.Partial)
                        ArrivedAtDestination = true;
                });

            GameNpc.Movement.SetDestination(target, _walkCallback, 2f, 1f);

            _lastStuckCheckTime = Time.time;
            _lastStuckCheckPos = null;
            _stuckCount = 0;
        }

        /// <summary>
        /// Warps the NPC instantly to the given position.
        /// </summary>
        public void WarpTo(Vector3 target)
        {
            if (!IsValid) return;

            try
            {
                GameNpc.Movement?.Warp(target);
                GameNpc.Movement?.Stop();
            }
            catch (Exception ex)
            {
                Logger.Warning($"WarpTo failed for {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if the NPC is stuck and warps to the target after repeated failures.
        /// </summary>
        public void CheckStuck()
        {
            if (!IsValid || _currentWalkTarget == null) return;
            if (ArrivedAtDestination) return;
            if (Time.time - _lastStuckCheckTime < StuckCheckInterval) return;

            _lastStuckCheckTime = Time.time;
            var pos = GameNpc.transform.position;

            if (_lastStuckCheckPos.HasValue)
            {
                float dist = Vector3.Distance(pos, _lastStuckCheckPos.Value);
                if (dist < StuckThreshold)
                {
                    _stuckCount++;
                    if (_stuckCount >= 2)
                    {
                        WarpTo(_currentWalkTarget.Value);
                        ArrivedAtDestination = true;
                        _stuckCount = 0;
                    }
                }
                else
                {
                    _stuckCount = 0;
                }
            }

            _lastStuckCheckPos = pos;
        }

        /// <summary>
        /// Re-issues movement if the NPC stopped mid-walk (pickpocket, ragdoll, etc.).
        /// </summary>
        public void EnsureMoving()
        {
            if (!IsValid || ArrivedAtDestination || _currentWalkTarget == null) return;

            try
            {
                var movement = GameNpc.Movement;
                if (movement == null) return;

                float dist = Vector3.Distance(GameNpc.transform.position, _currentWalkTarget.Value);
                if (dist > 3f && !movement.IsMoving)
                    WalkTo(_currentWalkTarget.Value);
            }
            catch { }
        }

        // =====================================================================
        //  Browsing
        // =====================================================================

        /// <summary>
        /// Initializes the browse phase with a list of positions to visit.
        /// </summary>
        public void StartBrowsing(List<Vector3> positions)
        {
            _browsePositions = positions;
            _browseTargetIndex = 0;
            _browsePauseEndTime = 0f;
            ArrivedAtDestination = false;

            if (_browsePositions.Count > 0)
                WalkTo(_browsePositions[0]);
        }

        /// <summary>
        /// Advances the browse state machine. Returns true when all targets have been visited.
        /// </summary>
        public bool TickBrowsing()
        {
            if (_browsePositions == null || _browsePositions.Count == 0)
                return true;

            // Currently pausing at a storage entity
            if (_browsePauseEndTime > 0f)
            {
                if (Time.time < _browsePauseEndTime)
                    return false;

                // Pause ended — advance to next target
                _browsePauseEndTime = 0f;
                _browseTargetIndex++;

                if (_browseTargetIndex >= _browsePositions.Count)
                    return true;

                ArrivedAtDestination = false;
                WalkTo(_browsePositions[_browseTargetIndex]);
                return false;
            }

            // Arrived at current target — start pause
            if (ArrivedAtDestination)
            {
                _browsePauseEndTime = Time.time + BrowsePauseDuration;
                return false;
            }

            return false;
        }

        // =====================================================================
        //  Cleanup
        // =====================================================================

        /// <summary>
        /// Removes this customer from tracking and destroys the NPC.
        /// </summary>
        public void Despawn()
        {
            Active.Remove(Id);

            if (GameNpc != null)
            {
                if (!IsAdopted)
                    NpcSpawner.Despawn(GameNpc);
                GameNpc = null;
            }
        }

        /// <summary>
        /// Despawns all active customers. Called on scene transitions.
        /// </summary>
        public static void CleanupAll()
        {
            foreach (var customer in Active.Values)
            {
                if (customer.GameNpc != null)
                {
                    if (!customer.IsAdopted)
                        NpcSpawner.Despawn(customer.GameNpc);
                    customer.GameNpc = null;
                }
            }
            Active.Clear();
        }
    }
}
