using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using S1API.Misc;
using S1API.UI;
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Money;
using Il2CppTMPro;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Money;
using TMPro;
#endif

namespace OverTheCounter.Apps
{
    public partial class GreenTabApp
    {
        private const float SECTION_HEADER_HEIGHT = 26f;
        private const float SECTION_GAP = 14f;

        private void RefreshWallCards()
        {
            string currentExt = GetCurrentExteriorWallStyleId();
            string currentInt = GetCurrentInteriorWallStyleId();
            _pendingExteriorWallId = currentExt;
            _pendingInteriorWallId = currentInt;

            float balance = GetOnlineBalance();
            var extStyles = WallStyle.ExteriorStyles.Values.ToList();
            var intStyles = WallStyle.InteriorStyles.Values.ToList();

            int extRows = (extStyles.Count + GRID_COLUMNS - 1) / GRID_COLUMNS;
            int intRows = (intStyles.Count + GRID_COLUMNS - 1) / GRID_COLUMNS;
            float extSectionHeight = SECTION_HEADER_HEIGHT + extRows * (CARD_HEIGHT + CARD_GAP);
            float intSectionHeight = SECTION_HEADER_HEIGHT + intRows * (CARD_HEIGHT + CARD_GAP);
            float totalHeight = 10 + extSectionHeight + SECTION_GAP + intSectionHeight + 10;

            var contentRect = _cardGrid.GetComponent<RectTransform>();
            contentRect.sizeDelta = new Vector2(0, totalHeight);

            float yOffset = -8;

            // ---- EXTERIOR section ----
            CreateSectionHeader("EXTERIOR WALLS", yOffset);
            yOffset -= SECTION_HEADER_HEIGHT;

            for (int i = 0; i < extStyles.Count; i++)
            {
                var style = extStyles[i];
                int col = i % GRID_COLUMNS;
                int row = i / GRID_COLUMNS;

                bool isEquipped = style.Id == currentExt;
                bool isSelected = style.Id == _pendingExteriorWallId;
                bool canAfford = CanAffordWall(currentExt, style.Id, balance, true);

                float xPos = 10 + col * (CARD_WIDTH + CARD_GAP);
                float yPos = yOffset - row * (CARD_HEIGHT + CARD_GAP);

                var card = CreateWallCard(style, isEquipped, isSelected, canAfford, xPos, yPos, true);
                _cardEntries.Add(card);
            }

            yOffset -= extRows * (CARD_HEIGHT + CARD_GAP) + SECTION_GAP;

            // ---- INTERIOR section ----
            CreateSectionHeader("INTERIOR WALLS", yOffset);
            yOffset -= SECTION_HEADER_HEIGHT;

            for (int i = 0; i < intStyles.Count; i++)
            {
                var style = intStyles[i];
                int col = i % GRID_COLUMNS;
                int row = i / GRID_COLUMNS;

                bool isEquipped = style.Id == currentInt;
                bool isSelected = style.Id == _pendingInteriorWallId;
                bool canAfford = CanAffordWall(currentInt, style.Id, balance, false);

                float xPos = 10 + col * (CARD_WIDTH + CARD_GAP);
                float yPos = yOffset - row * (CARD_HEIGHT + CARD_GAP);

                var card = CreateWallCard(style, isEquipped, isSelected, canAfford, xPos, yPos, false);
                _cardEntries.Add(card);
            }
        }

        private void CreateSectionHeader(string text, float yPos)
        {
            var header = TMPFactory.Text($"Header_{text}", text,
                _cardGrid, 12, TextAlignmentOptions.Left, FontStyles.Bold);
            header.color = TextMuted;
            var headerRect = header.gameObject.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0, 1);
            headerRect.sizeDelta = new Vector2(0, SECTION_HEADER_HEIGHT);
            headerRect.anchoredPosition = new Vector2(10, yPos);
        }

        private CardEntry CreateWallCard(WallStyle style, bool isEquipped, bool isSelected, bool canAfford,
            float xPos, float yPos, bool isExterior)
        {
            string prefix = isExterior ? "ext" : "int";
            string prefixedId = $"{prefix}:{style.Id}";

            var card = UIFactory.Panel($"Card_{prefixedId}", _cardGrid, isSelected ? CardSelected : CardBg);
            var cardRect = card.GetComponent<RectTransform>();
            cardRect.anchorMin = new Vector2(0, 1);
            cardRect.anchorMax = new Vector2(0, 1);
            cardRect.pivot = new Vector2(0, 1);
            cardRect.sizeDelta = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            cardRect.anchoredPosition = new Vector2(xPos, yPos);

            var cardImage = card.GetComponent<Image>();

            // Colored square swatch
            var swatchColor = WallSwatchColors.TryGetValue(style.Id, out var sc) ? sc : TextMuted;
            var swatch = UIFactory.Panel("Swatch", card.transform, swatchColor);
            var swatchRect = swatch.GetComponent<RectTransform>();
            swatchRect.anchorMin = new Vector2(0.1f, 0.42f);
            swatchRect.anchorMax = new Vector2(0.9f, 0.92f);
            swatchRect.offsetMin = Vector2.zero;
            swatchRect.offsetMax = Vector2.zero;
            var swatchImage = swatch.GetComponent<Image>();

            // Name label
            var nameLabel = TMPFactory.Text($"Name_{prefixedId}", style.DisplayName,
                card.transform, 12, TextAlignmentOptions.Left, FontStyles.Bold);
            nameLabel.color = Color.white;
            var nameRect = nameLabel.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = Vector2.zero;
            nameRect.anchorMax = new Vector2(1, 0.38f);
            nameRect.offsetMin = new Vector2(6, 14);
            nameRect.offsetMax = new Vector2(-6, 0);

            // Price label
            string priceStr = style.Cost <= 0 ? "FREE" : $"${style.Cost:F0}";
            var priceLabel = TMPFactory.Text($"Price_{prefixedId}", priceStr,
                card.transform, 11, TextAlignmentOptions.Left);
            priceLabel.color = style.Cost <= 0 ? AccentGreen : TextMuted;
            var priceRect = priceLabel.gameObject.GetComponent<RectTransform>();
            priceRect.anchorMin = Vector2.zero;
            priceRect.anchorMax = new Vector2(1, 0.20f);
            priceRect.offsetMin = new Vector2(6, 0);
            priceRect.offsetMax = new Vector2(-6, 0);

            // EQUIPPED badge
            GameObject equippedBadge;
            if (isEquipped)
            {
                equippedBadge = UIFactory.Panel("EquippedBadge", card.transform, EquippedBadge);
                var badgeRect = equippedBadge.GetComponent<RectTransform>();
                badgeRect.anchorMin = new Vector2(1, 1);
                badgeRect.anchorMax = new Vector2(1, 1);
                badgeRect.pivot = new Vector2(1, 1);
                badgeRect.sizeDelta = new Vector2(60, 16);
                badgeRect.anchoredPosition = new Vector2(-4, -4);

                var badgeText = TMPFactory.Text("EquippedText", "EQUIPPED",
                    equippedBadge.transform, 9, TextAlignmentOptions.Center, FontStyles.Bold);
                badgeText.color = Color.white;
                var btRect = badgeText.gameObject.GetComponent<RectTransform>();
                btRect.anchorMin = Vector2.zero;
                btRect.anchorMax = Vector2.one;
                btRect.offsetMin = Vector2.zero;
                btRect.offsetMax = Vector2.zero;
            }
            else
            {
                equippedBadge = new GameObject("EquippedBadge");
                equippedBadge.transform.SetParent(card.transform, false);
                equippedBadge.SetActive(false);
            }

            // Lock overlay
            var lockOverlay = UIFactory.Panel("LockOverlay", card.transform, LockedOverlay);
            var lockOvRect = lockOverlay.GetComponent<RectTransform>();
            lockOvRect.anchorMin = Vector2.zero;
            lockOvRect.anchorMax = Vector2.one;
            lockOvRect.offsetMin = Vector2.zero;
            lockOvRect.offsetMax = Vector2.zero;

            var lockSp = LoadIcon("LockIcon");
            if (lockSp != null)
            {
                var lockIconGo = new GameObject("LockIconOverlay");
                lockIconGo.transform.SetParent(lockOverlay.transform, false);
                var lockIconImg = lockIconGo.AddComponent<Image>();
                lockIconImg.sprite = lockSp;
                lockIconImg.preserveAspect = true;
                lockIconImg.color = new Color(0.9f, 0.3f, 0.3f, 0.7f);
                var lockIconRect = lockIconGo.GetComponent<RectTransform>();
                lockIconRect.anchorMin = new Vector2(0.35f, 0.45f);
                lockIconRect.anchorMax = new Vector2(0.65f, 0.85f);
                lockIconRect.offsetMin = Vector2.zero;
                lockIconRect.offsetMax = Vector2.zero;
            }

            var lockText = TMPFactory.Text("LockText", "INSUFFICIENT FUNDS",
                lockOverlay.transform, 10, TextAlignmentOptions.Center, FontStyles.Bold);
            lockText.color = new Color(0.9f, 0.3f, 0.3f);
            var lockTextRect = lockText.gameObject.GetComponent<RectTransform>();
            lockTextRect.anchorMin = new Vector2(0, 0);
            lockTextRect.anchorMax = new Vector2(1, 0.45f);
            lockTextRect.offsetMin = Vector2.zero;
            lockTextRect.offsetMax = Vector2.zero;

            lockOverlay.SetActive(false);

            // Click handler
            var btn = card.AddComponent<Button>();
            btn.targetGraphic = cardImage;
            btn.onClick.AddListener(new Action(() => OnCardClicked(prefixedId)));

            return new CardEntry
            {
                StyleId = prefixedId,
                Card = card,
                CardImage = cardImage,
                PriceText = priceLabel,
                EquippedBadge = equippedBadge,
                LockOverlay = lockOverlay,
                SwatchImage = swatchImage,
            };
        }

        private void ApplyWallUpgrade()
        {
            string currentExt = GetCurrentExteriorWallStyleId();
            string currentInt = GetCurrentInteriorWallStyleId();

            bool extChanged = _pendingExteriorWallId != null && _pendingExteriorWallId != currentExt;
            bool intChanged = _pendingInteriorWallId != null && _pendingInteriorWallId != currentInt;

            if (!extChanged && !intChanged) return;

            float totalCost = 0;
            if (extChanged) totalCost += GetWallUpgradeCost(currentExt, _pendingExteriorWallId, true);
            if (intChanged) totalCost += GetWallUpgradeCost(currentInt, _pendingInteriorWallId, false);

            float balance = GetOnlineBalance();
            if (totalCost > 0 && balance < totalCost) return;

            if (totalCost > 0)
            {
                try
                {
                    var mm = NetworkSingleton<MoneyManager>.Instance;
                    mm?.CreateOnlineTransaction("Wall Upgrade", -totalCost, 1, "Wall style upgrade");
                }
                catch (Exception ex)
                {
                    OTCLog.Error(OTCLog.Systems.Patch, $"Wall payment failed: {ex.Message}");
                    return;
                }
            }

            if (extChanged)
                ApplyBuildingExteriorWall(_pendingExteriorWallId);

            if (intChanged)
                ApplyBuildingInteriorWall(_pendingInteriorWallId);

            if (NetworkHelper.IsHost)
            {
                try { ConfigSyncData.Instance?.PublishGameState(); }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Network, $"Failed to sync wall change: {ex.Message}");
                }
            }
            else
            {
                if (extChanged)
                    ConfigSyncData.SendQuestAction($"STYLE:{_selectedBuildingId}:ext_wall:{_pendingExteriorWallId}");
                if (intChanged)
                    ConfigSyncData.SendQuestAction($"STYLE:{_selectedBuildingId}:int_wall:{_pendingInteriorWallId}");
            }

            OTCLog.Msg(OTCLog.Systems.Patch, $"Wall upgrade applied");
            RefreshCards();
        }

        private static float GetWallUpgradeCost(string fromId, string toId, bool isExterior)
        {
            var from = isExterior ? WallStyle.GetExterior(fromId) : WallStyle.GetInterior(fromId);
            var to = isExterior ? WallStyle.GetExterior(toId) : WallStyle.GetInterior(toId);
            float diff = to.Cost - from.Cost;
            return diff > 0 ? diff : 0;
        }

        private static bool CanAffordWall(string currentId, string targetId, float balance, bool isExterior)
        {
            if (currentId == targetId) return true;
            float cost = GetWallUpgradeCost(currentId, targetId, isExterior);
            return cost <= 0 || balance >= cost;
        }
    }
}
