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
        // Client reconstruction
        // ==================================================================

        /// <summary>
        /// Rebuilds the message thread from host state flags.
        /// Called on clients after receiving synced state.
        /// </summary>
        public void ReconstructClientThread(
            bool introCompleted, int crmTier, bool saasActive,
            bool upgradeAvailable, bool shackListed, bool shackOwned)
        {
            _messages.Clear();

            if (introCompleted)
            {
                var msg = new OtcPropertyMessage
                {
                    Id = "software_offer",
                    Sender = "static",
                    IsEmbed = true,
                    EmbedTitle = "Software Package",
                    EmbedDescription = "Encrypted comms and customer management tools.",
                    EmbedItems = new List<string>
                    {
                        $"${Config.StaticTier1BankCost.Value:N0}",
                        $"{Config.StaticTier1WeedGrams.Value}g Weed"
                    },
                    EmbedLocation = "Casino"
                };
                if (crmTier >= 1) msg.EmbedStatus = "PURCHASED";
                _messages.Add(msg);
            }

            if (shackListed)
            {
                _messages.Add(new OtcPropertyMessage
                {
                    Id = "shack_intro",
                    Sender = "static",
                    Text = "Got something for you. Property listing from one of my contacts. " +
                           "Small operation, good location. Details below."
                });

                var shackEmbed = new OtcPropertyMessage
                {
                    Id = $"{PropertySaveData.ShackId}_card",
                    Sender = "static",
                    IsEmbed = true,
                    EmbedTitle = "Westville Shack",
                    EmbedDescription = "Small dispensary, good location. Move your legal product " +
                                       "through the counter. Door locked until purchased.",
                    EmbedItems = new List<string> { $"${Config.ShackPurchasePrice.Value:N0}" },
                    EmbedLocation = "Westville",
                    EmbedImageResource = "OverTheCounter.Resources.ShackPhoto.png",
                    EmbedButtonLabel = "Purchase",
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
                        Id = $"{PropertySaveData.ShackId}_purchased",
                        Sender = "static",
                        Text = "Done. Door's unlocked, keys are yours. " +
                               "Set up shop and customers will find you."
                    });
                }
            }

            if (!saasActive && crmTier >= 1)
            {
                _messages.Add(new OtcPropertyMessage
                {
                    Id = "sub_failed",
                    Sender = "static",
                    Text = "Payment failed. Service suspended. Come see me to restore it."
                });
            }

            if (upgradeAvailable)
            {
                int targetTier = crmTier + 1;
                _messages.Add(BuildUpgradeEmbed(targetTier));
            }
        }

        internal static OtcPropertyMessage BuildUpgradeEmbed(int targetTier) =>
            targetTier == 2
                ? new OtcPropertyMessage
                {
                    Id = "upgrade_offer",
                    Sender = "static",
                    IsEmbed = true,
                    EmbedTitle = "Private Server",
                    EmbedDescription = "Full region coverage. Expanded customer network.",
                    EmbedItems = new List<string>
                    {
                        $"${Config.StaticTier2BankCost.Value:N0}",
                        $"{Config.StaticTier2MethGrams.Value}g Meth"
                    },
                    EmbedLocation = "Casino"
                }
                : new OtcPropertyMessage
                {
                    Id = "upgrade_offer",
                    Sender = "static",
                    IsEmbed = true,
                    EmbedTitle = "Enterprise Tier",
                    EmbedDescription = "GPS tracking. Maximum customer throughput.",
                    EmbedItems = new List<string>
                    {
                        $"${Config.StaticTier3BankCost.Value:N0}",
                        $"{Config.StaticTier3PremiumMethGrams.Value}g Premium Meth"
                    },
                    EmbedLocation = "Casino"
                };
    }
}
