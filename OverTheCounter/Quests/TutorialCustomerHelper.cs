using OverTheCounter.Logic;
using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;
using UnityEngine;

namespace OverTheCounter.Quests
{
    /// <summary>
    /// Shared logic for spawning a tutorial customer during a quest's
    /// "make your first sale" stage. Used by StorefrontGrowthQuest (shack),
    /// StorefrontExpansionQuest (dispensary), and future building quests.
    /// </summary>
    internal class TutorialCustomerHelper
    {
        private readonly string _buildingId;
        private readonly Func<bool> _isStoreOpen;
        private readonly Func<BuildingTarget> _getTarget;

        private bool _spawned;
        private string _customerId;

        public TutorialCustomerHelper(string buildingId, Func<bool> isStoreOpen, Func<BuildingTarget> getTarget)
        {
            _buildingId = buildingId;
            _isStoreOpen = isStoreOpen;
            _getTarget = getTarget;
        }

        /// <summary>
        /// Call each tick during the "make sale" stage. Respawns if the previous
        /// tutorial customer left without buying. Only spawns before 8 PM.
        /// </summary>
        public void Tick()
        {
            // Respawn if tutorial customer left without buying
            if (_spawned && _customerId != null
                && !CustomerInstance.Active.ContainsKey(_customerId))
            {
                _spawned = false;
                _customerId = null;
            }

            if (S1API.GameTime.TimeManager.CurrentTime < 2000)
                TrySpawn();
        }

        /// <summary>Resets internal state (e.g. on quest load).</summary>
        public void Reset()
        {
            _spawned = false;
            _customerId = null;
        }

        private void TrySpawn()
        {
            if (_spawned) return;
            if (!_isStoreOpen()) return;

            try
            {
                var spawnPoint = CustomerSpawnPoints.GetRandomSpawnPoint(_buildingId);
                if (spawnPoint == null) return;

                var target = _getTarget();
                if (target == null) return;

                string id = $"tutorial_{UnityEngine.Random.Range(1000, 9999)}";
                int seed = UnityEngine.Random.Range(0, int.MaxValue);
                var customer = CustomerInstance.Create(id, seed, spawnPoint, target);

                if (customer != null)
                {
                    // Boost budget + lower standards so tutorial customer is very likely to buy
                    var prefs = customer.Preferences;
                    prefs.MaxBudgetPerItem = 200f;
                    prefs.QualityExpectation = 0f;
                    customer.Preferences = prefs;

                    customer.WalkTo(target.ExteriorApproachPosition);
                    _customerId = id;
                    _spawned = true;
                    OTCLog.Msg(OTCLog.Systems.Quest, $"Spawned tutorial customer for {_buildingId}");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"TutorialCustomer spawn failed ({_buildingId}): {ex.Message}");
            }
        }
    }
}
