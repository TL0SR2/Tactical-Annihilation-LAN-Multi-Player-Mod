using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class InputGateRulesTests
    {
        [Theory]
        [InlineData(false, true, 0, 0, true)]
        [InlineData(true, false, 0, 0, true)]
        [InlineData(true, true, 0, 0, true)]
        [InlineData(true, true, 1, 0, false)]
        public void IsLocalPlayersTurn_cases(bool inBattle, bool armed, int cur, int localSlot, bool expected)
        {
            Assert.Equal(expected, InputGateRules.IsLocalPlayersTurn(inBattle, armed, cur, localSlot));
        }

        [Theory]
        [InlineData(false, true, false, false, false)]
        [InlineData(true, true, false, false, true)]
        [InlineData(true, true, true, false, true)]
        [InlineData(true, true, false, true, false)]
        public void ShouldBlockLocalInput_cases(bool inBattle, bool armed, bool applyingRemote, bool localTurn, bool expected)
        {
            Assert.Equal(expected, InputGateRules.ShouldBlockLocalInput(inBattle, armed, applyingRemote, localTurn));
        }

        [Fact]
        public void MayAuthorizeStart_requires_host_ready_gates()
        {
            Assert.False(InputGateRules.MayAuthorizeStart(false, true, true));
            Assert.False(InputGateRules.MayAuthorizeStart(true, false, true));
            Assert.False(InputGateRules.MayAuthorizeStart(true, true, false));
            Assert.True(InputGateRules.MayAuthorizeStart(true, true, true));
        }
    }

    public class HashAndWireTests
    {
        [Fact]
        public void StableHash16_is_deterministic()
        {
            var a = HashUtil.StableHash16("skirmish-map-body");
            var b = HashUtil.StableHash16("skirmish-map-body");
            var c = HashUtil.StableHash16("other");
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.Equal(16, a.Length);
        }

        [Fact]
        public void TryParseEndpoint_ok_and_fail()
        {
            Assert.True(WireCodec.TryParseEndpoint("127.0.0.1:24555", out var host, out var port));
            Assert.Equal("127.0.0.1", host);
            Assert.Equal(24555, port);
            Assert.False(WireCodec.TryParseEndpoint("bad", out _, out _));
            Assert.False(WireCodec.TryParseEndpoint(":99", out _, out _));
        }

        [Fact]
        public void Envelope_frame_roundtrip()
        {
            var env = new Envelope
            {
                Type = MsgType.LobbyDraft,
                BattleId = "abc",
                Seq = 7,
                PayloadJson = "{\"mapId\":\"m1\"}"
            };
            var frame = WireCodec.EncodeFrame(env);
            Assert.True(WireCodec.TryDecodeFrame(frame, 0, frame.Length, out var decoded, out var consumed));
            Assert.Equal(frame.Length, consumed);
            Assert.Equal(MsgType.LobbyDraft, decoded.Type);
            Assert.Equal("abc", decoded.BattleId);
            Assert.Equal((uint)7, decoded.Seq);
            Assert.Contains("m1", decoded.PayloadJson);
        }
    }
}
