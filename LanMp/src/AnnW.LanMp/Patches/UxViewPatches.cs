using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ANNW;
using HarmonyLib;

namespace AnnW.LanMp.Patches
{
    /// <summary>Route UX FOW queries through local viewer fraction (INV-VIEW) in LAN battles.</summary>
    internal static class UxViewPatches
    {
        private static readonly FieldInfo CurPlayerField =
            AccessTools.Field(typeof(GS_Battle), nameof(GS_Battle.cur_player));

        private static readonly FieldInfo FractionField =
            AccessTools.Field(typeof(Player), nameof(Player.fraction));

        private static readonly MethodInfo GetUxViewFraction =
            AccessTools.Method(typeof(ViewUtil), nameof(ViewUtil.GetUxViewFraction));

        private static readonly MethodInfo GetFraction =
            AccessTools.Property(typeof(UnitData), nameof(UnitData.fraction))?.GetGetMethod();

        private static readonly FieldInfo BattleSelfField =
            AccessTools.Field(typeof(GS_Battle), "self");

        [HarmonyPatch(typeof(UnitData), "GetMoveZone")]
        private static class Patch_GetMoveZone_Fow
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                if (GetFraction == null || BattleSelfField == null)
                    return codes;

                for (var i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode != OpCodes.Callvirt ||
                        !(codes[i].operand is MethodInfo mi) ||
                        mi.Name != "AcquireFOWMap")
                        continue;
                    if (i < 2)
                        continue;
                    if (codes[i - 1].opcode != OpCodes.Call || !codes[i - 1].OperandIs(GetFraction))
                        continue;
                    if (codes[i - 2].opcode != OpCodes.Ldarg_0 && codes[i - 2].opcode != OpCodes.Ldarg &&
                        !(codes[i - 2].opcode == OpCodes.Ldarg_S && codes[i - 2].operand?.ToString() == "0"))
                        continue;

                    codes[i - 2] = new CodeInstruction(OpCodes.Ldsfld, BattleSelfField);
                    codes[i - 1] = new CodeInstruction(OpCodes.Call, GetUxViewFraction);
                }
                return codes;
            }
        }

        [HarmonyPatch(typeof(UX_Manager), "OnWorldLeftClick_Alt", typeof(UnityEngine.Vector3))]
        private static class Patch_OnWorldLeftClick_Fow
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);

                for (var i = 0; i < codes.Count - 1; i++)
                {
                    if (codes[i].opcode == OpCodes.Ldfld && codes[i].OperandIs(CurPlayerField) &&
                        codes[i + 1].opcode == OpCodes.Ldfld && codes[i + 1].OperandIs(FractionField))
                    {
                        codes[i] = new CodeInstruction(OpCodes.Call, GetUxViewFraction);
                        codes.RemoveAt(i + 1);
                    }
                }
                return codes;
            }
        }

        [HarmonyPatch(typeof(GS_Battle), "CanSeeMovement")]
        private static class Patch_CanSeeMovement
        {
            private static bool Prefix(GS_Battle __instance, ref bool __result,
                Inctor2 move_target, Inctor2? move_from, UnitData unit)
            {
                if (!GateUtil.LanArmed(out _))
                    return true;

                var result = true;
                if (__instance.skipping_all ||
                    (unit?.player?.ai != null && unit.player.ai.skipping) ||
                    (__instance.is_auto_guiding && __instance.auto_guide_skip_animations))
                    result = false;

                if (!__instance.functions.Querry(GAME_FUNCTION.NoFOW) && move_from.HasValue)
                {
                    var fow = GameAPI.self.GetFOWMap(ViewUtil.GetUxViewFraction(__instance));
                    if (!fow.CanSeeUnit(move_target) && !fow.CanSeeUnit(move_from.Value))
                        result = false;
                }

                __result = result;
                return false;
            }
        }

        [HarmonyPatch(typeof(GS_Battle), "CanObserve")]
        private static class Patch_CanObserve
        {
            private static bool Prefix(GS_Battle __instance, ref bool __result, Inctor2 pos)
            {
                if (!GateUtil.LanArmed(out _))
                    return true;

                var result = true;
                var ai = __instance.cur_player?.ai;
                if (__instance.skipping_all ||
                    (ai != null && ai.skipping) ||
                    (__instance.is_auto_guiding && __instance.auto_guide_skip_animations))
                    result = false;

                if (!__instance.functions.Querry(GAME_FUNCTION.NoFOW) &&
                    !GameAPI.self.GetFOWMap(ViewUtil.GetUxViewFraction(__instance)).CanSeeUnit(pos))
                    result = false;

                __result = result;
                return false;
            }
        }
    }
}
