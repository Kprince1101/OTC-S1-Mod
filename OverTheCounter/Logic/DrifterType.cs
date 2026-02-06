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
        /// Robber - appears as Normal, attacks player after handover instead of paying. 3% spawn weight.
        /// </summary>
        Robber,

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
        public const int NormalWeight = 77;   // 0-76 = Normal (77%)
        public const int WhaleWeight = 87;    // 77-86 = Whale (10%)
        public const int FiendWeight = 92;    // 87-91 = Fiend (5%)
        public const int RobberWeight = 95;   // 92-94 = Robber (3%)
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
            if (roll < RobberWeight)
                return DrifterType.Robber;

            return DrifterType.Narc;
        }

    }
}
