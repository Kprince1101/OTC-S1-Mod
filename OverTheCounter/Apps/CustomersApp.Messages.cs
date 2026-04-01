using MelonLoader;
using S1API.Money;
using S1API.UI;
using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppTMPro;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
using TMPro;
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
        private static readonly Color BuyBtnDisabledColor = new Color(0.45f, 0.2f, 0.2f);
        private static readonly Color SoldColor = new Color(0.35f, 0.35f, 0.35f);
        private static readonly Color SidebarBg = new Color(0.06f, 0.07f, 0.09f);
        private static readonly Color SidebarSelectedBg = new Color(0.12f, 0.14f, 0.18f);
        private static readonly Color AvatarBg = new Color(0.15f, 0.35f, 0.25f);
        private static readonly Color SeparatorColor = new Color(0.2f, 0.22f, 0.26f);
        private static readonly Color PlayerBubbleBg = new Color(0.14f, 0.16f, 0.26f);
        private static readonly Color PlayerTextColor = new Color(0.70f, 0.82f, 0.95f);

        // Thread layout edges (shared by embeds, bubbles, response options)
        private const float ThreadLeft = 0.04f;
        private const float ThreadRight = 0.96f;

        // Thread view refs (for rebuild after purchase)
        private RectTransform _threadViewRect;
        private ScrollRect _threadScroll;

        // Collapsible thread state
        private readonly System.Collections.Generic.Dictionary<string, bool> _threadCollapsed = new();
        private readonly System.Collections.Generic.HashSet<string> _autoCollapsedOnce = new();
        private readonly System.Collections.Generic.Dictionary<string, RectTransform> _threadContainers = new();
        private string _scrollToThread;

        // Thread header color
        private static readonly Color ThreadHeaderBg = new Color(0.10f, 0.12f, 0.16f);
        private static readonly Color ThreadHeaderActiveTint = new Color(0.55f, 0.85f, 0.65f);
        private static readonly Color ThreadHeaderCompletedTint = new Color(0.45f, 0.45f, 0.45f);

        // Cached sprites
        private readonly System.Collections.Generic.Dictionary<string, Sprite> _embedImageCache = new();
        private Image _sidebarMugshotImage;
        private int _lastNotifiedCount;
        private int _lastSeenCount;
        private int _lastPopulatedHash;

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

            // Rebuild messages from current state so count is accurate
            StaticThreadSaveData.Instance?.ReconcileHostThread();
            int messageCount = StaticThreadSaveData.Instance?.MessageCount ?? 0;

            // If overlay is open, refresh thread when content changes
            if (_messagesOverlay != null)
            {
                if (messageCount > _lastSeenCount)
                {
                    _lastSeenCount = messageCount;
                    _lastNotifiedCount = messageCount;
                }
                StaticThreadSaveData.Instance?.MarkAllSeen();

                int hash = ComputeThreadHash();
                if (hash != _lastPopulatedHash)
                    PopulateThread(snapToBottom: false);
            }

            bool hasUnread = messageCount > _lastSeenCount;
            _messageBadge.SetActive(hasUnread);

            // Start/stop pulse coroutine based on unread state
            if (hasUnread && _badgePulseCoroutine == null)
                _badgePulseCoroutine = MelonCoroutines.Start(BadgePulseRoutine());
            else if (!hasUnread && _badgePulseCoroutine != null)
            {
                MelonCoroutines.Stop(_badgePulseCoroutine);
                _badgePulseCoroutine = null;
                // Reset scale so badge doesn't reappear mid-pulse size
                var rt = _messageBadge?.GetComponent<RectTransform>();
                if (rt != null) rt.localScale = Vector3.one;
            }

            // Home screen icon badge (red circle with count, like Messages app)
            UpdateHomeScreenBadge(hasUnread ? 1 : 0);

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

        private System.Collections.IEnumerator BadgePulseRoutine()
        {
            var bright = new Color(0.9f, 0.15f, 0.15f, 1f);
            var dim = new Color(0.9f, 0.15f, 0.15f, 0.35f);
            var badgeTransform = _messageBadge?.GetComponent<RectTransform>();
            while (true)
            {
                if (_messageBadgeImage == null || badgeTransform == null) yield break;
                float t = Mathf.PingPong(Time.time * 2f, 1f);

                // Alpha pulse
                _messageBadgeImage.color = Color.Lerp(dim, bright, t);

                // Scale pulse (1.0x → 1.5x)
                float scale = Mathf.Lerp(1f, 1.5f, t);
                badgeTransform.localScale = new Vector3(scale, scale, 1f);

                yield return null;
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
                var path = System.IO.Path.Combine(Core.OtcIconDir, "CustomersIcon.png");
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
                ["purchase_shack"] = OnPurchaseShack,
                ["purchase_warehouse"] = OnPurchaseWarehouse,
                ["purchase_dispensary"] = OnPurchaseDispensary,
                ["accept_intro"] = OnAcceptIntro,
                ["accept_upgrade"] = OnAcceptUpgrade,
                ["purchase_tier1_money"] = OnPurchaseTier1Money,
                ["purchase_upgrade_money"] = OnPurchaseUpgradeMoney
            };

            // Hide tab pages and deselect all tabs
            _managersPage?.SetActive(false);
            _customersPage?.SetActive(false);
            if (_managersTabImage != null) _managersTabImage.color = Color.clear;
            if (_employeesTabImage != null) _employeesTabImage.color = Color.clear;
            if (_customersTabImage != null) _customersTabImage.color = Color.clear;
            if (_managersTabUnderline != null) _managersTabUnderline.color = Color.clear;
            if (_employeesTabUnderline != null) _employeesTabUnderline.color = Color.clear;
            if (_customersTabUnderline != null) _customersTabUnderline.color = Color.clear;
            if (_managersTabText != null) { _managersTabText.text = "Managers"; _managersTabText.color = new Color(0.55f, 0.55f, 0.55f); }
            if (_employeesTabText != null) { _employeesTabText.text = "Employees"; _employeesTabText.color = new Color(0.55f, 0.55f, 0.55f); }
            if (_customersTabText != null) { _customersTabText.text = "Customers"; _customersTabText.color = new Color(0.55f, 0.55f, 0.55f); }
            if (_msgTabUnderline != null) _msgTabUnderline.color = TabUnderlineColor;
            if (_msgBtnImage != null) _msgBtnImage.color = ActiveTabBg;

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
            _threadContainers.Clear();
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

            _threadViewRect = UIFactory.ScrollableVerticalList("ThreadScroll", threadPanel.transform, out _threadScroll);

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

        private int ComputeThreadHash()
        {
            var messages = StaticThreadSaveData.Instance?.GetMessages();
            if (messages == null) return 0;
            int hash = messages.Count;
            float balance = Money.GetOnlineBalance();
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                hash = hash * 31 + (m.Text?.GetHashCode() ?? 0);
                hash = hash * 31 + (m.EmbedStatus?.GetHashCode() ?? 0);
                hash = hash * 31 + (m.EmbedButtonLabel?.GetHashCode() ?? 0);
                if (m.EmbedItems != null)
                    foreach (var item in m.EmbedItems)
                        hash = hash * 31 + (item?.GetHashCode() ?? 0);
                // Track affordability so button color updates when balance changes
                if (!string.IsNullOrEmpty(m.EmbedButtonAction))
                {
                    float cost = GetActionCost(m.EmbedButtonAction);
                    hash = hash * 31 + (balance >= cost ? 1 : 0);
                }
            }
            return hash;
        }

        private void PopulateThread(bool snapToBottom = true)
        {
            if (_threadViewRect == null || _threadScroll == null) return;

            // Save scroll position before rebuild (ClearChildren destroys content)
            float savedScrollPos = _threadScroll.verticalNormalizedPosition;

            // Rebuild message list from current state flags before rendering
            StaticThreadSaveData.Instance?.ReconcileHostThread();

            _parallaxPhotos.Clear();
            _threadContainers.Clear();

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

            // Group messages into sequential thread runs
            var threadGroups = new System.Collections.Generic.List<(string threadId, System.Collections.Generic.List<OtcPropertyMessage> msgs)>();
            string currentThreadId = "\x01"; // sentinel — never matches any real ThreadId
            System.Collections.Generic.List<OtcPropertyMessage> currentGroup = null;

            foreach (var msg in messages)
            {
                string tid = msg.ThreadId;
                if (tid != currentThreadId)
                {
                    currentGroup = new System.Collections.Generic.List<OtcPropertyMessage>();
                    threadGroups.Add((tid, currentGroup));
                    currentThreadId = tid;
                }
                currentGroup.Add(msg);
            }

            int flatIndex = 0;
            foreach (var (threadId, group) in threadGroups)
            {
                if (threadId == null)
                {
                    // Inline messages — no thread container
                    foreach (var msg in group)
                    {
                        if (flatIndex == _unreadDividerIndex)
                            AddUnreadDivider(content);
                        flatIndex++;

                        if (msg.IsEmbed)
                            AddEmbed(content, msg);
                        else
                            AddBubble(content, msg.Text ?? "", msg.Sender);
                    }
                    continue;
                }

                // Determine thread metadata — track what the LAST embed looks like
                bool hasPendingAction = false;
                bool hasInProgressEmbed = false;
                string lastEmbedStatus = null;
                OtcPropertyMessage threadResponse = null;

                foreach (var msg in group)
                {
                    if (msg.Sender == "player_option") { threadResponse = msg; }
                    if (msg.IsEmbed)
                    {
                        // Each new embed resets — only the last embed's state matters
                        lastEmbedStatus = msg.EmbedStatus;
                        hasPendingAction = !string.IsNullOrEmpty(msg.EmbedButtonAction);
                        hasInProgressEmbed = msg.EmbedItems != null && msg.EmbedItems.Count > 0
                            && string.IsNullOrEmpty(msg.EmbedStatus) && string.IsNullOrEmpty(msg.EmbedButtonAction);
                    }
                }

                // Thread title from lookup table
                string title = threadId switch
                {
                    "crm" => "CRM Software",
                    "upgrade1" => "Private Server",
                    "upgrade2" => "Enterprise Tier",
                    "shack" => "Westville Shack",
                    "warehouse" => "Warehouse",
                    "dispensary" => "Big Dispensary",
                    _ => threadId
                };

                // Completed when the last embed has a final status and nothing pending after it
                string status = lastEmbedStatus;
                bool isCompleted = !string.IsNullOrEmpty(status) && !hasPendingAction && !hasInProgressEmbed;
                // Auto-collapse once when thread first completes, then respect user preference
                if (isCompleted && _autoCollapsedOnce.Add(threadId))
                    _threadCollapsed.Remove(threadId);
                bool collapsed = _threadCollapsed.TryGetValue(threadId, out bool userPref) ? userPref : isCompleted;

                // Create thread container
                var container = CreateThreadContainer(content, threadId, title, status, collapsed, isCompleted);

                if (!collapsed)
                {
                    var threadContent = container.Find("ThreadContent");
                    foreach (var msg in group)
                    {
                        if (flatIndex == _unreadDividerIndex)
                            AddUnreadDivider(threadContent);
                        flatIndex++;

                        if (msg.Sender == "player_option")
                            continue; // rendered as response button below

                        if (msg.IsEmbed)
                            AddEmbed(threadContent, msg);
                        else
                            AddBubble(threadContent, msg.Text ?? "", msg.Sender);
                    }

                    // Render response button inside thread (skip if completed)
                    if (threadResponse != null && !isCompleted)
                        AddResponseButton(threadContent, threadResponse);
                }
                else
                {
                    flatIndex += group.Count;
                }
            }

            // Update hash
            _lastPopulatedHash = ComputeThreadHash();

            // Scroll handling — force full layout rebuild so ContentSizeFitters propagate
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            Canvas.ForceUpdateCanvases();
            if (_scrollToThread != null && _threadContainers.TryGetValue(_scrollToThread, out var targetRect))
            {
                _scrollToThread = null;
                ScrollToThread(targetRect);
            }
            else if (snapToBottom)
            {
                if (_threadScroll != null)
                    _threadScroll.verticalNormalizedPosition = 0f;
            }
            else
            {
                // Restore scroll position after rebuild
                _threadScroll.verticalNormalizedPosition = savedScrollPos;
            }
        }

        private Transform CreateThreadContainer(Transform parent, string threadId, string title, string status, bool collapsed, bool isCompleted)
        {
            var containerGo = UIFactory.Panel($"Thread_{threadId}", parent, Color.clear);
            var containerRect = containerGo.GetComponent<RectTransform>();

            // Track for scroll-to-thread
            _threadContainers[threadId] = containerRect;

            // VLG to stack header + content
            var containerVlg = containerGo.AddComponent<VerticalLayoutGroup>();
            containerVlg.spacing = 0;
            containerVlg.padding = new RectOffset(0, 0, 0, 0);
            containerVlg.childForceExpandWidth = true;
            containerVlg.childForceExpandHeight = false;
            containerVlg.childControlWidth = true;
            containerVlg.childControlHeight = true;

            var containerCsf = containerGo.AddComponent<ContentSizeFitter>();
            containerCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // ── Thread Header ──
            var headerGo = UIFactory.Panel("ThreadHeader", containerGo.transform, ThreadHeaderBg);
            var headerLe = headerGo.AddComponent<LayoutElement>();
            headerLe.minHeight = 36;
            headerLe.preferredHeight = 36;

            // Chevron
            var chevronTmp = TMPFactory.Text("Chevron", collapsed ? "\u25BA" : "\u25BC", headerGo.transform, 16, TextAlignmentOptions.Left);
            var chevronRect = chevronTmp.gameObject.GetComponent<RectTransform>();
            chevronRect.anchorMin = new Vector2(0, 0);
            chevronRect.anchorMax = new Vector2(0, 1);
            chevronRect.pivot = new Vector2(0, 0.5f);
            chevronRect.sizeDelta = new Vector2(28, 0);
            chevronRect.anchoredPosition = new Vector2(10, 0);
            chevronTmp.color = isCompleted ? ThreadHeaderCompletedTint : ThreadHeaderActiveTint;

            // Title
            var titleTmp = TMPFactory.Text("Title", $"<b>{title}</b>", headerGo.transform, 17, TextAlignmentOptions.Left);
            var titleRect = titleTmp.gameObject.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 0);
            titleRect.anchorMax = new Vector2(0.7f, 1);
            titleRect.offsetMin = new Vector2(34, 0);
            titleRect.offsetMax = Vector2.zero;
            titleTmp.color = isCompleted ? ThreadHeaderCompletedTint : ThreadHeaderActiveTint;

            // Status badge (right-aligned, completed only)
            if (isCompleted && !string.IsNullOrEmpty(status))
            {
                var statusTmp = TMPFactory.Text("Status", status, headerGo.transform, 15, TextAlignmentOptions.Right);
                var statusRect = statusTmp.gameObject.GetComponent<RectTransform>();
                statusRect.anchorMin = new Vector2(0.7f, 0);
                statusRect.anchorMax = new Vector2(1, 1);
                statusRect.offsetMin = Vector2.zero;
                statusRect.offsetMax = new Vector2(-10, 0);
                statusTmp.color = ThreadHeaderCompletedTint;
            }

            // Click handler
            string capturedId = threadId;
            headerGo.AddComponent<Button>().onClick.AddListener(new Action(() =>
            {
                _threadCollapsed[capturedId] = !collapsed;
                PopulateThread(snapToBottom: false);
            }));

            // ── Thread Content ──
            var contentGo = UIFactory.Panel("ThreadContent", containerGo.transform, Color.clear);
            var contentVlg = contentGo.AddComponent<VerticalLayoutGroup>();
            contentVlg.spacing = 8;
            contentVlg.padding = new RectOffset(0, 0, 4, 8);
            contentVlg.childForceExpandWidth = true;
            contentVlg.childForceExpandHeight = false;
            contentVlg.childControlWidth = true;
            contentVlg.childControlHeight = true;

            var contentCsf = contentGo.AddComponent<ContentSizeFitter>();
            contentCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            contentGo.SetActive(!collapsed);

            return containerGo.transform;
        }

        private void AddResponseButton(Transform parent, OtcPropertyMessage option)
        {
            var wrapper = UIFactory.Panel("ResponseWrapper", parent, Color.clear);
            var wrapperLe = wrapper.AddComponent<LayoutElement>();
            wrapperLe.minHeight = 48;
            wrapperLe.preferredHeight = 48;

            // Bubble - right-aligned, auto-width via ContentSizeFitter
            var bubble = UIFactory.Panel("ResponseBubble", wrapper.transform, new Color(0.10f, 0.30f, 0.24f));
            var bubbleRect = bubble.GetComponent<RectTransform>();
            bubbleRect.anchorMin = new Vector2(1, 0.5f);
            bubbleRect.anchorMax = new Vector2(1, 0.5f);
            bubbleRect.pivot = new Vector2(1, 0.5f);
            bubbleRect.anchoredPosition = new Vector2(-14, 0);
            bubbleRect.sizeDelta = new Vector2(0, 36);

            var hlg = bubble.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(18, 18, 0, 0);
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childForceExpandWidth = false;
            hlg.childControlWidth = true;

            var bubbleCsf = bubble.AddComponent<ContentSizeFitter>();
            bubbleCsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            string actionKey = option.EmbedButtonAction;
            string targetThread = option.ThreadId;
            bubble.AddComponent<Button>().onClick.AddListener(new Action(() =>
            {
                _scrollToThread = targetThread;
                if (_embedActions != null && _embedActions.TryGetValue(actionKey, out var handler))
                    handler();
            }));

            var btnLabel = TMPFactory.Text("Label", $"<b>{option.EmbedButtonLabel}</b>", bubble.transform, 18, TextAlignmentOptions.Center);
            btnLabel.color = new Color(0.6f, 0.95f, 0.75f);
        }

        private void ScrollToThread(RectTransform threadRect)
        {
            if (_threadScroll == null || threadRect == null) return;

            var contentRect = _threadScroll.content;
            float contentHeight = contentRect.rect.height;
            float viewportHeight = _threadScroll.viewport != null
                ? _threadScroll.viewport.rect.height
                : _threadScroll.GetComponent<RectTransform>().rect.height;

            if (contentHeight <= viewportHeight)
            {
                _threadScroll.verticalNormalizedPosition = 1f;
                return;
            }

            // anchoredPosition.y is pivot-relative — compute actual top edge, with margin
            float threadTop = Mathf.Max(0f, -(threadRect.anchoredPosition.y + threadRect.rect.height * threadRect.pivot.y) - 10f);
            float threadHeight = threadRect.rect.height;
            float scrollRange = contentHeight - viewportHeight;

            float targetY;
            if (threadHeight <= viewportHeight)
                targetY = threadTop; // thread fits: show header at top
            else
                targetY = threadTop + threadHeight - viewportHeight; // too tall: show bottom at viewport bottom

            _threadScroll.verticalNormalizedPosition = 1f - Mathf.Clamp01(targetY / scrollRange);
        }

        private void AddBubble(Transform parent, string text, string sender)
        {
            bool isPlayer = sender == "player";

            var wrapper = UIFactory.Panel("BubbleWrapper", parent, new Color(0, 0, 0, 0));
            var wrapperLe = wrapper.AddComponent<LayoutElement>();
            wrapperLe.minHeight = 80;
            wrapperLe.preferredHeight = 80;

            var bubble = UIFactory.Panel("Bubble", wrapper.transform, isPlayer ? PlayerBubbleBg : BubbleBg);
            var bubbleRect = bubble.GetComponent<RectTransform>();
            bubbleRect.anchorMin = new Vector2(isPlayer ? 0.28f : ThreadLeft, 0);
            bubbleRect.anchorMax = new Vector2(isPlayer ? ThreadRight : 0.72f, 1);
            bubbleRect.offsetMin = Vector2.zero;
            bubbleRect.offsetMax = Vector2.zero;

            var msgTmp = TMPFactory.Text("Text", text, bubble.transform, 18,
                isPlayer ? TextAlignmentOptions.TopRight : TextAlignmentOptions.TopLeft);
            var textRect = msgTmp.gameObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(14, 10);
            textRect.offsetMax = new Vector2(-14, -10);
            msgTmp.color = isPlayer ? PlayerTextColor : new Color(0.78f, 0.92f, 0.82f);
            TMPFactory.SetWrapping(msgTmp, true);
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
            float itemsHeight = hasItems ? msg.EmbedItems.Count * 29f : 0f;
            float locationHeight = !hasStatus && hasLocation ? 29f : 0f;
            float statusHeight = hasStatus ? 39f : 0f;
            float buttonHeight = hasButton ? 42f : 0f;
            float cardHeight = 16f + photoHeight + titleHeight + statusHeight
                + (hasStatus ? itemsHeight : descHeight + itemsHeight + locationHeight)
                + buttonHeight;

            var wrapper = UIFactory.Panel("EmbedWrapper", parent, new Color(0, 0, 0, 0));
            var wrapperLe = wrapper.AddComponent<LayoutElement>();
            wrapperLe.minHeight = cardHeight;
            wrapperLe.preferredHeight = cardHeight;

            float cardInsetMin = ThreadLeft;
            float cardInsetMax = ThreadRight;

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
                var statusText = UIFactory.Text("Status", $"<b>{msg.EmbedStatus}</b>", card.transform, hasPhoto ? 26 : 21, TextAnchor.MiddleCenter);
                var statusRect = statusText.gameObject.GetComponent<RectTransform>();
                statusRect.anchorMin = new Vector2(0, 1);
                statusRect.anchorMax = new Vector2(1, 1);
                statusRect.pivot = new Vector2(0.5f, 1);
                statusRect.sizeDelta = new Vector2(0, 34);
                statusRect.anchoredPosition = new Vector2(0, -y);
                statusText.color = SoldColor;
                y += statusHeight;
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
            }

            // Items list - shown in both active and completed states
            if (hasItems)
            {
                foreach (var item in msg.EmbedItems)
                {
                    bool struck = item.StartsWith("<s>");
                    var itemTmp = TMPFactory.Text("Item", $"  \u2022  {item}", card.transform, 18, TextAlignmentOptions.TopLeft);
                    var itemRect = itemTmp.gameObject.GetComponent<RectTransform>();
                    itemRect.anchorMin = new Vector2(0, 1);
                    itemRect.anchorMax = new Vector2(1, 1);
                    itemRect.pivot = new Vector2(0, 1);
                    itemRect.sizeDelta = new Vector2(0, 24);
                    itemRect.anchoredPosition = new Vector2(0, -y);
                    itemRect.offsetMin = new Vector2(textInset, itemRect.offsetMin.y);
                    itemRect.offsetMax = new Vector2(-8, itemRect.offsetMax.y);
                    itemTmp.color = struck ? new Color(0.55f, 0.55f, 0.45f) : new Color(0.9f, 0.9f, 0.6f);
                    y += 29f;
                }
            }

            if (!hasStatus)
            {
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
                    float actionCost = GetActionCost(msg.EmbedButtonAction);
                    bool canAfford = actionCost <= 0 || Money.GetOnlineBalance() >= actionCost;
                    var btnPanel = UIFactory.Panel("EmbedBtn", card.transform, canAfford ? BuyBtnColor : BuyBtnDisabledColor);
                    var btnRect = btnPanel.GetComponent<RectTransform>();
                    btnRect.anchorMin = new Vector2(1, 0);
                    btnRect.anchorMax = new Vector2(1, 0);
                    btnRect.pivot = new Vector2(1, 0);
                    btnRect.sizeDelta = new Vector2(150, 38);
                    btnRect.anchoredPosition = new Vector2(-12, 8);

                    string actionKey = msg.EmbedButtonAction;
                    string embedThread = msg.ThreadId;
                    btnPanel.AddComponent<Button>().onClick.AddListener(new Action(() =>
                    {
                        _scrollToThread = embedThread;
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

        private static float GetActionCost(string action)
        {
            switch (action)
            {
                case "purchase_shack": return Config.ShackPurchasePrice.Value;
                case "purchase_warehouse": return Config.WarehousePurchasePrice.Value;
                case "purchase_dispensary": return Config.DispensaryPurchasePrice.Value;
                case "purchase_tier1_money": return Config.StaticTier1BankCost.Value;
                case "purchase_upgrade_money":
                    int tier = (StaticSaveData.Instance?.CrmTier ?? 0) + 1;
                    return tier == 2 ? Config.StaticTier2BankCost.Value : Config.StaticTier3BankCost.Value;
                default: return 0;
            }
        }

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
            PopulateThread(snapToBottom: false);
        }

        private void OnPurchaseWarehouse()
        {
            if (PropertySaveData.Instance == null) return;
            if (PropertySaveData.Instance.IsPropertyOwned(PropertySaveData.WarehouseId)) return;

            float price = Config.WarehousePurchasePrice.Value;
            if (Money.GetOnlineBalance() < price)
                return;

            Money.CreateOnlineTransaction("OTC Property", -price, 1f, "Static Services");
            PropertySaveData.Instance.PurchaseProperty(PropertySaveData.WarehouseId);

            if (!NetworkHelper.IsHost)
                ConfigSyncData.SendQuestAction("PURCHASE_WAREHOUSE");

            PopulateThread(snapToBottom: false);
        }

        private void OnPurchaseDispensary()
        {
            if (PropertySaveData.Instance == null) return;
            if (PropertySaveData.Instance.IsPropertyOwned(PropertySaveData.DispensaryId)) return;

            float price = Config.DispensaryPurchasePrice.Value;
            if (Money.GetOnlineBalance() < price)
                return;

            Money.CreateOnlineTransaction("OTC Property", -price, 1f, "Static Services");
            PropertySaveData.Instance.PurchaseProperty(PropertySaveData.DispensaryId);

            if (!NetworkHelper.IsHost)
                ConfigSyncData.SendQuestAction("PURCHASE_DISPENSARY");

            PopulateThread(snapToBottom: false);
        }

        private void OnAcceptIntro()
        {
            if (StaticSaveData.Instance == null) return;
            if (StaticSaveData.Instance.IntroCompleted) return;

            if (NetworkHelper.IsHost)
            {
                StaticSaveData.Instance.OnIntroCompleted();
            }
            else
            {
                ConfigSyncData.SendQuestAction("STATIC_INTRO_COMPLETED");
            }

            PopulateThread(snapToBottom: false);
        }

        private void OnAcceptUpgrade()
        {
            if (StaticSaveData.Instance == null) return;
            if (StaticSaveData.Instance.UpgradeAccepted || !StaticSaveData.Instance.UpgradeAvailable) return;

            if (NetworkHelper.IsHost)
            {
                StaticSaveData.Instance.AcceptUpgrade();
            }
            else
            {
                ConfigSyncData.SendQuestAction("STATIC_ACCEPT_UPGRADE");
            }

            PopulateThread(snapToBottom: false);
        }

        private void OnPurchaseTier1Money()
        {
            if (StaticSaveData.Instance == null) return;
            if (StaticSaveData.Instance.Tier1MoneyPaid || StaticSaveData.Instance.CrmTier >= 1) return;

            float cost = Config.StaticTier1BankCost.Value;
            if (Money.GetOnlineBalance() < cost) return;

            Money.CreateOnlineTransaction("OTC License", -cost, 1f, "Static Services");

            if (NetworkHelper.IsHost)
            {
                StaticSaveData.Instance.PayTier1Money();
            }
            else
            {
                ConfigSyncData.SendQuestAction("STATIC_PAY_TIER1");
            }

            PopulateThread(snapToBottom: false);
        }

        private void OnPurchaseUpgradeMoney()
        {
            if (StaticSaveData.Instance == null) return;
            if (StaticSaveData.Instance.UpgradeMoneyPaid || !StaticSaveData.Instance.UpgradeAvailable) return;

            int targetTier = StaticSaveData.Instance.CrmTier + 1;
            float cost = targetTier == 2 ? Config.StaticTier2BankCost.Value : Config.StaticTier3BankCost.Value;
            if (Money.GetOnlineBalance() < cost) return;

            Money.CreateOnlineTransaction("OTC Upgrade", -cost, 1f, "Static Services");

            if (NetworkHelper.IsHost)
            {
                StaticSaveData.Instance.PayUpgradeMoney();
            }
            else
            {
                ConfigSyncData.SendQuestAction("STATIC_PAY_UPGRADE");
            }

            PopulateThread(snapToBottom: false);
        }
    }
}
