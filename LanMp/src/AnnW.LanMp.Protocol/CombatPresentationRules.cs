namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Pure helpers for Guest attach-only combat presentation (ADR-003 R4).
    /// State stays Host-attached; these only decide what visuals to fire.
    /// </summary>
    public static class CombatPresentationRules
    {
        /// <summary>
        /// Vanilla Hurt → ShowAsDamage(damage / hp_max) with ToString("P0").
        /// </summary>
        public static bool TryDamageRatio(float oldHp, float newHp, float hpMax, out float ratio)
        {
            ratio = 0f;
            var dealt = oldHp - newHp;
            if (dealt <= 0.01f || hpMax < 0.01f)
                return false;
            ratio = dealt / hpMax;
            return ratio > 0.0001f;
        }

        /// <summary>
        /// size==1: Event_DieExplode runs on first coroutine frame (before first yield).
        /// Keep a short lead so DescendUnit can start before Guest Dispose.
        /// </summary>
        public const float DeathVisualLeadSecondsSmall = 0.35f;

        /// <summary>
        /// size&gt;1 COMBAT death: vanilla yields 3×0.3s + 0.1s before Event_DieExplode
        /// (debris / building break-apart). Guest must not RemoveUnit before that.
        /// </summary>
        public const float DeathVisualLeadSecondsLarge = 1.25f;

        /// <summary>Legacy alias — small-unit lead (tests / callers without chassis size).</summary>
        public const float DeathVisualLeadSeconds = DeathVisualLeadSecondsSmall;

        /// <summary>
        /// Seconds to yield after KickUnitDeathVisual before RemoveUnit/Dispose.
        /// Matches vanilla proc_UnitDeathAnimation timing by chassis footprint.
        /// </summary>
        public static float DeathVisualLeadSecondsForChassisSize(int chassisSize) =>
            chassisSize > 1 ? DeathVisualLeadSecondsLarge : DeathVisualLeadSecondsSmall;
    }
}
