using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class CoEnergyRulesTests
    {
        [Theory]
        [InlineData(0, 80f)]
        [InlineData(1, 125f)] // 1*(5+40)+80 = 125
        [InlineData(2, 180f)] // 2*(10+40)+80 = 180
        [InlineData(10, 500f)] // clamped
        public void ComputeEnergyMax_matches_vanilla_formula(int used, float expected)
        {
            Assert.Equal(expected, CoEnergyRules.ComputeEnergyMax(used));
        }

        [Fact]
        public void IsEnergyFull_threshold()
        {
            Assert.False(CoEnergyRules.IsEnergyFull(79f, 80f));
            Assert.True(CoEnergyRules.IsEnergyFull(80f, 80f));
            Assert.True(CoEnergyRules.IsEnergyFull(80.5f, 80f));
            Assert.False(CoEnergyRules.IsEnergyFull(10f, 0f));
        }
    }
}
