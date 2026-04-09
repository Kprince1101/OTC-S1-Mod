namespace OverTheCounter.Logic
{
    /// <summary>
    /// Lifecycle states for a store customer NPC.
    /// </summary>
    public enum CustomerState
    {
        /// <summary>Spawned, walking toward the store entrance (stair base).</summary>
        WalkingToStore = 0,

        /// <summary>At stair base, walking through NavMeshLink into the building interior.</summary>
        EnteringStore = 1,

        /// <summary>No storage found — walking to room center to look around before retrying.</summary>
        LookingAround = 2,

        /// <summary>Walking between and pausing at storage entities inside the store.</summary>
        Browsing = 3,

        /// <summary>Walking to checkout counter and paying for browsed items.</summary>
        CheckingOut = 4,

        /// <summary>Walking to door interior before descending stairs to leave.</summary>
        ExitingStore = 5,

        /// <summary>Descended stairs, walking back to the despawn point.</summary>
        LeavingStore = 6,

        /// <summary>Ready for removal from the scene.</summary>
        Despawning = 7
    }
}
