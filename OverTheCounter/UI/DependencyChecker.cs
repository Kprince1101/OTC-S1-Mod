using System;
using OverTheCounter.Utilities;
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
    /// Detects missing OTC dependencies (S1API, S1MAPI, MeshVault) and shows a
    /// popup on the main menu when any are absent.
    /// CRITICAL: This class must have ZERO references to S1API, S1MAPI, or MeshVault
    /// types so it can be JIT-compiled even when those assemblies are missing.
    /// </summary>
    public static class DependencyChecker
    {
        /// <summary>Whether any required dependency is missing.</summary>
        public static bool HasMissingDeps { get; private set; }

        private struct DepResult
        {
            public string Name;
            public bool Found;
            public string Instruction;
        }

        private static DepResult[] _results;

        /// <summary>
        /// Scans loaded assemblies for required OTC dependencies and sets <see cref="HasMissingDeps"/>.
        /// Must be called before any S1API types are referenced.
        /// </summary>
        public static void RunChecks()
        {
            _results = new[]
            {
                Check("S1API Loader", "S1APILoader",
                    "Install S1API. The S1APILoader plugin goes in your Plugins folder."),
                Check("S1API", "S1API",
                    "Install S1API. The S1API mod file goes in your Mods folder.",
                    excludePrefix: "S1APILoader"),
                Check("S1MAPI", "S1MAPI",
                    "Install S1MAPI. Place the S1MAPI DLL in your UserLibs folder."),
                Check("MeshVault", "MeshVault",
                    "Install MeshVault. The MeshVault plugin goes in your Plugins folder."),
            };

            HasMissingDeps = Array.Exists(_results, r => !r.Found);

            if (HasMissingDeps)
            {
                OTCLog.Warning(OTCLog.Systems.General, "=== Missing Dependencies Detected ===");
                foreach (var r in _results)
                {
                    if (!r.Found)
                        OTCLog.Warning(OTCLog.Systems.General, $"  MISSING: {r.Name}");
                }
                OTCLog.Warning(OTCLog.Systems.General, "OTC features are disabled until all dependencies are installed.");
            }
        }

        private static DepResult Check(string name, string assemblyPrefix, string instruction,
            string excludePrefix = null)
        {
            bool found = false;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name;
                if (asmName.StartsWith(assemblyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (excludePrefix != null &&
                        asmName.StartsWith(excludePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    found = true;
                    break;
                }
            }
            return new DepResult { Name = name, Found = found, Instruction = instruction };
        }

        /// <summary>
        /// Creates a standalone overlay popup listing missing dependencies.
        /// Safe to call multiple times — skips if already visible.
        /// </summary>
        public static void ShowPopup()
        {
            if (_results == null) return;
            if (GameObject.Find("OTC_DepCheckCanvas") != null) return;

            // Canvas — ScreenSpaceOverlay at highest sort order
            var canvasGO = new GameObject("OTC_DepCheckCanvas");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9999;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGO.AddComponent<GameCanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();

            // Dark backdrop — blocks interaction with main menu
            var backdropGO = new GameObject("Backdrop");
            backdropGO.transform.SetParent(canvasGO.transform, false);
            var backdropRT = backdropGO.AddComponent<RectTransform>();
            backdropRT.anchorMin = Vector2.zero;
            backdropRT.anchorMax = Vector2.one;
            backdropRT.offsetMin = Vector2.zero;
            backdropRT.offsetMax = Vector2.zero;
            var backdropImg = backdropGO.AddComponent<Image>();
            backdropImg.color = new Color(0f, 0f, 0f, 0.75f);

            // Center panel
            var panelGO = new GameObject("Panel");
            panelGO.transform.SetParent(canvasGO.transform, false);
            var panelRT = panelGO.AddComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(720, 480);
            var panelImg = panelGO.AddComponent<Image>();
            panelImg.color = new Color(0.1f, 0.1f, 0.12f, 0.97f);
            panelImg.sprite = TMPFactory.GetRoundedSprite();
            panelImg.type = Image.Type.Sliced;

            // VerticalLayoutGroup for content flow
            var vlg = panelGO.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(32, 32, 24, 24);
            vlg.spacing = 8;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperCenter;

            // Title
            AddText(panelGO.transform, "Title",
                "OverTheCounter - Missing Dependencies",
                24, FontStyles.Bold, Color.white, 40);

            // Subtitle
            AddText(panelGO.transform, "Subtitle",
                "OTC requires these mods to function.\nInstall the missing mods and restart the game.",
                16, FontStyles.Normal, new Color(0.75f, 0.75f, 0.75f), 50, wrap: true);

            // Spacer
            AddSpacer(panelGO.transform, 8);

            // Dependency status lines
            foreach (var dep in _results)
            {
                if (dep.Found)
                {
                    AddText(panelGO.transform, dep.Name,
                        dep.Name + "  -  Installed",
                        18, FontStyles.Normal, new Color(0.3f, 0.9f, 0.3f), 30);
                }
                else
                {
                    AddText(panelGO.transform, dep.Name,
                        dep.Name + "  -  NOT FOUND",
                        18, FontStyles.Bold, new Color(1f, 0.4f, 0.4f), 30);

                    AddText(panelGO.transform, dep.Name + "_Inst",
                        "     " + dep.Instruction,
                        15, FontStyles.Normal, new Color(0.6f, 0.6f, 0.6f), 24, wrap: true);
                }
            }

            // Spacer before button
            AddSpacer(panelGO.transform, 12);

            // Dismiss button
            var (btnGO, btn, _) = TMPFactory.RoundedButtonWithLabel(
                "Dismiss", "Dismiss", panelGO.transform,
                new Color(0.25f, 0.25f, 0.3f), 160, 38, 16, Color.white);
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
            {
                UnityEngine.Object.Destroy(canvasGO);
            }));
        }

        private static void AddText(Transform parent, string name, string content,
            int fontSize, FontStyles style, Color color, float height, bool wrap = false)
        {
            // Container for layout sizing
            var container = new GameObject(name + "_Container");
            container.transform.SetParent(parent, false);
            var containerRT = container.AddComponent<RectTransform>();
            var le = container.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1;

            var tmp = TMPFactory.Text(name, content, container.transform, fontSize,
                TextAlignmentOptions.Left, style);
            tmp.color = color;
            if (wrap) TMPFactory.SetWrapping(tmp, true);
        }

        private static void AddSpacer(Transform parent, float height)
        {
            var spacer = new GameObject("Spacer");
            spacer.transform.SetParent(parent, false);
            spacer.AddComponent<RectTransform>();
            var le = spacer.AddComponent<LayoutElement>();
            le.preferredHeight = height;
        }
    }
}
