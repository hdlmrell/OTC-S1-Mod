using HarmonyLib;
using MelonLoader;
using OverTheCounter.Logic;
using OverTheCounter.UI;
using OverTheCounter.Utilities;
using System;

#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI.Handover;
#else
using ScheduleOne.Economy;
using ScheduleOne.ItemFramework;
using ScheduleOne.Quests;
using ScheduleOne.UI.Handover;
#endif

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Harmony patch to detect handover completion for drifter NPCs.
    /// Identifies drifters by NPC ID (not proximity). Payment and narc sting
    /// are handled by DrifterManager's HandoverScreen callback — this marks
    /// the deal completed as a safety net.
    /// </summary>
    [HarmonyPatch(typeof(Customer), "ProcessHandover")]
    public static class DrifterHandoverPatch
    {
        // Thread-local storage to track drifter ID between prefix and postfix
        private static readonly System.Threading.ThreadLocal<string> _pendingDrifterId = new();

        /// <summary>
        /// Prefix: Check if this handover's customer IS a drifter with an active deal.
        /// </summary>
        public static void Prefix(
            Customer __instance,
            Contract contract,
            GameSystem.Collections.Generic.List<ItemInstance> items,
            bool handoverByPlayer,
            bool giveBonuses)
        {
            _pendingDrifterId.Value = null;

            // Only process handovers initiated by the player
            if (!handoverByPlayer)
                return;

            if (__instance?.NPC == null)
                return;

            try
            {
                string npcId = __instance.NPC.ID;

                // Check if this customer IS a drifter with an accepted deal
                if (!DrifterInstance.Active.TryGetValue(npcId, out var drifter))
                    return;

                if (drifter.DealCompleted)
                    return;

                _pendingDrifterId.Value = npcId;
                OTCLog.Msg(OTCLog.Systems.Drifter, $"Detected handover for {npcId}");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Drifter, $"Handover prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix: Mark the drifter deal as completed (idempotent safety net).
        /// </summary>
        public static void Postfix(
            Customer __instance,
            Contract contract,
            GameSystem.Collections.Generic.List<ItemInstance> items,
            bool handoverByPlayer,
            bool giveBonuses)
        {
            var drifterId = _pendingDrifterId.Value;
            _pendingDrifterId.Value = null;

            if (drifterId == null)
                return;

            // Only host processes drifter completions
            if (!NetworkHelper.IsHost)
                return;

            try
            {
                DrifterManager.Instance?.OnDealCompleted(drifterId);
                OTCLog.Msg(OTCLog.Systems.Drifter, $"Completed deal {drifterId}");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Drifter, $"Handover postfix error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patches every HandoverScreen.Open_* mode to clear stale customer slots before opening.
    /// Submit does NOT clear slots unless the callback does, so items left by a robbed
    /// drifter deal would bleed into the next handover (e.g. a desperation deal).
    /// </summary>
    [HarmonyPatch]
    public static class HandoverScreenOpenPatch
    {
        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(HandoverScreen), nameof(HandoverScreen.Open_Contract));
            yield return AccessTools.Method(typeof(HandoverScreen), nameof(HandoverScreen.Open_Sample));
            yield return AccessTools.Method(typeof(HandoverScreen), nameof(HandoverScreen.Open_Offer));
            yield return AccessTools.Method(typeof(HandoverScreen), nameof(HandoverScreen.Open_SpecialCustomer));
        }

        public static void Prefix(HandoverScreen __instance)
        {
            try
            {
                __instance.DestroyCustomerItems();
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"DestroyCustomerItems failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Shows the Smart Fill overlay when the HandoverScreen opens in Contract mode.
    /// </summary>
    [HarmonyPatch(typeof(HandoverScreen), nameof(HandoverScreen.Open_Contract))]
    public static class HandoverScreenOpenContractPatch
    {
        public static void Postfix()
        {
            try
            {
                OTCLog.Msg(OTCLog.Systems.Drifter, "HandoverScreen Open_Contract fired");
                HandoverFillUI.Show();
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"Show overlay failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Hides the Smart Fill overlay when the HandoverScreen closes.
    /// </summary>
    [HarmonyPatch(typeof(HandoverScreen), nameof(HandoverScreen.Close))]
    public static class HandoverScreenClosePatch
    {
        public static void Postfix()
        {
            try
            {
                HandoverFillUI.Hide();
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Drifter, $"Hide overlay failed: {ex.Message}");
            }
        }
    }
}
