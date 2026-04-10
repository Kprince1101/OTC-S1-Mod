using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppTMPro;
using GameCanvasScaler = Il2CppScheduleOne.UI.CanvasScaler;
#else
using TMPro;
using GameCanvasScaler = ScheduleOne.UI.CanvasScaler;
#endif

namespace OverTheCounter.UI
{
    /// <summary>
    /// Modal popup for renaming the dispensary. Shows a text input with Confirm/Cancel buttons.
    /// </summary>
    public static class RenamePopup
    {
        private static GameObject _canvas;
        private static Action _onRenamed;

        public static void Show(string currentName, Action onRenamed = null)
        {
            if (_canvas != null) return; // already open
            _onRenamed = onRenamed;

            // Canvas — ScreenSpaceOverlay above everything
            var canvasGO = new GameObject("OTC_RenamePopup");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGO.AddComponent<GameCanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();
            _canvas = canvasGO;

            // Dark backdrop (blocks clicks)
            var backdrop = new GameObject("Backdrop");
            backdrop.transform.SetParent(canvasGO.transform, false);
            var bdRT = backdrop.AddComponent<RectTransform>();
            bdRT.anchorMin = Vector2.zero;
            bdRT.anchorMax = Vector2.one;
            bdRT.offsetMin = Vector2.zero;
            bdRT.offsetMax = Vector2.zero;
            var bdImg = backdrop.AddComponent<Image>();
            bdImg.color = new Color(0f, 0f, 0f, 0.6f);

            // Center panel
            var panel = new GameObject("Panel");
            panel.transform.SetParent(canvasGO.transform, false);
            var panelRT = panel.AddComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(420, 200);
            var panelImg = panel.AddComponent<Image>();
            panelImg.color = new Color(0.1f, 0.1f, 0.12f, 0.97f);
            panelImg.sprite = TMPFactory.GetRoundedSprite();
            panelImg.type = Image.Type.Sliced;

            // Title
            var titleTmp = TMPFactory.Text("Title", "Rename Dispensary", panel.transform,
                20, TextAlignmentOptions.Center, FontStyles.Bold);
            titleTmp.color = Color.white;
            var titleRT = titleTmp.GetComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0f, 0.75f);
            titleRT.anchorMax = new Vector2(1f, 1f);
            titleRT.offsetMin = new Vector2(16f, 0f);
            titleRT.offsetMax = new Vector2(-16f, -8f);

            // Input field background
            var inputBg = new GameObject("InputBg");
            inputBg.transform.SetParent(panel.transform, false);
            var inputRT = inputBg.AddComponent<RectTransform>();
            inputRT.anchorMin = new Vector2(0.05f, 0.4f);
            inputRT.anchorMax = new Vector2(0.95f, 0.7f);
            inputRT.offsetMin = Vector2.zero;
            inputRT.offsetMax = Vector2.zero;
            var inputImg = inputBg.AddComponent<Image>();
            inputImg.color = new Color(0.18f, 0.18f, 0.2f);
            inputImg.sprite = TMPFactory.GetRoundedSprite();
            inputImg.type = Image.Type.Sliced;

            // Viewport
            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(inputBg.transform, false);
            var vpRT = viewport.AddComponent<RectTransform>();
            vpRT.anchorMin = Vector2.zero;
            vpRT.anchorMax = Vector2.one;
            vpRT.offsetMin = new Vector2(8f, 2f);
            vpRT.offsetMax = new Vector2(-8f, -2f);
            viewport.AddComponent<RectMask2D>();

            // Text component
            var inputText = TMPFactory.Text("Text", "", viewport.transform,
                17, TextAlignmentOptions.Left, FontStyles.Normal);
            inputText.color = Color.white;
            var itRect = inputText.GetComponent<RectTransform>();
            itRect.anchorMin = Vector2.zero;
            itRect.anchorMax = Vector2.one;
            itRect.offsetMin = Vector2.zero;
            itRect.offsetMax = Vector2.zero;

            // Placeholder
            var placeholder = TMPFactory.Text("Placeholder", "Enter name...", viewport.transform,
                17, TextAlignmentOptions.Left, FontStyles.Italic);
            placeholder.color = new Color(0.5f, 0.5f, 0.5f);
            var phRect = placeholder.GetComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = Vector2.zero;
            phRect.offsetMax = Vector2.zero;

            // TMP_InputField
            var inputField = inputBg.AddComponent<TMP_InputField>();
            inputField.textViewport = vpRT;
            inputField.textComponent = inputText;
            inputField.placeholder = placeholder;
            inputField.fontAsset = inputText.font;
            inputField.pointSize = 17;
            inputField.characterLimit = 20;
            inputField.contentType = TMP_InputField.ContentType.Standard;
            inputField.text = currentName ?? "";

            // Lock game input while typing
            inputField.onSelect.AddListener(new Action<string>(_ =>
            {
#if IL2CPP
                Il2CppScheduleOne.GameInput.IsTyping = true;
#else
                ScheduleOne.GameInput.IsTyping = true;
#endif
            }));

            inputField.onDeselect.AddListener(new Action<string>(_ =>
            {
#if IL2CPP
                Il2CppScheduleOne.GameInput.IsTyping = false;
#else
                ScheduleOne.GameInput.IsTyping = false;
#endif
            }));

            // Button row
            var btnRow = new GameObject("ButtonRow");
            btnRow.transform.SetParent(panel.transform, false);
            var btnRowRT = btnRow.AddComponent<RectTransform>();
            btnRowRT.anchorMin = new Vector2(0.05f, 0.05f);
            btnRowRT.anchorMax = new Vector2(0.95f, 0.35f);
            btnRowRT.offsetMin = Vector2.zero;
            btnRowRT.offsetMax = Vector2.zero;
            var hlg = btnRow.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 12;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleCenter;

            // Cancel button
            CreateButton(btnRow.transform, "Cancel", new Color(0.3f, 0.3f, 0.32f), () =>
            {
                Close();
            });

            // Confirm button
            CreateButton(btnRow.transform, "Confirm", new Color(0.15f, 0.55f, 0.3f), () =>
            {
                string name = inputField.text?.Trim();
                if (string.IsNullOrEmpty(name)) name = "Dispensary";
                if (name.Length > 20) name = name.Substring(0, 20);

                if (PropertySaveData.Instance != null)
                    PropertySaveData.Instance.DispensaryDisplayName = name;
                Dispensary.UpdateSignText(name);
                if (NetworkHelper.IsHost)
                    ConfigSyncData.MarkGameStateDirty();
                else
                    ConfigSyncData.SendQuestAction($"DISP_RENAME:{name}");

                _onRenamed?.Invoke();
                Close();
            });
        }

        private static void Close()
        {
#if IL2CPP
            Il2CppScheduleOne.GameInput.IsTyping = false;
#else
            ScheduleOne.GameInput.IsTyping = false;
#endif
            if (_canvas != null)
            {
                GameObject.Destroy(_canvas);
                _canvas = null;
            }
        }

        private static void CreateButton(Transform parent, string label, Color bgColor, Action onClick)
        {
            var btnGO = new GameObject($"Btn_{label}");
            btnGO.transform.SetParent(parent, false);
            var btnImg = btnGO.AddComponent<Image>();
            btnImg.color = bgColor;
            btnImg.sprite = TMPFactory.GetRoundedSprite();
            btnImg.type = Image.Type.Sliced;
            var btn = btnGO.AddComponent<Button>();
            btn.targetGraphic = btnImg;
            btn.onClick.AddListener(new Action(onClick));

            var txt = TMPFactory.Text($"Label_{label}", label, btnGO.transform,
                16, TextAlignmentOptions.Center, FontStyles.Bold);
            txt.color = Color.white;
            var txtRT = txt.GetComponent<RectTransform>();
            txtRT.anchorMin = Vector2.zero;
            txtRT.anchorMax = Vector2.one;
            txtRT.offsetMin = Vector2.zero;
            txtRT.offsetMax = Vector2.zero;
        }
    }
}
