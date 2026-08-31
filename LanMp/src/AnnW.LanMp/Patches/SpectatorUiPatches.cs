using System;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Ui;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// LAN spectator turn UX: hide bottom-right idle cluster while spectating,
    /// restore only what we hid (never force-enable deprecated vanilla controls).
    /// </summary>
    internal static class SpectatorUiPatches
    {
        private static GameObject _bannerGo;
        private static TextMeshProUGUI _bannerTmp;
        private static bool _lastSpectate;
        private static string _lastBanner;

        private static bool _hidRightPage;
        private static bool _hidEndTurn;

        [HarmonyPatch(typeof(SS_ANNW_Game), nameof(SS_ANNW_Game.StartGameFormal))]
        private static class Patch_StartGameFormal
        {
            private static void Postfix()
            {
                LanMpPlugin.Instance?.Authority?.ApplyLocalViewBinding("start-game-formal");
            }
        }

        [HarmonyPatch(typeof(UI_Part_IdleButtons), nameof(UI_Part_IdleButtons.OnBtn_EndTurn))]
        private static class Patch_Idle_EndTurn
        {
            private static bool Prefix() => !GateUtil.IsSpectating();
        }

        [HarmonyPatch(typeof(UI_Part_IdleButtons), nameof(UI_Part_IdleButtons.OnBtn_UndoMove))]
        private static class Patch_Idle_Undo
        {
            private static bool Prefix() => !GateUtil.IsSpectating();
        }

        [HarmonyPatch(typeof(UI_Part_IdleButtons), nameof(UI_Part_IdleButtons.OnBtn_NextUnit))]
        private static class Patch_Idle_NextUnit
        {
            private static bool Prefix() => !GateUtil.IsSpectating();
        }

        [HarmonyPatch(typeof(UI_Part_IdleButtons), nameof(UI_Part_IdleButtons.OnBtn_AutoCmd))]
        private static class Patch_Idle_AutoCmd
        {
            private static bool Prefix() => !GateUtil.IsSpectating();
        }

        [HarmonyPatch(typeof(UI_Part_IdleButtons), nameof(UI_Part_IdleButtons.OnBtn_UnitList))]
        private static class Patch_Idle_UnitList
        {
            private static bool Prefix() => !GateUtil.IsSpectating();
        }

        [HarmonyPatch(typeof(UI_Part_IdleButtons), nameof(UI_Part_IdleButtons.OnBtn_History))]
        private static class Patch_Idle_History
        {
            private static bool Prefix() => !GateUtil.IsSpectating();
        }

        [HarmonyPatch(typeof(UI_Part_IdleButtons), nameof(UI_Part_IdleButtons.OnBtn_SimpleFraction))]
        private static class Patch_Idle_SimpleFraction
        {
            private static bool Prefix() => !GateUtil.IsSpectating();
        }

        [HarmonyPatch(typeof(UI_Part_IdleButtons), nameof(UI_Part_IdleButtons.OnBtn_SwitchFilter))]
        private static class Patch_Idle_Filter
        {
            private static bool Prefix() => !GateUtil.IsSpectating();
        }

        [HarmonyPatch(typeof(UI_Part_IdleButtons), "Update")]
        private static class Patch_Idle_Update
        {
            private static void Postfix(UI_Part_IdleButtons __instance)
            {
                if (__instance == null || !GateUtil.LanArmed(out _))
                {
                    // Solo / campaign: never keep LAN hide flags; restore once then clear.
                    RestoreIdleCluster(__instance);
                    _hidRightPage = false;
                    _hidEndTurn = false;
                    SetBanner(false, null, __instance);
                    return;
                }

                var spectate = GateUtil.IsSpectating();
                if (spectate)
                    HideIdleCluster(__instance);
                else
                    RestoreIdleCluster(__instance);

                SetBanner(spectate, BuildOperatorLabel(), __instance);
            }
        }

        /// <summary>Only hide; never SetActive(true) on controls that vanilla left disabled (e.g. unit list).</summary>
        private static void HideIdleCluster(UI_Part_IdleButtons idle)
        {
            if (idle.default_right_page != null)
            {
                if (idle.default_right_page.activeSelf)
                    _hidRightPage = true;
                idle.default_right_page.SetActive(false);
            }

            // Vanilla Update() re-enables these every frame; force off after it runs (Postfix).
            try
            {
                if (idle.btn_auto_cmd != null)
                    idle.btn_auto_cmd.gameObject.SetActive(false);
                if (idle.btn_undo_move != null)
                    idle.btn_undo_move.gameObject.SetActive(false);
                if (idle.btn_next_unit != null)
                    idle.btn_next_unit.gameObject.SetActive(false);

                // These MulColor toggles sit outside default_right_page — hide explicitly.
                if (idle.mc_history != null)
                    idle.mc_history.gameObject.SetActive(false);
                if (idle.mc_filter != null)
                    idle.mc_filter.gameObject.SetActive(false);
                if (idle.mc_simple_fraction != null)
                    idle.mc_simple_fraction.gameObject.SetActive(false);
                if (idle.mc_unitlist != null)
                    idle.mc_unitlist.gameObject.SetActive(false);
            }
            catch { /* ignore */ }

            try
            {
                var ping = SingletonMono<SS_ANNW_Game>.self?.ui?.ping_manager;
                if (ping?.rt_end_turn_btn != null)
                {
                    if (ping.rt_end_turn_btn.gameObject.activeSelf)
                        _hidEndTurn = true;
                    ping.rt_end_turn_btn.gameObject.SetActive(false);
                }
            }
            catch
            {
                // UI may not exist yet.
            }
        }

        private static void RestoreIdleCluster(UI_Part_IdleButtons idle)
        {
            if (_hidRightPage && idle != null && idle.default_right_page != null)
            {
                idle.default_right_page.SetActive(true);
                _hidRightPage = false;
            }

            if (_hidEndTurn)
            {
                try
                {
                    var ping = SingletonMono<SS_ANNW_Game>.self?.ui?.ping_manager;
                    if (ping?.rt_end_turn_btn != null)
                        ping.rt_end_turn_btn.gameObject.SetActive(true);
                }
                catch { /* ignore */ }
                _hidEndTurn = false;
            }
        }

        private static string BuildOperatorLabel()
        {
            var battle = GS_Battle.self;
            var cur = battle?.cur_player;
            if (cur == null)
                return "观战回合";

            var name = ResolveOperatorName(cur);
            if (cur.is_ai)
                return "观战回合，当前操作者：" + name + "（AI）";
            return "观战回合，当前操作者：" + name;
        }

        private static string ResolveOperatorName(Player cur)
        {
            var plugin = LanMpPlugin.Instance;
            var draft = plugin?.Lobby?.Draft;
            if (draft?.seats != null && cur.index >= 0 && cur.index < draft.seats.Length)
            {
                var seat = draft.seats[cur.index];
                if (seat != null && !string.IsNullOrWhiteSpace(seat.occupantName))
                    return seat.occupantName.Trim();
                if (seat != null && !string.IsNullOrEmpty(seat.peerId))
                {
                    if (seat.peerId == draft.hostPeerId && !string.IsNullOrWhiteSpace(draft.hostDisplayName))
                        return draft.hostDisplayName.Trim();
                    if (seat.peerId == draft.guestPeerId && !string.IsNullOrWhiteSpace(draft.guestDisplayName))
                        return draft.guestDisplayName.Trim();
                }
            }

            try
            {
                var sd = cur.co_data?.sd_commander;
                if (sd != null)
                {
                    try
                    {
                        var loc = LAN.Get("SD_LAN_CO_NAME." + sd.name);
                        if (!string.IsNullOrEmpty(loc) && loc.IndexOf("SD_LAN_CO_NAME", StringComparison.Ordinal) < 0)
                            return loc;
                    }
                    catch { /* ignore */ }
                    if (!string.IsNullOrEmpty(sd.name))
                        return sd.name;
                }
            }
            catch { /* ignore */ }

            return cur.is_ai ? ("AI" + cur.index) : ("玩家" + cur.index);
        }

        private static void SetBanner(bool show, string text, UI_Part_IdleButtons idle)
        {
            if (!show)
            {
                if (_bannerGo != null && _bannerGo.activeSelf)
                    _bannerGo.SetActive(false);
                _lastSpectate = false;
                _lastBanner = null;
                return;
            }

            EnsureBanner(idle);
            if (_bannerGo == null || _bannerTmp == null)
                return;

            if (!_bannerGo.activeSelf)
                _bannerGo.SetActive(true);

            if (text != _lastBanner || !_lastSpectate)
            {
                _bannerTmp.text = text ?? "观战回合";
                _lastBanner = text;
            }
            _lastSpectate = true;
        }

        private static void EnsureBanner(UI_Part_IdleButtons idle)
        {
            if (_bannerGo != null)
                return;

            try
            {
                Transform parent = null;
                if (idle != null)
                    parent = idle.transform.parent != null ? idle.transform.parent : idle.transform;
                if (parent == null)
                {
                    var ui = SingletonMono<SS_ANNW_Game>.self?.ui;
                    if (ui != null)
                        parent = ui.transform;
                }
                if (parent == null)
                    return;

                if (!AnnwUiKit.EnsureSampled())
                    return;

                var rt = AnnwUiKit.CreateRect("LanMp_SpectatorBanner", parent);
                rt.anchorMin = new Vector2(1f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(1f, 0f);
                rt.sizeDelta = new Vector2(420f, 48f);
                rt.anchoredPosition = new Vector2(-24f, 28f);

                var img = AnnwUiKit.CreateImage(rt, AnnwUiKit.PanelSprite, new Color(0.05f, 0.05f, 0.08f, 0.72f));
                if (img != null)
                    img.raycastTarget = false;

                _bannerTmp = AnnwUiKit.CreateTmp(
                    rt, "Text", "观战回合", 22f,
                    new Color(0.92f, 0.92f, 0.85f, 1f),
                    TextAlignmentOptions.MidlineRight);
                if (_bannerTmp != null)
                {
                    _bannerTmp.raycastTarget = false;
                    var tr = _bannerTmp.rectTransform;
                    tr.anchorMin = Vector2.zero;
                    tr.anchorMax = Vector2.one;
                    tr.offsetMin = new Vector2(12f, 4f);
                    tr.offsetMax = new Vector2(-12f, -4f);
                }

                _bannerGo = rt.gameObject;
                _bannerGo.SetActive(false);
            }
            catch (Exception ex)
            {
                LanMpPlugin.Log?.LogWarning("[SpectatorUI] banner: " + ex.Message);
            }
        }

        [HarmonyPatch(typeof(UX_Manager), "OnWorldLeftClick_Alt", typeof(Vector3))]
        private static class Patch_UxLeftClick_Spectate
        {
            private static bool Prefix() => !GateUtil.IsSpectating();
        }

        [HarmonyPatch(typeof(UX_Manager), "OnWorldRightClick_Alt", typeof(Vector3))]
        private static class Patch_UxRightClick_Spectate
        {
            private static bool Prefix() => !GateUtil.IsSpectating();
        }
    }
}
