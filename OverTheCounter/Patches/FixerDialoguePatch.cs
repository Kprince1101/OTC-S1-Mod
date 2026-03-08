using HarmonyLib;
using OverTheCounter.Logic;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using System;

#if IL2CPP
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.Property;
#else
using ScheduleOne.Dialogue;
using ScheduleOne.DevUtilities;
using ScheduleOne.Money;
using ScheduleOne.Property;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Harmony patches on DialogueController_Fixer (Manny the Fixer) to add
    /// "Manager" as an employee type option. When selected, the location list
    /// shows owned businesses instead of houses, and CONFIRM creates a Manager
    /// instead of a vanilla employee.
    /// </summary>
    [HarmonyPatch(typeof(DialogueController_Fixer))]
    public static class FixerDialoguePatch
    {
        // State tracking across dialogue steps
        private static bool _managerSelected;
        private static Business _selectedBusiness;

        /// <summary>
        /// Postfix on ModifyChoiceList: inject "Manager" into the employee type
        /// selection node, and override location list for businesses when manager
        /// is selected.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("ModifyChoiceList")]
        public static void ModifyChoiceList_Postfix(
            DialogueController_Fixer __instance,
            string dialogueLabel,
            GameSystem.Collections.Generic.List<DialogueChoiceData> existingChoices)
        {
            try
            {
                // Detect employee type selection node by presence of "Botanist" choice
                bool isEmployeeTypeNode = false;
                for (int i = 0; i < existingChoices.Count; i++)
                {
                    if (existingChoices[i].ChoiceLabel == "Botanist")
                    {
                        isEmployeeTypeNode = true;
                        break;
                    }
                }

                // Detect Manny's intro node (first-time "You the new guy?" dialogue)
                // by checking for the "Probably" choice — only the intro has it.
                bool isIntroNode = false;
                for (int i = 0; i < existingChoices.Count; i++)
                {
                    if (existingChoices[i].ChoiceText == "Probably")
                    {
                        isIntroNode = true;
                        break;
                    }
                }

                // Show "Warehouse hours?" on ENTRY greeting only (not the intro or employee type node)
                // Hide once quest is started (stage >= 1) — player already knows about Bella
                if (dialogueLabel == "ENTRY" && !isEmployeeTypeNode && !isIntroNode &&
                    (BellaSaveData.Instance == null || BellaSaveData.Instance.Stage == 0))
                {
                    var warehouseChoice = new DialogueChoiceData();
                    warehouseChoice.ChoiceText = "Can I get into the warehouse before 6pm?";
                    warehouseChoice.ChoiceLabel = "WarehouseHours";
                    existingChoices.Add(warehouseChoice);
                }

                if (isEmployeeTypeNode)
                {
                    var managerChoice = new DialogueChoiceData();
                    managerChoice.ChoiceText = "Manager (supply runs, business logistics)";
                    managerChoice.ChoiceLabel = "Manager";
                    existingChoices.Add(managerChoice);
                }

                // Override location selection for managers — show businesses instead of houses
                if (dialogueLabel == "SELECT_LOCATION" && _managerSelected)
                {
                    existingChoices.Clear();

                    foreach (var biz in Business.OwnedBusinesses)
                    {
                        if (biz == null) continue;
                        if (!ManagerLocations.HasLocation(biz.PropertyCode)) continue;
                        if (ManagerInstance.HasManager(biz.PropertyCode)) continue;

                        var choice = new DialogueChoiceData();
                        choice.ChoiceText = biz.PropertyName;
                        choice.ChoiceLabel = biz.PropertyCode;
                        existingChoices.Add(choice);
                    }

                    if (existingChoices.Count == 0)
                    {
                        var noChoice = new DialogueChoiceData();
                        noChoice.ChoiceText = "No eligible businesses";
                        noChoice.ChoiceLabel = "NO_BUSINESSES";
                        existingChoices.Add(noChoice);
                        OTCLog.Msg(OTCLog.Systems.Patch, "No eligible businesses for manager hiring");
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"ModifyChoiceList_Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix on ChoiceCallback: track Manager selection state and intercept
        /// CONFIRM to run our hire flow instead of vanilla Confirm().
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("ChoiceCallback")]
        public static bool ChoiceCallback_Prefix(
            DialogueController_Fixer __instance,
            string choiceLabel)
        {
            try
            {
                if (choiceLabel == "WarehouseHours")
                {
                    // Trigger the quest (POI appears while player reads Manny's response)
                    if (NetworkHelper.IsHost)
                    {
                        BellaSaveData.Instance?.TriggerQuest();
                    }
                    else
                    {
                        ConfigSyncData.SendQuestAction("BELLA_INTRO");
                    }

                    // Show Manny's response — 0 choices = "continue" button → EndDialogue
                    var responseNode = new DialogueNodeData();
                    responseNode.DialogueText = "Talk to Bella downtown. Tell her I sent you.";
                    responseNode.DialogueNodeLabel = "WAREHOUSE_RESPONSE";
#if IL2CPP
                    responseNode.choices = new Il2CppReferenceArray<DialogueChoiceData>(0);
#else
                    responseNode.choices = new DialogueChoiceData[0];
#endif
                    __instance.GetHandler()?.ShowNode(responseNode);

                    OTCLog.Msg(OTCLog.Systems.Patch, "Warehouse hours quest triggered from Fixer dialogue");
                    return false;
                }

                if (choiceLabel == "Manager")
                {
                    _managerSelected = true;
                    _selectedBusiness = null;
                    OTCLog.Msg(OTCLog.Systems.Patch, "Manager employee type selected");
                    return true; // Let vanilla handle dialogue progression
                }

                // Reset on other employee type selections
                if (choiceLabel is "Botanist" or "Packager" or "Chemist" or "Cleaner")
                {
                    _managerSelected = false;
                    _selectedBusiness = null;
                    return true;
                }

                // Intercept CONFIRM when manager is selected
                if (choiceLabel == "CONFIRM" && _managerSelected)
                {
                    if (_selectedBusiness != null)
                    {
                        if (NetworkHelper.IsHost)
                        {
                            OTCLog.Msg(OTCLog.Systems.Patch, $"Hiring manager at {_selectedBusiness.PropertyCode} via Fixer dialogue (host)");
                            ManagerController.Instance?.HireManager(_selectedBusiness);
                        }
                        else
                        {
                            OTCLog.Msg(OTCLog.Systems.Patch, $"Requesting manager hire at {_selectedBusiness.PropertyCode} via Fixer dialogue (client)");
                            ConfigSyncData.SendQuestAction($"MANAGER_HIRE:{_selectedBusiness.PropertyCode}");
                        }
                    }
                    else
                    {
                        OTCLog.Warning(OTCLog.Systems.Patch, "CONFIRM with manager selected but no business chosen");
                    }

                    // Null out selectedProperty so vanilla Confirm() won't fire
                    // (it checks selectedProperty != null before calling Confirm())
#if IL2CPP
                    __instance.selectedProperty = null;
#else
                    AccessTools.Field(typeof(ScheduleOne.Dialogue.DialogueController_Fixer), "selectedProperty")
                        ?.SetValue(__instance, null);
#endif

                    _managerSelected = false;
                    _selectedBusiness = null;

                    return true; // Let base.ChoiceCallback handle dialogue flow
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"ChoiceCallback_Prefix error: {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// Postfix on ChoiceCallback: navigate to SELECT_LOCATION when Manager is
        /// clicked (vanilla employee types have target nodes in the dialogue asset,
        /// but our dynamically-added choice doesn't). Also capture business selection.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("ChoiceCallback")]
        public static void ChoiceCallback_Postfix(
            DialogueController_Fixer __instance,
            string choiceLabel)
        {
            try
            {
                // When Manager is selected, manually navigate to SELECT_LOCATION
                if (choiceLabel == "Manager")
                {
                    var dialogue = DialogueHandler.activeDialogue;
                    var node = dialogue?.GetDialogueNodeByLabel("SELECT_LOCATION");
                    if (node != null)
                    {
                        __instance.GetHandler().ShowNode(node);
                        OTCLog.Msg(OTCLog.Systems.Patch, "Navigated to SELECT_LOCATION for manager hiring");
                    }
                    else
                    {
                        OTCLog.Error(OTCLog.Systems.Patch, "SELECT_LOCATION node not found in active dialogue");
                    }
                    return;
                }

                if (!_managerSelected) return;

                // Capture business selection (vanilla already handles FINALIZE
                // navigation since businesses are Properties in OwnedProperties)
                foreach (var biz in Business.OwnedBusinesses)
                {
                    if (biz != null && string.Equals(biz.PropertyCode, choiceLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        _selectedBusiness = biz;
                        OTCLog.Msg(OTCLog.Systems.Patch, $"Business selected for manager: {biz.PropertyCode}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"ChoiceCallback_Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix on CheckChoice: validate manager-specific constraints instead
        /// of vanilla employee capacity/fee checks.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("CheckChoice")]
        public static bool CheckChoice_Prefix(
            DialogueController_Fixer __instance,
            string choiceLabel,
            ref string invalidReason,
            ref bool __result)
        {
            try
            {
                // Warehouse hours is always valid
                if (choiceLabel == "WarehouseHours")
                {
                    __result = true;
                    return false;
                }

                // Block the "no businesses" placeholder
                if (choiceLabel == "NO_BUSINESSES")
                {
                    invalidReason = "You need to own a business first";
                    __result = false;
                    return false;
                }

                // Override CONFIRM validation for managers
                if (choiceLabel == "CONFIRM" && _managerSelected)
                {
                    var moneyManager = NetworkSingleton<MoneyManager>.Instance;
                    float fee = Config.ManagerSigningFee.Value;

                    if (moneyManager == null || moneyManager.cashBalance < fee)
                    {
                        invalidReason = "Insufficient cash";
                        __result = false;
                        return false;
                    }

                    __result = true;
                    return false; // Skip vanilla (which checks employee prefab fee)
                }

                // Override business selection validation for managers
                if (_managerSelected)
                {
                    foreach (var biz in Business.OwnedBusinesses)
                    {
                        if (biz != null && string.Equals(biz.PropertyCode, choiceLabel, StringComparison.OrdinalIgnoreCase))
                        {
                            if (ManagerInstance.HasManager(biz.PropertyCode))
                            {
                                invalidReason = "Already has a manager";
                                __result = false;
                                return false;
                            }

                            __result = true;
                            return false; // Skip vanilla employee capacity check
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"CheckChoice_Prefix error: {ex.Message}");
            }

            return true; // Run vanilla for non-manager cases
        }

        /// <summary>
        /// Prefix on ModifyDialogueText: show manager costs on FINALIZE node.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("ModifyDialogueText")]
        public static bool ModifyDialogueText_Prefix(
            DialogueController_Fixer __instance,
            string dialogueLabel,
            string dialogueText,
            ref string __result)
        {
            try
            {
                if (dialogueLabel == "FINALIZE" && _managerSelected)
                {
                    float signingFee = Config.ManagerSigningFee.Value;
                    float dailyWage = Config.ManagerDailyWage.Value;

                    __result = dialogueText
                        .Replace("<SIGN_FEE>", $"<color=#54E717>{MoneyManager.FormatAmount(signingFee)}</color>")
                        .Replace("<DAILY_WAGE>", $"<color=#54E717>{MoneyManager.FormatAmount(dailyWage)}</color>");

                    return false; // Skip vanilla
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"ModifyDialogueText_Prefix error: {ex.Message}");
            }

            return true;
        }
    }
}
