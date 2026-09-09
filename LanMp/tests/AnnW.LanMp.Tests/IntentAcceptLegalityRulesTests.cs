using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class IntentAcceptLegalityRulesTests
    {
        [Fact]
        public void Host_move_zone_args_are_geometry_without_fow_cull()
        {
            Assert.False(IntentAcceptLegalityRules.HostMoveCullFriendly);
            Assert.True(IntentAcceptLegalityRules.HostMoveNoCullTransport);
            Assert.False(IntentAcceptLegalityRules.HostMoveCullFow);
            Assert.True(IntentAcceptLegalityRules.UxMoveCullFow);
            // Host Accept zone ⊇ UX paint when boards match (UX culls FOW, Host does not).
            Assert.NotEqual(
                IntentAcceptLegalityRules.HostMoveCullFow,
                IntentAcceptLegalityRules.UxMoveCullFow);
        }

        [Fact]
        public void No_soft_accept_cant_do_reasons()
        {
            // TARGET_NOT_VISIBLE (2) must hard-Nack — DETECTED ≠ SEEN for attack.
            Assert.False(IntentAcceptLegalityRules.IsSoftAcceptCantDoReason(2));
            Assert.False(IntentAcceptLegalityRules.IsSoftAcceptCantDoReason(0));
            Assert.False(IntentAcceptLegalityRules.IsSoftAcceptCantDoReason(5));
            Assert.False(IntentAcceptLegalityRules.IsSoftAcceptCantDoReason(15));
        }

        [Fact]
        public void Host_do_action_uses_owner_fow_not_viewer_soft_pass()
        {
            Assert.True(IntentAcceptLegalityRules.HostDoActionUsesOwnerFow);
            Assert.False(IntentAcceptLegalityRules.IsSoftAcceptCantDoReason(
                IntentAcceptLegalityRules.SoftAcceptCantDoTargetNotVisible));
        }

        [Fact]
        public void Guest_must_not_fail_fast_board_legality()
        {
            Assert.False(IntentAcceptLegalityRules.GuestMayFailFastBoardLegality);
        }
    }
}
