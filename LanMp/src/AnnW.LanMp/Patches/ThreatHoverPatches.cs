using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ANNW;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// LAN hover threat (TargetSel_Hover): keep spectating previews, bind stay-attack to INV-VIEW.
    /// INV-VIEW: local viewer ownership, not cur_player / remote seat.
    /// </summary>
    internal static class ThreatHoverPatches
    {
        private static readonly MethodInfo GetControlState =
            AccessTools.PropertyGetter(typeof(GS_Battle), nameof(GS_Battle.control_state));

        private static readonly MethodInfo ShouldSuppress =
            AccessTools.Method(typeof(ViewUtil), nameof(ViewUtil.ShouldSuppressHoverThreatOverlay));

        private static readonly MethodInfo ShouldRender =
            AccessTools.Method(typeof(ViewUtil), nameof(ViewUtil.ShouldRenderHoverThreatOverlay));

        private static readonly MethodInfo GetUxViewPlayer =
            AccessTools.Method(typeof(ViewUtil), nameof(ViewUtil.GetUxViewPlayer));

        private static readonly FieldInfo CurPlayerField =
            AccessTools.Field(typeof(GS_Battle), nameof(GS_Battle.cur_player));

        [HarmonyPatch(typeof(TargetSel_Hover), "OnSimpleMouseHover")]
        private static class Patch_OnSimpleMouseHover
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                if (GetControlState == null || ShouldSuppress == null)
                    return codes;

                // Vanilla: get_control_state; brfalse.s continue  (Human=0 → skip Clear)
                // LAN:     ShouldSuppress;   brfalse.s continue  (allow → skip Clear)
                for (var i = 0; i < codes.Count - 1; i++)
                {
                    if (codes[i].opcode != OpCodes.Call || !codes[i].OperandIs(GetControlState))
                        continue;
                    if (codes[i + 1].opcode != OpCodes.Brfalse && codes[i + 1].opcode != OpCodes.Brfalse_S)
                        continue;

                    codes[i] = new CodeInstruction(OpCodes.Call, ShouldSuppress);
                    break;
                }
                return codes;
            }
        }

        [HarmonyPatch(typeof(TargetSel_Hover), nameof(TargetSel_Hover.RenderOverlay))]
        private static class Patch_RenderOverlay
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                if (GetControlState == null || ShouldRender == null)
                    return codes;

                // Vanilla: get_control_state; brtrue.s skip  (non-Human → skip RenderNonSel)
                // LAN:     ShouldRender;      brfalse.s skip (false → skip RenderNonSel)
                for (var i = 0; i < codes.Count - 1; i++)
                {
                    if (codes[i].opcode != OpCodes.Call || !codes[i].OperandIs(GetControlState))
                        continue;
                    if (codes[i + 1].opcode != OpCodes.Brtrue && codes[i + 1].opcode != OpCodes.Brtrue_S)
                        continue;

                    var skipTarget = codes[i + 1].operand;
                    codes[i] = new CodeInstruction(OpCodes.Call, ShouldRender);
                    codes[i + 1] = new CodeInstruction(
                        codes[i + 1].opcode == OpCodes.Brtrue_S ? OpCodes.Brfalse_S : OpCodes.Brfalse,
                        skipTarget);
                    break;
                }
                return codes;
            }
        }

        [HarmonyPatch(typeof(TargetSel_Hover), nameof(TargetSel_Hover.PrepareUnitUnderMouse))]
        private static class Patch_PrepareUnitUnderMouse
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                if (CurPlayerField == null || GetUxViewPlayer == null)
                    return codes;

                for (var i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode != OpCodes.Ldfld || !codes[i].OperandIs(CurPlayerField))
                        continue;
                    codes[i] = new CodeInstruction(OpCodes.Call, GetUxViewPlayer);
                    break;
                }
                return codes;
            }
        }
    }
}
