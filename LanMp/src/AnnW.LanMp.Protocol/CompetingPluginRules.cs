using System;

namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Detect other AnnW LAN competitors (P-Learn PL0 / A14).
    /// Pure string rules — filesystem / Chainloader probes live in the plugin host.
    /// </summary>
    public static class CompetingPluginRules
    {
        /// <summary>BepInEx plugin GUID substrings (OrdinalIgnoreCase).</summary>
        public static readonly string[] ConflictingGuidSubstrings =
        {
            "xingyistarry.mp",
            "xingyistarry"
        };

        /// <summary>Assembly / folder / file name hints (OrdinalIgnoreCase).</summary>
        public static readonly string[] ConflictingNameHints =
        {
            "XingyiStarry.Mp",
            "XingyiStarry.Mp.dll",
            "XingyiStarry.Mp.Protocol.dll"
        };

        public static bool IsConflictingGuid(string pluginGuid)
        {
            if (string.IsNullOrEmpty(pluginGuid))
                return false;
            foreach (var fragment in ConflictingGuidSubstrings)
            {
                if (pluginGuid.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public static bool IsConflictingPathOrFileName(string pathOrFileName)
        {
            if (string.IsNullOrEmpty(pathOrFileName))
                return false;
            foreach (var hint in ConflictingNameHints)
            {
                if (pathOrFileName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public static string FormatConflictMessage(string detected)
        {
            var label = string.IsNullOrWhiteSpace(detected) ? "未知联机插件" : detected.Trim();
            return "检测到其它联机插件（" + label +
                   "）。请勿同时加载 AnnW.LanMp 与 XingyiStarry.Mp；请移出其一后重启游戏。";
        }
    }
}
