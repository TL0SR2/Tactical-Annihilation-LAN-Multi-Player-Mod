namespace AnnW.LanMp.Protocol
{
    /// <summary>Pure presentation gate rules (testable, no Unity).</summary>
    public static class PresentationRules
    {
        public static bool ShouldRunVanillaSeatPresentation(
            bool inLanBattle,
            bool gatesArmed,
            bool battlePlayPhase,
            bool seatIsAi,
            int seatIndex,
            int localHumanIndex,
            bool hasLocalHuman)
        {
            if (!inLanBattle || !gatesArmed || !battlePlayPhase)
                return true;
            if (seatIsAi || !hasLocalHuman)
                return true;
            return seatIndex == localHumanIndex;
        }

        public static bool IsHostSkippingPresentation(bool skippingAll, bool curIsAi, bool aiSkipping)
        {
            if (skippingAll)
                return true;
            if (curIsAi && aiSkipping)
                return true;
            return false;
        }

        /// <summary>
        /// Host stamps moveDuration=0 when skipping animations.
        /// DoAction normal broadcasts use moveDuration=1 so legacy 0 only means skip.
        /// UnitMoved normal always uses moveDuration &gt; 0.
        /// </summary>
        public static bool ShouldFastPresent(float moveDuration, string kind)
        {
            if (moveDuration > 0.001f)
                return false;
            return kind == "UnitMoved" || kind == "DoAction";
        }

        public static float ResolveMoveDuration(float cmdMoveDuration, float templateAniSpeed, float fallback = 0.2f)
        {
            if (ShouldFastPresent(cmdMoveDuration, "UnitMoved"))
                return 0f;
            if (cmdMoveDuration > 0.001f)
                return cmdMoveDuration;
            if (templateAniSpeed > 0.001f)
                return templateAniSpeed;
            return fallback;
        }

        /// <summary>
        /// Remote-watch camera must not track units the local viewer cannot see (FOW leak).
        /// Own units always follow; foreign / AI units only when visible.
        /// </summary>
        public static bool ShouldFollowUnitCamera(
            bool inLanBattle,
            bool gatesArmed,
            bool battlePlayPhase,
            int unitOwnerIndex,
            int localHumanIndex,
            bool hasLocalHuman,
            bool unitVisibleToLocalViewer)
        {
            if (!inLanBattle || !gatesArmed || !battlePlayPhase)
                return true;
            if (!hasLocalHuman)
                return true;
            if (unitOwnerIndex == localHumanIndex)
                return true;
            return unitVisibleToLocalViewer;
        }
    }
}
