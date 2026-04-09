using HarmonyLib;
using OverTheCounter.Logic;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using System;

#if IL2CPP
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.NPCs;
using NPC = Il2CppScheduleOne.NPCs.NPC;
#else
using ScheduleOne.Dialogue;
using ScheduleOne.Interaction;
using ScheduleOne.NPCs;
using NPC = ScheduleOne.NPCs.NPC;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Intercepts E-key interaction on OTC customers who are ready for checkout.
    /// Instead of opening NPC dialogue, starts the checkout flow directly.
    /// </summary>
    public static class CustomerCheckoutInterceptPatch
    {
        /// <summary>Manually patches DialogueController.Hovered and Interacted (private methods).</summary>
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var hovered = AccessTools.Method(typeof(DialogueController), "Hovered");
                if (hovered != null)
                {
                    harmony.Patch(hovered,
                        postfix: new HarmonyMethod(typeof(CustomerCheckoutInterceptPatch), nameof(Hovered_Postfix)));
                    OTCLog.Msg(OTCLog.Systems.Patch, "Patched DialogueController.Hovered");
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, "DialogueController.Hovered not found");
                }

                var interacted = AccessTools.Method(typeof(DialogueController), "Interacted");
                if (interacted != null)
                {
                    harmony.Patch(interacted,
                        prefix: new HarmonyMethod(typeof(CustomerCheckoutInterceptPatch), nameof(Interacted_Prefix)));
                    OTCLog.Msg(OTCLog.Systems.Patch, "Patched DialogueController.Interacted");
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.Patch, "DialogueController.Interacted not found");
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Patch, $"CustomerCheckoutInterceptPatch.Apply: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds the OTC customer matching this NPC that is ready for player checkout.
        /// Returns null if the NPC is not an OTC customer or isn't checkout-ready.
        /// </summary>
        private static CustomerInstance FindCheckoutReadyCustomer(NPC npc)
        {
            foreach (var c in CustomerInstance.Active.Values)
            {
                if (c.GameNpc != npc) continue;

                // Found the OTC customer for this NPC — check readiness
                if (c.State != CustomerState.CheckingOut) return null;
                if (c.AssignedCounter == null) return null;
                if (c.AssignedCounter.IsStaffed) return null;
                if (c.AssignedCounter.Queue.Count == 0 || c.AssignedCounter.Queue[0] != c.Id) return null;
                if (!c.ArrivedAtDestination || c.CheckoutArrivalTime <= 0f) return null;
                return c;
            }
            return null;
        }

        private static void Hovered_Postfix(DialogueController __instance)
        {
            try
            {
                var handler = __instance.GetComponent<DialogueHandler>();
                if (handler == null) return;
                var npc = handler.NPC;
                if (npc == null) return;

                var customer = FindCheckoutReadyCustomer(npc);
                if (customer == null) return;

                __instance.IntObj.SetMessage("Checkout");
                __instance.IntObj.SetInteractableState(InteractableObject.EInteractableState.Default);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,
                    $"CustomerCheckoutInterceptPatch.Hovered: {ex.Message}");
            }
        }

        private static bool Interacted_Prefix(DialogueController __instance)
        {
            try
            {
                var handler = __instance.GetComponent<DialogueHandler>();
                if (handler == null) return true;
                var npc = handler.NPC;
                if (npc == null) return true;

                var customer = FindCheckoutReadyCustomer(npc);
                if (customer == null) return true;

                CheckoutProcess.TryStartCheckoutForCustomer(customer);
                return false;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch,
                    $"CustomerCheckoutInterceptPatch.Interacted: {ex.Message}");
                return true;
            }
        }
    }
}
