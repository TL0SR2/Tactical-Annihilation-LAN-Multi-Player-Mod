using System;
using System.Collections.Generic;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using BepInEx.Logging;
using UnityEngine;

namespace AnnW.LanMp.Presentation
{
    /// <summary>
    /// Visual-only refresh after attach-only command apply (no authoritative state mutation).
    /// INV: Guest attach-only must still fire vanilla Event_DoActionAni / build / spawn cues (ADR-001 R4).
    /// </summary>
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

        /// <summary>
        /// Fire weapon/mesh action presentation without DoActionCell (no RNG / spawn).
        /// Returns seconds the caller should yield on AnnW CoroutineObject (float wait).
        /// </summary>
        internal static float KickDoActionVisual(
            UnitData unit,
            ActionCate cate,
            GameTileData tile,
            ManualLogSource log = null)
        {
            if (unit == null)
                return 0f;
            if (cate == ActionCate.NONE || cate == ActionCate.SET_TRAIN_POS)
                return 0f;

            ActionData action = null;
            try { action = unit.GetAction(cate); }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] GetAction: " + ex.Message);
            }
            if (action == null)
                return 0f;

            try
            {
                if (tile != null)
                    unit.Event_SetAiming?.Invoke(tile.pos);
            }
            catch { /* ignore */ }

            try { BattleEventBus.self.TriggerUnitActionStart(unit, cate); }
            catch { /* ignore */ }

            try { unit.Event_ActionsStart?.Invoke(0, false); }
            catch { /* ignore */ }

            var target = tile;
            if (target == null)
            {
                try { target = GameTileData.Get(unit.pos); }
                catch { target = null; }
            }

            var wait = 0.35f;
            try
            {
                if (target != null && unit.Event_GetActionTime != null)
                {
                    var list = unit.Event_GetActionTime.GetInvocationList();
                    for (var i = 0; i < list.Length; i++)
                    {
                        if (list[i] is Func<GameTileData, ActionData, int, float> fn)
                            wait = Mathf.Max(wait, fn(target, action, 0));
                    }
                }
            }
            catch { /* keep default */ }

            try
            {
                var post = action.GetPostActionWaitTime();
                if (post > 0.01f)
                    wait = Mathf.Max(wait, post);
            }
            catch { /* ignore */ }

            if (wait < 0.2f)
                wait = 0.35f;
            if (wait > 2.5f)
                wait = 2.5f;

            try
            {
                unit.in_animation = true;
                if (target != null)
                    unit.Event_DoActionAni?.Invoke(target, action, 0);
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] Event_DoActionAni: " + ex.Message);
            }

            return wait;
        }

        internal static void FinishDoActionVisual(UnitData unit)
        {
            if (unit == null)
                return;
            try { unit.in_animation = false; }
            catch { /* ignore */ }
            try { unit.Event_ResetAiming?.Invoke(); }
            catch { /* ignore */ }
            try { unit.Event_ActionsEnd?.Invoke(0, false); }
            catch { /* ignore */ }
            try { unit.Event_ActionEnd?.Invoke(); }
            catch { /* ignore */ }
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
                catch (Exception ex)
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

                // TRAIN / QUICK_BUILD_MINER: factory → spawn slide.
                if ((cate == ActionCate.TRAIN || cate == ActionCate.QUICK_BUILD_MINER) &&
                    actorId >= 0 && GameAPI.self != null)
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
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] new unit: " + ex.Message);
            }
        }
    }
}
