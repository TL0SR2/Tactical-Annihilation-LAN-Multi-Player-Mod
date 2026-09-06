using System;
using AnnW.LanMp.Protocol;
using HarmonyLib;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// Guest+Host: force Player.res_mul / ai_intelligence from SGS after SetupForSkirmish.
    /// Vanilla preset-AI branches ignore SGS floats (GetAIDiff*); without this, Guest/Host can
    /// disagree or drop Host-stamped eco even when LobbyStart draft was correct.
    /// </summary>
    internal static class SkirmishEconomyPatches
    {
        // SetupForSkirmish is private — nameof() does not compile against it.
        [HarmonyPatch(typeof(GS_Battle), "SetupForSkirmish")]
        private static class Patch_SetupForSkirmish
        {
            private static void Postfix(StartGameSetting sgs)
            {
                if (!ShouldApply())
                    return;
                ApplySgsEconomyToPlayers(sgs, "SetupForSkirmish");
            }
        }

        /// <summary>Second chokepoint after scene prepare — catches any mid-init overwrite.</summary>
        [HarmonyPatch(typeof(GS_Battle), nameof(GS_Battle.PrepareBattle))]
        private static class Patch_PrepareBattle_Economy
        {
            private static void Postfix()
            {
                if (!ShouldApply())
                    return;
                ApplySgsEconomyToPlayers(SS_ANNW_Game.start_game_setting, "PrepareBattle");
            }
        }

        private static bool ShouldApply()
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null || !plugin.Enabled.Value)
                return false;
            if (plugin.Lobby == null || !plugin.Lobby.StartAuthorized)
                return false;
            return true;
        }

        /// <summary>
        /// After SetupForSkirmish, vanilla replaces all_player.players with the exist-SGS
        /// list in iteration order (then RefreshIndex). Do NOT index by pos_ind — that was
        /// only valid mid-loop before the list rebuild.
        /// </summary>
        internal static void ApplySgsEconomyToPlayers(StartGameSetting sgs, string tag)
        {
            if (sgs?.players == null || GS_Battle.self?.all_player?.players == null)
                return;

            var live = GS_Battle.self.all_player.players;
            var liveIdx = 0;
            for (var i = 0; i < sgs.players.Count; i++)
            {
                var sp = sgs.players[i];
                if (sp == null || !sp.exist)
                    continue;
                if (liveIdx >= live.Count)
                {
                    LanMpPlugin.Log?.LogWarning(
                        $"[Eco] {tag} live list shorter than exist SGS (live={live.Count} at sgs[{i}])");
                    break;
                }

                var p = live[liveIdx++];
                if (p == null)
                    continue;

                var res = SkirmishSeatEconomy.ResolveEffectiveResMul(sp.res_percent, (int)sp.controller);
                if (Math.Abs(p.res_mul - res) > 0.001f)
                {
                    LanMpPlugin.Log?.LogInfo(
                        $"[Eco] {tag} sgs[{i}]→live[{liveIdx - 1}] pos{sp.pos_ind} " +
                        $"res_mul {p.res_mul:0.###} → {res:0.###} " +
                        $"(ctrl={sp.controller} role={(LanMpPlugin.Instance?.Net.Role.ToString() ?? "?")})");
                }
                p.res_mul = res;

                if (p.is_ai)
                {
                    var intel = SkirmishSeatEconomy.ResolveEffectiveAiIntelligence(
                        sp.ai_interlligence, (int)sp.controller);
                    if (Math.Abs(p.ai_intelligence - intel) > 0.001f)
                    {
                        LanMpPlugin.Log?.LogInfo(
                            $"[Eco] {tag} sgs[{i}]→live[{liveIdx - 1}] pos{sp.pos_ind} " +
                            $"ai_intel {p.ai_intelligence:0.###} → {intel:0.###}");
                    }
                    p.ai_intelligence = intel;
                }
            }
        }
    }
}
