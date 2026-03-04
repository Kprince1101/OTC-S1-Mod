using S1API.Entities;
using S1API.Entities.Dialogue;
using S1API.Entities.Schedule;
using S1API.Entities.Appearances.CustomizationFields;
using S1API.Entities.Appearances.FaceLayerFields;
using S1API.Entities.Appearances.BodyLayerFields;
using S1API.Entities.Appearances.AccessoryFields;
using S1API.GameTime;
using S1API.Money;
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
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC:StaticNPC");
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
                Logger.Warning($"WarpToSpawn failed: {ex.Message}");
            }
        }

        private bool CanTalkToStatic =>
            (StaticSaveData.Instance?.QuestTriggered ?? false) ||
            ScheduleOne.Money.ATM.WeeklyDepositSum >= Config.AtmDepositTrigger.Value;

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
                Logger.Error($"SetupDialogue FAILED: {ex.Message}\n{ex.StackTrace}");
            }

            try
            {
                _gameNpc.Behaviour.ConsumeProductBehaviour.onConsumeDone.AddListener(new System.Action(OnCocaineConsumed));
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to register onConsumeDone listener: {ex.Message}");
            }

            if (StaticSaveData.Instance == null)
            {
                try
                {
                    new StaticSaveData();
                    ConfigSyncData.ApplyPendingGameState();
                }
                catch (Exception ex) { Logger.Warning($"StaticSaveData fallback creation failed: {ex.Message}"); }
            }

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
            // IL2CPP: native Dialogue object may be destroyed after scene transitions while C# wrapper survives
            try { if (Dialogue.IsDialogueInProgress) return; } catch { return; }

            bool canTalk = CanTalkToStatic;
            bool introCompleted = StaticSaveData.Instance?.IntroCompleted ?? false;
            bool saasActive = StaticSaveData.Instance?.SaasActive ?? false;
            int crmTier = StaticSaveData.Instance?.CrmTier ?? 0;
            bool upgradeAvailable = StaticSaveData.Instance?.UpgradeAvailable ?? false;
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
                    // ── Intro: Focused pitch on actual features ──
                    container.AddNode("ENTRY", "*nods at your phone* \u2014 You're scaling and you don't even know it. I'm Static. I build software for operations like yours \u2014 keeps everything managed so you're not doing it in your head.", choices =>
                    {
                        choices.Add("PITCH_START", "What software?", "PITCH1");
                        choices.Add("LEAVE", "Not interested.", "LEAVE_EXIT");
                    });

                    container.AddNode("PITCH1", "One app. You see your managers \u2014 where they are, what they're running. Your employees \u2014 which property, what's in their inventory, if there's a problem. Your customers \u2014 who they are, what they buy. *taps head* Tight operation.", choices =>
                    {
                        choices.Add("PITCH1_NEXT", "What's it cost?", "COST");
                    });

                    container.AddNode("COST", $"${Config.StaticTier1BankCost.Value:N0} \u2014 bank transfer, not cash \u2014 and {Config.StaticTier1WeedGrams.Value} grams of weed. Call the weed a licensing fee. *sniffs* Bring both.", choices =>
                    {
                        choices.Add("ACCEPT", "Deal.", "ACCEPT_EXIT");
                        choices.Add("LEAVE", "I'll think about it.", "LEAVE_EXIT");
                    });

                    container.AddNode("ACCEPT_EXIT", "*taps temple* Good. Come back with it.");
                    container.AddNode("LEAVE_EXIT", "*scratches neck* You know where I am.");
                }
                else if (crmTier == 0)
                {
                    // ── Initial Purchase: bank transfer + weed ──
                    float bankBalance = Money.GetOnlineBalance();
                    int weedGrams = CountWeedInInventory();

                    if (bankBalance >= Config.StaticTier1BankCost.Value && weedGrams >= Config.StaticTier1WeedGrams.Value)
                    {
                        container.AddNode("ENTRY", $"*sniffs* You got it? ${Config.StaticTier1BankCost.Value:N0} in the bank, {Config.StaticTier1WeedGrams.Value} grams. I'm showing {weedGrams}g on you and ${bankBalance:N0} in your account. We doing this or what?", choices =>
                        {
                            choices.Add("BUY_INITIAL", $"Buy Software (${Config.StaticTier1BankCost.Value:N0} transfer + {Config.StaticTier1WeedGrams.Value}g Weed)", "BUY_INITIAL_EXIT");
                            choices.Add("LEAVE", "Not yet.", "LEAVE_EXIT");
                        });

                        container.AddNode("BUY_INITIAL_EXIT", $"*cracks knuckles* \u2014 You're live. Check the app \u2014 managers, employees, customers, all of it. Northtown and Westville covered. I'm billing ${Config.SaasWeeklyCost.Value:N0} a week from your bank. Don't let it run dry.");
                    }
                    else
                    {
                        container.AddNode("ENTRY", $"*taps foot* \u2014 I need ${Config.StaticTier1BankCost.Value:N0} in your bank and {Config.StaticTier1WeedGrams.Value} grams of weed. You're sitting on ${bankBalance:N0} and {weedGrams}g. That's not enough. Come back ready.", choices =>
                        {
                            choices.Add("LEAVE", "I'll be back.", "LEAVE_EXIT");
                        });
                    }

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
                else if (upgradeAvailable && crmTier == 1)
                {
                    // ── Tier 1→2 Upgrade: Private Server pitch with fee misdirection ──
                    float bankBalance = Money.GetOnlineBalance();
                    int methGrams = CountMethInInventory();

                    if (bankBalance >= Config.StaticTier2BankCost.Value && methGrams >= Config.StaticTier2MethGrams.Value)
                    {
                        container.AddNode("ENTRY", $"*rubs hands together* \u2014 Hey. Hey. I've been up all night. Cracked something. Fix for those dead zones.", choices =>
                        {
                            choices.Add("T2_PITCH_Q", "What fix?", "T2_PITCH");
                        });

                        container.AddNode("T2_PITCH", "Private server. Dedicated box \u2014 bypasses the firmware garbage. Every region lights up, not just the two. Plus I'm adding addiction tracking per customer. *sniffs* The whole picture.", choices =>
                        {
                            choices.Add("T2_FEE_Q", "What about the weekly fee?", "T2_FEE");
                        });

                        container.AddNode("T2_FEE", "*waves hand* \u2014 The grand a week? Yeah, that might drop off once the hardware pays for itself. No promises, but... it's on my list. Probably.", choices =>
                        {
                            choices.Add("T2_COST_Q", "What do you need?", "T2_COST");
                        });

                        container.AddNode("T2_COST", $"${Config.StaticTier2BankCost.Value:N0}, bank transfer. And {Config.StaticTier2MethGrams.Value} grams of meth \u2014 server maintenance runs hot, I need to stay sharp. You've got {methGrams}g on you and ${bankBalance:N0} in the bank.", choices =>
                        {
                            choices.Add("BUY_UPGRADE", $"Upgrade to Premium (${Config.StaticTier2BankCost.Value:N0} + {Config.StaticTier2MethGrams.Value}g Meth)", "BUY_UPGRADE_EXIT");
                            choices.Add("LEAVE", "Not yet.", "LEAVE_EXIT");
                            choices.Add("CANCEL", "Cancel Service", "CANCEL_WARN");
                        });

                        container.AddNode("BUY_UPGRADE_EXIT", "*pupils dilate* \u2014 Private server's spinning. Full coverage, addiction metrics \u2014 you're Premium. You're welcome.");
                    }
                    else
                    {
                        container.AddNode("ENTRY", $"*jittery* \u2014 Premium's unlocked. ${Config.StaticTier2BankCost.Value:N0} in the bank, {Config.StaticTier2MethGrams.Value} grams of meth. You're not there yet.", choices =>
                        {
                            choices.Add("T2_PITCH_SHORT_Q", "What's the upgrade?", "T2_PITCH_SHORT");
                            choices.Add("CANCEL", "Cancel Service", "CANCEL_WARN");
                        });

                        container.AddNode("T2_PITCH_SHORT", "Private server. Nukes the dead zones, full coverage. Fee might drop once it's paid off. *scratches jaw* ...Probably.", choices =>
                        {
                            choices.Add("LEAVE", "I'll get it.", "LEAVE_EXIT");
                        });
                    }

                    container.AddNode("CANCEL_WARN", "*stops fidgeting* \u2014 You pull the plug, everything goes dark. No refunds. No chargebacks. I don't negotiate with quitters.", choices =>
                    {
                        choices.Add("CANCEL_CONFIRM", "Cancel it.", "CANCEL_EXIT");
                        choices.Add("CANCEL_BACK", "Never mind.", "LEAVE_EXIT");
                    });
                    container.AddNode("CANCEL_EXIT", "*shrugs* \u2014 Your funeral. Service is dead. You want back in, it'll cost you. And I'll remember this.");
                    container.AddNode("LEAVE_EXIT", "*nods, twitches*");
                }
                else if (upgradeAvailable && crmTier == 2)
                {
                    // ── Tier 2→3 Upgrade: THE TRAP — fee is permanent ──
                    float bankBalance = Money.GetOnlineBalance();
                    int methGrams = CountMethInInventory(EQuality.Premium);

                    if (bankBalance >= Config.StaticTier3BankCost.Value && methGrams >= Config.StaticTier3PremiumMethGrams.Value)
                    {
                        container.AddNode("ENTRY", "*bouncing on heels* \u2014 Final tier. Enterprise. This is the big one.", choices =>
                        {
                            choices.Add("T3_PITCH_Q", "What does it do?", "T3_PITCH");
                        });

                        container.AddNode("T3_PITCH", "GPS. Every customer, pinned live on your map. You see them walking around. Route to them, hit the desperate ones first. *sniffs* Never miss a sale again.", choices =>
                        {
                            choices.Add("T3_REVEAL_Q", "What about dropping the weekly fee?", "T3_REVEAL");
                        });

                        container.AddNode("T3_REVEAL", "*stops bouncing* ...Yeah. About that. The GPS pings? They're not automated. I'm doing those by hand \u2014 triangulating towers, spoofing cell data. That's labor. My labor.", choices =>
                        {
                            choices.Add("T3_TRAP_Q", "So the fee stays?", "T3_TRAP");
                        });

                        container.AddNode("T3_TRAP", "*dead stare* \u2014 The thousand a week is permanent. Protection money. You're paying for the infrastructure and the guy running it \u2014 *points to self* \u2014 which is me. That's the deal. That was always the deal.", choices =>
                        {
                            choices.Add("T3_COST_Q", "Fine. What do you need?", "T3_COST");
                            choices.Add("T3_SCAM_Q", "That's a scam.", "T3_SCAM");
                        });

                        container.AddNode("T3_COST", $"${Config.StaticTier3BankCost.Value:N0}, bank transfer. {Config.StaticTier3PremiumMethGrams.Value} grams of premium meth \u2014 not that stepped-on garbage, the real thing. You've got {methGrams}g premium and ${bankBalance:N0} in the bank.", choices =>
                        {
                            choices.Add("BUY_UPGRADE", $"Upgrade to Enterprise (${Config.StaticTier3BankCost.Value:N0} + {Config.StaticTier3PremiumMethGrams.Value}g Premium Meth)", "BUY_UPGRADE_EXIT");
                            choices.Add("LEAVE", "Not yet.", "LEAVE_EXIT");
                        });

                        container.AddNode("T3_SCAM", "*laughs* \u2014 Scam? You've been running three tiers of my intel and your operation's still standing. Call it whatever helps you sleep. You want GPS tracking or not?", choices =>
                        {
                            choices.Add("T3_COST_Q", "Fine. What do you need?", "T3_COST");
                            choices.Add("LEAVE", "I'm out.", "LEAVE_EXIT");
                        });

                        container.AddNode("BUY_UPGRADE_EXIT", "Enterprise. Full stack \u2014 GPS, addiction data, every region, every customer. *sniffs* And yeah. The thousand a week stays. Every week. Forever. Welcome aboard.");
                    }
                    else
                    {
                        container.AddNode("ENTRY", $"*pacing* \u2014 Enterprise. ${Config.StaticTier3BankCost.Value:N0} in the bank. {Config.StaticTier3PremiumMethGrams.Value} grams premium meth. You're short.", choices =>
                        {
                            choices.Add("T3_PITCH_SHORT_Q", "Tell me about it.", "T3_PITCH_SHORT");
                            choices.Add("CANCEL", "Cancel Service", "CANCEL_WARN");
                        });

                        container.AddNode("T3_PITCH_SHORT", "GPS tracking. Every customer on the map. *pauses* ...But I'll level with you \u2014 the weekly fee? Permanent. Protection money. Non-negotiable. That's the price of the full stack.", choices =>
                        {
                            choices.Add("LEAVE", "I'll get the goods.", "LEAVE_EXIT");
                        });
                    }

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

            Dialogue.OnChoiceSelected("ACCEPT", () =>
            {
                try
                {
                    if (NetworkHelper.IsHost)
                        StaticSaveData.Instance?.OnIntroCompleted();
                    else
                        ConfigSyncData.SendQuestAction("STATIC_INTRO_COMPLETED");

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
                    if (Money.GetOnlineBalance() < Config.StaticTier1BankCost.Value || CountWeedInInventory() < Config.StaticTier1WeedGrams.Value)
                        return;

                    // Local player effects — always execute (it's this player's money/inventory).
                    Money.CreateOnlineTransaction("OTC License", -Config.StaticTier1BankCost.Value, 1f, "Static Services");
                    RemoveWeedFromInventory(Config.StaticTier1WeedGrams.Value);

                    if (NetworkHelper.IsHost)
                        StaticSaveData.Instance?.PurchaseInitial();
                    else
                        ConfigSyncData.SendQuestAction("STATIC_PURCHASE_INITIAL");

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
                        if (Money.GetOnlineBalance() < Config.StaticTier2BankCost.Value || CountMethInInventory() < Config.StaticTier2MethGrams.Value)
                            return;

                        Money.CreateOnlineTransaction("OTC Premium", -Config.StaticTier2BankCost.Value, 1f, "Static Services");
                        RemoveMethFromInventory(Config.StaticTier2MethGrams.Value);
                    }
                    else if (tier == 2)
                    {
                        if (Money.GetOnlineBalance() < Config.StaticTier3BankCost.Value || CountMethInInventory(EQuality.Premium) < Config.StaticTier3PremiumMethGrams.Value)
                            return;

                        Money.CreateOnlineTransaction("OTC Enterprise", -Config.StaticTier3BankCost.Value, 1f, "Static Services");
                        RemoveMethFromInventory(Config.StaticTier3PremiumMethGrams.Value, EQuality.Premium);
                    }
                    else
                    {
                        return;
                    }

                    if (NetworkHelper.IsHost)
                        StaticSaveData.Instance?.PurchaseUpgrade();
                    else
                        ConfigSyncData.SendQuestAction("STATIC_PURCHASE_UPGRADE");

                    TriggerCocaineConsumption();
                    RefreshDialogue();
                }
                catch (Exception ex)
                {
                    Logger.Error($"BUY_UPGRADE callback failed: {ex.Message}");
                }
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
                    Logger.Error($"RESTORE_FINAL callback failed: {ex.Message}");
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
                    Logger.Warning("No cocaine definition found for consumption");
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

        private bool IsPackagedMeth(ScheduleOne.ItemFramework.ItemSlot slot, out int packagingQuantity, EQuality minQuality = EQuality.Trash)
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
                Logger.Warning($"CountWeedInInventory failed: {ex.Message}");
            }
            return totalGrams;
        }

        private int CountMethInInventory(EQuality minQuality = EQuality.Trash)
        {
            int totalGrams = 0;
            try
            {
                var playerInv = PlayerSingleton<ScheduleOne.PlayerScripts.PlayerInventory>.Instance;
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
                var playerInv = PlayerSingleton<ScheduleOne.PlayerScripts.PlayerInventory>.Instance;
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
            if (Instance == this)
            {
                DialogueReady = false;
                Instance = null;
            }
            StaticSaveData.ResetInstance();
            base.OnDestroyed();
        }
    }
}
