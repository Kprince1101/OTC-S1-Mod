using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.Utilities;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.UI.Handover;
using Il2CppTMPro;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Product;
using ScheduleOne.UI.Handover;
using TMPro;
#endif

namespace OverTheCounter.UI
{
    /// <summary>
    /// Floating overlay shown on the HandoverScreen (Contract mode only).
    /// First click: bare minimum fill (smallest packaging, exact quality preferred).
    /// Second click: boost — adds more product until acceptance >= 95%.
    /// </summary>
    public static class HandoverFillUI
    {
        private static GameObject _overlayRoot;
        private static TextMeshProUGUI _statusText;
        private static bool _hasFilled;

        public static void Show()
        {
            if (_overlayRoot != null)
            {
                UnityEngine.Object.Destroy(_overlayRoot);
                _overlayRoot = null;
            }

            _hasFilled = false;
            BuildUI();
        }

        public static void Hide()
        {
            if (_overlayRoot != null)
            {
                UnityEngine.Object.Destroy(_overlayRoot);
                _overlayRoot = null;
            }
            _statusText = null;
            _hasFilled = false;
        }

        private static void BuildUI()
        {
            var handover = Singleton<HandoverScreen>.Instance;
            if (handover == null || handover.DoneButton == null) return;

            var doneRect = handover.DoneButton.GetComponent<RectTransform>();
            if (doneRect == null || doneRect.parent == null) return;

            // Parent into the game's handover UI as a sibling of the DoneButton.
            // This keeps Smart Fill aligned with the DONE button at any UI scale.
            _overlayRoot = new GameObject("OTC_SmartFillPanel");
            _overlayRoot.transform.SetParent(doneRect.parent, false);

            var panelRect = _overlayRoot.AddComponent<RectTransform>();
            panelRect.anchorMin = doneRect.anchorMin;
            panelRect.anchorMax = doneRect.anchorMax;
            panelRect.pivot = doneRect.pivot;
            panelRect.sizeDelta = new Vector2(160f, 40f);
            panelRect.anchoredPosition = doneRect.anchoredPosition + new Vector2(0, 50);

            // Smart Fill button
            var (btnMask, btn, btnLabel) = TMPFactory.RoundedButtonWithLabel(
                "SmartFillBtn", "Smart Fill", _overlayRoot.transform,
                new Color(0.2f, 0.5f, 0.2f), 140, 32, 14, Color.white
            );

            var btnRect = btnMask.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 1f);
            btnRect.anchorMax = new Vector2(0.5f, 1f);
            btnRect.pivot = new Vector2(0.5f, 1f);
            btnRect.anchoredPosition = new Vector2(0, -6);

            var btnColors = btn.colors;
            btnColors.normalColor = new Color(0.2f, 0.5f, 0.2f);
            btnColors.highlightedColor = new Color(0.3f, 0.6f, 0.3f);
            btnColors.pressedColor = new Color(0.15f, 0.35f, 0.15f);
            btnColors.selectedColor = new Color(0.2f, 0.5f, 0.2f);
            btn.colors = btnColors;

            btn.onClick.AddListener(new Action(OnSmartFillClicked));

            // Status text below button (rich text enabled for bold)
            _statusText = TMPFactory.Text("StatusText", "", _overlayRoot.transform, 15, TextAlignmentOptions.Center);
            _statusText.richText = true;
            _statusText.color = new Color(0.7f, 0.7f, 0.7f);
            var statusRect = _statusText.gameObject.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0, 0);
            statusRect.anchorMax = new Vector2(1, 0);
            statusRect.pivot = new Vector2(0.5f, 0);
            statusRect.anchoredPosition = new Vector2(0, 4);
            statusRect.sizeDelta = new Vector2(0, 22);

            _overlayRoot.SetActive(true);
        }

        private static void OnSmartFillClicked()
        {
            try
            {
                var handover = Singleton<HandoverScreen>.Instance;
                if (handover == null) { SetStatus("Handover screen not found"); return; }

                var contract = handover.CurrentContract;
                if (contract?.ProductList?.entries == null) { SetStatus("No contract"); return; }

                var customerSlots = handover.GetCustomerSlots();
                if (customerSlots == null || customerSlots.Length == 0) { SetStatus("No customer slots"); return; }

                var playerInv = PlayerSingleton<PlayerInventory>.Instance;
                if (playerInv?.hotbarSlots == null) { SetStatus("Inventory unavailable"); return; }

                if (_hasFilled)
                    DoBoostFill(handover, contract, customerSlots, playerInv);
                else
                    DoMinimumFill(handover, contract, customerSlots, playerInv);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Smart Fill error: {ex.Message}");
                SetStatus("Error!", true);
            }
        }

        /// <summary>
        /// First click: fill the bare minimum to meet the contract gram requirements.
        /// Prefers smallest packaging and exact quality match.
        /// </summary>
        private static void DoMinimumFill(HandoverScreen handover, ScheduleOne.Quests.Contract contract,
            ItemSlot[] customerSlots, PlayerInventory playerInv)
        {
            var hotbar = playerInv.hotbarSlots;
            var entries = contract.ProductList.entries;
            int totalPlaced = 0;
            int totalGramsRequested = 0;
            int totalGramsPlaced = 0;
            bool slotsFull = false;
            bool qualityMismatch = false;

            for (int e = 0; e < entries.Count; e++)
            {
                var entry = entries[e];
                if (entry == null || string.IsNullOrEmpty(entry.ProductID)) continue;

                int remainingGrams = entry.Quantity;
                totalGramsRequested += entry.Quantity;
                EQuality requestedQuality = entry.Quality;

                var matchingSlots = new List<(int index, int multiplier, EQuality quality)>();
                for (int h = 0; h < hotbar.Count; h++)
                {
                    var slot = hotbar[h];
                    if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

                    string slotId;
                    try { slotId = slot.ItemInstance.ID; }
                    catch { continue; }

                    if (slotId != entry.ProductID) continue;

                    int mult = ContractAggregator.GetPackagingMultiplier(slot.ItemInstance);
                    EQuality slotQuality = EQuality.Standard;
                    var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
                    if (productItem != null)
                        slotQuality = productItem.Quality;

                    matchingSlots.Add((h, mult, slotQuality));
                }

                // Sort: exact quality match > closest above > highest below
                // Within same quality, smallest packaging first (precise filling)
                matchingSlots.Sort((a, b) =>
                {
                    int aPriority = GetQualityPriority(a.quality, requestedQuality);
                    int bPriority = GetQualityPriority(b.quality, requestedQuality);
                    if (aPriority != bPriority) return aPriority.CompareTo(bPriority);
                    return a.multiplier.CompareTo(b.multiplier);
                });

                foreach (var (slotIdx, multiplier, slotQuality) in matchingSlots)
                {
                    if (remainingGrams <= 0) break;

                    var sourceSlot = hotbar[slotIdx];
                    if (sourceSlot?.ItemInstance == null || sourceSlot.Quantity <= 0) continue;

                    if (slotQuality != requestedQuality)
                        qualityMismatch = true;

                    // Round up so we don't skip a 5g jar when only 4g is needed
                    int itemsNeeded = (remainingGrams + multiplier - 1) / multiplier;
                    int itemsToTake = Math.Min(itemsNeeded, sourceSlot.Quantity);
                    if (itemsToTake <= 0) continue;

                    int placed = TryPlaceInCustomerSlots(customerSlots, sourceSlot, itemsToTake, handover);

                    if (placed <= 0) { slotsFull = true; break; }

                    totalGramsPlaced += placed * multiplier;
                    remainingGrams -= placed * multiplier;
                    totalPlaced += placed;
                }

                if (slotsFull) break;
            }

            _hasFilled = totalPlaced > 0;

            if (totalPlaced == 0)
            {
                SetStatus(slotsFull ? "Customer slots full" : "No matching products", true);
                return;
            }

            string msg = $"{totalGramsPlaced}g/{totalGramsRequested}g";
            bool warn = false;

            if (totalGramsPlaced > totalGramsRequested) { msg += " (over)"; warn = true; }
            else if (slotsFull) { msg += " (slots full)"; warn = true; }

            if (qualityMismatch) { msg += " ~quality"; warn = true; }

            SetStatus(msg, warn);
        }

        /// <summary>
        /// Second click: boost fill. Reads current acceptance % from the contract,
        /// and keeps adding matching product until acceptance >= 95% or inventory is empty.
        /// </summary>
        private static void DoBoostFill(HandoverScreen handover, ScheduleOne.Quests.Contract contract,
            ItemSlot[] customerSlots, PlayerInventory playerInv)
        {
            // Check current match
            var currentItems = GetCustomerItemsList(customerSlots);
            int matchedCount;
            float match = contract.GetProductListMatch(currentItems, out matchedCount);

            if (match >= 0.95f)
            {
                int pct = Mathf.RoundToInt(match * 100f);
                SetStatus($"{pct}% — already good");
                return;
            }

            var hotbar = playerInv.hotbarSlots;
            var entries = contract.ProductList.entries;
            int totalAdded = 0;
            bool slotsFull = false;

            // Keep adding product until >= 95% or we run out
            for (int pass = 0; pass < 50 && match < 0.95f; pass++)
            {
                bool addedThisPass = false;

                for (int e = 0; e < entries.Count; e++)
                {
                    var entry = entries[e];
                    if (entry == null || string.IsNullOrEmpty(entry.ProductID)) continue;

                    // Find a matching hotbar slot (smallest packaging first)
                    for (int h = 0; h < hotbar.Count; h++)
                    {
                        var slot = hotbar[h];
                        if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

                        string slotId;
                        try { slotId = slot.ItemInstance.ID; }
                        catch { continue; }

                        if (slotId != entry.ProductID) continue;

                        int placed = TryPlaceInCustomerSlots(customerSlots, slot, 1, handover);
                        if (placed <= 0) { slotsFull = true; break; }

                        totalAdded += placed;
                        addedThisPass = true;

                        // Re-check match
                        currentItems = GetCustomerItemsList(customerSlots);
                        match = contract.GetProductListMatch(currentItems, out matchedCount);
                        if (match >= 0.95f) break;
                    }

                    if (match >= 0.95f || slotsFull) break;
                }

                if (!addedThisPass || match >= 0.95f || slotsFull) break;
            }

            int finalPct = Mathf.RoundToInt(match * 100f);
            if (totalAdded == 0)
            {
                SetStatus(slotsFull ? $"{finalPct}% — slots full" : $"{finalPct}% — no more product", true);
            }
            else if (match >= 0.95f)
            {
                SetStatus($"+{totalAdded} boost → {finalPct}%");
            }
            else
            {
                SetStatus($"+{totalAdded} boost → {finalPct}%", true);
            }
        }

        /// <summary>
        /// Gathers all packaged product items currently in the customer slots.
        /// Mirrors HandoverScreen.GetCustomerItems().
        /// Returns the IL2CPP list type on IL2CPP builds, System list on Mono.
        /// </summary>
#if IL2CPP
        private static Il2CppSystem.Collections.Generic.List<ScheduleOne.ItemFramework.ItemInstance> GetCustomerItemsList(ItemSlot[] slots)
        {
            var list = new Il2CppSystem.Collections.Generic.List<ScheduleOne.ItemFramework.ItemInstance>();
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i]?.ItemInstance == null) continue;
                var pi = slots[i].ItemInstance.TryCast<ProductItemInstance>();
                if (pi != null && pi.AppliedPackaging != null)
                    list.Add(slots[i].ItemInstance);
            }
            return list;
        }
#else
        private static List<ScheduleOne.ItemFramework.ItemInstance> GetCustomerItemsList(ItemSlot[] slots)
        {
            var list = new List<ScheduleOne.ItemFramework.ItemInstance>();
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i]?.ItemInstance == null) continue;
                var pi = slots[i].ItemInstance as ProductItemInstance;
                if (pi != null && pi.AppliedPackaging != null)
                    list.Add(slots[i].ItemInstance);
            }
            return list;
        }
#endif

        private static int TryPlaceInCustomerSlots(
            ItemSlot[] customerSlots, HotbarSlot sourceSlot,
            int amount, HandoverScreen handover)
        {
            int placed = 0;
            var sourceItem = sourceSlot.ItemInstance;

            // First pass: stack into existing matching customer slots
            for (int i = 0; i < customerSlots.Length && placed < amount; i++)
            {
                var cs = customerSlots[i];
                if (cs?.ItemInstance == null) continue;

                try
                {
                    if (!cs.ItemInstance.CanStackWith(sourceItem, false)) continue;

                    int stackLimit;
                    try { stackLimit = cs.ItemInstance.StackLimit; }
                    catch { stackLimit = 20; }

                    int canAdd = stackLimit - cs.Quantity;
                    if (canAdd <= 0) continue;

                    int toAdd = Math.Min(canAdd, amount - placed);
                    cs.ChangeQuantity(toAdd);
                    placed += toAdd;
                }
                catch { }
            }

            // Second pass: place into empty customer slots
            for (int i = 0; i < customerSlots.Length && placed < amount; i++)
            {
                var cs = customerSlots[i];
                if (cs == null || cs.ItemInstance != null) continue;

                try
                {
                    int toPlace = amount - placed;

                    int stackLimit;
                    try { stackLimit = sourceItem.StackLimit; }
                    catch { stackLimit = 20; }

                    toPlace = Math.Min(toPlace, stackLimit);

                    var clone = sourceItem.GetCopy(toPlace);
                    cs.SetStoredItem(clone);

                    handover.TrackItemAsPlayer(clone);

                    placed += toPlace;
                }
                catch { }
            }

            // Remove placed items from source hotbar slot
            if (placed > 0)
            {
                try
                {
                    if (placed >= sourceSlot.Quantity)
                        sourceSlot.ClearStoredInstance();
                    else
                        sourceSlot.ChangeQuantity(-placed);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, $"Error updating source slot: {ex.Message}");
                }
            }

            return placed;
        }

        private static int GetQualityPriority(EQuality itemQuality, EQuality requested)
        {
            if (itemQuality == requested) return 0;
            if (itemQuality > requested) return 1 + (itemQuality - requested);
            return 100 + (requested - itemQuality);
        }

        private static void SetStatus(string message, bool warn = false)
        {
            if (_statusText != null)
            {
                _statusText.text = warn ? $"<b>{message}</b>" : message;
                _statusText.color = warn
                    ? new Color(1f, 0.7f, 0.2f)
                    : new Color(0.7f, 0.7f, 0.7f);
            }
        }
    }
}
