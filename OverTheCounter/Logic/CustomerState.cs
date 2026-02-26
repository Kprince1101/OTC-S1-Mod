namespace OverTheCounter.Logic
{
    /// <summary>
    /// Lifecycle states for a store customer NPC.
    /// </summary>
    public enum CustomerState
    {
        /// <summary>Spawned, walking toward the store entrance (stair base).</summary>
        WalkingToStore,

        /// <summary>At stair base, walking through NavMeshLink into the building interior.</summary>
        EnteringStore,

        /// <summary>No storage found — walking to room center to look around before retrying.</summary>
        LookingAround,

        /// <summary>Walking between and pausing at storage entities inside the store.</summary>
        Browsing,

        /// <summary>Done browsing, walking back to the despawn point.</summary>
        LeavingStore,

        /// <summary>Ready for removal from the scene.</summary>
        Despawning
    }
}
