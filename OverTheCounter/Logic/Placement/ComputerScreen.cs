using MelonLoader;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Product;
using Il2CppTMPro;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Product;
using TMPro;
#endif

namespace OverTheCounter.Logic.Placement
{
    /// <summary>
    /// WorldSpace Canvas on the checkout computer monitor.
    /// Shows store name always; shows product info + [F] Checkout when a customer is waiting.
    /// </summary>
    public static class ComputerScreen
    {
        private static GameObject _canvasGo;
        private static Canvas _canvas;
        private static TextMeshProUGUI _headerText;
        private static GameObject _checkoutPanel;
        private static TextMeshProUGUI _promptText;

        private const int MaxProductRows = 3;
        private static readonly List<ProductRow> _productRows = new();

        private struct ProductRow
        {
            public GameObject Root;
            public Image Icon;
            public TextMeshProUGUI Label;
        }

        /// <summary>
        /// Creates the WorldSpace Canvas on the computer monitor.
        /// Called from CheckoutCounter.ApplyDeskVisual after the computer mesh is instantiated.
        /// </summary>
        public static void Create(GameObject computerGo)
        {
            if (computerGo == null) return;
            if (_canvasGo != null) return;

            try
            {
                // Create canvas GO as child of the Computer (all-in-one with built-in screen)
                _canvasGo = new GameObject("OTC_ComputerScreen");
                _canvasGo.transform.SetParent(computerGo.transform, false);

                // Position canvas on the built-in screen face (values from runtime editor).
                _canvasGo.transform.localPosition = new Vector3(-0.1600f, -0.0100f, 0.2450f);
                _canvasGo.transform.localRotation = Quaternion.Euler(0f, 269f, 270f);
                _canvasGo.transform.localScale = new Vector3(-0.0022f, 0.0019f, 0.0019f);

                // Canvas component
                _canvas = _canvasGo.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.WorldSpace;
                _canvas.sortingOrder = 10;

                var rt = _canvasGo.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(200f, 150f);
                rt.pivot = new Vector2(0.5f, 0.5f);

                // Background
                var bgGo = new GameObject("Background");
                bgGo.transform.SetParent(_canvasGo.transform, false);
                var bgRt = bgGo.AddComponent<RectTransform>();
                bgRt.anchorMin = Vector2.zero;
                bgRt.anchorMax = Vector2.one;
                bgRt.offsetMin = Vector2.zero;
                bgRt.offsetMax = Vector2.zero;
                var bgImg = bgGo.AddComponent<Image>();
                bgImg.color = new Color(0.05f, 0.08f, 0.15f, 0.95f);
                bgImg.raycastTarget = false;

                // Header text (store name — always visible)
                _headerText = CreateText("Header", _canvasGo.transform,
                    new Vector2(0f, 55f), new Vector2(180f, 40f),
                    "GreenTab POS", 14, TextAlignmentOptions.Center,
                    new Color(0.7f, 0.85f, 1f));

                // Checkout panel (hidden by default)
                _checkoutPanel = new GameObject("CheckoutPanel");
                _checkoutPanel.transform.SetParent(_canvasGo.transform, false);
                var panelRt = _checkoutPanel.AddComponent<RectTransform>();
                panelRt.anchorMin = new Vector2(0.5f, 0.5f);
                panelRt.anchorMax = new Vector2(0.5f, 0.5f);
                panelRt.pivot = new Vector2(0.5f, 0.5f);
                panelRt.sizeDelta = new Vector2(180f, 100f);
                panelRt.anchoredPosition = new Vector2(0f, -10f);

                // Product rows (up to 3)
                for (int i = 0; i < MaxProductRows; i++)
                {
                    var row = CreateProductRow(i, _checkoutPanel.transform);
                    _productRows.Add(row);
                }

                // [F] Checkout prompt at bottom of panel
                _promptText = CreateText("Prompt", _checkoutPanel.transform,
                    new Vector2(0f, -40f), new Vector2(180f, 24f),
                    "[F] Checkout", 12, TextAlignmentOptions.Center,
                    new Color(1f, 0.9f, 0.4f));

                _checkoutPanel.SetActive(false);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Error($"ComputerScreen.Create failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows the checkout panel with product info and [F] prompt.
        /// </summary>
        public static void ShowCheckoutInfo(List<CustomerInstance.SelectedProduct> products)
        {
            if (_checkoutPanel == null) return;

            try
            {
                for (int i = 0; i < MaxProductRows; i++)
                {
                    if (i < products.Count)
                    {
                        var product = products[i];
                        var row = _productRows[i];
                        row.Root.SetActive(true);
                        row.Label.text = product.ProductName;

                        // Try to get the product icon sprite
                        try
                        {
                            var iconMgr = Singleton<ProductIconManager>.Instance;
                            if (iconMgr != null && product.ProductId != null && product.PackagingId != null)
                            {
                                var sprite = iconMgr.GetIcon(product.ProductId, product.PackagingId, true);
                                if (sprite != null)
                                {
                                    row.Icon.sprite = sprite;
                                    row.Icon.color = Color.white;
                                }
                                else
                                {
                                    row.Icon.sprite = null;
                                    row.Icon.color = new Color(0.4f, 0.4f, 0.4f, 0.5f);
                                }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        _productRows[i].Root.SetActive(false);
                    }
                }

                _checkoutPanel.SetActive(true);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"ShowCheckoutInfo failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Hides the checkout panel, returning to store name only.
        /// </summary>
        public static void HideCheckoutInfo()
        {
            if (_checkoutPanel != null)
                _checkoutPanel.SetActive(false);
        }

        /// <summary>
        /// Destroys the canvas. Called on scene cleanup.
        /// </summary>
        public static void Cleanup()
        {
            _productRows.Clear();
            if (_canvasGo != null)
            {
                UnityEngine.Object.Destroy(_canvasGo);
                _canvasGo = null;
            }
            _canvas = null;
            _headerText = null;
            _checkoutPanel = null;
            _promptText = null;
        }

        // =====================================================================
        //  UI helpers
        // =====================================================================

        private static ProductRow CreateProductRow(int index, Transform parent)
        {
            float yPos = 20f - index * 22f;

            var rowGo = new GameObject($"ProductRow_{index}");
            rowGo.transform.SetParent(parent, false);
            var rowRt = rowGo.AddComponent<RectTransform>();
            rowRt.anchorMin = new Vector2(0.5f, 0.5f);
            rowRt.anchorMax = new Vector2(0.5f, 0.5f);
            rowRt.pivot = new Vector2(0.5f, 0.5f);
            rowRt.sizeDelta = new Vector2(170f, 20f);
            rowRt.anchoredPosition = new Vector2(0f, yPos);

            // Icon on the left
            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(rowGo.transform, false);
            var iconRt = iconGo.AddComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0f, 0.5f);
            iconRt.anchorMax = new Vector2(0f, 0.5f);
            iconRt.pivot = new Vector2(0f, 0.5f);
            iconRt.sizeDelta = new Vector2(18f, 18f);
            iconRt.anchoredPosition = new Vector2(2f, 0f);
            var iconImg = iconGo.AddComponent<Image>();
            iconImg.color = new Color(0.4f, 0.4f, 0.4f, 0.5f);
            iconImg.raycastTarget = false;

            // Product name text
            var labelText = CreateText("Label", rowGo.transform,
                new Vector2(14f, 0f), new Vector2(145f, 20f),
                "", 10, TextAlignmentOptions.Left,
                new Color(0.9f, 0.9f, 0.9f));

            // Anchor label to the right of icon
            var labelRt = labelText.GetComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 0.5f);
            labelRt.anchorMax = new Vector2(0f, 0.5f);
            labelRt.pivot = new Vector2(0f, 0.5f);
            labelRt.anchoredPosition = new Vector2(24f, 0f);

            rowGo.SetActive(false);

            return new ProductRow
            {
                Root = rowGo,
                Icon = iconImg,
                Label = labelText
            };
        }

        private static TextMeshProUGUI CreateText(string name, Transform parent,
            Vector2 anchoredPos, Vector2 sizeDelta,
            string content, int fontSize, TextAlignmentOptions alignment, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = anchoredPos;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = content;
            tmp.fontSize = fontSize;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.raycastTarget = false;

            return tmp;
        }
    }
}
