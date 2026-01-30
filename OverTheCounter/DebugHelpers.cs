#if DEBUG
using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne.DevUtilities;
using S1API.Console;
using S1API.GameTime;
using S1API.Items;
using S1API.Money;
using S1API.Products;
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

            GUILayout.BeginArea(new Rect(10, 10, 260, 440), "DEV TOOLS", GUI.skin.window);

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

            GUILayout.EndArea();
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
