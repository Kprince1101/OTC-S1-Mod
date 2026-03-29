using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.GameTime;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Product;
using Customer = Il2CppScheduleOne.Economy.Customer;
using EDrugType = Il2CppScheduleOne.Product.EDrugType;
using NPCSpeedController = Il2CppScheduleOne.NPCs.NPCSpeedController;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Economy;
using ScheduleOne.Map;
using ScheduleOne.NPCs;
using ScheduleOne.Product;
using Customer = ScheduleOne.Economy.Customer;
using EDrugType = ScheduleOne.Product.EDrugType;
using NPCSpeedController = ScheduleOne.NPCs.NPCSpeedController;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Manages deal-driven customer redirection. When vanilla NPCs' deal cooldowns fire,
    /// this class decides whether to redirect them to an OTC building instead of texting.
    /// Handles warp point selection, operating hours, speed boosts, and post-sale rewards.
    /// </summary>
    internal static class DispensaryDealManager
    {
        private const int OpenHour = 8;
        private const int CloseHour = 20;
        private const int CloseMinute = CloseHour * 60; // 1200
        private const int RushThresholdMinutes = 60;     // speed boost when < 60 min to close
        private const int CutoffMinutes = 30;            // don't redirect when < 30 min to close

        private const float RushSpeed = 0.4f; // same as RequestProductBehaviour fast walk
        private const string RushSpeedId = "otc_deal_rush";

        private const int DealXP = 20;
        private const int PreOpenWindowMinutes = 60; // defer deals within 1 hour before open

        /// <summary>Tracks active deal customer IDs for cleanup.</summary>
        internal static readonly HashSet<string> ActiveDealCustomerIds = new();

        /// <summary>Tracks vanilla NPC IDs that have been redirected to prevent duplicate redirects.</summary>
        private static readonly HashSet<string> RedirectedNpcIds = new();

        /// <summary>Deals deferred to opening time (NPCs whose cooldown fired in the pre-open window).</summary>
        private static readonly List<DeferredDeal> _deferredDeals = new();

        /// <summary>Number of customers redirected to the shack today.</summary>
        private static int _shackDailyCount;

        private struct DeferredDeal
        {
            public Customer Customer;
            public EDrugType DrugType;
        }

        /// <summary>Returns true if this vanilla NPC ID has already been redirected.</summary>
        internal static bool IsNpcRedirected(string npcId) => RedirectedNpcIds.Contains(npcId);

        // ─────────────────────────────────────────────────────────
        //  Building selection
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Finds the best available OTC building that stocks a matching drug type.
        /// Dispensary is always preferred over Shack when both are available.
        /// Shack is region-locked to Northtown/Westville and has a daily customer cap.
        /// Returns null if no building qualifies.
        /// </summary>
        internal static BuildingTarget FindAvailableBuilding(EDrugType drugType, EMapRegion npcRegion)
        {
            // Check Dispensary first (preferred) — no region lock, no daily cap
            if (PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.DispensaryId) == true
                && Dispensary.IsStoreOpen
                && Dispensary.Target != null
                && PropertyInventory.HasDrugType(Dispensary.DispensaryGrid, drugType))
            {
                return Dispensary.Target;
            }

            // Fall back to Shack — region-locked + daily cap
            if (PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.ShackId) == true
                && WestvilleShack.IsStoreOpen
                && WestvilleShack.Target != null
                && IsRegionAllowedForShack(npcRegion)
                && _shackDailyCount < Config.ShackDailyCustomerCap.Value
                && PropertyInventory.HasDrugType(WestvilleShack.ShackGrid, drugType))
            {
                return WestvilleShack.Target;
            }

            return null;
        }

        /// <summary>
        /// Returns true if the NPC's region is allowed at the Westville Shack.
        /// Only Northtown and Westville NPCs can shop at the shack.
        /// </summary>
        private static bool IsRegionAllowedForShack(EMapRegion region)
        {
            return region == EMapRegion.Northtown || region == EMapRegion.Westville;
        }

        // ─────────────────────────────────────────────────────────
        //  Operating hours
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if we're within operating hours and have enough time
        /// before closing to redirect an NPC (at least 30 min before close).
        /// </summary>
        internal static bool IsWithinOperatingHours()
        {
            int time = TimeManager.CurrentTime; // HHMM format (e.g. 1930 = 7:30 PM)
            int hour = time / 100;
            int minute = time % 100;
            int currentMinutes = hour * 60 + minute;

            return currentMinutes >= OpenHour * 60
                && currentMinutes < CloseMinute - CutoffMinutes;
        }

        /// <summary>
        /// Returns true if the NPC should get a speed boost (30-60 min before close).
        /// </summary>
        internal static bool ShouldRush()
        {
            int time = TimeManager.CurrentTime;
            int hour = time / 100;
            int minute = time % 100;
            int currentMinutes = hour * 60 + minute;
            int minutesToClose = CloseMinute - currentMinutes;

            return minutesToClose > CutoffMinutes && minutesToClose <= RushThresholdMinutes;
        }

        // ─────────────────────────────────────────────────────────
        //  Pre-open deferral
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the current time is within the pre-open window (e.g. 7:00-7:59)
        /// and at least one OTC store has its open switch on.
        /// </summary>
        internal static bool IsInPreOpenWindow()
        {
            int time = TimeManager.CurrentTime;
            int hour = time / 100;
            int minute = time % 100;
            int currentMinutes = hour * 60 + minute;
            int openMinutes = OpenHour * 60;

            if (currentMinutes < openMinutes - PreOpenWindowMinutes || currentMinutes >= openMinutes)
                return false;

            // At least one store must have its open switch on
            return WestvilleShack.IsStoreOpen || Dispensary.IsStoreOpen;
        }

        /// <summary>
        /// Returns true if any open building could serve this NPC's region at opening time.
        /// </summary>
        private static bool CouldAnyBuildingServe(EMapRegion npcRegion)
        {
            // Dispensary has no region lock — any NPC can go there
            if (PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.DispensaryId) == true
                && Dispensary.IsStoreOpen)
                return true;

            // Shack is region-locked
            if (PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.ShackId) == true
                && WestvilleShack.IsStoreOpen
                && IsRegionAllowedForShack(npcRegion))
                return true;

            return false;
        }

        /// <summary>
        /// Queues a deal for redirect at opening time. Resets the vanilla cooldown so
        /// the NPC doesn't text for a deal in the meantime.
        /// </summary>
        internal static bool DeferDeal(Customer customer, EDrugType drugType)
        {
            // Don't defer if no building could serve this NPC's region
            if (!CouldAnyBuildingServe(customer.NPC.Region))
                return false;

            _deferredDeals.Add(new DeferredDeal { Customer = customer, DrugType = drugType });
            ResetDealCooldown(customer);

            OTCLog.Msg(OTCLog.Systems.Customer,
                $"Deferred deal for {customer.NPC?.fullName} ({drugType}) until {OpenHour}:00");
            return true;
        }

        /// <summary>
        /// Processes all deferred deals — redirects queued NPCs to available buildings.
        /// Called from CustomerManager when the store opens (8:00).
        /// </summary>
        internal static void ProcessDeferredDeals()
        {
            if (_deferredDeals.Count == 0) return;

            OTCLog.Msg(OTCLog.Systems.Customer,
                $"Processing {_deferredDeals.Count} deferred deal(s) at opening time");

            for (int i = 0; i < _deferredDeals.Count; i++)
            {
                var deal = _deferredDeals[i];
                try
                {
                    if (deal.Customer?.NPC == null) continue;
                    if (IsNpcRedirected(deal.Customer.NPC.ID)) continue;

                    var target = FindAvailableBuilding(deal.DrugType, deal.Customer.NPC.Region);
                    if (target == null)
                    {
                        OTCLog.Msg(OTCLog.Systems.Customer,
                            $"Deferred deal for {deal.Customer.NPC.fullName}: no building available, skipping");
                        continue;
                    }

                    Redirect(deal.Customer, deal.DrugType, target);
                }
                catch (System.Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Customer,
                        $"Failed to process deferred deal: {ex.Message}");
                }
            }

            _deferredDeals.Clear();
        }

        // ─────────────────────────────────────────────────────────
        //  NPC redirect
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Redirects a vanilla Customer NPC to an OTC building. Warps them to a random
        /// nearby warp point and creates a CustomerInstance to manage their visit.
        /// Called from the Harmony prefix when a deal is intercepted.
        /// </summary>
        internal static bool Redirect(Customer vanillaCustomer, EDrugType drugType, BuildingTarget target)
        {
            if (vanillaCustomer?.NPC?.Movement == null)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, "Redirect failed: vanilla customer or movement is null");
                return false;
            }

            // Find warp point
            var warpPos = GetRandomNearbyWarpPoint(target.BuildingPosition);
            if (!warpPos.HasValue)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"Redirect failed: no warp points found near {target.Name}");
                return false;
            }

            // Track this NPC as redirected
            RedirectedNpcIds.Add(vanillaCustomer.NPC.ID);

            // Suppress vanilla NPC behavior system BEFORE warping — prevents schedule/AI
            // from overriding our navigation with SetDestination calls to home/activities
            SuppressVanillaBehaviour(vanillaCustomer.NPC);

            // Reset deal cooldown immediately so ShouldTryGenerateDeal() returns false next tick
            ResetDealCooldown(vanillaCustomer);

            // Capture original position BEFORE warp — NPC walks back here after checkout
            var preWarpPosition = vanillaCustomer.NPC.transform.position;

            // Warp NPC to the chosen point
            vanillaCustomer.NPC.Movement.Warp(warpPos.Value);

            // Apply rush speed if close to closing time
            if (ShouldRush())
            {
                var speedControl = new NPCSpeedController.SpeedControl(RushSpeedId, 5, RushSpeed);
                vanillaCustomer.NPC.Movement.SpeedController.AddSpeedControl(speedControl);
            }

            // Create deal CustomerInstance wrapping the vanilla NPC
            var customer = CustomerInstance.CreateFromDealNPC(vanillaCustomer, target, preWarpPosition);
            if (customer == null)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"Redirect failed: CreateFromDealNPC returned null for {vanillaCustomer.NPC.fullName}");
                return false;
            }

            ActiveDealCustomerIds.Add(customer.Id);

            // Track shack daily count
            if (target.Name == "WestvilleShack")
                _shackDailyCount++;

            // Start walking to the building entrance
            customer.WalkTo(target.ExteriorApproachPosition);

            OTCLog.Msg(OTCLog.Systems.Customer,
                $"Redirected {vanillaCustomer.NPC.fullName} to {target.Name} for {drugType} deal (warp: {warpPos.Value}, shackDaily: {_shackDailyCount})");

            return true;
        }

        // ─────────────────────────────────────────────────────────
        //  Warp point selection
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Gets a random warp point from the 5 closest to the target position.
        /// Returns null if NPCManager or warp points are unavailable.
        /// </summary>
        private static Vector3? GetRandomNearbyWarpPoint(Vector3 targetPosition)
        {
            try
            {
                var npcManager = NetworkSingleton<NPCManager>.Instance;
                if (npcManager == null) return null;

                var warpPoints = npcManager.GetOrderedDistanceWarpPoints(targetPosition);
                if (warpPoints == null || warpPoints.Count == 0) return null;

                int count = Mathf.Min(5, warpPoints.Count);
                var chosen = warpPoints[UnityEngine.Random.Range(0, count)];
                return chosen != null ? chosen.position : (Vector3?)null;
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"GetRandomNearbyWarpPoint failed: {ex.Message}");
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────
        //  Post-sale rewards
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Applies deal completion rewards: XP, relationship change, tip deposit,
        /// and cooldown reset. Called after a deal customer finishes checkout.
        /// </summary>
        internal static void ApplyDealRewards(CustomerInstance customer, float saleTotal)
        {
            if (!customer.IsDealCustomer || customer.VanillaCustomer == null)
                return;

            var vanillaCustomer = customer.VanillaCustomer;

            // XP — same as vanilla player handover
            try
            {
#if IL2CPP
                var levelManager = Il2CppScheduleOne.DevUtilities.NetworkSingleton<
                    Il2CppScheduleOne.Levelling.LevelManager>.Instance;
#else
                var levelManager = ScheduleOne.DevUtilities.NetworkSingleton<
                    ScheduleOne.Levelling.LevelManager>.Instance;
#endif
                levelManager?.AddXP(DealXP);
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"AddXP failed: {ex.Message}");
            }

            // Relationship — based on satisfaction (effect match count)
            try
            {
                float satisfaction = CalculateSatisfaction(customer);
                float relChange = Mathf.Lerp(-0.5f, 0.5f, satisfaction) * 0.2f;
                vanillaCustomer.NPC?.RelationData?.ChangeRelationship(relChange);
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"Relationship change failed: {ex.Message}");
            }

            // Tip — based on preferred effect matches
            float tip = CalculateTip(customer, saleTotal);
            if (tip > 0f && customer.AssignedCounter != null)
            {
                customer.AssignedCounter.DepositToRegister(tip);
                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"Deal tip: ${tip:F2} from {vanillaCustomer.NPC?.fullName}");
            }

            // Reset deal cooldown
            ResetDealCooldown(vanillaCustomer);

            OTCLog.Msg(OTCLog.Systems.Customer,
                $"Deal rewards applied for {vanillaCustomer.NPC?.fullName}: XP={DealXP}, tip=${tip:F2}, sale=${saleTotal:F2}");
        }

        /// <summary>
        /// Resets deal cooldown for a deal customer who left without buying.
        /// They still made the trip, so the cooldown resets.
        /// </summary>
        internal static void ApplyDealCooldownOnly(CustomerInstance customer)
        {
            if (!customer.IsDealCustomer || customer.VanillaCustomer == null)
                return;

            ResetDealCooldown(customer.VanillaCustomer);
        }

        /// <summary>
        /// Returns the tip amount for a deal customer. Non-deal customers return 0.
        /// </summary>
        internal static float GetTipAmount(CustomerInstance customer, float saleTotal)
        {
            if (customer == null || !customer.IsDealCustomer) return 0f;
            return CalculateTip(customer, saleTotal);
        }

        /// <summary>
        /// Calculates tip as a percentage of sale total based on preferred effect matches.
        /// 0 matches = 0%, 1 = 25%, 2 = 50%, 3 = 100%.
        /// </summary>
        private static float CalculateTip(CustomerInstance customer, float saleTotal)
        {
            int effectMatches = CountEffectMatches(customer);
            float tipPercent = effectMatches switch
            {
                0 => 0f,
                1 => 0.25f,
                2 => 0.50f,
                _ => 1.00f, // 3+ = double the sale
            };
            return saleTotal * tipPercent;
        }

        /// <summary>
        /// Satisfaction score for relationship calculation.
        /// 0 matches = 0.3, 1 = 0.5, 2 = 0.7, 3 = 1.0.
        /// </summary>
        private static float CalculateSatisfaction(CustomerInstance customer)
        {
            int effectMatches = CountEffectMatches(customer);
            return effectMatches switch
            {
                0 => 0.3f,
                1 => 0.5f,
                2 => 0.7f,
                _ => 1.0f,
            };
        }

        /// <summary>
        /// Counts how many of the customer's preferred effects are present in their
        /// selected products.
        /// </summary>
        private static int CountEffectMatches(CustomerInstance customer)
        {
            if (customer.Preferences.PreferredEffectIds == null || customer.SelectedProducts == null)
                return 0;

            var preferredSet = new HashSet<string>();
            foreach (var effectId in customer.Preferences.PreferredEffectIds)
            {
                if (!string.IsNullOrEmpty(effectId))
                    preferredSet.Add(effectId.ToLower());
            }

            if (preferredSet.Count == 0) return 0;

            int matches = 0;
            foreach (var selected in customer.SelectedProducts)
            {
                if (selected.EffectIds == null) continue;
                foreach (var effectId in selected.EffectIds)
                {
                    if (!string.IsNullOrEmpty(effectId) && preferredSet.Contains(effectId.ToLower()))
                        matches++;
                }
            }

            return Mathf.Min(matches, 3);
        }

        /// <summary>
        /// Resets the vanilla Customer's deal cooldown timers to 0.
        /// Resets both TimeSinceLastDealCompleted and TimeSinceLastDealOffered
        /// so ShouldTryGenerateDeal() returns false on the next tick.
        /// Uses backing field on IL2CPP, reflection on Mono.
        /// </summary>
        private static void ResetDealCooldown(Customer vanillaCustomer)
        {
            try
            {
#if IL2CPP
                vanillaCustomer._TimeSinceLastDealCompleted_k__BackingField = 0;
                vanillaCustomer._TimeSinceLastDealOffered_k__BackingField = 0;
#else
                var prop = typeof(Customer).GetProperty("TimeSinceLastDealCompleted",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                prop?.SetValue(vanillaCustomer, 0);

                var prop2 = typeof(Customer).GetProperty("TimeSinceLastDealOffered",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                prop2?.SetValue(vanillaCustomer, 0);
#endif
                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"Reset deal cooldown for {vanillaCustomer.NPC?.fullName}");
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"Failed to reset deal cooldown: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────
        //  Vanilla behaviour suppression
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Suppresses the vanilla NPC behavior system (schedules, AI) to prevent it from
        /// overriding our navigation with SetDestination calls. Pauses the active behaviour
        /// (stops OnTick/OnMinutePass ticking) and disables the NPCBehaviour component
        /// (stops Update/LateUpdate from activating new behaviours).
        /// </summary>
        private static void SuppressVanillaBehaviour(NPC npc)
        {
            try
            {
                var behaviour = npc?.Behaviour;
                if (behaviour == null) return;

                // Pause active behaviour — sets Active=false, clears activeBehaviour ref.
                // This makes OnTick/OnUncappedMinutePass no-ops (they check activeBehaviour != null).
                behaviour.activeBehaviour?.Pause();

                // Disable NPCBehaviour component — stops Update/LateUpdate so no new
                // behaviours get activated/resumed while we control the NPC.
                behaviour.enabled = false;

                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"Suppressed vanilla behaviour for {npc.fullName}");
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"Failed to suppress vanilla behaviour: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores the vanilla NPC behavior system. Re-enables the NPCBehaviour component
        /// so Update() resumes — it will naturally find the highest-priority enabled behaviour
        /// and activate/resume it on the next frame.
        /// </summary>
        private static void RestoreVanillaBehaviour(NPC npc)
        {
            try
            {
                var behaviour = npc?.Behaviour;
                if (behaviour == null) return;

                behaviour.enabled = true;

                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"Restored vanilla behaviour for {npc.fullName}");
            }
            catch (System.Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer,
                    $"Failed to restore vanilla behaviour: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────
        //  Cleanup
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Releases a deal customer's NPC back to normal vanilla behavior.
        /// Removes speed controls and restores their AI state.
        /// </summary>
        internal static void ReleaseDealNPC(CustomerInstance customer)
        {
            if (!customer.IsDealCustomer || customer.VanillaCustomer == null)
                return;

            var npc = customer.VanillaCustomer.NPC;
            if (npc?.Movement?.SpeedController != null)
            {
                // Remove any rush speed we applied
                npc.Movement.SpeedController.RemoveSpeedControl(RushSpeedId);
            }

            // If NPC is still inside a building (e.g. OnDayPass cleanup before checkout),
            // recall them so S1MAPI releases tracking, re-enables NavMeshAgent, and warps to exterior.
            // Without this, the NPC gets stuck at interior elevation with a disabled agent.
            var nav = customer.Target?.NavBuilder;
            if (nav != null && npc?.Movement != null && nav.IsNPCInside(npc.Movement))
            {
                OTCLog.Msg(OTCLog.Systems.Customer,
                    $"Deal NPC {npc.fullName} still inside building during release — recalling");
                nav.RecallNPC(npc.Movement);
            }

            // Restore vanilla NPC behavior system so schedule/AI resumes naturally
            RestoreVanillaBehaviour(npc);

            ActiveDealCustomerIds.Remove(customer.Id);
            if (npc != null)
                RedirectedNpcIds.Remove(npc.ID);

            OTCLog.Msg(OTCLog.Systems.Customer,
                $"Released deal NPC {npc?.fullName} back to vanilla behavior");
        }

        /// <summary>Resets daily counters. Called from CustomerManager.OnDayPass().</summary>
        internal static void OnDayPass()
        {
            _shackDailyCount = 0;
        }

        /// <summary>Clears all tracking state.</summary>
        internal static void Cleanup()
        {
            ActiveDealCustomerIds.Clear();
            RedirectedNpcIds.Clear();
            _deferredDeals.Clear();
            _shackDailyCount = 0;
        }
    }
}
