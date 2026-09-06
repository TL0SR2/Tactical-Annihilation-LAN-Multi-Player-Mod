using HarmonyLib;
using AnnW.LanMp.Sync;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// Ensure leaving battle (quit / menu exit) notifies the LAN peer.
    /// Vanilla DoQuitOut only LeaveGame + LoadScene — no network teardown.
    /// </summary>
    internal static class SessionLifecyclePatches
    {
        [HarmonyPatch(typeof(SS_ANNW_Game), nameof(SS_ANNW_Game.DoQuitOut))]
        private static class Patch_DoQuitOut
        {
            private static void Prefix()
            {
                if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                    return;
                var plugin = LanMpPlugin.Instance;
                if (plugin == null || !plugin.Enabled.Value)
                    return;
                if (plugin.Authority == null || !plugin.Authority.InLanBattle || plugin.Authority.MatchSettled)
                    return;
                plugin.Authority.NotifyLeavingBattle("quit-out");
            }
        }

        [HarmonyPatch(typeof(SS_ANNW_Game), nameof(SS_ANNW_Game.LeaveGame))]
        private static class Patch_LeaveGame
        {
            private static void Prefix()
            {
                if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                    return;
                var plugin = LanMpPlugin.Instance;
                if (plugin == null || !plugin.Enabled.Value)
                    return;
                // DoQuitOut already notified; LeaveGame alone still needs a signal.
                if (plugin.Authority == null || !plugin.Authority.InLanBattle || plugin.Authority.MatchSettled)
                    return;
                plugin.Authority.NotifyLeavingBattle("leave-game");
            }
        }
    }
}
