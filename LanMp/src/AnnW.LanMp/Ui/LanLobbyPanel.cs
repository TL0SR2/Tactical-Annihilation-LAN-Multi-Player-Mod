using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using AnnW.LanMp.Authority;
using AnnW.LanMp.Protocol;
using UnityEngine;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Dedicated LAN lobby window (not a debug strip).
    /// Auto-opens in skirmish room; F8 toggles.
    /// </summary>
    internal static class LanLobbyPanel
    {
        public static bool Visible { get; set; }
        /// <summary>True when opened via main-menu 多人联机大厅 (not solo skirmish).</summary>
        public static bool OpenedFromMainMenuEntry { get; set; }

        private static Rect _rect = new Rect(80, 80, 560, 620);
        private static Vector2 _mapScroll;
        private static Vector2 _logScroll;
        private static string _mapId = "";
        private static List<string> _maps;
        private static int _mapIndex;
        private static string _lastValidate = "";
        private static int _hostSlot;
        private static int _guestSlot = 1;
        private static int _fow = 1;
        private static int _win;
        private static int _quickStart = 2;
        private static string _syncedDraftKey;
        private static string _cachedLanIp;
        private static float _lanIpAt;

        private static readonly string[] FowLabels = { "迷雾:关", "迷雾:标准" };
        private static readonly string[] WinLabels = { "胜:CO+厂", "胜:CO", "胜:全灭" };
        private static readonly string[] QsLabels = { "开局:无", "开局:CmdBot", "开局:标准" };

        private static GUIStyle _titleStyle;
        private static GUIStyle _sectionStyle;
        private static GUIStyle _hintStyle;

        public static void Open()
        {
            Visible = true;
            PrefetchLanIp();
            LanMpPlugin.Log?.LogInfo("[LobbyUI] Open Visible=true");
        }

        public static void OpenFromMainMenu()
        {
            OpenedFromMainMenuEntry = true;
            Visible = true;
            PrefetchLanIp();
            // Center on next draw
            _rect = new Rect(0, 0, 560, 620);
            LanMpPlugin.Log?.LogInfo("[LobbyUI] OpenFromMainMenu Visible=true");
        }

        public static void Close()
        {
            Visible = false;
            LanMpPlugin.Log?.LogInfo("[LobbyUI] Close");
        }

        public static void Toggle()
        {
            if (Visible)
                Close();
            else
                Open();
        }

        public static void Draw(LanMpPlugin plugin)
        {
            if (!Visible || plugin == null)
                return;

            try
            {
                GUI.depth = -1000;
                EnsureStyles();

                // Full-screen dim so the lobby is obviously a real panel, not a toast.
                var prev = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.72f);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = prev;

                var w = Mathf.Min(580f, Mathf.Max(320f, Screen.width - 24f));
                var h = Mathf.Min(640f, Mathf.Max(280f, Screen.height - 24f));
                if (_rect.width < 100f || _rect.height < 100f
                    || _rect.xMax < 0 || _rect.yMax < 0
                    || _rect.x > Screen.width || _rect.y > Screen.height)
                {
                    _rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
                }
                else
                {
                    _rect.width = w;
                    _rect.height = h;
                }

                _rect = GUI.Window(592311, _rect, id =>
                {
                    try
                    {
                        DrawWindow(id, plugin);
                    }
                    catch (System.Exception ex)
                    {
                        GUILayout.Label("大厅绘制错误: " + ex.Message);
                        LanMpPlugin.Log?.LogError("[LobbyUI] DrawWindow: " + ex);
                    }
                }, SafeLobbyTitle());

                // Click dim outside? keep window modal by eating mouse on backdrop — Window already on top.
                DrawToast();
            }
            catch (System.Exception ex)
            {
                LanMpPlugin.Log?.LogError("[LobbyUI] Draw: " + ex);
            }
        }

        private static string SafeLobbyTitle()
        {
            try
            {
                return LAN.Get(LanLocalization.Cate, LanLocalization.KeyLobby);
            }
            catch
            {
                return "LAN Lobby";
            }
        }

        private static void PrefetchLanIp()
        {
            try
            {
                _ = GetLanIpHint();
            }
            catch
            {
                _cachedLanIp = "127.0.0.1";
            }
        }

        private static void DrawToast()
        {
            var toast = UiFeedback.ActiveToast;
            if (string.IsNullOrEmpty(toast))
                return;
            GUI.Box(new Rect(20, Screen.height - 70, Mathf.Min(700, Screen.width - 40), 48), toast);
        }

        private static Vector2 _bodyScroll;

        private static void DrawWindow(int id, LanMpPlugin plugin)
        {
            // Lazy map load inside window so chrome still appears if SD fails.
            try { EnsureMaps(LanMpPlugin.Log); } catch (System.Exception ex) { LanMpPlugin.Log?.LogWarning("[LobbyUI] maps: " + ex.Message); }
            try { PullEditorsFromLiveDraft(plugin.Lobby); } catch { /* ignore */ }

            string title;
            try { title = LAN.Get(LanLocalization.Cate, LanLocalization.KeyLobby); }
            catch { title = "LAN Lobby"; }

            GUILayout.Label(title + "  v" + LanMpPlugin.PluginVersion, _titleStyle);
            GUILayout.Label("主菜单联机大厅", _hintStyle);
            GUILayout.Label(StatusHeadline(plugin), _hintStyle);

            _bodyScroll = GUILayout.BeginScrollView(_bodyScroll);
            DrawConnectionSection(plugin);
            GUILayout.Space(8);
            DrawPlayersSection(plugin);
            GUILayout.Space(8);
            DrawDraftSection(plugin);
            GUILayout.Space(8);
            DrawActionsSection(plugin);
            GUILayout.Space(8);
            DrawLogSection();
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("关闭大厅", GUILayout.Height(28)))
                Close();
            GUILayout.Label("F8 开关", _hintStyle);
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        private static string StatusHeadline(LanMpPlugin plugin)
        {
            var net = plugin.Net;
            if (net.Role == PeerRole.None)
                return "状态: 未连接 — 请先「创建房间」或「加入房间」";
            if (net.Role == PeerRole.Host)
                return net.IsConnected
                    ? "状态: 主机 · 客机已连接 · 可 Ready / 开战"
                    : "状态: 主机监听中 · 等待客机加入 " + DescribeHostEndpoint(plugin);
            return net.IsConnected
                ? "状态: 客机 · 已连接到 " + plugin.JoinAddress.Value
                : "状态: 客机 · 连接中/断开";
        }

        private static void DrawConnectionSection(LanMpPlugin plugin)
        {
            var net = plugin.Net;
            GUILayout.Label("【连接】", _sectionStyle);

            GUILayout.Label("本机局域网地址（给对面填 Join）: " + GetLanIpHint());
            GUILayout.BeginHorizontal();
            GUILayout.Label("主机端口", GUILayout.Width(70));
            var portStr = GUILayout.TextField(plugin.HostPort.Value.ToString(), GUILayout.Width(80));
            if (int.TryParse(portStr, out var port) && port > 0 && port < 65536)
                plugin.HostPort.Value = port;
            GUILayout.Label("  → 创建后监听 " + DescribeHostEndpoint(plugin), _hintStyle);
            GUILayout.EndHorizontal();

            GUILayout.Label("加入目标 (主机IP:端口) — Join 会连到这里，不是自动匹配");
            plugin.JoinAddress.Value = GUILayout.TextField(plugin.JoinAddress.Value);

            GUILayout.BeginHorizontal();
            GUI.enabled = net.Role == PeerRole.None;
            if (GUILayout.Button("创建房间 (Host)", GUILayout.Height(32)))
            {
                try
                {
                    net.StartHost(plugin.HostPort.Value);
                    UiFeedback.Push("已创建房间，监听 " + DescribeHostEndpoint(plugin));
                    SkirmishRoomPresence.RefreshConfirmLabel();
                }
                catch (System.Exception ex)
                {
                    UiFeedback.Push("创建失败: " + ex.Message);
                    LanMpPlugin.Log?.LogError(ex);
                }
            }
            GUI.enabled = net.Role == PeerRole.None;
            if (GUILayout.Button("加入房间 (Join)", GUILayout.Height(32)))
            {
                try
                {
                    net.ConnectGuest(plugin.JoinAddress.Value);
                    UiFeedback.Push("正在加入 " + plugin.JoinAddress.Value);
                    SkirmishRoomPresence.RefreshConfirmLabel();
                }
                catch (System.Exception ex)
                {
                    UiFeedback.Push("加入失败: " + ex.Message);
                    LanMpPlugin.Log?.LogError(ex);
                }
            }
            GUI.enabled = net.Role != PeerRole.None;
            if (GUILayout.Button("离开房间", GUILayout.Height(32)))
            {
                net.Disconnect("lobby");
                UiFeedback.Push("已离开房间");
                SkirmishRoomPresence.RefreshConfirmLabel();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label($"角色={net.Role}  已连接={net.IsConnected}  本端={net.LocalPeerId}  对端={net.RemotePeerId ?? "无"}");
        }

        private static void DrawPlayersSection(LanMpPlugin plugin)
        {
            var lobby = plugin.Lobby;
            GUILayout.Label("【玩家就绪】", _sectionStyle);
            GUILayout.Label($"本端 Ready: {BoolCn(lobby.LocalReady)}    对端 Ready: {BoolCn(lobby.RemoteReady)}    可开战: {BoolCn(lobby.CanStart)}");

            GUILayout.BeginHorizontal();
            GUI.enabled = plugin.Net.Role != PeerRole.None && plugin.Net.IsConnected;
            if (GUILayout.Button("准备 Ready", GUILayout.Height(28)))
            {
                lobby.SetLocalReady(true);
                UiFeedback.Push("Ready = 是");
            }
            if (GUILayout.Button("取消准备", GUILayout.Height(28)))
            {
                lobby.SetLocalReady(false);
                UiFeedback.Push("Ready = 否");
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (plugin.Net.Role == PeerRole.Host && !plugin.Net.IsConnected)
                GUILayout.Label("提示: 客机连上之后才能双方 Ready。", _hintStyle);
        }

        private static void DrawDraftSection(LanMpPlugin plugin)
        {
            var net = plugin.Net;
            var lobby = plugin.Lobby;
            var log = LanMpPlugin.Log;

            GUILayout.Label("【对局配置 Draft】", _sectionStyle);
            GUILayout.Label("也可在原版遭遇战选图后点确认 → 自动写入 Draft（Host 时）", _hintStyle);

            GUILayout.Label("地图");
            if (_maps != null && _maps.Count > 0)
            {
                _mapScroll = GUILayout.BeginScrollView(_mapScroll, GUILayout.Height(90));
                var newIdx = GUILayout.SelectionGrid(_mapIndex, _maps.ToArray(), 1);
                if (newIdx != _mapIndex)
                {
                    _mapIndex = newIdx;
                    _mapId = _maps[_mapIndex];
                }
                GUILayout.EndScrollView();
            }
            _mapId = GUILayout.TextField(_mapId);

            _fow = GUILayout.Toolbar(_fow, FowLabels);
            _win = GUILayout.Toolbar(_win, WinLabels);
            _quickStart = GUILayout.Toolbar(_quickStart, QsLabels);

            GUILayout.BeginHorizontal();
            GUILayout.Label("主机槽", GUILayout.Width(50));
            int.TryParse(GUILayout.TextField(_hostSlot.ToString(), GUILayout.Width(36)), out _hostSlot);
            GUILayout.Label("客机槽", GUILayout.Width(50));
            int.TryParse(GUILayout.TextField(_guestSlot.ToString(), GUILayout.Width(36)), out _guestSlot);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("校验地图", GUILayout.Height(26)))
            {
                _lastValidate = SkirmishDraftCapture.ValidateAndHash(BuildDraftFromEditors(net), log);
                UiFeedback.Push(_lastValidate);
            }
            GUI.enabled = net.Role == PeerRole.Host;
            if (GUILayout.Button("发布 Draft", GUILayout.Height(26)))
            {
                var draft = BuildDraftFromEditors(net);
                var summary = SkirmishDraftCapture.ValidateAndHash(draft, log);
                _lastValidate = summary;
                if (summary.StartsWith("map resolve") || summary.StartsWith("draft"))
                    UiFeedback.Push(summary);
                else
                {
                    lobby.PublishLocalDraft(draft);
                    _mapId = draft.mapId;
                    UiFeedback.Push("Draft 已发布. " + summary);
                }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_lastValidate))
                GUILayout.Label(_lastValidate, _hintStyle);

            GUILayout.Label($"当前 Draft: {lobby.Draft.mapId}  hash={lobby.Draft.mapContentHash}");
            GUILayout.Label($"fow={lobby.Draft.fowType} win={lobby.Draft.winCondition} qs={lobby.Draft.quickStart} 槽 H/G={lobby.Draft.hostSlotIndex}/{lobby.Draft.guestSlotIndex}");
        }

        private static void DrawActionsSection(LanMpPlugin plugin)
        {
            var lobby = plugin.Lobby;
            var auth = plugin.Authority;
            GUILayout.Label("【开战】", _sectionStyle);

            GUI.enabled = plugin.Net.Role == PeerRole.Host && lobby.CanStart;
            if (GUILayout.Button("开始战斗 (双方进战)", GUILayout.Height(34)))
            {
                auth.TryHostStartBattle();
                UiFeedback.Push("已请求开战");
            }
            GUI.enabled = true;

            if (auth.InLanBattle)
                GUILayout.Label("当前已在联机战斗中  gates=" + auth.GatesArmed, _hintStyle);
            else if (plugin.Net.Role == PeerRole.Host && !lobby.CanStart)
                GUILayout.Label("开战条件: 客机已连接 + 双方 Ready + Draft 有有效 hash", _hintStyle);

            var cs = plugin.Checksum;
            if (cs.MismatchPaused)
                GUILayout.Label(cs.RepairInFlight ? "状态 Hash 不一致 — 纠偏中…" : "状态 Hash 不一致 — 已暂停", _hintStyle);
        }

        private static void DrawLogSection()
        {
            GUILayout.Label("【最近消息】", _sectionStyle);
            _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.Height(70));
            foreach (var line in UiFeedback.Recent)
                GUILayout.Label(line);
            GUILayout.EndScrollView();
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

        private static string DescribeHostEndpoint(LanMpPlugin plugin)
        {
            return GetLanIpHint() + ":" + plugin.HostPort.Value;
        }

        private static string GetLanIpHint()
        {
            if (!string.IsNullOrEmpty(_cachedLanIp) && Time.unscaledTime - _lanIpAt < 30f)
                return _cachedLanIp;
            _lanIpAt = Time.unscaledTime;
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    var end = socket.LocalEndPoint as IPEndPoint;
                    _cachedLanIp = end != null ? end.Address.ToString() : "127.0.0.1";
                }
            }
            catch
            {
                _cachedLanIp = "127.0.0.1";
            }
            return _cachedLanIp;
        }

        private static string BoolCn(bool v) => v ? "是" : "否";

        private static void EnsureStyles()
        {
            if (_titleStyle != null)
                return;
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold
            };
            _sectionStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold
            };
            _sectionStyle.normal.textColor = new Color(0.7f, 0.9f, 0.85f);
            _hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                wordWrap = true
            };
            _hintStyle.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
        }
    }
}
