using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Caption + ▼ (right-pinned) + floating option list — skirmish structure, not Instantiated prefabs.
    /// </summary>
    internal static class LanDropMenu
    {
        private static RectTransform _layer;
        private static GameObject _openList;
        private static Handle _openHandle;

        public sealed class Handle
        {
            public RectTransform Root;
            public Button Caption;
            public TextMeshProUGUI Label;
            public Image Chrome;
            public readonly List<Option> Options = new List<Option>();
            public int Value;
            public Action<int> OnChanged;

            public bool Interactable
            {
                get => Caption != null && Caption.interactable;
                set
                {
                    if (Caption != null)
                        Caption.interactable = value;
                }
            }
        }

        public struct Option
        {
            public string Text;
            public int Id;
            public Option(string text, int id)
            {
                Text = text;
                Id = id;
            }
        }

        public static Handle Create(
            RectTransform row,
            string name,
            float flex,
            float height,
            IList<Option> options,
            int selectedId,
            Action<int> onChanged,
            bool interactable = true)
        {
            var h = new Handle { OnChanged = onChanged, Value = selectedId };
            if (options != null)
                h.Options.AddRange(options);

            var rt = AnnwUiKit.CreateRect(name, row);
            h.Root = rt;
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = flex;
            le.minWidth = 72f;
            le.minHeight = height;
            le.preferredHeight = height;

            h.Chrome = AnnwUiKit.CreateImage(rt, AnnwUiKit.PanelSprite, AnnwUiKit.ButtonColor);
            h.Caption = rt.gameObject.AddComponent<Button>();
            h.Caption.targetGraphic = h.Chrome;
            h.Caption.interactable = interactable;
            var colors = h.Caption.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.12f, 1.05f, 0.92f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.65f);
            h.Caption.colors = colors;

            // Caption text: left-aligned; right inset matches vanilla Label (477−432=45).
            var fontMax = SkirmishUiMetrics.RuleCaptionFont;
            h.Label = AnnwUiKit.CreateTmp(rt, "Caption", FindLabel(h), fontMax,
                AnnwUiKit.BodyColor, TextAlignmentOptions.MidlineLeft);
            h.Label.enableAutoSizing = true;
            h.Label.fontSizeMin = 14f;
            h.Label.fontSizeMax = fontMax;
            var lrt = h.Label.rectTransform;
            lrt.offsetMin = new Vector2(SkirmishUiMetrics.CaptionPadLeft, 1f);
            lrt.offsetMax = new Vector2(-SkirmishUiMetrics.CaptionPadRight, -1f);

            PlaceArrow(rt);

            h.Caption.onClick.AddListener(() =>
            {
                if (!h.Caption.interactable) return;
                Toggle(h);
            });
            return h;
        }

        /// <summary>
        /// Vanilla TMP_Dropdown pins a small triangle Image on the far right;
        /// fall back to a TMP glyph only when no sprite was sampled.
        /// </summary>
        private static void PlaceArrow(RectTransform host)
        {
            var art = AnnwUiKit.CreateRect("Arrow", host);
            art.anchorMin = new Vector2(1f, 0.5f);
            art.anchorMax = new Vector2(1f, 0.5f);
            art.pivot = new Vector2(1f, 0.5f);
            art.sizeDelta = new Vector2(SkirmishUiMetrics.ArrowW, SkirmishUiMetrics.ArrowH);
            art.anchoredPosition = new Vector2(-SkirmishUiMetrics.ArrowPadRight, 0f);

            if (AnnwUiKit.ArrowSprite != null)
            {
                var img = art.gameObject.AddComponent<Image>();
                img.sprite = AnnwUiKit.ArrowSprite;
                img.color = AnnwUiKit.TitleColor;
                img.raycastTarget = false;
                img.preserveAspect = true;
                return;
            }

            var arrow = art.gameObject.AddComponent<TextMeshProUGUI>();
            if (AnnwUiKit.Font != null)
                arrow.font = AnnwUiKit.Font;
            arrow.fontSize = 16f;
            arrow.color = AnnwUiKit.TitleColor;
            arrow.alignment = TextAlignmentOptions.Center;
            arrow.text = "▼";
            arrow.raycastTarget = false;
            arrow.overflowMode = TextOverflowModes.Overflow;
        }

        public static void Tint(Handle h, Color c)
        {
            if (h?.Chrome == null) return;
            c.a = 0.9f;
            h.Chrome.color = Color.Lerp(AnnwUiKit.ButtonColor, c, 0.55f);
        }

        public static void CloseOpen()
        {
            if (_openList != null)
            {
                UnityEngine.Object.Destroy(_openList);
                _openList = null;
            }
            _openHandle = null;
        }

        public static void BringFloaterPopupToFront(Component popup)
        {
            if (popup == null) return;
            EnsureLayer();
            if (_layer != null)
                _layer.SetAsLastSibling();

            var floater = UI_Floater.self;
            Transform raise = popup.transform;
            if (floater != null)
            {
                var p = popup.transform;
                while (p.parent != null && p.parent != floater.transform)
                    p = p.parent;
                if (p.parent == floater.transform)
                    raise = p;
            }
            raise.SetAsLastSibling();

            foreach (var c in popup.GetComponentsInParent<Canvas>(true))
            {
                if (c == null) continue;
                c.overrideSorting = true;
                if (c.sortingOrder < 120)
                    c.sortingOrder = 120;
            }
            foreach (var c in popup.GetComponentsInChildren<Canvas>(true))
            {
                if (c == null) continue;
                c.overrideSorting = true;
                if (c.sortingOrder < 120)
                    c.sortingOrder = 120;
            }
        }

        private static string FindLabel(Handle h)
        {
            foreach (var o in h.Options)
                if (o.Id == h.Value)
                    return o.Text;
            return h.Options.Count > 0 ? h.Options[0].Text : "—";
        }

        private static void Toggle(Handle h)
        {
            if (_openHandle == h)
            {
                CloseOpen();
                return;
            }
            CloseOpen();
            Open(h);
        }

        private static void EnsureLayer()
        {
            if (_layer != null) return;
            var floater = UI_Floater.self;
            if (floater == null) return;
            var existing = floater.transform.Find("LanMp_DropLayer") as RectTransform;
            if (existing != null)
            {
                _layer = existing;
                return;
            }
            _layer = AnnwUiKit.CreateRect("LanMp_DropLayer", floater.transform);
            AnnwUiKit.StretchFull(_layer);
            _layer.SetAsLastSibling();
        }

        private static void Open(Handle h)
        {
            EnsureLayer();
            if (_layer == null || h?.Root == null) return;

            _openHandle = h;
            var itemH = Mathf.Max(30f, AnnwUiKit.DropdownHeight);
            var count = Mathf.Max(1, h.Options.Count);
            var visible = Mathf.Min(count, 8);
            var height = itemH * visible + 6f;

            var corners = new Vector3[4];
            h.Root.GetWorldCorners(corners); // 0=bl 1=tl 2=tr 3=br
            var width = Vector3.Distance(corners[0], corners[3]);

            var listRt = AnnwUiKit.CreateRect("LanDropList", _layer);
            _openList = listRt.gameObject;
            listRt.anchorMin = listRt.anchorMax = new Vector2(0.5f, 0.5f);
            listRt.pivot = new Vector2(0f, 1f);
            listRt.sizeDelta = new Vector2(Mathf.Max(100f, width), height);
            listRt.position = corners[0];

            var canvas = _layer.GetComponentInParent<Canvas>();
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var screenBl = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            if (screenBl.y < height + 8f)
            {
                listRt.pivot = new Vector2(0f, 0f);
                listRt.position = corners[1];
            }

            AnnwUiKit.CreateImage(listRt, AnnwUiKit.PanelSprite, new Color(0.10f, 0.06f, 0.03f, 0.98f));

            var content = AnnwUiKit.CreateRect("Content", listRt);
            AnnwUiKit.StretchFull(content);
            content.offsetMin = new Vector2(3f, 3f);
            content.offsetMax = new Vector2(-3f, -3f);
            var v = content.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 2f;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            foreach (var opt in h.Options)
            {
                var captured = opt;
                var btn = AnnwUiKit.CreateButton(content, "Opt" + opt.Id, opt.Text, itemH - 2f, () =>
                {
                    h.Value = captured.Id;
                    if (h.Label != null)
                        h.Label.text = captured.Text;
                    CloseOpen();
                    try { h.OnChanged?.Invoke(captured.Id); }
                    catch (Exception ex) { LanMpPlugin.Log?.LogWarning("[Drop] " + ex.Message); }
                });
                var ble = btn.GetComponent<LayoutElement>();
                if (ble != null)
                {
                    ble.minHeight = itemH - 2f;
                    ble.preferredHeight = itemH - 2f;
                }
            }

            _layer.SetAsLastSibling();
            listRt.SetAsLastSibling();
        }
    }
}
