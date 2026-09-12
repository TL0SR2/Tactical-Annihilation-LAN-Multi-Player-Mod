using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using ANNW;
using HarmonyLib;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// LAN surrender → seat defeat + spectate (same path as wipe-out), never vanilla
    /// <c>Surrender</c> which is only <c>EndGame(false)</c>.
    /// Must not <c>return true</c> under Suppress/Applying — that re-enabled hotseat EndGame
    /// while allied humans still fight (Host surrender regression).
    /// </summary>
    internal static class SurrenderPatches
    {
        [HarmonyPatch(typeof(SS_ANNW_Game), "Surrender")]
        private static class Patch_Surrender
        {
            private static bool Prefix()
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (plugin.Authority.MatchSettled)
                    return false;

                // LAN: never fall through to vanilla Surrender (= EndGame(false)).
                if (SyncContext.ApplyingRemoteCommand)
                {
                    GateUtil.Toast("请稍候再投降");
                    return false;
                }

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
                    // Seat wipe + spectate even if Accept Suppress is held (pause menu).
                    plugin.Sync.HostApplySurrender(local, sourceIntentId: null);
                    return false;
                }

                // Role.None while gates armed — still block vanilla EndGame.
                return false;
            }
        }
    }
}
