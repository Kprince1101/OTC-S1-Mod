using OverTheCounter.Utilities;
using UnityEngine;
using System;
using System.Collections.Generic;

#if IL2CPP
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
#else
using ScheduleOne.NPCs;
using ScheduleOne.AvatarFramework;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Spawns budtender NPCs using NpcSpawner infrastructure.
    /// Green uniform (tucked T-shirt + cap) with randomized biometrics from seed.
    /// </summary>
    public static class BudtenderSpawner
    {
        // Name pools — casual employee names, distinct from managers/drifters
        private static readonly string[] MaleFirstNames = {
            "Marcus", "Julian", "Tyler", "Devon", "Casey", "Riley", "Jordan",
            "Bryce", "Liam", "Noah", "Ethan", "Mason", "Logan", "Owen"
        };

        private static readonly string[] FemaleFirstNames = {
            "Sarah", "Elena", "Kaelen", "Maya", "Jade", "Zoe", "Luna",
            "Ivy", "Nova", "Aria", "Chloe", "Mia", "Lily", "Ruby"
        };

        private static readonly string[] LastNames = {
            "Thorne", "Vance", "Cross", "Chen", "Reyes", "Park", "Kim",
            "Santos", "Rivera", "Patel", "Nguyen", "Okafor", "Hall", "Brooks"
        };

        // Green uniform colors
        private static readonly Color ShirtGreen = new Color(0.18f, 0.52f, 0.22f);
        private static readonly Color CapGreen = new Color(0.15f, 0.45f, 0.18f);
        private static readonly Color DarkPants = new Color(0.12f, 0.12f, 0.14f);
        private static readonly Color DarkShoes = new Color(0.15f, 0.15f, 0.15f);

        // Cap-friendly hairstyles (short cuts that don't clip through the cap)
        private static readonly string[] CapFriendlyMaleHair = {
            "Avatar/Hair/buzzcut/BuzzCut",
            "Avatar/Hair/closebuzzcut/CloseBuzzCut",
            "Avatar/Hair/receding/Receding",
        };
        private static readonly string[] CapFriendlyFemaleHair = {
            "Avatar/Hair/bun/Bun",
            "Avatar/Hair/lowbun/LowBun",
            "Avatar/Hair/closebuzzcut/CloseBuzzCut",
        };

        /// <summary>
        /// Determines gender from seed. Returns 0-1 float where >= 0.5 is female.
        /// </summary>
        public static float DetermineGender(int seed)
        {
            var state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            float gender = UnityEngine.Random.Range(0f, 1f);
            UnityEngine.Random.state = state;
            return gender;
        }

        /// <summary>
        /// Derives deterministic first/last name from seed.
        /// </summary>
        public static (string firstName, string lastName) GetBudtenderName(int seed)
        {
            float gender = DetermineGender(seed);
            bool isFemale = gender >= 0.5f;

            var state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            // Burn the gender draw (must match DetermineGender's first draw)
            UnityEngine.Random.Range(0f, 1f);

            var firstPool = isFemale ? FemaleFirstNames : MaleFirstNames;

            // Check which names are already in use
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bt in BudtenderInstance.Active.Values)
            {
                if (bt.GameNpc != null && !string.IsNullOrEmpty(bt.GameNpc.FirstName))
                    usedNames.Add(bt.GameNpc.FirstName);
            }

            var firstName = firstPool[UnityEngine.Random.Range(0, firstPool.Length)];
            if (usedNames.Contains(firstName))
            {
                int startIdx = UnityEngine.Random.Range(0, firstPool.Length);
                for (int i = 0; i < firstPool.Length; i++)
                {
                    string candidate = firstPool[(startIdx + i) % firstPool.Length];
                    if (!usedNames.Contains(candidate))
                    {
                        firstName = candidate;
                        break;
                    }
                }
            }

            var lastName = LastNames[UnityEngine.Random.Range(0, LastNames.Length)];

            UnityEngine.Random.state = state;
            return (firstName, lastName);
        }

        /// <summary>
        /// Spawns a budtender NPC at the specified position using NpcSpawner.
        /// </summary>
        public static NPC Spawn(string id, int seed, Vector3 position, Quaternion rotation)
        {
            var (firstName, lastName) = GetBudtenderName(seed);

            var npc = NpcSpawner.SpawnCivilianNpc(
                id,
                $"Budtender_{id}",
                firstName,
                lastName,
                position,
                rotation);

            if (npc == null)
            {
                OTCLog.Error(OTCLog.Systems.Customer, $"BudtenderSpawner: failed to spawn {id}");
                return null;
            }

            ApplyAppearance(npc, seed);
            CustomerInstance.SetVoiceDatabase(npc, seed);
            OTCLog.Msg(OTCLog.Systems.Customer, $"BudtenderSpawner: spawned {id} ({firstName} {lastName})");
            return npc;
        }

        /// <summary>
        /// Applies green uniform appearance with randomized biometrics.
        /// </summary>
        public static void ApplyAppearance(NPC npc, int seed)
        {
            if (npc?.Avatar == null)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, "BudtenderSpawner: Cannot apply appearance — NPC or Avatar null");
                return;
            }

            try
            {
                var state = UnityEngine.Random.state;
                UnityEngine.Random.InitState(seed);

                var settings = ScriptableObject.CreateInstance<AvatarSettings>();

                if (settings.FaceLayerSettings == null)
                    settings.FaceLayerSettings = new GameSystem.Collections.Generic.List<AvatarSettings.LayerSetting>();
                if (settings.BodyLayerSettings == null)
                    settings.BodyLayerSettings = new GameSystem.Collections.Generic.List<AvatarSettings.LayerSetting>();
                if (settings.AccessorySettings == null)
                    settings.AccessorySettings = new GameSystem.Collections.Generic.List<AvatarSettings.AccessorySetting>();

                // Gender MUST be first draw (matches DetermineGender)
                settings.Gender = UnityEngine.Random.Range(0f, 1f);
                bool isFemale = settings.Gender >= 0.5f;

                // Biometrics — reuse NpcSpawner pools
                var skinTone = NpcSpawner.SkinTones[UnityEngine.Random.Range(0, NpcSpawner.SkinTones.Length)];
                settings.SkinColor = skinTone;

                var faceColor = new Color(skinTone.r * 0.92f, skinTone.g * 0.88f, skinTone.b * 0.85f);

                settings.Height = UnityEngine.Random.Range(0.93f, 1.08f);
                settings.Weight = UnityEngine.Random.Range(0.3f, 0.6f);

                // Hair
                float hairHue = UnityEngine.Random.value;
                Color hairColor;
                if (hairHue < 0.4f)
                    hairColor = new Color(0.1f, 0.08f, 0.06f);
                else if (hairHue < 0.7f)
                    hairColor = new Color(0.35f, 0.22f, 0.12f);
                else if (hairHue < 0.9f)
                    hairColor = new Color(0.7f, 0.55f, 0.35f);
                else
                    hairColor = new Color(0.55f, 0.25f, 0.15f);
                settings.HairColor = hairColor;

                var hairPool = isFemale ? CapFriendlyFemaleHair : CapFriendlyMaleHair;
                settings.HairPath = hairPool[UnityEngine.Random.Range(0, hairPool.Length)];

                // Eyes
                settings.EyeBallTint = Color.white;
                settings.EyeballMaterialIdentifier = "Default";
                settings.PupilDilation = UnityEngine.Random.Range(0.5f, 0.8f);
                settings.EyebrowScale = UnityEngine.Random.Range(0.8f, 1.1f);
                settings.EyebrowThickness = UnityEngine.Random.Range(0.7f, 1.2f);
                settings.LeftEyeLidColor = skinTone;
                settings.RightEyeLidColor = skinTone;

                var eyeConfig = new Eye.EyeLidConfiguration { topLidOpen = 0.5f, bottomLidOpen = 0.5f };
                settings.LeftEyeRestingState = eyeConfig;
                settings.RightEyeRestingState = eyeConfig;

                // Clear layers
                settings.FaceLayerSettings.Clear();
                settings.BodyLayerSettings.Clear();
                settings.AccessorySettings.Clear();

                // Face layer (prevents black face)
                var faceLayer = new AvatarSettings.LayerSetting();
                faceLayer.layerPath = NpcSpawner.FaceExpressions[
                    UnityEngine.Random.Range(0, NpcSpawner.FaceExpressions.Length)];
                faceLayer.layerTint = faceColor;
                settings.FaceLayerSettings.Add(faceLayer);

                // Green tucked T-shirt
                var shirtLayer = new AvatarSettings.LayerSetting();
                shirtLayer.layerPath = "Avatar/Layers/Top/Tucked T-Shirt";
                shirtLayer.layerTint = ShirtGreen;
                settings.BodyLayerSettings.Add(shirtLayer);

                // Dark pants
                var pantsLayer = new AvatarSettings.LayerSetting();
                pantsLayer.layerPath = "Avatar/Layers/Bottom/Jeans";
                pantsLayer.layerTint = DarkPants;
                settings.BodyLayerSettings.Add(pantsLayer);

                // Green cap
                var capSetting = new AvatarSettings.AccessorySetting();
                capSetting.path = "Avatar/Accessories/Head/Cap/Cap";
                capSetting.color = CapGreen;
                settings.AccessorySettings.Add(capSetting);

                // Sneakers
                var shoeSetting = new AvatarSettings.AccessorySetting();
                shoeSetting.path = "Avatar/Accessories/Feet/Sneakers/Sneakers";
                shoeSetting.color = DarkShoes;
                settings.AccessorySettings.Add(shoeSetting);

                npc.Avatar.LoadAvatarSettings(settings);
                UnityEngine.Random.state = state;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Customer, $"BudtenderSpawner: ApplyAppearance failed for {npc.ID}: {ex.Message}");
            }
        }
    }
}
