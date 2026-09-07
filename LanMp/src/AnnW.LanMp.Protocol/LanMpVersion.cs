namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Single source of truth for AnnW.LanMp release / Hello pluginVersion.
    /// Bump ONLY here — Pack-Release + tests sync README / reject mismatches.
    /// </summary>
    public static class LanMpVersion
    {
        /// <summary>SemVer displayed in UI, zip name, and Hello/Welcome exchange.</summary>
        public const string Current = "0.18.3";
    }
}
