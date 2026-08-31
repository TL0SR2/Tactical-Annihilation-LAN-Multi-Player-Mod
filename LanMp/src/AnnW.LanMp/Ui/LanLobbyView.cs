using ANNW;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Vanilla-pattern popup host: <see cref="UI_Stackable"/> under Floater + CanvasGroup fade / panel scale
    /// (same contract as <see cref="UI_PopupPanel"/>), built at runtime with no prefab clone.
    /// </summary>
    public sealed class LanLobbyView : UI_Stackable
    {
        public GameObject panel;
        public CanvasGroup cgOverall;

        public TextMeshProUGUI title;
        public TextMeshProUGUI status;
        public TMP_InputField joinInput;

        public override void Show()
        {
            base.Show();
            PlayOpenAnimation();
        }

        public void ShowPanel()
        {
            Show();
        }

        public override void Hide()
        {
            base.Hide();
        }

        public void HidePanel()
        {
            Hide();
        }

        private void PlayOpenAnimation()
        {
            if (panel != null)
            {
                panel.transform.localScale = Vector3.one * 0.5f;
                panel.transform.DOKill();
                panel.transform.DOScale(1f, 0.2f);
            }

            if (cgOverall != null)
            {
                cgOverall.alpha = 0f;
                cgOverall.DOKill();
                cgOverall.DOFade(1f, 0.1f);
            }
        }
    }
}
