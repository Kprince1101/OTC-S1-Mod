using MelonLoader;
using System;
using UnityEngine;

#if IL2CPP
using Il2CppEPOOutline;
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Tools;
using Il2CppScheduleOne.UI;
#else
using EPOOutline;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.Economy;
using ScheduleOne.EntityFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Tools;
using ScheduleOne.UI;
#endif

namespace OverTheCounter.UI
{
    /// <summary>
    /// Custom raycast selector for route endpoints. Detects both PlaceableStorageEntity
    /// and DeadDrop when the player aims at objects in the world — the game's built-in
    /// ObjectSelector only works with BuildableItem (which dead drops are not).
    /// </summary>
    public static class RouteEntitySelector
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:RouteSelector");

        private static bool _isOpen;
        private static Action<PlaceableStorageEntity, DeadDrop> _callback;

        // Hover tracking
        private static PlaceableStorageEntity _highlightedPSE;
        private static DeadDrop _highlightedDD;
        private static Outlinable _ddOutlineEffect;

        private static readonly Color HoverOutlineColor = Color.white;
        private const float RaycastRange = 5f;

        public static bool IsOpen => _isOpen;

        /// <summary>
        /// Opens the selector. Player looks at objects and clicks to select.
        /// Callback receives (PSE, DeadDrop) — exactly one non-null, or both null on cancel.
        /// </summary>
        public static void Open(string instruction, Action<PlaceableStorageEntity, DeadDrop> callback)
        {
            if (_isOpen) Close(false);

            _isOpen = true;
            _callback = callback;
            _highlightedPSE = null;
            HideDeadDropOutline();

            try
            {
                // Close the clipboard while selecting (same as ObjectSelector does)
                Singleton<ManagementClipboard>.Instance?.Close(true);

                // Show instruction at top of screen
                Singleton<HUD>.Instance?.ShowTopScreenText(instruction);

                // Show input prompt
                Singleton<InputPromptsCanvas>.Instance?.LoadModule("objectselector");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Open UI setup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Must be called every frame from EnforceUI or a patch while the selector is open.
        /// </summary>
        public static void Tick()
        {
            if (!_isOpen) return;

            try
            {
                // Raycast from player camera
                PlaceableStorageEntity hitPSE = null;
                DeadDrop hitDD = null;

                var cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam != null)
                {
                    RaycastHit hit;
                    if (cam.LookRaycast(RaycastRange, out hit, GetDetectionMask(), true, 0.1f))
                    {
                        var col = hit.collider;
                        if (col != null)
                        {
                            hitPSE = col.GetComponentInParent<PlaceableStorageEntity>();
                            if (hitPSE == null)
                                hitDD = col.GetComponentInParent<DeadDrop>();
                        }
                    }
                }

                // Update hover state
                UpdateHover(hitPSE, hitDD);

                // Check for click
                if (GameInput.GetButtonDown(GameInput.ButtonCode.PrimaryClick))
                {
                    if (hitPSE != null || hitDD != null)
                    {
                        Close(true, hitPSE, hitDD);
                        return;
                    }
                }

                // Check for cancel (Escape or right-click)
                if (GameInput.GetButtonDown(GameInput.ButtonCode.Escape) ||
                    GameInput.GetButtonDown(GameInput.ButtonCode.Back))
                {
                    Close(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Tick error: {ex.Message}");
                Close(false);
            }
        }

        private static void UpdateHover(PlaceableStorageEntity hitPSE, DeadDrop hitDD)
        {
            // Clear previous PSE outline if we're no longer hovering it
            if (_highlightedPSE != null && _highlightedPSE != hitPSE)
            {
                try { _highlightedPSE.HideOutline(); } catch { }
                _highlightedPSE = null;
            }

            // Clear previous dead drop outline if we're no longer hovering it
            if (_highlightedDD != null && _highlightedDD != hitDD)
            {
                HideDeadDropOutline();
            }

            if (hitPSE != null)
            {
                // Show outline on PSE
                if (_highlightedPSE != hitPSE)
                {
                    _highlightedPSE = hitPSE;
                    try { hitPSE.ShowOutline(HoverOutlineColor); } catch { }
                }

                // Show name in crosshair
                string name = GetPSEName(hitPSE);
                try { Singleton<HUD>.Instance?.CrosshairText?.Show(name, Color.white); } catch { }
            }
            else if (hitDD != null)
            {
                // Show outline on dead drop
                if (_highlightedDD != hitDD)
                {
                    ShowDeadDropOutline(hitDD);
                }

                string name = "Dead Drop (" + (hitDD.DeadDropName ?? "Unknown") + ")";
                try { Singleton<HUD>.Instance?.CrosshairText?.Show(name, Color.white); } catch { }
            }
            else
            {
                // Nothing hovered — hide crosshair text
                try { Singleton<HUD>.Instance?.CrosshairText?.Hide(); } catch { }
            }
        }

        private static void Close(bool pushSelection, PlaceableStorageEntity pse = null, DeadDrop dd = null)
        {
            _isOpen = false;

            // Clear outlines
            if (_highlightedPSE != null)
            {
                try { _highlightedPSE.HideOutline(); } catch { }
                _highlightedPSE = null;
            }
            HideDeadDropOutline();

            try
            {
                Singleton<HUD>.Instance?.HideTopScreenText();
                Singleton<HUD>.Instance?.CrosshairText?.Hide();

                if (Singleton<InputPromptsCanvas>.Instance?.currentModuleLabel == "objectselector")
                    Singleton<InputPromptsCanvas>.Instance?.UnloadModule();
            }
            catch { }

            // Reopen clipboard
            try
            {
                var mi = Singleton<ManagementInterface>.Instance;
                if (mi?.EquippedClipboard != null)
                {
                    Singleton<ManagementClipboard>.Instance?.Open(
                        mi.Configurables, mi.EquippedClipboard);
                }
            }
            catch { }

            // Fire callback
            if (pushSelection && _callback != null)
            {
                try { _callback(pse, dd); }
                catch (Exception ex) { Logger.Error($"Callback error: {ex.Message}"); }
            }
            else if (!pushSelection && _callback != null)
            {
                try { _callback(null, null); }
                catch (Exception ex) { Logger.Error($"Cancel callback error: {ex.Message}"); }
            }

            _callback = null;
        }

        private static void ShowDeadDropOutline(DeadDrop dd)
        {
            HideDeadDropOutline();
            _highlightedDD = dd;

            try
            {
                var go = dd.gameObject;
                _ddOutlineEffect = go.GetComponent<Outlinable>();
                if (_ddOutlineEffect == null)
                {
                    _ddOutlineEffect = go.AddComponent<Outlinable>();
                    _ddOutlineEffect.OutlineParameters.BlurShift = 0f;
                    _ddOutlineEffect.OutlineParameters.DilateShift = 0.5f;
                    _ddOutlineEffect.OutlineParameters.FillPass.Shader =
                        Resources.Load<Shader>("Easy performant outline/Shaders/Fills/ColorFill");

                    var renderers = go.GetComponentsInChildren<MeshRenderer>();
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        renderers[i].allowOcclusionWhenDynamic = false;
                        var target = new OutlineTarget(renderers[i], 0);
                        _ddOutlineEffect.TryAddTarget(target);
                    }
                }

                _ddOutlineEffect.OutlineParameters.Color = HoverOutlineColor;
                Color32 fill = HoverOutlineColor;
                fill.a = 9;
                _ddOutlineEffect.OutlineParameters.FillPass.SetColor("_PublicColor", fill);
                _ddOutlineEffect.enabled = true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Dead drop outline failed: {ex.Message}");
            }
        }

        private static void HideDeadDropOutline()
        {
            if (_ddOutlineEffect != null)
            {
                try { _ddOutlineEffect.enabled = false; } catch { }
                _ddOutlineEffect = null;
            }
            _highlightedDD = null;
        }

        private static string GetPSEName(PlaceableStorageEntity pse)
        {
            try
            {
                var buildable = pse.TryCast<BuildableItem>();
                if (buildable?.ItemInstance != null)
                    return buildable.ItemInstance.Name;
            }
            catch { }
            return pse?.gameObject?.name ?? "Storage";
        }

        /// <summary>Detection mask matching the game's selectors.</summary>
        private static int GetDetectionMask() =>
            Physics.DefaultRaycastLayers;
    }
}
