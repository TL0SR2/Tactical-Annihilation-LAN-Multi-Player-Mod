using System.Collections.Generic;
using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class SkirmishEndRulesTests
    {
        [Fact]
        public void Surrender_with_allied_ai_alive_vs_enemy_does_not_settle()
        {
            // Host human defeated; same-fraction AI + enemy AI still fighting.
            var seats = new List<SkirmishEndRules.SeatAlive>
            {
                new SkirmishEndRules.SeatAlive(defeated: true, fraction: 0, neutral: false),
                new SkirmishEndRules.SeatAlive(defeated: false, fraction: 0, neutral: false),
                new SkirmishEndRules.SeatAlive(defeated: false, fraction: 1, neutral: false)
            };
            Assert.False(SkirmishEndRules.ShouldSettleMatch(seats, out var n));
            Assert.Equal(2, n);
        }

        [Fact]
        public void Last_opposing_faction_wiped_settles()
        {
            var seats = new List<SkirmishEndRules.SeatAlive>
            {
                new SkirmishEndRules.SeatAlive(false, 0, false),
                new SkirmishEndRules.SeatAlive(true, 1, false),
                new SkirmishEndRules.SeatAlive(true, 1, false)
            };
            Assert.True(SkirmishEndRules.ShouldSettleMatch(seats, out var n));
            Assert.Equal(1, n);
        }

        [Fact]
        public void All_defeated_settles()
        {
            var seats = new List<SkirmishEndRules.SeatAlive>
            {
                new SkirmishEndRules.SeatAlive(true, 0, false),
                new SkirmishEndRules.SeatAlive(true, 1, false)
            };
            Assert.True(SkirmishEndRules.ShouldSettleMatch(seats, out var n));
            Assert.Equal(0, n);
        }

        [Fact]
        public void Neutral_ignored()
        {
            var seats = new List<SkirmishEndRules.SeatAlive>
            {
                new SkirmishEndRules.SeatAlive(false, 0, false),
                new SkirmishEndRules.SeatAlive(false, 1, false),
                new SkirmishEndRules.SeatAlive(false, 2, true)
            };
            Assert.False(SkirmishEndRules.ShouldSettleMatch(seats, out var n));
            Assert.Equal(2, n);
        }
    }
}
