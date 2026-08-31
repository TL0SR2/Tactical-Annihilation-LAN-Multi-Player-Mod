using HarmonyLib;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using AnnW.LanMp.Ui;
using ANNW;
using UnityEngine;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// FOW/display binding + commander skill gating/sync.
    /// Game sets last_human_player to each Human on StartPlayerTurn — must rebind to LocalHuman
    /// before FOW/UI listeners run (TriggerPlayerTurnStarted Prefix).
    /// </summary>
    internal static class FowAndSkillPatches
    {
        [HarmonyPatch(typeof(BattleEventBus), nameof(BattleEventBus.TriggerPlayerTurnStarted))]
        private static class Patch_TriggerPlayerTurnStarted
        {
            private static void Prefix()
            {
                LanMpPlugin.Instance?.Authority?.ApplyLocalViewBinding("turn-started-bus");
            }
        }

        [HarmonyPatch(typeof(BattleEventBus), nameof(BattleEventBus.TriggerFOWDirty))]
        private static class Patch_TriggerFOWDirty
        {
            private static int _depth;

            private static bool Prefix(ref bool __state)
            {
                // Nested Postfix must NOT clear the outer guard (bool flag was wrong).
                if (_depth > 0)
                {
                    __state = false;
                    return false;
                }
                _depth++;
                __state = true;
                LanMpPlugin.Instance?.Authority?.ApplyLocalViewBinding("fow-dirty");
                return true;
            }

            private static void Postfix(bool __state)
            {
                if (__state && _depth > 0)
                    _depth--;
                if (__state)
                    BattleSyncTrace.Ev("FowDirtyDone");
            }
        }

        [HarmonyPatch(typeof(GS_Battle), "GetDisplayFraction")]
        private static class Patch_GetDisplayFraction
        {
            private static bool Prefix(ref Fraction __result)
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
                __result = local.fraction;
                return false;
            }
        }

        [HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.SetUXState_Skill))]
        private static class Patch_SetUXState_Skill
        {
            private static bool Prefix()
            {
                if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                    return true;
                if (!GateUtil.ShouldBlockUx(out var reason))
                    return true;
                GateUtil.Toast(reason);
                return false;
            }
        }

        [HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.DoSkillDirectly))]
        private static class Patch_DoSkillDirectly
        {
            private static bool Prefix(ActionData skill)
            {
                if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                    return true;
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (GateUtil.ShouldBlockUx(out var reason))
                {
                    GateUtil.Toast(reason);
                    return false;
                }

            if (plugin.Net.Role == PeerRole.Guest)
            {
                if (!GateUtil.GuestMayEmitIntent(plugin))
                {
                    // Allow setup / remote apply through; otherwise swallow (no Intent spam).
                    if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                        return true;
                    return !GateUtil.IsBattlePlayPhase();
                }
                var intent = plugin.Sync.BuildIntent("CastSkill", target: Inctor2.Zero);
                if (skill?.sd_skill != null)
                    intent.extrasJson = skill.sd_skill.name;
                plugin.Sync.SubmitIntent(intent, guestOptimisticApply: false);
                return false;
            }

                return true;
            }
        }

        [HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.proc_SkillDoAction))]
        private static class Patch_proc_SkillDoAction
        {
            private static bool Prefix(GameTileData lt)
            {
                if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                    return true;
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (GateUtil.ShouldBlockUx(out var reason))
                {
                    GateUtil.Toast(reason);
                    return false;
                }

                if (plugin.Net.Role == PeerRole.Guest)
                {
                    if (!GateUtil.GuestMayEmitIntent(plugin))
                    {
                        if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                            return true;
                        return !GateUtil.IsBattlePlayPhase();
                    }
                    var pos = lt != null ? lt.pos : Inctor2.Zero;
                    var skill = GS_Battle.self?.selected_skill ?? GS_Battle.self?.cur_player?.co_data?.skill_action;
                    var intent = plugin.Sync.BuildIntent("CastSkill", target: pos);
                    if (skill?.sd_skill != null)
                        intent.extrasJson = skill.sd_skill.name;
                    plugin.Sync.SubmitIntent(intent, guestOptimisticApply: false);
                    return false;
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(UI_SkillBtn), nameof(UI_SkillBtn.OnClick))]
        private static class Patch_UI_SkillBtn_OnClick
        {
            private static bool Prefix()
            {
                if (!GateUtil.ShouldBlockUx(out var reason))
                    return true;
                GateUtil.Toast(reason);
                return false;
            }
        }

        [HarmonyPatch(typeof(GameAPI), nameof(GameAPI.CreateUnit))]
        private static class Patch_CreateUnit
        {
            private static bool Prefix(
                CREATE_REASON reason,
                UnitTemplate template,
                Inctor2? pos,
                Player player,
                bool building,
                bool spawned,
                bool trigger_ps,
                ref UnitData __result)
            {
                if (SyncContext.AllowForcedCreate)
                    return true;

                if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                {
                    // Guest DoAction replay: prefer unit already spawned via CreateUnit command.
                    if (GateUtil.LanArmed(out var plug) && plug.Net.Role == PeerRole.Guest
                        && GateUtil.IsBattlePlayPhase() && pos.HasValue)
                    {
                        var existing = ResultAttachmentBridge.FindUnitAt(pos.Value, template, player);
                        if (existing != null)
                        {
                            __result = existing;
                            return false;
                        }
                    }
                    return true;
                }

                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                // Guest must run local CreateUnit during map setup (turns==0).
                if (plugin.Net.Role == PeerRole.Guest && GateUtil.IsBattlePlayPhase())
                {
                    LanMpPlugin.Log?.LogInfo("[Gate] Blocked Guest CreateUnit (play phase)");
                    return false;
                }
                return true;
            }

            private static void Postfix(UnitData __result)
            {
                if (__result == null || !SyncContext.ForcedUnitId.HasValue)
                    return;
                var want = SyncContext.ForcedUnitId.Value;
                SyncContext.ForcedUnitId = null;
                SyncContext.ForceUnitId(__result, want);
            }
        }

        [HarmonyPatch(typeof(GameAPI), nameof(GameAPI.RemoveUnit))]
        private static class Patch_RemoveUnit
        {
            private static bool Prefix()
            {
                if (SyncContext.ApplyingRemoteCommand || SyncContext.SuppressNetworkEmit)
                    return true;
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (plugin.Net.Role == PeerRole.Guest && GateUtil.IsBattlePlayPhase())
                {
                    LanMpPlugin.Log?.LogInfo("[Gate] Blocked Guest RemoveUnit (play phase)");
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(UX_Manager), "OnWorldRightClick_Alt", typeof(Vector3))]
        private static class Patch_WorldRightClick
        {
            private static bool Prefix()
            {
                if (!GateUtil.ShouldBlockUx(out var reason))
                    return true;
                GateUtil.Toast(reason);
                return false;
            }
        }
    }
}
