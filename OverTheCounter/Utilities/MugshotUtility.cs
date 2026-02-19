using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.DevUtilities;
using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Generates mugshots for manager NPCs using the game's MugshotGenerator rig directly.
    /// Uses GPU warmup, two-yield frame timing, direct GetTexture, content-validated retry,
    /// and AllowCulling discipline. Waits for S1API's own mugshot processing to finish.
    /// </summary>
    internal static class MugshotUtility
    {
        private static readonly MelonLogger.Instance Logger = new("OTC:MugshotUtility");

        // Incremented on scene transitions. MelonCoroutines persist across scenes,
        // so in-flight coroutines check this to self-abort.
        private static int _sessionId;

        // S1API mutex — check _isProcessingMugshots to avoid concurrent rig access
        private static bool _s1apiReflectionReady;
        private static bool _s1apiReflectionFailed;
        private static FieldInfo _s1apiIsProcessing;

        // Internal queue
        private static readonly Queue<MugshotRequest> _queue = new();
        private static bool _isProcessing;

        private struct MugshotRequest
        {
            public Il2CppScheduleOne.NPCs.NPC GameNpc;
            public string Label;
            public Action<Sprite> OnComplete;
            public AvatarSettings ExplicitSettings;
        }

        /// <summary>
        /// Resets session state. Call on scene transitions.
        /// </summary>
        public static void ResetSession()
        {
            _sessionId++;
            _queue.Clear();
            _isProcessing = false;
        }

        /// <summary>
        /// Enqueues a mugshot generation request for the given game NPC.
        /// <paramref name="onComplete"/> fires with the generated Sprite, or null on failure.
        /// If <paramref name="explicitSettings"/> is provided, those exact settings are used
        /// for the capture (ensures host/client determinism from the same seed).
        /// </summary>
        public static void Generate(Il2CppScheduleOne.NPCs.NPC gameNpc, string label, Action<Sprite> onComplete,
            AvatarSettings explicitSettings = null)
        {
            _queue.Enqueue(new MugshotRequest
            {
                GameNpc = gameNpc,
                Label = label,
                OnComplete = onComplete,
                ExplicitSettings = explicitSettings
            });

            if (!_isProcessing)
            {
                _isProcessing = true;
                MelonCoroutines.Start(ProcessQueue());
            }
        }

        private static bool InitS1ApiReflection()
        {
            if (_s1apiReflectionReady) return true;
            if (_s1apiReflectionFailed) return false;

            try
            {
                var appType = typeof(S1API.Entities.NPCAppearance);
                _s1apiIsProcessing = appType.GetField("_isProcessingMugshots",
                    BindingFlags.Static | BindingFlags.NonPublic);

                if (_s1apiIsProcessing == null)
                {
                    Logger.Warning("Could not find S1API _isProcessingMugshots field");
                    _s1apiReflectionFailed = true;
                    return false;
                }

                _s1apiReflectionReady = true;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"S1API reflection init failed: {ex.Message}");
                _s1apiReflectionFailed = true;
                return false;
            }
        }

        private static bool IsS1ApiProcessing()
        {
            if (!InitS1ApiReflection()) return false;
            try { return (bool)_s1apiIsProcessing.GetValue(null); }
            catch { return false; }
        }

        private static IEnumerator ProcessQueue()
        {
            int mySession = _sessionId;

            // Find MugshotGenerator singleton
            MugshotGenerator generator = null;
            try { generator = Singleton<MugshotGenerator>.Instance; } catch { }
            if (generator == null)
                generator = UnityEngine.Object.FindObjectOfType<MugshotGenerator>();

            if (generator == null)
            {
                Logger.Warning("MugshotGenerator not found");
                DrainQueue(null);
                yield break;
            }

            var mugshotRig = generator.MugshotRig;
            var iconGenerator = generator.Generator;

            if (mugshotRig == null || iconGenerator == null)
            {
                Logger.Warning("MugshotRig or IconGenerator is null");
                DrainQueue(null);
                yield break;
            }

            // Wait for S1API to finish its mugshot processing
            int waitFrames = 0;
            while (IsS1ApiProcessing() && waitFrames < 600)
            {
                waitFrames++;
                yield return null;
            }
            if (mySession != _sessionId) { _isProcessing = false; yield break; }
            if (waitFrames > 0 && Config.VerboseLogging.Value)
                Logger.Msg($"Waited {waitFrames} frames for S1API mugshot processing to finish");

            // Flush the rig to a clean state. After S1API finishes, the rig retains
            // the last vanilla NPC's bone transforms/mesh, causing misframed captures.
            // Mid-game (cold rig) needs 5 cycles to prime renderers; post-S1API (warm rig)
            // needs 1 cycle to flush stale state.
            int warmupCycles = waitFrames == 0 ? 5 : 1;
            if (Config.VerboseLogging.Value)
                Logger.Msg(waitFrames == 0
                    ? "Mid-game mugshot request — warming up MugshotRig (5 cycles)"
                    : "Post-S1API flush — resetting MugshotRig (1 cycle)");

            for (int w = 0; w < warmupCycles; w++)
            {
                Transform mugshotParent = mugshotRig.transform.parent;
                if (mugshotParent != null)
                    mugshotParent.gameObject.SetActive(true);
                mugshotRig.gameObject.SetActive(true);

                if (mugshotRig.Animation != null)
                    mugshotRig.Animation.AllowCulling = false;

                mugshotRig.SetVisible(true);
                mugshotRig.Impostor?.DisableImpostor();

                if (generator.DefaultSettings != null)
                    mugshotRig.LoadAvatarSettings(generator.DefaultSettings);

                SetLayerRecursively(mugshotRig.gameObject, LayerMask.NameToLayer("IconGeneration"));

                yield return null;
                yield return new WaitForEndOfFrame();

                if (mySession != _sessionId) { _isProcessing = false; yield break; }

                mugshotRig.gameObject.SetActive(false);
            }

            if (Config.VerboseLogging.Value)
                Logger.Msg("MugshotRig reset complete");

            // Process queue
            while (_queue.Count > 0)
            {
                if (mySession != _sessionId) break;

                var req = _queue.Dequeue();
                if (req.GameNpc == null)
                {
                    req.OnComplete?.Invoke(null);
                    continue;
                }

                // Get settings: explicit (deterministic) or from NPC's current avatar
                AvatarSettings captureSettings;
                if (req.ExplicitSettings != null)
                    captureSettings = UnityEngine.Object.Instantiate(req.ExplicitSettings);
                else if (req.GameNpc.Avatar?.CurrentSettings != null)
                    captureSettings = UnityEngine.Object.Instantiate(req.GameNpc.Avatar.CurrentSettings);
                else
                {
                    Logger.Warning($"{req.Label}: no settings available, skipping");
                    req.OnComplete?.Invoke(null);
                    continue;
                }
                captureSettings.Height = 1f;

                // Save and swap avatar reference — prevents game code from accessing
                // the wrong avatar during the capture's yield frames
                Il2CppScheduleOne.AvatarFramework.Avatar previousAvatar = req.GameNpc.Avatar;
                req.GameNpc.Avatar = mugshotRig;

                // === Content-validated capture with retry ===
                const int maxRetries = 30;
                const float contentBrightnessFloor = 0.01f;
                Texture2D generatedMugshot = null;
                bool hasContent = false;

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    Transform mugshotParent = mugshotRig.transform.parent;
                    if (mugshotParent != null)
                        mugshotParent.gameObject.SetActive(true);
                    mugshotRig.gameObject.SetActive(true);

                    // AllowCulling discipline — rig MUST deactivate with AllowCulling = false;
                    // restoring true too early causes the Animator to enter culled mode,
                    // skipping bone evaluation on the next activation.
                    bool prevAllowCulling = mugshotRig.Animation != null && mugshotRig.Animation.AllowCulling;
                    if (mugshotRig.Animation != null)
                        mugshotRig.Animation.AllowCulling = false;

                    mugshotRig.SetVisible(true);
                    mugshotRig.Impostor?.DisableImpostor();

                    mugshotRig.LoadAvatarSettings(captureSettings);
                    SetLayerRecursively(mugshotRig.gameObject, LayerMask.NameToLayer("IconGeneration"));

                    var smrs = mugshotRig.GetComponentsInChildren<SkinnedMeshRenderer>();
                    foreach (var smr in smrs)
                        smr.updateWhenOffscreen = true;

                    // Two-yield pattern: yield null (full frame for material properties via
                    // Avatar's internal Update/LateUpdate cycle) + WaitForEndOfFrame (resume
                    // after Animation/LateUpdate for correct bone state)
                    yield return null;
                    yield return new WaitForEndOfFrame();

                    generatedMugshot = null;
                    try
                    {
                        generatedMugshot = iconGenerator.GetTexture(mugshotRig.transform);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"{req.Label}: GetTexture failed: {ex.Message}");
                    }

                    // Content validation — 5 pixel samples for brightness > threshold
                    hasContent = false;
                    float maxBrightness = 0f;
                    if (generatedMugshot != null && generatedMugshot.width > 0 && generatedMugshot.height > 0)
                    {
                        int cx = generatedMugshot.width / 2;
                        int cy = generatedMugshot.height / 2;
                        Color[] samples =
                        {
                            generatedMugshot.GetPixel(cx, cy),
                            generatedMugshot.GetPixel(cx, (int)(generatedMugshot.height * 0.85f)),
                            generatedMugshot.GetPixel(cx, (int)(generatedMugshot.height * 0.15f)),
                            generatedMugshot.GetPixel((int)(generatedMugshot.width * 0.25f), cy),
                            generatedMugshot.GetPixel((int)(generatedMugshot.width * 0.75f), cy)
                        };

                        foreach (var s in samples)
                        {
                            float b = s.r + s.g + s.b;
                            if (b > maxBrightness) maxBrightness = b;
                        }
                        hasContent = maxBrightness > contentBrightnessFloor;
                    }

                    if (Config.VerboseLogging.Value)
                        Logger.Msg($"{req.Label} attempt={attempt}: bright={maxBrightness:F3} content={hasContent}");

                    if (hasContent)
                        break;

                    // No content — deactivate and retry
                    if (generator.DefaultSettings != null)
                        mugshotRig.LoadAvatarSettings(generator.DefaultSettings);
                    if (mugshotRig.Animation != null)
                        mugshotRig.Animation.AllowCulling = prevAllowCulling;
                    mugshotRig.gameObject.SetActive(false);

                    if (attempt == maxRetries)
                        Logger.Warning($"{req.Label}: no content after {maxRetries + 1} attempts, using last capture");
                }

                // Create sprite and fire callback
                Sprite resultSprite = null;
                if (generatedMugshot != null)
                {
                    try
                    {
                        generatedMugshot.Apply();
                        Rect cropRect = new Rect(0, 0, generatedMugshot.width, generatedMugshot.height);
                        resultSprite = Sprite.Create(generatedMugshot, cropRect, Vector2.zero);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"{req.Label}: sprite creation failed: {ex.Message}");
                    }
                }

                // Restore avatar reference
                req.GameNpc.Avatar = previousAvatar ?? mugshotRig;

                // Reset rig with full two-yield cycle — LoadAvatarSettings alone doesn't
                // flush the previous NPC's mesh/material state to renderers. The Avatar's
                // internal Update/LateUpdate must run a full frame to propagate the reset.
                // Without this, subsequent captures inherit stale appearance from the previous NPC.
                if (generator.DefaultSettings != null)
                    mugshotRig.LoadAvatarSettings(generator.DefaultSettings);
                if (mugshotRig.Animation != null)
                    mugshotRig.Animation.AllowCulling = false;
                yield return null;
                yield return new WaitForEndOfFrame();
                mugshotRig.gameObject.SetActive(false);

                if (Config.VerboseLogging.Value)
                    Logger.Msg($"{req.Label}: mugshot {(resultSprite != null ? "ready" : "failed")}");
                req.OnComplete?.Invoke(resultSprite);
            }

            _isProcessing = false;
        }

        private static void DrainQueue(Sprite result)
        {
            while (_queue.Count > 0)
                _queue.Dequeue().OnComplete?.Invoke(result);
            _isProcessing = false;
        }

        private static void SetLayerRecursively(GameObject obj, int layer)
        {
            if (obj == null) return;
            obj.layer = layer;
            for (int i = 0; i < obj.transform.childCount; i++)
                SetLayerRecursively(obj.transform.GetChild(i).gameObject, layer);
        }
    }
}
