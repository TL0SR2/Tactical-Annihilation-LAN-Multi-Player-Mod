namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Baked skirmish layout numbers (canvas units) from a live InfoSkm dump.
    /// Source: BepInEx LogOutput 2026-08-30 — info_custom / dd_fow / Item_Option.
    /// Shared Floater CanvasScaler handles resolution; these are authored sizes.
    /// </summary>
    internal static class SkirmishUiMetrics
    {
        // InfoSkm: box_label_* 280×59, label TMP 255×55 @32; dd_* 477×65, caption @28
        public const float RuleDropH = 65f;
        public const float RuleLabelW = 255f;
        public const float RuleLabelFont = 32f;
        public const float RuleRowSpacing = 6f;
        public const float RuleCaptionFont = 28f;
        /// <summary>dd width − Label width on dd_fow (477 − 432).</summary>
        public const float CaptionPadRight = 45f;
        public const float CaptionPadLeft = 14f;

        // Arrow on dd_fow: 29×19, anchor right, anchoredPosition.x = −30
        public const float ArrowW = 29f;
        public const float ArrowH = 19f;
        public const float ArrowPadRight = 30f;

        // Item_Option 1407×63; btn_team/… dropdowns 65 tall, caption @28; CO txt @27
        public const float SeatDropH = 65f;
        public const float SeatRowH = 63f;
        public const float SeatCaptionFont = 28f;
        public const float CoBtnFont = 27f;
        public const float SeatsHeaderFont = 36f;

        // Map list: match seat-row chrome height (user request — same visual weight as lobby slots)
        public const float MapBtnH = SeatRowH;
        public const float MapBtnFont = 28f;
        public const float MapTitleFont = 42f;
        public const float MapDesFont = 32f;

        /// <summary>No-op: metrics are baked. Kept so call sites stay stable.</summary>
        public static void EnsureSampled() { }
    }
}
