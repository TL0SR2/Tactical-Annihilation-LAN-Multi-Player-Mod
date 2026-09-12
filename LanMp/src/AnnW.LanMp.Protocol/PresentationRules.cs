namespace AnnW.LanMp.Protocol
{
    using System;

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
        /// <summary>
        /// P-Learn PL2: Host may broadcast UnitMoved with geometry before Accept animation
        /// finishes so Guest/spectators start lerp in parallel (Intent≠optimistic mutate).
        /// </summary>
        public static bool ShouldBroadcastAheadOfHostAccept(string kind)
            => string.Equals(kind, "UnitMoved", StringComparison.Ordinal);

        /// <summary>
        /// P-Learn PL3: Equipment / build-with-move stay as UnitMoved then DoAction (EQ stash),
        /// not a separate CommandKind — presentation-ahead on the move leg is enough.
        /// </summary>
        public static bool IsCompositeMoveThenActionChain(string moveKind, string followUpKind)
        {
            if (!string.Equals(moveKind, "UnitMoved", StringComparison.Ordinal))
                return false;
            return string.Equals(followUpKind, "DoAction", StringComparison.Ordinal);
        }

        public static bool ShouldFastPresent(float moveDuration, string kind)
        {
            if (moveDuration > 0.001f)
                return false;
            return kind == "UnitMoved" || kind == "DoAction";
        }

        /// <summary>
        /// Guest attach-only DoAction should still play Event_DoActionAni when Host did not skip.
        /// </summary>
        public static bool ShouldPresentAttachOnlyDoAction(float moveDuration) =>
            !ShouldFastPresent(moveDuration, "DoAction");

        /// <summary>
        /// Host Accept DoAction should skip vanilla ExecuteAction anim so Command (with attach)
        /// broadcasts promptly; local Event_DoActionAni is kicked after Accept without blocking.
        /// </summary>
        public static bool ShouldSkipHostAcceptDoActionAnim(bool suppressNetworkEmit) =>
            suppressNetworkEmit;

        /// <summary>
        /// Mirror vanilla DoAction_MultiTarget / DoAction_Parallel shot spacing.
        /// </summary>
        public static float ResolveMultiShotInterval(int zoneCount, float settingsInterval)
        {
            if (settingsInterval > 0.001f)
                return settingsInterval;
            if (zoneCount > 30)
                return 0.05f;
            if (zoneCount > 10)
                return 0.1f;
            return 0.2f;
        }

        /// <summary>
        /// mul_tar / PARREL need one Event_DoActionAni per effect tile (index &gt; 0).
        /// </summary>
        public static bool ShouldLoopDoActionAni(int mulTar, bool trajIsParallel) =>
            mulTar > 0 || trajIsParallel;

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

        /// <summary>
        /// INV-VIEW: only rewrite GetMoveZone FOW for the local viewer's own/ally units.
        /// Enemy threat previews must keep the unit owner's FOW (otherwise ranges skew).
        /// </summary>
        public static bool UseLocalViewerFowForMoveZone(
            bool inLanBattle,
            bool gatesArmed,
            bool hasLocalHuman,
            int unitFraction,
            int localViewerFraction)
        {
            if (!inLanBattle || !gatesArmed || !hasLocalHuman)
                return false;
            return unitFraction == localViewerFraction;
        }

        /// <summary>
        /// Vanilla clears hover threat when control_state != Human (AI turns).
        /// LAN still wants enemy/ally hover threat while spectating remote/AI seats.
        /// Script/AutoGuide stay suppressed.
        /// </summary>
        public static bool ShouldSuppressHoverThreatOverlay(
            bool inLanBattle,
            bool gatesArmed,
            bool isScript,
            bool isAutoGuide,
            bool isAiProcessing)
        {
            if (isScript || isAutoGuide)
                return true;
            if (!inLanBattle || !gatesArmed)
                return isAiProcessing;
            return false;
        }

        public static bool ShouldRenderHoverThreatOverlay(
            bool inLanBattle,
            bool gatesArmed,
            bool isScript,
            bool isAutoGuide,
            bool isHumanControl)
        {
            if (isScript || isAutoGuide)
                return false;
            if (!inLanBattle || !gatesArmed)
                return isHumanControl;
            return true;
        }

        /// <summary>
        /// CO skill button (UI_Part_SkillPower): vanilla hides on AI turns via cur_player.is_ai,
        /// but LAN spectating a remote human still has is_ai=false — must hide locally.
        /// Own turn + has skill SD → show (energy-full glow is vanilla Render / IsEnergyMax).
        /// </summary>
        public static bool ShouldShowCoSkillButton(
            bool inLanBattle,
            bool gatesArmed,
            bool isSpectating,
            bool curPlayerIsAi,
            bool hasCoSkillSd)
        {
            if (!hasCoSkillSd)
                return false;
            if (curPlayerIsAi)
                return false;
            if (inLanBattle && gatesArmed && isSpectating)
                return false;
            return true;
        }
    }
}
