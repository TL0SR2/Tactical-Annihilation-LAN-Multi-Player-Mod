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
        public void Soft_accept_only_target_not_visible()
        {
            Assert.True(IntentAcceptLegalityRules.IsSoftAcceptCantDoReason(2));
            Assert.False(IntentAcceptLegalityRules.IsSoftAcceptCantDoReason(0));
            Assert.False(IntentAcceptLegalityRules.IsSoftAcceptCantDoReason(5));
            Assert.False(IntentAcceptLegalityRules.IsSoftAcceptCantDoReason(15));
        }

        [Fact]
        public void Guest_must_not_fail_fast_board_legality()
        {
            Assert.False(IntentAcceptLegalityRules.GuestMayFailFastBoardLegality);
        }
    }
}
