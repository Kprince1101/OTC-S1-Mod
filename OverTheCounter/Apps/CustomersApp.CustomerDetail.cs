using S1API.UI;
using System;
using UnityEngine;
using UnityEngine.UI;
using OverTheCounter.Logic;
using OverTheCounter.Utilities;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Effects;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI.Phone.Map;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.UI.Items;
#else
using ScheduleOne.Economy;
using ScheduleOne.NPCs;
using ScheduleOne.Effects;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI.Phone.Map;
using ScheduleOne.Map;
using ScheduleOne.UI.Items;
#endif

namespace OverTheCounter.Apps
{
    public partial class CustomersApp
    {
        // Populates the right-side detail panel for the selected customer.
        // Called from SelectCustomer and RefreshCustomersPage.
        private void PopulateCustomerDetail(Customer customer, int tier)
        {
            CleanupCustomerDetailFields();
            ClearChildren(_custDetailPanel.transform);

            // ── Gather data safely ──
            string firstName = "Customer", lastName = "", regionStr = "";
            try
            {
                firstName = customer.NPC.FirstName ?? "Customer";
                lastName = customer.NPC.LastName ?? "";
                regionStr = customer.NPC.Region.ToString();
            }
            catch { }

            // ── Top strip (mugshot + name + optional GPS) ──
            const float TopH = 96f;

            var mugFrame = UIFactory.Panel("MugFrame", _custDetailPanel.transform, new Color(0.45f, 0.50f, 0.52f));
            var mugFrameRect = mugFrame.GetComponent<RectTransform>();
            mugFrameRect.anchorMin = new Vector2(0, 1);
            mugFrameRect.anchorMax = new Vector2(0, 1);
            mugFrameRect.pivot = new Vector2(0, 1);
            mugFrameRect.anchoredPosition = new Vector2(12, -12);
            mugFrameRect.sizeDelta = new Vector2(70, 70);

            var mugPanel = UIFactory.Panel("Mugshot", mugFrame.transform, new Color(0.15f, 0.15f, 0.15f));
            var mugPanelRect = mugPanel.GetComponent<RectTransform>();
            mugPanelRect.anchorMin = Vector2.zero;
            mugPanelRect.anchorMax = Vector2.one;
            mugPanelRect.offsetMin = new Vector2(2, 2);
            mugPanelRect.offsetMax = new Vector2(-2, -2);
            try
            {
                var mugSprite = customer.NPC.MugshotSprite;
                if (mugSprite != null)
                {
                    var mugImg = mugPanel.GetComponent<Image>();
                    mugImg.sprite = mugSprite;
                    mugImg.color = Color.white;
                }
            }
            catch { }

            var nameText = UIFactory.Text("Name", $"<b>{firstName} {lastName}</b>", _custDetailPanel.transform, 18, TextAnchor.MiddleLeft);
            nameText.color = Color.white;
            var nameRect = nameText.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 1);
            nameRect.anchorMax = new Vector2(1, 1);
            nameRect.pivot = new Vector2(0, 1);
            nameRect.anchoredPosition = new Vector2(90, -14);
            nameRect.sizeDelta = new Vector2(-90, 24);

            var subText = UIFactory.Text("Subtitle", $"Customer  \u00B7  {regionStr}", _custDetailPanel.transform, 13, TextAnchor.MiddleLeft);
            subText.color = new Color(0.55f, 0.55f, 0.55f);
            var subRect = subText.gameObject.GetComponent<RectTransform>();
            subRect.anchorMin = new Vector2(0, 1);
            subRect.anchorMax = new Vector2(1, 1);
            subRect.pivot = new Vector2(0, 1);
            subRect.anchoredPosition = new Vector2(90, -40);
            subRect.sizeDelta = new Vector2(-90, 20);

            // "Show on Map" button (tier 3 only) — top-right corner
            if (tier >= 3)
            {
                var mapBtnGo = UIFactory.Panel("MapBtn", _custDetailPanel.transform, new Color(0.15f, 0.35f, 0.45f));
                var mapBtnRect = mapBtnGo.GetComponent<RectTransform>();
                mapBtnRect.anchorMin = new Vector2(1, 1);
                mapBtnRect.anchorMax = new Vector2(1, 1);
                mapBtnRect.pivot = new Vector2(1, 1);
                mapBtnRect.anchoredPosition = new Vector2(-10, -14);
                mapBtnRect.sizeDelta = new Vector2(90, 24);

                var mapBtn = mapBtnGo.AddComponent<Button>();
                mapBtn.onClick.AddListener(new Action(() => ShowCustomerMap(customer)));

                var mapBtnLabel = UIFactory.Text("MapBtnLabel", "Show on Map", mapBtnGo.transform, 12, TextAnchor.MiddleCenter);
                mapBtnLabel.color = Color.white;
                var mapBtnLabelRect = mapBtnLabel.gameObject.GetComponent<RectTransform>();
                mapBtnLabelRect.anchorMin = Vector2.zero;
                mapBtnLabelRect.anchorMax = Vector2.one;
                mapBtnLabelRect.offsetMin = Vector2.zero;
                mapBtnLabelRect.offsetMax = Vector2.zero;
            }

            // ── Thin separator below top strip ──
            var sepLine = UIFactory.Panel("Sep", _custDetailPanel.transform, new Color(0.28f, 0.28f, 0.28f));
            var sepRect = sepLine.GetComponent<RectTransform>();
            sepRect.anchorMin = new Vector2(0, 1);
            sepRect.anchorMax = new Vector2(1, 1);
            sepRect.pivot = new Vector2(0.5f, 1);
            sepRect.anchoredPosition = new Vector2(0, -TopH);
            sepRect.sizeDelta = new Vector2(0, 1);

            // ── Scroll view for stats (below top strip) ──
            var scrollGo = UIFactory.Panel("DetailScroll", _custDetailPanel.transform, Color.clear);
            var scrollGoRect = scrollGo.GetComponent<RectTransform>();
            scrollGoRect.anchorMin = Vector2.zero;
            scrollGoRect.anchorMax = Vector2.one;
            scrollGoRect.offsetMin = Vector2.zero;
            scrollGoRect.offsetMax = new Vector2(0, -(TopH + 1));
            scrollGo.AddComponent<RectMask2D>();
            var sr = scrollGo.AddComponent<ScrollRect>();
            sr.horizontal = false;
            sr.scrollSensitivity = 20f;

            var contentGo = UIFactory.Panel("Content", scrollGo.transform, Color.clear);
            var contentRt = contentGo.GetComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0, 1);
            contentRt.anchorMax = new Vector2(1, 1);
            contentRt.pivot = new Vector2(0.5f, 1);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0, 0);
            var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.spacing = 4;
            vlg.padding = new RectOffset(12, 12, 8, 8);
            var csf = contentGo.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            sr.content = contentRt;

            var scrollContent = contentGo.transform;

            // ── Relationship ──
            float relationship = 0f;
            try { relationship = Mathf.Clamp01(customer.NPC.RelationData.NormalizedRelationDelta); } catch { }

            AddDetailLabel(scrollContent, "RELATIONSHIP");
            _custDetailRelFill = AddDetailBar(scrollContent, relationship, new Color(0.2f, 0.65f, 0.9f));
            AddDetailSpacer(scrollContent, 4f);

            // ── Addiction ──
            float addiction = 0f;
            try { addiction = Mathf.Clamp01(customer.CurrentAddiction); } catch { }

            AddDetailLabel(scrollContent, "ADDICTION");
            _custDetailAddFill = AddDetailBar(scrollContent, addiction, new Color(0.85f, 0.3f, 0.3f));
            AddDetailSpacer(scrollContent, 4f);

            // ── Standards (star rating) ──
            AddDetailLabel(scrollContent, "STANDARDS");
            try
            {
                var standards = customer.CustomerData.Standards;
                AddStandardsStars(scrollContent, standards);
            }
            catch
            {
                AddDetailText(scrollContent, "Unknown", 13, 18f);
            }
            AddDetailSpacer(scrollContent, 4f);

            // ── Connections ──
            AddDetailLabel(scrollContent, "CONNECTIONS");
            string connectionsStr = "None";
            try
            {
                var connections = customer.NPC.RelationData.Connections;
                if (connections != null && connections.Count > 0)
                {
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var npc in connections)
                    {
                        if (npc != null && !string.IsNullOrEmpty(npc.fullName))
                            names.Add(npc.fullName);
                    }
                    if (names.Count > 0)
                        connectionsStr = string.Join(", ", names);
                }
            }
            catch { }
            AddDetailText(scrollContent, connectionsStr, 13, 36f, wrap: true);
            AddDetailSpacer(scrollContent, 4f);

            // ── Favourite Effects ──
            AddDetailLabel(scrollContent, "FAVOURITE EFFECTS");
            try
            {
                var effects = customer.CustomerData.PreferredProperties;
                if (effects != null && effects.Count > 0)
                {
                    foreach (var eff in effects)
                    {
                        try
                        {
                            string effName = eff?.Name ?? "";
                            Color effColor = eff?.ProductColor ?? new Color(0.7f, 0.7f, 0.7f);
                            if (string.IsNullOrEmpty(effName)) continue;
                            var effText = AddDetailText(scrollContent, $"\u25CF  {effName}", 13, 18f);
                            effText.color = effColor;
                        }
                        catch { }
                    }
                }
                else
                {
                    AddDetailText(scrollContent, "None", 13, 18f);
                }
            }
            catch
            {
                AddDetailText(scrollContent, "None", 13, 18f);
            }
            AddDetailSpacer(scrollContent, 4f);

            // ── Weekly Purchases ──
            AddDetailLabel(scrollContent, "WEEKLY PURCHASES");
            _custDetailWeeklyText = AddDetailText(scrollContent, BuildWeeklyText(customer), 13, 40f, wrap: true);
            _custDetailWeeklyText.supportRichText = true;

        }

        // ==================================================================
        // Detail layout helpers
        // ==================================================================

        private void AddDetailLabel(Transform parent, string text)
        {
            var t = UIFactory.Text("Lbl_" + text, text, parent, 11, TextAnchor.MiddleLeft);
            t.color = new Color(0.52f, 0.52f, 0.52f);
            var le = t.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 16f;
            le.flexibleWidth = 1;
        }

        private Text AddDetailText(Transform parent, string text, int fontSize, float height, bool wrap = false)
        {
            var t = UIFactory.Text("Txt_" + text.GetHashCode(), text, parent, fontSize, TextAnchor.UpperLeft);
            t.color = new Color(0.78f, 0.78f, 0.78f);
            if (wrap) t.horizontalOverflow = HorizontalWrapMode.Wrap;
            var le = t.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1;
            return t;
        }

        private void AddDetailSpacer(Transform parent, float height)
        {
            var s = UIFactory.Panel("Spacer", parent, Color.clear);
            var le = s.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1;
            var img = s.GetComponent<Image>();
            if (img != null) img.raycastTarget = false;
        }

        private RectTransform AddDetailBar(Transform parent, float value, Color fillColor)
        {
            var barContainer = UIFactory.Panel("Bar", parent, Color.clear);
            var le = barContainer.AddComponent<LayoutElement>();
            le.preferredHeight = 16f;
            le.flexibleWidth = 1;

            var bg = UIFactory.Panel("Bg", barContainer.transform, new Color(0.2f, 0.2f, 0.2f));
            var bgRt = bg.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;

            var fill = UIFactory.Panel("Fill", barContainer.transform, fillColor);
            var fillRt = fill.GetComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = new Vector2(Mathf.Clamp01(value), 1);
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;

            int pct = Mathf.RoundToInt(value * 100f);
            var pctText = UIFactory.Text("Pct", $"{pct}%", barContainer.transform, 11, TextAnchor.MiddleCenter);
            pctText.color = Color.white;
            var pctRt = pctText.gameObject.GetComponent<RectTransform>();
            pctRt.anchorMin = Vector2.zero;
            pctRt.anchorMax = Vector2.one;
            pctRt.offsetMin = Vector2.zero;
            pctRt.offsetMax = Vector2.zero;
            var shadow = pctText.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.7f);
            shadow.effectDistance = new Vector2(1, -1);

            return fillRt;
        }

        private void AddStandardsStars(Transform parent, ECustomerStandard standards)
        {
            // Colors matching the game's quality star palette
            Color starColor = standards switch
            {
                ECustomerStandard.VeryLow  => new Color32(80, 145, 50, 255),
                ECustomerStandard.Low      => new Color32(80, 145, 50, 255),
                ECustomerStandard.Moderate => new Color32(100, 190, 255, 255),
                ECustomerStandard.High     => new Color32(225, 75, 255, 255),
                ECustomerStandard.VeryHigh => new Color32(255, 200, 50, 255),
                _                          => Color.white
            };
            string label = standards switch
            {
                ECustomerStandard.VeryLow  => "Very Low",
                ECustomerStandard.Low      => "Low",
                ECustomerStandard.Moderate => "Moderate",
                ECustomerStandard.High     => "High",
                ECustomerStandard.VeryHigh => "Very High",
                _                          => "Unknown"
            };

            // Row panel — full width via parent VLG, fixed height
            var rowGo = UIFactory.Panel("StandardsRow", parent, Color.clear);
            var rowImg = rowGo.GetComponent<Image>();
            if (rowImg != null) rowImg.raycastTarget = false;
            var rowLe = rowGo.AddComponent<LayoutElement>();
            rowLe.preferredHeight = 20f;
            rowLe.flexibleWidth = 1;

            // Star: anchored to left edge, 18px wide
            var starGo = new GameObject("StarIcon");
            starGo.transform.SetParent(rowGo.transform, false);
            var starRt = starGo.AddComponent<RectTransform>();
            starRt.anchorMin = new Vector2(0, 0);
            starRt.anchorMax = new Vector2(0, 1);
            starRt.offsetMin = Vector2.zero;
            starRt.offsetMax = new Vector2(18, 0);
            var starImg = starGo.AddComponent<Image>();
            starImg.sprite = GetOrCreateStarSprite();
            starImg.color = starColor;
            starImg.preserveAspect = true;
            starImg.raycastTarget = false;

            // Label: starts 22px from left (18 star + 4 gap), fills rest of row
            var labelText = UIFactory.Text("StandardLabel", label, rowGo.transform, 13, TextAnchor.MiddleLeft);
            labelText.color = new Color(0.78f, 0.78f, 0.78f);
            var labelRt = labelText.gameObject.GetComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0, 0);
            labelRt.anchorMax = new Vector2(1, 1);
            labelRt.offsetMin = new Vector2(22, 0);
            labelRt.offsetMax = Vector2.zero;
        }

        private static Sprite _custStarSprite;

        private static Sprite GetOrCreateStarSprite()
        {
            if (_custStarSprite != null) return _custStarSprite;

            try
            {
                var allQiic = Resources.FindObjectsOfTypeAll<QualityItemInfoContent>();
                if (allQiic != null)
                {
                    for (int i = 0; i < allQiic.Length; i++)
                    {
                        if (allQiic[i]?.Star?.sprite != null)
                        {
                            _custStarSprite = allQiic[i].Star.sprite;
                            return _custStarSprite;
                        }
                    }
                }
            }
            catch { }

            _custStarSprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/Knob.psd");
            return _custStarSprite;
        }

        // ==================================================================
        // Customer map page (full-screen, opened via "Show on Map" button)
        // ==================================================================

        private void ShowCustomerMap(Customer customer)
        {
            // Save scroll position before hiding the customers page
            _savedScrollPos = _customersScroll != null ? _customersScroll.verticalNormalizedPosition : 1f;

            if (_customersPage != null) _customersPage.SetActive(false);

            // Build map page as sibling of _customersPage under _rootPanel
            const float BackH = 32f;
            _custMapPage = UIFactory.Panel("CustMapPage", _rootPanel.transform, new Color(0.10f, 0.10f, 0.12f));
            var pageRect = _custMapPage.GetComponent<RectTransform>();
            pageRect.anchorMin = Vector2.zero;
            pageRect.anchorMax = Vector2.one;
            pageRect.offsetMin = Vector2.zero;
            pageRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            // Back bar
            var backBar = UIFactory.Panel("BackBar", _custMapPage.transform, new Color(0.10f, 0.10f, 0.12f));
            var backBarRect = backBar.GetComponent<RectTransform>();
            backBarRect.anchorMin = new Vector2(0, 1);
            backBarRect.anchorMax = new Vector2(1, 1);
            backBarRect.pivot = new Vector2(0.5f, 1);
            backBarRect.anchoredPosition = Vector2.zero;
            backBarRect.sizeDelta = new Vector2(0, BackH);
            backBar.AddComponent<Button>().onClick.AddListener(new Action(CloseCustomerMap));

            var backLabel = UIFactory.Text("BackLbl", "\u2190  Back", backBar.transform, 14, TextAnchor.MiddleLeft);
            backLabel.color = new Color(0.4f, 0.7f, 1f);
            var backLabelRect = backLabel.gameObject.GetComponent<RectTransform>();
            backLabelRect.anchorMin = Vector2.zero;
            backLabelRect.anchorMax = Vector2.one;
            backLabelRect.offsetMin = new Vector2(12, 0);
            backLabelRect.offsetMax = Vector2.zero;

            string custName = "";
            try { custName = customer.NPC.FirstName + " " + customer.NPC.LastName; } catch { }
            var titleLabel = UIFactory.Text("MapCustName", custName, backBar.transform, 14, TextAnchor.MiddleCenter);
            titleLabel.color = Color.white;
            var titleRect = titleLabel.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = Vector2.zero;
            titleRect.offsetMax = Vector2.zero;

            // Separator
            var sep = UIFactory.Panel("Sep", _custMapPage.transform, new Color(0.22f, 0.22f, 0.22f));
            var sepRect = sep.GetComponent<RectTransform>();
            sepRect.anchorMin = new Vector2(0, 1);
            sepRect.anchorMax = new Vector2(1, 1);
            sepRect.pivot = new Vector2(0.5f, 1);
            sepRect.anchoredPosition = new Vector2(0, -BackH);
            sepRect.sizeDelta = new Vector2(0, 1);

            // Map area (fills rest of page below back bar)
            var mapArea = UIFactory.Panel("MapArea", _custMapPage.transform, new Color(0.20f, 0.259f, 0.298f));
            var mapAreaRect = mapArea.GetComponent<RectTransform>();
            mapAreaRect.anchorMin = Vector2.zero;
            mapAreaRect.anchorMax = Vector2.one;
            mapAreaRect.offsetMin = Vector2.zero;
            mapAreaRect.offsetMax = new Vector2(0, -(BackH + 1));

            BuildMapInArea(mapArea, customer);
            StartMinimapTracking();
        }

        private void CloseCustomerMap()
        {
            StopMinimapTracking();
            CleanupCustomerDetailFields();

            if (_custMapPage != null)
            {
                UnityEngine.Object.Destroy(_custMapPage);
                _custMapPage = null;
            }

            if (_customersPage != null) _customersPage.SetActive(true);

            // Restore scroll position
            if (_customersScroll != null)
                _customersScroll.verticalNormalizedPosition = _savedScrollPos;
        }

        private void BuildMapInArea(GameObject mapArea, Customer customer)
        {
            Sprite mapSprite = null;
            try
            {
                var mapApp = PlayerSingleton<MapApp>.Instance;
                if (mapApp != null) mapSprite = mapApp.MainMapSprite;
            }
            catch { }

            if (mapSprite == null)
            {
                var noMapText = UIFactory.Text("NoMap", "Map unavailable", mapArea.transform, 14, TextAnchor.MiddleCenter);
                noMapText.color = new Color(0.4f, 0.4f, 0.4f);
                var noMapRect = noMapText.gameObject.GetComponent<RectTransform>();
                noMapRect.anchorMin = Vector2.zero;
                noMapRect.anchorMax = Vector2.one;
                noMapRect.offsetMin = Vector2.zero;
                noMapRect.offsetMax = Vector2.zero;
                return;
            }

            mapArea.AddComponent<RectMask2D>();
            _minimapBgImage = mapArea.GetComponent<Image>();

            var mapObj = new GameObject("MapImage");
            mapObj.transform.SetParent(mapArea.transform, false);
            var mapImage = mapObj.AddComponent<Image>();
            mapImage.sprite = mapSprite;
            mapImage.preserveAspect = true;

            _minimapImageRect = mapObj.GetComponent<RectTransform>();
            _minimapImageRect.anchorMin = new Vector2(0.5f, 0.5f);
            _minimapImageRect.anchorMax = new Vector2(0.5f, 0.5f);
            _minimapImageRect.pivot = new Vector2(0.5f, 0.5f);
            _minimapDisplaySize = 1200f;
            _minimapImageRect.sizeDelta = new Vector2(_minimapDisplaySize, _minimapDisplaySize);

            _minimapContentW = mapSprite.rect.width;
            _minimapContentH = mapSprite.rect.height;
            try
            {
                var mapApp = PlayerSingleton<MapApp>.Instance;
                if (mapApp?.ContentRect != null)
                {
                    _minimapContentW = mapApp.ContentRect.rect.width;
                    _minimapContentH = mapApp.ContentRect.rect.height;
                }
            }
            catch { }

            // NPC marker (circle with mugshot)
            float markerSize = 40f;
            var markerObj = new GameObject("Marker");
            markerObj.transform.SetParent(mapArea.transform, false);
            var markerImg = markerObj.AddComponent<Image>();
            markerImg.sprite = GetOrCreateCircleSprite();
            markerImg.color = Color.white;
            var markerRect = markerObj.GetComponent<RectTransform>();
            markerRect.anchorMin = new Vector2(0.5f, 0.5f);
            markerRect.anchorMax = new Vector2(0.5f, 0.5f);
            markerRect.pivot = new Vector2(0.5f, 0.5f);
            markerRect.sizeDelta = new Vector2(markerSize, markerSize);
            markerRect.anchoredPosition = Vector2.zero;

            var markerIconObj = new GameObject("MarkerIcon");
            markerIconObj.transform.SetParent(markerObj.transform, false);
            _minimapMarkerIcon = markerIconObj.AddComponent<Image>();
            _minimapMarkerIcon.preserveAspect = true;
            _minimapMarkerIcon.color = Color.clear;
            var markerIconRect = markerIconObj.GetComponent<RectTransform>();
            markerIconRect.anchorMin = new Vector2(0.1f, 0.1f);
            markerIconRect.anchorMax = new Vector2(0.9f, 0.9f);
            markerIconRect.offsetMin = Vector2.zero;
            markerIconRect.offsetMax = Vector2.zero;
            try
            {
                var mugSprite = customer.NPC.MugshotSprite;
                if (mugSprite != null)
                {
                    _minimapMarkerIcon.sprite = mugSprite;
                    _minimapMarkerIcon.color = Color.white;
                }
            }
            catch { }

            // Destination marker (inactive for customers)
            var destObj = new GameObject("DestMarker");
            destObj.transform.SetParent(_minimapImageRect.transform, false);
            var destImg = destObj.AddComponent<Image>();
            destImg.color = new Color(1f, 0f, 0f, 0.45f);
            _minimapDestRect = destObj.GetComponent<RectTransform>();
            _minimapDestRect.anchorMin = new Vector2(0.5f, 0.5f);
            _minimapDestRect.anchorMax = new Vector2(0.5f, 0.5f);
            _minimapDestRect.pivot = new Vector2(0.5f, 0.5f);
            _minimapDestRect.sizeDelta = new Vector2(8f, 8f);
            _minimapDestRect.gameObject.SetActive(false);
        }

        // ==================================================================
        // Cleanup
        // ==================================================================

        private void CleanupCustomerDetailFields()
        {
            _custDetailRelFill = null;
            _custDetailAddFill = null;
            _custDetailWeeklyText = null;
            _minimapImageRect = null;
            _minimapMarkerIcon = null;
            _minimapDestRect = null;
            _minimapBgImage = null;
        }

        // ==================================================================
        // Periodic refresh
        // ==================================================================

        private void RefreshCustomerDetail()
        {
            var c = _selectedCustomer;
            if (c == null) return;

            if (_custDetailRelFill != null)
            {
                float rel = 0f;
                try { rel = Mathf.Clamp01(c.NPC.RelationData.NormalizedRelationDelta); } catch { }
                _custDetailRelFill.anchorMax = new Vector2(rel, 1);
            }

            if (_custDetailAddFill != null)
            {
                float add = 0f;
                try { add = Mathf.Clamp01(c.CurrentAddiction); } catch { }
                _custDetailAddFill.anchorMax = new Vector2(add, 1);
            }

            if (_custDetailWeeklyText != null)
                _custDetailWeeklyText.text = BuildWeeklyText(c);
        }

        // ==================================================================
        // Weekly text helper
        // ==================================================================

        private string BuildWeeklyText(Customer customer)
        {
            try
            {
                var records = customer.WeeklyPurchaseRecord;
                if (records == null || records.Count == 0)
                    return "No purchases this week";

                float totalSpent = 0f;
                Customer.ProductPurchaseRecord topRecord = null;
                float topSpent = -1f;

                foreach (var rec in records)
                {
                    if (rec == null) continue;
                    totalSpent += rec.TotalSpent;
                    if (rec.TotalSpent > topSpent)
                    {
                        topSpent = rec.TotalSpent;
                        topRecord = rec;
                    }
                }

                if (topRecord == null)
                    return "No purchases this week";

                string topName = topRecord.ProductID;
                try
                {
#if IL2CPP
                    var def = Il2CppScheduleOne.Registry.GetItem<Il2CppScheduleOne.Product.ProductDefinition>(topRecord.ProductID);
#else
                    var def = ScheduleOne.Registry.GetItem<ScheduleOne.Product.ProductDefinition>(topRecord.ProductID);
#endif
                    if (def != null && !string.IsNullOrEmpty(def.Name))
                        topName = def.Name;
                }
                catch { }

                return $"Top: {topName}  \u00D7{topRecord.Quantity}  (${topRecord.TotalSpent:N0})\nTotal:  ${totalSpent:N0}";
            }
            catch
            {
                return "No purchases this week";
            }
        }
    }
}
