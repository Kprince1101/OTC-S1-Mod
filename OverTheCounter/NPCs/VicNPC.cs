using S1API.Entities;
using S1API.Entities.Dialogue;
using S1API.Entities.Schedule;
using S1API.Entities.Appearances.CustomizationFields;
using S1API.Entities.Appearances.FaceLayerFields;
using S1API.Entities.Appearances.BodyLayerFields;
using S1API.Entities.Appearances.AccessoryFields;
using S1API.Money;
using S1API.GameTime;
using OverTheCounter.Quests;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using UnityEngine;
using UnityEngine.AI;
using MelonLoader;
using System;

#if IL2CPP
using Il2CppScheduleOne.VoiceOver;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Product;
#else
using ScheduleOne.VoiceOver;
using ScheduleOne.DevUtilities;
using ScheduleOne.Product;
#endif

namespace OverTheCounter.NPCs
{
    /// <summary>
    /// Vic Reynolds - A corrupt bank teller NPC who facilitates early-game money laundering.
    /// Spawns behind the bank in a secluded alley.
    /// </summary>
    public sealed class VicNPC : NPC
    {
        private static readonly EVOLineType[] DismissalSounds = { EVOLineType.Angry, EVOLineType.Annoyed, EVOLineType.No };

        private static readonly Vector3 SpawnPosition = new Vector3(67.75f, 0.97f, 32.36f);
        private static readonly Quaternion SpawnRotation = Quaternion.Euler(0f, 87.6f, 0f);

        private ScheduleOne.NPCs.NPC _gameNpc;

        /// <summary>
        /// Static reference so VicSaveData can trigger a dialogue rebuild after state changes.
        /// </summary>
        public static VicNPC Instance { get; private set; }

        /// <summary>True once SetupDialogue() has built the initial container.</summary>
        public bool DialogueReady { get; private set; }

        /// <summary>True while the player is in an active dialogue with Vic.</summary>
        /// <remarks>Try-catch: IL2CPP native object may be destroyed after scene transitions while C# wrapper survives.</remarks>
        public bool IsInDialogue { get { try { return Dialogue?.IsDialogueInProgress ?? false; } catch { return false; } } }

        public Vector3? CurrentPosition
        {
            get
            {
                try { return _gameNpc?.transform.position; }
                catch { return null; }
            }
        }

        public override bool IsPhysical => true;

        /// <summary>
        /// Warps Vic to the spawn position on the host.
        /// Movement.Warp() snaps to the NavMesh surface (correct Y) and
        /// sends a FishNet RPC that syncs the position to all clients.
        /// Must only be called on the host.
        /// </summary>
        public void WarpToSpawn()
        {
            try
            {
                // Pre-snap position to NavMesh surface so the Warp RPC sends
                // the correct ground-level Y to clients. Without this, clients
                // with a disabled NavMeshAgent receive the raw SpawnPosition Y
                // and the NPC floats above ground.
                Vector3 warpPos = SpawnPosition;
                if (NavMesh.SamplePosition(SpawnPosition, out NavMeshHit hit, 10f, NavMesh.AllAreas))
                    warpPos = hit.position;

                Movement.Warp(warpPos);
                Movement.Stop();
                Movement.FaceDirection(SpawnRotation * Vector3.forward);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"WarpToSpawn failed: {ex.Message}");
            }
        }

        protected override void ConfigurePrefab(NPCPrefabBuilder builder)
        {
            builder
                .WithIdentity("vic_bank_teller", "Vic", "Reynolds")
                .WithSpawnPosition(SpawnPosition, SpawnRotation)
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
                    plan.WalkTo(SpawnPosition, 10, true, 1f, true);
                });
        }

        protected override void OnCreated()
        {
            base.OnCreated();
            Instance = this;

            _gameNpc = gameObject.GetComponent<ScheduleOne.NPCs.NPC>();

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
                OTCLog.Error(OTCLog.Systems.NPC, $"Failed to set Vic's appearance: {ex.Message}");
                Appearance.GenerateRandomAppearance();
                Appearance.Build();
            }

            EnsureVoiceDatabase();

            // Schedule + pathfinding run on host only. The host's Movement.Warp()
            // sends a FishNet RPC to sync position to clients. WarpToSpawn()
            // pre-snaps Y via NavMesh.SamplePosition so the RPC sends correct
            // ground-level coordinates regardless of client NavMeshAgent state.
            if (NetworkHelper.IsHost)
            {
                Schedule.Enable();
                Schedule.EnforceState();
            }

            try
            {
                SetupDialogue();
                DialogueReady = true;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"SetupDialogue FAILED: {ex.Message}\n{ex.StackTrace}");
            }

            if (VicSaveData.Instance == null)
            {
                try
                {
                    new VicSaveData();
                    ConfigSyncData.ApplyPendingGameState();
                }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"VicSaveData fallback creation failed: {ex.Message}"); }
            }

            VicSaveData.Instance?.OnVicSpawned();
        }

        /// <summary>
        /// Borrows a voice database from an existing NPC if Vic's prefab lacks one.
        /// </summary>
        private void EnsureVoiceDatabase()
        {
            try
            {
                if (_gameNpc == null || _gameNpc.VoiceOverEmitter == null) return;
                if (_gameNpc.VoiceOverEmitter.GetDatabase() != null) return;

                var allNpcs = UnityEngine.Object.FindObjectsOfType<ScheduleOne.NPCs.NPC>();
                foreach (var npc in allNpcs)
                {
                    if (npc.GetInstanceID() == _gameNpc.GetInstanceID()) continue;
                    if (npc.VoiceOverEmitter != null && npc.VoiceOverEmitter.GetDatabase() != null)
                    {
                        _gameNpc.VoiceOverEmitter.SetDatabase(npc.VoiceOverEmitter.GetDatabase(), false);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"Failed to initialize voice: {ex.Message}");
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
                OTCLog.Warning(OTCLog.Systems.NPC, $"Failed to play dismissal sound: {ex.Message}");
            }
        }

        /// <summary>
        /// Rebuilds the dialogue container based on VicIntroQuest stage.
        /// Called on spawn, on conversation start, and after quest state changes.
        /// </summary>
        public void RefreshDialogue()
        {
            int stage = VicIntroQuest.Instance?.Stage ?? 0;

            // If the quest was just created but Instance isn't ready yet,
            // HasBeenTexted guarantees we're at least stage 1.
            if (stage == 0 && (VicSaveData.Instance?.HasBeenTexted ?? false))
            {
                stage = VicSaveData.Instance.Unlocked ? 3 : 1;
            }

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

                    container.AddNode("LINE3", $"Bring me {Config.VicIntroWeedGrams.Value} grams of weed and I'll help you clean some cash.", choices =>
                    {
                        choices.Add("ACCEPT", "I'll get it done.", "ACCEPT_EXIT");
                    });

                    container.AddNode("ACCEPT_EXIT", "Good. Don't keep me waiting.");
                }
                else if (stage == 2)
                {
                    // Handover stage — quest obj2 active
                    int weedGrams = CountWeedInInventory();
                    if (weedGrams >= Config.VicIntroWeedGrams.Value)
                    {
                        container.AddNode("ENTRY", "You got the stuff?", choices =>
                        {
                            choices.Add("HANDOVER", "Hand it over", "HANDOVER_EXIT");
                            choices.Add("LEAVE", "Not yet", "LEAVE_EXIT");
                        });

                        container.AddNode("HANDOVER_EXIT", "Nice doing business. Come find me whenever you want to clean some cash.");
                        container.AddNode("LEAVE_EXIT", "Don't take too long.");
                    }
                    else
                    {
                        container.AddNode("ENTRY", $"You got the stuff? I need {Config.VicIntroWeedGrams.Value} grams of weed. You've got {weedGrams} grams.", choices =>
                        {
                            choices.Add("LEAVE", "I'll be back", "LEAVE_EXIT");
                        });

                        container.AddNode("LEAVE_EXIT", "Don't keep me waiting.");
                    }
                }
                else if (stage >= 3)
                {
                    // Post-quest: laundering service (tier based on trust level)
                    int today = TimeManager.ElapsedDays;
                    int lastDeposit = VicSaveData.Instance?.LastDepositDay ?? -1;
                    bool cooldownActive = lastDeposit >= 0 && lastDeposit >= today;
                    float cash = Money.GetCashBalance();

                    int trustLevel = VicSaveData.Instance?.TrustLevel ?? 0;
                    bool tier2 = trustLevel >= Config.VicTier2TrustUnlock.Value;
                    float requiredCash = tier2 ? Config.VicTier2Cost.Value : Config.VicTier1Cost.Value;
                    float returnAmount = tier2 ? Config.VicTier2Return.Value : Config.VicTier1Return.Value;

                    if (cooldownActive)
                    {
                        container.AddNode("ENTRY", "Too much heat today. Come back tomorrow.", choices =>
                        {
                            choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                        });

                        container.AddNode("LEAVE_EXIT", "*glances around nervously*");
                    }
                    else if (cash >= requiredCash)
                    {
                        if (tier2)
                        {
                            bool tier2IntroShown = VicSaveData.Instance?.Tier2IntroShown ?? false;
                            string entryText = tier2IntroShown
                                ? $"Ready to move some cash? Same deal — ${Config.VicTier2Cost.Value:N0} in, ${Config.VicTier2Return.Value:N0} back clean."
                                : $"You're reliable. We can move more. I'll run ${Config.VicTier2Cost.Value:N0} through the books — you'll get ${Config.VicTier2Return.Value:N0} back clean.";

                            container.AddNode("ENTRY", entryText, choices =>
                            {
                                choices.Add("LAUNDER", $"Launder ${Config.VicTier2Cost.Value:N0} (Receive ${Config.VicTier2Return.Value:N0})", "LAUNDER_EXIT");
                                choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                            });

                            container.AddNode("LAUNDER_EXIT", "Done. Bigger numbers, same clean paper trail.");
                        }
                        else
                        {
                            container.AddNode("ENTRY", $"You need some cash cleaned? I can run ${Config.VicTier1Cost.Value:N0} through the books. You'll get ${Config.VicTier1Return.Value:N0} back in your account.", choices =>
                            {
                                choices.Add("LAUNDER", $"Launder ${Config.VicTier1Cost.Value:N0} (Receive ${Config.VicTier1Return.Value:N0})", "LAUNDER_EXIT");
                                choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                            });

                            container.AddNode("LAUNDER_EXIT", "Done. Check your account -- should see a deposit from a consulting gig.");
                        }
                        container.AddNode("LEAVE_EXIT", "You know where to find me.");
                    }
                    else
                    {
                        if (tier2)
                        {
                            container.AddNode("ENTRY", $"You need at least ${Config.VicTier2Cost.Value:N0} in cash for me to work with. You're short.", choices =>
                            {
                                choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                            });
                        }
                        else
                        {
                            container.AddNode("ENTRY", $"You need at least ${Config.VicTier1Cost.Value:N0} in cash for me to work with. You're short.", choices =>
                            {
                                choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                            });
                        }

                        container.AddNode("LEAVE_EXIT", "Don't waste my time until you've got the cash.");
                    }
                }
                else
                {
                    // Stage 0 / not texted
                    container.AddNode("ENTRY", "Not now. I'll find you when I need something.", choices =>
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

            Dialogue.OnChoiceSelected("LEAVE", () =>
            {
                PlayDismissalSound();
            });

            Dialogue.OnChoiceSelected("ACCEPT", () =>
            {
                try
                {
                    if (NetworkHelper.IsHost)
                    {
                        VicSaveData.Instance?.HandleRemoteAction("VIC_QUEST_ACCEPTED");
                    }
                    else
                    {
                        ConfigSyncData.SendQuestAction("VIC_QUEST_ACCEPTED");
                    }

                    // Defer rebuild — dialogue is still open; Tick() refreshes on close.
                    if (VicSaveData.Instance != null)
                        VicSaveData.Instance._dialogueStale = true;
                }
                catch (Exception ex)
                {
                    OTCLog.Error(OTCLog.Systems.NPC, $"ACCEPT callback failed: {ex.Message}");
                }
            });

            Dialogue.OnChoiceSelected("HANDOVER", () =>
            {
                try
                {
                    if (!RemoveWeedFromInventory(Config.VicIntroWeedGrams.Value))
                    {
                        OTCLog.Warning(OTCLog.Systems.NPC, $"HANDOVER: failed to remove {Config.VicIntroWeedGrams.Value}g weed from inventory.");
                        return;
                    }

                    if (NetworkHelper.IsHost)
                    {
                        VicSaveData.Instance?.HandleRemoteAction("VIC_QUEST_COMPLETE");
                    }
                    else
                    {
                        ConfigSyncData.SendQuestAction("VIC_QUEST_COMPLETE");
                    }

                    // Defer rebuild — dialogue is still open; Tick() refreshes on close.
                    if (VicSaveData.Instance != null)
                        VicSaveData.Instance._dialogueStale = true;
                }
                catch (Exception ex)
                {
                    OTCLog.Error(OTCLog.Systems.NPC, $"HANDOVER callback failed: {ex.Message}");
                }
            });

            Dialogue.OnChoiceSelected("LAUNDER", () =>
            {
                try
                {
                    int today = TimeManager.ElapsedDays;
                    int lastDeposit = VicSaveData.Instance?.LastDepositDay ?? -1;
                    bool cooldownActive = lastDeposit >= 0 && lastDeposit >= today;

                    int trustLevel = VicSaveData.Instance?.TrustLevel ?? 0;
                    bool tier2 = trustLevel >= Config.VicTier2TrustUnlock.Value;
                    float cost = tier2 ? Config.VicTier2Cost.Value : Config.VicTier1Cost.Value;
                    float payout = tier2 ? Config.VicTier2Return.Value : Config.VicTier1Return.Value;

                    if (cooldownActive || Money.GetCashBalance() < cost)
                        return;

                    // Local player effects — always execute (it's this player's money).
                    Money.ChangeCashBalance(-cost, true, true);
                    Money.CreateOnlineTransaction("Consulting Fee", payout, 1f, "VR Services");

                    // Set cooldown immediately (host + client) so the callback's own
                    // re-check blocks any double-clicks before the dialogue rebuilds.
                    VicSaveData.Instance?.OnLaunderComplete(today);

                    if (NetworkHelper.IsHost)
                    {
                        if (tier2)
                            VicSaveData.Instance?.MarkTier2IntroShown();
                    }
                    else
                    {
                        ConfigSyncData.SendQuestAction("VIC_LAUNDER");
                    }

                    // Defer rebuild — dialogue is still open; Tick() refreshes on close.
                    if (VicSaveData.Instance != null)
                        VicSaveData.Instance._dialogueStale = true;
                }
                catch (Exception ex)
                {
                    OTCLog.Error(OTCLog.Systems.NPC, $"LAUNDER callback failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Returns true if the slot holds packaged weed (jar or baggie).
        /// Checks the Il2Cpp ProductItemInstance directly: must have AppliedPackaging
        /// and its Definition must be an Il2Cpp WeedDefinition.
        /// </summary>
        private bool IsPackagedWeed(ScheduleOne.ItemFramework.ItemSlot slot, out int packagingQuantity)
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
                var weedDef = productItem.Definition.TryCast<ScheduleOne.Product.WeedDefinition>();
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
                var playerInv = PlayerSingleton<ScheduleOne.PlayerScripts.PlayerInventory>.Instance;
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
                OTCLog.Warning(OTCLog.Systems.NPC, $"CountWeedInInventory failed: {ex.Message}");
            }
            return totalGrams;
        }

        private bool RemoveWeedFromInventory(int grams)
        {
            int remaining = grams;
            try
            {
                var playerInv = PlayerSingleton<ScheduleOne.PlayerScripts.PlayerInventory>.Instance;
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
                OTCLog.Error(OTCLog.Systems.NPC, $"RemoveWeedFromInventory failed: {ex.Message}");
                return false;
            }
            return remaining <= 0;
        }

        protected override void OnDestroyed()
        {
            if (Instance == this)
            {
                DialogueReady = false;
                Instance = null;
            }
            VicSaveData.ResetInstance();
            base.OnDestroyed();
        }
    }
}
