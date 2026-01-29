using S1API.Entities;
using S1API.Entities.Dialogue;
using S1API.Entities.Schedule;
using S1API.Entities.Appearances.CustomizationFields;
using S1API.Entities.Appearances.FaceLayerFields;
using S1API.Entities.Appearances.BodyLayerFields;
using S1API.Entities.Appearances.AccessoryFields;
using S1API.Money;
using S1API.GameTime;
using Il2CppScheduleOne.VoiceOver;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Product;
using OverTheCounter.Quests;
using OverTheCounter.SaveData;
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

        /// <summary>
        /// Static reference so VicSaveData can trigger a dialogue rebuild after state changes.
        /// </summary>
        public static VicNPC Instance { get; private set; }

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
            Instance = this;

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

            VicSaveData.Instance?.OnVicSpawned();

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

        /// <summary>
        /// Rebuilds the dialogue container based on VicIntroQuest stage.
        /// Called on spawn, on conversation start, and after quest state changes.
        /// </summary>
        public void RefreshDialogue()
        {
            int stage = VicIntroQuest.Instance?.Stage ?? 0;

            Dialogue.BuildAndRegisterContainer("VicGreeting", container =>
            {
                if (stage == 1)
                {
                    // First meeting — quest obj1 active
                    container.AddNode("ENTRY", "Good, you showed up. Look, I work at the bank and I've seen your deposits getting flagged.", choices =>
                    {
                        choices.Add("CONT1", "...", "LINE2");
                    });

                    container.AddNode("LINE2", "I'm throwing a party for the bank manager and some of the other tellers.", choices =>
                    {
                        choices.Add("CONT2", "...", "LINE3");
                    });

                    container.AddNode("LINE3", "Bring me 40 grams of weed and I'll help cushion your deposit limits.", choices =>
                    {
                        choices.Add("ACCEPT", "I'll get it done.", "ACCEPT_EXIT");
                    });

                    container.AddNode("ACCEPT_EXIT", "Good. Don't keep me waiting.");
                }
                else if (stage == 2)
                {
                    // Handover stage — quest obj2 active
                    int weedGrams = CountWeedInInventory();
                    if (weedGrams >= 40)
                    {
                        container.AddNode("ENTRY", "You got the stuff?", choices =>
                        {
                            choices.Add("HANDOVER", "Hand it over", "HANDOVER_EXIT");
                            choices.Add("LEAVE", "Not yet", "LEAVE_EXIT");
                        });

                        container.AddNode("HANDOVER_EXIT", "Nice doing business. Your limits just got a lot friendlier.");
                        container.AddNode("LEAVE_EXIT", "Don't take too long.");
                    }
                    else
                    {
                        container.AddNode("ENTRY", $"You got the stuff? I need 40 grams of weed. You've got {weedGrams} grams.", choices =>
                        {
                            choices.Add("LEAVE", "I'll be back", "LEAVE_EXIT");
                        });

                        container.AddNode("LEAVE_EXIT", "Don't keep me waiting.");
                    }
                }
                else if (stage >= 3)
                {
                    // Post-quest: laundering service
                    int today = TimeManager.ElapsedDays;
                    int lastDeposit = VicSaveData.Instance?.LastDepositDay ?? -1;
                    bool cooldownActive = lastDeposit >= 0 && lastDeposit >= today;
                    float cash = Money.GetCashBalance();

                    if (cooldownActive)
                    {
                        container.AddNode("ENTRY", "Too much heat today. Come back tomorrow.", choices =>
                        {
                            choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                        });

                        container.AddNode("LEAVE_EXIT", "*glances around nervously*");
                    }
                    else if (cash >= 500f)
                    {
                        container.AddNode("ENTRY", "You need some cash cleaned? I can run $500 through the books. You'll get $400 back in your account.", choices =>
                        {
                            choices.Add("LAUNDER", "Launder $500 (Receive $400)", "LAUNDER_EXIT");
                            choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                        });

                        container.AddNode("LAUNDER_EXIT", "Done. Check your account -- should see a deposit from a consulting gig.");
                        container.AddNode("LEAVE_EXIT", "You know where to find me.");
                    }
                    else
                    {
                        container.AddNode("ENTRY", "You need at least $500 in cash for me to work with. You're short.", choices =>
                        {
                            choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                        });

                        container.AddNode("LEAVE_EXIT", "Don't waste my time until you've got the cash.");
                    }
                }
                else
                {
                    // Stage 0 / not texted
                    container.AddNode("ENTRY", "Vic is ignoring you.", choices =>
                    {
                        choices.Add("LEAVE", "Leave", "EXIT");
                    });

                    container.AddNode("EXIT", "*grunts*");
                }
            });

            Dialogue.UseContainerOnInteract("VicGreeting");
        }

        private void SetupDialogue()
        {
            Dialogue.BuildAndSetDatabase(db =>
            {
                db.WithModuleEntry("Responses", "IGNORE", "Vic is ignoring you.");
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
                    VicIntroQuest.Instance?.CompleteObj1();
                    RefreshDialogue();
                }
                catch (Exception ex)
                {
                    Logger.Error($"ACCEPT callback failed: {ex.Message}");
                }
            });

            Dialogue.OnChoiceSelected("HANDOVER", () =>
            {
                try
                {
                    if (RemoveWeedFromInventory(40))
                    {
                        VicIntroQuest.Instance?.CompleteObj2();
                        VicSaveData.Instance?.OnQuestComplete();
                        RefreshDialogue();
                    }
                    else
                    {
                        Logger.Warning("HANDOVER: failed to remove 40g weed from inventory.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"HANDOVER callback failed: {ex.Message}");
                }
            });

            Dialogue.OnChoiceSelected("LAUNDER", () =>
            {
                try
                {
                    int today = TimeManager.ElapsedDays;
                    int lastDeposit = VicSaveData.Instance?.LastDepositDay ?? -1;
                    bool cooldownActive = lastDeposit >= 0 && lastDeposit >= today;

                    if (cooldownActive || Money.GetCashBalance() < 500f)
                        return;

                    Money.ChangeCashBalance(-500f, true, true);
                    Money.CreateOnlineTransaction("Consulting Fee", 400f, 1f, "VR Services");
                    VicSaveData.Instance?.OnLaunderComplete(today);
                    RefreshDialogue();
                }
                catch (Exception ex)
                {
                    Logger.Error($"LAUNDER callback failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Returns true if the slot holds packaged weed (jar or baggie).
        /// Checks the Il2Cpp ProductItemInstance directly: must have AppliedPackaging
        /// and its Definition must be an Il2Cpp WeedDefinition.
        /// </summary>
        private bool IsPackagedWeed(Il2CppScheduleOne.ItemFramework.ItemSlot slot, out int packagingQuantity)
        {
            packagingQuantity = 0;
            if (slot == null || slot.ItemInstance == null || slot.Quantity <= 0)
                return false;

            try
            {
                var productItem = slot.ItemInstance.TryCast<ProductItemInstance>();
                if (productItem == null) return false;

                var packaging = productItem.AppliedPackaging;
                if (packaging == null || packaging.Quantity <= 0) return false;

                // Check the definition is a weed product at the Il2Cpp level
                if (productItem.Definition == null) return false;
                var weedDef = productItem.Definition.TryCast<Il2CppScheduleOne.Product.WeedDefinition>();
                if (weedDef == null) return false;

                packagingQuantity = packaging.Quantity;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private int CountWeedInInventory()
        {
            int totalGrams = 0;
            try
            {
                var playerInv = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance;
                if (playerInv?.hotbarSlots == null) return 0;

                for (int i = 0; i < playerInv.hotbarSlots.Count; i++)
                {
                    var slot = playerInv.hotbarSlots[i];
                    if (!IsPackagedWeed(slot, out int multiplier)) continue;
                    totalGrams += slot.Quantity * multiplier;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"CountWeedInInventory failed: {ex.Message}");
            }
            return totalGrams;
        }

        private bool RemoveWeedFromInventory(int grams)
        {
            int remaining = grams;
            try
            {
                var playerInv = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance;
                if (playerInv?.hotbarSlots == null) return false;

                for (int i = 0; i < playerInv.hotbarSlots.Count && remaining > 0; i++)
                {
                    var slot = playerInv.hotbarSlots[i];
                    if (!IsPackagedWeed(slot, out int multiplier)) continue;

                    int slotGrams = slot.Quantity * multiplier;

                    if (slotGrams <= remaining)
                    {
                        remaining -= slotGrams;
                        slot.ClearStoredInstance();
                    }
                    else
                    {
                        int stacksToRemove = remaining / multiplier;
                        if (remaining % multiplier != 0)
                            stacksToRemove++;

                        slot.ChangeQuantity(-stacksToRemove);
                        remaining = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"RemoveWeedFromInventory failed: {ex.Message}");
                return false;
            }
            return remaining <= 0;
        }

        protected override void OnDestroyed()
        {
            base.OnDestroyed();
        }
    }
}
