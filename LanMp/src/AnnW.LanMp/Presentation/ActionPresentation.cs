using System;
using System.Collections.Generic;
using System.Reflection;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using ANNW;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AnnW.LanMp.Presentation
{
    /// <summary>
    /// Visual-only refresh after attach-only command apply (no authoritative state mutation).
    /// INV: Guest attach-only must still fire vanilla Event_DoActionAni / build / spawn cues (ADR-001 R4),
    /// plus launch/hit SFX that normally live in PrepareAction / DoAction_* (not invoked on attach-only).
    /// </summary>
    internal static class ActionPresentation
    {
        // GS_Battle.CanObserve + SoundUtils launch/hit are assembly-internal after game update.
        private static readonly MethodInfo CanObserveMethod =
            AccessTools.Method(typeof(GS_Battle), "CanObserve", new[] { typeof(Inctor2) });
        private static readonly MethodInfo PlayLaunchMethod =
            AccessTools.Method(typeof(SoundUtils), "PlaySound_ActionLaunch", new[] { typeof(ActionData) });
        private static readonly MethodInfo PlayHitMethod =
            AccessTools.Method(typeof(SoundUtils), "PlaySound_ActionHit", new[] { typeof(SD_ANNW_ACTION) });

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

            // Vanilla launch SFX lives in ActionData.PrepareAction — attach-only never calls it.
            // Do not invoke PrepareAction (TriggerPreDoAction / zone cache side effects).
            TryPlayActionLaunch(unit, action, target, log);

            return wait;
        }

        /// <param name="cate">When set, play hit SFX (vanilla DoAction_* path skipped by attach-only).</param>
        /// <param name="tile">Target tile for CanObserve / ShouldPlaySound gate (same as PrepareAction).</param>
        internal static void FinishDoActionVisual(
            UnitData unit,
            ActionCate cate = ActionCate.NONE,
            GameTileData tile = null,
            ManualLogSource log = null)
        {
            if (unit == null)
                return;

            if (cate != ActionCate.NONE && cate != ActionCate.SET_TRAIN_POS)
            {
                ActionData action = null;
                try { action = unit.GetAction(cate); }
                catch (Exception ex)
                {
                    log?.LogWarning("[Presentation] GetAction(hit): " + ex.Message);
                }
                TryPlayActionHit(unit, action, tile ?? SafeTileAt(unit), log);
            }

            try { unit.in_animation = false; }
            catch { /* ignore */ }
            try { unit.Event_ResetAiming?.Invoke(); }
            catch { /* ignore */ }
            try { unit.Event_ActionsEnd?.Invoke(0, false); }
            catch { /* ignore */ }
            try { unit.Event_ActionEnd?.Invoke(); }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Mirror PrepareAction audible gate: CanObserve(target|owner) then ShouldPlaySound.
        /// </summary>
        private static bool IsActionAudible(UnitData owner, GameTileData gtd)
        {
            var battle = GS_Battle.self;
            if (battle == null)
                return false;

            var audible = false;
            try
            {
                if (gtd != null && CallCanObserve(battle, gtd.pos))
                    audible = true;
            }
            catch { /* ignore */ }

            try
            {
                if (owner != null && CallCanObserve(battle, owner.pos))
                    audible = true;
            }
            catch { /* ignore */ }

            if (!audible)
                return false;

            try
            {
                Inctor2? pos = null;
                if (gtd != null)
                    pos = gtd.pos;
                return battle.ShouldPlaySound(pos);
            }
            catch
            {
                return true;
            }
        }

        private static bool CallCanObserve(GS_Battle battle, Inctor2 pos)
        {
            if (CanObserveMethod == null || battle == null)
                return true;
            try
            {
                return (bool)CanObserveMethod.Invoke(battle, new object[] { pos });
            }
            catch
            {
                return true;
            }
        }

        private static void TryPlayActionLaunch(
            UnitData unit,
            ActionData action,
            GameTileData tile,
            ManualLogSource log)
        {
            if (action == null || !IsActionAudible(unit, tile))
                return;
            try
            {
                if (PlayLaunchMethod != null)
                    PlayLaunchMethod.Invoke(null, new object[] { action });
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] ActionLaunch sfx: " + ex.Message);
            }
        }

        private static void TryPlayActionHit(
            UnitData unit,
            ActionData action,
            GameTileData tile,
            ManualLogSource log)
        {
            if (action?.sd_action == null || !IsActionAudible(unit, tile))
                return;
            try
            {
                if (PlayHitMethod != null)
                    PlayHitMethod.Invoke(null, new object[] { action.sd_action });
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] ActionHit sfx: " + ex.Message);
            }
        }

        private static GameTileData SafeTileAt(UnitData unit)
        {
            if (unit == null)
                return null;
            try { return GameTileData.Get(unit.pos); }
            catch { return null; }
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
