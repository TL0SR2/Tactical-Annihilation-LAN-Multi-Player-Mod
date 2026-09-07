using System;
using System.Collections.Generic;
using AnnW.LanMp.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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

        /// <summary>
        /// Compact log Eco control: label + Slider (t=0→×0.1, t=0.5→×1, t=1→×100).
        /// Commits on pointer-up so SeatEdit is not spammed while dragging.
        /// </summary>
        public static void AddEcoLogSlider(
            RectTransform row,
            float flex,
            float currentMul,
            Action<float> onCommitted,
            bool interactable)
        {
            var h = AnnwUiKit.DropdownHeight;
            var host = AnnwUiKit.CreateRect("Eco", row);
            var le = host.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = flex;
            le.minWidth = 110f;
            le.minHeight = h;
            le.preferredHeight = h;

            AnnwUiKit.CreateImage(host, AnnwUiKit.PanelSprite, new Color(0.22f, 0.12f, 0.06f, 0.75f));

            var hlg = host.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(4, 4, 2, 2);
            hlg.spacing = 4f;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;

            var mul = SkirmishSeatEconomy.ClampLanResPercent(
                currentMul > 0f ? currentMul : SkirmishSeatEconomy.DefaultResPercent);
            var labelHost = AnnwUiKit.CreateRect("MulHost", host);
            var labelLe = labelHost.gameObject.AddComponent<LayoutElement>();
            labelLe.minWidth = 36f;
            labelLe.preferredWidth = 44f;
            labelLe.flexibleWidth = 0f;
            var label = AnnwUiKit.CreateTmp(
                labelHost, "Mul", SkirmishSeatEconomy.FormatResMul(mul),
                SkirmishUiMetrics.SeatCaptionFont, AnnwUiKit.BodyColor, TextAlignmentOptions.Center);
            label.enableAutoSizing = true;
            label.fontSizeMin = 11f;
            label.fontSizeMax = SkirmishUiMetrics.SeatCaptionFont;

            var sliderHost = AnnwUiKit.CreateRect("Slider", host);
            var sliderLe = sliderHost.gameObject.AddComponent<LayoutElement>();
            sliderLe.minWidth = 56f;
            sliderLe.flexibleWidth = 1f;
            sliderLe.minHeight = h - 6f;
            sliderLe.preferredHeight = h - 6f;

            var slider = BuildLogSlider(sliderHost, h - 6f);
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.wholeNumbers = false;
            slider.value = SkirmishSeatEconomy.EcoMulToSliderT(mul);
            slider.interactable = interactable;

            var pending = mul;
            var lastCommitted = mul;
            var snapping = false;
            slider.onValueChanged.AddListener(t =>
            {
                if (SuppressCallbacks || snapping) return;
                pending = SkirmishSeatEconomy.EcoSliderTToMul(t);
                label.text = SkirmishSeatEconomy.FormatResMul(pending);
            });

            void Commit()
            {
                if (SuppressCallbacks || !interactable) return;
                var q = SkirmishSeatEconomy.QuantizeLanResPercent(pending);
                label.text = SkirmishSeatEconomy.FormatResMul(q);
                snapping = true;
                try { slider.value = SkirmishSeatEconomy.EcoMulToSliderT(q); }
                finally { snapping = false; }
                if (SkirmishSeatEconomy.ApproxEqual(q, lastCommitted))
                    return;
                lastCommitted = q;
                onCommitted?.Invoke(q);
            }

            var trigger = slider.gameObject.AddComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.PointerUp, _ => Commit());
            AddTrigger(trigger, EventTriggerType.PointerClick, _ => Commit());
        }

        private static void AddTrigger(EventTrigger trigger, EventTriggerType type, Action<BaseEventData> cb)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(e => cb(e));
            trigger.triggers.Add(entry);
        }

        private static Slider BuildLogSlider(RectTransform host, float height)
        {
            var bg = AnnwUiKit.CreateImage(
                host, AnnwUiKit.WhiteSprite ?? AnnwUiKit.PanelSprite,
                new Color(0.12f, 0.08f, 0.05f, 0.95f), Image.Type.Sliced);
            Stretch(bg.rectTransform, 0f, 0.28f);

            var fillArea = AnnwUiKit.CreateRect("Fill Area", host);
            Stretch(fillArea, 5f, 0.28f);
            var fill = AnnwUiKit.CreateImage(
                fillArea, AnnwUiKit.WhiteSprite ?? AnnwUiKit.PanelSprite,
                new Color(0.75f, 0.55f, 0.2f, 0.95f), Image.Type.Sliced);
            StretchFull(fill.rectTransform);

            var handleArea = AnnwUiKit.CreateRect("Handle Slide Area", host);
            Stretch(handleArea, 0f, 0f);
            var handle = AnnwUiKit.CreateImage(
                handleArea, AnnwUiKit.WhiteSprite ?? AnnwUiKit.PanelSprite,
                new Color(0.95f, 0.85f, 0.55f, 1f), Image.Type.Sliced);
            handle.rectTransform.sizeDelta = new Vector2(12f, height);

            var slider = host.gameObject.AddComponent<Slider>();
            slider.targetGraphic = handle;
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.direction = Slider.Direction.LeftToRight;
            slider.transition = Selectable.Transition.ColorTint;
            return slider;
        }

        private static void Stretch(RectTransform rt, float insetX, float yFrac)
        {
            rt.anchorMin = new Vector2(0f, yFrac);
            rt.anchorMax = new Vector2(1f, 1f - yFrac);
            rt.offsetMin = new Vector2(insetX, 0f);
            rt.offsetMax = new Vector2(-insetX, 0f);
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
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
