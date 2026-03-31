using HarmonyLib;
using OverTheCounter.Utilities;
using System;
using System.Collections.Generic;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.Weather;
#else
using ScheduleOne.Weather;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Makes OTC building interiors count as "under cover" for the weather system.
    /// <list type="bullet">
    /// <item>IsPositionUnderCover postfix — NPCs inside OTC buildings are marked under cover
    ///   (no umbrellas, no rain-running, no wetness accumulation).</item>
    /// <item>UpdateVolume prefix — forces enclosureBlend to 1 when the player is inside,
    ///   which muffles rain audio via the existing indoor/outdoor audio crossfade.</item>
    /// <item>EnvironmentManager.Update postfix — disables weather effect rendering when the
    ///   player enters an OTC building and re-enables on exit (covers both ParticleSystem
    ///   and VFX Graph renderers via Renderer base class search).</item>
    /// </list>
    /// </summary>
    internal static class WeatherPatches
    {
        /// <summary>Axis-aligned bounding box for an OTC building interior.</summary>
        internal readonly struct BuildingBounds
        {
            public readonly float MinX, MaxX, MinZ, MaxZ, FloorY, CeilingY;

            public BuildingBounds(Vector3 origin, float width, float height, float depth, float padding = 1f)
            {
                MinX = origin.x - padding;
                MaxX = origin.x + width + padding;
                MinZ = origin.z - padding;
                MaxZ = origin.z + depth + padding;
                FloorY = origin.y - padding;
                CeilingY = origin.y + height + padding;
            }

            public bool Contains(Vector3 pos)
            {
                return pos.x >= MinX && pos.x <= MaxX &&
                       pos.z >= MinZ && pos.z <= MaxZ &&
                       pos.y >= FloorY && pos.y <= CeilingY;
            }
        }

        private static readonly List<BuildingBounds> _bounds = new();
        private static bool _playerInside;
        private static readonly List<GameObject> _disabledWeatherObjects = new();
        private static float _lastRefreshTime;

        /// <summary>
        /// Register an OTC building's interior volume for weather exclusion.
        /// Call during building setup. Origin is the floor-level SW corner.
        /// </summary>
        internal static void RegisterBuilding(Vector3 floorOrigin, float width, float height, float depth)
        {
            _bounds.Add(new BuildingBounds(floorOrigin, width, height, depth));
        }

        /// <summary>Clears all registered building bounds and restores suppressed weather objects.</summary>
        internal static void Cleanup()
        {
            RestoreWeatherParticles();
            _bounds.Clear();
            _playerInside = false;
        }

        /// <summary>Returns true if a world position is inside any registered OTC building.</summary>
        internal static bool IsInsideOtcBuilding(Vector3 pos)
        {
            for (int i = 0; i < _bounds.Count; i++)
                if (_bounds[i].Contains(pos)) return true;
            return false;
        }

        // ================================================================
        //  Apply — manual Harmony registration (HarmonyDontPatchAll)
        // ================================================================

        /// <summary>Registers Harmony patches for weather suppression inside OTC buildings.</summary>
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var isUnderCover = AccessTools.Method(typeof(EnvironmentManager),
                    nameof(EnvironmentManager.IsPositionUnderCover));
                if (isUnderCover != null)
                    harmony.Patch(isUnderCover,
                        postfix: new HarmonyMethod(typeof(WeatherPatches), nameof(IsUnderCover_Postfix)));

                var updateVolume = AccessTools.Method(typeof(WeatherVolume),
                    nameof(WeatherVolume.UpdateVolume));
                if (updateVolume != null)
                    harmony.Patch(updateVolume,
                        prefix: new HarmonyMethod(typeof(WeatherPatches), nameof(UpdateVolume_Prefix)));

                var envUpdate = AccessTools.Method(typeof(EnvironmentManager), "Update");
                if (envUpdate != null)
                    harmony.Patch(envUpdate,
                        postfix: new HarmonyMethod(typeof(WeatherPatches), nameof(EnvUpdate_Postfix)));

                OTCLog.Msg(OTCLog.Systems.Patch, "Weather patches applied");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"WeatherPatches.Apply failed: {ex.Message}");
            }
        }

        // ================================================================
        //  Patch 1: NPC under-cover check
        //  The baked heightmap doesn't include OTC buildings, so NPCs
        //  inside get IsUnderCover = false. This postfix fixes that.
        // ================================================================

        private static void IsUnderCover_Postfix(Vector3 position, ref bool __result)
        {
            if (!__result)
                __result = IsInsideOtcBuilding(position);
        }

        // ================================================================
        //  Patch 2: Rain audio muffling
        //  enclosureBlend (0 = outdoors, 1 = fully enclosed) drives the
        //  indoor/outdoor audio crossfade on each WeatherEffectController.
        // ================================================================

        private static void UpdateVolume_Prefix(ref float enclosureBlend)
        {
            var cam = Camera.main;
            if (cam != null && IsInsideOtcBuilding(cam.transform.position))
                enclosureBlend = 1f;
        }

        // ================================================================
        //  Patch 3: Suppress weather visuals inside OTC buildings
        //  On state change (enter/exit), disable all Renderer GameObjects
        //  under WeatherVolume objects. Using Renderer (base class) catches
        //  both ParticleSystemRenderer and VFXRenderer (VFX Graph rain).
        // ================================================================

        private static void EnvUpdate_Postfix()
        {
            var cam = Camera.main;
            if (cam == null) return;

            bool inside = IsInsideOtcBuilding(cam.transform.position);

            if (inside && !_playerInside)
            {
                // Entering OTC building — suppress weather particles
                _playerInside = true;
                SuppressWeatherParticles();
                var names = string.Join(", ", _disabledWeatherObjects.ConvertAll(go => go != null ? go.name : "null"));
                OTCLog.Msg(OTCLog.Systems.Patch, $"Suppressed {_disabledWeatherObjects.Count} weather objects (entered building): {names}");
            }
            else if (!inside && _playerInside)
            {
                // Exiting OTC building — restore weather particles
                _playerInside = false;
                OTCLog.Msg(OTCLog.Systems.Patch, $"Restored {_disabledWeatherObjects.Count} weather objects (exited building)");
                RestoreWeatherParticles();
            }
            else if (inside && Time.time - _lastRefreshTime > 3f)
            {
                // Periodic refresh while inside (handles new weather volumes spawning)
                SuppressWeatherParticles();
            }
        }

        private static void SuppressWeatherParticles()
        {
            _lastRefreshTime = Time.time;

            var volumes = UnityEngine.Object.FindObjectsOfType<WeatherVolume>();
            if (volumes == null) return;

            foreach (var vol in volumes)
            {
                if (vol == null) continue;

                // Renderer is the base class for ParticleSystemRenderer, VFXRenderer,
                // MeshRenderer, etc. — catches ALL visual weather effects including
                // VFX Graph rain that ParticleSystem search missed.
                var renderers = vol.GetComponentsInChildren<Renderer>(true);
                if (renderers == null) continue;

                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    var go = r.gameObject;
                    if (go == null || !go.activeSelf) continue;

                    go.SetActive(false);
                    _disabledWeatherObjects.Add(go);
                }
            }
        }

        private static void RestoreWeatherParticles()
        {
            foreach (var go in _disabledWeatherObjects)
            {
                if (go != null)
                    go.SetActive(true);
            }
            _disabledWeatherObjects.Clear();
        }
    }
}
