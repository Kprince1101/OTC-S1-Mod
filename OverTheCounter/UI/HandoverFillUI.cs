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
    /// Second click: boost - adds more product until acceptance >= 95%.
    /// </summary>
    public static class HandoverFillUI
    {
        private static GameObject _overlayRoot;
        private static TextMeshProUGUI _btnLabel;
        private static Image _btnBgImage;
        private static bool _hasFilled;
        private static int _lastSlotFingerprint;
        private static Vector2 _originalDonePos;
        private static bool _donePosSaved;

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
            // Restore DONE button to its original position
            if (_donePosSaved)
            {
                var handover = Singleton<HandoverScreen>.Instance;
                if (handover?.DoneButton != null)
                {
                    var doneRect = handover.DoneButton.GetComponent<RectTransform>();
                    if (doneRect != null)
                        doneRect.anchoredPosition = _originalDonePos;
                }
            }

            if (_overlayRoot != null)
            {
                UnityEngine.Object.Destroy(_overlayRoot);
                _overlayRoot = null;
            }
            _btnLabel = null;
            _btnBgImage = null;
            _hasFilled = false;
        }

        private static void BuildUI()
        {
            var handover = Singleton<HandoverScreen>.Instance;
            if (handover == null || handover.DoneButton == null) return;

            var doneRect = handover.DoneButton.GetComponent<RectTransform>();
            if (doneRect == null || doneRect.parent == null) return;

            // Save the DONE button's original position once
            if (!_donePosSaved)
            {
                _originalDonePos = doneRect.anchoredPosition;
                _donePosSaved = true;
            }

            // Smart Fill is wider than DONE to fit status messages
            float doneW = doneRect.sizeDelta.x;
            float doneH = doneRect.sizeDelta.y;
            float fillW = doneW + 80f;
            float gap = 10f;

            // Center both buttons as a pair around the original DONE position
            doneRect.anchoredPosition = _originalDonePos + new Vector2((fillW + gap) / 2f, 0);

            // Smart Fill sits as a sibling of the DONE button
            _overlayRoot = new GameObject("OTC_SmartFillPanel");
            _overlayRoot.transform.SetParent(doneRect.parent, false);

            var panelRect = _overlayRoot.AddComponent<RectTransform>();
            panelRect.anchorMin = doneRect.anchorMin;
            panelRect.anchorMax = doneRect.anchorMax;
            panelRect.pivot = doneRect.pivot;
            panelRect.sizeDelta = new Vector2(fillW, doneH);
            panelRect.anchoredPosition = _originalDonePos - new Vector2((gap + doneW) / 2f, 0);

            // Smart Fill button - matches DONE sizing, feedback shown ON button text
            var (btnMask, btn, btnLabel) = TMPFactory.RoundedButtonWithLabel(
                "SmartFillBtn", "Smart Fill", _overlayRoot.transform,
                BtnNormalColor, (int)fillW, (int)doneH, 15, Color.white
            );

            var btnRect = btnMask.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 0.5f);
            btnRect.anchorMax = new Vector2(0.5f, 0.5f);
            btnRect.pivot = new Vector2(0.5f, 0.5f);
            btnRect.anchoredPosition = Vector2.zero;

            var btnColors = btn.colors;
            btnColors.normalColor = BtnNormalColor;
            btnColors.highlightedColor = new Color(0.3f, 0.6f, 0.3f);
            btnColors.pressedColor = new Color(0.15f, 0.35f, 0.15f);
            btnColors.selectedColor = BtnNormalColor;
            btn.colors = btnColors;

            _btnLabel = btnLabel;
            _btnLabel.richText = true;
            _btnBgImage = btn.GetComponent<Image>();

            btn.onClick.AddListener(new Action(OnSmartFillClicked));

            _overlayRoot.SetActive(true);
        }

        private static void OnSmartFillClicked()
        {
            try
            {
                var handover = Singleton<HandoverScreen>.Instance;
                if (handover == null) { SetStatus("No screen", true); return; }

                var contract = handover.CurrentContract;
                if (contract?.ProductList?.entries == null) { SetStatus("No contract", true); return; }

                var customerSlots = handover.GetCustomerSlots();
                if (customerSlots == null || customerSlots.Length == 0) { SetStatus("No slots", true); return; }

                var playerInv = PlayerSingleton<PlayerInventory>.Instance;
                if (playerInv?.hotbarSlots == null) { SetStatus("No inventory", true); return; }

                // Reset to minimum fill if player changed customer slots since last fill
                int fp = SlotFingerprint(customerSlots);
                if (fp != _lastSlotFingerprint)
                    _hasFilled = false;

                // Check if already fulfilled before doing any work
                var currentItems = GetCustomerItemsList(customerSlots);
                int mc;
                float currentMatch = contract.GetProductListMatch(currentItems, out mc);
                if (currentMatch >= 0.95f)
                {
                    SetStatus("Fulfilled");
                    return;
                }

                OTCLog.Msg(OTCLog.Systems.Patch, $"Smart Fill: entries={contract.ProductList.entries.Count}, " +
                    $"hotbar={playerInv.hotbarSlots.Count}, customerSlots={customerSlots.Length}, boost={_hasFilled}");

                if (_hasFilled)
                    DoBoostFill(handover, contract, customerSlots, playerInv);
                else
                    DoMinimumFill(handover, contract, customerSlots, playerInv);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"Smart Fill error: {ex}");
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
            bool hasUnpackaged = false;

            // Count grams already in customer slots per product ID
            var existingGrams = new Dictionary<string, int>();
            for (int i = 0; i < customerSlots.Length; i++)
            {
                if (customerSlots[i]?.ItemInstance == null || customerSlots[i].Quantity <= 0) continue;
                string id;
                try { id = customerSlots[i].ItemInstance.ID; }
                catch { continue; }
#if IL2CPP
                var pi = customerSlots[i].ItemInstance.TryCast<ProductItemInstance>();
#else
                var pi = customerSlots[i].ItemInstance as ProductItemInstance;
#endif
                if (pi == null || pi.AppliedPackaging == null) continue;
                int grams = customerSlots[i].Quantity * pi.AppliedPackaging.Quantity;
                if (existingGrams.ContainsKey(id))
                    existingGrams[id] += grams;
                else
                    existingGrams[id] = grams;
            }

            for (int e = 0; e < entries.Count; e++)
            {
                var entry = entries[e];
                if (entry == null || string.IsNullOrEmpty(entry.ProductID)) continue;

                // Subtract grams already in customer slots from what we need to place
                int alreadyPlaced = existingGrams.ContainsKey(entry.ProductID) ? existingGrams[entry.ProductID] : 0;
                int remainingGrams = Math.Max(0, entry.Quantity - alreadyPlaced);
                totalGramsRequested += entry.Quantity;
                totalGramsPlaced += Math.Min(alreadyPlaced, entry.Quantity);
                EQuality requestedQuality = entry.Quality;

                if (remainingGrams <= 0) continue;

                var matchingSlots = new List<(ItemSlot slot, int multiplier, EQuality quality)>();
                for (int h = 0; h < hotbar.Count; h++)
                {
                    var slot = hotbar[h];
                    if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

                    string slotId;
                    try { slotId = slot.ItemInstance.ID; }
                    catch { continue; }

                    if (slotId != entry.ProductID) continue;

                    // Skip unpackaged product - game ignores it in match calculation
#if IL2CPP
                    var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                    var productItem = slot.ItemInstance as ProductItemInstance;
#endif
                    if (productItem == null || productItem.AppliedPackaging == null)
                    {
                        hasUnpackaged = true;
                        continue;
                    }

                    int mult = productItem.AppliedPackaging.Quantity;
                    EQuality slotQuality = productItem.Quality;

                    matchingSlots.Add((slot, mult, slotQuality));
                }

                // Also scan backpack as fallback source
                var bpSlots = BackpackBridge.GetSlots();
                for (int b = 0; b < bpSlots.Length; b++)
                {
                    var bpSlot = bpSlots[b];
                    if (bpSlot?.ItemInstance == null || bpSlot.Quantity <= 0) continue;

                    string slotId;
                    try { slotId = bpSlot.ItemInstance.ID; }
                    catch { continue; }

                    if (slotId != entry.ProductID) continue;

#if IL2CPP
                    var productItem = bpSlot.ItemInstance.TryCast<ProductItemInstance>();
#else
                    var productItem = bpSlot.ItemInstance as ProductItemInstance;
#endif
                    if (productItem == null || productItem.AppliedPackaging == null)
                    {
                        hasUnpackaged = true;
                        continue;
                    }

                    int mult = productItem.AppliedPackaging.Quantity;
                    EQuality slotQuality = productItem.Quality;

                    matchingSlots.Add((bpSlot, mult, slotQuality));
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

                foreach (var (sourceSlot, multiplier, slotQuality) in matchingSlots)
                {
                    if (remainingGrams <= 0) break;

                    if (sourceSlot?.ItemInstance == null || sourceSlot.Quantity <= 0) continue;

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

            _lastSlotFingerprint = SlotFingerprint(customerSlots);

            if (totalPlaced == 0)
            {
                string noMatchMsg = slotsFull ? "Slots full"
                    : hasUnpackaged ? "Package first"
                    : "No match found";
                SetStatus(noMatchMsg, true);
                return;
            }

            // Check if quantity is short - don't offer boost for quantity issues
            if (totalGramsPlaced < totalGramsRequested)
            {
                int missing = totalGramsRequested - totalGramsPlaced;
                SetStatus($"Missing {missing}g", true);
                return;
            }

            // Quantity met - check if acceptance is low (quality issue)
            var filledItems = GetCustomerItemsList(customerSlots);
            int mc;
            float acceptance = contract.GetProductListMatch(filledItems, out mc);
            if (acceptance < 0.95f)
            {
                _hasFilled = true;
                SetStatus("Unsatisfied - add more?", true);
                return;
            }

            SetStatus("Fulfilled");
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

            var entries = contract.ProductList.entries;

            if (match >= 0.95f)
            {
                SetStatus("Fulfilled");
                return;
            }

            var hotbar = playerInv.hotbarSlots;
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

                        // Skip unpackaged product - game ignores it in match calculation
#if IL2CPP
                        var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
#else
                        var productItem = slot.ItemInstance as ProductItemInstance;
#endif
                        if (productItem == null || productItem.AppliedPackaging == null) continue;

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

                    // If no hotbar match, check backpack
                    if (!addedThisPass)
                    {
                        var bpSlots = BackpackBridge.GetSlots();
                        for (int b = 0; b < bpSlots.Length; b++)
                        {
                            var bpSlot = bpSlots[b];
                            if (bpSlot?.ItemInstance == null || bpSlot.Quantity <= 0) continue;

                            string bpId;
                            try { bpId = bpSlot.ItemInstance.ID; }
                            catch { continue; }

                            if (bpId != entry.ProductID) continue;

#if IL2CPP
                            var bpProduct = bpSlot.ItemInstance.TryCast<ProductItemInstance>();
#else
                            var bpProduct = bpSlot.ItemInstance as ProductItemInstance;
#endif
                            if (bpProduct == null || bpProduct.AppliedPackaging == null) continue;

                            int placed = TryPlaceInCustomerSlots(customerSlots, bpSlot, 1, handover);
                            if (placed <= 0) { slotsFull = true; break; }

                            totalAdded += placed;
                            addedThisPass = true;

                            currentItems = GetCustomerItemsList(customerSlots);
                            match = contract.GetProductListMatch(currentItems, out matchedCount);
                            if (match >= 0.95f) break;
                        }
                    }

                    if (match >= 0.95f || slotsFull) break;
                }

                if (!addedThisPass || match >= 0.95f || slotsFull) break;
            }

            _lastSlotFingerprint = SlotFingerprint(customerSlots);

            var (gramsPlaced, gramsRequested) = CountGrams(customerSlots, entries);
            if (totalAdded == 0)
            {
                SetStatus(slotsFull ? $"{gramsPlaced}g/{gramsRequested}g full" : $"{gramsPlaced}g/{gramsRequested}g", true);
            }
            else
            {
                string msg = $"{gramsPlaced}g/{gramsRequested}g";
                bool warn = gramsPlaced < gramsRequested;
                if (slotsFull) { msg += " full"; warn = true; }
                SetStatus(msg, warn);
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
            ItemSlot[] customerSlots, ItemSlot sourceSlot,
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

        /// <summary>
        /// Counts total grams of packaged product in customer slots vs total grams requested by contract.
        /// </summary>
        private static (int placed, int requested) CountGrams(
            ItemSlot[] customerSlots,
#if IL2CPP
            Il2CppSystem.Collections.Generic.List<ProductList.Entry> entries)
#else
            List<ProductList.Entry> entries)
#endif
        {
            int totalRequested = 0;
            for (int e = 0; e < entries.Count; e++)
            {
                if (entries[e] != null)
                    totalRequested += entries[e].Quantity;
            }

            int totalPlaced = 0;
            for (int i = 0; i < customerSlots.Length; i++)
            {
                if (customerSlots[i]?.ItemInstance == null || customerSlots[i].Quantity <= 0) continue;
#if IL2CPP
                var pi = customerSlots[i].ItemInstance.TryCast<ProductItemInstance>();
#else
                var pi = customerSlots[i].ItemInstance as ProductItemInstance;
#endif
                if (pi == null || pi.AppliedPackaging == null) continue;
                totalPlaced += customerSlots[i].Quantity * pi.AppliedPackaging.Quantity;
            }

            return (totalPlaced, totalRequested);
        }

        /// <summary>
        /// Simple fingerprint of customer slot contents for detecting player changes.
        /// </summary>
        private static int SlotFingerprint(ItemSlot[] slots)
        {
            int hash = 17;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i]?.ItemInstance == null) continue;
                hash = hash * 31 + slots[i].Quantity;
                try { hash = hash * 31 + slots[i].ItemInstance.ID.GetHashCode(); }
                catch { }
            }
            return hash;
        }

        private static readonly Color BtnNormalColor = new Color(0.2f, 0.5f, 0.2f);
        private static readonly Color BtnWarnColor = new Color(0.75f, 0.45f, 0.1f);
        private static readonly Color BtnSuccessColor = new Color(0.15f, 0.45f, 0.25f);

        private static void SetStatus(string message, bool warn = false)
        {
            OTCLog.Msg(OTCLog.Systems.Patch, $"Smart Fill status: {message} (warn={warn})");
            if (_btnLabel != null)
                _btnLabel.text = message;
            if (_btnBgImage != null)
                _btnBgImage.color = warn ? BtnWarnColor : BtnSuccessColor;
        }
    }
}
