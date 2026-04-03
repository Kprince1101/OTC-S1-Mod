using System;
using System.IO;
using System.Reflection;
using MelonLoader.Utils;
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
    /// popup on the main menu when any are absent, outdated, or misplaced.
    /// CRITICAL: This class must have ZERO references to S1API, S1MAPI, or MeshVault
    /// types so it can be JIT-compiled even when those assemblies are missing.
    /// </summary>
    public static class DependencyChecker
    {
        /// <summary>Whether any required startup dependency is missing or outdated.</summary>
        public static bool HasMissingDeps { get; private set; }

        /// <summary>Whether multiplayer dependencies are missing, outdated, or misplaced.</summary>
        public static bool HasMultiplayerIssues { get; private set; }

        private struct DepResult
        {
            public string Name;
            public bool Found;
            public string Instruction;
            public string Version;
            public string MinVersion;
            public bool VersionOk;
            public string MisplacedPath;
            public string ExpectedFolder;
        }

        private static DepResult[] _results;
        private static DepResult[] _multiplayerResults;

        /// <summary>
        /// Scans loaded assemblies for required OTC dependencies and sets <see cref="HasMissingDeps"/>.
        /// Detects misplaced DLLs and outdated versions.
        /// Must be called before any S1API types are referenced.
        /// </summary>
        public static void RunChecks()
        {
            var modsDir = MelonEnvironment.ModsDirectory;
            var profileDir = Path.GetDirectoryName(modsDir);
            var pluginsDir = Path.Combine(profileDir, "Plugins");
            var userLibsDir = Path.Combine(profileDir, "UserLibs");
            var folders = new[] { modsDir, pluginsDir, userLibsDir };

            _results = new[]
            {
                Check("S1API Loader", "S1APILoader",
                    "Install S1API. The S1APILoader plugin goes in your Plugins folder.",
                    expectedFolder: "Plugins", folderPaths: folders),
                Check("S1API", "S1API",
                    "Install S1API. The S1API mod file goes in your Mods folder.",
                    excludePrefix: "S1APILoader", minVersion: "3.0.0",
                    expectedFolder: "Mods", folderPaths: folders),
                Check("S1MAPI", "S1MAPI",
                    "Install S1MAPI. Place the S1MAPI DLL in your UserLibs folder.",
                    minVersion: "2.0.0",
                    expectedFolder: "UserLibs", folderPaths: folders),
                Check("MeshVault", "MeshVault",
                    "Install MeshVault. The MeshVault plugin goes in your Plugins folder.",
                    minVersion: "1.0.8",
                    expectedFolder: "Plugins", folderPaths: folders),
            };

            HasMissingDeps = Array.Exists(_results, r => !r.Found || !r.VersionOk);

            if (!HasMissingDeps) return;

            OTCLog.Warning(OTCLog.Systems.General, "=== Missing/Outdated Dependencies Detected ===");
            foreach (var r in _results)
            {
                if (!r.Found && r.MisplacedPath != null)
                {
                    var fileName = Path.GetFileName(r.MisplacedPath);
                    var wrongFolder = Path.GetFileName(Path.GetDirectoryName(r.MisplacedPath));
                    OTCLog.Warning(OTCLog.Systems.General,
                        $"  MISPLACED: {r.Name} — found {fileName} in {wrongFolder}, expected {r.ExpectedFolder}");
                }
                else if (!r.Found)
                {
                    OTCLog.Warning(OTCLog.Systems.General, $"  MISSING: {r.Name}");
                }
                else if (!r.VersionOk)
                {
                    OTCLog.Warning(OTCLog.Systems.General,
                        $"  OUTDATED: {r.Name} (v{r.Version}, requires v{r.MinVersion})");
                }
            }
            OTCLog.Warning(OTCLog.Systems.General,
                "OTC features are disabled until all dependencies are installed/updated.");
        }

        /// <summary>
        /// Checks multiplayer-specific dependencies (SteamNetworkLib).
        /// Called at runtime when a second player joins the lobby.
        /// </summary>
        public static void RunMultiplayerChecks()
        {
            var modsDir = MelonEnvironment.ModsDirectory;
            var profileDir = Path.GetDirectoryName(modsDir);
            var pluginsDir = Path.Combine(profileDir, "Plugins");
            var userLibsDir = Path.Combine(profileDir, "UserLibs");
            var folders = new[] { modsDir, pluginsDir, userLibsDir };

            _multiplayerResults = new[]
            {
                Check("SteamNetworkLib", "SteamNetworkLib",
                    "Install SteamNetworkLib. Place the DLL in your UserLibs folder.",
                    minVersion: "1.2.1",
                    expectedFolder: "UserLibs", folderPaths: folders),
            };

            HasMultiplayerIssues = Array.Exists(_multiplayerResults, r => !r.Found || !r.VersionOk);

            if (!HasMultiplayerIssues) return;

            OTCLog.Warning(OTCLog.Systems.Network, "=== Multiplayer Dependency Issue ===");
            foreach (var r in _multiplayerResults)
            {
                if (!r.Found && r.MisplacedPath != null)
                {
                    var fileName = Path.GetFileName(r.MisplacedPath);
                    var wrongFolder = Path.GetFileName(Path.GetDirectoryName(r.MisplacedPath));
                    OTCLog.Warning(OTCLog.Systems.Network,
                        $"  MISPLACED: {r.Name}, found {fileName} in {wrongFolder}, expected {r.ExpectedFolder}");
                }
                else if (!r.Found)
                {
                    OTCLog.Warning(OTCLog.Systems.Network, $"  MISSING: {r.Name}");
                }
                else if (!r.VersionOk)
                {
                    OTCLog.Warning(OTCLog.Systems.Network,
                        $"  OUTDATED: {r.Name} (v{r.Version}, requires v{r.MinVersion})");
                }
            }
            OTCLog.Warning(OTCLog.Systems.Network,
                "OTC multiplayer sync is disabled. Install SteamNetworkLib and restart.");
        }

        private static DepResult Check(string name, string assemblyPrefix, string instruction,
            string excludePrefix = null, string minVersion = null,
            string expectedFolder = null, string[] folderPaths = null)
        {
            var result = new DepResult
            {
                Name = name,
                Instruction = instruction,
                MinVersion = minVersion,
                ExpectedFolder = expectedFolder,
                VersionOk = true
            };

            // Check loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name;
                if (!asmName.StartsWith(assemblyPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (excludePrefix != null &&
                    asmName.StartsWith(excludePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Found = true;
                result.Version = GetBestVersion(asm);

                // Verify loaded from expected folder
                if (expectedFolder != null)
                {
                    try
                    {
                        var loc = asm.Location;
                        if (!string.IsNullOrEmpty(loc))
                        {
                            var loadedFolder = Path.GetFileName(Path.GetDirectoryName(loc));
                            if (!string.Equals(loadedFolder, expectedFolder,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                result.MisplacedPath = loc;
                                result.Found = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OTCLog.Warning(OTCLog.Systems.General,
                            $"Could not read assembly location for {name}: {ex.Message}");
                    }
                }

                break;
            }

            // Version check
            if (result.Found && minVersion != null && result.Version != null)
            {
                try
                {
                    result.VersionOk = new Version(result.Version) >= new Version(minVersion);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.General,
                        $"Version parse failed for {name}: {ex.Message}");
                }
            }

            // Filesystem scan for misplaced DLLs (only when not found and not already detected)
            if (!result.Found && result.MisplacedPath == null &&
                folderPaths != null && expectedFolder != null)
                result.MisplacedPath = ScanForMisplacedDll(assemblyPrefix, expectedFolder,
                    folderPaths, excludePrefix);

            return result;
        }

        /// <summary>
        /// Returns the best available version string for an assembly.
        /// Prefers AssemblyInformationalVersion (matches mod managers) over AssemblyVersion
        /// which many mods leave at 1.0.0.0.
        /// </summary>
        private static string GetBestVersion(System.Reflection.Assembly asm)
        {
            // Read MelonInfoAttribute by metadata — avoids type matching issues
            // across different MelonLoader versions at compile vs runtime
            try
            {
                foreach (var data in CustomAttributeData.GetCustomAttributes(asm))
                {
                    if (data.AttributeType.Name != "MelonInfoAttribute") continue;
                    // Constructor: (Type systemType, string name, string version, ...)
                    if (data.ConstructorArguments.Count >= 3)
                    {
                        var verStr = data.ConstructorArguments[2].Value as string;
                        if (verStr != null && Version.TryParse(verStr, out _))
                            return verStr;
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.General,
                    $"MelonInfo version read failed for {asm.GetName().Name}: {ex.Message}");
            }

            // Try informational version (set by some csproj configurations)
            try
            {
                foreach (var data in CustomAttributeData.GetCustomAttributes(asm))
                {
                    if (data.AttributeType.Name != "AssemblyInformationalVersionAttribute") continue;
                    if (data.ConstructorArguments.Count >= 1)
                    {
                        var raw = data.ConstructorArguments[0].Value as string;
                        if (raw != null)
                        {
                            var idx = raw.IndexOfAny(new[] { '+', '-' });
                            if (idx >= 0) raw = raw.Substring(0, idx);
                            if (Version.TryParse(raw, out _))
                                return raw;
                        }
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.General,
                    $"Informational version read failed for {asm.GetName().Name}: {ex.Message}");
            }

            // Fall back to assembly version (often 1.0.0.0 for ML mods)
            var ver = asm.GetName().Version;
            if (ver == null) return null;
            return ver.Revision > 0 ? ver.ToString() : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        }

        /// <summary>
        /// Scans folder paths for a DLL matching the assembly prefix in a folder
        /// other than the expected one. Returns the misplaced path, or null.
        /// </summary>
        private static string ScanForMisplacedDll(string assemblyPrefix, string expectedFolder,
            string[] folderPaths, string excludePrefix = null)
        {
            foreach (var folder in folderPaths)
            {
                if (!Directory.Exists(folder)) continue;

                var folderName = Path.GetFileName(folder);
                if (string.Equals(folderName, expectedFolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    foreach (var file in Directory.GetFiles(folder, "*.dll",
                                 SearchOption.TopDirectoryOnly))
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        if (!fileName.StartsWith(assemblyPrefix, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (excludePrefix != null &&
                            fileName.StartsWith(excludePrefix, StringComparison.OrdinalIgnoreCase))
                            continue;
                        return file;
                    }
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.General,
                        $"Failed to scan {folder}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Creates a standalone overlay popup listing missing/outdated startup dependencies.
        /// Safe to call multiple times — skips if already visible.
        /// </summary>
        public static void ShowPopup()
        {
            if (_results == null) return;
            if (GameObject.Find("OTC_DepCheckCanvas") != null) return;
            BuildPopup("OTC_DepCheckCanvas",
                "OverTheCounter - Missing Dependencies",
                "OTC requires these mods to function.\nInstall the missing mods and restart the game.",
                _results, 540);
        }

        /// <summary>
        /// Creates a standalone overlay popup for missing multiplayer dependencies.
        /// Safe to call multiple times — skips if already visible.
        /// </summary>
        public static void ShowMultiplayerPopup()
        {
            if (_multiplayerResults == null) return;
            if (GameObject.Find("OTC_MultiplayerDepCanvas") != null) return;
            BuildPopup("OTC_MultiplayerDepCanvas",
                "OverTheCounter - Multiplayer Sync",
                "The following mod is required for co-op features to work between players.",
                _multiplayerResults, 360);
        }

        private static void BuildPopup(string canvasName, string title, string subtitle,
            DepResult[] results, float panelHeight)
        {
            // Canvas — ScreenSpaceOverlay at highest sort order
            var canvasGO = new GameObject(canvasName);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9999;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGO.AddComponent<GameCanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();

            // Dark backdrop
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
            panelRT.sizeDelta = new Vector2(720, panelHeight);
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

            AddText(panelGO.transform, "Title", title,
                24, FontStyles.Bold, Color.white, 40);

            AddText(panelGO.transform, "Subtitle", subtitle,
                16, FontStyles.Normal, new Color(0.75f, 0.75f, 0.75f), 50, wrap: true);

            AddSpacer(panelGO.transform, 8);

            // Dependency status lines
            foreach (var dep in results)
            {
                if (dep.Found && dep.VersionOk)
                {
                    var suffix = dep.Version != null ? $" (v{dep.Version})" : "";
                    AddText(panelGO.transform, dep.Name,
                        dep.Name + "  -  Installed" + suffix,
                        18, FontStyles.Normal, new Color(0.3f, 0.9f, 0.3f), 30);
                }
                else if (dep.Found && !dep.VersionOk)
                {
                    AddText(panelGO.transform, dep.Name,
                        $"{dep.Name}  -  Outdated (v{dep.Version}, requires v{dep.MinVersion})",
                        18, FontStyles.Bold, new Color(1f, 0.7f, 0.2f), 30);

                    AddText(panelGO.transform, dep.Name + "_Inst",
                        $"     Update {dep.Name} to v{dep.MinVersion} or newer.",
                        15, FontStyles.Normal, new Color(0.6f, 0.6f, 0.6f), 24, wrap: true);
                }
                else if (dep.MisplacedPath != null)
                {
                    var fileName = Path.GetFileName(dep.MisplacedPath);
                    var wrongFolder = Path.GetFileName(Path.GetDirectoryName(dep.MisplacedPath));
                    AddText(panelGO.transform, dep.Name,
                        dep.Name + "  -  NOT FOUND",
                        18, FontStyles.Bold, new Color(1f, 0.4f, 0.4f), 30);

                    AddText(panelGO.transform, dep.Name + "_Inst",
                        $"     Found {fileName} in {wrongFolder} folder. Move it to {dep.ExpectedFolder}.",
                        15, FontStyles.Normal, new Color(0.6f, 0.6f, 0.6f), 24, wrap: true);
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

            AddSpacer(panelGO.transform, 12);

            var (mask, btn, _) = TMPFactory.RoundedButtonWithLabel(
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
            var container = new GameObject(name + "_Container");
            container.transform.SetParent(parent, false);
            container.AddComponent<RectTransform>();
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
