using HarmonyLib;
using AnnW.LanMp.Sync;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// Ensure leaving battle (quit / menu exit) notifies the LAN peer.
    /// Vanilla DoQuitOut only LeaveGame + LoadScene — no network teardown.
    /// INV-17: always NotifyLeavingBattle even under Suppress/Applying — user quit during
    /// Accept must not silent-leave without peer Abort (sibling of bus:not-connected toast).
    /// </summary>
    internal static class SessionLifecyclePatches
    {
        [HarmonyPatch(typeof(SS_ANNW_Game), nameof(SS_ANNW_Game.DoQuitOut))]
        private static class Patch_DoQuitOut
        {
            private static void Prefix()
            {
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
                var plugin = LanMpPlugin.Instance;
                if (plugin == null || !plugin.Enabled.Value)
                    return;
                // DoQuitOut already notified; LeaveGame alone still needs a signal.
                if (plugin.Authority == null || !plugin.Authority.InLanBattle || plugin.Authority.MatchSettled)
                    return;
                plugin.Authority.NotifyLeavingBattle("leave-game");
            }
        }

        /// <summary>
        /// LAN: no mid-match RestartLevel (hotseat reload). Same class as Surrender fallthrough —
        /// vanilla restart desyncs peers without MatchEnd.
        /// </summary>
        [HarmonyPatch(typeof(SS_ANNW_Game), "RestartLevel")]
        private static class Patch_RestartLevel
        {
            private static bool Prefix()
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (!plugin.Authority.InLanBattle || plugin.Authority.MatchSettled)
                    return true;
                GateUtil.Toast("局域网对局请退出后重新开局");
                return false;
            }
        }

        [HarmonyPatch(typeof(SS_ANNW_Game), "TryRestartLevel")]
        private static class Patch_TryRestartLevel
        {
            private static bool Prefix()
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (!plugin.Authority.InLanBattle || plugin.Authority.MatchSettled)
                    return true;
                GateUtil.Toast("局域网对局请退出后重新开局");
                return false;
            }
        }
    }
}
