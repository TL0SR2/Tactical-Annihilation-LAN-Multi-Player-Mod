using AnnW.LanMp.Protocol;
using UnityEngine;

namespace AnnW.LanMp.Ui
{
    /// <summary>Thin status strip; lobby controls live in <see cref="LanLobbyPanel"/>.</summary>
    internal static class LanHud
    {
        private static GUIStyle _labelStyle;
        private static GUIStyle _btnStyle;

        public static void Draw(LanMpPlugin plugin)
        {
            if (plugin == null || !plugin.Enabled.Value)
                return;
            if (plugin.ShowHudBanner != null && !plugin.ShowHudBanner.Value)
                return;

            EnsureStyles();

            var net = plugin.Net;
            var lobby = plugin.Lobby;
            var h = 32f;
            var area = new Rect(0, 0, Screen.width, h);

            var prev = GUI.color;
            GUI.color = SkirmishRoomPresence.IsOpen
                ? new Color(0.05f, 0.22f, 0.2f, 0.92f)
                : new Color(0.08f, 0.12f, 0.14f, 0.75f);
            GUI.DrawTexture(area, Texture2D.whiteTexture);
            GUI.color = prev;

            string roleTxt;
            if (net.Role == PeerRole.None)
                roleTxt = "idle";
            else if (net.Role == PeerRole.Host)
                roleTxt = net.IsConnected ? "HOST+guest" : "HOST listening";
            else
                roleTxt = "GUEST";

            var draft = string.IsNullOrEmpty(lobby.Draft?.mapId) ? "-" : lobby.Draft.mapId;
            var line = $"LanMp  |  {roleTxt}  |  ready {Bool(lobby.LocalReady)}/{Bool(lobby.RemoteReady)}  |  draft={draft}  |  F8 大厅";
            GUI.Label(new Rect(10, 4, Screen.width - 200, 24), line, _labelStyle);

            if (GUI.Button(new Rect(Screen.width - 130, 4, 120, 24),
                    LanLobbyNativePanel.IsOpen || LanLobbyPanel.Visible ? "关闭大厅" : "打开大厅", _btnStyle))
            {
                if (LanLobbyNativePanel.IsOpen)
                    LanLobbyNativePanel.Close();
                else
                    LanLobbyNativePanel.Open();
            }
        }

        private static string Bool(bool v) => v ? "Y" : "N";

        private static void EnsureStyles()
        {
            if (_labelStyle != null)
                return;
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            _labelStyle.normal.textColor = new Color(0.85f, 0.95f, 0.9f);
            _btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold };
        }
    }
}
