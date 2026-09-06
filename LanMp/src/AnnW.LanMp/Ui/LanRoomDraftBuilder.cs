using System;
using AnnW.LanMp.Protocol;
using BepInEx.Logging;

namespace AnnW.LanMp.Ui
{
    /// <summary>Builds LobbyDraftDto seats from map data (AllPlayer preview semantics).</summary>
    internal static class LanRoomDraftBuilder
    {
        public static bool TryBuildFromBuiltin(
            LanRoomMapCatalog.Entry map,
            int fow,
            int win,
            int quickStart,
            string hostName,
            string guestName,
            NetSession net,
            ManualLogSource log,
            out LobbyDraftDto draft,
            out string error)
        {
            draft = null;
            error = null;
            if (map == null)
            {
                error = "未选择地图";
                return false;
            }

            if (!LanRoomMapCatalog.TryLoadText(map, out var text, out var mapKey))
            {
                error = "无法加载地图: " + map.DisplayName;
                return false;
            }

            DynOb ob;
            try
            {
                ob = Singleton<BattleAndMapFileSystem>.self.ReadFileWithMeta_Asset(text);
            }
            catch (Exception ex)
            {
                error = "解析地图失败: " + ex.Message;
                return false;
            }

            if (ob == null)
            {
                error = "地图数据为空";
                return false;
            }

            var preview = new AllPlayer();
            try
            {
                preview.LoadOb(ob.GetKey_Obj("commander"), preview: true);
            }
            catch (Exception ex)
            {
                error = "读取指挥官槽失败: " + ex.Message;
                return false;
            }

            if (preview.players == null || preview.players.Count < 2)
            {
                error = "地图人类位不足（需要至少 2 个槽）";
                return false;
            }

            var hostPeer = net?.LocalPeerId ?? "";
            var hostSlot = 0;
            var seats = new LobbySeatDto[preview.players.Count];
            var usedColors = new bool[LobbySeatLogic.ColorCount];

            for (var i = 0; i < preview.players.Count; i++)
            {
                var src = preview.players[i];
                var color = (int)src.co_color;
                if (color < 0 || color >= LobbySeatLogic.ColorCount)
                    color = i % LobbySeatLogic.ColorCount;
                // Force unique colors on build
                if (usedColors[color])
                {
                    for (var c = 0; c < LobbySeatLogic.ColorCount; c++)
                    {
                        if (!usedColors[c])
                        {
                            color = c;
                            break;
                        }
                    }
                }
                usedColors[color] = true;

                var coId = src.co_data != null && src.co_data.sd_commander != null
                    ? src.co_data.sd_commander.name
                    : "";
                var team = (int)src.fraction;
                var aiCtrl = (int)PlayerControl.AI_Normal;

                if (i == hostSlot)
                {
                    seats[i] = LobbySeatLogic.MakeHostSeat(hostPeer, hostName, i, team, color, coId);
                }
                else
                {
                    seats[i] = LobbySeatLogic.MakeAiSeat(i, team, color, coId, aiCtrl);
                }
            }

            draft = new LobbyDraftDto
            {
                mapId = mapKey,
                mapDisplayName = map.DisplayName,
                mapContentHash = LanRoomMapCatalog.HashOf(text),
                fowType = fow,
                winCondition = win,
                quickStart = quickStart,
                hostPeerId = hostPeer,
                guestPeerId = "",
                hostDisplayName = hostName ?? "",
                guestDisplayName = "",
                hostSlotIndex = hostSlot,
                guestSlotIndex = -1,
                seats = seats
            };

            // Re-seat every connected guest into HumanStandby slots (map change mid-session).
            if (net != null && net.Role == PeerRole.Host && net.IsConnected)
            {
                var peers = net.GetConnectedPeerIds();
                var standbyIdx = 1;
                foreach (var peerId in peers)
                {
                    if (string.IsNullOrEmpty(peerId) || peerId == hostPeer)
                        continue;
                    while (standbyIdx < seats.Length &&
                           LobbySeatLogic.GetState(seats[standbyIdx]) != LobbySeatState.Ai &&
                           LobbySeatLogic.GetState(seats[standbyIdx]) != LobbySeatState.HumanStandby)
                        standbyIdx++;
                    if (standbyIdx >= seats.Length)
                        break;
                    if (LobbySeatLogic.GetState(seats[standbyIdx]) == LobbySeatState.Ai)
                        LobbySeatLogic.TryPromoteToStandby(seats[standbyIdx], out _);
                    var display = net.GetPeerDisplayName(peerId);
                    if (string.IsNullOrEmpty(display))
                        display = peerId;
                    LobbySeatLogic.TrySeatHuman(draft, peerId, display, out _, out _);
                    standbyIdx++;
                }
                LobbySeatLogic.RefreshLegacyGuestFields(draft);
            }

            return true;
        }

        public static void RefreshOccupantNames(LobbyDraftDto draft, string hostName, string guestName)
        {
            if (draft == null)
                return;
            draft.hostDisplayName = hostName ?? "";
            draft.guestDisplayName = guestName ?? "";
            if (draft.seats == null)
                return;
            for (var i = 0; i < draft.seats.Length; i++)
            {
                var s = draft.seats[i];
                if (s == null)
                    continue;
                if (LobbySeatLogic.GetState(s) != LobbySeatState.HumanSeated)
                    continue;
                if (s.peerId == draft.hostPeerId || i == draft.hostSlotIndex)
                    s.occupantName = draft.hostDisplayName;
                // Keep existing non-host occupantName if already set; only fill legacy guest name
                // for the first guestPeerId seat when occupant is empty.
                else if (string.IsNullOrEmpty(s.occupantName) &&
                         !string.IsNullOrEmpty(draft.guestPeerId) && s.peerId == draft.guestPeerId)
                    s.occupantName = string.IsNullOrEmpty(draft.guestDisplayName) ? "" : draft.guestDisplayName;
            }
        }
    }
}
