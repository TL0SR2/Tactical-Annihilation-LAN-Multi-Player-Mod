namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Which attachment domains to apply per command kind and peer role.
    /// Mid-turn full player[] on Host can be stale for non-active seats — never blast Guest
    /// with foreign-seat zeros. Guest local-human turn syncs economy from Host per Command.
    /// </summary>
    public static class AttachmentApplyPolicy
    {
        public enum ResourceApplyMode
        {
            None,
            LocalSeatOnly,
            AllPlayers
        }

        public static ResourceApplyMode GetResourceApplyMode(
            string commandKind,
            bool isGuest,
            bool isLocalHumanTurn,
            bool hasLocalHumanSeat)
        {
            // EndTurn + CastSkill: full board (skills may spend/grant metal/power for any seat).
            if (commandKind == "EndTurn" || commandKind == "CastSkill")
                return ResourceApplyMode.AllPlayers;
            if (isGuest && hasLocalHumanSeat && isLocalHumanTurn &&
                (commandKind == "DoAction" || commandKind == "UnitMoved" || commandKind == "Undo"))
                return ResourceApplyMode.LocalSeatOnly;
            return ResourceApplyMode.None;
        }

        public static bool ShouldApplyPlayerResources(ResourceApplyMode mode) =>
            mode != ResourceApplyMode.None;

        /// <summary>
        /// Guest must not re-run Host DoAction simulation when attachment is present —
        /// BUILD/TRAIN would double-spawn; ATTACK would re-roll RNG (ghost units).
        /// </summary>
        public static bool ShouldGuestAttachOnlyDoAction(bool isGuest, bool hasAttachment) =>
            isGuest && hasAttachment;

        /// <summary>
        /// Without Host attachment Guest must never ExecuteAction (local RNG / CreateUnit divergence).
        /// </summary>
        public static bool ShouldGuestSkipDoActionWithoutAttach(bool isGuest, bool hasAttachment) =>
            isGuest && !hasAttachment;
    }
}
