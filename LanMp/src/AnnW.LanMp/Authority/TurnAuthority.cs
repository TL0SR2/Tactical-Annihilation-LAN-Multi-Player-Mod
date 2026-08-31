using System;
using System.Collections;
using AnnW.LanMp.Core;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using BepInEx.Logging;
using UnityEngine;

namespace AnnW.LanMp.Authority
{
    /// <summary>
    /// ADR-004: Host-only turn cursor authority + EndTurn Command with nextPlayer.
    /// INV-T1/T2/T4 — Guest never discovers next operator via MannualEndTurn.
    /// </summary>
    public sealed class TurnAuthority : ILanMpModule
    {
        public string Name => "M03b-TurnAuthority";

        private readonly NetSession _net;
        private readonly AuthorityService _authority;
        private readonly ManualLogSource _log;
        private bool _hooked;

        private int _pendingEndedPlayer = -1;
        private int _pendingEndedTurn = -1;
        private bool _hasPendingEnd;

        /// <summary>Prepared EndTurn after Host OnPlayerTurnStarted (under suppress or for bus emit).</summary>
        public CommandDto PendingEndTurnCommand { get; private set; }

        /// <summary>Signaled when a Host turn transition produced an EndTurn payload.</summary>
        public bool EndTurnReady { get; private set; }

        public event Action<CommandDto> OnHostEndTurnReady;

        /// <summary>Guest: waiting for Host EndTurn while non-local seat is active.</summary>
        public bool GuestWatching { get; private set; }
        private bool _guestWatchSignal;

        public TurnAuthority(NetSession net, AuthorityService authority, ManualLogSource log)
        {
            _net = net;
            _authority = authority;
            _log = log;
        }

        public void Start() { }

        public void Stop()
        {
            Unhook();
            ClearPending();
            GuestWatching = false;
            _guestWatchSignal = false;
        }

        public void Tick(float dt) { }

        public void OnSceneChanged(string sceneName)
        {
            var isBattle = sceneName != null &&
                           sceneName.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) >= 0;
            var lan = isBattle && _authority != null &&
                      (_authority.InLanBattle ||
                       (LanMpPlugin.Instance?.Lobby != null && LanMpPlugin.Instance.Lobby.StartAuthorized));
            if (lan)
                Hook();
            else
            {
                Unhook();
                ClearPending();
                GuestWatching = false;
            }
        }

        private void Hook()
        {
            if (_hooked)
                return;
            try
            {
                BattleEventBus.self.OnPlayerTurnEnded += OnPlayerTurnEnded;
                BattleEventBus.self.OnPlayerTurnStarted += OnPlayerTurnStarted;
                _hooked = true;
                _log.LogInfo("[TurnAuth] hooked TurnEnded/Started");
            }
            catch (Exception ex)
            {
                _log.LogWarning("[TurnAuth] hook: " + ex.Message);
            }
        }

        private void Unhook()
        {
            if (!_hooked)
                return;
            try
            {
                BattleEventBus.self.OnPlayerTurnEnded -= OnPlayerTurnEnded;
                BattleEventBus.self.OnPlayerTurnStarted -= OnPlayerTurnStarted;
            }
            catch { /* ignore */ }
            _hooked = false;
        }

        private void ClearPending()
        {
            _hasPendingEnd = false;
            _pendingEndedPlayer = -1;
            _pendingEndedTurn = -1;
            PendingEndTurnCommand = null;
            EndTurnReady = false;
        }

        private void OnPlayerTurnEnded(Player player, int turn)
        {
            if (_authority == null || !_authority.InLanBattle)
                return;
            if (_net.Role != PeerRole.Host)
                return;
            _pendingEndedPlayer = player != null ? player.index : -1;
            _pendingEndedTurn = turn;
            _hasPendingEnd = true;
        }

        private void OnPlayerTurnStarted(Player player, int turn)
        {
            if (_authority == null || !_authority.InLanBattle)
                return;
            if (_net.Role != PeerRole.Host)
                return;
            // Opening turn: no prior EndTurn to broadcast.
            if (!_hasPendingEnd)
                return;

            var ended = _pendingEndedPlayer;
            var turnBefore = _pendingEndedTurn;
            _hasPendingEnd = false;

            // Cursor fields only here — board capture is deferred to Accept/broadcast path so
            // TriggerPlayerTurnStarted stays light (no sync JSON on the turn-bus stack).
            var cmd = new CommandDto
            {
                battleId = LanMpPlugin.Instance?.Lobby?.BattleId ?? "",
                kind = "EndTurn",
                playerIndex = ended,
                endedPlayerIndex = ended,
                turnBefore = turnBefore,
                turn = turnBefore,
                nextPlayerIndex = player != null ? player.index : -1,
                turnsAfter = turn,
                endTurnReason = "turn-started"
            };

            PendingEndTurnCommand = cmd;
            EndTurnReady = true;
            _log.LogInfo(
                $"[TurnAuth] EndTurn ready ended={ended}→next={cmd.nextPlayerIndex} turns {turnBefore}→{turn}");
            BattleSyncTrace.EvCommand("EndTurnReady", cmd);

            // When not suppressed, Sync will broadcast via event (captures board there).
            if (!SyncContext.SuppressNetworkEmit)
                OnHostEndTurnReady?.Invoke(cmd);
        }

        /// <summary>Host: attach board snapshot immediately before broadcast.</summary>
        public void AttachBoardSnapshot(CommandDto cmd)
        {
            if (cmd == null)
                return;
            try
            {
                var board = ResultAttachmentBridge.CaptureBoard(_log);
                if (ResultAttachmentCodec.HasPayload(board))
                    cmd.resultAttachmentJson = ResultAttachmentCodec.ToJson(board);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[TurnAuth] capture: " + ex.Message);
            }
        }

        public CommandDto ConsumePendingEndTurn()
        {
            var cmd = PendingEndTurnCommand;
            PendingEndTurnCommand = null;
            EndTurnReady = false;
            return cmd;
        }

        /// <summary>Guest applies authoritative cursor (INV-T2). Does not call MannualEndTurn.</summary>
        public void ApplyCursorFromCommand(CommandDto cmd)
        {
            if (cmd == null)
                return;
            var battle = GS_Battle.self;
            if (battle?.all_player?.players == null)
                return;

            if (cmd.turnsAfter > 0)
                battle.turns = cmd.turnsAfter;

            var next = cmd.nextPlayerIndex;
            if (next < 0 || next >= battle.all_player.players.Count)
            {
                _log.LogWarning("[TurnAuth] bad nextPlayerIndex=" + next);
                return;
            }

            battle.current_co_index = next;
            var player = battle.all_player.players[next];
            battle.cur_player = player;

            var localIdx = _authority?.GetLocalHumanSlotIndex();
            var isLocal = localIdx.HasValue && player != null && player.index == localIdx.Value;
            battle.is_player_in_control = false;

            if (isLocal && player != null && !player.is_ai)
                battle.last_human_player = player;

            try
            {
                BattleEventBus.self.TriggerFOWDirty();
            }
            catch { /* ignore */ }

            ResultAttachmentBridge.RefreshUnactionedLists(_log);
            _log.LogInfo(
                $"[TurnAuth] Guest cursor → p={next} turn={battle.turns} localControl={isLocal && player != null && !player.is_ai}");
            BattleSyncTrace.EvCommand("CursorSet", cmd,
                detail: "localControl=" + (isLocal && player != null && !player.is_ai));

            AnnW.LanMp.Presentation.RemoteTurnPresentation.OnSeatActivated(
                player, isLocal && player != null && !player.is_ai, _log);

            // Release any prior RemoteWatch; caller may BeginGuestWatchIfNeeded for the new seat.
            _guestWatchSignal = true;
            GuestWatching = false;
        }

        public IEnumerator CoGuestWatchRemoteTurn()
        {
            GuestWatching = true;
            _guestWatchSignal = false;
            var waited = 0f;
            _log.LogInfo("[TurnAuth] Guest RemoteWatch start");
            BattleSyncTrace.Ev("WatchStart",
                curPlayer: GS_Battle.self?.cur_player != null ? GS_Battle.self.cur_player.index : (int?)null,
                turn: GS_Battle.self != null ? GS_Battle.self.turns : (int?)null);
            var cur = GS_Battle.self?.cur_player;
            if (cur != null)
                AnnW.LanMp.Presentation.RemoteTurnPresentation.OnSeatActivated(cur, false, _log);
            while (!_guestWatchSignal && waited < 600f)
            {
                waited += Time.unscaledDeltaTime;
                yield return AnnW.LanMp.Sync.AnnWCoroutine.NextTick;
            }
            GuestWatching = false;
            _guestWatchSignal = false;
            if (waited >= 600f)
            {
                _log.LogWarning("[TurnAuth] RemoteWatch timeout");
                BattleSyncTrace.Ev("WatchTimeout");
            }
            else
            {
                _log.LogInfo("[TurnAuth] RemoteWatch done");
                BattleSyncTrace.Ev("WatchEnd");
            }
        }

        public void SignalGuestWatch()
        {
            _guestWatchSignal = true;
        }

        public void BeginGuestWatchIfNeeded()
        {
            if (_net.Role != PeerRole.Guest || !_authority.InLanBattle)
                return;
            if (GuestWatching)
                return;
            var battle = GS_Battle.self;
            var cur = battle?.cur_player;
            if (cur == null)
                return;
            var local = _authority.GetLocalHumanSlotIndex();
            if (local.HasValue && cur.index == local.Value && !cur.is_ai)
                return; // local human — no watch

            var gc = GameController.self;
            if (gc != null)
                gc.StartCoroutine(CoGuestWatchRemoteTurn());
        }
    }
}
