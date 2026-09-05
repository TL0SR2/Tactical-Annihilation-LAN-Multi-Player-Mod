using System;
using System.Threading;
using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class CommandProtocolTests
    {
        [Fact]
        public void CommandDto_json_roundtrip_preserves_fields()
        {
            var cmd = new CommandDto
            {
                cmdId = "c1",
                sourceIntentId = "i1",
                battleId = "b1",
                turn = 3,
                playerIndex = 1,
                kind = "DoAction",
                netUnitId = 42,
                actionCate = 7,
                targetX = 5,
                targetY = -2,
                fromX = 4,
                fromY = -2,
                extrasJson = "{}",
                resultAttachmentJson = "{\"dmg\":3}",
                hasTarget = true
            };

            var json = JsonUtil.ToJson(cmd);
            var back = JsonUtil.FromJson<CommandDto>(json);
            Assert.Equal(cmd.cmdId, back.cmdId);
            Assert.Equal(cmd.kind, back.kind);
            Assert.Equal(cmd.netUnitId, back.netUnitId);
            Assert.Equal(cmd.actionCate, back.actionCate);
            Assert.Equal(cmd.targetX, back.targetX);
            Assert.Equal(cmd.targetY, back.targetY);
            Assert.Equal(cmd.resultAttachmentJson, back.resultAttachmentJson);
            Assert.True(back.hasTarget);
        }

        [Fact]
        public void IntentDto_null_target_roundtrips_hasTarget_false()
        {
            var intent = new IntentDto
            {
                intentId = "i",
                kind = "DoAction",
                actionCate = 3,
                targetX = 0,
                targetY = 0,
                hasTarget = false,
                extrasJson = "unit_scout"
            };
            var back = JsonUtil.FromJson<IntentDto>(JsonUtil.ToJson(intent));
            Assert.False(back.hasTarget);
            Assert.Equal(0, back.targetX);
            Assert.Equal("unit_scout", back.extrasJson);
        }

        [Fact]
        public void EndTurn_command_roundtrips_nextPlayer_fields()
        {
            var cmd = new CommandDto
            {
                kind = "EndTurn",
                endedPlayerIndex = 1,
                turnBefore = 3,
                nextPlayerIndex = 2,
                turnsAfter = 3,
                endTurnReason = "turn-started",
                hasTarget = false
            };
            var back = JsonUtil.FromJson<CommandDto>(JsonUtil.ToJson(cmd));
            Assert.Equal(1, back.endedPlayerIndex);
            Assert.Equal(2, back.nextPlayerIndex);
            Assert.Equal(3, back.turnsAfter);
            Assert.Equal("turn-started", back.endTurnReason);
        }

        [Fact]
        public void Host_broadcasts_command_guest_receives_via_loopback()
        {
            var port = TestNetUtil.AllocateLoopbackPort();
            var hostLog = new CollectingLanLogger();
            var guestLog = new CollectingLanLogger();
            var hostNet = new NetSession(hostLog, "h");
            var guestNet = new NetSession(guestLog, "g");

            CommandDto received = null;
            guestNet.Subscribe(env =>
            {
                if (env.Type == MsgType.Command)
                    received = JsonUtil.FromJson<CommandDto>(env.PayloadJson);
            });

            try
            {
                hostNet.StartHost(port);
                Thread.Sleep(80);
                guestNet.ConnectGuest("127.0.0.1:" + port);

                Assert.True(Wait(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return hostNet.IsConnected && guestNet.IsConnected;
                }));

                hostNet.Send(new Envelope
                {
                    Type = MsgType.Command,
                    BattleId = "b",
                    PayloadJson = JsonUtil.ToJson(new CommandDto
                    {
                        cmdId = "x",
                        kind = "EndTurn",
                        turn = 2,
                        playerIndex = 0
                    })
                });

                Assert.True(Wait(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return received != null;
                }));

                Assert.Equal("EndTurn", received.kind);
                Assert.Equal(2, received.turn);
            }
            finally
            {
                guestNet.Disconnect("t");
                hostNet.Disconnect("t");
            }
        }

        [Fact]
        public void StateHashDto_roundtrip()
        {
            var dto = new StateHashDto { battleId = "b", turn = 9, playerIndex = 1, hash = "abcdef0123456789" };
            var back = JsonUtil.FromJson<StateHashDto>(JsonUtil.ToJson(dto));
            Assert.Equal(dto.hash, back.hash);
            Assert.Equal(dto.turn, back.turn);
        }

        [Fact]
        public void ResultAttachment_codec_roundtrip_and_find()
        {
            var dto = new ResultAttachmentDto
            {
                turn = 4,
                coIndex = 1,
                units = new[]
                {
                    new UnitSnapDto { unitId = 42, ownerIndex = 0, x = 3, y = -1, hpCur = 12.5f, dead = false }
                },
                players = new[]
                {
                    new PlayerSnapDto { index = 0, metal = 100, power = 50, defeated = false }
                }
            };

            Assert.True(ResultAttachmentCodec.HasPayload(dto));
            var json = ResultAttachmentCodec.ToJson(dto);
            var back = ResultAttachmentCodec.FromJson(json);
            Assert.Equal(4, back.turn);
            Assert.Equal(1, back.coIndex);
            Assert.Equal(42, ResultAttachmentCodec.FindUnit(back, 42).unitId);
            Assert.Equal(12.5f, ResultAttachmentCodec.FindUnit(back, 42).hpCur);
            Assert.Equal(100, back.players[0].metal);
            Assert.Null(ResultAttachmentCodec.FromJson(null));
            Assert.False(ResultAttachmentCodec.HasPayload(null));
        }

        [Fact]
        public void ResultAttachment_wrecks_roundtrip_and_hasPayload()
        {
            var onlyWrecks = new ResultAttachmentDto
            {
                turn = 1,
                coIndex = 0,
                wrecks = new[]
                {
                    new WreckSnapDto { x = 2, y = -3, amount = 150 }
                }
            };
            Assert.True(ResultAttachmentCodec.HasPayload(onlyWrecks));
            var back = ResultAttachmentCodec.FromJson(ResultAttachmentCodec.ToJson(onlyWrecks));
            Assert.Single(back.wrecks);
            Assert.Equal(2, back.wrecks[0].x);
            Assert.Equal(-3, back.wrecks[0].y);
            Assert.Equal(150, back.wrecks[0].amount);

            // Empty wrecks array is still payload (clear-all on Guest).
            Assert.True(ResultAttachmentCodec.HasPayload(new ResultAttachmentDto
            {
                wrecks = new WreckSnapDto[0]
            }));
            // Legacy omit
            Assert.False(ResultAttachmentCodec.HasPayload(new ResultAttachmentDto()));
        }

        [Fact]
        public void StateSnapshotDto_roundtrip_embeds_attachment()
        {
            var snap = new StateSnapshotDto
            {
                battleId = "b1",
                turn = 2,
                playerIndex = 0,
                hashAfter = "deadbeefcafebabe",
                attachment = new ResultAttachmentDto
                {
                    turn = 2,
                    coIndex = 0,
                    units = new[] { new UnitSnapDto { unitId = 1, x = 0, y = 0, hpCur = 1f } },
                    players = new PlayerSnapDto[0]
                }
            };
            var back = JsonUtil.FromJson<StateSnapshotDto>(JsonUtil.ToJson(snap));
            Assert.Equal(snap.hashAfter, back.hashAfter);
            Assert.True(ResultAttachmentCodec.HasPayload(back.attachment));
            Assert.Equal(1, back.attachment.units[0].unitId);
        }

        [Fact]
        public void SnapshotRequest_loopback_host_guest()
        {
            var port = 29000 + new Random().Next(1000, 2000);
            var hostNet = new NetSession(new CollectingLanLogger(), "h");
            var guestNet = new NetSession(new CollectingLanLogger(), "g");
            SnapshotRequestDto gotReq = null;
            StateSnapshotDto gotSnap = null;

            hostNet.Subscribe(env =>
            {
                if (env.Type != MsgType.SnapshotRequest)
                    return;
                gotReq = JsonUtil.FromJson<SnapshotRequestDto>(env.PayloadJson);
                hostNet.Send(new Envelope
                {
                    Type = MsgType.StateSnapshot,
                    BattleId = gotReq.battleId,
                    PayloadJson = JsonUtil.ToJson(new StateSnapshotDto
                    {
                        battleId = gotReq.battleId,
                        turn = gotReq.turn,
                        hashAfter = "aabbccddeeff0011",
                        attachment = new ResultAttachmentDto
                        {
                            turn = gotReq.turn,
                            units = new[] { new UnitSnapDto { unitId = 9, hpCur = 3f } },
                            players = new PlayerSnapDto[0]
                        }
                    })
                });
            });
            guestNet.Subscribe(env =>
            {
                if (env.Type == MsgType.StateSnapshot)
                    gotSnap = JsonUtil.FromJson<StateSnapshotDto>(env.PayloadJson);
            });

            try
            {
                hostNet.StartHost(port);
                Thread.Sleep(80);
                guestNet.ConnectGuest("127.0.0.1:" + port);
                Assert.True(Wait(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return hostNet.IsConnected && guestNet.IsConnected;
                }));

                guestNet.Send(new Envelope
                {
                    Type = MsgType.SnapshotRequest,
                    BattleId = "b",
                    PayloadJson = JsonUtil.ToJson(new SnapshotRequestDto { battleId = "b", turn = 5, reason = "test" })
                });

                Assert.True(Wait(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return gotSnap != null;
                }));

                Assert.NotNull(gotReq);
                Assert.Equal(5, gotReq.turn);
                Assert.Equal("aabbccddeeff0011", gotSnap.hashAfter);
                Assert.Equal(9, gotSnap.attachment.units[0].unitId);
            }
            finally
            {
                guestNet.Disconnect("t");
                hostNet.Disconnect("t");
            }
        }

        private static bool Wait(Func<bool> pred, int ms = 3000)
        {
            var t0 = Environment.TickCount;
            while (Environment.TickCount - t0 < ms)
            {
                if (pred())
                    return true;
                Thread.Sleep(20);
            }
            return pred();
        }
    }
}
