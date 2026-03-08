using MelonLoader;
using MelonLoader.Preferences;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace OverTheCounter
{
    public static class Config
    {
        // ── Manager System ──
        private static MelonPreferences_Category _managers;

        public static ConfigEntry<float> ManagerDailyWage;
        public static ConfigEntry<float> ManagerSigningFee;
        public static ConfigEntry<bool> AlternateHire;

        // ── Vic Laundering ──
        private static MelonPreferences_Category _laundering;

        public static ConfigEntry<float> VicTier1Cost;
        public static ConfigEntry<float> VicTier1Return;
        public static ConfigEntry<int> VicTier2TrustUnlock;
        public static ConfigEntry<float> VicTier2Cost;
        public static ConfigEntry<float> VicTier2Return;
        public static ConfigEntry<int> VicIntroWeedGrams;

        // ── Bella Protocol ──
        private static MelonPreferences_Category _bella;

        public static ConfigEntry<float> BellaWeedValue;
        public static ConfigEntry<float> BellaMethValue;
        public static ConfigEntry<float> BellaCokeValue;

        // ── Contract Notifications ──
        private static MelonPreferences_Category _notifications;

        public static ConfigEntry<bool> ConsolidationEnabled;
        public static ConfigEntry<int> ConsolidationThreshold;

        // ── Static Subscription ──
        private static MelonPreferences_Category _subscription;

        public static ConfigEntry<float> SaasWeeklyCost;
        public static ConfigEntry<int> SaasCycleDays;
        public static ConfigEntry<float> AtmDepositTrigger;
        public static ConfigEntry<float> StaticTier1BankCost;
        public static ConfigEntry<int> StaticTier1WeedGrams;
        public static ConfigEntry<float> StaticTier2BankCost;
        public static ConfigEntry<int> StaticTier2MethGrams;
        public static ConfigEntry<float> StaticTier3BankCost;
        public static ConfigEntry<int> StaticTier3PremiumMethGrams;

        // ── World ──
        private static MelonPreferences_Category _world;

        public static ConfigEntry<int> StackSizeMultiplier;
        public static ConfigEntry<bool> GraffitiReEdit;
        public static ConfigEntry<bool> RecipePinEnabled;

        // ── Desperation System ──
        private static MelonPreferences_Category _desperation;

        public static ConfigEntry<bool> DesperationEnabled;
        public static ConfigEntry<float> FiendAddictionThreshold;
        public static ConfigEntry<float> TriggerChancePerHour;
        public static ConfigEntry<int> MaxEventsPerDay;
        public static ConfigEntry<int> ResponseDeadlineMinutes;
        public static ConfigEntry<int> DeadlineMinutes;
        public static ConfigEntry<float> BonusMultiplier;
        public static ConfigEntry<float> RelationshipPenalty;
        public static ConfigEntry<int> CooldownMinutes;
        public static ConfigEntry<int> DayStartHour;
        public static ConfigEntry<int> DayEndHour;

        // ── Drifter System ──
        private static MelonPreferences_Category _drifters;

        public static ConfigEntry<bool> DrifterEnabled;
        public static ConfigEntry<float> DrifterSpawnChancePerHour;
        public static ConfigEntry<int> MaxActiveDrifters;
        public static ConfigEntry<int> DrifterDayStartHour;
        public static ConfigEntry<int> DrifterDayEndHour;
        public static ConfigEntry<int> DrifterOfferWindowMin;
        public static ConfigEntry<int> DrifterDeliveryDeadlineMin;
        public static ConfigEntry<int> DrifterLingerMinMin;
        public static ConfigEntry<int> DrifterLingerMaxMin;
        public static ConfigEntry<float> DrifterMinDealValue;

        // ── Debug ──
        private static MelonPreferences_Category _debug;

        public static ConfigEntry<bool> VerboseLogging;
        public static ConfigEntry<bool> ManagerVerboseLogging;
        public static ConfigEntry<bool> DrifterVerboseLogging;
        public static ConfigEntry<bool> DesperationVerboseLogging;
        public static ConfigEntry<bool> NpcVerboseLogging;
        public static ConfigEntry<bool> NetworkVerboseLogging;
        public static ConfigEntry<bool> QuestVerboseLogging;
        public static ConfigEntry<bool> NotificationVerboseLogging;
        public static ConfigEntry<bool> PatchVerboseLogging;

        // ── Minimap ──
        private static MelonPreferences_Category _minimap;

        public static ConfigEntry<bool> MinimapEnabled;
        public static ConfigEntry<int> MinimapSize;
        public static ConfigEntry<bool> MinimapRotateWithPlayer;
        public static ConfigEntry<bool> MinimapCircle;
        public static ConfigEntry<int> MinimapDefaultZoom;
        public static ConfigEntry<float> MinimapIconScale;
        public static MelonPreferences_Entry<KeyCode> MinimapToggleKey;
        public static ConfigEntry<int> MinimapHorizontalOffset;
        public static ConfigEntry<int> MinimapVerticalOffset;
        public static ConfigEntry<bool> MinimapInfoOnTop;
        public static MelonPreferences_Entry<Color> MinimapBorderColor;
        public static ConfigEntry<int> MinimapBorderWidth;
        public static ConfigEntry<bool> MinimapShowTime;
        public static ConfigEntry<bool> MinimapShowDay;
        public static ConfigEntry<bool> MinimapUse24HourClock;
        public static ConfigEntry<bool> MinimapShowRank;
        public static ConfigEntry<bool> MinimapShowCompass;
        public static ConfigEntry<bool> MinimapShowEdgeIndicators;

        // ── Minimap POIs ──
        private static MelonPreferences_Category _minimapPoi;

        public static ConfigEntry<bool> MinimapShowPotentialCustomers;
        public static ConfigEntry<bool> MinimapShowCustomers;
        public static ConfigEntry<bool> MinimapShowDealers;
        public static ConfigEntry<bool> MinimapShowDeadDrops;
        public static ConfigEntry<bool> MinimapShowContracts;
        public static ConfigEntry<bool> MinimapShowQuests;
        public static ConfigEntry<bool> MinimapShowProperties;
        public static ConfigEntry<bool> MinimapShowManagers;

        // All entries for bulk operations
        private static readonly Dictionary<string, ConfigEntry<float>> _floatEntries = new();
        private static readonly Dictionary<string, ConfigEntry<int>> _intEntries = new();
        private static readonly Dictionary<string, ConfigEntry<bool>> _boolEntries = new();

        // Settings that are purely local (cosmetic/UI) and should never be
        // synced from host to client. Each client reads their own preference.
        private static readonly HashSet<string> _localOnlyKeys = new()
        {
            "ConsolidationEnabled",
            "ConsolidationThreshold",
            "ManagerVerboseLogging",
            "DrifterVerboseLogging",
            "DesperationVerboseLogging",
            "NpcVerboseLogging",
            "NetworkVerboseLogging",
            "QuestVerboseLogging",
            "NotificationVerboseLogging",
            "PatchVerboseLogging",
            "VerboseLogging",
            "MinimapEnabled",
            "MinimapSize",
            "MinimapRotateWithPlayer",
            "MinimapCircle",
            "MinimapDefaultZoom",
            "MinimapIconScale",
            "MinimapHorizontalOffset",
            "MinimapVerticalOffset",
            "MinimapInfoOnTop",
            "MinimapBorderWidth",
            "MinimapShowTime",
            "MinimapShowDay",
            "MinimapUse24HourClock",
            "MinimapShowPotentialCustomers",
            "MinimapShowCustomers",
            "MinimapShowDealers",
            "MinimapShowDeadDrops",
            "MinimapShowContracts",
            "MinimapShowQuests",
            "MinimapShowProperties",
            "MinimapShowManagers",
            "MinimapShowRank",
            "MinimapShowCompass",
            "MinimapShowEdgeIndicators",
            "RecipePinEnabled"
        };

        public static void Initialize()
        {
            // ── Manager System ──
            _managers = MelonPreferences.CreateCategory("OverTheCounter_Managers", "Manager System");

            ManagerDailyWage = Register(_managers.CreateEntry("ManagerDailyWage", 350f, "Base Daily Wage",
                "Base daily wage before upgrade fees are added"));
            ManagerSigningFee = Register(_managers.CreateEntry("ManagerSigningFee", 3000f, "Signing Fee",
                "One-time fee deducted from player cash when hiring a manager"));
            AlternateHire = Register(_managers.CreateEntry("AlternateHire", false, "Alternate Hire",
                "Show hire buttons in the OTC app instead of using Manny's dialogue. Enable if another mod conflicts with Manny."));

            // ── Vic Laundering ──
            _laundering = MelonPreferences.CreateCategory("OverTheCounter_Laundering", "Vic Laundering");

            VicTier1Cost = Register(_laundering.CreateEntry("VicTier1Cost", 500f, "Tier 1 Cost",
                "Cash required for tier-1 laundering"));
            VicTier1Return = Register(_laundering.CreateEntry("VicTier1Return", 400f, "Tier 1 Return",
                "Clean money returned for tier-1 laundering"));
            VicTier2TrustUnlock = Register(_laundering.CreateEntry("VicTier2TrustUnlock", 7, "Tier 2 Trust Unlock",
                "Trust level required to unlock tier-2 laundering"));
            VicTier2Cost = Register(_laundering.CreateEntry("VicTier2Cost", 900f, "Tier 2 Cost",
                "Cash required for tier-2 laundering"));
            VicTier2Return = Register(_laundering.CreateEntry("VicTier2Return", 750f, "Tier 2 Return",
                "Clean money returned for tier-2 laundering"));
            VicIntroWeedGrams = Register(_laundering.CreateEntry("VicIntroWeedGrams", 40, "Intro Quest Weed Grams",
                "Grams of weed required to complete Vic's intro quest"));

            // ── Bella Protocol ──
            _bella = MelonPreferences.CreateCategory("OverTheCounter_Bella", "Bella Protocol");

            BellaWeedValue = Register(_bella.CreateEntry("BellaWeedValue", 105f, "Weed Mix Value",
                "Minimum base price for the weed mix Bella requires"));
            BellaMethValue = Register(_bella.CreateEntry("BellaMethValue", 200f, "Meth Mix Value",
                "Minimum base price for the meth mix Bella requires"));
            BellaCokeValue = Register(_bella.CreateEntry("BellaCokeValue", 400f, "Cocaine Mix Value",
                "Minimum base price for the cocaine mix Bella requires"));

            // ── Contract Notifications ──
            _notifications = MelonPreferences.CreateCategory("OverTheCounter_Notifications", "Contract Notifications");

            ConsolidationEnabled = Register(_notifications.CreateEntry("Enabled", true, "Enabled",
                "Enable/disable contract consolidation (groups same-window deliveries into one HUD entry)"));
            ConsolidationThreshold = Register(_notifications.CreateEntry("ConsolidationThreshold", 5, "Consolidation Threshold",
                "Minimum contracts in a window before consolidation kicks in"));

            // ── Static Subscription ──
            _subscription = MelonPreferences.CreateCategory("OverTheCounter_Subscription", "Static Subscription");

            SaasWeeklyCost = Register(_subscription.CreateEntry("SaasWeeklyCost", 1000f, "Weekly Cost",
                "Bank balance deducted each billing cycle"));
            SaasCycleDays = Register(_subscription.CreateEntry("SaasCycleDays", 7, "Cycle Days",
                "Number of days between subscription payments"));
            AtmDepositTrigger = Register(_subscription.CreateEntry("AtmDepositTrigger", 5000f, "ATM Deposit Trigger",
                "Weekly ATM deposit sum that triggers Static's intro quest"));
            StaticTier1BankCost = Register(_subscription.CreateEntry("StaticTier1BankCost", 1500f, "Tier 1 Bank Cost",
                "Bank transfer cost for the initial software package"));
            StaticTier1WeedGrams = Register(_subscription.CreateEntry("StaticTier1WeedGrams", 12, "Tier 1 Weed Grams",
                "Grams of weed required for the initial package"));
            StaticTier2BankCost = Register(_subscription.CreateEntry("StaticTier2BankCost", 6000f, "Tier 2 Bank Cost",
                "Bank transfer cost for the Premium upgrade"));
            StaticTier2MethGrams = Register(_subscription.CreateEntry("StaticTier2MethGrams", 5, "Tier 2 Meth Grams",
                "Grams of meth required for the Premium upgrade"));
            StaticTier3BankCost = Register(_subscription.CreateEntry("StaticTier3BankCost", 12000f, "Tier 3 Bank Cost",
                "Bank transfer cost for the Enterprise upgrade"));
            StaticTier3PremiumMethGrams = Register(_subscription.CreateEntry("StaticTier3PremiumMethGrams", 10, "Tier 3 Premium Meth Grams",
                "Grams of premium meth required for Enterprise upgrade"));

            // ── World ──
            _world = MelonPreferences.CreateCategory("OverTheCounter_World", "World");

            StackSizeMultiplier = Register(_world.CreateEntry("StackSizeMultiplier", 1,
                "Stack Size Multiplier",
                "Multiplies the stack limit of all stackable items by this value. " +
                "1 = vanilla stacks (default). 2 = double stacks. Affects all inventories. " +
                "Host value is used in multiplayer. Changing mid-save does not retroactively " +
                "adjust existing item quantities.",
                validator: new ValueRange<int>(1, 10)));

            GraffitiReEdit = Register(_world.CreateEntry("GraffitiReEdit", true,
                "Graffiti Re-Edit",
                "Allow re-editing spray paint surfaces without consuming spray cans. " +
                "Spray can must still be equipped to interact."));

            RecipePinEnabled = Register(_world.CreateEntry("RecipePinEnabled", true,
                "Recipe Pin",
                "Show a Pin Recipe button in the Product Manager app. " +
                "Pins a draggable overlay showing the full mixing chain for a product."));

            // ── Desperation System ──
            _desperation = MelonPreferences.CreateCategory("OverTheCounter", "Desperation System");

            DesperationEnabled = Register(_desperation.CreateEntry("Enabled", true, "Enabled",
                "Enable/disable the desperation system (urgent fiend requests)"));
            FiendAddictionThreshold = Register(_desperation.CreateEntry("FiendAddictionThreshold", 0.67f, "Fiend Addiction Threshold",
                "Addiction level required to qualify as a Fiend (0.0–1.0)"));
            TriggerChancePerHour = Register(_desperation.CreateEntry("TriggerChancePerHour", 0.12f, "Trigger Chance Per Hour",
                "Probability of a desperation event each hour (0.0–1.0)"));
            MaxEventsPerDay = Register(_desperation.CreateEntry("MaxEventsPerDay", 3, "Max Events Per Day",
                "Hard cap on desperation events per day"));
            ResponseDeadlineMinutes = Register(_desperation.CreateEntry("ResponseDeadlineMinutes", 60, "Response Deadline (min)",
                "In-game minutes the player has to respond to a desperation offer"));
            DeadlineMinutes = Register(_desperation.CreateEntry("DeadlineMinutes", 120, "Delivery Deadline (min)",
                "In-game minutes to deliver after accepting a desperation contract"));
            BonusMultiplier = Register(_desperation.CreateEntry("BonusMultiplier", 0.45f, "Bonus Multiplier",
                "Extra payment multiplier for desperation deliveries (0.45 = 45%)"));
            RelationshipPenalty = Register(_desperation.CreateEntry("RelationshipPenalty", -15f, "Relationship Penalty",
                "Relationship change on failed desperation event"));
            CooldownMinutes = Register(_desperation.CreateEntry("CooldownMinutes", 1440, "Cooldown (min)",
                "Minutes a customer is locked out after a failed event (1440 = 24h)"));
            DayStartHour = Register(_desperation.CreateEntry("DayStartHour", 800, "Day Start Hour",
                "Earliest 24h time for desperation rolls (800 = 8:00 AM)"));
            DayEndHour = Register(_desperation.CreateEntry("DayEndHour", 2100, "Day End Hour",
                "Latest 24h time for desperation rolls (2100 = 9:00 PM)"));

            // ── Drifter System ──
            _drifters = MelonPreferences.CreateCategory("OverTheCounter_Drifters", "Drifter System");

            DrifterEnabled = Register(_drifters.CreateEntry("Enabled", true, "Enabled",
                "Enable/disable the drifter system (random street NPCs offering one-time deals)"));
            DrifterSpawnChancePerHour = Register(_drifters.CreateEntry("DrifterSpawnChancePerHour", 0.38f, "Spawn Chance Per Hour",
                "Base spawn chance per hour at max regions. Scaled down by unlocked region count (0.0-1.0)"));
            MaxActiveDrifters = Register(_drifters.CreateEntry("MaxActiveDrifters", 3, "Max Active Drifters",
                "Maximum number of drifters that can be active at once"));
            DrifterDayStartHour = Register(_drifters.CreateEntry("DrifterDayStartHour", 800, "Day Start Hour",
                "Earliest 24h time for drifter spawns (800 = 8:00 AM)"));
            DrifterDayEndHour = Register(_drifters.CreateEntry("DrifterDayEndHour", 2100, "Day End Hour",
                "Latest 24h time for drifter spawns (2100 = 9:00 PM)"));
            DrifterOfferWindowMin = Register(_drifters.CreateEntry("DrifterOfferWindowMin", 120, "Offer Window (min)",
                "Minutes player has to respond to a drifter offer (120 = 2 hours)"));
            DrifterDeliveryDeadlineMin = Register(_drifters.CreateEntry("DrifterDeliveryDeadlineMin", 240, "Delivery Deadline (min)",
                "Minutes to deliver after accepting a drifter deal (240 = 4 hours)"));
            DrifterLingerMinMin = Register(_drifters.CreateEntry("DrifterLingerMinMin", 30, "Linger Min (min)",
                "Minimum minutes a drifter lingers after deal completion/expiry"));
            DrifterLingerMaxMin = Register(_drifters.CreateEntry("DrifterLingerMaxMin", 60, "Linger Max (min)",
                "Maximum minutes a drifter lingers after deal completion/expiry"));
            DrifterMinDealValue = Register(_drifters.CreateEntry("DrifterMinDealValue", 90f, "Min Deal Value ($)",
                "Soft minimum deal value - drifters ask for more quantity until the deal reaches this threshold"));

            // ── Debug ──
            _debug = MelonPreferences.CreateCategory("OverTheCounter_Debug", "Debug");

            VerboseLogging = Register(_debug.CreateEntry("VerboseLogging", false, "Verbose Logging (General)",
                "Catch-all verbose logging for systems without a dedicated toggle"));
            ManagerVerboseLogging = Register(_debug.CreateEntry("ManagerVerboseLogging", false, "Verbose: Manager",
                "Enable detailed manager logging (shopping list breakdowns, per-item details)"));
            DrifterVerboseLogging = Register(_debug.CreateEntry("DrifterVerboseLogging", false, "Verbose: Drifter",
                "Enable detailed drifter system logging"));
            DesperationVerboseLogging = Register(_debug.CreateEntry("DesperationVerboseLogging", false, "Verbose: Desperation",
                "Enable detailed desperation system logging"));
            NpcVerboseLogging = Register(_debug.CreateEntry("NpcVerboseLogging", false, "Verbose: NPC",
                "Enable detailed NPC logging (Vic, Bella, Static)"));
            NetworkVerboseLogging = Register(_debug.CreateEntry("NetworkVerboseLogging", false, "Verbose: Network",
                "Enable detailed network sync logging"));
            QuestVerboseLogging = Register(_debug.CreateEntry("QuestVerboseLogging", false, "Verbose: Quest",
                "Enable detailed quest logging"));
            NotificationVerboseLogging = Register(_debug.CreateEntry("NotificationVerboseLogging", false, "Verbose: Notification",
                "Enable detailed notification system logging"));
            PatchVerboseLogging = Register(_debug.CreateEntry("PatchVerboseLogging", false, "Verbose: Patch",
                "Enable detailed Harmony patch logging"));

            // ── Minimap POIs ──
            _minimapPoi = MelonPreferences.CreateCategory("OverTheCounter_MinimapPOI", "Minimap POIs");

            MinimapShowPotentialCustomers = Register(_minimapPoi.CreateEntry("MinimapShowPotentialCustomers", true,
                "Show Potential Customers", "Show potential customer icons on the minimap"));
            MinimapShowCustomers = Register(_minimapPoi.CreateEntry("MinimapShowCustomers", false,
                "Show Customers", "Show unlocked customer icons on the minimap"));
            MinimapShowDealers = Register(_minimapPoi.CreateEntry("MinimapShowDealers", true,
                "Show Dealers", "Show dealer icons on the minimap (potential and active)"));
            MinimapShowDeadDrops = Register(_minimapPoi.CreateEntry("MinimapShowDeadDrops", true,
                "Show Dead Drops", "Show dead drop icons on the minimap"));
            MinimapShowContracts = Register(_minimapPoi.CreateEntry("MinimapShowContracts", true,
                "Show Contracts", "Show contract delivery icons on the minimap"));
            MinimapShowQuests = Register(_minimapPoi.CreateEntry("MinimapShowQuests", true,
                "Show Quests", "Show quest objective icons on the minimap"));
            MinimapShowProperties = Register(_minimapPoi.CreateEntry("MinimapShowProperties", true,
                "Show Properties", "Show owned property icons on the minimap"));
            MinimapShowManagers = Register(_minimapPoi.CreateEntry("MinimapShowManagers", true,
                "Show Managers", "Show manager icons on the minimap"));

            // ── Minimap ──
            _minimap = MelonPreferences.CreateCategory("OverTheCounter_Minimap", "Minimap");

            MinimapEnabled = Register(_minimap.CreateEntry("MinimapEnabled", false, "Enabled",
                "Show the minimap overlay"));
            MinimapSize = Register(_minimap.CreateEntry("MinimapSize", 250, "Size (px)",
                "Minimap size in pixels"));
            MinimapRotateWithPlayer = Register(_minimap.CreateEntry("MinimapRotateWithPlayer", false,
                "Rotate With Player", "Rotate minimap to match player facing direction"));
            MinimapCircle = Register(_minimap.CreateEntry("MinimapCircle", false, "Circle Shape",
                "Use circular minimap shape instead of square"));
            MinimapDefaultZoom = Register(_minimap.CreateEntry("MinimapDefaultZoom", 2,
                "Default Zoom", "Starting zoom level, 1=closest, 3=farthest (1-3)"));
            MinimapIconScale = Register(_minimap.CreateEntry("MinimapIconScale", 0.7f, "Icon Scale",
                "POI icon scale on the minimap"));
            MinimapToggleKey = _minimap.CreateEntry("MinimapToggleKey", KeyCode.N, "Toggle Key",
                "Hotkey to cycle minimap zoom");
            MinimapHorizontalOffset = Register(_minimap.CreateEntry("MinimapHorizontalOffset", 100,
                "Horizontal Offset", "Horizontal position (0=left, 100=right)",
                validator: new ValueRange<int>(0, 100)));
            MinimapVerticalOffset = Register(_minimap.CreateEntry("MinimapVerticalOffset", 0,
                "Vertical Offset", "Vertical position (0=top, 100=bottom)",
                validator: new ValueRange<int>(0, 100)));
            MinimapInfoOnTop = Register(_minimap.CreateEntry("MinimapInfoOnTop", false,
                "Info Panels On Top", "Place time/day and rank bar above the minimap instead of below"));
            MinimapBorderColor = _minimap.CreateEntry("MinimapBorderColor",
                new Color(0.2f, 0.2f, 0.2f, 0.9f), "Border Color",
                "Minimap border color");
            MinimapBorderWidth = Register(_minimap.CreateEntry("MinimapBorderWidth", 4,
                "Border Width", "Border thickness in pixels per side (2-10)"));
            MinimapShowTime = Register(_minimap.CreateEntry("MinimapShowTime", true,
                "Show Time", "Display the current time near the minimap"));
            MinimapShowDay = Register(_minimap.CreateEntry("MinimapShowDay", true,
                "Show Day", "Display the current day near the minimap"));
            MinimapUse24HourClock = Register(_minimap.CreateEntry("MinimapUse24HourClock", false,
                "24-Hour Clock", "Use 24-hour time format instead of 12-hour AM/PM"));
            MinimapShowRank = Register(_minimap.CreateEntry("MinimapShowRank", false,
                "Show Rank/XP", "Display rank name and XP progress bar near the minimap"));
            MinimapShowCompass = Register(_minimap.CreateEntry("MinimapShowCompass", true,
                "Show Compass", "Display N/S/E/W cardinal direction labels on the minimap edge"));
            MinimapShowEdgeIndicators = Register(_minimap.CreateEntry("MinimapShowEdgeIndicators", true,
                "Edge Indicators", "Show POI icons pinned to the minimap edge for off-screen points of interest"));
        }

        private static ConfigEntry<float> Register(MelonPreferences_Entry<float> entry)
        {
            var wrapped = new ConfigEntry<float>(entry);
            _floatEntries[entry.Identifier] = wrapped;
            return wrapped;
        }

        private static ConfigEntry<int> Register(MelonPreferences_Entry<int> entry)
        {
            var wrapped = new ConfigEntry<int>(entry);
            _intEntries[entry.Identifier] = wrapped;
            return wrapped;
        }

        private static ConfigEntry<bool> Register(MelonPreferences_Entry<bool> entry)
        {
            var wrapped = new ConfigEntry<bool>(entry);
            _boolEntries[entry.Identifier] = wrapped;
            return wrapped;
        }

        /// <summary>
        /// Serializes all config values as "key=value|key=value|..." for network transport.
        /// Float values use InvariantCulture to avoid locale issues.
        /// </summary>
        public static string SerializeAll()
        {
            // Always serialize the raw MelonPreference values, not overrides.
            // The host is the authority — overrides are for clients only.
            var parts = new List<string>();

            foreach (var kvp in _floatEntries)
            {
                if (_localOnlyKeys.Contains(kvp.Key)) continue;
                parts.Add($"{kvp.Key}={kvp.Value.RawEntry.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            foreach (var kvp in _intEntries)
            {
                if (_localOnlyKeys.Contains(kvp.Key)) continue;
                parts.Add($"{kvp.Key}={kvp.Value.RawEntry.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            foreach (var kvp in _boolEntries)
            {
                if (_localOnlyKeys.Contains(kvp.Key)) continue;
                parts.Add($"{kvp.Key}={kvp.Value.RawEntry.Value}");
            }

            return string.Join("|", parts);
        }

        /// <summary>
        /// Applies host config overrides from a deserialized key-value dictionary.
        /// Unknown keys are silently ignored.
        /// </summary>
        public static void ApplyOverrides(Dictionary<string, string> data)
        {
            foreach (var kvp in data)
            {
                if (_localOnlyKeys.Contains(kvp.Key)) continue;

                if (_floatEntries.TryGetValue(kvp.Key, out var floatEntry))
                {
                    if (float.TryParse(kvp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fVal))
                        floatEntry.SetOverride(fVal);
                }
                else if (_intEntries.TryGetValue(kvp.Key, out var intEntry))
                {
                    if (int.TryParse(kvp.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iVal))
                        intEntry.SetOverride(iVal);
                }
                else if (_boolEntries.TryGetValue(kvp.Key, out var boolEntry))
                {
                    if (bool.TryParse(kvp.Value, out bool bVal))
                        boolEntry.SetOverride(bVal);
                }
            }
        }

        /// <summary>
        /// Clears all session overrides (e.g. on disconnect). Values revert to local MelonPreferences.
        /// </summary>
        public static void ClearAllOverrides()
        {
            foreach (var entry in _floatEntries.Values)
                entry.ClearOverride();

            foreach (var entry in _intEntries.Values)
                entry.ClearOverride();

            foreach (var entry in _boolEntries.Values)
                entry.ClearOverride();
        }

        /// <summary>
        /// Subscribes to value-changed events on all syncable (non-local-only) config entries.
        /// When the host changes any setting (via any mod manager UI or file edit), the new
        /// config is pushed to clients automatically.
        /// </summary>
        public static void SubscribeToChanges()
        {
            foreach (var kvp in _floatEntries)
            {
                if (_localOnlyKeys.Contains(kvp.Key)) continue;
                kvp.Value.RawEntry.OnEntryValueChangedUntyped.Subscribe(OnSyncableEntryChanged);
            }
            foreach (var kvp in _intEntries)
            {
                if (_localOnlyKeys.Contains(kvp.Key)) continue;
                kvp.Value.RawEntry.OnEntryValueChangedUntyped.Subscribe(OnSyncableEntryChanged);
            }
            foreach (var kvp in _boolEntries)
            {
                if (_localOnlyKeys.Contains(kvp.Key)) continue;
                kvp.Value.RawEntry.OnEntryValueChangedUntyped.Subscribe(OnSyncableEntryChanged);
            }
        }

        private static void OnSyncableEntryChanged(object oldValue, object newValue)
        {
            if (!NetworkHelper.IsHost) return;
            ConfigSyncData.Instance?.RefreshFromConfig();
        }
    }
}
