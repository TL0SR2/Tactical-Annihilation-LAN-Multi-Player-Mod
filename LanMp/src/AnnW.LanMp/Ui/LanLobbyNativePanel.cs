using System;
using System.Text;
using ANNW;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// LAN lobby built from scratch under <see cref="UI_Floater"/> using vanilla Stackable + popup animation pattern.
    /// Does <b>not</b> Instantiate Options/General prefabs — only samples font/sprite assets via <see cref="AnnwUiKit"/>.
    /// </summary>
    internal static class LanLobbyNativePanel
    {
        public const string RootName = "LanMp_LobbyRoot";

        private static LanLobbyView _view;
        private static float _refreshAt;

        public static bool IsOpen => _view != null && _view.gameObject.activeInHierarchy;

        public static void Open()
        {
            LanLocalization.EnsureRegistered();
            LanLobbyPanel.Visible = false;
            LanRoomPanel.EnsureNetHooks();

            if (!TryEnsureBuilt())
            {
                UiFeedback.Push("联机大厅构建失败（见日志）");
                return;
            }

            LanLobbyPanel.OpenedFromMainMenuEntry = true;
            RefreshStatus(force: true);
            _view.ShowPanel();
            LanMpPlugin.Log?.LogInfo("[LobbyUI] Opened from-scratch lobby");
        }

        public static void Close()
        {
            if (_view != null)
                _view.HidePanel();
        }

        public static void Tick()
        {
            if (!IsOpen)
                return;
            if (Time.unscaledTime < _refreshAt)
                return;
            _refreshAt = Time.unscaledTime + 0.35f;
            RefreshStatus(force: false);
        }

        private static bool TryEnsureBuilt()
        {
            if (_view != null)
                return true;

            var floater = UI_Floater.self;
            if (floater == null)
            {
                LanMpPlugin.Log?.LogWarning("[LobbyUI] UI_Floater.self null");
                return false;
            }

            if (!AnnwUiKit.EnsureSampled())
                return false;

            // Destroy leftovers from older clone-based builds.
            DestroyNamed(floater.transform, "LanMp_NativeLobby");
            DestroyNamed(floater.transform, RootName);

            var rootRt = AnnwUiKit.CreateRect(RootName, floater.transform);
            AnnwUiKit.StretchFull(rootRt);
            rootRt.SetAsLastSibling();

            var cg = rootRt.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = 1f;
            cg.blocksRaycasts = true;
            cg.interactable = true;

            var view = rootRt.gameObject.AddComponent<LanLobbyView>();
            view.cgOverall = cg;

            // Full-screen dim (click closes — optional; keep blocker only)
            var dimRt = AnnwUiKit.CreateRect("Dim", rootRt);
            AnnwUiKit.StretchFull(dimRt);
            AnnwUiKit.CreateImage(dimRt, AnnwUiKit.WhiteSprite, AnnwUiKit.DimColor, Image.Type.Simple);

            // Center panel — sized so fixed-height rows are not crushed by VLG.
            var panelRt = AnnwUiKit.CreateRect("Panel", rootRt);
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(640f, 480f);
            AnnwUiKit.CreateImage(panelRt, AnnwUiKit.PanelSprite, AnnwUiKit.PanelColor);
            view.panel = panelRt.gameObject;

            // Vertical body
            var body = AnnwUiKit.CreateRect("Body", panelRt);
            AnnwUiKit.StretchFull(body);
            body.offsetMin = new Vector2(28f, 28f);
            body.offsetMax = new Vector2(-28f, -28f);
            var vlg = body.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(4, 4, 4, 4);
            vlg.spacing = 10f;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            const float btnH = 48f;
            var plugin = LanMpPlugin.Instance;

            // Title
            var titleHost = AnnwUiKit.CreateRect("TitleRow", body);
            var titleLe = titleHost.gameObject.AddComponent<LayoutElement>();
            titleLe.minHeight = 44f;
            titleLe.preferredHeight = 44f;
            titleLe.flexibleHeight = 0f;
            var title = AnnwUiKit.CreateTmp(titleHost, "Title",
                LAN.Get(LanLocalization.Cate, LanLocalization.KeyLobby),
                30f, AnnwUiKit.TitleColor, TextAlignmentOptions.Center);
            title.fontStyle = FontStyles.Bold;
            title.text = LAN.Get(LanLocalization.Cate, LanLocalization.KeyLobby);
            view.title = title;

            // Status (connect-only)
            var statusHost = AnnwUiKit.CreateRect("StatusRow", body);
            var statusLe = statusHost.gameObject.AddComponent<LayoutElement>();
            statusLe.minHeight = 72f;
            statusLe.preferredHeight = 72f;
            statusLe.flexibleHeight = 0f;
            var status = AnnwUiKit.CreateTmp(statusHost, "Status", "", 17f, AnnwUiKit.BodyColor, TextAlignmentOptions.TopLeft);
            status.lineSpacing = -8f;
            view.status = status;

            // Display name
            var nameLabelHost = AnnwUiKit.CreateRect("NameLabelRow", body);
            var nlLe = nameLabelHost.gameObject.AddComponent<LayoutElement>();
            nlLe.minHeight = 26f;
            nlLe.preferredHeight = 26f;
            AnnwUiKit.CreateTmp(nameLabelHost, "NameLabel", "显示名称", 18f, AnnwUiKit.BodyColor, TextAlignmentOptions.MidlineLeft);
            var nameInput = AnnwUiKit.CreateInput(body, "NameInput", "玩家", 44f);
            if (plugin != null)
                nameInput.text = plugin.DisplayName.Value ?? "";
            nameInput.onValueChanged.AddListener(v =>
            {
                if (LanMpPlugin.Instance != null)
                    LanMpPlugin.Instance.DisplayName.Value = v ?? "";
            });

            // Join label + input
            var joinLabelHost = AnnwUiKit.CreateRect("JoinLabelRow", body);
            var jlLe = joinLabelHost.gameObject.AddComponent<LayoutElement>();
            jlLe.minHeight = 26f;
            jlLe.preferredHeight = 26f;
            jlLe.flexibleHeight = 0f;
            AnnwUiKit.CreateTmp(joinLabelHost, "JoinLabel",
                LAN.Get(LanLocalization.Cate, LanLocalization.KeyJoin) + " (IP:端口)",
                18f, AnnwUiKit.BodyColor, TextAlignmentOptions.MidlineLeft);

            var input = AnnwUiKit.CreateInput(body, "JoinInput", "127.0.0.1:24555", 44f);
            input.text = plugin != null ? plugin.JoinAddress.Value : "127.0.0.1:24555";
            input.onValueChanged.AddListener(v =>
            {
                if (LanMpPlugin.Instance != null)
                    LanMpPlugin.Instance.JoinAddress.Value = v ?? "";
            });
            view.joinInput = input;

            // Actions: create / join / close only
            var row1 = AnnwUiKit.CreateRow(body, "RowHostJoin", btnH);
            AnnwUiKit.CreateButton(row1, "BtnHost",
                LAN.Get(LanLocalization.Cate, LanLocalization.KeyHost), btnH, OnHost);
            AnnwUiKit.CreateButton(row1, "BtnJoin",
                LAN.Get(LanLocalization.Cate, LanLocalization.KeyJoin), btnH, OnJoin);

            var row2 = AnnwUiKit.CreateRow(body, "RowClose", btnH);
            AnnwUiKit.CreateButton(row2, "BtnClose",
                LAN.Get(LanLocalization.Cate, LanLocalization.KeyClose), btnH, () => Close());

            rootRt.gameObject.SetActive(false);
            _view = view;
            LanMpPlugin.Log?.LogInfo("[LobbyUI] Built from-scratch lobby under UI_Floater");
            return true;
        }

        private static void DestroyNamed(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t != null)
                UnityEngine.Object.DestroyImmediate(t.gameObject);
        }

        private static void BindLocalized(TextMeshProUGUI tmp, string key)
        {
            if (tmp == null)
                return;
            // Runtime-built TMP: do not AddComponent Localized_Txt (OnEnable NREs without private txt_ugui wired).
            tmp.text = LAN.Get(LanLocalization.Cate, key);
        }

        private static void RefreshStatus(bool force)
        {
            if (_view?.status == null)
                return;
            var plugin = LanMpPlugin.Instance;
            if (plugin == null)
                return;

            var sb = new StringBuilder();
            sb.AppendLine(RoleLine(plugin));
            sb.Append("创建或加入后将进入联机房间。");
            _view.status.text = sb.ToString();

            if (force && _view.joinInput != null)
                _view.joinInput.text = plugin.JoinAddress.Value ?? "";
        }

        private static string RoleLine(LanMpPlugin plugin)
        {
            var net = plugin.Net;
            if (net.Role == Protocol.PeerRole.None)
                return "状态: 未连接 — 请创建或加入房间";
            if (net.Role == Protocol.PeerRole.Host)
                return net.IsConnected
                    ? "状态: 主机（客机 " + net.ConnectedPeerCount + " 已连接）"
                    : "状态: 主机监听 :" + plugin.HostPort.Value;
            return net.IsConnected ? "状态: 客机已连接" : "状态: 客机未连接";
        }

        private static void ApplyDisplayName(LanMpPlugin plugin)
        {
            var name = plugin.DisplayName?.Value;
            if (string.IsNullOrWhiteSpace(name))
                name = "玩家" + (plugin.Net.LocalPeerId ?? "").Substring(0, Math.Min(4, (plugin.Net.LocalPeerId ?? "xxxx").Length));
            plugin.Net.LocalDisplayName = name.Trim();
        }

        private static void OnHost()
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null) return;
            try
            {
                ApplyDisplayName(plugin);
                plugin.Net.StartHost(plugin.HostPort.Value);
                Close();
                LanRoomPanel.Open();
            }
            catch (System.Exception ex)
            {
                UiFeedback.Push(ex.Message);
                LanMpPlugin.Log?.LogError(ex);
            }
        }

        private static void OnJoin()
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null) return;
            try
            {
                if (_view?.joinInput != null)
                    plugin.JoinAddress.Value = _view.joinInput.text;
                ApplyDisplayName(plugin);
                LanRoomPanel.EnsureNetHooks();
                UiFeedback.Push("正在加入…");
                plugin.Net.ConnectGuest(plugin.JoinAddress.Value);
                // Room opens only after Welcome (OnConnected). Reject stays on connect panel via toast.
            }
            catch (System.Exception ex)
            {
                UiFeedback.Push(ex.Message);
                LanMpPlugin.Log?.LogError(ex);
            }
        }
    }
}
