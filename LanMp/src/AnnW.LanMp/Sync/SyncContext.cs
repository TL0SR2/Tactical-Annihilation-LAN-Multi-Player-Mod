using System;
using AnnW.LanMp.Protocol;

namespace AnnW.LanMp.Sync
{
    /// <summary>Prevents re-entrant network emit while applying a remote Command.</summary>
    public static class SyncContext
    {
        public static bool SuppressNetworkEmit { get; set; }
        public static bool ApplyingRemoteCommand { get; set; }

        /// <summary>
        /// True only while <see cref="CommandApplyQueue"/> is inside <c>MoveNext</c> of an apply body.
        /// Guest UX clicks happen between yields (flag false) and must not ride Suppress/Applying.
        /// </summary>
        public static bool InApplyEnumerator { get; set; }

        /// <summary>When set, next GameAPI.CreateUnit remaps to this id (then cleared).</summary>
        public static int? ForcedUnitId { get; set; }

        /// <summary>Guest CreateUnit allowed only when applying an authoritative CreateUnit command.</summary>
        public static bool AllowForcedCreate { get; set; }

        /// <summary>
        /// Host Intent Accept: GetMoveZone / CanDoAction AcquireFOWMap use unit-owner FOW
        /// (not INV-VIEW local viewer). Replaces historical soft TARGET_NOT_VISIBLE accept —
        /// Accept FOW is authoritative SEEN check on the acting faction map.
        /// See IntentAcceptLegalityRules.
        /// </summary>
        public static bool PreferUnitOwnerFowForMoveZone { get; set; }

        /// <summary>
        /// After MatchEnd payload is applied — allow vanilla <c>SS_ANNW_Game.EndGame</c>
        /// (proc_EndGame settlement UI) once. LAN Prefix otherwise blocks / rebroadcasts.
        /// </summary>
        public static bool AllowVanillaEndGameUi { get; set; }

        /// <summary>
        /// Guest CastSkill presentation runs vanilla <c>DoActionAni</c> for VFX/timing only;
        /// <c>DoActionCell</c> must no-op (ADR-003 — Host attachment is board truth).
        /// </summary>
        public static bool PresentationSkipActionCell { get; set; }

        /// <summary>
        /// Host turn SafePump re-entrancy guard — nested StartNextPlayerTurn/EndPlayerTurn
        /// must not wrap again while an outer turn pump is active.
        /// </summary>
        public static bool InHostTurnSafePump { get; set; }

        /// <summary>
        /// Host CO skill cast in progress — Bus CreateUnit/DoAction fold into CastSkill attach.
        /// Independent of <see cref="SuppressNetworkEmit"/> (Accept must not be cleared by CastDone).
        /// </summary>
        public static bool SkillCastSuppressEmit { get; set; }

        public static IDisposable BeginRemoteApply()
        {
            return new Scope(remote: true);
        }

        public static IDisposable BeginLocalAuthoritativeEmit()
        {
            return new Scope(remote: false);
        }

        public static void ForceUnitId(UnitData unit, int forcedId)
        {
            if (unit == null || forcedId <= 0 || unit.unit_id == forcedId)
                return;
            var all = GS_Battle.self?.all_unit;
            if (all == null)
                return;
            try
            {
                var t = all.GetType();
                t.GetMethod("UnregID", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(all, new object[] { unit });
                unit.unit_id = forcedId;
                t.GetMethod("RegID", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(all, new object[] { unit });
                var cursor = t.GetField("unit_id_cursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (cursor != null)
                {
                    var cur = (int)cursor.GetValue(all);
                    if (forcedId > cur)
                        cursor.SetValue(all, forcedId);
                }
            }
            catch (Exception)
            {
                // Best-effort remap.
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly bool _remote;
            private readonly bool _prevSuppress;
            private readonly bool _prevApplying;

            public Scope(bool remote)
            {
                _remote = remote;
                _prevSuppress = SuppressNetworkEmit;
                _prevApplying = ApplyingRemoteCommand;
                if (remote)
                {
                    SuppressNetworkEmit = true;
                    ApplyingRemoteCommand = true;
                }
            }

            public void Dispose()
            {
                SuppressNetworkEmit = _prevSuppress;
                ApplyingRemoteCommand = _prevApplying;
            }
        }
    }
}
