using S1API.Entities;
using S1API.Entities.Dialogue;
using S1API.Entities.Schedule;
using S1API.Entities.Appearances.CustomizationFields;
using S1API.Entities.Appearances.FaceLayerFields;
using S1API.Entities.Appearances.BodyLayerFields;
using S1API.Entities.Appearances.AccessoryFields;
using OverTheCounter.Logic;
using OverTheCounter.Quests;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using UnityEngine;
using UnityEngine.AI;
using MelonLoader;
using System;
using System.Collections;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.VoiceOver;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.UI.Handover;
#else
using ScheduleOne.Economy;
using ScheduleOne.VoiceOver;
using ScheduleOne.DevUtilities;
using ScheduleOne.Product;
using ScheduleOne.Map;
using ScheduleOne.UI.Handover;
#endif

namespace OverTheCounter.NPCs
{
    public sealed class BellaNPC : NPC
    {
        private static readonly Vector3 SpawnPosition = new Vector3(74.1f, 1.0f, 57.2f);
        private static readonly Quaternion SpawnRotation = Quaternion.Euler(0f, 180f, 0f);

        private ScheduleOne.NPCs.NPC _gameNpc;
        private Customer _customerComponent;

        // Tracks what the current handover expects
        private EDrugType _pendingDrugType;
        private float _pendingMinPrice;

        public static BellaNPC Instance { get; private set; }
        public bool DialogueReady { get; private set; }

        public ScheduleOne.NPCs.NPC GameNpc => _gameNpc;

        public override bool IsPhysical => true;

        /// <summary>
        /// Warps Bella to the spawn position on the host with NavMesh pre-snapping.
        /// Movement.Warp() sends a FishNet RPC that syncs the correct ground-level
        /// Y to all clients, preventing floating.
        /// </summary>
        public void WarpToSpawn()
        {
            try
            {
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
                .WithIdentity("bella_penthouse", "Bella", "Noir")
                .WithSpawnPosition(SpawnPosition, SpawnRotation)
                .WithAppearanceDefaults(av =>
                {
                    av.Gender = 1.0f;
                    av.Height = 0.93f;
                    av.Weight = 0.4f;
                    av.HairPath = HairStyle.MessyBob;
                    av.HairColor = new Color(0.85f, 0.2f, 0.45f); // vivid pink
                    av.SkinColor = new Color32(215, 185, 160, 255);
                    av.WithFaceLayer(Face.NeutralPout, new Color(0.85f, 0.75f, 0.65f));
                    av.WithBodyLayer(Shirts.VNeck, new Color(0.2f, 0.15f, 0.3f)); // dark purple hoodie-like
                    av.WithBodyLayer(Pants.CargoPants, new Color(0.25f, 0.25f, 0.28f)); // dark casual pants
                    av.WithAccessoryLayer(Feet.Sneakers, new Color(0.9f, 0.9f, 0.92f));
                });
            // No schedule — InjectIntoBuilding() handles placement.
        }

        protected override void OnCreated()
        {
            base.OnCreated();
            Instance = this;

            _gameNpc = gameObject.GetComponent<ScheduleOne.NPCs.NPC>();

            // Add Customer component for HandoverScreen support (same pattern as drifters)
            try
            {
                _customerComponent = DrifterSpawner.AddCustomerComponentToActive(_gameNpc);
                if (_customerComponent != null)
                {
                    OTCLog.Msg(OTCLog.Systems.NPC, "Customer component added to Bella for HandoverScreen support");
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.NPC, "Failed to add Customer component — HandoverScreen will not be available");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"Customer component setup failed: {ex.Message}");
            }

            try
            {
                Appearance
                    .Set<Gender>(1.0f)
                    .Set<Height>(0.93f)
                    .Set<Weight>(0.4f)
                    .Set<SkinColor>(new Color32(215, 185, 160, 255))
                    .Set<EyeBallTint>(Color.white)
                    .Set<HairStyle>(HairStyle.MessyBob)
                    .Set<HairColor>(new Color(0.85f, 0.2f, 0.45f))
                    .WithFaceLayer<Face>(Face.NeutralPout, new Color(0.85f, 0.75f, 0.65f))
                    .WithBodyLayer<Shirts>(Shirts.VNeck, new Color(0.2f, 0.15f, 0.3f))
                    .WithBodyLayer<Pants>(Pants.CargoPants, new Color(0.25f, 0.25f, 0.28f))
                    .WithAccessoryLayer<Feet>(Feet.Sneakers, new Color(0.9f, 0.9f, 0.92f))
                    .Build();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"Failed to set Bella's appearance: {ex.Message}");
                Appearance.GenerateRandomAppearance();
                Appearance.Build();
            }

            EnsureVoiceDatabase();

            // Delay building injection — S1API sets SetVisible(isPhysical) after a 0.1s
            // delay in NPCPatches, which would override our SetVisible(false). We wait
            // 0.5s so our injection runs AFTER that coroutine finishes.
            MelonCoroutines.Start(DelayedBuildingInjection());

            // No schedule — Bella stays inside the building and only appears via summon.

            SetupDialogue();
            DialogueReady = true;

            if (BellaSaveData.Instance == null)
            {
                try
                {
                    new BellaSaveData();
                    ConfigSyncData.ApplyPendingGameState();
                }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"BellaSaveData fallback creation failed: {ex.Message}"); }
            }
        }

        private void EnsureVoiceDatabase()
        {
            try
            {
                if (_gameNpc == null || _gameNpc.VoiceOverEmitter == null) return;
                if (_gameNpc.VoiceOverEmitter.GetDatabase() != null) return;

                // Use a female voice from EmployeeManager's FemaleVoices array
                var empManager = ScheduleOne.DevUtilities.NetworkSingleton<
                    ScheduleOne.Employees.EmployeeManager>.Instance;
                if (empManager?.FemaleVoices != null && empManager.FemaleVoices.Length > 0)
                {
                    _gameNpc.VoiceOverEmitter.SetDatabase(empManager.FemaleVoices[0], false);
                    return;
                }

                // Fallback: copy from any NPC that has a voice database
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

        private IEnumerator DelayedBuildingInjection()
        {
            // Wait for S1API's SetClientVisibilityDelayed coroutine to finish
            yield return new WaitForSeconds(0.5f);
            InjectIntoBuilding();
        }

        /// <summary>
        /// Injects Bella into the "Tall Tower" NPCEnterableBuilding so she appears
        /// in the vanilla door-knock summon menu. Looks up by name first, falls back
        /// to nearest building within 25m of spawn position.
        /// </summary>
        private void InjectIntoBuilding()
        {
            try
            {
                if (_gameNpc == null) return;

                var buildings = UnityEngine.Object.FindObjectsOfType<NPCEnterableBuilding>();
                if (buildings == null || buildings.Length == 0)
                {
                    OTCLog.Warning(OTCLog.Systems.NPC, "No NPCEnterableBuilding instances found.");
                    return;
                }

                // Primary: match by building name (scene-baked, stable across saves)
                NPCEnterableBuilding nearest = null;
                float nearestDist = 0f;

                foreach (var b in buildings)
                {
                    if (b != null && b.BuildingName == "Tall Tower")
                    {
                        nearest = b;
                        nearestDist = Vector3.Distance(b.transform.position, SpawnPosition);
                        break;
                    }
                }

                // Fallback: nearest building within 25m of spawn position
                if (nearest == null)
                {
                    nearestDist = float.MaxValue;
                    foreach (var b in buildings)
                    {
                        if (b == null) continue;
                        float dist = Vector3.Distance(b.transform.position, SpawnPosition);
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearest = b;
                        }
                    }

                    if (nearest == null || nearestDist > 25f)
                    {
                        OTCLog.Warning(OTCLog.Systems.NPC, $"No building found within 25m of Bella's position. Nearest: {nearestDist:F1}m");
                        return;
                    }

                    OTCLog.Warning(OTCLog.Systems.NPC, $"'Tall Tower' not found by name, using nearest building: {nearest.BuildingName} ({nearestDist:F1}m)");
                }

                // Use the game's proper EnterBuilding flow:
                // 1. Set CurrentBuilding + LastEnteredDoor
                int doorIndex = 0;
                _gameNpc.SetCurrentBuilding(nearest);
                if (nearest.Doors != null && nearest.Doors.Length > 0)
                    _gameNpc.LastEnteredDoor = nearest.Doors[doorIndex];

                // 2. Disable awareness (NPC is "inside")
                try { _gameNpc.Awareness?.SetAwarenessActive(false); } catch { }

                // 3. Add to building occupant list
                // Reflection: old game has NPCEnteredBuilding(NPC), new game has (NPC, StaticDoor)
                var enteredMethod = nearest.GetType().GetMethod("NPCEnteredBuilding");
                if (enteredMethod != null && enteredMethod.GetParameters().Length == 2)
                    enteredMethod.Invoke(nearest, new object[] { _gameNpc, nearest.Doors[doorIndex] });
                else
                    enteredMethod?.Invoke(nearest, new object[] { _gameNpc });

                // 4. Hide NPC properly (model + nav agent)
                _gameNpc.SetVisible(false);

                // 5. Disable schedule so WalkTo action doesn't pull Bella outside
                try { Schedule.Disable(); }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Schedule disable failed (non-fatal): {ex.Message}"); }

                // 6. Stop movement and warp to door access point
                try
                {
                    _gameNpc.Movement?.Stop();
                    if (nearest.Doors != null && nearest.Doors.Length > 0 &&
                        nearest.Doors[doorIndex]?.AccessPoint != null)
                    {
                        _gameNpc.Movement.Warp(nearest.Doors[doorIndex].AccessPoint);
                        _gameNpc.Movement.FaceDirection(
                            -nearest.Doors[doorIndex].AccessPoint.forward, 0f);
                    }
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.NPC, $"Movement warp failed (non-fatal): {ex.Message}");
                }

                // Only summonable while quest is active (stages 1-4)
                int stage = BellaSaveData.Instance?.Stage ?? 0;
                _gameNpc.CanBeSummoned = stage >= 1 && stage < 5;

                // Verify injection
                int occupantCount = nearest.OccupantCount;
                OTCLog.Msg(OTCLog.Systems.NPC, $"Bella injected into building '{nearest.BuildingName}' (distance: {nearestDist:F1}m, " +
                               $"occupants={occupantCount}, CanBeSummoned={_gameNpc.CanBeSummoned}, " +
                               $"CurrentBuilding={((_gameNpc.CurrentBuilding != null) ? "set" : "null")}, " +
                               $"isVisible={_gameNpc.isVisible})");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"InjectIntoBuilding failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Enables summoning after the quest is triggered.
        /// </summary>
        public void EnableSummoning()
        {
            if (_gameNpc != null)
                _gameNpc.CanBeSummoned = true;
        }

        /// <summary>
        /// Re-injects Bella into the building after a summon expires.
        /// Called from BellaSummonPatch when the timer runs out.
        /// </summary>
        public void ReInjectIntoBuilding()
        {
            if (_gameNpc == null)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, "ReInjectIntoBuilding: _gameNpc is null");
                return;
            }

            OTCLog.Msg(OTCLog.Systems.NPC, "Re-injecting Bella into building after summon");
            InjectIntoBuilding();
        }

        public void RefreshDialogue()
        {
            // IL2CPP: native Dialogue object may be destroyed after scene transitions while C# wrapper survives
            try { if (Dialogue.IsDialogueInProgress) return; } catch { return; }

            int stage = BellaSaveData.Instance?.Stage ?? 0;
            if (stage == 0 && BellaProtocolQuest.Instance != null)
                stage = BellaProtocolQuest.Instance.Stage;

            Dialogue.BuildAndRegisterContainer("BellaGreeting", container =>
            {
                if (stage == 1)
                {
                    // First meeting — player doesn't know what Bella wants yet
                    container.AddNode("ENTRY", "...You're the one Manny sent? You don't look like the type.", choices =>
                    {
                        choices.Add("CONT1", "...", "LINE2");
                    });

                    container.AddNode("LINE2", "I run logistics for some people. Night shifts, warehouse access, that kind of thing.", choices =>
                    {
                        choices.Add("CONT2", "...", "LINE3");
                    });

                    container.AddNode("LINE3", $"But I don't work for free. First, bring me a weed mix worth at least ${Config.BellaWeedValue.Value:N0}. Good stuff — not ditch.", choices =>
                    {
                        choices.Add("ACCEPT_WEED", "Consider it done.", "ACCEPT_WEED_EXIT");
                        choices.Add("DECLINE_WEED", "You need sleep, not weed.", "DECLINE_WEED_EXIT");
                    });

                    container.AddNode("ACCEPT_WEED_EXIT", "Don't take forever.");
                    container.AddNode("DECLINE_WEED_EXIT", "Bring it or don't. I don't care about your opinion.");
                }
                else if (stage == 2)
                {
                    // Weed delivery stage — always offer handover
                    container.AddNode("ENTRY", $"You got that weed mix? ${Config.BellaWeedValue.Value:N0} minimum.", choices =>
                    {
                        choices.Add("HANDOVER_WEED", "Hand it over", "HANDOVER_WEED_WAIT");
                        choices.Add("ILL_BE_BACK", "I'll be back", "ILL_BE_BACK_EXIT");
                    });

                    container.AddNode("HANDOVER_WEED_WAIT", "Let me see what you've got.");
                    container.AddNode("ILL_BE_BACK_EXIT", "You better.");
                }
                else if (stage == 3)
                {
                    // Meth delivery stage
                    container.AddNode("ENTRY", $"Where's my glass? A meth mix worth at least ${Config.BellaMethValue.Value:N0}.", choices =>
                    {
                        choices.Add("HANDOVER_METH", "Hand it over", "HANDOVER_METH_WAIT");
                        choices.Add("ILL_BE_BACK", "I'll be back", "ILL_BE_BACK_EXIT");
                    });

                    container.AddNode("HANDOVER_METH_WAIT", "Let me see.");
                    container.AddNode("ILL_BE_BACK_EXIT", "Clock's ticking.");
                }
                else if (stage == 4)
                {
                    // Cocaine delivery stage
                    container.AddNode("ENTRY", $"A cocaine mix worth at least ${Config.BellaCokeValue.Value:N0}. You got it?", choices =>
                    {
                        choices.Add("HANDOVER_COKE", "Hand it over", "HANDOVER_COKE_WAIT");
                        choices.Add("ILL_BE_BACK", "I'll be back", "ILL_BE_BACK_EXIT");
                    });

                    container.AddNode("HANDOVER_COKE_WAIT", "Show me.");
                    container.AddNode("ILL_BE_BACK_EXIT", "Last step. Don't blow it.");
                }
                else if (stage >= 5)
                {
                    // Post-quest
                    container.AddNode("ENTRY", "Warehouse is open for you anytime. Now get out, match starting.", choices =>
                    {
                        choices.Add("PLEASURE", "Pleasure doing business", "EXIT");
                        choices.Add("GOOD_LUCK", "Good luck", "EXIT");
                    });

                    container.AddNode("EXIT", "*already putting headset on*");
                }
                else
                {
                    // Stage 0 / not triggered
                    container.AddNode("ENTRY", "Do I know you? No? Then get out.", choices =>
                    {
                        choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                    });

                    container.AddNode("LEAVE_EXIT", "*slams door*");
                }
            });

            Dialogue.UseContainerOnInteract("BellaGreeting");
        }

        private void SetupDialogue()
        {
            Dialogue.BuildAndSetDatabase(db =>
            {
                db.WithModuleEntry("Responses", "IGNORE", "Bella is ignoring you.");
            });

            RefreshDialogue();

            // Stage 1 → 2: First meeting, player accepts weed request
            Dialogue.OnChoiceSelected("ACCEPT_WEED", () =>
            {
                try { AdvanceStage(); }
                catch (Exception ex) { OTCLog.Error(OTCLog.Systems.NPC, $"ACCEPT_WEED callback failed: {ex.Message}"); }
            });

            // Sarcastic decline still advances (Bella doesn't care about your opinion)
            Dialogue.OnChoiceSelected("DECLINE_WEED", () =>
            {
                try { AdvanceStage(); }
                catch (Exception ex) { OTCLog.Error(OTCLog.Systems.NPC, $"DECLINE_WEED callback failed: {ex.Message}"); }
            });

            // Handovers — close dialogue and open HandoverScreen
            Dialogue.OnChoiceSelected("HANDOVER_WEED", () =>
            {
                try { OpenHandoverForDrug(EDrugType.Marijuana, Config.BellaWeedValue.Value); }
                catch (Exception ex) { OTCLog.Error(OTCLog.Systems.NPC, $"HANDOVER_WEED callback failed: {ex.Message}"); }
            });

            Dialogue.OnChoiceSelected("HANDOVER_METH", () =>
            {
                try { OpenHandoverForDrug(EDrugType.Methamphetamine, Config.BellaMethValue.Value); }
                catch (Exception ex) { OTCLog.Error(OTCLog.Systems.NPC, $"HANDOVER_METH callback failed: {ex.Message}"); }
            });

            Dialogue.OnChoiceSelected("HANDOVER_COKE", () =>
            {
                try { OpenHandoverForDrug(EDrugType.Cocaine, Config.BellaCokeValue.Value); }
                catch (Exception ex) { OTCLog.Error(OTCLog.Systems.NPC, $"HANDOVER_COKE callback failed: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Opens the HandoverScreen for the player to hand over a product.
        /// </summary>
        private void OpenHandoverForDrug(EDrugType drugType, float minPrice)
        {
            _pendingDrugType = drugType;
            _pendingMinPrice = minPrice;

            var handoverScreen = Singleton<HandoverScreen>.Instance;
            if (handoverScreen == null || _customerComponent == null)
            {
                OTCLog.Error(OTCLog.Systems.NPC, "Cannot open HandoverScreen — singleton or Customer component missing");
                return;
            }

            // Stop Bella's movement while handover is open
            try { _gameNpc?.Movement?.Stop(); }
            catch { }

            // Create callback (IL2CPP delegate pattern from DrifterManager)
            GameSystem.Action<HandoverScreen.EHandoverOutcome,
                GameSystem.Collections.Generic.List<ScheduleOne.ItemFramework.ItemInstance>,
                float> callback =
                (GameSystem.Action<HandoverScreen.EHandoverOutcome,
                    GameSystem.Collections.Generic.List<ScheduleOne.ItemFramework.ItemInstance>,
                    float>)
                new Action<HandoverScreen.EHandoverOutcome,
                    GameSystem.Collections.Generic.List<ScheduleOne.ItemFramework.ItemInstance>,
                    float>(OnHandoverClosed);

            // Success chance always 100% (we validate ourselves in the callback)
            GameSystem.Func<GameSystem.Collections.Generic.List<ScheduleOne.ItemFramework.ItemInstance>,
                float, float> successChance =
                (GameSystem.Func<GameSystem.Collections.Generic.List<ScheduleOne.ItemFramework.ItemInstance>,
                    float, float>)
                new Func<GameSystem.Collections.Generic.List<ScheduleOne.ItemFramework.ItemInstance>,
                    float, float>((items, price) => 1.0f);

            handoverScreen.Open(null, _customerComponent, HandoverScreen.EMode.Offer, callback, successChance, false);

            // End dialogue AFTER Open() — Open() adds an active UI element, so
            // DialogueCanvas.EndDialogue() sees activeUIElementCount > 0 and skips
            // the UnlockPlayer coroutine that would re-lock the mouse next frame.
            try { _gameNpc?.DialogueHandler?.EndDialogue(); }
            catch { }

            OTCLog.Msg(OTCLog.Systems.NPC, $"HandoverScreen opened for {drugType} (min ${minPrice:N0})");
        }

        /// <summary>
        /// Callback when the HandoverScreen closes.
        /// </summary>
        private void OnHandoverClosed(
            HandoverScreen.EHandoverOutcome outcome,
            GameSystem.Collections.Generic.List<ScheduleOne.ItemFramework.ItemInstance> items,
            float askingPrice)
        {
            OTCLog.Msg(OTCLog.Systems.NPC, $"Handover closed: outcome={outcome}, pendingDrug={_pendingDrugType}, pendingMinPrice=${_pendingMinPrice:F2}");

            if (outcome == HandoverScreen.EHandoverOutcome.Cancelled)
            {
                // Player cancelled — they can talk to Bella again
                return;
            }

            // Check if any handed-over item meets the requirement
            ProductItemInstance acceptedProduct = null;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] != null && IsValidMix(items[i], _pendingDrugType, _pendingMinPrice))
                    {
                        acceptedProduct = items[i].TryCast<ProductItemInstance>();
                        break;
                    }
                }
            }

            var handoverScreen = Singleton<HandoverScreen>.Instance;

            if (acceptedProduct != null)
            {
                // Consume the items
                try { handoverScreen?.ClearCustomerSlots(false); }
                catch { }

                // Advance to next stage
                AdvanceStage();

                // Bella consumes the exact product (plays animation + applies effects)
                try
                {
                    _gameNpc?.Behaviour?.ConsumeProduct(acceptedProduct);
                    OTCLog.Msg(OTCLog.Systems.NPC, $"Bella consuming product: {acceptedProduct.ID}");
                }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"ConsumeProduct failed: {ex.Message}"); }

                // Play the deal completion sound
                try
                {
                    var popup = Singleton<ScheduleOne.UI.DealCompletionPopup>.Instance;
                    if (popup?.SoundEffect != null)
                        popup.SoundEffect.Play();
                }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"Deal sound failed: {ex.Message}"); }

                // Bella reacts positively via world dialogue
                string[] successLines = _pendingDrugType switch
                {
                    EDrugType.Marijuana => new[] { $"Good quality. Next I need a meth mix worth at least ${Config.BellaMethValue.Value:N0}. Crystal, not shake." },
                    EDrugType.Methamphetamine => new[] { $"Perfect. Last thing — a cocaine mix worth at least ${Config.BellaCokeValue.Value:N0}. Premium. Then we're in business." },
                    EDrugType.Cocaine => new[] { "We're good. The warehouse is open for your people anytime. 24/7." },
                    _ => new[] { "Good." }
                };

                try { _gameNpc?.SendWorldSpaceDialogue(successLines[0], 6f); }
                catch { }

                OTCLog.Msg(OTCLog.Systems.NPC, $"Handover ACCEPTED for {_pendingDrugType}");
            }
            else
            {
                // Return items to player
                try { handoverScreen?.ClearCustomerSlots(true); }
                catch { }

                // Bella rejects
                try { _gameNpc?.SendWorldSpaceDialogue("That's not what I asked for. Try again.", 5f); }
                catch { }

                OTCLog.Msg(OTCLog.Systems.NPC, $"Handover REJECTED for {_pendingDrugType} (didn't meet requirements)");
            }
        }

        private void AdvanceStage()
        {
            if (NetworkHelper.IsHost)
            {
                BellaSaveData.Instance?.CompleteCurrentStage();
            }
            else
            {
                ConfigSyncData.SendQuestAction("BELLA_ADVANCE");
            }

            RefreshDialogue();
        }

        /// <summary>
        /// Checks if an item is a packaged product of the given drug type
        /// with MarketValue >= threshold.
        /// </summary>
        private bool IsValidMix(ScheduleOne.ItemFramework.ItemInstance item, EDrugType drugType, float minBasePrice)
        {
            try
            {
                var productItem = item.TryCast<ProductItemInstance>();
                if (productItem == null)
                    return false;

                var packaging = productItem.AppliedPackaging;
                if (packaging == null || packaging.Quantity <= 0)
                    return false;

                if (productItem.Definition == null)
                    return false;

                var productDef = productItem.Definition.TryCast<ProductDefinition>();
                if (productDef == null)
                    return false;

                return productDef.DrugType == drugType && productDef.MarketValue >= minBasePrice;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"[IsValidMix] Exception: {ex.Message}");
                return false;
            }
        }

        protected override void OnDestroyed()
        {
            if (Instance == this)
            {
                DialogueReady = false;
                Instance = null;
            }
            BellaSaveData.ResetInstance();
            base.OnDestroyed();
        }
    }
}
