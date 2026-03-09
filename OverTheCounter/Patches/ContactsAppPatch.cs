using HarmonyLib;
using MelonLoader;
using OverTheCounter.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using ContactsAppType = Il2CppScheduleOne.UI.Phone.ContactsApp.ContactsApp;
using RelationCircleType = Il2CppScheduleOne.UI.Relations.RelationCircle;
using NPCManagerType = Il2CppScheduleOne.NPCs.NPCManager;
using NPCType = Il2CppScheduleOne.NPCs.NPC;
using GameManagerSingleton = Il2CppScheduleOne.DevUtilities.NetworkSingleton<Il2CppScheduleOne.DevUtilities.GameManager>;
#else
using ContactsAppType = ScheduleOne.UI.Phone.ContactsApp.ContactsApp;
using RelationCircleType = ScheduleOne.UI.Relations.RelationCircle;
using NPCManagerType = ScheduleOne.NPCs.NPCManager;
using NPCType = ScheduleOne.NPCs.NPC;
using GameManagerSingleton = ScheduleOne.DevUtilities.NetworkSingleton<ScheduleOne.DevUtilities.GameManager>;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Fixes ContactsApp initialization in multiplayer (client joining host).
    ///
    /// FIX 1 (Prefix on Start):
    ///   If GameManager is not yet ready, skip Start() and retry once it is.
    ///   Prevents the NetworkSingleton null crash at Start() line 23.
    ///
    /// FIX 2 (Postfix on Start):
    ///   If circles still have null AssignedNPC after Start(), waits for NPCRegistry
    ///   to populate then repairs portraits, connection lines, and click handlers.
    ///
    /// UPDATE GUARD (Finalizer on Update):
    ///   Swallows KeyNotFoundException in Update() while RegionDict is empty.
    ///   Uses a Finalizer so base.Update() (home button detection) always runs.
    /// </summary>
    public static class ContactsAppFix
    {
        /// <summary>Instance IDs for which a Fix 1 retry coroutine is in-flight.</summary>
        private static readonly HashSet<int> _fix1Pending = new();

        /// <summary>Instance IDs for which a Fix 2 repair coroutine is currently running.</summary>
        private static readonly HashSet<int> _repairRunning = new();

        /// <summary>
        /// Circle instance IDs for which we have wired Button.onClick directly.
        /// Prevents double-wiring if the coroutine runs twice for the same circle.
        /// </summary>
        private static readonly HashSet<int> _wiredCircles = new();

        /// <summary>
        /// NPC connection IDs cached in NPC.Awake (before FishNet reconciliation destroys scene NPCs).
        /// Key: NPC ID, Value: list of connected NPC IDs. Used as fallback in CreateConnectionLines
        /// when FullGameConnections is empty on FishNet-reconciled client NPCs.
        /// </summary>
        private static readonly Dictionary<string, List<string>> _connectionCache = new();

        /// <summary>Cached FieldInfo for the private RegionDict field (Mono only).</summary>
#if !IL2CPP
        private static FieldInfo _regionDictFieldMono;

        /// <summary>Cached FieldInfo for App&lt;T&gt;.appContainer (Mono only; IL2CPP exposes it public).</summary>
        private static FieldInfo _appContainerFieldMono;
#endif

        /// <summary>Cached MethodInfo for the private ZoomToRect method.</summary>
        private static MethodInfo _zoomToRectMethod;

        /// <summary>Cached MethodInfo for the private Select method.</summary>
        private static MethodInfo _selectMethod;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var startMethod = AccessTools.Method(typeof(ContactsAppType), "Start");
                var updateMethod = AccessTools.Method(typeof(ContactsAppType), "Update");

                if (startMethod == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, "ContactsApp.Start not found — patch skipped.");
                    return;
                }

                // ── FIX 1: Prefix — defer Start() if GameManager not ready ──────────
                harmony.Patch(startMethod,
                    prefix: new HarmonyMethod(typeof(ContactsAppFix), nameof(Fix1_StartPrefix)));

                // ── FIX 2: Postfix — repair circles if NPCRegistry was empty ────────
                harmony.Patch(startMethod,
                    postfix: new HarmonyMethod(typeof(ContactsAppFix), nameof(Fix2_StartPostfix)));

                // ── UPDATE GUARD: suppress RegionDict KeyNotFoundException ───────────
                // Uses a Finalizer (not a Prefix) so base.Update() always runs.
                // base.Update() contains the home-button physics-raycast detection.
                if (updateMethod != null)
                {
                    harmony.Patch(updateMethod,
                        finalizer: new HarmonyMethod(typeof(ContactsAppFix), nameof(UpdateGuard_Finalizer)));
                }

                // ── NPC.Awake cache: capture connections before FishNet destroys scene NPCs ─
                var npcAwakeMethod = AccessTools.Method(typeof(NPCType), "Awake");
                if (npcAwakeMethod != null)
                {
                    harmony.Patch(npcAwakeMethod,
                        postfix: new HarmonyMethod(typeof(ContactsAppFix), nameof(NPC_Awake_Postfix)));
                }

                OTCLog.Msg(OTCLog.Systems.Patch, "ContactsApp multiplayer fix applied.");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"ContactsAppFix.Apply failed: {ex.Message}");
            }
        }

        // ─── Shared helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures appContainer is inactive when the app is not open.
        /// App&lt;T&gt;.Start() calls SetOpen(false) to hide the container, but if Start()
        /// throws before reaching that line (Phone singleton not ready), appContainer
        /// stays active — causing ContactsApp to render on top of other phone apps.
        /// </summary>
        private static void EnsureAppContainerHidden(ContactsAppType instance)
        {
            try
            {
                if (instance == null || instance.isOpen) return;
#if IL2CPP
                var ac = instance.appContainer;
                if (ac != null && ac.gameObject.activeSelf)
                    ac.gameObject.SetActive(false);
#else
                if (_appContainerFieldMono == null)
                {
                    var t = typeof(ContactsAppType).BaseType;
                    while (t != null)
                    {
                        _appContainerFieldMono = t.GetField("appContainer",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_appContainerFieldMono != null) break;
                        t = t.BaseType;
                    }
                }
                var container = _appContainerFieldMono?.GetValue(instance) as RectTransform;
                if (container != null && container.gameObject.activeSelf)
                    container.gameObject.SetActive(false);
#endif
            }
            catch { }
        }

        private static bool IsGameManagerReady()
        {
            try { return GameManagerSingleton.InstanceExists; }
            catch { return false; }
        }

        /// <summary>
        /// Returns the count of entries in the private RegionDict field.
        /// Returns 0 if empty/null, -1 if the field can't be read.
        /// </summary>
        private static int GetRegionDictCount(ContactsAppType instance)
        {
            try
            {
#if IL2CPP
                var dict = instance.RegionDict;
                return dict != null ? (int)dict.Count : 0;
#else
                _regionDictFieldMono ??= typeof(ContactsAppType)
                    .GetField("RegionDict", BindingFlags.NonPublic | BindingFlags.Instance);
                var dict = _regionDictFieldMono?.GetValue(instance) as System.Collections.IDictionary;
                return dict?.Count ?? 0;
#endif
            }
            catch { return -1; }
        }

        /// <summary>
        /// Returns the ConnectionsContainer for the region that physically contains this circle,
        /// determined by traversing the circle's parent hierarchy to find a matching RegionUI.Container.
        /// This avoids npc.Region which returns wrong values for FishNet-reconciled NPCs on client.
        /// </summary>
        private static RectTransform GetConnectionsContainerForCircle(ContactsAppType instance, RelationCircleType circle)
        {
            var regionUIs = instance.RegionUIs;
            if (regionUIs == null) return null;
            foreach (var rui in regionUIs)
            {
                if (rui?.Container != null && circle.transform.IsChildOf(rui.Container))
                    return rui.ConnectionsContainer;
            }
            return null;
        }

        /// <summary>
        /// Replicates ContactsApp.Start() lines 65-122: instantiates connection line GameObjects
        /// between connected NPC circles into the per-region ConnectionsContainer.
        /// Uses circle hierarchy (IsChildOf) to determine region — not npc.Region, which returns
        /// wrong values for FishNet-reconciled NPCs. Returns the number of lines created.
        /// </summary>
        private static int CreateConnectionLines(ContactsAppType instance)
        {
            try
            {
                var connPrefab = instance.ConnectionPrefab;
                if (connPrefab == null) return 0;

                _zoomToRectMethod ??= AccessTools.Method(typeof(ContactsAppType), "ZoomToRect");

                var circles = instance.CirclesContainer?.GetComponentsInChildren<RelationCircleType>(true);
                if (circles == null || circles.Length == 0) return 0;

                // Pre-build O(1) lookup maps. circleToContainer uses IsChildOf (hierarchy) rather
                // than npc.Region, because npc.Region returns wrong enum values for FishNet NPCs.
                var idToCircle = new Dictionary<string, RelationCircleType>(circles.Length, StringComparer.OrdinalIgnoreCase);
                var circleToContainer = new Dictionary<RelationCircleType, RectTransform>(circles.Length);
                foreach (var c in circles)
                {
                    if (c == null) continue;
                    if (!string.IsNullOrEmpty(c.AssignedNPC_ID))
                        idToCircle[c.AssignedNPC_ID] = c;
                    circleToContainer[c] = GetConnectionsContainerForCircle(instance, c);
                }

                int created = 0;
                foreach (var circle in circles)
                {
                    if (circle == null) continue;

                    if (!circleToContainer.TryGetValue(circle, out var connectionsContainer) || connectionsContainer == null)
                        continue;

                    var npc = circle.AssignedNPC;
                    if (npc == null) continue;
                    var relData = npc.RelationData;
                    if (relData == null) continue;

                    // Prefer live FullGameConnections; fall back to the NPC.Awake cache if
                    // FishNet-reconciled NPCs have empty connection lists.
                    var connList = relData.Connections;
                    List<string> connectionIds = null;
                    if (connList != null && connList.Count > 0)
                    {
                        connectionIds = new List<string>(connList.Count);
                        foreach (var other in connList)
                        {
                            if (other != null) connectionIds.Add(other.ID);
                        }
                    }
                    else
                    {
                        _connectionCache.TryGetValue(circle.AssignedNPC_ID, out connectionIds);
                    }

                    if (connectionIds == null || connectionIds.Count == 0) continue;

                    foreach (var otherId in connectionIds)
                    {
                        if (!idToCircle.TryGetValue(otherId, out var otherCircle)) continue;

                        // Skip cross-region lines: compare containers by hierarchy, not npc.Region
                        if (!circleToContainer.TryGetValue(otherCircle, out var otherContainer) || otherContainer != connectionsContainer)
                            continue;

                        // Deduplicate: skip if line already exists (matches vanilla naming at Start() line 108)
                        string fwdName = circle.AssignedNPC_ID + " -> " + otherId;
                        string revName = otherId + " -> " + circle.AssignedNPC_ID;
                        if (connectionsContainer.Find(fwdName) != null || connectionsContainer.Find(revName) != null)
                            continue;

                        // Instantiate and position (mirrors Start() lines 101-106)
                        var lineRT = UnityEngine.Object.Instantiate(connPrefab, connectionsContainer)
                            .GetComponent<RectTransform>();
                        lineRT.name = fwdName;
                        lineRT.anchoredPosition = (otherCircle.Rect.anchoredPosition + circle.Rect.anchoredPosition) / 2f;
                        var vec = otherCircle.Rect.anchoredPosition - circle.Rect.anchoredPosition;
                        lineRT.localRotation = Quaternion.Euler(0f, 0f, -Mathf.Atan2(vec.x, vec.y) * 57.29578f);
                        lineRT.sizeDelta = new Vector2(lineRT.sizeDelta.x,
                            Vector2.Distance(otherCircle.Rect.anchoredPosition, circle.Rect.anchoredPosition));

                        // Wire StartButton/EndButton zoom listeners (mirrors Start() lines 109-116)
                        var otherRT = otherCircle.Rect;
                        var circleRT = circle.Rect;
                        var capturedInst = instance;
                        var zoomMethod = _zoomToRectMethod;
                        var startBtn = lineRT.Find("StartButton")?.GetComponent<Button>();
                        if (startBtn != null)
                            startBtn.onClick.AddListener(new System.Action(() =>
                                zoomMethod?.Invoke(capturedInst, new object[] { otherRT })));
                        var endBtn = lineRT.Find("EndButton")?.GetComponent<Button>();
                        if (endBtn != null)
                            endBtn.onClick.AddListener(new System.Action(() =>
                                zoomMethod?.Invoke(capturedInst, new object[] { circleRT })));

                        created++;
                    }
                }

                return created;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"CreateConnectionLines threw: {ex.Message}");
                return 0;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // FIX 1 — Prefix: skip Start() if GameManager not ready, retry later.
        // ══════════════════════════════════════════════════════════════════════════

        private static bool Fix1_StartPrefix(ContactsAppType __instance)
        {
            int id = __instance.GetInstanceID();

            // Our retry coroutine re-invokes Start() with id in _fix1Pending. Let it through.
            if (_fix1Pending.Contains(id))
            {
                _fix1Pending.Remove(id);
                return true;
            }

            if (!IsGameManagerReady())
            {
                _fix1Pending.Add(id);
                MelonCoroutines.Start(Fix1_RetryStart(__instance, id));
                return false; // skip original Start()
            }

            return true; // GameManager ready — run Start() normally
        }

        private static IEnumerator Fix1_RetryStart(ContactsAppType instance, int id)
        {
            // Wait up to ~60 s (600 frames at 10fps) for GameManager
            for (int i = 0; i < 600; i++)
            {
                yield return null;
                if (instance == null) { _fix1Pending.Remove(id); yield break; }
                try { if (instance.gameObject == null) { _fix1Pending.Remove(id); yield break; } }
                catch { _fix1Pending.Remove(id); yield break; }
                if (IsGameManagerReady()) break;
            }

            if (!IsGameManagerReady())
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"GameManager timed out (inst={id}) — aborting retry.");
                _fix1Pending.Remove(id);
                yield break;
            }

            // A few extra frames to let other singletons settle
            for (int i = 0; i < 3; i++) yield return null;
            if (instance == null) { _fix1Pending.Remove(id); yield break; }

            try
            {
                // _fix1Pending still contains id → Fix1_StartPrefix lets this call through
                var startMethod = AccessTools.Method(typeof(ContactsAppType), "Start");
                startMethod?.Invoke(instance, null);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"Start() retry threw: {ex.Message}");
                _fix1Pending.Remove(id);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // FIX 2 — Postfix: if circles still have null NPCs after Start(), repair.
        // ══════════════════════════════════════════════════════════════════════════

        private static void Fix2_StartPostfix(ContactsAppType __instance)
        {
            int instId = __instance.GetInstanceID();
            try
            {
                var circles = __instance.GetComponentsInChildren<RelationCircleType>(true);
                if (circles == null || circles.Length == 0) return;

                bool anyUnassigned = false;
                foreach (var c in circles)
                {
                    if (c != null && c.AssignedNPC == null) { anyUnassigned = true; break; }
                }

                if (!anyUnassigned) return;
                if (_repairRunning.Contains(instId)) return;

                _repairRunning.Add(instId);
                MelonCoroutines.Start(Fix2_WaitAndRepair(__instance, instId));
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"StartPostfix threw: {ex.Message}");
            }
        }

        private static IEnumerator Fix2_WaitAndRepair(ContactsAppType instance, int instId)
        {
            // Wait for NPCRegistry to stabilize: count > 0 and unchanged for 10 consecutive frames.
            int lastCount = 0, stableFrames = 0;
            for (int i = 0; i < 600; i++)
            {
                yield return null;
                if (instance == null) { _repairRunning.Remove(instId); yield break; }
                try { if (instance.gameObject == null) { _repairRunning.Remove(instId); yield break; } }
                catch { _repairRunning.Remove(instId); yield break; }
                int count = NPCManagerType.NPCRegistry?.Count ?? 0;
                if (count == 0) { stableFrames = 0; continue; }
                if (count != lastCount) { lastCount = count; stableFrames = 0; }
                else if (++stableFrames >= 10) break;
            }

            // Also wait for GameManager (needed for SetSelectedRegion)
            for (int i = 0; i < 300; i++)
            {
                yield return null;
                if (instance == null) { _repairRunning.Remove(instId); yield break; }
                if (IsGameManagerReady()) break;
            }

            if (instance == null) { _repairRunning.Remove(instId); yield break; }

            // ── Repair passes: reassign portraits ────────────────────────────────
            // Only repair production circles (Circles_FullGame). Demo/Tutorial circles
            // behave differently in AssignNPC and crash when repaired (Sam, Fiona).
            int repaired = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                if (pass > 0)
                {
                    for (int i = 0; i < 60; i++) yield return null;
                    if (instance == null) { _repairRunning.Remove(instId); yield break; }
                }

                try
                {
                    var circles = instance.CirclesContainer?.GetComponentsInChildren<RelationCircleType>(true);
                    if (circles == null) break;

                    int stillNull = 0;
                    repaired = 0;
                    foreach (var c in circles)
                    {
                        if (c == null) continue;
                        if (c.AssignedNPC == null) c.LoadNPCData();
                        if (c.AssignedNPC != null)
                        {
                            try { c.AssignNPC(c.AssignedNPC); repaired++; }
                            catch { /* expected for some NPCs (e.g. Sam, Fiona) with null RelationData */ }
                        }
                        else stillNull++;
                    }

                    if (stillNull == 0) break;
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, $"Repair pass {pass + 1} threw: {ex.Message}");
                    break;
                }
            }

            // ── Create missing connection lines ───────────────────────────────────
            if (instance == null) { _repairRunning.Remove(instId); yield break; }
            int linesCreated = CreateConnectionLines(instance);

            // ── Activate CirclesContainer if it ended up inactive ─────────────────
            if (instance == null) { _repairRunning.Remove(instId); yield break; }
            try
            {
                var cc = instance.CirclesContainer;
                if (cc != null && !cc.gameObject.activeSelf)
                    cc.gameObject.SetActive(true);
            }
            catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.Patch, $"Activate CirclesContainer threw: {ex.Message}"); }

            // Let Unity recalculate ContentRect layout before ZoomToRect reads sizeDelta.
            yield return null;
            yield return null;
            yield return null;
            if (instance == null) { _repairRunning.Remove(instId); yield break; }

            // ── Re-call SetSelectedRegion ─────────────────────────────────────────
            try
            {
                if (IsGameManagerReady())
                    instance.SetSelectedRegion(instance.SelectedRegion, true);
                else
                    OTCLog.Warning(OTCLog.Systems.Patch, $"GameManager not ready — skipping SetSelectedRegion (inst={instId}).");
            }
            catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.Patch, $"SetSelectedRegion threw: {ex.Message}"); }

            // ── Select first valid circle to initialize DetailPanel ───────────────
            if (instance == null) { _repairRunning.Remove(instId); yield break; }
            try
            {
                _selectMethod ??= AccessTools.Method(typeof(ContactsAppType), "Select");
                if (_selectMethod != null)
                {
                    var prodCircles = instance.CirclesContainer?.GetComponentsInChildren<RelationCircleType>(true);
                    if (prodCircles != null)
                    {
                        foreach (var c in prodCircles)
                        {
                            if (c != null && c.AssignedNPC != null)
                            {
                                _selectMethod.Invoke(instance, new object[] { c });
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.Patch, $"Select threw: {ex.Message}"); }

            // ── Wire Button.onClick directly for all production circles ───────────
            // Safety net: if Start() threw before the onClicked wiring loop
            // (lines 125-133), circles are unclickable. We wire Button.onClick
            // directly so Select() fires regardless of onClicked state.
            int wired = 0;
            if (instance != null)
            {
                try
                {
                    _selectMethod ??= AccessTools.Method(typeof(ContactsAppType), "Select");
                    var prodCircles = instance.CirclesContainer?.GetComponentsInChildren<RelationCircleType>(true);
                    if (prodCircles != null && _selectMethod != null)
                    {
                        foreach (var c in prodCircles)
                        {
                            if (c == null || !_wiredCircles.Add(c.GetInstanceID())) continue;
                            var capturedCircle = c;
                            var capturedInst = instance;
                            var capturedSelect = _selectMethod;
                            c.Button?.onClick.AddListener(new System.Action(() =>
                            {
                                try { capturedSelect.Invoke(capturedInst, new object[] { capturedCircle }); }
                                catch { }
                            }));
                            wired++;
                        }
                    }
                }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.Patch, $"Click-wiring threw: {ex.Message}"); }
            }

            // Safety net: if Start() threw before SetOpen(false), appContainer stays active
            // and ContactsApp renders on top of other apps. Deactivate it if not open.
            EnsureAppContainerHidden(instance);

            OTCLog.Msg(OTCLog.Systems.Patch, $"ContactsApp repaired: {repaired} portraits, {linesCreated} connection lines, {wired} clicks wired.");

            _repairRunning.Remove(instId);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // NPC.Awake POSTFIX — cache connection IDs before FishNet destroys scene NPCs.
        //
        // Scene-loaded NPCs Awake with FullGameConnections populated from scene data.
        // FishNet then destroys those GameObjects and spawns prefab-based replacements
        // that have empty FullGameConnections. We cache the IDs here so
        // CreateConnectionLines can use them even after the scene NPCs are gone.
        // ══════════════════════════════════════════════════════════════════════════

        private static void NPC_Awake_Postfix(NPCType __instance)
        {
            try
            {
                var id = __instance.ID;
                if (string.IsNullOrEmpty(id)) return;

                var relData = __instance.RelationData;
                if (relData == null) return;

                var connections = relData.Connections;
                if (connections == null || connections.Count == 0) return;

                var ids = new List<string>(connections.Count);
                foreach (var other in connections)
                {
                    if (other == null) continue;
                    var otherId = other.ID;
                    if (!string.IsNullOrEmpty(otherId))
                        ids.Add(otherId);
                }

                if (ids.Count > 0)
                    _connectionCache[id] = ids;
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // UPDATE GUARD — Finalizer: suppress KeyNotFoundException in Update()
        // while RegionDict is empty (Start() not yet completed).
        //
        // Uses a Finalizer so base.Update() ALWAYS runs — base.Update() contains
        // the physics-raycast home-button detection. A prefix returning false would
        // skip it, breaking the home button on the client.
        // ══════════════════════════════════════════════════════════════════════════

        private static Exception UpdateGuard_Finalizer(ContactsAppType __instance, Exception __exception)
        {
            if (__exception != null && GetRegionDictCount(__instance) == 0)
                return null;
            return __exception;
        }

    }
}
