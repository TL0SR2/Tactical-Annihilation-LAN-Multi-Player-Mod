using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class ActionExtrasCodecTests
    {
        [Fact]
        public void TrainTemplate_roundtrip_legacy_bare()
        {
            var e = ActionExtrasCodec.FromTrainTemplate("SOME_UNIT");
            Assert.True(ActionExtrasCodec.TryGetTrainTemplateId(e, out var id));
            Assert.Equal("SOME_UNIT", id);
            Assert.False(ActionExtrasCodec.TryGetUnloadUnitId(e, out _));
        }

        [Fact]
        public void TrainTemplate_explicit_prefix()
        {
            Assert.True(ActionExtrasCodec.TryGetTrainTemplateId("t:ABC", out var id));
            Assert.Equal("ABC", id);
        }

        [Fact]
        public void UnloadUnit_roundtrip()
        {
            var e = ActionExtrasCodec.FromUnloadUnitId(42);
            Assert.Equal("u:42", e);
            Assert.True(ActionExtrasCodec.TryGetUnloadUnitId(e, out var id));
            Assert.Equal(42, id);
            Assert.False(ActionExtrasCodec.TryGetTrainTemplateId(e, out _));
        }

        [Fact]
        public void Needs_flags_match_vanilla_cates()
        {
            Assert.True(ActionExtrasCodec.NeedsTrainTemplate(2));  // BUILD
            Assert.True(ActionExtrasCodec.NeedsTrainTemplate(4));  // TRAIN
            Assert.False(ActionExtrasCodec.NeedsTrainTemplate(1)); // ATTACK
            Assert.True(ActionExtrasCodec.NeedsUnloadUnit(9));     // UNLOAD_SINGLE
            Assert.False(ActionExtrasCodec.NeedsUnloadUnit(8));    // UNLOAD auto
        }
    }
}
