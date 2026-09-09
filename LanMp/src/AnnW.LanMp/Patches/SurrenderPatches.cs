using System.Collections.Generic;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using AnnW.LanMp.Ui;
using ANNW;
using HarmonyLib;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// LAN surrender → seat defeat + spectate (same path as wipe-out), not EndGame(false).
    /// Vanilla <c>SS_ANNW_Game.Surrender</c> only calls EndGame — Guest was a no-op; Host
    /// wrongly ended the whole match without marking defeated.
    /// </summary>
    internal static class SurrenderPatches
    {
        [HarmonyPatch(typeof(SS_ANNW_Game), "Surrender")]
        private static class Patch_Surrender
        {
            private static bool Prefix()
            {
                if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                    return true;
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (plugin.Authority.MatchSettled)
                    return false;

                var local = plugin.Authority.TryGetLocalHumanPlayer();
                if (local == null)
                {
                    GateUtil.Toast("无法投降");
                    return false;
                }
                if (local.defeated)
                {
                    GateUtil.Toast("本席已战败");
                    return false;
                }

                if (plugin.Net.Role == PeerRole.Guest)
                {
                    if (!GateUtil.GuestMayEmitIntent(plugin))
                    {
                        GateUtil.Toast("请等待主机确认上一操作");
                        return false;
                    }
                    var intent = plugin.Sync.BuildIntent("Surrender");
                    intent.playerIndex = local.index;
                    intent.hasTarget = false;
                    plugin.Sync.SubmitIntent(intent, guestOptimisticApply: false);
                    GateUtil.Toast("已请求投降…");
                    return false;
                }

                if (plugin.Net.Role == PeerRole.Host)
                {
                    plugin.Sync.HostApplySurrender(local, sourceIntentId: null);
                    return false;
                }

                return true;
            }
        }
    }
}
