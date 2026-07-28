// Centralized accessors for game members that are public on IL2CPP (unhollowed)
// but private/internal on Mono. Each accessor works on both platforms:
// - IL2CPP: direct property/field access (fast, compile-time checked)
// - Mono: Harmony AccessTools reflection (slower, runtime checked)
//
// Usage: replace `obj.PrivateField` with `obj.GetPrivateField()` (or SetPrivateField).
// This keeps #if blocks in ONE file instead of scattered across the codebase.

using HarmonyLib;

#if IL2CPP
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Handover;
using Il2CppScheduleOne.UI.Phone.ContactsApp;
using Il2CppScheduleOne.VoiceOver;
#else
using System.Collections.Generic;
using System.Reflection;
using ScheduleOne.Dialogue;
using ScheduleOne.Economy;
using ScheduleOne.ItemFramework;
using ScheduleOne.Management;
using ScheduleOne.Map;
using ScheduleOne.Messaging;
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Quests;
using ScheduleOne.UI;
using ScheduleOne.UI.Handover;
using ScheduleOne.UI.Phone.ContactsApp;
using ScheduleOne.VoiceOver;
#endif

namespace OverTheCounter
{
    /// <summary>
    /// Extension methods for accessing private/internal game members cross-platform.
    /// On IL2CPP, these compile to direct access. On Mono, they use cached reflection.
    /// </summary>
    internal static class PrivateAccess
    {
#if !IL2CPP
        // Cached FieldInfo / PropertyInfo for Mono reflection
        private static readonly FieldInfo _voeDatabase = AccessTools.Field(typeof(VOEmitter), "Database");
        private static readonly FieldInfo _customerData = AccessTools.Field(typeof(Customer), "customerData");
        private static readonly FieldInfo _questTitle = AccessTools.Field(typeof(Quest), "title");
        private static readonly FieldInfo _dcHandler = AccessTools.Field(typeof(DialogueController), "handler");
        private static readonly FieldInfo _questEntryUI = AccessTools.Field(typeof(QuestEntry), "entryUI");
        private static readonly FieldInfo _miConfigPanels = AccessTools.Field(typeof(ManagementInterface), "ConfigPanelPrefabs");
        private static readonly PropertyInfo _npcMsgConv = AccessTools.Property(typeof(NPC), "MSGConversation");
        private static readonly PropertyInfo _npcCurrentBuilding = AccessTools.Property(typeof(NPC), "CurrentBuilding");
        private static readonly PropertyInfo _custTimeDealCompleted = AccessTools.Property(typeof(Customer), "TimeSinceLastDealCompleted");
        private static readonly PropertyInfo _custTimeDealOffered = AccessTools.Property(typeof(Customer), "TimeSinceLastDealOffered");
        private static readonly PropertyInfo _custOfferedContract = AccessTools.Property(typeof(Customer), "OfferedContractInfo");
        private static readonly PropertyInfo _custPoI = AccessTools.Property(typeof(Customer), "potentialCustomerPoI");
        private static readonly FieldInfo _hsCustomerSlots = AccessTools.Field(typeof(HandoverScreen), "CustomerSlots");
        private static readonly FieldInfo _hsOriginalItemLocations = AccessTools.Field(typeof(HandoverScreen), "OriginalItemLocations");
        private static readonly System.Type _hsEItemSource = typeof(HandoverScreen).GetNestedType("EItemSource", BindingFlags.NonPublic);
        private static readonly object _hsEItemSourcePlayer = _hsEItemSource != null ? System.Enum.Parse(_hsEItemSource, "Player") : null;
#endif

        // --- VOEmitter.Database (private field on Mono, SetDatabase() is public) ---
        public static VODatabase GetDatabase(this VOEmitter emitter)
        {
#if IL2CPP
            return emitter._currentDatabase;
#else
            return (VODatabase)_voeDatabase?.GetValue(emitter);
#endif
        }

        // --- Customer.customerData (private field) ---
        // Named GetCustData/SetCustData to avoid collision with game's Customer.GetCustomerData()
        // which returns Il2CppScheduleOne.Persistence.Datas.CustomerData (SaveData), not the field.
        public static CustomerData GetCustData(this Customer customer)
        {
#if IL2CPP
            return customer.customerData;
#else
            return (CustomerData)_customerData?.GetValue(customer);
#endif
        }

        public static void SetCustData(this Customer customer, CustomerData data)
        {
#if IL2CPP
            customer.customerData = data;
#else
            _customerData?.SetValue(customer, data);
#endif
        }

        // --- Quest.title (private field) ---
        public static string GetTitle(this Quest quest)
        {
#if IL2CPP
            return quest.title;
#else
            return (string)_questTitle?.GetValue(quest);
#endif
        }

        public static void SetTitle(this Quest quest, string value)
        {
#if IL2CPP
            quest.title = value;
#else
            _questTitle?.SetValue(quest, value);
#endif
        }

        // --- DialogueController.handler (private field) ---
        public static DialogueHandler GetHandler(this DialogueController dc)
        {
#if IL2CPP
            return dc.handler;
#else
            return (DialogueHandler)_dcHandler?.GetValue(dc);
#endif
        }

        // --- ManagementInterface.ConfigPanelPrefabs (protected field on Mono) ---
        public static ManagementInterface.ConfigurableTypePanel[] GetConfigPanelPrefabs(this ManagementInterface mi)
        {
#if IL2CPP
            return mi.ConfigPanelPrefabs;
#else
            return (ManagementInterface.ConfigurableTypePanel[])_miConfigPanels?.GetValue(mi);
#endif
        }

        // --- NPC.MSGConversation (property, no public setter on Mono) ---
        public static MSGConversation GetMSGConversation(this NPC npc)
        {
#if IL2CPP
            return npc.MSGConversation;
#else
            return (MSGConversation)_npcMsgConv?.GetValue(npc);
#endif
        }

        public static void SetMSGConversation(this NPC npc, MSGConversation conv)
        {
#if IL2CPP
            npc.MSGConversation = conv;
#else
            _npcMsgConv?.SetValue(npc, conv);
#endif
        }

        // --- NPC.CurrentBuilding (property, no public setter on Mono) ---
        public static void SetCurrentBuilding(this NPC npc, ScheduleOne.Map.NPCEnterableBuilding building)
        {
#if IL2CPP
            npc.CurrentBuilding = building;
#else
            _npcCurrentBuilding?.SetValue(npc, building);
#endif
        }

        // --- Customer.TimeSinceLastDealCompleted (property, no public setter on Mono) ---
        public static void SetTimeSinceLastDealCompleted(this Customer customer, int value)
        {
#if IL2CPP
            customer.TimeSinceLastDealCompleted = value;
#else
            _custTimeDealCompleted?.SetValue(customer, value);
#endif
        }

        // --- Customer.TimeSinceLastDealOffered (property, no public setter on Mono) ---
        public static void SetTimeSinceLastDealOffered(this Customer customer, int value)
        {
#if IL2CPP
            customer.TimeSinceLastDealOffered = value;
#else
            _custTimeDealOffered?.SetValue(customer, value);
#endif
        }

        // --- Customer.OfferedContractInfo (property, no public setter on Mono) ---
        public static ContractInfo GetOfferedContractInfo(this Customer customer)
        {
#if IL2CPP
            return customer.OfferedContractInfo;
#else
            return (ContractInfo)_custOfferedContract?.GetValue(customer);
#endif
        }

        public static void SetOfferedContractInfo(this Customer customer, ContractInfo info)
        {
#if IL2CPP
            customer.OfferedContractInfo = info;
#else
            _custOfferedContract?.SetValue(customer, info);
#endif
        }

        // --- Customer.potentialCustomerPoI (property, read-only on Mono) ---
        public static NPCPoI GetPotentialCustomerPoI(this Customer customer)
        {
#if IL2CPP
            return customer.potentialCustomerPoI;
#else
            return (NPCPoI)_custPoI?.GetValue(customer);
#endif
        }

        public static void SetPotentialCustomerPoI(this Customer customer, NPCPoI poi)
        {
#if IL2CPP
            customer.potentialCustomerPoI = poi;
#else
            _custPoI?.SetValue(customer, poi);
#endif
        }

        // --- QuestEntry.entryUI (private field) ---
        public static QuestEntryHUDUI GetEntryUI(this QuestEntry entry)
        {
#if IL2CPP
            return entry.entryUI;
#else
            return (QuestEntryHUDUI)_questEntryUI?.GetValue(entry);
#endif
        }

        // --- DialogueChoice.shouldShowCheck (Func<bool,bool> on IL2CPP, nested delegate on Mono) ---
        public static void SetShouldShowCheck(this DialogueController.DialogueChoice choice, System.Func<bool, bool> func)
        {
#if IL2CPP
            choice.shouldShowCheck = func;
#else
            choice.shouldShowCheck = new DialogueController.DialogueChoice.ShouldShowCheck(func.Invoke);
#endif
        }

        // --- HandoverScreen.CustomerSlots -> now the public field/property _customerSlots ---
        // (renamed and re-exposed as public in the current game version; the underlying
        // storage is an Il2Cpp native array, so we copy it into a managed array rather than
        // relying on an implicit conversion operator that may not exist)
        public static ItemSlot[] GetCustomerSlots(this HandoverScreen hs)
        {
#if IL2CPP
            var native = hs._customerSlots;
            if (native == null) return null;
            var result = new ItemSlot[native.Length];
            for (int i = 0; i < native.Length; i++) result[i] = native[i];
            return result;
#else
            return (ItemSlot[])_hsCustomerSlots?.GetValue(hs);
#endif
        }

        // --- HandoverScreen.OriginalItemLocations / EItemSource: fully removed from the current
        // HandoverScreen (confirmed via unfiltered Cecil dump -- no matching property, field, or
        // nested type). There is no longer any public/private hook for tagging an item's source,
        // so this is now a no-op. NOTE: this means smart-filled items are no longer explicitly
        // marked as player-owned; if HandoverScreen's own (now-internal) origin tracking doesn't
        // return them to the player automatically on a cancelled handover, they could end up
        // attributed to the customer instead. Flagging as a possible regression, not confirmed.
        public static void TrackItemAsPlayer(this HandoverScreen hs, ItemInstance item)
        {
#if IL2CPP
            // No-op: no replacement API found for OriginalItemLocations/EItemSource.
#else
            var dict = _hsOriginalItemLocations?.GetValue(hs);
            if (dict != null && _hsEItemSourcePlayer != null)
            {
                // Dictionary<ItemInstance, EItemSource>.Add via reflection
                var addMethod = dict.GetType().GetMethod("set_Item");
                addMethod?.Invoke(dict, new object[] { item, _hsEItemSourcePlayer });
            }
#endif
        }
    }
}
