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
                int brokenGettersPatched = PatchBrokenGetters(module);

                if (hasLastNamePatched == 0 && exitActionPatched == 0 && brokenGettersPatched == 0)
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
                    exitActionPatched + " broken ExitAction/ExitDelegate statement(s) removed (custom phone apps can now register their icons), " +
                    brokenGettersPatched + " broken getter call(s) fixed (genuinely-missing getters removed entirely; getters that exist but throw on off-scene template construction wrapped in try/catch so they only fall back to null when they actually throw).");
            }
            catch (Exception ex)
            {
                // ex.Message alone ("Value cannot be null. (Parameter 'key')") isn't enough to find
                // which of PatchHasLastName/PatchExitAction/PatchBrokenGetters/module.Write() threw,
                // or which line -- a harness rebuilt to byte-for-byte match this method (same live
                // DLL, same resolver search directories, same Mono.Cecil.dll, reading via the same
                // in-memory-stream approach) could NOT reproduce this exception, so guessing further
                // from outside isn't productive. Log everything needed to pinpoint it for real.
                Logger.Warning("PatchS1APIBugs failed (non-fatal, the underlying bugs will keep occurring): " +
                    ex.GetType().FullName + ": " + ex.Message);
                Logger.Warning("PatchS1APIBugs exception stack trace:\n" + ex.StackTrace);
                if (ex.InnerException != null)
                {
                    Logger.Warning("PatchS1APIBugs inner exception: " +
                        ex.InnerException.GetType().FullName + ": " + ex.InnerException.Message + "\n" +
                        ex.InnerException.StackTrace);
                }
            }
        }

        /// <summary>
        /// S1API's NPC construction path (the base constructor plus every helper method it calls --
        /// InitializeHealthComponent, InitializeAwarenessComponent, InitializeBehaviourComponents,
        /// InitializeVisionComponents, InitializeInteractables, InitializeInventoryComponent,
        /// InitializeRelationshipData, RestoreRuntimeAvatarAppearance, the conversation-category
        /// helpers, NPCAppearance.ApplyDefaultSettings, NPCPrefabIdentity.CreateAvatarSettings, the
        /// Customer/Dealer data builders, etc.) calls a large number of setters on the game's own
        /// types that this game version no longer exposes as property setters. Traced the *entire*
        /// broken set at once by loading the actual S1API DLL and the real game assembly
        /// (Cpp2IL's cpp2il_out/Assembly-CSharp.dll) side by side with Mono.Cecil and diffing what
        /// S1API's construction call graph calls against what the live game type actually has --
        /// rather than continuing to discover these one crash/rebuild/relaunch at a time.
        ///
        /// Verified: most of these (~58 of 66) still exist as plain public *fields* on the real
        /// type -- the game's interop layer just stopped generating a set_X wrapper method for
        /// them. A small number (~8: NPC.set_ConversationCategories, NPC.set_intObj,
        /// NPCInventory.set_PickpocketIntObj, NPCHealth.set_MaxHealth, CustomerData.
        /// set_DefaultAffinityData, Dealer.set_Cut/set_DealerType/set_SigningFee) have no matching
        /// field at all and may be genuinely gone or renamed.
        ///
        /// For now every entry here gets the same treatment as the original hasLastName fix: the
        /// call is replaced with two pops (stack-neutral no-op), which reliably stops the
        /// MissingMethodException and lets custom NPCs (Vic/Static/Bella) finish constructing. This
        /// is NOT a full fix for the field-backed ones -- popping means whatever value S1API meant
        /// to assign (avatar appearance data, behaviour-component wiring, health/awareness event
        /// hookups, dealer config, etc.) is silently dropped instead of applied. A more complete fix
        /// would rewrite the call into a direct field store (same stack shape: [push instance][push
        /// value][stfld] lines up exactly with [push instance][push value][call set_X]) instead of
        /// discarding the value, but that requires matching each field's real interop type at
        /// runtime to build a correct FieldReference, which needs testing against the live game
        /// process (not just the static Cpp2IL dump this analysis used) to get right. Left as a
        /// follow-up -- flagging here so a symptom like "Vic has default appearance" or "Vic doesn't
        /// react to being shot" isn't a mystery later.
        ///
        /// Matching is by (declaring type simple name, method name) pair, not method name alone --
        /// name-only matching is NOT safe here: e.g. set_Responses exists as a legitimate, working
        /// call on NPC itself AND as a broken call on NPCAwareness, in the same helper method.
        /// Popping the working NPC.set_Responses call by name-only accident would silently regress
        /// working functionality while "fixing" an unrelated crash.
        /// </summary>
        private static readonly (string DeclaringType, string MethodName)[] BrokenNpcSetterCalls =
        {
            // NPC itself -- these show up directly in the parameterless constructor / are called
            // from it eagerly; confirmed via repeated build/relaunch cycles plus the full-graph scan.
            ("NPC", "set_hasLastName"),
            ("NPC", "set_MugshotSprite"),
            ("NPC", "set_LastName"),
            ("NPC", "set_FirstName"),
            ("NPC", "set_ID"),
            ("NPC", "set_BakedGUID"),
            ("NPC", "set_intObj"),
            ("NPC", "set_RelationData"),
            ("NPC", "set_ConversationCategories"),

            // NPCHealth (InitializeHealthComponent)
            ("NPCHealth", "set_onDie"),
            ("NPCHealth", "set_onKnockedOut"),
            ("NPCHealth", "set_MaxHealth"),

            // NPCAwareness (InitializeAwarenessComponent / InitializeVisionComponents) -- note
            // set_Responses here is NPCAwareness's, distinct from the working NPC.set_Responses.
            ("NPCAwareness", "set_onExplosionHeard"),
            ("NPCAwareness", "set_onGunshotHeard"),
            ("NPCAwareness", "set_onHitByCar"),
            ("NPCAwareness", "set_onNoticedDrugDealing"),
            ("NPCAwareness", "set_onNoticedGeneralCrime"),
            ("NPCAwareness", "set_onNoticedPettyCrime"),
            ("NPCAwareness", "set_onNoticedPlayerViolatingCurfew"),
            ("NPCAwareness", "set_onNoticedSuspiciousPlayer"),
            ("NPCAwareness", "set_Listener"),
            ("NPCAwareness", "set_Responses"),
            ("NPCAwareness", "set_VisionCone"),

            // NPCBehaviour (InitializeBehaviourComponents)
            ("NPCBehaviour", "set_CoweringBehaviour"),
            ("NPCBehaviour", "set_FleeBehaviour"),
            ("NPCBehaviour", "set_GenericDialogueBehaviour"),
            ("NPCBehaviour", "set_RequestProductBehaviour"),
            ("NPCBehaviour", "set_CallPoliceBehaviour"),
            ("NPCBehaviour", "set_CombatBehaviour"),
            ("NPCBehaviour", "set_StationaryBehaviour"),
            ("NPCBehaviour", "set_FaceTargetBehaviour"),
            ("NPCBehaviour", "set_ConsumeProductBehaviour"),
            ("NPCBehaviour", "set_UnconsciousBehaviour"),
            ("NPCBehaviour", "set_DeadBehaviour"),

            // VisionCone / StateContainer (InitializeVisionComponents)
            ("VisionCone", "set_DefaultStatesOfInterest"),
            ("VisionCone", "set_QuestionMarkPopup"),
            ("StateContainer", "set_state"),

            // NPCInventory (InitializeInventoryComponent)
            ("NPCInventory", "set_PickpocketIntObj"),

            // AvatarSettings (NPCAppearance.ApplyDefaultSettings, NPCPrefabIdentity.CreateAvatarSettings)
            ("AvatarSettings", "set_SkinColor"),
            ("AvatarSettings", "set_Height"),
            ("AvatarSettings", "set_Gender"),
            ("AvatarSettings", "set_Weight"),
            ("AvatarSettings", "set_EyebrowScale"),
            ("AvatarSettings", "set_EyebrowThickness"),
            ("AvatarSettings", "set_EyebrowRestingHeight"),
            ("AvatarSettings", "set_EyebrowRestingAngle"),
            ("AvatarSettings", "set_LeftEyeLidColor"),
            ("AvatarSettings", "set_RightEyeLidColor"),
            ("AvatarSettings", "set_LeftEyeRestingState"),
            ("AvatarSettings", "set_RightEyeRestingState"),
            ("AvatarSettings", "set_EyeballMaterialIdentifier"),
            ("AvatarSettings", "set_EyeBallTint"),
            ("AvatarSettings", "set_PupilDilation"),
            ("AvatarSettings", "set_HairPath"),
            ("AvatarSettings", "set_HairColor"),
            ("AvatarSettings", "set_ImpostorTexture"),
            ("AvatarSettings", "set_FaceLayerSettings"),
            ("AvatarSettings", "set_BodyLayerSettings"),
            ("AvatarSettings", "set_AccessorySettings"),
            ("LayerSetting", "set_layerPath"),
            ("LayerSetting", "set_layerTint"),
            ("AccessorySetting", "set_path"),
            ("AccessorySetting", "set_color"),

            // Customer / Dealer / CustomerData (TrySetCustomerDataOnComponent, TryApplyDealerDefaults,
            // CustomerDataBuilder..ctor)
            ("Customer", "set_customerData"),
            ("Customer", "set_currentAffinityData"),
            ("Dealer", "set_SigningFee"),
            ("Dealer", "set_Cut"),
            ("Dealer", "set_DealerType"),
            ("CustomerData", "set_DefaultAffinityData"),
        };

        private static int PatchHasLastName(ModuleDefinition module)
        {
            int patchedCount = 0;

            // Scan every method in the whole module (not just S1API.Entities.NPC's own
            // constructors) -- the broken calls live in helper methods across several
            // S1API types (NPCAppearance, NPCPrefabIdentity, the Customer/Dealer data
            // builders), not just in NPC's own constructor body. AllTypes (not module.Types) --
            // see that helper's doc comment for why nested/compiler-generated types matter here too.
            foreach (var type in AllTypes(module))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;

                    var body = method.Body;
                    var il = body.GetILProcessor();
                    // Iterate backwards since we're replacing instructions in place by index.
                    for (int i = body.Instructions.Count - 1; i >= 0; i--)
                    {
                        var instr = body.Instructions[i];
                        bool isCall = instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt;
                        if (!isCall || !(instr.Operand is MethodReference mref)) continue;

                        var declaringName = mref.DeclaringType?.Name;
                        if (declaringName == null) continue;

                        bool isKnownBroken = false;
                        for (int n = 0; n < BrokenNpcSetterCalls.Length; n++)
                        {
                            var (t, m) = BrokenNpcSetterCalls[n];
                            if (mref.Name == m && declaringName == t) { isKnownBroken = true; break; }
                        }
                        if (!isKnownBroken) continue;

                        var pop1 = il.Create(OpCodes.Pop);
                        var pop2 = il.Create(OpCodes.Pop);

                        // Insert the replacement pair *before* the call, redirect every reference
                        // to the call instruction (branch targets elsewhere in the method, AND
                        // exception-handler Try/Handler/Filter boundaries) onto pop1, THEN remove
                        // the call. Cecil's ILProcessor.Replace/Remove do NOT do this redirection
                        // themselves -- if the removed instruction happened to also be an
                        // ExceptionHandler boundary marker (this module has several EH-heavy
                        // methods, e.g. NPCPrefabBuilder.WithAppearanceDefaults has 4 EH regions),
                        // the naive Replace() left that boundary field pointing at an Instruction
                        // object no longer in the method body. Cecil's writer doesn't throw on
                        // that -- it silently serializes a stale/wrong byte offset -- so the DLL
                        // still loads fine and only the CLR's JIT-time IL verifier ever catches it,
                        // as InvalidProgramException. This was a real regression introduced by the
                        // original call->2-pops patch; fixed by always repointing references before
                        // removing.
                        il.InsertBefore(instr, pop1);
                        il.InsertBefore(instr, pop2);
                        RedirectReferences(body, instr, pop1);
                        il.Remove(instr);

                        patchedCount++;
                    }
                }
            }
            return patchedCount;
        }

        /// <summary>
        /// Repoints every reference to <paramref name="oldTarget"/> -- branch operands (including
        /// switch-statement target arrays) anywhere in <paramref name="body"/>, and exception-handler
        /// TryStart/TryEnd/HandlerStart/HandlerEnd/FilterStart boundaries -- onto
        /// <paramref name="newTarget"/>. Must be called before removing an instruction that might be
        /// referenced either way; see the comment in <see cref="PatchHasLastName"/> for why.
        /// </summary>
        private static void RedirectReferences(Mono.Cecil.Cil.MethodBody body, Instruction oldTarget, Instruction newTarget)
        {
            foreach (var other in body.Instructions)
            {
                if (other.Operand is Instruction single && single == oldTarget)
                    other.Operand = newTarget;
                else if (other.Operand is Instruction[] many)
                {
                    for (int k = 0; k < many.Length; k++)
                        if (many[k] == oldTarget) many[k] = newTarget;
                }
            }

            if (!body.HasExceptionHandlers) return;
            foreach (var eh in body.ExceptionHandlers)
            {
                if (eh.TryStart == oldTarget) eh.TryStart = newTarget;
                if (eh.TryEnd == oldTarget) eh.TryEnd = newTarget;
                if (eh.HandlerStart == oldTarget) eh.HandlerStart = newTarget;
                if (eh.HandlerEnd == oldTarget) eh.HandlerEnd = newTarget;
                if (eh.FilterStart == oldTarget) eh.FilterStart = newTarget;
            }
        }

        /// <summary>
        /// Yields every type in <paramref name="module"/>, INCLUDING nested types recursively --
        /// unlike a bare `module.Types` enumeration, which only covers top-level types.
        ///
        /// Found via the patch-harness (built to verify the try/catch getter fix below): every one of
        /// the ~100 built-in-NPC-lookup lambdas (npc => npc.ID == "...", one per named NPC like
        /// S1API.Entities.NPCs.Northtown.MickLubbin) compiles into a method on a compiler-generated
        /// nested closure type (e.g. MickLubbin/&lt;&gt;c), NOT a method directly on the outer type --
        /// same story for any lambda/local function capturing state, and for iterator/async state
        /// machines (MoveNext on a nested `&lt;Foo&gt;d__N` type). All of PatchHasLastName,
        /// PatchBrokenGetters, and RemoveStatementsReferencing (via PatchExitAction) used to iterate
        /// `module.Types` directly, meaning every broken call living inside one of these compiler-
        /// generated nested types was silently invisible to every patch in this file -- not just the
        /// getter fix being added here, but the ALREADY-DEPLOYED setter and ExitAction fixes too. Vic's
        /// own construction crash happened to route entirely through NPC's own directly-declared
        /// methods (confirmed via the harness -- zero "unexpected" misses there), so this gap didn't
        /// block progress so far, but it was silently leaving an unknown number of other broken calls
        /// (in lambdas, closures, state machines) completely unpatched anywhere in the module. Fixed
        /// once, here, for every patch that walks `module.Types` -- rather than finding this the same
        /// way the getter list itself was found: one crash at a time.
        /// </summary>
        private static System.Collections.Generic.IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            foreach (var type in module.Types)
                foreach (var t in FlattenWithNested(type))
                    yield return t;
        }

        /// <summary>Yields <paramref name="type"/> itself, then every nested type recursively. See <see cref="AllTypes"/>.</summary>
        private static System.Collections.Generic.IEnumerable<TypeDefinition> FlattenWithNested(TypeDefinition type)
        {
            yield return type;
            foreach (var nested in type.NestedTypes)
                foreach (var t in FlattenWithNested(nested))
                    yield return t;
        }

        /// <summary>
        /// Getters that are MISSING entirely from the real game type -- exactly the same root cause
        /// as BrokenNpcSetterCalls above, just the getter half instead of the setter half. Found by
        /// tracing the full call graph reachable from S1API.Entities.NPC..ctor() (same BFS technique
        /// used to build the setter list) and diffing every "get_X" call on an
        /// Il2CppScheduleOne.* type against the real game assembly (Cpp2IL's
        /// cpp2il_out/Assembly-CSharp.dll). Since the member genuinely doesn't exist anywhere on the
        /// type, calling it can never legitimately succeed regardless of which NPC instance it's
        /// called on -- so unlike BrokenGettersOnOwnS1NpcFieldOnly below, these are safe to
        /// neutralize at every call site in the whole module, not just ones reachable from
        /// construction. Confirmed: NPC.get_ConversationCategories (matches the recurring "Method not
        /// found" MissingMethodException seen live in
        /// EnsureConversationCategoriesInitialized/ResetConversationCategoriesToDefaults),
        /// NPC.get_intObj, NPCInventory.get_PickpocketIntObj, Behaviour.get_onDisable/get_onEnable,
        /// CustomerData.get_DefaultAffinityData.
        /// </summary>
        private static readonly (string DeclaringType, string MethodName)[] BrokenGettersMissingEverywhere =
        {
            ("NPC", "get_ConversationCategories"),
            ("NPC", "get_intObj"),
            ("NPCInventory", "get_PickpocketIntObj"),
            ("Behaviour", "get_onDisable"),
            ("Behaviour", "get_onEnable"),
            ("CustomerData", "get_DefaultAffinityData"),
        };

        /// <summary>
        /// Getters that EXIST on the real game type (calling them compiles and resolves fine
        /// everywhere) but throw internally when called on an NPC built via S1API's off-scene
        /// "prefab template" construction path (Activator.CreateInstance -> parameterless
        /// constructor), because that path never runs the normal in-scene spawn lifecycle that would
        /// populate whatever backing state the getter reads. Can't be found by diffing member lists
        /// (the member isn't missing) -- only by actually running the game and hitting the exception.
        /// Confirmed live: NPC.get_MugshotSprite (System.NullReferenceException, in NPC..ctor()'s "use
        /// default icon if none was set" check) and NPC.get_ID (same exception, in
        /// EnsureMessageConversationReady and other InitializeXxx helpers that read the NPC's own ID).
        ///
        /// UNLIKE BrokenGettersMissingEverywhere, these getters work perfectly fine when called on a
        /// real, already-spawned NPC -- e.g. S1API generates one lambda per built-in named NPC
        /// (S1API.Entities.NPCs.Northtown.MickLubbin, .Downtown.EugeneBuckley, etc.) whose whole job is
        /// `npc => npc.ID == "<built-in id>"`, comparing a REAL live game NPC's ID to find it.
        ///
        /// An earlier version of this fix tried to scope the pop;ldnull swap to only call sites shaped
        /// like `ldfld ...::S1NPC; call get_X` (S1API.Entities.NPC reading its own backing field) to
        /// avoid also neutralizing those lookup lambdas. That was proven insufficient two ways: (1) a
        /// harness run over every remaining call site still found 6 "unexpected" get_ID calls that
        /// didn't fit the field-scoped shape but still needed the same fix, and (2) more decisively, a
        /// grep confirmed EnsureMessageConversationReady -- which contains exactly the get_ID call site
        /// this list targets -- is legitimately called AGAIN, post-spawn, on a real live NPC from
        /// NPCPatches.cs:778. Since the exact same call site is sometimes reached with our not-yet-
        /// populated template and sometimes with a fully-populated live NPC, no static IL shape (field
        /// pattern, method name, declaring type, or otherwise) can safely distinguish "about to crash"
        /// from "about to succeed" here -- the two cases are the identical instruction sequence.
        ///
        /// So instead of removing the call, these are wrapped in a try/catch(Exception) via
        /// <see cref="WrapCallInTryCatchDefaultNull"/> that substitutes null ONLY if the call actually
        /// throws at runtime. This is safe unconditionally: a legitimate call on a live NPC just
        /// succeeds and the try/catch is a no-op overhead-wise, while a call on our off-scene template
        /// throws exactly as before but now gets caught and neutralized instead of crashing
        /// construction.
        /// </summary>
        private static readonly (string DeclaringType, string MethodName)[] BrokenGettersThrowOnOffSceneConstruction =
        {
            ("NPC", "get_MugshotSprite"),
            ("NPC", "get_ID"),
        };

        /// <summary>
        /// Applies both getter-fix lists above.
        ///
        /// BrokenGettersMissingEverywhere: each match is replaced with `pop; ldnull` -- pops the same
        /// instance reference the call would have consumed, then pushes a null reference in place of
        /// whatever the call would have returned. Stack-neutral (call: pop 1/push 1; replacement: pop
        /// 1/push 1), so it's a drop-in swap. Safe at every call site module-wide since the member
        /// genuinely doesn't exist anywhere. Uses the same EH/branch-reference redirect as
        /// <see cref="PatchHasLastName"/> -- removing an instruction that happens to be an
        /// exception-handler boundary or branch target without redirecting those references first
        /// corrupts the method.
        ///
        /// BrokenGettersThrowOnOffSceneConstruction: each match is wrapped in a try/catch via
        /// <see cref="WrapCallInTryCatchDefaultNull"/> instead of removed -- see that list's doc
        /// comment for why a blanket match is only safe with a runtime try/catch, not a static removal.
        ///
        /// Both lists only substitute null for reference-typed return values; a value-typed match is
        /// skipped with a warning rather than risking invalid IL, since no getter in either list needs
        /// that today.
        /// </summary>
        private static int PatchBrokenGetters(ModuleDefinition module)
        {
            int patchedCount = 0;
            foreach (var type in AllTypes(module))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;

                    var body = method.Body;
                    var il = body.GetILProcessor();

                    // Pass 1: BrokenGettersMissingEverywhere -- blanket pop;ldnull removal.
                    for (int i = body.Instructions.Count - 1; i >= 0; i--)
                    {
                        var instr = body.Instructions[i];
                        bool isCall = instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt;
                        if (!isCall || !(instr.Operand is MethodReference mref)) continue;

                        var declaringName = mref.DeclaringType?.Name;
                        if (declaringName == null) continue;

                        bool isMissingEverywhere = false;
                        for (int n = 0; n < BrokenGettersMissingEverywhere.Length; n++)
                        {
                            var (t, m) = BrokenGettersMissingEverywhere[n];
                            if (mref.Name == m && declaringName == t) { isMissingEverywhere = true; break; }
                        }
                        if (!isMissingEverywhere) continue;

                        if (mref.ReturnType.IsValueType)
                        {
                            Logger.Warning("Broken-getter patch: " + declaringName + "." + mref.Name +
                                " returns a value type -- ldnull can't stand in for it, skipping in " +
                                method.DeclaringType.FullName + "." + method.Name + " (non-fatal, needs a dedicated fix).");
                            continue;
                        }

                        var popThis = il.Create(OpCodes.Pop);
                        var pushNull = il.Create(OpCodes.Ldnull);
                        il.InsertBefore(instr, popThis);
                        il.InsertBefore(instr, pushNull);
                        RedirectReferences(body, instr, popThis);
                        il.Remove(instr);

                        patchedCount++;
                    }

                    // Pass 2: BrokenGettersThrowOnOffSceneConstruction -- wrap in try/catch instead of
                    // removing. This MUST snapshot the matching Instruction objects up front rather than
                    // walking body.Instructions by index (as pass 1 does) -- a plain descending index
                    // scan is only safe if every mutation lands strictly AFTER the current index (pure
                    // insertion, as pass 1 and the original version of this pass performed). Once
                    // WrapCallInTryCatchDefaultNull can RELOCATE a "extra" stack-value prefix (see
                    // TryFindRelocatablePrefix), it also REMOVES instructions that sit BEFORE the call
                    // being processed -- which shifts every later index down by the removed count. A
                    // descending index-based loop doesn't account for that shift, so after one relocating
                    // wrap it can land back on an index that now holds an instruction already processed
                    // (confirmed via the patch-harness: every call site needing relocation was visited
                    // twice, back-to-back, the second time against its own already-rewritten remains --
                    // usually a harmless no-op since the shape no longer matches, but in the null-
                    // conditional case where the second pass's re-derived bounds happened to still look
                    // valid, it actually SUCCEEDED AGAIN, adding a second, redundant nested try/catch
                    // around the same already-wrapped call). Snapshotting the actual Instruction
                    // references first sidesteps this: each matched instruction is visited exactly once,
                    // regardless of how much later processing reshuffles positions around it.
                    int throwOnOffSceneCount = 0;
                    for (int i = 0; i < body.Instructions.Count; i++)
                    {
                        var instr = body.Instructions[i];
                        bool isCall = instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt;
                        if (!isCall || !(instr.Operand is MethodReference mref)) continue;
                        var declaringName = mref.DeclaringType?.Name;
                        if (declaringName == null) continue;
                        for (int n = 0; n < BrokenGettersThrowOnOffSceneConstruction.Length; n++)
                        {
                            var (t, m) = BrokenGettersThrowOnOffSceneConstruction[n];
                            if (mref.Name == m && declaringName == t) { throwOnOffSceneCount++; break; }
                        }
                    }

                    if (throwOnOffSceneCount > 0)
                    {
                        var throwOnOffSceneCandidates = new Instruction[throwOnOffSceneCount];
                        int candidateWriteIdx = 0;
                        for (int i = 0; i < body.Instructions.Count; i++)
                        {
                            var instr = body.Instructions[i];
                            bool isCall = instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt;
                            if (!isCall || !(instr.Operand is MethodReference mref)) continue;
                            var declaringName = mref.DeclaringType?.Name;
                            if (declaringName == null) continue;

                            bool isThrowOnOffScene = false;
                            for (int n = 0; n < BrokenGettersThrowOnOffSceneConstruction.Length; n++)
                            {
                                var (t, m) = BrokenGettersThrowOnOffSceneConstruction[n];
                                if (mref.Name == m && declaringName == t) { isThrowOnOffScene = true; break; }
                            }
                            if (!isThrowOnOffScene) continue;

                            throwOnOffSceneCandidates[candidateWriteIdx++] = instr;
                        }

                        for (int c = 0; c < throwOnOffSceneCandidates.Length; c++)
                        {
                            if (WrapCallInTryCatchDefaultNull(method, throwOnOffSceneCandidates[c]))
                                patchedCount++;
                        }
                    }
                }
            }
            return patchedCount;
        }

        /// <summary>
        /// Wraps the expression that loads the target instance and calls <paramref name="callInstr"/>
        /// (a getter call) in a try/catch(System.Exception) that substitutes a null reference if the
        /// call throws, leaving the call itself untouched otherwise. Unlike the pop;ldnull swap used
        /// for BrokenGettersMissingEverywhere, this can't remove the call -- the exact same call site
        /// is legitimately reached both by our off-scene template (where it throws) and by real live
        /// NPCs elsewhere (where it must keep working), so the fix has to be a runtime fallback, not a
        /// static rewrite.
        ///
        /// First checks for the null-conditional (`s1Npc?.ID`) compiler idiom -- confirmed via the
        /// patch-harness to be the OVERWHELMINGLY dominant shape for these two getters throughout
        /// S1API, including NPC..ctor() and EnsureMessageConversationReady themselves -- and hands off
        /// to <see cref="NullConditionalWrap"/> if found, since that shape needs different handling
        /// (see its doc comment for why). Otherwise falls through to the plain case below.
        ///
        /// Plain case: the instance-loading instructions immediately before the call (e.g. `ldfld
        /// ...::S1NPC`, or just `ldarg`/`ldloc` for a parameter/local) are found via the same backward
        /// stack-balance walk used elsewhere in this file (see RemoveStatementsReferencing /
        /// GetPopCount / GetPushCount), except seeded with the CALL's own pop requirement (1, for the
        /// instance -- this getter takes no other arguments) instead of 0, since a getter call isn't
        /// stack-neutral by itself: it pushes the return value the rest of the statement goes on to
        /// consume.
        ///
        /// Once the range [start..callInstr] is found, the surrounding statement becomes:
        ///   try     { start..callInstr; stloc temp; leave landing }
        ///   catch   { pop; ldnull; stloc temp; leave landing }
        ///   landing:  ldloc temp   -- same single value on the stack the call itself used to leave,
        ///                             so everything after this point in the method is untouched.
        ///
        /// Skips (logs a warning, leaves the original call in place -- i.e. no worse than before this
        /// fix) if: the backward walk can't cleanly resolve (crosses a branch/loop boundary), any
        /// instruction strictly inside the range besides the start itself is targeted by a branch or
        /// exception-handler boundary from elsewhere in the method (would mean jumping into the middle
        /// of the new try region, which is invalid IL), or the return type is a value type (ldnull
        /// needs a reference-typed local).
        /// </summary>
        private static bool WrapCallInTryCatchDefaultNull(MethodDefinition method, Instruction callInstr)
        {
            var body = method.Body;
            var il = body.GetILProcessor();

            if (!(callInstr.Operand is MethodReference mref)) return false;
            if (mref.ReturnType.IsValueType)
            {
                Logger.Warning("Broken-getter try/catch patch: " + mref.DeclaringType?.Name + "." + mref.Name +
                    " returns a value type -- this technique needs a reference-typed local, skipping in " +
                    method.DeclaringType.FullName + "." + method.Name + " (non-fatal, needs a dedicated fix).");
                return false;
            }

            Instruction condBr = FindBranchTargeting(body, callInstr);
            if (condBr != null && (condBr.OpCode == OpCodes.Brtrue || condBr.OpCode == OpCodes.Brtrue_S) &&
                condBr.Previous != null && condBr.Previous.OpCode == OpCodes.Dup)
            {
                return NullConditionalWrap(method, callInstr, condBr, mref);
            }

            // Reload-based variant of the same `x?.Y` idiom: when x is a cheap, side-effect-free
            // ldarg/ldloc, the compiler skips dup+pop entirely and just reloads the same parameter/local
            // a second time after the branch instead of keeping a duplicate on the stack. Confirmed via
            // the patch-harness in CreateWrapperForNetworkSpawnedNPC's own exception-logging code
            // (`baseNpc?.ID` where baseNpc is a method parameter): [ldarg N; brtrue L; ldnull; br M; L:
            // ldarg N; call get_X(); M: ...] -- no pop before the fallback ldnull, since brtrue already
            // fully consumed the one-and-only copy of the value. Crucially, the branch here targets the
            // RELOAD instruction immediately before the call, not the call itself, so it has to be
            // looked up separately from the dup case's condBr (which does target callInstr directly).
            Instruction condBrReload = callInstr.Previous != null ? FindBranchTargeting(body, callInstr.Previous) : null;
            if (condBrReload != null && (condBrReload.OpCode == OpCodes.Brtrue || condBrReload.OpCode == OpCodes.Brtrue_S) &&
                condBrReload.Previous != null &&
                TryGetLoadIndex(condBrReload.Previous, out bool isArg1, out int idx1) &&
                TryGetLoadIndex(callInstr.Previous, out bool isArg2, out int idx2) &&
                isArg1 == isArg2 && idx1 == idx2)
            {
                return NullConditionalWrapReload(method, callInstr, condBrReload, mref);
            }

            int callIndex = body.Instructions.IndexOf(callInstr);
            if (callIndex < 0) return false;

            int needed = GetPopCount(callInstr);
            int idx = callIndex - 1;
            while (needed > 0 && idx >= 0)
            {
                needed -= GetPushCount(body.Instructions[idx]);
                needed += GetPopCount(body.Instructions[idx]);
                idx--;
            }
            int startIndex = idx + 1;

            if (needed != 0)
            {
                Logger.Warning("Broken-getter try/catch patch: could not cleanly bound the statement around " +
                    mref.Name + " in " + method.DeclaringType.FullName + "." + method.Name + " -- skipping that occurrence (non-fatal).");
                return false;
            }

            // "needed == 0" only proves the SPECIFIC value(s) callInstr consumes have been fully
            // accounted for walking backward this far -- it says nothing about whether OTHER,
            // unrelated values are ALSO sitting on the stack at this point (e.g. this call is the
            // 3rd argument of a 4-argument String.Concat -- the first two arguments are already
            // pushed and waiting). A try region requires the stack to be genuinely EMPTY at
            // TryStart, for every way of reaching it, not just "balanced relative to one consumer" --
            // see ComputeStackDepths' doc comment for how this is verified properly. If the stack
            // isn't empty, try to relocate the offending prefix out of the way instead of giving up
            // outright -- see TryFindRelocatablePrefix's doc comment for why that's safe and when it
            // isn't possible.
            int[] depths = ComputeStackDepths(body);
            Instruction[] relocate = Array.Empty<Instruction>();
            if (depths[startIndex] != 0)
            {
                relocate = TryFindRelocatablePrefix(body, startIndex, depths[startIndex]);
                if (relocate == null)
                {
                    Logger.Warning("Broken-getter try/catch patch: statement around " + mref.Name + " in " +
                        method.DeclaringType.FullName + "." + method.Name + " does not start with an empty evaluation stack (depth " +
                        depths[startIndex] + ") and the preceding value(s) aren't safely relocatable -- skipping that occurrence (non-fatal).");
                    return false;
                }
            }

            var start = body.Instructions[startIndex];

            // Refuse if anything strictly inside (start, callInstr] is targeted by a branch/EH
            // boundary from elsewhere in the method -- that would mean jumping into the middle of the
            // new try region, which is invalid IL. (Branching to `start` itself is fine -- that's the
            // try region's legitimate entry point.)
            for (int r = startIndex + 1; r <= callIndex; r++)
            {
                var candidate = body.Instructions[r];
                bool isTarget = false;
                foreach (var other in body.Instructions)
                {
                    if (other == candidate) continue;
                    if (other.Operand is Instruction t && t == candidate) { isTarget = true; break; }
                    if (other.Operand is Instruction[] ts)
                    {
                        foreach (var tt in ts) if (tt == candidate) { isTarget = true; break; }
                        if (isTarget) break;
                    }
                }
                if (!isTarget && body.HasExceptionHandlers)
                {
                    foreach (var eh in body.ExceptionHandlers)
                    {
                        if (eh.TryStart == candidate || eh.TryEnd == candidate || eh.HandlerStart == candidate ||
                            eh.HandlerEnd == candidate || eh.FilterStart == candidate) { isTarget = true; break; }
                    }
                }
                if (isTarget)
                {
                    Logger.Warning("Broken-getter try/catch patch: statement around " + mref.Name + " in " +
                        method.DeclaringType.FullName + "." + method.Name + " has a branch/EH boundary landing mid-statement -- skipping that occurrence (non-fatal).");
                    return false;
                }
            }

            // Detach the relocatable prefix now (before it's shadowed by anything we insert) --
            // RemoveExactly is safe here since TryFindRelocatablePrefix already proved none of these
            // instructions are themselves referenced from elsewhere.
            foreach (var r in relocate) il.Remove(r);

            var exceptionType = FindExceptionCatchType(method.Module);
            var tempLocal = new VariableDefinition(mref.ReturnType);
            body.Variables.Add(tempLocal);

            var stlocOk = il.Create(OpCodes.Stloc, tempLocal);
            var popEx = il.Create(OpCodes.Pop);
            var ldnullEx = il.Create(OpCodes.Ldnull);
            var stlocEx = il.Create(OpCodes.Stloc, tempLocal);
            var landing = il.Create(OpCodes.Nop);
            var loadResult = il.Create(OpCodes.Ldloc, tempLocal);
            var leaveOk = il.Create(OpCodes.Leave, landing);
            var leaveEx = il.Create(OpCodes.Leave, landing);

            // Splice in, in order, right after the original call instruction. [start..callInstr] is
            // left completely unchanged and becomes the try body.
            il.InsertAfter(callInstr, stlocOk);
            il.InsertAfter(stlocOk, leaveOk);
            il.InsertAfter(leaveOk, popEx);
            il.InsertAfter(popEx, ldnullEx);
            il.InsertAfter(ldnullEx, stlocEx);
            il.InsertAfter(stlocEx, leaveEx);
            il.InsertAfter(leaveEx, landing);

            // Re-emit the relocated prefix (same Instruction objects, so any FAR-AWAY reference
            // outside this whole region -- already proven not to exist by TryFindRelocatablePrefix,
            // but re-stated here for clarity -- would remain valid regardless) right after landing,
            // then push our real-or-substituted result LAST so the final stack order matches exactly
            // what the original code before this call expected: [relocated values..., our result].
            var insertAfter = landing;
            foreach (var r in relocate) { il.InsertAfter(insertAfter, r); insertAfter = r; }
            il.InsertAfter(insertAfter, loadResult);

            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                CatchType = exceptionType,
                TryStart = start,
                TryEnd = popEx,
                HandlerStart = popEx,
                HandlerEnd = landing,
            });

            return true;
        }

        /// <summary>
        /// Handles the `x?.Member` null-conditional idiom the C# compiler emits around
        /// get_ID/get_MugshotSprite call sites throughout S1API -- confirmed via the patch-harness to
        /// be the dominant shape, not an edge case (NPC..ctor(), EnsureMessageConversationReady, and
        /// most other reachable call sites all use it). Recognized shape:
        ///
        ///   [instance-load]     -- pushes the target instance; stack is empty beforehand
        ///   dup
        ///   brtrue.s L          -- condBr; jumps directly to the call when the instance is non-null
        ///   pop
        ///   ldnull
        ///   br.s M              -- M == callInstr.Next; the merge point where either path's value is used
        ///   L: call get_X()     -- == callInstr
        ///   M: ...
        ///
        /// The generic backward-walk in <see cref="WrapCallInTryCatchDefaultNull"/> mis-binds this: it
        /// finds "ldnull; br.s" nets to zero stack balance and treats that as the whole statement, when
        /// the call is actually reached via a completely different control-flow path (the brtrue jump)
        /// -- its own safety check then (correctly) refuses to touch it, since callInstr is itself a
        /// branch target, but that means this dominant shape would otherwise never get fixed at all.
        ///
        /// This method recognizes the idiom explicitly and rewrites the whole contiguous range from
        /// where the instance starts loading through the call into a single try region: entry is at
        /// the instance-load, with an empty stack exactly like the plain case, then execution branches
        /// internally (via the untouched condBr) to the call when non-null. The existing null-fallback's
        /// `br.s M` is rewritten in place (same Instruction object, so anything that happened to
        /// reference it stays valid) into `stloc temp; leave landing` -- it's now exiting a protected
        /// region, which requires `leave` rather than a bare `br`, and the stack (currently just the
        /// fallback `null`) has to be stashed in the same shared temp the try/catch already uses rather
        /// than carried across the leave. The call itself gets the same `stloc temp; leave landing`
        /// appended, and a catch(Exception) does the same null substitution as the plain case.
        /// `landing: ldloc temp` then continues exactly where M used to start.
        ///
        /// Skips (logs a warning, leaves the original call in place) if the instance-load can't be
        /// cleanly bounded, the fallback block doesn't match this exact shape (some other null-
        /// conditional variant this technique hasn't been taught), or anything else in the range is
        /// targeted from elsewhere in the method -- all non-fatal, same safety posture as the plain case.
        /// </summary>
        private static bool NullConditionalWrap(MethodDefinition method, Instruction callInstr, Instruction condBr, MethodReference mref)
        {
            var body = method.Body;
            var il = body.GetILProcessor();

            var dup = condBr.Previous;
            int dupIndex = body.Instructions.IndexOf(dup);
            if (dupIndex < 0) return false;

            int needed = GetPopCount(dup);
            int idx = dupIndex - 1;
            while (needed > 0 && idx >= 0)
            {
                needed -= GetPushCount(body.Instructions[idx]);
                needed += GetPopCount(body.Instructions[idx]);
                idx--;
            }
            int startIndex = idx + 1;

            if (needed != 0)
            {
                Logger.Warning("Broken-getter try/catch patch: could not cleanly bound the null-conditional instance load around " +
                    mref.Name + " in " + method.DeclaringType.FullName + "." + method.Name + " -- skipping that occurrence (non-fatal).");
                return false;
            }

            // Same reasoning as the plain case's identical check -- see WrapCallInTryCatchDefaultNull.
            // Confirmed via real-world testing to matter here too: EnsureMessageConversationReady and
            // EnsureMessageConversationInstance both build log/diagnostic messages where `S1NPC?.ID` is
            // the 2nd/3rd argument of a String.Concat -- earlier arguments (a Logger reference, a
            // string literal) are already pushed and waiting when the null-conditional load begins, so
            // "needed == 0" was satisfied while the actual stack was very much non-empty. That produced
            // a real InvalidProgramException in-game before this check was added. Try relocating the
            // offending prefix (see TryFindRelocatablePrefix) before giving up.
            int[] depths = ComputeStackDepths(body);
            Instruction[] relocate = Array.Empty<Instruction>();
            if (depths[startIndex] != 0)
            {
                relocate = TryFindRelocatablePrefix(body, startIndex, depths[startIndex]);
                if (relocate == null)
                {
                    Logger.Warning("Broken-getter try/catch patch: null-conditional statement around " + mref.Name + " in " +
                        method.DeclaringType.FullName + "." + method.Name + " does not start with an empty evaluation stack (depth " +
                        depths[startIndex] + ") and the preceding value(s) aren't safely relocatable -- skipping that occurrence (non-fatal).");
                    return false;
                }
            }

            var start = body.Instructions[startIndex];

            var popFallback = condBr.Next;
            var ldnullFallback = popFallback?.Next;
            var brFallback = ldnullFallback?.Next;
            bool shapeMatches = popFallback != null && popFallback.OpCode == OpCodes.Pop &&
                ldnullFallback != null && ldnullFallback.OpCode == OpCodes.Ldnull &&
                brFallback != null && (brFallback.OpCode == OpCodes.Br || brFallback.OpCode == OpCodes.Br_S) &&
                brFallback.Operand is Instruction brTarget && brTarget == callInstr.Next &&
                brFallback.Next == callInstr;

            if (!shapeMatches)
            {
                Logger.Warning("Broken-getter try/catch patch: " + mref.Name + " in " + method.DeclaringType.FullName + "." + method.Name +
                    " looked like a null-conditional access but didn't match the expected shape exactly -- skipping that occurrence (non-fatal).");
                return false;
            }

            int callIndex = body.Instructions.IndexOf(callInstr);

            // Refuse if anything strictly inside (start, callInstr) -- excluding callInstr itself,
            // which we KNOW is targeted by condBr and is handled by construction -- is targeted by some
            // OTHER branch/EH boundary from elsewhere in the method. That would mean a stray jump into
            // the middle of the new composite try region.
            for (int r = startIndex + 1; r < callIndex; r++)
            {
                var candidate = body.Instructions[r];
                bool isTarget = false;
                foreach (var other in body.Instructions)
                {
                    if (other == candidate) continue;
                    if (other.Operand is Instruction t && t == candidate) { isTarget = true; break; }
                    if (other.Operand is Instruction[] ts)
                    {
                        foreach (var tt in ts) if (tt == candidate) { isTarget = true; break; }
                        if (isTarget) break;
                    }
                }
                if (!isTarget && body.HasExceptionHandlers)
                {
                    foreach (var eh in body.ExceptionHandlers)
                    {
                        if (eh.TryStart == candidate || eh.TryEnd == candidate || eh.HandlerStart == candidate ||
                            eh.HandlerEnd == candidate || eh.FilterStart == candidate) { isTarget = true; break; }
                    }
                }
                if (isTarget)
                {
                    Logger.Warning("Broken-getter try/catch patch: null-conditional statement around " + mref.Name + " in " +
                        method.DeclaringType.FullName + "." + method.Name + " has an unexpected branch/EH boundary landing mid-statement -- skipping that occurrence (non-fatal).");
                    return false;
                }
            }

            // Detach the relocatable prefix now, before it's shadowed by anything we insert --
            // TryFindRelocatablePrefix already proved none of these instructions are themselves
            // referenced from elsewhere.
            foreach (var r in relocate) il.Remove(r);

            var exceptionType = FindExceptionCatchType(method.Module);
            var tempLocal = new VariableDefinition(mref.ReturnType);
            body.Variables.Add(tempLocal);

            var stlocOk = il.Create(OpCodes.Stloc, tempLocal);
            var popEx = il.Create(OpCodes.Pop);
            var ldnullEx = il.Create(OpCodes.Ldnull);
            var stlocEx = il.Create(OpCodes.Stloc, tempLocal);
            var landing = il.Create(OpCodes.Nop);
            var loadResult = il.Create(OpCodes.Ldloc, tempLocal);
            var leaveOk = il.Create(OpCodes.Leave, landing);

            // Rewrite the existing fallback's "br(.s) M" in place -- same Instruction object, so
            // anything that happened to reference it (nothing should, in the confirmed shape, but this
            // is free insurance) stays valid without needing RedirectReferences.
            var stlocFallback = il.Create(OpCodes.Stloc, tempLocal);
            il.InsertBefore(brFallback, stlocFallback);
            brFallback.OpCode = OpCodes.Leave;
            brFallback.Operand = landing;

            // Normal-completion path: right after the call itself.
            il.InsertAfter(callInstr, stlocOk);
            il.InsertAfter(stlocOk, leaveOk);
            il.InsertAfter(leaveOk, popEx);
            il.InsertAfter(popEx, ldnullEx);
            il.InsertAfter(ldnullEx, stlocEx);
            var leaveEx = il.Create(OpCodes.Leave, landing);
            il.InsertAfter(stlocEx, leaveEx);
            il.InsertAfter(leaveEx, landing);

            // Re-emit the relocated prefix right after landing, then push our real-or-substituted
            // result LAST so the final stack order matches exactly what the original code expected:
            // [relocated values..., our result]. See the identical comment in
            // WrapCallInTryCatchDefaultNull for the full reasoning.
            var insertAfter = landing;
            foreach (var r in relocate) { il.InsertAfter(insertAfter, r); insertAfter = r; }
            il.InsertAfter(insertAfter, loadResult);

            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                CatchType = exceptionType,
                TryStart = start,
                TryEnd = popEx,
                HandlerStart = popEx,
                HandlerEnd = landing,
            });

            return true;
        }

        /// <summary>
        /// If <paramref name="instr"/> is a simple, side-effect-free "ldarg N" or "ldloc N" (any of the
        /// short-form, long-form, or indexed opcodes), returns true and reports which kind and index.
        /// Used to recognize the reload-based null-conditional idiom -- see
        /// <see cref="NullConditionalWrapReload"/> -- by comparing the load before the `brtrue` check
        /// against the reload right before the getter call: same kind + same index means "same
        /// parameter/local reloaded twice", which is exactly what the compiler emits instead of
        /// dup+pop when the instance expression is cheap enough to just re-evaluate.
        /// </summary>
        private static bool TryGetLoadIndex(Instruction instr, out bool isArg, out int index)
        {
            isArg = false;
            index = -1;
            if (instr == null) return false;

            var op = instr.OpCode;
            if (op == OpCodes.Ldarg_0) { isArg = true; index = 0; return true; }
            if (op == OpCodes.Ldarg_1) { isArg = true; index = 1; return true; }
            if (op == OpCodes.Ldarg_2) { isArg = true; index = 2; return true; }
            if (op == OpCodes.Ldarg_3) { isArg = true; index = 3; return true; }
            if (op == OpCodes.Ldarg || op == OpCodes.Ldarg_S)
            {
                if (instr.Operand is ParameterDefinition pd) { isArg = true; index = pd.Index; return true; }
                return false;
            }
            if (op == OpCodes.Ldloc_0) { isArg = false; index = 0; return true; }
            if (op == OpCodes.Ldloc_1) { isArg = false; index = 1; return true; }
            if (op == OpCodes.Ldloc_2) { isArg = false; index = 2; return true; }
            if (op == OpCodes.Ldloc_3) { isArg = false; index = 3; return true; }
            if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S)
            {
                if (instr.Operand is VariableDefinition vd) { isArg = false; index = vd.Index; return true; }
                return false;
            }
            return false;
        }

        /// <summary>
        /// Handles the reload-based variant of the `x?.Member` idiom -- see the doc comment where this
        /// is dispatched from <see cref="WrapCallInTryCatchDefaultNull"/> for the exact shape. Confirmed
        /// via the patch-harness in <c>CreateWrapperForNetworkSpawnedNPC</c>'s own exception-logging code
        /// (`baseNpc?.ID` where baseNpc is a method parameter, inside the catch handler that reports why
        /// wrapper construction failed):
        ///
        ///   [instance-load]     -- e.g. ldarg.1; stack is empty beforehand
        ///   brtrue.s L          -- condBr; fully consumes the one-and-only copy of the value
        ///   ldnull              -- fallback: NO pop first (nothing left to discard, unlike the dup case)
        ///   br.s M              -- M == callInstr.Next
        ///   L: [instance-reload]  -- same parameter/local reloaded fresh
        ///      call get_X()     -- == callInstr
        ///   M: ...
        ///
        /// Structurally identical to <see cref="NullConditionalWrap"/> otherwise: same try/catch
        /// mechanics, same stack-depth verification + relocation for the "extra values already on the
        /// stack" problem, same in-place rewrite of the fallback's `br(.s)` into `stloc temp; leave
        /// landing`. The only differences are (a) bounding starts from the first instance-load rather
        /// than a `dup`, and (b) the fallback shape has no leading `pop`.
        /// </summary>
        private static bool NullConditionalWrapReload(MethodDefinition method, Instruction callInstr, Instruction condBr, MethodReference mref)
        {
            var body = method.Body;
            var il = body.GetILProcessor();

            var loadFirst = condBr.Previous;
            int loadIndex = body.Instructions.IndexOf(loadFirst);
            if (loadIndex < 0) return false;

            int needed = GetPopCount(loadFirst);
            int idx = loadIndex - 1;
            while (needed > 0 && idx >= 0)
            {
                needed -= GetPushCount(body.Instructions[idx]);
                needed += GetPopCount(body.Instructions[idx]);
                idx--;
            }
            int startIndex = idx + 1;

            if (needed != 0)
            {
                Logger.Warning("Broken-getter try/catch patch: could not cleanly bound the reload-based null-conditional instance load around " +
                    mref.Name + " in " + method.DeclaringType.FullName + "." + method.Name + " -- skipping that occurrence (non-fatal).");
                return false;
            }

            // Same reasoning as NullConditionalWrap's identical check.
            int[] depths = ComputeStackDepths(body);
            Instruction[] relocate = Array.Empty<Instruction>();
            if (depths[startIndex] != 0)
            {
                relocate = TryFindRelocatablePrefix(body, startIndex, depths[startIndex]);
                if (relocate == null)
                {
                    Logger.Warning("Broken-getter try/catch patch: reload-based null-conditional statement around " + mref.Name + " in " +
                        method.DeclaringType.FullName + "." + method.Name + " does not start with an empty evaluation stack (depth " +
                        depths[startIndex] + ") and the preceding value(s) aren't safely relocatable -- skipping that occurrence (non-fatal).");
                    return false;
                }
            }

            var start = body.Instructions[startIndex];

            var ldnullFallback = condBr.Next;
            var brFallback = ldnullFallback?.Next;
            bool shapeMatches = ldnullFallback != null && ldnullFallback.OpCode == OpCodes.Ldnull &&
                brFallback != null && (brFallback.OpCode == OpCodes.Br || brFallback.OpCode == OpCodes.Br_S) &&
                brFallback.Operand is Instruction brTarget && brTarget == callInstr.Next &&
                brFallback.Next == callInstr.Previous;

            if (!shapeMatches)
            {
                Logger.Warning("Broken-getter try/catch patch: " + mref.Name + " in " + method.DeclaringType.FullName + "." + method.Name +
                    " looked like a reload-based null-conditional access but didn't match the expected shape exactly -- skipping that occurrence (non-fatal).");
                return false;
            }

            int callIndex = body.Instructions.IndexOf(callInstr);
            int reloadIndex = callIndex - 1;

            // Refuse if anything strictly inside (start, callInstr] -- excluding the reload instruction
            // right before callInstr, which we KNOW is targeted by condBr and is handled by construction
            // -- is targeted by some OTHER branch/EH boundary from elsewhere in the method.
            for (int r = startIndex + 1; r <= callIndex; r++)
            {
                if (r == reloadIndex) continue;
                var candidate = body.Instructions[r];
                bool isTarget = false;
                foreach (var other in body.Instructions)
                {
                    if (other == candidate) continue;
                    if (other.Operand is Instruction t && t == candidate) { isTarget = true; break; }
                    if (other.Operand is Instruction[] ts)
                    {
                        foreach (var tt in ts) if (tt == candidate) { isTarget = true; break; }
                        if (isTarget) break;
                    }
                }
                if (!isTarget && body.HasExceptionHandlers)
                {
                    foreach (var eh in body.ExceptionHandlers)
                    {
                        if (eh.TryStart == candidate || eh.TryEnd == candidate || eh.HandlerStart == candidate ||
                            eh.HandlerEnd == candidate || eh.FilterStart == candidate) { isTarget = true; break; }
                    }
                }
                if (isTarget)
                {
                    Logger.Warning("Broken-getter try/catch patch: reload-based null-conditional statement around " + mref.Name + " in " +
                        method.DeclaringType.FullName + "." + method.Name + " has an unexpected branch/EH boundary landing mid-statement -- skipping that occurrence (non-fatal).");
                    return false;
                }
            }

            foreach (var r in relocate) il.Remove(r);

            var exceptionType = FindExceptionCatchType(method.Module);
            var tempLocal = new VariableDefinition(mref.ReturnType);
            body.Variables.Add(tempLocal);

            var stlocOk = il.Create(OpCodes.Stloc, tempLocal);
            var popEx = il.Create(OpCodes.Pop);
            var ldnullEx = il.Create(OpCodes.Ldnull);
            var stlocEx = il.Create(OpCodes.Stloc, tempLocal);
            var landing = il.Create(OpCodes.Nop);
            var loadResult = il.Create(OpCodes.Ldloc, tempLocal);
            var leaveOk = il.Create(OpCodes.Leave, landing);

            // Rewrite the existing fallback's "br(.s) M" in place, same as NullConditionalWrap.
            var stlocFallback = il.Create(OpCodes.Stloc, tempLocal);
            il.InsertBefore(brFallback, stlocFallback);
            brFallback.OpCode = OpCodes.Leave;
            brFallback.Operand = landing;

            il.InsertAfter(callInstr, stlocOk);
            il.InsertAfter(stlocOk, leaveOk);
            il.InsertAfter(leaveOk, popEx);
            il.InsertAfter(popEx, ldnullEx);
            il.InsertAfter(ldnullEx, stlocEx);
            var leaveEx = il.Create(OpCodes.Leave, landing);
            il.InsertAfter(stlocEx, leaveEx);
            il.InsertAfter(leaveEx, landing);

            var insertAfter = landing;
            foreach (var r in relocate) { il.InsertAfter(insertAfter, r); insertAfter = r; }
            il.InsertAfter(insertAfter, loadResult);

            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                CatchType = exceptionType,
                TryStart = start,
                TryEnd = popEx,
                HandlerStart = popEx,
                HandlerEnd = landing,
            });

            return true;
        }

        /// <summary>
        /// Returns the first instruction in <paramref name="body"/> whose operand is a direct branch
        /// reference to <paramref name="target"/> (a plain single-instruction branch operand, not a
        /// switch-target array), or null if none exists. Used to detect the null-conditional idiom's
        /// `brtrue.s` jumping directly to a getter call -- see <see cref="NullConditionalWrap"/>.
        /// </summary>
        private static Instruction FindBranchTargeting(Mono.Cecil.Cil.MethodBody body, Instruction target)
        {
            foreach (var instr in body.Instructions)
            {
                if (instr.Operand is Instruction single && single == target) return instr;
            }
            return null;
        }

        /// <summary>
        /// Finds an existing `System.Exception` TypeReference already present in this module (e.g. the
        /// Catch region NPCPrefabBuilder.WithAppearanceDefaults already has) to reuse as the CatchType
        /// for new handlers added by <see cref="WrapCallInTryCatchDefaultNull"/> -- avoids any risk of
        /// importing a corlib reference that doesn't exactly match the identity/version this module was
        /// already compiled against. Falls back to importing typeof(Exception) if no existing Catch
        /// region is found.
        /// </summary>
        private static TypeReference FindExceptionCatchType(ModuleDefinition module)
        {
            foreach (var type in AllTypes(module))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasExceptionHandlers) continue;
                    foreach (var eh in method.Body.ExceptionHandlers)
                    {
                        if (eh.HandlerType == ExceptionHandlerType.Catch && eh.CatchType != null &&
                            eh.CatchType.FullName == "System.Exception")
                            return eh.CatchType;
                    }
                }
            }
            return module.ImportReference(typeof(Exception));
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

            // Include nested types (compiler-generated closures/lambdas capturing locals, iterator
            // and async state machines) -- see AllTypes' doc comment for why this matters. PhoneApp's
            // exit-chain wiring is exactly the kind of code that gets compiled into a closure when it
            // captures outer locals for a delegate.
            foreach (var nestedOrSelfType in FlattenWithNested(type))
            foreach (var method in nestedOrSelfType.Methods)
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

        /// <summary>
        /// Computes the evaluation-stack depth immediately BEFORE each instruction in
        /// <paramref name="body"/>, returned as a parallel array indexed the same as
        /// <c>body.Instructions</c> (-1 for any instruction never proven reachable from a known-depth
        /// point, e.g. genuinely dead code).
        ///
        /// Needed because "local" stack-balance walks (like the ones in
        /// <see cref="WrapCallInTryCatchDefaultNull"/> and <see cref="NullConditionalWrap"/>, and in
        /// <see cref="RemoveStatementsReferencing"/>) only prove that ONE particular downstream
        /// consumer's inputs have been fully accounted for walking backward -- they say nothing about
        /// whether OTHER, unrelated values are ALSO sitting on the stack at that point (e.g. a call is
        /// the 3rd argument of a 4-argument String.Concat -- the first two arguments are already
        /// pushed and waiting, invisible to a walk that only tracks what ITS OWN caller needs). A new
        /// try region requires the stack to be genuinely EMPTY at TryStart, for every way of reaching
        /// it -- confirmed the hard way: EnsureMessageConversationReady's `S1NPC?.ID` read is the 3rd
        /// Concat argument inside a catch handler's logging code, and wrapping just that local
        /// expression (ignoring the two already-pushed arguments underneath) produced IL that passed
        /// every other check here but still threw InvalidProgramException in-game.
        ///
        /// Standard depth-propagation worklist, seeded at every point guaranteed to have a known depth
        /// (method entry = 0; each try region's TryStart = 0; each catch/filter HandlerStart = 1, for
        /// the exception object the CLR pushes; each finally/fault HandlerStart = 0; each FilterStart =
        /// 1), then relaxed forward across fallthrough and branch edges using GetPopCount/GetPushCount
        /// until no instruction's depth changes. Plain arrays throughout (no List/Dictionary/Queue) --
        /// same Mono-compatibility reason as everywhere else in this file (see LoaderConfig.Whitelist).
        /// </summary>
        private static int[] ComputeStackDepths(Mono.Cecil.Cil.MethodBody body)
        {
            int n = body.Instructions.Count;
            int[] depth = new int[n];
            for (int i = 0; i < n; i++) depth[i] = -1;
            if (n == 0) return depth;

            depth[0] = 0;
            if (body.HasExceptionHandlers)
            {
                foreach (var eh in body.ExceptionHandlers)
                {
                    int tryIdx = body.Instructions.IndexOf(eh.TryStart);
                    if (tryIdx >= 0) depth[tryIdx] = 0;

                    int handlerIdx = eh.HandlerStart != null ? body.Instructions.IndexOf(eh.HandlerStart) : -1;
                    if (handlerIdx >= 0)
                    {
                        bool pushesException = eh.HandlerType == ExceptionHandlerType.Catch || eh.HandlerType == ExceptionHandlerType.Filter;
                        depth[handlerIdx] = pushesException ? 1 : 0;
                    }

                    if (eh.FilterStart != null)
                    {
                        int filterIdx = body.Instructions.IndexOf(eh.FilterStart);
                        if (filterIdx >= 0) depth[filterIdx] = 1;
                    }
                }
            }

            for (int pass = 0; pass < 4; pass++)
            {
                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    if (depth[i] < 0) continue;
                    var instr = body.Instructions[i];
                    int after = depth[i] - GetPopCount(instr) + GetPushCount(instr);

                    bool falls = true;
                    if (instr.OpCode == OpCodes.Br || instr.OpCode == OpCodes.Br_S ||
                        instr.OpCode == OpCodes.Leave || instr.OpCode == OpCodes.Leave_S ||
                        instr.OpCode == OpCodes.Ret || instr.OpCode == OpCodes.Throw ||
                        instr.OpCode == OpCodes.Endfinally || instr.OpCode == OpCodes.Endfilter ||
                        instr.OpCode == OpCodes.Rethrow)
                        falls = false;

                    if (falls && i + 1 < n && depth[i + 1] < 0) { depth[i + 1] = after; changed = true; }

                    if (instr.Operand is Instruction single)
                    {
                        int ti = body.Instructions.IndexOf(single);
                        if (ti >= 0 && depth[ti] < 0) { depth[ti] = after; changed = true; }
                    }
                    else if (instr.Operand is Instruction[] many)
                    {
                        for (int k = 0; k < many.Length; k++)
                        {
                            int ti = body.Instructions.IndexOf(many[k]);
                            if (ti >= 0 && depth[ti] < 0) { depth[ti] = after; changed = true; }
                        }
                    }
                }
                if (!changed) break;
            }

            return depth;
        }

        /// <summary>
        /// When ComputeStackDepths shows <paramref name="extra"/> unrelated values already sitting on
        /// the stack below <paramref name="startIndex"/>, this looks for a way to make wrapping
        /// possible anyway by RELOCATING those <paramref name="extra"/> immediately-preceding
        /// instructions to run AFTER the new try/catch instead of before it, rather than giving up.
        ///
        /// This needs no type inference (unlike hoisting into typed temp locals would) because it only
        /// fires when each of the <paramref name="extra"/> instructions is a "pure" single-value load
        /// (Push1/Pop0 -- e.g. ldsfld/ldstr/ldarg/ldfld/ldloc; nothing that consumes anything or has
        /// any other stack effect) that is not itself targeted by any branch or exception-handler
        /// boundary elsewhere in the method. Moving such an instruction changes only WHEN it executes,
        /// not WHAT it produces (same field/arg/local/literal read, still idempotent), and confirming
        /// no existing reference points at it means relocating can't silently corrupt some OTHER
        /// branch/EH region that happened to use it as a boundary marker.
        ///
        /// Confirmed exactly this shape in practice: EnsureMessageConversationReady and
        /// EnsureMessageConversationInstance both build a log/diagnostic message via String.Concat,
        /// where `S1NPC?.ID` is a middle argument and a Logger reference plus a string literal are
        /// already pushed as earlier Concat arguments -- exactly two pure, reference-typed loads
        /// immediately before the null-conditional's own instance-load begins.
        ///
        /// Returns the instructions to relocate (in original order, empty array if extra is 0), or
        /// null if the preceding instructions don't all match this safe shape (caller should then give
        /// up as before).
        /// </summary>
        private static Instruction[] TryFindRelocatablePrefix(Mono.Cecil.Cil.MethodBody body, int startIndex, int extra)
        {
            if (extra <= 0) return Array.Empty<Instruction>();
            if (startIndex - extra < 0) return null;

            var candidates = new Instruction[extra];
            for (int k = 0; k < extra; k++)
            {
                var instr = body.Instructions[startIndex - extra + k];
                if (GetPushCount(instr) != 1 || GetPopCount(instr) != 0) return null;
                candidates[k] = instr;
            }

            foreach (var candidate in candidates)
            {
                foreach (var other in body.Instructions)
                {
                    if (other == candidate) continue;
                    if (other.Operand is Instruction t && t == candidate) return null;
                    if (other.Operand is Instruction[] ts)
                        foreach (var tt in ts) if (tt == candidate) return null;
                }
                if (body.HasExceptionHandlers)
                {
                    foreach (var eh in body.ExceptionHandlers)
                    {
                        if (eh.TryStart == candidate || eh.TryEnd == candidate || eh.HandlerStart == candidate ||
                            eh.HandlerEnd == candidate || eh.FilterStart == candidate)
                            return null;
                    }
                }
            }

            return candidates;
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
