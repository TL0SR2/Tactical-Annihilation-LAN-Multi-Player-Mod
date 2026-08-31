using AnnW.LanMp.Presentation;
using AnnW.LanMp.Protocol;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;

namespace AnnW.LanMp.Patches
{
    /// <summary>Suppress foreign-seat vanilla UX on Host; route Guest hints via RemoteTurnPresentation.</summary>
    /// <remarks>Economy (UpdateResIncomes / ExecuteResIncomes) is Host simulation — never gate here.</remarks>
    internal static class PresentationPatches
    {
        [HarmonyPatch(typeof(BattleEventBus), nameof(BattleEventBus.TriggerTurnHint))]
        private static class Patch_TriggerTurnHint
        {
            private static bool Prefix(int turn, Player player)
            {
                if (PresentationContext.VanillaTurnHint)
                    return true;
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (!GateUtil.IsBattlePlayPhase())
                    return true;
                if (player == null || player.is_ai)
                    return true;
                if (plugin.Net.Role == PeerRole.Host &&
                    !plugin.Authority.IsLocalPlayersTurn(player.index))
                    return false;
                return true;
            }
        }

        /// <summary>Skill-charged ping on foreign human StartPlayerTurn — Host must not see Guest ping.</summary>
        [HarmonyPatch]
        private static class Patch_PingElement
        {
            private static MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("PingManager");
                return t == null ? null : AccessTools.Method(t, "PingElement");
            }

            private static bool Prepare(MethodBase original) => original != null;

            private static bool Prefix()
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (!GateUtil.IsBattlePlayPhase())
                    return true;
                var cur = GS_Battle.self?.cur_player;
                if (cur == null || cur.is_ai)
                    return true;
                return plugin.Authority.IsLocalPlayersTurn(cur.index);
            }
        }

        /// <summary>Block foreign-seat toast messages (e.g. MSG_LowPower) on Host.</summary>
        [HarmonyPatch]
        private static class Patch_UiMessages_AddMessage
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var name in new[] { "UI_Messages", "UI_Part_Messages", "UI_MessagePanel" })
                {
                    var t = AccessTools.TypeByName(name);
                    if (t == null)
                        continue;
                    foreach (var m in AccessTools.GetDeclaredMethods(t))
                    {
                        if (m.Name == "AddMessage")
                            yield return m;
                    }
                }
            }

            private static bool Prefix()
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (!GateUtil.IsBattlePlayPhase())
                    return true;
                var cur = GS_Battle.self?.cur_player;
                if (cur == null || cur.is_ai)
                    return true;
                return plugin.Authority.IsLocalPlayersTurn(cur.index);
            }
        }
    }
}
