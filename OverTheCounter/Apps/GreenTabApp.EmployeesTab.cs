using OverTheCounter.Logic;
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
#else
using TMPro;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        // ==================================================================
        //  Employees / Staffing tab
        // ==================================================================

        private TextMeshProUGUI _staffingTitle;
        private TextMeshProUGUI _payrollLabel;
        private Transform _staffCardGrid;
        private readonly List<GameObject> _staffCards = new();
        // Shelf toggle removed — budtenders now access all storage (closets last)

        // Staff card layout — generous sizing, max ~5 cards
        private const float STAFF_CARD_WIDTH = 240f;
        private const float STAFF_CARD_HEIGHT = 160f;
        private const int STAFF_COLUMNS = 3;

        // Colors specific to staffing tab
        private static readonly Color FireRed = new(0.75f, 0.22f, 0.22f);
        private static readonly Color HireGreen = new(0.22f, 0.56f, 0.24f);
        private static readonly Color DisabledBtn = new(0.20f, 0.20f, 0.20f);

        private void BuildEmployeesPanel(Transform parent)
        {
            var panel = UIFactory.Panel("EmployeesPanel", parent, BgDark);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(NAV_WIDTH_FRAC, 0);
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            _tabPanels[AppTab.Employees] = panel;

            float pad = 10f;

            // ---- Header row ----
            _staffingTitle = TMPFactory.Text("StaffingTitle", "Staffing",
                panel.transform, 16, TextAlignmentOptions.TopLeft, FontStyles.Bold);
            _staffingTitle.color = Color.white;
            var titleRect = _staffingTitle.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(0.5f, 1);
            titleRect.offsetMin = new Vector2(pad, -32);
            titleRect.offsetMax = new Vector2(0, -pad);

            _payrollLabel = TMPFactory.Text("PayrollLabel", "",
                panel.transform, 15, TextAlignmentOptions.TopRight);
            _payrollLabel.color = TextMuted;
            var payRect = _payrollLabel.gameObject.GetComponent<RectTransform>();
            payRect.anchorMin = new Vector2(0.5f, 1);
            payRect.anchorMax = new Vector2(1, 1);
            payRect.offsetMin = new Vector2(0, -32);
            payRect.offsetMax = new Vector2(-pad, -pad);

            // ---- Scrollable card area ----
            var scrollGo = UIFactory.Panel("StaffScroll", panel.transform, Color.clear);
            var scrollRect = scrollGo.GetComponent<RectTransform>();
            scrollRect.anchorMin = new Vector2(0, 0.12f); // leave room for footer
            scrollRect.anchorMax = new Vector2(1, 0.9f);  // leave room for header
            scrollRect.offsetMin = new Vector2(pad, 0);
            scrollRect.offsetMax = new Vector2(-pad, 0);

            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            // Viewport (mask)
            var viewport = UIFactory.Panel("StaffViewport", scrollGo.transform, Color.clear);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = Vector2.zero;
            viewport.AddComponent<RectMask2D>();

            // Content container
            var contentGo = new GameObject("StaffContent");
            contentGo.transform.SetParent(viewport.transform, false);
            var contentRect = contentGo.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;

            scroll.viewport = vpRect;
            scroll.content = contentRect;
            _staffCardGrid = contentGo.transform;

            // ---- Footer ----
            var footer = UIFactory.Panel("StaffFooter", panel.transform, FooterBg);
            var footerRect = footer.GetComponent<RectTransform>();
            footerRect.anchorMin = Vector2.zero;
            footerRect.anchorMax = new Vector2(1, 0.12f);
            footerRect.offsetMin = Vector2.zero;
            footerRect.offsetMax = Vector2.zero;

            var footerInfo = TMPFactory.Text("FooterInfo",
                "Staff access all storage (closets used last)",
                footer.transform, 15, TextAlignmentOptions.Center);
            footerInfo.color = TextDim;
            var infoRect = footerInfo.gameObject.GetComponent<RectTransform>();
            infoRect.anchorMin = Vector2.zero;
            infoRect.anchorMax = Vector2.one;
            infoRect.offsetMin = new Vector2(pad, 0);
            infoRect.offsetMax = new Vector2(-pad, 0);

            panel.SetActive(false);
        }

        private void RefreshStaffing()
        {
            // Clear old cards
            foreach (var card in _staffCards)
            {
                if (card != null) UnityEngine.Object.Destroy(card);
            }
            _staffCards.Clear();

            var counters = CheckoutCounter.AllCounters;
            var buildings = GetBuildingsForSelection();
            int activeStaff = 0;

            // Filter counters to selected building(s)
            var filtered = new List<(CheckoutCounterInstance counter, int index)>();
            for (int i = 0; i < counters.Count; i++)
            {
                var c = counters[i];
                if (_selectedBuildingId != AllPropertiesId)
                {
                    if (c.BuildingId != _selectedBuildingId) continue;
                }
                filtered.Add((c, i));
            }

            // Update title
            if (_staffingTitle != null)
            {
                string bName = GetBuildingDisplayName(_selectedBuildingId);
                _staffingTitle.text = $"<b>Staffing</b>  <color=#9E9E9E><size=90%>{bName}</size></color>";
            }

            // Calculate grid
            int cols = STAFF_COLUMNS;
            int rows = (filtered.Count + cols - 1) / cols;
            float cardW = STAFF_CARD_WIDTH;
            float cardH = STAFF_CARD_HEIGHT;
            float totalHeight = 10 + rows * (cardH + CARD_GAP);

            if (_staffCardGrid != null)
            {
                var contentRect = _staffCardGrid.GetComponent<RectTransform>();
                contentRect.sizeDelta = new Vector2(0, totalHeight);
            }

            float cashBalance = 0f;
            try { cashBalance = S1API.Money.Money.GetCashBalance(); } catch { }

            for (int f = 0; f < filtered.Count; f++)
            {
                var (counter, globalIdx) = filtered[f];
                int col = f % cols;
                int row = f / cols;
                float xPos = 10 + col * (cardW + CARD_GAP);
                float yPos = -(8 + row * (cardH + CARD_GAP));

                bool isStaffed = counter.IsStaffed;
                if (isStaffed) activeStaff++;

                string buildingName = GetBuildingDisplayName(counter.BuildingId);
                int counterNum = GetCounterNumberInBuilding(counter);

                var card = CreateStaffCard(counter, globalIdx, buildingName, counterNum,
                    isStaffed, cashBalance, xPos, yPos, cardW, cardH);
                _staffCards.Add(card);
            }

            // Update payroll
            if (_payrollLabel != null)
            {
                float dailyCost = activeStaff * 200f;
                _payrollLabel.text = dailyCost > 0
                    ? $"Payroll: <color=#4CAF50>${dailyCost:F0}/day</color>  ({activeStaff} staff)"
                    : "No staff hired";
            }

            // Shelf toggle removed — no visual update needed
        }

        private GameObject CreateStaffCard(
            CheckoutCounterInstance counter, int globalIdx,
            string buildingName, int counterNum,
            bool isStaffed, float cashBalance,
            float xPos, float yPos, float cardW, float cardH)
        {
            var card = UIFactory.Panel($"StaffCard_{globalIdx}", _staffCardGrid, CardBg);
            var cardRect = card.GetComponent<RectTransform>();
            cardRect.anchorMin = new Vector2(0, 1);
            cardRect.anchorMax = new Vector2(0, 1);
            cardRect.pivot = new Vector2(0, 1);
            cardRect.sizeDelta = new Vector2(cardW, cardH);
            cardRect.anchoredPosition = new Vector2(xPos, yPos);

            // Building name (small, muted, top)
            var bldgLabel = TMPFactory.Text($"Bldg_{globalIdx}", buildingName,
                card.transform, 15, TextAlignmentOptions.TopLeft);
            bldgLabel.color = TextDim;
            bldgLabel.enableAutoSizing = true;
            bldgLabel.fontSizeMin = 10;
            bldgLabel.fontSizeMax = 15;
            var bldgRect = bldgLabel.gameObject.GetComponent<RectTransform>();
            bldgRect.anchorMin = new Vector2(0, 0.78f);
            bldgRect.anchorMax = new Vector2(1, 1);
            bldgRect.offsetMin = new Vector2(8, 0);
            bldgRect.offsetMax = new Vector2(-8, -4);

            // Counter name
            var counterLabel = TMPFactory.Text($"Counter_{globalIdx}", $"Counter #{counterNum}",
                card.transform, 15, TextAlignmentOptions.TopLeft, FontStyles.Bold);
            counterLabel.color = Color.white;
            var cntRect = counterLabel.gameObject.GetComponent<RectTransform>();
            cntRect.anchorMin = new Vector2(0, 0.58f);
            cntRect.anchorMax = new Vector2(1, 0.78f);
            cntRect.offsetMin = new Vector2(8, 0);
            cntRect.offsetMax = new Vector2(-8, 0);

            if (isStaffed)
            {
                // NPC name
                string npcName = "Employee";
                if (BudtenderInstance.Active.TryGetValue(counter.AssignedBudtenderId, out var bt)
                    && bt.GameNpc != null)
                {
                    npcName = $"{bt.GameNpc.FirstName} {bt.GameNpc.LastName}";
                }

                var nameLabel = TMPFactory.Text($"Name_{globalIdx}", npcName,
                    card.transform, 15, TextAlignmentOptions.Left);
                nameLabel.color = AccentGreen;
                var nameRect = nameLabel.gameObject.GetComponent<RectTransform>();
                nameRect.anchorMin = new Vector2(0, 0.38f);
                nameRect.anchorMax = new Vector2(1, 0.58f);
                nameRect.offsetMin = new Vector2(8, 0);
                nameRect.offsetMax = new Vector2(-8, 0);

                // FIRE button
                var (fireGo, fireBtn, fireText) = TMPFactory.RoundedButtonWithLabel(
                    $"Fire_{globalIdx}", "FIRE", card.transform,
                    FireRed, 80, 28, 15, Color.white);
                var fireRect = fireGo.GetComponent<RectTransform>();
                fireRect.anchorMin = new Vector2(0.5f, 0);
                fireRect.anchorMax = new Vector2(0.5f, 0);
                fireRect.pivot = new Vector2(0.5f, 0);
                fireRect.anchoredPosition = new Vector2(0, 8);

                string capturedBtId = counter.AssignedBudtenderId;
                fireBtn.onClick.AddListener(new Action(() =>
                {
                    if (NetworkHelper.IsHost)
                        BudtenderController.Fire(capturedBtId);
                    else
                        ConfigSyncData.SendQuestAction($"BUDTENDER_FIRE:{capturedBtId}");
                    RefreshStaffing();
                }));
            }
            else
            {
                // Vacant label
                var vacantLabel = TMPFactory.Text($"Vacant_{globalIdx}", "Vacant",
                    card.transform, 15, TextAlignmentOptions.Left);
                vacantLabel.color = TextDim;
                var vacRect = vacantLabel.gameObject.GetComponent<RectTransform>();
                vacRect.anchorMin = new Vector2(0, 0.38f);
                vacRect.anchorMax = new Vector2(1, 0.58f);
                vacRect.offsetMin = new Vector2(8, 0);
                vacRect.offsetMax = new Vector2(-8, 0);

                // Check if can hire
                bool canAfford = cashBalance >= 200f;
                bool atCap = !BudtenderController.CanHire(counter.BuildingId);
                bool canHire = canAfford && !atCap;

                string btnLabel = atCap ? "MAX STAFF" : "HIRE $200";
                Color btnColor = canHire ? HireGreen : DisabledBtn;
                Color textColor = canHire ? Color.white : TextDim;

                var (hireGo, hireBtn, hireText) = TMPFactory.RoundedButtonWithLabel(
                    $"Hire_{globalIdx}", btnLabel, card.transform,
                    btnColor, 110, 28, 15, textColor);
                var hireRect = hireGo.GetComponent<RectTransform>();
                hireRect.anchorMin = new Vector2(0.5f, 0);
                hireRect.anchorMax = new Vector2(0.5f, 0);
                hireRect.pivot = new Vector2(0.5f, 0);
                hireRect.anchoredPosition = new Vector2(0, 8);

                if (canHire)
                {
                    int capturedIdx = globalIdx;
                    hireBtn.onClick.AddListener(new Action(() =>
                    {
                        var c = CheckoutCounter.GetCounterByIndex(capturedIdx);
                        if (c == null) return;
                        if (NetworkHelper.IsHost)
                            BudtenderController.Hire(c);
                        else
                            ConfigSyncData.SendQuestAction($"BUDTENDER_HIRE:{capturedIdx}");
                        RefreshStaffing();
                    }));
                }
                else
                {
                    hireBtn.interactable = false;
                }
            }

            return card;
        }

        // Shelf toggle removed — budtenders access all storage with closets-last priority

        /// <summary>Returns the 1-based counter number within its building.</summary>
        private static int GetCounterNumberInBuilding(CheckoutCounterInstance counter)
        {
            int num = 1;
            foreach (var c in CheckoutCounter.AllCounters)
            {
                if (c == counter) return num;
                if (c.BuildingId == counter.BuildingId) num++;
            }
            return num;
        }
    }
}
