using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
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
        private void RefreshLightingCards()
        {
            string currentLighting = GetCurrentLightingStyleId();
            _pendingLightingId = currentLighting;

            float balance = GetOnlineBalance();
            var styles = LightingStyle.All.Values.ToList();

            int rows = (styles.Count + GRID_COLUMNS - 1) / GRID_COLUMNS;
            float totalHeight = 10 + rows * (CARD_HEIGHT + CARD_GAP);
            var contentRect = _cardGrid.GetComponent<RectTransform>();
            contentRect.sizeDelta = new Vector2(0, totalHeight);

            for (int i = 0; i < styles.Count; i++)
            {
                var style = styles[i];
                int col = i % GRID_COLUMNS;
                int row = i / GRID_COLUMNS;

                bool isEquipped = style.Id == currentLighting;
                bool isSelected = style.Id == _pendingLightingId;
                bool canAfford = CanAffordLighting(currentLighting, style.Id, balance);

                float xPos = 10 + col * (CARD_WIDTH + CARD_GAP);
                float yPos = -(8 + row * (CARD_HEIGHT + CARD_GAP));

                var card = CreateLightingCard(style, isEquipped, isSelected, canAfford, xPos, yPos);
                _cardEntries.Add(card);
            }
        }

        private CardEntry CreateLightingCard(LightingStyle style, bool isEquipped, bool isSelected, bool canAfford,
            float xPos, float yPos)
        {
            var card = UIFactory.Panel($"Card_{style.Id}", _cardGrid, isSelected ? CardSelected : CardBg);
            var cardRect = card.GetComponent<RectTransform>();
            cardRect.anchorMin = new Vector2(0, 1);
            cardRect.anchorMax = new Vector2(0, 1);
            cardRect.pivot = new Vector2(0, 1);
            cardRect.sizeDelta = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            cardRect.anchoredPosition = new Vector2(xPos, yPos);

            var cardImage = card.GetComponent<Image>();

            // Colored swatch representing the light color
            var swatchColor = LightingSwatchColors.TryGetValue(style.Id, out var sc) ? sc : TextMuted;
            var swatch = UIFactory.Panel("Swatch", card.transform, swatchColor);
            var swatchRect = swatch.GetComponent<RectTransform>();
            swatchRect.anchorMin = new Vector2(0.1f, 0.42f);
            swatchRect.anchorMax = new Vector2(0.9f, 0.92f);
            swatchRect.offsetMin = Vector2.zero;
            swatchRect.offsetMax = Vector2.zero;
            var swatchImage = swatch.GetComponent<Image>();

            // Name label
            var nameLabel = TMPFactory.Text($"Name_{style.Id}", style.DisplayName,
                card.transform, 12, TextAlignmentOptions.Left, FontStyles.Bold);
            nameLabel.color = Color.white;
            var nameRect = nameLabel.gameObject.GetComponent<RectTransform>();
            nameRect.anchorMin = Vector2.zero;
            nameRect.anchorMax = new Vector2(1, 0.38f);
            nameRect.offsetMin = new Vector2(6, 14);
            nameRect.offsetMax = new Vector2(-6, 0);

            // Price label
            string priceStr = style.Cost <= 0 ? "FREE" : $"${style.Cost:F0}";
            var priceLabel = TMPFactory.Text($"Price_{style.Id}", priceStr,
                card.transform, 11, TextAlignmentOptions.Left);
            priceLabel.color = style.Cost <= 0 ? AccentGreen : TextMuted;
            var priceRect = priceLabel.gameObject.GetComponent<RectTransform>();
            priceRect.anchorMin = Vector2.zero;
            priceRect.anchorMax = new Vector2(1, 0.20f);
            priceRect.offsetMin = new Vector2(6, 0);
            priceRect.offsetMax = new Vector2(-6, 0);

            // EQUIPPED badge
            GameObject equippedBadge = null;
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
            string styleId = style.Id;
            var btn = card.AddComponent<Button>();
            btn.targetGraphic = cardImage;
            btn.onClick.AddListener(new Action(() => OnCardClicked(styleId)));

            return new CardEntry
            {
                StyleId = style.Id,
                Card = card,
                CardImage = cardImage,
                PriceText = priceLabel,
                EquippedBadge = equippedBadge,
                LockOverlay = lockOverlay,
                SwatchImage = swatchImage,
            };
        }

        private void ApplyLightingUpgrade()
        {
            string currentLighting = GetCurrentLightingStyleId();
            if (_pendingLightingId == null || _pendingLightingId == currentLighting) return;

            var newStyle = LightingStyle.Get(_pendingLightingId);
            float balance = GetOnlineBalance();
            float cost = GetLightingUpgradeCost(currentLighting, _pendingLightingId);

            if (cost > 0 && balance < cost) return;

            if (cost > 0)
            {
                try
                {
                    var mm = NetworkSingleton<MoneyManager>.Instance;
                    mm?.CreateOnlineTransaction("Lighting Upgrade", -cost, 1, $"Upgraded to {newStyle.DisplayName}");
                }
                catch (Exception ex)
                {
                    OTCLog.Error(OTCLog.Systems.Patch, $"Lighting payment failed: {ex.Message}");
                    return;
                }
            }

            ApplyBuildingLighting(newStyle);

            try { ConfigSyncData.Instance?.PublishGameState(); }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"Failed to sync lighting change: {ex.Message}");
            }

            OTCLog.Msg(OTCLog.Systems.Patch, $"Lighting upgraded to '{newStyle.DisplayName}' for ${cost:F0}");
            RefreshCards();
        }

        private static float GetLightingUpgradeCost(string fromId, string toId)
        {
            var from = LightingStyle.Get(fromId);
            var to = LightingStyle.Get(toId);
            float diff = to.Cost - from.Cost;
            return diff > 0 ? diff : 0;
        }

        private static bool CanAffordLighting(string currentId, string targetId, float balance)
        {
            if (currentId == targetId) return true;
            float cost = GetLightingUpgradeCost(currentId, targetId);
            return cost <= 0 || balance >= cost;
        }
    }
}
