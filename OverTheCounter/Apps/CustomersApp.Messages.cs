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
        private static readonly Color BubbleBg = new Color(0.17f, 0.20f, 0.25f);
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
        private readonly System.Collections.Generic.Dictionary<string, Sprite> _embedImageCache = new();
        private Image _sidebarMugshotImage;
        private int _lastNotifiedCount;
        private int _lastSeenCount;

        // Button action handlers keyed by EmbedButtonAction string
        private System.Collections.Generic.Dictionary<string, Action> _embedActions;
        private int _unreadDividerIndex = -1;

        // Parallax photo tracking
        private class ParallaxEntry { public RectTransform Card; public RectTransform Photo; public float MaskHeight; }
        private readonly System.Collections.Generic.List<ParallaxEntry> _parallaxPhotos = new();
        private static readonly Vector3[] VpCorners = new Vector3[4];
        private static readonly Vector3[] CardCorners = new Vector3[4];

        // ==================================================================
        // Badge
        // ==================================================================

        internal void RefreshMessagesBadge()
        {
            if (_messageBadge == null) return;

            int messageCount = StaticThreadSaveData.Instance?.MessageCount ?? 0;

            // If overlay is open, auto-read new messages and refresh thread
            if (_messagesOverlay != null && messageCount > _lastSeenCount)
            {
                _lastSeenCount = messageCount;
                _lastNotifiedCount = messageCount; // suppress toast while viewing
                StaticThreadSaveData.Instance?.MarkAllSeen();
                PopulateThread();
            }

            bool hasUnread = messageCount > _lastSeenCount;
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

            // Fire toast for each batch of new messages
            if (messageCount > _lastNotifiedCount)
            {
                _lastNotifiedCount = messageCount;
                try
                {
                    Singleton<NotificationsManager>.Instance?.SendNotification(
                        "OTC App", "New message from Static",
                        GetAppIcon(), 5f, true);
                }
                catch { }
            }
        }

        // ==================================================================
        // Asset Loading
        // ==================================================================

        private static Sprite _appIconCache;

        /// <summary>Loads and caches the OTC app icon for toast notifications.</summary>
        internal static Sprite GetAppIcon()
        {
            if (_appIconCache != null) return _appIconCache;
            try
            {
                var path = System.IO.Path.Combine(
                    MelonLoader.Utils.MelonEnvironment.UserDataDirectory,
                    "S1API", "Icons", "CustomersIcon.png");
                if (!System.IO.File.Exists(path)) return null;
                var data = System.IO.File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, data)) return null;
                _appIconCache = Sprite.Create(tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f));
            }
            catch { }
            return _appIconCache;
        }

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

        private Sprite LoadEmbedImage(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return null;
            if (_embedImageCache.TryGetValue(resourceName, out var cached)) return cached;

            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null) return null;

                byte[] data = new byte[stream.Length];
                stream.Read(data, 0, data.Length);

                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, data)) return null;

                var sprite = Sprite.Create(tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f));
                _embedImageCache[resourceName] = sprite;
                return sprite;
            }
            catch { return null; }
        }

        // ==================================================================
        // Open / Close
        // ==================================================================

        private void OpenMessagesOverlay()
        {
            if (_messagesOverlay != null) return;

            var thread = StaticThreadSaveData.Instance;
            int totalMessages = thread?.MessageCount ?? 0;
            int unseenCount = thread?.UnseenCount ?? 0;
            int previousSeenCount = totalMessages - unseenCount;
            _lastSeenCount = totalMessages;
            _lastNotifiedCount = totalMessages;
            _unreadDividerIndex = (previousSeenCount > 0 && unseenCount > 0)
                ? previousSeenCount : -1;
            thread?.MarkAllSeen();
            RefreshMessagesBadge();

            // Initialize button action handlers
            _embedActions ??= new System.Collections.Generic.Dictionary<string, Action>
            {
                ["purchase_shack"] = OnPurchaseShack
            };

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

            bool hasMessages = (StaticThreadSaveData.Instance?.MessageCount ?? 0) > 0;

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
            _parallaxPhotos.Clear();
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
            bool hasUnreadDot = (StaticThreadSaveData.Instance?.MessageCount ?? 0) > _lastSeenCount;
            if (hasUnreadDot)
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

            // Hook parallax scroll callback
            _threadScroll.onValueChanged.AddListener(new Action<Vector2>(_ => UpdateParallax()));
            Canvas.ForceUpdateCanvases();
            UpdateParallax();
        }

        // ==================================================================
        // Thread Content
        // ==================================================================

        private void PopulateThread()
        {
            if (_threadViewRect == null || _threadScroll == null) return;
            _parallaxPhotos.Clear();

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

            var messages = StaticThreadSaveData.Instance?.GetMessages()
                ?? new System.Collections.Generic.List<OtcPropertyMessage>();

            for (int i = 0; i < messages.Count; i++)
            {
                if (i == _unreadDividerIndex)
                    AddUnreadDivider(content);

                var msg = messages[i];
                if (msg.IsEmbed)
                    AddEmbed(content, msg);
                else
                    AddBubble(content, msg.Text ?? "");
            }
        }

        private void AddBubble(Transform parent, string text)
        {
            var wrapper = UIFactory.Panel("BubbleWrapper", parent, new Color(0, 0, 0, 0));
            var wrapperLe = wrapper.AddComponent<LayoutElement>();
            wrapperLe.minHeight = 80;
            wrapperLe.preferredHeight = 80;

            var bubble = UIFactory.Panel("Bubble", wrapper.transform, BubbleBg);
            var bubbleRect = bubble.GetComponent<RectTransform>();
            bubbleRect.anchorMin = new Vector2(0.15f, 0);
            bubbleRect.anchorMax = new Vector2(0.85f, 1);
            bubbleRect.offsetMin = Vector2.zero;
            bubbleRect.offsetMax = Vector2.zero;

            var msgText = UIFactory.Text("Text", text, bubble.transform, 18, TextAnchor.UpperLeft);
            var textRect = msgText.gameObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(14, 10);
            textRect.offsetMax = new Vector2(-14, -10);
            msgText.color = new Color(0.78f, 0.92f, 0.82f);
        }

        private void AddUnreadDivider(Transform parent)
        {
            var wrapper = UIFactory.Panel("UnreadDivider", parent, new Color(0, 0, 0, 0));
            var le = wrapper.AddComponent<LayoutElement>();
            le.minHeight = 28;
            le.preferredHeight = 28;

            // Left line
            var leftLine = UIFactory.Panel("LeftLine", wrapper.transform, new Color(0.4f, 0.25f, 0.25f));
            var leftRect = leftLine.GetComponent<RectTransform>();
            leftRect.anchorMin = new Vector2(0.05f, 0.5f);
            leftRect.anchorMax = new Vector2(0.42f, 0.5f);
            leftRect.sizeDelta = new Vector2(0, 1);

            // Label
            var label = UIFactory.Text("UnreadLabel", "Unread", wrapper.transform, 14, TextAnchor.MiddleCenter);
            var labelRect = label.gameObject.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0.42f, 0);
            labelRect.anchorMax = new Vector2(0.58f, 1);
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            label.color = new Color(0.7f, 0.35f, 0.35f);

            // Right line
            var rightLine = UIFactory.Panel("RightLine", wrapper.transform, new Color(0.4f, 0.25f, 0.25f));
            var rightRect = rightLine.GetComponent<RectTransform>();
            rightRect.anchorMin = new Vector2(0.58f, 0.5f);
            rightRect.anchorMax = new Vector2(0.95f, 0.5f);
            rightRect.sizeDelta = new Vector2(0, 1);
        }

        private void AddEmbed(Transform parent, OtcPropertyMessage msg)
        {
            bool hasStatus = !string.IsNullOrEmpty(msg.EmbedStatus);
            var photo = LoadEmbedImage(msg.EmbedImageResource);
            bool hasPhoto = photo != null;
            bool hasItems = msg.EmbedItems != null && msg.EmbedItems.Count > 0;
            bool hasButton = !hasStatus && !string.IsNullOrEmpty(msg.EmbedButtonLabel);
            bool hasLocation = !string.IsNullOrEmpty(msg.EmbedLocation);

            // Calculate card height
            float photoHeight = hasPhoto ? 220f : 0f;
            float titleHeight = 29f;
            float descHeight = hasPhoto ? 52f : 24f;
            float itemsHeight = !hasStatus && hasItems ? msg.EmbedItems.Count * 29f : 0f;
            float locationHeight = !hasStatus && hasLocation ? 29f : 0f;
            float statusHeight = hasStatus ? 39f : 0f;
            float buttonHeight = hasButton ? 42f : 0f;
            float cardHeight = 16f + photoHeight
                + titleHeight + (hasStatus ? statusHeight : descHeight + itemsHeight + locationHeight)
                + buttonHeight;

            var wrapper = UIFactory.Panel("EmbedWrapper", parent, new Color(0, 0, 0, 0));
            var wrapperLe = wrapper.AddComponent<LayoutElement>();
            wrapperLe.minHeight = cardHeight;
            wrapperLe.preferredHeight = cardHeight;

            // 60% card width for all embeds
            float cardInsetMin = 0.20f;
            float cardInsetMax = 0.80f;

            var card = UIFactory.Panel("EmbedCard", wrapper.transform, CardBg);
            var cardRect = card.GetComponent<RectTransform>();
            cardRect.anchorMin = new Vector2(cardInsetMin, 0);
            cardRect.anchorMax = new Vector2(cardInsetMax, 1);
            cardRect.offsetMin = Vector2.zero;
            cardRect.offsetMax = Vector2.zero;

            float y = 0f;
            float textInset = hasPhoto ? 16f : 16f;

            // Left accent bar — full card height
            var accent = UIFactory.Panel("Accent", card.transform, CardBorder);
            var accentRect = accent.GetComponent<RectTransform>();
            accentRect.anchorMin = Vector2.zero;
            accentRect.anchorMax = new Vector2(0, 1);
            accentRect.pivot = new Vector2(0, 0.5f);
            accentRect.sizeDelta = new Vector2(3, 0);
            accentRect.anchoredPosition = Vector2.zero;

            // Photo — full-width header, cropped to fill via RectMask2D
            if (hasPhoto)
            {
                var photoMask = UIFactory.Panel("PhotoMask", card.transform, Color.clear);
                var maskRect = photoMask.GetComponent<RectTransform>();
                maskRect.anchorMin = new Vector2(0, 1);
                maskRect.anchorMax = new Vector2(1, 1);
                maskRect.pivot = new Vector2(0.5f, 1);
                maskRect.sizeDelta = new Vector2(0, photoHeight);
                maskRect.anchoredPosition = Vector2.zero;
                photoMask.AddComponent<RectMask2D>();

                // Inner image — preserveAspect fills width, mask clips vertical overflow
                var photoObj = UIFactory.Panel("Photo", photoMask.transform, Color.black);
                var photoObjRect = photoObj.GetComponent<RectTransform>();
                photoObjRect.anchorMin = new Vector2(0, 0.5f);
                photoObjRect.anchorMax = new Vector2(1, 0.5f);
                photoObjRect.pivot = new Vector2(0.5f, 0.5f);
                photoObjRect.sizeDelta = new Vector2(0, photoHeight * 2f);
                photoObjRect.anchoredPosition = Vector2.zero;

                var photoImage = photoObj.GetComponent<Image>();
                photoImage.sprite = photo;
                photoImage.color = Color.white;
                photoImage.preserveAspect = true;

                y = photoHeight;
                _parallaxPhotos.Add(new ParallaxEntry { Card = cardRect, Photo = photoObjRect, MaskHeight = photoHeight });
            }

            // Title
            y += 8f;
            int titleSize = hasPhoto ? 26 : 21;
            var titleText = UIFactory.Text("Title", $"<b>{msg.EmbedTitle ?? ""}</b>", card.transform, titleSize, TextAnchor.UpperLeft);
            var titleRect = titleText.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(1, 1);
            titleRect.pivot = new Vector2(0, 1);
            titleRect.sizeDelta = new Vector2(0, titleHeight);
            titleRect.anchoredPosition = new Vector2(0, -y);
            titleRect.offsetMin = new Vector2(textInset, titleRect.offsetMin.y);
            titleRect.offsetMax = new Vector2(-8, titleRect.offsetMax.y);
            titleText.color = Color.white;
            y += titleHeight;

            if (hasStatus)
            {
                // Status replaces all content below title
                var statusText = UIFactory.Text("Status", $"<b>{msg.EmbedStatus}</b>", card.transform, hasPhoto ? 26 : 21, TextAnchor.MiddleCenter);
                var statusRect = statusText.gameObject.GetComponent<RectTransform>();
                statusRect.anchorMin = new Vector2(0, 1);
                statusRect.anchorMax = new Vector2(1, 1);
                statusRect.pivot = new Vector2(0.5f, 1);
                statusRect.sizeDelta = new Vector2(0, 34);
                statusRect.anchoredPosition = new Vector2(0, -y);
                statusText.color = SoldColor;
            }
            else
            {
                // Location (below title for photo cards, at bottom for non-photo)
                if (hasPhoto && hasLocation)
                {
                    var locText = UIFactory.Text("Location", msg.EmbedLocation, card.transform, 20, TextAnchor.UpperLeft);
                    var locRect = locText.gameObject.GetComponent<RectTransform>();
                    locRect.anchorMin = new Vector2(0, 1);
                    locRect.anchorMax = new Vector2(1, 1);
                    locRect.pivot = new Vector2(0, 1);
                    locRect.sizeDelta = new Vector2(0, 26);
                    locRect.anchoredPosition = new Vector2(0, -y);
                    locRect.offsetMin = new Vector2(textInset, locRect.offsetMin.y);
                    locRect.offsetMax = new Vector2(-8, locRect.offsetMax.y);
                    locText.color = new Color(0.6f, 0.6f, 0.6f);
                    y += 29f;
                }

                // Description
                if (!string.IsNullOrEmpty(msg.EmbedDescription))
                {
                    int descSize = hasPhoto ? 18 : 17;
                    var descText = UIFactory.Text("Desc", msg.EmbedDescription, card.transform, descSize, TextAnchor.UpperLeft);
                    var descRect = descText.gameObject.GetComponent<RectTransform>();
                    descRect.anchorMin = new Vector2(0, 1);
                    descRect.anchorMax = new Vector2(1, 1);
                    descRect.pivot = new Vector2(0, 1);
                    descRect.sizeDelta = new Vector2(0, descHeight);
                    descRect.anchoredPosition = new Vector2(0, -y);
                    descRect.offsetMin = new Vector2(textInset, descRect.offsetMin.y);
                    descRect.offsetMax = new Vector2(-12, descRect.offsetMax.y);
                    descText.color = hasPhoto ? new Color(0.55f, 0.7f, 0.6f) : new Color(0.5f, 0.6f, 0.55f);
                    y += descHeight + 4f;
                }

                // Items list
                if (hasItems)
                {
                    foreach (var item in msg.EmbedItems)
                    {
                        var itemText = UIFactory.Text("Item", $"  \u2022  {item}", card.transform, 18, TextAnchor.UpperLeft);
                        var itemRect = itemText.gameObject.GetComponent<RectTransform>();
                        itemRect.anchorMin = new Vector2(0, 1);
                        itemRect.anchorMax = new Vector2(1, 1);
                        itemRect.pivot = new Vector2(0, 1);
                        itemRect.sizeDelta = new Vector2(0, 24);
                        itemRect.anchoredPosition = new Vector2(0, -y);
                        itemRect.offsetMin = new Vector2(textInset, itemRect.offsetMin.y);
                        itemRect.offsetMax = new Vector2(-8, itemRect.offsetMax.y);
                        itemText.color = new Color(0.9f, 0.9f, 0.6f);
                        y += 29f;
                    }
                }

                // Location (at bottom for non-photo embeds)
                if (!hasPhoto && hasLocation)
                {
                    var locText = UIFactory.Text("Location", $"Location: {msg.EmbedLocation}", card.transform, 16, TextAnchor.UpperLeft);
                    var locRect = locText.gameObject.GetComponent<RectTransform>();
                    locRect.anchorMin = new Vector2(0, 1);
                    locRect.anchorMax = new Vector2(1, 1);
                    locRect.pivot = new Vector2(0, 1);
                    locRect.sizeDelta = new Vector2(0, 24);
                    locRect.anchoredPosition = new Vector2(0, -y);
                    locRect.offsetMin = new Vector2(textInset, locRect.offsetMin.y);
                    locRect.offsetMax = new Vector2(-8, locRect.offsetMax.y);
                    locText.color = new Color(0.5f, 0.65f, 0.6f);
                }

                // Button
                if (hasButton)
                {
                    var btnPanel = UIFactory.Panel("EmbedBtn", card.transform, BuyBtnColor);
                    var btnRect = btnPanel.GetComponent<RectTransform>();
                    btnRect.anchorMin = new Vector2(1, 0);
                    btnRect.anchorMax = new Vector2(1, 0);
                    btnRect.pivot = new Vector2(1, 0);
                    btnRect.sizeDelta = new Vector2(112, 38);
                    btnRect.anchoredPosition = new Vector2(-12, 8);

                    string actionKey = msg.EmbedButtonAction;
                    btnPanel.AddComponent<Button>().onClick.AddListener(new Action(() =>
                    {
                        if (_embedActions != null && _embedActions.TryGetValue(actionKey, out var handler))
                            handler();
                    }));

                    var btnLabel = UIFactory.Text("BtnLabel", $"<b>{msg.EmbedButtonLabel}</b>", btnPanel.transform, 21, TextAnchor.MiddleCenter);
                    var btnLabelRect = btnLabel.gameObject.GetComponent<RectTransform>();
                    btnLabelRect.anchorMin = Vector2.zero;
                    btnLabelRect.anchorMax = Vector2.one;
                    btnLabelRect.offsetMin = Vector2.zero;
                    btnLabelRect.offsetMax = Vector2.zero;
                    btnLabel.color = Color.white;
                }
            }
        }

        // ==================================================================
        // Parallax
        // ==================================================================

        private void UpdateParallax()
        {
            if (_threadScroll == null || _parallaxPhotos.Count == 0) return;
            var viewport = _threadScroll.viewport ?? _threadScroll.GetComponent<RectTransform>();
            float viewportHeight = viewport.rect.height;
            if (viewportHeight <= 0) return;

            viewport.GetWorldCorners(VpCorners);
            float vpBottom = VpCorners[0].y;
            float vpTop = VpCorners[1].y;

            foreach (var entry in _parallaxPhotos)
            {
                if (entry.Card == null || entry.Photo == null) continue;
                entry.Card.GetWorldCorners(CardCorners);
                float cardCenter = (CardCorners[0].y + CardCorners[1].y) * 0.5f;

                // t: 0 = bottom of viewport, 1 = top
                float t = Mathf.InverseLerp(vpBottom, vpTop, cardCenter);

                // Shift image within mask — 30% of mask height max travel
                float maxOffset = entry.MaskHeight * 0.3f;
                float offset = (t - 0.5f) * 2f * maxOffset;
                entry.Photo.anchoredPosition = new Vector2(0, offset);
            }
        }

        // ==================================================================
        // Purchase
        // ==================================================================

        private void OnPurchaseShack()
        {
            if (PropertySaveData.Instance == null) return;
            if (PropertySaveData.Instance.IsPropertyOwned(PropertySaveData.ShackId)) return;

            float price = Config.ShackPurchasePrice.Value;
            if (Money.GetOnlineBalance() < price)
                return;

            Money.CreateOnlineTransaction("OTC Property", -price, 1f, "Static Services");
            PropertySaveData.Instance.PurchaseProperty(PropertySaveData.ShackId);

            if (!NetworkHelper.IsHost)
                ConfigSyncData.SendQuestAction("PURCHASE_WESTVILLE_SHACK");

            // Rebuild thread to show confirmation
            PopulateThread();
        }
    }
}
