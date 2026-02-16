using S1API.UI;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using OverTheCounter.Logic;

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
        }

        private void RefreshManagersPage()
        {
            if (_managersContentParent == null) return;
            ClearChildren(_managersContentParent);
            PopulateManagerList(_managersContentParent);
        }

        private void PopulateManagerList(Transform contentParent)
        {
            if (ManagerInstance.Active.Count == 0)
            {
                var emptyText = UIFactory.Text("EmptyMsg", "No managers hired yet.", contentParent, 18, TextAnchor.MiddleCenter);
                emptyText.color = new Color(0.5f, 0.5f, 0.5f);
                var emptyLayout = emptyText.gameObject.AddComponent<LayoutElement>();
                emptyLayout.preferredHeight = 60;
                emptyLayout.flexibleWidth = 1;
                return;
            }

            foreach (var mgr in ManagerInstance.Active.Values)
            {
                if (mgr.State == ManagerState.Fired) continue;
                CreateManagerCard(contentParent, mgr);
            }
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
            Il2CppScheduleOne.NPCs.NPCInventory npcInventory = null;
            try { npcInventory = mgr.GameNpc?.GetComponent<Il2CppScheduleOne.NPCs.NPCInventory>(); }
            catch { }

            int displaySlots = 5;
            if (npcInventory?.ItemSlots != null)
                displaySlots = Math.Max(5, npcInventory.ItemSlots.Count);

            float cardHeight = CardHeight(displaySlots);

            var cardObj = UIFactory.Panel($"Card_{mgr.Id}", parent, new Color(0.18f, 0.18f, 0.18f));
            var cardLayout = cardObj.AddComponent<LayoutElement>();
            cardLayout.preferredHeight = cardHeight;
            cardLayout.flexibleWidth = 1;

            // ── Mugshot with stroke frame ──
            var mugFrame = UIFactory.Panel("MugFrame", cardObj.transform, new Color(0.45f, 0.50f, 0.52f));
            var mugFrameRect = mugFrame.GetComponent<RectTransform>();
            mugFrameRect.anchorMin = new Vector2(0, 0.90f);
            mugFrameRect.anchorMax = new Vector2(0, 0.90f);
            mugFrameRect.pivot = new Vector2(0, 1f);
            mugFrameRect.anchoredPosition = new Vector2(8, 0);
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

            var nameText = UIFactory.Text("Name", $"<b>{firstName} {lastName}</b>", cardObj.transform, 14, TextAnchor.MiddleLeft);
            nameText.color = Color.white;
            var nameRect = nameText.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 0.68f);
            nameRect.anchorMax = new Vector2(0.25f, 0.90f);
            nameRect.offsetMin = new Vector2(92, 0);
            nameRect.offsetMax = Vector2.zero;

            // ── Left: Business + Balance ──
            string bizName = mgr.AssignedBusiness?.PropertyName ?? mgr.BusinessPropertyCode ?? "Unassigned";
            string bizLine = bizName;
            if (mgr.HasLocker)
            {
                float cash = mgr.GetLockerCash();
                bizLine += $" - <color=#66BF4D>${cash:N0}</color>";
            }
            var bizText = UIFactory.Text("Business", bizLine, cardObj.transform, 12, TextAnchor.MiddleLeft);
            bizText.color = new Color(0.55f, 0.55f, 0.55f);
            bizText.supportRichText = true;
            var bizRect = bizText.gameObject.GetComponent<RectTransform>();
            bizRect.anchorMin = new Vector2(0, 0.46f);
            bizRect.anchorMax = new Vector2(0.25f, 0.68f);
            bizRect.offsetMin = new Vector2(92, 0);
            bizRect.offsetMax = Vector2.zero;

            // ── Left: Status ──
            var (statusStr, statusColor) = GetStatusDisplay(mgr.State);
            var statusText = UIFactory.Text("Status", statusStr, cardObj.transform, 12, TextAnchor.MiddleLeft);
            statusText.color = statusColor;
            var statusRect = statusText.gameObject.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0, 0.24f);
            statusRect.anchorMax = new Vector2(0.25f, 0.46f);
            statusRect.offsetMin = new Vector2(92, 0);
            statusRect.offsetMax = Vector2.zero;

            // ── Center: Inventory Slots Grid (actual NPC inventory) ──
            var invPanel = UIFactory.Panel("Inventory", cardObj.transform, Color.clear);
            var invRect = invPanel.GetComponent<RectTransform>();
            invRect.anchorMin = new Vector2(0.40f, 0.04f);
            invRect.anchorMax = new Vector2(0.72f, 0.82f);
            invRect.offsetMin = Vector2.zero;
            invRect.offsetMax = Vector2.zero;

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
                int qty = 0;
                try
                {
                    if (npcInventory?.ItemSlots != null && i < npcInventory.ItemSlots.Count)
                    {
                        var slot = npcInventory.ItemSlots[i];
                        if (slot?.ItemInstance?.Definition != null)
                        {
                            icon = slot.ItemInstance.Definition.Icon;
                            qty = slot.Quantity;
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
                    if (qty > 0)
                    {
                        var qtyText = UIFactory.Text($"Qty_{i}", qty.ToString(), slotPanel.transform, 13, TextAnchor.LowerRight);
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
                else { }
            }

            // ── Right: Detail page chevron button ──
            var mgrRef = mgr;

            var (chevMask, chevBtn, chevLabel) = UIFactory.RoundedButtonWithLabel(
                "DetailBtn", "\u203A", cardObj.transform, // › right angle quote (fallback if icon missing)
                new Color(0.15f, 0.35f, 0.45f), 65, 65, 8, Color.white);
            var chevRect = chevMask.GetComponent<RectTransform>();
            chevRect.anchorMin = new Vector2(0.96f, 0.82f);
            chevRect.anchorMax = new Vector2(0.96f, 0.82f);
            chevRect.pivot = new Vector2(0.5f, 1f);
            chevRect.anchoredPosition = Vector2.zero;
            chevLabel.gameObject.SetActive(false);
            _chevronSprite ??= LoadIconResource("ChevronIcon");
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
