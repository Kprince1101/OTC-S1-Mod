using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;
using MelonLoader.Utils;
using Mono.Cecil;
using Mono.Cecil.Cil;
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

        // MB_OKCANCEL = 0x01, MB_ICONWARNING = 0x30, IDOK = 1
        private const uint MB_OKCANCEL = 0x00000001;
        private const uint MB_ICONWARNING = 0x00000030;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC Loader");
        private static LoaderConfig _config = new LoaderConfig();
        private static string _configPath = "";

        /// <summary>
        /// Runs before any mods load. Restores previously-disabled DLLs, then disables
        /// any DLL that targets the wrong game branch (IL2CPP vs. Mono).
        /// </summary>
        public override void OnPreInitialization()
        {
            // Prevent duplicate execution if both standalone Loader and full OTC package are installed.
            const string sentinel = "OTC_LOADER_INITIALIZED";
            if (AppDomain.CurrentDomain.GetData(sentinel) != null)
            {
                Logger.Msg("Another OTC Loader instance already ran — skipping this copy.");
                return;
            }
            AppDomain.CurrentDomain.SetData(sentinel, true);

            string modsPath = MelonEnvironment.ModsDirectory;
            if (!Directory.Exists(modsPath)) return;

            LoadConfig();

            Branch gameBranch = MelonUtils.IsGameIl2Cpp() ? Branch.Il2Cpp : Branch.Mono;
            string branchName = gameBranch == Branch.Il2Cpp ? "IL2CPP" : "Mono";
            string wrongBranchName = gameBranch == Branch.Il2Cpp ? "Mono" : "IL2CPP";

            // Must run before MelonLoader loads S1API's Mods DLL into the CLR — once that
            // assembly is loaded, its broken IL is permanently baked in for this process.
            PatchS1APIHasLastNameBug(modsPath, gameBranch == Branch.Il2Cpp);

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
            string[] firstTimeDisabled = new string[allDlls.Length]; // DLLs disabled for the first time this session
            int firstTimeCount = 0;
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

                bool wasAlreadyDisabled = false;
                try
                {
                    File.Move(dll, dll + DisabledExt);
                    disabled++;
                    // Only log the first time — if it was already .off last run, stay silent.
                    for (int a = 0; a < alreadyDisabledCount; a++)
                    {
                        if (string.Equals(alreadyDisabled[a], filename, StringComparison.OrdinalIgnoreCase))
                        { wasAlreadyDisabled = true; break; }
                    }
                    if (!wasAlreadyDisabled)
                    {
                        Logger.Msg("Disabled '" + filename + "' — targets " + wrongBranchName + " but game is " + branchName + ".");
                        if (firstTimeCount < firstTimeDisabled.Length)
                            firstTimeDisabled[firstTimeCount++] = filename;
                    }
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

                    if (hasCompat)
                    {
                        // Only log compat message on first-time disables
                        if (!wasAlreadyDisabled)
                        {
                            string compatName = "";
                            for (int j = 0; j < allDlls.Length; j++)
                            {
                                if (j == i || skip[j]) continue;
                                if (branches[j] != null && branches[j] != gameBranch) continue;
                                bool sameDir  = string.Equals(Path.GetDirectoryName(allDlls[j]), dir, StringComparison.OrdinalIgnoreCase);
                                bool sameName = string.Equals(StripBranchKeyword(Path.GetFileName(allDlls[j])), myBase, StringComparison.OrdinalIgnoreCase);
                                if (sameDir || sameName) { compatName = Path.GetFileName(allDlls[j]); break; }
                            }
                            Logger.Msg("  → Compatible version kept: " + compatName);
                        }
                    }
                    else
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

            // ── Pass 4: Prompt restart if DLLs were disabled for the first time ──
            // .NET's assembly resolver may have already cached the wrong-branch DLLs
            // before our plugin ran. A restart ensures the renamed files are invisible.
            if (firstTimeCount > 0)
                PromptRestart(firstTimeDisabled, firstTimeCount);
        }

        // ── S1API hasLastName IL patch ──────────────────────────────────────────

        /// <summary>
        /// Binary-patches S1API's compiled NPC.cs constructor(s) to remove calls to
        /// Il2CppScheduleOne.NPCs.NPC.set_hasLastName(bool) -- a setter the current
        /// game version no longer has. S1API (ifBars fork, confirmed still broken as
        /// of v3.0.6/stable) calls this unconditionally from S1API.Entities.NPC's bare
        /// parameterless constructor whenever an NPC has no last name at construction
        /// time (which is every custom NPC built via NPCPrefabBuilder, since identity
        /// isn't applied until after the base constructor runs) -- and, separately,
        /// from an obsolete 4-arg constructor. Both funnel through the same broken IL.
        ///
        /// Why this can't be fixed with a normal Harmony patch (like everything else
        /// OTC neutralizes from S1API): Harmony must fully JIT-prepare whatever method
        /// it targets (patch OR unpatch) to build a redirect, and .NET JITs an entire
        /// method body up front, resolving every call token in it -- including tokens
        /// on branches that would never execute. Since set_hasLastName's token lives
        /// directly in the constructor's own IL, JIT-preparing that constructor for
        /// ANY reason throws immediately, so Harmony can never attach anything to it.
        /// This crash is 100% unconditional: every custom NPC in OTC (Vic, Static,
        /// Bella) fails to even construct, regardless of identity, schedule, or time
        /// of day -- confirmed via S1APIPhoneAppDiagnostic capturing the exact trace:
        /// "MissingMethodException: ... NPC.set_hasLastName(Boolean)" at
        /// "S1API.Entities.NPC..ctor()" / "OverTheCounter.NPCs.VicNPC..ctor()".
        ///
        /// The only place this IS fixable is before the CLR ever loads S1API's
        /// assembly -- i.e. rewriting the IL on disk. Since this plugin already runs
        /// via MelonPlugin.OnPreInitialization() (before MelonLoader loads any Mods,
        /// S1API included) and already depends on Mono.Cecil, we can open S1API's DLL,
        /// find every call to set_hasLastName inside S1API.Entities.NPC's
        /// constructor(s), and replace each call instruction with two pops (dropping
        /// the instance reference and the bool argument the call would have consumed)
        /// -- a stack-neutral no-op. hasLastName is just an internal display flag;
        /// simply never writing to it is harmless since the flag doesn't exist on the
        /// current game version anyway, and FirstName/LastName (which DO still work)
        /// are unaffected.
        ///
        /// Idempotent and fail-open: re-running finds nothing to patch and no-ops; any
        /// failure (DLL missing, Cecil error, file locked) is caught and logged as a
        /// warning without blocking the rest of loading. If a future S1API release
        /// fixes this upstream, this patch simply stops finding anything to do.
        /// </summary>
        private static void PatchS1APIHasLastNameBug(string modsPath, bool isIl2Cpp)
        {
            try
            {
                string dllPath = FindS1APIDll(modsPath, isIl2Cpp);
                if (dllPath == null)
                {
                    Logger.Msg("S1API DLL not found — skipping hasLastName patch.");
                    return;
                }

                // Read the whole file into memory first and work entirely off that buffer —
                // writing back to the same path while Cecil still holds a stream open on it
                // (e.g. ReadModule(path, ReadWrite=true) -> Write(path)) risks a file-lock
                // conflict. Reading into memory releases the file handle immediately.
                byte[] originalBytes = File.ReadAllBytes(dllPath);
                using var readStream = new MemoryStream(originalBytes);
                using var module = ModuleDefinition.ReadModule(readStream);

                var npcType = module.GetType("S1API.Entities.NPC");
                if (npcType == null)
                {
                    Logger.Msg("S1API.Entities.NPC type not found in " + Path.GetFileName(dllPath) + " — skipping hasLastName patch.");
                    return;
                }

                int patchedCount = 0;
                foreach (var method in npcType.Methods)
                {
                    if (!method.IsConstructor || !method.HasBody) continue;

                    var il = method.Body.GetILProcessor();
                    // Iterate backwards since we're replacing instructions in place by index.
                    for (int i = method.Body.Instructions.Count - 1; i >= 0; i--)
                    {
                        var instr = method.Body.Instructions[i];
                        bool isCall = instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt;
                        if (!isCall || !(instr.Operand is MethodReference mref) || mref.Name != "set_hasLastName")
                            continue;

                        // set_hasLastName(bool) is an instance setter: by the time we reach the
                        // call, the stack holds [instance, boolValue]. Two pops discard exactly
                        // what the call would have consumed, leaving the stack balanced.
                        var pop1 = il.Create(OpCodes.Pop);
                        var pop2 = il.Create(OpCodes.Pop);
                        il.Replace(instr, pop1);
                        il.InsertAfter(pop1, pop2);
                        patchedCount++;
                    }
                }

                if (patchedCount == 0)
                {
                    Logger.Msg("No hasLastName calls found in S1API.Entities.NPC — already patched, or this S1API version doesn't need it.");
                    return;
                }

                using (var writeStream = new MemoryStream())
                {
                    module.Write(writeStream);
                    File.WriteAllBytes(dllPath, writeStream.ToArray());
                }

                Logger.Msg("Patched " + patchedCount + " broken set_hasLastName call(s) in S1API (" +
                    Path.GetFileName(dllPath) + ") — custom NPCs (Vic/Static/Bella) can now construct.");
            }
            catch (Exception ex)
            {
                Logger.Warning("PatchS1APIHasLastNameBug failed (non-fatal, custom NPCs will keep failing to spawn): " + ex.Message);
            }
        }

        /// <summary>Finds S1API's main Mods-folder DLL for the currently running branch (IL2CPP vs Mono).</summary>
        private static string FindS1APIDll(string modsPath, bool isIl2Cpp)
        {
            string keyword = isIl2Cpp ? "il2cpp" : "mono";
            foreach (string path in Directory.GetFiles(modsPath, "S1API*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(path);
                // Skip the S1API bootstrap loader (S1APILoader.MelonLoader.dll) specifically --
                // NOT any filename containing "Loader", since the real S1API mod DLL we need is
                // itself named "S1API.Il2Cpp.MelonLoader.dll" / "S1API.Mono.MelonLoader.dll" and
                // would otherwise be excluded by its own "MelonLoader" suffix. This previously
                // caused FindS1APIDll to return null unconditionally, silently skipping the
                // hasLastName patch every run ("S1API DLL not found — skipping hasLastName patch.").
                if (name.StartsWith("S1APILoader", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.ToLowerInvariant().Contains(keyword))
                    return path;
            }
            return null;
        }

        /// <summary>
        /// Prompts the user to restart via a native MessageBox when wrong-branch DLLs
        /// were disabled for the first time. Falls back to a log warning on non-Windows.
        /// </summary>
        private static void PromptRestart(string[] disabledFiles, int count)
        {
            string modList = "";
            for (int i = 0; i < count; i++)
            {
                if (modList.Length > 0) modList += ", ";
                modList += Path.GetFileNameWithoutExtension(disabledFiles[i]);
            }

            Logger.Warning("First-time disable of: " + modList);
            Logger.Warning("A restart is recommended so the disabled DLLs are fully unloaded.");

            string message = "OTC Loader disabled incompatible mod DLL(s) that may have already been cached by the runtime:\n\n"
                + modList + "\n\n"
                + "A restart is recommended to avoid errors.\n\n"
                + "Click OK to close the game, or Cancel to continue anyway.";

            try
            {
                int result = MessageBox(IntPtr.Zero, message, "OTC Loader — Restart Recommended", MB_OKCANCEL | MB_ICONWARNING);
                if (result == 1) // IDOK
                    Environment.Exit(0);
            }
            catch
            {
                // P/Invoke unavailable (Linux native, etc.) — log-only fallback
                Logger.Warning("╔══════════════════════════════════════════════════════════╗");
                Logger.Warning("║  RESTART RECOMMENDED                                     ║");
                Logger.Warning("║                                                          ║");
                Logger.Warning("║  Incompatible mods were disabled but may have already     ║");
                Logger.Warning("║  been cached. Please close and restart the game.          ║");
                Logger.Warning("║  Affected: " + modList);
                Logger.Warning("╚══════════════════════════════════════════════════════════╝");
            }
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
