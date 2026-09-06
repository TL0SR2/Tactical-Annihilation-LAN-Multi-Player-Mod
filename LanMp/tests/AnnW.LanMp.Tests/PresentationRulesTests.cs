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
        [InlineData(0f, false)]
        [InlineData(1f, true)]
        [InlineData(0.3f, true)]
        public void ShouldPresentAttachOnlyDoAction_cases(float moveDuration, bool expected)
        {
            Assert.Equal(expected, PresentationRules.ShouldPresentAttachOnlyDoAction(moveDuration));
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

        [Fact]
        public void UnitSnapDto_roundtrips_unitExp_in_json()
        {
            var dto = new ResultAttachmentDto
            {
                units = new[]
                {
                    new UnitSnapDto { unitId = 8, unitRank = 1, unitExp = 12.5f, unitExpReq = 40f, x = 0, y = 0 }
                }
            };
            var back = ResultAttachmentCodec.FromJson(ResultAttachmentCodec.ToJson(dto));
            var u = ResultAttachmentCodec.FindUnit(back, 8);
            Assert.Equal(1, u.unitRank);
            Assert.Equal(12.5f, u.unitExp, 3);
            Assert.Equal(40f, u.unitExpReq, 3);
            Assert.Equal(-1f, new UnitSnapDto().unitExp, 3);
            Assert.Equal(-1f, new UnitSnapDto().unitExpReq, 3);
        }

        [Fact]
        public void UnitSnapDto_roundtrips_cd_cding_in_json()
        {
            var dto = new ResultAttachmentDto
            {
                units = new[]
                {
                    new UnitSnapDto { unitId = 11, cd = 2, cding = true, x = 0, y = 1 }
                }
            };
            var json = ResultAttachmentCodec.ToJson(dto);
            var back = ResultAttachmentCodec.FromJson(json);
            var u = ResultAttachmentCodec.FindUnit(back, 11);
            Assert.Equal(2, u.cd);
            Assert.True(u.cding);
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
        [InlineData("UnitMoved", true, false, -1, true)]
        [InlineData("DoAction", false, true, -1, true)]
        [InlineData("DoAction", false, false, -1, false)]
        [InlineData("DoAction", false, true, 15, false)]
        [InlineData("EndTurn", true, true, -1, false)]
        public void IsUnitSpentForIntent_cases(string kind, bool moved, bool actioned, int cate, bool expected)
        {
            Assert.Equal(expected, IntentValidateRules.IsUnitSpentForIntent(kind, moved, actioned, cate));
        }

        [Fact]
        public void UnitSnapDto_roundtrips_shdPercent_in_json()
        {
            var dto = new ResultAttachmentDto
            {
                units = new[]
                {
                    new UnitSnapDto { unitId = 5, shdPercent = 0.85f, x = 2, y = -1 },
                    new UnitSnapDto { unitId = 6, shdPercent = 0f, x = 0, y = 0 }
                }
            };
            var json = ResultAttachmentCodec.ToJson(dto);
            var back = ResultAttachmentCodec.FromJson(json);
            Assert.Equal(0.85f, ResultAttachmentCodec.FindUnit(back, 5).shdPercent, 3);
            Assert.Equal(0f, ResultAttachmentCodec.FindUnit(back, 6).shdPercent, 3);
            Assert.Equal(-1f, new UnitSnapDto().shdPercent, 3);
        }

        [Fact]
        public void UnitSnapDto_roundtrips_factoryBpLeft_in_json()
        {
            var dto = new ResultAttachmentDto
            {
                units = new[]
                {
                    new UnitSnapDto { unitId = 3, factoryBpLeft = 40, hasTrainPos = true, trainPosX = 1, trainPosY = 2 }
                }
            };
            var json = ResultAttachmentCodec.ToJson(dto);
            var back = ResultAttachmentCodec.FromJson(json);
            var u = ResultAttachmentCodec.FindUnit(back, 3);
            Assert.Equal(40, u.factoryBpLeft);
            Assert.True(u.hasTrainPos);
            Assert.Equal(1, u.trainPosX);
        }

        [Fact]
        public void UnitSnapDto_roundtrips_transport_fields_in_json()
        {
            var dto = new ResultAttachmentDto
            {
                units = new[]
                {
                    new UnitSnapDto
                    {
                        unitId = 10,
                        transporting = true,
                        transporterUnitId = 20,
                        x = 3,
                        y = 4
                    },
                    new UnitSnapDto
                    {
                        unitId = 20,
                        cargoUnitIds = new[] { 10, 11 },
                        unloadBpLeft = 5,
                        unloadBpMaxBase = 20,
                        transportLoadedBp = 8,
                        transportMaxBpBase = 40,
                        x = 3,
                        y = 4
                    }
                },
                players = new[]
                {
                    new PlayerSnapDto
                    {
                        index = 0,
                        teleportCargoUnitIds = new[] { 30 },
                        teleportLoadedBp = 3,
                        teleportMaxBpBase = 50
                    }
                }
            };
            var back = ResultAttachmentCodec.FromJson(ResultAttachmentCodec.ToJson(dto));
            var cargo = ResultAttachmentCodec.FindUnit(back, 10);
            Assert.True(cargo.transporting);
            Assert.Equal(20, cargo.transporterUnitId);
            var carrier = ResultAttachmentCodec.FindUnit(back, 20);
            Assert.Equal(new[] { 10, 11 }, carrier.cargoUnitIds);
            Assert.Equal(5, carrier.unloadBpLeft);
            Assert.Equal(20, carrier.unloadBpMaxBase);
            Assert.Equal(8, carrier.transportLoadedBp);
            Assert.Equal(40, carrier.transportMaxBpBase);
            Assert.Equal(new[] { 30 }, back.players[0].teleportCargoUnitIds);
            Assert.Equal(3, back.players[0].teleportLoadedBp);
            Assert.Equal(50, back.players[0].teleportMaxBpBase);
            Assert.Equal(-1, new UnitSnapDto().transporterUnitId);
            Assert.Equal(-1, new UnitSnapDto().unloadBpMaxBase);
            Assert.Equal(-1, new UnitSnapDto().transportLoadedBp);
            Assert.Null(new UnitSnapDto().cargoUnitIds);
            Assert.Null(new PlayerSnapDto().teleportCargoUnitIds);
        }

        [Fact]
        public void AttachmentHasTransportPayload_legacy_omits_are_empty()
        {
            // Defaults must look like legacy omit for ApplyTransportState skip.
            var u = new UnitSnapDto();
            Assert.False(u.transporting);
            Assert.Equal(-1, u.transporterUnitId);
            Assert.Null(u.cargoUnitIds);
            Assert.Equal(-1, u.unloadBpLeft);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(3, true)]
        public void CanAcceptUndo_cases(int depth, bool expected)
        {
            Assert.Equal(expected, IntentValidateRules.CanAcceptUndo(depth));
        }

        [Theory]
        [InlineData(1, false, true)]
        [InlineData(1, true, false)]
        [InlineData(0, true, false)]
        [InlineData(0, false, false)]
        public void CanAcceptUndo_rejects_actioned_stack(int depth, bool actionedOnStack, bool expected)
        {
            Assert.Equal(expected, IntentValidateRules.CanAcceptUndo(depth, actionedOnStack));
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

        [Theory]
        [InlineData(false, true, true, 1, 1, false)]
        [InlineData(true, true, true, 1, 1, true)]
        [InlineData(true, true, true, 2, 1, false)]
        [InlineData(true, true, false, 1, 1, false)]
        public void UseLocalViewerFowForMoveZone_cases(
            bool inLan, bool armed, bool hasLocal, int unitFrac, int localFrac, bool expected)
        {
            Assert.Equal(expected, PresentationRules.UseLocalViewerFowForMoveZone(
                inLan, armed, hasLocal, unitFrac, localFrac));
        }

        [Theory]
        // solo AI processing → suppress
        [InlineData(false, false, false, false, true, true)]
        // solo Human → allow
        [InlineData(false, false, false, false, false, false)]
        // LAN AI processing → still allow hover threat
        [InlineData(true, true, false, false, true, false)]
        // LAN script → suppress
        [InlineData(true, true, true, false, false, true)]
        [InlineData(true, true, false, true, false, true)]
        public void ShouldSuppressHoverThreatOverlay_cases(
            bool inLan, bool armed, bool script, bool autoGuide, bool ai, bool expected)
        {
            Assert.Equal(expected, PresentationRules.ShouldSuppressHoverThreatOverlay(
                inLan, armed, script, autoGuide, ai));
        }

        [Theory]
        [InlineData(false, false, false, false, true, true)]
        [InlineData(false, false, false, false, false, false)]
        [InlineData(true, true, false, false, false, true)]
        [InlineData(true, true, true, false, true, false)]
        public void ShouldRenderHoverThreatOverlay_cases(
            bool inLan, bool armed, bool script, bool autoGuide, bool human, bool expected)
        {
            Assert.Equal(expected, PresentationRules.ShouldRenderHoverThreatOverlay(
                inLan, armed, script, autoGuide, human));
        }
    }
}
