using S1API.Money;
using S1API.UI;
using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
#endif

namespace OverTheCounter.Apps
{
    public partial class CustomersApp
    {
        // Colors
        private static readonly Color MsgBg = new Color(0.08f, 0.08f, 0.1f);
        private static readonly Color BubbleBg = new Color(0.15f, 0.18f, 0.22f);
        private static readonly Color CardBg = new Color(0.12f, 0.15f, 0.2f);
        private static readonly Color CardBorder = new Color(0.25f, 0.55f, 0.45f);
        private static readonly Color BuyBtnColor = new Color(0.15f, 0.5f, 0.35f);
        private static readonly Color SoldColor = new Color(0.35f, 0.35f, 0.35f);
        private static readonly Color SidebarBg = new Color(0.06f, 0.07f, 0.09f);
        private static readonly Color SidebarSelectedBg = new Color(0.12f, 0.14f, 0.18f);
        private static readonly Color AvatarBg = new Color(0.15f, 0.35f, 0.25f);
        private static readonly Color SeparatorColor = new Color(0.2f, 0.22f, 0.26f);

        // Thread view refs (for rebuild after purchase)
        private RectTransform _threadViewRect;
        private ScrollRect _threadScroll;

        // Cached sprites
        private Sprite _propertyPhoto;
        private Image _sidebarMugshotImage;
        private bool _notificationSent;

        // ==================================================================
        // Badge
        // ==================================================================

        internal void RefreshMessagesBadge()
        {
            if (_messageBadge == null) return;

            bool hasUnread = !_messagesRead
                && GetEffectiveTier() >= 1
                && !(StaticSaveData.Instance?.ShackPurchased ?? false);

            _messageBadge.SetActive(hasUnread);

            // Update sidebar mugshot each tick (S1API replaces default icon async)
            if (_sidebarMugshotImage != null)
            {
                var mug = GetStaticMugshot();
                if (mug != null)
                {
                    _sidebarMugshotImage.sprite = mug;
                    _sidebarMugshotImage.color = Color.white;
                }
            }

            // Fire toast notification once per session when message first becomes available
            if (hasUnread && !_notificationSent)
            {
                _notificationSent = true;
                try
                {
                    Singleton<NotificationsManager>.Instance?.SendNotification(
                        "Static", "New encrypted message",
                        GetStaticMugshot(), 5f, true);
                }
                catch { }
            }
        }

        // ==================================================================
        // Asset Loading
        // ==================================================================

        /// <summary>Always reads live from the game NPC — no caching, since S1API replaces the default icon async.</summary>
        private Sprite GetStaticMugshot()
        {
            try
            {
                var go = NPCs.StaticNPC.Instance?.gameObject;
                if (go == null) return null;
                var gameNpc = go.GetComponent<
#if IL2CPP
                    Il2CppScheduleOne.NPCs.NPC
#else
                    ScheduleOne.NPCs.NPC
#endif
                >();
                return gameNpc?.MugshotSprite;
            }
            catch { return null; }
        }

        private void LoadPropertyPhoto()
        {
            if (_propertyPhoto != null) return;

            try
            {
                string resourceName = "OverTheCounter.Resources.ShackPhoto.png";
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null) return;

                byte[] data = new byte[stream.Length];
                stream.Read(data, 0, data.Length);

                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, data)) return;

                _propertyPhoto = Sprite.Create(tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f));
            }
            catch { }
        }

        // ==================================================================
        // Open / Close
        // ==================================================================

        private void OpenMessagesOverlay()
        {
            if (_messagesOverlay != null) return;

            _messagesRead = true;
            RefreshMessagesBadge();

            // Load property photo
            LoadPropertyPhoto();

            // Hide tab pages
            _managersPage?.SetActive(false);
            _customersPage?.SetActive(false);

            _messagesOverlay = UIFactory.Panel("MessagesOverlay", _rootPanel.transform, MsgBg);
            var rect = _messagesOverlay.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            // Header row
            var header = UIFactory.Panel("MsgHeader", _messagesOverlay.transform, new Color(0.1f, 0.12f, 0.16f));
            var headerRect = header.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = Vector2.one;
            headerRect.pivot = new Vector2(0.5f, 1);
            headerRect.anchoredPosition = Vector2.zero;
            headerRect.sizeDelta = new Vector2(0, 32);

            // Back button
            var backBtn = UIFactory.Panel("BackBtn", header.transform, Color.clear);
            var backRect = backBtn.GetComponent<RectTransform>();
            backRect.anchorMin = new Vector2(0, 0);
            backRect.anchorMax = new Vector2(0, 1);
            backRect.pivot = new Vector2(0, 0.5f);
            backRect.sizeDelta = new Vector2(50, 0);
            backRect.anchoredPosition = new Vector2(5, 0);
            backBtn.AddComponent<Button>().onClick.AddListener(new Action(() =>
            {
                CloseMessagesOverlay();
                SwitchTab(_activeTab);
            }));
            var backLabel = UIFactory.Text("BackLabel", "<b>\u25C0 Back</b>", backBtn.transform, 12, TextAnchor.MiddleLeft);
            var backLabelRect = backLabel.gameObject.GetComponent<RectTransform>();
            backLabelRect.anchorMin = Vector2.zero;
            backLabelRect.anchorMax = Vector2.one;
            backLabelRect.offsetMin = new Vector2(5, 0);
            backLabelRect.offsetMax = Vector2.zero;
            backLabel.color = new Color(0.5f, 0.8f, 0.7f);

            var titleText = UIFactory.Text("Title", "<b>Messages</b>", header.transform, 16, TextAnchor.MiddleCenter);
            var titleRect = titleText.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.2f, 0);
            titleRect.anchorMax = new Vector2(0.8f, 1);
            titleRect.offsetMin = Vector2.zero;
            titleRect.offsetMax = Vector2.zero;
            titleText.color = Color.white;

            // Content area below header
            var contentArea = UIFactory.Panel("ContentArea", _messagesOverlay.transform, Color.clear);
            var contentRect = contentArea.GetComponent<RectTransform>();
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = new Vector2(0, -32);

            bool hasMessages = GetEffectiveTier() >= 1;

            if (!hasMessages)
            {
                var noMsg = UIFactory.Text("NoMsg", "<color=#666666>No messages</color>",
                    contentArea.transform, 14, TextAnchor.MiddleCenter);
                var noMsgRect = noMsg.gameObject.GetComponent<RectTransform>();
                noMsgRect.anchorMin = Vector2.zero;
                noMsgRect.anchorMax = Vector2.one;
                noMsgRect.offsetMin = Vector2.zero;
                noMsgRect.offsetMax = Vector2.zero;
                return;
            }

            // Split layout: sidebar (left) + thread (right)
            BuildSidebar(contentArea.transform);
            BuildSeparator(contentArea.transform);
            BuildThreadPanel(contentArea.transform);
        }

        internal void CloseMessagesOverlay()
        {
            if (_messagesOverlay == null) return;
            UnityEngine.Object.Destroy(_messagesOverlay);
            _messagesOverlay = null;
            _threadViewRect = null;
            _threadScroll = null;
            _sidebarMugshotImage = null;
        }

        // ==================================================================
        // Sidebar (left panel — contact list)
        // ==================================================================

        private void BuildSidebar(Transform parent)
        {
            var sidebar = UIFactory.Panel("Sidebar", parent, SidebarBg);
            var sidebarRect = sidebar.GetComponent<RectTransform>();
            sidebarRect.anchorMin = Vector2.zero;
            sidebarRect.anchorMax = new Vector2(0.18f, 1);
            sidebarRect.offsetMin = Vector2.zero;
            sidebarRect.offsetMax = Vector2.zero;

            bool shackPurchased = StaticSaveData.Instance?.ShackPurchased ?? false;

            // Contact row — fills most of the sidebar
            var contactRow = UIFactory.Panel("StaticRow", sidebar.transform, SidebarSelectedBg);
            var rowRect = contactRow.GetComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0, 1);
            rowRect.anchorMax = new Vector2(1, 1);
            rowRect.pivot = new Vector2(0.5f, 1);
            rowRect.anchoredPosition = new Vector2(0, -4);
            rowRect.sizeDelta = new Vector2(-6, 90);

            // Avatar (mugshot or initial) — large, centered
            var avatar = UIFactory.Panel("Avatar", contactRow.transform, AvatarBg);
            var avatarRect = avatar.GetComponent<RectTransform>();
            avatarRect.anchorMin = new Vector2(0.5f, 1);
            avatarRect.anchorMax = new Vector2(0.5f, 1);
            avatarRect.pivot = new Vector2(0.5f, 1);
            avatarRect.sizeDelta = new Vector2(60, 60);
            avatarRect.anchoredPosition = new Vector2(0, -4);

            _sidebarMugshotImage = avatar.GetComponent<Image>();
            var currentMug = GetStaticMugshot();
            if (currentMug != null)
            {
                _sidebarMugshotImage.sprite = currentMug;
                _sidebarMugshotImage.color = Color.white;
            }
            else
            {
                var initial = UIFactory.Text("Initial", "<b>S</b>", avatar.transform, 28, TextAnchor.MiddleCenter);
                var initialRect = initial.gameObject.GetComponent<RectTransform>();
                initialRect.anchorMin = Vector2.zero;
                initialRect.anchorMax = Vector2.one;
                initialRect.offsetMin = Vector2.zero;
                initialRect.offsetMax = Vector2.zero;
                initial.color = new Color(0.6f, 0.9f, 0.7f);
            }

            // Name below avatar
            var nameText = UIFactory.Text("Name", "<b>ST4T1C</b>", contactRow.transform, 14, TextAnchor.MiddleCenter);
            var nameRect = nameText.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 0);
            nameRect.anchorMax = new Vector2(1, 1);
            nameRect.offsetMin = new Vector2(2, 2);
            nameRect.offsetMax = new Vector2(-2, -66);
            nameText.color = new Color(0.5f, 0.85f, 0.7f);

            // Unread dot
            if (!shackPurchased)
            {
                var dot = UIFactory.Panel("UnreadDot", contactRow.transform, new Color(0.4f, 0.8f, 0.6f));
                var dotRect = dot.GetComponent<RectTransform>();
                dotRect.anchorMin = new Vector2(1, 1);
                dotRect.anchorMax = new Vector2(1, 1);
                dotRect.pivot = new Vector2(1, 1);
                dotRect.sizeDelta = new Vector2(8, 8);
                dotRect.anchoredPosition = new Vector2(-2, -2);
            }
        }

        // ==================================================================
        // Separator
        // ==================================================================

        private void BuildSeparator(Transform parent)
        {
            var sep = UIFactory.Panel("Separator", parent, SeparatorColor);
            var sepRect = sep.GetComponent<RectTransform>();
            sepRect.anchorMin = new Vector2(0.18f, 0);
            sepRect.anchorMax = new Vector2(0.18f, 1);
            sepRect.pivot = new Vector2(0.5f, 0.5f);
            sepRect.sizeDelta = new Vector2(1, 0);
            sepRect.anchoredPosition = Vector2.zero;
        }

        // ==================================================================
        // Thread Panel (right side)
        // ==================================================================

        private void BuildThreadPanel(Transform parent)
        {
            var threadPanel = UIFactory.Panel("ThreadPanel", parent, MsgBg);
            var panelRect = threadPanel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.18f, 0);
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = new Vector2(1, 0); // 1px past separator
            panelRect.offsetMax = Vector2.zero;

            // Scroll area — fills the entire thread panel (no redundant header)
            var scrollArea = UIFactory.Panel("ScrollArea", threadPanel.transform, Color.clear);
            var scrollAreaRect = scrollArea.GetComponent<RectTransform>();
            scrollAreaRect.anchorMin = Vector2.zero;
            scrollAreaRect.anchorMax = Vector2.one;
            scrollAreaRect.offsetMin = Vector2.zero;
            scrollAreaRect.offsetMax = Vector2.zero;

            _threadViewRect = UIFactory.ScrollableVerticalList("ThreadScroll", scrollArea.transform, out _threadScroll);

            // Constrain scroll (same as Managers/Customers pages)
            var scrollRt = _threadScroll.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = Vector2.zero;
            scrollRt.offsetMax = Vector2.zero;

            _threadScroll.horizontal = false;
            _threadScroll.scrollSensitivity = 20f;

            var contentFitter = _threadViewRect.GetComponent<ContentSizeFitter>();
            if (contentFitter != null)
                contentFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            _threadViewRect.anchorMin = Vector2.zero;
            _threadViewRect.anchorMax = new Vector2(1, 1);
            _threadViewRect.pivot = new Vector2(0, 1);
            _threadViewRect.sizeDelta = new Vector2(0, _threadViewRect.sizeDelta.y);

            PopulateThread();
        }

        // ==================================================================
        // Thread Content
        // ==================================================================

        private void PopulateThread()
        {
            if (_threadViewRect == null || _threadScroll == null) return;

            var content = _threadScroll.content;
            ClearChildren(content);

            var vlg = content.gameObject.GetComponent<VerticalLayoutGroup>();
            if (vlg == null)
            {
                vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
                vlg.spacing = 8;
                vlg.padding = new RectOffset(10, 10, 10, 10);
                vlg.childForceExpandWidth = true;
                vlg.childForceExpandHeight = false;
                vlg.childControlWidth = true;
                vlg.childControlHeight = true;
            }

            var csf = content.gameObject.GetComponent<ContentSizeFitter>();
            if (csf == null)
            {
                csf = content.gameObject.AddComponent<ContentSizeFitter>();
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            bool purchased = StaticSaveData.Instance?.ShackPurchased ?? false;

            // Message 1: Greeting
            AddBubble(content, "Got something for you. Property listing from one of my contacts. " +
                "Small operation, good location. Details below.");

            // Message 2: Property listing card (with photo)
            AddPropertyCard(content, purchased);

            // Message 3: Post-purchase confirmation
            if (purchased)
            {
                AddBubble(content, "Done. Door's unlocked, keys are yours. " +
                    "Set up shop and customers will find you.");
            }
        }

        private void AddBubble(Transform parent, string text)
        {
            var bubble = UIFactory.Panel("Bubble", parent, BubbleBg);
            var le = bubble.AddComponent<LayoutElement>();
            le.minHeight = 65;
            le.preferredHeight = 65;

            var msgText = UIFactory.Text("Text", text, bubble.transform, 14, TextAnchor.UpperLeft);
            var textRect = msgText.gameObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(12, 8);
            textRect.offsetMax = new Vector2(-12, -8);
            msgText.color = new Color(0.7f, 0.85f, 0.75f);
        }

        private void AddPropertyCard(Transform parent, bool purchased)
        {
            // Card height depends on whether we have a property photo
            float photoPad = 10f;
            float photoHeight = _propertyPhoto != null ? 220f : 0f;
            float cardHeight = 170f + photoHeight + (_propertyPhoto != null ? photoPad : 0f);

            // Wrapper to center the card at 75% width
            var wrapper = UIFactory.Panel("CardWrapper", parent, new Color(0, 0, 0, 0));
            var wrapperLe = wrapper.AddComponent<LayoutElement>();
            wrapperLe.minHeight = cardHeight;
            wrapperLe.preferredHeight = cardHeight;

            var card = UIFactory.Panel("PropertyCard", wrapper.transform, CardBg);
            var cardRect = card.GetComponent<RectTransform>();
            cardRect.anchorMin = new Vector2(0.125f, 0);
            cardRect.anchorMax = new Vector2(0.875f, 1);
            cardRect.offsetMin = Vector2.zero;
            cardRect.offsetMax = Vector2.zero;

            // Left green accent bar
            var accent = UIFactory.Panel("Accent", card.transform, CardBorder);
            var accentRect = accent.GetComponent<RectTransform>();
            accentRect.anchorMin = Vector2.zero;
            accentRect.anchorMax = new Vector2(0, 1);
            accentRect.pivot = new Vector2(0, 0.5f);
            accentRect.sizeDelta = new Vector2(3, 0);
            accentRect.anchoredPosition = Vector2.zero;

            float yOffset = 0f;

            // Property photo (if available)
            if (_propertyPhoto != null)
            {
                var photoPanel = UIFactory.Panel("Photo", card.transform, Color.black);
                var photoRect = photoPanel.GetComponent<RectTransform>();
                photoRect.anchorMin = new Vector2(0, 1);
                photoRect.anchorMax = new Vector2(1, 1);
                photoRect.pivot = new Vector2(0.5f, 1);
                photoRect.sizeDelta = new Vector2(0, photoHeight);
                photoRect.anchoredPosition = new Vector2(0, -photoPad);
                photoRect.offsetMin = new Vector2(3, photoRect.offsetMin.y); // past accent bar

                var photoImage = photoPanel.GetComponent<Image>();
                photoImage.sprite = _propertyPhoto;
                photoImage.color = Color.white;
                photoImage.preserveAspect = true;

                yOffset = photoHeight + photoPad;
            }

            // Property name — set sizeDelta/anchoredPosition first, then offsets (sizeDelta resets offsets)
            var nameText = UIFactory.Text("PropName", "<b>Westville Shack</b>", card.transform, 20, TextAnchor.UpperLeft);
            var nameRect = nameText.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 1);
            nameRect.anchorMax = new Vector2(1, 1);
            nameRect.pivot = new Vector2(0, 1);
            nameRect.sizeDelta = new Vector2(0, 26);
            nameRect.anchoredPosition = new Vector2(0, -(yOffset + 10));
            nameRect.offsetMin = new Vector2(24, nameRect.offsetMin.y);
            nameRect.offsetMax = new Vector2(-8, nameRect.offsetMax.y);
            nameText.color = Color.white;

            // Location
            var locText = UIFactory.Text("Location", "Westville", card.transform, 15, TextAnchor.UpperLeft);
            var locRect = locText.gameObject.GetComponent<RectTransform>();
            locRect.anchorMin = new Vector2(0, 1);
            locRect.anchorMax = new Vector2(1, 1);
            locRect.pivot = new Vector2(0, 1);
            locRect.sizeDelta = new Vector2(0, 20);
            locRect.anchoredPosition = new Vector2(0, -(yOffset + 36));
            locRect.offsetMin = new Vector2(24, locRect.offsetMin.y);
            locRect.offsetMax = new Vector2(-8, locRect.offsetMax.y);
            locText.color = new Color(0.6f, 0.6f, 0.6f);

            // Description
            var descText = UIFactory.Text("Desc",
                "Small dispensary, good location. Move your legal product through " +
                "the counter. Door locked until purchased.",
                card.transform, 14, TextAnchor.UpperLeft);
            var descRect = descText.gameObject.GetComponent<RectTransform>();
            descRect.anchorMin = new Vector2(0, 1);
            descRect.anchorMax = new Vector2(1, 1);
            descRect.pivot = new Vector2(0, 1);
            descRect.sizeDelta = new Vector2(0, 40);
            descRect.anchoredPosition = new Vector2(0, -(yOffset + 58));
            descRect.offsetMin = new Vector2(24, descRect.offsetMin.y);
            descRect.offsetMax = new Vector2(-12, descRect.offsetMax.y);
            descText.color = new Color(0.55f, 0.7f, 0.6f);

            // Price / status row at bottom
            float price = Config.ShackPurchasePrice.Value;

            if (purchased)
            {
                var soldText = UIFactory.Text("Sold", "<b>SOLD</b>", card.transform, 20, TextAnchor.MiddleCenter);
                var soldRect = soldText.gameObject.GetComponent<RectTransform>();
                soldRect.anchorMin = new Vector2(0, 0);
                soldRect.anchorMax = new Vector2(1, 0);
                soldRect.pivot = new Vector2(0.5f, 0);
                soldRect.sizeDelta = new Vector2(0, 28);
                soldRect.anchoredPosition = new Vector2(0, 8);
                soldText.color = SoldColor;
            }
            else
            {
                var priceText = UIFactory.Text("Price", $"<b>${price:N0}</b>", card.transform, 18, TextAnchor.MiddleLeft);
                var priceRect = priceText.gameObject.GetComponent<RectTransform>();
                priceRect.anchorMin = new Vector2(0, 0);
                priceRect.anchorMax = new Vector2(0.5f, 0);
                priceRect.pivot = new Vector2(0, 0);
                priceRect.sizeDelta = new Vector2(0, 30);
                priceRect.anchoredPosition = new Vector2(24, 8);
                priceText.color = new Color(0.9f, 0.9f, 0.6f);

                var buyBtn = UIFactory.Panel("BuyBtn", card.transform, BuyBtnColor);
                var buyRect = buyBtn.GetComponent<RectTransform>();
                buyRect.anchorMin = new Vector2(1, 0);
                buyRect.anchorMax = new Vector2(1, 0);
                buyRect.pivot = new Vector2(1, 0);
                buyRect.sizeDelta = new Vector2(100, 30);
                buyRect.anchoredPosition = new Vector2(-12, 8);
                buyBtn.AddComponent<Button>().onClick.AddListener(new Action(OnPurchaseShack));

                var buyLabel = UIFactory.Text("BuyLabel", "<b>Purchase</b>", buyBtn.transform, 16, TextAnchor.MiddleCenter);
                var buyLabelRect = buyLabel.gameObject.GetComponent<RectTransform>();
                buyLabelRect.anchorMin = Vector2.zero;
                buyLabelRect.anchorMax = Vector2.one;
                buyLabelRect.offsetMin = Vector2.zero;
                buyLabelRect.offsetMax = Vector2.zero;
                buyLabel.color = Color.white;
            }
        }

        // ==================================================================
        // Purchase
        // ==================================================================

        private void OnPurchaseShack()
        {
            if (StaticSaveData.Instance == null) return;
            if (StaticSaveData.Instance.ShackPurchased) return;

            float price = Config.ShackPurchasePrice.Value;
            if (Money.GetOnlineBalance() < price)
                return;

            Money.CreateOnlineTransaction("OTC Property", -price, 1f, "Static Services");

            if (NetworkHelper.IsHost)
            {
                StaticSaveData.Instance.PurchaseShack();
            }
            else
            {
                StaticSaveData.Instance.PurchaseShack();
                ConfigSyncData.SendQuestAction("STATIC_SHACK_PURCHASED");
            }

            // Rebuild thread to show confirmation
            PopulateThread();
        }
    }
}
