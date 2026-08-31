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
                if (IntentValidateRules.IsUnitSpentForIntent(intentKind, unit.moved, unit.actioned))
                    return false;
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
                // EQ move→build: UnitMoved await is open — stash DoAction for after Command lands.
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
