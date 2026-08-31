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

            // Re-seat existing guest into first standby if already connected (map change mid-session).
            if (net != null && net.Role == PeerRole.Host && net.IsConnected
                && !string.IsNullOrEmpty(net.RemotePeerId))
            {
                // Need a standby first
                if (seats.Length > 1)
                    LobbySeatLogic.TryPromoteToStandby(seats[1], out _);
                LobbySeatLogic.TrySeatHuman(draft, net.RemotePeerId, guestName ?? net.RemoteDisplayName,
                    out _, out _);
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
                else if (!string.IsNullOrEmpty(draft.guestPeerId) && s.peerId == draft.guestPeerId)
                    s.occupantName = string.IsNullOrEmpty(draft.guestDisplayName) ? "" : draft.guestDisplayName;
            }
        }
    }
}
