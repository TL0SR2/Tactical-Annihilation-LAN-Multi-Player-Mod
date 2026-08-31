namespace AnnW.LanMp.Presentation
{
    /// <summary>Re-entrancy / defer flags for LAN presentation layer (no sync state).</summary>
    internal static class PresentationContext
    {
        /// <summary>Bypass PresentationPatches on TriggerTurnHint — call vanilla UI directly.</summary>
        internal static bool VanillaTurnHint;

        /// <summary>Guest EndTurn switched to local seat but ApplyQueue/animations still running.</summary>
        internal static bool ControlGrantPending;
    }
}
