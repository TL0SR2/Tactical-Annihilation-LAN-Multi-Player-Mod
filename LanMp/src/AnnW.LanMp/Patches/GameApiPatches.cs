using System.Collections;
using HarmonyLib;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using AnnW.LanMp.Ui;
using ANNW;
using UnityEngine;

namespace AnnW.LanMp.Patches
{
    internal static class GateUtil
    {
        internal static bool LanArmed(out LanMpPlugin plugin)
        {
            plugin = LanMpPlugin.Instance;
            if (plugin == null || !plugin.Enabled.Value)
                return false;
            var auth = plugin.Authority;
            return auth != null && auth.InLanBattle && auth.GatesArmed;
        }

        internal static bool IsBattlePlayPhase()
        {
            var battle = GS_Battle.self;
            // turns==0：PrepareBattle / 摆子 / 开局协程，必须放行本机 Create/Move。
            return battle != null && battle.turns >= 1;
        }

        /// <summary>Like vanilla AI turn: local peer cannot act; UX should stay quiet.</summary>
        internal static bool IsSpectating()
        {
            if (!LanArmed(out var plugin))
                return false;
            if (plugin.Authority.IsLocalHumanDefeated())
                return true;
            var battle = GS_Battle.self;
            if (battle?.cur_player == null)
                return false;
            if (Presentation.PresentationContext.ControlGrantPending)
                return true;
            var sync = plugin.Sync;
            if (sync != null && !sync.IsApplyQueueIdle)
            {
                var local = plugin.Authority.GetLocalHumanSlotIndex();
                if (local.HasValue && battle.cur_player.index == local.Value && !battle.cur_player.is_ai)
                    return true;
            }
            return plugin.Authority.ShouldBlockLocalInput(battle.cur_player.index);
        }

        internal static bool ShouldBlockUx(out string reason)
        {
            reason = null;
            if (!LanArmed(out var plugin))
                return false;
            if (!IsBattlePlayPhase())
                return false;
            if (plugin.Checksum != null && plugin.Checksum.MismatchPaused)
            {
                reason = "状态校验暂停中";
                return true;
            }
            if (Presentation.PresentationContext.ControlGrantPending)
            {
                reason = null;
                return true;
            }
            var sync = plugin.Sync;
            if (sync != null && !sync.IsApplyQueueIdle)
            {
                var battleEarly = GS_Battle.self;
                var localEarly = plugin.Authority.GetLocalHumanSlotIndex();
                if (battleEarly?.cur_player != null && localEarly.HasValue &&
                    battleEarly.cur_player.index == localEarly.Value && !battleEarly.cur_player.is_ai)
                {
                    reason = null;
                    return true;
                }
            }
            var battle = GS_Battle.self;
            if (battle?.cur_player == null)
                return false;
            if (!plugin.Authority.ShouldBlockLocalInput(battle.cur_player.index))
                return false;
            // Spectator: reason null → silent block (vanilla AI-watch style).
            return true;
        }

        internal static bool ShouldBlockUnit(UnitData unit, out string reason)
        {
            reason = null;
            if (unit == null)
                return false;
            if (!LanArmed(out var plugin))
                return false;
            if (!IsBattlePlayPhase())
                return false;
            if (SyncContext.ApplyingRemoteCommand)
                return false;
            var battle = GS_Battle.self;
            if (battle?.cur_player == null)
                return false;
            var local = plugin.Authority.GetLocalHumanSlotIndex();
            if (!local.HasValue)
                return false;

            if (!plugin.Authority.IsLocalPlayersTurn(battle.cur_player.index))
                return true; // silent spectate

            var owner = unit.player != null ? unit.player.index : unit.player_index;
            if (owner == local.Value)
                return false;
            reason = InputGateRules.BlockReasonNotYourUnit;
            return true;
        }

        /// <summary>Guest Intent only on own turn after play starts, and not while awaiting Host.</summary>
        internal static bool GuestMayEmitIntent(LanMpPlugin plugin)
        {
            if (plugin == null || plugin.Net.Role != PeerRole.Guest)
                return false;
            if (!IsBattlePlayPhase())
                return false;
            var battle = GS_Battle.self;
            if (battle?.cur_player == null)
                return false;
            if (!plugin.Authority.IsLocalPlayersTurn(battle.cur_player.index))
                return false;
            if (plugin.Sync != null && !plugin.Sync.GuestCanEmitIntent(out _))
                return false;
            return true;
        }

        internal static void Toast(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return;
            if (msg == _lastToast && Time.unscaledTime - _lastToastAt < 1.25f)
                return;
            _lastToast = msg;
            _lastToastAt = Time.unscaledTime;
            UiFeedback.Push(msg);
        }

        private static string _lastToast;
        private static float _lastToastAt;
    }

    [HarmonyPatch(typeof(GameAPI), nameof(GameAPI.TryEndHumanTurn))]
    internal static class Patch_TryEndHumanTurn
    {
        private static bool Prefix()
        {
            if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                return true;
            if (!GateUtil.LanArmed(out var plugin))
                return true;
            if (plugin.Checksum != null && plugin.Checksum.MismatchPaused)
            {
                GateUtil.Toast("状态校验暂停中");
                return false;
            }
            var battle = GS_Battle.self;
            if (battle?.cur_player == null)
                return true;
            if (plugin.Authority.ShouldBlockLocalInput(battle.cur_player.index))
                return false;
            // Refresh cache so EndTurn popup matches actioned flags after attach-only sync.
            ResultAttachmentBridge.RefreshUnactionedLists();
            return true;
        }
    }

    [HarmonyPatch(typeof(GameAPI), nameof(GameAPI.MannualEndTurn))]
    internal static class Patch_MannualEndTurn
    {
        private static bool Prefix()
        {
            if (SyncContext.SuppressNetworkEmit || SyncContext.ApplyingRemoteCommand)
                return true;
            if (!GateUtil.LanArmed(out var plugin))
                return true;
            if (plugin.Checksum != null && plugin.Checksum.MismatchPaused)
            {
                GateUtil.Toast("状态校验暂停中");
                return false;
            }

            var battle = GS_Battle.self;
            if (battle?.cur_player == null)
                return true;

            if (plugin.Authority.ShouldBlockLocalInput(battle.cur_player.index))
                return false;

            // Host+Guest: Intent → Host Accept → Apply → OnPlayerTurnEnded broadcast.
            plugin.Sync.SubmitIntent(plugin.Sync.BuildIntent("EndTurn"), guestOptimisticApply: false);
            return false;
        }
    }

    // =====================================================================
    // ADR-001 ultimate Guest chokepoints
    // Moves → DoMove / DoMoveWithAni; Actions → DoActionWithAni / DoActionAni
    // =====================================================================

    [HarmonyPatch(typeof(UnitData), nameof(UnitData.DoMove))]
    internal static class Patch_UnitData_DoMove
    {
        private static bool Prefix(UnitData __instance, Inctor2 move_target)
        {
            var from = __instance != null ? __instance.pos : Inctor2.Zero;
            return GuestMutationGate.AllowLocalMutation(
                GuestMutationGate.Kind.UnitMoved, __instance, null, move_target, from);
        }
    }

    [HarmonyPatch(typeof(UnitData), nameof(UnitData.DoMoveWithAni))]
    internal static class Patch_UnitData_DoMoveWithAni
    {
        private static bool Prefix(UnitData __instance, Inctor2 move_target, ref IEnumerator __result)
        {
            var from = __instance != null ? __instance.pos : Inctor2.Zero;
            if (GuestMutationGate.AllowLocalMutation(
                    GuestMutationGate.Kind.UnitMoved, __instance, null, move_target, from))
                return true;
            if (__instance != null)
                __instance.in_animation = false;
            __result = EmptyRoutine();
            return false;
        }

        private static IEnumerator EmptyRoutine()
        {
            yield break;
        }
    }

    [HarmonyPatch(typeof(UnitData), nameof(UnitData.DoActionWithAni))]
    internal static class Patch_UnitData_DoActionWithAni
    {
        private static bool Prefix(UnitData __instance, GameTileData lt, ActionData the_action, bool skipping, ref IEnumerator __result)
        {
            var cate = ActionCate.NONE;
            string extras = null;
            try
            {
                if (the_action?.sd_action != null)
                    cate = the_action.sd_action.cate;
                if (the_action?.train_template?.sd_unit != null)
                    extras = the_action.train_template.sd_unit.name;
                else if ((cate == ActionCate.TRAIN || cate == ActionCate.BUILD) &&
                         GS_Battle.self?.ux_unit_template?.sd_unit != null)
                    extras = GS_Battle.self.ux_unit_template.sd_unit.name;
            }
            catch { /* ignore */ }

            // null lt → Intent.hasTarget=false (AutoSetPos). Never substitute (0,0).
            Inctor2? pos = lt != null ? (Inctor2?)lt.pos : null;
            if (GuestMutationGate.AllowLocalMutation(
                    GuestMutationGate.Kind.DoAction, __instance, cate, pos, null, extras))
                return true;
            __result = EmptyRoutine();
            return false;
        }

        private static IEnumerator EmptyRoutine()
        {
            yield break;
        }
    }

    /// <summary>EQ UX calls DoActionAni directly, bypassing DoActionWithAni.</summary>
    [HarmonyPatch(typeof(ActionData), nameof(ActionData.DoActionAni))]
    internal static class Patch_ActionData_DoActionAni
    {
        private static bool Prefix(ActionData __instance, GameTileData gtd, bool skipping, ref IEnumerator __result)
        {
            var owner = __instance != null ? __instance.owner : null;
            var cate = ActionCate.NONE;
            string extras = null;
            try
            {
                if (__instance?.sd_action != null)
                    cate = __instance.sd_action.cate;
                if (__instance?.train_template?.sd_unit != null)
                    extras = __instance.train_template.sd_unit.name;
                else if ((cate == ActionCate.TRAIN || cate == ActionCate.BUILD) &&
                         GS_Battle.self?.ux_unit_template?.sd_unit != null)
                    extras = GS_Battle.self.ux_unit_template.sd_unit.name;
            }
            catch { /* ignore */ }

            Inctor2? pos = gtd != null ? (Inctor2?)gtd.pos : null;
            if (GuestMutationGate.AllowLocalMutation(
                    GuestMutationGate.Kind.DoAction, owner, cate, pos, null, extras))
                return true;
            if (owner != null)
                owner.in_animation = false;
            __result = EmptyRoutine();
            return false;
        }

        private static IEnumerator EmptyRoutine()
        {
            yield break;
        }
    }

    [HarmonyPatch(typeof(GameAPI), nameof(GameAPI.DoActionInstant))]
    internal static class Patch_DoActionInstant
    {
        private static bool Prefix(UnitData unit, ActionCate cate, Inctor2 target_pos)
        {
            return GuestMutationGate.AllowLocalMutation(
                GuestMutationGate.Kind.DoAction, unit, cate, target_pos);
        }
    }

    [HarmonyPatch(typeof(GameAPI), nameof(GameAPI.MoveUnitInstantly))]
    internal static class Patch_MoveUnitInstantly
    {
        private static bool Prefix(UnitData unit, Inctor2 to)
        {
            var from = unit != null ? unit.pos : Inctor2.Zero;
            return GuestMutationGate.AllowLocalMutation(
                GuestMutationGate.Kind.UnitMoved, unit, null, to, from);
        }
    }

    [HarmonyPatch(typeof(GameController), nameof(GameController.ExecuteAction))]
    internal static class Patch_GameController_ExecuteAction
    {
        private static bool Prefix(UnitData unit, ActionCate cate, GameTileData target)
        {
            Inctor2? pos = target != null ? (Inctor2?)target.pos : null;
            return GuestMutationGate.AllowLocalMutation(
                GuestMutationGate.Kind.DoAction, unit, cate, pos);
        }
    }

    [HarmonyPatch(typeof(UndoMoveData), nameof(UndoMoveData.UndoLastMove))]
    internal static class Patch_UndoLastMove
    {
        private static bool Prefix()
        {
            if (SyncContext.SuppressNetworkEmit || SyncContext.ApplyingRemoteCommand)
                return true;
            if (!GateUtil.LanArmed(out var plugin))
                return true;
            var battle = GS_Battle.self;
            if (battle?.cur_player == null)
                return true;
            if (plugin.Authority.ShouldBlockLocalInput(battle.cur_player.index))
                return false;

            // Guest button uses Host-stamped depth; block click-spam when nothing to undo.
            if (plugin.Net.Role == PeerRole.Guest &&
                (plugin.Sync == null || plugin.Sync.GuestUndoAvailable <= 0))
                return false;

            // Host: empty stack → silent no-op (do not Accept→Nack→toast Guest while they spectate).
            if (plugin.Net.Role == PeerRole.Host)
            {
                var n = 0;
                try { n = battle.undo_move != null ? battle.undo_move.GetUndoMoveCount() : 0; }
                catch { n = 0; }
                if (n <= 0)
                    return false;
            }

            plugin.Sync.SubmitIntent(plugin.Sync.BuildIntent("Undo"), guestOptimisticApply: false);
            return false;
        }
    }

    [HarmonyPatch(typeof(AutoGuideController), nameof(AutoGuideController.TryAutoCommandSelectedUnits))]
    internal static class Patch_AutoCmd_Selected
    {
        private static bool Prefix()
        {
            if (!GateUtil.LanArmed(out var plugin))
                return true;
            if (!GateUtil.IsBattlePlayPhase())
                return true;
            if (plugin.Net.Role != PeerRole.Guest)
                return true;
            if (!GateUtil.GuestMayEmitIntent(plugin))
                return false;

            // Host runs AutoGuide against the same seat; unit selection is mirrored via Host UX
            // only for Unacted. Selected path: send unit ids so Host can select then run.
            var extras = CollectSelectedUnitIds();
            var intent = plugin.Sync.BuildIntent("AutoCmd");
            intent.extrasJson = extras;
            plugin.Sync.SubmitIntent(intent, guestOptimisticApply: false);
            return false;
        }

        private static string CollectSelectedUnitIds()
        {
            try
            {
                var sel = GS_Battle.self?.selected_units;
                if (sel == null || sel.Count == 0)
                    return "unacted";
                var sb = new System.Text.StringBuilder();
                foreach (var u in sel)
                {
                    if (u == null) continue;
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(u.unit_id);
                }
                return sb.Length == 0 ? "unacted" : sb.ToString();
            }
            catch
            {
                return "unacted";
            }
        }
    }

    [HarmonyPatch(typeof(AutoGuideController), nameof(AutoGuideController.TryAutoCommandUnactedUnits))]
    internal static class Patch_AutoCmd_Unacted
    {
        private static bool Prefix()
        {
            if (!GateUtil.LanArmed(out var plugin))
                return true;
            if (!GateUtil.IsBattlePlayPhase())
                return true;
            if (plugin.Net.Role != PeerRole.Guest)
                return true;
            if (!GateUtil.GuestMayEmitIntent(plugin))
                return false;
            var intent = plugin.Sync.BuildIntent("AutoCmd");
            intent.extrasJson = "unacted";
            plugin.Sync.SubmitIntent(intent, guestOptimisticApply: false);
            return false;
        }
    }

    /// <summary>Guest self-destruct (QuickMenu Die DELETE) → Host RemoveUnit Intent.</summary>
    [HarmonyPatch]
    internal static class Patch_UnitData_Die
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(UnitData), "Die", new[]
            {
                typeof(DieReason), typeof(float), typeof(UnitData), typeof(ActionData), typeof(bool), typeof(Inctor2?)
            });
        }

        private static bool Prepare() => TargetMethod() != null;

        private static bool Prefix(UnitData __instance, DieReason reason)
        {
            if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                return true;
            if (reason != DieReason.DELETE)
                return true;
            if (!GateUtil.LanArmed(out var plugin))
                return true;
            if (!GateUtil.IsBattlePlayPhase())
                return true;
            if (plugin.Net.Role != PeerRole.Guest)
                return true;
            if (!GateUtil.GuestMayEmitIntent(plugin))
                return false;
            if (GateUtil.ShouldBlockUnit(__instance, out var reasonBlock))
            {
                GateUtil.Toast(reasonBlock);
                return false;
            }

            var intent = plugin.Sync.BuildIntent("RemoveUnit", __instance);
            plugin.Sync.SubmitIntent(intent, guestOptimisticApply: false);
            return false;
        }
    }

    [HarmonyPatch(typeof(UndoMoveData), nameof(UndoMoveData.GetUndoMoveCount))]
    internal static class Patch_GetUndoMoveCount
    {
        private static void Postfix(ref int __result)
        {
            if (!GateUtil.LanArmed(out var plugin))
                return;
            if (plugin.Net.Role != PeerRole.Guest)
                return;
            if (!GateUtil.IsBattlePlayPhase())
                return;
            // Guest local stack is always empty; Host-stamped depth is the sole UI source of truth.
            __result = plugin.Sync?.GuestUndoAvailable ?? 0;
        }
    }

    [HarmonyPatch(typeof(PlayerAI), nameof(PlayerAI.OnStartTurn_DoTurn))]
    internal static class Patch_PlayerAI_DoTurn
    {
        private static bool Prefix(ref IEnumerator __result)
        {
            if (!GateUtil.LanArmed(out var plugin))
                return true;
            if (plugin.Net.Role != PeerRole.Guest)
                return true;

            LanMpPlugin.Log?.LogInfo("[Gate] Guest watching Host AI turn");
            __result = plugin.Sync.CoGuestWatchRemoteTurn();
            return false;
        }
    }

    [HarmonyPatch(typeof(GS_Battle), nameof(GS_Battle.PrepareBattle))]
    internal static class Patch_PrepareBattle
    {
        private static void Postfix()
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null || !plugin.Enabled.Value)
                return;
            if (!plugin.Lobby.StartAuthorized)
                return;
            if (GS_Battle.self == null)
                return;

            var id = plugin.Authority.PendingBattleId ?? plugin.Lobby.BattleId;
            if (!string.IsNullOrEmpty(id))
            {
                GS_Battle.self.battle_id = id;
                LanMpPlugin.Log.LogInfo("[Patch] PrepareBattle battle_id override -> " + id);
            }

            plugin.Authority.ApplyLocalViewBinding("prepare-battle");
        }
    }

    [HarmonyPatch(typeof(GS_Battle), "GetDisplayPlayer")]
    internal static class Patch_GetDisplayPlayer
    {
        private static bool Prefix(ref Player __result)
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null || !plugin.Enabled.Value)
                return true;
            var auth = plugin.Authority;
            if (auth == null || !auth.InLanBattle || !auth.GatesArmed)
                return true;
            var local = auth.TryGetLocalHumanPlayer();
            if (local == null)
                return true;
            __result = local;
            return false;
        }
    }

    [HarmonyPatch(typeof(UX_Manager), "OnWorldLeftClick_Alt", typeof(Vector3))]
    internal static class Patch_WorldLeftClick
    {
        private static bool Prefix()
        {
            if (!GateUtil.ShouldBlockUx(out var reason))
                return true;
            GateUtil.Toast(reason);
            return false;
        }
    }

    [HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.SelectUnit))]
    internal static class Patch_SelectUnit
    {
        private static bool Prefix(UnitData unit)
        {
            if (GateUtil.ShouldBlockUnit(unit, out var reason))
            {
                GateUtil.Toast(reason);
                return false;
            }
            if (GateUtil.ShouldBlockUx(out reason))
            {
                GateUtil.Toast(reason);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(SS_ANNW_Game), "EndGame", typeof(bool))]
    internal static class Patch_EndGame
    {
        private static bool Prefix(bool victory)
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null || !plugin.Enabled.Value)
                return true;
            if (!plugin.Authority.InLanBattle)
                return true;

            // Guest never runs vanilla EndGame — wait for Host MatchEnd payload.
            if (plugin.Net.Role == PeerRole.Guest)
            {
                LanMpPlugin.Log?.LogInfo("[Gate] Blocked Guest EndGame (wait MatchEnd)");
                return false;
            }

            if (plugin.Net.Role == PeerRole.Host)
            {
                // Win often fires inside HostAcceptIntent while SuppressNetworkEmit is set.
                // Must still broadcast MatchEnd — do NOT early-return on Suppress/ApplyingRemote.
                plugin.Authority.BroadcastMatchEnd(victory, "EndGame");
                return false;
            }

            return true;
        }
    }
}
