using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.DevUtilities;
using MelonLoader;
using System;
using System.Collections;

using System.Reflection;
using UnityEngine;

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Handles mugshot generation and loading for mod NPCs.
    /// Fixed-appearance NPCs (Vic, Static, Bella) use pre-baked mugshots from embedded
    /// resources via <see cref="ApplyPreBaked"/>. Dynamic-appearance NPCs (managers)
    /// use runtime capture via <see cref="Generate"/> with the game's shared MugshotRig.
    /// </summary>
    internal static class MugshotUtility
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:MugshotUtility");

        // Prevents multiple coroutines from capturing simultaneously.
        // All coroutines resume after the same 3s delay; without this flag
        // they all see the rig as idle in the same frame and race.
        private static bool _isCapturing;

        // Incremented on scene transitions. MelonCoroutines persist across scenes,
        // so in-flight coroutines from a previous load check this to self-abort.
        private static int _sessionId;

        /// <summary>
        /// Resets session state. Call on scene transitions.
        /// </summary>
        public static void ResetSession()
        {
            _isCapturing = false;
            _sessionId++;
        }

        /// <summary>
        /// Loads a pre-baked mugshot from an embedded resource.
        /// Resource name format: OverTheCounter.Resources.Mugshots.{name}.png
        /// Returns null if the resource is missing or fails to load.
        /// </summary>
        public static Sprite LoadFromEmbeddedResource(string name)
        {
            try
            {
                string resourceName = $"OverTheCounter.Resources.Mugshots.{name}.png";
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    Logger.Warning($"Embedded mugshot not found: {resourceName}");
                    return null;
                }

                byte[] data = new byte[stream.Length];
                stream.Read(data, 0, data.Length);

                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, data))
                {
                    Logger.Warning($"Failed to decode embedded mugshot: {name}");
                    return null;
                }

                var sprite = Sprite.Create(tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f));
                if (Config.VerboseLogging.Value)
                    Logger.Msg($"Loaded pre-baked mugshot: {name} ({tex.width}x{tex.height})");
                return sprite;
            }
            catch (Exception ex)
            {
                Logger.Warning($"LoadFromEmbeddedResource failed for {name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Loads a pre-baked mugshot and applies it via the callback immediately, then
        /// re-applies after 4s to overwrite S1API's ProcessMugshotQueue (which runs
        /// after Appearance.Build() and overwrites Icon + RefreshMessagingIcons).
        /// </summary>
        public static void ApplyPreBaked(string name, Action<Sprite> applier)
        {
            var sprite = LoadFromEmbeddedResource(name);
            if (sprite == null) return;
            applier(sprite);
            MelonCoroutines.Start(ReapplyAfterDelay(sprite, applier));
        }

        private static IEnumerator ReapplyAfterDelay(Sprite sprite, Action<Sprite> applier)
        {
            yield return new WaitForSeconds(4f);
            applier(sprite);
        }

        /// <summary>
        /// Starts a coroutine that captures a mugshot for the given game NPC.
        /// Waits for S1API's ProcessMugshotQueue to finish (3s delay) then uses
        /// the shared MugshotRig with our own setup sequence.
        /// <paramref name="onComplete"/> fires with the generated Sprite, or null on failure.
        /// Used for dynamic-appearance NPCs (managers). Fixed-appearance NPCs
        /// (Vic, Static, Bella) use <see cref="ApplyPreBaked"/> instead.
        /// </summary>
        public static void Generate(Il2CppScheduleOne.NPCs.NPC gameNpc, string label, Action<Sprite> onComplete, int attempt = 0)
        {
            MelonCoroutines.Start(GenerateCoroutine(gameNpc, label, onComplete, attempt));
        }

        private static IEnumerator GenerateCoroutine(Il2CppScheduleOne.NPCs.NPC gameNpc, string label, Action<Sprite> onComplete, int attempt)
        {
            int mySession = _sessionId;

            // Wait for avatar to have settings loaded
            int avatarWait = 0;
            while ((gameNpc?.Avatar?.CurrentSettings == null) && avatarWait < 600)
            {
                avatarWait++;
                yield return null;
            }
            if (mySession != _sessionId) yield break;
            if (gameNpc?.Avatar?.CurrentSettings == null)
            {
                Logger.Warning($"{label}: mugshot aborted — Avatar.CurrentSettings still null after {avatarWait} frames");
                onComplete?.Invoke(null);
                yield break;
            }

            var generator = Singleton<MugshotGenerator>.Instance;
            if (generator == null || generator.MugshotRig == null || generator.Generator == null)
            {
                Logger.Warning($"{label}: MugshotGenerator not available");
                onComplete?.Invoke(null);
                yield break;
            }

            // Give S1API's ProcessMugshotQueue time to finish before we touch the rig.
            yield return new WaitForSeconds(3f);
            if (mySession != _sessionId) yield break;

            // Wait for MugshotRig to be idle AND no other capture in progress
            int idleWait = 0;
            while ((_isCapturing || generator.MugshotRig.gameObject.activeSelf) && idleWait < 600)
            {
                idleWait++;
                yield return null;
            }
            if (mySession != _sessionId) yield break;
            if (idleWait > 0)
                if (Config.VerboseLogging.Value)
                    Logger.Msg($"{label}: waited {idleWait} frames for MugshotRig idle");

            _isCapturing = true;

            Texture2D resultTex = null;
            bool callbackFired = false;

            // Save state
            var previousAvatar = gameNpc.Avatar;
            var mugshotRig = generator.MugshotRig;
            var iconGen = generator.Generator;
            bool prevCulling = false;
            bool prevModifyLighting = iconGen.ModifyLighting;
            var prevAmbientMode = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            float prevAmbientIntensity = RenderSettings.ambientIntensity;
            var mugshotParent = mugshotRig.transform.parent;
            bool prevParentActive = mugshotParent != null && mugshotParent.gameObject.activeSelf;

            try
            {
                gameNpc.Avatar = mugshotRig;
                if (mugshotParent != null)
                    mugshotParent.gameObject.SetActive(true);
                mugshotRig.gameObject.SetActive(true);

                if (mugshotRig.Animation != null)
                {
                    prevCulling = mugshotRig.Animation.AllowCulling;
                    mugshotRig.Animation.AllowCulling = false;
                }

                mugshotRig.SetVisible(true);
                mugshotRig.Impostor?.DisableImpostor();

                var mugshotSettings = UnityEngine.Object.Instantiate(previousAvatar.CurrentSettings);
                mugshotSettings.Height = 1f;
                gameNpc.Avatar.LoadAvatarSettings(mugshotSettings);

                LayerUtility.SetLayerRecursively(
                    mugshotRig.gameObject, LayerMask.NameToLayer("IconGeneration"));
                var smRenderers = mugshotRig.GetComponentsInChildren<SkinnedMeshRenderer>();
                for (int r = 0; r < smRenderers.Length; r++)
                    smRenderers[r].updateWhenOffscreen = true;

                iconGen.ModifyLighting = true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"{label}: mugshot setup failed: {ex.Message}");
                _isCapturing = false;
                gameNpc.Avatar = previousAvatar;
                iconGen.ModifyLighting = prevModifyLighting;
                try { mugshotRig.gameObject.SetActive(false); } catch { }
                if (attempt < 2)
                    Generate(gameNpc, label, onComplete, attempt + 1);
                else
                    onComplete?.Invoke(null);
                yield break;
            }

            try
            {
                gameNpc.Avatar.GetMugshot((Il2CppSystem.Action<Texture2D>)((Texture2D tex) =>
                {
                    resultTex = tex;
                    callbackFired = true;
                }));
            }
            catch (Exception ex)
            {
                Logger.Warning($"{label}: GetMugshot call failed: {ex.Message}");
                _isCapturing = false;
                gameNpc.Avatar = previousAvatar;
                iconGen.ModifyLighting = prevModifyLighting;
                try { mugshotRig.gameObject.SetActive(false); } catch { }
                if (attempt < 2)
                    Generate(gameNpc, label, onComplete, attempt + 1);
                else
                    onComplete?.Invoke(null);
                yield break;
            }

            // Wait for render (LateUpdate)
            int callbackWait = 0;
            while (!callbackFired && callbackWait < 600)
            {
                callbackWait++;
                yield return null;
            }
            if (mySession != _sessionId) { _isCapturing = false; yield break; }

            // Cleanup — all AFTER render
            iconGen.ModifyLighting = prevModifyLighting;
            RenderSettings.ambientMode = prevAmbientMode;
            RenderSettings.ambientLight = prevAmbientLight;
            RenderSettings.ambientIntensity = prevAmbientIntensity;
            gameNpc.Avatar = previousAvatar ?? mugshotRig;
            try
            {
                if (previousAvatar != null)
                    previousAvatar.LoadAvatarSettings(previousAvatar.CurrentSettings);
            }
            catch { }
            try
            {
                if (generator.DefaultSettings != null)
                    mugshotRig.LoadAvatarSettings(generator.DefaultSettings);
                mugshotRig.gameObject.SetActive(false);
                if (mugshotParent != null)
                    mugshotParent.gameObject.SetActive(prevParentActive);
                if (mugshotRig.Animation != null)
                    mugshotRig.Animation.AllowCulling = prevCulling;
            }
            catch { }

            _isCapturing = false;

            if (resultTex == null)
            {
                Logger.Warning($"{label}: mugshot capture returned null (attempt {attempt})");
                if (attempt < 2)
                    Generate(gameNpc, label, onComplete, attempt + 1);
                else
                    onComplete?.Invoke(null);
                yield break;
            }

            // Create sprite from full texture
            try
            {
                resultTex.Apply();
                var sprite = Sprite.Create(resultTex,
                    new Rect(0, 0, resultTex.width, resultTex.height),
                    new Vector2(0.5f, 0.5f));

                Logger.Msg($"{label}: mugshot generated ({resultTex.width}x{resultTex.height}, attempt {attempt})");
                onComplete?.Invoke(sprite);
            }
            catch (Exception ex)
            {
                Logger.Warning($"{label}: mugshot sprite creation failed: {ex.Message}");
                if (attempt < 2)
                    Generate(gameNpc, label, onComplete, attempt + 1);
                else
                    onComplete?.Invoke(null);
            }
        }
    }
}
