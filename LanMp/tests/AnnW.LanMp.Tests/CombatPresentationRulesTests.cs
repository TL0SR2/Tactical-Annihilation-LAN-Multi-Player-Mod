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
        public void DeathVisualLead_small_unit_under_half_second()
        {
            Assert.Equal(
                CombatPresentationRules.DeathVisualLeadSecondsSmall,
                CombatPresentationRules.DeathVisualLeadSecondsForChassisSize(1));
            Assert.True(CombatPresentationRules.DeathVisualLeadSecondsSmall > 0.05f);
            Assert.True(CombatPresentationRules.DeathVisualLeadSecondsSmall < 0.5f);
        }

        [Fact]
        public void DeathVisualLead_large_building_covers_vanilla_explode_delay()
        {
            // Vanilla size>1: 3×0.3s + 0.1s before Event_DieExplode ≈ 1.0s.
            Assert.Equal(
                CombatPresentationRules.DeathVisualLeadSecondsLarge,
                CombatPresentationRules.DeathVisualLeadSecondsForChassisSize(2));
            Assert.True(CombatPresentationRules.DeathVisualLeadSecondsLarge >= 1.0f);
            Assert.True(CombatPresentationRules.DeathVisualLeadSecondsLarge < 2.5f);
        }
    }
}
