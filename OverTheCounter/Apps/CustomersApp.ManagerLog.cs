using S1API.UI;
using System;
using UnityEngine;
using UnityEngine.UI;
using OverTheCounter.Logic;

namespace OverTheCounter.Apps
{
    public partial class CustomersApp
    {
        private void BuildManagerLogPage(ManagerInstance mgr)
        {
            // Hide list pages
            _managersPage.SetActive(false);
            _customersPage.SetActive(false);

            if (_managerLogPage != null)
                UnityEngine.Object.Destroy(_managerLogPage);

            _managerLogPage = UIFactory.Panel("ManagerLogPage", _rootPanel.transform, new Color(0.12f, 0.12f, 0.12f));
            var pageRect = _managerLogPage.GetComponent<RectTransform>();
            pageRect.anchorMin = Vector2.zero;
            pageRect.anchorMax = Vector2.one;
            pageRect.offsetMin = Vector2.zero;
            pageRect.offsetMax = new Vector2(0, -HEADER_HEIGHT);

            // ── Back button bar ──
            var backBar = UIFactory.Panel("BackBar", _managerLogPage.transform, new Color(0.15f, 0.15f, 0.15f));
            var backBarRect = backBar.GetComponent<RectTransform>();
            backBarRect.anchorMin = new Vector2(0, 1);
            backBarRect.anchorMax = new Vector2(1, 1);
            backBarRect.pivot = new Vector2(0.5f, 1);
            backBarRect.anchoredPosition = Vector2.zero;
            backBarRect.sizeDelta = new Vector2(0, 32);

            var backBtn = backBar.AddComponent<Button>();
            backBtn.onClick.AddListener(new Action(CloseManagerLog));

            var backText = UIFactory.Text("BackLabel", "\u25C0  Back", backBar.transform, 16, TextAnchor.MiddleLeft); // ◀ left triangle
            backText.color = new Color(0.7f, 0.7f, 0.7f);
            var backTextRect = backText.gameObject.GetComponent<RectTransform>();
            backTextRect.anchorMin = Vector2.zero;
            backTextRect.anchorMax = Vector2.one;
            backTextRect.offsetMin = new Vector2(12, 0);
            backTextRect.offsetMax = Vector2.zero;

            // ── Header ──
            string firstName = "Manager";
            try { firstName = mgr.GameNpc?.FirstName ?? "Manager"; } catch { }

            var header = UIFactory.Text("LogHeader", $"<b>Debug Log - {firstName}</b>", _managerLogPage.transform, 16, TextAnchor.MiddleLeft);
            header.color = new Color(0.7f, 0.7f, 0.7f);
            var headerRect = header.gameObject.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0, 1);
            headerRect.anchoredPosition = new Vector2(12, -36);
            headerRect.sizeDelta = new Vector2(-24, 28);

            // ── Console container ──
            var console = UIFactory.Panel("Console", _managerLogPage.transform, new Color(0.08f, 0.08f, 0.08f));
            var consoleRect = console.GetComponent<RectTransform>();
            consoleRect.anchorMin = Vector2.zero;
            consoleRect.anchorMax = Vector2.one;
            consoleRect.offsetMin = new Vector2(8, 8);
            consoleRect.offsetMax = new Vector2(-8, -68);

            console.AddComponent<RectMask2D>();

            // ScrollRect
            var scrollObj = UIFactory.Panel("Scroll", console.transform, Color.clear);
            var scrollObjRect = scrollObj.GetComponent<RectTransform>();
            scrollObjRect.anchorMin = Vector2.zero;
            scrollObjRect.anchorMax = Vector2.one;
            scrollObjRect.offsetMin = Vector2.zero;
            scrollObjRect.offsetMax = Vector2.zero;

            _logScrollRect = scrollObj.AddComponent<ScrollRect>();
            _logScrollRect.horizontal = false;
            _logScrollRect.scrollSensitivity = 20f;

            // Content
            var content = new GameObject("Content");
            content.transform.SetParent(scrollObj.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0, 1);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0, 0);

            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;

            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            _logScrollRect.content = contentRect;

            // Log text
            _logText = UIFactory.Text("LogText", "", content.transform, 11, TextAnchor.UpperLeft);
            _logText.color = new Color(0.45f, 0.85f, 0.45f);
            _logText.supportRichText = false;
            _logText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _logText.verticalOverflow = VerticalWrapMode.Overflow;
            var logTextRect = _logText.gameObject.GetComponent<RectTransform>();
            logTextRect.anchorMin = Vector2.zero;
            logTextRect.anchorMax = new Vector2(1, 1);
            logTextRect.offsetMin = new Vector2(6, 4);
            logTextRect.offsetMax = new Vector2(-6, -4);

            _logPageManager = mgr;
            RefreshLogContent();
        }

        private void RefreshLogContent()
        {
            if (_logText == null || _logPageManager == null) return;

            var buffer = _logPageManager.LogBuffer;
            if (buffer.Count == 0)
            {
                _logText.text = "No log entries yet.";
                _logText.fontStyle = FontStyle.Italic;
                _logText.color = new Color(0.5f, 0.5f, 0.5f);
            }
            else
            {
                _logText.fontStyle = FontStyle.Normal;
                _logText.color = new Color(0.45f, 0.85f, 0.45f);
                _logText.text = string.Join("\n", buffer);
            }

            // Auto-scroll to bottom
            if (_logScrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                _logScrollRect.verticalNormalizedPosition = 0f;
            }
        }
    }
}
