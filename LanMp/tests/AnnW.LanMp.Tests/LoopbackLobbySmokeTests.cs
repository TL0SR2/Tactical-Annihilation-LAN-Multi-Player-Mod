using System;
using System.Threading;
using AnnW.LanMp.Protocol;
using Xunit;

namespace AnnW.LanMp.Tests
{
    public class LobbySeatLogicTests
    {
        [Fact]
        public void Promote_standby_then_seat_and_release()
        {
            var draft = new LobbyDraftDto
            {
                hostPeerId = "h1",
                seats = new[]
                {
                    LobbySeatLogic.MakeHostSeat("h1", "Host", 0, 0, 0, "coA"),
                    LobbySeatLogic.MakeAiSeat(1, 1, 1, "coB", LobbySeatLogic.DefaultAiController)
                }
            };
            Assert.Equal(0, LobbySeatLogic.CountJoinable(draft));
            Assert.True(LobbySeatLogic.TryPromoteToStandby(draft.seats[1], out _));
            Assert.Equal(1, LobbySeatLogic.CountJoinable(draft));
            Assert.True(LobbySeatLogic.TrySeatHuman(draft, "g1", "Guest", out var idx, out _));
            Assert.Equal(1, idx);
            Assert.Equal(LobbySeatState.HumanSeated, LobbySeatLogic.GetState(draft.seats[1]));
            Assert.Equal(0, LobbySeatLogic.CountJoinable(draft));
            Assert.True(LobbySeatLogic.TryReleaseHuman(draft, "g1", out _));
            Assert.Equal(LobbySeatState.HumanStandby, LobbySeatLogic.GetState(draft.seats[1]));
            Assert.Equal(1, LobbySeatLogic.CountJoinable(draft));
        }

        [Fact]
        public void Color_and_pos_unique()
        {
            var draft = new LobbyDraftDto
            {
                seats = new[]
                {
                    LobbySeatLogic.MakeHostSeat("h", "H", 0, 0, 0, ""),
                    LobbySeatLogic.MakeAiSeat(1, 1, 1, "", 2)
                }
            };
            Assert.True(LobbySeatLogic.IsColorTaken(draft, 0, 1));
            Assert.False(LobbySeatLogic.IsColorTaken(draft, 0, 0));
            Assert.Equal(2, LobbySeatLogic.NextFreeColor(draft, 0, 0));
            Assert.True(LobbySeatLogic.IsPosTaken(draft, 1, 0));
            var req = new SeatEditRequest
            {
                seatIndex = 1,
                setColor = true,
                color = 0,
                peerId = "h"
            };
            Assert.False(LobbySeatLogic.TryApplyEdit(draft, req, true, "h", out var nack, out _));
            Assert.Equal(SeatEditNackCode.ColorTaken, nack);
        }

        [Fact]
        public void Bake_assigns_random_pos()
        {
            var draft = new LobbyDraftDto
            {
                seats = new[]
                {
                    LobbySeatLogic.MakeHostSeat("h", "H", 0, 0, 0, "a"),
                    LobbySeatLogic.MakeAiSeat(1, 1, 1, "", 2)
                }
            };
            draft.seats[1].posMode = (int)LobbyPosMode.Random;
            LobbySeatLogic.BakeForStart(draft, 42, new[] { "coX", "coY" });
            Assert.Equal(LobbyPosMode.Fixed, LobbySeatLogic.GetPosMode(draft.seats[1]));
            Assert.NotEqual(0, draft.seats[1].pos); // host took 0
            Assert.False(string.IsNullOrEmpty(draft.seats[1].coId));
        }
    }

    /// <summary>
    /// Same-process Host+Guest loopback smoke for M02+M01 (no Unity / no Steam).
    /// </summary>
    public class LoopbackLobbySmokeTests
    {
        [Fact]
        public void Host_guest_draft_ready_canStart_and_lobbyStart()
        {
            var port = TestNetUtil.AllocateLoopbackPort();
            var hostLog = new CollectingLanLogger();
            var guestLog = new CollectingLanLogger();

            var hostNet = new NetSession(hostLog, "hostpeer1");
            var guestNet = new NetSession(guestLog, "guestpeer1");
            var hostLobby = new LobbySession(hostNet, hostLog);
            var guestLobby = new LobbySession(guestNet, guestLog);
            hostLobby.Start();
            guestLobby.Start();

            LobbyStartPayload guestSawStart = null;
            guestLobby.OnLobbyStart += p => guestSawStart = p;
            LobbyRejectPayload reject = null;
            guestNet.OnLobbyRejected += p => reject = p;

            try
            {
                hostNet.StartHost(port);
                Thread.Sleep(100);

                // Open a human standby before guest may join
                var draft = new LobbyDraftDto
                {
                    mapId = "TestMap",
                    mapContentHash = HashUtil.StableHash16("body"),
                    hostPeerId = hostNet.LocalPeerId,
                    hostDisplayName = "Host",
                    hostSlotIndex = 0,
                    seats = new[]
                    {
                        LobbySeatLogic.MakeHostSeat(hostNet.LocalPeerId, "Host", 0, 0, 0, "coA"),
                        LobbySeatLogic.MakeAiSeat(1, 1, 1, "coB", LobbySeatLogic.DefaultAiController)
                    }
                };
                Assert.True(LobbySeatLogic.TryPromoteToStandby(draft.seats[1], out _));
                hostLobby.PublishLocalDraft(draft);

                guestNet.ConnectGuest("127.0.0.1:" + port);

                Assert.True(WaitUntil(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return hostNet.IsConnected && guestNet.IsConnected;
                }, 3000), "TCP/Welcome timeout; reject=" + (reject != null ? reject.message : "none"));

                Assert.Null(reject);
                Assert.Equal(LobbySeatState.HumanSeated, LobbySeatLogic.GetState(hostLobby.Draft.seats[1]));

                Assert.True(WaitUntil(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return guestLobby.Draft.mapId == "TestMap"
                           && LobbySeatLogic.FindSeatIndexByPeer(guestLobby.Draft, guestNet.LocalPeerId) == 1;
                }, 3000), "Draft/seat sync timeout");

                hostLobby.SetLocalReady(true);
                guestLobby.SetLocalReady(true);

                Assert.True(WaitUntil(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return hostLobby.CanStart && guestLobby.CanStart;
                }, 3000), "CanStart timeout");

                Assert.True(InputGateRules.MayAuthorizeStart(true, hostLobby.CanStart, gatesArmed: true));

                hostLobby.AuthorizeAndBroadcastStart(12345);

                Assert.True(WaitUntil(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return guestSawStart != null && guestLobby.StartAuthorized;
                }, 3000), "LobbyStart timeout");

                Assert.Equal(hostLobby.BattleId, guestLobby.BattleId);
                Assert.Equal(12345, guestLobby.BattleSeed);
                Assert.Equal("TestMap", guestSawStart.draft.mapId);
                Assert.True(hostLobby.StartAuthorized);
            }
            finally
            {
                guestNet.Disconnect("test-end");
                hostNet.Disconnect("test-end");
            }
        }

        [Fact]
        public void Join_without_standby_is_rejected()
        {
            var port = TestNetUtil.AllocateLoopbackPort();
            var hostLog = new CollectingLanLogger();
            var guestLog = new CollectingLanLogger();
            var hostNet = new NetSession(hostLog, "hostpeer2");
            var guestNet = new NetSession(guestLog, "guestpeer2");
            var hostLobby = new LobbySession(hostNet, hostLog);
            var guestLobby = new LobbySession(guestNet, guestLog);
            hostLobby.Start();
            guestLobby.Start();

            LobbyRejectPayload reject = null;
            guestNet.OnLobbyRejected += p => reject = p;

            try
            {
                hostNet.StartHost(port);
                Thread.Sleep(80);
                hostLobby.PublishLocalDraft(new LobbyDraftDto
                {
                    mapId = "M",
                    mapContentHash = "abcd",
                    hostPeerId = hostNet.LocalPeerId,
                    seats = new[]
                    {
                        LobbySeatLogic.MakeHostSeat(hostNet.LocalPeerId, "H", 0, 0, 0, ""),
                        LobbySeatLogic.MakeAiSeat(1, 1, 1, "", 2)
                    }
                });

                guestNet.ConnectGuest("127.0.0.1:" + port);
                Assert.True(WaitUntil(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return reject != null;
                }, 3000), "expected LobbyReject");

                Assert.Equal((int)LobbyRejectCode.NoHumanSlot, reject.code);
                Assert.False(guestNet.IsConnected);
            }
            finally
            {
                guestNet.Disconnect("test-end");
                hostNet.Disconnect("test-end");
            }
        }

        [Fact]
        public void Guest_draft_sync_preserves_host_ready_mirror()
        {
            var port = TestNetUtil.AllocateLoopbackPort();
            var hostLog = new CollectingLanLogger();
            var guestLog = new CollectingLanLogger();
            var hostNet = new NetSession(hostLog, "hostpeer3");
            var guestNet = new NetSession(guestLog, "guestpeer3");
            var hostLobby = new LobbySession(hostNet, hostLog);
            var guestLobby = new LobbySession(guestNet, guestLog);
            hostLobby.Start();
            guestLobby.Start();

            try
            {
                hostNet.StartHost(port);
                Thread.Sleep(80);
                var draft = new LobbyDraftDto
                {
                    mapId = "TestMap",
                    mapContentHash = HashUtil.StableHash16("body"),
                    hostPeerId = hostNet.LocalPeerId,
                    hostDisplayName = "Host",
                    seats = new[]
                    {
                        LobbySeatLogic.MakeHostSeat(hostNet.LocalPeerId, "Host", 0, 0, 0, "coA"),
                        LobbySeatLogic.MakeAiSeat(1, 1, 1, "coB", LobbySeatLogic.DefaultAiController)
                    }
                };
                Assert.True(LobbySeatLogic.TryPromoteToStandby(draft.seats[1], out _));
                hostLobby.PublishLocalDraft(draft);

                guestNet.ConnectGuest("127.0.0.1:" + port);
                Assert.True(WaitUntil(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return hostNet.IsConnected && guestNet.IsConnected
                           && LobbySeatLogic.FindSeatIndexByPeer(guestLobby.Draft, guestNet.LocalPeerId) == 1;
                }, 3000));

                hostLobby.SetLocalReady(true);
                Assert.True(WaitUntil(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return guestLobby.RemoteReady;
                }, 3000), "Guest should mirror Host ready");

                // Guest preference edit → Host BroadcastDraft; Guest must keep Host ready mirror.
                guestLobby.RequestSeatEdit(new SeatEditRequest
                {
                    seatIndex = 1,
                    setColor = true,
                    color = 3
                });
                Assert.True(WaitUntil(() =>
                {
                    hostNet.Pump();
                    guestNet.Pump();
                    return guestLobby.Draft.seats != null
                           && guestLobby.Draft.seats[1].color == 3;
                }, 3000), "color sync timeout");

                Assert.True(guestLobby.RemoteReady, "Host ready mirror must survive LobbyDraft on Guest");
                Assert.True(hostLobby.LocalReady);
                Assert.False(guestLobby.LocalReady, "Guest ready cleared after own preference edit");
            }
            finally
            {
                guestNet.Disconnect("test-end");
                hostNet.Disconnect("test-end");
            }
        }

        private static bool WaitUntil(Func<bool> pred, int timeoutMs)
        {
            var start = Environment.TickCount;
            while (Environment.TickCount - start < timeoutMs)
            {
                if (pred())
                    return true;
                Thread.Sleep(20);
            }
            return pred();
        }
    }
}
