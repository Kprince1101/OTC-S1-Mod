using OverTheCounter.Logic;
using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using S1API.GameTime;
using S1API.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppTMPro;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
using ProductItemInstance = Il2CppScheduleOne.Product.ProductItemInstance;
#else
using TMPro;
using Grid = ScheduleOne.Tiles.Grid;
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
using ProductItemInstance = ScheduleOne.Product.ProductItemInstance;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        // ==================================================================
        //  Overview tab — dashboard with summary rows and line charts
        // ==================================================================

        private static readonly Color GridLineCol = new Color(0.3f, 0.3f, 0.3f, 0.5f);

        // Inventory row refs
        private Transform _overviewInvItemsContainer;
        private Transform _inventoryChartContainer;

        // Sales row refs
        private Transform _overviewSalesItemsContainer;
        private Transform _salesChartContainer;

        // Properties row ref
        private Transform _overviewPropertyListContainer;

        // Employees widget ref
        private TextMeshProUGUI _overviewWageLabel;

        // Tooltip entries for mouse-follow hover (rendered by shared tooltip in GreenTabApp.cs)
        private List<(RectTransform rect, string text)> _overviewTooltipEntries;

        private void BuildOverviewPanel(Transform parent)
        {
            var panel = UIFactory.Panel("OverviewPanel", parent, BgDark);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(NAV_WIDTH_FRAC, 0);
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            _tabPanels[AppTab.Overview] = panel;

            // Scrollable content
            var scrollView = UIFactory.Panel("OverviewScroll", panel.transform, Color.clear);
            var scrollViewRect = scrollView.GetComponent<RectTransform>();
            scrollViewRect.anchorMin = Vector2.zero;
            scrollViewRect.anchorMax = Vector2.one;
            scrollViewRect.offsetMin = Vector2.zero;
            scrollViewRect.offsetMax = Vector2.zero;

            var scroll = scrollView.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            var viewport = UIFactory.Panel("OvViewport", scrollView.transform, Color.clear);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = Vector2.zero;
            viewport.AddComponent<RectMask2D>();

            var content = new GameObject("OvContent");
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;

            scroll.viewport = vpRect;
            scroll.content = contentRect;

            var ct = content.transform;
            float pad = 10f;
            float rowH = 175f;
            float smallRowH = 125f;
            float gap = 12f;
            float leftFrac = 0.65f;

            // ---- Row 1: Inventory (left 65% items, right 35% chart) ----
            float row1Y = -pad;
            BuildOverviewRow(ct, "INVENTORY", row1Y, rowH, leftFrac, pad,
                out _overviewInvItemsContainer, out _inventoryChartContainer,
                "Inventory", AppTab.Inventory);

            // ---- Row 2: Sales (left 65% popular items, right 35% chart) ----
            float row2Y = row1Y - rowH - gap;
            BuildOverviewRow(ct, "TOP SELLERS", row2Y, rowH, leftFrac, pad,
                out _overviewSalesItemsContainer, out _salesChartContainer,
                "Sales", AppTab.Sales);

            // ---- Row 3: Properties (left) + Employees (right) ----
            float row3Y = row2Y - rowH - gap;
            BuildPropertiesWidget(ct, pad, row3Y, 0.47f, smallRowH);
            BuildEmployeesWidget(ct, 0.53f, row3Y, 0.47f, smallRowH);

            // Total content height
            contentRect.sizeDelta = new Vector2(0, -(row3Y) + smallRowH + pad);

            panel.SetActive(false);
        }

        /// <summary>Builds a row with left items panel (65%) and right chart panel (35%).</summary>
        private void BuildOverviewRow(Transform parent, string title, float yOffset, float height,
            float leftFrac, float pad,
            out Transform itemsContainer, out Transform chartContainer,
            string buttonLabel, AppTab targetTab)
        {
            // Left panel — items
            var leftPanel = RoundedPanel($"Row_{title}_Left", parent, CardBg);
            var leftRect = leftPanel.GetComponent<RectTransform>();
            leftRect.anchorMin = new Vector2(0, 1);
            leftRect.anchorMax = new Vector2(leftFrac - 0.01f, 1);
            leftRect.pivot = new Vector2(0, 1);
            leftRect.anchoredPosition = new Vector2(pad, yOffset);
            leftRect.sizeDelta = new Vector2(-pad, height);

            // Left title
            var leftTitle = TMPFactory.Text($"Title_{title}", title,
                leftPanel.transform, 15, TextAlignmentOptions.TopLeft, FontStyles.Bold);
            leftTitle.color = TextMuted;
            var ltRect = leftTitle.gameObject.GetComponent<RectTransform>();
            ltRect.anchorMin = new Vector2(0, 1);
            ltRect.anchorMax = new Vector2(0.7f, 1);
            ltRect.offsetMin = new Vector2(10, -20);
            ltRect.offsetMax = new Vector2(0, -4);

            // Navigation button (top-right of left panel)
            var (_, btn, _) = TMPFactory.RoundedButtonWithLabel(
                $"Btn_{title}", buttonLabel, leftPanel.transform,
                AccentGreenDark, 90, 24, 15, Color.white);
            var btnRect = btn.gameObject.GetComponent<RectTransform>().parent.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1, 1);
            btnRect.anchorMax = new Vector2(1, 1);
            btnRect.pivot = new Vector2(1, 1);
            btnRect.anchoredPosition = new Vector2(-8, -5);
            btn.onClick.AddListener(new Action(() => SwitchTab(targetTab)));

            // Items container (below title+button, above bottom margin)
            var itemsPanel = new GameObject($"Items_{title}");
            itemsPanel.transform.SetParent(leftPanel.transform, false);
            var ipRect = itemsPanel.AddComponent<RectTransform>();
            ipRect.anchorMin = Vector2.zero;
            ipRect.anchorMax = Vector2.one;
            ipRect.offsetMin = new Vector2(4, 4);
            ipRect.offsetMax = new Vector2(-4, -34);
            itemsContainer = itemsPanel.transform;

            // Right panel — chart
            var rightPanel = RoundedPanel($"Row_{title}_Right", parent, CardBg);
            var rightRect = rightPanel.GetComponent<RectTransform>();
            rightRect.anchorMin = new Vector2(leftFrac + 0.01f, 1);
            rightRect.anchorMax = new Vector2(1, 1);
            rightRect.pivot = new Vector2(0, 1);
            rightRect.anchoredPosition = new Vector2(0, yOffset);
            rightRect.sizeDelta = new Vector2(-pad, height);
            chartContainer = rightPanel.transform;
        }

        // ==================================================================
        //  Bottom widgets (Properties + Employees)
        // ==================================================================

        private void BuildPropertiesWidget(Transform parent, float xOffset, float yOffset, float widthFrac, float height)
        {
            var widget = RoundedPanel("PropWidget", parent, CardBg);
            var r = widget.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(widthFrac, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(xOffset, yOffset);
            r.sizeDelta = new Vector2(0, height);

            var title = TMPFactory.Text("PropTitle", "PROPERTIES", widget.transform, 15, TextAlignmentOptions.TopLeft);
            title.color = TextMuted;
            PositionLabel(title, 0, 1, 0.7f, 1, 10, -6);

            var (_, btn, _) = TMPFactory.RoundedButtonWithLabel(
                "CustomizeBtn", "Customize", widget.transform,
                AccentGreenDark, 90, 24, 15, Color.white);
            var btnRect = btn.gameObject.GetComponent<RectTransform>().parent.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1, 1);
            btnRect.anchorMax = new Vector2(1, 1);
            btnRect.pivot = new Vector2(1, 1);
            btnRect.anchoredPosition = new Vector2(-8, -4);
            btn.onClick.AddListener(new Action(() => SwitchTab(AppTab.Customize)));

            // Properties list container
            var propContainer = new GameObject("PropItems");
            propContainer.transform.SetParent(widget.transform, false);
            var pcRect = propContainer.AddComponent<RectTransform>();
            pcRect.anchorMin = Vector2.zero;
            pcRect.anchorMax = Vector2.one;
            pcRect.offsetMin = new Vector2(4, 6);
            pcRect.offsetMax = new Vector2(-4, -34);
            _overviewPropertyListContainer = propContainer.transform;
        }

        private void BuildEmployeesWidget(Transform parent, float xFrac, float yOffset, float widthFrac, float height)
        {
            var widget = RoundedPanel("EmpWidget", parent, CardBg);
            var r = widget.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(xFrac, 1);
            r.anchorMax = new Vector2(xFrac + widthFrac, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(0, yOffset);
            r.sizeDelta = new Vector2(-10, height);

            var title = TMPFactory.Text("EmpTitle", "EMPLOYEES", widget.transform, 15, TextAlignmentOptions.TopLeft);
            title.color = TextMuted;
            PositionLabel(title, 0, 1, 0.7f, 1, 10, -6);

            _overviewWageLabel = TMPFactory.Text("EmpWage", "", widget.transform,
                15, TextAlignmentOptions.Center);
            _overviewWageLabel.color = TextMuted;
            PositionLabel(_overviewWageLabel, 0.1f, 0.15f, 0.9f, 0.75f, 0, 0);

            var (_, btn, _) = TMPFactory.RoundedButtonWithLabel(
                "ViewEmpBtn", "Employees", widget.transform,
                AccentGreenDark, 90, 24, 15, Color.white);
            var btnRect = btn.gameObject.GetComponent<RectTransform>().parent.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1, 1);
            btnRect.anchorMax = new Vector2(1, 1);
            btnRect.pivot = new Vector2(1, 1);
            btnRect.anchoredPosition = new Vector2(-8, -4);
            btn.onClick.AddListener(new Action(() => SwitchTab(AppTab.Employees)));
        }

        /// <summary>Helper to position a TMP label using anchor fractions.</summary>
        private static void PositionLabel(TextMeshProUGUI tmp,
            float aMinX, float aMinY, float aMaxX, float aMaxY,
            float padLeft, float padTop)
        {
            var rt = tmp.gameObject.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(aMinX, aMinY);
            rt.anchorMax = new Vector2(aMaxX, aMaxY);
            rt.offsetMin = new Vector2(padLeft, 0);
            rt.offsetMax = new Vector2(0, padTop);
        }

        // ==================================================================
        //  Grid line helpers
        // ==================================================================

        private static void AddHLine(Transform parent, float yPos)
        {
            var line = new GameObject("HLine");
            line.transform.SetParent(parent, false);
            var img = line.AddComponent<Image>();
            img.color = GridLineCol;
            img.raycastTarget = false;
            var rt = line.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1);
            rt.sizeDelta = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(0, yPos);
        }

        private static void AddVLine(Transform row, float xFrac)
        {
            var line = new GameObject("VLine");
            line.transform.SetParent(row, false);
            var img = line.AddComponent<Image>();
            img.color = GridLineCol;
            img.raycastTarget = false;
            var rt = line.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(xFrac, 0);
            rt.anchorMax = new Vector2(xFrac, 1);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(1, 0);
        }

        // ==================================================================
        //  Line chart builder
        // ==================================================================

        /// <summary>Builds a line chart inside the given container.</summary>
        private void BuildLineChart(Transform container, string title, List<(string label, float value)> data,
            bool isCurrency = false, List<(RectTransform rect, string text)> tooltipEntries = null)
        {
            // Clear previous children
            for (int i = container.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(container.GetChild(i).gameObject);

            // Title
            var titleTmp = TMPFactory.Text("ChartTitle", title, container, 15, TextAlignmentOptions.TopLeft, FontStyles.Bold);
            titleTmp.color = TextMuted;
            var titleRect = titleTmp.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = new Vector2(10, -20);
            titleRect.offsetMax = new Vector2(-10, -4);

            if (data == null || data.Count == 0)
            {
                var noData = TMPFactory.Text("NoData", "No data yet", container, 15, TextAlignmentOptions.Center);
                noData.color = TextDim;
                var noDataRect = noData.gameObject.GetComponent<RectTransform>();
                noDataRect.anchorMin = new Vector2(0.1f, 0.2f);
                noDataRect.anchorMax = new Vector2(0.9f, 0.7f);
                noDataRect.offsetMin = Vector2.zero;
                noDataRect.offsetMax = Vector2.zero;
                return;
            }

            // Chart area (below title)
            float chartPadLeft = 40f;
            float chartPadRight = 8f;
            float chartPadTop = 24f;
            float chartPadBottom = 24f;

            var chartArea = new GameObject("ChartArea");
            chartArea.transform.SetParent(container, false);
            var caRect = chartArea.AddComponent<RectTransform>();
            caRect.anchorMin = Vector2.zero;
            caRect.anchorMax = Vector2.one;
            caRect.offsetMin = new Vector2(chartPadLeft, chartPadBottom);
            caRect.offsetMax = new Vector2(-chartPadRight, -chartPadTop);

            // Find max value for scaling
            float maxVal = 1f;
            foreach (var d in data)
                if (d.value > maxVal) maxVal = d.value;

            int count = data.Count;
            float dotSize = 6f;

            for (int i = 0; i < count; i++)
            {
                float xFrac = count > 1 ? (float)i / (count - 1) : 0.5f;
                float yFrac = data[i].value / maxVal;

                // Dot at data point
                var dot = new GameObject($"Dot_{i}");
                dot.transform.SetParent(chartArea.transform, false);
                var dotImg = dot.AddComponent<Image>();
                dotImg.color = AccentGreen;
                dotImg.raycastTarget = false;
                var dotRect = dot.GetComponent<RectTransform>();
                dotRect.anchorMin = new Vector2(xFrac, yFrac);
                dotRect.anchorMax = new Vector2(xFrac, yFrac);
                dotRect.pivot = new Vector2(0.5f, 0.5f);
                dotRect.sizeDelta = new Vector2(dotSize, dotSize);

                // Register for tooltip hover
                if (tooltipEntries != null)
                {
                    string valStr = isCurrency ? $"${data[i].value:F0}" : $"{data[i].value:F0}";
                    tooltipEntries.Add((dotRect, $"Day {data[i].label}: {valStr}"));
                }

                // Line segment to next point
                if (i < count - 1)
                {
                    float x2Frac = (float)(i + 1) / (count - 1);
                    float y2Frac = data[i + 1].value / maxVal;
                    DrawLineSegment(chartArea.transform, xFrac, yFrac, x2Frac, y2Frac, AccentGreen);
                }

                // X-axis label — parented to chartArea so xFrac aligns with dots
                var xLabel = TMPFactory.Text($"XLabel_{i}", data[i].label,
                    chartArea.transform, 15, TextAlignmentOptions.Top);
                xLabel.color = TextDim;
                xLabel.enableAutoSizing = true;
                xLabel.fontSizeMin = 15;
                xLabel.fontSizeMax = 15;
                var xlRect = xLabel.gameObject.GetComponent<RectTransform>();
                xlRect.anchorMin = new Vector2(xFrac, 0);
                xlRect.anchorMax = new Vector2(xFrac, 0);
                xlRect.pivot = new Vector2(0.5f, 1);
                xlRect.sizeDelta = new Vector2(32, 18);
                xlRect.anchoredPosition = new Vector2(0, -2);
            }

            // Y-axis labels (0 and max)
            var yMin = TMPFactory.Text("YMin", "0", container, 15, TextAlignmentOptions.Right);
            yMin.color = TextDim;
            yMin.enableAutoSizing = true;
            yMin.fontSizeMin = 15;
            yMin.fontSizeMax = 15;
            var yMinRect = yMin.gameObject.GetComponent<RectTransform>();
            yMinRect.anchorMin = new Vector2(0, 0);
            yMinRect.anchorMax = new Vector2(0, 0);
            yMinRect.pivot = new Vector2(1, 0);
            yMinRect.sizeDelta = new Vector2(36, 18);
            yMinRect.anchoredPosition = new Vector2(chartPadLeft - 3, chartPadBottom - 6);

            string prefix = isCurrency ? "$" : "";
            string maxLabel = maxVal >= 1000 ? $"${maxVal / 1000:F1}k" : $"{prefix}{maxVal:F0}";
            var yMax = TMPFactory.Text("YMax", maxLabel, container, 15, TextAlignmentOptions.Right);
            yMax.color = TextDim;
            yMax.enableAutoSizing = true;
            yMax.fontSizeMin = 15;
            yMax.fontSizeMax = 15;
            var yMaxRect = yMax.gameObject.GetComponent<RectTransform>();
            yMaxRect.anchorMin = new Vector2(0, 1);
            yMaxRect.anchorMax = new Vector2(0, 1);
            yMaxRect.pivot = new Vector2(1, 1);
            yMaxRect.sizeDelta = new Vector2(36, 18);
            yMaxRect.anchoredPosition = new Vector2(chartPadLeft - 3, -chartPadTop);
        }

        /// <summary>Draws a thin rotated line segment between two normalized positions.</summary>
        private static void DrawLineSegment(Transform parent,
            float x1Frac, float y1Frac, float x2Frac, float y2Frac, Color color)
        {
            var parentRect = parent.GetComponent<RectTransform>();
            float pw = parentRect.rect.width;
            float ph = parentRect.rect.height;
            if (pw <= 0) pw = 120f;
            if (ph <= 0) ph = 80f;

            var lineGo = new GameObject("LineSeg");
            lineGo.transform.SetParent(parent, false);
            var lineImg = lineGo.AddComponent<Image>();
            lineImg.color = color;
            lineImg.raycastTarget = false;
            var lineRt = lineGo.GetComponent<RectTransform>();

            lineRt.anchorMin = new Vector2(x1Frac, y1Frac);
            lineRt.anchorMax = new Vector2(x1Frac, y1Frac);
            lineRt.pivot = new Vector2(0, 0.5f);

            float dx = (x2Frac - x1Frac) * pw;
            float dy = (y2Frac - y1Frac) * ph;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;

            lineRt.sizeDelta = new Vector2(distance, 2f);
            lineRt.localEulerAngles = new Vector3(0, 0, angle);
            lineRt.anchoredPosition = Vector2.zero;
        }

        // ==================================================================
        //  Inventory/Sales/Properties item row builders
        // ==================================================================

        private void RefreshInventoryItems()
        {
            if (_overviewInvItemsContainer == null) return;

            for (int i = _overviewInvItemsContainer.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_overviewInvItemsContainer.GetChild(i).gameObject);

            var buildings = GetBuildingsForSelection();
            var items = new Dictionary<string, (string name, int qty)>();

            foreach (var bid in buildings)
            {
                var grid = GetGridForBuilding(bid);
                if (grid == null) continue;
                var storages = PropertyInventory.GetStorages(grid, includePrivate: true);
                foreach (var storage in storages)
                {
                    if (storage?.ItemSlots == null) continue;
                    for (int s = 0; s < storage.ItemSlots.Count; s++)
                    {
                        var slot = storage.ItemSlots[s];
                        if (slot?.ItemInstance == null || slot.Quantity <= 0) continue;

                        string key = null;
                        string displayName = slot.ItemInstance.Name ?? "Unknown";
#if IL2CPP
                        var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
                        var prodDef = productItem?.Definition?.TryCast<ProductDefinition>();
#else
                        var productItem = slot.ItemInstance as ProductItemInstance;
                        var prodDef = productItem?.Definition as ProductDefinition;
#endif
                        if (prodDef != null)
                        {
                            key = prodDef.ID;
                            displayName = prodDef.Name ?? displayName;
                        }
                        if (key == null) key = displayName;

                        int pkgMult = ContractAggregator.GetPackagingMultiplier(slot.ItemInstance);
                        if (pkgMult <= 0) pkgMult = 1;
                        int units = slot.Quantity * pkgMult;

                        if (items.TryGetValue(key, out var existing))
                            items[key] = (existing.name, existing.qty + units);
                        else
                            items[key] = (displayName, units);
                    }
                }
            }

            if (items.Count == 0)
            {
                var empty = TMPFactory.Text("NoInv", "No products on display", _overviewInvItemsContainer,
                    15, TextAlignmentOptions.TopLeft);
                empty.color = TextDim;
                var rt = empty.gameObject.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = new Vector2(6, 0);
                rt.offsetMax = Vector2.zero;
                return;
            }

            var sorted = items.OrderByDescending(kv => kv.Value.qty).Take(5).ToList();
            float rowH = 24f;

            for (int i = 0; i < sorted.Count; i++)
            {
                var kv = sorted[i];
                float yPos = -(i * rowH);
                Color rowBg = i % 2 == 0 ? Color.clear : new Color(0.10f, 0.10f, 0.10f, 0.5f);

                var row = UIFactory.Panel($"InvRow_{i}", _overviewInvItemsContainer, rowBg);
                var rRect = row.GetComponent<RectTransform>();
                rRect.anchorMin = new Vector2(0, 1);
                rRect.anchorMax = new Vector2(1, 1);
                rRect.pivot = new Vector2(0, 1);
                rRect.sizeDelta = new Vector2(0, rowH);
                rRect.anchoredPosition = new Vector2(0, yPos);

                // Name
                var nameLabel = TMPFactory.Text($"Name_{i}", kv.Value.name, row.transform,
                    15, TextAlignmentOptions.Left);
                nameLabel.color = new Color(0.85f, 0.85f, 0.85f);
                nameLabel.overflowMode = TextOverflowModes.Ellipsis;
                var nlRect = nameLabel.gameObject.GetComponent<RectTransform>();
                nlRect.anchorMin = Vector2.zero;
                nlRect.anchorMax = new Vector2(0.72f, 1);
                nlRect.offsetMin = new Vector2(6, 0);
                nlRect.offsetMax = new Vector2(-4, 0);
                _overviewTooltipEntries.Add((nlRect, kv.Value.name));

                AddVLine(row.transform, 0.72f);

                // Qty
                var qtyLabel = TMPFactory.Text($"Qty_{i}", $"x{kv.Value.qty}", row.transform,
                    15, TextAlignmentOptions.Right, FontStyles.Bold);
                qtyLabel.color = AccentGreen;
                var qlRect = qtyLabel.gameObject.GetComponent<RectTransform>();
                qlRect.anchorMin = new Vector2(0.72f, 0);
                qlRect.anchorMax = Vector2.one;
                qlRect.offsetMin = new Vector2(4, 0);
                qlRect.offsetMax = new Vector2(-4, 0);

                if (i < sorted.Count - 1)
                    AddHLine(_overviewInvItemsContainer, yPos - rowH);
            }
        }

        private void RefreshSalesItems()
        {
            if (_overviewSalesItemsContainer == null) return;

            for (int i = _overviewSalesItemsContainer.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_overviewSalesItemsContainer.GetChild(i).gameObject);

            var salesLog = PropertySaveData.Instance?.GetSalesLog();

            // Filter by building if not "all"
            if (salesLog != null && _selectedBuildingId != AllPropertiesId)
                salesLog = salesLog.Where(s => s.BuildingId == _selectedBuildingId).ToList();

            if (salesLog == null || salesLog.Count == 0)
            {
                var empty = TMPFactory.Text("NoSales", "No sales recorded", _overviewSalesItemsContainer,
                    15, TextAlignmentOptions.TopLeft);
                empty.color = TextDim;
                var rt = empty.gameObject.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = new Vector2(6, 0);
                rt.offsetMax = Vector2.zero;
                return;
            }

            // Aggregate by product name
            var productRevenue = new Dictionary<string, float>();
            var productQty = new Dictionary<string, int>();
            foreach (var sale in salesLog)
            {
                string name = sale.ProductName ?? sale.ProductId;
                float rev = sale.Quantity * sale.PricePerUnit;
                if (productRevenue.ContainsKey(name))
                {
                    productRevenue[name] += rev;
                    productQty[name] += sale.Quantity;
                }
                else
                {
                    productRevenue[name] = rev;
                    productQty[name] = sale.Quantity;
                }
            }

            var sorted = productRevenue.OrderByDescending(kv => kv.Value).Take(5).ToList();
            float rowH = 24f;

            for (int i = 0; i < sorted.Count; i++)
            {
                var kv = sorted[i];
                int qty = productQty[kv.Key];
                float yPos = -(i * rowH);
                Color rowBg = i % 2 == 0 ? Color.clear : new Color(0.10f, 0.10f, 0.10f, 0.5f);

                var row = UIFactory.Panel($"SaleRow_{i}", _overviewSalesItemsContainer, rowBg);
                var rRect = row.GetComponent<RectTransform>();
                rRect.anchorMin = new Vector2(0, 1);
                rRect.anchorMax = new Vector2(1, 1);
                rRect.pivot = new Vector2(0, 1);
                rRect.sizeDelta = new Vector2(0, rowH);
                rRect.anchoredPosition = new Vector2(0, yPos);

                // Name
                var nameLabel = TMPFactory.Text($"Name_{i}", kv.Key, row.transform,
                    15, TextAlignmentOptions.Left);
                nameLabel.color = new Color(0.85f, 0.85f, 0.85f);
                nameLabel.overflowMode = TextOverflowModes.Ellipsis;
                var nlRect = nameLabel.gameObject.GetComponent<RectTransform>();
                nlRect.anchorMin = Vector2.zero;
                nlRect.anchorMax = new Vector2(0.42f, 1);
                nlRect.offsetMin = new Vector2(6, 0);
                nlRect.offsetMax = new Vector2(-4, 0);
                _overviewTooltipEntries.Add((nlRect, kv.Key));

                AddVLine(row.transform, 0.42f);

                // Qty
                var qtyLabel = TMPFactory.Text($"Qty_{i}", $"x{qty}", row.transform,
                    15, TextAlignmentOptions.Right);
                qtyLabel.color = TextMuted;
                var qlRect = qtyLabel.gameObject.GetComponent<RectTransform>();
                qlRect.anchorMin = new Vector2(0.42f, 0);
                qlRect.anchorMax = new Vector2(0.62f, 1);
                qlRect.offsetMin = new Vector2(4, 0);
                qlRect.offsetMax = new Vector2(-4, 0);

                AddVLine(row.transform, 0.62f);

                // Revenue
                var revLabel = TMPFactory.Text($"Rev_{i}", $"${kv.Value:F0}", row.transform,
                    15, TextAlignmentOptions.Right, FontStyles.Bold);
                revLabel.color = AccentGreen;
                var rlRect = revLabel.gameObject.GetComponent<RectTransform>();
                rlRect.anchorMin = new Vector2(0.62f, 0);
                rlRect.anchorMax = Vector2.one;
                rlRect.offsetMin = new Vector2(4, 0);
                rlRect.offsetMax = new Vector2(-4, 0);

                if (i < sorted.Count - 1)
                    AddHLine(_overviewSalesItemsContainer, yPos - rowH);
            }
        }

        private void RefreshPropertyRows()
        {
            if (_overviewPropertyListContainer == null) return;

            _storeNameLabel = null;
            for (int i = _overviewPropertyListContainer.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_overviewPropertyListContainer.GetChild(i).gameObject);

            var buildings = GetOwnedBuildings();
            if (buildings.Count == 0)
            {
                var empty = TMPFactory.Text("NoProp", "None", _overviewPropertyListContainer,
                    15, TextAlignmentOptions.TopLeft);
                empty.color = TextDim;
                var rt = empty.gameObject.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = new Vector2(6, 0);
                rt.offsetMax = Vector2.zero;
                return;
            }

            float rowH = 22f;
            float warnH = 16f;
            float yPos = 0f;

            for (int i = 0; i < buildings.Count; i++)
            {
                string bid = buildings[i];
                string name = GetBuildingDisplayName(bid);
                Color rowBg = i % 2 == 0 ? Color.clear : new Color(0.10f, 0.10f, 0.10f, 0.5f);

                var row = UIFactory.Panel($"Prop_{i}", _overviewPropertyListContainer, rowBg);
                var rRect = row.GetComponent<RectTransform>();
                rRect.anchorMin = new Vector2(0, 1);
                rRect.anchorMax = new Vector2(1, 1);
                rRect.pivot = new Vector2(0, 1);
                rRect.sizeDelta = new Vector2(0, rowH);
                rRect.anchoredPosition = new Vector2(0, yPos);

                var nameLabel = TMPFactory.Text($"PropName_{i}", name, row.transform,
                    15, TextAlignmentOptions.Left);
                nameLabel.color = Color.white;
                nameLabel.overflowMode = TextOverflowModes.Ellipsis;
                var nlRect = nameLabel.gameObject.GetComponent<RectTransform>();
                nlRect.anchorMin = Vector2.zero;
                nlRect.anchorMax = new Vector2(0.50f, 1);
                nlRect.offsetMin = new Vector2(6, 0);
                nlRect.offsetMax = new Vector2(-4, 0);
                _overviewTooltipEntries.Add((nlRect, name));

                // Store shack name label for quest highlight
                if (bid == PropertySaveData.ShackId)
                    _storeNameLabel = nameLabel;

                // Store status (Open / Closed / After Hours)
                var (statusText, statusColor) = GetStoreStatus(bid);
                var statusLabel = TMPFactory.Text($"PropStatus_{i}", statusText, row.transform,
                    15, TextAlignmentOptions.Right, FontStyles.Bold);
                statusLabel.color = statusColor;
                var slRect = statusLabel.gameObject.GetComponent<RectTransform>();
                slRect.anchorMin = new Vector2(0.50f, 0);
                slRect.anchorMax = new Vector2(0.78f, 1);
                slRect.offsetMin = new Vector2(4, 0);
                slRect.offsetMax = new Vector2(-4, 0);

                // Open/Close toggle switch
                CreateStoreToggle(row.transform, bid);

                yPos -= rowH;

                // Warning sub-row (why customers aren't coming)
                var warnings = GetStoreWarnings(bid);
                if (warnings.Count > 0)
                {
                    var warnLabel = TMPFactory.Text($"PropWarn_{i}",
                        string.Join(", ", warnings),
                        _overviewPropertyListContainer, 13, TextAlignmentOptions.Left);
                    warnLabel.color = new Color(0.95f, 0.75f, 0.3f);
                    var wlRect = warnLabel.gameObject.GetComponent<RectTransform>();
                    wlRect.anchorMin = new Vector2(0, 1);
                    wlRect.anchorMax = new Vector2(1, 1);
                    wlRect.pivot = new Vector2(0, 1);
                    wlRect.sizeDelta = new Vector2(0, warnH);
                    wlRect.anchoredPosition = new Vector2(0, yPos);
                    wlRect.offsetMin = new Vector2(10, wlRect.offsetMin.y);
                    yPos -= warnH;
                }

                if (i < buildings.Count - 1)
                    AddHLine(_overviewPropertyListContainer, yPos);
            }
        }

        // ==================================================================
        //  Store toggle
        // ==================================================================

        private static readonly Color ToggleOnColor = new(0.30f, 0.69f, 0.31f);   // AccentGreen
        private static readonly Color ToggleOffColor = new(0.9f, 0.3f, 0.3f);     // Red

        private static bool GetStoreOpenState(string buildingId) =>
            buildingId == PropertySaveData.ShackId
                ? WestvilleShack.IsStoreOpen
                : Dispensary.IsStoreOpen;

        private void CreateStoreToggle(Transform parent, string buildingId)
        {
            bool isOn = GetStoreOpenState(buildingId);
            float trackW = 36f, trackH = 18f, thumbSize = 14f;
            float thumbPad = (trackH - thumbSize) * 0.5f;

            // Track (pill background)
            var trackGo = new GameObject($"Toggle_{buildingId}");
            trackGo.AddComponent<RectTransform>();
            trackGo.AddComponent<CanvasRenderer>();
            trackGo.AddComponent<Image>();
            trackGo.AddComponent<Button>();
            trackGo.transform.SetParent(parent, false);
            var trackRect = trackGo.GetComponent<RectTransform>();
            trackRect.anchorMin = new Vector2(1, 0.5f);
            trackRect.anchorMax = new Vector2(1, 0.5f);
            trackRect.pivot = new Vector2(1, 0.5f);
            trackRect.sizeDelta = new Vector2(trackW, trackH);
            trackRect.anchoredPosition = new Vector2(-6, 0);

            var trackImg = trackGo.GetComponent<Image>();
            trackImg.sprite = TMPFactory.GetRoundedSprite();
            trackImg.type = Image.Type.Sliced;
            trackImg.color = isOn ? ToggleOnColor : ToggleOffColor;

            // Thumb (white circle)
            var thumbGo = new GameObject("Thumb");
            thumbGo.AddComponent<RectTransform>();
            thumbGo.AddComponent<CanvasRenderer>();
            thumbGo.AddComponent<Image>();
            thumbGo.transform.SetParent(trackGo.transform, false);
            var thumbRect = thumbGo.GetComponent<RectTransform>();
            thumbRect.anchorMin = new Vector2(isOn ? 1 : 0, 0.5f);
            thumbRect.anchorMax = new Vector2(isOn ? 1 : 0, 0.5f);
            thumbRect.pivot = new Vector2(isOn ? 1 : 0, 0.5f);
            thumbRect.sizeDelta = new Vector2(thumbSize, thumbSize);
            thumbRect.anchoredPosition = new Vector2(isOn ? -thumbPad : thumbPad, 0);

            var thumbImg = thumbGo.GetComponent<Image>();
            thumbImg.sprite = TMPFactory.GetRoundedSprite();
            thumbImg.type = Image.Type.Sliced;
            thumbImg.color = Color.white;

            // Click handler
            var btn = trackGo.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(
#if IL2CPP
                (UnityEngine.Events.UnityAction)(() =>
#else
                () =>
#endif
            {
                bool newState = !GetStoreOpenState(buildingId);
                if (buildingId == PropertySaveData.ShackId)
                    WestvilleShack.ToggleStoreFromUI(newState);
                else
                    Dispensary.ToggleStoreFromUI(newState);
                RefreshPropertyRows();
            }
#if IL2CPP
            )
#endif
            );
        }

        // ==================================================================
        //  Refresh
        // ==================================================================

        private void RefreshOverview()
        {
            _overviewTooltipEntries = new List<(RectTransform, string)>();
            HideTooltip();

            RefreshInventoryItems();
            RefreshSalesItems();
            RefreshPropertyRows();
            RefreshEmployeesWage();

            RefreshInventoryChart();
            RefreshSalesChart();
        }

        private void RefreshEmployeesWage()
        {
            if (_overviewWageLabel == null) return;

            float total = 0f;
            foreach (var mgr in ManagerInstance.Active.Values)
                total += mgr.GetDailyWage();
            total += BudtenderInstance.Active.Count * BudtenderController.DailyWage;

            _overviewWageLabel.text = $"Daily Wages: ${total:F0}";
        }

        private void RefreshInventoryChart()
        {
            if (_inventoryChartContainer == null) return;

            var psd = PropertySaveData.Instance;
            var snapshots = psd?.GetInventorySnapshots();
            var chartData = new List<(string label, float value)>();

            if (snapshots != null && snapshots.Count > 0)
            {
                foreach (var snap in snapshots)
                    chartData.Add(($"{snap.GameDay}", snap.TotalCount));
            }

            BuildLineChart(_inventoryChartContainer, "Inventory (7 Day)", chartData, tooltipEntries: _overviewTooltipEntries);
        }

        private void RefreshSalesChart()
        {
            if (_salesChartContainer == null) return;

            var psd = PropertySaveData.Instance;
            var salesLog = psd?.GetSalesLog();

            // Filter by building if not "all"
            if (salesLog != null && _selectedBuildingId != AllPropertiesId)
                salesLog = salesLog.Where(s => s.BuildingId == _selectedBuildingId).ToList();

            var chartData = new List<(string label, float value)>();

            if (salesLog != null && salesLog.Count > 0)
            {
                int today = TimeManager.ElapsedDays;
                var dailyRevenue = new Dictionary<int, float>();
                foreach (var sale in salesLog)
                {
                    if (sale.GameDay > today - 7 && sale.GameDay <= today)
                    {
                        if (!dailyRevenue.ContainsKey(sale.GameDay))
                            dailyRevenue[sale.GameDay] = 0;
                        dailyRevenue[sale.GameDay] += sale.Quantity * sale.PricePerUnit;
                    }
                }

                for (int d = today - 6; d <= today; d++)
                {
                    float rev = dailyRevenue.TryGetValue(d, out var v) ? v : 0f;
                    chartData.Add(($"{d}", rev));
                }
            }

            BuildLineChart(_salesChartContainer, "Sales $ (7 Day)", chartData, isCurrency: true, tooltipEntries: _overviewTooltipEntries);
        }
    }
}
