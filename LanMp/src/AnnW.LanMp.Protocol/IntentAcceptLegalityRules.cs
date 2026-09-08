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
    /// - ActionData.CanDoAction FOW check (TARGET_NOT_VISIBLE) is soft — Host FOW can lag UX.
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
        /// CanDoAction reason codes that Host soft-accepts (continue Apply).
        /// Only FOW visibility — not range, afford, factory BP, etc.
        /// Values mirror ANNW.REASON_CANTDO where TARGET_NOT_VISIBLE == 2.
        /// </summary>
        public const int SoftAcceptCantDoTargetNotVisible = 2;

        public static bool IsSoftAcceptCantDoReason(int reasonCantDo) =>
            reasonCantDo == SoftAcceptCantDoTargetNotVisible;

        /// <summary>Guest gate may check ownership/spent only — never board geometry/FOW.</summary>
        public static bool GuestMayFailFastBoardLegality => false;
    }
}
