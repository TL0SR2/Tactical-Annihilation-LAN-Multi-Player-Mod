using System.Collections;
using System.Reflection;
using ANNW;
using HarmonyLib;

namespace AnnW.LanMp.Sync
{
    /// <summary>
    /// Host-authoritative legality for Guest DoAction / UnitMoved Intents.
    /// Vanilla UX gates range via GetActionZone + CanDoAction; ExecuteAction does not re-check,
    /// so Intent Accept must — otherwise a desynced Guest board can land over-range hits.
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

            GameTileData tile = null;
            if (hasTarget)
            {
                var pos = new Inctor2(targetX, targetY);
                // GameTileData.GetValid is internal — use public GameAPI.GetTile.
                if (GameAPI.self != null)
                    tile = GameAPI.self.GetTile(pos);
                if (tile == null)
                {
                    error = "bad-target";
                    return false;
                }

                // Public equivalent of UX GetActionZone.Contains(target).
                if (!action.IsPosInSelectZone(unit.pos, tile, unit))
                {
                    error = "out-of-range";
                    return false;
                }
            }

            var reason = action.CanDoAction(tile, null);
            if (reason != REASON_CANTDO.OK)
            {
                error = reason == REASON_CANTDO.TARGET_NOT_VISIBLE
                    ? "target-not-visible"
                    : "cant-do";
                return false;
            }

            if (!action.CanAfford(tile))
            {
                error = "cant-afford";
                return false;
            }

            return true;
        }

        public static bool TryValidateUnitMoved(UnitData unit, int targetX, int targetY, out string error)
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
                // Match vanilla defaults: cull_friendly, no_cull_transport, cull_fow.
                zone = GetMoveZoneMi.Invoke(unit, new object[] { true, true, true }) as IList;
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
