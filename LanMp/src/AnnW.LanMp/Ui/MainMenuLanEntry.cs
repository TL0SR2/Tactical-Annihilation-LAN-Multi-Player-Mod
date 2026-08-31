using System.Collections.Generic;
using System.Text;
using ANNW;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Injects「多人联机大厅」into main-menu Skirmish submenu using vanilla Localized_Txt + SD_LAN_LAN.
    /// </summary>
    internal static class MainMenuLanEntry
    {
        public const string ButtonObjectName = "LanMp_LanLobbyBtn";
        public const string LineObjectName = "LanMp_LanLobbyLine";

        private static bool _dumpedOnce;

        public static void EnsureInjected(UI_MENU_MainMenu menu)
        {
            if (menu == null || menu.pop_skirmish == null)
                return;

            LanLocalization.EnsureRegistered();

            var pop = menu.pop_skirmish;
            if (!_dumpedOnce)
            {
                _dumpedOnce = true;
                DumpPop(pop);
            }

            var existing = FindDeep(pop.transform, ButtonObjectName);
            if (existing != null)
            {
                existing.gameObject.SetActive(true);
                LanLocalization.BindLobbyButton(existing.gameObject);
                EnsureLine(pop, existing as RectTransform ?? existing.GetComponent<RectTransform>());
                return;
            }

            var template = FindTemplateButton(pop);
            if (template == null)
            {
                LanMpPlugin.Log?.LogWarning("[UI] pop_skirmish has no Button to clone");
                return;
            }

            var clone = Object.Instantiate(template.gameObject, template.transform.parent, false);
            clone.name = ButtonObjectName;
            clone.SetActive(true);

            var anchor = FindLowestButton(pop, template);
            PlaceBelow(anchor.transform as RectTransform, clone.transform as RectTransform);

            // Keep Localized_Txt; retarget cate/key to our SD_LAN_LAN row (do not Destroy localization).
            LanLocalization.BindLobbyButton(clone);

            var btn = clone.GetComponent<Button>() ?? clone.GetComponentInChildren<Button>(true);
            if (btn != null)
            {
                var ev = new Button.ButtonClickedEvent();
                ev.AddListener(() => OnClicked(menu));
                btn.onClick = ev;
                btn.interactable = true;
            }

            // Prevent cloned UI_MenuBtn / persistent hooks from still firing SkirmishNew.
            foreach (var menuBtn in clone.GetComponentsInChildren<UI_MenuBtn>(true))
                Object.Destroy(menuBtn);

            EnsureLine(pop, clone.transform as RectTransform);
            LanMpPlugin.Log?.LogInfo("[UI] Injected LAN lobby btn via Localized_Txt key=" +
                                     LanLocalization.Cate + "/" + LanLocalization.KeyLobby +
                                     " (template=" + template.gameObject.name + ")");
        }

        private static void OnClicked(UI_MENU_MainMenu menu)
        {
            if (menu?.pop_skirmish != null)
                menu.pop_skirmish.SetActive(false);
            if (menu?.pop_battle_space != null)
                menu.pop_battle_space.SetActive(false);
            if (menu?.pop_editor != null)
                menu.pop_editor.SetActive(false);

            LanLobbyNativePanel.Open();
            LanMpPlugin.Log?.LogInfo("[UI] LAN lobby button clicked");
        }

        private static void EnsureLine(GameObject pop, RectTransform newBtn)
        {
            if (pop == null || newBtn == null)
                return;

            var existingLine = FindDeep(pop.transform, LineObjectName);
            if (existingLine != null)
            {
                existingLine.gameObject.SetActive(true);
                RetargetLine(existingLine.GetComponent<UILineRenderer>(), newBtn);
                return;
            }

            var templateLine = FindBestLineTemplate(pop);
            if (templateLine == null)
            {
                LanMpPlugin.Log?.LogWarning("[UI] No UILineRenderer under pop_skirmish to clone");
                return;
            }

            var lineGo = Object.Instantiate(templateLine.gameObject, templateLine.transform.parent, false);
            lineGo.name = LineObjectName;
            lineGo.SetActive(true);
            RetargetLine(lineGo.GetComponent<UILineRenderer>(), newBtn);
            LanMpPlugin.Log?.LogInfo("[UI] Cloned menu line from " + templateLine.gameObject.name);
        }

        private static void RetargetLine(UILineRenderer lr, RectTransform newBtn)
        {
            if (lr == null || newBtn == null)
                return;

            var oldPts = AccessTools.Field(typeof(UILineRenderer), "_points")?.GetValue(lr) as Vector2[];
            if (oldPts == null || oldPts.Length == 0)
            {
                var local = (Vector2)lr.transform.InverseTransformPoint(newBtn.position);
                lr.SetPoints(new[] { local + Vector2.left * 40f, local });
                return;
            }

            var pts = (Vector2[])oldPts.Clone();
            var endLocal = (Vector2)lr.transform.InverseTransformPoint(newBtn.position);
            if (pts.Length == 1)
            {
                lr.SetPoints(new[] { pts[0], endLocal });
                return;
            }

            pts[pts.Length - 1] = endLocal;
            if (pts.Length >= 3)
                pts[pts.Length - 2] = new Vector2(pts[pts.Length - 2].x, endLocal.y);
            lr.SetPoints(pts);
        }

        private static UILineRenderer FindBestLineTemplate(GameObject pop)
        {
            UILineRenderer best = null;
            var bestScore = int.MinValue;
            foreach (var lr in pop.GetComponentsInChildren<UILineRenderer>(true))
            {
                if (lr == null || lr.gameObject.name.StartsWith("LanMp_"))
                    continue;
                var name = lr.gameObject.name ?? "";
                var score = 0;
                if (name.IndexOf("new", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 10;
                if (name.IndexOf("load", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 5;
                var pts = AccessTools.Field(typeof(UILineRenderer), "_points")?.GetValue(lr) as Vector2[];
                if (pts != null)
                    score += pts.Length;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = lr;
                }
            }
            return best;
        }

        private static Button FindTemplateButton(GameObject pop)
        {
            Button best = null;
            foreach (var btn in pop.GetComponentsInChildren<Button>(true))
            {
                if (btn == null || btn.gameObject.name.StartsWith("LanMp_"))
                    continue;
                var name = btn.gameObject.name ?? "";
                if (name.IndexOf("New", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return btn;
                if (best == null)
                    best = btn;
            }
            return best;
        }

        private static Button FindLowestButton(GameObject pop, Button fallback)
        {
            Button lowest = fallback;
            var lowestY = float.MaxValue;
            foreach (var btn in pop.GetComponentsInChildren<Button>(true))
            {
                if (btn == null || btn.gameObject.name.StartsWith("LanMp_"))
                    continue;
                var rt = btn.transform as RectTransform;
                if (rt == null)
                    continue;
                if (rt.anchoredPosition.y < lowestY)
                {
                    lowestY = rt.anchoredPosition.y;
                    lowest = btn;
                }
            }
            return lowest ?? fallback;
        }

        private static void PlaceBelow(RectTransform template, RectTransform clone)
        {
            if (template == null || clone == null)
                return;
            clone.anchorMin = template.anchorMin;
            clone.anchorMax = template.anchorMax;
            clone.pivot = template.pivot;
            clone.sizeDelta = template.sizeDelta;
            clone.localScale = template.localScale;
            var step = Mathf.Max(52f, Mathf.Abs(template.rect.height) + 12f);
            clone.anchoredPosition = template.anchoredPosition + new Vector2(0f, -step);
            clone.SetAsLastSibling();
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
                return null;
            if (root.name == name)
                return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var f = FindDeep(root.GetChild(i), name);
                if (f != null)
                    return f;
            }
            return null;
        }

        private static void DumpPop(GameObject pop)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[UI] pop_skirmish dump:");
            DumpNode(pop.transform, 0, sb);
            foreach (var lr in pop.GetComponentsInChildren<UILineRenderer>(true))
            {
                var pts = AccessTools.Field(typeof(UILineRenderer), "_points")?.GetValue(lr) as Vector2[];
                sb.Append("  LINE ").Append(lr.gameObject.name).Append(" pts=");
                if (pts != null)
                {
                    foreach (var p in pts)
                        sb.Append('(').Append(p.x.ToString("0.0")).Append(',').Append(p.y.ToString("0.0")).Append(") ");
                }
                sb.AppendLine();
            }
            foreach (var loc in pop.GetComponentsInChildren<Localized_Txt>(true))
            {
                sb.Append("  LOC ").Append(loc.gameObject.name)
                    .Append(" cate=").Append(loc.cate)
                    .Append(" key=").Append(loc.key)
                    .AppendLine();
            }
            LanMpPlugin.Log?.LogInfo(sb.ToString());
        }

        private static void DumpNode(Transform t, int depth, StringBuilder sb)
        {
            var pad = new string(' ', depth * 2);
            var comps = new List<string>();
            foreach (var c in t.GetComponents<Component>())
            {
                if (c != null)
                    comps.Add(c.GetType().Name);
            }
            sb.Append(pad).Append(t.name).Append(" [").Append(string.Join(",", comps)).AppendLine("]");
            for (var i = 0; i < t.childCount; i++)
                DumpNode(t.GetChild(i), depth + 1, sb);
        }
    }
}
