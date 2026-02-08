#if DEBUG
using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne.DevUtilities;
using S1API.Console;
using S1API.GameTime;
using S1API.Items;
using S1API.Money;
using S1API.Products;
using OverTheCounter.Logic;
using UnityEngine;
using MelonLoader;
using System;
using System.Linq;
using System.Reflection;

namespace OverTheCounter
{
    public class DebugHelpers : MonoBehaviour
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("DebugHelpers");
        private bool _menuVisible;
        private Vector2 _scrollPos;

        private static readonly FieldInfo S1ItemInstanceField =
            typeof(S1API.Items.ItemInstance).GetField("S1ItemInstance",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly PropertyInfo S1ItemDefProperty =
            typeof(S1API.Items.ItemDefinition).GetProperty("S1ItemDefinition",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static ProductDefinition _cachedWeedDef;
        private static ProductDefinition _cachedMethDef;
        private static ProductDefinition _cachedCocaineDef;

        public static void Register()
        {
            ClassInjector.RegisterTypeInIl2Cpp<DebugHelpers>();
        }

        public DebugHelpers() : base() { }
        public DebugHelpers(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                _menuVisible = !_menuVisible;
                if (_menuVisible)
                    RefreshCachedDefinitions();
            }
        }

        private static void RefreshCachedDefinitions()
        {
            try
            {
                if (_cachedWeedDef == null)
                {
                    var discovered = ProductPopulator.GetWeedDefinitions();
                    _cachedWeedDef = (discovered != null && discovered.Count > 0)
                        ? discovered[0]
                        : FindFromRegistry<Il2CppScheduleOne.Product.WeedDefinition>();
                }

                if (_cachedMethDef == null)
                {
                    var discovered = ProductPopulator.GetMethDefinitions();
                    _cachedMethDef = (discovered != null && discovered.Count > 0)
                        ? discovered[0]
                        : FindFromRegistry<Il2CppScheduleOne.Product.MethDefinition>();
                }

                if (_cachedCocaineDef == null)
                {
                    var discovered = ProductPopulator.GetCocaineDefinitions();
                    _cachedCocaineDef = (discovered != null && discovered.Count > 0)
                        ? discovered[0]
                        : FindFromRegistry<Il2CppScheduleOne.Product.CocaineDefinition>();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"RefreshCachedDefinitions: {ex.Message}");
            }
        }

        private static ProductDefinition FindFromRegistry<T>() where T : Il2CppSystem.Object
        {
            try
            {
                var allItems = ItemManager.GetAllItemDefinitions();
                foreach (var item in allItems)
                {
                    if (!(item is ProductDefinition pd)) continue;

                    try
                    {
                        var il2cppDef = S1ItemDefProperty?.GetValue(item) as Il2CppScheduleOne.ItemFramework.ItemDefinition;
                        if (il2cppDef?.TryCast<T>() != null)
                            return pd;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"FindFromRegistry<{typeof(T).Name}>: {ex.Message}");
            }
            return null;
        }

        private void OnGUI()
        {
            if (!_menuVisible) return;

            float menuHeight = Mathf.Min(Screen.height - 20f, 900f);
            GUILayout.BeginArea(new Rect(10, 10, 300, menuHeight), "DEV TOOLS", GUI.skin.window);
            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            // Show current player position
            try
            {
                var p = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                if (p != null)
                {
                    var pos = p.transform.position;
                    GUILayout.Label($"Pos: ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})  Y:{p.transform.eulerAngles.y:F0}");
                }
            }
            catch { }

            if (GUILayout.Button("+$1000 Cash"))
                Money.ChangeCashBalance(1000f, true, true);

            if (GUILayout.Button("+$1000 Bank"))
                Money.CreateOnlineTransaction("Debug Deposit", 1000f, 1f, "Debug");

            if (GUILayout.Button("+100 XP"))
                ConsoleHelper.GiveXp(100);

            if (GUILayout.Button("Set Night (22:00)"))
                ConsoleHelper.SetTime("2200");

            if (GUILayout.Button("+1 Hour"))
            {
                int current = TimeManager.CurrentTime;
                int hours = (current / 100 + 1) % 24;
                int mins = current % 100;
                ConsoleHelper.SetTime((hours * 100 + mins).ToString("D4"));
            }

            if (GUILayout.Button("Force Save"))
                ConsoleHelper.SaveGame();

            GUILayout.Space(8);

            if (GUILayout.Button("+5 Weed Jars (5g)"))
                SpawnPackagedProduct(_cachedWeedDef, "jar", 5, 5, "weed jars");

            if (GUILayout.Button("+5 Weed Baggies (1g)"))
                SpawnPackagedProduct(_cachedWeedDef, "baggie", 1, 5, "weed baggies");

            if (GUILayout.Button("+5 Meth Baggies (1g)"))
                SpawnPackagedProduct(_cachedMethDef, "baggie", 1, 5, "meth baggies");

            if (GUILayout.Button("+5 Cocaine Baggies (1g)"))
                SpawnPackagedProduct(_cachedCocaineDef, "baggie", 1, 5, "cocaine baggies");

            if (GUILayout.Button("+5 Premium Meth Baggies (1g)"))
                SpawnPackagedProduct(_cachedMethDef, "baggie", 1, 5, "premium meth baggies", Il2CppScheduleOne.ItemFramework.EQuality.Premium);

            GUILayout.Space(8);

            if (GUILayout.Button(_speedBoosted ? "Speed: BOOSTED (2.4x)" : "Speed Boost (2.4x)"))
                ToggleSpeedBoost();

            GUILayout.Space(8);

            if (GUILayout.Button("Force Desperation (Meth)"))
            {
                try
                {
                    RefreshCachedDefinitions();
                    if (_cachedMethDef != null)
                    {
                        DesperationManager.DebugProductId = _cachedMethDef.ID;
                        if (!DesperationManager.DebugForceRandomTrigger())
                            DesperationManager.DebugProductId = null;
                    }
                    else
                    {
                        Logger.Warning("No meth definition cached for desperation debug");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Force Desperation failed: {ex.Message}");
                    DesperationManager.DebugProductId = null;
                }
            }

            GUILayout.Space(8);

            // Drifter debug buttons
            GUILayout.Label($"Drifters: {DrifterManager.DebugGetStatus().Split('\n')[1]}");

            if (GUILayout.Button("Spawn Drifter (Random)"))
                DrifterManager.DebugSpawnDrifter(DrifterTypeWeights.GetRandomType());

            if (GUILayout.Button("Spawn Drifter (Normal)"))
                DrifterManager.DebugSpawnDrifter(DrifterType.Normal);

            if (GUILayout.Button("Spawn Drifter (Whale)"))
                DrifterManager.DebugSpawnDrifter(DrifterType.Whale);

            if (GUILayout.Button("Spawn Drifter (Fiend)"))
                DrifterManager.DebugSpawnDrifter(DrifterType.Fiend);

            if (GUILayout.Button("Spawn Drifter (Robber)"))
                DrifterManager.DebugSpawnDrifter(DrifterType.Robber);

            if (GUILayout.Button("Spawn Drifter (Narc)"))
                DrifterManager.DebugSpawnDrifter(DrifterType.Narc);

            if (GUILayout.Button("Despawn All Drifters"))
                DrifterInstance.CleanupAll();

            GUILayout.Space(8);

            // Manager debug buttons
            GUILayout.Label($"Managers: {ManagerInstance.Active.Count} active");

            if (GUILayout.Button("Hire Manager (nearest biz)"))
                ManagerController.DebugSpawnManager();

            if (GUILayout.Button("Despawn All Managers"))
                ManagerInstance.CleanupAll();

            if (GUILayout.Button("Manager Status"))
            {
                var status = ManagerController.DebugGetStatus();
                Logger.Msg($"[Debug] {status}");
            }

            GUILayout.Space(8);

            // --- Hotspot Editor ---
            GUILayout.Label("--- Hotspot Editor ---");

            GUILayout.Label($"Hotspots: {DrifterHotspots.AllHotspots.Count} in code");

            // Step 1: Mark spawn
            if (_hsStep == 0)
            {
                GUILayout.Label("Walk to SPAWN point, face away from dest.");
                if (GUILayout.Button("1. Mark SPAWN"))
                {
                    try
                    {
                        var player = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                        if (player != null)
                        {
                            _hsSpawnPos = player.transform.position;
                            _hsSpawnYRot = player.transform.eulerAngles.y;
                            _hsStep = 1;
                            Logger.Msg($"[HOTSPOT] Spawn marked: ({_hsSpawnPos.x:F2}, {_hsSpawnPos.y:F2}, {_hsSpawnPos.z:F2}) Y:{_hsSpawnYRot:F1}");
                        }
                    }
                    catch (Exception ex) { Logger.Warning($"Mark spawn failed: {ex.Message}"); }
                }
            }
            // Step 2: Mark destination
            else if (_hsStep == 1)
            {
                GUILayout.Label($"Spawn: ({_hsSpawnPos.x:F1}, {_hsSpawnPos.z:F1}) Y:{_hsSpawnYRot:F0}");
                GUILayout.Label("Walk to DESTINATION, face where NPC looks.");
                if (GUILayout.Button("2. Mark DESTINATION"))
                {
                    try
                    {
                        var player = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                        if (player != null)
                        {
                            _hsDestPos = player.transform.position;
                            _hsDestYRot = player.transform.eulerAngles.y;
                            _hsStep = 2;
                            _hsDesc = "";
                            Logger.Msg($"[HOTSPOT] Dest marked: ({_hsDestPos.x:F2}, {_hsDestPos.y:F2}, {_hsDestPos.z:F2}) Y:{_hsDestYRot:F1}");
                        }
                    }
                    catch (Exception ex) { Logger.Warning($"Mark dest failed: {ex.Message}"); }
                }
                if (GUILayout.Button("Undo Spawn"))
                    _hsStep = 0;
            }
            // Step 3: Describe & save
            else if (_hsStep == 2)
            {
                GUILayout.Label($"Spawn: ({_hsSpawnPos.x:F1}, {_hsSpawnPos.z:F1}) Y:{_hsSpawnYRot:F0}");
                GUILayout.Label($"Dest:  ({_hsDestPos.x:F1}, {_hsDestPos.z:F1}) Y:{_hsDestYRot:F0}");
                GUILayout.Space(4);
                _hsDesc = DrawInputField("Desc", _hsDesc ?? "", 0);

                if (GUILayout.Button("SAVE HOTSPOT"))
                {
                    string desc = string.IsNullOrWhiteSpace(_hsDesc) ? "TODO" : _hsDesc.Trim();
                    _hotspotCounter++;

                    string spawnVec = $"new Vector3({_hsSpawnPos.x:F2}f, {_hsSpawnPos.y:F2}f, {_hsSpawnPos.z:F2}f)";
                    string destVec = $"new Vector3({_hsDestPos.x:F2}f, {_hsDestPos.y:F2}f, {_hsDestPos.z:F2}f)";

                    string code = $"new Hotspot(\"Spot {_hotspotCounter}\", \"{desc}\",\n"
                                + $"    {destVec}, {_hsDestYRot:F1}f,\n"
                                + $"    {spawnVec}, {_hsSpawnYRot:F1}f),";

                    GUIUtility.systemCopyBuffer = code;
                    Logger.Msg($"[HOTSPOT] === Spot {_hotspotCounter} saved ===");
                    Logger.Msg($"[HOTSPOT] Desc: \"{desc}\"");
                    Logger.Msg($"[HOTSPOT] Code (copied):\n{code}");

                    _hsStep = 0;
                    _hsDesc = "";
                    _activeInputField = -1;
                }
                if (GUILayout.Button("Undo Dest"))
                    _hsStep = 1;
            }

            if (_hotspotCounter > 0)
                GUILayout.Label($"Saved this session: {_hotspotCounter}");

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private bool _speedBoosted;

        // Hotspot editor state (3-step: 0=spawn, 1=dest, 2=describe)
        private int _hsStep;
        private Vector3 _hsSpawnPos;
        private float _hsSpawnYRot;
        private Vector3 _hsDestPos;
        private float _hsDestYRot;
        private string _hsDesc = "";
        private int _hotspotCounter;
        private int _activeInputField = -1;

        /// <summary>
        /// Custom text input that avoids GUILayout.TextField (stripped in IL2CPP).
        /// Click to focus, type with keyboard, Enter/Escape to unfocus.
        /// </summary>
        private string DrawInputField(string label, string value, int fieldId)
        {
            bool isActive = _activeInputField == fieldId;
            string display = isActive
                ? $"{label}: {value}_"
                : $"{label}: {(string.IsNullOrEmpty(value) ? "(click)" : value)}";

            if (GUILayout.Button(display, isActive ? GUI.skin.box : GUI.skin.button))
                _activeInputField = isActive ? -1 : fieldId;

            if (isActive)
            {
                Event e = Event.current;
                if (e.type == EventType.KeyDown)
                {
                    if (e.keyCode == KeyCode.Backspace && value.Length > 0)
                    {
                        value = value.Substring(0, value.Length - 1);
                        e.Use();
                    }
                    else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.Escape)
                    {
                        _activeInputField = -1;
                        e.Use();
                    }
                    else if (e.character != '\0' && !char.IsControl(e.character))
                    {
                        value += e.character;
                        e.Use();
                    }
                }
            }

            return value;
        }

        private void ToggleSpeedBoost()
        {
            try
            {
                _speedBoosted = !_speedBoosted;
                ConsoleHelper.SetPlayerMoveSpeedMultiplier(_speedBoosted ? 2.4f : 1f);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Speed boost failed: {ex.Message}");
                _speedBoosted = false;
            }
        }

        private static void SpawnPackagedProduct(ProductDefinition productDef, string packagingId, int gramsPerUnit, int count, string label,
            Il2CppScheduleOne.ItemFramework.EQuality? quality = null)
        {
            try
            {
                if (productDef == null)
                {
                    Logger.Warning($"No product definition found for {label}");
                    return;
                }

                PackagingDefinition packaging = ProductPopulator.GetPackaging(packagingId);
                if (packaging == null)
                {
                    Logger.Warning($"Packaging '{packagingId}' not found");
                    return;
                }

                var playerInv = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance;
                if (playerInv?.hotbarSlots == null)
                    return;

                ProductInstance product = ProductPopulator.CreatePackagedProduct(productDef, packaging, gramsPerUnit);
                if (product == null)
                    return;

                var il2cppItem = S1ItemInstanceField?.GetValue(product) as Il2CppScheduleOne.ItemFramework.ItemInstance;

                if (quality.HasValue)
                {
                    var qualityItem = il2cppItem?.TryCast<Il2CppScheduleOne.ItemFramework.QualityItemInstance>();
                    qualityItem?.SetQuality(quality.Value);
                }
                if (il2cppItem == null)
                    return;

                int remaining = count;

                for (int i = 0; i < playerInv.hotbarSlots.Count && remaining > 0; i++)
                {
                    var slot = playerInv.hotbarSlots[i];
                    if (slot == null || slot.ItemInstance == null) continue;

                    try
                    {
                        if (!slot.ItemInstance.CanStackWith(il2cppItem)) continue;

                        int stackLimit;
                        try { stackLimit = slot.ItemInstance.StackLimit; }
                        catch { stackLimit = 20; }

                        int canAdd = stackLimit - slot.Quantity;
                        if (canAdd <= 0) continue;

                        int toAdd = Math.Min(canAdd, remaining);
                        slot.ChangeQuantity(toAdd);
                        remaining -= toAdd;
                    }
                    catch { }
                }

                for (int i = 0; i < playerInv.hotbarSlots.Count && remaining > 0; i++)
                {
                    var slot = playerInv.hotbarSlots[i];
                    if (slot == null || slot.ItemInstance != null) continue;

                    try
                    {
                        int stackLimit;
                        try { stackLimit = il2cppItem.StackLimit; }
                        catch { stackLimit = 20; }

                        int toPlace = Math.Min(remaining, stackLimit);
                        var copy = il2cppItem.GetCopy(toPlace);
                        slot.SetStoredItem(copy);
                        remaining -= toPlace;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Spawn {label} failed: {ex.Message}");
            }
        }
    }
}
#endif
