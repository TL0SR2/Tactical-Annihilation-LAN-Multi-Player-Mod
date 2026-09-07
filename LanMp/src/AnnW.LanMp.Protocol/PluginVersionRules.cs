namespace AnnW.LanMp.Protocol
{
    /// <summary>Hello/Welcome plugin version exchange (distinct from wire ProtocolVersion).</summary>
    public static class PluginVersionRules
    {
        public static string Normalize(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return "";
            return version.Trim();
        }

        public static bool IsCompatible(string hostVersion, string guestVersion)
        {
            var h = Normalize(hostVersion);
            var g = Normalize(guestVersion);
            if (h.Length == 0 || g.Length == 0)
                return false;
            return string.Equals(h, g, System.StringComparison.Ordinal);
        }

        public static string FormatMismatchMessage(string hostVersion, string guestVersion)
        {
            var h = Normalize(hostVersion);
            var g = Normalize(guestVersion);
            if (h.Length == 0) h = "(未知)";
            if (g.Length == 0) g = "(未知)";
            return "加入失败：双方联机插件版本不一致。主机：" + h + "，客机：" + g;
        }

        public static LobbyRejectPayload MakeMismatchReject(string hostVersion, string guestVersion)
        {
            var h = Normalize(hostVersion);
            var g = Normalize(guestVersion);
            return new LobbyRejectPayload
            {
                code = (int)LobbyRejectCode.PluginVersionMismatch,
                message = FormatMismatchMessage(h, g),
                hostPluginVersion = string.IsNullOrEmpty(h) ? "" : h,
                guestPluginVersion = string.IsNullOrEmpty(g) ? "" : g
            };
        }
    }
}
