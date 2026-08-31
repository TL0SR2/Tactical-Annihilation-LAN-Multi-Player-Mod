using ANNW;
using UnityEngine;
using UnityEngine.UI;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Wires a real <see cref="MiniMapGen"/> into the LAN room preview.
    /// Reuses the vanilla component (clone widget only — not the whole skirmish screen).
    /// </summary>
    internal static class LanRoomMinimap
    {
        private static MiniMapGen _gen;
        private static GameObject _hint;
        private static RectTransform _host;

        public static void Attach(RectTransform previewHost, GameObject hintLabel)
        {
            _host = previewHost;
            _hint = hintLabel;
            EnsureGen();
        }

        public static void Clear()
        {
            if (_hint != null)
                _hint.SetActive(true);
            if (_gen != null)
                _gen.gameObject.SetActive(false);
        }

        public static void Render(DynOb mapOb)
        {
            if (mapOb == null || _host == null)
            {
                Clear();
                return;
            }

            if (!EnsureGen())
            {
                Clear();
                LanMpPlugin.Log?.LogWarning("[RoomUI] MiniMapGen template unavailable");
                return;
            }

            try
            {
                if (_hint != null)
                    _hint.SetActive(false);
                _gen.gameObject.SetActive(true);
                _gen.RenderMiniMap(mapOb, is_battle_save: false, show_pos: true);
            }
            catch (System.Exception ex)
            {
                LanMpPlugin.Log?.LogWarning("[RoomUI] RenderMiniMap failed: " + ex.Message);
                Clear();
            }
        }

        private static bool EnsureGen()
        {
            if (_gen != null)
                return true;
            if (_host == null)
                return false;

            var template = FindTemplate();
            if (template == null)
                return false;

            // Destroy leftover children except hint
            for (var i = _host.childCount - 1; i >= 0; i--)
            {
                var c = _host.GetChild(i);
                if (_hint != null && c.gameObject == _hint)
                    continue;
                if (c.name.StartsWith("LanMp_MiniMap"))
                    Object.Destroy(c.gameObject);
            }

            var go = Object.Instantiate(template.gameObject, _host, false);
            go.name = "LanMp_MiniMap";
            go.SetActive(true);
            var rt = go.transform as RectTransform;
            if (rt != null)
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.localScale = Vector3.one;
                rt.localRotation = Quaternion.identity;
            }

            _gen = go.GetComponent<MiniMapGen>();
            // Hide any pool text that might overflow until first render
            return _gen != null;
        }

        private static MiniMapGen FindTemplate()
        {
            try
            {
                var sk = SS_ANNW_Menu.self != null ? SS_ANNW_Menu.self.screen_skirmish : null;
                if (sk != null && sk.info_skirmish != null && sk.info_skirmish.miniMapGen != null)
                    return sk.info_skirmish.miniMapGen;
            }
            catch
            {
                // fall through
            }

            // Any live MiniMapGen in the menu scene
            // Fallback: any MiniMapGen already in the loaded menu scene.
            var all = Resources.FindObjectsOfTypeAll<MiniMapGen>();
            return all != null && all.Length > 0 ? all[0] : null;
        }
    }
}
