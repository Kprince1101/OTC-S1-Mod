using S1API.Entities;
using S1API.Entities.Dialogue;
using S1API.Entities.Schedule;
using S1API.Entities.Appearances.CustomizationFields;
using S1API.Entities.Appearances.FaceLayerFields;
using S1API.Entities.Appearances.BodyLayerFields;
using S1API.Entities.Appearances.AccessoryFields;
using S1API.GameTime;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using UnityEngine;
using UnityEngine.AI;
using MelonLoader;
using System;

#if IL2CPP
using Il2CppScheduleOne.VoiceOver;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Product;
#else
using ScheduleOne.VoiceOver;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Product;
#endif

namespace OverTheCounter.NPCs
{
    public sealed class StaticNPC : NPC
    {
        private static readonly EVOLineType[] DismissalSounds = { EVOLineType.Angry, EVOLineType.Annoyed, EVOLineType.No };

        private ScheduleOne.NPCs.NPC _gameNpc;
        private static CocaineDefinition _cachedCocaineDef;

        public static StaticNPC Instance { get; private set; }

        /// <summary>True once SetupDialogue() has built the initial container.</summary>
        public bool DialogueReady { get; private set; }

        /// <summary>True while the player is in an active dialogue with Static.</summary>
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

        private static readonly Vector3 SpawnPosition = new Vector3(13.72f, 5.16f, 95.96f);
        private static readonly Quaternion SpawnRotation = Quaternion.Euler(0.0f, 270.0f, 0.0f);

        /// <summary>
        /// Warps Static to the spawn position on the host.
        /// Movement.Warp() snaps to the NavMesh surface (correct Y) and
        /// sends a FishNet RPC that syncs the position to all clients.
        /// Must only be called on the host.
        /// </summary>
        public void WarpToSpawn()
        {
            try
            {
                // Movement may be null during load (OnSleepEnd fires before NPC is fully initialized)
                if (Movement == null) return;

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

        private bool CanTalkToStatic =>
            StaticSaveData.Instance?.QuestTriggered ?? false;

        protected override void ConfigurePrefab(NPCPrefabBuilder builder)
        {
            builder
                .WithIdentity("static_casino_fixer", "Static", "")
                .WithSpawnPosition(SpawnPosition, SpawnRotation)
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
                });

            // NOTE: .WithSchedule(plan => plan.WalkTo(SpawnPosition, ...)) used to be
            // here, but on the current game version S1API.Entities.NPCPrefabBuilder
            // .WithSchedule() -> PrecreateActionsForSpecs() unconditionally throws
            // TypeLoadException on 'Il2CppScheduleOne.NPCs.Schedules.NPCSignal_WaitForDelivery'
            // (a type that no longer exists), regardless of what's actually in the
            // spec list -- this crashed schedule registration for Static (and VicNPC,
            // same symptom) and left their prefab in a state where the game later
            // fails to instantiate them at all ("Failed to instantiate custom NPC type
            // ... with default data"). It's not a Harmony patch, so there's no
            // Unpatch-based fix -- S1API just calls into its own broken code directly.
            //
            // BellaNPC already established the working pattern: skip .WithSchedule()
            // entirely and rely on WithSpawnPosition() + WarpToSpawn() (below) to place
            // the NPC. Static doesn't patrol -- his old "schedule" was just WalkTo his
            // own spawn point on a loop, which WithSpawnPosition + WarpToSpawn already cover.
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
                OTCLog.Error(OTCLog.Systems.NPC, $"Failed to set Static's appearance: {ex.Message}");
                Appearance.GenerateRandomAppearance();
                Appearance.Build();
            }

            EnsureVoiceDatabase();

            // Pathfinding placement runs on host only. The host's Movement.Warp()
            // sends a FishNet RPC to sync position to clients. WarpToSpawn()
            // pre-snaps Y via NavMesh.SamplePosition so the RPC sends correct
            // ground-level coordinates regardless of client NavMeshAgent state.
            //
            // Schedule.Enable()/EnforceState() used to run here too, but there's no
            // schedule plan configured anymore (see ConfigurePrefab) -- enabling an
            // unconfigured schedule isn't something BellaNPC (the working precedent
            // for schedule-less custom NPCs) does either, so we don't call it.

            try
            {
                SetupDialogue();
                DialogueReady = true;
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"SetupDialogue FAILED: {ex.Message}\n{ex.StackTrace}");
            }

            try
            {
                _gameNpc.Behaviour.ConsumeProductBehaviour.onConsumeDone.AddListener(new System.Action(OnCocaineConsumed));
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"Failed to register onConsumeDone listener: {ex.Message}");
            }

            if (StaticSaveData.Instance == null)
            {
                try
                {
                    new StaticSaveData();
                    ConfigSyncData.ApplyPendingGameState();
                }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.NPC, $"StaticSaveData fallback creation failed: {ex.Message}"); }
            }

            if (StaticThreadSaveData.Instance == null)
            {
                try { new StaticThreadSaveData(); }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.Quest, $"StaticThreadSaveData fallback creation failed: {ex.Message}"); }
            }

            if (PropertySaveData.Instance == null)
            {
                try { new PropertySaveData(); }
                catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.Quest, $"PropertySaveData fallback creation failed: {ex.Message}"); }
            }

            // Host: reconcile thread from current state variables.
            // Fixes old saves with missing/incomplete messages without losing IsSeen state.
            if (NetworkHelper.IsHost)
                StaticThreadSaveData.Instance?.ReconcileHostThread();

            // Apply pending state AFTER all save data instances exist,
            // so ReconstructClientThread can find StaticThreadSaveData.Instance
            ConfigSyncData.ApplyPendingGameState();

            StaticSaveData.Instance?.OnStaticSpawned();
        }

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

        public void RefreshDialogue()
        {
            // IL2CPP: native Dialogue object may be destroyed after scene transitions while C# wrapper survives
            try { if (Dialogue.IsDialogueInProgress) return; } catch { return; }

            bool canTalk = CanTalkToStatic;
            bool introCompleted = StaticSaveData.Instance?.IntroCompleted ?? false;
            bool saasActive = StaticSaveData.Instance?.SaasActive ?? false;
            int crmTier = StaticSaveData.Instance?.CrmTier ?? 0;
            bool earlyVisitSeen = StaticSaveData.Instance?.EarlyVisitSeen ?? true;
            int currentTime;
            try { currentTime = TimeManager.CurrentTime; }
            catch { currentTime = 0; }

            Dialogue.BuildAndRegisterContainer("StaticGreeting", container =>
            {
                if (!canTalk)
                {
                    container.AddNode("ENTRY", "You lost? I don't talk to tourists. Come back when you're moving real volume.", choices =>
                    {
                        choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                    });

                    container.AddNode("LEAVE_EXIT", "*turns away*");
                }
                else if (!earlyVisitSeen && currentTime >= 700 && currentTime < 1600)
                {
                    // ── Early Visit: player glitched into casino before 4pm ──
                    // Mark seen immediately so it never triggers again.
                    StaticSaveData.Instance?.OnEarlyVisitSeen();

                    container.AddNode("ENTRY", "*jumps back* \u2014 What the \u2014 how did you get in here? Doors don't unlock until four. *stares at door* ...Did you clip through? You actually noclipped through the collision mesh?", choices =>
                    {
                        choices.Add("EARLY_MAYBE", "Maybe.", "EARLY_REACT");
                        choices.Add("EARLY_DENY", "The door was open.", "EARLY_DENY_RESP");
                    });

                    container.AddNode("EARLY_REACT", "*rubs eyes* \u2014 That's not \u2014 the navmesh boundary is hardcoded. The trigger volume should've \u2014 *sniffs* ...you know what, I'm not even mad. That's a client-side exploit. Respect. But come back after four. I'm still compiling.");
                    container.AddNode("EARLY_DENY_RESP", "*squints* \u2014 No it wasn't. I wrote the access scheduler myself. Cron job fires at 1600 sharp. *scratches neck* ...Unless someone's injecting frames at the seam. Huh. Come back after four \u2014 I need to patch this.");
                }
                else if (!introCompleted)
                {
                    // ── Intro: redirect to OTC app ──
                    container.AddNode("ENTRY", "*nods at your phone* \u2014 Check the OTC app. Everything's in there. I don't do sales pitches in person anymore.", choices =>
                    {
                        choices.Add("LEAVE", "Got it.", "LEAVE_EXIT");
                    });

                    container.AddNode("LEAVE_EXIT", "*scratches neck* You know where I am.");
                }
                else if (crmTier == 0)
                {
                    // ── Awaiting purchase: redirect to app + dead drop ──
                    container.AddNode("ENTRY", "*taps foot* \u2014 Pay through the app. Drop the product at the dead drop near me. I don't handle goods in person.", choices =>
                    {
                        choices.Add("LEAVE", "I'll handle it.", "LEAVE_EXIT");
                    });

                    container.AddNode("LEAVE_EXIT", "*waves dismissively* \u2014 Tick tock.");
                }
                else if (!saasActive)
                {
                    // ── Suspended: Terms + App Question restore flow ──
                    container.AddNode("ENTRY", "*flat tone* \u2014 Service is dead. You let the payment lapse. I don't run a charity.", choices =>
                    {
                        choices.Add("RESTORE_FINAL", $"Restore Service (${Config.SaasWeeklyCost.Value:N0})", "RESTORE_TERMS");
                        choices.Add("LEAVE", "Not now.", "LEAVE_EXIT");
                    });

                    container.AddNode("RESTORE_TERMS", "*pulls out crumpled paper* \u2014 New terms before I flip anything. Section 14-B: all outbound data routes through my relay node. Section 22: no third-party scraping of the feed. And Section 9-F: you waive all dispute rights on billing cycles. *sniffs* Standard stuff.", choices =>
                    {
                        choices.Add("RESTORE_ACCEPT", "Fine. Whatever. Turn it on.", "RESTORE_APP_Q");
                        choices.Add("RESTORE_PUSH", "That's ridiculous.", "RESTORE_PRESSURE");
                    });

                    container.AddNode("RESTORE_PRESSURE", "*leans forward* \u2014 Ridiculous? You know what's ridiculous? Your customers are out there right now buying from someone else. Take the terms or walk.", choices =>
                    {
                        choices.Add("RESTORE_ACCEPT", "Fine.", "RESTORE_APP_Q");
                    });

                    container.AddNode("RESTORE_APP_Q", "*typing on phone* \u2014 Processing...", choices =>
                    {
                        choices.Add("APP_WHY", "Why can't I just do this in the app?", "APP_REASON");
                        choices.Add("RESTORE_FINAL", "Whatever. Just do it.", "RESTORE_EXIT");
                    });

                    container.AddNode("APP_REASON", "*sighs heavily* \u2014 The app runs on client-side rendering. Reactivation needs a kernel-level handshake with my relay server \u2014 can't initiate that from a sandboxed UI thread. Has to be done at the hardware layer. In person. *taps phone* It's an architecture thing.", choices =>
                    {
                        choices.Add("APP_DEFLECT_Q", "That makes no sense.", "APP_DEFLECT");
                        choices.Add("RESTORE_FINAL", "Whatever. Just do it.", "RESTORE_EXIT");
                    });

                    container.AddNode("APP_DEFLECT", "*sniffs* \u2014 Makes perfect sense if you understood mesh networking. Which you don't. Are we done here?", choices =>
                    {
                        choices.Add("RESTORE_FINAL", "Just turn it on.", "RESTORE_EXIT");
                    });

                    container.AddNode("RESTORE_EXIT", "*nods once* \u2014 You're back online. Don't let the balance dry up again. I won't be this nice next time.");
                    container.AddNode("LEAVE_EXIT", "*waves dismissively* \u2014 Tick tock.");
                }
                else if (StaticSaveData.Instance?.UpgradeAvailable == true)
                {
                    // ── Upgrade available: redirect to app + cancel option ──
                    container.AddNode("ENTRY", "*jittery* \u2014 Upgrade's in the app. Pay through OTC, drop the goods at the dead drop. I don't handle product anymore.", choices =>
                    {
                        choices.Add("CANCEL", "Cancel Service", "CANCEL_WARN");
                        choices.Add("LEAVE", "Got it.", "LEAVE_EXIT");
                    });

                    container.AddNode("CANCEL_WARN", "*stops fidgeting* \u2014 You pull the plug, everything goes dark. No refunds. No chargebacks. I don't negotiate with quitters.", choices =>
                    {
                        choices.Add("CANCEL_CONFIRM", "Cancel it.", "CANCEL_EXIT");
                        choices.Add("CANCEL_BACK", "Never mind.", "LEAVE_EXIT");
                    });
                    container.AddNode("CANCEL_EXIT", "*shrugs* \u2014 Your funeral. Service is dead. You want back in, it'll cost you. And I'll remember this.");
                    container.AddNode("LEAVE_EXIT", "*nods, twitches*");
                }
                else
                {
                    // ── Active, no upgrade pending ──
                    int daysLeft = (StaticSaveData.Instance?.SaasNextPaymentDay ?? 0) - (StaticSaveData.Instance?.DayPassCount ?? 0);
                    if (daysLeft < 0) daysLeft = 0;

                    string tierLabel = crmTier == 3 ? "Enterprise" : crmTier == 2 ? "Premium" : "Standard";
                    container.AddNode("ENTRY", $"*fidgeting* \u2014 Service is running. {tierLabel} tier. Next bill in {daysLeft} days. Don't overthink it.", choices =>
                    {
                        choices.Add("CANCEL", "Cancel Service", "CANCEL_WARN");
                        choices.Add("LEAVE", "Leave", "LEAVE_EXIT");
                    });

                    container.AddNode("CANCEL_WARN", "*stops fidgeting* \u2014 You pull the plug, everything goes dark. No refunds. No chargebacks. I don't negotiate with quitters.", choices =>
                    {
                        choices.Add("CANCEL_CONFIRM", "Cancel it.", "CANCEL_EXIT");
                        choices.Add("CANCEL_BACK", "Never mind.", "LEAVE_EXIT");
                    });
                    container.AddNode("CANCEL_EXIT", "*shrugs* \u2014 Your funeral. Service is dead. You want back in, it'll cost you. And I'll remember this.");
                    container.AddNode("LEAVE_EXIT", "*nods, twitches*");
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

            Dialogue.OnChoiceSelected("LEAVE", () =>
            {
                PlayDismissalSound();
            });

            Dialogue.OnChoiceSelected("EARLY_MAYBE", () =>
            {
                StaticSaveData.Instance?.OnEarlyVisitSeen();
            });

            Dialogue.OnChoiceSelected("EARLY_DENY", () =>
            {
                StaticSaveData.Instance?.OnEarlyVisitSeen();
            });

            Dialogue.OnChoiceSelected("RESTORE_FINAL", () =>
            {
                try
                {
                    if (NetworkHelper.IsHost)
                        StaticSaveData.Instance?.ReactivateSubscription();
                    else
                        ConfigSyncData.SendQuestAction("STATIC_REACTIVATE");

                    TriggerCocaineConsumption();
                    RefreshDialogue();
                }
                catch (Exception ex)
                {
                    OTCLog.Error(OTCLog.Systems.NPC, $"RESTORE_FINAL callback failed: {ex.Message}");
                }
            });

            Dialogue.OnChoiceSelected("CANCEL_CONFIRM", () =>
            {
                try
                {
                    if (NetworkHelper.IsHost)
                        StaticSaveData.Instance?.CancelSubscription();
                    else
                        ConfigSyncData.SendQuestAction("STATIC_CANCEL");

                    TriggerCocaineConsumption();
                    RefreshDialogue();
                }
                catch (Exception ex)
                {
                    OTCLog.Error(OTCLog.Systems.NPC, $"CANCEL_CONFIRM callback failed: {ex.Message}");
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
                    var pm = UnityEngine.Object.FindObjectOfType<ScheduleOne.Product.ProductManager>();
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
                    OTCLog.Warning(OTCLog.Systems.NPC, "No cocaine definition found for consumption");
                    return;
                }

                var productItem = new ProductItemInstance(
                    (ScheduleOne.ItemFramework.ItemDefinition)(object)_cachedCocaineDef,
                    1,
                    EQuality.Standard,
                    (ScheduleOne.Product.Packaging.PackagingDefinition)null
                );

                _gameNpc.Behaviour.ConsumeProduct(productItem);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.NPC, $"TriggerCocaineConsumption failed: {ex.Message}");
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
                OTCLog.Warning(OTCLog.Systems.NPC, $"OnCocaineConsumed failed: {ex.Message}");
            }
        }

        protected override void OnDestroyed()
        {
            if (Instance == this)
            {
                DialogueReady = false;
                Instance = null;
            }
            base.OnDestroyed();
        }
    }
}
