namespace OverTheCounter.Logic
{
    /// <summary>
    /// Static upgrade tables and wage calculation for manager speed/inventory tiers.
    /// Wage formula: BaseWage + (ExtraSlots × SlotDailyFee) + SpeedTierFee
    /// Hard cap: fully maxed = $950/day (under $1,000 limit).
    /// </summary>
    internal static class ManagerUpgrades
    {
        public const int BaseSlots = 5;
        public const int MaxSpeedTier = 4;
        public const int MaxExtraSlots = 5;
        public const float SlotDailyFee = 30f;

        // Speed tiers: (displayMultiplier, speedControlValue, oneTimeBuyIn, dailyFeeIncrease)
        // SpeedControl value = defaultWalkSpeed(0.08) × multiplier
        private static readonly (float mult, float speed, float buyIn, float dailyFee)[] SpeedTiers =
        {
            (1.5f, 0.120f, 0f,     0f),
            (1.75f, 0.140f, 5000f,  50f),
            (2.0f, 0.160f, 12000f, 100f),
            (2.25f, 0.180f, 25000f, 150f),
            (2.5f, 0.200f, 45000f, 450f),
        };

        // Per-slot buy-in costs for slots 6–10 (index 0 = slot 6)
        private static readonly float[] SlotBuyIn = { 1500f, 4000f, 8000f, 14000f, 20000f };

        public static float CalculateDailyWage(int speedTier, int extraSlots)
        {
            speedTier = Clamp(speedTier, 0, MaxSpeedTier);
            extraSlots = Clamp(extraSlots, 0, MaxExtraSlots);
            return Config.ManagerDailyWage.Value + (extraSlots * SlotDailyFee) + SpeedTiers[speedTier].dailyFee;
        }

        public static float GetSpeedControlValue(int speedTier)
            => SpeedTiers[Clamp(speedTier, 0, MaxSpeedTier)].speed;

        public static int GetTotalSlots(int extraSlots)
            => BaseSlots + Clamp(extraSlots, 0, MaxExtraSlots);

        public static string GetSpeedLabel(int speedTier)
        {
            float m = SpeedTiers[Clamp(speedTier, 0, MaxSpeedTier)].mult;
            return m == (int)m ? $"{m:F1}x" : $"{m}x";
        }

        public static float GetNextSpeedBuyIn(int currentTier)
            => currentTier < MaxSpeedTier ? SpeedTiers[currentTier + 1].buyIn : -1f;

        public static float GetNextSpeedDailyFee(int currentTier)
            => currentTier < MaxSpeedTier ? SpeedTiers[currentTier + 1].dailyFee : 0f;

        public static float GetNextSlotBuyIn(int currentExtra)
            => currentExtra < MaxExtraSlots ? SlotBuyIn[currentExtra] : -1f;

        public static bool IsSpeedMaxed(int speedTier) => speedTier >= MaxSpeedTier;
        public static bool IsInventoryMaxed(int extraSlots) => extraSlots >= MaxExtraSlots;

        private static int Clamp(int value, int min, int max)
            => value < min ? min : value > max ? max : value;
    }
}
