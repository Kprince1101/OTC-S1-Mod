using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.Logic.Placement;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.GameTime;
using S1API.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using Il2CppTMPro;
using GameCanvasScaler = Il2CppScheduleOne.UI.CanvasScaler;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI;
using TMPro;
using GameCanvasScaler = ScheduleOne.UI.CanvasScaler;
#endif

namespace OverTheCounter.UI
{
    /// <summary>
    /// HUD overlay showing checkout queue alerts per building on the right side of the screen.
    /// </summary>
    [RegisterTypeInIl2Cpp]
    public class StoreAlertOverlay : MonoBehaviour
    {
        /// <summary>Set true to hide the overlay (e.g. during checkout camera lock).</summary>
        public static bool Suppressed;

        // Deterministic building order for stable hash + consistent UI ordering
        private static readonly string[] BuildingIds = { PropertySaveData.ShackId, PropertySaveData.DispensaryId };

        private GameObject _canvasObj;
        private GameObject _panelObj;
        private bool _built;

        // Per-building UI elements (ordered by BuildingIds)
        private readonly List<BuildingBlock> _blocks = new();

        // Alert data gathered each tick
        private readonly Dictionary<string, AlertData> _alerts = new();
        private int _lastHash;

        private Sprite _alertIcon;
        private bool _iconLoaded;

        private struct AlertData
        {
            public string DisplayName;
            public int QueueCount;
            public int MinutesRemaining;
        }

        private class BuildingBlock
        {
            public string BuildingId;
            public GameObject Root;
            public TextMeshProUGUI NameLabel;
            public TextMeshProUGUI DetailLabel;
        }

        /// <summary>Registers the type for IL2CPP class injection.</summary>
        public static void Register()
        {
#if IL2CPP
            ClassInjector.RegisterTypeInIl2Cpp<StoreAlertOverlay>();
#endif
        }

        private void Update()
        {
            try
            {
                if (Player.Local == null) return;

                if (!_built)
                    TryBuild();
                if (!_built) return;

                // Hide when paused
                if (Singleton<PauseMenu>.InstanceExists && Singleton<PauseMenu>.Instance.IsPaused)
                {
                    if (_canvasObj.activeSelf) _canvasObj.SetActive(false);
                    return;
                }

                // Hide when suppressed (checkout) or disabled
                if (Suppressed || !Config.StoreAlertEnabled.Value)
                {
                    if (_canvasObj.activeSelf) _canvasObj.SetActive(false);
                    return;
                }

                GatherAlerts();

                int hash = ComputeHash();
                if (hash != _lastHash)
                {
                    _lastHash = hash;
                    UpdateBlocks();
                }

                // Hide entire overlay when nothing to show
                bool hasAlerts = _alerts.Count > 0;
                if (_canvasObj.activeSelf != hasAlerts)
                    _canvasObj.SetActive(hasAlerts);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"StoreAlertOverlay.Update: {ex.Message}");
            }
        }

        private void TryBuild()
        {
            try
            {
                _canvasObj = new GameObject("OTC_StoreAlertCanvas");
                _canvasObj.transform.SetParent(transform, false);

                var canvas = _canvasObj.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 1;

                var scaler = _canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;

                _canvasObj.AddComponent<GameCanvasScaler>();
                _canvasObj.AddComponent<GraphicRaycaster>();

                // Panel: top-right, below minimap area
                _panelObj = new GameObject("AlertPanel");
                _panelObj.transform.SetParent(_canvasObj.transform, false);

                var panelRect = _panelObj.AddComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(1f, 1f);
                panelRect.anchorMax = new Vector2(1f, 1f);
                panelRect.pivot = new Vector2(1f, 1f);
                panelRect.anchoredPosition = new Vector2(-20f, -350f);
                panelRect.sizeDelta = new Vector2(240f, 0f); // height set dynamically

                var panelImg = _panelObj.AddComponent<Image>();
                panelImg.color = new Color(0.1f, 0.1f, 0.1f, 0.75f);
                panelImg.raycastTarget = false;

                var vlg = _panelObj.AddComponent<VerticalLayoutGroup>();
                vlg.padding = new RectOffset(10, 10, 8, 8);
                vlg.spacing = 6f;
                vlg.childForceExpandWidth = true;
                vlg.childForceExpandHeight = false;
                vlg.childControlWidth = true;
                vlg.childControlHeight = true;

                var csf = _panelObj.AddComponent<ContentSizeFitter>();
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

                _canvasObj.SetActive(false); // start hidden until alerts exist
                _built = true;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StoreAlertOverlay.TryBuild: {ex.Message}");
            }
        }

        // ── Data Gathering ──

        private void GatherAlerts()
        {
            _alerts.Clear();

            int currentTime = TimeManager.CurrentTime;
            int currentMins = (currentTime / 100) * 60 + (currentTime % 100);

            foreach (var counter in CheckoutCounter.AllCounters)
            {
                if (counter.Queue.Count == 0 || counter.IsStaffed) continue;

                string bId = counter.BuildingId;
                if (string.IsNullOrEmpty(bId)) continue;

                if (!_alerts.TryGetValue(bId, out var existing))
                {
                    existing = new AlertData
                    {
                        DisplayName = GetBuildingDisplayName(bId),
                        QueueCount = 0,
                        MinutesRemaining = 240
                    };
                }

                existing.QueueCount += counter.Queue.Count;

                // Track nearest deadline from front customer
                string frontId = counter.Queue[0];
                if (CustomerInstance.Active.TryGetValue(frontId, out var frontCustomer))
                {
                    int startTime = frontCustomer.CheckoutStartTime;
                    if (startTime > 0)
                    {
                        int startMins = (startTime / 100) * 60 + (startTime % 100);
                        int remaining = 240 - (currentMins - startMins);
                        if (remaining < 0) remaining += 1440; // midnight wrap
                        if (remaining > 240) remaining = 0;   // past deadline
                        if (remaining < existing.MinutesRemaining)
                            existing.MinutesRemaining = remaining;
                    }
                }

                _alerts[bId] = existing;
            }
        }

        /// <summary>Deterministic hash over alert data using fixed building order.</summary>
        private int ComputeHash()
        {
            int h = _alerts.Count;
            for (int i = 0; i < BuildingIds.Length; i++)
            {
                if (_alerts.TryGetValue(BuildingIds[i], out var data))
                {
                    h = h * 31 + i;
                    h = h * 31 + data.QueueCount;
                    h = h * 31 + data.MinutesRemaining;
                }
            }
            return h;
        }

        // ── UI Blocks ──

        /// <summary>Updates building blocks in-place when possible, only creates/destroys when set changes.</summary>
        private void UpdateBlocks()
        {
            // Build ordered list of active building IDs
            int alertIdx = 0;
            for (int i = 0; i < BuildingIds.Length; i++)
            {
                string bId = BuildingIds[i];
                if (!_alerts.TryGetValue(bId, out var data)) continue;

                if (alertIdx < _blocks.Count && _blocks[alertIdx].BuildingId == bId)
                {
                    // Same building in same slot, update text in-place
                    UpdateBlockText(_blocks[alertIdx].DetailLabel, data);
                }
                else
                {
                    // Building set changed, rebuild from this point
                    DestroyBlocksFrom(alertIdx);
                    for (int j = i; j < BuildingIds.Length; j++)
                    {
                        if (_alerts.TryGetValue(BuildingIds[j], out var d))
                            _blocks.Add(CreateBlock(BuildingIds[j], d));
                    }
                    return;
                }
                alertIdx++;
            }

            // Remove trailing blocks that are no longer needed
            DestroyBlocksFrom(alertIdx);
        }

        private void DestroyBlocksFrom(int startIdx)
        {
            for (int i = _blocks.Count - 1; i >= startIdx; i--)
            {
                if (_blocks[i].Root != null)
                    Destroy(_blocks[i].Root);
                _blocks.RemoveAt(i);
            }
        }

        private BuildingBlock CreateBlock(string buildingId, AlertData data)
        {
            var root = new GameObject("AlertBlock_" + buildingId);
            root.transform.SetParent(_panelObj.transform, false);

            root.AddComponent<RectTransform>();
            var rootLayout = root.AddComponent<VerticalLayoutGroup>();
            rootLayout.spacing = 2f;
            rootLayout.childForceExpandWidth = true;
            rootLayout.childForceExpandHeight = false;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = true;

            // Header row: icon + building name
            var headerObj = new GameObject("Header");
            headerObj.transform.SetParent(root.transform, false);
            headerObj.AddComponent<RectTransform>();
            var headerLayout = headerObj.AddComponent<HorizontalLayoutGroup>();
            headerLayout.spacing = 6f;
            headerLayout.childForceExpandWidth = false;
            headerLayout.childForceExpandHeight = false;
            headerLayout.childControlWidth = true;
            headerLayout.childControlHeight = true;
            headerLayout.childAlignment = TextAnchor.MiddleLeft;

            // Icon
            var iconObj = new GameObject("Icon");
            iconObj.transform.SetParent(headerObj.transform, false);
            var iconRect = iconObj.AddComponent<RectTransform>();
            iconRect.sizeDelta = new Vector2(24f, 24f);
            var iconImg = iconObj.AddComponent<Image>();
            iconImg.sprite = GetAlertIcon();
            iconImg.raycastTarget = false;
            var iconLE = iconObj.AddComponent<LayoutElement>();
            iconLE.preferredWidth = 24f;
            iconLE.preferredHeight = 24f;

            // Building name — needs LayoutElement so HorizontalLayoutGroup gives it space
            var nameLabel = TMPFactory.Text("Name", data.DisplayName, headerObj.transform,
                16, TextAlignmentOptions.Left, FontStyles.Bold);
            nameLabel.raycastTarget = false;
            nameLabel.overflowMode = TextOverflowModes.Ellipsis;
            TMPFactory.SetWrapping(nameLabel, false);
            var nameLE = nameLabel.gameObject.AddComponent<LayoutElement>();
            nameLE.flexibleWidth = 1f;
            nameLE.preferredHeight = 24f;

            // Detail row
            var detailLabel = TMPFactory.Text("Detail", "", root.transform, 15);
            detailLabel.raycastTarget = false;
            detailLabel.margin = new Vector4(30f, 0f, 0f, 0f); // indent past icon
            TMPFactory.SetWrapping(detailLabel, false);
            UpdateBlockText(detailLabel, data);

            return new BuildingBlock
            {
                BuildingId = buildingId,
                Root = root,
                NameLabel = nameLabel,
                DetailLabel = detailLabel
            };
        }

        private static void UpdateBlockText(TextMeshProUGUI label, AlertData data)
        {
            int hours = data.MinutesRemaining / 60;
            int mins = data.MinutesRemaining % 60;
            string timeStr = hours > 0 ? $"{hours}h {mins}m" : $"{mins}m";
            string colorTag = data.MinutesRemaining < 120 ? "#ff6b6b" : "#66bb6a";
            label.text = $"\u2022 {data.QueueCount} waiting  <color={colorTag}>({timeStr})</color>";
        }

        private static string GetBuildingDisplayName(string buildingId)
        {
            if (buildingId == PropertySaveData.ShackId) return "Westville Shack";
            if (buildingId == PropertySaveData.DispensaryId) return "Big Dispensary";
            return buildingId;
        }

        private Sprite GetAlertIcon()
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                if (Core.OtcIconDir != null)
                {
                    string path = Path.Combine(Core.OtcIconDir, "StoreAlertIcon.png");
                    _alertIcon = ImageUtils.LoadImage(path);
                }
            }
            return _alertIcon;
        }

    }
}
