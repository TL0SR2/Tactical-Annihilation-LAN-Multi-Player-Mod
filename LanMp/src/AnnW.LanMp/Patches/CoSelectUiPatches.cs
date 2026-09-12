using System;
using System.Collections.Generic;
using System.Reflection;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Ui;
using ANNW;
using HarmonyLib;
using UnityEngine;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// Vanilla skirmish CO select bugs (also hit LAN room ShowForSkirmish):
    /// 1) PartPS.IsAvailable uses Max(1, level) under for_skirmish — free slot index==2
    ///    still needs level&gt;2, so low-level COs cannot pick the free PS.
    /// 2) Skill/PS picker popups can open under the LAN room canvas — raise sorting.
    /// 3) RenderCOInfo(SetAsCampaign) loads archive/table into temp_* — LAN must re-seed
    ///    seat.skillId/psIds so confirm does not stamp archive defaults over authored picks.
    /// 4) PopulateSkillAndPSFromUI must stay on CUSTOM while for_skirmish so confirm reads temp_*.
    /// </summary>
    internal static class CoSelectUiPatches
    {
        private static readonly FieldInfo AllPsFi =
            AccessTools.Field(typeof(UI_CO_Select_Info), "all_ps");
        private static readonly MethodInfo RenderSkillMi =
            AccessTools.Method(typeof(UI_CO_Select_Info), "RenderSkill");
        private static readonly MethodInfo RenderAllPsMi =
            AccessTools.Method(typeof(UI_CO_Select_Info), "RenderAllPS");
        private static readonly MethodInfo SetSelectionMi =
            AccessTools.Method(typeof(UI_CO_Select), "SetSelection", new[] { typeof(CO_Sel_Item) });

        /// <summary>Seat loadout to paint onto temp_* after SetAsCampaign (same CO only).</summary>
        private static string _seedCoId;
        private static string _seedSkillId;
        private static string[] _seedPsIds;
        private static bool _seedActive;

        /// <summary>Call before/after ShowForSkirmish so RenderCOInfo can restore seat picks.</summary>
        internal static void BeginSeatLoadoutSeed(LobbySeatDto seat)
        {
            _seedActive = false;
            _seedCoId = null;
            _seedSkillId = null;
            _seedPsIds = null;
            if (seat == null)
                return;
            if (string.IsNullOrEmpty(seat.coId) || seat.coId == "__none__")
                return;
            _seedCoId = seat.coId;
            _seedSkillId = seat.skillId ?? "";
            _seedPsIds = seat.psIds;
            _seedActive = CoLoadoutRules.HasAuthoredLoadout(seat);
        }

        internal static void ClearSeatLoadoutSeed()
        {
            _seedActive = false;
            _seedCoId = null;
            _seedSkillId = null;
            _seedPsIds = null;
        }

        /// <summary>Mirror vanilla UI_SKM_PlayerSetting.OnBtnCO_Sel after ShowForSkirmish.</summary>
        internal static void ApplySkirmishSelection(UI_CO_Select select, LobbySeatDto seat)
        {
            if (select == null || SetSelectionMi == null)
                return;
            var item = new CO_Sel_Item();
            try
            {
                if (seat == null || string.IsNullOrEmpty(seat.coId))
                {
                    item.is_random = true;
                }
                else if (seat.coId == "__none__")
                {
                    item.sd_co = null;
                    item.is_random = false;
                }
                else
                {
                    item.sd_co = SDBase<SD_ANNW_CO>.Get(seat.coId);
                    item.is_random = false;
                }
            }
            catch
            {
                item = new CO_Sel_Item { is_random = true };
            }

            try
            {
                SetSelectionMi.Invoke(select, new object[] { item });
            }
            catch (Exception ex)
            {
                LanMpPlugin.Log?.LogWarning("[RoomUI] SetSelection: " + ex.Message);
            }
        }

        /// <summary>
        /// Read CUSTOM temps (what the player sees) — more reliable than Populate when
        /// data_source drifts to CAMPAIGN (which ignores temp for fixed COs).
        /// </summary>
        internal static void ReadSkirmishLoadoutFromUi(
            UI_CO_Select select,
            out string skillId,
            out string[] psIds)
        {
            skillId = "";
            psIds = new string[0];
            if (select?.co_info == null)
                return;

            try
            {
                var info = select.co_info;
                if (select.for_skirmish)
                    info.data_source = UI_CO_Select_Info.DATA_SOURCE.CUSTOM;

                if (info.part_skill != null && !string.IsNullOrEmpty(info.part_skill.temp_selected_skill))
                    skillId = info.part_skill.temp_selected_skill;

                var list = new List<string>();
                var allPs = AllPsFi?.GetValue(info) as System.Collections.IList;
                if (allPs != null)
                {
                    for (var i = 0; i < allPs.Count && i < 3; i++)
                    {
                        if (!(allPs[i] is UI_CO_Select_PartPS part))
                            continue;
                        if (string.IsNullOrEmpty(part.temp_selcted_ps))
                            continue;
                        if (!list.Contains(part.temp_selcted_ps))
                            list.Add(part.temp_selcted_ps);
                    }
                }

                psIds = list.ToArray();
            }
            catch (Exception ex)
            {
                LanMpPlugin.Log?.LogWarning("[RoomUI] ReadSkirmishLoadout: " + ex.Message);
            }
        }

        private static void ApplySeedToInfo(UI_CO_Select_Info info)
        {
            if (!_seedActive || info?.parent == null || string.IsNullOrEmpty(_seedCoId))
                return;
            var sd = info.parent.cur_selection.sd_co;
            if (sd == null || !string.Equals(sd.name, _seedCoId, StringComparison.Ordinal))
                return;

            try
            {
                if (info.part_skill != null && !string.IsNullOrEmpty(_seedSkillId))
                    info.part_skill.temp_selected_skill = _seedSkillId;

                var allPs = AllPsFi?.GetValue(info) as System.Collections.IList;
                if (allPs != null && _seedPsIds != null)
                {
                    for (var i = 0; i < allPs.Count && i < 3; i++)
                    {
                        if (!(allPs[i] is UI_CO_Select_PartPS part))
                            continue;
                        part.temp_selcted_ps = i < _seedPsIds.Length && _seedPsIds[i] != null
                            ? _seedPsIds[i]
                            : "";
                    }
                }

                RenderSkillMi?.Invoke(info, null);
                RenderAllPsMi?.Invoke(info, null);
            }
            catch (Exception ex)
            {
                LanMpPlugin.Log?.LogWarning("[RoomUI] seed loadout: " + ex.Message);
            }
        }

        /// <summary>Skirmish: unlock all three PS slots (incl. free) regardless of archive level.</summary>
        [HarmonyPatch(typeof(UI_CO_Select_PartPS), "IsAvailable")]
        private static class Patch_PartPS_IsAvailable
        {
            private static bool Prefix(UI_CO_Select_PartPS __instance, ref bool __result)
            {
                try
                {
                    if (!LanCoSelectActive())
                        return true;
                    var parent = __instance.parent;
                    if (parent == null || !parent.for_skirmish)
                        return true;
                    if (parent.cur_selection.sd_co == null)
                    {
                        __result = false;
                        return false;
                    }
                    // index 0..2 → need effectiveLevel > index; force effectiveLevel ≥ 3.
                    __result = __instance.index < 3;
                    return false;
                }
                catch
                {
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(UI_CO_Select_Info), "RenderCOInfo")]
        private static class Patch_RenderCOInfo_FreePsButton
        {
            private static void Postfix(UI_CO_Select_Info __instance, bool for_skirmish)
            {
                if (!for_skirmish || __instance == null || !LanCoSelectActive())
                    return;
                try
                {
                    // Mirror Zero: always show random-PS in skirmish so free slot is reachable.
                    if (__instance.btn_rd_ps != null)
                        __instance.btn_rd_ps.SetActive(true);
                    // SetAsCampaign just filled archive/table into temp — restore LAN seat picks.
                    ApplySeedToInfo(__instance);
                }
                catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Ensure confirm reads temp_* (CUSTOM). CAMPAIGN Populate ignores temp for fixed COs
        /// and would stamp table defaults despite the UI showing the player's pick.
        /// </summary>
        [HarmonyPatch(typeof(UI_CO_Select_Info), nameof(UI_CO_Select_Info.PopulateSkillAndPSFromUI))]
        private static class Patch_Populate_ForceCustomInSkirmish
        {
            private static void Prefix(UI_CO_Select_Info __instance)
            {
                try
                {
                    if (!LanCoSelectActive())
                        return;
                    if (__instance?.parent != null && __instance.parent.for_skirmish)
                        __instance.data_source = UI_CO_Select_Info.DATA_SOURCE.CUSTOM;
                }
                catch { /* ignore */ }
            }
        }

        /// <summary>INV-SOLO: CO/PS overrides only while LAN room / start / seat seed is live.</summary>
        private static bool LanCoSelectActive()
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null || plugin.Enabled == null || !plugin.Enabled.Value)
                return false;
            return SoloIsolationRules.AllowLanCoSelectOverrides(
                pluginEnabled: true,
                lanRoomOpen: LanRoomPanel.IsOpen,
                startAuthorized: plugin.Lobby != null && plugin.Lobby.StartAuthorized,
                seatSeedActive: _seedActive);
        }

        [HarmonyPatch(typeof(UI_POP_SK_Select), nameof(UI_POP_SK_Select.ShowSKSselect))]
        private static class Patch_ShowSKSelect
        {
            private static void Postfix(UI_POP_SK_Select __instance)
            {
                if (!LanCoSelectActive())
                    return;
                LanDropMenu.BringFloaterPopupToFront(__instance);
            }
        }

        [HarmonyPatch(typeof(UI_POP_PS_Select), nameof(UI_POP_PS_Select.ShowPSSelect))]
        private static class Patch_ShowPSSelect
        {
            private static void Postfix(UI_POP_PS_Select __instance)
            {
                if (!LanCoSelectActive())
                    return;
                LanDropMenu.BringFloaterPopupToFront(__instance);
            }
        }
    }
}
