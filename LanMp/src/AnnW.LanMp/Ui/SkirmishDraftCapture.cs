using System.Collections.Generic;
using System.Text;
using ANNW;
using AnnW.LanMp.Authority;
using AnnW.LanMp.Protocol;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AnnW.LanMp.Ui
{
    /// <summary>Read native skirmish panel into <see cref="LobbyDraftDto"/> (solo-testable).</summary>
    internal static class SkirmishDraftCapture
    {
        private static readonly AccessTools.FieldRef<UI_MENU_LevelSelect_InfoSkm, DynOb> SelectedOb =
            AccessTools.FieldRefAccess<UI_MENU_LevelSelect_InfoSkm, DynOb>("selected_ob");
        private static readonly AccessTools.FieldRef<UI_MENU_LevelSelect_InfoSkm, string> SelectedObName =
            AccessTools.FieldRefAccess<UI_MENU_LevelSelect_InfoSkm, string>("selected_ob_name");
        private static readonly AccessTools.FieldRef<UI_MENU_LevelSelect_InfoSkm, string> SelectedMap =
            AccessTools.FieldRefAccess<UI_MENU_LevelSelect_InfoSkm, string>("selected_map");

        public static bool TryCapture(UI_MENU_LevelSelect_InfoSkm ui, NetSession net, out LobbyDraftDto draft, out string error)
        {
            draft = null;
            error = null;
            if (ui == null)
            {
                error = "skirmish UI null";
                return false;
            }

            var ob = SelectedOb(ui);
            var obName = SelectedObName(ui);
            var mapPath = SelectedMap(ui);

            string mapId;
            string display;
            if (ob != null && !string.IsNullOrEmpty(obName))
            {
                mapId = obName;
                display = obName;
                // Prefer Resources-style path when SD can resolve; BuildStartGameSetting accepts SD name.
            }
            else if (!string.IsNullOrEmpty(mapPath))
            {
                mapId = mapPath;
                display = System.IO.Path.GetFileNameWithoutExtension(mapPath);
            }
            else
            {
                error = "no map selected";
                return false;
            }

            var fow = ReadIntProp(AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "dd_fow")?.GetValue(ui), "value", 1);
            var win = ReadIntProp(AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "dd_condition")?.GetValue(ui), "value", 0);
            var qs = ReadIntProp(AccessTools.Field(typeof(UI_MENU_LevelSelect_InfoSkm), "dd_quickStart")?.GetValue(ui), "value", 2);

            var hostSlot = 0;
            var guestSlot = 1;
            TryReadHumanSlots(ui.group, ref hostSlot, ref guestSlot);

            draft = new LobbyDraftDto
            {
                mapId = mapId,
                mapDisplayName = display ?? mapId,
                mapContentHash = "pending",
                fowType = fow,
                winCondition = win,
                quickStart = qs,
                hostPeerId = net?.LocalPeerId ?? "",
                guestPeerId = net?.RemotePeerId ?? "",
                hostSlotIndex = hostSlot,
                guestSlotIndex = guestSlot
            };
            return true;
        }

        public static string ValidateAndHash(LobbyDraftDto draft, ManualLogSource log)
        {
            if (draft == null)
                return "draft null";
            var sgs = BattleBootstrap.BuildStartGameSetting(draft, log);
            if (sgs == null)
                return "map resolve failed";
            var sb = new StringBuilder();
            sb.Append("OK map=").Append(sgs.filename);
            sb.Append(" hash=").Append(draft.mapContentHash);
            sb.Append(" players=").Append(sgs.players?.Count ?? 0);
            sb.Append(" fow=").Append(draft.fowType);
            sb.Append(" win=").Append(draft.winCondition);
            sb.Append(" qs=").Append(draft.quickStart);
            sb.Append(" slots H/G=").Append(draft.hostSlotIndex).Append('/').Append(draft.guestSlotIndex);
            return sb.ToString();
        }

        private static int ReadIntProp(object component, string propName, int fallback)
        {
            if (component == null)
                return fallback;
            try
            {
                var p = component.GetType().GetProperty(propName);
                if (p == null)
                    return fallback;
                return (int)p.GetValue(component, null);
            }
            catch
            {
                return fallback;
            }
        }

        private static void TryReadHumanSlots(UI_SKM_PlayerSettingGroup group, ref int hostSlot, ref int guestSlot)
        {
            if (group == null)
                return;
            try
            {
                var list = group.GenerateData();
                if (list == null || list.Count == 0)
                    return;
                var humans = new List<int>();
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] != null && list[i].controller == PlayerControl.Human)
                        humans.Add(i);
                }
                if (humans.Count >= 1)
                    hostSlot = humans[0];
                if (humans.Count >= 2)
                    guestSlot = humans[1];
                else if (list.Count > 1)
                    guestSlot = hostSlot == 0 ? 1 : 0;
            }
            catch
            {
                // Keep defaults.
            }
        }
    }
}
