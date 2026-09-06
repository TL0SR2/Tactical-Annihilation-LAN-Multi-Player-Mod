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

        [Fact]
        public void Host_sets_resPercent_on_human_guest_rejected()
        {
            var draft = new LobbyDraftDto
            {
                hostPeerId = "h",
                seats = new[]
                {
                    LobbySeatLogic.MakeHostSeat("h", "H", 0, 0, 0, ""),
                    LobbySeatLogic.MakeAiSeat(1, 1, 1, "", 3)
                }
            };
            Assert.Equal(1f, draft.seats[1].resPercent, 3);
            Assert.True(LobbySeatLogic.TryApplyEdit(draft, new SeatEditRequest
            {
                seatIndex = 0,
                setResPercent = true,
                resPercent = 1.5f,
                peerId = "h"
            }, true, "h", out _, out _));
            Assert.Equal(1.5f, draft.seats[0].resPercent, 3);

            Assert.False(LobbySeatLogic.TryApplyEdit(draft, new SeatEditRequest
            {
                seatIndex = 0,
                setResPercent = true,
                resPercent = 2f,
                peerId = "g"
            }, false, "g", out var nack, out _));
            Assert.Equal(SeatEditNackCode.NotAllowed, nack);
            Assert.Equal(1.5f, draft.seats[0].resPercent, 3);
        }

        [Fact]
        public void Host_eco_or_intel_promotes_preset_ai_to_custom()
        {
            var draft = new LobbyDraftDto
            {
                seats = new[]
                {
                    LobbySeatLogic.MakeHostSeat("h", "H", 0, 0, 0, ""),
                    LobbySeatLogic.MakeAiSeat(1, 1, 1, "", 3) // Normal
                }
            };
            Assert.Equal(3, draft.seats[1].controller);
            Assert.True(LobbySeatLogic.TryApplyEdit(draft, new SeatEditRequest
            {
                seatIndex = 1,
                setResPercent = true,
                resPercent = 2f,
                peerId = "h"
            }, true, "h", out _, out _));
            Assert.Equal(SkirmishSeatEconomy.ControllerCustom, draft.seats[1].controller);
            Assert.Equal(2f, draft.seats[1].resPercent, 3);

            draft.seats[1].controller = 4; // Hard
            SkirmishSeatEconomy.ApplyPresetToSeat(draft.seats[1], 4);
            Assert.True(LobbySeatLogic.TryApplyEdit(draft, new SeatEditRequest
            {
                seatIndex = 1,
                setAiIntelligence = true,
                aiIntelligence = 0.2f,
                peerId = "h"
            }, true, "h", out _, out _));
            Assert.Equal(SkirmishSeatEconomy.ControllerCustom, draft.seats[1].controller);
            Assert.Equal(0.2f, draft.seats[1].aiIntelligence, 3);
        }

        [Fact]
        public void Host_preset_controller_refills_eco_tables()
        {
            var draft = new LobbyDraftDto
            {
                seats = new[]
                {
                    LobbySeatLogic.MakeAiSeat(0, 0, 0, "", SkirmishSeatEconomy.ControllerCustom)
                }
            };
            draft.seats[0].resPercent = 3f;
            draft.seats[0].aiIntelligence = 0.2f;
            Assert.True(LobbySeatLogic.TryApplyEdit(draft, new SeatEditRequest
            {
                seatIndex = 0,
                setController = true,
                controller = 5, // Crazy
                peerId = "h"
            }, true, "h", out _, out _));
            Assert.Equal(5, draft.seats[0].controller);
            Assert.Equal(SkirmishSeatEconomy.GetPresetResMul(5), draft.seats[0].resPercent, 3);
            Assert.Equal(SkirmishSeatEconomy.GetPresetAiIntelligence(5), draft.seats[0].aiIntelligence, 3);
        }
    }

    public class SkirmishSeatEconomyTests
    {
        [Fact]
        public void ResolveForStart_human_applies_host_res()
        {
            var seat = LobbySeatLogic.MakeHostSeat("h", "H", 0, 0, 0, "");
            seat.resPercent = 2f;
            var stamp = SkirmishSeatEconomy.ResolveForStart(seat, humanSeated: true);
            Assert.Equal(SkirmishSeatEconomy.ControllerHuman, stamp.controller);
            Assert.Equal(2f, stamp.resPercent, 3);
            Assert.Equal(0f, stamp.aiIntelligence, 3);
        }

        [Fact]
        public void ResolveForStart_preset_ai_unchanged_when_floats_match()
        {
            var seat = LobbySeatLogic.MakeAiSeat(0, 0, 0, "", 4); // Hard → 1.2 / 0.9
            var stamp = SkirmishSeatEconomy.ResolveForStart(seat, humanSeated: false);
            Assert.Equal(4, stamp.controller);
            Assert.Equal(1.2f, stamp.resPercent, 3);
            Assert.Equal(0.9f, stamp.aiIntelligence, 3);
        }

        [Fact]
        public void ResolveForStart_promotes_to_custom_when_host_overrides_eco()
        {
            var seat = LobbySeatLogic.MakeAiSeat(0, 0, 0, "", 3); // Normal
            seat.resPercent = 2f; // Host eco override; controller still 3 in draft
            var stamp = SkirmishSeatEconomy.ResolveForStart(seat, humanSeated: false);
            Assert.Equal(SkirmishSeatEconomy.ControllerCustom, stamp.controller);
            Assert.Equal(2f, stamp.resPercent, 3);
            Assert.Equal(0.7f, stamp.aiIntelligence, 3);
        }

        [Fact]
        public void ResolveForStart_promotes_to_custom_when_host_overrides_intel()
        {
            var seat = LobbySeatLogic.MakeAiSeat(0, 0, 0, "", 3);
            seat.aiIntelligence = 0.2f;
            var stamp = SkirmishSeatEconomy.ResolveForStart(seat, humanSeated: false);
            Assert.Equal(SkirmishSeatEconomy.ControllerCustom, stamp.controller);
            Assert.Equal(1f, stamp.resPercent, 3);
            Assert.Equal(0.2f, stamp.aiIntelligence, 3);
        }

        [Fact]
        public void ResolveForStart_custom_keeps_both_floats()
        {
            var seat = LobbySeatLogic.MakeAiSeat(0, 0, 0, "", SkirmishSeatEconomy.ControllerCustom);
            seat.resPercent = 1.5f;
            seat.aiIntelligence = 0.4f;
            var stamp = SkirmishSeatEconomy.ResolveForStart(seat, humanSeated: false);
            Assert.Equal(SkirmishSeatEconomy.ControllerCustom, stamp.controller);
            Assert.Equal(1.5f, stamp.resPercent, 3);
            Assert.Equal(0.4f, stamp.aiIntelligence, 3);
        }

        [Fact]
        public void LobbyStart_json_roundtrip_preserves_eco_for_guest_bootstrap()
        {
            var draft = new LobbyDraftDto
            {
                mapId = "M",
                mapContentHash = "hash",
                seats = new[]
                {
                    LobbySeatLogic.MakeHostSeat("host", "H", 0, 0, 0, "coA"),
                    LobbySeatLogic.MakeAiSeat(1, 1, 1, "coB", 3)
                }
            };
            draft.seats[0].resPercent = 2f;
            // Host eco override on AI without relying on UI promote (Resolve must still force Custom).
            draft.seats[1].resPercent = 2f;
            draft.seats[1].aiIntelligence = 0.7f;

            var payload = new LobbyStartPayload
            {
                battleId = "battle",
                battleSeed = 42,
                draft = draft
            };
            var json = JsonUtil.ToJson(payload);
            var guest = JsonUtil.FromJson<LobbyStartPayload>(json);
            Assert.NotNull(guest?.draft?.seats);
            Assert.Equal(2f, guest.draft.seats[0].resPercent, 3);
            Assert.Equal(2f, guest.draft.seats[1].resPercent, 3);

            var hostHuman = SkirmishSeatEconomy.ResolveForStart(draft.seats[0], true);
            var guestHuman = SkirmishSeatEconomy.ResolveForStart(guest.draft.seats[0], true);
            Assert.Equal(hostHuman.controller, guestHuman.controller);
            Assert.Equal(hostHuman.resPercent, guestHuman.resPercent, 3);

            var hostAi = SkirmishSeatEconomy.ResolveForStart(draft.seats[1], false);
            var guestAi = SkirmishSeatEconomy.ResolveForStart(guest.draft.seats[1], false);
            Assert.Equal(SkirmishSeatEconomy.ControllerCustom, hostAi.controller);
            Assert.Equal(hostAi.controller, guestAi.controller);
            Assert.Equal(hostAi.resPercent, guestAi.resPercent, 3);
            Assert.Equal(hostAi.aiIntelligence, guestAi.aiIntelligence, 3);
        }

        [Fact]
        public void LobbyStart_json_roundtrip_human_eco_only()
        {
            var draft = new LobbyDraftDto
            {
                seats = new[]
                {
                    LobbySeatLogic.MakeHostSeat("h", "H", 0, 0, 0, ""),
                    LobbySeatLogic.MakeHostSeat("g", "G", 1, 1, 1, "")
                }
            };
            draft.seats[0].state = (int)LobbySeatState.HumanSeated;
            draft.seats[1].state = (int)LobbySeatState.HumanSeated;
            draft.seats[0].resPercent = 1.5f;
            draft.seats[1].resPercent = 0.5f;

            var guest = JsonUtil.FromJson<LobbyStartPayload>(JsonUtil.ToJson(new LobbyStartPayload
            {
                battleId = "b",
                battleSeed = 1,
                draft = draft
            }));

            var g0 = SkirmishSeatEconomy.ResolveForStart(guest.draft.seats[0], true);
            var g1 = SkirmishSeatEconomy.ResolveForStart(guest.draft.seats[1], true);
            Assert.Equal(SkirmishSeatEconomy.ControllerHuman, g0.controller);
            Assert.Equal(1.5f, g0.resPercent, 3);
            Assert.Equal(0.5f, g1.resPercent, 3);
        }

        [Fact]
        public void ResolveEffective_prefers_sgs_then_preset_then_defaults()
        {
            Assert.Equal(2f, SkirmishSeatEconomy.ResolveEffectiveResMul(2f, 3), 3);
            Assert.Equal(1f, SkirmishSeatEconomy.ResolveEffectiveResMul(-1f, 3), 3); // Normal preset
            Assert.Equal(0.5f, SkirmishSeatEconomy.ResolveEffectiveResMul(-1f, 1), 3); // Beginner
            Assert.Equal(1f, SkirmishSeatEconomy.ResolveEffectiveResMul(-1f, 0), 3); // Human

            Assert.Equal(0.4f, SkirmishSeatEconomy.ResolveEffectiveAiIntelligence(0.4f, 6), 3);
            Assert.Equal(0.7f, SkirmishSeatEconomy.ResolveEffectiveAiIntelligence(-1f, 3), 3);
            Assert.Equal(
                SkirmishSeatEconomy.VanillaCustomAiIntelligenceFallback,
                SkirmishSeatEconomy.ResolveEffectiveAiIntelligence(-1f, 6),
                3);
        }

        [Fact]
        public void DefaultAiController_is_AI_Normal()
        {
            Assert.Equal(3, LobbySeatLogic.DefaultAiController);
            var seat = LobbySeatLogic.MakeAiSeat(0, 0, 0, "", LobbySeatLogic.DefaultAiController);
            Assert.Equal(1f, seat.resPercent, 3);
            Assert.Equal(0.7f, seat.aiIntelligence, 3);
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

        [Fact]
        public void Host_two_guests_seat_ready_and_lobbyStart()
        {
            var port = TestNetUtil.AllocateLoopbackPort();
            var hostLog = new CollectingLanLogger();
            var g1Log = new CollectingLanLogger();
            var g2Log = new CollectingLanLogger();

            var hostNet = new NetSession(hostLog, "host-mg");
            var g1Net = new NetSession(g1Log, "guest-mg1");
            var g2Net = new NetSession(g2Log, "guest-mg2");
            var hostLobby = new LobbySession(hostNet, hostLog);
            var g1Lobby = new LobbySession(g1Net, g1Log);
            var g2Lobby = new LobbySession(g2Net, g2Log);
            hostLobby.Start();
            g1Lobby.Start();
            g2Lobby.Start();

            LobbyStartPayload g1Start = null;
            LobbyStartPayload g2Start = null;
            g1Lobby.OnLobbyStart += p => g1Start = p;
            g2Lobby.OnLobbyStart += p => g2Start = p;

            try
            {
                hostNet.StartHost(port);
                Thread.Sleep(100);

                var draft = new LobbyDraftDto
                {
                    mapId = "MultiMap",
                    mapContentHash = HashUtil.StableHash16("multi"),
                    hostPeerId = hostNet.LocalPeerId,
                    hostDisplayName = "Host",
                    seats = new[]
                    {
                        LobbySeatLogic.MakeHostSeat(hostNet.LocalPeerId, "Host", 0, 0, 0, "coA"),
                        LobbySeatLogic.MakeAiSeat(1, 1, 1, "coB", LobbySeatLogic.DefaultAiController),
                        LobbySeatLogic.MakeAiSeat(2, 2, 2, "coC", LobbySeatLogic.DefaultAiController)
                    }
                };
                Assert.True(LobbySeatLogic.TryPromoteToStandby(draft.seats[1], out _));
                Assert.True(LobbySeatLogic.TryPromoteToStandby(draft.seats[2], out _));
                hostLobby.PublishLocalDraft(draft);

                g1Net.ConnectGuest("127.0.0.1:" + port);
                Assert.True(WaitUntil(() =>
                {
                    Pump3(hostNet, g1Net, g2Net);
                    return hostNet.IsConnected && g1Net.IsConnected
                           && LobbySeatLogic.FindSeatIndexByPeer(hostLobby.Draft, g1Net.LocalPeerId) >= 0;
                }, 4000), "guest1 join timeout");

                g2Net.ConnectGuest("127.0.0.1:" + port);
                Assert.True(WaitUntil(() =>
                {
                    Pump3(hostNet, g1Net, g2Net);
                    return g2Net.IsConnected
                           && hostNet.ConnectedPeerCount == 2
                           && LobbySeatLogic.FindSeatIndexByPeer(hostLobby.Draft, g2Net.LocalPeerId) >= 0;
                }, 4000), "guest2 join timeout; peers=" + hostNet.ConnectedPeerCount);

                Assert.Equal(0, LobbySeatLogic.CountJoinable(hostLobby.Draft));

                Assert.True(WaitUntil(() =>
                {
                    Pump3(hostNet, g1Net, g2Net);
                    return g1Lobby.Draft.mapId == "MultiMap"
                           && g2Lobby.Draft.mapId == "MultiMap"
                           && LobbySeatLogic.CountSeatedHumans(g1Lobby.Draft) == 3
                           && LobbySeatLogic.CountSeatedHumans(g2Lobby.Draft) == 3;
                }, 4000), "draft fan-out timeout");

                hostLobby.SetLocalReady(true);
                g1Lobby.SetLocalReady(true);
                g2Lobby.SetLocalReady(true);

                Assert.True(WaitUntil(() =>
                {
                    Pump3(hostNet, g1Net, g2Net);
                    return hostLobby.CanStart && g1Lobby.CanStart && g2Lobby.CanStart;
                }, 4000), "CanStart timeout");

                Assert.True(hostLobby.IsPeerReady(g1Net.LocalPeerId));
                Assert.True(hostLobby.IsPeerReady(g2Net.LocalPeerId));

                hostLobby.AuthorizeAndBroadcastStart(99);
                Assert.True(WaitUntil(() =>
                {
                    Pump3(hostNet, g1Net, g2Net);
                    return g1Start != null && g2Start != null
                           && g1Lobby.StartAuthorized && g2Lobby.StartAuthorized;
                }, 4000), "LobbyStart fan-out timeout");

                Assert.Equal(hostLobby.BattleId, g1Lobby.BattleId);
                Assert.Equal(hostLobby.BattleId, g2Lobby.BattleId);
                Assert.Equal(99, g1Lobby.BattleSeed);
                Assert.Equal(99, g2Lobby.BattleSeed);
            }
            finally
            {
                g2Net.Disconnect("test-end");
                g1Net.Disconnect("test-end");
                hostNet.Disconnect("test-end");
            }
        }

        [Fact]
        public void Third_guest_rejected_when_standby_full()
        {
            var port = TestNetUtil.AllocateLoopbackPort();
            var hostLog = new CollectingLanLogger();
            var hostNet = new NetSession(hostLog, "host-full");
            var g1 = new NetSession(new CollectingLanLogger(), "g-full1");
            var g2 = new NetSession(new CollectingLanLogger(), "g-full2");
            var g3 = new NetSession(new CollectingLanLogger(), "g-full3");
            var hostLobby = new LobbySession(hostNet, hostLog);
            new LobbySession(g1, new CollectingLanLogger()).Start();
            new LobbySession(g2, new CollectingLanLogger()).Start();
            var g3Lobby = new LobbySession(g3, new CollectingLanLogger());
            hostLobby.Start();
            g3Lobby.Start();

            LobbyRejectPayload reject = null;
            g3.OnLobbyRejected += p => reject = p;

            try
            {
                hostNet.StartHost(port);
                Thread.Sleep(80);
                var draft = new LobbyDraftDto
                {
                    mapId = "Full",
                    mapContentHash = "hash",
                    hostPeerId = hostNet.LocalPeerId,
                    seats = new[]
                    {
                        LobbySeatLogic.MakeHostSeat(hostNet.LocalPeerId, "H", 0, 0, 0, ""),
                        LobbySeatLogic.MakeAiSeat(1, 1, 1, "", 2),
                        LobbySeatLogic.MakeAiSeat(2, 2, 2, "", 2)
                    }
                };
                LobbySeatLogic.TryPromoteToStandby(draft.seats[1], out _);
                LobbySeatLogic.TryPromoteToStandby(draft.seats[2], out _);
                hostLobby.PublishLocalDraft(draft);

                g1.ConnectGuest("127.0.0.1:" + port);
                Assert.True(WaitUntil(() => { Pump3(hostNet, g1, g2); return g1.IsConnected; }, 3000));
                g2.ConnectGuest("127.0.0.1:" + port);
                Assert.True(WaitUntil(() => { Pump3(hostNet, g1, g2); return g2.IsConnected && hostNet.ConnectedPeerCount == 2; }, 3000));

                g3.ConnectGuest("127.0.0.1:" + port);
                Assert.True(WaitUntil(() =>
                {
                    hostNet.Pump();
                    g1.Pump();
                    g2.Pump();
                    g3.Pump();
                    return reject != null;
                }, 4000), "expected NoHumanSlot for 3rd guest");

                Assert.Equal((int)LobbyRejectCode.NoHumanSlot, reject.code);
                Assert.False(g3.IsConnected);
                Assert.Equal(2, hostNet.ConnectedPeerCount);
            }
            finally
            {
                g3.Disconnect("test-end");
                g2.Disconnect("test-end");
                g1.Disconnect("test-end");
                hostNet.Disconnect("test-end");
            }
        }

        private static void Pump3(NetSession a, NetSession b, NetSession c)
        {
            a.Pump();
            b.Pump();
            c.Pump();
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
