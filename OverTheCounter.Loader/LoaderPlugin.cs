using System;
using System.IO;
using System.Reflection;
using MelonLoader;
using MelonLoader.Utils;
using Mono.Cecil;
using Newtonsoft.Json;

[assembly: MelonInfo(typeof(OverTheCounter.Loader.LoaderPlugin), "OTC Loader", "1.0.0", "hdlmrell", null)]
[assembly: MelonColor(100, 200, 180, 255)]

namespace OverTheCounter.Loader
{
    internal enum Branch { Mono, Il2Cpp }

    [Serializable]
    internal class LoaderConfig
    {
        /// <summary>DLL filenames (case-insensitive) that the loader will never disable.</summary>
        // string[] instead of List<string>: List<T> references System.Collections v6.0.0.0, which
        // doesn't exist in Mono's CLR and prevents the plugin from loading on Mono games.
        public string[] Whitelist = Array.Empty<string>();
    }

    /// <summary>
    /// MelonPlugin that runs before any mods load and disables wrong-branch DLLs.
    /// Detects branch via filename keywords ("mono"/"il2cpp"), with Mono.Cecil inspection as fallback.
    /// Restores previously-disabled DLLs first so branch switches work automatically.
    /// Replaces the functionality of SwapperPlugin, which has a critical bug on fresh installs.
    /// </summary>
    public class LoaderPlugin : MelonPlugin
    {
        // Use .off instead of .di so SwapperPlugin's restore pass can't undo our work.
        // SwapperPlugin restores by stripping ".di" from filenames — ".off" is immune to that.
        // We still restore legacy .di files in pass 1 for users who previously had SwapperPlugin working.
        private const string DisabledExt = ".off";
        private const string ConfigFileName = "OTCLoader.config.json";

        // Never touch these — infrastructure or ourselves.
        // string[] instead of HashSet<string>: same reason as LoaderConfig.Whitelist above.
        private static readonly string[] BuiltinBlacklist =
        {
            "OverTheCounter-Loader.dll",
            "SwapperPlugin.dll",
        };

        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC Loader");
        private static LoaderConfig _config = new LoaderConfig();
        private static string _configPath = "";

        /// <summary>
        /// Runs before any mods load. Restores previously-disabled DLLs, then disables
        /// any DLL that targets the wrong game branch (IL2CPP vs. Mono).
        /// </summary>
        public override void OnPreInitialization()
        {
            string modsPath = MelonEnvironment.ModsDirectory;
            if (!Directory.Exists(modsPath)) return;

            LoadConfig();

            Branch gameBranch = MelonUtils.IsGameIl2Cpp() ? Branch.Il2Cpp : Branch.Mono;
            string branchName = gameBranch == Branch.Il2Cpp ? "IL2CPP" : "Mono";
            string wrongBranchName = gameBranch == Branch.Il2Cpp ? "Mono" : "IL2CPP";

            // ── Pass 1: Restore all previously-disabled DLLs ─────────────────────
            // Runs first so branch switches are handled automatically.
            // Handles:
            //   .dll.off      — our format
            //   .dll.off.di   — our .off file that SwapperPlugin subsequently disabled (runs after us alphabetically)
            //   .dll.di       — legacy SwapperPlugin format
            // Track filenames we restore from OUR format so Pass 2 can suppress the "Disabled" log —
            // these were already disabled last run, no need to tell the user again.
            string[] alreadyDisabled = new string[256];
            int alreadyDisabledCount = 0;

            foreach (string diFile in Directory.GetFiles(modsPath, "*", SearchOption.AllDirectories))
            {
                bool isOurs = diFile.EndsWith(".dll" + DisabledExt, StringComparison.OrdinalIgnoreCase);
                bool isOursPlusDi = diFile.EndsWith(".dll" + DisabledExt + ".di", StringComparison.OrdinalIgnoreCase);
                bool isLegacy = diFile.EndsWith(".dll.di", StringComparison.OrdinalIgnoreCase);
                if (!isOurs && !isOursPlusDi && !isLegacy) continue;

                // Strip the full suffix back to the original .dll name.
                // isOursPlusDi must be checked before isLegacy because .dll.off.di ends with .di too.
                string ext = isOursPlusDi ? (DisabledExt + ".di") : (isLegacy ? ".di" : DisabledExt);
                string original = diFile.Substring(0, diFile.Length - ext.Length);
                try
                {
                    if (!File.Exists(original))
                    {
                        File.Move(diFile, original);
                        // Record that this file was already disabled by us (not first-time)
                        if ((isOurs || isOursPlusDi) && alreadyDisabledCount < alreadyDisabled.Length)
                            alreadyDisabled[alreadyDisabledCount++] = Path.GetFileName(original);
                    }
                    else
                        File.Delete(diFile); // stale .off/.di alongside an existing .dll — clean it up
                }
                catch (Exception ex)
                {
                    Logger.Warning("Could not restore " + Path.GetFileName(diFile) + ": " + ex.Message);
                }
            }

            // ── Pass 2: Evaluate every DLL and disable wrong-branch ones ──────────
            string[] allDlls = Directory.GetFiles(modsPath, "*.dll", SearchOption.AllDirectories);

            // Pre-compute skip flags and branches using only arrays (no List/Dictionary/LINQ).
            // List<T>/Dictionary<K,V> reference System.Collections v6.0.0.0, which Mono lacks.
            bool[] skip = new bool[allDlls.Length];
            Branch?[] branches = new Branch?[allDlls.Length];

            for (int i = 0; i < allDlls.Length; i++)
            {
                string filename = Path.GetFileName(allDlls[i]);
                string dir = Path.GetDirectoryName(allDlls[i]);

                // DLLs inside a Plugins/ subfolder are MelonPlugins with their own loading — skip them.
                skip[i] = Path.GetFileName(dir).Equals("Plugins", StringComparison.OrdinalIgnoreCase)
                           || IsBlacklisted(filename);

                if (!skip[i])
                    branches[i] = DetectBranch(allDlls[i]);
            }

            int disabled = 0;
            string[] warnedDirs          = new string[allDlls.Length]; // at most one entry per unique dir
            string[] warnedModNames      = new string[allDlls.Length]; // human-readable name for each
            string[] warnedDisabledLists = new string[allDlls.Length]; // disabled DLL filenames per entry
            int warnedCount = 0;

            for (int i = 0; i < allDlls.Length; i++)
            {
                if (skip[i] || branches[i] == null || branches[i] == gameBranch) continue;

                string dll = allDlls[i];
                string filename = Path.GetFileName(dll);
                string dir = Path.GetDirectoryName(dll);

                try
                {
                    File.Move(dll, dll + DisabledExt);
                    disabled++;
                    // Only log the first time — if it was already .off last run, stay silent.
                    bool wasAlreadyDisabled = false;
                    for (int a = 0; a < alreadyDisabledCount; a++)
                    {
                        if (string.Equals(alreadyDisabled[a], filename, StringComparison.OrdinalIgnoreCase))
                        { wasAlreadyDisabled = true; break; }
                    }
                    if (!wasAlreadyDisabled)
                        Logger.Msg("Disabled '" + filename + "' — targets " + wrongBranchName + " but game is " + branchName + ".");
                }
                catch (Exception ex)
                {
                    Logger.Warning("Could not disable '" + filename + "': " + ex.Message);
                    continue;
                }

                // ── Pass 3: Warn once per directory that has no compatible DLL ──────
                bool alreadyWarned = false;
                for (int w = 0; w < warnedCount; w++)
                {
                    if (string.Equals(warnedDirs[w], dir, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyWarned = true;
                        break;
                    }
                }

                if (!alreadyWarned)
                {
                    bool hasCompat = false;
                    string myBase = StripBranchKeyword(filename);
                    for (int j = 0; j < allDlls.Length; j++)
                    {
                        if (j == i || skip[j]) continue;
                        if (branches[j] != null && branches[j] != gameBranch) continue;
                        // Same directory, or matching base name anywhere in the Mods tree
                        bool sameDir  = string.Equals(Path.GetDirectoryName(allDlls[j]), dir, StringComparison.OrdinalIgnoreCase);
                        bool sameName = string.Equals(StripBranchKeyword(Path.GetFileName(allDlls[j])), myBase, StringComparison.OrdinalIgnoreCase);
                        if (sameDir || sameName) { hasCompat = true; break; }
                    }

                    if (!hasCompat)
                    {
                        string modName = Path.GetFileNameWithoutExtension(filename);
                        string disabledList = "";
                        for (int j = 0; j < allDlls.Length; j++)
                        {
                            if (skip[j] || branches[j] == null || branches[j] == gameBranch) continue;
                            if (!string.Equals(Path.GetDirectoryName(allDlls[j]), dir, StringComparison.OrdinalIgnoreCase)) continue;
                            if (disabledList.Length > 0) disabledList += ", ";
                            disabledList += Path.GetFileName(allDlls[j]);
                        }
                        warnedDirs[warnedCount]          = dir;
                        warnedModNames[warnedCount]      = modName;
                        warnedDisabledLists[warnedCount] = disabledList;
                        warnedCount++;
                    }
                }
            }

            if (warnedCount > 0)
            {
                Logger.Warning("╔══════════════════════════════════════════════════════════╗");
                Logger.Warning("║  INCOMPATIBLE MODS — no " + branchName + "-compatible version found");
                Logger.Warning("║");
                for (int w = 0; w < warnedCount; w++)
                {
                    Logger.Warning("║  • " + warnedModNames[w]);
                    Logger.Warning("║    Disabled: " + warnedDisabledLists[w]);
                }
                Logger.Warning("║");
                Logger.Warning("║  → Check each mod page for a " + branchName + "-compatible release,");
                Logger.Warning("║    or switch your game to the branch those mods support.");
                Logger.Warning("║");
                Logger.Warning("║  → Think this is a mistake? Open the config file and");
                Logger.Warning("║    add the filename to the Whitelist:");
                Logger.Warning("║    " + _configPath);
                Logger.Warning("╚══════════════════════════════════════════════════════════╝");
            }

            if (disabled > 0 || warnedCount > 0)
            {
                string incompatNames = "";
                for (int w = 0; w < warnedCount; w++)
                {
                    if (incompatNames.Length > 0) incompatNames += ", ";
                    incompatNames += warnedModNames[w];
                }
                string incompatSuffix = warnedCount > 0 ? (": " + incompatNames) : "";
                Logger.Msg("Done — " + disabled + " wrong-branch DLL(s) correctly handled, " +
                                warnedCount + " mod(s) have no compatible version" + incompatSuffix + ".");
            }
            else
                Logger.Msg("All DLLs are compatible with " + branchName + ".");
        }

        // ── Config ───────────────────────────────────────────────────────────────

        private static void LoadConfig()
        {
            _configPath = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                ConfigFileName);
            string configPath = _configPath;

            if (File.Exists(configPath))
            {
                try
                {
                    _config = JsonConvert.DeserializeObject<LoaderConfig>(File.ReadAllText(configPath)) ?? new LoaderConfig();
                }
                catch (Exception ex)
                {
                    Logger.Warning("Could not read config, using defaults: " + ex.Message);
                    _config = new LoaderConfig();
                }
            }
            else
            {
                _config = new LoaderConfig();
                try
                {
                    File.WriteAllText(configPath,
                        "{\n" +
                        "  \"_readme\": \"Add DLL filenames to Whitelist to prevent OTC Loader from disabling them.\",\n" +
                        "  \"Whitelist\": [\n" +
                        "    \"ExampleMod-IL2Cpp.dll\",\n" +
                        "    \"AnotherExampleMod-IL2Cpp.dll\"\n" +
                        "  ]\n" +
                        "}\n");
                    Logger.Msg("Created default config at " + configPath);
                }
                catch { /* Non-fatal — defaults apply */ }
            }
        }

        /// <summary>
        /// Strips branch keywords (IL2CPP / Mono) and common separators from a DLL name
        /// so that "SteamNetworkLib-IL2Cpp" and "SteamNetworkLib-Mono" compare equal.
        /// </summary>
        private static string StripBranchKeyword(string filename)
        {
            string name = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
            string[] tokens = { "-il2cpp", "_il2cpp", ".il2cpp", "-mono", "_mono", ".mono" };
            foreach (string token in tokens)
            {
                if (name.EndsWith(token))
                    return name.Substring(0, name.Length - token.Length);
            }
            return name;
        }

        private static bool IsBlacklisted(string filename) =>
            Array.Exists(BuiltinBlacklist, b => string.Equals(b, filename, StringComparison.OrdinalIgnoreCase)) ||
            filename.StartsWith("S1API", StringComparison.OrdinalIgnoreCase) || // S1API has its own branch detection — never touch it
            Array.Exists(_config.Whitelist ?? Array.Empty<string>(), w => string.Equals(w, filename, StringComparison.OrdinalIgnoreCase));

        // ── Detection ────────────────────────────────────────────────────────────

        /// <summary>
        /// Detects the target branch of a DLL.
        /// Returns null if the DLL appears branch-agnostic or cannot be determined.
        /// </summary>
        private static Branch? DetectBranch(string path)
        {
            string filename = Path.GetFileName(path).ToLowerInvariant();

            // Fast path: filename keyword
            if (filename.Contains("mono")) return Branch.Mono;
            if (filename.Contains("il2cpp")) return Branch.Il2Cpp;

            // Fallback: Mono.Cecil type reference inspection
            try
            {
                using var asm = AssemblyDefinition.ReadAssembly(path);
                foreach (var module in asm.Modules)
                {
                    foreach (var typeRef in module.GetTypeReferences())
                    {
                        string ns = (typeRef.Namespace ?? "").ToLowerInvariant();
                        if (ns.StartsWith("il2cppscheduleone") || ns.StartsWith("il2cppsystem"))
                            return Branch.Il2Cpp;
                        if (ns == "scheduleone")
                            return Branch.Mono;
                    }
                }
            }
            catch { /* Native DLLs, obfuscated assemblies, or locked files — skip silently */ }

            return null;
        }
    }
}
