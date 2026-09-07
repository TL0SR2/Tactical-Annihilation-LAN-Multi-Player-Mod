using System;
using System.Collections.Generic;

namespace AnnW.LanMp.Protocol
{
    /// <summary>M01 lobby — Phase B multi-guest TCP.</summary>
    public sealed class LobbySession
    {
        private readonly NetSession _net;
        private readonly ILanLogger _log;
        private readonly Dictionary<string, bool> _peerReady = new Dictionary<string, bool>();

        public LobbyDraftDto Draft { get; private set; } = new LobbyDraftDto();
        public bool LocalReady { get; private set; }

        /// <summary>Compat: any remote HumanSeated peer is ready (AND of remotes).</summary>
        public bool RemoteReady
        {
            get
            {
                if (Draft?.seats == null)
                    return false;
                var any = false;
                foreach (var s in Draft.seats)
                {
                    if (LobbySeatLogic.GetState(s) != LobbySeatState.HumanSeated)
                        continue;
                    var peer = s?.peerId;
                    if (string.IsNullOrEmpty(peer) || peer == _net.LocalPeerId)
                        continue;
                    any = true;
                    if (!IsPeerReady(peer))
                        return false;
                }
                return any;
            }
        }

        public bool CanStart { get; private set; }
        public string BattleId { get; private set; }
        public int BattleSeed { get; private set; }
        public bool StartAuthorized { get; private set; }
        public LobbyRejectPayload LastReject { get; private set; }

        public Func<IList<string>> CoPoolProvider { get; set; }

        /// <summary>
        /// Host-only: after BakeForStart, stamp skillId/psIds from game tables (ADR-005).
        /// Set by plugin (<c>CoLoadoutResolver.StampDraft</c>).
        /// </summary>
        public Action<LobbyDraftDto> AfterBakeCoLoadout { get; set; }
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

        public bool IsPeerReady(string peerId)
        {
            if (string.IsNullOrEmpty(peerId))
                return false;
            if (peerId == _net.LocalPeerId)
                return LocalReady;
            return _peerReady.TryGetValue(peerId, out var r) && r;
        }

        public void Start()
        {
            _net.Subscribe(OnEnvelope);
            _net.OnDisconnected += OnNetDisconnected;
            _net.OnPeerDisconnected += OnPeerDisconnected;
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
            SetPeerReadyMap(_net.LocalPeerId, ready);
            OnReadyChanged?.Invoke();
            if (_net.IsConnected || _net.Role == PeerRole.Host)
            {
                var env = new Envelope
                {
                    Type = MsgType.LobbyReady,
                    PayloadJson = JsonUtil.ToJson(new ReadyPayload
                    {
                        peerId = _net.LocalPeerId,
                        ready = ready
                    })
                };
                if (_net.Role == PeerRole.Host)
                    _net.TryBroadcast(env);
                else
                    _net.Send(env);
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
            try { AfterBakeCoLoadout?.Invoke(Draft); }
            catch (Exception ex)
            {
                _log.Error("[Lobby] AfterBakeCoLoadout failed: " + ex.Message);
                throw;
            }
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
                var ok = _net.TryBroadcast(new Envelope
                {
                    Type = MsgType.LobbyStart,
                    BattleId = BattleId,
                    PayloadJson = JsonUtil.ToJson(payload)
                });
                if (!ok)
                {
                    StartAuthorized = false;
                    BattleId = null;
                    _log.Error("[Lobby] LobbyStart broadcast failed — aborting start");
                    throw new InvalidOperationException("LobbyStart broadcast failed");
                }
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
            SyncSlotIndicesFromSeats();
            ClearAllReady();
            OnDraftChanged?.Invoke();
            BroadcastDraft();
            RecomputeCanStart();
            _log.Info($"[Lobby] Guest seated seat={idx} peer={hello.peerId}");
        }

        private void OnPeerDisconnected(string peerId, string reason)
        {
            if (_net.Role != PeerRole.Host)
                return;
            if (string.IsNullOrEmpty(peerId))
                return;
            // Mid-battle: Authority aborts the match — do not mutate draft / BroadcastDraft
            // to remaining Guests (would race MatchAbort and desync lobby lights).
            if (!string.IsNullOrEmpty(BattleId) || StartAuthorized)
            {
                _log.Info("[Lobby] Peer left mid-battle — seat release deferred peer=" + peerId);
                return;
            }
            if (!LobbySeatLogic.TryReleaseHuman(Draft, peerId, out _))
                return;
            _peerReady.Remove(peerId);
            SyncSlotIndicesFromSeats();
            ClearAllReady();
            OnDraftChanged?.Invoke();
            if (_net.IsConnected)
                BroadcastDraft();
            RecomputeCanStart();
            _log.Info("[Lobby] Peer left seat released peer=" + peerId + " reason=" + reason);
        }

        private void OnNetDisconnected(string reason)
        {
            // Host peer drops handled by OnPeerDisconnected (seat release).
            if (_net.Role == PeerRole.Host)
                return;
            // Guest (or Role already None after Disconnect): drop ghost HumanSeated from last match.
            ResetDraftOccupancyKeepMap();
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
            if (req.setState)
                ClearAllReady();
            else
                ClearPeerReady(req.peerId);
            if (req.setCoId)
            {
                try { AfterBakeCoLoadout?.Invoke(Draft); }
                catch (Exception ex)
                {
                    _log.Warn("[Lobby] re-stamp CO loadout after SeatEdit: " + ex.Message);
                }
            }
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
                    // Multi-guest: never trust payload peerId — stamp from TCP source.
                    if (string.IsNullOrEmpty(env.SourcePeerId))
                    {
                        _log.Warn("[Lobby] SeatEdit ignored — missing SourcePeerId");
                        break;
                    }
                    req.peerId = env.SourcePeerId;
                    var ok = LobbySeatLogic.TryApplyEdit(
                        Draft, req, asHost: false, editorPeerId: req.peerId,
                        out var nack, out var msg);
                    if (!ok)
                    {
                        var nackEnv = new Envelope
                        {
                            Type = MsgType.SeatEditNack,
                            PayloadJson = JsonUtil.ToJson(new SeatEditNack
                            {
                                requestId = req.requestId,
                                code = (int)nack,
                                message = msg
                            })
                        };
                        _net.TrySendTo(req.peerId, nackEnv);
                        break;
                    }
                    ClearPeerReady(req.peerId);
                    if (req.setCoId)
                    {
                        try { AfterBakeCoLoadout?.Invoke(Draft); }
                        catch (Exception ex)
                        {
                            _log.Warn("[Lobby] re-stamp CO loadout after Guest SeatEdit: " + ex.Message);
                        }
                    }
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
                    // Host: identity from TCP only (blocks forged Ready for other peers).
                    if (_net.Role == PeerRole.Host)
                    {
                        if (string.IsNullOrEmpty(env.SourcePeerId))
                            return;
                        p.peerId = env.SourcePeerId;
                    }
                    if (p.peerId == _net.LocalPeerId)
                        LocalReady = p.ready;
                    SetPeerReadyMap(p.peerId, p.ready);
                    // Host mirrors Ready to all other guests so everyone sees lights.
                    if (_net.Role == PeerRole.Host && _net.IsConnected &&
                        !string.IsNullOrEmpty(p.peerId) && p.peerId != _net.LocalPeerId)
                    {
                        _net.TryBroadcast(new Envelope
                        {
                            Type = MsgType.LobbyReady,
                            PayloadJson = JsonUtil.ToJson(p)
                        });
                    }
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
            _net.TryBroadcast(new Envelope
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
                _net.TryBroadcast(new Envelope
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
                if (!IsPeerReady(peer))
                    return false;
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
            LobbySeatLogic.RefreshLegacyGuestFields(Draft);
        }

        private void ClearAllReady()
        {
            LocalReady = false;
            _peerReady.Clear();
            CanStart = false;
            StartAuthorized = false;
            OnReadyChanged?.Invoke();
            OnCanStartChanged?.Invoke();
            BroadcastReadyState(_net.LocalPeerId, false);
            foreach (var id in _net.GetConnectedPeerIds())
                BroadcastReadyState(id, false);
        }

        private void ClearPeerReady(string peerId)
        {
            if (peerId == _net.LocalPeerId)
                LocalReady = false;
            SetPeerReadyMap(peerId, false);
            CanStart = false;
            OnReadyChanged?.Invoke();
            OnCanStartChanged?.Invoke();
            BroadcastReadyState(peerId, false);
        }

        private void SetPeerReadyMap(string peerId, bool ready)
        {
            if (string.IsNullOrEmpty(peerId) || peerId == _net.LocalPeerId)
                return;
            _peerReady[peerId] = ready;
        }

        private void BroadcastReadyState(string peerId, bool ready)
        {
            if (_net.Role != PeerRole.Host || !_net.IsConnected || string.IsNullOrEmpty(peerId))
                return;
            _net.TryBroadcast(new Envelope
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
            _peerReady.Clear();
            CanStart = false;
            StartAuthorized = false;
            BattleId = null;
            OnReadyChanged?.Invoke();
            OnCanStartChanged?.Invoke();
        }

        public void ClearBattleAuthorization()
        {
            StartAuthorized = false;
            BattleId = null;
            LocalReady = false;
            _peerReady.Clear();
            CanStart = false;
            OnReadyChanged?.Invoke();
            OnCanStartChanged?.Invoke();
            RecomputeCanStart();
        }

        /// <summary>
        /// Keep map/rules; clear all human occupancy (ghost names from last match).
        /// Used on Guest disconnect and Host leave-room before a fresh lobby.
        /// </summary>
        public void ResetDraftOccupancyKeepMap()
        {
            if (Draft != null)
            {
                if (Draft.seats != null)
                {
                    for (var i = 0; i < Draft.seats.Length; i++)
                    {
                        var s = Draft.seats[i];
                        if (s == null || !s.exist)
                            continue;
                        if (LobbySeatLogic.GetState(s) != LobbySeatState.HumanSeated)
                            continue;
                        var ai = s.standbyController > 0
                            ? s.standbyController
                            : LobbySeatLogic.DefaultAiController;
                        s.state = (int)LobbySeatState.Ai;
                        s.peerId = "";
                        s.occupantName = "";
                        s.controller = ai;
                        s.standbyController = ai;
                    }
                }

                Draft.hostPeerId = "";
                Draft.guestPeerId = "";
                Draft.hostDisplayName = "";
                Draft.guestDisplayName = "";
                Draft.guestSlotIndex = -1;
            }

            ResetSessionState();
            OnDraftChanged?.Invoke();
            _log.Info("[Lobby] Draft occupancy cleared (map/rules kept)");
        }

        /// <summary>
        /// Host: HumanSeated must match live TCP peers (+ local Host).
        /// Call after MatchEnd drop, Abort return-to-lobby, and every room Open.
        /// </summary>
        public void ReconcileSeatsToConnectedPeers()
        {
            if (_net.Role != PeerRole.Host)
                return;
            if (Draft?.seats == null || Draft.seats.Length == 0)
            {
                ResetSessionState();
                RecomputeCanStart();
                return;
            }

            var local = _net.LocalPeerId ?? "";
            var live = new HashSet<string>(_net.GetConnectedPeerIds() ?? Array.Empty<string>());
            if (!string.IsNullOrEmpty(local))
                live.Add(local);

            var released = 0;
            var toRelease = new List<string>();
            for (var i = 0; i < Draft.seats.Length; i++)
            {
                var s = Draft.seats[i];
                if (s == null || LobbySeatLogic.GetState(s) != LobbySeatState.HumanSeated)
                    continue;
                if (string.IsNullOrEmpty(s.peerId) || !live.Contains(s.peerId))
                {
                    if (!string.IsNullOrEmpty(s.peerId))
                        toRelease.Add(s.peerId);
                    else
                    {
                        var ai = s.standbyController > 0
                            ? s.standbyController
                            : LobbySeatLogic.DefaultAiController;
                        s.state = (int)LobbySeatState.Ai;
                        s.occupantName = "";
                        s.controller = ai;
                        s.standbyController = ai;
                        released++;
                    }
                }
            }

            foreach (var peerId in toRelease)
            {
                if (LobbySeatLogic.TryReleaseHuman(Draft, peerId, out var idx))
                {
                    _peerReady.Remove(peerId);
                    // Ghost from last match → AI (not an open join slot).
                    if (idx >= 0 && idx < Draft.seats.Length)
                        LobbySeatLogic.TryDemoteToAi(Draft.seats[idx], out _);
                    released++;
                }
            }

            // Ensure local Host is seated with current display name.
            Draft.hostPeerId = local;
            if (!string.IsNullOrEmpty(local))
            {
                var hostIdx = LobbySeatLogic.FindSeatIndexByPeer(Draft, local);
                if (hostIdx < 0)
                {
                    var idx = Draft.hostSlotIndex;
                    if (idx < 0 || idx >= Draft.seats.Length)
                        idx = 0;
                    var seat = Draft.seats[idx];
                    if (seat != null && seat.exist)
                    {
                        if (LobbySeatLogic.GetState(seat) == LobbySeatState.HumanSeated &&
                            !string.IsNullOrEmpty(seat.peerId) && seat.peerId != local)
                            LobbySeatLogic.TryReleaseHuman(Draft, seat.peerId, out _);

                        seat.state = (int)LobbySeatState.HumanSeated;
                        seat.peerId = local;
                        seat.occupantName = string.IsNullOrWhiteSpace(_net.LocalDisplayName)
                            ? (seat.occupantName ?? "")
                            : _net.LocalDisplayName.Trim();
                        seat.controller = 0;
                        Draft.hostSlotIndex = idx;
                    }
                }
                else
                {
                    var seat = Draft.seats[hostIdx];
                    if (seat != null && !string.IsNullOrWhiteSpace(_net.LocalDisplayName))
                        seat.occupantName = _net.LocalDisplayName.Trim();
                    Draft.hostSlotIndex = hostIdx;
                }

                Draft.hostDisplayName = string.IsNullOrWhiteSpace(_net.LocalDisplayName)
                    ? (Draft.hostDisplayName ?? "")
                    : _net.LocalDisplayName.Trim();
            }

            LobbySeatLogic.RefreshLegacyGuestFields(Draft);
            if (string.IsNullOrEmpty(Draft.guestPeerId))
                Draft.guestDisplayName = "";

            SyncSlotIndicesFromSeats();
            ClearAllReady();
            OnDraftChanged?.Invoke();
            if (_net.IsConnected)
                BroadcastDraft();
            RecomputeCanStart();
            _log.Info("[Lobby] Reconcile seats live=" + live.Count + " released~=" + released);
        }

        /// <summary>Compat alias — prefer <see cref="ReconcileSeatsToConnectedPeers"/>.</summary>
        public void ReleaseSeatsForDisconnectedPeers()
        {
            ReconcileSeatsToConnectedPeers();
        }

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
