using System.Collections.Generic;
using AnnW.LanMp.Authority;
using AnnW.LanMp.Protocol;
using UnityEngine;

namespace AnnW.LanMp.Ui
{
    internal static class LanOverlay
    {
        private static Rect _rect = new Rect(20, 20, 520, 560);
        private static Vector2 _scroll;
        private static string _mapId = "";
        private static List<string> _maps;
        private static int _mapIndex;
        private static int _tab; // 0 session 1 draft 2 status
        private static string _lastValidate = "";
        private static int _hostSlot;
        private static int _guestSlot = 1;
        private static int _fow = 1;
        private static int _win;
        private static int _quickStart = 2;

        private static string _syncedDraftKey;

        private static readonly string[] Tabs = { "Session", "Draft", "Status" };
        private static readonly string[] FowLabels = { "FOW:None", "FOW:Standard" };
        private static readonly string[] WinLabels = { "Win:CO+Factory", "Win:CO", "Win:AllUnits" };
        private static readonly string[] QsLabels = { "QS:None", "QS:CmdBot", "QS:Standard" };

        public static void Draw(LanMpPlugin plugin)
        {
            _rect = GUI.Window(592310, _rect, id => DrawWindow(id, plugin), "AnnW LanMp  F8");
            var toast = UiFeedback.ActiveToast;
            if (!string.IsNullOrEmpty(toast))
            {
                var tr = new Rect(20, Screen.height - 70, Mathf.Min(640, Screen.width - 40), 48);
                GUI.Box(tr, toast);
            }
        }

        private static void DrawWindow(int id, LanMpPlugin plugin)
        {
            var net = plugin.Net;
            var lobby = plugin.Lobby;
            var auth = plugin.Authority;
            var log = LanMpPlugin.Log;

            EnsureMaps(log);
            PullEditorsFromLiveDraft(lobby);

            GUILayout.Label($"v{LanMpPlugin.PluginVersion}  |  role={net.Role}  connected={net.IsConnected}");
            _tab = GUILayout.Toolbar(_tab, Tabs);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(430));
            switch (_tab)
            {
                case 0:
                    DrawSession(plugin);
                    break;
                case 1:
                    DrawDraft(plugin);
                    break;
                default:
                    DrawStatus(plugin);
                    break;
            }
            GUILayout.EndScrollView();

            GUI.DragWindow();
        }

        private static void EnsureMaps(BepInEx.Logging.ManualLogSource log)
        {
            if (_maps != null)
                return;
            _maps = BattleBootstrap.ListBuiltinSkirmishMapNames(log);
            if (_maps.Count > 0 && string.IsNullOrEmpty(_mapId))
                _mapId = _maps[0];
        }

        private static void PullEditorsFromLiveDraft(LobbySession lobby)
        {
            if (lobby?.Draft == null || string.IsNullOrEmpty(lobby.Draft.mapId))
                return;
            var key = lobby.Draft.mapId + "|" + lobby.Draft.mapContentHash + "|" + lobby.Draft.fowType + "|" +
                      lobby.Draft.winCondition + "|" + lobby.Draft.quickStart + "|" +
                      lobby.Draft.hostSlotIndex + "|" + lobby.Draft.guestSlotIndex;
            if (key == _syncedDraftKey)
                return;
            _syncedDraftKey = key;
            _mapId = lobby.Draft.mapId;
            _fow = Mathf.Clamp(lobby.Draft.fowType, 0, 1);
            _win = Mathf.Clamp(lobby.Draft.winCondition, 0, 2);
            _quickStart = Mathf.Clamp(lobby.Draft.quickStart, 0, 2);
            _hostSlot = lobby.Draft.hostSlotIndex;
            _guestSlot = lobby.Draft.guestSlotIndex;
            if (_maps != null)
            {
                var idx = _maps.IndexOf(_mapId);
                if (idx >= 0)
                    _mapIndex = idx;
            }
        }

        private static void DrawSession(LanMpPlugin plugin)
        {
            var net = plugin.Net;
            var lobby = plugin.Lobby;
            var auth = plugin.Authority;
            var log = LanMpPlugin.Log;

            GUILayout.Label($"peer={net.LocalPeerId}");
            GUILayout.Label($"remote={net.RemotePeerId ?? "(none)"}");
            GUILayout.Label($"ready L/R={lobby.LocalReady}/{lobby.RemoteReady}  canStart={lobby.CanStart}");
            GUILayout.Label($"gates={auth.GatesArmed}  inBattle={auth.InLanBattle}");
            GUILayout.Label($"battleId={lobby.BattleId ?? "-"}  seed={lobby.BattleSeed}");

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Host", GUILayout.Height(28)))
            {
                try
                {
                    net.StartHost(plugin.HostPort.Value);
                    UiFeedback.Push("Hosting on port " + plugin.HostPort.Value);
                }
                catch (System.Exception ex)
                {
                    log?.LogError(ex);
                    UiFeedback.Push("Host failed: " + ex.Message);
                }
            }
            if (GUILayout.Button("Join", GUILayout.Height(28)))
            {
                try
                {
                    net.ConnectGuest(plugin.JoinAddress.Value);
                    UiFeedback.Push("Joining " + plugin.JoinAddress.Value);
                }
                catch (System.Exception ex)
                {
                    log?.LogError(ex);
                    UiFeedback.Push("Join failed: " + ex.Message);
                }
            }
            if (GUILayout.Button("Disconnect", GUILayout.Height(28)))
            {
                net.Disconnect("ui");
                UiFeedback.Push("Disconnected");
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Join address (host:port)");
            plugin.JoinAddress.Value = GUILayout.TextField(plugin.JoinAddress.Value);

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUI.enabled = net.Role != PeerRole.None && (net.IsConnected || net.Role == PeerRole.Host);
            if (GUILayout.Button("Ready"))
            {
                if (!net.IsConnected && net.Role == PeerRole.Host)
                    UiFeedback.Push("Ready needs a connected guest (Host is listening)");
                else
                {
                    lobby.SetLocalReady(true);
                    UiFeedback.Push("Ready=true");
                }
            }
            if (GUILayout.Button("Unready"))
            {
                lobby.SetLocalReady(false);
                UiFeedback.Push("Ready=false");
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUI.enabled = net.Role == PeerRole.Host && lobby.CanStart;
            if (GUILayout.Button("Start Battle (M03 gate)", GUILayout.Height(30)))
            {
                auth.TryHostStartBattle();
                UiFeedback.Push("Start Battle requested");
            }
            GUI.enabled = true;

            GUILayout.Space(8);
            GUILayout.Label("Solo test tip:");
            GUILayout.Label("1) Host ? 2) ?????????? ? Draft ???????");
            GUILayout.Label("3) F8 ? Draft/Status ?? hash????? Ready");
        }

        private static void DrawDraft(LanMpPlugin plugin)
        {
            var net = plugin.Net;
            var lobby = plugin.Lobby;
            var log = LanMpPlugin.Log;

            GUILayout.Label("Map (SD name / Resources path / file)");
            if (_maps != null && _maps.Count > 0)
            {
                var newIdx = GUILayout.SelectionGrid(_mapIndex, _maps.ToArray(), 1);
                if (newIdx != _mapIndex)
                {
                    _mapIndex = newIdx;
                    _mapId = _maps[_mapIndex];
                }
            }
            _mapId = GUILayout.TextField(_mapId);

            GUILayout.Space(4);
            _fow = GUILayout.Toolbar(_fow, FowLabels);
            _win = GUILayout.Toolbar(_win, WinLabels);
            _quickStart = GUILayout.Toolbar(_quickStart, QsLabels);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Host slot", GUILayout.Width(70));
            var hs = GUILayout.TextField(_hostSlot.ToString(), GUILayout.Width(40));
            int.TryParse(hs, out _hostSlot);
            GUILayout.Label("Guest slot", GUILayout.Width(70));
            var gs = GUILayout.TextField(_guestSlot.ToString(), GUILayout.Width(40));
            int.TryParse(gs, out _guestSlot);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Validate map", GUILayout.Height(28)))
            {
                var probe = BuildDraftFromEditors(net);
                _lastValidate = SkirmishDraftCapture.ValidateAndHash(probe, log);
                UiFeedback.Push(_lastValidate);
            }
            GUI.enabled = net.Role == PeerRole.Host;
            if (GUILayout.Button("Publish Draft", GUILayout.Height(28)))
            {
                var draft = BuildDraftFromEditors(net);
                var summary = SkirmishDraftCapture.ValidateAndHash(draft, log);
                if (summary.StartsWith("map resolve") || summary.StartsWith("draft"))
                {
                    _lastValidate = summary;
                    UiFeedback.Push(summary);
                }
                else
                {
                    lobby.PublishLocalDraft(draft);
                    _lastValidate = summary;
                    UiFeedback.Push("Published. " + summary);
                    // Keep editors in sync with hashed draft
                    _mapId = draft.mapId;
                }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_lastValidate))
                GUILayout.Label(_lastValidate);

            GUILayout.Space(4);
            GUILayout.Label("Live draft: " + lobby.Draft.mapId);
            GUILayout.Label("hash=" + lobby.Draft.mapContentHash);
            GUILayout.Label($"fow={lobby.Draft.fowType} win={lobby.Draft.winCondition} qs={lobby.Draft.quickStart}");
            GUILayout.Label($"slots H/G={lobby.Draft.hostSlotIndex}/{lobby.Draft.guestSlotIndex}");
        }

        private static LobbyDraftDto BuildDraftFromEditors(NetSession net)
        {
            return new LobbyDraftDto
            {
                mapId = _mapId,
                mapDisplayName = _mapId,
                mapContentHash = "pending",
                fowType = _fow,
                winCondition = _win,
                quickStart = _quickStart,
                hostPeerId = net?.LocalPeerId ?? "",
                guestPeerId = net?.RemotePeerId ?? "",
                hostSlotIndex = _hostSlot,
                guestSlotIndex = _guestSlot
            };
        }

        private static void DrawStatus(LanMpPlugin plugin)
        {
            var cs = plugin.Checksum;
            if (cs.MismatchPaused)
            {
                GUILayout.Label(cs.RepairInFlight
                    ? "HASH MISMATCH ? repairing?"
                    : "HASH MISMATCH ? Strict pause");
            }
            GUILayout.Label($"hash local ={cs.LastLocalHash ?? "-"}");
            GUILayout.Label($"hash remote={cs.LastRemoteHash ?? "-"}");

            GUILayout.Space(6);
            GUILayout.Label("Config");
            GUILayout.Label($"InterceptSkirmishStart={plugin.InterceptSkirmishStart.Value}");
            GUILayout.Label($"AttachResults={plugin.AttachResultsOnCommands.Value}");
            GUILayout.Label($"RepairOnMismatch={plugin.RepairOnMismatch.Value}");
            GUILayout.Label($"StrictStateHash={plugin.StrictStateHash.Value}");

            GUILayout.Space(6);
            GUILayout.Label("Activity");
            foreach (var line in UiFeedback.Recent)
                GUILayout.Label(line);

            GUILayout.Space(6);
            GUILayout.Label("Steam often blocks same-PC dual launch.");
        }
    }
}
