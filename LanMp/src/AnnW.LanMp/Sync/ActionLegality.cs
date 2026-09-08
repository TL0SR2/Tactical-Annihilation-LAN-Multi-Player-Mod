using System;
using System.Collections;
using System.Reflection;
using AnnW.LanMp.Protocol;
using ANNW;
using HarmonyLib;

namespace AnnW.LanMp.Sync
{
    /// <summary>
    /// Host Accept legality — sole board-geometry chokepoint for Guest DoAction / UnitMoved.
    /// See <see cref="IntentAcceptLegalityRules"/> for the FOW/geometry contract (INV-T / ADR-001).
    /// Do not call from <c>GuestMutationGate</c>.
    /// </summary>
    internal static class ActionLegality
    {
        private static readonly MethodInfo GetMoveZoneMi = AccessTools.Method(
            typeof(UnitData), "GetMoveZone", new[] { typeof(bool), typeof(bool), typeof(bool) });

        /// <summary>
        /// Host Accept DoAction: extras bind → hard select-zone → soft FOW visibility →
        /// hard other CanDoAction → hard CanAfford.
        /// </summary>
        public static bool TryValidateDoAction(
            UnitData unit,
            int actionCate,
            bool hasTarget,
            int targetX,
            int targetY,
            string extrasJson,
            out string error)
        {
            error = null;
            if (unit == null)
            {
                error = "unit-missing";
                return false;
            }

            var cate = (ActionCate)actionCate;
            var action = unit.GetAction(cate);
            if (action == null)
            {
                error = "no-action";
                return false;
            }

            PrepareActionUxContext(unit, cate, extrasJson);

            GameTileData tile = null;
            if (hasTarget)
            {
                var pos = new Inctor2(targetX, targetY);
                if (GameAPI.self != null)
                    tile = GameAPI.self.GetTile(pos);
                if (tile == null)
                {
                    error = "bad-target";
                    return false;
                }

                // Hard: GetSelectZone has no FOW — this is the over-range attack gate.
                if (!action.IsPosInSelectZone(unit.pos, tile, unit))
                {
                    error = "out-of-range";
                    return false;
                }
            }

            var reason = action.CanDoAction(tile, null);
            if (reason != REASON_CANTDO.OK)
            {
                if (IntentAcceptLegalityRules.IsSoftAcceptCantDoReason((int)reason))
                {
                    LanMpPlugin.Log?.LogWarning(
                        "[ActionLegality] Host soft-accept TARGET_NOT_VISIBLE unit=" +
                        unit.unit_id + " cate=" + cate);
                }
                else
                {
                    error = MapCantDoCode(reason);
                    return false;
                }
            }

            if (!action.CanAfford(tile))
            {
                error = "cant-afford";
                return false;
            }

            return true;
        }

        public static void PrepareActionUxContext(UnitData unit, ActionCate cate, string extrasJson)
        {
            if (unit == null)
                return;

            var action = unit.GetAction(cate);
            if (ActionExtrasCodec.TryGetTrainTemplateId(extrasJson, out var tplId) && action != null)
                TryBindTrainTemplate(action, tplId);

            if (ActionExtrasCodec.NeedsUnloadUnit((int)cate) &&
                ActionExtrasCodec.TryGetUnloadUnitId(extrasJson, out var cargoId))
            {
                try
                {
                    if (GS_Battle.self != null)
                        GS_Battle.self.ux_unload_unit = ResultAttachmentBridge.FindUnit(cargoId);
                }
                catch
                {
                    /* leave unset */
                }
            }
        }

        public static string CaptureExtrasForIntent(ActionCate cate, ActionData action)
        {
            try
            {
                if (ActionExtrasCodec.NeedsUnloadUnit((int)cate))
                {
                    var cargo = GS_Battle.self?.ux_unload_unit;
                    if (cargo != null)
                        return ActionExtrasCodec.FromUnloadUnitId(cargo.unit_id);
                }

                if (action?.train_template?.sd_unit != null)
                    return ActionExtrasCodec.FromTrainTemplate(action.train_template.sd_unit.name);

                if (ActionExtrasCodec.NeedsTrainTemplate((int)cate) &&
                    GS_Battle.self?.ux_unit_template?.sd_unit != null)
                    return ActionExtrasCodec.FromTrainTemplate(GS_Battle.self.ux_unit_template.sd_unit.name);
            }
            catch
            {
                /* omit */
            }
            return null;
        }

        public static void TryBindTrainTemplate(ActionData action, string templateId)
        {
            if (action == null || string.IsNullOrEmpty(templateId))
                return;
            try
            {
                if (action.train_template?.sd_unit != null &&
                    string.Equals(action.train_template.sd_unit.name, templateId, StringComparison.Ordinal))
                    return;

                var tpl = UnitTemplate.Acquire(templateId);
                if (tpl != null)
                    action.train_template = tpl;
            }
            catch
            {
                /* leave unset */
            }
        }

        /// <summary>
        /// Host Accept UnitMoved: GetMoveZone per <see cref="IntentAcceptLegalityRules"/>
        /// (no FOW cull). Zone miss is hard — soft-accept would re-open over-range Apply.
        /// </summary>
        public static bool TryValidateUnitMoved(
            UnitData unit,
            int targetX,
            int targetY,
            out string error)
        {
            error = null;
            if (unit == null)
            {
                error = "unit-missing";
                return false;
            }

            var dest = new Inctor2(targetX, targetY);
            if (GetMoveZoneMi == null)
            {
                error = "no-move-zone";
                return false;
            }

            // Stale UX temp blacklist on Host can shrink zones (same-faction / shared Player).
            try { unit.player?.temp_move_black?.Clear(); }
            catch { /* ignore */ }

            // PreferUnitOwnerFow: AcquireFOWMap still runs before cull_fow branch; keep owner
            // FOW if anything else reads the map. cull_fow:false skips CanWalk (authority geom).
            var prevFow = SyncContext.PreferUnitOwnerFowForMoveZone;
            SyncContext.PreferUnitOwnerFowForMoveZone = true;
            try
            {
                var zone = GetMoveZoneMi.Invoke(unit, new object[]
                {
                    IntentAcceptLegalityRules.HostMoveCullFriendly,
                    IntentAcceptLegalityRules.HostMoveNoCullTransport,
                    IntentAcceptLegalityRules.HostMoveCullFow
                }) as IList;
                if (zone == null)
                {
                    error = "no-move-zone";
                    return false;
                }

                if (ZoneContains(zone, dest))
                    return true;

                error = "out-of-move-range";
                return false;
            }
            catch
            {
                error = "no-move-zone";
                return false;
            }
            finally
            {
                SyncContext.PreferUnitOwnerFowForMoveZone = prevFow;
            }
        }

        private static bool ZoneContains(IList zone, Inctor2 dest)
        {
            for (var i = 0; i < zone.Count; i++)
            {
                var item = zone[i];
                if (item is Inctor2 p && p.x == dest.x && p.y == dest.y)
                    return true;
            }
            return false;
        }

        public static string MapCantDoCode(REASON_CANTDO reason)
        {
            switch (reason)
            {
                case REASON_CANTDO.TARGET_NOT_VISIBLE:
                    return "target-not-visible";
                case REASON_CANTDO.NO_ENOUGH_RES:
                    return "cant-afford";
                case REASON_CANTDO.FAC_NO_BP_LEFT:
                    return "no-factory-bp";
                case REASON_CANTDO.NO_UNLOAD_POWER:
                    return "no-unload-bp";
                case REASON_CANTDO.UNLOAD_CAN_NOT_STAY:
                    return "unload-cant-stay";
                case REASON_CANTDO.SHD_FULL:
                case REASON_CANTDO.SHD_FILL_TOO_LOW:
                    return "shield-full";
                case REASON_CANTDO.NO_TRAIN_SPACE:
                case REASON_CANTDO.CAN_NOT_SPAWN:
                    return "cant-spawn";
                default:
                    return "cant-do";
            }
        }

        public static string MapUserMessage(string code)
        {
            switch (code)
            {
                case "out-of-range":
                    return "目标超出有效射程";
                case "out-of-move-range":
                    return "无法移动到该位置";
                case "target-not-visible":
                    return "目标不可见";
                case "cant-afford":
                    return "资源不足";
                case "no-factory-bp":
                    return "工厂建造点不足";
                case "no-unload-bp":
                    return "卸载点数不足";
                case "unload-cant-stay":
                    return "该位置无法卸载";
                case "shield-full":
                    return "护盾不可充能";
                case "cant-spawn":
                    return "无法在此建造/生产";
                case "cant-do":
                case "bad-target":
                case "no-action":
                    return "无法执行该行动";
                default:
                    return null;
            }
        }
    }
}
