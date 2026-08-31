using System;
using AnnW.LanMp.Protocol;

namespace AnnW.LanMp.Sync
{
    /// <summary>Prevents re-entrant network emit while applying a remote Command.</summary>
    public static class SyncContext
    {
        public static bool SuppressNetworkEmit { get; set; }
        public static bool ApplyingRemoteCommand { get; set; }

        /// <summary>When set, next GameAPI.CreateUnit remaps to this id (then cleared).</summary>
        public static int? ForcedUnitId { get; set; }

        /// <summary>Guest CreateUnit allowed only when applying an authoritative CreateUnit command.</summary>
        public static bool AllowForcedCreate { get; set; }

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
