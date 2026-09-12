using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class SoloIsolationRulesTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Nested_fow_dirty_bodies_always_allowed(bool inLan, bool nested)
        {
            Assert.True(SoloIsolationRules.AllowNestedFowDirtyBody(inLan, nested));
        }

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        public void Local_view_rebind_only_on_outer_lan_fow(bool inLan, bool nested, bool expect)
        {
            Assert.Equal(expect, SoloIsolationRules.ShouldApplyLocalViewOnFowDirty(inLan, nested));
        }

        [Fact]
        public void Unbound_co_action_fow_local_ux_vs_accept()
        {
            const int caster = 1;
            const int spectator = 0;
            Assert.Equal(spectator,
                SoloIsolationRules.UnboundActionFowFraction(caster, spectator, hostAcceptOrPreferOwner: false));
            Assert.Equal(caster,
                SoloIsolationRules.UnboundActionFowFraction(caster, spectator, hostAcceptOrPreferOwner: true));
            Assert.Equal(caster, SoloIsolationRules.VanillaActionFowFraction(caster));
        }

        [Theory]
        [InlineData(true, true, false, false, true)]
        [InlineData(true, false, true, false, true)]
        [InlineData(true, false, false, true, true)]
        [InlineData(true, false, false, false, false)]
        [InlineData(false, true, true, true, false)]
        public void Co_select_overrides_only_in_lan_context(
            bool enabled, bool room, bool startAuth, bool seatSeed, bool expect)
        {
            Assert.Equal(expect,
                SoloIsolationRules.AllowLanCoSelectOverrides(enabled, room, startAuth, seatSeed));
        }
    }
}
