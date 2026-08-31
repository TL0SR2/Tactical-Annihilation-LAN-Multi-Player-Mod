using System.Collections.Generic;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using BepInEx.Logging;

namespace AnnW.LanMp.Presentation
{
    /// <summary>Visual-only refresh after attach-only command apply (no state mutation).</summary>
    internal static class ActionPresentation
    {
        internal static HashSet<int> SnapshotAliveIds()
        {
            var set = new HashSet<int>();
            var alive = GS_Battle.self?.all_unit?.units_alive;
            if (alive == null)
                return set;
            foreach (var u in alive)
            {
                if (u != null)
                    set.Add(u.unit_id);
            }
            return set;
        }

        internal static void AfterAttachApply(
            ResultAttachmentDto attach,
            ManualLogSource log,
            CommandDto cmd = null,
            HashSet<int> idsBeforeApply = null)
        {
            if (attach?.units == null)
                return;

            var focusId = cmd != null ? cmd.netUnitId : -1;
            var cate = cmd != null ? (ActionCate)cmd.actionCate : ActionCate.NONE;

            foreach (var us in attach.units)
            {
                if (us == null)
                    continue;
                var unit = ResultAttachmentBridge.FindUnit(us.unitId);
                if (unit == null)
                    continue;
                var wasNew = idsBeforeApply != null && !idsBeforeApply.Contains(us.unitId);
                try
                {
                    typeof(UnitData).GetMethod(
                        "ReDraw",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic)?.Invoke(unit, new object[] { false });
                    if (unit.building)
                        unit.Event_Repaired?.Invoke();
                    unit.Event_UpdatePos?.Invoke();

                    if (wasNew)
                        PresentNewUnit(unit, cate, focusId, log);
                }
                catch (System.Exception ex)
                {
                    log?.LogWarning("[Presentation] unit refresh " + us.unitId + ": " + ex.Message);
                }
            }

            try { BattleEventBus.self.TriggerBattlefieldChanged(); }
            catch { /* ignore */ }
            try { BattleEventBus.self.TriggerFOWChanged(); }
            catch { /* ignore */ }
            RemoteTurnPresentation.RefreshLocalVision(log);
        }

        private static void PresentNewUnit(UnitData unit, ActionCate cate, int actorId, ManualLogSource log)
        {
            try
            {
                if (unit.building)
                {
                    try { BattleEventBus.self.TriggerUnitBuildStarted(unit); }
                    catch { /* ignore */ }
                }

                // TRAIN: play factory → spawn visual when we know the factory actor.
                if (cate == ActionCate.TRAIN && actorId >= 0 && GameAPI.self != null)
                {
                    var fac = ResultAttachmentBridge.FindUnit(actorId);
                    if (fac != null && fac.unit_id != unit.unit_id)
                    {
                        var from = fac.pos;
                        var to = unit.pos;
                        GameAPI.self.MoveUnitInstantly(unit, from);
                        GameAPI.self.MoveUnitVisual(unit, from, to, 0.35f);
                    }
                }
            }
            catch (System.Exception ex)
            {
                log?.LogWarning("[Presentation] new unit: " + ex.Message);
            }
        }
    }
}
