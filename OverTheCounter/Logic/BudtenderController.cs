using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Money;
using S1API.GameTime;
using System;
using System.Collections.Generic;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Static controller for all budtender operations: hire/fire, building caps,
    /// wage management, tick orchestration, and serialization for save/sync.
    /// </summary>
    public static class BudtenderController
    {
        public const float DailyWage = 200f;

        // Budtender shift: arrive 1hr before store open, leave 1hr after store close
        public const int ShiftStartMinute = (StoreHours.OpenHour - 1) * 60;  // 7:00 AM = 420
        public const int ShiftEndMinute = (StoreHours.CloseHour + 1) * 60;   // 9:00 PM = 1260

        // Max budtenders per building type
        private static readonly Dictionary<string, int> MaxStaff = new()
        {
            { PropertySaveData.ShackId, 1 },
            { Dispensary.DispensaryId, 3 },
            { PropertySaveData.WarehouseId, 1 }
        };

        private static int _nextId;
        private static int _lastWageDay = -1;

        // =================================================================
        //  Hire / Fire
        // =================================================================

        /// <summary>Can a new budtender be hired for this building?</summary>
        public static bool CanHire(string buildingId)
        {
            if (!MaxStaff.TryGetValue(buildingId, out int max)) return false;
            return CountStaff(buildingId) < max;
        }

        /// <summary>Count hired budtenders in a building (including off-duty).</summary>
        public static int CountStaff(string buildingId)
        {
            int count = 0;
            foreach (var bt in BudtenderInstance.Active.Values)
            {
                if (bt.AssignedCounter?.BuildingId == buildingId)
                    count++;
            }
            return count;
        }

        /// <summary>Get max allowed staff for a building.</summary>
        public static int GetMaxStaff(string buildingId)
        {
            return MaxStaff.TryGetValue(buildingId, out int max) ? max : 0;
        }

        /// <summary>
        /// Hires a budtender for the specified counter. Deducts wage from player cash.
        /// Returns the new BudtenderInstance, or null on failure.
        /// Host-only.
        /// </summary>
        public static BudtenderInstance Hire(CheckoutCounterInstance counter)
        {
            if (counter == null)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, "BudtenderController.Hire: null counter");
                return null;
            }

            var buildingId = counter.BuildingId;
            if (!CanHire(buildingId))
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"BudtenderController.Hire: at max capacity for {buildingId}");
                return null;
            }

            // Check if counter already has a budtender
            if (!string.IsNullOrEmpty(counter.AssignedBudtenderId))
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    "BudtenderController.Hire: counter already staffed");
                return null;
            }

            // Check if player can afford
            float cash = Money.GetCashBalance();
            if (cash < DailyWage)
            {
                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"BudtenderController.Hire: can't afford ${DailyWage} (have ${cash:F0})");
                return null;
            }

            // Deduct wage
            Money.ChangeCashBalance(-DailyWage, true, true);

            // Generate ID and seed
            string id = $"budtender_{_nextId++}";
            int seed = UnityEngine.Random.Range(1000, 999999);

            var instance = BudtenderInstance.Create(id, seed, counter);
            if (instance == null)
            {
                // Refund on failure
                Money.ChangeCashBalance(DailyWage, false, false);
                return null;
            }

            // Mark as paid for today
            instance.PaidForToday = true;
            instance.PaidOnDay = S1API.GameTime.TimeManager.ElapsedDays;

            OTCLog.Msg(OTCLog.Systems.Customer,
                $"Hired budtender {id} for counter {CheckoutCounter.GetCounterIndex(counter)} in {buildingId}");

            // Sync to clients
            ConfigSyncData.MarkGameStateDirty();

            return instance;
        }

        /// <summary>
        /// Fires a budtender. No refund. Host-only.
        /// </summary>
        public static void Fire(string budtenderId)
        {
            if (!BudtenderInstance.Active.TryGetValue(budtenderId, out var bt))
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"BudtenderController.Fire: budtender {budtenderId} not found");
                return;
            }

            OTCLog.Msg(OTCLog.Systems.Customer, $"Firing budtender {budtenderId}");
            bt.GracefulDespawn();

            // Sync to clients
            ConfigSyncData.MarkGameStateDirty();
        }

        // =================================================================
        //  Tick — called from Core.OnLateUpdate
        // =================================================================

        /// <summary>
        /// Ticks all active budtenders. Host-only.
        /// </summary>
        public static void Tick()
        {
            if (!NetworkHelper.IsHost) return;

            // Check for day change — wage deduction
            CheckDayChange();

            // Check operating hours — send home / call in
            CheckOperatingHours();

            // Tick each budtender (snapshot to avoid collection-modified during tick)
            var snapshot = new List<BudtenderInstance>(BudtenderInstance.Active.Values);
            foreach (var bt in snapshot)
            {
                try
                {
                    bt.Tick();
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"BudtenderController.Tick: error for {bt.Id}: {ex.Message}");
                }
            }

            // Tick fired budtenders walking out
            BudtenderInstance.TickLeaving();
        }

        /// <summary>
        /// Returns true if the current game time is within budtender operating hours.
        /// </summary>
        public static bool IsWithinOperatingHours()
        {
            int currentMinutes = TimeManager.CurrentTime;
            // CurrentTime is HHMM format (e.g. 730 = 7:30 AM)
            int hours = currentMinutes / 100;
            int mins = currentMinutes % 100;
            int totalMinutes = hours * 60 + mins;
            return totalMinutes >= ShiftStartMinute && totalMinutes < ShiftEndMinute;
        }

        private static void CheckOperatingHours()
        {
            bool onDuty = IsWithinOperatingHours();

            foreach (var bt in new List<BudtenderInstance>(BudtenderInstance.Active.Values))
            {
                if (!onDuty && bt.State != BudtenderState.Off && bt.State != BudtenderState.LeavingBuilding)
                {
                    bt.SendHome();
                }
                else if (onDuty && bt.State == BudtenderState.Off && bt.GameNpc == null)
                {
                    bt.CallIn();
                }
            }
        }

        // =================================================================
        //  Wage system
        // =================================================================

        private static void CheckDayChange()
        {
            int currentDay = S1API.GameTime.TimeManager.ElapsedDays;
            if (_lastWageDay == currentDay) return;
            if (_lastWageDay < 0)
            {
                // First tick — just record the day, don't charge
                _lastWageDay = currentDay;
                return;
            }

            _lastWageDay = currentDay;

            // New day — charge wages for all active budtenders
            var toFire = new List<string>();
            foreach (var bt in BudtenderInstance.Active.Values)
            {
                if (bt.PaidOnDay == currentDay) continue; // already paid (save/reload)

                float cash = Money.GetCashBalance();
                if (cash >= DailyWage)
                {
                    Money.ChangeCashBalance(-DailyWage, true, false);
                    bt.PaidForToday = true;
                    bt.PaidOnDay = currentDay;
                    OTCLog.Msg(OTCLog.Systems.Customer,
                        $"Budtender {bt.Id}: daily wage ${DailyWage} deducted");
                }
                else
                {
                    OTCLog.Msg(OTCLog.Systems.Customer,
                        $"Budtender {bt.Id}: can't afford wage, firing");
                    toFire.Add(bt.Id);
                }
            }

            foreach (var id in toFire)
                Fire(id);
        }

        // =================================================================
        //  Serialization (for save/load and multiplayer sync)
        // =================================================================

        /// <summary>
        /// Serializes all active budtenders and counter enabled state to a string.
        /// Format: "counterIndex:seed:paidOnDay;counterIndex:seed:paidOnDay;disabled=idx,idx"
        /// </summary>
        public static string Serialize()
        {
            var parts = new List<string>();

            foreach (var bt in BudtenderInstance.Active.Values)
            {
                int counterIdx = CheckoutCounter.GetCounterIndex(bt.AssignedCounter);
                if (counterIdx < 0) continue;
                parts.Add($"{counterIdx}:{bt.Seed}:{bt.PaidOnDay}");
            }

            // Serialize disabled counter indices
            var disabledIndices = new List<string>();
            var allCounters = CheckoutCounter.AllCounters;
            for (int i = 0; i < allCounters.Count; i++)
            {
                if (!allCounters[i].IsEnabled)
                    disabledIndices.Add(i.ToString());
            }
            if (disabledIndices.Count > 0)
                parts.Add($"disabled={string.Join(",", disabledIndices)}");

            return string.Join(";", parts);
        }

        /// <summary>
        /// Deserializes and respawns budtenders from save data.
        /// Host-only.
        /// </summary>
        public static void Deserialize(string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            int currentDay = S1API.GameTime.TimeManager.ElapsedDays;
            var entries = data.Split(';');

            foreach (var entry in entries)
            {
                // Legacy shelf flag — ignore (removed in recommendation redesign)
                if (entry.StartsWith("shelf=")) continue;

                // Parse disabled counter indices
                if (entry.StartsWith("disabled="))
                {
                    var idxStr = entry.Substring("disabled=".Length);
                    foreach (var idx in idxStr.Split(','))
                    {
                        if (int.TryParse(idx, out int ci))
                        {
                            var c = CheckoutCounter.GetCounterByIndex(ci);
                            if (c != null) c.IsEnabled = false;
                        }
                    }
                    continue;
                }

                var fields = entry.Split(':');
                if (fields.Length < 3) continue;

                if (!int.TryParse(fields[0], out int counterIdx)) continue;
                if (!int.TryParse(fields[1], out int seed)) continue;
                if (!int.TryParse(fields[2], out int paidOnDay)) continue;

                var counter = CheckoutCounter.GetCounterByIndex(counterIdx);
                if (counter == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"BudtenderController.Deserialize: counter {counterIdx} not found");
                    continue;
                }

                // Skip if counter already staffed
                if (!string.IsNullOrEmpty(counter.AssignedBudtenderId)) continue;

                string id = $"budtender_{_nextId++}";
                var instance = BudtenderInstance.Create(id, seed, counter);
                if (instance != null)
                {
                    instance.PaidOnDay = paidOnDay;
                    instance.PaidForToday = (paidOnDay == currentDay);
                    OTCLog.Msg(OTCLog.Systems.Customer,
                        $"Restored budtender {id} at counter {counterIdx} (paidOnDay={paidOnDay})");
                }
            }

            _lastWageDay = currentDay;
        }

        /// <summary>Resets state on scene unload.</summary>
        public static void Reset()
        {
            BudtenderInstance.CleanupAll();
            _nextId = 0;
            _lastWageDay = -1;
        }
    }
}
