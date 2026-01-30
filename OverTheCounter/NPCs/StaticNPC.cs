using S1API.Entities;
using S1API.Entities.Dialogue;
using S1API.Entities.Schedule;
using S1API.Entities.Appearances.CustomizationFields;
using S1API.Entities.Appearances.FaceLayerFields;
using S1API.Entities.Appearances.BodyLayerFields;
using S1API.Entities.Appearances.AccessoryFields;
using Il2CppScheduleOne.VoiceOver;
using Il2CppScheduleOne.DevUtilities;
using OverTheCounter.SaveData;
using UnityEngine;
using MelonLoader;
using System;

namespace OverTheCounter.NPCs
{
    public sealed class StaticNPC : NPC
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("StaticNPC");
        private static readonly EVOLineType[] DismissalSounds = { EVOLineType.Angry, EVOLineType.Annoyed, EVOLineType.No };

        private Il2CppScheduleOne.NPCs.NPC _gameNpc;

        public static StaticNPC Instance { get; private set; }

        public bool DialogueReady { get; private set; }

        public override bool IsPhysical => true;

        private bool CanTalkToStatic => Il2CppScheduleOne.Money.ATM.WeeklyDepositSum >= 5000f;

        protected override void ConfigurePrefab(NPCPrefabBuilder builder)
        {
            var spawnPosition = new Vector3(13.72f, 5.16f, 95.96f);
            var spawnRotation = Quaternion.Euler(0.0f, 230.7f, 0.0f);

            builder
                .WithIdentity("static_casino_fixer", "Static", "")
                .WithSpawnPosition(spawnPosition, spawnRotation)
                .WithAppearanceDefaults(av =>
                {
                    av.Gender = 0.0f;
                    av.Height = 0.85f;
                    av.Weight = 0.45f;
                    av.HairPath = HairStyle.Spiky;
                    av.HairColor = new Color(0.08f, 0.08f, 0.08f);
                    av.SkinColor = new Color32(210, 195, 185, 255);
                    av.WithFaceLayer(Face.Agitated, new Color(0.8f, 0.72f, 0.65f));
                    av.WithFaceLayer(FacialHair.Stubble, new Color(0.15f, 0.12f, 0.1f));
                    av.WithFaceLayer(Eyes.TiredEyes, new Color(0.6f, 0.5f, 0.5f));
                    av.WithBodyLayer(Shirts.FlannelButtonUp, new Color(0.25f, 0.12f, 0.12f));
                    av.WithBodyLayer(Pants.Jeans, new Color(0.15f, 0.15f, 0.2f));
                    av.WithAccessoryLayer(Feet.Sneakers, new Color(0.15f, 0.15f, 0.15f));
                })
                .WithSchedule(plan =>
                {
                    plan.WalkTo(spawnPosition, 10);
                });
        }

        protected override void OnCreated()
        {
            base.OnCreated();
            Instance = this;

            _gameNpc = gameObject.GetComponent<Il2CppScheduleOne.NPCs.NPC>();

            try
            {
                Appearance
                    .Set<Gender>(0.0f)
                    .Set<Height>(0.85f)
                    .Set<Weight>(0.45f)
                    .Set<SkinColor>(new Color32(210, 195, 185, 255))
                    .Set<EyeBallTint>(Color.white)
                    .Set<EyebrowScale>(0.85f)
                    .Set<EyebrowThickness>(0.6f)
                    .Set<HairStyle>(HairStyle.Spiky)
                    .Set<HairColor>(new Color(0.08f, 0.08f, 0.08f))
                    .WithFaceLayer<Face>(Face.Agitated, new Color(0.8f, 0.72f, 0.65f))
                    .WithFaceLayer<FacialHair>(FacialHair.Stubble, new Color(0.15f, 0.12f, 0.1f))
                    .WithFaceLayer<Eyes>(Eyes.TiredEyes, new Color(0.6f, 0.5f, 0.5f))
                    .WithBodyLayer<Shirts>(Shirts.FlannelButtonUp, new Color(0.25f, 0.12f, 0.12f))
                    .WithBodyLayer<Pants>(Pants.Jeans, new Color(0.15f, 0.15f, 0.2f))
                    .WithAccessoryLayer<Head>(Head.RectangleFrameGlasses, new Color(0.15f, 0.15f, 0.15f))
                    .WithAccessoryLayer<Chest>(Chest.OpenVest, new Color(0.12f, 0.12f, 0.14f))
                    .WithAccessoryLayer<Feet>(Feet.Sneakers, new Color(0.15f, 0.15f, 0.15f))
                    .WithBodyLayer<Accessories>(Accessories.FingerlessGloves, new Color(0.12f, 0.12f, 0.12f))
                    .Build();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to set Static's appearance: {ex.Message}");
                Appearance.GenerateRandomAppearance();
                Appearance.Build();
            }

            EnsureVoiceDatabase();
            Schedule.Enable();
            SetupDialogue();
            DialogueReady = true;

            StaticSaveData.Instance?.OnStaticSpawned();
        }

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

        public void RefreshDialogue()
        {
            bool introCompleted = StaticSaveData.Instance?.IntroCompleted ?? false;

            Dialogue.BuildAndRegisterContainer("StaticGreeting", container =>
            {
                if (!CanTalkToStatic)
                {
                    container.AddNode("ENTRY", "You lost? I don't talk to tourists. Come back when you're moving real volume.", choices =>
                    {
                        choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                    });

                    container.AddNode("LEAVE_EXIT", "*turns away*");
                }
                else if (!introCompleted)
                {
                    container.AddNode("ENTRY", "You've been pushing volume. I noticed. I'm Static \u2014 I keep things... running. We should talk business.", choices =>
                    {
                        choices.Add("ACCEPT", "I'm listening.", "ACCEPT_EXIT");
                        choices.Add("LEAVE", "Not now.", "LEAVE_EXIT");
                    });

                    container.AddNode("ACCEPT_EXIT", "Good. I'll be in touch.");
                    container.AddNode("LEAVE_EXIT", "You know where to find me.");
                }
                else
                {
                    container.AddNode("ENTRY", "We've already talked. I'll have something for you soon.", choices =>
                    {
                        choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                    });

                    container.AddNode("LEAVE_EXIT", "*nods*");
                }
            });

            Dialogue.UseContainerOnInteract("StaticGreeting");
        }

        private void SetupDialogue()
        {
            Dialogue.BuildAndSetDatabase(db =>
            {
                db.WithModuleEntry("Responses", "IGNORE", "Static is ignoring you.");
            });

            RefreshDialogue();

            Dialogue.OnConversationStart(() =>
            {
                RefreshDialogue();
            });

            Dialogue.OnChoiceSelected("LEAVE", () =>
            {
                PlayDismissalSound();
            });

            Dialogue.OnChoiceSelected("ACCEPT", () =>
            {
                try
                {
                    StaticSaveData.Instance?.OnIntroCompleted();
                    RefreshDialogue();
                }
                catch (Exception ex)
                {
                    Logger.Error($"ACCEPT callback failed: {ex.Message}");
                }
            });
        }

        protected override void OnDestroyed()
        {
            base.OnDestroyed();
        }
    }
}
