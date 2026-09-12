using System;
using System.Collections;
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
        private static readonly MethodInfo UnitReDrawMi =
            AccessTools.Method(typeof(UnitData), "ReDraw", new[] { typeof(bool) });

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
        /// Units alive locally before attach but absent from Host attachment (combat kills / orphans).
        /// Must run before Apply removes them.
        /// </summary>
        internal static List<UnitData> CollectMissingUnits(
            HashSet<int> idsBeforeApply,
            ResultAttachmentDto attach)
        {
            var list = new List<UnitData>();
            if (idsBeforeApply == null || idsBeforeApply.Count == 0)
                return list;

            var hostIds = new HashSet<int>();
            if (attach?.units != null)
            {
                foreach (var us in attach.units)
                {
                    if (us != null)
                        hostIds.Add(us.unitId);
                }
            }

            foreach (var id in idsBeforeApply)
            {
                if (hostIds.Contains(id))
                    continue;
                var unit = ResultAttachmentBridge.FindUnit(id);
                if (unit != null && !unit.dead)
                    list.Add(unit);
            }
            return list;
        }

        /// <summary>
        /// Presentation-only: mirror UnitData.Hurt → FUIM_FloatNumber.ShowAsDamage(damage/hp_max).
        /// Does not mutate HP / RNG.
        /// </summary>
        internal static void PresentHpDamageFloat(
            UnitData unit,
            float oldHp,
            float newHp,
            ManualLogSource log = null)
        {
            if (unit == null)
                return;
            float hpMax;
            try { hpMax = unit.hp_max.value; }
            catch { return; }

            if (!CombatPresentationRules.TryDamageRatio(oldHp, newHp, hpMax, out var ratio))
                return;

            try
            {
                FUIM_FloatNumber.CreateFloatText(unit.pos, check_fow: true)?.ShowAsDamage(ratio);
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] ShowAsDamage: " + ex.Message);
            }
        }

        /// <summary>
        /// Kick vanilla death VFX coroutine (GameController CoroutineObject). Caller must delay
        /// <see cref="CombatPresentationRules.DeathVisualLeadSecondsForChassisSize"/> before
        /// RemoveUnit/Dispose so size&gt;1 buildings can reach Event_DieExplode (debris).
        /// Returns the recommended yield seconds for this victim.
        /// </summary>
        internal static float KickUnitDeathVisual(
            UnitData victim,
            UnitData attacker,
            float damageHint,
            ManualLogSource log = null)
        {
            if (victim == null || GameAPI.self == null)
                return 0f;

            // Mirror UnitData.Die presentation prelude (without CreateWreck / authority).
            try
            {
                victim.dying = true;
                UnitReDrawMi?.Invoke(victim, new object[] { true });
            }
            catch { /* ignore */ }

            try
            {
                PresentHpDamageFloat(victim, victim.hp_cur, 0f, log);
            }
            catch { /* ignore */ }

            try
            {
                GameAPI.self.PlayUnitDeathAnimation(
                    victim.pos,
                    damageHint > 0.01f ? damageHint : victim.hp_cur,
                    victim,
                    DieReason.COMBAT,
                    attacker,
                    attacker != null ? attacker.player : null,
                    null);
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] PlayUnitDeathAnimation: " + ex.Message);
            }

            return DeathVisualLeadFor(victim);
        }

        /// <summary>Kick death visuals for all doomed units; return max lead seconds to yield.</summary>
        internal static float KickUnitDeathVisuals(
            System.Collections.Generic.IList<UnitData> doomed,
            UnitData attacker,
            ManualLogSource log = null)
        {
            var maxLead = 0f;
            if (doomed == null)
                return maxLead;
            for (var i = 0; i < doomed.Count; i++)
            {
                var victim = doomed[i];
                if (victim == null || victim.dead)
                    continue;
                var lead = KickUnitDeathVisual(victim, attacker, victim.hp_cur, log);
                if (lead > maxLead)
                    maxLead = lead;
            }
            return maxLead;
        }

        internal static float DeathVisualLeadFor(UnitData unit)
        {
            var size = 1;
            try
            {
                if (unit != null)
                    size = unit.size;
            }
            catch { size = 1; }
            return CombatPresentationRules.DeathVisualLeadSecondsForChassisSize(size);
        }

        /// <summary>
        /// Guest CastSkill presentation (ADR-003 R4 / M07 B7).
        /// Must NOT run vanilla <c>DoActionAni</c>: PrepareAction does <c>AutoSetPos().Value</c>
        /// on null tile (Nullable crash) and MultiTarget/Parallel spawn sibling
        /// <c>CoroutineObject.StartCoroutine</c> that <c>yield null</c> — poisons
        /// <c>SS_ANNW_Game.Update</c> (NRE / soft-lock). Board truth is Host attachment;
        /// Guest only plays safe VFX + bus cast cues.
        /// </summary>
        internal static IEnumerator CoKickSkillCastVisual(CommandDto cmd, ManualLogSource log = null)
        {
            try { BattleEventBus.self.TriggerSkillCastStarted(); }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] SkillCastStarted: " + ex.Message);
            }

            var battle = GS_Battle.self;
            Player caster = null;
            if (cmd != null && cmd.playerIndex >= 0 && battle?.all_player?.players != null)
            {
                foreach (var p in battle.all_player.players)
                {
                    if (p != null && p.index == cmd.playerIndex)
                    {
                        caster = p;
                        break;
                    }
                }
            }
            if (caster == null)
                caster = battle?.cur_player;

            var co = caster?.co_data;
            if (co != null && !string.IsNullOrEmpty(cmd?.extrasJson) &&
                (co.skill_action == null ||
                 co.skill == null ||
                 !string.Equals(co.skill.name, cmd.extrasJson, StringComparison.Ordinal)))
            {
                try
                {
                    if (co.skill == null ||
                        !string.Equals(co.skill.name, cmd.extrasJson, StringComparison.Ordinal))
                    {
                        var sd = SD_ANNW_SKILL.Get(cmd.extrasJson, true);
                        if (sd != null)
                            co.SetSKill(sd);
                    }
                }
                catch (Exception ex)
                {
                    log?.LogWarning("[Presentation] skill rebind: " + ex.Message);
                }
            }

            // Safe VFX only — never DoActionAni / nested CoroutineObject skill procs.
            var prevSkip = SyncContext.PresentationSkipActionCell;
            SyncContext.PresentationSkipActionCell = true;
            try
            {
                TryPlaySkillCastVfx(co?.skill_action, cmd, log);
                // One tick so FOW/UI can breathe before attach stamps the board.
                yield return AnnWCoroutine.NextTick;
            }
            finally
            {
                SyncContext.PresentationSkipActionCell = prevSkip;
            }

            try { BattleEventBus.self.TriggerSkillCastDone(); }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] SkillCastDone: " + ex.Message);
            }
        }

        private static void TryPlaySkillCastVfx(ActionData skill, CommandDto cmd, ManualLogSource log)
        {
            if (skill?.sd_action == null || cmd == null || !cmd.hasTarget)
                return;

            Inctor2 pos;
            try { pos = new Inctor2(cmd.targetX, cmd.targetY); }
            catch { return; }

            try
            {
                var battle = GS_Battle.self;
                if (battle == null)
                    return;
                var canSee = true;
                if (CanObserveMethod != null)
                    canSee = (bool)CanObserveMethod.Invoke(battle, new object[] { pos });
                if (!canSee)
                    return;

                var getWp = AccessTools.Method(typeof(SS_ANNW_Game), "GetWP", new[] { typeof(Inctor2) });
                if (getWp == null)
                    return;
                var wp = (Vector3)getWp.Invoke(null, new object[] { pos });
                var vfx = SingletonMonoAuto<VFX>.self;
                if (vfx == null)
                    return;

                var vfxGlobal = skill.sd_action.vfx_global;
                if (!string.IsNullOrEmpty(vfxGlobal))
                    vfx.CreateVFX(vfxGlobal, wp, null, null);
                var vfxHit = skill.sd_action.vfx_hit;
                if (!string.IsNullOrEmpty(vfxHit))
                    vfx.CreateVFX(vfxHit, wp, null, null);
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] skill VFX: " + ex.Message);
            }
        }

        /// <summary>
        /// Legacy fire-and-forget bus cue — prefer <see cref="CoKickSkillCastVisual"/> on Guest Apply.
        /// </summary>
        internal static void KickSkillCastCue(ManualLogSource log = null)
        {
            try { BattleEventBus.self.TriggerSkillCastStarted(); }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] SkillCastStarted: " + ex.Message);
            }
        }

        private static readonly MethodInfo GetEffectZoneMi = FindGetEffectZone();

        private static MethodInfo FindGetEffectZone()
        {
            var mi = AccessTools.Method(typeof(ActionData), "GetEffectZone",
                new[] { typeof(Inctor2), typeof(Inctor2?), typeof(bool) });
            if (mi != null)
                return mi;
            // Fallback: any GetEffectZone(Inctor2, ...)
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ActionData)))
            {
                if (m == null || m.Name != "GetEffectZone")
                    continue;
                var ps = m.GetParameters();
                if (ps.Length >= 1 && ps[0].ParameterType == typeof(Inctor2))
                    return m;
            }
            return AccessTools.Method(typeof(ActionData), "GetEffectZone");
        }

        /// <summary>
        /// Fire weapon/mesh action presentation without DoActionCell (no RNG / spawn).
        /// Returns seconds the caller should yield on AnnW CoroutineObject (float wait).
        /// Single-shot only — prefer <see cref="CoKickDoActionVisual"/> for mul_tar / PARREL.
        /// </summary>
        internal static float KickDoActionVisual(
            UnitData unit,
            ActionCate cate,
            GameTileData tile,
            ManualLogSource log = null)
        {
            if (!TryBeginDoActionVisual(unit, cate, tile, log, out var action, out var target, out var wait))
                return 0f;

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

            TryPlayActionLaunch(unit, action, target, log);
            return wait;
        }

        /// <summary>
        /// Attach-only DoAction presentation: loop Event_DoActionAni over GetEffectZone when
        /// mul_tar / PARREL (vanilla DoAction_MultiTarget / DoAction_Parallel). No DoActionCell.
        /// </summary>
        internal static IEnumerator CoKickDoActionVisual(
            UnitData unit,
            ActionCate cate,
            GameTileData tile,
            ManualLogSource log = null)
        {
            if (!TryBeginDoActionVisual(unit, cate, tile, log, out var action, out var target, out var wait))
                yield break;

            var shots = ResolveDoActionAniTargets(action, target, unit, log);
            var interval = 0.2f;
            try
            {
                var settingsInterval = -1f;
                if (action.sd_action?.settings != null && action.sd_action.settings.Has("interval"))
                    settingsInterval = action.sd_action.settings.GetAsFloat("interval");
                interval = PresentationRules.ResolveMultiShotInterval(shots.Count, settingsInterval);
            }
            catch { /* keep default */ }

            try { unit.in_animation = true; }
            catch { /* ignore */ }

            for (var i = 0; i < shots.Count; i++)
            {
                var shotTile = shots[i];
                if (shotTile == null)
                    continue;
                try { unit.Event_SetAiming?.Invoke(shotTile.pos); }
                catch { /* ignore */ }

                var shotWait = wait;
                try
                {
                    if (unit.Event_GetActionTime != null)
                    {
                        var list = unit.Event_GetActionTime.GetInvocationList();
                        for (var d = 0; d < list.Length; d++)
                        {
                            if (list[d] is Func<GameTileData, ActionData, int, float> fn)
                                shotWait = Mathf.Max(shotWait, fn(shotTile, action, i));
                        }
                    }
                }
                catch { /* keep */ }
                if (shotWait < 0.15f)
                    shotWait = 0.2f;
                if (shotWait > 2.5f)
                    shotWait = 2.5f;

                try { unit.Event_DoActionAni?.Invoke(shotTile, action, i); }
                catch (Exception ex)
                {
                    log?.LogWarning("[Presentation] Event_DoActionAni[" + i + "]: " + ex.Message);
                }

                if (i == 0)
                    TryPlayActionLaunch(unit, action, shotTile, log);

                if (i + 1 < shots.Count && interval > 0.001f)
                    yield return interval;
                else if (i + 1 >= shots.Count && shotWait > 0.001f)
                    yield return shotWait;
            }
        }

        private static bool TryBeginDoActionVisual(
            UnitData unit,
            ActionCate cate,
            GameTileData tile,
            ManualLogSource log,
            out ActionData action,
            out GameTileData target,
            out float wait)
        {
            action = null;
            target = null;
            wait = 0f;
            if (unit == null)
                return false;
            if (cate == ActionCate.NONE || cate == ActionCate.SET_TRAIN_POS)
                return false;

            try { action = unit.GetAction(cate); }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] GetAction: " + ex.Message);
            }
            if (action == null)
                return false;

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

            target = tile;
            if (target == null)
            {
                try { target = GameTileData.Get(unit.pos); }
                catch { target = null; }
            }

            wait = 0.35f;
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
            return true;
        }

        private static List<GameTileData> ResolveDoActionAniTargets(
            ActionData action,
            GameTileData primary,
            UnitData unit,
            ManualLogSource log)
        {
            var list = new List<GameTileData>();
            if (primary != null)
                list.Add(primary);

            try
            {
                var sd = action?.sd_action;
                if (sd == null || primary == null)
                    return list;

                var loop = PresentationRules.ShouldLoopDoActionAni(
                    sd.mul_tar,
                    sd.traj == TrajShape.PARREL);
                if (!loop || GetEffectZoneMi == null)
                    return list;

                object zoneObj = null;
                try
                {
                    var ps = GetEffectZoneMi.GetParameters();
                    object[] args;
                    if (ps.Length >= 3)
                        args = new object[] { primary.pos, null, false };
                    else if (ps.Length == 2)
                        args = new object[] { primary.pos, null };
                    else
                        args = new object[] { primary.pos };
                    zoneObj = GetEffectZoneMi.Invoke(action, args);
                }
                catch (Exception ex)
                {
                    log?.LogWarning("[Presentation] GetEffectZone: " + ex.Message);
                    return list;
                }

                if (!(zoneObj is List<Inctor2> zone) || zone.Count == 0)
                    return list;

                list.Clear();
                for (var i = 0; i < zone.Count; i++)
                {
                    GameTileData t = null;
                    try { t = GameTileData.Get(zone[i]); }
                    catch { t = null; }
                    if (t != null)
                        list.Add(t);
                }
                if (list.Count == 0 && primary != null)
                    list.Add(primary);
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Presentation] multi-shot targets: " + ex.Message);
                if (list.Count == 0 && primary != null)
                    list.Add(primary);
            }

            return list;
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
