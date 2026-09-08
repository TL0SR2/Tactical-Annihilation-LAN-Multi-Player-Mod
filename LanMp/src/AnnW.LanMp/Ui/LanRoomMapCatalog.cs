using System;
using System.Collections.Generic;
using System.IO;
using ANNW;
using AnnW.LanMp.Protocol;
using BepInEx.Logging;
using UnityEngine;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Room map catalog: official skirmish Resources + local UserMaps/*.map.
    /// Stable draft id for homemade maps is <c>user:{relative}</c> (never Host absolute paths).
    /// </summary>
    internal static class LanRoomMapCatalog
    {
        public const string UserMapPrefix = "user:";

        public sealed class Entry
        {
            public string Id;
            public string DisplayName;
            public string ListLabel;
            public string ResourcesPath;
            /// <summary>Absolute path for homemade maps only.</summary>
            public string AbsolutePath;
            public int PlayerNum;
            public string SizeText;
            public string ThemeText;
            public bool IsUser;
            public bool IsSection;
        }

        public static bool IsUserMapId(string mapId) =>
            !string.IsNullOrEmpty(mapId) &&
            mapId.StartsWith(UserMapPrefix, StringComparison.OrdinalIgnoreCase);

        public static string ToUserMapId(string relativeUnderUserMaps)
        {
            if (string.IsNullOrEmpty(relativeUnderUserMaps))
                return "";
            var rel = relativeUnderUserMaps.Replace('\\', '/').TrimStart('/');
            return UserMapPrefix + rel;
        }

        public static string RelativeFromUserMapId(string mapId)
        {
            if (!IsUserMapId(mapId))
                return null;
            return mapId.Substring(UserMapPrefix.Length).Replace('\\', '/').TrimStart('/');
        }

        public static string GetUserMapsRoot(ManualLogSource log = null)
        {
            try
            {
                var pfs = Singleton<ProfileFileSystem>.self;
                if (pfs == null)
                    return null;
                if (string.IsNullOrEmpty(pfs.maps_path))
                    pfs.ForceInit();
                return pfs.maps_path;
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Room] UserMaps root failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Resolve <c>user:rel/path.map</c> to an absolute path under this peer's UserMaps.</summary>
        public static bool TryResolveUserMapAbsolute(string mapId, out string absolutePath, ManualLogSource log = null)
        {
            absolutePath = null;
            var rel = RelativeFromUserMapId(mapId);
            if (string.IsNullOrEmpty(rel))
                return false;
            var root = GetUserMapsRoot(log);
            if (string.IsNullOrEmpty(root))
                return false;
            // Reject path escape
            if (rel.IndexOf("..", StringComparison.Ordinal) >= 0)
                return false;
            absolutePath = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var absNorm = absolutePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!absNorm.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(absNorm, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                absolutePath = null;
                return false;
            }
            return true;
        }

        public static List<Entry> ListForRoom(ManualLogSource log, int maxBuiltin = 80, int maxUser = 120)
        {
            var list = new List<Entry>();
            list.Add(new Entry
            {
                Id = "__hdr_builtin__",
                ListLabel = "── 官方地图 ──",
                DisplayName = "官方地图",
                IsSection = true
            });
            list.AddRange(ListBuiltin(log, maxBuiltin));

            list.Add(new Entry
            {
                Id = "__hdr_user__",
                ListLabel = "── 自制地图 (UserMaps) ──",
                DisplayName = "自制地图",
                IsSection = true
            });
            var user = ListUser(log, maxUser);
            if (user.Count == 0)
            {
                var tip = "（暂无 .map — 放入 Documents\\My Games\\Tactical Annihilation\\…\\UserMaps）";
                var root = GetUserMapsRoot(log);
                if (!string.IsNullOrEmpty(root))
                    tip = "（暂无 .map — " + root + "）";
                list.Add(new Entry
                {
                    Id = "__empty_user__",
                    ListLabel = tip,
                    DisplayName = tip,
                    IsSection = true
                });
            }
            else
                list.AddRange(user);

            return list;
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
                    var path = "Skirmish/" + sd.name;
                    var display = ResolveLevelName(sd.name);
                    var playerNum = TryReadPlayerNum(path);
                    list.Add(new Entry
                    {
                        Id = sd.name,
                        DisplayName = display,
                        ListLabel = playerNum > 0 ? display + "(" + playerNum + ")" : display,
                        ResourcesPath = path,
                        PlayerNum = playerNum,
                        IsUser = false
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

        public static List<Entry> ListUser(ManualLogSource log, int max = 120)
        {
            var list = new List<Entry>();
            var root = GetUserMapsRoot(log);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                log?.LogInfo("[Room] UserMaps missing or empty root=" + (root ?? "(null)"));
                return list;
            }

            try
            {
                var files = Directory.GetFiles(root, "*.map", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                var rootFull = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (var file in files)
                {
                    var full = Path.GetFullPath(file);
                    if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var rel = full.Substring(rootFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var relSlash = rel.Replace('\\', '/');
                    var id = ToUserMapId(relSlash);
                    var display = Path.GetFileNameWithoutExtension(full);
                    var playerNum = TryReadPlayerNumLocal(full);
                    var folder = Path.GetDirectoryName(relSlash);
                    var label = playerNum > 0 ? display + "(" + playerNum + ")" : display;
                    if (!string.IsNullOrEmpty(folder))
                        label = folder.Replace('\\', '/') + "/" + label;
                    list.Add(new Entry
                    {
                        Id = id,
                        DisplayName = display,
                        ListLabel = label,
                        AbsolutePath = full,
                        PlayerNum = playerNum,
                        IsUser = true
                    });
                    if (list.Count >= max)
                        break;
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Room] ListUser maps failed: " + ex.Message);
            }

            return list;
        }

        public static bool TryLoadText(Entry entry, out string text, out string mapKey)
        {
            text = null;
            mapKey = entry?.ResourcesPath ?? entry?.Id;
            if (entry == null || entry.IsSection)
                return false;

            if (entry.IsUser || IsUserMapId(entry.Id))
            {
                mapKey = entry.Id;
                var path = entry.AbsolutePath;
                if (string.IsNullOrEmpty(path) && !TryResolveUserMapAbsolute(entry.Id, out path))
                    return false;
                if (!File.Exists(path))
                    return false;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch
                {
                    return false;
                }
                return !string.IsNullOrEmpty(text);
            }

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
                        var path = "Skirmish/" + sd.name;
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
                mapKey = string.IsNullOrEmpty(entry.ResourcesPath) ? ("Skirmish/" + entry.Id) : entry.ResourcesPath;
            if (Resources.Load<TextAsset>(mapKey) == null && ta != null)
                mapKey = "Skirmish/" + ta.name;
            return !string.IsNullOrEmpty(text);
        }

        /// <summary>Guest/Host: local file (or Resources) must exist; homemade maps must match draft hash.</summary>
        /// <summary>Guest: missing file or content hash ≠ draft → needs LobbyMapTransfer.</summary>
        public static bool NeedsUserMapSync(LobbyDraftDto draft, ManualLogSource log = null)
        {
            if (draft == null || !IsUserMapId(draft.mapId))
                return false;
            if (!TryResolveUserMapAbsolute(draft.mapId, out var abs, log) || !File.Exists(abs))
                return true;
            if (string.IsNullOrEmpty(draft.mapContentHash))
                return false;
            try
            {
                var hash = HashOf(File.ReadAllText(abs));
                return !string.Equals(hash, draft.mapContentHash, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        public static string PeekLocalUserMapHash(LobbyDraftDto draft, ManualLogSource log = null)
        {
            if (draft == null || !IsUserMapId(draft.mapId))
                return "";
            if (!TryResolveUserMapAbsolute(draft.mapId, out var abs, log) || !File.Exists(abs))
                return "";
            try
            {
                return HashOf(File.ReadAllText(abs));
            }
            catch
            {
                return "";
            }
        }

        public static LobbyMapTransferPayload TryBuildMapTransfer(string mapId, ManualLogSource log = null)
        {
            if (!IsUserMapId(mapId))
                return null;
            if (!TryResolveUserMapAbsolute(mapId, out var abs, log) || !File.Exists(abs))
            {
                log?.LogWarning("[Room] Host map transfer: file missing " + mapId);
                return null;
            }
            try
            {
                var text = File.ReadAllText(abs);
                if (string.IsNullOrEmpty(text))
                    return null;
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                return new LobbyMapTransferPayload
                {
                    mapId = mapId,
                    mapDisplayName = Path.GetFileNameWithoutExtension(abs),
                    mapContentHash = HashOf(text),
                    contentBase64 = Convert.ToBase64String(bytes)
                };
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Room] Host map transfer read failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Write/overwrite UserMaps file from Host payload. Same hash → no-op success.</summary>
        public static bool TryApplyMapTransfer(LobbyMapTransferPayload transfer, ManualLogSource log = null)
        {
            if (transfer == null || !IsUserMapId(transfer.mapId) || string.IsNullOrEmpty(transfer.contentBase64))
            {
                log?.LogWarning("[Room] Map transfer apply: bad payload");
                return false;
            }
            if (!TryResolveUserMapAbsolute(transfer.mapId, out var abs, log))
            {
                log?.LogWarning("[Room] Map transfer apply: bad mapId " + transfer.mapId);
                return false;
            }
            string text;
            try
            {
                var bytes = Convert.FromBase64String(transfer.contentBase64);
                text = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Room] Map transfer apply: bad base64: " + ex.Message);
                return false;
            }
            if (string.IsNullOrEmpty(text))
                return false;

            var expected = transfer.mapContentHash ?? "";
            var actual = HashOf(text);
            if (!string.IsNullOrEmpty(expected) &&
                !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                log?.LogWarning("[Room] Map transfer apply: payload hash mismatch");
                return false;
            }
            try
            {
                if (File.Exists(abs))
                {
                    var existing = HashOf(File.ReadAllText(abs));
                    if (string.Equals(existing, actual, StringComparison.OrdinalIgnoreCase))
                    {
                        log?.LogInfo("[Room] Map transfer skip write — already matches " + transfer.mapId);
                        return true;
                    }
                    log?.LogInfo("[Room] Map transfer overwrite (hash differs) " + abs);
                }
                else
                    log?.LogInfo("[Room] Map transfer write new " + abs);

                var dir = Path.GetDirectoryName(abs);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(abs, text);
                var written = HashOf(File.ReadAllText(abs));
                if (!string.Equals(written, actual, StringComparison.OrdinalIgnoreCase))
                {
                    log?.LogWarning("[Room] Map transfer verify failed after write");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Room] Map transfer write failed: " + ex.Message);
                return false;
            }
        }

        public static bool TryValidateLocalMap(LobbyDraftDto draft, out string error, ManualLogSource log = null)
        {
            error = null;
            if (draft == null || string.IsNullOrEmpty(draft.mapId))
            {
                error = "未选择地图";
                return false;
            }

            if (IsUserMapId(draft.mapId))
            {
                if (!TryResolveUserMapAbsolute(draft.mapId, out var abs, log) || !File.Exists(abs))
                {
                    error = "本地缺少自制图，等待房主同步：" + (RelativeFromUserMapId(draft.mapId) ?? draft.mapId);
                    return false;
                }
                try
                {
                    var text = File.ReadAllText(abs);
                    var hash = HashOf(text);
                    if (!string.IsNullOrEmpty(draft.mapContentHash) &&
                        !string.Equals(draft.mapContentHash, hash, StringComparison.OrdinalIgnoreCase))
                    {
                        error = "自制图与房主不一致，等待覆盖同步…";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    error = "读取自制图失败：" + ex.Message;
                    return false;
                }
                return true;
            }

            // Builtin / Resources path
            if (Resources.Load<TextAsset>(draft.mapId) != null)
                return true;
            if (draft.mapId.IndexOf('/') < 0 && Resources.Load<TextAsset>("Skirmish/" + draft.mapId) != null)
                return true;
            try
            {
                var sd = SDBase<SD_ANNW_SK_MAP>.Get(draft.mapId);
                if (sd != null && Resources.Load<TextAsset>("Skirmish/" + sd.name) != null)
                    return true;
            }
            catch
            {
                // ignore
            }
            if (File.Exists(draft.mapId))
                return true;

            error = "无法加载地图：" + (draft.mapDisplayName ?? draft.mapId);
            return false;
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

        private static int TryReadPlayerNumLocal(string absolutePath)
        {
            try
            {
                if (!File.Exists(absolutePath))
                    return 0;
                var text = File.ReadAllText(absolutePath);
                var meta = Singleton<BattleAndMapFileSystem>.self.ReadFileWithMeta_Asset(text, read_meta_not_main: true);
                return meta?.GetKey_Int("player_num") ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
