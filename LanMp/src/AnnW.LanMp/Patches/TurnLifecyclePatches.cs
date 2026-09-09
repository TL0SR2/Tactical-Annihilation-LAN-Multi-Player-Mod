using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using HarmonyLib;
using UnityEngine;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// ADR-004 turn cursor + INV-VIEW (last_human = local FOW viewer, never remote seat).
    /// INV-T10: Host turn progression (NextTurn / EndPlayerTurn / StartNextPlayerTurn → AI)
    /// is SafePump'd without Apply timeout so multi-AI chains cannot abort mid-turn.
    /// </summary>
    internal static class TurnLifecyclePatches
    {
        [HarmonyPatch(typeof(GameController), nameof(GameController.NextTurn))]
        private static class Patch_NextTurn
        {
            private static bool Prefix(ref IEnumerator __result)
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;

                if (plugin.Net.Role == PeerRole.Guest &&
                    !SyncContext.ApplyingRemoteCommand &&
                    !SyncContext.SuppressNetworkEmit)
                {
                    __result = Empty();
                    return false;
                }

                if (plugin.Net.Role == PeerRole.Host && plugin.Authority.InLanBattle)
                {
                    __result = CoLanHostNextTurn();
                    return false;
                }

                return true;
            }

            private static IEnumerator CoLanHostNextTurn()
            {
                var battle = GS_Battle.self;
                if (battle == null || GameController.self == null)
                    yield break;
                if (battle.turns == 0)
                {
                    BattleEventBus.self.TriggerBeforeFirstTurn();
                    BattleEventBus.self.TriggerTurnStarted(0);
                }
                battle.turns++;
                battle.current_co_index = -1;
                TryInvoke(battle.all_unit, "ClearDeadUnits");
                battle.last_died_unit = null;
                battle.last_died_unit_pos = Inctor2.Zero;
                battle.last_levelup_unit = null;
                BattleEventBus.self.TriggerTurnStarted(battle.turns);
                TryInvoke(battle, "CaptureAllTurnSnaps");
                // StartNextPlayerTurn Postfix SafePump — do not double-wrap or 45s-timeout here.
                yield return GameController.self.StartNextPlayerTurn();
            }

            private static void TryInvoke(object target, string method)
            {
                if (target == null)
                    return;
                try
                {
                    var mi = target.GetType().GetMethod(
                        method,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    mi?.Invoke(target, null);
                }
                catch { /* ignore */ }
            }
        }

        [HarmonyPatch(typeof(GameController), nameof(GameController.EndPlayerTurn))]
        private static class Patch_EndPlayerTurn
        {
            private static bool Prefix(ref IEnumerator __result)
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (plugin.Net.Role != PeerRole.Guest)
                    return true;
                if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                    return true;
                __result = Empty();
                return false;
            }

            /// <summary>
            /// Host MannualEndTurn → EndPlayerTurn → StartNextPlayerTurn(AI): wrap once with
            /// unbounded SafePump so AI null/int0 yields cannot busy-spin CoroutineObject.
            /// </summary>
            private static void Postfix(ref IEnumerator __result)
            {
                __result = MaybeWrapHostTurnPump(__result, "HostEndPlayerTurn");
            }
        }

        [HarmonyPatch(typeof(GameController), nameof(GameController.StartNextPlayerTurn))]
        private static class Patch_StartNextPlayerTurn
        {
            private static bool Prefix(ref IEnumerator __result)
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (plugin.Net.Role != PeerRole.Guest)
                    return true;
                if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                    return true;
                __result = Empty();
                return false;
            }

            private static void Postfix(ref IEnumerator __result)
            {
                __result = MaybeWrapHostTurnPump(__result, "HostStartNextPlayerTurn");
            }
        }

        /// <summary>Guest only: RemoteWatch for non-local seats. Host runs vanilla TurnLoop.</summary>
        [HarmonyPatch(typeof(GameController), nameof(GameController.StartPlayerTurn))]
        private static class Patch_StartPlayerTurn
        {
            private static bool Prefix(Player player, ref IEnumerator __result)
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (plugin.Net.Role != PeerRole.Guest)
                    return true;
                if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                    return true;
                if (player == null)
                    return true;

                var local = plugin.Authority.GetLocalHumanSlotIndex();
                if (local.HasValue && player.index == local.Value && !player.is_ai)
                    return true;

                if (plugin.TurnAuth != null)
                    __result = plugin.TurnAuth.CoGuestWatchRemoteTurn();
                else
                    __result = Empty();
                return false;
            }
        }

        /// <summary>
        /// INV-VIEW at the IL source: compiler state-machine MoveNext stores last_human / in_control.
        /// Prefix on StartPlayerTurn cannot see those stores — must transpile MoveNext.
        /// </summary>
        [HarmonyPatch]
        private static class Patch_StartPlayerTurn_MoveNext_View
        {
            private static MethodBase TargetMethod()
            {
                var sm = typeof(GameController).GetNestedTypes(BindingFlags.NonPublic)
                    .FirstOrDefault(t => t.Name.Contains("StartPlayerTurn"));
                if (sm == null)
                    throw new InvalidOperationException("StartPlayerTurn state machine not found");
                return AccessTools.Method(sm, "MoveNext");
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var lastHuman = AccessTools.Field(typeof(GS_Battle), nameof(GS_Battle.last_human_player));
                var inControl = AccessTools.Field(typeof(GS_Battle), nameof(GS_Battle.is_player_in_control));
                var assignLast = AccessTools.Method(typeof(Patch_StartPlayerTurn_MoveNext_View), nameof(LanAssignLastHuman));
                var assignCtrl = AccessTools.Method(typeof(Patch_StartPlayerTurn_MoveNext_View), nameof(LanAssignInControl));

                foreach (var inst in instructions)
                {
                    if (inst.StoresField(lastHuman))
                    {
                        // stack: battle, player
                        yield return new CodeInstruction(OpCodes.Call, assignLast);
                        continue;
                    }
                    if (inst.StoresField(inControl))
                    {
                        // stack: battle, bool
                        yield return new CodeInstruction(OpCodes.Call, assignCtrl);
                        continue;
                    }
                    yield return inst;
                }
            }

            private static void LanAssignLastHuman(GS_Battle battle, Player player)
            {
                if (battle == null)
                    return;
                if (!LanViewActive(out var local))
                {
                    battle.last_human_player = player;
                    return;
                }
                battle.last_human_player = local ?? player;
            }

            private static void LanAssignInControl(GS_Battle battle, bool vanillaValue)
            {
                if (battle == null)
                    return;
                if (!LanViewActive(out var local))
                {
                    battle.is_player_in_control = vanillaValue;
                    return;
                }
                var cur = battle.cur_player;
                battle.is_player_in_control =
                    local != null && cur != null && !cur.is_ai && cur.index == local.index;
            }

            private static bool LanViewActive(out Player local)
            {
                local = null;
                var plugin = LanMpPlugin.Instance;
                var auth = plugin?.Authority;
                if (plugin == null || auth == null || !auth.InLanBattle || !auth.GatesArmed)
                    return false;
                local = auth.TryGetLocalHumanPlayer();
                return true;
            }
        }

        /// <summary>
        /// Single Host turn-progression chokepoint (INV-T10): unbounded SafePump, nested-safe.
        /// </summary>
        private static IEnumerator MaybeWrapHostTurnPump(IEnumerator body, string tag)
        {
            if (body == null)
                return Empty();
            if (!GateUtil.LanArmed(out var plugin))
                return body;
            if (plugin.Net.Role != PeerRole.Host || !plugin.Authority.InLanBattle)
                return body;
            if (SyncContext.InHostTurnSafePump)
                return body;
            return CoHostTurnSafePump(body, tag);
        }

        private static IEnumerator CoHostTurnSafePump(IEnumerator body, string tag)
        {
            var prev = SyncContext.InHostTurnSafePump;
            SyncContext.InHostTurnSafePump = true;
            try
            {
                yield return AnnWCoroutine.SafePump(
                    body,
                    AnnWCoroutine.NoTimeout,
                    LanMpPlugin.Log,
                    tag);
            }
            finally
            {
                SyncContext.InHostTurnSafePump = prev;
            }
        }

        private static IEnumerator Empty()
        {
            yield break;
        }
    }
}
