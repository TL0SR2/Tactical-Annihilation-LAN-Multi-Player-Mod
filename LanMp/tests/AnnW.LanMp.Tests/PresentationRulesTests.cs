using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class PresentationRulesTests
    {
        [Theory]
        [InlineData(false, true, true, false, 0, 0, true, true)]
        [InlineData(true, true, true, false, 1, 0, true, false)]
        [InlineData(true, true, true, false, 0, 0, true, true)]
        [InlineData(true, true, true, true, 1, 0, true, true)]
        public void ShouldRunVanillaSeatPresentation_cases(
            bool inLan, bool armed, bool playPhase, bool seatAi,
            int seatIdx, int localIdx, bool hasLocal, bool expected)
        {
            Assert.Equal(expected, PresentationRules.ShouldRunVanillaSeatPresentation(
                inLan, armed, playPhase, seatAi, seatIdx, localIdx, hasLocal));
        }

        [Theory]
        [InlineData(true, false, false, true)]
        [InlineData(false, true, true, true)]
        [InlineData(false, true, false, false)]
        public void IsHostSkippingPresentation_cases(bool skipAll, bool curAi, bool aiSkip, bool expected)
        {
            Assert.Equal(expected, PresentationRules.IsHostSkippingPresentation(skipAll, curAi, aiSkip));
        }

        [Theory]
        [InlineData(0f, "UnitMoved", true)]
        [InlineData(0f, "DoAction", true)]
        [InlineData(1f, "DoAction", false)]
        [InlineData(0.3f, "UnitMoved", false)]
        [InlineData(0f, "EndTurn", false)]
        public void ShouldFastPresent_cases(float moveDuration, string kind, bool expected)
        {
            Assert.Equal(expected, PresentationRules.ShouldFastPresent(moveDuration, kind));
        }

        [Theory]
        [InlineData(0f, 0.5f, 0.2f, 0f)]
        [InlineData(0.4f, 0.5f, 0.2f, 0.4f)]
        [InlineData(0f, 0f, 0.2f, 0f)]
        public void ResolveMoveDuration_cases(float cmdDur, float tplSpeed, float fallback, float expected)
        {
            Assert.Equal(expected, PresentationRules.ResolveMoveDuration(cmdDur, tplSpeed, fallback));
        }

        [Fact]
        public void UnitSnapDto_roundtrips_unitRank_in_json()
        {
            var dto = new ResultAttachmentDto
            {
                units = new[]
                {
                    new UnitSnapDto { unitId = 7, unitRank = 3, x = 1, y = 2 }
                }
            };
            var json = ResultAttachmentCodec.ToJson(dto);
            var back = ResultAttachmentCodec.FromJson(json);
            Assert.Equal(3, ResultAttachmentCodec.FindUnit(back, 7).unitRank);
        }

        [Theory]
        [InlineData("EndTurn", false, false, true, AttachmentApplyPolicy.ResourceApplyMode.AllPlayers)]
        [InlineData("DoAction", true, true, true, AttachmentApplyPolicy.ResourceApplyMode.LocalSeatOnly)]
        [InlineData("UnitMoved", true, true, true, AttachmentApplyPolicy.ResourceApplyMode.LocalSeatOnly)]
        [InlineData("Undo", true, true, true, AttachmentApplyPolicy.ResourceApplyMode.LocalSeatOnly)]
        [InlineData("DoAction", true, false, true, AttachmentApplyPolicy.ResourceApplyMode.None)]
        [InlineData("DoAction", true, true, false, AttachmentApplyPolicy.ResourceApplyMode.None)]
        [InlineData("DoAction", false, true, true, AttachmentApplyPolicy.ResourceApplyMode.None)]
        public void AttachmentApplyPolicy_resourceMode_cases(
            string kind, bool isGuest, bool localTurn, bool hasLocal, AttachmentApplyPolicy.ResourceApplyMode expected)
        {
            Assert.Equal(expected, AttachmentApplyPolicy.GetResourceApplyMode(kind, isGuest, localTurn, hasLocal));
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        public void ShouldGuestAttachOnlyDoAction_cases(bool isGuest, bool hasAttach, bool expected)
        {
            Assert.Equal(expected, AttachmentApplyPolicy.ShouldGuestAttachOnlyDoAction(isGuest, hasAttach));
        }

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(false, false, false)]
        public void ShouldGuestSkipDoActionWithoutAttach_cases(bool isGuest, bool hasAttach, bool expected)
        {
            Assert.Equal(expected, AttachmentApplyPolicy.ShouldGuestSkipDoActionWithoutAttach(isGuest, hasAttach));
        }

        [Theory]
        [InlineData("UnitMoved", true, false, true)]
        [InlineData("DoAction", false, true, true)]
        [InlineData("DoAction", false, false, false)]
        [InlineData("EndTurn", true, true, false)]
        public void IsUnitSpentForIntent_cases(string kind, bool moved, bool actioned, bool expected)
        {
            Assert.Equal(expected, IntentValidateRules.IsUnitSpentForIntent(kind, moved, actioned));
        }

        [Theory]
        [InlineData(false, true, true, 2, 1, true, false, true)]
        [InlineData(true, true, true, 1, 1, true, false, true)]
        [InlineData(true, true, true, 2, 1, true, true, true)]
        [InlineData(true, true, true, 2, 1, true, false, false)]
        [InlineData(true, true, true, 2, 1, false, true, true)]
        public void ShouldFollowUnitCamera_cases(
            bool inLan, bool armed, bool playPhase,
            int owner, int local, bool hasLocal, bool visible, bool expected)
        {
            Assert.Equal(expected, PresentationRules.ShouldFollowUnitCamera(
                inLan, armed, playPhase, owner, local, hasLocal, visible));
        }
    }
}
