using System;
using System.Collections.Generic;
using System.Linq;
using S1API.Internal.Abstraction;
using S1API.Saveables;

namespace OverTheCounter.SaveData
{
    /// <summary>
    /// Persists the encrypted messaging thread between Static and the player.
    /// Host saves/loads via SaveableField; clients reconstruct from synced state flags.
    /// </summary>
    public class StaticThreadSaveData : Saveable
    {
        [SaveableField("otc_static_thread")]
        private List<OtcPropertyMessage> _messages = new();

        /// <summary>Singleton instance, set during construction or load.</summary>
        public static StaticThreadSaveData Instance { get; private set; }

        internal static void ResetInstance() => Instance = null;

        public StaticThreadSaveData()
        {
            Instance = this;
        }

        protected override void OnLoaded()
        {
            Instance = this;
            ConfigSyncData.ApplyPendingGameState();
        }

        // ==================================================================
        // Thread access
        // ==================================================================

        /// <summary>Total number of messages in the thread.</summary>
        public int MessageCount => _messages.Count;

        /// <summary>Number of messages not yet marked as seen.</summary>
        public int UnseenCount => _messages.Count(m => !m.IsSeen);

        /// <summary>Returns the full message list (mutable reference for UI rendering).</summary>
        public List<OtcPropertyMessage> GetMessages() => _messages;

        /// <summary>
        /// Marks all messages as seen. Call when the player opens the thread.
        /// </summary>
        public void MarkAllSeen()
        {
            foreach (var msg in _messages)
                msg.IsSeen = true;
        }

        // ==================================================================
        // Add / Update
        // ==================================================================

        /// <summary>
        /// Adds a plain text message. Returns true if added (new).
        /// </summary>
        public bool AddMessage(string id, string text)
        {
            if (_messages.Any(m => m.Id == id)) return false;
            _messages.Add(new OtcPropertyMessage
            {
                Id = id,
                Sender = "static",
                Text = text
            });
            return true;
        }

        /// <summary>
        /// Adds or replaces a plain text message (same position if replacing).
        /// Returns true if it was new (not a replacement).
        /// </summary>
        public bool AddOrUpdateMessage(string id, string text)
        {
            var existing = _messages.FirstOrDefault(m => m.Id == id);
            if (existing != null)
            {
                existing.Text = text;
                return false;
            }
            _messages.Add(new OtcPropertyMessage
            {
                Id = id,
                Sender = "static",
                Text = text
            });
            return true;
        }

        /// <summary>
        /// Adds an embed message. Sets Sender and IsEmbed automatically.
        /// Returns true if added (new).
        /// </summary>
        public bool AddEmbed(OtcPropertyMessage msg)
        {
            if (_messages.Any(m => m.Id == msg.Id)) return false;
            msg.Sender = "static";
            msg.IsEmbed = true;
            _messages.Add(msg);
            return true;
        }

        /// <summary>
        /// Adds or replaces an embed message (same position if replacing).
        /// Returns true if it was new (not a replacement).
        /// </summary>
        public bool AddOrUpdateEmbed(OtcPropertyMessage msg)
        {
            msg.Sender = "static";
            msg.IsEmbed = true;
            var existing = _messages.FirstOrDefault(m => m.Id == msg.Id);
            if (existing != null)
            {
                int idx = _messages.IndexOf(existing);
                _messages[idx] = msg;
                return false;
            }
            _messages.Add(msg);
            return true;
        }

        /// <summary>
        /// Sets EmbedStatus on a message and clears its button.
        /// </summary>
        public void SetEmbedStatus(string id, string status)
        {
            var msg = _messages.FirstOrDefault(m => m.Id == id);
            if (msg == null) return;
            msg.EmbedStatus = status;
            msg.EmbedButtonLabel = null;
            msg.EmbedButtonAction = null;
        }

        // ==================================================================
        // Reconstruction / reconciliation
        // ==================================================================

        /// <summary>
        /// Called on the host after all save data is loaded.
        /// Rebuilds the thread from current state variables, preserving IsSeen flags.
        /// Fixes old saves where messages were missing or incomplete.
        /// </summary>
        public void ReconcileHostThread()
        {
            if (StaticSaveData.Instance == null) return;

            // Heal thread order for old saves that don't track it yet
            if (string.IsNullOrEmpty(StaticSaveData.Instance.ThreadOrder))
            {
                if (StaticSaveData.Instance.QuestTriggered)
                    StaticSaveData.Instance.ActivateThread("crm");
                if (StaticSaveData.Instance.CrmTier >= 2
                    || (StaticSaveData.Instance.UpgradeAvailable && StaticSaveData.Instance.CrmTier == 1))
                    StaticSaveData.Instance.ActivateThread("upgrade1");
                if (StaticSaveData.Instance.CrmTier >= 3
                    || (StaticSaveData.Instance.UpgradeAvailable && StaticSaveData.Instance.CrmTier == 2))
                    StaticSaveData.Instance.ActivateThread("upgrade2");
                bool shackExists = PropertySaveData.Instance?.GetProperty(PropertySaveData.ShackId) != null;
                if (shackExists)
                    StaticSaveData.Instance.ActivateThread("shack");
            }

            // Preserve which messages the host has already read
            var seenIds = new HashSet<string>(
                _messages.Where(m => m.IsSeen).Select(m => m.Id));

            bool shackListed = PropertySaveData.Instance?.GetProperty(PropertySaveData.ShackId) != null;
            bool shackOwned = PropertySaveData.Instance?.IsPropertyOwned(PropertySaveData.ShackId) ?? false;

            ReconstructClientThread(
                introCompleted: StaticSaveData.Instance.IntroCompleted,
                crmTier: StaticSaveData.Instance.CrmTier,
                saasActive: StaticSaveData.Instance.SaasActive,
                upgradeAvailable: StaticSaveData.Instance.UpgradeAvailable,
                shackListed: shackListed,
                shackOwned: shackOwned,
                questTriggered: StaticSaveData.Instance.QuestTriggered,
                tier1MoneyPaid: StaticSaveData.Instance.Tier1MoneyPaid,
                tier1ProductDelivered: StaticSaveData.Instance.Tier1ProductDelivered,
                upgradeAccepted: StaticSaveData.Instance.UpgradeAccepted,
                upgradeMoneyPaid: StaticSaveData.Instance.UpgradeMoneyPaid,
                upgradeProductDelivered: StaticSaveData.Instance.UpgradeProductDelivered,
                threadOrder: StaticSaveData.Instance.ThreadOrder);

            // Restore seen state so previously-read messages don't show as unread
            foreach (var msg in _messages)
                if (seenIds.Contains(msg.Id))
                    msg.IsSeen = true;
        }

        /// <summary>
        /// Rebuilds the message thread from host state flags.
        /// Called on clients after receiving synced state.
        /// </summary>
        public void ReconstructClientThread(
            bool introCompleted, int crmTier, bool saasActive,
            bool upgradeAvailable, bool shackListed, bool shackOwned,
            bool questTriggered = false,
            bool tier1MoneyPaid = false, bool tier1ProductDelivered = false,
            bool upgradeAccepted = false,
            bool upgradeMoneyPaid = false, bool upgradeProductDelivered = false,
            string threadOrder = "")
        {
            _messages.Clear();

            // ── CRM thread: intro + software purchase ────────────────────
            if (questTriggered)
            {
                var introCard = new OtcPropertyMessage
                {
                    Id = "intro_accept",
                    Sender = "static",
                    IsEmbed = true,
                    ThreadId = "crm",
                    EmbedTitle = "CRM Software",
                    EmbedDescription = "Encrypted comms, customer management, employee tracking.",
                    EmbedItems = new List<string>
                    {
                        "Manager Dashboard",
                        "Customer Grid",
                        "Employee Overview"
                    }
                };
                if (introCompleted)
                    introCard.EmbedStatus = "ACCEPTED";
                _messages.Add(introCard);

                if (introCompleted && crmTier < 1)
                {
                    _messages.Add(new OtcPropertyMessage
                    {
                        Id = "intro_accept_reply",
                        Sender = "player",
                        ThreadId = "crm",
                        Text = "I'm in. What do you need from me?"
                    });
                }
            }

            if (introCompleted)
            {
                var offer = new OtcPropertyMessage
                {
                    Id = "software_offer",
                    Sender = "static",
                    IsEmbed = true,
                    ThreadId = "crm",
                    EmbedTitle = "Software Package",
                    EmbedDescription = "Encrypted comms and customer management tools.",
                    EmbedLocation = "Casino"
                };

                string payItem = $"Pay ${Config.StaticTier1BankCost.Value:N0} via OTC app";
                string dropItem = $"Drop off {Config.StaticTier1WeedGrams.Value}g weed at the casino dead drop";
                if (tier1MoneyPaid || crmTier >= 1) payItem = $"<s>{payItem}</s>";
                if (tier1ProductDelivered || crmTier >= 1) dropItem = $"<s>{dropItem}</s>";
                offer.EmbedItems = new List<string> { payItem, dropItem };

                if (crmTier >= 1)
                    offer.EmbedStatus = "PURCHASED";
                else if (!tier1MoneyPaid)
                {
                    offer.EmbedButtonLabel = $"Pay ${Config.StaticTier1BankCost.Value:N0}";
                    offer.EmbedButtonAction = "purchase_tier1_money";
                }
                _messages.Add(offer);

                if (crmTier < 1)
                {
                    if (tier1MoneyPaid)
                    {
                        _messages.Add(new OtcPropertyMessage
                        {
                            Id = "tier1_money_reply", Sender = "player", ThreadId = "crm",
                            Text = "Money's wired. Where's the drop?"
                        });
                        _messages.Add(new OtcPropertyMessage
                        {
                            Id = "tier1_drop_instructions", Sender = "static", ThreadId = "crm",
                            Text = "Drop the weed at the dead drop near the casino. I'll have someone pick it up."
                        });
                    }
                }
                else
                {
                    _messages.Add(new OtcPropertyMessage
                    {
                        Id = "tier1_activated", Sender = "static", ThreadId = "crm",
                        Text = "Software's live. You're connected. Check the app - everything's unlocked."
                    });
                }
            }

            // Intro response option (belongs to CRM thread)
            if (questTriggered && !introCompleted)
            {
                _messages.Add(new OtcPropertyMessage
                {
                    Id = "response_intro",
                    Sender = "player_option",
                    ThreadId = "crm",
                    Text = "I'm in. What do you need from me?",
                    EmbedButtonLabel = "I'm In",
                    EmbedButtonAction = "accept_intro"
                });
            }

            // ── Subscription failure (no thread — inline) ────────────────
            if (!saasActive && crmTier >= 1)
            {
                _messages.Add(new OtcPropertyMessage
                {
                    Id = "sub_failed", Sender = "static",
                    Text = "Payment failed. Service suspended. Come see me to restore it."
                });
            }

            // ── Upgrade 1 thread: Private Server (tier 1→2) ─────────────
            if (crmTier >= 2)
            {
                // Completed history — embed + result only (matches CRM pattern)
                var hist1 = BuildUpgradeEmbed(2, moneyPaid: true, productDelivered: true);
                hist1.Id = "upgrade1_offer_completed";
                hist1.ThreadId = "upgrade1";
                hist1.EmbedStatus = "PURCHASED";
                _messages.Add(hist1);
                _messages.Add(new OtcPropertyMessage
                {
                    Id = "upgrade1_done", Sender = "static", ThreadId = "upgrade1",
                    Text = "Upgrade complete. Private server's online."
                });
            }
            else if (upgradeAvailable && crmTier == 1)
            {
                // Active upgrade offer for tier 2
                AddActiveUpgradeMessages("upgrade1", 2, upgradeAccepted, upgradeMoneyPaid, upgradeProductDelivered);
            }

            // ── Upgrade 2 thread: Enterprise Tier (tier 2→3) ─────────────
            if (crmTier >= 3)
            {
                // Completed history — embed + result only (matches CRM pattern)
                var hist2 = BuildUpgradeEmbed(3, moneyPaid: true, productDelivered: true);
                hist2.Id = "upgrade2_offer_completed";
                hist2.ThreadId = "upgrade2";
                hist2.EmbedStatus = "PURCHASED";
                _messages.Add(hist2);
                _messages.Add(new OtcPropertyMessage
                {
                    Id = "upgrade2_done", Sender = "static", ThreadId = "upgrade2",
                    Text = "Full scale. You've got the whole package now."
                });
            }
            else if (upgradeAvailable && crmTier == 2)
            {
                // Active upgrade offer for tier 3
                AddActiveUpgradeMessages("upgrade2", 3, upgradeAccepted, upgradeMoneyPaid, upgradeProductDelivered);
            }

            // ── Shack thread: property listing ───────────────────────────
            if (shackListed)
            {
                _messages.Add(new OtcPropertyMessage
                {
                    Id = "shack_intro", Sender = "static", ThreadId = "shack",
                    Text = "Got something for you. Property listing from one of my contacts. " +
                           "Small operation, good location. Details below."
                });

                var shackEmbed = new OtcPropertyMessage
                {
                    Id = $"{PropertySaveData.ShackId}_card",
                    Sender = "static",
                    IsEmbed = true,
                    ThreadId = "shack",
                    EmbedTitle = "Westville Shack",
                    EmbedDescription = "Small dispensary, good location. Move your legal product " +
                                       "through the counter. Door locked until purchased.",
                    EmbedItems = new List<string> { $"${Config.ShackPurchasePrice.Value:N0}" },
                    EmbedLocation = "Westville",
                    EmbedImageResource = "OverTheCounter.Resources.ShackPhoto.png",
                    EmbedButtonLabel = $"Pay ${Config.ShackPurchasePrice.Value:N0}",
                    EmbedButtonAction = "purchase_shack"
                };
                if (shackOwned)
                {
                    shackEmbed.EmbedStatus = "SOLD";
                    shackEmbed.EmbedButtonLabel = null;
                    shackEmbed.EmbedButtonAction = null;
                }
                _messages.Add(shackEmbed);

                if (shackOwned)
                {
                    _messages.Add(new OtcPropertyMessage
                    {
                        Id = $"{PropertySaveData.ShackId}_purchase_reply",
                        Sender = "player", ThreadId = "shack",
                        Text = "I'll take it."
                    });
                    _messages.Add(new OtcPropertyMessage
                    {
                        Id = $"{PropertySaveData.ShackId}_purchased",
                        Sender = "static", ThreadId = "shack",
                        Text = "Done. Door's unlocked, keys are yours. " +
                               "Set up shop and customers will find you."
                    });
                }
            }

            // ── Reorder threads by saved activation order ────────────────
            if (!string.IsNullOrEmpty(threadOrder))
            {
                var buckets = new Dictionary<string, List<OtcPropertyMessage>>();
                var inlineList = new List<OtcPropertyMessage>();
                foreach (var msg in _messages)
                {
                    if (msg.ThreadId == null)
                    {
                        inlineList.Add(msg);
                    }
                    else
                    {
                        if (!buckets.ContainsKey(msg.ThreadId))
                            buckets[msg.ThreadId] = new List<OtcPropertyMessage>();
                        buckets[msg.ThreadId].Add(msg);
                    }
                }

                var orderedIds = new List<string>();
                foreach (var id in threadOrder.Split(','))
                    if (!string.IsNullOrEmpty(id) && buckets.ContainsKey(id))
                        orderedIds.Add(id);

                // Fallback: append threads not in saved order using default ordering
                string[] defaultOrder = { "crm", "upgrade1", "upgrade2", "shack" };
                foreach (var id in defaultOrder)
                    if (buckets.ContainsKey(id) && !orderedIds.Contains(id))
                        orderedIds.Add(id);

                _messages.Clear();
                foreach (var id in orderedIds)
                {
                    _messages.AddRange(buckets[id]);
                    // Inline messages (e.g. sub_failed) go after CRM thread
                    if (id == "crm")
                        _messages.AddRange(inlineList);
                }
                // If no CRM thread, append inline messages at the end
                if (!orderedIds.Contains("crm") && inlineList.Count > 0)
                    _messages.AddRange(inlineList);
            }
        }

        /// <summary>
        /// Adds the active upgrade messages (teaser or full payment embed) for a given thread.
        /// </summary>
        private void AddActiveUpgradeMessages(string threadId, int targetTier,
            bool upgradeAccepted, bool upgradeMoneyPaid, bool upgradeProductDelivered)
        {
            string teaserTitle = targetTier == 2 ? "Private Server" : "Enterprise Tier";

            if (!upgradeAccepted)
            {
                _messages.Add(new OtcPropertyMessage
                {
                    Id = "upgrade_teaser", Sender = "static", ThreadId = threadId,
                    Text = $"I've got a {teaserTitle} package ready. Interested?"
                });
                // Response option inside the thread
                string btnText = GetAcceptanceButtonText(targetTier);
                _messages.Add(new OtcPropertyMessage
                {
                    Id = "response_upgrade",
                    Sender = "player_option",
                    ThreadId = threadId,
                    Text = btnText,
                    EmbedButtonLabel = btnText,
                    EmbedButtonAction = "accept_upgrade"
                });
            }
            else
            {
                _messages.Add(new OtcPropertyMessage
                {
                    Id = "upgrade_accepted_reply", Sender = "player", ThreadId = threadId,
                    Text = GetAcceptancePlayerText(targetTier)
                });
                _messages.Add(new OtcPropertyMessage
                {
                    Id = "upgrade_catch_reply", Sender = "static", ThreadId = threadId,
                    Text = GetAcceptanceStaticReply(targetTier)
                });

                var embed = BuildUpgradeEmbed(targetTier, upgradeMoneyPaid, upgradeProductDelivered);
                embed.ThreadId = threadId;
                _messages.Add(embed);

                if (upgradeMoneyPaid)
                {
                    _messages.Add(new OtcPropertyMessage
                    {
                        Id = "upgrade_money_reply", Sender = "player", ThreadId = threadId,
                        Text = "Payment sent. Same spot?"
                    });
                }
            }
        }

        private static string GetAcceptancePlayerText(int targetTier) =>
            targetTier == 2
                ? "Whats the catch? I already paid up!"
                : "Again? ...Fine. What do you need.";

        private static string GetAcceptanceButtonText(int targetTier) =>
            targetTier == 2
                ? "What's the catch?"
                : "Another one?";

        private static string GetAcceptanceStaticReply(int targetTier) =>
            targetTier == 2
                ? "No catch. Just business. Here's what I need."
                : "Last one. I promise. Here's the deal.";

        internal static OtcPropertyMessage BuildUpgradeEmbed(
            int targetTier,
            bool moneyPaid = false,
            bool productDelivered = false)
        {
            string title;
            string description;
            int bankCost;
            int productGrams;
            string productLabel;

            if (targetTier == 2)
            {
                title = "Private Server";
                description = "Full region coverage. Expanded customer network.";
                bankCost = (int)Config.StaticTier2BankCost.Value;
                productGrams = (int)Config.StaticTier2MethGrams.Value;
                productLabel = "meth";
            }
            else
            {
                title = "Enterprise Tier";
                description = "GPS tracking. Maximum customer throughput.";
                bankCost = (int)Config.StaticTier3BankCost.Value;
                productGrams = (int)Config.StaticTier3PremiumMethGrams.Value;
                productLabel = "premium meth";
            }

            string payItem = $"Pay ${bankCost:N0} via OTC app";
            string dropItem = $"Drop off {productGrams}g {productLabel} at the casino dead drop";
            if (moneyPaid) payItem = $"<s>{payItem}</s>";
            if (productDelivered) dropItem = $"<s>{dropItem}</s>";

            var embed = new OtcPropertyMessage
            {
                Id = "upgrade_offer",
                Sender = "static",
                IsEmbed = true,
                EmbedTitle = title,
                EmbedDescription = description,
                EmbedLocation = "Casino",
                EmbedItems = new List<string> { payItem, dropItem }
            };

            if (!moneyPaid)
            {
                embed.EmbedButtonLabel = $"Pay ${bankCost:N0}";
                embed.EmbedButtonAction = "purchase_upgrade_money";
            }

            return embed;
        }
    }
}
