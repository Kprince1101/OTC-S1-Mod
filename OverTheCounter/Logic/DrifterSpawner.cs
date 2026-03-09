using MelonLoader;
using MelonLoader.Utils;
using OverTheCounter.Utilities;
using S1API.Utils;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;

#if IL2CPP
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Messaging;
#else
using ScheduleOne.NPCs;
using ScheduleOne.Economy;
using ScheduleOne.Messaging;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Drifter-specific NPC setup. Delegates core spawning to <see cref="NpcSpawner"/>
    /// and adds messaging, voice, and icon initialization on top.
    /// </summary>
    public static class DrifterSpawner
    {
        internal static readonly Dictionary<string, MSGConversation> DrifterConversations = new();

        // =====================================================================
        //  Spawn / Despawn (delegate to NpcSpawner)
        // =====================================================================

        /// <summary>
        /// Spawns a new drifter NPC at the specified position.
        /// </summary>
        /// <param name="id">Unique identifier for this drifter.</param>
        /// <param name="firstName">First name for the NPC.</param>
        /// <param name="lastName">Last name for the NPC.</param>
        /// <param name="position">World position to spawn at.</param>
        /// <param name="rotation">Facing rotation.</param>
        /// <returns>The spawned NPC, or null on failure.</returns>
        public static NPC Spawn(string id, string firstName, string lastName, Vector3 position, Quaternion rotation) =>
            NpcSpawner.SpawnCivilianNpc(id, $"Drifter_{id}", firstName, lastName, position, rotation);

        /// <summary>
        /// Despawns and destroys a drifter NPC.
        /// </summary>
        public static void Despawn(NPC npc) =>
            NpcSpawner.Despawn(npc);

        /// <summary>
        /// Resets the prefab cache. Call on scene transitions.
        /// </summary>
        public static void ResetCache() =>
            NpcSpawner.ResetCache();

        // =====================================================================
        //  Appearance (delegate to NpcSpawner)
        // =====================================================================

        /// <summary>
        /// Determines gender from a seed (first RNG draw).
        /// </summary>
        public static float DetermineGender(int seed) =>
            NpcSpawner.DetermineGender(seed);

        /// <summary>
        /// Generates random appearance for an NPC using IL2CPP AvatarSettings.
        /// </summary>
        public static void GenerateRandomAppearance(NPC npc, int seed) =>
            NpcSpawner.GenerateRandomAppearance(npc, seed);

        // =====================================================================
        //  Customer component (delegate to NpcSpawner)
        // =====================================================================

        /// <summary>
        /// Adds a Customer component to an inactive NPC with proper CustomerData setup.
        /// </summary>
        public static Customer AddCustomerComponent(NPC npc) =>
            NpcSpawner.AddCustomerComponent(npc);

        /// <summary>
        /// Adds a Customer component to an already-active NPC.
        /// </summary>
        public static Customer AddCustomerComponentToActive(NPC npc) =>
            NpcSpawner.AddCustomerComponentToActive(npc);

        /// <summary>
        /// Isolates a drifter's Customer component from the vanilla deal system.
        /// </summary>
        public static void IsolateDrifterCustomer(NPC npc) =>
            NpcSpawner.IsolateCustomerComponent(npc);

        /// <summary>
        /// Gets the Customer component from an NPC.
        /// </summary>
        public static Customer GetCustomerComponent(NPC npc) =>
            NpcSpawner.GetCustomerComponent(npc);

        // =====================================================================
        //  Drifter-specific: messaging, voice, icon
        // =====================================================================

        /// <summary>
        /// Initializes the NPC's messaging system so it can send text messages.
        /// </summary>
        public static void InitializeMessaging(NPC npc)
        {
            if (npc == null) return;

            try
            {
                if (npc.GetMSGConversation() != null)
                {
                    OTCLog.Msg(OTCLog.Systems.Drifter, $"Messaging already initialized for {npc.ID}");
                    return;
                }

                var conversation = new MSGConversation(npc, npc.fullName);
                npc.SetMSGConversation(conversation);
                conversation.SetIsKnown(true);
                DrifterConversations[npc.ID] = conversation;

                OTCLog.Msg(OTCLog.Systems.Drifter, $"Initialized messaging for {npc.ID}");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"InitializeMessaging failed for {npc.ID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Borrows a voice database from an existing NPC.
        /// </summary>
        public static void EnsureVoiceDatabase(NPC npc)
        {
            if (npc?.VoiceOverEmitter == null) return;
            if (npc.VoiceOverEmitter.GetDatabase() != null) return;

            try
            {
                var allNpcs = UnityEngine.Object.FindObjectsOfType<NPC>();
                foreach (var other in allNpcs)
                {
                    if (other.GetInstanceID() == npc.GetInstanceID())
                        continue;

                    if (other.VoiceOverEmitter?.GetDatabase() != null)
                    {
                        npc.VoiceOverEmitter.SetDatabase(other.VoiceOverEmitter.GetDatabase(), false);
                        OTCLog.Msg(OTCLog.Systems.Drifter, $"Borrowed voice database for {npc.ID}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"EnsureVoiceDatabase failed: {ex.Message}");
            }
        }

        private static Sprite _drifterIcon;

        /// <summary>
        /// Sets the drifter profile icon on the NPC's MugshotSprite for messaging.
        /// </summary>
        public static void SetDrifterIcon(NPC npc)
        {
            if (npc == null) return;
            var icon = GetDrifterIcon();
            if (icon != null)
                npc.MugshotSprite = icon;
        }

        private static Sprite GetDrifterIcon()
        {
            if (_drifterIcon != null)
                return _drifterIcon;

            try
            {
                string iconPath = Path.Combine(MelonEnvironment.UserDataDirectory, "S1API", "Icons", "DrifterProfileIcon.png");
                _drifterIcon = ImageUtils.LoadImage(iconPath);
                if (_drifterIcon != null)
                {
                    OTCLog.Msg(OTCLog.Systems.Drifter, "Loaded ProfileIcon.png");
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.Drifter, "ProfileIcon.png not found or failed to load");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"Failed to load icon: {ex.Message}");
            }

            return _drifterIcon;
        }
    }
}
