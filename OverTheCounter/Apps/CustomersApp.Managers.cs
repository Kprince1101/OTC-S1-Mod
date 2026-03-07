using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using S1API.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.Property;
using Il2CppTMPro;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Money;
using ScheduleOne.Property;
using TMPro;
#endif

namespace OverTheCounter.Apps
{
    public partial class CustomersApp
    {
        private void BuildManagersPage(Transform parent)
        {
            var contentRect = UIFactory.ScrollableVerticalList("ManagerScroll", parent, out ScrollRect scrollRect);

            var scrollRt = scrollRect.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = Vector2.zero;
            scrollRt.offsetMax = Vector2.zero;

            scrollRect.horizontal = false;
            scrollRect.scrollSensitivity = 20f;

            // Force content width to match viewport (prevent horizontal overflow)
            var contentFitter = contentRect.GetComponent<ContentSizeFitter>();
            if (contentFitter != null)
                contentFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0, 1);
            contentRect.sizeDelta = new Vector2(0, contentRect.sizeDelta.y);

            var contentLayout = contentRect.GetComponent<VerticalLayoutGroup>();
            if (contentLayout != null)
            {
                contentLayout.childControlHeight = true;
                contentLayout.childControlWidth = true;
                contentLayout.childForceExpandHeight = false;
                contentLayout.childForceExpandWidth = true;
                contentLayout.childAlignment = TextAnchor.UpperCenter;
                contentLayout.spacing = 6;
                contentLayout.padding = new RectOffset(10, 10, 8, 12);
            }

            _managersContentParent = contentRect.transform;
            PopulateManagerList(_managersContentParent);
            _lastManagerListFingerprint = BuildManagerListFingerprint();
        }

        // Fingerprint of the last-rendered list — skip rebuild when nothing changed
        private string _lastManagerListFingerprint;

        private void RefreshManagersPage()
        {
            if (_managersContentParent == null) return;

            string fingerprint = BuildManagerListFingerprint();
            if (fingerprint == _lastManagerListFingerprint) return;

            ClearChildren(_managersContentParent);
            PopulateManagerList(_managersContentParent);
            _lastManagerListFingerprint = fingerprint;
        }

        /// <summary>
        /// Builds a string fingerprint of the current manager list state.
        /// Changes when managers are hired/fired, businesses are bought,
        /// manager status changes, or config toggles.
        /// </summary>
        private string BuildManagerListFingerprint()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(Config.AlternateHire.Value ? "AH1|" : "AH0|");

                foreach (var m in ManagerInstance.Active.Values)
                {
                    if (m.State == ManagerState.Fired) continue;
                    var (status, _) = GetStatusDisplay(m);
                    sb.Append(m.Id).Append(':').Append(m.BusinessPropertyCode)
                      .Append(':').Append(status).Append('|');
                }

                if (Config.AlternateHire.Value)
                {
                    var managedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in ManagerInstance.Active.Values)
                        if (m.State != ManagerState.Fired && !string.IsNullOrEmpty(m.BusinessPropertyCode))
                            managedCodes.Add(m.BusinessPropertyCode);

                    foreach (var biz in Business.OwnedBusinesses)
                    {
                        if (biz == null) continue;
                        if (managedCodes.Contains(biz.PropertyCode)) continue;
                        sb.Append("hire:").Append(biz.PropertyCode).Append('|');
                    }
                }

                return sb.ToString();
            }
            catch { return null; } // null forces rebuild
        }

        private void PopulateManagerList(Transform contentParent)
        {
            bool alternateHire = Config.AlternateHire.Value;

            // Build sorted entries: active managers + unmanaged businesses (when alternate hire is on)
            var managedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var managers = ManagerInstance.Active.Values
                .Where(m => m.State != ManagerState.Fired)
                .ToList();

            foreach (var m in managers)
            {
                if (!string.IsNullOrEmpty(m.BusinessPropertyCode))
                    managedCodes.Add(m.BusinessPropertyCode);
            }

            var unmanagedBusinesses = new List<Business>();
            if (alternateHire)
            {
                try
                {
                    foreach (var biz in Business.OwnedBusinesses)
                    {
                        if (biz == null) continue;
                        if (managedCodes.Contains(biz.PropertyCode)) continue;
                        unmanagedBusinesses.Add(biz);
                    }
                }
                catch { }
            }

            if (managers.Count == 0 && unmanagedBusinesses.Count == 0)
            {
                string msg = alternateHire ? "No properties owned yet." : "No managers hired yet.";
                var emptyText = TMPFactory.Text("EmptyMsg", msg, contentParent, 18, TextAlignmentOptions.Center);
                emptyText.color = new Color(0.5f, 0.5f, 0.5f);
                var emptyLayout = emptyText.gameObject.AddComponent<LayoutElement>();
                emptyLayout.preferredHeight = 60;
                emptyLayout.flexibleWidth = 1;
                return;
            }

            // Interleave by property name for consistent ordering
            var entries = new List<(string sortKey, ManagerInstance mgr, Business biz)>();
            foreach (var m in managers)
                entries.Add((m.AssignedBusiness?.PropertyName ?? m.BusinessPropertyCode ?? m.Id, m, null));
            foreach (var b in unmanagedBusinesses)
                entries.Add((b.PropertyName ?? b.PropertyCode, null, b));

            entries.Sort((a, b) => string.Compare(a.sortKey, b.sortKey, StringComparison.OrdinalIgnoreCase));

            foreach (var (_, mgr, biz) in entries)
            {
                if (mgr != null)
                    CreateManagerCard(contentParent, mgr);
                else
                    CreateHireCard(contentParent, biz);
            }
        }

        private void CreateHireCard(Transform parent, Business business)
        {
            float cardHeight = 72f;
            var cardObj = UIFactory.Panel($"Hire_{business.PropertyCode}", parent, new Color(0.15f, 0.17f, 0.15f));
            var cardLayout = cardObj.AddComponent<LayoutElement>();
            cardLayout.preferredHeight = cardHeight;
            cardLayout.flexibleWidth = 1;

            // Property name
            string bizName = business.PropertyName ?? business.PropertyCode;
            var nameText = TMPFactory.Text("Name", $"<b>{bizName}</b>", cardObj.transform, 15, TextAlignmentOptions.Left);
            nameText.color = Color.white;
            var nameRect = nameText.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 1);
            nameRect.anchorMax = new Vector2(0.5f, 1);
            nameRect.pivot = new Vector2(0, 1);
            nameRect.anchoredPosition = new Vector2(12, -10);
            nameRect.sizeDelta = new Vector2(0, 20);

            // "No manager" status
            var statusText = TMPFactory.Text("Status", "No manager", cardObj.transform, 15, TextAlignmentOptions.Left);
            statusText.color = new Color(0.5f, 0.5f, 0.5f);
            var statusRect = statusText.gameObject.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0, 1);
            statusRect.anchorMax = new Vector2(0.5f, 1);
            statusRect.pivot = new Vector2(0, 1);
            statusRect.anchoredPosition = new Vector2(12, -32);
            statusRect.sizeDelta = new Vector2(0, 18);

            // Cost label
            float signingFee = Config.ManagerSigningFee.Value;
            var costText = TMPFactory.Text("Cost", $"<color=#BBA033>${signingFee:N0}</color>", cardObj.transform, 15, TextAlignmentOptions.Left);
            costText.richText = true;
            costText.color = new Color(0.7f, 0.7f, 0.7f);
            var costRect = costText.gameObject.GetComponent<RectTransform>();
            costRect.anchorMin = new Vector2(0, 1);
            costRect.anchorMax = new Vector2(0.5f, 1);
            costRect.pivot = new Vector2(0, 1);
            costRect.anchoredPosition = new Vector2(12, -50);
            costRect.sizeDelta = new Vector2(0, 18);

            // Hire button
            string propCode = business.PropertyCode;
            var (btnMask, btn, btnLabel) = TMPFactory.RoundedButtonWithLabel(
                "HireBtn", "Hire", cardObj.transform,
                new Color(0.2f, 0.5f, 0.2f), 80, 36, 14, Color.white);
            var btnRect = btnMask.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(1, 0.5f);
            btnRect.anchorMax = new Vector2(1, 0.5f);
            btnRect.pivot = new Vector2(1, 0.5f);
            btnRect.anchoredPosition = new Vector2(-12, 0);

            var btnColors = btn.colors;
            btnColors.normalColor = new Color(0.2f, 0.5f, 0.2f);
            btnColors.highlightedColor = new Color(0.3f, 0.6f, 0.3f);
            btnColors.pressedColor = new Color(0.15f, 0.35f, 0.15f);
            btnColors.selectedColor = new Color(0.2f, 0.5f, 0.2f);
            btn.colors = btnColors;

            var capturedStatusText = statusText;
            btn.onClick.AddListener(new Action(() =>
            {
                try
                {
                    if (ManagerInstance.HasManager(propCode))
                    {
                        capturedStatusText.text = "Already has a manager";
                        capturedStatusText.color = new Color(0.8f, 0.4f, 0.4f);
                        return;
                    }

                    float fee = Config.ManagerSigningFee.Value;
                    var moneyMgr = NetworkSingleton<ScheduleOne.Money.MoneyManager>.Instance;
                    if (moneyMgr == null || moneyMgr.cashBalance < fee)
                    {
                        capturedStatusText.text = $"Need ${fee:N0} cash";
                        capturedStatusText.color = new Color(0.8f, 0.4f, 0.4f);
                        return;
                    }

                    if (NetworkHelper.IsHost)
                    {
                        ManagerController.Instance?.HireManager(business);
                    }
                    else
                    {
                        ConfigSyncData.SendQuestAction($"MANAGER_HIRE:{propCode}");
                    }

                    // Force rebuild on next refresh
                    _lastManagerListFingerprint = null;
                    MelonLoader.MelonCoroutines.Start(DelayedRefresh());
                }
                catch (Exception ex)
                {
                    Melon<Core>.Logger.Error($"[CustomersApp] Hire error: {ex.Message}");
                    capturedStatusText.text = "Error!";
                    capturedStatusText.color = new Color(0.8f, 0.4f, 0.4f);
                }
            }));
        }

        private System.Collections.IEnumerator DelayedRefresh()
        {
            yield return new UnityEngine.WaitForSeconds(0.5f);
            RefreshManagersPage();
        }

        // Chevron icon sprite (cached across cards)
        private static Sprite _chevronSprite;

        private static Sprite LoadIconResource(string name)
        {
            try
            {
                string resourceName = $"OverTheCounter.Resources.{name}.png";
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null) return null;

                byte[] data = new byte[stream.Length];
                stream.Read(data, 0, data.Length);

                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, data)) return null;

                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
            catch { return null; }
        }

        // Inventory layout constants
        private const int SLOTS_PER_ROW = 5;
        private const float SLOT_SIZE = 65f;
        private const float SLOT_GAP = 4f;
        private static readonly Color SlotBg = new Color(0.55f, 0.55f, 0.58f);

        private static float CardHeight(int slotCount)
        {
            int rows = Math.Max(1, (slotCount + SLOTS_PER_ROW - 1) / SLOTS_PER_ROW);
            if (rows <= 1) return 92f;
            return 92f + (rows - 1) * (SLOT_SIZE + SLOT_GAP);
        }

        private void CreateManagerCard(Transform parent, ManagerInstance mgr)
        {
            // ── Read actual NPC inventory (what you see in the trade dialog) ──
            ScheduleOne.NPCs.NPCInventory npcInventory = null;
            try { npcInventory = mgr.GameNpc?.GetComponent<ScheduleOne.NPCs.NPCInventory>(); }
            catch { }

            int displaySlots = 5;
            if (npcInventory?.ItemSlots != null)
                displaySlots = Math.Max(5, npcInventory.ItemSlots.Count);

            float cardHeight = CardHeight(displaySlots);

            var cardObj = UIFactory.Panel($"Card_{mgr.Id}", parent, new Color(0.18f, 0.18f, 0.18f));
            var cardLayout = cardObj.AddComponent<LayoutElement>();
            cardLayout.preferredHeight = cardHeight;
            cardLayout.flexibleWidth = 1;

            // ── Mugshot with stroke frame (top-anchored, fixed size) ──
            var mugFrame = UIFactory.Panel("MugFrame", cardObj.transform, new Color(0.45f, 0.50f, 0.52f));
            var mugFrameRect = mugFrame.GetComponent<RectTransform>();
            mugFrameRect.anchorMin = new Vector2(0, 1);
            mugFrameRect.anchorMax = new Vector2(0, 1);
            mugFrameRect.pivot = new Vector2(0, 1);
            mugFrameRect.anchoredPosition = new Vector2(8, -8);
            mugFrameRect.sizeDelta = new Vector2(69, 69);

            var mugPanel = UIFactory.Panel("Mugshot", mugFrame.transform, new Color(0.15f, 0.15f, 0.15f));
            var mugRect = mugPanel.GetComponent<RectTransform>();
            mugRect.anchorMin = Vector2.zero;
            mugRect.anchorMax = Vector2.one;
            mugRect.offsetMin = new Vector2(2, 2);
            mugRect.offsetMax = new Vector2(-2, -2);

            try
            {
                var mugSprite = mgr.GameNpc?.MugshotSprite;
                if (mugSprite != null)
                {
                    var mugImage = mugPanel.GetComponent<Image>();
                    mugImage.sprite = mugSprite;
                    mugImage.color = Color.white;
                }
            }
            catch { }

            // ── Left: Name ──
            string firstName = "Manager";
            string lastName = "";
            try
            {
                if (mgr.GameNpc != null)
                {
                    firstName = mgr.GameNpc.FirstName ?? "Manager";
                    lastName = mgr.GameNpc.LastName ?? "";
                }
            }
            catch { }

            var nameText = TMPFactory.Text("Name", $"<b>{firstName} {lastName}</b>", cardObj.transform, 15, TextAlignmentOptions.Left);
            nameText.color = Color.white;
            var nameRect = nameText.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 1);
            nameRect.anchorMax = new Vector2(0.25f, 1);
            nameRect.pivot = new Vector2(0, 1);
            nameRect.anchoredPosition = new Vector2(92, -10);
            nameRect.sizeDelta = new Vector2(0, 20);

            // ── Left: Business + Balance ──
            string bizName = mgr.AssignedBusiness?.PropertyName ?? mgr.BusinessPropertyCode ?? "Unassigned";
            string bizLine = bizName;
            if (mgr.HasLocker)
            {
                float cash = mgr.GetLockerCash();
                bizLine += $" - <color=#66BF4D>${cash:N0}</color>";
            }
            var bizText = TMPFactory.Text("Business", bizLine, cardObj.transform, 15, TextAlignmentOptions.Left);
            bizText.color = new Color(0.55f, 0.55f, 0.55f);
            bizText.richText = true;
            var bizRect = bizText.gameObject.GetComponent<RectTransform>();
            bizRect.anchorMin = new Vector2(0, 1);
            bizRect.anchorMax = new Vector2(0.25f, 1);
            bizRect.pivot = new Vector2(0, 1);
            bizRect.anchoredPosition = new Vector2(92, -32);
            bizRect.sizeDelta = new Vector2(0, 18);

            // ── Left: Status ──
            var (statusStr, statusColor) = GetStatusDisplay(mgr);
            var statusText = TMPFactory.Text("Status", statusStr, cardObj.transform, 15, TextAlignmentOptions.Left);
            statusText.color = statusColor;
            var statusRect = statusText.gameObject.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0, 1);
            statusRect.anchorMax = new Vector2(0.25f, 1);
            statusRect.pivot = new Vector2(0, 1);
            statusRect.anchoredPosition = new Vector2(92, -52);
            statusRect.sizeDelta = new Vector2(0, 18);

            // ── Center: Inventory Slots Grid (actual NPC inventory) ──
            var invPanel = UIFactory.Panel("Inventory", cardObj.transform, Color.clear);
            var invRect = invPanel.GetComponent<RectTransform>();
            invRect.anchorMin = new Vector2(0.40f, 1);
            invRect.anchorMax = new Vector2(0.72f, 1);
            invRect.pivot = new Vector2(0, 1);
            invRect.anchoredPosition = new Vector2(0, -6);
            int invRows = Math.Max(1, (displaySlots + SLOTS_PER_ROW - 1) / SLOTS_PER_ROW);
            float invGridHeight = invRows * SLOT_SIZE + Math.Max(0, invRows - 1) * SLOT_GAP;
            invRect.sizeDelta = new Vector2(0, invGridHeight);

            var grid = invPanel.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(SLOT_SIZE, SLOT_SIZE);
            grid.spacing = new Vector2(SLOT_GAP, SLOT_GAP);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = SLOTS_PER_ROW;
            grid.childAlignment = TextAnchor.UpperCenter;

            for (int i = 0; i < displaySlots; i++)
            {
                var slotPanel = UIFactory.Panel($"Slot_{i}", invPanel.transform, SlotBg);

                // Read directly from NPC inventory slot
                Sprite icon = null;
                string displayQty = null;
                try
                {
                    if (npcInventory?.ItemSlots != null && i < npcInventory.ItemSlots.Count)
                    {
                        var slot = npcInventory.ItemSlots[i];
                        if (slot?.ItemInstance?.Definition != null)
                        {
                            icon = slot.ItemInstance.Icon;
                            var cash = slot.ItemInstance.TryCast<ScheduleOne.ItemFramework.CashInstance>();
                            displayQty = cash != null ? $"${cash.Balance:N0}" : slot.Quantity.ToString();
                        }
                    }
                }
                catch { }

                if (icon != null)
                {
                    // Item icon
                    var iconObj = new GameObject("Icon");
                    iconObj.transform.SetParent(slotPanel.transform, false);
                    var iconImg = iconObj.AddComponent<Image>();
                    iconImg.sprite = icon;
                    iconImg.preserveAspect = true;
                    var iconRect = iconObj.GetComponent<RectTransform>();
                    iconRect.anchorMin = new Vector2(0.06f, 0.06f);
                    iconRect.anchorMax = new Vector2(0.94f, 0.94f);
                    iconRect.offsetMin = Vector2.zero;
                    iconRect.offsetMax = Vector2.zero;

                    // Quantity overlay (bottom-right)
                    if (displayQty != null)
                    {
                        var qtyText = TMPFactory.Text($"Qty_{i}", displayQty, slotPanel.transform, 15, TextAlignmentOptions.BottomRight);
                        qtyText.color = Color.white;
                        var qtyRect = qtyText.gameObject.GetComponent<RectTransform>();
                        qtyRect.anchorMin = Vector2.zero;
                        qtyRect.anchorMax = Vector2.one;
                        qtyRect.offsetMin = new Vector2(2, 1);
                        qtyRect.offsetMax = new Vector2(-3, 0);

                        var shadow = qtyText.gameObject.AddComponent<Shadow>();
                        shadow.effectColor = new Color(0, 0, 0, 0.85f);
                        shadow.effectDistance = new Vector2(1, -1);
                    }
                }
            }

            // ── Right: Detail page chevron button ──
            var mgrRef = mgr;

            var (chevMask, chevBtn, chevLabel) = TMPFactory.RoundedButtonWithLabel(
                "DetailBtn", "\u203A", cardObj.transform, // › right angle quote (fallback if icon missing)
                new Color(0.15f, 0.35f, 0.45f), 65, 65, 8, Color.white);
            var chevRect = chevMask.GetComponent<RectTransform>();
            chevRect.anchorMin = new Vector2(0.96f, 1);
            chevRect.anchorMax = new Vector2(0.96f, 1);
            chevRect.pivot = new Vector2(0.5f, 1);
            chevRect.anchoredPosition = new Vector2(0, -10);
            chevLabel.gameObject.SetActive(false);
            if (_chevronSprite == null) _chevronSprite = LoadIconResource("ChevronIcon");
            if (_chevronSprite != null)
            {
                var chevIcon = new GameObject("ChevronIcon");
                chevIcon.transform.SetParent(chevBtn.transform, false);
                var chevIconImg = chevIcon.AddComponent<Image>();
                chevIconImg.sprite = _chevronSprite;
                chevIconImg.preserveAspect = true;
                var chevIconRect = chevIcon.GetComponent<RectTransform>();
                chevIconRect.anchorMin = new Vector2(0.15f, 0.15f);
                chevIconRect.anchorMax = new Vector2(0.85f, 0.85f);
                chevIconRect.offsetMin = Vector2.zero;
                chevIconRect.offsetMax = Vector2.zero;
            }
            chevBtn.onClick.AddListener(new Action(() => ShowManagerDetail(mgrRef)));
        }
    }
}
