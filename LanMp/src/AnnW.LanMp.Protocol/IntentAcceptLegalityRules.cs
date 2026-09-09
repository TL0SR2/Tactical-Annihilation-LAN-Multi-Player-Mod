namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Pure policy for Host Accept range/FOW vs Guest Intent emit (ADR-001).
    ///
    /// Contract (vanilla UnitData.GetMoveZone IL):
    /// - PrepareMoveOp paints with (cull_friendly:false, no_cull_transport:true, cull_fow:true).
    /// - Host Accept uses the same first two flags but cull_fow:false — FOW CanWalk is skipped,
    ///   so Host zone ⊇ Guest UX paint when unit position matches. Geometry miss ⇒ hard Nack
    ///   (blocks desynced over-range Apply). FOW cannot false-Nack Host move Accept.
    /// - IsPosInSelectZone / GetSelectZone have no FOW — hard out-of-range for DoAction.
    /// - ActionData.CanDoAction FOW (TARGET_NOT_VISIBLE) is hard — vanilla needs FOWState.SEEN;
    ///   DETECTED alone must Nack (attack requires vision, not mere detection).
    /// - Host Accept CanDoAction uses unit-owner FOW (PreferUnitOwnerFow), not INV-VIEW —
    ///   this replaces the old soft TARGET_NOT_VISIBLE accept that masked viewer rewrite.
    /// - Guest UX CanDoAction/GetEffectZone uses INV-VIEW local viewer; never soft-pass
    ///   TARGET_NOT_VISIBLE. After board attach, RefreshLocalVision + combat UX cache flush
    ///   keeps Guest paint aligned with Host FOW (no lag soft-pass).
    /// - Guest must NOT re-run GetMoveZone/CanDoAction fail-fast (second chokepoint / INV-VIEW).
    /// </summary>
    public static class IntentAcceptLegalityRules
    {
        /// <summary>Matches TargetSel_Base.PrepareMoveOp GetMoveZone call (first two args).</summary>
        public const bool HostMoveCullFriendly = false;
        public const bool HostMoveNoCullTransport = true;

        /// <summary>
        /// Host Accept disables FOW cull so INV-VIEW / ally FOW cannot shrink authority zone.
        /// Guest UX keeps cull_fow:true — paints a subset of Host Accept geometry.
        /// </summary>
        public const bool HostMoveCullFow = false;

        /// <summary>Vanilla PrepareMoveOp third arg (for documentation / UX alignment checks).</summary>
        public const bool UxMoveCullFow = true;

        /// <summary>
        /// Host Accept DoAction FOW must use acting unit owner map (not INV-VIEW rewrite).
        /// </summary>
        public const bool HostDoActionUsesOwnerFow = true;

        /// <summary>
        /// Historical: TARGET_NOT_VISIBLE (==2) used to soft-accept for Host FOW lag.
        /// That let DETECTED-only attacks through; now always hard Nack (vanilla SEEN).
        /// </summary>
        public const int SoftAcceptCantDoTargetNotVisible = 2;

        /// <summary>No CanDoAction reasons are soft-accepted on Host.</summary>
        public static bool IsSoftAcceptCantDoReason(int reasonCantDo) => false;

        /// <summary>Guest gate may check ownership/spent only — never board geometry/FOW.</summary>
        public static bool GuestMayFailFastBoardLegality => false;
    }
}
