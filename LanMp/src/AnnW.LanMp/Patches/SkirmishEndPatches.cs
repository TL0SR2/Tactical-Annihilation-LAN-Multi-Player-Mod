using System.Collections.Generic;
using System.Reflection;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using HarmonyLib;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// LAN: settle only when ≤1 living non-neutral faction remains.
    /// Covers (1) vanilla hotseat "no Human left → EndGame(false)" with AI allies, and
    /// (2) Host surrender must not MatchEnd while an allied remote human still fights —
    /// that path was also hit via Surrender Prefix falling through to vanilla EndGame.
    /// </summary>
    internal static class SkirmishEndPatches
    {
        private static readonly FieldInfo GameEndedField =
            AccessTools.Field(typeof(SkirmishLogic), "_gameEnded");
        private static readonly FieldInfo WinningFactionField =
            AccessTools.Field(typeof(SkirmishLogic), "_winningFaction");

        [HarmonyPatch(typeof(SkirmishLogic), "CheckSkirmishEndGame")]
        private static class Patch_CheckSkirmishEndGame
        {
            private static bool Prefix(SkirmishLogic __instance)
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (plugin.Net.Role != PeerRole.Host || !plugin.Authority.InLanBattle)
                    return true;
                if (plugin.Authority.MatchSettled)
                    return false;

                if (GameEndedField != null && GameEndedField.GetValue(__instance) is bool ended && ended)
                    return false;

                var battle = GS_Battle.self;
                if (battle?.all_player?.players == null)
                    return true;

                var seats = new List<SkirmishEndRules.SeatAlive>();
                foreach (var p in battle.all_player.players)
                {
                    if (p == null)
                        continue;
                    seats.Add(new SkirmishEndRules.SeatAlive(
                        p.defeated,
                        (int)p.fraction,
                        p.fraction == Fraction.NEUTRAL));
                }

                if (!SkirmishEndRules.ShouldSettleMatch(seats, out var livingFracs))
                {
                    LanMpPlugin.Log?.LogInfo(
                        "[Skirmish] LAN CheckSkirmishEndGame — continue (" + livingFracs +
                        " living factions; humans-left hotseat rule skipped)");
                    return false;
                }

                // ≤1 living faction: settle via Host MatchEnd (same as vanilla EndGame path).
                if (GameEndedField != null)
                    GameEndedField.SetValue(__instance, true);

                Fraction winFrac = Fraction.TEAM1;
                foreach (var p in battle.all_player.players)
                {
                    if (p == null || p.defeated || p.fraction == Fraction.NEUTRAL)
                        continue;
                    winFrac = p.fraction;
                    break;
                }
                if (WinningFactionField != null)
                    WinningFactionField.SetValue(__instance, winFrac);

                var victory = livingFracs >= 1;
                LanMpPlugin.Log?.LogInfo(
                    "[Skirmish] LAN CheckSkirmishEndGame — settle livingFracs=" + livingFracs +
                    " victory=" + victory + " winFrac=" + winFrac);
                try
                {
                    var game = SingletonMono<SS_ANNW_Game>.self;
                    if (game != null)
                    {
                        var endGame = AccessTools.Method(typeof(SS_ANNW_Game), "EndGame", new[] { typeof(bool) });
                        endGame?.Invoke(game, new object[] { victory });
                    }
                }
                catch (System.Exception ex)
                {
                    LanMpPlugin.Log?.LogWarning("[Skirmish] LAN EndGame: " + ex.Message);
                }

                return false;
            }
        }
    }
}
