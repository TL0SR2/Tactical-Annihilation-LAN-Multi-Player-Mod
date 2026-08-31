using System;

namespace AnnW.LanMp.Protocol
{
    /// <summary>M01 lobby — no scene load.</summary>
    public sealed class LobbySession
    {
        private readonly NetSession _net;
        private readonly ILanLogger _log;

        public LobbyDraftDto Draft { get; private set; } = new LobbyDraftDto();
        public bool LocalReady { get; private set; }
        public bool RemoteReady { get; private set; }
        public bool CanStart { get; private set; }
        public string BattleId { get; private set; }
        public int BattleSeed { get; private set; }
        public bool StartAuthorized { get; private set; }
        public LobbyRejectPayload LastReject { get; private set; }

        /// <summary>Optional CO name pool for Bake (Host fills from game data).</summary>
        public Func<System.Collections.Generic.IList<string>> CoPoolProvider { get; set; }

        /// <summary>True while Host considers battle start armed (blocks joins).</summary>
        public Func<bool> IsBattleStartedGate { get; set; }

        public event Action OnDraftChanged;
        public event Action OnReadyChanged;
        public event Action OnCanStartChanged;
        public event Action<LobbyStartPayload> OnLobbyStart;
        public event Action<LobbyRejectPayload> OnLobbyRejected;
        public event Action<SeatEditNack> OnSeatEditNack;

        public LobbySession(NetSession net, ILanLogger log)
        {
            _net = net;
            _log = log ?? NullLanLogger.Instance;
        }

        public void Start()
        {
            _net.Subscribe(OnEnvelope);
            _net.OnDisconnected += OnNetDisconnected;
            _net.AdmitGuest = TryAdmitGuest;
            _net.OnGuestAdmitted = OnGuestAdmitted;
        }

        public void PublishLocalDraft(LobbyDraftDto draft)
        {
            Draft = draft ?? new LobbyDraftDto();
            if (_net.Role == PeerRole.Host)
            {
                Draft.hostPeerId = _net.LocalPeerId;
                SyncSlotIndicesFromSeats();
            }

            ClearAllReady();
            OnDraftChanged?.Invoke();

            if (_net.Role == PeerRole.Host)
            {
                if (_net.IsConnected)
                    BroadcastDraft();
                RecomputeCanStart();
            }
        }

        /// <summary>Host re-sends current Draft after a guest connects (may be empty map).</summary>
        public void RepublishCurrentDraftIfHost()
        {
            if (_net.Role != PeerRole.Host)
                return;
            PublishLocalDraft(Draft ?? new LobbyDraftDto());
        }

        public void SetLocalReady(bool ready)
        {
            if (_net.Role == PeerRole.None)
                return;
            // Host may ready alone (no guest). Guest needs connection.
            if (_net.Role == PeerRole.Guest && !_net.IsConnected)
                return;
            if (string.IsNullOrEmpty(Draft.mapId))
            {
                _log.Warn("[Lobby] Cannot ready without mapId");
                return;
            }
            if (LobbySeatLogic.FindSeatIndexByPeer(Draft, _net.LocalPeerId) < 0
                && _net.Role != PeerRole.Host)
            {
                _log.Warn("[Lobby] Cannot ready without seated human");
                return;
            }

            LocalReady = ready;
            OnReadyChanged?.Invoke();
            if (_net.IsConnected)
            {
                _net.Send(new Envelope
                {
                    Type = MsgType.LobbyReady,
                    PayloadJson = JsonUtil.ToJson(new ReadyPayload
                    {
                        peerId = _net.LocalPeerId,
                        ready = ready
                    })
                });
            }
            RecomputeCanStart();
        }

        public void RequestSeatEdit(SeatEditRequest req)
        {
            if (req == null)
                return;
            if (Draft?.seats == null || Draft.seats.Length == 0)
            {
                _log.Warn("[Lobby] SeatEdit ignored — draft has no seats (select a map first)");
                OnSeatEditNack?.Invoke(new SeatEditNack
                {
                    requestId = req.requestId,
                    code = (int)SeatEditNackCode.BadSeat,
                    message = "请先选择地图"
                });
                return;
            }

            req.peerId = _net.LocalPeerId;
            if (string.IsNullOrEmpty(req.requestId))
                req.requestId = Guid.NewGuid().ToString("N").Substring(0, 8);

            if (_net.Role == PeerRole.Host)
            {
                ApplySeatEditAsHost(req);
                return;
            }

            if (_net.Role != PeerRole.Guest || !_net.IsConnected)
                return;
            _net.Send(new Envelope
            {
                Type = MsgType.SeatEditRequest,
                PayloadJson = JsonUtil.ToJson(req)
            });
        }

        public void AuthorizeAndBroadcastStart(int battleSeed)
        {
            if (_net.Role != PeerRole.Host)
                throw new InvalidOperationException("Only Host can start");
            if (!CanStart)
                throw new InvalidOperationException("Not all ready");

            LobbySeatLogic.BakeForStart(Draft, battleSeed, CoPoolProvider?.Invoke());
            SyncSlotIndicesFromSeats();

            BattleId = Guid.NewGuid().ToString("N");
            BattleSeed = battleSeed;
            StartAuthorized = true;
            var payload = new LobbyStartPayload
            {
                battleId = BattleId,
                battleSeed = BattleSeed,
                draft = Draft
            };
            if (_net.IsConnected)
            {
                _net.Send(new Envelope
                {
                    Type = MsgType.LobbyStart,
                    BattleId = BattleId,
                    PayloadJson = JsonUtil.ToJson(payload)
                });
            }
            _log.Info($"[Lobby] LobbyStart battleId={BattleId} seed={BattleSeed}");
            OnLobbyStart?.Invoke(payload);
        }

        private LobbyRejectPayload TryAdmitGuest(HelloPayload hello)
        {
            if (_net.Role != PeerRole.Host)
                return MakeReject(LobbyRejectCode.Generic, 0, 0, 0);

            if (IsBattleStartedGate != null && IsBattleStartedGate())
                return MakeReject(LobbyRejectCode.BattleStarted,
                    LobbySeatLogic.CountSeatedHumans(Draft) + LobbySeatLogic.CountJoinable(Draft),
                    LobbySeatLogic.CountSeatedHumans(Draft),
                    LobbySeatLogic.CountJoinable(Draft));

            // Phase A: one guest TCP only (NetSession already blocks second stream, but Hello path may race).
            if (_net.IsConnected && !string.IsNullOrEmpty(_net.RemotePeerId)
                && _net.RemotePeerId != hello?.peerId)
            {
                return MakeReject(LobbyRejectCode.GuestSlotTaken,
                    LobbySeatLogic.CountSeatedHumans(Draft) + LobbySeatLogic.CountJoinable(Draft),
                    LobbySeatLogic.CountSeatedHumans(Draft),
                    LobbySeatLogic.CountJoinable(Draft));
            }

            var joinable = LobbySeatLogic.CountJoinable(Draft);
            if (joinable <= 0)
            {
                return MakeReject(LobbyRejectCode.NoHumanSlot,
                    LobbySeatLogic.CountSeatedHumans(Draft),
                    LobbySeatLogic.CountSeatedHumans(Draft),
                    0);
            }

            return null;
        }

        private void OnGuestAdmitted(HelloPayload hello)
        {
            if (_net.Role != PeerRole.Host || hello == null)
                return;
            if (!LobbySeatLogic.TrySeatHuman(Draft, hello.peerId, hello.displayName, out var idx, out var err))
            {
                _log.Warn("[Lobby] Admit ok but seat failed: " + err);
                return;
            }
            Draft.guestPeerId = hello.peerId;
            Draft.guestDisplayName = hello.displayName ?? "";
            Draft.guestSlotIndex = idx;
            SyncSlotIndicesFromSeats();
            ClearAllReady();
            OnDraftChanged?.Invoke();
            BroadcastDraft();
            RecomputeCanStart();
            _log.Info($"[Lobby] Guest seated seat={idx} peer={hello.peerId}");
        }

        private void OnNetDisconnected(string reason)
        {
            if (_net.Role == PeerRole.Host && !string.IsNullOrEmpty(Draft?.guestPeerId))
            {
                if (LobbySeatLogic.TryReleaseHuman(Draft, Draft.guestPeerId, out _))
                {
                    Draft.guestPeerId = null;
                    Draft.guestDisplayName = "";
                    Draft.guestSlotIndex = -1;
                    SyncSlotIndicesFromSeats();
                    ClearAllReady();
                    OnDraftChanged?.Invoke();
                    RecomputeCanStart();
                }
            }
            else if (_net.Role != PeerRole.Host)
            {
                // Guest session ended: wipe ready/battle; keep last draft mirror until new join.
                ResetSessionState();
            }
            else
            {
                ResetSessionState();
            }
        }

        private void ApplySeatEditAsHost(SeatEditRequest req)
        {
            var ok = LobbySeatLogic.TryApplyEdit(
                Draft, req, asHost: true, editorPeerId: _net.LocalPeerId,
                out var nack, out var msg);
            if (!ok)
            {
                OnSeatEditNack?.Invoke(new SeatEditNack
                {
                    requestId = req.requestId,
                    code = (int)nack,
                    message = msg
                });
                return;
            }
            // Preference edit: clear only editor ready; structural: clear all.
            if (req.setState)
                ClearAllReady();
            else
                ClearPeerReady(req.peerId);
            SyncSlotIndicesFromSeats();
            OnDraftChanged?.Invoke();
            if (_net.IsConnected)
                BroadcastDraft();
            RecomputeCanStart();
        }

        private void OnEnvelope(Envelope env)
        {
            switch (env.Type)
            {
                case MsgType.LobbyReject:
                {
                    var p = JsonUtil.FromJson<LobbyRejectPayload>(env.PayloadJson);
                    LastReject = p;
                    _log.Warn("[Lobby] Rejected: " + (p != null ? LobbySeatLogic.RejectMessage((LobbyRejectCode)p.code) : "?"));
                    OnLobbyRejected?.Invoke(p);
                    break;
                }
                case MsgType.LobbyDraft:
                    if (_net.Role == PeerRole.Guest)
                    {
                        Draft = JsonUtil.FromJson<LobbyDraftDto>(env.PayloadJson) ?? new LobbyDraftDto();
                        // Ready is synced only via LobbyReady — never wipe Host ready mirror on every draft.
                        OnDraftChanged?.Invoke();
                        _log.Info("[Lobby] Draft received map=" + Draft.mapId);
                    }
                    break;
                case MsgType.SeatEditRequest:
                {
                    if (_net.Role != PeerRole.Host)
                        break;
                    var req = JsonUtil.FromJson<SeatEditRequest>(env.PayloadJson);
                    if (req == null)
                        break;
                    var ok = LobbySeatLogic.TryApplyEdit(
                        Draft, req, asHost: false, editorPeerId: req.peerId,
                        out var nack, out var msg);
                    if (!ok)
                    {
                        _net.Send(new Envelope
                        {
                            Type = MsgType.SeatEditNack,
                            PayloadJson = JsonUtil.ToJson(new SeatEditNack
                            {
                                requestId = req.requestId,
                                code = (int)nack,
                                message = msg
                            })
                        });
                        break;
                    }
                    ClearPeerReady(req.peerId);
                    SyncSlotIndicesFromSeats();
                    OnDraftChanged?.Invoke();
                    BroadcastDraft();
                    RecomputeCanStart();
                    break;
                }
                case MsgType.SeatEditNack:
                {
                    var n = JsonUtil.FromJson<SeatEditNack>(env.PayloadJson);
                    if (n != null)
                        OnSeatEditNack?.Invoke(n);
                    break;
                }
                case MsgType.LobbyReady:
                {
                    var p = JsonUtil.FromJson<ReadyPayload>(env.PayloadJson);
                    if (p == null)
                        return;
                    if (p.peerId == _net.LocalPeerId)
                        LocalReady = p.ready;
                    else
                        RemoteReady = p.ready;
                    OnReadyChanged?.Invoke();
                    RecomputeCanStart();
                    break;
                }
                case MsgType.LobbyCanStart:
                {
                    var p = JsonUtil.FromJson<CanStartPayload>(env.PayloadJson);
                    CanStart = p != null && p.canStart;
                    OnCanStartChanged?.Invoke();
                    break;
                }
                case MsgType.LobbyStart:
                {
                    var p = JsonUtil.FromJson<LobbyStartPayload>(env.PayloadJson);
                    if (p == null)
                        return;
                    BattleId = p.battleId;
                    BattleSeed = p.battleSeed;
                    Draft = p.draft ?? Draft;
                    StartAuthorized = true;
                    _log.Info("[Lobby] LobbyStart received battleId=" + BattleId);
                    OnLobbyStart?.Invoke(p);
                    break;
                }
            }
        }

        private void BroadcastDraft()
        {
            _net.Send(new Envelope
            {
                Type = MsgType.LobbyDraft,
                PayloadJson = JsonUtil.ToJson(Draft)
            });
        }

        private void RecomputeCanStart()
        {
            if (_net.Role != PeerRole.Host)
                return;

            var ok = !string.IsNullOrEmpty(Draft.mapId)
                     && !string.IsNullOrEmpty(Draft.mapContentHash)
                     && AllSeatedHumansReady();

            if (ok == CanStart)
                return;
            CanStart = ok;
            OnCanStartChanged?.Invoke();
            if (_net.IsConnected)
            {
                _net.Send(new Envelope
                {
                    Type = MsgType.LobbyCanStart,
                    PayloadJson = JsonUtil.ToJson(new CanStartPayload { canStart = CanStart })
                });
            }
            _log.Info("[Lobby] CanStart=" + CanStart);
        }

        private bool AllSeatedHumansReady()
        {
            if (Draft?.seats == null)
                return false;
            var any = false;
            foreach (var s in Draft.seats)
            {
                if (LobbySeatLogic.GetState(s) != LobbySeatState.HumanSeated)
                    continue;
                any = true;
                var peer = s.peerId;
                if (string.IsNullOrEmpty(peer))
                    return false;
                if (peer == _net.LocalPeerId)
                {
                    if (!LocalReady)
                        return false;
                }
                else
                {
                    if (!RemoteReady)
                        return false;
                }
            }
            return any;
        }

        private void SyncSlotIndicesFromSeats()
        {
            if (Draft?.seats == null)
                return;
            Draft.hostSlotIndex = LobbySeatLogic.FindSeatIndexByPeer(Draft, Draft.hostPeerId);
            if (Draft.hostSlotIndex < 0)
                Draft.hostSlotIndex = LobbySeatLogic.FindSeatIndexByPeer(Draft, _net.LocalPeerId);
            Draft.guestSlotIndex = string.IsNullOrEmpty(Draft.guestPeerId)
                ? -1
                : LobbySeatLogic.FindSeatIndexByPeer(Draft, Draft.guestPeerId);
        }

        private void ClearAllReady()
        {
            LocalReady = false;
            RemoteReady = false;
            CanStart = false;
            StartAuthorized = false;
            OnReadyChanged?.Invoke();
            OnCanStartChanged?.Invoke();
            BroadcastReadyState(_net.LocalPeerId, false);
            if (!string.IsNullOrEmpty(_net.RemotePeerId))
                BroadcastReadyState(_net.RemotePeerId, false);
        }

        private void ClearPeerReady(string peerId)
        {
            if (peerId == _net.LocalPeerId)
                LocalReady = false;
            else
                RemoteReady = false;
            CanStart = false;
            OnReadyChanged?.Invoke();
            OnCanStartChanged?.Invoke();
            BroadcastReadyState(peerId, false);
        }

        private void BroadcastReadyState(string peerId, bool ready)
        {
            if (_net.Role != PeerRole.Host || !_net.IsConnected || string.IsNullOrEmpty(peerId))
                return;
            _net.Send(new Envelope
            {
                Type = MsgType.LobbyReady,
                PayloadJson = JsonUtil.ToJson(new ReadyPayload
                {
                    peerId = peerId,
                    ready = ready
                })
            });
        }

        private void ResetSessionState()
        {
            LocalReady = false;
            RemoteReady = false;
            CanStart = false;
            StartAuthorized = false;
            BattleId = null;
            OnReadyChanged?.Invoke();
            OnCanStartChanged?.Invoke();
        }

        /// <summary>Clear start/battle tokens without wiping seat draft (MatchAbort / rematch).</summary>
        public void ClearBattleAuthorization()
        {
            StartAuthorized = false;
            BattleId = null;
            LocalReady = false;
            RemoteReady = false;
            CanStart = false;
            OnReadyChanged?.Invoke();
            OnCanStartChanged?.Invoke();
            RecomputeCanStart();
        }

        /// <summary>After guest leave or rematch: ensure draft seat labels are clean and UI rebuilds.</summary>
        public void NotifyDraftUiRefresh()
        {
            OnDraftChanged?.Invoke();
        }

        private static LobbyRejectPayload MakeReject(LobbyRejectCode code, int maxH, int online, int joinable)
        {
            return new LobbyRejectPayload
            {
                code = (int)code,
                message = LobbySeatLogic.RejectMessage(code),
                maxHumans = maxH,
                onlineHumans = online,
                joinableSlots = joinable
            };
        }
    }
}
