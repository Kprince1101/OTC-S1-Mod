using OverTheCounter.SaveData;
using OverTheCounter.UI;
using S1API.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppTMPro;
#else
using TMPro;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        // ==================================================================
        //  Sales tab — grouped transaction log with summary stats
        // ==================================================================

        private Transform _salesTableContent;
        private TextMeshProUGUI _salesTotalRevenue;
        private TextMeshProUGUI _salesTotalCount;
        private TextMeshProUGUI _salesAvgPrice;
        private TextMeshProUGUI _salesTitleLabel;

        // Tooltip entries for mouse-follow hover (rendered by shared tooltip in GreenTabApp.cs)
        private List<(RectTransform rect, string text)> _salesTooltipEntries;

        private void BuildSalesPanel(Transform parent)
        {
            var panel = UIFactory.Panel("SalesPanel", parent, BgDark);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(NAV_WIDTH_FRAC, 0);
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            _tabPanels[AppTab.Sales] = panel;

            float pad = 10f;

            // Title — property name + inline hint, updated during refresh
            _salesTitleLabel = TMPFactory.Text("SalesTabTitle", "Sales",
                panel.transform, 16, TextAlignmentOptions.TopLeft);
            _salesTitleLabel.color = Color.white;
            _salesTitleLabel.richText = true;
            var titleRect = _salesTitleLabel.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(0.95f, 1);
            titleRect.offsetMin = new Vector2(pad, -28);
            titleRect.offsetMax = new Vector2(0, -pad);

            // Summary stats row
            float statsY = -32f;
            float statsH = 40f;

            var statsPanel = RoundedPanel("SalesStats", panel.transform, CardBg);
            var statsRect = statsPanel.GetComponent<RectTransform>();
            statsRect.anchorMin = new Vector2(0, 1);
            statsRect.anchorMax = new Vector2(1, 1);
            statsRect.pivot = new Vector2(0.5f, 1);
            statsRect.anchoredPosition = new Vector2(0, statsY);
            statsRect.sizeDelta = new Vector2(-pad * 2, statsH);

            _salesTotalRevenue = TMPFactory.Text("StatRev", "Revenue: $0",
                statsPanel.transform, 15, TextAlignmentOptions.Left, FontStyles.Bold);
            _salesTotalRevenue.color = AccentGreen;
            PositionLabel(_salesTotalRevenue, 0, 0, 0.33f, 1, 12, 0);

            _salesTotalCount = TMPFactory.Text("StatCnt", "Sales: 0",
                statsPanel.transform, 15, TextAlignmentOptions.Left);
            _salesTotalCount.color = Color.white;
            PositionLabel(_salesTotalCount, 0.33f, 0, 0.66f, 1, 0, 0);

            _salesAvgPrice = TMPFactory.Text("StatAvg", "Avg: $0",
                statsPanel.transform, 15, TextAlignmentOptions.Left);
            _salesAvgPrice.color = Color.white;
            PositionLabel(_salesAvgPrice, 0.66f, 0, 1, 1, 0, 0);

            // Column header
            float headerY = statsY - statsH - 6;
            float headerH = 24f;
            var headerPanel = UIFactory.Panel("TableHeader", panel.transform,
                new Color(0.12f, 0.12f, 0.12f));
            var headerRect = headerPanel.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0.5f, 1);
            headerRect.anchoredPosition = new Vector2(0, headerY);
            headerRect.sizeDelta = new Vector2(-pad * 2, headerH);

            // Columns: Day | Time | Customer | Products | Subtotal | Tip | Total
            BuildTableHeaderCell(headerPanel.transform, "Day", 0f, 0.07f);
            BuildTableHeaderCell(headerPanel.transform, "Time", 0.07f, 0.16f);
            BuildTableHeaderCell(headerPanel.transform, "Customer", 0.16f, 0.30f);
            BuildTableHeaderCell(headerPanel.transform, "Products", 0.30f, 0.58f);
            BuildTableHeaderCell(headerPanel.transform, "Subtotal", 0.58f, 0.72f);
            BuildTableHeaderCell(headerPanel.transform, "Tip", 0.72f, 0.85f);
            BuildTableHeaderCell(headerPanel.transform, "Total", 0.85f, 1f);

            // Scrollable table body
            float tableTop = headerY - headerH;
            var scrollContainer = UIFactory.Panel("SalesScrollContainer", panel.transform, Color.clear);
            var scrollContRect = scrollContainer.GetComponent<RectTransform>();
            scrollContRect.anchorMin = new Vector2(0, 0);
            scrollContRect.anchorMax = new Vector2(1, 1);
            scrollContRect.offsetMin = new Vector2(pad, pad);
            scrollContRect.offsetMax = new Vector2(-pad, tableTop);

            var scrollView = UIFactory.Panel("SalesScrollView", scrollContainer.transform, Color.clear);
            var scrollViewRect = scrollView.GetComponent<RectTransform>();
            scrollViewRect.anchorMin = Vector2.zero;
            scrollViewRect.anchorMax = Vector2.one;
            scrollViewRect.offsetMin = Vector2.zero;
            scrollViewRect.offsetMax = Vector2.zero;

            var scroll = scrollView.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            var viewport = UIFactory.Panel("SalesViewport", scrollView.transform, Color.clear);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = Vector2.zero;
            viewport.AddComponent<RectMask2D>();

            var content = new GameObject("SalesContent");
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;

            scroll.viewport = vpRect;
            scroll.content = contentRect;

            _salesTableContent = content.transform;

            panel.SetActive(false);
        }

        // Tooltip hover logic is handled by the shared UpdateTooltipHover in GreenTabApp.cs

        private static void BuildTableHeaderCell(Transform parent, string text, float xMin, float xMax)
        {
            var label = TMPFactory.Text($"Hdr_{text}", text, parent, 15, TextAlignmentOptions.Left, FontStyles.Bold);
            label.color = TextMuted;
            var rt = label.gameObject.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(xMin, 0);
            rt.anchorMax = new Vector2(xMax, 1);
            rt.offsetMin = new Vector2(6, 0);
            rt.offsetMax = new Vector2(-2, 0);
        }

        private void RefreshSales()
        {
            if (_salesTableContent == null) return;

            // Clear tooltip entries
            HideTooltip();
            _salesTooltipEntries = new List<(RectTransform, string)>();

            // Clear table
            for (int i = _salesTableContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_salesTableContent.GetChild(i).gameObject);

            // Update title with selected property name + inline hint
            if (_salesTitleLabel != null)
            {
                string displayName = GetBuildingDisplayName(_selectedBuildingId);
                _salesTitleLabel.text = $"<b>{displayName}</b>  <color=#9E9E9E><size=90%>Select another property via dropdown</size></color>";
            }

            var psd = PropertySaveData.Instance;
            var salesLog = psd?.GetSalesLog();

            // Filter by building if not "all"
            if (salesLog != null && _selectedBuildingId != AllPropertiesId)
                salesLog = salesLog.Where(s => s.BuildingId == _selectedBuildingId).ToList();

            if (salesLog == null || salesLog.Count == 0)
            {
                if (_salesTotalRevenue != null) _salesTotalRevenue.text = "Revenue: $0";
                if (_salesTotalCount != null) _salesTotalCount.text = "Sales: 0";
                if (_salesAvgPrice != null) _salesAvgPrice.text = "Avg: $0";

                var contentRect = _salesTableContent.GetComponent<RectTransform>();
                contentRect.sizeDelta = new Vector2(0, 30);

                var empty = TMPFactory.Text("EmptySales", "No sales recorded yet.",
                    _salesTableContent, 15, TextAlignmentOptions.Center);
                empty.color = TextDim;
                var emptyRect = empty.gameObject.GetComponent<RectTransform>();
                emptyRect.anchorMin = new Vector2(0.1f, 0);
                emptyRect.anchorMax = new Vector2(0.9f, 1);
                emptyRect.offsetMin = Vector2.zero;
                emptyRect.offsetMax = Vector2.zero;
                return;
            }

            // Compute stats
            float totalRevenue = 0f;
            float totalTips = 0f;
            foreach (var sale in salesLog)
            {
                totalRevenue += sale.Quantity * sale.PricePerUnit;
                totalTips += sale.TipAmount;
            }

            // Group into transactions
            var transactions = GroupTransactions(salesLog);
            int txCount = transactions.Count;
            float avgPerTx = txCount > 0 ? totalRevenue / txCount : 0f;

            float grandRevenue = totalRevenue + totalTips;
            if (_salesTotalRevenue != null) _salesTotalRevenue.text = $"Revenue: ${grandRevenue:F0}";
            if (_salesTotalCount != null) _salesTotalCount.text = $"Transactions: {txCount}";
            if (_salesAvgPrice != null) _salesAvgPrice.text = $"Tips: ${totalTips:F0}";

            // Sort transactions by day desc, then hour desc
            transactions.Sort((a, b) =>
            {
                int dayComp = b.GameDay.CompareTo(a.GameDay);
                return dayComp != 0 ? dayComp : b.GameHour.CompareTo(a.GameHour);
            });

            // Render rows
            float rowH = 28f;
            float yOffset = 0f;

            for (int i = 0; i < transactions.Count; i++)
            {
                var tx = transactions[i];
                Color rowBg = i % 2 == 0 ? Color.clear : new Color(0.10f, 0.10f, 0.10f, 0.5f);

                var row = UIFactory.Panel($"TxRow_{i}", _salesTableContent, rowBg);
                var rowRect = row.GetComponent<RectTransform>();
                rowRect.anchorMin = new Vector2(0, 1);
                rowRect.anchorMax = new Vector2(1, 1);
                rowRect.pivot = new Vector2(0.5f, 1);
                rowRect.sizeDelta = new Vector2(0, rowH);
                rowRect.anchoredPosition = new Vector2(0, yOffset);

                // Day
                BuildSalesCell(row.transform, $"{tx.GameDay}", 0f, 0.07f, Color.white);

                // Time
                string timeStr = tx.GameHour > 0 ? FormatTime12h(tx.GameHour) : "--";
                BuildSalesCell(row.transform, timeStr, 0.07f, 0.16f, TextMuted);

                // Customer
                string custName = !string.IsNullOrEmpty(tx.CustomerName) ? tx.CustomerName : "Unknown";
                var custLabel = BuildSalesCell(row.transform, custName, 0.16f, 0.30f, new Color(0.85f, 0.85f, 0.85f));
                custLabel.overflowMode = TextOverflowModes.Ellipsis;
                _salesTooltipEntries.Add((custLabel.gameObject.GetComponent<RectTransform>(), custName));

                // Products — compact summary, register for tooltip hover
                string productSummary = BuildProductSummary(tx.Items);
                string productFull = BuildProductFullText(tx.Items);
                var productLabel = BuildSalesCell(row.transform, productSummary, 0.30f, 0.58f,
                    new Color(0.82f, 0.82f, 0.82f));
                productLabel.overflowMode = TextOverflowModes.Ellipsis;
                _salesTooltipEntries.Add((productLabel.gameObject.GetComponent<RectTransform>(), productFull));

                // Compute money values
                float txSubtotal = 0f;
                float txTip = 0f;
                foreach (var item in tx.Items)
                {
                    txSubtotal += item.Quantity * item.PricePerUnit;
                    txTip += item.TipAmount;
                }
                float txGrandTotal = txSubtotal + txTip;

                // Subtotal
                BuildSalesCell(row.transform, $"${txSubtotal:F0}", 0.58f, 0.72f, Color.white);

                // Tip
                string tipStr = txTip > 0 ? $"${txTip:F0}" : "--";
                BuildSalesCell(row.transform, tipStr, 0.72f, 0.85f,
                    txTip > 0 ? AccentGreen : TextDim);

                // Total (subtotal + tip)
                BuildSalesCell(row.transform, $"${txGrandTotal:F0}", 0.85f, 1f, AccentGreen, FontStyles.Bold);

                yOffset -= rowH;
            }

            var cRect = _salesTableContent.GetComponent<RectTransform>();
            cRect.sizeDelta = new Vector2(0, -yOffset);
        }

        // ------------------------------------------------------------------
        //  Transaction grouping
        // ------------------------------------------------------------------

        private struct SalesTransaction
        {
            public int GameDay;
            public int GameHour;
            public string CustomerName;
            public List<OtcSaleRecord> Items;
        }

        private static List<SalesTransaction> GroupTransactions(List<OtcSaleRecord> sales)
        {
            var result = new List<SalesTransaction>();
            var byTxId = new Dictionary<string, List<OtcSaleRecord>>();
            var noTxId = new List<OtcSaleRecord>();

            foreach (var sale in sales)
            {
                if (!string.IsNullOrEmpty(sale.TransactionId))
                {
                    // Include GameDay in key to prevent cross-day merging from old colliding IDs
                    string key = $"{sale.TransactionId}_{sale.GameDay}";
                    if (!byTxId.TryGetValue(key, out var list))
                    {
                        list = new List<OtcSaleRecord>();
                        byTxId[key] = list;
                    }
                    list.Add(sale);
                }
                else
                {
                    noTxId.Add(sale);
                }
            }

            foreach (var kvp in byTxId)
            {
                var items = kvp.Value;
                var first = items[0];
                result.Add(new SalesTransaction
                {
                    GameDay = first.GameDay,
                    GameHour = first.GameHour,
                    CustomerName = first.CustomerName,
                    Items = items
                });
            }

            // Legacy records: group by Day + Customer
            var legacyGroups = new Dictionary<string, List<OtcSaleRecord>>();
            foreach (var sale in noTxId)
            {
                string key = $"{sale.GameDay}_{sale.CustomerName ?? ""}";
                if (!legacyGroups.TryGetValue(key, out var list))
                {
                    list = new List<OtcSaleRecord>();
                    legacyGroups[key] = list;
                }
                list.Add(sale);
            }
            foreach (var kvp in legacyGroups)
            {
                var items = kvp.Value;
                var first = items[0];
                result.Add(new SalesTransaction
                {
                    GameDay = first.GameDay,
                    GameHour = first.GameHour,
                    CustomerName = first.CustomerName,
                    Items = items
                });
            }

            return result;
        }

        // ------------------------------------------------------------------
        //  Product text helpers
        // ------------------------------------------------------------------

        private static string BuildProductSummary(List<OtcSaleRecord> items)
        {
            // Aggregate same-name products within the transaction
            var aggregated = new Dictionary<string, int>();
            foreach (var it in items)
            {
                string name = it.ProductName ?? it.ProductId ?? "Unknown";
                if (aggregated.ContainsKey(name))
                    aggregated[name] += it.Quantity;
                else
                    aggregated[name] = it.Quantity;
            }

            if (aggregated.Count == 1)
            {
                var kv = aggregated.First();
                return $"{kv.Key} x{kv.Value}";
            }

            var parts = new List<string>();
            foreach (var kv in aggregated)
                parts.Add($"{kv.Key} x{kv.Value}");
            return string.Join(", ", parts);
        }

        private static string BuildProductFullText(List<OtcSaleRecord> items)
        {
            // Aggregate by name+quality for the full tooltip
            var groups = new Dictionary<string, (string name, int quality, int qty, float price)>();
            foreach (var it in items)
            {
                string name = it.ProductName ?? it.ProductId ?? "Unknown";
                string key = $"{name}_{it.QualityLevel}";
                if (groups.TryGetValue(key, out var existing))
                    groups[key] = (existing.name, existing.quality, existing.qty + it.Quantity, existing.price);
                else
                    groups[key] = (name, it.QualityLevel, it.Quantity, it.PricePerUnit);
            }

            var lines = new List<string>();
            foreach (var g in groups.Values)
            {
                string qualStr = g.quality > 0 ? $" ({QualityLabel(g.quality)})" : "";
                float lineTotal = g.qty * g.price;
                lines.Add($"{g.name}{qualStr} x{g.qty} @ ${g.price:F0} = ${lineTotal:F0}");
            }
            return string.Join("\n", lines);
        }

        private static string QualityLabel(int q) => q switch
        {
            1 => "Standard",
            2 => "Premium",
            3 => "Heavenly",
            4 => "Masterwork",
            _ => $"Q{q}"
        };

        // ------------------------------------------------------------------
        //  Cell helper
        // ------------------------------------------------------------------

        private static TextMeshProUGUI BuildSalesCell(Transform parent, string text,
            float xMin, float xMax, Color color, FontStyles style = FontStyles.Normal)
        {
            var label = TMPFactory.Text("Cell", text, parent, 15, TextAlignmentOptions.Left, style);
            label.color = color;
            var rt = label.gameObject.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(xMin, 0);
            rt.anchorMax = new Vector2(xMax, 1);
            rt.offsetMin = new Vector2(6, 0);
            rt.offsetMax = new Vector2(-2, 0);
            return label;
        }
    }
}
