using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using OverTheCounter.Utilities;
using S1API.Quests;

#if IL2CPP
using S1Quest = Il2CppScheduleOne.Quests.Quest;
using S1QuestEntryData = Il2CppScheduleOne.Persistence.Datas.QuestEntryData;
#else
using S1Quest = ScheduleOne.Quests.Quest;
using S1QuestEntryData = ScheduleOne.Persistence.Datas.QuestEntryData;
#endif

namespace OverTheCounter.Quests
{
    /// <summary>
    /// Quest creation helpers that guarantee a deterministic shared GUID
    /// between host and client and fix the init order so the game's journal
    /// entry exists before the quest goes Active.
    ///
    /// Background:
    ///   S1API's QuestManager.CreateQuest accepts a guid parameter but never
    ///   applies it. InitializeQuest is deferred until Unity's Start() fires,
    ///   meaning Begin() runs first with a null journalEntry and nothing ever
    ///   activates the UI later. In multiplayer, host and client quests get
    ///   different random GUIDs so the game's SendQuestAction/SendQuestState
    ///   RPCs never route between them.
    ///
    /// This helper:
    ///   1. Creates the quest via QuestManager.CreateQuest (same as before)
    ///   2. Assigns the supplied GUID to the underlying S1Quest
    ///   3. Immediately calls s1Quest.InitializeQuest(...) — registers in
    ///      GUIDManager, adds to game Quests list, builds journal entry
    ///   4. Returns the quest ready for AddEntry / Begin
    ///
    /// Once host and client quests share a GUID the game's native FishNet
    /// quest RPCs route correctly and all subsequent state changes sync
    /// automatically without manual reconciliation.
    /// </summary>
    internal static class QuestHelper
    {
        /// <summary>
        /// Produces a deterministic Guid-format string from a stable key.
        /// Same key always yields the same Guid, across sessions and peers.
        /// Use distinct keys per quest type (e.g. "bella-protocol", "static-intro").
        /// </summary>
        public static string StableGuid(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("StableGuid key cannot be null/empty", nameof(key));

            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("otc-quest:" + key));
            return new Guid(hash).ToString();
        }

        /// <summary>
        /// Creates a managed quest with a deterministic shared GUID and
        /// forces the correct init order (InitializeQuest before Begin).
        /// Caller is still responsible for AddEntry / Begin / StartQuest.
        /// </summary>
        public static T CreateWithGuid<T>(string guid) where T : Quest
        {
            var quest = (T)QuestManager.CreateQuest<T>();
            if (quest == null)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"QuestManager.CreateQuest<{typeof(T).Name}> returned null.");
                return null;
            }

            TryForceInit(quest, guid);
            return quest;
        }

        /// <summary>
        /// Reads the live GUID from the underlying S1Quest, or empty string
        /// if the quest has not been initialized yet. Used by SaveData
        /// classes to backfill _questGuid from legacy saves (where the game
        /// restored a randomly generated GUID before this helper existed).
        /// </summary>
        public static string GetCurrentGuid(Quest quest)
        {
            if (quest == null) return string.Empty;
            var s1Quest = GetS1Quest(quest);
            return s1Quest?.StaticGUID ?? string.Empty;
        }

        /// <summary>
        /// Applies a GUID + manual InitializeQuest on an already-loaded
        /// quest. Safe no-op if the quest has not exposed an S1Quest.
        /// </summary>
        private static void TryForceInit(Quest quest, string guid)
        {
            try
            {
                var s1Quest = GetS1Quest(quest);
                if (s1Quest == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Quest,
                        $"TryForceInit: S1Quest field not found on {quest.GetType().Name}.");
                    return;
                }

                s1Quest.StaticGUID = guid;

                var title = GetProtectedString(quest, "Title");
                var description = GetProtectedString(quest, "Description");

#if IL2CPP
                var entries = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<S1QuestEntryData>(0);
#else
                var entries = Array.Empty<S1QuestEntryData>();
#endif
                s1Quest.InitializeQuest(title ?? string.Empty, description ?? string.Empty, entries, guid);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest,
                    $"TryForceInit failed for {quest.GetType().Name}: {ex.Message}");
            }
        }

        private static S1Quest GetS1Quest(Quest quest)
        {
            var field = typeof(Quest).GetField("S1Quest",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(quest) as S1Quest;
        }

        private static string GetProtectedString(Quest quest, string propertyName)
        {
            var prop = quest.GetType().GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return prop?.GetValue(quest) as string;
        }
    }
}
