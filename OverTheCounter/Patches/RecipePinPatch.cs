using HarmonyLib;
using MelonLoader;
using OverTheCounter.UI;
using System;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using ProductAppDetailPanelType = Il2CppScheduleOne.UI.Phone.ProductManagerApp.ProductAppDetailPanel;
using ProductDefinitionType = Il2CppScheduleOne.Product.ProductDefinition;
#else
using ProductAppDetailPanelType = ScheduleOne.UI.Phone.ProductManagerApp.ProductAppDetailPanel;
using ProductDefinitionType = ScheduleOne.Product.ProductDefinition;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Injects a "Pin Recipe" button into the Product Manager phone app's detail panel.
    /// Postfix on SetActiveProduct updates the button for the currently viewed product.
    /// </summary>
    public static class RecipePinPatch
    {
        private static Button _pinButton;
        private static ProductDefinitionType _currentProduct;

        /// <summary>Patches ProductAppDetailPanel.SetActiveProduct to inject the pin button.</summary>
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var setActive = AccessTools.Method(typeof(ProductAppDetailPanelType), "SetActiveProduct");
                if (setActive != null)
                    harmony.Patch(setActive,
                        postfix: new HarmonyMethod(typeof(RecipePinPatch), nameof(SetActiveProduct_Postfix)));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[OTC] RecipePinPatch failed to apply: " + ex.Message);
            }
        }

        private static void SetActiveProduct_Postfix(ProductAppDetailPanelType __instance,
            ProductDefinitionType productDefinition)
        {
            if (!Config.RecipePinEnabled.Value) return;

            try
            {
                _currentProduct = productDefinition;

                if (_pinButton == null || _pinButton.gameObject == null)
                    CreatePinButton(__instance);

                if (_pinButton == null) return;

                // Only show if the product has a recipe chain (base products don't)
                bool hasRecipe = productDefinition != null &&
                                 productDefinition.Recipes != null &&
                                 productDefinition.Recipes.Count > 0;
                _pinButton.gameObject.SetActive(hasRecipe && __instance.Container.activeSelf);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[OTC] RecipePinPatch postfix error: " + ex.Message);
            }
        }

        private static void CreatePinButton(ProductAppDetailPanelType panel)
        {
            // Small button anchored to the right of the "Recipe(s)" label
            Transform parent = panel.RecipesLabel;
            if (parent == null) parent = panel.Container?.transform;
            if (parent == null) return;

            var btnObj = new GameObject("OTC_PinRecipeBtn");
            btnObj.transform.SetParent(parent, false);

            var rect = btnObj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            // Position right after the "Recipe(s)" text
            var recipesText = panel.RecipesLabel.GetComponentInChildren<Text>();
            float textWidth = recipesText != null ? recipesText.preferredWidth : 60f;
            rect.anchoredPosition = new Vector2(textWidth + 14, 2);
            rect.sizeDelta = new Vector2(44, 24);

            var bg = btnObj.AddComponent<Image>();
            bg.color = new Color(0.25f, 0.25f, 0.25f, 0.9f);

            _pinButton = btnObj.AddComponent<Button>();
            var colors = _pinButton.colors;
            colors.normalColor = new Color(0.25f, 0.25f, 0.25f, 0.9f);
            colors.highlightedColor = new Color(0.4f, 0.4f, 0.4f, 1f);
            colors.pressedColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            _pinButton.colors = colors;
            _pinButton.onClick.AddListener(new Action(OnPinClicked));

            var label = S1API.UI.UIFactory.Text("Label", "Pin", btnObj.transform, 15, TextAnchor.MiddleCenter);
            label.raycastTarget = false;
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
        }

        private static void OnPinClicked()
        {
            if (_currentProduct == null) return;
            RecipeOverlay.Instance?.Pin(_currentProduct);
        }
    }
}
