using OverTheCounter.Utilities;
using System;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Storage;
#else
using ScheduleOne.Economy;
using ScheduleOne.ItemFramework;
using ScheduleOne.Product;
using ScheduleOne.Storage;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// Finds the vanilla "Behind Casino" dead drop and provides product
    /// detection helpers for the Static quest line.
    /// </summary>
    public static class CasinoDeadDrop
    {
        private const string DropName = "Behind Casino";

        private static DeadDrop _deadDrop;

        /// <summary>The vanilla dead drop instance, or null if not yet found.</summary>
        public static DeadDrop Instance => _deadDrop;

        /// <summary>World position of the dead drop (for quest markers).</summary>
        public static Vector3 Position => _deadDrop != null
            ? _deadDrop.transform.position
            : new Vector3(8.48f, 1.64f, 86.61f); // fallback

        /// <summary>
        /// Finds the vanilla "Behind Casino" dead drop.
        /// Call after the game has fully loaded.
        /// </summary>
        public static void Initialize()
        {
            if (_deadDrop != null) return;

            try
            {
                var allDrops = DeadDrop.DeadDrops;
                for (int i = 0; i < allDrops.Count; i++)
                {
                    var drop = allDrops[i];
                    if (drop == null) continue;
                    if (string.Equals(drop.DeadDropName, DropName, StringComparison.OrdinalIgnoreCase))
                    {
                        _deadDrop = drop;
                        OTCLog.Msg(OTCLog.Systems.Patch,
                            $"Found vanilla dead drop '{DropName}' ({drop.Storage.SlotCount} slots)");
                        return;
                    }
                }

                OTCLog.Warning(OTCLog.Systems.Patch, $"Vanilla dead drop '{DropName}' not found");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"CasinoDeadDrop.Initialize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears the cached reference. Call on scene unload.
        /// </summary>
        public static void Cleanup()
        {
            _deadDrop = null;
        }

        // ── Product detection ────────────────────────────────────────────

        /// <summary>
        /// Counts total grams of packaged weed in the dead drop.
        /// </summary>
        public static int CountPackagedWeed()
        {
            if (_deadDrop?.Storage == null) return 0;

            int total = 0;
            try
            {
                var slots = _deadDrop.Storage.ItemSlots;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (!IsPackagedWeed(slots[i], out int multiplier)) continue;
                    total += slots[i].Quantity * multiplier;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"CountPackagedWeed failed: {ex.Message}");
            }
            return total;
        }

        /// <summary>
        /// Counts total grams of packaged meth in the dead drop.
        /// </summary>
        public static int CountPackagedMeth(EQuality minQuality = EQuality.Trash)
        {
            if (_deadDrop?.Storage == null) return 0;

            int total = 0;
            try
            {
                var slots = _deadDrop.Storage.ItemSlots;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (!IsPackagedMeth(slots[i], out int multiplier, minQuality)) continue;
                    total += slots[i].Quantity * multiplier;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"CountPackagedMeth failed: {ex.Message}");
            }
            return total;
        }

        /// <summary>
        /// Removes the specified grams of packaged weed from the dead drop.
        /// Returns true if enough product was cleared.
        /// </summary>
        public static bool ClearWeed(int grams)
        {
            if (_deadDrop?.Storage == null) return false;

            int remaining = grams;
            try
            {
                var slots = _deadDrop.Storage.ItemSlots;
                for (int i = 0; i < slots.Count && remaining > 0; i++)
                {
                    if (!IsPackagedWeed(slots[i], out int multiplier)) continue;

                    int slotGrams = slots[i].Quantity * multiplier;
                    if (slotGrams <= remaining)
                    {
                        remaining -= slotGrams;
                        slots[i].ClearStoredInstance();
                    }
                    else
                    {
                        int stacksToRemove = remaining / multiplier;
                        if (remaining % multiplier != 0) stacksToRemove++;
                        slots[i].ChangeQuantity(-stacksToRemove);
                        remaining = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"ClearWeed failed: {ex.Message}");
                return false;
            }
            return remaining <= 0;
        }

        /// <summary>
        /// Removes the specified grams of packaged meth from the dead drop.
        /// Returns true if enough product was cleared.
        /// </summary>
        public static bool ClearMeth(int grams, EQuality minQuality = EQuality.Trash)
        {
            if (_deadDrop?.Storage == null) return false;

            int remaining = grams;
            try
            {
                var slots = _deadDrop.Storage.ItemSlots;
                for (int i = 0; i < slots.Count && remaining > 0; i++)
                {
                    if (!IsPackagedMeth(slots[i], out int multiplier, minQuality)) continue;

                    int slotGrams = slots[i].Quantity * multiplier;
                    if (slotGrams <= remaining)
                    {
                        remaining -= slotGrams;
                        slots[i].ClearStoredInstance();
                    }
                    else
                    {
                        int stacksToRemove = remaining / multiplier;
                        if (remaining % multiplier != 0) stacksToRemove++;
                        slots[i].ChangeQuantity(-stacksToRemove);
                        remaining = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"ClearMeth failed: {ex.Message}");
                return false;
            }
            return remaining <= 0;
        }

        // ── Item type checks ─────────────────────────────────────────────

        private static bool IsPackagedWeed(ItemSlot slot, out int packagingQuantity)
        {
            packagingQuantity = 0;
            if (slot == null || slot.ItemInstance == null || slot.Quantity <= 0)
                return false;

            try
            {
                var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
                if (productItem == null) return false;

                var packaging = productItem.AppliedPackaging;
                if (packaging == null || packaging.Quantity <= 0) return false;

                if (productItem.Definition == null) return false;
                if (productItem.Definition.TryCast<WeedDefinition>() == null) return false;

                packagingQuantity = packaging.Quantity;
                return true;
            }
            catch { return false; }
        }

        private static bool IsPackagedMeth(ItemSlot slot, out int packagingQuantity, EQuality minQuality = EQuality.Trash)
        {
            packagingQuantity = 0;
            if (slot == null || slot.ItemInstance == null || slot.Quantity <= 0)
                return false;

            try
            {
                var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
                if (productItem == null) return false;

                var packaging = productItem.AppliedPackaging;
                if (packaging == null || packaging.Quantity <= 0) return false;

                if (productItem.Definition == null) return false;
                if (productItem.Definition.TryCast<MethDefinition>() == null) return false;
                if (productItem.Quality < minQuality) return false;

                packagingQuantity = packaging.Quantity;
                return true;
            }
            catch { return false; }
        }
    }
}
