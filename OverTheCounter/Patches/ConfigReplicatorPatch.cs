using HarmonyLib;
using MelonLoader;
using System;
using System.Collections;
using System.Reflection;

#if IL2CPP
using NativeConfigReplicator = Il2CppScheduleOne.Management.ConfigurationReplicator;
#else
using NativeConfigReplicator = ScheduleOne.Management.ConfigurationReplicator;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Fixes a timing issue where ConfigurationReplicator receives field RPCs on the
    /// client before Configuration is initialized. The game defers these to onLoadComplete,
    /// but Configuration can still be null when the deferred lambda runs, crashing the
    /// LoadAsClient coroutine (stuck on "Syncing..." forever).
    ///
    /// OTC exacerbates this by adding loading work that widens the timing window.
    ///
    /// Fix: Harmony Prefix on each RpcLogic___ReceiveXxxField method. When Configuration
    /// is null, the RPC is re-queued via coroutine and retried each frame until
    /// Configuration is set (preserving the field update for vanilla items like renamed
    /// storage racks). After ~2s of retries, the RPC is dropped — this only happens for
    /// OTC grid items whose Configuration is permanently null (they don't need it).
    ///
    /// Mono also gets a Finalizer on Do() lambdas as defense-in-depth.
    /// </summary>
    public static class ConfigReplicatorPatch
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:ConfigReplicatorFix");

        /// <summary>Max frames to retry before dropping (~2s at 60fps).</summary>
        private const int MaxRetries = 120;

        private static readonly string[] RpcLogicMethods =
        {
            "RpcLogic___ReceiveItemField_2801973956",
            "RpcLogic___ReceiveNPCField_1687693739",
            "RpcLogic___ReceiveObjectField_1687693739",
            "RpcLogic___ReceiveObjectListField_690244341",
            "RpcLogic___ReceiveRecipeField_1692629761",
            "RpcLogic___ReceiveNumberField_1293284375",
            "RpcLogic___ReceiveRouteListField_3226448297",
            "RpcLogic___ReceiveQualityField_3536682170",
            "RpcLogic___ReceiveStringField_2801973956",
        };

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            var configType = typeof(NativeConfigReplicator);

            // --- Primary fix: Prefix on RpcLogic methods (IL2CPP + Mono) ---
            var prefix = new HarmonyMethod(typeof(ConfigReplicatorPatch), nameof(RpcLogicPrefix));
            int prefixCount = 0;

            foreach (var name in RpcLogicMethods)
            {
                try
                {
                    var method = AccessTools.Method(configType, name);
                    if (method != null)
                    {
                        harmony.Patch(method, prefix: prefix);
                        prefixCount++;
                    }
                    else
                    {
                        Logger.Warning($"RpcLogic method not found: {name}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to patch {name}: {ex.Message}");
                }
            }

            Logger.Msg($"Patched {prefixCount}/{RpcLogicMethods.Length} RpcLogic methods (retry-on-null-Configuration).");

            // --- Defense-in-depth: Finalizer on Do() lambdas (Mono only) ---
#if !IL2CPP
            try
            {
                var nestedTypes = configType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Instance);
                var finalizer = new HarmonyMethod(typeof(ConfigReplicatorPatch), nameof(DeferredHandlerFinalizer));

                foreach (var nestedType in nestedTypes)
                {
                    if (!nestedType.Name.Contains("DisplayClass")) continue;

                    var methods = nestedType.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    foreach (var method in methods)
                    {
                        if (!method.Name.Contains("g__Do")) continue;

                        try
                        {
                            harmony.Patch(method, finalizer: finalizer);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Failed to patch finalizer {nestedType.Name}.{method.Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Finalizer reflection failed: {ex.Message}");
            }

#endif
        }

        /// <summary>
        /// Harmony Prefix — when Configuration is null, re-queues the RPC via coroutine
        /// instead of letting it crash. Retries each frame until Configuration is set
        /// (vanilla items) or gives up after MaxRetries (OTC grid items).
        /// </summary>
        private static bool RpcLogicPrefix(
            NativeConfigReplicator __instance,
            MethodBase __originalMethod,
            object[] __args)
        {
            if (__instance.Configuration == null)
            {
                var argsCopy = (object[])__args.Clone();
                MelonCoroutines.Start(RetryRpc(__instance, __originalMethod, argsCopy));
                return false; // skip original — coroutine will re-invoke when ready
            }
            return true;
        }

        /// <summary>
        /// Polls each frame until Configuration is set, then re-invokes the original
        /// RpcLogic method. The Prefix fires again on re-invoke but Configuration is
        /// now set, so it passes through to the original game code.
        /// </summary>
        private static IEnumerator RetryRpc(
            NativeConfigReplicator instance,
            MethodBase method,
            object[] args)
        {
            for (int i = 0; i < MaxRetries; i++)
            {
                yield return null; // wait one frame

                // Instance destroyed while waiting
                if (instance == null || instance.gameObject == null) yield break;

                if (instance.Configuration != null)
                {
                    // Configuration is now set — re-invoke the original method.
                    // Our Prefix fires again, sees Configuration is set, returns true.
                    try
                    {
                        method.Invoke(instance, args);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Deferred RPC failed on '{instance.gameObject.name}': {ex.Message}");
                    }
                    yield break;
                }
            }

            // Exhausted retries — OTC grid item or permanently uninitialized entity.
            // Safe to drop: OTC items don't need config replication.
            try
            {
                Logger.Warning($"Dropped config RPC on '{instance.gameObject.name}' after {MaxRetries} retries ({method.Name})");
            }
            catch { }
        }

        /// <summary>
        /// Harmony Finalizer (Mono only) — defense-in-depth. Swallows exceptions from
        /// deferred Do() lambdas so they don't kill the LoadAsClient coroutine.
        /// </summary>
        private static Exception DeferredHandlerFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Logger.Warning($"[Finalizer] Caught exception in deferred handler: {__exception.Message}");
            }
            return null; // swallow
        }
    }
}
