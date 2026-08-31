using ANNW;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnnW.LanMp.Ui
{
    /// <summary>Dedicated LAN room page (built from scratch; skirmish-like structure, not a clone).</summary>
    public sealed class LanRoomView : UI_Stackable
    {
        public GameObject panel;
        public CanvasGroup cgOverall;

        public TextMeshProUGUI title;
        public TextMeshProUGUI statusLine;
        public TextMeshProUGUI mapInfo;
        public TextMeshProUGUI seatsText;
        public TextMeshProUGUI rosterText;
        public TextMeshProUGUI ruleFowLabel;
        public TextMeshProUGUI ruleWinLabel;
        public TextMeshProUGUI ruleQsLabel;

        public RectTransform mapListContent;
        public Button btnReady;
        public Button btnStart;
        public Button btnLeave;
        public Button btnRuleFow;
        public Button btnRuleWin;
        public Button btnRuleQs;

        public override void Show()
        {
            base.Show();
            PlayOpenAnimation();
        }

        public void ShowPanel() => Show();

        public override void Hide() => base.Hide();

        public void HidePanel() => Hide();

        private void PlayOpenAnimation()
        {
            if (panel != null)
            {
                panel.transform.localScale = Vector3.one * 0.92f;
                panel.transform.DOKill();
                panel.transform.DOScale(1f, 0.18f);
            }

            if (cgOverall != null)
            {
                cgOverall.alpha = 0f;
                cgOverall.DOKill();
                cgOverall.DOFade(1f, 0.12f);
            }
        }
    }
}
