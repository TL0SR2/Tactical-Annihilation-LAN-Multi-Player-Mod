namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// INV-SOLO: pure checks for "plugin must not change solo/campaign semantics".
    /// Transpilers may stay installed; runtime helpers must no-op / match vanilla when
    /// <c>inLanBattle</c> is false.
    /// </summary>
    public static class SoloIsolationRules
    {
        /// <summary>
        /// Vanilla <c>ActionData.CanDoAction</c> / <c>GetEffectZone</c> FOW source is
        /// <c>action.player.fraction</c> — never owner, never cur_player.
        /// </summary>
        public static int VanillaActionFowFraction(int actionPlayerFraction)
            => actionPlayerFraction;

        /// <summary>
        /// Nested <c>TriggerFOWDirty</c> bodies must always run (CO skills nest FOW during cast).
        /// The historical LAN freeze was from rebinding <c>last_human_player</c> mid-nested
        /// re-entry — not from running nested FOW itself. Never skip nested bodies.
        /// </summary>
        public static bool AllowNestedFowDirtyBody(bool inLanBattle, bool alreadyInsideOuterFowDirty)
        {
            _ = inLanBattle;
            _ = alreadyInsideOuterFowDirty;
            return true;
        }

        /// <summary>
        /// INV-VIEW: rebind local viewer only on the outermost FOWDirty entry while LAN-armed.
        /// Nested entries must not call <c>ApplyLocalViewBinding</c> again (hotseat fight / freeze).
        /// </summary>
        public static bool ShouldApplyLocalViewOnFowDirty(bool inLanBattle, bool alreadyInsideOuterFowDirty)
            => inLanBattle && !alreadyInsideOuterFowDirty;

        /// <summary>
        /// Unbound (CO) actions: FOW = caster <c>action.player</c>, never local spectator FOW.
        /// Host Accept Guest CastSkill / Guest presenting Host cast both need caster FOW.
        /// </summary>
        public static int UnboundActionFowFraction(int actionPlayerFraction, int localViewerFraction)
        {
            _ = localViewerFraction;
            return actionPlayerFraction;
        }

        /// <summary>
        /// CO select / free-PS unlock patches must not rewrite vanilla skirmish UI unless
        /// the LAN room / start path is active.
        /// </summary>
        public static bool AllowLanCoSelectOverrides(bool pluginEnabled, bool lanRoomOpen, bool startAuthorized, bool seatSeedActive)
        {
            if (!pluginEnabled)
                return false;
            return lanRoomOpen || startAuthorized || seatSeedActive;
        }
    }
}
