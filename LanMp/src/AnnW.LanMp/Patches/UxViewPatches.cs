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

        private static readonly MethodInfo GetActionUxFowFraction =
            AccessTools.Method(typeof(ViewUtil), nameof(ViewUtil.GetActionUxFowFraction));

        private static readonly MethodInfo GetMoveZoneFowFraction =
            AccessTools.Method(typeof(ViewUtil), nameof(ViewUtil.GetMoveZoneFowFraction));

        private static readonly MethodInfo GetFraction =
            AccessTools.Property(typeof(UnitData), nameof(UnitData.fraction))?.GetGetMethod();

        private static readonly FieldInfo ActionPlayerField =
            AccessTools.Field(typeof(ActionData), nameof(ActionData.player));

        private static readonly FieldInfo BattleSelfField =
            AccessTools.Field(typeof(GS_Battle), "self");

        /// <summary>
        /// Replace <c>action.player.fraction → AcquireFOWMap</c> with INV-VIEW
        /// <see cref="ViewUtil.GetActionUxFowFraction"/> (BUILD ghost / AOE / CanDoAction UX).
        /// </summary>
        private static List<CodeInstruction> RewriteActionPlayerFow(List<CodeInstruction> codes)
        {
            if (ActionPlayerField == null || FractionField == null || GetActionUxFowFraction == null ||
                BattleSelfField == null)
                return codes;

            for (var i = 0; i < codes.Count - 2; i++)
            {
                // ldarg.0 ; ldfld player ; ldfld fraction ; callvirt AcquireFOWMap
                if (codes[i].opcode != OpCodes.Ldarg_0 &&
                    !(codes[i].opcode == OpCodes.Ldarg && Equals(codes[i].operand, 0)) &&
                    !(codes[i].opcode == OpCodes.Ldarg_S && codes[i].operand?.ToString() == "0"))
                    continue;
                if (i + 3 >= codes.Count)
                    continue;
                if (codes[i + 1].opcode != OpCodes.Ldfld || !codes[i + 1].OperandIs(ActionPlayerField))
                    continue;
                if (codes[i + 2].opcode != OpCodes.Ldfld || !codes[i + 2].OperandIs(FractionField))
                    continue;
                if (codes[i + 3].opcode != OpCodes.Callvirt ||
                    !(codes[i + 3].operand is MethodInfo mi) ||
                    mi.Name != "AcquireFOWMap")
                    continue;

                // Keep ldarg.0 (action); replace player+fraction with battle + helper.
                codes[i + 1] = new CodeInstruction(OpCodes.Ldsfld, BattleSelfField);
                codes[i + 2] = new CodeInstruction(OpCodes.Call, GetActionUxFowFraction);
                // AcquireFOWMap stays at i+3
            }
            return codes;
        }

        [HarmonyPatch(typeof(ActionData), nameof(ActionData.CanDoAction))]
        private static class Patch_CanDoAction_Fow
        {
            // INV-VIEW FOW only — do NOT soft-pass TARGET_NOT_VISIBLE.
            // Vanilla FOWTile.CanDoAction requires SEEN (vision); DETECTED alone must fail.
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                RewriteActionPlayerFow(new List<CodeInstruction>(instructions));
        }

        // GetEffectZone / CheckAttackAnyone / OnMapUnitChange are non-public — patch by name string.
        [HarmonyPatch(typeof(ActionData), "GetEffectZone")]
        private static class Patch_GetEffectZone_Fow
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                RewriteActionPlayerFow(new List<CodeInstruction>(instructions));
        }

        [HarmonyPatch(typeof(UnitData), "CheckAttackAnyone")]
        private static class Patch_CheckAttackAnyone_Fow
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                if (GetFraction == null || BattleSelfField == null || GetMoveZoneFowFraction == null)
                    return codes;

                for (var i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode != OpCodes.Callvirt ||
                        !(codes[i].operand is MethodInfo mi) ||
                        mi.Name != "AcquireFOWMap")
                        continue;
                    if (i < 2)
                        continue;
                    // ... get_fraction(unit) → AcquireFOWMap  ⇒  unit, battle → GetMoveZoneFowFraction
                    if (codes[i - 1].opcode != OpCodes.Call || !codes[i - 1].OperandIs(GetFraction))
                        continue;
                    if (codes[i - 2].opcode != OpCodes.Ldarg_0 && codes[i - 2].opcode != OpCodes.Ldarg &&
                        !(codes[i - 2].opcode == OpCodes.Ldarg_S && codes[i - 2].operand?.ToString() == "0"))
                        continue;

                    codes[i - 1] = new CodeInstruction(OpCodes.Ldsfld, BattleSelfField);
                    codes.Insert(i, new CodeInstruction(OpCodes.Call, GetMoveZoneFowFraction));
                    i++;
                }
                return codes;
            }
        }

        /// <summary>
        /// Vanilla clears can_attack_anyone only when unit.player == cur_player.
        /// Guest skips StartTurn while spectating — keep local-faction caches fresh (turret dots).
        /// </summary>
        [HarmonyPatch(typeof(UnitData), "OnMapUnitChange")]
        private static class Patch_OnMapUnitChange_AttackCache
        {
            private static void Postfix(UnitData __instance)
            {
                if (__instance?.player == null)
                    return;
                if (!GateUtil.LanArmed(out var plugin))
                    return;
                var local = plugin.Authority.TryGetLocalHumanPlayer();
                if (local == null)
                    return;
                if (__instance.player.fraction != local.fraction)
                    return;
                // Already cleared by vanilla when player==cur_player; still OK to null again.
                try { UnitDataAccess.ClearCanAttackAnyone(__instance); }
                catch { /* ignore */ }
            }
        }

        [HarmonyPatch(typeof(UnitData), "GetMoveZone")]
        private static class Patch_GetMoveZone_Fow
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                if (GetFraction == null || BattleSelfField == null || GetMoveZoneFowFraction == null)
                    return codes;

                // this.fraction → AcquireFOWMap  ⇒  this, GS_Battle.self → GetMoveZoneFowFraction
                // Own/ally keep INV-VIEW FOW; enemy threat previews keep owner FOW.
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

                    // ldarg_0 (unit) already at i-2; replace get_fraction with battle + helper.
                    codes[i - 1] = new CodeInstruction(OpCodes.Ldsfld, BattleSelfField);
                    codes.Insert(i, new CodeInstruction(OpCodes.Call, GetMoveZoneFowFraction));
                    // AcquireFOWMap shifted by +1 after Insert
                    i++;
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

                // During Host Accept / Host local CastSkill, VFX gates must use the casting
                // seat's FOW (cur_player), not the local spectator's INV-VIEW map.
                var observeFrac = AnnW.LanMp.Sync.SyncContext.SkillCastSuppressEmit &&
                                  __instance.cur_player != null
                    ? __instance.cur_player.fraction
                    : ViewUtil.GetUxViewFraction(__instance);

                if (!__instance.functions.Querry(GAME_FUNCTION.NoFOW) &&
                    !GameAPI.self.GetFOWMap(observeFrac).CanSeeUnit(pos))
                    result = false;

                __result = result;
                return false;
            }
        }
    }
}
