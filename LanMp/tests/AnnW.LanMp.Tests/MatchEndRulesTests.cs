using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class MatchEndRulesTests
    {
        private static MatchEndPayload OppositeTeamsHostWins()
        {
            return new MatchEndPayload
            {
                // Host EndGame(true) — must NOT make Guest win.
                victory = true,
                victoryFlag = true,
                winnerFraction = 0,
                results = new[]
                {
                    new SeatMatchResultDto
                    {
                        playerIndex = 0,
                        defeated = false,
                        winner = true,
                        fraction = 0,
                        ownerPeerId = "host"
                    },
                    new SeatMatchResultDto
                    {
                        playerIndex = 1,
                        defeated = true,
                        winner = false,
                        fraction = 1,
                        ownerPeerId = "guest"
                    }
                }
            };
        }

        [Fact]
        public void Guest_on_opposite_team_loses_when_Host_wins()
        {
            var end = OppositeTeamsHostWins();
            Assert.False(MatchEndRules.ResolveLocalVictory(
                end, localSeatIndex: 1, localPeerId: "guest", localFraction: 1,
                allowHostVictoryFallback: false));
            Assert.True(MatchEndRules.ResolveLocalVictory(
                end, localSeatIndex: 0, localPeerId: "host", localFraction: 0,
                allowHostVictoryFallback: true));
        }

        [Fact]
        public void Guest_never_inherits_Host_victory_bool_without_seat_row()
        {
            var end = new MatchEndPayload
            {
                victory = true,
                winnerFraction = -1,
                results = null
            };
            Assert.False(MatchEndRules.ResolveLocalVictory(
                end, localSeatIndex: 1, localPeerId: "guest", localFraction: null,
                allowHostVictoryFallback: false));
            Assert.True(MatchEndRules.ResolveLocalVictory(
                end, localSeatIndex: 0, localPeerId: "host", localFraction: null,
                allowHostVictoryFallback: true));
        }

        [Fact]
        public void Allied_guest_wins_with_host_via_fraction()
        {
            var end = new MatchEndPayload
            {
                victory = true,
                winnerFraction = 0,
                results = new[]
                {
                    new SeatMatchResultDto
                    {
                        playerIndex = 0, defeated = false, winner = true, fraction = 0, ownerPeerId = "host"
                    },
                    new SeatMatchResultDto
                    {
                        playerIndex = 2, defeated = false, winner = true, fraction = 0, ownerPeerId = "guest"
                    },
                    new SeatMatchResultDto
                    {
                        playerIndex = 1, defeated = true, winner = false, fraction = 1, ownerPeerId = "ai"
                    }
                }
            };
            Assert.True(MatchEndRules.ResolveLocalVictory(
                end, localSeatIndex: 2, localPeerId: "guest", localFraction: 0,
                allowHostVictoryFallback: false));
        }

        [Fact]
        public void Defeated_spectator_on_winning_faction_still_wins()
        {
            var rows = new[]
            {
                new SeatMatchResultDto
                {
                    playerIndex = 0, defeated = false, winner = false, fraction = 0, ownerPeerId = "host"
                },
                // Guest wiped mid-match, same faction as Host — spectates to MatchEnd.
                new SeatMatchResultDto
                {
                    playerIndex = 2, defeated = true, winner = false, fraction = 0, ownerPeerId = "guest"
                },
                new SeatMatchResultDto
                {
                    playerIndex = 1, defeated = true, winner = false, fraction = 1, ownerPeerId = "enemy"
                }
            };
            var winnerFrac = MatchEndRules.AssignFactionWinners(rows);
            Assert.Equal(0, winnerFrac);
            Assert.True(rows[0].winner);
            Assert.True(rows[1].winner); // defeated but faction won
            Assert.False(rows[2].winner);

            var end = new MatchEndPayload
            {
                victory = true,
                winnerFraction = winnerFrac,
                results = rows
            };
            Assert.True(MatchEndRules.ResolveLocalVictory(
                end, localSeatIndex: 2, localPeerId: "guest", localFraction: 0,
                allowHostVictoryFallback: false));
        }

        [Fact]
        public void Match_by_ownerPeerId_when_seat_index_missing()
        {
            var end = OppositeTeamsHostWins();
            Assert.False(MatchEndRules.ResolveLocalVictory(
                end, localSeatIndex: 99, localPeerId: "guest", localFraction: null,
                allowHostVictoryFallback: false));
        }
    }
}
