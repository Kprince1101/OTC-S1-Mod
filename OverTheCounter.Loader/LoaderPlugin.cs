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
            PatchS1APIBugs(modsPath, gameBranch == Branch.Il2Cpp);

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

        // ── S1API binary IL patches ─────────────────────────────────────────────
        //
        // Both bugs below share the same shape: a game update removed a type/member
        // that S1API's own compiled IL still references directly inside a method body
        // (not behind a dead branch -- these execute/get-JIT-prepared unconditionally).
        // Harmony can't fix either: it must fully JIT-prepare whatever method it
        // targets (as patch OR unpatch) to build a redirect, and preparing a method
        // whose own IL references a missing type/member throws immediately, before
        // Harmony ever gets a chance to attach anything. The only fix point is before
        // the CLR ever loads S1API's assembly -- rewriting the IL on disk. Since this
        // plugin already runs via MelonPlugin.OnPreInitialization() (before MelonLoader
        // loads any Mods, S1API included) and already depends on Mono.Cecil, we open
        // S1API's DLL once, apply both patches to the in-memory module, and write it
        // back. Idempotent and fail-open throughout: re-running finds nothing left to
        // patch and no-ops; any failure (DLL missing, Cecil error, file locked) is
        // caught and logged as a warning without blocking the rest of loading. If a
        // future S1API release fixes either bug upstream, that patch just stops
        // finding anything to do.

        private static void PatchS1APIBugs(string modsPath, bool isIl2Cpp)
        {
            try
            {
                string dllPath = FindS1APIDll(modsPath, isIl2Cpp);
                if (dllPath == null)
                {
                    Logger.Msg("S1API DLL not found — skipping binary IL patches.");
                    return;
                }

                // Read the whole file into memory first and work entirely off that buffer —
                // writing back to the same path while Cecil still holds a stream open on it
                // (e.g. ReadModule(path, ReadWrite=true) -> Write(path)) risks a file-lock
                // conflict. Reading into memory releases the file handle immediately.
                byte[] originalBytes = File.ReadAllBytes(dllPath);
                using var readStream = new MemoryStream(originalBytes);

                // Reading from an in-memory buffer (see the file-lock note above) gives Cecil no
                // directory to search from, so ANY assembly resolution it needs -- e.g. certain
                // TypeReference property accesses, or its own internal work during Write() --
                // fails outright ("Failed to resolve assembly: 'UnityEngine.CoreModule, ...'").
                // Point it at the actual runtime locations these assemblies live: S1API's own
                // folder, the profile's MelonLoader\Il2CppAssemblies (interop stub assemblies,
                // e.g. UnityEngine.CoreModule.dll -- same folder LocalPaths.targets' ManagedDllPath
                // points build-time references at), and MelonLoader's own directory.
                var resolver = new DefaultAssemblyResolver();
                string dllDir = Path.GetDirectoryName(dllPath);
                if (!string.IsNullOrEmpty(dllDir)) resolver.AddSearchDirectory(dllDir);

                string profileRoot = Directory.GetParent(modsPath)?.FullName;
                if (!string.IsNullOrEmpty(profileRoot))
                {
                    string il2cppAssembliesDir = Path.Combine(profileRoot, "MelonLoader", "Il2CppAssemblies");
                    if (Directory.Exists(il2cppAssembliesDir)) resolver.AddSearchDirectory(il2cppAssembliesDir);
                }

                string melonLoaderDir = Path.GetDirectoryName(typeof(MelonPlugin).Assembly.Location);
                if (!string.IsNullOrEmpty(melonLoaderDir)) resolver.AddSearchDirectory(melonLoaderDir);

                var readerParams = new ReaderParameters { AssemblyResolver = resolver };
                using var module = ModuleDefinition.ReadModule(readStream, readerParams);

                int hasLastNamePatched = PatchHasLastName(module);
                int exitActionPatched = PatchExitAction(module);

                if (hasLastNamePatched == 0 && exitActionPatched == 0)
                {
                    Logger.Msg("No known S1API IL bugs found to patch in " + Path.GetFileName(dllPath) +
                        " — already patched, or this S1API version doesn't need it.");
                    return;
                }

                using (var writeStream = new MemoryStream())
                {
                    module.Write(writeStream);
                    File.WriteAllBytes(dllPath, writeStream.ToArray());
                }

                Logger.Msg("Patched S1API (" + Path.GetFileName(dllPath) + "): " +
                    hasLastNamePatched + " broken NPC constructor setter call(s) removed (custom NPCs — Vic/Static/Bella — can now construct further), " +
                    exitActionPatched + " broken ExitAction/ExitDelegate statement(s) removed (custom phone apps can now register their icons).");
            }
            catch (Exception ex)
            {
                Logger.Warning("PatchS1APIBugs failed (non-fatal, the underlying bugs will keep occurring): " + ex.Message);
            }
        }

        /// <summary>
        /// See the class-level notes above: S1API's <c>NPC()</c> base constructor unconditionally
        /// calls a handful of setters on the game's own NPC type that this game version no longer
        /// has -- confirmed so far: <c>set_hasLastName(bool)</c> and <c>set_MugshotSprite(Sprite)</c>.
        /// Both showed up the same way: fix one, rebuild, the constructor's JIT gets past that call
        /// and immediately hits the next broken one further down the same method body (expected --
        /// the whole method is resolved eagerly, so every broken call in it is "real", not
        /// conditional; we only find out about each one once the prior one stops masking it).
        /// Every call found so far is a simple instance-setter invocation of the form
        /// <c>[push instance][push value][call set_X]</c> with the result discarded, so replacing
        /// the call with two pops (dropping exactly what it would have consumed) is stack-neutral
        /// and requires no further IL adjustment. Add new names to <see cref="BrokenNpcSetterNames"/>
        /// as they surface -- same fix, same risk profile, no other code changes needed.
        /// </summary>
        private static readonly string[] BrokenNpcSetterNames =
        {
            "set_hasLastName",
            "set_MugshotSprite",
        };

        private static int PatchHasLastName(ModuleDefinition module)
        {
            var npcType = module.GetType("S1API.Entities.NPC");
            if (npcType == null) return 0;

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
                    if (!isCall || !(instr.Operand is MethodReference mref)) continue;

                    bool isKnownBroken = false;
                    for (int n = 0; n < BrokenNpcSetterNames.Length; n++)
                    {
                        if (mref.Name == BrokenNpcSetterNames[n]) { isKnownBroken = true; break; }
                    }
                    if (!isKnownBroken) continue;

                    var pop1 = il.Create(OpCodes.Pop);
                    var pop2 = il.Create(OpCodes.Pop);
                    il.Replace(instr, pop1);
                    il.InsertAfter(pop1, pop2);
                    patchedCount++;
                }
            }
            return patchedCount;
        }

        /// <summary>
        /// S1API.PhoneApp.PhoneApp.SpawnUI wires up an exit/back-button listener via
        /// <c>Il2CppScheduleOne.DevUtilities.ExitAction</c> -- a type the current game version no
        /// longer has. Confirmed (via reading S1API's actual source, both released and unreleased
        /// branches) unconditionally broken: every custom phone app (OTC's CustomersApp/GreenTabApp,
        /// even the built-in ModsApp) fails to register its icon with a TypeLoadException on
        /// ExitAction, thrown from inside the interop delegate conversion S1API uses to hook the
        /// exit chain (S1API.PhoneApp.PhoneApp.OnDestroyed has the matching teardown call and would
        /// hit the same problem once triggered).
        ///
        /// Unlike hasLastName (a single self-contained setter call), the broken code here is a
        /// small chain of nested expressions ("build a delegate, convert it, register it" / "read
        /// the field, deregister, null it out"), so a single call->2-pops swap isn't enough --
        /// removing an inner instruction changes what the next instruction up the chain actually
        /// has waiting on the stack. Instead: find each *terminal* (stack-neutral, push=0)
        /// instruction whose operand mentions ExitAction/ExitDelegate (a void call or a field
        /// store), then walk backward from it counting "still need N more values" against each
        /// preceding instruction's own push/pop -- this naturally traces back through however many
        /// nested pushes feed it (delegate construction, generic conversion call, etc.) to the
        /// exact point the stack was last balanced, which is the true start of that statement.
        /// Removing that whole [start..landmark] range is safe because, by construction, it nets to
        /// zero stack effect. As a safety net: if the backward walk can't fully resolve (crosses a
        /// branch/loop boundary) or the range would delete a branch target or an exception-handler
        /// boundary, that occurrence is skipped and logged rather than risking corrupted IL.
        /// </summary>
        private static int PatchExitAction(ModuleDefinition module)
        {
            int patched = 0;
            string[] keywords = { "ExitAction", "ExitDelegate" };

            var phoneApp = module.GetType("S1API.PhoneApp.PhoneApp");
            if (phoneApp != null) patched += RemoveStatementsReferencing(phoneApp, keywords);

            // TVApp has the identical pattern in S1API's source. OTC doesn't use TVApp today, but
            // patching it too costs nothing extra and pre-empts the same crash for any future
            // TV-based feature (or another mod sharing this S1API copy).
            var tvApp = module.GetType("S1API.TVApp.TVApp");
            if (tvApp != null) patched += RemoveStatementsReferencing(tvApp, keywords);

            return patched;
        }

        /// <summary>
        /// Removes every self-contained IL statement in <paramref name="type"/>'s methods whose
        /// outermost (stack-neutral) instruction references a type/method/field whose name
        /// contains any of <paramref name="keywords"/>. See <see cref="PatchExitAction"/> for the
        /// reasoning. Arrays (not List/Dictionary) throughout: this plugin must also load under the
        /// Mono branch of the game, whose CLR lacks the System.Collections v6.0.0.0 that generic
        /// collections pull in.
        /// </summary>
        private static int RemoveStatementsReferencing(TypeDefinition type, string[] keywords)
        {
            int removedStatements = 0;

            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                var body = method.Body;

                Instruction[] landmarks = new Instruction[64];
                int landmarkCount = 0;
                foreach (var instr in body.Instructions)
                {
                    if (instr.Operand == null) continue;
                    if (GetPushCount(instr) != 0) continue; // only terminal (void-effect) instructions
                    if (!MentionsAny(instr.Operand, keywords)) continue;
                    if (landmarkCount < landmarks.Length)
                        landmarks[landmarkCount++] = instr;
                }
                if (landmarkCount == 0) continue;

                var il = body.GetILProcessor();
                // Process last-to-first so removing one range doesn't shift indices for the rest.
                for (int li = landmarkCount - 1; li >= 0; li--)
                {
                    var landmark = landmarks[li];
                    int landmarkIndex = body.Instructions.IndexOf(landmark);
                    if (landmarkIndex < 0) continue; // already swept up by a later removal

                    int needed = GetPopCount(landmark);
                    int idx = landmarkIndex - 1;
                    while (needed > 0 && idx >= 0)
                    {
                        needed -= GetPushCount(body.Instructions[idx]);
                        needed += GetPopCount(body.Instructions[idx]);
                        idx--;
                    }
                    int start = idx + 1;

                    if (needed != 0)
                    {
                        Logger.Warning("ExitAction patch: could not cleanly bound a statement around " +
                            landmark.OpCode + " in " + type.Name + "." + method.Name + " -- skipping that occurrence (non-fatal).");
                        continue;
                    }

                    bool touchesControlFlow = false;
                    for (int r = start; r <= landmarkIndex && !touchesControlFlow; r++)
                    {
                        var candidate = body.Instructions[r];
                        foreach (var other in body.Instructions)
                        {
                            if (other.Operand is Instruction t && t == candidate) { touchesControlFlow = true; break; }
                            if (other.Operand is Instruction[] ts)
                            {
                                bool isTarget = false;
                                for (int ti = 0; ti < ts.Length; ti++)
                                    if (ts[ti] == candidate) { isTarget = true; break; }
                                if (isTarget) { touchesControlFlow = true; break; }
                            }
                        }
                        if (!touchesControlFlow && body.HasExceptionHandlers)
                        {
                            foreach (var eh in body.ExceptionHandlers)
                            {
                                if (eh.TryStart == candidate || eh.TryEnd == candidate ||
                                    eh.HandlerStart == candidate || eh.HandlerEnd == candidate ||
                                    eh.FilterStart == candidate)
                                { touchesControlFlow = true; break; }
                            }
                        }
                    }
                    if (touchesControlFlow)
                    {
                        Logger.Warning("ExitAction patch: statement around " + landmark.OpCode + " in " +
                            type.Name + "." + method.Name + " overlaps a branch target or exception-handler boundary -- skipping that occurrence (non-fatal).");
                        continue;
                    }

                    for (int r = landmarkIndex; r >= start; r--)
                        il.Remove(body.Instructions[r]);
                    removedStatements++;
                }
            }

            return removedStatements;
        }

        private static bool MentionsAny(object operand, string[] keywords)
        {
            switch (operand)
            {
                case FieldReference fref:
                    return NameMentionsAny(fref.FieldType, keywords) || NameMentionsAny(fref.DeclaringType, keywords);
                case MethodReference mref:
                    if (NameMentionsAny(mref.ReturnType, keywords) || NameMentionsAny(mref.DeclaringType, keywords)) return true;
                    foreach (var p in mref.Parameters)
                        if (NameMentionsAny(p.ParameterType, keywords)) return true;
                    if (mref is GenericInstanceMethod gim)
                        foreach (var ga in gim.GenericArguments)
                            if (NameMentionsAny(ga, keywords)) return true;
                    return false;
                case TypeReference tref:
                    return NameMentionsAny(tref, keywords);
                default:
                    return false;
            }
        }

        private static bool NameMentionsAny(TypeReference t, string[] keywords)
        {
            if (t == null) return false;
            string n = t.FullName ?? "";
            foreach (var kw in keywords)
                if (n.Contains(kw)) return true;
            if (t is GenericInstanceType git)
                foreach (var ga in git.GenericArguments)
                    if (NameMentionsAny(ga, keywords)) return true;
            return false;
        }

        private static int GetPopCount(Instruction instr)
        {
            switch (instr.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref:
                    return 1;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi:
                    return 2;
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref:
                    return 3;
                case StackBehaviour.Varpop:
                    return GetVarPopCount(instr);
                default:
                    return 0;
            }
        }

        private static int GetVarPopCount(Instruction instr)
        {
            if (!(instr.Operand is MethodReference mref)) return 0;
            int count = mref.Parameters.Count;
            if (instr.OpCode == OpCodes.Newobj) return count; // newobj: no separate 'this' pop, runtime allocates it
            if (mref.HasThis) count += 1;
            return count;
        }

        private static int GetPushCount(Instruction instr)
        {
            switch (instr.OpCode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1:
                case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8:
                case StackBehaviour.Pushref:
                    return 1;
                case StackBehaviour.Push1_push1:
                    return 2;
                case StackBehaviour.Varpush:
                    return GetVarPushCount(instr);
                default:
                    return 0;
            }
        }

        private static int GetVarPushCount(Instruction instr)
        {
            if (instr.OpCode == OpCodes.Newobj) return 1;
            if (instr.Operand is MethodReference mref)
                return mref.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
            return 0;
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
