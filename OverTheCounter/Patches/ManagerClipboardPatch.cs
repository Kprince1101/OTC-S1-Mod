using HarmonyLib;
using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.UI;
using System;
using System.Collections;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Tools;
using Il2CppScheduleOne.UI.Management;
#else
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.Interaction;
using ScheduleOne.Management;
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Tools;
using ScheduleOne.UI.Management;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Harmony patches for Manager clipboard integration:
    /// 1. Intercepts clipboard interact to open custom config panel for Manager NPCs
    /// 2. Hooks clipboard close to clean up our panel
    /// 3. Shows outline + crosshair prompt when hovering a Manager with clipboard equipped
    /// </summary>
    public static class ManagerClipboardPatch
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:ManagerClipboard");

        // Highlight tracking
        private static NPC _highlightedNpc;

        /// <summary>
        /// Applies all Manager clipboard patches.
        /// </summary>
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                // Patch ManagementClipboard_Equippable.Update to intercept interact for Manager NPCs
                var updateTarget = AccessTools.Method(
                    typeof(ScheduleOne.Tools.ManagementClipboard_Equippable), "Update");
                if (updateTarget != null)
                {
                    harmony.Patch(updateTarget,
                        prefix: new HarmonyMethod(typeof(ManagerClipboardPatch), nameof(UpdatePrefix)),
                        postfix: new HarmonyMethod(typeof(ManagerClipboardPatch), nameof(UpdatePostfix)));
                    if (Config.ManagerVerboseLogging.Value)
                        Logger.Msg("Patched ManagementClipboard_Equippable.Update (prefix + postfix)");
                }
                else
                {
                    Logger.Warning("ManagementClipboard_Equippable.Update not found");
                }

                // Patch EmployeeHome.SetAssignedEmployee to clear our manager when another employee takes the locker
                var setEmployeeTarget = AccessTools.Method(
                    typeof(ScheduleOne.Employees.EmployeeHome), "SetAssignedEmployee",
                    new[] { typeof(ScheduleOne.Employees.Employee) });
                if (setEmployeeTarget != null)
                {
                    harmony.Patch(setEmployeeTarget,
                        postfix: new HarmonyMethod(typeof(ManagerClipboardPatch), nameof(SetAssignedEmployeePostfix)));
                    if (Config.ManagerVerboseLogging.Value)
                        Logger.Msg("Patched EmployeeHome.SetAssignedEmployee");
                }

                // Patch ManagementClipboard_Equippable.Unequip to clear manager outline
                var unequipTarget = AccessTools.Method(
                    typeof(ScheduleOne.Tools.ManagementClipboard_Equippable), "Unequip");
                if (unequipTarget != null)
                {
                    harmony.Patch(unequipTarget,
                        postfix: new HarmonyMethod(typeof(ManagerClipboardPatch), nameof(UnequipPostfix)));
                    if (Config.ManagerVerboseLogging.Value)
                        Logger.Msg("Patched ManagementClipboard_Equippable.Unequip");
                }

                // Patch ManagementClipboard.Close to clean up our panel
                var closeTarget = AccessTools.Method(
                    typeof(ScheduleOne.Tools.ManagementClipboard), "Close",
                    new[] { typeof(bool) });
                if (closeTarget != null)
                {
                    harmony.Patch(closeTarget,
                        postfix: new HarmonyMethod(typeof(ManagerClipboardPatch), nameof(ClosePostfix)));
                    if (Config.ManagerVerboseLogging.Value)
                        Logger.Msg("Patched ManagementClipboard.Close");
                }
                else
                {
                    Logger.Warning("ManagementClipboard.Close not found");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to apply Manager clipboard patches: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix on ManagementClipboard_Equippable.Update().
        /// When the player presses Interact while clipboard is equipped and not open,
        /// checks if they're looking at a Manager NPC and opens our custom panel instead.
        /// Manager raycast runs BEFORE the HoveredValidInteractableObject guard because
        /// Manager NPCs inherit an InteractableObject that would otherwise block us.
        /// </summary>
        private static bool UpdatePrefix(ScheduleOne.Tools.ManagementClipboard_Equippable __instance)
        {
            try
            {
                // Only intercept when clipboard is NOT already open
                if (Singleton<ManagementClipboard>.Instance == null) return true;
                if (Singleton<ManagementClipboard>.Instance.IsOpen) return true;

                // Only on interact press
                if (!GameInput.GetButtonDown(GameInput.ButtonCode.Interact)) return true;

                // Raycast for NPC using RaycastAll (avoids Default-layer furniture blocking detection)
                var npc = RaycastForNpc();
                if (npc == null) return true;

                var mgr = FindManagerByNpc(npc);
                if (mgr == null) return true;

                // Safety checks: don't open if another management selector is already active.
                // If ManagementInterface isn't accessible yet (first-ever clipboard use,
                // Awake hasn't fired), skip — no selectors can be open if MI hasn't initialized.
                try
                {
                    var mi = Singleton<ManagementInterface>.Instance;
                    if (mi != null)
                    {
                        if (mi.ObjectSelector?.IsOpen == true) return true;
                        if (mi.TransitEntitySelector?.IsOpen == true) return true;
                        if (mi.NPCSelector?.IsOpen == true) return true;
                    }
                }
                catch { /* MI not ready — safe to proceed */ }

                if (GameInput.IsTyping) return true;

                // Suppress the NPC's InteractableObject to prevent InteractionManager
                // from also processing this E press (which would open dialogue)
                SuppressNpcInteract(npc);

                // Open vanilla clipboard with empty configurable list (gives us the clipboard UX)
                var emptyList = new GameSystem.Collections.Generic.List<IConfigurable>();
                Singleton<ManagementClipboard>.Instance.Open(emptyList, __instance);

                // Inject our custom panel (EnforceUI in postfix handles persistent label fixes)
                ManagerConfigPanel.Open(mgr);

                if (Config.ManagerVerboseLogging.Value)
                    Logger.Msg($"Opened config panel for manager {mgr.Id}");
                return false; // Skip vanilla Update logic for this frame
            }
            catch (Exception ex)
            {
                Logger.Error($"UpdatePrefix error: {ex.Message}\n{ex.StackTrace}");
                return true;
            }
        }

        /// <summary>
        /// Postfix on ManagementClipboard_Equippable.Update().
        /// Manages outline highlight + crosshair prompt for Manager NPCs when clipboard is equipped.
        /// Runs every frame while clipboard is equipped (the Update method only fires while equipped).
        /// </summary>
        private static void UpdatePostfix()
        {
            try
            {
                // Tick the route entity selector even when clipboard is closed
                // (RouteEntitySelector.Open() closes the clipboard, so it must tick independently)
                if (RouteEntitySelector.IsOpen)
                {
                    RouteEntitySelector.Tick();
                    return;
                }

                // When clipboard is open, enforce our UI overrides every frame
                if (Singleton<ManagementClipboard>.Instance != null &&
                    Singleton<ManagementClipboard>.Instance.IsOpen)
                {
                    if (ManagerConfigPanel.IsOpen)
                        ManagerConfigPanel.EnforceUI();

                    ClearHighlight();
                    return;
                }

                NPC hitNpc = RaycastForNpc();
                ManagerInstance hitMgr = null;

                if (hitNpc != null)
                    hitMgr = FindManagerByNpc(hitNpc);

                if (hitMgr != null && hitNpc != null)
                {
                    // Highlight this manager
                    if (_highlightedNpc != hitNpc)
                    {
                        // Clear previous highlight if different NPC
                        ClearHighlight();
                        _highlightedNpc = hitNpc;
                        hitNpc.ShowOutline(Color.white);
                    }

                    // Show crosshair prompt
                    if (Singleton<ManagementWorldspaceCanvas>.InstanceExists)
                        Singleton<ManagementWorldspaceCanvas>.Instance.ShowCrosshairPrompt("Manage Manager");
                }
                else
                {
                    ClearHighlight();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"UpdatePostfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears highlight outline and crosshair prompt from the previously highlighted manager.
        /// </summary>
        private static void ClearHighlight()
        {
            if (_highlightedNpc != null)
            {
                try { _highlightedNpc.HideOutline(); } catch { }
                _highlightedNpc = null;
            }

            try
            {
                if (Singleton<ManagementWorldspaceCanvas>.InstanceExists)
                    Singleton<ManagementWorldspaceCanvas>.Instance.HideCrosshairPrompt();
            }
            catch { }
        }

        /// <summary>
        /// Postfix on ManagementClipboard.Close().
        /// Cleans up our config panel when the clipboard is closed (by any means).
        /// </summary>
        private static void ClosePostfix(bool preserveState)
        {
            try
            {
                // Don't close our panel if the clipboard is preserving state
                // (ObjectSelector temporarily closes clipboard)
                if (preserveState) return;

                if (ManagerConfigPanel.IsOpen)
                {
                    ManagerConfigPanel.Close();
                    if (Config.ManagerVerboseLogging.Value)
                        Logger.Msg("Config panel closed (clipboard closed)");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"ClosePostfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on ManagementClipboard_Equippable.Unequip().
        /// Clears manager outline when clipboard is put away (Update stops running, so the
        /// postfix highlight cleanup never gets a chance to clear it).
        /// </summary>
        private static void UnequipPostfix()
        {
            ClearHighlight();
        }

        /// <summary>
        /// Postfix on EmployeeHome.SetAssignedEmployee().
        /// When another employee (vanilla or other mods) is assigned to a locker that
        /// a Manager is using, clears the Manager's locker assignment.
        /// </summary>
        private static void SetAssignedEmployeePostfix(ScheduleOne.Employees.EmployeeHome __instance,
            ScheduleOne.Employees.Employee employee)
        {
            try
            {
                // Only care when a non-null employee is being assigned (not cleared)
                if (employee == null) return;

                foreach (var mgr in ManagerInstance.Active.Values)
                {
                    if (mgr.AssignedLocker == __instance)
                    {
                        Logger.Msg($"Locker claimed by employee '{employee.fullName}', clearing manager {mgr.Id}");
                        mgr.ClearLocker();
                        mgr.Configuration.Locker = null;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"SetAssignedEmployeePostfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Raycasts forward from camera using RaycastAll to find the closest NPC.
        /// RaycastAll avoids Default-layer objects (furniture, counters) blocking NPC detection
        /// that would occur with single Physics.Raycast.
        /// </summary>
        private static NPC RaycastForNpc(float maxDistance = 5f)
        {
            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
            if (playerCamera == null) return null;

            var hits = Physics.RaycastAll(
                playerCamera.transform.position,
                playerCamera.transform.forward,
                maxDistance,
                LayerMask.GetMask("NPC", "Default") | (1 << 31), // layer 31 = NPCInteract360's relocated capsule colliders
                QueryTriggerInteraction.Ignore);

            if (hits == null || hits.Length == 0) return null;

            NPC closestNpc = null;
            float closestDist = float.MaxValue;

            foreach (var hit in hits)
            {
                var npc = hit.collider?.GetComponentInParent<NPC>();
                if (npc != null && hit.distance < closestDist)
                {
                    closestNpc = npc;
                    closestDist = hit.distance;
                }
            }

            return closestNpc;
        }

        /// <summary>
        /// Finds the ManagerInstance that owns a given NPC.
        /// Tries reference equality first, then falls back to ID comparison
        /// (IL2CPP managed wrappers can differ after save/load even for the same native object).
        /// </summary>
        private static ManagerInstance FindManagerByNpc(NPC npc)
        {
            if (npc == null) return null;

            // Fast path: reference equality
            foreach (var mgr in ManagerInstance.Active.Values)
            {
                if (mgr.GameNpc == npc)
                    return mgr;
            }

            // Fallback: match by NPC.ID (survives save/load reference mismatches)
            string npcId = npc.ID;
            if (!string.IsNullOrEmpty(npcId))
            {
                foreach (var mgr in ManagerInstance.Active.Values)
                {
                    if (mgr.Id == npcId)
                    {
                        // Re-link the reference so future lookups use the fast path
                        if (Config.ManagerVerboseLogging.Value)
                            Logger.Msg($"FindManagerByNpc: re-linked {npcId} via ID fallback");
                        return mgr;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Suppresses the NPC's InteractableObject for one frame to prevent
        /// InteractionManager from opening dialogue on the same E press.
        /// </summary>
        private static void SuppressNpcInteract(NPC npc)
        {
            try
            {
                var intObj = npc.GetComponentInChildren<InteractableObject>();
                if (intObj != null && intObj.enabled)
                {
                    intObj.enabled = false;
                    MelonCoroutines.Start(ReenableIntObj(intObj));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"SuppressNpcInteract error: {ex.Message}");
            }
        }

        private static IEnumerator ReenableIntObj(InteractableObject intObj)
        {
            yield return null;
            try
            {
                if (intObj != null)
                    intObj.enabled = true;
            }
            catch { }
        }

    }
}
