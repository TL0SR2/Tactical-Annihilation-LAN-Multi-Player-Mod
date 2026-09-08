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
        public void Authored_loadout_is_not_needs_default()
        {
            var seat = LobbySeatLogic.MakeAiSeat(0, 0, 0, "zero", LobbySeatLogic.DefaultAiController);
            Assert.True(CoLoadoutRules.NeedsDefaultStamp(seat));
            Assert.False(CoLoadoutRules.HasAuthoredLoadout(seat));
            CoLoadoutRules.Stamp(seat, "free_sk", new[] { "ps_a", "ps_b" });
            Assert.True(CoLoadoutRules.HasAuthoredLoadout(seat));
            Assert.False(CoLoadoutRules.NeedsDefaultStamp(seat));
        }

        [Fact]
        public void SeatEdit_setLoadout_stamps_skill_and_ps()
        {
            var draft = new LobbyDraftDto
            {
                seats = new[]
                {
                    LobbySeatLogic.MakeHostSeat("h", "H", 0, 0, 0, "coA")
                }
            };
            var req = new SeatEditRequest
            {
                seatIndex = 0,
                peerId = "h",
                setCoId = true,
                coId = "zero",
                setLoadout = true,
                skillId = "sk_zero",
                psIds = new[] { "p1", "p2", "p3" }
            };
            Assert.True(LobbySeatLogic.TryApplyEdit(draft, req, true, "h", out var nack, out _));
            Assert.Equal(SeatEditNackCode.Generic, nack);
            Assert.Equal("zero", draft.seats[0].coId);
            Assert.Equal("sk_zero", draft.seats[0].skillId);
            Assert.Equal(new[] { "p1", "p2", "p3" }, draft.seats[0].psIds);
        }

        [Fact]
        public void SeatEdit_co_change_without_loadout_clears_stamp()
        {
            var draft = new LobbyDraftDto
            {
                seats = new[]
                {
                    LobbySeatLogic.MakeHostSeat("h", "H", 0, 0, 0, "coA")
                }
            };
            CoLoadoutRules.Stamp(draft.seats[0], "old", new[] { "x" });
            var req = new SeatEditRequest
            {
                seatIndex = 0,
                peerId = "h",
                setCoId = true,
                coId = "coB"
            };
            Assert.True(LobbySeatLogic.TryApplyEdit(draft, req, true, "h", out _, out _));
            Assert.Equal("coB", draft.seats[0].coId);
            Assert.Equal("", draft.seats[0].skillId);
            Assert.Empty(draft.seats[0].psIds);
        }
    }
}
