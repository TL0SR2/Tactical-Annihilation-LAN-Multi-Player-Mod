using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class CoLoadoutRulesTests
    {
        [Fact]
        public void Stamp_and_clear_roundtrip()
        {
            var seat = LobbySeatLogic.MakeAiSeat(0, 0, 0, "coA", LobbySeatLogic.DefaultAiController);
            Assert.True(CoLoadoutRules.NeedsDefaultStamp(seat));
            CoLoadoutRules.Stamp(seat, "sk1", new[] { "ps1", "ps2" });
            Assert.True(CoLoadoutRules.HasSkillId(seat));
            Assert.True(CoLoadoutRules.HasAnyPs(seat));
            Assert.False(CoLoadoutRules.NeedsDefaultStamp(seat));
            CoLoadoutRules.Clear(seat);
            Assert.Equal("", seat.skillId);
            Assert.NotNull(seat.psIds);
            Assert.Empty(seat.psIds);
        }

        [Fact]
        public void BakeForStart_clears_loadout_when_co_none()
        {
            var draft = new LobbyDraftDto
            {
                seats = new[]
                {
                    LobbySeatLogic.MakeHostSeat("h", "H", 0, 0, 0, "__none__")
                }
            };
            CoLoadoutRules.Stamp(draft.seats[0], "stale", new[] { "x" });
            LobbySeatLogic.BakeForStart(draft, 1, null);
            Assert.Equal("", draft.seats[0].skillId);
            Assert.Empty(draft.seats[0].psIds);
        }

        [Fact]
        public void PlayerSnap_coEnergy_legacy_omit_default()
        {
            var p = new PlayerSnapDto();
            Assert.True(p.coEnergy < 0f);
            Assert.True(p.skillUsedTimes < 0);
            Assert.Null(p.effectObJson);
        }
    }
}
