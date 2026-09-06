using System.Text;
using AnnW.LanMp.Protocol;
using UnityEngine;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Post-MatchEnd settlement outside the battle scene (ADR-001 Host MatchEnd payload).
    /// Network may already be down — display uses the cached payload only.
    /// Mid-defeat spectate does not open this; only Host MatchEnd does.
    /// </summary>
    internal static class MatchSettlementUi
    {
        private static bool _visible;
        private static string _title = "";
        private static string _body = "";
        private static Rect _win = new Rect(0, 0, 420, 280);

        public static bool IsVisible => _visible;

        public static void Show(
            MatchEndPayload end,
            bool localVictory,
            int? localSeatIndex = null,
            string localPeerId = null)
        {
            var row = MatchEndRules.FindLocalResult(end, localSeatIndex, localPeerId);
            var wipedButFactionWin = localVictory && row != null && row.defeated;

            _title = wipedButFactionWin
                ? "阵营胜利"
                : (localVictory ? "战斗胜利" : "战斗失败");
            _body = FormatBody(end, localVictory, wipedButFactionWin);
            _visible = true;
            _win = new Rect(
                (Screen.width - 420f) * 0.5f,
                (Screen.height - 280f) * 0.5f,
                420f,
                280f);

            UiFeedback.Push(_title + " — 对局已结束");
            TryFloater(_title, _body);
        }

        public static void Hide()
        {
            _visible = false;
        }

        public static void Draw()
        {
            if (!_visible)
                return;
            _win = GUI.ModalWindow(
                592311,
                _win,
                id =>
                {
                    GUILayout.Label(_title);
                    GUILayout.Space(6);
                    GUILayout.Label(_body);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("确定", GUILayout.Height(32)))
                        Hide();
                    GUI.DragWindow(new Rect(0, 0, 10000, 24));
                },
                "对局结算");
        }

        private static string FormatBody(MatchEndPayload end, bool localVictory, bool wipedButFactionWin)
        {
            var sb = new StringBuilder();
            if (wipedButFactionWin)
                sb.AppendLine("本席已淘汰（观战），所属阵营最终胜利");
            else
                sb.AppendLine(localVictory ? "本席：胜利" : "本席：失败");

            if (end == null)
                return sb.ToString();

            if (!string.IsNullOrEmpty(end.reason))
                sb.AppendLine("原因：" + end.reason);
            if (end.winnerFraction >= 0)
                sb.AppendLine("胜利阵营：" + end.winnerFraction);

            if (end.results != null && end.results.Length > 0)
            {
                sb.AppendLine("各席结果：");
                foreach (var r in end.results)
                {
                    if (r == null)
                        continue;
                    var mark = r.winner ? "胜" : "败";
                    if (r.defeated && r.winner)
                        mark = "淘汰·阵营胜";
                    else if (r.defeated)
                        mark = "败";
                    var who = string.IsNullOrEmpty(r.ownerPeerId) ? "" : (" @" + ShortPeer(r.ownerPeerId));
                    sb.AppendLine($"  席{r.playerIndex} 阵营{r.fraction}{who} → {mark}");
                }
            }

            sb.AppendLine();
            sb.Append("（胜负按阵营；中途淘汰可观战至终局再结算）");
            return sb.ToString();
        }

        private static string ShortPeer(string peerId)
        {
            if (string.IsNullOrEmpty(peerId) || peerId.Length <= 6)
                return peerId ?? "";
            return peerId.Substring(0, 6);
        }

        private static void TryFloater(string title, string body)
        {
            try
            {
                var pop = UI_Floater.self?.pop_general;
                if (pop == null)
                    return;
                var text = title + "\n" + body;
                if (text.Length > 400)
                    text = text.Substring(0, 400) + "…";
                pop.ShowAsSimple("[LanMp] " + text);
            }
            catch
            {
                // Menu floater may be unavailable mid-transition.
            }
        }
    }
}
