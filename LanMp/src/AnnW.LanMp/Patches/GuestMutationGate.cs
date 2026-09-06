using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using ANNW;
using UnityEngine;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// ADR-001 single-center lock: Guest never mutates battle state locally in play phase.
    /// All Guest UX/AI/EQ/AutoCmd attempts become Intent → Host Validate+Apply → Command.
    /// Host local play is allowed; Host emits via EventBus (see CommandSyncService HostEmit checklist).
    /// </summary>
    internal static class GuestMutationGate
    {
        internal enum Kind
        {
            DoAction,
            UnitMoved,
            EndTurn,
            Undo,
            CastSkill
        }

        /// <summary>
        /// true = run original method (Host local / remote apply / setup / non-LAN).
        /// false = Guest blocked or Intent already submitted — caller must skip local mutation.
        /// </summary>
        internal static bool AllowLocalMutation(
            Kind kind,
            UnitData unit = null,
            ActionCate? cate = null,
            Inctor2? target = null,
            Inctor2? from = null,
            string extrasJson = null)
        {
            if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                return true;

            if (!GateUtil.LanArmed(out var plugin))
                return true;

            // Map setup: both peers simulate locally from the same start seed.
            if (!GateUtil.IsBattlePlayPhase())
                return true;

            // Host path (a): mutate locally; CommandSyncService EventBus emits Commands.
            if (plugin.Net.Role == PeerRole.Host)
                return true;

            // --- Guest play phase: never mutate; Intent only ---
            if (kind == Kind.DoAction || kind == Kind.UnitMoved)
            {
                if (GateUtil.ShouldBlockUnit(unit, out var reason))
                {
                    GateUtil.Toast(reason);
                    return false;
                }

                var intentKind = KindToIntent(kind);
                var cateVal = cate.HasValue ? (int)cate.Value : -1;
                if (IntentValidateRules.IsUnitSpentForIntent(intentKind, unit.moved, unit.actioned, cateVal))
                    return false;

                // Move range: safe to check at current pos. DoAction must wait — EQ stashes
                // attack-from-destination while unit is still at the old tile locally.
                if (kind == Kind.UnitMoved && target.HasValue)
                {
                    if (!ActionLegality.TryValidateUnitMoved(
                            unit, target.Value.x, target.Value.y, out var moveErr))
                    {
                        var msg = ActionLegality.MapUserMessage(moveErr);
                        if (!string.IsNullOrEmpty(msg))
                            GateUtil.Toast(msg);
                        return false;
                    }
                }
            }
            else if (kind == Kind.EndTurn || kind == Kind.Undo || kind == Kind.CastSkill)
            {
                var battle = GS_Battle.self;
                if (battle?.cur_player != null &&
                    plugin.Authority.ShouldBlockLocalInput(battle.cur_player.index))
                    return false; // silent spectate
            }

            if (!GateUtil.GuestMayEmitIntent(plugin))
            {
                // EQ move→action: UnitMoved await is open — stash DoAction for after Command lands.
                // Do not range-check here (unit not yet at destination on Guest).
                if (kind == Kind.DoAction)
                {
                    var follow = plugin.Sync.BuildIntent(
                        KindToIntent(kind), unit, cate, target, from);
                    if (!string.IsNullOrEmpty(extrasJson))
                        follow.extrasJson = extrasJson;
                    if (plugin.Sync.TryStashGuestFollowUp(follow))
                        return false;
                }
                return false;
            }

            // Direct DoAction (no pending Move): fail-fast with Host-identical legality.
            if (kind == Kind.DoAction)
            {
                var cateVal = cate.HasValue ? (int)cate.Value : -1;
                var hasTarget = target.HasValue;
                var tx = hasTarget ? target.Value.x : 0;
                var ty = hasTarget ? target.Value.y : 0;
                if (!ActionLegality.TryValidateDoAction(
                        unit, cateVal, hasTarget, tx, ty, out var legalErr))
                {
                    var msg = ActionLegality.MapUserMessage(legalErr);
                    if (!string.IsNullOrEmpty(msg))
                        GateUtil.Toast(msg);
                    return false;
                }
            }

            var intent = plugin.Sync.BuildIntent(
                KindToIntent(kind),
                unit,
                cate,
                target,
                from);
            if (!string.IsNullOrEmpty(extrasJson))
                intent.extrasJson = extrasJson;

            // Collapse duplicate Prefix hits (DoMove+WithAni / WithAni+DoActionAni) within one frame.
            var dedupe = intent.kind + ":" + intent.netUnitId + ":" + intent.actionCate + ":" +
                         intent.hasTarget + ":" + intent.targetX + "," + intent.targetY;
            if (dedupe == _lastDedupeKey && Time.unscaledTime - _lastDedupeAt < 0.2f)
                return false;

            _lastDedupeKey = dedupe;
            _lastDedupeAt = Time.unscaledTime;

            LanMpPlugin.Log?.LogInfo(
                $"[Gate] Guest Intent {intent.kind} unit={intent.netUnitId} cate={intent.actionCate} hasTarget={intent.hasTarget} ({intent.targetX},{intent.targetY}) tpl={intent.extrasJson}");
            plugin.Sync.SubmitIntent(intent, guestOptimisticApply: false);
            return false;
        }

        private static string _lastDedupeKey;
        private static float _lastDedupeAt;

        private static string KindToIntent(Kind kind)
        {
            switch (kind)
            {
                case Kind.UnitMoved: return "UnitMoved";
                case Kind.EndTurn: return "EndTurn";
                case Kind.Undo: return "Undo";
                case Kind.CastSkill: return "CastSkill";
                default: return "DoAction";
            }
        }
    }
}
