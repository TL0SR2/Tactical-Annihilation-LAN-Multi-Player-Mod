using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Seat cell widgets built to match skirmish structure (caption + ▼ + floating list),
    /// not by Instantiating vanilla dropdown prefabs.
    /// </summary>
    internal static class LanSeatCell
    {
        /// <summary>Ignore dropdown callbacks while seats are being rebuilt.</summary>
        public static bool SuppressCallbacks;

        public static void AddDropdown(
            RectTransform row,
            string name,
            float flex,
            IList<LanDropMenu.Option> options,
            int selectedId,
            Action<int> onIdChanged,
            bool interactable)
        {
            var height = AnnwUiKit.DropdownHeight;
            LanDropMenu.Create(row, name, flex, height, options, selectedId, id =>
            {
                if (SuppressCallbacks) return;
                onIdChanged?.Invoke(id);
            }, interactable);
        }

        public static void AddStatic(RectTransform row, string name, float flex, string text)
        {
            var host = AnnwUiKit.CreateRect(name, row);
            var le = host.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = flex;
            le.minWidth = 64f;
            le.minHeight = AnnwUiKit.DropdownHeight;
            le.preferredHeight = AnnwUiKit.DropdownHeight;
            AnnwUiKit.CreateImage(host, AnnwUiKit.PanelSprite, new Color(0.22f, 0.12f, 0.06f, 0.75f));
            var tmp = AnnwUiKit.CreateTmp(host, "T", text, SkirmishUiMetrics.SeatCaptionFont, AnnwUiKit.BodyColor, TextAlignmentOptions.Center);
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 14f;
            tmp.fontSizeMax = SkirmishUiMetrics.SeatCaptionFont;
        }

        public static Button AddCoButton(RectTransform row, float flex, string label, bool interactable, UnityEngine.Events.UnityAction onClick)
        {
            var h = AnnwUiKit.DropdownHeight;
            var btn = AnnwUiKit.CreateButton(row, "CO", label, h, () =>
            {
                if (SuppressCallbacks) return;
                onClick?.Invoke();
            });
            btn.interactable = interactable;
            var le = btn.GetComponent<LayoutElement>();
            if (le != null)
            {
                le.flexibleWidth = flex;
                le.minWidth = 72f;
                le.minHeight = h;
                le.preferredHeight = h;
            }
            var lbl = btn.GetComponentInChildren<TextMeshProUGUI>();
            if (lbl != null)
            {
                lbl.enableAutoSizing = true;
                lbl.fontSizeMin = 14f;
                lbl.fontSizeMax = SkirmishUiMetrics.CoBtnFont;
                lbl.fontSize = SkirmishUiMetrics.CoBtnFont;
            }
            return btn;
        }
    }
}
