using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Phase B: Host may hold N guest TCP peers; Guest still has one link to Host.
    /// </summary>
    public sealed class NetSession
    {
        public const ushort ProtocolVersion = WireCodec.ProtocolVersion;

        private sealed class PeerConn
        {
            public string ConnKey;
            public string PeerId;
            public string DisplayName = "";
            public TcpClient Client;
            public NetworkStream Stream;
            public Thread ReadThread;
            public long LastRecvTickMs;
            public bool AwaitingHello;
            public bool Admitted;
            /// <summary>True after DropPeerKeepHosting claimed this conn (idempotent / supersede-safe).</summary>
            public volatile bool Dropped;
        }

        private readonly ILanLogger _log;
        private readonly ConcurrentQueue<Envelope> _incoming = new ConcurrentQueue<Envelope>();
        private readonly ConcurrentQueue<PendingHello> _pendingHostHellos = new ConcurrentQueue<PendingHello>();
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        private readonly List<Action<Envelope>> _handlers = new List<Action<Envelope>>();
        private readonly object _peersLock = new object();
        private readonly Dictionary<string, PeerConn> _peersByConn = new Dictionary<string, PeerConn>();
        private readonly Dictionary<string, PeerConn> _peersByPeerId = new Dictionary<string, PeerConn>();
        private readonly object _seqLock = new object();

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private uint _nextSeq = 1;
        private float _heartbeatTimer;
        private readonly string _localPeerId;
        private volatile bool _welcomeReceived;
        private PeerConn _guestLink;
        // Host CaptureBoard / AI skip bursts can stall the main thread for many seconds;
        // 8s idle kill dropped Guests mid-hitch and looked like "Intent never arrived".
        private const int HeartbeatTimeoutMs = 30000;

        private struct PendingHello
        {
            public string ConnKey;
            public Envelope Env;
        }

        public PeerRole Role { get; private set; } = PeerRole.None;

        /// <summary>Host: any admitted guest connected. Guest: Welcome received.</summary>
        public bool IsConnected
        {
            get
            {
                if (Role == PeerRole.Guest)
                    return _guestLink != null && IsPeerLive(_guestLink) && _welcomeReceived;
                if (Role != PeerRole.Host)
                    return false;
                lock (_peersLock)
                {
                    foreach (var p in _peersByPeerId.Values)
                    {
                        if (IsPeerLive(p))
                            return true;
                    }
                    return false;
                }
            }
        }

        public string LocalPeerId => _localPeerId;

        /// <summary>Compat: first admitted remote peer id (Host) or Host id (Guest).</summary>
        public string RemotePeerId
        {
            get
            {
                if (Role == PeerRole.Guest)
                    return _guestLink != null && _welcomeReceived ? _guestHostPeerId : null;
                lock (_peersLock)
                {
                    foreach (var kv in _peersByPeerId)
                    {
                        if (IsPeerLive(kv.Value))
                            return kv.Key;
                    }
                }
                return null;
            }
        }

        private string _guestHostPeerId;
        private string _guestHostDisplayName = "";

        public string LocalDisplayName { get; set; } = "";

        public string RemoteDisplayName
        {
            get
            {
                if (Role == PeerRole.Guest)
                    return _guestHostDisplayName ?? "";
                var id = RemotePeerId;
                if (string.IsNullOrEmpty(id))
                    return "";
                lock (_peersLock)
                {
                    return _peersByPeerId.TryGetValue(id, out var p) ? (p.DisplayName ?? "") : "";
                }
            }
        }

        public LobbyRejectPayload LastReject { get; private set; }

        public Func<HelloPayload, LobbyRejectPayload> AdmitGuest { get; set; }
        public Action<HelloPayload> OnGuestAdmitted { get; set; }

        public event Action OnConnected;
        /// <summary>Guest full disconnect, or Host when a peer is dropped (compat). Prefer OnPeerDisconnected on Host.</summary>
        public event Action<string> OnDisconnected;
        /// <summary>Host: one admitted guest TCP closed; peerId may be empty if Hello never completed.</summary>
        public event Action<string, string> OnPeerDisconnected;
        public event Action<LobbyRejectPayload> OnLobbyRejected;

        public NetSession(ILanLogger log, string peerId = null)
        {
            _log = log ?? NullLanLogger.Instance;
            _localPeerId = string.IsNullOrEmpty(peerId)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : peerId;
        }

        public IReadOnlyList<string> GetConnectedPeerIds()
        {
            var list = new List<string>();
            lock (_peersLock)
            {
                foreach (var kv in _peersByPeerId)
                {
                    if (IsPeerLive(kv.Value))
                        list.Add(kv.Key);
                }
            }
            return list;
        }

        public int ConnectedPeerCount => GetConnectedPeerIds().Count;

        public string GetPeerDisplayName(string peerId)
        {
            if (string.IsNullOrEmpty(peerId))
                return "";
            lock (_peersLock)
            {
                if (_peersByPeerId.TryGetValue(peerId, out var p))
                    return p.DisplayName ?? "";
            }
            return "";
        }

        public void Subscribe(Action<Envelope> handler) => _handlers.Add(handler);

        public void Pump()
        {
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { _log.Error("[Net] mainThread action: " + ex); }
            }

            while (_pendingHostHellos.TryDequeue(out var pending))
            {
                try
                {
                    HandleHostHello(pending.ConnKey, pending.Env);
                }
                catch (Exception ex)
                {
                    _log.Error("[Net] HandleHostHello: " + ex);
                }
            }

            while (_incoming.TryDequeue(out var env))
            {
                NoteRecvForSource(env.SourcePeerId);
                if (env.Type == MsgType.Ping)
                {
                    try
                    {
                        var pong = new Envelope { Type = MsgType.Pong, BattleId = env.BattleId, PayloadJson = "{}" };
                        if (Role == PeerRole.Host && !string.IsNullOrEmpty(env.SourcePeerId))
                        {
                            if (!TrySendTo(env.SourcePeerId, pong))
                                _log.Warn("[Net] Pong send failed peer=" + env.SourcePeerId);
                        }
                        else if (!TrySend(pong))
                            _log.Warn("[Net] Pong send failed");
                    }
                    catch (Exception ex)
                    {
                        _log.Error("[Net] Pong: " + ex);
                    }
                    continue;
                }
                if (env.Type == MsgType.Pong)
                    continue;

                if (env.Type == MsgType.Welcome && Role == PeerRole.Guest)
                {
                    try
                    {
                        var w = JsonUtil.FromJson<WelcomePayload>(env.PayloadJson);
                        _guestHostPeerId = w.peerId;
                        _guestHostDisplayName = w.displayName ?? "";
                        _welcomeReceived = true;
                        _log.Info("[Net] Welcome from peer=" + _guestHostPeerId + " name=" + _guestHostDisplayName);
                        OnConnected?.Invoke();
                    }
                    catch { /* ignore */ }
                }

                if (env.Type == MsgType.LobbyReject && Role == PeerRole.Guest)
                {
                    try
                    {
                        LastReject = JsonUtil.FromJson<LobbyRejectPayload>(env.PayloadJson);
                        OnLobbyRejected?.Invoke(LastReject);
                    }
                    catch { /* ignore */ }
                }

                foreach (var h in _handlers)
                {
                    try { h(env); }
                    catch (Exception ex) { _log.Error("[Net] handler: " + ex); }
                }

                if (env.Type == MsgType.LobbyReject && Role == PeerRole.Guest)
                    Disconnect("lobby-reject");
            }
        }

        public void Tick(float dt)
        {
            if (Role == PeerRole.None)
                return;

            if (Role == PeerRole.Guest)
            {
                if (!IsConnected)
                    return;
                _heartbeatTimer += dt;
                if (_heartbeatTimer >= 2f)
                {
                    _heartbeatTimer = 0f;
                    try
                    {
                        if (!TrySend(new Envelope { Type = MsgType.Ping, PayloadJson = "{}" }))
                        {
                            ForceDropPeer(null, "heartbeat-send-fail");
                            return;
                        }
                    }
                    catch
                    {
                        ForceDropPeer(null, "heartbeat-send-fail");
                        return;
                    }
                }
                CheckIdle(_guestLink, null);
                return;
            }

            // Host: ping all admitted peers; drop idle ones.
            _heartbeatTimer += dt;
            List<PeerConn> snapshot;
            lock (_peersLock)
            {
                snapshot = new List<PeerConn>(_peersByConn.Values);
            }

            var doPing = false;
            if (_heartbeatTimer >= 2f)
            {
                _heartbeatTimer = 0f;
                doPing = true;
            }

            foreach (var peer in snapshot)
            {
                if (!peer.Admitted || !IsPeerLive(peer))
                    continue;
                if (doPing)
                {
                    try
                    {
                        if (!TrySendToConn(peer, new Envelope { Type = MsgType.Ping, PayloadJson = "{}" }))
                        {
                            ForceDropPeer(peer, "heartbeat-send-fail");
                            continue;
                        }
                    }
                    catch
                    {
                        ForceDropPeer(peer, "heartbeat-send-fail");
                        continue;
                    }
                }
                CheckIdle(peer, peer);
            }
        }

        private void CheckIdle(PeerConn peer, PeerConn dropTarget)
        {
            if (peer == null || peer.LastRecvTickMs <= 0)
                return;
            var idle = Environment.TickCount - peer.LastRecvTickMs;
            if (idle < 0) idle = HeartbeatTimeoutMs + 1;
            if (idle > HeartbeatTimeoutMs)
            {
                _log.Warn("[Net] Heartbeat timeout — dropping peer=" + (peer.PeerId ?? peer.ConnKey));
                ForceDropPeer(dropTarget ?? peer, "heartbeat-timeout");
            }
        }

        private void NoteRecvForSource(string peerId)
        {
            if (Role == PeerRole.Guest)
            {
                NoteRecv(_guestLink);
                return;
            }
            if (string.IsNullOrEmpty(peerId))
                return;
            lock (_peersLock)
            {
                if (_peersByPeerId.TryGetValue(peerId, out var p))
                    NoteRecv(p);
            }
        }

        private static void NoteRecv(PeerConn peer)
        {
            if (peer == null) return;
            peer.LastRecvTickMs = Environment.TickCount;
            if (peer.LastRecvTickMs == 0)
                peer.LastRecvTickMs = 1;
        }

        private void ForceDropPeer(PeerConn peer, string reason)
        {
            if (Role == PeerRole.Host)
            {
                if (peer != null)
                    DropPeerKeepHosting(peer, reason);
                else
                {
                    // Drop all (legacy)
                    List<PeerConn> all;
                    lock (_peersLock)
                        all = new List<PeerConn>(_peersByConn.Values);
                    foreach (var p in all)
                        DropPeerKeepHosting(p, reason);
                }
            }
            else if (Role == PeerRole.Guest)
                Disconnect(reason);
        }

        public void StartHost(int port)
        {
            Disconnect("restart-host");
            Role = PeerRole.Host;
            _running = true;
            _welcomeReceived = true;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _log.Info($"[Net] Hosting on 0.0.0.0:{port} peerId={_localPeerId} (multi-guest)");
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LanMp-Accept" };
            _acceptThread.Start();
        }

        public void ConnectGuest(string address)
        {
            Disconnect("restart-guest");
            Role = PeerRole.Guest;
            _welcomeReceived = false;
            LastReject = null;
            _guestHostPeerId = null;
            _guestHostDisplayName = "";
            if (!WireCodec.TryParseEndpoint(address, out var host, out var port))
                throw new ArgumentException("Invalid address, expected host:port");

            _running = true;
            var client = new TcpClient();
            client.NoDelay = true;
            client.Connect(host, port);
            var stream = client.GetStream();
            var conn = new PeerConn
            {
                ConnKey = "guest-self",
                Client = client,
                Stream = stream,
                Admitted = true
            };
            NoteRecv(conn);
            _guestLink = conn;
            _log.Info($"[Net] TCP to {host}:{port} as Guest peerId={_localPeerId} (await Welcome)");
            StartReader(conn);
            Send(new Envelope
            {
                Type = MsgType.Hello,
                PayloadJson = JsonUtil.ToJson(new HelloPayload
                {
                    peerId = _localPeerId,
                    protocolVersion = ProtocolVersion,
                    displayName = LocalDisplayName ?? ""
                })
            });
        }

        public void Disconnect(string reason)
        {
            var wasRole = Role;
            _running = false;
            try
            {
                if (wasRole == PeerRole.Guest && _guestLink != null && IsPeerLive(_guestLink))
                {
                    TrySendToConn(_guestLink, new Envelope
                    {
                        Type = MsgType.Disconnect,
                        PayloadJson = JsonUtil.ToJson(new DisconnectPayload { reason = reason })
                    });
                }
                else if (wasRole == PeerRole.Host)
                {
                    List<PeerConn> all;
                    lock (_peersLock)
                        all = new List<PeerConn>(_peersByConn.Values);
                    foreach (var p in all)
                    {
                        TrySendToConn(p, new Envelope
                        {
                            Type = MsgType.Disconnect,
                            PayloadJson = JsonUtil.ToJson(new DisconnectPayload { reason = reason })
                        });
                    }
                }
            }
            catch { /* ignore */ }

            CloseAllPeers();
            try { _listener?.Stop(); } catch { }
            _listener = null;
            Role = PeerRole.None;
            _guestLink = null;
            _guestHostPeerId = null;
            _guestHostDisplayName = "";
            _welcomeReceived = false;

            var intentionalRestart = reason != null &&
                (reason.StartsWith("restart-", StringComparison.Ordinal) ||
                 reason == "shutdown");
            if (wasRole != PeerRole.None && !intentionalRestart)
                FireDisconnected(reason);
            _log.Info("[Net] Disconnected: " + reason + " (was " + wasRole + ")");
        }

        /// <summary>Host: drop one guest (or legacy: drop first). Listener stays up.</summary>
        public void DropGuestKeepHosting(string reason)
        {
            if (Role != PeerRole.Host)
            {
                Disconnect(reason);
                return;
            }
            PeerConn target = null;
            lock (_peersLock)
            {
                foreach (var p in _peersByConn.Values)
                {
                    target = p;
                    break;
                }
            }
            if (target != null)
                DropPeerKeepHosting(target, reason);
        }

        public void DropPeerKeepHosting(string peerId, string reason)
        {
            if (Role != PeerRole.Host || string.IsNullOrEmpty(peerId))
                return;
            PeerConn peer;
            lock (_peersLock)
            {
                if (!_peersByPeerId.TryGetValue(peerId, out peer))
                    return;
            }
            DropPeerKeepHosting(peer, reason, notifyLobby: true);
        }

        /// <summary>Host: drop every guest TCP without lobby seat events (post MatchAbort).</summary>
        public void DropAllPeersKeepHosting(string reason)
        {
            if (Role != PeerRole.Host)
                return;
            List<PeerConn> all;
            lock (_peersLock)
                all = new List<PeerConn>(_peersByConn.Values);
            foreach (var p in all)
                DropPeerKeepHosting(p, reason ?? "drop-all", notifyLobby: false);
        }

        private void DropPeerKeepHosting(PeerConn peer, string reason)
        {
            DropPeerKeepHosting(peer, reason, notifyLobby: true);
        }

        private void DropPeerKeepHosting(PeerConn peer, string reason, bool notifyLobby)
        {
            if (peer == null || Role != PeerRole.Host)
                return;
            if (peer.Dropped)
                return;
            peer.Dropped = true;

            var peerId = peer.PeerId ?? "";
            var wasAdmitted = peer.Admitted;
            RemovePeerIfCurrent(peer);

            try
            {
                if (peer.Stream != null && peer.Client != null && peer.Client.Connected)
                {
                    var env = new Envelope
                    {
                        Type = MsgType.Disconnect,
                        ProtocolVersion = ProtocolVersion,
                        PayloadJson = JsonUtil.ToJson(new DisconnectPayload { reason = reason })
                    };
                    lock (_seqLock)
                        env.Seq = _nextSeq++;
                    var frame = WireCodec.EncodeFrame(env);
                    peer.Stream.Write(frame, 0, frame.Length);
                }
            }
            catch { /* ignore */ }

            try { peer.Stream?.Close(); } catch { }
            try { peer.Client?.Close(); } catch { }

            _log.Info("[Net] Peer dropped, still hosting: " + reason + " peer=" + peerId +
                      " notifyLobby=" + notifyLobby);
            // Lobby seat release only for admitted peers that were not silently replaced.
            if (notifyLobby && wasAdmitted && !string.IsNullOrEmpty(peerId))
                FirePeerDisconnected(peerId, reason);
            // Do NOT FireDisconnected here — that used to AbortMatch for every single guest leave
            // while remaining guests stayed in-battle with no MatchAbort (P0 multi-guest orphan).
        }

        /// <summary>Remove from maps only if this instance is still the registered one.</summary>
        private void RemovePeerIfCurrent(PeerConn peer)
        {
            lock (_peersLock)
            {
                if (_peersByConn.TryGetValue(peer.ConnKey, out var byConn) &&
                    ReferenceEquals(byConn, peer))
                    _peersByConn.Remove(peer.ConnKey);
                if (!string.IsNullOrEmpty(peer.PeerId) &&
                    _peersByPeerId.TryGetValue(peer.PeerId, out var byId) &&
                    ReferenceEquals(byId, peer))
                    _peersByPeerId.Remove(peer.PeerId);
            }
        }

        private void RemovePeerLocked(PeerConn peer) => RemovePeerIfCurrent(peer);

        private void CloseAllPeers()
        {
            List<PeerConn> all;
            lock (_peersLock)
            {
                all = new List<PeerConn>(_peersByConn.Values);
                _peersByConn.Clear();
                _peersByPeerId.Clear();
            }
            if (_guestLink != null)
            {
                all.Add(_guestLink);
                _guestLink = null;
            }
            foreach (var p in all)
            {
                try { p.Stream?.Close(); } catch { }
                try { p.Client?.Close(); } catch { }
            }
        }

        private void RunOnMainThread(Action action)
        {
            if (action == null) return;
            _mainThreadActions.Enqueue(action);
        }

        private void FireDisconnected(string reason)
        {
            RunOnMainThread(() =>
            {
                try { OnDisconnected?.Invoke(reason); }
                catch (Exception ex) { _log.Error("[Net] OnDisconnected: " + ex); }
            });
        }

        private void FirePeerDisconnected(string peerId, string reason)
        {
            RunOnMainThread(() =>
            {
                try { OnPeerDisconnected?.Invoke(peerId ?? "", reason); }
                catch (Exception ex) { _log.Error("[Net] OnPeerDisconnected: " + ex); }
            });
        }

        public bool TrySend(Envelope env)
        {
            if (Role == PeerRole.Guest)
                return TrySendToConn(_guestLink, env);
            // Host legacy: broadcast to all admitted peers.
            return TryBroadcast(env);
        }

        public bool TrySendTo(string peerId, Envelope env)
        {
            if (env == null || string.IsNullOrEmpty(peerId))
                return false;
            PeerConn peer;
            lock (_peersLock)
            {
                if (!_peersByPeerId.TryGetValue(peerId, out peer))
                    return false;
            }
            return TrySendToConn(peer, env);
        }

        public bool TryBroadcast(Envelope env)
        {
            if (env == null)
                return false;
            if (Role == PeerRole.Guest)
                return TrySendToConn(_guestLink, env);

            List<PeerConn> targets;
            lock (_peersLock)
            {
                targets = new List<PeerConn>();
                foreach (var p in _peersByPeerId.Values)
                {
                    if (IsPeerLive(p))
                        targets.Add(p);
                }
            }
            if (targets.Count == 0)
                return false;
            var ok = true;
            foreach (var p in targets)
            {
                if (!TrySendToConn(p, CloneEnv(env)))
                    ok = false;
            }
            return ok;
        }

        private static Envelope CloneEnv(Envelope env)
        {
            return new Envelope
            {
                ProtocolVersion = env.ProtocolVersion,
                Type = env.Type,
                BattleId = env.BattleId,
                Seq = env.Seq,
                PayloadJson = env.PayloadJson,
                SourcePeerId = env.SourcePeerId
            };
        }

        private bool TrySendToConn(PeerConn peer, Envelope env)
        {
            if (env == null || peer == null)
                return false;
            var stream = peer.Stream;
            var client = peer.Client;
            if (stream == null || client == null || !client.Connected)
                return false;
            try
            {
                lock (peer)
                {
                    stream = peer.Stream;
                    if (stream == null)
                        return false;
                    lock (_seqLock)
                    {
                        env.Seq = _nextSeq++;
                        env.ProtocolVersion = ProtocolVersion;
                    }
                    var frame = WireCodec.EncodeFrame(env);
                    stream.Write(frame, 0, frame.Length);
                    stream.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("[Net] Send failed: " + ex.Message);
                return false;
            }
        }

        public void Send(Envelope env) => TrySend(env);

        private static bool IsPeerLive(PeerConn p) =>
            p != null && p.Client != null && p.Client.Connected && p.Stream != null;

        private void AcceptLoop()
        {
            try
            {
                while (_running)
                {
                    if (_listener == null)
                        break;
                    if (!_listener.Pending())
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    TcpClient incoming;
                    try { incoming = _listener.AcceptTcpClient(); }
                    catch { break; }
                    incoming.NoDelay = true;

                    var connKey = "tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    var peer = new PeerConn
                    {
                        ConnKey = connKey,
                        Client = incoming,
                        Stream = incoming.GetStream(),
                        AwaitingHello = true,
                        Admitted = false
                    };
                    NoteRecv(peer);
                    lock (_peersLock)
                        _peersByConn[connKey] = peer;

                    _log.Info("[Net] Guest TCP accepted conn=" + connKey + " — awaiting Hello");
                    StartReader(peer);
                }
            }
            catch (Exception ex)
            {
                if (_running)
                    _log.Error("[Net] AcceptLoop: " + ex.Message);
            }
        }

        private void StartReader(PeerConn peer)
        {
            peer.ReadThread = new Thread(() => ReadLoop(peer))
            {
                IsBackground = true,
                Name = "LanMp-Read-" + peer.ConnKey
            };
            peer.ReadThread.Start();
        }

        private void ReadLoop(PeerConn peer)
        {
            var lenBuf = new byte[4];
            string endReason = null;
            try
            {
                while (_running && peer.Stream != null && !peer.Dropped)
                {
                    if (!ReadExact(peer.Stream, lenBuf, 4))
                    {
                        endReason = "remote-eof";
                        break;
                    }
                    var len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf, 0));
                    if (len <= 0 || len > 2_000_000)
                        throw new IOException("Invalid frame length " + len);
                    var payload = new byte[len];
                    if (!ReadExact(peer.Stream, payload, len))
                    {
                        endReason = "remote-eof";
                        break;
                    }
                    var frame = new byte[4 + len];
                    Buffer.BlockCopy(lenBuf, 0, frame, 0, 4);
                    Buffer.BlockCopy(payload, 0, frame, 4, len);
                    if (!WireCodec.TryDecodeFrame(frame, 0, frame.Length, out var env, out _))
                        continue;
                    NoteRecv(peer);

                    // Host: never trust wire SourcePeerId; stamp only after admit.
                    if (Role == PeerRole.Host)
                    {
                        env.SourcePeerId = null;
                        if (peer.Admitted && !string.IsNullOrEmpty(peer.PeerId))
                            env.SourcePeerId = peer.PeerId;
                    }

                    if (env.ProtocolVersion != ProtocolVersion)
                    {
                        if (Role == PeerRole.Host && peer.AwaitingHello)
                        {
                            SendRejectAndDropConn(peer, new LobbyRejectPayload
                            {
                                code = (int)LobbyRejectCode.ProtocolMismatch,
                                message = LobbySeatLogic.RejectMessage(LobbyRejectCode.ProtocolMismatch)
                            });
                        }
                        else
                        {
                            _log.Error($"[Net] Protocol mismatch remote={env.ProtocolVersion} local={ProtocolVersion}");
                            if (Role == PeerRole.Host)
                                DropPeerKeepHosting(peer, "protocol-mismatch");
                            else
                                Disconnect("protocol-mismatch");
                        }
                        endReason = null;
                        break;
                    }

                    if (env.Type == MsgType.Hello && Role == PeerRole.Host)
                    {
                        _pendingHostHellos.Enqueue(new PendingHello { ConnKey = peer.ConnKey, Env = env });
                        continue;
                    }

                    // Host: drop all non-Hello traffic until admitted (blocks forged Ready/Intent).
                    if (Role == PeerRole.Host && !peer.Admitted)
                    {
                        _log.Warn("[Net] Dropping pre-admit frame type=" + env.Type + " conn=" + peer.ConnKey);
                        continue;
                    }

                    if (env.Type == MsgType.Disconnect)
                    {
                        _incoming.Enqueue(env);
                        endReason = null;
                        if (Role == PeerRole.Host)
                            DropPeerKeepHosting(peer, "remote-disconnect");
                        else
                            Disconnect("remote-disconnect");
                        break;
                    }

                    _incoming.Enqueue(env);
                }
            }
            catch (Exception ex)
            {
                if (_running && !peer.Dropped)
                {
                    _log.Warn("[Net] ReadLoop ended: " + ex.Message + " conn=" + peer.ConnKey);
                    endReason = "read-error";
                }
            }

            if (_running && endReason != null && !peer.Dropped)
            {
                _log.Warn("[Net] ReadLoop peer lost: " + endReason + " conn=" + peer.ConnKey);
                if (Role == PeerRole.Host)
                    DropPeerKeepHosting(peer, endReason);
                else
                    Disconnect(endReason);
            }
        }

        private bool HandleHostHello(string connKey, Envelope env)
        {
            PeerConn peer;
            lock (_peersLock)
            {
                if (!_peersByConn.TryGetValue(connKey, out peer))
                {
                    _log.Warn("[Net] Hello for unknown conn=" + connKey);
                    return false;
                }
            }

            HelloPayload hello;
            try
            {
                hello = JsonUtil.FromJson<HelloPayload>(env.PayloadJson);
            }
            catch
            {
                SendRejectAndDropConn(peer, new LobbyRejectPayload
                {
                    code = (int)LobbyRejectCode.Generic,
                    message = LobbySeatLogic.RejectMessage(LobbyRejectCode.Generic)
                });
                return false;
            }

            if (hello == null || hello.protocolVersion != ProtocolVersion)
            {
                SendRejectAndDropConn(peer, new LobbyRejectPayload
                {
                    code = (int)LobbyRejectCode.ProtocolMismatch,
                    message = LobbySeatLogic.RejectMessage(LobbyRejectCode.ProtocolMismatch)
                });
                return false;
            }

            // Already admitted same peerId (reconnect): silently supersede old conn — do not
            // FirePeerDisconnected (would release seat) or Remove from maps under new peer.
            PeerConn existing = null;
            lock (_peersLock)
            {
                if (_peersByPeerId.TryGetValue(hello.peerId, out existing) && existing != peer)
                    _log.Warn("[Net] Duplicate peerId — superseding old conn peer=" + hello.peerId);
                else
                    existing = null;
            }
            if (existing != null)
                DropPeerKeepHosting(existing, "peer-replaced", notifyLobby: false);

            LobbyRejectPayload reject = null;
            try
            {
                reject = AdmitGuest?.Invoke(hello);
            }
            catch (Exception ex)
            {
                _log.Error("[Net] AdmitGuest: " + ex);
                reject = new LobbyRejectPayload
                {
                    code = (int)LobbyRejectCode.Generic,
                    message = LobbySeatLogic.RejectMessage(LobbyRejectCode.Generic)
                };
            }

            if (reject != null)
            {
                SendRejectAndDropConn(peer, reject);
                return false;
            }

            peer.PeerId = hello.peerId;
            peer.DisplayName = hello.displayName ?? "";
            peer.AwaitingHello = false;
            peer.Admitted = true;
            lock (_peersLock)
                _peersByPeerId[hello.peerId] = peer;

            try
            {
                OnGuestAdmitted?.Invoke(hello);
            }
            catch (Exception ex)
            {
                _log.Error("[Net] OnGuestAdmitted: " + ex);
            }

            TrySendToConn(peer, new Envelope
            {
                Type = MsgType.Welcome,
                PayloadJson = JsonUtil.ToJson(new WelcomePayload
                {
                    peerId = _localPeerId,
                    protocolVersion = ProtocolVersion,
                    displayName = LocalDisplayName ?? "",
                    assignedSeatIndex = -1
                })
            });

            env.SourcePeerId = hello.peerId;
            _incoming.Enqueue(env);
            try { OnConnected?.Invoke(); }
            catch (Exception ex) { _log.Error("[Net] OnConnected: " + ex); }
            return true;
        }

        private void SendRejectAndDropConn(PeerConn peer, LobbyRejectPayload reject)
        {
            try
            {
                TrySendToConn(peer, new Envelope
                {
                    Type = MsgType.LobbyReject,
                    PayloadJson = JsonUtil.ToJson(reject)
                });
                Thread.Sleep(30);
            }
            catch { /* ignore */ }
            DropPeerKeepHosting(peer, "admit-reject");
        }

        private static bool ReadExact(NetworkStream stream, byte[] buffer, int size)
        {
            var read = 0;
            while (read < size)
            {
                var n = stream.Read(buffer, read, size - read);
                if (n <= 0)
                    return false;
                read += n;
            }
            return true;
        }

        private class DisconnectPayload
        {
            public string reason;
        }
    }
}
