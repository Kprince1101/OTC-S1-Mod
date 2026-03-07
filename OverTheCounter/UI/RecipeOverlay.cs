using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppInterop.Runtime.Injection;
using Il2CppTMPro;
using ProductDefinitionType = Il2CppScheduleOne.Product.ProductDefinition;
using ItemDefinitionType = Il2CppScheduleOne.ItemFramework.ItemDefinition;
using GameCanvasScaler = Il2CppScheduleOne.UI.CanvasScaler;
#else
using TMPro;
using ProductDefinitionType = ScheduleOne.Product.ProductDefinition;
using ItemDefinitionType = ScheduleOne.ItemFramework.ItemDefinition;
using GameCanvasScaler = ScheduleOne.UI.CanvasScaler;
#endif

namespace OverTheCounter.UI
{
    [RegisterTypeInIl2Cpp]
    public class RecipeOverlay : MonoBehaviour
    {
        /// <summary>Singleton instance created in OnSceneWasLoaded.</summary>
        public static RecipeOverlay Instance { get; private set; }

        private GameObject _canvasObj;
        private RectTransform _panelRect;
        private RectTransform _titleBarRect;
        private bool _dragging;
        private Vector2 _lastMousePos;
        private string _pinnedProductId;
        private List<(RectTransform rect, string name)> _chainItems;
        private GameObject _tooltipObj;
        private TextMeshProUGUI _tooltipText;

        // Temporary storage for chain data during overlay construction.
        // Avoids passing ValueTuple lists through instance method signatures,
        // which IL2CPP interop cannot register.
        private static List<string> _chainNames;
        private static List<Sprite> _chainIcons;

        private const float ICON_SIZE = 40f;
        private const int SORT_ORDER = 100;

        /// <summary>Registers this type with IL2CPP interop. Call once during initialization.</summary>
        public static void Register()
        {
#if IL2CPP
            ClassInjector.RegisterTypeInIl2Cpp<RecipeOverlay>();
#endif
        }

        private void Awake() => Instance = this;

        private void Update()
        {
            if (_canvasObj == null || !_canvasObj.activeSelf) return;

            if (Input.GetMouseButtonDown(0) && _titleBarRect != null &&
                RectTransformUtility.RectangleContainsScreenPoint(_titleBarRect, Input.mousePosition))
            {
                _dragging = true;
                _lastMousePos = Input.mousePosition;
            }

            if (Input.GetMouseButtonUp(0))
                _dragging = false;

            if (_dragging && _panelRect != null)
            {
                Vector2 delta = (Vector2)Input.mousePosition - _lastMousePos;
                _panelRect.anchoredPosition += delta;
                _lastMousePos = Input.mousePosition;
            }

            // Tooltip: show full name on hover
            if (_chainItems != null)
            {
                bool found = false;
                for (int i = 0; i < _chainItems.Count; i++)
                {
                    var item = _chainItems[i];
                    if (item.rect != null &&
                        RectTransformUtility.RectangleContainsScreenPoint(item.rect, Input.mousePosition))
                    {
                        ShowTooltip(item.name);
                        found = true;
                        break;
                    }
                }
                if (!found) HideTooltip();
            }
        }

        /// <summary>Pins a product's recipe chain as a draggable overlay. Toggles off if already pinned.</summary>
        public void Pin(ProductDefinitionType product)
        {
            if (!Config.RecipePinEnabled.Value || product == null) return;

            if (_pinnedProductId == product.ID)
            {
                Unpin();
                return;
            }

            Unpin();
            _pinnedProductId = product.ID;

            // Build chain into static parallel lists (avoids ValueTuple in instance signatures)
            _chainNames = new List<string>();
            _chainIcons = new List<Sprite>();
            BuildChainRecursive(product, _chainNames, _chainIcons, 0);
            _chainNames.Add(product.Name);
            _chainIcons.Add(product.Icon);

            CreateOverlay(product.Name);
        }

        /// <summary>Destroys the overlay and clears the pinned product.</summary>
        public void Unpin()
        {
            if (_canvasObj != null)
                Destroy(_canvasObj);
            _canvasObj = null;
            _panelRect = null;
            _titleBarRect = null;
            _pinnedProductId = null;
            _chainItems = null;
            _tooltipObj = null;
            _tooltipText = null;
            _dragging = false;
        }

        /// <summary>
        /// Recursively walks ProductDefinition.Recipes to build the ingredient chain
        /// into parallel name/icon lists. Static to avoid IL2CPP interop registration issues.
        /// </summary>
        private static void BuildChainRecursive(ProductDefinitionType product,
            List<string> names, List<Sprite> icons, int depth)
        {
            if (depth > 20) return;

            if (product.Recipes == null || product.Recipes.Count == 0)
            {
                names.Add(product.Name);
                icons.Add(product.Icon);
                return;
            }

            var recipe = product.Recipes[0];
            if (recipe?.Ingredients == null || recipe.Ingredients.Count < 2)
            {
                names.Add(product.Name);
                icons.Add(product.Icon);
                return;
            }

            var item0 = recipe.Ingredients[0]?.Item;
            var item1 = recipe.Ingredients[1]?.Item;

            var pd0 = AsProduct(item0);
            var pd1 = AsProduct(item1);

            if (pd0 != null)
            {
                BuildChainRecursive(pd0, names, icons, depth + 1);
                if (item1 != null) { names.Add(item1.Name); icons.Add(item1.Icon); }
            }
            else if (pd1 != null)
            {
                BuildChainRecursive(pd1, names, icons, depth + 1);
                if (item0 != null) { names.Add(item0.Name); icons.Add(item0.Icon); }
            }
            else
            {
                names.Add(product.Name);
                icons.Add(product.Icon);
            }
        }

        private static ProductDefinitionType AsProduct(ItemDefinitionType item)
        {
            if (item == null) return null;
#if IL2CPP
            return item.TryCast<ProductDefinitionType>();
#else
            return item as ProductDefinitionType;
#endif
        }

        private void CreateOverlay(string productName)
        {
            _canvasObj = new GameObject("OTC_RecipeOverlay");
            var canvas = _canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SORT_ORDER;
            var scaler = _canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            _canvasObj.AddComponent<GameCanvasScaler>();
            _canvasObj.AddComponent<GraphicRaycaster>();
            DontDestroyOnLoad(_canvasObj);

            // Panel (auto-sized)
            var panelObj = new GameObject("Panel");
            panelObj.transform.SetParent(_canvasObj.transform, false);
            _panelRect = panelObj.AddComponent<RectTransform>();
            _panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            _panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            _panelRect.pivot = new Vector2(0.5f, 0.5f);

            var panelImg = panelObj.AddComponent<Image>();
            panelImg.color = new Color(0.1f, 0.1f, 0.1f, 0.88f);

            var panelLayout = panelObj.AddComponent<VerticalLayoutGroup>();
            panelLayout.spacing = 0;
            panelLayout.padding = new RectOffset(0, 0, 0, 6);
            panelLayout.childControlWidth = true;
            panelLayout.childControlHeight = true;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;

            var panelFitter = panelObj.AddComponent<ContentSizeFitter>();
            panelFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            panelFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Title bar
            BuildTitleBar(panelObj.transform, productName);

            // Chain row — reads from _chainNames/_chainIcons
            _chainItems = new List<(RectTransform, string)>();
            BuildChainRow(panelObj.transform);

            // Done with temp chain data
            _chainNames = null;
            _chainIcons = null;

            // Tooltip (hidden by default, child of canvas so it floats above panel)
            BuildTooltip(_canvasObj.transform);
        }

        private void BuildTitleBar(Transform parent, string productName)
        {
            var titleObj = new GameObject("TitleBar");
            titleObj.transform.SetParent(parent, false);
            _titleBarRect = titleObj.AddComponent<RectTransform>();

            var titleBg = titleObj.AddComponent<Image>();
            titleBg.color = new Color(0.18f, 0.18f, 0.18f, 1f);
            titleBg.raycastTarget = true;

            var titleLayout = titleObj.AddComponent<HorizontalLayoutGroup>();
            titleLayout.spacing = 4f;
            titleLayout.padding = new RectOffset(10, 4, 4, 4);
            titleLayout.childControlWidth = true;
            titleLayout.childControlHeight = true;
            titleLayout.childForceExpandWidth = false;
            titleLayout.childForceExpandHeight = true;
            titleLayout.childAlignment = TextAnchor.MiddleLeft;

            // Title text
            var titleText = TMPFactory.Text("Title", productName, titleObj.transform, 15, TextAlignmentOptions.Left);
            titleText.raycastTarget = false;
            var titleLE = titleText.gameObject.AddComponent<LayoutElement>();
            titleLE.flexibleWidth = 1f;
            titleLE.minWidth = 80f;

            // Close button
            var closeObj = new GameObject("CloseBtn");
            closeObj.transform.SetParent(titleObj.transform, false);
            var closeBg = closeObj.AddComponent<Image>();
            closeBg.color = new Color(0.6f, 0.15f, 0.15f, 0.9f);
            var closeBtn = closeObj.AddComponent<Button>();
            closeBtn.onClick.AddListener(new Action(Unpin));
            var closeLE = closeObj.AddComponent<LayoutElement>();
            closeLE.preferredWidth = 22f;
            closeLE.preferredHeight = 22f;

            var closeText = TMPFactory.Text("X", "\u2715", closeObj.transform, 15, TextAlignmentOptions.Center);
            closeText.raycastTarget = false;
            var ctr = closeText.GetComponent<RectTransform>();
            ctr.anchorMin = Vector2.zero;
            ctr.anchorMax = Vector2.one;
            ctr.offsetMin = Vector2.zero;
            ctr.offsetMax = Vector2.zero;
        }

        private void BuildChainRow(Transform parent)
        {
            var rowObj = new GameObject("ChainRow");
            rowObj.transform.SetParent(parent, false);

            var rowLayout = rowObj.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 2f;
            rowLayout.padding = new RectOffset(10, 10, 4, 4);
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;

            var rowFitter = rowObj.AddComponent<ContentSizeFitter>();
            rowFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            rowFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            int count = _chainNames?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    AddArrow(rowObj.transform);
                AddChainItem(rowObj.transform, _chainNames[i], _chainIcons[i]);
            }
        }

        private void AddChainItem(Transform parent, string name, Sprite icon)
        {
            var itemObj = new GameObject("Item_" + name);
            itemObj.transform.SetParent(parent, false);

            var itemLayout = itemObj.AddComponent<VerticalLayoutGroup>();
            itemLayout.spacing = 2f;
            itemLayout.childControlWidth = true;
            itemLayout.childControlHeight = true;
            itemLayout.childForceExpandWidth = false;
            itemLayout.childForceExpandHeight = false;
            itemLayout.childAlignment = TextAnchor.MiddleCenter;

            // Icon
            var iconObj = new GameObject("Icon");
            iconObj.transform.SetParent(itemObj.transform, false);
            var img = iconObj.AddComponent<Image>();
            img.sprite = icon;
            img.preserveAspect = true;
            img.raycastTarget = false;
            var iconLE = iconObj.AddComponent<LayoutElement>();
            iconLE.preferredWidth = ICON_SIZE;
            iconLE.preferredHeight = ICON_SIZE;

            // Label
            var label = TMPFactory.Text("Label", TruncateName(name), itemObj.transform, 15, TextAlignmentOptions.Center);
            label.raycastTarget = false;
            var labelLE = label.gameObject.AddComponent<LayoutElement>();
            labelLE.preferredWidth = ICON_SIZE + 20f;

            _chainItems?.Add((itemObj.GetComponent<RectTransform>(), name));
        }

        private static void AddArrow(Transform parent)
        {
            var arrowText = TMPFactory.Text("Arrow", "\u2192", parent, 16, TextAlignmentOptions.Center);
            arrowText.raycastTarget = false;
            var arrowLE = arrowText.gameObject.AddComponent<LayoutElement>();
            arrowLE.preferredWidth = 18f;
        }

        private void BuildTooltip(Transform canvasTransform)
        {
            _tooltipObj = new GameObject("Tooltip");
            _tooltipObj.transform.SetParent(canvasTransform, false);
            var rt = _tooltipObj.AddComponent<RectTransform>();
            rt.pivot = new Vector2(0.5f, 0f);

            var bg = _tooltipObj.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.05f, 0.05f, 0.95f);
            bg.raycastTarget = false;

            var hlg = _tooltipObj.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(8, 8, 4, 4);
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;

            _tooltipText = TMPFactory.Text("Text", "", _tooltipObj.transform, 15, TextAlignmentOptions.Center);
            _tooltipText.raycastTarget = false;

            var fitter = _tooltipObj.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _tooltipObj.SetActive(false);
        }

        private void ShowTooltip(string name)
        {
            if (_tooltipObj == null) return;
            _tooltipObj.SetActive(true);
            _tooltipText.text = name;
            _tooltipObj.GetComponent<RectTransform>().position =
                (Vector2)Input.mousePosition + new Vector2(0, 24);
        }

        private void HideTooltip()
        {
            if (_tooltipObj != null)
                _tooltipObj.SetActive(false);
        }

        private static string TruncateName(string name)
        {
            return name.Length > 10 ? name.Substring(0, 9) + "." : name;
        }
    }
}
