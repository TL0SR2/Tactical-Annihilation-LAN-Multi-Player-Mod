using System;
using System.Collections.Generic;
using ANNW;
using AnnW.LanMp.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Connect → Room. Dedicated page with skirmish-like semantics (not a clone of screen_skirmish).
    /// </summary>
    internal static class LanRoomPanel
    {
        public const string RootName = "LanMp_RoomRoot";
        private const int UiBuild = 15;
        private static LanDropMenu.Handle _ddFow;
        private static LanDropMenu.Handle _ddWin;
        private static LanDropMenu.Handle _ddQs;

        private static LanRoomView _view;
        private static int _builtUi;
        private static RectTransform _seatList;
        private static RectTransform _previewHost;
        private static TextMeshProUGUI _mapTitle;
        private static TextMeshProUGUI _mapDes;
        private static TextMeshProUGUI _seatHeader;
        private static Image _previewBox;
        private static GameObject _previewHint;
        private static bool _hooks;
        private static float _refreshAt;
        private static float _publishAt;
        private static bool _publishPending;
        private static List<LanRoomMapCatalog.Entry> _maps = new List<LanRoomMapCatalog.Entry>();
        private static string _selectedMapId;
        private static int _fow = 1;
        private static int _win;
        private static int _qs = 2;
        private static readonly List<Button> _mapButtons = new List<Button>();
        private static readonly List<TextMeshProUGUI> _mapLabels = new List<TextMeshProUGUI>();
        private static bool _applyingRemote;

        private static string FowLabel(int v)
        {
            try
            {
                return v == 0 ? LAN.Get("UI_FOW_Type.None") : LAN.Get("UI_FOW_Type.Standard");
            }
            catch { return v == 0 ? "无" : "标准"; }
        }

        private static string WinLabel(int v)
        {
            try
            {
                if (v == 1) return LAN.Get("UI_DestoryCO");
                if (v == 2) return LAN.Get("UI_DestoryAllUnits");
                if (v == 3) return LAN.Get("UI_None");
                return LAN.Get("UI_DestoryCOAndFactory");
            }
            catch
            {
                return v == 1 ? "击杀指挥官" : v == 2 ? "全歼" : v == 3 ? "无" : "击杀指挥与工厂";
            }
        }

        private static string QsLabel(int v)
        {
            try
            {
                if (v == 0) return LAN.Get("UI_QuickStart_None");
                if (v == 1) return LAN.Get("UI_QuickStart_CmdBot");
                return LAN.Get("UI_QuickStart_Standard");
            }
            catch
            {
                return v == 0 ? "无" : v == 1 ? "指挥机器人" : "简单基地";
            }
        }

        public static bool IsOpen => _view != null && _view.gameObject.activeInHierarchy;

        public static void Open()
        {
            LanLocalization.EnsureRegistered();
            EnsureHooks();
            if (!TryEnsureBuilt())
            {
                LanMpPlugin.Log?.LogError("[RoomUI] build failed");
                return;
            }

            LanLobbyNativePanel.Close();
            // Force rebuild if leftover empty chrome from older versions.
            if (_view.mapListContent != null && _view.mapListContent.childCount == 0)
                ReloadMaps();
            else
                ReloadMaps();

            ApplyLocalNamesToDraftIfHost();
            RefreshAll();
            _view.ShowPanel();
            LanMpPlugin.Log?.LogInfo("[RoomUI] Opened dedicated LAN room");
        }

        public static void Close()
        {
            LanDropMenu.CloseOpen();
            if (_view != null)
                _view.HidePanel();
        }

        public static void Tick()
        {
            if (_publishPending && Time.unscaledTime >= _publishAt)
            {
                _publishPending = false;
                PublishNow();
            }

            if (!IsOpen)
                return;

            MaybeRefreshGuestNameFromNet();

            if (Time.unscaledTime < _refreshAt)
                return;
            _refreshAt = Time.unscaledTime + 0.35f;
            RefreshStatusAndRoster();
            RefreshActionButtons();
        }

        private static void MaybeRefreshGuestNameFromNet()
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null || plugin.Net.Role != PeerRole.Host || !plugin.Net.IsConnected)
                return;
            var remote = plugin.Net.RemoteDisplayName;
            if (string.IsNullOrEmpty(remote))
                return;
            var draft = plugin.Lobby.Draft;
            if (draft != null && draft.guestDisplayName == remote)
                return;
            LanRoomDraftBuilder.RefreshOccupantNames(draft ?? new LobbyDraftDto(), DisplayName(plugin), remote.Trim());
            if (string.IsNullOrEmpty(draft?.mapId))
                plugin.Lobby.PublishLocalDraft(draft ?? new LobbyDraftDto());
            else
                SchedulePublish();
        }

        private static void EnsureHooks()
        {
            if (_hooks)
                return;
            var plugin = LanMpPlugin.Instance;
            if (plugin?.Net == null || plugin.Lobby == null)
                return;
            plugin.Net.OnConnected += OnNetConnected;
            plugin.Net.OnDisconnected += OnNetDisconnected;
            plugin.Lobby.OnDraftChanged += OnDraftChanged;
            plugin.Lobby.OnReadyChanged += () =>
            {
                if (!IsOpen) return;
                RefreshStatusAndRoster();
                RefreshActionButtons();
                RefreshSeatsFromDraft();
            };
            plugin.Lobby.OnCanStartChanged += () =>
            {
                if (IsOpen)
                    RefreshActionButtons();
            };
            plugin.Lobby.OnSeatEditNack += n =>
            {
                if (n == null) return;
                var msg = string.IsNullOrEmpty(n.message) ? "无法修改座位" : n.message;
                if (n.code == (int)SeatEditNackCode.ColorTaken) msg = "该颜色已被占用";
                else if (n.code == (int)SeatEditNackCode.PosTaken) msg = "该起始位置已被占用";
                UiFeedback.Push(msg);
                if (IsOpen) RefreshSeatsFromDraft();
            };
            // LobbyReject toast: only Plugin.Awake (avoid double toast).
            _hooks = true;
        }

        /// <summary>
        /// Guest never opens the room before Welcome; hooks must be armed from connect UI / plugin boot
        /// so <see cref="OnNetConnected"/> can open the room.
        /// </summary>
        public static void EnsureNetHooks()
        {
            EnsureHooks();
        }

        private static void OnNetConnected()
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null)
                return;
            // Guest seating + draft already applied in LobbySession.OnGuestAdmitted.
            if (!IsOpen && plugin.Net.Role != PeerRole.None)
                Open();
            else if (IsOpen)
                RefreshAll();
        }

        private static void OnNetDisconnected(string reason)
        {
            var plugin = LanMpPlugin.Instance;
            // Host keeps room when guest drops (listener still up).
            if (plugin?.Net.Role == PeerRole.Host)
            {
                if (IsOpen)
                    RefreshAll();
                LanMpPlugin.Log?.LogInfo("[RoomUI] guest left, host stays: " + reason);
                return;
            }
            if (!IsOpen)
                return;
            Close();
            LanLobbyNativePanel.Open();
            var reject = plugin?.Net.LastReject ?? plugin?.Lobby.LastReject;
            if (reject != null)
                UiFeedback.Push(string.IsNullOrEmpty(reject.message)
                    ? LobbySeatLogic.RejectMessage((LobbyRejectCode)reject.code)
                    : reject.message);
            LanMpPlugin.Log?.LogInfo("[RoomUI] left room: " + reason);
        }

        private static void OnDraftChanged()
        {
            if (!IsOpen)
                return;
            var plugin = LanMpPlugin.Instance;
            if (plugin?.Net.Role == PeerRole.Guest)
            {
                _applyingRemote = true;
                try { ApplyDraftToUi(plugin.Lobby.Draft); }
                finally { _applyingRemote = false; }
            }
            RefreshAll();
        }

        private static bool TryEnsureBuilt()
        {
            if (_view != null && _builtUi == UiBuild)
                return true;

            var floater = UI_Floater.self;
            if (floater == null || !AnnwUiKit.EnsureSampled())
            {
                LanMpPlugin.Log?.LogWarning("[RoomUI] Floater/UI kit unavailable");
                return false;
            }

            DestroyNamed(floater.transform, RootName);
            DestroyNamed(floater.transform, "LanMp_DropLayer");
            _view = null;
            _seatList = null;
            _previewHost = null;
            _previewHint = null;
            LanRoomMinimap.Clear();

            var rootRt = AnnwUiKit.CreateRect(RootName, floater.transform);
            AnnwUiKit.StretchFull(rootRt);
            rootRt.SetAsLastSibling();
            var cg = rootRt.gameObject.AddComponent<CanvasGroup>();
            var view = rootRt.gameObject.AddComponent<LanRoomView>();
            view.cgOverall = cg;

            // Full-bleed panel like vanilla screen_skirmish (not a centered modal).
            var dim = AnnwUiKit.CreateRect("Dim", rootRt);
            AnnwUiKit.StretchFull(dim);
            AnnwUiKit.CreateImage(dim, AnnwUiKit.WhiteSprite, new Color(0f, 0f, 0f, 0.35f), Image.Type.Simple);

            var panelRt = AnnwUiKit.CreateRect("Panel", rootRt);
            AnnwUiKit.StretchFull(panelRt);
            // Match skirmish-like margins inside the menu canvas.
            panelRt.offsetMin = new Vector2(36f, 28f);
            panelRt.offsetMax = new Vector2(-36f, -28f);
            AnnwUiKit.CreateImage(panelRt, AnnwUiKit.PanelSprite, AnnwUiKit.PanelColor);
            view.panel = panelRt.gameObject;

            const float pad = 18f;
            const float headerH = 48f;
            const float statusH = 26f;
            const float footerH = 56f;
            const float mapColFrac = 0.24f;
            const float gap = 12f;
            // Vanilla InfoSkm: minimap ~ square beside title/des + rule rows (label | dropdown @65)
            const float topBandH = 230f;
            const float previewSize = 196f;

            // Header
            var header = AnnwUiKit.CreateRect("Header", panelRt);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.sizeDelta = new Vector2(-(pad * 2f), headerH);
            header.anchoredPosition = new Vector2(0f, -pad);

            view.title = AnnwUiKit.CreateTmp(header, "Title", "联机房间", 28f, AnnwUiKit.TitleColor, TextAlignmentOptions.MidlineLeft);
            view.title.fontStyle = FontStyles.Bold;
            var titleRt = view.title.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 0f);
            titleRt.anchorMax = new Vector2(0.55f, 1f);
            titleRt.offsetMin = Vector2.zero;
            titleRt.offsetMax = Vector2.zero;

            view.btnLeave = AnnwUiKit.CreateButton(header, "BtnLeave",
                LAN.Get(LanLocalization.Cate, LanLocalization.KeyLeave), headerH - 8f, OnLeave);
            var leaveRt = view.btnLeave.transform as RectTransform;
            leaveRt.anchorMin = new Vector2(1f, 0.5f);
            leaveRt.anchorMax = new Vector2(1f, 0.5f);
            leaveRt.pivot = new Vector2(1f, 0.5f);
            leaveRt.sizeDelta = new Vector2(160f, headerH - 8f);
            leaveRt.anchoredPosition = Vector2.zero;
            StripLayout(view.btnLeave);

            // Status under title
            var statusRow = AnnwUiKit.CreateRect("Status", panelRt);
            statusRow.anchorMin = new Vector2(0f, 1f);
            statusRow.anchorMax = new Vector2(1f, 1f);
            statusRow.pivot = new Vector2(0.5f, 1f);
            statusRow.sizeDelta = new Vector2(-(pad * 2f), statusH);
            statusRow.anchoredPosition = new Vector2(0f, -(pad + headerH + 2f));
            view.statusLine = AnnwUiKit.CreateTmp(statusRow, "StatusTxt", "", 15f, AnnwUiKit.BodyColor, TextAlignmentOptions.MidlineLeft);

            // Footer — ready / start only
            var footer = AnnwUiKit.CreateRect("Footer", panelRt);
            footer.anchorMin = new Vector2(0f, 0f);
            footer.anchorMax = new Vector2(1f, 0f);
            footer.pivot = new Vector2(0.5f, 0f);
            footer.sizeDelta = new Vector2(-(pad * 2f), footerH);
            footer.anchoredPosition = new Vector2(0f, pad);

            view.btnReady = AnnwUiKit.CreateButton(footer, "BtnReady",
                LAN.Get(LanLocalization.Cate, LanLocalization.KeyReady), footerH - 8f, OnReadyToggle);
            var readyRt = view.btnReady.transform as RectTransform;
            PlaceFooterBtn(readyRt, 0f, 0.48f);
            StripLayout(view.btnReady);

            view.btnStart = AnnwUiKit.CreateButton(footer, "BtnStart",
                LAN.Get(LanLocalization.Cate, LanLocalization.KeyStart), footerH - 8f, OnStart);
            var startRt = view.btnStart.transform as RectTransform;
            PlaceFooterBtn(startRt, 0.52f, 1f);
            StripLayout(view.btnStart);

            var mainTop = pad + headerH + statusH + 8f;
            var mainBottom = pad + footerH + 8f;

            // Left: maps (narrower)
            var mapsCol = AnnwUiKit.CreateRect("Maps", panelRt);
            mapsCol.anchorMin = new Vector2(0f, 0f);
            mapsCol.anchorMax = new Vector2(mapColFrac, 1f);
            mapsCol.offsetMin = new Vector2(pad, mainBottom);
            mapsCol.offsetMax = new Vector2(-gap * 0.5f, -mainTop);
            AnnwUiKit.CreateImage(mapsCol, AnnwUiKit.PanelSprite, AnnwUiKit.InputColor);

            var mapsTitle = AnnwUiKit.CreateTmp(mapsCol, "MapsTitle", "内置地图", 17f, AnnwUiKit.TitleColor, TextAlignmentOptions.MidlineLeft);
            var mt = mapsTitle.rectTransform;
            mt.anchorMin = new Vector2(0f, 1f);
            mt.anchorMax = new Vector2(1f, 1f);
            mt.pivot = new Vector2(0.5f, 1f);
            mt.sizeDelta = new Vector2(-12f, 26f);
            mt.anchoredPosition = new Vector2(0f, -6f);

            var scrollHost = AnnwUiKit.CreateRect("Scroll", mapsCol);
            scrollHost.anchorMin = Vector2.zero;
            scrollHost.anchorMax = Vector2.one;
            scrollHost.offsetMin = new Vector2(6f, 6f);
            scrollHost.offsetMax = new Vector2(-6f, -34f);
            var scroll = scrollHost.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            var viewport = AnnwUiKit.CreateRect("Viewport", scrollHost);
            AnnwUiKit.StretchFull(viewport);
            viewport.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.01f);
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            scroll.viewport = viewport;
            var content = AnnwUiKit.CreateRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = new Vector2(0f, 0f);
            var contentV = content.gameObject.AddComponent<VerticalLayoutGroup>();
            contentV.spacing = 2f;
            contentV.padding = new RectOffset(2, 2, 2, 2);
            contentV.childControlWidth = true;
            contentV.childControlHeight = true;
            contentV.childForceExpandWidth = true;
            contentV.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = content;
            view.mapListContent = content;

            // Right: detail
            var detail = AnnwUiKit.CreateRect("Detail", panelRt);
            detail.anchorMin = new Vector2(mapColFrac, 0f);
            detail.anchorMax = new Vector2(1f, 1f);
            detail.offsetMin = new Vector2(gap * 0.5f, mainBottom);
            detail.offsetMax = new Vector2(-pad, -mainTop);
            AnnwUiKit.CreateImage(detail, AnnwUiKit.PanelSprite, AnnwUiKit.InputColor);

            // Top band mirrors vanilla InfoSkm: [minimap] [title/des] [FOW/Win/QS vertical]
            var infoRow = AnnwUiKit.CreateRect("InfoRow", detail);
            infoRow.anchorMin = new Vector2(0f, 1f);
            infoRow.anchorMax = new Vector2(1f, 1f);
            infoRow.pivot = new Vector2(0.5f, 1f);
            infoRow.sizeDelta = new Vector2(-20f, topBandH);
            infoRow.anchoredPosition = new Vector2(0f, -10f);

            _previewHost = AnnwUiKit.CreateRect("Preview", infoRow);
            _previewHost.anchorMin = new Vector2(0f, 0.5f);
            _previewHost.anchorMax = new Vector2(0f, 0.5f);
            _previewHost.pivot = new Vector2(0f, 0.5f);
            _previewHost.sizeDelta = new Vector2(previewSize, previewSize);
            _previewHost.anchoredPosition = new Vector2(10f, 0f);
            _previewBox = AnnwUiKit.CreateImage(_previewHost, AnnwUiKit.PanelSprite, new Color(0.08f, 0.05f, 0.03f, 1f));
            var previewHintTmp = AnnwUiKit.CreateTmp(_previewHost, "Hint", "小地图", 14f, new Color(1f, 1f, 1f, 0.35f), TextAlignmentOptions.Center);
            _previewHint = previewHintTmp.gameObject;
            LanRoomMinimap.Attach(_previewHost, _previewHint);

            // Meta column (title + size/theme) — left of rules, like vanilla
            var meta = AnnwUiKit.CreateRect("Meta", infoRow);
            meta.anchorMin = new Vector2(0f, 0f);
            meta.anchorMax = new Vector2(0.40f, 1f);
            meta.offsetMin = new Vector2(previewSize + 18f, 10f);
            meta.offsetMax = new Vector2(-8f, -10f);

            _mapTitle = AnnwUiKit.CreateTmp(meta, "MapTitle", "请选择地图", SkirmishUiMetrics.MapTitleFont, AnnwUiKit.TitleColor, TextAlignmentOptions.TopLeft);
            _mapTitle.fontStyle = FontStyles.Bold;
            var titleRt2 = _mapTitle.rectTransform;
            titleRt2.anchorMin = new Vector2(0f, 0.55f);
            titleRt2.anchorMax = new Vector2(1f, 1f);
            titleRt2.offsetMin = Vector2.zero;
            titleRt2.offsetMax = Vector2.zero;

            _mapDes = AnnwUiKit.CreateTmp(meta, "MapDes", "选图后显示尺寸与主题", SkirmishUiMetrics.MapDesFont, AnnwUiKit.BodyColor, TextAlignmentOptions.TopLeft);
            var desRt = _mapDes.rectTransform;
            desRt.anchorMin = new Vector2(0f, 0f);
            desRt.anchorMax = new Vector2(1f, 0.55f);
            desRt.offsetMin = Vector2.zero;
            desRt.offsetMax = Vector2.zero;
            view.mapInfo = _mapDes;

            // Rules: vertical stack of horizontal [label | dropdown] rows (vanilla InfoSkm)
            var rules = AnnwUiKit.CreateRect("Rules", infoRow);
            rules.anchorMin = new Vector2(0.40f, 0f);
            rules.anchorMax = new Vector2(1f, 1f);
            rules.offsetMin = new Vector2(8f, 10f);
            rules.offsetMax = new Vector2(-10f, -10f);
            var rulesV = rules.gameObject.AddComponent<VerticalLayoutGroup>();
            rulesV.spacing = SkirmishUiMetrics.RuleRowSpacing;
            rulesV.childAlignment = TextAnchor.UpperLeft;
            rulesV.childControlWidth = true;
            rulesV.childControlHeight = true;
            rulesV.childForceExpandWidth = true;
            rulesV.childForceExpandHeight = false;
            rulesV.padding = new RectOffset(0, 0, 0, 0);

            var dh = AnnwUiKit.RuleDropdownHeight;
            // Vanilla InfoSkm: [战争迷雾][标准 ▼] same row — label LEFT, dropdown RIGHT.
            _ddFow = CreateRuleDrop(rules, "Fow", "UI_FowSetting", "战争迷雾", dh, new[]
            {
                new LanDropMenu.Option(FowLabel(0), 0),
                new LanDropMenu.Option(FowLabel(1), 1)
            }, _fow, id =>
            {
                if (!IsHost() || _applyingRemote || LanSeatCell.SuppressCallbacks) return;
                _fow = id;
                SchedulePublish();
            });
            _ddWin = CreateRuleDrop(rules, "Win", "UI_SkirmishWinSetting", "胜利条件", dh, new[]
            {
                new LanDropMenu.Option(WinLabel(0), 0),
                new LanDropMenu.Option(WinLabel(1), 1),
                new LanDropMenu.Option(WinLabel(2), 2),
                new LanDropMenu.Option(WinLabel(3), 3)
            }, _win, id =>
            {
                if (!IsHost() || _applyingRemote || LanSeatCell.SuppressCallbacks) return;
                _win = id;
                SchedulePublish();
            });
            _ddQs = CreateRuleDrop(rules, "Qs", "UI_QuickStartSetting", "开局单位", dh, new[]
            {
                new LanDropMenu.Option(QsLabel(0), 0),
                new LanDropMenu.Option(QsLabel(1), 1),
                new LanDropMenu.Option(QsLabel(2), 2)
            }, _qs, id =>
            {
                if (!IsHost() || _applyingRemote || LanSeatCell.SuppressCallbacks) return;
                _qs = id;
                SchedulePublish();
            });
            view.btnRuleFow = _ddFow.Caption;
            view.btnRuleWin = _ddWin.Caption;
            view.btnRuleQs = _ddQs.Caption;
            view.ruleFowLabel = _ddFow.Label;
            view.ruleWinLabel = _ddWin.Label;
            view.ruleQsLabel = _ddQs.Label;

            // Seats below info band
            _seatHeader = AnnwUiKit.CreateTmp(detail, "SeatsTitle",
                "玩家设置", SkirmishUiMetrics.SeatsHeaderFont, AnnwUiKit.TitleColor, TextAlignmentOptions.MidlineLeft);
            var sh = _seatHeader.rectTransform;
            sh.anchorMin = new Vector2(0f, 1f);
            sh.anchorMax = new Vector2(1f, 1f);
            sh.pivot = new Vector2(0.5f, 1f);
            sh.sizeDelta = new Vector2(-20f, 40f);
            sh.anchoredPosition = new Vector2(0f, -(topBandH + 14f));

            var seatScroll = AnnwUiKit.CreateRect("SeatScroll", detail);
            seatScroll.anchorMin = new Vector2(0f, 0f);
            seatScroll.anchorMax = new Vector2(1f, 1f);
            seatScroll.offsetMin = new Vector2(10f, 10f);
            seatScroll.offsetMax = new Vector2(-10f, -(topBandH + 56f));
            // No Mask on seat scroll — dropdown lists live on floater layer; mask was clipping cells.
            var seatSr = seatScroll.gameObject.AddComponent<ScrollRect>();
            seatSr.horizontal = false;
            seatSr.vertical = true;
            seatSr.movementType = ScrollRect.MovementType.Clamped;
            var seatVp = AnnwUiKit.CreateRect("Viewport", seatScroll);
            AnnwUiKit.StretchFull(seatVp);
            seatVp.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.01f);
            seatVp.gameObject.AddComponent<RectMask2D>();
            seatSr.viewport = seatVp;
            _seatList = AnnwUiKit.CreateRect("SeatList", seatVp);
            _seatList.anchorMin = new Vector2(0f, 1f);
            _seatList.anchorMax = new Vector2(1f, 1f);
            _seatList.pivot = new Vector2(0.5f, 1f);
            _seatList.sizeDelta = new Vector2(0f, 0f);
            var seatV = _seatList.gameObject.AddComponent<VerticalLayoutGroup>();
            seatV.spacing = AnnwUiKit.SkirmishSeatSpacing;
            seatV.padding = new RectOffset(0, 0, 0, 8);
            seatV.childControlWidth = true;
            seatV.childControlHeight = true;
            seatV.childForceExpandWidth = true;
            seatV.childForceExpandHeight = false;
            _seatList.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            seatSr.content = _seatList;

            view.rosterText = null;
            view.seatsText = null;

            rootRt.gameObject.SetActive(false);
            _view = view;
            _builtUi = UiBuild;
            LanMpPlugin.Log?.LogInfo("[RoomUI] Built dedicated room (layout build=" + UiBuild + ")");
            return true;
        }

        private static LanDropMenu.Handle CreateRuleDrop(
            RectTransform rules,
            string name,
            string lanKey,
            string fallbackTitle,
            float dh,
            LanDropMenu.Option[] options,
            int value,
            Action<int> onChanged)
        {
            var row = AnnwUiKit.CreateRect(name + "Row", rules);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.minHeight = dh;
            le.preferredHeight = dh;
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8f;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = true;
            h.padding = new RectOffset(0, 0, 0, 0);

            var titleText = SafeUiLan(lanKey, fallbackTitle);
            var title = AnnwUiKit.CreateTmp(row, "Title", titleText, SkirmishUiMetrics.RuleLabelFont,
                AnnwUiKit.TitleColor, TextAlignmentOptions.MidlineLeft);
            title.enableAutoSizing = false;
            title.overflowMode = TextOverflowModes.Ellipsis;
            var tle = title.gameObject.AddComponent<LayoutElement>();
            tle.minWidth = SkirmishUiMetrics.RuleLabelW;
            tle.preferredWidth = SkirmishUiMetrics.RuleLabelW;
            tle.flexibleWidth = 0f;
            tle.minHeight = dh;
            tle.preferredHeight = dh;
            // CreateTmp StretchFull — pin to label column width via LE under HLG.
            var trt = title.rectTransform;
            trt.anchorMin = new Vector2(0f, 0f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            var drop = LanDropMenu.Create(row, name, 1f, dh, options, value, onChanged);
            var dle = drop.Root.GetComponent<LayoutElement>();
            if (dle != null)
            {
                dle.flexibleWidth = 1f;
                dle.minWidth = 80f;
            }
            return drop;
        }

        private static string SafeUiLan(string key, string fallback)
        {
            try
            {
                var t = LAN.Get(key);
                if (!string.IsNullOrEmpty(t) && t != key && t.IndexOf("miss:", StringComparison.OrdinalIgnoreCase) < 0)
                    return t;
            }
            catch
            {
                // ignore
            }
            return fallback;
        }

        private static void PlaceFooterBtn(RectTransform rt, float aMin, float aMax)
        {
            rt.anchorMin = new Vector2(aMin, 0f);
            rt.anchorMax = new Vector2(aMax, 1f);
            rt.offsetMin = new Vector2(4f, 2f);
            rt.offsetMax = new Vector2(-4f, -2f);
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        private static void StripLayout(Button btn)
        {
            var le = btn.GetComponent<LayoutElement>();
            if (le != null)
                UnityEngine.Object.Destroy(le);
        }

        private static void ReloadMaps()
        {
            _maps = LanRoomMapCatalog.ListBuiltin(LanMpPlugin.Log);
            RebuildMapButtons();
        }

        private static void RebuildMapButtons()
        {
            if (_view?.mapListContent == null)
                return;
            foreach (Transform child in _view.mapListContent)
                UnityEngine.Object.Destroy(child.gameObject);
            _mapButtons.Clear();
            _mapLabels.Clear();

            var host = IsHost();
            foreach (var map in _maps)
            {
                var captured = map;
                var btn = AnnwUiKit.CreateMapListButton(_view.mapListContent, "Map_" + map.Id, map.ListLabel ?? map.DisplayName, () =>
                {
                    if (!IsHost() || _applyingRemote)
                        return;
                    SelectMap(captured);
                });
                btn.interactable = host;
                _mapButtons.Add(btn);
                _mapLabels.Add(btn.GetComponentInChildren<TextMeshProUGUI>());
            }
        }

        private static void SelectMap(LanRoomMapCatalog.Entry map)
        {
            _selectedMapId = map.Id;
            HighlightSelectedMap();

            // Commit draft BEFORE binding interactive seats — edits apply to Lobby.Draft, not a detached preview.
            if (!LanRoomDraftBuilder.TryBuildFromBuiltin(
                    map, _fow, _win, _qs,
                    DisplayName(LanMpPlugin.Instance),
                    GuestDisplayName(LanMpPlugin.Instance),
                    LanMpPlugin.Instance?.Net, LanMpPlugin.Log,
                    out var draft, out var err))
            {
                if (_view?.statusLine != null)
                    _view.statusLine.text = "状态：" + err;
                LanMpPlugin.Log?.LogWarning("[RoomUI] " + err);
                return;
            }

            var plugin = LanMpPlugin.Instance;
            if (plugin != null && IsHost())
                plugin.Lobby.PublishLocalDraft(draft);

            DynOb ob = null;
            if (LanRoomMapCatalog.TryLoadText(map, out var text, out _))
            {
                try
                {
                    ob = Singleton<BattleAndMapFileSystem>.self.ReadFileWithMeta_Asset(text);
                    LanRoomMapCatalog.FillDetail(map, ob);
                }
                catch { /* ignore */ }
            }
            RefreshMapMeta(map);
            LanRoomMinimap.Render(ob);
            RefreshSeatsFromDraft();
            RefreshRuleLabels();
            RefreshStatusAndRoster();
            RefreshActionButtons();
        }

        private static void SchedulePublish()
        {
            if (!IsHost() || _applyingRemote)
                return;
            _publishPending = true;
            _publishAt = Time.unscaledTime + 0.15f;
        }

        private static void PublishNow()
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null || !IsHost())
                return;

            var entry = FindMap(_selectedMapId);
            if (entry == null && !string.IsNullOrEmpty(plugin.Lobby.Draft?.mapDisplayName))
                entry = _maps.Find(m => m.DisplayName == plugin.Lobby.Draft.mapDisplayName
                                        || m.Id == plugin.Lobby.Draft.mapId);

            // Keep Host seat edits: if same map already drafted, only refresh rules/names.
            var cur = plugin.Lobby.Draft;
            if (cur != null && cur.seats != null && cur.seats.Length > 0
                && entry != null
                && (!string.IsNullOrEmpty(cur.mapId) || !string.IsNullOrEmpty(cur.mapDisplayName))
                && (cur.mapDisplayName == entry.DisplayName
                    || cur.mapId == entry.Id
                    || cur.mapId == entry.ResourcesPath
                    || (cur.mapId != null && cur.mapId.EndsWith("/" + entry.Id))))
            {
                cur.fowType = _fow;
                cur.winCondition = _win;
                cur.quickStart = _qs;
                LanRoomDraftBuilder.RefreshOccupantNames(cur, DisplayName(plugin), GuestDisplayName(plugin));
                if (string.IsNullOrEmpty(cur.mapContentHash)
                    && LanRoomMapCatalog.TryLoadText(entry, out var t, out _))
                    cur.mapContentHash = LanRoomMapCatalog.HashOf(t);
                plugin.Lobby.PublishLocalDraft(cur);
                RefreshAll();
                return;
            }

            if (entry == null)
            {
                var empty = new LobbyDraftDto
                {
                    hostDisplayName = DisplayName(plugin),
                    guestDisplayName = GuestDisplayName(plugin),
                    hostPeerId = plugin.Net.LocalPeerId,
                    guestPeerId = plugin.Net.RemotePeerId ?? ""
                };
                plugin.Lobby.PublishLocalDraft(empty);
                RefreshAll();
                return;
            }

            if (!LanRoomDraftBuilder.TryBuildFromBuiltin(
                    entry, _fow, _win, _qs,
                    DisplayName(plugin), GuestDisplayName(plugin),
                    plugin.Net, LanMpPlugin.Log,
                    out var draft, out var err))
            {
                if (_view?.statusLine != null)
                    _view.statusLine.text = "状态：" + err;
                LanMpPlugin.Log?.LogWarning("[RoomUI] " + err);
                return;
            }

            plugin.Lobby.PublishLocalDraft(draft);
            RefreshAll();
        }

        private static void ApplyDraftToUi(LobbyDraftDto draft)
        {
            if (draft == null)
                return;
            _fow = draft.fowType;
            _win = draft.winCondition;
            _qs = draft.quickStart;
            if (!string.IsNullOrEmpty(draft.mapDisplayName) || !string.IsNullOrEmpty(draft.mapId))
            {
                var match = _maps.Find(m =>
                    m.DisplayName == draft.mapDisplayName ||
                    m.Id == draft.mapDisplayName ||
                    m.Id == draft.mapId ||
                    (draft.mapId != null && (draft.mapId.EndsWith("/" + m.Id) || draft.mapId == m.ResourcesPath)));
                _selectedMapId = match != null ? match.Id : draft.mapDisplayName;
            }
            else
                _selectedMapId = null;

            HighlightSelectedMap();
            SetRuleButtonsInteractable(false);
            foreach (var b in _mapButtons)
                b.interactable = false;
        }

        private static void ApplyLocalNamesToDraftIfHost()
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null || plugin.Net.Role != PeerRole.Host)
                return;
            var d = plugin.Lobby.Draft ?? new LobbyDraftDto();
            LanRoomDraftBuilder.RefreshOccupantNames(d, DisplayName(plugin), GuestDisplayName(plugin));
            if (string.IsNullOrEmpty(d.mapId))
                plugin.Lobby.PublishLocalDraft(d);
        }

        private static void RefreshAll()
        {
            RefreshMapInfo();
            RefreshRuleLabels();
            RefreshSeatsFromDraft();
            RefreshStatusAndRoster();
            RefreshActionButtons();
            HighlightSelectedMap();
            var host = IsHost();
            SetRuleButtonsInteractable(host);
            foreach (var b in _mapButtons)
                b.interactable = host;
        }

        private static void RefreshMapInfo()
        {
            var draft = LanMpPlugin.Instance?.Lobby?.Draft;
            LanRoomMapCatalog.Entry entry = null;
            if (!string.IsNullOrEmpty(_selectedMapId))
                entry = FindMap(_selectedMapId);
            else if (draft != null && !string.IsNullOrEmpty(draft.mapDisplayName))
                entry = _maps.Find(m => m.DisplayName == draft.mapDisplayName || m.Id == draft.mapId);

            if (entry != null)
            {
                RefreshMapMeta(entry);
                // Re-render minimap when refreshing from draft (guest / reopen)
                if (LanRoomMapCatalog.TryLoadText(entry, out var text, out _))
                {
                    try
                    {
                        var ob = Singleton<BattleAndMapFileSystem>.self.ReadFileWithMeta_Asset(text);
                        LanRoomMapCatalog.FillDetail(entry, ob);
                        RefreshMapMeta(entry);
                        LanRoomMinimap.Render(ob);
                    }
                    catch
                    {
                        LanRoomMinimap.Clear();
                    }
                }
            }
            else
            {
                if (_mapTitle != null)
                    _mapTitle.text = IsHost() ? "请选择地图" : "等待房主选择地图";
                if (_mapDes != null)
                    _mapDes.text = "选图后显示尺寸与主题；配置自动同步";
                LanRoomMinimap.Clear();
            }
        }

        private static void RefreshMapMeta(LanRoomMapCatalog.Entry entry)
        {
            if (entry == null)
                return;
            if (_mapTitle != null)
                _mapTitle.text = entry.DisplayName;
            if (_mapDes != null)
            {
                var size = string.IsNullOrEmpty(entry.SizeText) ? "?" : entry.SizeText;
                var theme = string.IsNullOrEmpty(entry.ThemeText) ? "?" : entry.ThemeText;
                try
                {
                    _mapDes.text = string.Format(LAN.Get("SizeThemeFormat"), size, theme);
                }
                catch
                {
                    _mapDes.text = "尺寸:" + size + "  主题: " + theme;
                }
            }
            if (_previewBox != null)
            {
                // Placeholder until MiniMapGen is wired; tint marks "has selection".
                _previewBox.color = new Color(0.12f, 0.08f, 0.04f, 1f);
            }
        }

        private static void RefreshRuleLabels()
        {
            if (_ddFow?.Label != null)
            {
                _ddFow.Value = _fow;
                _ddFow.Label.text = FowLabel(_fow);
            }
            if (_ddWin?.Label != null)
            {
                _ddWin.Value = _win;
                _ddWin.Label.text = WinLabel(_win);
            }
            if (_ddQs?.Label != null)
            {
                _ddQs.Value = _qs;
                _ddQs.Label.text = QsLabel(_qs);
            }
        }

        private static void SetBtnLabel(TextMeshProUGUI tmp, string text)
        {
            if (tmp != null)
                tmp.text = text;
        }

        private static void RefreshSeatsFromDraft()
        {
            var draft = LanMpPlugin.Instance?.Lobby?.Draft;
            if (draft?.seats != null && draft.seats.Length > 0)
            {
                RefreshSeatsVisual(draft);
                return;
            }

            // Placeholder before map pick.
            var plugin = LanMpPlugin.Instance;
            var hostName = plugin != null ? DisplayName(plugin) : "房主";
            var peer = plugin?.Net.LocalPeerId ?? "";
            var placeholder = new LobbyDraftDto
            {
                hostPeerId = peer,
                hostSlotIndex = 0,
                guestSlotIndex = -1,
                seats = new[]
                {
                    LobbySeatLogic.MakeHostSeat(peer, hostName, 0, 0, 0, ""),
                    LobbySeatLogic.MakeAiSeat(1, 1, 1, "", LobbySeatLogic.DefaultAiController)
                }
            };
            RefreshSeatsVisual(placeholder);
        }

        private static void RefreshSeatsVisual(LobbyDraftDto draft)
        {
            if (_seatList == null)
                return;
            var plugin = LanMpPlugin.Instance;
            var peer = plugin?.Net.LocalPeerId ?? "";
            var lobby = plugin?.Lobby;
            Func<string, bool> ready = pid =>
            {
                if (lobby == null || string.IsNullOrEmpty(pid))
                    return false;
                return lobby.IsPeerReady(pid);
            };
            LanRoomSeats.Rebuild(_seatList, draft, peer, IsHost(), ready, OnSeatUiChanged);
        }

        private static void OnSeatUiChanged()
        {
            if (!IsOpen)
                return;
            RefreshSeatsFromDraft();
            RefreshStatusAndRoster();
            RefreshActionButtons();
        }

        private static void RefreshStatusAndRoster()
        {
            if (_view == null)
                return;
            var plugin = LanMpPlugin.Instance;
            if (plugin == null)
                return;
            if (_view.statusLine != null)
                _view.statusLine.text = BuildStatus(plugin);
        }

        private static string BuildStatus(LanMpPlugin plugin)
        {
            var net = plugin.Net;
            var lobby = plugin.Lobby;
            var draft = lobby.Draft;
            var joinable = LobbySeatLogic.CountJoinable(draft);
            var seated = LobbySeatLogic.CountSeatedHumans(draft);

            if (net.Role == PeerRole.Host && string.IsNullOrEmpty(draft?.mapId))
                return "状态：请选择地图；选图后其余槽默认为 AI，将某一槽切为「人类位·暂 AI」后客机才能加入";
            if (net.Role == PeerRole.Host && !net.IsConnected)
            {
                if (joinable <= 0)
                    return "状态：可加入空位 0 — 将 AI 槽切为「人类位·暂 AI」以开放加入";
                return "状态：监听中 · 可加入空位 " + joinable + " · 人类 " + seated + " — 等待玩家加入";
            }
            if (net.Role == PeerRole.Host && net.IsConnected)
            {
                return "状态：已连接客机 " + net.ConnectedPeerCount + " · 人类 " + seated +
                       " · 可加入 " + joinable + (lobby.CanStart ? " — 可以开战" : " — 等待准备");
            }
            if (string.IsNullOrEmpty(draft?.mapId))
                return net.Role == PeerRole.Host ? "状态：请选择地图" : "状态：等待房主选择地图";
            if (net.Role == PeerRole.Guest && !net.IsConnected)
                return "状态：正在加入…";
            if (lobby.CanStart)
                return net.Role == PeerRole.Host
                    ? "状态：已入座人类均已准备 — 可以开战"
                    : "状态：已准备，等待房主开战";
            if (lobby.LocalReady)
                return "状态：你已准备，等待其他人类玩家";
            return "状态：人类 " + seated + " · 可加入 " + joinable + " — 确认阵容后请准备";
        }

        private static void RefreshActionButtons()
        {
            if (_view == null)
                return;
            var plugin = LanMpPlugin.Instance;
            if (plugin == null)
                return;
            var lobby = plugin.Lobby;
            var net = plugin.Net;
            var seated = LobbySeatLogic.FindSeatIndexByPeer(lobby.Draft, net.LocalPeerId) >= 0
                         || (net.Role == PeerRole.Host && !string.IsNullOrEmpty(lobby.Draft?.mapId));
            var canReady = net.Role != PeerRole.None
                           && !string.IsNullOrEmpty(lobby.Draft?.mapId)
                           && seated
                           && (net.Role == PeerRole.Host || net.IsConnected);
            if (_view.btnReady != null)
            {
                _view.btnReady.interactable = canReady;
                var label = _view.btnReady.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null)
                    label.text = lobby.LocalReady ? "取消准备" : LAN.Get(LanLocalization.Cate, LanLocalization.KeyReady);
            }

            if (_view.btnStart != null)
                _view.btnStart.interactable = net.Role == PeerRole.Host && lobby.CanStart;
        }

        private static void HighlightSelectedMap()
        {
            for (var i = 0; i < _maps.Count && i < _mapButtons.Count; i++)
            {
                var img = _mapButtons[i].targetGraphic as Image;
                if (img == null)
                    continue;
                var selected = _maps[i].Id == _selectedMapId;
                img.color = selected
                    ? new Color(0.85f, 0.48f, 0.16f, 1f)
                    : AnnwUiKit.ButtonColor;
            }
        }

        private static void SetRuleButtonsInteractable(bool on)
        {
            if (_ddFow != null) _ddFow.Interactable = on;
            if (_ddWin != null) _ddWin.Interactable = on;
            if (_ddQs != null) _ddQs.Interactable = on;
        }

        private static void OnReadyToggle()
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null) return;
            plugin.Lobby.SetLocalReady(!plugin.Lobby.LocalReady);
            RefreshActionButtons();
            RefreshStatusAndRoster();
            RefreshSeatsFromDraft();
        }

        private static void OnStart() => LanMpPlugin.Instance?.Authority.TryHostStartBattle();

        private static void OnLeave()
        {
            LanDropMenu.CloseOpen();
            var plugin = LanMpPlugin.Instance;
            if (plugin?.Authority != null && plugin.Authority.InLanBattle && !plugin.Authority.MatchSettled)
            {
                plugin.Authority.NotifyLeavingBattle("leave-room");
                if (plugin.Net.Role == PeerRole.Host && plugin.Net.IsConnected)
                {
                    try { plugin.Net.Disconnect("leave-room"); }
                    catch { /* ignore */ }
                }
                // Guest NotifyLeavingBattle already Disconnects.
                return;
            }

            plugin?.Net.Disconnect("leave-room");
            Close();
            LanLobbyNativePanel.Open();
        }

        private static bool IsHost() => LanMpPlugin.Instance?.Net.Role == PeerRole.Host;

        private static string DisplayName(LanMpPlugin plugin)
        {
            if (plugin == null) return "玩家";
            var n = plugin.DisplayName?.Value;
            if (!string.IsNullOrWhiteSpace(n))
                return n.Trim();
            if (!string.IsNullOrEmpty(plugin.Net.LocalDisplayName))
                return plugin.Net.LocalDisplayName;
            return "玩家" + ShortId(plugin.Net.LocalPeerId);
        }

        private static string GuestDisplayName(LanMpPlugin plugin)
        {
            if (plugin == null || !plugin.Net.IsConnected)
                return "";
            if (!string.IsNullOrEmpty(plugin.Net.RemoteDisplayName))
                return plugin.Net.RemoteDisplayName.Trim();
            if (!string.IsNullOrEmpty(plugin.Lobby.Draft?.guestDisplayName))
                return plugin.Lobby.Draft.guestDisplayName;
            return ShortId(plugin.Net.RemotePeerId);
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "—";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        private static LanRoomMapCatalog.Entry FindMap(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return _maps.Find(m => m.Id == id || m.DisplayName == id || m.ResourcesPath == id);
        }

        private static void DestroyNamed(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t != null)
                UnityEngine.Object.DestroyImmediate(t.gameObject);
        }
    }
}
