namespace OverTheCounter.Logic
{
    /// <summary>
    /// Types of drifter NPCs with different behaviors and spawn weights.
    /// </summary>
    public enum DrifterType
    {
        /// <summary>
        /// Standard one-time deal with random product. 80% spawn weight.
        /// </summary>
        Normal,

        /// <summary>
        /// Bulk buyer - 2-3x quantities, 1.3x price multiplier. 10% spawn weight.
        /// </summary>
        Whale,

        /// <summary>
        /// Urgent buyer - meth/coke preference, 1.5x price, desperate messaging. 5% spawn weight.
        /// </summary>
        Fiend,

        /// <summary>
        /// Police sting - triggers arrest on handover completion. 5% spawn weight.
        /// </summary>
        Narc
    }

    /// <summary>
    /// Spawn weight configuration for drifter types.
    /// </summary>
    public static class DrifterTypeWeights
    {
        // Cumulative weights for weighted random selection (out of 100)
        public const int NormalWeight = 80;   // 0-79 = Normal (80%)
        public const int WhaleWeight = 90;    // 80-89 = Whale (10%)
        public const int FiendWeight = 95;    // 90-94 = Fiend (5%)
        // 95-99 = Narc (5%)

        /// <summary>
        /// Selects a random drifter type based on spawn weights.
        /// </summary>
        public static DrifterType GetRandomType()
        {
            int roll = UnityEngine.Random.Range(0, 100);

            if (roll < NormalWeight)
                return DrifterType.Normal;
            if (roll < WhaleWeight)
                return DrifterType.Whale;
            if (roll < FiendWeight)
                return DrifterType.Fiend;

            return DrifterType.Narc;
        }

        /// <summary>
        /// Gets the price multiplier for a drifter type.
        /// </summary>
        public static float GetPriceMultiplier(DrifterType type)
        {
            return type switch
            {
                DrifterType.Whale => 1.3f,
                DrifterType.Fiend => 1.5f,
                _ => 1.0f
            };
        }

        /// <summary>
        /// Gets the quantity multiplier range for a drifter type.
        /// Returns (min, max) multipliers.
        /// </summary>
        public static (float min, float max) GetQuantityMultiplierRange(DrifterType type)
        {
            return type switch
            {
                DrifterType.Whale => (2.0f, 3.0f),
                _ => (1.0f, 1.0f)
            };
        }
    }
}
