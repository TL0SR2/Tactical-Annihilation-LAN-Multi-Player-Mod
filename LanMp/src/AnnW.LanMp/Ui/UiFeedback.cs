using System;
using System.Collections.Generic;
using UnityEngine;

namespace AnnW.LanMp.Ui
{
    /// <summary>Short in-overlay / floater messages for solo UI testing.</summary>
    internal static class UiFeedback
    {
        private const int MaxLines = 8;
        private static readonly List<string> Lines = new List<string>();
        private static string _toast;
        private static float _toastUntil;

        public static IReadOnlyList<string> Recent => Lines;

        public static string ActiveToast
        {
            get
            {
                if (string.IsNullOrEmpty(_toast) || Time.unscaledTime > _toastUntil)
                    return null;
                return _toast;
            }
        }

        public static void Push(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            Lines.Insert(0, line);
            while (Lines.Count > MaxLines)
                Lines.RemoveAt(Lines.Count - 1);

            _toast = message;
            _toastUntil = Time.unscaledTime + 4f;

            LanMpPlugin.Log?.LogInfo("[UI] " + message);
            TryGameFloater(message);
        }

        private static void TryGameFloater(string message)
        {
            try
            {
                var pop = UI_Floater.self?.pop_general;
                if (pop == null)
                    return;
                pop.ShowAsSimple("[LanMp] " + message);
                // Lobby/room roots call SetAsLastSibling; raise toast above them.
                RaiseAboveLanPanels(pop);
            }
            catch
            {
                // Menu floater may be unavailable in some scenes.
            }
        }

        private static void RaiseAboveLanPanels(Component popup)
        {
            if (popup == null) return;
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
                if (c.sortingOrder < 200)
                    c.sortingOrder = 200;
            }
            foreach (var c in popup.GetComponentsInChildren<Canvas>(true))
            {
                if (c == null) continue;
                c.overrideSorting = true;
                if (c.sortingOrder < 200)
                    c.sortingOrder = 200;
            }
        }
    }
}
