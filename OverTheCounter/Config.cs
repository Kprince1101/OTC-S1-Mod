using MelonLoader;

namespace OverTheCounter
{
    public static class Config
    {
        // ── Desperation System ──
        private static MelonPreferences_Category _desperation;

        public static MelonPreferences_Entry<float> FiendAddictionThreshold;
        public static MelonPreferences_Entry<float> TriggerChancePerHour;
        public static MelonPreferences_Entry<int> MaxEventsPerDay;
        public static MelonPreferences_Entry<int> ResponseDeadlineMinutes;
        public static MelonPreferences_Entry<int> DeadlineMinutes;
        public static MelonPreferences_Entry<float> BonusMultiplier;
        public static MelonPreferences_Entry<float> RelationshipPenalty;
        public static MelonPreferences_Entry<int> CooldownMinutes;
        public static MelonPreferences_Entry<int> DayStartHour;
        public static MelonPreferences_Entry<int> DayEndHour;

        // ── Vic Laundering ──
        private static MelonPreferences_Category _laundering;

        public static MelonPreferences_Entry<float> VicTier1Cost;
        public static MelonPreferences_Entry<float> VicTier1Return;
        public static MelonPreferences_Entry<int> VicTier2TrustUnlock;
        public static MelonPreferences_Entry<float> VicTier2Cost;
        public static MelonPreferences_Entry<float> VicTier2Return;

        // ── Static Subscription ──
        private static MelonPreferences_Category _subscription;

        public static MelonPreferences_Entry<float> SaasWeeklyCost;
        public static MelonPreferences_Entry<int> SaasCycleDays;
        public static MelonPreferences_Entry<float> AtmDepositTrigger;
        public static MelonPreferences_Entry<float> StaticTier1BankCost;
        public static MelonPreferences_Entry<int> StaticTier1WeedGrams;
        public static MelonPreferences_Entry<float> StaticTier2BankCost;
        public static MelonPreferences_Entry<int> StaticTier2MethGrams;
        public static MelonPreferences_Entry<float> StaticTier3BankCost;
        public static MelonPreferences_Entry<int> StaticTier3PremiumMethGrams;

        // ── Contract Notifications ──
        private static MelonPreferences_Category _notifications;

        public static MelonPreferences_Entry<int> ConsolidationThreshold;

        public static void Initialize()
        {
            // ── Desperation System ──
            _desperation = MelonPreferences.CreateCategory("OverTheCounter", "Desperation System");

            FiendAddictionThreshold = _desperation.CreateEntry("FiendAddictionThreshold", 0.67f, "Fiend Addiction Threshold",
                "Addiction level required to qualify as a Fiend (0.0–1.0)");
            TriggerChancePerHour = _desperation.CreateEntry("TriggerChancePerHour", 0.12f, "Trigger Chance Per Hour",
                "Probability of a desperation event each hour (0.0–1.0)");
            MaxEventsPerDay = _desperation.CreateEntry("MaxEventsPerDay", 3, "Max Events Per Day",
                "Hard cap on desperation events per day");
            ResponseDeadlineMinutes = _desperation.CreateEntry("ResponseDeadlineMinutes", 60, "Response Deadline (min)",
                "In-game minutes the player has to respond to a desperation offer");
            DeadlineMinutes = _desperation.CreateEntry("DeadlineMinutes", 120, "Delivery Deadline (min)",
                "In-game minutes to deliver after accepting a desperation contract");
            BonusMultiplier = _desperation.CreateEntry("BonusMultiplier", 0.45f, "Bonus Multiplier",
                "Extra payment multiplier for desperation deliveries (0.45 = 45%)");
            RelationshipPenalty = _desperation.CreateEntry("RelationshipPenalty", -15f, "Relationship Penalty",
                "Relationship change on failed desperation event");
            CooldownMinutes = _desperation.CreateEntry("CooldownMinutes", 1440, "Cooldown (min)",
                "Minutes a customer is locked out after a failed event (1440 = 24h)");
            DayStartHour = _desperation.CreateEntry("DayStartHour", 800, "Day Start Hour",
                "Earliest 24h time for desperation rolls (800 = 8:00 AM)");
            DayEndHour = _desperation.CreateEntry("DayEndHour", 2100, "Day End Hour",
                "Latest 24h time for desperation rolls (2100 = 9:00 PM)");

            // ── Vic Laundering ──
            _laundering = MelonPreferences.CreateCategory("OverTheCounter_Laundering", "Vic Laundering");

            VicTier1Cost = _laundering.CreateEntry("VicTier1Cost", 500f, "Tier 1 Cost",
                "Cash required for tier-1 laundering");
            VicTier1Return = _laundering.CreateEntry("VicTier1Return", 400f, "Tier 1 Return",
                "Clean money returned for tier-1 laundering");
            VicTier2TrustUnlock = _laundering.CreateEntry("VicTier2TrustUnlock", 7, "Tier 2 Trust Unlock",
                "Trust level required to unlock tier-2 laundering");
            VicTier2Cost = _laundering.CreateEntry("VicTier2Cost", 900f, "Tier 2 Cost",
                "Cash required for tier-2 laundering");
            VicTier2Return = _laundering.CreateEntry("VicTier2Return", 750f, "Tier 2 Return",
                "Clean money returned for tier-2 laundering");

            // ── Static Subscription ──
            _subscription = MelonPreferences.CreateCategory("OverTheCounter_Subscription", "Static Subscription");

            SaasWeeklyCost = _subscription.CreateEntry("SaasWeeklyCost", 1000f, "Weekly Cost",
                "Bank balance deducted each billing cycle");
            SaasCycleDays = _subscription.CreateEntry("SaasCycleDays", 7, "Cycle Days",
                "Number of days between subscription payments");
            AtmDepositTrigger = _subscription.CreateEntry("AtmDepositTrigger", 5000f, "ATM Deposit Trigger",
                "Weekly ATM deposit sum that triggers Static's intro quest");
            StaticTier1BankCost = _subscription.CreateEntry("StaticTier1BankCost", 3000f, "Tier 1 Bank Cost",
                "Bank transfer cost for the initial software package");
            StaticTier1WeedGrams = _subscription.CreateEntry("StaticTier1WeedGrams", 20, "Tier 1 Weed Grams",
                "Grams of weed required for the initial package");
            StaticTier2BankCost = _subscription.CreateEntry("StaticTier2BankCost", 6000f, "Tier 2 Bank Cost",
                "Bank transfer cost for the Premium upgrade");
            StaticTier2MethGrams = _subscription.CreateEntry("StaticTier2MethGrams", 5, "Tier 2 Meth Grams",
                "Grams of meth required for the Premium upgrade");
            StaticTier3BankCost = _subscription.CreateEntry("StaticTier3BankCost", 12000f, "Tier 3 Bank Cost",
                "Bank transfer cost for the Enterprise upgrade");
            StaticTier3PremiumMethGrams = _subscription.CreateEntry("StaticTier3PremiumMethGrams", 10, "Tier 3 Premium Meth Grams",
                "Grams of premium meth required for Enterprise upgrade");

            // ── Contract Notifications ──
            _notifications = MelonPreferences.CreateCategory("OverTheCounter_Notifications", "Contract Notifications");

            ConsolidationThreshold = _notifications.CreateEntry("ConsolidationThreshold", 5, "Consolidation Threshold",
                "Minimum contracts in a window before consolidation kicks in");
        }
    }
}
