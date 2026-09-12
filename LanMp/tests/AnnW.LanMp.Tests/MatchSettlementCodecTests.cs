using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class MatchSettlementCodecTests
    {
        [Fact]
        public void Roundtrip_preserves_statics_and_snaps()
        {
            var att = new MatchSettlementAttachment
            {
                seats = new[]
                {
                    new SeatSettlementDto
                    {
                        playerIndex = 1,
                        statics = new PlayerBattleStaticsDto
                        {
                            totalMetalGet = 1200,
                            totalPowerGet = 800,
                            unitKilled = 5,
                            unitLost = 2,
                            bdDestroyed = 1
                        },
                        turnSnaps = new[]
                        {
                            new TurnSnapDto
                            {
                                turn = 3,
                                metal = 400,
                                power = 200,
                                unitCount = 2,
                                units = new[]
                                {
                                    new UnitTurnStateDto { unitId = 42, x = 1, y = 2 }
                                }
                            }
                        }
                    }
                }
            };

            var json = MatchSettlementCodec.ToJson(att);
            var back = MatchSettlementCodec.FromJson(json);
            Assert.True(MatchSettlementCodec.HasPayload(back));
            Assert.Single(back.seats);
            Assert.Equal(1, back.seats[0].playerIndex);
            Assert.Equal(1200, back.seats[0].statics.totalMetalGet);
            Assert.Equal(5, back.seats[0].statics.unitKilled);
            Assert.Single(back.seats[0].turnSnaps);
            Assert.Equal(3, back.seats[0].turnSnaps[0].turn);
            Assert.Equal(42, back.seats[0].turnSnaps[0].units[0].unitId);
            Assert.Equal(1, back.seats[0].turnSnaps[0].units[0].x);
        }

        [Fact]
        public void HasPayload_false_for_empty()
        {
            Assert.False(MatchSettlementCodec.HasPayload(null));
            Assert.False(MatchSettlementCodec.HasPayload(new MatchSettlementAttachment()));
            Assert.False(MatchSettlementCodec.HasPayload(new MatchSettlementAttachment
            {
                seats = new SeatSettlementDto[0]
            }));
            Assert.False(MatchSettlementCodec.HasPayload(new MatchSettlementAttachment
            {
                seats = new[] { new SeatSettlementDto { playerIndex = 0 } }
            }));
        }

        [Fact]
        public void MatchEndPayload_settlementJson_roundtrips_via_envelope_json()
        {
            var end = new MatchEndPayload
            {
                victory = true,
                battleId = "b1",
                settlementJson = MatchSettlementCodec.ToJson(new MatchSettlementAttachment
                {
                    seats = new[]
                    {
                        new SeatSettlementDto
                        {
                            playerIndex = 0,
                            statics = new PlayerBattleStaticsDto { totalMetalGet = 99 }
                        }
                    }
                })
            };
            var wire = JsonUtil.ToJson(end);
            var back = JsonUtil.FromJson<MatchEndPayload>(wire);
            var att = MatchSettlementCodec.FromJson(back.settlementJson);
            Assert.Equal(99, att.seats[0].statics.totalMetalGet);
        }
    }
}
