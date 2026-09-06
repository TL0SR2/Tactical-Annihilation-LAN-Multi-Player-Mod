using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using AnnW.LanMp.Authority;
using AnnW.LanMp.Core;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;

namespace AnnW.LanMp.Checksum
{
    /// <summary>
    /// M05: LAN board checkpoints ride EndTurn (same moment as ResultAttachment).
    /// Do NOT hash on OnPlayerTurnEnded — that races EndTurn apply and uses Save_General
    /// (wider than attachment), which false-pauses Guest after AI seats.
    /// </summary>
    public sealed class StateChecksumService : ILanMpModule
    {
        public string Name => "M05-Checksum";

        private const int MaxRepairAttempts = 2;

        private readonly NetSession _net;
        private readonly AuthorityService _authority;
        private readonly ConfigEntry<bool> _strict;
        private readonly ConfigEntry<bool> _repairOnMismatch;
        private readonly ManualLogSource _log;

        private StateHashDto _pendingHostHash;
        private bool _repairInFlight;
        private int _repairAttempts;

        public string LastLocalHash { get; private set; }
        public string LastRemoteHash { get; private set; }
        public bool MismatchPaused { get; private set; }
        public bool RepairInFlight => _repairInFlight;

        public StateChecksumService(
            NetSession net,
            AuthorityService authority,
            ConfigEntry<bool> strict,
            ConfigEntry<bool> repairOnMismatch,
            ManualLogSource log)
        {
            _net = net;
            _authority = authority;
            _strict = strict;
            _repairOnMismatch = repairOnMismatch;
            _log = log;
        }

        public void Start()
        {
            _net.Subscribe(OnEnvelope);
            try
            {
                BattleEventBus.self.OnPlayerTurnEnded += OnPlayerTurnEnded;
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Checksum] subscribe OnPlayerTurnEnded failed: " + ex.Message);
            }
        }

        public void Stop()
        {
            try { BattleEventBus.self.OnPlayerTurnEnded -= OnPlayerTurnEnded; } catch { /* ignore */ }
        }

        public void Tick(float dt) { }

        public void OnSceneChanged(string sceneName)
        {
            if (sceneName == null || sceneName.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) < 0)
                ResetRepairState();
        }

        public void ResetRepairState()
        {
            _repairInFlight = false;
            _repairAttempts = 0;
            _pendingHostHash = null;
            MismatchPaused = false;
        }

        /// <summary>Legacy Save_General hash (debug / non-LAN). LAN EndTurn uses <see cref="HashBoard"/>.</summary>
        public string ComputeBattleHash()
        {
            var battle = GS_Battle.self;
            if (battle == null)
                return "nobattle";

            var ob = battle.Save_General(as_map: true);
            var raw = ob != null ? ob.ToString() : "";
            return Sha16(raw);
        }

        public string HashBoard(ResultAttachmentDto board)
        {
            if (board == null)
                return "noboard";
            var sb = new StringBuilder(256);
            sb.Append("t=").Append(board.turn).Append(";c=").Append(board.coIndex).Append(';');
            if (board.players != null)
            {
                foreach (var p in board.players.OrderBy(x => x.index))
                {
                    if (p == null) continue;
                    sb.Append('p').Append(p.index).Append(':')
                        .Append(p.metal).Append(',').Append(p.power).Append(',').Append(p.defeated ? 1 : 0)
                        .Append(';');
                }
            }
            if (board.units != null)
            {
                foreach (var u in board.units.OrderBy(x => x.unitId))
                {
                    if (u == null) continue;
                    sb.Append('u').Append(u.unitId).Append(':')
                        .Append(u.ownerIndex).Append(',').Append(u.x).Append(',').Append(u.y).Append(',')
                        .Append(u.hpCur.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                        .Append(u.dead ? 1 : 0).Append(',')
                        .Append(u.actioned ? 1 : 0).Append(',').Append(u.moved ? 1 : 0).Append(',')
                        .Append(u.building ? 1 : 0).Append(',').Append(u.buildingProgress).Append(',')
                        .Append(u.cd).Append(',').Append(u.cding ? 1 : 0).Append(',')
                        .Append(u.factoryBpLeft).Append(',')
                        .Append(u.shdPercent.ToString("0.###", CultureInfo.InvariantCulture))
                        .Append(';');
                }
            }
            if (board.wrecks != null)
            {
                foreach (var w in board.wrecks.OrderBy(x => x.x).ThenBy(x => x.y))
                {
                    if (w == null || w.amount <= 0) continue;
                    sb.Append('w').Append(w.x).Append(',').Append(w.y).Append(':').Append(w.amount).Append(';');
                }
            }
            return Sha16(sb.ToString());
        }

        /// <summary>Host: stamp EndTurn with attachment-domain hash (call after board attach).</summary>
        public void StampEndTurnHash(CommandDto cmd)
        {
            if (cmd == null || cmd.kind != "EndTurn")
                return;
            if (_net.Role != PeerRole.Host || !_authority.InLanBattle)
                return;

            ResultAttachmentDto board = null;
            try
            {
                if (!string.IsNullOrEmpty(cmd.resultAttachmentJson))
                    board = ResultAttachmentCodec.FromJson(cmd.resultAttachmentJson);
                if (!ResultAttachmentCodec.HasPayload(board))
                {
                    board = ResultAttachmentBridge.CaptureBoard(_log);
                    if (ResultAttachmentCodec.HasPayload(board))
                        cmd.resultAttachmentJson = ResultAttachmentCodec.ToJson(board);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Checksum] StampEndTurn capture: " + ex.Message);
            }

            var hash = HashBoard(board);
            cmd.stateHash = hash;
            LastLocalHash = hash;
            _log.LogInfo(
                $"[Checksum] EndTurn stamp ended={cmd.endedPlayerIndex}→{cmd.nextPlayerIndex} hash={hash}");
        }

        /// <summary>
        /// Guest: after EndTurn apply. Wire integrity = HashBoard(attachment)==stateHash.
        /// Recapture drift must NOT Strict-pause — ResultAttachment Apply is lossy vs CaptureBoard
        /// (turn-5 bricks: Apply 87 units still local≠stamp). Host attachment is ADR-001 truth.
        /// </summary>
        public void GuestVerifyEndTurn(CommandDto cmd)
        {
            if (cmd == null || cmd.kind != "EndTurn")
                return;
            if (_net.Role != PeerRole.Guest || !_authority.InLanBattle)
                return;

            if (string.IsNullOrEmpty(cmd.stateHash))
            {
                _log.LogWarning("[Checksum] EndTurn missing stateHash — clearing pause");
                ClearPause();
                return;
            }

            LastRemoteHash = cmd.stateHash;
            ResultAttachmentDto wireBoard = null;
            try
            {
                wireBoard = ResultAttachmentCodec.FromJson(cmd.resultAttachmentJson);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Checksum] EndTurn attachment parse: " + ex.Message);
            }

            if (!ResultAttachmentCodec.HasPayload(wireBoard))
            {
                _log.LogWarning("[Checksum] EndTurn empty attachment — clearing pause");
                ClearPause();
                return;
            }

            var wireHash = HashBoard(wireBoard);
            if (!string.Equals(wireHash, cmd.stateHash, StringComparison.Ordinal))
            {
                // Stamp does not match payload — real integrity failure.
                _log.LogError(
                    $"[Checksum] EndTurn WIRE mismatch stamp={cmd.stateHash} attach={wireHash} next={cmd.nextPlayerIndex}");
                _pendingHostHash = new StateHashDto
                {
                    battleId = cmd.battleId ?? "",
                    turn = cmd.turnsAfter > 0 ? cmd.turnsAfter : cmd.turn,
                    playerIndex = cmd.nextPlayerIndex,
                    hash = cmd.stateHash
                };
                BeginRepairOrPause(_pendingHostHash);
                return;
            }

            ResultAttachmentDto localBoard = null;
            try
            {
                localBoard = ResultAttachmentBridge.CaptureBoard(_log);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Checksum] Guest EndTurn recapture: " + ex.Message);
            }

            var local = HashBoard(localBoard);
            LastLocalHash = local;
            if (!string.Equals(local, wireHash, StringComparison.Ordinal))
            {
                _log.LogWarning(
                    $"[Checksum] ApplyDrift after EndTurn stamp={wireHash} local={local} next={cmd.nextPlayerIndex} " +
                    $"(units wire={wireBoard.units?.Length ?? 0} local={localBoard?.units?.Length ?? 0}) — not pausing");
                BattleSyncTrace.Ev("ChecksumApplyDrift",
                    kind: "EndTurn",
                    nextPlayer: cmd.nextPlayerIndex,
                    turnsAfter: cmd.turnsAfter,
                    detail: $"stamp={wireHash};local={local}");
                // Best-effort second apply; still do not brick input if drift remains.
                try
                {
                    ResultAttachmentBridge.Apply(wireBoard, _log, snapPositions: true);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Checksum] drift re-apply: " + ex.Message);
                }
            }
            else
            {
                _log.LogInfo("[Checksum] EndTurn OK " + local);
            }

            ClearPause();
        }

        private void ClearPause()
        {
            MismatchPaused = false;
            _repairInFlight = false;
            _repairAttempts = 0;
            _pendingHostHash = null;
        }

        [Obsolete("Use ComputeBattleHash / HashBoard")]
        public string ComputePlaceholderHash(int turn, int playerIndex, string battleId)
        {
            return ComputeBattleHash();
        }

        public void HostPublishHash(int turn, int playerIndex, string battleId)
        {
            // Retained for debug tools; LAN play path uses StampEndTurnHash.
            if (_net.Role != PeerRole.Host || SyncContext.SuppressNetworkEmit)
                return;
            if (!_authority.InLanBattle)
                return;

            var hash = ComputeBattleHash();
            LastLocalHash = hash;
            var p = new StateHashDto
            {
                battleId = battleId ?? "",
                turn = turn,
                playerIndex = playerIndex,
                hash = hash
            };
            _net.TryBroadcast(new Envelope
            {
                Type = MsgType.StateHash,
                BattleId = p.battleId,
                PayloadJson = JsonUtil.ToJson(p)
            });
            _log.LogInfo($"[Checksum] Host hash turn={turn} player={playerIndex} hash={hash}");
        }

        private void OnPlayerTurnEnded(Player player, int turn)
        {
            if (!_authority.InLanBattle || player == null)
                return;

            // INV: LAN checkpoints are EndTurn Commands only (attachment + stateHash).
            // TurnEnded Save_General hash races Guest EndTurn apply and false-pauses input.
            return;
        }

        private void OnEnvelope(Envelope env)
        {
            if (env.Type == MsgType.StateHash && _net.Role == PeerRole.Guest)
            {
                // Ignore legacy TurnEnded hashes while InLanBattle — EndTurn owns verify.
                if (_authority.InLanBattle)
                {
                    _log.LogInfo("[Checksum] Ignoring legacy StateHash (EndTurn is checkpoint)");
                    return;
                }
                _pendingHostHash = JsonUtil.FromJson<StateHashDto>(env.PayloadJson);
                LastRemoteHash = _pendingHostHash?.hash;
                TryComparePending();
                return;
            }

            if (env.Type == MsgType.SnapshotRequest && _net.Role == PeerRole.Host)
            {
                HandleSnapshotRequest(env);
                return;
            }

            if (env.Type == MsgType.StateSnapshot && _net.Role == PeerRole.Guest)
            {
                HandleStateSnapshot(env);
            }
        }

        private void TryComparePending()
        {
            if (_pendingHostHash == null || GS_Battle.self == null)
                return;

            if (GS_Battle.self.turns < _pendingHostHash.turn)
                return;

            var board = ResultAttachmentBridge.CaptureBoard(_log);
            var local = HashBoard(board);
            LastLocalHash = local;
            if (string.Equals(local, _pendingHostHash.hash, StringComparison.Ordinal))
            {
                _log.LogInfo("[Checksum] OK " + local);
                MismatchPaused = false;
                _repairInFlight = false;
                _repairAttempts = 0;
                return;
            }

            _log.LogError($"[Checksum] MISMATCH remote={_pendingHostHash.hash} local={local} turn={_pendingHostHash.turn}");
            BeginRepairOrPause(_pendingHostHash);
        }

        private void BeginRepairOrPause(StateHashDto hostHash)
        {
            var repairEnabled = _repairOnMismatch != null && _repairOnMismatch.Value;
            if (repairEnabled && !_repairInFlight && _repairAttempts < MaxRepairAttempts)
            {
                _repairInFlight = true;
                _repairAttempts++;
                MismatchPaused = true;
                var req = new SnapshotRequestDto
                {
                    battleId = hostHash.battleId ?? "",
                    turn = hostHash.turn,
                    reason = "statehash-mismatch"
                };
                _net.Send(new Envelope
                {
                    Type = MsgType.SnapshotRequest,
                    BattleId = req.battleId,
                    PayloadJson = JsonUtil.ToJson(req)
                });
                _log.LogWarning($"[Checksum] Requesting StateSnapshot (attempt {_repairAttempts}/{MaxRepairAttempts})");
                return;
            }

            if (_strict != null && _strict.Value)
                MismatchPaused = true;
            _repairInFlight = false;
        }

        private void HandleSnapshotRequest(Envelope env)
        {
            if (!_authority.InLanBattle)
                return;

            var req = JsonUtil.FromJson<SnapshotRequestDto>(env.PayloadJson);
            ResultAttachmentDto attach;
            try
            {
                attach = ResultAttachmentBridge.CaptureBoard(_log);
            }
            catch (Exception ex)
            {
                _log.LogError("[Checksum] CaptureBoard for snapshot failed: " + ex);
                return;
            }

            var hash = HashBoard(attach);
            LastLocalHash = hash;
            var snap = new StateSnapshotDto
            {
                battleId = req?.battleId ?? LanMpPlugin.Instance?.Lobby?.BattleId ?? "",
                turn = req?.turn ?? (GS_Battle.self != null ? GS_Battle.self.turns : 0),
                playerIndex = GS_Battle.self?.cur_player != null ? GS_Battle.self.cur_player.index : -1,
                hashAfter = hash,
                attachment = attach
            };

            var snapEnv = new Envelope
            {
                Type = MsgType.StateSnapshot,
                BattleId = snap.battleId,
                PayloadJson = JsonUtil.ToJson(snap)
            };
            // Never broadcast snapshot — would force-apply repair onto healthy guests.
            if (string.IsNullOrEmpty(env.SourcePeerId) || !_net.TrySendTo(env.SourcePeerId, snapEnv))
            {
                _log.LogWarning("[Checksum] StateSnapshot directed send failed peer=" + (env.SourcePeerId ?? ""));
                return;
            }
            _log.LogInfo($"[Checksum] Sent StateSnapshot turn={snap.turn} hash={hash} units={attach?.units?.Length ?? 0}");
        }

        private void HandleStateSnapshot(Envelope env)
        {
            // Only the Guest that requested repair should apply — ignore unsolicited snapshots.
            if (!_repairInFlight)
            {
                _log.LogInfo("[Checksum] Ignoring unsolicited StateSnapshot");
                return;
            }

            var snap = JsonUtil.FromJson<StateSnapshotDto>(env.PayloadJson);
            if (snap == null)
            {
                _repairInFlight = false;
                return;
            }

            _log.LogInfo($"[Checksum] Applying StateSnapshot turn={snap.turn} hashAfter={snap.hashAfter}");
            try
            {
                ResultAttachmentBridge.Apply(snap.attachment, _log, snapPositions: true);
            }
            catch (Exception ex)
            {
                _log.LogError("[Checksum] Snapshot apply failed: " + ex);
                _repairInFlight = false;
                if (_strict != null && _strict.Value)
                    MismatchPaused = true;
                return;
            }

            _repairInFlight = false;
            var localBoard = ResultAttachmentBridge.CaptureBoard(_log);
            var local = HashBoard(localBoard);
            LastLocalHash = local;
            LastRemoteHash = snap.hashAfter;

            var expected = !string.IsNullOrEmpty(snap.hashAfter)
                ? snap.hashAfter
                : HashBoard(snap.attachment);

            if (string.Equals(local, expected, StringComparison.Ordinal))
            {
                _log.LogInfo("[Checksum] Repair OK " + local);
                ClearPause();
                return;
            }

            _log.LogWarning(
                $"[Checksum] Repair ApplyDrift local={local} host={expected} — clearing pause (Host snapshot applied)");
            ClearPause();
        }

        private static string Sha16(string raw)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw ?? ""));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant().Substring(0, 16);
            }
        }
    }
}
