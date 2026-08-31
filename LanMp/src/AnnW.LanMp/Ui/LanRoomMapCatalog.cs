using System;
using System.Collections.Generic;
using AnnW.LanMp.Protocol;
using BepInEx.Logging;
using UnityEngine;

namespace AnnW.LanMp.Ui
{
    /// <summary>Built-in map catalog — same data semantics as skirmish list (name + player_num), not UI clone.</summary>
    internal static class LanRoomMapCatalog
    {
        public sealed class Entry
        {
            public string Id;
            public string DisplayName;
            public string ListLabel;
            public string ResourcesPath;
            public int PlayerNum;
            public string SizeText;
            public string ThemeText;
        }

        public static List<Entry> ListBuiltin(ManualLogSource log, int max = 80)
        {
            var list = new List<Entry>();
            try
            {
                foreach (var kv in SDBase<SD_ANNW_SK_MAP>.dic)
                {
                    var sd = kv.Value;
                    if (sd == null || sd.hide)
                        continue;
                    var pack = sd.pack != null ? sd.pack.name : "Unknown";
                    var path = "Skirmish/" + pack + "/" + sd.name;
                    var display = ResolveLevelName(sd.name);
                    var playerNum = TryReadPlayerNum(path);
                    list.Add(new Entry
                    {
                        Id = sd.name,
                        DisplayName = display,
                        ListLabel = playerNum > 0 ? display + "(" + playerNum + ")" : display,
                        ResourcesPath = path,
                        PlayerNum = playerNum
                    });
                    if (list.Count >= max)
                        break;
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Room] ListBuiltin maps failed: " + ex.Message);
            }

            list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));
            return list;
        }

        public static bool TryLoadText(Entry entry, out string text, out string mapKey)
        {
            text = null;
            mapKey = entry?.ResourcesPath ?? entry?.Id;
            if (entry == null)
                return false;

            var ta = Resources.Load<TextAsset>(entry.ResourcesPath);
            if (ta == null)
                ta = Resources.Load<TextAsset>("Skirmish/" + entry.Id);
            if (ta == null)
            {
                try
                {
                    var sd = SDBase<SD_ANNW_SK_MAP>.Get(entry.Id);
                    if (sd != null)
                    {
                        var pack = sd.pack != null ? sd.pack.name : "Unknown";
                        var path = "Skirmish/" + pack + "/" + sd.name;
                        ta = Resources.Load<TextAsset>(path);
                        if (ta != null)
                            mapKey = path;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (ta == null)
                return false;
            text = ta.text;
            mapKey = entry.ResourcesPath;
            if (Resources.Load<TextAsset>(mapKey) == null)
                mapKey = ta.name;
            return !string.IsNullOrEmpty(text);
        }

        public static string HashOf(string mapText) => HashUtil.StableHash16(mapText ?? "");

        public static void FillDetail(Entry entry, DynOb ob)
        {
            if (entry == null || ob == null)
                return;
            try
            {
                var terrain = ob.GetKey_Obj("terrain");
                if (terrain == null)
                    return;
                var half = terrain.GetKey_Inctor("half_size");
                entry.SizeText = (half.x * 2 + 1) + "x" + (half.y * 2 + 1);
                var themeKey = terrain.GetKey_String("theme_name");
                entry.ThemeText = ResolveThemeName(themeKey);
            }
            catch
            {
                // keep prior
            }
        }

        /// <summary>Same source as skirmish list: SD_LAN_LEVEL_NAME.cn/en by current language.</summary>
        public static string ResolveLevelName(string id)
        {
            if (string.IsNullOrEmpty(id))
                return id;
            try
            {
                if (SDBase<SD_LAN_LEVEL_NAME>.Has(id, alert: false))
                {
                    var row = SDBase<SD_LAN_LEVEL_NAME>.dic[id];
                    if (row != null)
                    {
                        var zh = IsZh();
                        var n = zh ? row.cn : row.en;
                        if (!string.IsNullOrEmpty(n))
                            return n;
                    }
                }
            }
            catch
            {
                // fall through
            }
            return id;
        }

        public static string ResolveThemeName(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "";
            try
            {
                // Public API used by vanilla GetThemeName
                var n = LAN.Get("THEME", key);
                if (!string.IsNullOrEmpty(n) && n != key && n.IndexOf("miss:", StringComparison.OrdinalIgnoreCase) < 0)
                    return n;
            }
            catch
            {
                // ignore
            }
            return key;
        }

        private static bool IsZh()
        {
            try
            {
                var lan = Singleton<LAN>.self;
                return lan != null && lan.cur_language == LocalizedLanguage.zh_CN;
            }
            catch
            {
                return true;
            }
        }

        private static int TryReadPlayerNum(string path)
        {
            try
            {
                var ta = Resources.Load<TextAsset>(path);
                if (ta == null)
                    return 0;
                var meta = Singleton<BattleAndMapFileSystem>.self.ReadFileWithMeta_Asset(ta.text, read_meta_not_main: true);
                return meta?.GetKey_Int("player_num") ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
