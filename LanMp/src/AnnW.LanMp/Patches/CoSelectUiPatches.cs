using HarmonyLib;
using AnnW.LanMp.Ui;
using ANNW;
using UnityEngine;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// Vanilla skirmish CO select bugs (also hit LAN room ShowForSkirmish):
    /// 1) PartPS.IsAvailable uses Max(1, level) under for_skirmish — free slot index==2
    ///    still needs level&gt;2, so low-level COs cannot pick the free PS.
    /// 2) Skill/PS picker popups can open under the LAN room canvas — raise sorting.
    /// </summary>
    internal static class CoSelectUiPatches
    {
        /// <summary>Skirmish: unlock all three PS slots (incl. free) regardless of archive level.</summary>
        [HarmonyPatch(typeof(UI_CO_Select_PartPS), "IsAvailable")]
        private static class Patch_PartPS_IsAvailable
        {
            private static bool Prefix(UI_CO_Select_PartPS __instance, ref bool __result)
            {
                try
                {
                    var parent = __instance.parent;
                    if (parent == null || !parent.for_skirmish)
                        return true;
                    if (parent.cur_selection.sd_co == null)
                    {
                        __result = false;
                        return false;
                    }
                    // index 0..2 → need effectiveLevel > index; force effectiveLevel ≥ 3.
                    __result = __instance.index < 3;
                    return false;
                }
                catch
                {
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(UI_CO_Select_Info), "RenderCOInfo")]
        private static class Patch_RenderCOInfo_FreePsButton
        {
            private static void Postfix(UI_CO_Select_Info __instance, bool for_skirmish)
            {
                if (!for_skirmish || __instance == null)
                    return;
                try
                {
                    // Mirror Zero: always show random-PS in skirmish so free slot is reachable.
                    if (__instance.btn_rd_ps != null)
                        __instance.btn_rd_ps.SetActive(true);
                }
                catch { /* ignore */ }
            }
        }

        [HarmonyPatch(typeof(UI_POP_SK_Select), nameof(UI_POP_SK_Select.ShowSKSselect))]
        private static class Patch_ShowSKSelect
        {
            private static void Postfix(UI_POP_SK_Select __instance)
            {
                LanDropMenu.BringFloaterPopupToFront(__instance);
            }
        }

        [HarmonyPatch(typeof(UI_POP_PS_Select), nameof(UI_POP_PS_Select.ShowPSSelect))]
        private static class Patch_ShowPSSelect
        {
            private static void Postfix(UI_POP_PS_Select __instance)
            {
                LanDropMenu.BringFloaterPopupToFront(__instance);
            }
        }
    }
}
