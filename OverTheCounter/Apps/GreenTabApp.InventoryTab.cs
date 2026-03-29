using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using S1API.UI;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppTMPro;
using Il2CppScheduleOne.Product;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
#else
using TMPro;
using ScheduleOne.Product;
using Grid = ScheduleOne.Tiles.Grid;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        // ==================================================================
        //  Inventory tab — flat product inventory filtered by dropdown
        // ==================================================================

        private Transform _inventoryContent;
        private List<(RectTransform rect, string text)> _inventoryTooltipEntries;
        private TextMeshProUGUI _invTitleLabel;

        private void BuildInventoryPanel(Transform parent)
        {
            var panel = UIFactory.Panel("InventoryPanel", parent, BgDark);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(NAV_WIDTH_FRAC, 0);
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            _tabPanels[AppTab.Inventory] = panel;

            float pad = 10f;

            // Title — property name + inline hint, updated during refresh
            _invTitleLabel = TMPFactory.Text("InvTabTitle", "Inventory",
                panel.transform, 16, TextAlignmentOptions.TopLeft);
            _invTitleLabel.color = Color.white;
            _invTitleLabel.richText = true;
            var titleRect = _invTitleLabel.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(0.95f, 1);
            titleRect.offsetMin = new Vector2(pad, -28);
            titleRect.offsetMax = new Vector2(0, -pad);

            // Scrollable content
            var scrollContainer = UIFactory.Panel("InvScrollContainer", panel.transform, Color.clear);
            var scrollContRect = scrollContainer.GetComponent<RectTransform>();
            scrollContRect.anchorMin = new Vector2(0, 0);
            scrollContRect.anchorMax = new Vector2(1, 1);
            scrollContRect.offsetMin = new Vector2(pad, pad);
            scrollContRect.offsetMax = new Vector2(-pad, -32);

            var scrollView = UIFactory.Panel("InvScrollView", scrollContainer.transform, Color.clear);
            var scrollViewRect = scrollView.GetComponent<RectTransform>();
            scrollViewRect.anchorMin = Vector2.zero;
            scrollViewRect.anchorMax = Vector2.one;
            scrollViewRect.offsetMin = Vector2.zero;
            scrollViewRect.offsetMax = Vector2.zero;

            var scroll = scrollView.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            var viewport = UIFactory.Panel("InvViewport", scrollView.transform, Color.clear);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = Vector2.zero;
            viewport.AddComponent<RectMask2D>();

            var content = new GameObject("InvContent");
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;

            scroll.viewport = vpRect;
            scroll.content = contentRect;

            _inventoryContent = content.transform;

            panel.SetActive(false);
        }

        private void RefreshInventory()
        {
            if (_inventoryContent == null) return;

            // Update title with selected property name + inline hint
            if (_invTitleLabel != null)
            {
                string displayName = GetBuildingDisplayName(_selectedBuildingId);
                _invTitleLabel.text = $"<b>{displayName}</b>  <color=#9E9E9E><size=90%>Select another property via dropdown</size></color>";
            }

            // Clear tooltip entries + content
            HideTooltip();
            _inventoryTooltipEntries = new List<(RectTransform, string)>();
            for (int i = _inventoryContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_inventoryContent.GetChild(i).gameObject);

            var buildings = GetBuildingsForSelection();
            if (buildings.Count == 0)
            {
                var contentRect = _inventoryContent.GetComponent<RectTransform>();
                contentRect.sizeDelta = new Vector2(0, 30);
                var empty = TMPFactory.Text("EmptyInv", "No properties owned.",
                    _inventoryContent, 15, TextAlignmentOptions.Center);
                empty.color = TextDim;
                var emptyRect = empty.gameObject.GetComponent<RectTransform>();
                emptyRect.anchorMin = new Vector2(0.1f, 0);
                emptyRect.anchorMax = new Vector2(0.9f, 1);
                emptyRect.offsetMin = Vector2.zero;
                emptyRect.offsetMax = Vector2.zero;
                return;
            }

            // Gather and merge items from all selected buildings
            var aggregated = new Dictionary<string, InventoryItem>();
            foreach (var bid in buildings)
            {
                var grid = GetGridForBuilding(bid);
                var items = GatherInventoryItems(grid);
                foreach (var item in items)
                {
                    string key = $"{item.Name}_{item.Quality}";
                    if (aggregated.TryGetValue(key, out var existing))
                    {
                        existing.Quantity += item.Quantity;
                        aggregated[key] = existing;
                    }
                    else
                    {
                        aggregated[key] = item;
                    }
                }
            }

            var sorted = new List<InventoryItem>(aggregated.Values);
            sorted.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

            if (sorted.Count == 0)
            {
                var contentRect = _inventoryContent.GetComponent<RectTransform>();
                contentRect.sizeDelta = new Vector2(0, 30);
                var empty = TMPFactory.Text("EmptyInv", "No products on display",
                    _inventoryContent, 15, TextAlignmentOptions.Center);
                empty.color = TextDim;
                var emptyRect = empty.gameObject.GetComponent<RectTransform>();
                emptyRect.anchorMin = new Vector2(0.1f, 0);
                emptyRect.anchorMax = new Vector2(0.9f, 1);
                emptyRect.offsetMin = Vector2.zero;
                emptyRect.offsetMax = Vector2.zero;
                return;
            }

            float yOffset = 0f;
            float rowH = 24f;

            // Column header
            var headerRow = UIFactory.Panel("InvColHeader", _inventoryContent, new Color(0.12f, 0.12f, 0.12f));
            var headerRect = headerRow.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0.5f, 1);
            headerRect.sizeDelta = new Vector2(0, rowH);
            headerRect.anchoredPosition = new Vector2(0, yOffset);

            BuildInvHeaderCell(headerRow.transform, "Product", 0f, 0.50f, 14);
            BuildInvHeaderCell(headerRow.transform, "Qty", 0.50f, 0.65f, 4);
            BuildInvHeaderCell(headerRow.transform, "Quality", 0.65f, 1f, 4);

            yOffset -= rowH + 2;

            for (int idx = 0; idx < sorted.Count; idx++)
            {
                var item = sorted[idx];
                Color rowBg = idx % 2 == 0 ? Color.clear : new Color(0.10f, 0.10f, 0.10f, 0.5f);
                var row = UIFactory.Panel($"InvRow_{idx}", _inventoryContent, rowBg);
                var rRect = row.GetComponent<RectTransform>();
                rRect.anchorMin = new Vector2(0, 1);
                rRect.anchorMax = new Vector2(1, 1);
                rRect.pivot = new Vector2(0.5f, 1);
                rRect.sizeDelta = new Vector2(0, rowH);
                rRect.anchoredPosition = new Vector2(0, yOffset);

                // Product name
                var nameLabel = TMPFactory.Text("Name", item.Name,
                    row.transform, 15, TextAlignmentOptions.Left);
                nameLabel.color = new Color(0.85f, 0.85f, 0.85f);
                nameLabel.overflowMode = TextOverflowModes.Ellipsis;
                var nlRect = nameLabel.gameObject.GetComponent<RectTransform>();
                nlRect.anchorMin = new Vector2(0, 0);
                nlRect.anchorMax = new Vector2(0.50f, 1);
                nlRect.offsetMin = new Vector2(14, 0);
                nlRect.offsetMax = Vector2.zero;
                _inventoryTooltipEntries.Add((nlRect, item.Name));

                // Quantity
                var qtyLabel = TMPFactory.Text("Qty", $"x{item.Quantity}",
                    row.transform, 15, TextAlignmentOptions.Left, FontStyles.Bold);
                qtyLabel.color = AccentGreen;
                var qlRect = qtyLabel.gameObject.GetComponent<RectTransform>();
                qlRect.anchorMin = new Vector2(0.50f, 0);
                qlRect.anchorMax = new Vector2(0.65f, 1);
                qlRect.offsetMin = new Vector2(4, 0);
                qlRect.offsetMax = Vector2.zero;

                // Quality stars
                if (item.Quality > 0)
                {
                    var starsContainer = new GameObject("Stars");
                    starsContainer.transform.SetParent(row.transform, false);
                    var scRect = starsContainer.AddComponent<RectTransform>();
                    scRect.anchorMin = new Vector2(0.65f, 0);
                    scRect.anchorMax = new Vector2(1, 1);
                    scRect.offsetMin = new Vector2(4, 0);
                    scRect.offsetMax = Vector2.zero;
                    CreateQualityStars(starsContainer.transform, item.Quality, 12f);
                }

                yOffset -= rowH;
            }

            var cRect = _inventoryContent.GetComponent<RectTransform>();
            cRect.sizeDelta = new Vector2(0, -yOffset);
        }

        private static void BuildInvHeaderCell(Transform parent, string text, float xMin, float xMax, float padLeft)
        {
            var label = TMPFactory.Text($"Hdr_{text}", text, parent, 15, TextAlignmentOptions.Left, FontStyles.Bold);
            label.color = TextMuted;
            var rt = label.gameObject.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(xMin, 0);
            rt.anchorMax = new Vector2(xMax, 1);
            rt.offsetMin = new Vector2(padLeft, 0);
            rt.offsetMax = new Vector2(-2, 0);
        }

        private struct InventoryItem
        {
            public string Name;
            public int Quantity;
            public int Quality;
        }

        /// <summary>Enumerates display storage and aggregates products by name + quality.</summary>
        private static List<InventoryItem> GatherInventoryItems(Grid grid)
        {
            var result = new List<InventoryItem>();
            if (grid == null) return result;

            var storages = PropertyInventory.GetStorages(grid, includePrivate: true);
            var aggregated = new Dictionary<string, InventoryItem>();

            foreach (var storage in storages)
            {
                if (storage?.ItemSlots == null) continue;
                for (int i = 0; i < storage.ItemSlots.Count; i++)
                {
                    var slot = storage.ItemSlots[i];
                    if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

                    string name = slot.ItemInstance.Name ?? "Unknown";
                    int quality = 0;

#if IL2CPP
                    var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
                    var prodDef = productItem?.Definition?.TryCast<ProductDefinition>();
#else
                    var productItem = slot.ItemInstance as ProductItemInstance;
                    var prodDef = productItem?.Definition as ProductDefinition;
#endif
                    if (prodDef != null)
                    {
                        name = prodDef.Name ?? name;
                        quality = (int)(productItem?.Quality ?? 0);
                    }

                    string key = $"{name}_{quality}";
                    if (aggregated.TryGetValue(key, out var existing))
                    {
                        existing.Quantity += slot.Quantity;
                        aggregated[key] = existing;
                    }
                    else
                    {
                        aggregated[key] = new InventoryItem
                        {
                            Name = name,
                            Quantity = slot.Quantity,
                            Quality = quality
                        };
                    }
                }
            }

            result.AddRange(aggregated.Values);
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return result;
        }
    }
}
