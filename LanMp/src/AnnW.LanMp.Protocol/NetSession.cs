using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace AnnW.LanMp.Protocol
{
    public sealed class NetSession
    {
        public const ushort ProtocolVersion = WireCodec.ProtocolVersion;

        private readonly ILanLogger _log;
        private readonly ConcurrentQueue<Envelope> _incoming = new ConcurrentQueue<Envelope>();
        private readonly ConcurrentQueue<Envelope> _pendingHostHellos = new ConcurrentQueue<Envelope>();
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        private readonly List<Action<Envelope>> _handlers = new List<Action<Envelope>>();
        private readonly object _sendLock = new object();
        private readonly object _clientLock = new object();

        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private Thread _acceptThread;
        private Thread _readThread;
        private volatile bool _running;
        private uint _nextSeq = 1;
        private float _heartbeatTimer;
        private long _lastRecvTickMs;
        private readonly string _localPeerId;
        private volatile bool _welcomeReceived;
        private volatile bool _hostAwaitingHello;
        /// <summary>Milliseconds without any inbound frame before treating the peer as dead.</summary>
        private const int HeartbeatTimeoutMs = 8000;

        public PeerRole Role { get; private set; } = PeerRole.None;
        public bool IsConnected => _stream != null && _client != null && _client.Connected &&
                                   (Role != PeerRole.Guest || _welcomeReceived);
        public string LocalPeerId => _localPeerId;
        public string RemotePeerId { get; private set; }
        public string LocalDisplayName { get; set; } = "";
        public string RemoteDisplayName { get; private set; } = "";
        public LobbyRejectPayload LastReject { get; private set; }

        /// <summary>Host: return reject payload to deny, or null to admit.</summary>
        public Func<HelloPayload, LobbyRejectPayload> AdmitGuest { get; set; }

        /// <summary>Host: after admit + Welcome, before handlers see Hello.</summary>
        public Action<HelloPayload> OnGuestAdmitted { get; set; }

        public event Action OnConnected;
        public event Action<string> OnDisconnected;
        public event Action<LobbyRejectPayload> OnLobbyRejected;

        public NetSession(ILanLogger log, string peerId = null)
        {
            _log = log ?? NullLanLogger.Instance;
            _localPeerId = string.IsNullOrEmpty(peerId)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : peerId;
        }

        public void Subscribe(Action<Envelope> handler) => _handlers.Add(handler);

        public void Pump()
        {
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { _log.Error("[Net] mainThread action: " + ex); }
            }

            // Host Hello/Admit must run on the Unity/main thread (mutates Lobby.Draft).
            while (_pendingHostHellos.TryDequeue(out var helloEnv))
            {
                try
                {
                    if (!HandleHostHello(helloEnv))
                    {
                        // Reject already dropped the guest stream; keep hosting.
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("[Net] HandleHostHello: " + ex);
                }
            }

            while (_incoming.TryDequeue(out var env))
            {
                NoteRecv();
                if (env.Type == MsgType.Ping)
                {
                    try
                    {
                        if (!TrySend(new Envelope { Type = MsgType.Pong, BattleId = env.BattleId, PayloadJson = "{}" }))
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
                        RemotePeerId = w.peerId;
                        RemoteDisplayName = w.displayName ?? "";
                        _welcomeReceived = true;
                        _log.Info("[Net] Welcome from peer=" + RemotePeerId + " name=" + RemoteDisplayName);
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
                        ForceDropPeer("heartbeat-send-fail");
                        return;
                    }
                }
                catch
                {
                    ForceDropPeer("heartbeat-send-fail");
                    return;
                }
            }

            // Safety net when TCP half-closes without EOF waking ReadLoop promptly.
            if (_lastRecvTickMs > 0)
            {
                var idle = Environment.TickCount - _lastRecvTickMs;
                if (idle < 0) idle = HeartbeatTimeoutMs + 1; // TickCount wrap
                if (idle > HeartbeatTimeoutMs)
                {
                    _log.Warn("[Net] Heartbeat timeout — dropping peer");
                    ForceDropPeer("heartbeat-timeout");
                }
            }
        }

        private void NoteRecv()
        {
            _lastRecvTickMs = Environment.TickCount;
            if (_lastRecvTickMs == 0)
                _lastRecvTickMs = 1;
        }

        private void ForceDropPeer(string reason)
        {
            if (Role == PeerRole.Host)
                DropGuestKeepHosting(reason);
            else if (Role == PeerRole.Guest)
                Disconnect(reason);
        }

        public void StartHost(int port)
        {
            Disconnect("restart-host");
            Role = PeerRole.Host;
            _running = true;
            _welcomeReceived = true; // host does not need Welcome
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _log.Info($"[Net] Hosting on 0.0.0.0:{port} peerId={_localPeerId}");
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LanMp-Accept" };
            _acceptThread.Start();
        }

        public void ConnectGuest(string address)
        {
            Disconnect("restart-guest");
            Role = PeerRole.Guest;
            _welcomeReceived = false;
            LastReject = null;
            if (!WireCodec.TryParseEndpoint(address, out var host, out var port))
                throw new ArgumentException("Invalid address, expected host:port");

            _running = true;
            _client = new TcpClient();
            _client.NoDelay = true;
            _client.Connect(host, port);
            _stream = _client.GetStream();
            NoteRecv();
            _log.Info($"[Net] TCP to {host}:{port} as Guest peerId={_localPeerId} (await Welcome)");
            StartReader();
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
            // OnConnected fires on Welcome in Pump — not here.
        }

        public void Disconnect(string reason)
        {
            var wasRole = Role;
            _running = false;
            _hostAwaitingHello = false;
            try
            {
                if (_stream != null && _client != null && _client.Connected)
                {
                    Send(new Envelope
                    {
                        Type = MsgType.Disconnect,
                        PayloadJson = JsonUtil.ToJson(new DisconnectPayload { reason = reason })
                    });
                }
            }
            catch { /* ignore */ }

            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            try { _listener?.Stop(); } catch { }
            lock (_clientLock)
            {
                _stream = null;
                _client = null;
            }
            // Keep listener only if we meant to stay hosting — Disconnect stops host too.
            _listener = null;
            Role = PeerRole.None;
            RemotePeerId = null;
            RemoteDisplayName = "";
            _welcomeReceived = false;
            _lastRecvTickMs = 0;
            // Intentional StartHost/ConnectGuest restarts must not fire UI leave handlers
            // (queued notify would run after Role is already Host/Guest again).
            var intentionalRestart = reason != null &&
                (reason.StartsWith("restart-", StringComparison.Ordinal) ||
                 reason == "shutdown");
            if (wasRole != PeerRole.None && !intentionalRestart)
                FireDisconnected(reason);
            _log.Info("[Net] Disconnected: " + reason + " (was " + wasRole + ")");
        }

        /// <summary>Host: drop guest TCP but keep listening. Safe to call from any thread.</summary>
        public void DropGuestKeepHosting(string reason)
        {
            if (Role != PeerRole.Host)
            {
                Disconnect(reason);
                return;
            }

            bool hadGuest;
            TcpClient clientToClose = null;
            NetworkStream streamToClose = null;
            lock (_clientLock)
            {
                hadGuest = _client != null || _stream != null;
                if (hadGuest)
                {
                    streamToClose = _stream;
                    clientToClose = _client;
                    _stream = null;
                    _client = null;
                }
            }

            if (!hadGuest)
                return;

            try
            {
                if (streamToClose != null && clientToClose != null && clientToClose.Connected)
                {
                    var env = new Envelope
                    {
                        Type = MsgType.Disconnect,
                        Seq = _nextSeq++,
                        ProtocolVersion = ProtocolVersion,
                        PayloadJson = JsonUtil.ToJson(new DisconnectPayload { reason = reason })
                    };
                    var frame = WireCodec.EncodeFrame(env);
                    lock (_sendLock)
                        streamToClose.Write(frame, 0, frame.Length);
                }
            }
            catch { /* ignore — guest may already be gone after LobbyReject */ }

            try { streamToClose?.Close(); } catch { }
            try { clientToClose?.Close(); } catch { }
            RemotePeerId = null;
            RemoteDisplayName = "";
            _hostAwaitingHello = false;
            _log.Info("[Net] Guest dropped, still hosting: " + reason);
            // Unity UI must not run on the TCP read thread (crash: TMP Mesh from ReadLoop).
            FireDisconnected(reason);
        }

        private void RunOnMainThread(Action action)
        {
            if (action == null) return;
            _mainThreadActions.Enqueue(action);
        }

        /// <summary>Fire OnDisconnected on main thread (Disconnect may be called off-thread).</summary>
        private void FireDisconnected(string reason)
        {
            RunOnMainThread(() =>
            {
                try { OnDisconnected?.Invoke(reason); }
                catch (Exception ex) { _log.Error("[Net] OnDisconnected: " + ex); }
            });
        }

        public bool TrySend(Envelope env)
        {
            if (env == null)
                return false;
            var stream = _stream;
            var client = _client;
            if (stream == null || client == null || !client.Connected)
                return false;
            try
            {
                lock (_sendLock)
                {
                    // Re-check under lock — Disconnect may have cleared stream.
                    stream = _stream;
                    if (stream == null)
                        return false;
                    env.Seq = _nextSeq++;
                    env.ProtocolVersion = ProtocolVersion;
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

        /// <summary>Best-effort send (lobby heartbeat / disconnect notify). Prefer TrySend when failure must Abort.</summary>
        public void Send(Envelope env)
        {
            TrySend(env);
        }

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

                    lock (_clientLock)
                    {
                        var busy = _client != null && _client.Connected;
                        if (busy)
                        {
                            // Phase A: reject extras with a short-lived reader
                            ThreadPool.QueueUserWorkItem(_ => RejectExtraClient(incoming));
                            continue;
                        }

                        _client = incoming;
                        _stream = incoming.GetStream();
                        _hostAwaitingHello = true;
                        RemotePeerId = null;
                        RemoteDisplayName = "";
                        NoteRecv();
                    }

                    _log.Info("[Net] Guest TCP accepted — awaiting Hello");
                    StartReader();
                }
            }
            catch (Exception ex)
            {
                if (_running)
                    _log.Error("[Net] AcceptLoop: " + ex.Message);
            }
        }

        private void RejectExtraClient(TcpClient extra)
        {
            try
            {
                using (extra)
                {
                    var stream = extra.GetStream();
                    // Best-effort: wait briefly for Hello then send Reject
                    extra.ReceiveTimeout = 3000;
                    var env = TryReadOneEnvelope(stream, 3000);
                    HelloPayload hello = null;
                    if (env != null && env.Type == MsgType.Hello)
                        hello = JsonUtil.FromJson<HelloPayload>(env.PayloadJson);

                    var reject = new LobbyRejectPayload
                    {
                        code = (int)LobbyRejectCode.GuestSlotTaken,
                        message = LobbySeatLogic.RejectMessage(LobbyRejectCode.GuestSlotTaken),
                        joinableSlots = 0
                    };
                    if (AdmitGuest != null && hello != null)
                    {
                        var gated = AdmitGuest(hello);
                        if (gated != null)
                            reject = gated;
                    }

                    var frame = WireCodec.EncodeFrame(new Envelope
                    {
                        Type = MsgType.LobbyReject,
                        ProtocolVersion = ProtocolVersion,
                        PayloadJson = JsonUtil.ToJson(reject)
                    });
                    stream.Write(frame, 0, frame.Length);
                    stream.Flush();
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("[Net] RejectExtraClient: " + ex.Message);
            }
        }

        private static Envelope TryReadOneEnvelope(NetworkStream stream, int timeoutMs)
        {
            try
            {
                var lenBuf = new byte[4];
                if (!ReadExact(stream, lenBuf, 4))
                    return null;
                var len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf, 0));
                if (len <= 0 || len > 2_000_000)
                    return null;
                var payload = new byte[len];
                if (!ReadExact(stream, payload, len))
                    return null;
                var frame = new byte[4 + len];
                Buffer.BlockCopy(lenBuf, 0, frame, 0, 4);
                Buffer.BlockCopy(payload, 0, frame, 4, len);
                WireCodec.TryDecodeFrame(frame, 0, frame.Length, out var env, out _);
                return env;
            }
            catch
            {
                return null;
            }
        }

        private void StartReader()
        {
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "LanMp-Read" };
            _readThread.Start();
        }

        private void ReadLoop()
        {
            var lenBuf = new byte[4];
            string endReason = null;
            try
            {
                while (_running && _stream != null)
                {
                    if (!ReadExact(_stream, lenBuf, 4))
                    {
                        endReason = "remote-eof";
                        break;
                    }
                    var len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf, 0));
                    if (len <= 0 || len > 2_000_000)
                        throw new IOException("Invalid frame length " + len);
                    var payload = new byte[len];
                    if (!ReadExact(_stream, payload, len))
                    {
                        endReason = "remote-eof";
                        break;
                    }
                    var frame = new byte[4 + len];
                    Buffer.BlockCopy(lenBuf, 0, frame, 0, 4);
                    Buffer.BlockCopy(payload, 0, frame, 4, len);
                    if (!WireCodec.TryDecodeFrame(frame, 0, frame.Length, out var env, out _))
                        continue;
                    NoteRecv();
                    if (env.ProtocolVersion != ProtocolVersion)
                    {
                        if (Role == PeerRole.Host && _hostAwaitingHello)
                        {
                            SendRejectAndDrop(new LobbyRejectPayload
                            {
                                code = (int)LobbyRejectCode.ProtocolMismatch,
                                message = LobbySeatLogic.RejectMessage(LobbyRejectCode.ProtocolMismatch)
                            });
                        }
                        else
                        {
                            _log.Error($"[Net] Protocol mismatch remote={env.ProtocolVersion} local={ProtocolVersion}");
                            Disconnect("protocol-mismatch");
                        }
                        endReason = null;
                        break;
                    }

                    if (env.Type == MsgType.Hello && Role == PeerRole.Host)
                    {
                        // Defer admit + Welcome to Pump (main thread) — do not mutate lobby off-thread.
                        _pendingHostHellos.Enqueue(env);
                        continue;
                    }

                    if (env.Type == MsgType.Disconnect)
                    {
                        _incoming.Enqueue(env);
                        endReason = null; // Disconnect/Drop below owns lifecycle
                        if (Role == PeerRole.Host)
                            DropGuestKeepHosting("remote-disconnect");
                        else
                            Disconnect("remote-disconnect");
                        break;
                    }

                    _incoming.Enqueue(env);
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    _log.Warn("[Net] ReadLoop ended: " + ex.Message);
                    endReason = "read-error";
                }
            }

            // Clean peer EOF used to fall out of the loop with no Disconnect — Guest kept playing.
            if (_running && endReason != null)
            {
                _log.Warn("[Net] ReadLoop peer lost: " + endReason);
                if (Role == PeerRole.Host)
                    DropGuestKeepHosting(endReason);
                else
                    Disconnect(endReason);
            }
        }

        private bool HandleHostHello(Envelope env)
        {
            HelloPayload hello;
            try
            {
                hello = JsonUtil.FromJson<HelloPayload>(env.PayloadJson);
            }
            catch
            {
                SendRejectAndDrop(new LobbyRejectPayload
                {
                    code = (int)LobbyRejectCode.Generic,
                    message = LobbySeatLogic.RejectMessage(LobbyRejectCode.Generic)
                });
                return false;
            }

            if (hello == null || hello.protocolVersion != ProtocolVersion)
            {
                SendRejectAndDrop(new LobbyRejectPayload
                {
                    code = (int)LobbyRejectCode.ProtocolMismatch,
                    message = LobbySeatLogic.RejectMessage(LobbyRejectCode.ProtocolMismatch)
                });
                return false;
            }

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
                SendRejectAndDrop(reject);
                return false;
            }

            RemotePeerId = hello.peerId;
            RemoteDisplayName = hello.displayName ?? "";
            _hostAwaitingHello = false;

            try
            {
                OnGuestAdmitted?.Invoke(hello);
            }
            catch (Exception ex)
            {
                _log.Error("[Net] OnGuestAdmitted: " + ex);
            }

            var assigned = -1;
            // assigned seat is already in draft via OnGuestAdmitted; Welcome carries index for convenience
            Send(new Envelope
            {
                Type = MsgType.Welcome,
                PayloadJson = JsonUtil.ToJson(new WelcomePayload
                {
                    peerId = _localPeerId,
                    protocolVersion = ProtocolVersion,
                    displayName = LocalDisplayName ?? "",
                    assignedSeatIndex = assigned
                })
            });

            _incoming.Enqueue(env); // optional: allow lobby to see Hello
            try { OnConnected?.Invoke(); }
            catch (Exception ex) { _log.Error("[Net] OnConnected: " + ex); }
            return true;
        }

        private void SendRejectAndDrop(LobbyRejectPayload reject)
        {
            try
            {
                Send(new Envelope
                {
                    Type = MsgType.LobbyReject,
                    PayloadJson = JsonUtil.ToJson(reject)
                });
                Thread.Sleep(30);
            }
            catch { /* ignore */ }
            DropGuestKeepHosting("admit-reject");
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
