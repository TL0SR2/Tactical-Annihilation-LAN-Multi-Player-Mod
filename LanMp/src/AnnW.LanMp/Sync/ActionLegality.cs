using System;
using System.Collections;
using System.Reflection;
using AnnW.LanMp.Protocol;
using ANNW;
using HarmonyLib;

namespace AnnW.LanMp.Sync
{
    /// <summary>
    /// Host-authoritative legality for Guest DoAction / UnitMoved Intents.
    ///
    /// Invariant: anything UX holds only in globals / ActionData fields that Host Validate
    /// would not see must travel in Intent.extrasJson and be rebound via
    /// <see cref="PrepareActionUxContext"/> before CanDoAction / ExecuteAction.
    ///
    /// UX-context matrix (CanDoAction / DoActionCell):
    /// | Cate            | Extra dep                         | Fix |
    /// | BUILD / TRAIN   | ActionData.train_template         | extras = SD unit name |
    /// | UNLOAD_SINGLE   | GS_Battle.ux_unload_unit          | extras = u:{unitId} |
    /// | UNLOAD (auto)   | board transport list + tile       | none |
    /// | ATTACK          | tile.GetUnit / CanHurtTarget      | board; FOW soft-allow |
    /// | REPAIR/HELP/…   | base CanDoAction                  | FOW soft-allow |
    /// | SHIELD_GEN      | board shield fill state           | none |
    /// | UnitMoved       | GetMoveZone                       | UX args / Host no FOW cull |
    /// </summary>
    internal static class ActionLegality
    {
        private static readonly MethodInfo GetMoveZoneMi = AccessTools.Method(
            typeof(UnitData), "GetMoveZone", new[] { typeof(bool), typeof(bool), typeof(bool) });

        public static bool TryValidateDoAction(
            UnitData unit,
            int actionCate,
            bool hasTarget,
            int targetX,
            int targetY,
            out string error) =>
            TryValidateDoAction(unit, actionCate, hasTarget, targetX, targetY, null, out error);

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

                if (!action.IsPosInSelectZone(unit.pos, tile, unit))
                {
                    error = "out-of-range";
                    return false;
                }
            }

            var reason = action.CanDoAction(tile, null);
            if (reason != REASON_CANTDO.OK)
            {
                // FOW is INV-VIEW; Host board is Intent truth.
                if (reason == REASON_CANTDO.TARGET_NOT_VISIBLE)
                {
                    /* allow */
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

        /// <summary>
        /// Rebind UX-only fields from Intent extras before Validate or ExecuteAction.
        /// Safe to call repeatedly; no-ops when extras empty / cate needs nothing.
        /// </summary>
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
                    /* leave unset — CanDoAction will reject */
                }
            }
        }

        /// <summary>Guest emit: snapshot UX globals / action fields into extrasJson.</summary>
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

        public static bool TryValidateUnitMoved(UnitData unit, int targetX, int targetY, out string error) =>
            TryValidateUnitMoved(unit, targetX, targetY, forHostAccept: false, out error);

        public static bool TryValidateUnitMoved(
            UnitData unit,
            int targetX,
            int targetY,
            bool forHostAccept,
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

            IList zone;
            try
            {
                // UX PrepareMoveOp: (false, true, true). Host Accept: no FOW cull.
                var cullFow = !forHostAccept;
                zone = GetMoveZoneMi.Invoke(unit, new object[] { false, true, cullFow }) as IList;
            }
            catch
            {
                error = "no-move-zone";
                return false;
            }

            if (zone == null)
            {
                error = "no-move-zone";
                return false;
            }

            for (var i = 0; i < zone.Count; i++)
            {
                if (zone[i] is Inctor2 p && p.Equals(dest))
                    return true;
            }

            error = "out-of-move-range";
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
