namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Hello/Welcome game-assembly content fingerprint (P-Learn PL4 / A13).
    /// Distinct from <see cref="PluginVersionRules"/> (mod version) and wire <c>ProtocolVersion</c>.
    /// </summary>
    public static class GameContentFingerprintRules
    {
        public static string Normalize(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint))
                return "";
            return fingerprint.Trim().ToLowerInvariant();
        }

        public static bool IsCompatible(string hostFingerprint, string guestFingerprint)
        {
            var h = Normalize(hostFingerprint);
            var g = Normalize(guestFingerprint);
            // Both missing: allow (unit tests / hash unavailable on both peers).
            if (h.Length == 0 && g.Length == 0)
                return true;
            if (h.Length == 0 || g.Length == 0)
                return false;
            return string.Equals(h, g, System.StringComparison.Ordinal);
        }

        public static string FormatMismatchMessage(string hostFingerprint, string guestFingerprint)
        {
            var h = Normalize(hostFingerprint);
            var g = Normalize(guestFingerprint);
            if (h.Length == 0) h = "(未知)";
            if (g.Length == 0) g = "(未知)";
            return "加入失败：双方游戏内容指纹不一致（Assembly-CSharp）。主机：" +
                   Truncate(h) + "，客机：" + Truncate(g);
        }

        public static LobbyRejectPayload MakeMismatchReject(string hostFingerprint, string guestFingerprint)
        {
            var h = Normalize(hostFingerprint);
            var g = Normalize(guestFingerprint);
            return new LobbyRejectPayload
            {
                code = (int)LobbyRejectCode.ContentFingerprintMismatch,
                message = FormatMismatchMessage(h, g),
                hostContentFingerprint = string.IsNullOrEmpty(h) ? "" : h,
                guestContentFingerprint = string.IsNullOrEmpty(g) ? "" : g
            };
        }

        private static string Truncate(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length <= 16)
                return hex;
            return hex.Substring(0, 16) + "…";
        }
    }
}
