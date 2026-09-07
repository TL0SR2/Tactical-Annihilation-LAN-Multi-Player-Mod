using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class CombatPresentationRulesTests
    {
        [Fact]
        public void TryDamageRatio_matches_vanilla_Hurt_formula()
        {
            Assert.True(CombatPresentationRules.TryDamageRatio(100f, 75f, 100f, out var ratio));
            Assert.Equal(0.25f, ratio, 3);
        }

        [Fact]
        public void TryDamageRatio_rejects_heal_or_zero()
        {
            Assert.False(CombatPresentationRules.TryDamageRatio(50f, 60f, 100f, out _));
            Assert.False(CombatPresentationRules.TryDamageRatio(50f, 50f, 100f, out _));
            Assert.False(CombatPresentationRules.TryDamageRatio(50f, 40f, 0f, out _));
        }

        [Fact]
        public void DeathVisualLeadSeconds_is_positive()
        {
            Assert.True(CombatPresentationRules.DeathVisualLeadSeconds > 0.05f);
            Assert.True(CombatPresentationRules.DeathVisualLeadSeconds < 2f);
        }
    }
}
