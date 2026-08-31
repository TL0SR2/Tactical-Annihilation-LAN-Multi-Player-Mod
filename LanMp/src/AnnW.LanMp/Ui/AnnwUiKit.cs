using System;
using System.Collections.Generic;
using ANNW;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Samples fonts/sprites/colors from live floater UI (asset references only — no Instantiate of whole panels).
    /// Matches vanilla popup look while building trees from scratch.
    /// </summary>
    internal static class AnnwUiKit
    {
        public static bool Ready { get; private set; }
        public static TMP_FontAsset Font { get; private set; }
        public static Sprite PanelSprite { get; private set; }
        public static Sprite ButtonSprite { get; private set; }
        public static Sprite WhiteSprite { get; private set; }
        public static Color PanelColor { get; private set; } = new Color(0.18f, 0.10f, 0.06f, 0.96f);
        public static Color DimColor { get; private set; } = new Color(0f, 0f, 0f, 0.65f);
        public static Color ButtonColor { get; private set; } = new Color(0.45f, 0.22f, 0.08f, 0.98f);
        public static Color TitleColor { get; private set; } = new Color(1f, 0.55f, 0.15f, 1f);
        public static Color BodyColor { get; private set; } = new Color(0.92f, 0.88f, 0.80f, 1f);
        public static Color InputColor { get; private set; } = new Color(0.10f, 0.06f, 0.04f, 0.98f);

        /// <summary>Vanilla skirmish seat dropdowns sit around this; never go thinner.</summary>
        public const float SkirmishDropdownMin = 36f;
        public const float SkirmishSeatRowMin = 48f;
        public const float SkirmishMapBtnMin = 28f;
        public const float SkirmishSeatSpacing = 7f;

        /// <summary>Seat / shared caption dropdown height (baked from Item_Option dropdowns).</summary>
        public static float DropdownHeight { get; private set; } = SkirmishUiMetrics.SeatDropH;

        /// <summary>InfoSkm rule dropdown height (baked from dd_fow).</summary>
        public static float RuleDropdownHeight { get; private set; } = SkirmishUiMetrics.RuleDropH;

        /// <summary>Seat row height (baked from Item_Option).</summary>
        public static float SeatRowHeight { get; private set; } = SkirmishUiMetrics.SeatRowH;

        /// <summary>Map list row height (baked).</summary>
        public static float MapBtnHeight { get; private set; } = SkirmishUiMetrics.MapBtnH;

        /// <summary>Vanilla TMP_Dropdown arrow glyph (Image), if found.</summary>
        public static Sprite ArrowSprite { get; private set; }

        private static TMP_Dropdown _dropdownTemplate;

        public static bool EnsureSampled()
        {
            if (Ready && Font != null)
            {
                EnsureDropdownTemplate();
                ApplyMetrics();
                return true;
            }

            var floater = UI_Floater.self;
            if (floater == null)
                return false;

            TextMeshProUGUI title = null;
            if (floater.pop_general != null)
                title = floater.pop_general.txt_title;
            if (title == null && floater.pop_options != null)
                title = floater.pop_options.GetComponentInChildren<TextMeshProUGUI>(true);
            if (title != null)
            {
                Font = title.font;
                TitleColor = title.color;
            }

            GameObject panelGo = null;
            if (floater.pop_general != null)
                panelGo = floater.pop_general.panel;
            if (panelGo == null && floater.pop_options != null)
                panelGo = floater.pop_options.panel;
            var panelImg = panelGo != null
                ? (panelGo.GetComponent<Image>() ?? panelGo.GetComponentInChildren<Image>(true))
                : null;
            if (panelImg != null)
            {
                PanelSprite = panelImg.sprite;
                PanelColor = panelImg.color;
            }

            ButtonSprite = null;
            foreach (var b in floater.GetComponentsInChildren<Button>(true))
            {
                if (b == null) continue;
                var img = b.GetComponent<Image>();
                if (img == null || img.sprite == null) continue;
                if (!LooksLikeSolidChrome(img.sprite)) continue;
                ButtonSprite = img.sprite;
                if (img.color.a > 0.2f)
                    ButtonColor = img.color;
                break;
            }

            foreach (var img in floater.GetComponentsInChildren<Image>(true))
            {
                if (img?.sprite == null) continue;
                if (!LooksLikeSolidChrome(img.sprite)) continue;
                WhiteSprite = img.sprite;
                break;
            }

            if (WhiteSprite == null && PanelSprite != null)
                WhiteSprite = PanelSprite;

            if (Font == null)
            {
                var any = floater.GetComponentInChildren<TextMeshProUGUI>(true);
                if (any != null)
                    Font = any.font;
            }

            EnsureDropdownTemplate();
            ApplyMetrics();
            Ready = Font != null;
            if (!Ready)
                LanMpPlugin.Log?.LogWarning("[UI] AnnwUiKit: failed to sample TMP font from floater");
            else
                LanMpPlugin.Log?.LogInfo("[UI] AnnwUiKit font=" + Font.name +
                                         " baked seatH=" + DropdownHeight +
                                         " ruleH=" + RuleDropdownHeight +
                                         " mapBtnH=" + MapBtnHeight +
                                         " arrowSprite=" + (ArrowSprite != null));
            return Ready;
        }

        private static void ApplyMetrics()
        {
            RuleDropdownHeight = SkirmishUiMetrics.RuleDropH;
            DropdownHeight = SkirmishUiMetrics.SeatDropH;
            SeatRowHeight = SkirmishUiMetrics.SeatRowH;
            MapBtnHeight = SkirmishUiMetrics.MapBtnH;
        }

        private static void EnsureDropdownTemplate()
        {
            if (_dropdownTemplate != null)
            {
                if (ArrowSprite == null)
                    SampleArrowSprite(_dropdownTemplate);
                return;
            }

            TMP_Dropdown best = null;
            var bestScore = -1;

            foreach (var info in Resources.FindObjectsOfTypeAll<UI_MENU_LevelSelect_InfoSkm>())
            {
                if (info == null) continue;
                foreach (var cand in new[] { info.dd_fow, info.dd_condition, info.dd_quickStart })
                {
                    if (cand == null) continue;
                    var score = 30;
                    if (cand.template != null) score += 2;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = cand;
                    }
                }
            }

            if (best == null)
            {
                foreach (var ps in Resources.FindObjectsOfTypeAll<UI_SKM_PlayerSetting>())
                {
                    if (ps == null) continue;
                    TMP_Dropdown cand = ps.dd_pos ?? ps.dd_team ?? ps.dd_color ?? ps.dd_op;
                    if (cand == null) continue;
                    var score = 20;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = cand;
                    }
                }
            }

            if (best == null)
            {
                foreach (var dd in Resources.FindObjectsOfTypeAll<TMP_Dropdown>())
                {
                    if (dd == null) continue;
                    if (dd.gameObject.name != null && dd.gameObject.name.StartsWith("LanMp_", StringComparison.Ordinal))
                        continue;
                    var n = dd.gameObject.name ?? "";
                    var score = 0;
                    if (n.IndexOf("dd_fow", StringComparison.OrdinalIgnoreCase) >= 0) score += 5;
                    if (n.IndexOf("dd_", StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
                    if (dd.template != null) score += 2;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = dd;
                    }
                }
            }

            if (best == null)
                return;

            _dropdownTemplate = best;
            SampleArrowSprite(best);
            LanMpPlugin.Log?.LogInfo("[UI] Dropdown arrow template=" + best.gameObject.name +
                                     " arrow=" + (ArrowSprite != null ? ArrowSprite.name : "null"));
        }

        private static void SampleArrowSprite(TMP_Dropdown dd)
        {
            if (ArrowSprite != null || dd == null)
                return;
            foreach (var img in dd.GetComponentsInChildren<Image>(true))
            {
                if (img == null || img.sprite == null) continue;
                var n = img.gameObject.name ?? "";
                if (n.IndexOf("arrow", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                ArrowSprite = img.sprite;
                return;
            }
        }

        /// <summary>
        /// Clone a vanilla TMP_Dropdown (chrome/template). Returns null if no sample in memory.
        /// </summary>
        public static TMP_Dropdown TryCloneDropdown(Transform parent, string name, float flexWidth)
        {
            EnsureDropdownTemplate();
            if (_dropdownTemplate == null)
                return null;

            var go = UnityEngine.Object.Instantiate(_dropdownTemplate.gameObject, parent, false);
            go.name = "LanMp_" + name;
            go.SetActive(true);
            var dd = go.GetComponent<TMP_Dropdown>();
            dd.onValueChanged.RemoveAllListeners();
            dd.ClearOptions();
            dd.interactable = true;

            // Kill leftover expanded list if template was open
            for (var i = go.transform.childCount - 1; i >= 0; i--)
            {
                var c = go.transform.GetChild(i);
                if (c.name.IndexOf("Dropdown List", StringComparison.OrdinalIgnoreCase) >= 0)
                    UnityEngine.Object.Destroy(c.gameObject);
            }

            var le = go.GetComponent<LayoutElement>();
            if (le == null)
                le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = flexWidth;
            le.minWidth = 70f;
            le.flexibleHeight = 0f;
            le.minHeight = DropdownHeight;
            le.preferredHeight = DropdownHeight;
            // Ignore template's ignoreLayout / zero preferred from pool items
            le.ignoreLayout = false;

            var rt = go.transform as RectTransform;
            if (rt != null)
            {
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, DropdownHeight);
                // Keep stretch width under HLG
                var sd = rt.sizeDelta;
                rt.sizeDelta = new Vector2(sd.x, DropdownHeight);
            }

            // Draw dropdown list above ScrollRect masks / room chrome
            var canvas = go.GetComponent<Canvas>();
            if (canvas == null)
                canvas = go.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 60;
            if (go.GetComponent<GraphicRaycaster>() == null)
                go.AddComponent<GraphicRaycaster>();

            return dd;
        }

        public static void WireDropdown(
            TMP_Dropdown dd,
            IList<string> labels,
            int value,
            Action<int> onChanged,
            bool interactable = true)
        {
            if (dd == null) return;
            dd.onValueChanged.RemoveAllListeners();
            dd.ClearOptions();
            var opts = new List<TMP_Dropdown.OptionData>();
            if (labels != null)
            {
                foreach (var t in labels)
                    opts.Add(new TMP_Dropdown.OptionData(t ?? ""));
            }
            dd.AddOptions(opts);
            if (opts.Count == 0)
            {
                dd.interactable = false;
                return;
            }
            var v = Mathf.Clamp(value, 0, opts.Count - 1);
            dd.SetValueWithoutNotify(v);
            dd.interactable = interactable;
            if (onChanged != null)
                dd.onValueChanged.AddListener(i => onChanged(i));
            dd.RefreshShownValue();
        }

        private static bool LooksLikeSolidChrome(Sprite s)
        {
            if (s == null)
                return false;
            var n = s.name ?? "";
            if (n.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (n.IndexOf("light", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (n.IndexOf("fx", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            var r = s.rect;
            if (r.width < 8f || r.height < 8f)
                return false;
            var aspect = r.width / Mathf.Max(1f, r.height);
            if (aspect > 6f || aspect < 0.15f)
                return false;
            return true;
        }

        public static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent != null ? parent.gameObject.layer : 5;
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            return rt;
        }

        public static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        public static Image CreateImage(RectTransform rt, Sprite sprite, Color color, Image.Type type = Image.Type.Sliced)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite ?? WhiteSprite ?? PanelSprite;
            img.type = sprite != null && sprite.border.sqrMagnitude > 0.01f ? type : Image.Type.Simple;
            img.color = color;
            img.raycastTarget = true;
            return img;
        }

        public static TextMeshProUGUI CreateTmp(RectTransform parent, string name, string text, float size, Color color, TextAlignmentOptions align)
        {
            var rt = CreateRect(name, parent);
            StretchFull(rt);
            var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null)
                tmp.font = Font;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.raycastTarget = false;
            tmp.text = text ?? "";
            tmp.enableAutoSizing = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return tmp;
        }

        public static Button CreateButton(Transform parent, string name, string label, float height, UnityEngine.Events.UnityAction onClick)
        {
            return CreateButton(parent, name, label, height, onClick, TextAlignmentOptions.Center, 22f);
        }

        /// <summary>Map list entry — left-aligned caption, height/font from skirmish LS_FOLDER_BTN metrics.</summary>
        public static Button CreateMapListButton(Transform parent, string name, string label, UnityEngine.Events.UnityAction onClick)
        {
            return CreateButton(parent, name, label, MapBtnHeight, onClick, TextAlignmentOptions.MidlineLeft, SkirmishUiMetrics.MapBtnFont);
        }

        public static Button CreateButton(
            Transform parent,
            string name,
            string label,
            float height,
            UnityEngine.Events.UnityAction onClick,
            TextAlignmentOptions align,
            float fontSize)
        {
            var rt = CreateRect(name, parent);
            rt.sizeDelta = new Vector2(0f, height);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = 0f;
            le.flexibleWidth = 1f;

            var chrome = PanelSprite ?? WhiteSprite ?? ButtonSprite;
            var img = CreateImage(rt, chrome, ButtonColor, Image.Type.Sliced);
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.05f, 0.9f, 1f);
            colors.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f);
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            colors.colorMultiplier = 1f;
            btn.colors = colors;

            var labelTmp = CreateTmp(rt, "Label", label, fontSize, BodyColor, align);
            labelTmp.enableAutoSizing = true;
            labelTmp.fontSizeMin = 11f;
            labelTmp.fontSizeMax = fontSize;
            labelTmp.overflowMode = TextOverflowModes.Ellipsis;
            var lrt = labelTmp.rectTransform;
            lrt.offsetMin = new Vector2(10f, 2f);
            lrt.offsetMax = new Vector2(-10f, -2f);

            var ev = new Button.ButtonClickedEvent();
            ev.AddListener(onClick);
            btn.onClick = ev;
            return btn;
        }

        public static RectTransform CreateRow(Transform parent, string name, float height, float spacing = 10f)
        {
            var rt = CreateRect(name, parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = 0f;
            le.flexibleWidth = 1f;
            var hlg = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = spacing;
            hlg.padding = new RectOffset(0, 0, 0, 0);
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;
            return rt;
        }

        public static TMP_InputField CreateInput(Transform parent, string name, string placeholder, float height)
        {
            var rt = CreateRect(name, parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = 0f;
            le.flexibleWidth = 1f;
            CreateImage(rt, PanelSprite ?? WhiteSprite ?? ButtonSprite, InputColor, Image.Type.Sliced);

            var textArea = CreateRect("Text Area", rt);
            StretchFull(textArea);
            textArea.offsetMin = new Vector2(14f, 8f);
            textArea.offsetMax = new Vector2(-14f, -8f);
            textArea.gameObject.AddComponent<RectMask2D>();

            var ph = CreateTmp(textArea, "Placeholder", placeholder, 18f, new Color(1f, 1f, 1f, 0.35f), TextAlignmentOptions.MidlineLeft);
            ph.fontStyle = FontStyles.Italic;

            var text = CreateTmp(textArea, "Text", "", 18f, BodyColor, TextAlignmentOptions.MidlineLeft);

            var input = rt.gameObject.AddComponent<TMP_InputField>();
            input.textViewport = textArea;
            input.textComponent = text;
            input.placeholder = ph;
            if (Font != null)
                input.fontAsset = Font;
            input.caretColor = TitleColor;
            input.selectionColor = new Color(TitleColor.r, TitleColor.g, TitleColor.b, 0.35f);
            return input;
        }
    }
}
