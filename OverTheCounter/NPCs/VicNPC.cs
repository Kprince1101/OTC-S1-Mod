using S1API.Entities;
using S1API.Entities.Dialogue;
using S1API.Entities.Schedule;
using S1API.Entities.Appearances.CustomizationFields;
using S1API.Entities.Appearances.FaceLayerFields;
using S1API.Entities.Appearances.BodyLayerFields;
using S1API.Entities.Appearances.AccessoryFields;
using Il2CppScheduleOne.VoiceOver;
using UnityEngine;
using MelonLoader;
using System;

namespace OverTheCounter.NPCs
{
    /// <summary>
    /// Vic Reynolds - A corrupt bank teller NPC who facilitates early-game money laundering.
    /// Spawns behind the bank in a secluded alley.
    /// </summary>
    public sealed class VicNPC : NPC
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("VicNPC");
        private static readonly EVOLineType[] DismissalSounds = { EVOLineType.Angry, EVOLineType.Annoyed, EVOLineType.No };

        private Il2CppScheduleOne.NPCs.NPC _gameNpc;

        public override bool IsPhysical => true;

        protected override void ConfigurePrefab(NPCPrefabBuilder builder)
        {
            var spawnPosition = new Vector3(72.08f, 0.97f, 31.71f);
            var spawnRotation = Quaternion.Euler(0f, 330f, 0f);

            builder
                .WithIdentity("vic_bank_teller", "Vic", "Reynolds")
                .WithSpawnPosition(spawnPosition, spawnRotation)
                .WithAppearanceDefaults(av =>
                {
                    av.Gender = 0.0f;
                    av.Height = 0.95f;
                    av.Weight = 0.6f;
                    av.HairPath = HairStyle.Receding;
                    av.HairColor = new Color(0.15f, 0.1f, 0.08f);
                    av.SkinColor = new Color32(230, 205, 180, 255);
                    av.WithFaceLayer(Face.NeutralPout, new Color(0.85f, 0.75f, 0.65f));
                    av.WithFaceLayer(FacialHair.Stubble, new Color(0.2f, 0.15f, 0.1f));
                    av.WithBodyLayer(Shirts.RolledButtonUp, new Color(0.9f, 0.9f, 0.92f));
                    av.WithBodyLayer(Pants.Jeans, new Color(0.12f, 0.12f, 0.18f));
                    av.WithAccessoryLayer(Feet.DressShoes, new Color(0.2f, 0.12f, 0.08f));
                })
                .WithSchedule(plan =>
                {
                    plan.WalkTo(spawnPosition, 10);
                });
        }

        protected override void OnCreated()
        {
            base.OnCreated();

            _gameNpc = gameObject.GetComponent<Il2CppScheduleOne.NPCs.NPC>();

            try
            {
                Appearance
                    .Set<Gender>(0.0f)
                    .Set<Height>(0.95f)
                    .Set<Weight>(0.6f)
                    .Set<SkinColor>(new Color32(230, 205, 180, 255))
                    .Set<EyeBallTint>(Color.white)
                    .Set<EyebrowScale>(0.9f)
                    .Set<EyebrowThickness>(0.7f)
                    .Set<HairStyle>(HairStyle.Receding)
                    .Set<HairColor>(new Color(0.15f, 0.1f, 0.08f))
                    .WithFaceLayer<Face>(Face.NeutralPout, new Color(0.85f, 0.75f, 0.65f))
                    .WithFaceLayer<FacialHair>(FacialHair.Stubble, new Color(0.2f, 0.15f, 0.1f))
                    .WithBodyLayer<Shirts>(Shirts.RolledButtonUp, new Color(0.9f, 0.9f, 0.92f))
                    .WithBodyLayer<Pants>(Pants.Jeans, new Color(0.12f, 0.12f, 0.18f))
                    .WithAccessoryLayer<Chest>(Chest.OpenVest, new Color(0.2f, 0.2f, 0.22f))
                    .WithAccessoryLayer<Feet>(Feet.DressShoes, new Color(0.2f, 0.12f, 0.08f))
                    .WithAccessoryLayer<Waist>(Waist.Belt, new Color(0.18f, 0.12f, 0.06f))
                    .WithAccessoryLayer<Head>(Head.RectangleFrameGlasses, new Color(0.15f, 0.15f, 0.15f))
                    .WithAccessoryLayer<Neck>(Neck.GoldChain, new Color(0.85f, 0.7f, 0.3f))
                    .WithAccessoryLayer<Hands>(Hands.Polex, new Color(0.7f, 0.72f, 0.74f))
                    .Build();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to set Vic's appearance: {ex.Message}");
                Appearance.GenerateRandomAppearance();
                Appearance.Build();
            }

            EnsureVoiceDatabase();
            Schedule.Enable();
            SetupDialogue();

            Logger.Msg("Vic has spawned behind the bank");
        }

        /// <summary>
        /// Borrows a voice database from an existing NPC if Vic's prefab lacks one.
        /// </summary>
        private void EnsureVoiceDatabase()
        {
            try
            {
                if (_gameNpc == null || _gameNpc.VoiceOverEmitter == null) return;
                if (_gameNpc.VoiceOverEmitter.Database != null) return;

                var allNpcs = UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.NPCs.NPC>();
                foreach (var npc in allNpcs)
                {
                    if (npc.GetInstanceID() == _gameNpc.GetInstanceID()) continue;
                    if (npc.VoiceOverEmitter != null && npc.VoiceOverEmitter.Database != null)
                    {
                        _gameNpc.VoiceOverEmitter.SetDatabase(npc.VoiceOverEmitter.Database, false);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to initialize voice: {ex.Message}");
            }
        }

        private void PlayDismissalSound()
        {
            try
            {
                var pick = DismissalSounds[UnityEngine.Random.Range(0, DismissalSounds.Length)];
                _gameNpc?.PlayVO(pick);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to play dismissal sound: {ex.Message}");
            }
        }

        private void SetupDialogue()
        {
            Dialogue.BuildAndSetDatabase(db =>
            {
                db.WithModuleEntry("Responses", "IGNORE", "Vic is ignoring you.");
            });

            Dialogue.BuildAndRegisterContainer("VicGreeting", container =>
            {
                container.AddNode("ENTRY", "Vic is ignoring you.", choices =>
                {
                    choices.Add("LEAVE", "Leave", "EXIT");
                });

                container.AddNode("EXIT", "*grunts*");
            });

            Dialogue.OnChoiceSelected("LEAVE", () =>
            {
                PlayDismissalSound();
            });

            Dialogue.UseContainerOnInteract("VicGreeting");
        }

        protected override void OnDestroyed()
        {
            base.OnDestroyed();
        }
    }
}
