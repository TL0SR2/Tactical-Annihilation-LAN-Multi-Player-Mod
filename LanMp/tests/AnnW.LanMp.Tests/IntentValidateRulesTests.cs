using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class IntentValidateRulesTests
    {
        [Fact]
        public void Rejects_wrong_turn_and_wrong_owner()
        {
            var intent = new IntentDto
            {
                intentId = "1",
                battleId = "b",
                turn = 2,
                playerIndex = 0,
                kind = "DoAction",
                netUnitId = 9
            };
            Assert.False(IntentValidateRules.TryValidateBasics(
                true, true, "b", intent, currentTurn: 3, currentPlayerIndex: 0, out var err));
            Assert.Equal("turn-mismatch", err);

            intent.turn = 3;
            Assert.True(IntentValidateRules.TryValidateBasics(
                true, true, "b", intent, 3, 0, out _));
            Assert.False(IntentValidateRules.TryValidateUnitOwner("DoAction", unitOwnerPlayerIndex: 1, actingPlayerIndex: 0, out err));
            Assert.Equal("unit-not-owned", err);
            Assert.True(IntentValidateRules.TryValidateUnitOwner("DoAction", 0, 0, out _));
        }

        [Fact]
        public void InputGate_blocks_foreign_unit_even_on_local_turn()
        {
            Assert.True(InputGateRules.ShouldBlockUnitControl(
                true, true, false, isLocalPlayersTurn: true,
                unitOwnerPlayerIndex: 1, localHumanSlotIndex: 0));
            Assert.False(InputGateRules.ShouldBlockUnitControl(
                true, true, false, true, 0, 0));
            Assert.True(InputGateRules.ShouldBlockUnitControl(
                true, true, false, isLocalPlayersTurn: false, 0, 0));
        }
    }
}
