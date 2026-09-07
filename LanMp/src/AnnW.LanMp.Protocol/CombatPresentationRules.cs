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
        /// Seconds to yield after KickUnitDeathVisual so CoroutineObject death VFX can start
        /// before RemoveUnit/Dispose (Host Die starts anim then DescendUnit; Guest must not Dispose instantly).
        /// </summary>
        public const float DeathVisualLeadSeconds = 0.4f;
    }
}
