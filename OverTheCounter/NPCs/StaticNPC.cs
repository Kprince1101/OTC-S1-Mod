using S1API.Entities;
using S1API.Entities.Dialogue;
using S1API.Entities.Schedule;
using S1API.Entities.Appearances.CustomizationFields;
using S1API.Entities.Appearances.FaceLayerFields;
using S1API.Entities.Appearances.BodyLayerFields;
using S1API.Entities.Appearances.AccessoryFields;
using S1API.GameTime;
using S1API.Money;
using Il2CppScheduleOne.VoiceOver;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Product;
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
        private static CocaineDefinition _cachedCocaineDef;

        public static StaticNPC Instance { get; private set; }

        public bool DialogueReady { get; private set; }

        public override bool IsPhysical => true;

        private bool CanTalkToStatic =>
            (StaticSaveData.Instance?.QuestTriggered ?? false) ||
            Il2CppScheduleOne.Money.ATM.WeeklyDepositSum >= 5000f;

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

            try
            {
                _gameNpc.Behaviour.ConsumeProductBehaviour.onConsumeDone.AddListener(new System.Action(OnCocaineConsumed));
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to register onConsumeDone listener: {ex.Message}");
            }

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
            bool saasActive = StaticSaveData.Instance?.SaasActive ?? false;
            int crmTier = StaticSaveData.Instance?.CrmTier ?? 0;
            bool upgradeAvailable = StaticSaveData.Instance?.UpgradeAvailable ?? false;

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
                else if (crmTier == 0)
                {
                    // Post-intro: waiting for initial purchase ($3000 + 20g weed)
                    float cash = Money.GetCashBalance();
                    int weedGrams = CountWeedInInventory();

                    if (cash >= 3000f && weedGrams >= 20)
                    {
                        container.AddNode("ENTRY", $"You got the package? $3,000 and 20 grams of weed. You're holding {weedGrams} grams and ${cash:N0} cash.", choices =>
                        {
                            choices.Add("BUY_INITIAL", "Buy Software ($3,000 + 20 grams Weed)", "BUY_INITIAL_EXIT");
                            choices.Add("LEAVE", "Not yet.", "LEAVE_EXIT");
                        });

                        container.AddNode("BUY_INITIAL_EXIT", "Package deployed. Your service is live. I'll bill you weekly \u2014 $1,000 from your bank account. Don't let it run dry.");
                    }
                    else
                    {
                        container.AddNode("ENTRY", $"You've only got ${cash:N0} and {weedGrams} grams of weed. I need $3,000 and 20 grams. Come back when you have the full amounts.", choices =>
                        {
                            choices.Add("LEAVE", "I'll be back.", "LEAVE_EXIT");
                        });
                    }

                    container.AddNode("LEAVE_EXIT", "Don't keep me waiting.");
                }
                else if (!saasActive)
                {
                    // Subscription suspended
                    container.AddNode("ENTRY", "Service is down. You missed a payment. Want to get back online?", choices =>
                    {
                        choices.Add("REACTIVATE", "Pay Back-Rent ($1,000)", "REACTIVATE_EXIT");
                        choices.Add("LEAVE", "Not now.", "LEAVE_EXIT");
                    });

                    container.AddNode("REACTIVATE_EXIT", "Good. We're back in business.");
                    container.AddNode("LEAVE_EXIT", "Your call. But the clock's ticking.");
                }
                else if (upgradeAvailable && crmTier == 1)
                {
                    // Upgrade 1 available ($6000 + 5g meth)
                    float cash = Money.GetCashBalance();
                    int methGrams = CountMethInInventory();

                    if (cash >= 6000f && methGrams >= 5)
                    {
                        container.AddNode("ENTRY", $"Premium tier's unlocked. I texted you what I need. You're holding {methGrams} grams meth and ${cash:N0} cash. We good?", choices =>
                        {
                            choices.Add("BUY_UPGRADE", "Upgrade to Premium ($6,000 + 5 grams Meth)", "BUY_UPGRADE_EXIT");
                            choices.Add("CANCEL", "Cancel Service", "CANCEL_WARN");
                            choices.Add("LEAVE", "Not yet.", "LEAVE_EXIT");
                        });

                        container.AddNode("BUY_UPGRADE_EXIT", "Premium's live. Faster throughput, deeper access. You're moving up.");
                    }
                    else
                    {
                        container.AddNode("ENTRY", $"Premium tier's ready. You've only got ${cash:N0} and {methGrams} grams of meth. I need $6,000 and 5 grams. Come back when you have the full amounts.", choices =>
                        {
                            choices.Add("CANCEL", "Cancel Service", "CANCEL_WARN");
                            choices.Add("LEAVE", "I'll get it.", "LEAVE_EXIT");
                        });
                    }

                    container.AddNode("CANCEL_WARN", "You sure? You pull the plug, you lose access. No refunds. I don't do chargebacks.", choices =>
                    {
                        choices.Add("CANCEL_CONFIRM", "Cancel it.", "CANCEL_EXIT");
                        choices.Add("CANCEL_BACK", "Never mind.", "LEAVE_EXIT");
                    });
                    container.AddNode("CANCEL_EXIT", "Your loss. Service is dead. You want back in, it's gonna cost you.");
                    container.AddNode("LEAVE_EXIT", "Opportunity doesn't wait forever.");
                }
                else if (upgradeAvailable && crmTier == 2)
                {
                    // Upgrade 2 available ($12000 + 10g high-quality meth)
                    float cash = Money.GetCashBalance();
                    int methGrams = CountMethInInventory(EQuality.Premium);

                    if (cash >= 12000f && methGrams >= 10)
                    {
                        container.AddNode("ENTRY", $"Enterprise tier. The final level. I texted you the cost. You're holding {methGrams} grams of premium meth and ${cash:N0} cash. Ready to go full scale?", choices =>
                        {
                            choices.Add("BUY_UPGRADE", "Upgrade to Enterprise ($12,000 + 10 grams Premium Meth)", "BUY_UPGRADE_EXIT");
                            choices.Add("CANCEL", "Cancel Service", "CANCEL_WARN");
                            choices.Add("LEAVE", "Not yet.", "LEAVE_EXIT");
                        });

                        container.AddNode("BUY_UPGRADE_EXIT", "Enterprise grade. Full scale. You're running the whole stack now.");
                    }
                    else
                    {
                        container.AddNode("ENTRY", $"Final tier's unlocked. You've only got ${cash:N0} and {methGrams} grams of premium meth. I need $12,000 and 10 grams of premium or better. Come back when you have the full amounts.", choices =>
                        {
                            choices.Add("CANCEL", "Cancel Service", "CANCEL_WARN");
                            choices.Add("LEAVE", "I'll get it.", "LEAVE_EXIT");
                        });
                    }

                    container.AddNode("CANCEL_WARN", "You sure? You pull the plug, you lose access. No refunds. I don't do chargebacks.", choices =>
                    {
                        choices.Add("CANCEL_CONFIRM", "Cancel it.", "CANCEL_EXIT");
                        choices.Add("CANCEL_BACK", "Never mind.", "LEAVE_EXIT");
                    });
                    container.AddNode("CANCEL_EXIT", "Your loss. Service is dead. You want back in, it's gonna cost you.");
                    container.AddNode("LEAVE_EXIT", "Last chance to go full scale. Don't sit on it.");
                }
                else
                {
                    // Active, no upgrade pending
                    int daysLeft = (StaticSaveData.Instance?.SaasNextPaymentDay ?? 0) - TimeManager.ElapsedDays;
                    if (daysLeft < 0) daysLeft = 0;

                    string tierLabel = crmTier == 3 ? "Enterprise" : crmTier == 2 ? "Premium" : "Standard";
                    container.AddNode("ENTRY", $"Service is running \u2014 {tierLabel} tier. Next payment in {daysLeft} days. Stay sharp.", choices =>
                    {
                        choices.Add("CANCEL", "Cancel Service", "CANCEL_WARN");
                        choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                    });

                    container.AddNode("CANCEL_WARN", "You sure? You pull the plug, you lose access. No refunds. I don't do chargebacks.", choices =>
                    {
                        choices.Add("CANCEL_CONFIRM", "Cancel it.", "CANCEL_EXIT");
                        choices.Add("CANCEL_BACK", "Never mind.", "LEAVE_EXIT");
                    });
                    container.AddNode("CANCEL_EXIT", "Your loss. Service is dead. You want back in, it's gonna cost you.");
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

            Dialogue.OnChoiceSelected("BUY_INITIAL", () =>
            {
                try
                {
                    if (Money.GetCashBalance() < 3000f || CountWeedInInventory() < 20)
                        return;

                    Money.ChangeCashBalance(-3000f, true, true);
                    RemoveWeedFromInventory(20);
                    StaticSaveData.Instance?.PurchaseInitial();
                    TriggerCocaineConsumption();
                    RefreshDialogue();
                }
                catch (Exception ex)
                {
                    Logger.Error($"BUY_INITIAL callback failed: {ex.Message}");
                }
            });

            Dialogue.OnChoiceSelected("BUY_UPGRADE", () =>
            {
                try
                {
                    int tier = StaticSaveData.Instance?.CrmTier ?? 0;

                    if (tier == 1)
                    {
                        if (Money.GetCashBalance() < 6000f || CountMethInInventory() < 5)
                            return;

                        Money.ChangeCashBalance(-6000f, true, true);
                        RemoveMethFromInventory(5);
                    }
                    else if (tier == 2)
                    {
                        if (Money.GetCashBalance() < 12000f || CountMethInInventory(EQuality.Premium) < 10)
                            return;

                        Money.ChangeCashBalance(-12000f, true, true);
                        RemoveMethFromInventory(10, EQuality.Premium);
                    }
                    else
                    {
                        return;
                    }

                    StaticSaveData.Instance?.PurchaseUpgrade();
                    TriggerCocaineConsumption();
                    RefreshDialogue();
                }
                catch (Exception ex)
                {
                    Logger.Error($"BUY_UPGRADE callback failed: {ex.Message}");
                }
            });

            Dialogue.OnChoiceSelected("REACTIVATE", () =>
            {
                try
                {
                    StaticSaveData.Instance?.ReactivateSubscription();
                    RefreshDialogue();
                }
                catch (Exception ex)
                {
                    Logger.Error($"REACTIVATE callback failed: {ex.Message}");
                }
            });

            Dialogue.OnChoiceSelected("CANCEL_CONFIRM", () =>
            {
                try
                {
                    StaticSaveData.Instance?.CancelSubscription();
                    RefreshDialogue();
                }
                catch (Exception ex)
                {
                    Logger.Error($"CANCEL_CONFIRM callback failed: {ex.Message}");
                }
            });

            Dialogue.OnChoiceSelected("CANCEL_BACK", () =>
            {
                PlayDismissalSound();
            });
        }

        // ── Cocaine consumption ──────────────────────────────────────

        public void TriggerCocaineConsumption()
        {
            try
            {
                if (_gameNpc?.Behaviour == null) return;

                if (_cachedCocaineDef == null)
                {
                    var pm = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.Product.ProductManager>();
                    if (pm?.AllProducts != null)
                    {
                        for (int i = 0; i < pm.AllProducts.Count; i++)
                        {
                            var cocaine = pm.AllProducts[i]?.TryCast<CocaineDefinition>();
                            if (cocaine != null)
                            {
                                _cachedCocaineDef = cocaine;
                                break;
                            }
                        }
                    }
                }

                if (_cachedCocaineDef == null)
                {
                    Logger.Warning("No cocaine definition found for consumption");
                    return;
                }

                var productItem = new ProductItemInstance(
                    (Il2CppScheduleOne.ItemFramework.ItemDefinition)(object)_cachedCocaineDef,
                    1,
                    EQuality.Standard,
                    (Il2CppScheduleOne.Product.Packaging.PackagingDefinition)null
                );

                _gameNpc.Behaviour.ConsumeProduct(productItem);
            }
            catch (Exception ex)
            {
                Logger.Error($"TriggerCocaineConsumption failed: {ex.Message}");
            }
        }

        private void OnCocaineConsumed()
        {
            try
            {
                if (_gameNpc?.Avatar == null) return;

                _gameNpc.Avatar.Eyes?.SetEyeballTint(new Color(0.78f, 0.94f, 1f, 1f), false);
                _gameNpc.Avatar.Eyes?.SetPupilDilation(1f, false);
                _gameNpc.Avatar.Eyes?.ForceBlink();
                _gameNpc.Avatar.LookController.LookLerpSpeed = 10f;
            }
            catch (Exception ex)
            {
                Logger.Warning($"OnCocaineConsumed failed: {ex.Message}");
            }
        }

        // ── Inventory helpers ──────────────────────────────────────────

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

                if (productItem.Definition == null) return false;
                if (productItem.Definition.TryCast<WeedDefinition>() == null) return false;

                packagingQuantity = packaging.Quantity;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsPackagedMeth(Il2CppScheduleOne.ItemFramework.ItemSlot slot, out int packagingQuantity, EQuality minQuality = EQuality.Trash)
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

                if (productItem.Definition == null) return false;
                if (productItem.Definition.TryCast<MethDefinition>() == null) return false;
                if (productItem.Quality < minQuality) return false;

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

        private int CountMethInInventory(EQuality minQuality = EQuality.Trash)
        {
            int totalGrams = 0;
            try
            {
                var playerInv = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance;
                if (playerInv?.hotbarSlots == null) return 0;

                for (int i = 0; i < playerInv.hotbarSlots.Count; i++)
                {
                    var slot = playerInv.hotbarSlots[i];
                    if (!IsPackagedMeth(slot, out int multiplier, minQuality)) continue;
                    totalGrams += slot.Quantity * multiplier;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"CountMethInInventory failed: {ex.Message}");
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

        private bool RemoveMethFromInventory(int grams, EQuality minQuality = EQuality.Trash)
        {
            int remaining = grams;
            try
            {
                var playerInv = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance;
                if (playerInv?.hotbarSlots == null) return false;

                for (int i = 0; i < playerInv.hotbarSlots.Count && remaining > 0; i++)
                {
                    var slot = playerInv.hotbarSlots[i];
                    if (!IsPackagedMeth(slot, out int multiplier, minQuality)) continue;

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
                Logger.Error($"RemoveMethFromInventory failed: {ex.Message}");
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
