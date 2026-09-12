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
                // INV-SOLO: ApplyLocalViewBinding no-ops outside InLanBattle; keep call cheap.
                if (!GateUtil.LanArmed(out _))
                    return;
                LanMpPlugin.Instance?.Authority?.ApplyLocalViewBinding("turn-started-bus");
            }
        }

        [HarmonyPatch(typeof(BattleEventBus), nameof(BattleEventBus.TriggerFOWDirty))]
        private static class Patch_TriggerFOWDirty
        {
            private static int _depth;

            private static bool Prefix(ref bool __state)
            {
                // INV-SOLO + CO skills (INV-15): nested TriggerFOWDirty bodies must always run
                // (Telle / Skill_Spawn nest FOW during cast). The EndTurn→Guest freeze was from
                // rebinding last_human_player mid-nested re-entry — not from nested FOW itself.
                var lan = GateUtil.LanArmed(out _);
                var nested = _depth > 0;
                if (!SoloIsolationRules.AllowNestedFowDirtyBody(lan, nested))
                {
                    __state = false;
                    return false;
                }

                if (SoloIsolationRules.ShouldApplyLocalViewOnFowDirty(lan, nested))
                    LanMpPlugin.Instance?.Authority?.ApplyLocalViewBinding("fow-dirty");

                _depth++;
                // Outer entry only: FowDirtyDone trace + owns depth pairing.
                __state = !nested;
                return true;
            }

            private static void Postfix(bool __state)
            {
                if (_depth > 0)
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
                if (!GateUtil.LanArmed(out _))
                    return true;
                // Host Accept CastSkill calls SetUXState_Skill under SkillCastSuppressEmit
                // while Host spectates the remote seat — must run vanilla (INV-17).
                if (GateUtil.AllowApplyDrivenVanillaBody())
                    return true;
                if (SyncContext.SuppressNetworkEmit)
                {
                    GateUtil.Toast("请稍候");
                    return false;
                }
                if (!GateUtil.ShouldBlockUx(out var reason))
                    return true;
                GateUtil.Toast(reason);
                return false;
            }

            private static void Postfix()
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return;
                if (plugin.Net.Role != PeerRole.Guest && plugin.Net.Role != PeerRole.Host)
                    return;
                // 片区友军强化等可点空地：CanDoAction 要 SEEN；进技能时强制对齐本机 FOW。
                try
                {
                    AnnW.LanMp.Presentation.RemoteTurnPresentation.RefreshLocalVision(LanMpPlugin.Log);
                }
                catch { /* ignore */ }
            }
        }

        [HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.DoSkillDirectly))]
        private static class Patch_DoSkillDirectly
        {
            private static bool Prefix(ActionData skill)
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (GateUtil.AllowApplyDrivenVanillaBody())
                    return true;
                if (SyncContext.SuppressNetworkEmit)
                {
                    GateUtil.Toast("请稍候");
                    return false;
                }
                if (GateUtil.ShouldBlockUx(out var reason))
                {
                    GateUtil.Toast(reason);
                    return false;
                }

                if (plugin.Net.Role == PeerRole.Guest)
                {
                    if (!GateUtil.GuestMayEmitIntent(plugin))
                    {
                        if (GateUtil.IsBattlePlayPhase())
                            GateUtil.Toast(InputGateRules.WaitingHostConfirm);
                        return !GateUtil.IsBattlePlayPhase();
                    }
                    var intent = plugin.Sync.BuildIntent("CastSkill");
                    if (skill?.sd_skill != null)
                        intent.extrasJson = skill.sd_skill.name;
                    plugin.Sync.SubmitIntent(intent, guestOptimisticApply: false);
                    try
                    {
                        var battle = GS_Battle.self;
                        if (battle != null)
                        {
                            battle.ux_state = UX_State.NONE;
                            battle.selected_skill = null;
                            battle.ux_action_cate = ActionCate.NONE;
                        }
                        BattleEventBus.self.TriggerUXStateChanged();
                    }
                    catch { /* ignore */ }
                    GateUtil.Toast("已请求释放技能…");
                    return false;
                }

                if (plugin.Net.Role == PeerRole.Host)
                    plugin.Sync.ArmHostLocalSkillCast(null);

                return true;
            }
        }

        [HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.proc_SkillDoAction))]
        private static class Patch_proc_SkillDoAction
        {
            private static bool Prefix(GameTileData lt)
            {
                if (!GateUtil.LanArmed(out var plugin))
                    return true;
                if (GateUtil.AllowApplyDrivenVanillaBody())
                    return true;
                if (SyncContext.SuppressNetworkEmit)
                {
                    GateUtil.Toast("请稍候");
                    return false;
                }
                if (GateUtil.ShouldBlockUx(out var reason))
                {
                    GateUtil.Toast(reason);
                    return false;
                }

                if (plugin.Net.Role == PeerRole.Guest)
                {
                    if (!GateUtil.GuestMayEmitIntent(plugin))
                    {
                        if (GateUtil.IsBattlePlayPhase())
                            GateUtil.Toast(InputGateRules.WaitingHostConfirm);
                        return !GateUtil.IsBattlePlayPhase();
                    }
                    if (lt == null)
                    {
                        GateUtil.Toast("请选择技能目标");
                        return false;
                    }
                    var pos = lt.pos;
                    var skill = GS_Battle.self?.selected_skill ?? GS_Battle.self?.cur_player?.co_data?.skill_action;
                    var intent = plugin.Sync.BuildIntent("CastSkill", target: pos);
                    if (skill?.sd_skill != null)
                        intent.extrasJson = skill.sd_skill.name;
                    plugin.Sync.SubmitIntent(intent, guestOptimisticApply: false);
                    // Leave SKILL_SELECT so further clicks are not silent no-ops while awaiting Host.
                    try
                    {
                        var battle = GS_Battle.self;
                        if (battle != null)
                        {
                            battle.ux_state = UX_State.NONE;
                            battle.selected_skill = null;
                            battle.ux_action_cate = ActionCate.NONE;
                        }
                        BattleEventBus.self.TriggerUXStateChanged();
                    }
                    catch { /* ignore */ }
                    GateUtil.Toast("已请求释放技能…");
                    return false;
                }

                // Host: arm SafeWrap + stamp tile before UX StartCoroutine body runs.
                if (plugin.Net.Role == PeerRole.Host)
                    plugin.Sync.ArmHostLocalSkillCast(lt);

                return true;
            }
        }

        /// <summary>
        /// Vanilla only Debug.Logs when the click is outside the skill select zone — Guest
        /// sees a dead click (especially area buffs that allow empty tiles inside zone but
        /// miss when FOW/select cache drifted). Toast on LAN local skill UX.
        /// </summary>
        [HarmonyPatch(typeof(ActionData), nameof(ActionData.IsPosInSelectZone))]
        private static class Patch_IsPosInSelectZone_Toast
        {
            private static void Postfix(ActionData __instance, GameTileData gtd, bool __result)
            {
                if (__result || gtd == null)
                    return;
                if (!GateUtil.LanArmed(out _))
                    return;
                if (GateUtil.AllowApplyDrivenVanillaBody())
                    return;
                var battle = GS_Battle.self;
                if (battle == null || battle.ux_state != UX_State.SKILL_SELECT)
                    return;
                if (!ReferenceEquals(battle.selected_skill, __instance) &&
                    !ReferenceEquals(battle.cur_player?.co_data?.skill_action, __instance))
                    return;
                GateUtil.Toast("不在技能可选范围内");
            }
        }

        [HarmonyPatch(typeof(UI_SkillBtn), nameof(UI_SkillBtn.OnClick))]
        private static class Patch_UI_SkillBtn_OnClick
        {
            private static bool Prefix(UI_SkillBtn __instance)
            {
                // INV-SOLO: never intercept skill clicks outside LAN battle.
                if (!GateUtil.LanArmed(out _))
                    return true;

                if (GateUtil.IsSpectating())
                    return false; // silent — button should already be hidden

                if (!GateUtil.ShouldBlockUx(out var reason))
                {
                    // Empty skill_action NRE guard (loadout miss / no-CO seats) — LAN only.
                    try
                    {
                        var co = GS_Battle.self?.cur_player?.co_data;
                        if (co?.skill_action == null)
                        {
                            GateUtil.Toast("当前指挥官无可用技能");
                            return false;
                        }
                    }
                    catch { /* allow vanilla */ }
                    return true;
                }
                GateUtil.Toast(reason);
                return false;
            }
        }

        [HarmonyPatch(typeof(UI_SkillBtn), nameof(UI_SkillBtn.Render))]
        private static class Patch_UI_SkillBtn_Render
        {
            private static bool Prefix(UI_SkillBtn __instance)
            {
                // INV-SOLO: do not mutate energy_max / hide buttons in solo/campaign.
                if (!GateUtil.LanArmed(out _))
                    return true;

                try
                {
                    if (GateUtil.IsSpectating())
                    {
                        if (__instance != null)
                            __instance.gameObject.SetActive(false);
                        return false;
                    }

                    var co = GS_Battle.self?.cur_player?.co_data;
                    if (co != null && co.skill_action == null)
                    {
                        // Vanilla Render NREs on null skill_action; keep button hidden.
                        if (__instance != null)
                            __instance.gameObject.SetActive(false);
                        return false;
                    }

                    // Keep energy_max in sync so IsEnergyMax / percent match Host attach formula.
                    if (co != null)
                    {
                        var max = CoEnergyRules.ComputeEnergyMax(co.skill_used_times);
                        if (System.Math.Abs(co.energy_max - max) > 0.001f)
                            co.energy_max = max;
                    }
                }
                catch { /* fall through to vanilla */ }
                return true;
            }
        }

        /// <summary>
        /// Vanilla UpdateRender shows the skill btn whenever cur_player is a non-AI human with
        /// a skill SD — including a remote human while we spectate. Hide on LAN spectate;
        /// on own turn refresh energy_max then let vanilla show + Render (full-energy ring).
        /// </summary>
        [HarmonyPatch(typeof(UI_Part_SkillPower), "UpdateRender")]
        private static class Patch_SkillPower_UpdateRender
        {
            private static bool Prefix(UI_Part_SkillPower __instance)
            {
                if (__instance?.skillBtn == null)
                    return true;

                if (!GateUtil.LanArmed(out _))
                    return true;

                var battle = GS_Battle.self;
                var cur = battle?.cur_player;
                var co = cur?.co_data;
                var hasSkill = false;
                try { hasSkill = co?.skill != null; }
                catch { hasSkill = false; }

                var show = PresentationRules.ShouldShowCoSkillButton(
                    inLanBattle: true,
                    gatesArmed: true,
                    isSpectating: GateUtil.IsSpectating(),
                    curPlayerIsAi: cur != null && cur.is_ai,
                    hasCoSkillSd: hasSkill);

                if (!show)
                {
                    __instance.skillBtn.gameObject.SetActive(false);
                    TrySetPingSkillBtn(false);
                    return false;
                }

                // Own operable turn: align energy_max before vanilla Render / IsEnergyMax.
                try
                {
                    if (co != null)
                    {
                        var max = CoEnergyRules.ComputeEnergyMax(co.skill_used_times);
                        if (System.Math.Abs(co.energy_max - max) > 0.001f)
                            co.energy_max = max;
                    }
                }
                catch { /* ignore */ }

                return true;
            }

            private static void Postfix(UI_Part_SkillPower __instance)
            {
                if (__instance?.skillBtn == null || !GateUtil.LanArmed(out _))
                    return;
                // Mirror ping target with button visibility (spectate hide / own-turn show).
                TrySetPingSkillBtn(__instance.skillBtn.gameObject.activeSelf);
            }

            private static void TrySetPingSkillBtn(bool active)
            {
                try
                {
                    var ping = SingletonMono<SS_ANNW_Game>.self?.ui?.ping_manager;
                    if (ping?.rt_skill_btn != null)
                        ping.rt_skill_btn.gameObject.SetActive(active);
                }
                catch { /* ui may be missing */ }
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
                    // INV-SOLO: Suppress/Applying flags must not alter CreateUnit outside LAN.
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

        /// <summary>
        /// Belt-and-suspenders: never let LifeTime OnEffectEnd Die during Guest attach rebinds.
        /// Real Host expiry still runs (no ApplyingRemoteCommand). Silent EffectHost clear is primary.
        /// INV-SOLO: never swallow OnEffectEnd outside LAN (would strand LifeTime summons).
        /// </summary>
        [HarmonyPatch(typeof(UnitEffect_LifeTime), "OnEffectEnd")]
        private static class Patch_LifeTime_OnEffectEnd
        {
            private static bool Prefix()
            {
                if (!GateUtil.LanArmed(out _))
                    return true;
                if (SyncContext.ApplyingRemoteCommand)
                    return false;
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
