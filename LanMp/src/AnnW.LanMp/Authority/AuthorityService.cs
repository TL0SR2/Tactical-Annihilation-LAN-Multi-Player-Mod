using System;
using System.Collections.Generic;
using BepInEx.Logging;
using AnnW.LanMp.Core;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using AnnW.LanMp.Ui;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnnW.LanMp.Authority
{
    public enum SlotKind
    {
        Empty,
        LocalHuman,
        RemoteHuman,
        AI
    }

    public sealed class SlotBinding
    {
        public int PosInd;
        public string OwnerPeerId;
        public SlotKind Kind;
    }

    /// <summary>M03: slot map, input gate, sole battle start gate, LocalHuman view binding.</summary>
    public sealed class AuthorityService : ILanMpModule
    {
        public string Name => "M03-Authority";

        private readonly LobbySession _lobby;
        private readonly NetSession _net;
        private readonly ManualLogSource _log;
        private readonly List<SlotBinding> _slots = new List<SlotBinding>();
        private bool _battleEventsHooked;
        private float _viewBindTimer;

        public bool GatesArmed { get; private set; }
        public bool InLanBattle { get; private set; }
        /// <summary>True after Host MatchEnd / settlement — scene leave must not AbortMatch peers.</summary>
        public bool MatchSettled { get; private set; }
        /// <summary>Last Host MatchEnd payload (Guest/Host may disconnect after; UI reads this).</summary>
        public MatchEndPayload LastMatchEnd { get; private set; }
        public bool LastLocalVictory { get; private set; }
        public string PendingBattleId { get; private set; }

        private bool _abortApplied;
        private bool _openLobbyAfterAbort;
        private bool _openLobbyAfterSettlement;
        /// <summary>Host: drop guests one tick after MatchEnd send so payload can land first.</summary>
        private bool _deferredDropPeersAfterMatchEnd;

        public AuthorityService(LobbySession lobby, NetSession net, ManualLogSource log)
        {
            _lobby = lobby;
            _net = net;
            _log = log;
        }

        public void Start()
        {
            _lobby.OnLobbyStart += OnLobbyStart;
            _net.Subscribe(OnEnvelope);
            _net.OnDisconnected += OnNetDisconnected;
            _net.OnPeerDisconnected += OnPeerDisconnected;
        }

        public void Stop()
        {
            UnhookBattleEvents();
            _net.OnDisconnected -= OnNetDisconnected;
            _net.OnPeerDisconnected -= OnPeerDisconnected;
            GatesArmed = false;
            InLanBattle = false;
            MatchSettled = false;
            LastMatchEnd = null;
            _openLobbyAfterSettlement = false;
            _deferredDropPeersAfterMatchEnd = false;
        }

        public void Tick(float dt)
        {
            if (_deferredDropPeersAfterMatchEnd)
            {
                _deferredDropPeersAfterMatchEnd = false;
                try
                {
                    if (_net.Role == PeerRole.Host)
                        _net.DropAllPeersKeepHosting("match-end");
                    else if (_net.IsConnected)
                        _net.Disconnect("match-end");
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Authority] deferred MatchEnd drop: " + ex.Message);
                }
            }

            if (_openLobbyAfterAbort)
            {
                _openLobbyAfterAbort = false;
                try
                {
                    if (_net.Role == PeerRole.Host)
                    {
                        try { _lobby.ReleaseSeatsForDisconnectedPeers(); }
                        catch (Exception ex) { _log.LogWarning("[Authority] seat reconcile: " + ex.Message); }
                        LanRoomPanel.Open();
                    }
                    else
                        LanLobbyNativePanel.Open();
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Authority] reopen lobby: " + ex.Message);
                }
            }

            if (_openLobbyAfterSettlement)
            {
                _openLobbyAfterSettlement = false;
                try
                {
                    if (_net.Role == PeerRole.Host)
                    {
                        try { _lobby.ReleaseSeatsForDisconnectedPeers(); }
                        catch (Exception ex) { _log.LogWarning("[Authority] seat reconcile: " + ex.Message); }
                        LanRoomPanel.Open();
                    }
                    else
                        LanLobbyNativePanel.Open();
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Authority] reopen lobby after settlement: " + ex.Message);
                }

                try
                {
                    if (LastMatchEnd != null)
                    {
                        MatchSettlementUi.Show(
                            LastMatchEnd,
                            LastLocalVictory,
                            GetLocalHumanSlotIndex(),
                            _net.LocalPeerId);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Authority] settlement UI: " + ex.Message);
                }
            }

            if (!InLanBattle || !GatesArmed)
                return;
            _viewBindTimer += dt;
            if (_viewBindTimer < 0.5f)
                return;
            _viewBindTimer = 0f;
            // Only correct drift; avoid fighting the engine every frame.
            ApplyLocalViewBinding("tick");
        }

        public void OnSceneChanged(string sceneName)
        {
            if (sceneName != null && sceneName.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (_lobby.StartAuthorized)
                {
                    InLanBattle = true;
                    _abortApplied = false;
                    MatchSettled = false;
                    LastMatchEnd = null;
                    MatchSettlementUi.Hide();
                    ArmGatesFromDraft();
                    ApplyBattleIdOverride();
                    HookBattleEvents();
                    ApplyLocalViewBinding("scene");
                    _log.LogInfo("[Authority] InLanBattle=true gates armed + view bound");
                    BattleSyncTrace.SetRole(_net.Role, _lobby.BattleId ?? PendingBattleId);
                    BattleSyncTrace.Ev("BattleEnter", detail: "InLanBattle");
                }
            }
            else if (InLanBattle && !_abortApplied && !MatchSettled)
            {
                // Left battle without AbortMatch (vanilla quit path) — notify peer then clear.
                _log.LogWarning("[Authority] Left Battle scene while LAN live — aborting");
                var host = _net.Role == PeerRole.Host;
                AbortMatch(host ? "host-left" : "guest-left", "scene-leave", broadcast: host, loadMenu: false);
                if (!host && _net.IsConnected)
                {
                    try { _net.Disconnect("scene-leave"); }
                    catch { /* ignore */ }
                }
            }
            else if (MatchSettled || _abortApplied)
            {
                UnhookBattleEvents();
                InLanBattle = false;
                GatesArmed = false;
            }
        }

        public int? GetLocalHumanSlotIndex()
        {
            foreach (var s in _slots)
            {
                if (s.Kind == SlotKind.LocalHuman)
                    return s.PosInd;
            }
            return null;
        }

        /// <summary>Peer that owns a draft/battle seat index, or null if AI / unbound.</summary>
        public string GetOwnerPeerIdForSeat(int seatIndex)
        {
            foreach (var s in _slots)
            {
                if (s.PosInd == seatIndex)
                    return s.OwnerPeerId;
            }
            return null;
        }

        public Player TryGetLocalHumanPlayer()
        {
            var battle = GS_Battle.self;
            if (battle?.all_player?.players == null)
                return null;
            var slot = GetLocalHumanSlotIndex();
            if (!slot.HasValue)
                return null;
            foreach (var p in battle.all_player.players)
            {
                if (p != null && p.index == slot.Value)
                    return p;
            }
            // Fallback: pos_ind may match list order before RefreshIndex
            if (slot.Value >= 0 && slot.Value < battle.all_player.players.Count)
                return battle.all_player.players[slot.Value];
            return null;
        }

        /// <summary>
        /// Force FOW/display and control flags onto LocalHuman.
        /// Game defaults last_human_player to the first Human (often Host slot).
        /// </summary>
        public void ApplyLocalViewBinding(string reason)
        {
            if (!InLanBattle || !GatesArmed)
                return;
            var battle = GS_Battle.self;
            if (battle == null)
                return;

            var local = TryGetLocalHumanPlayer();
            if (local == null)
                return;

            if (battle.last_human_player != local)
            {
                battle.last_human_player = local;
                _log.LogInfo($"[Authority] last_human_player -> local idx={local.index} ({reason})");
            }

            var cur = battle.cur_player;
            if (cur != null)
            {
                var shouldControl = IsLocalPlayersTurn(cur.index) && !cur.is_ai
                    && !local.defeated
                    && !AnnW.LanMp.Presentation.PresentationContext.ControlGrantPending;
                if (battle.is_player_in_control != shouldControl)
                {
                    battle.is_player_in_control = shouldControl;
                    _log.LogInfo($"[Authority] is_player_in_control={shouldControl} cur={cur.index} ({reason})");
                }
            }
        }

        public bool TryHostStartBattle()
        {
            if (_net.Role != PeerRole.Host)
            {
                _log.LogWarning("[Authority] Only Host can start battle");
                return false;
            }

            if (!_lobby.CanStart)
            {
                _log.LogWarning("[Authority] CanStart=false");
                return false;
            }

            ArmGatesFromDraft();
            if (!GatesArmed)
            {
                _log.LogError("[Authority] Refusing LobbyStart: gates not armed");
                return false;
            }

            var probe = BattleBootstrap.BuildStartGameSetting(_lobby.Draft, _log);
            if (probe == null)
            {
                _log.LogError("[Authority] Map bootstrap failed ? fix mapId before start");
                return false;
            }

            var seed = Environment.TickCount;
            _lobby.AuthorizeAndBroadcastStart(seed);
            return true;
        }

        public bool IsLocalPlayersTurn(int currentPlayerIndex)
        {
            if (!InLanBattle || !GatesArmed)
                return true;
            if (IsLocalHumanDefeated())
                return false;
            var slot = GetLocalHumanSlotIndex();
            if (!slot.HasValue)
                return true;
            return InputGateRules.IsLocalPlayersTurn(InLanBattle, GatesArmed, currentPlayerIndex, slot.Value);
        }

        /// <summary>Local human seat is wiped — stay in battle as spectator (INV-VIEW).</summary>
        public bool IsLocalHumanDefeated()
        {
            if (!InLanBattle || !GatesArmed)
                return false;
            var p = TryGetLocalHumanPlayer();
            return p != null && p.defeated;
        }

        public bool ShouldBlockLocalInput(int currentPlayerIndex)
        {
            if (IsLocalHumanDefeated())
                return true;
            return InputGateRules.ShouldBlockLocalInput(
                InLanBattle,
                GatesArmed,
                AnnW.LanMp.Sync.SyncContext.ApplyingRemoteCommand,
                IsLocalPlayersTurn(currentPlayerIndex));
        }

        public void BroadcastMatchEnd(bool victory, string reason)
        {
            if (_net.Role != PeerRole.Host)
                return;
            if (MatchSettled)
                return;

            var results = new System.Collections.Generic.List<SeatMatchResultDto>();
            var battle = GS_Battle.self;
            if (battle?.all_player?.players != null)
            {
                // Per-seat defeated for spectate truth; winner assigned by faction below
                // (defeated Guest on a winning team still gets MatchEnd victory).
                foreach (var p in battle.all_player.players)
                {
                    if (p == null || p.fraction == Fraction.NEUTRAL)
                        continue;
                    var owner = GetOwnerPeerIdForSeat(p.index);
                    results.Add(new SeatMatchResultDto
                    {
                        playerIndex = p.index,
                        defeated = p.defeated,
                        winner = false,
                        fraction = (int)p.fraction,
                        ownerPeerId = owner ?? ""
                    });
                }
            }

            var winnerFrac = MatchEndRules.AssignFactionWinners(results);

            var pld = new MatchEndPayload
            {
                // Legacy Host-seat EndGame(bool) only — ResolveLocalVictory ignores this for Guests.
                victory = victory,
                victoryFlag = victory,
                reason = reason ?? "",
                battleId = _lobby.BattleId ?? "",
                winnerFraction = winnerFrac,
                results = results.ToArray()
            };

            // Deliver payload while the link still works; peers may disconnect immediately after.
            if (_net.IsConnected)
            {
                try
                {
                    _net.TryBroadcast(new Envelope
                    {
                        Type = MsgType.MatchEnd,
                        BattleId = pld.battleId,
                        PayloadJson = JsonUtil.ToJson(pld)
                    });
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Authority] MatchEnd broadcast: " + ex.Message);
                }
            }

            _log.LogInfo(
                $"[Authority] MatchEnd victory={victory} winnerFrac={winnerFrac} seats={results.Count} reason={reason}");
            BattleSyncTrace.Ev("MatchEnd", detail: "victory=" + victory + " frac=" + winnerFrac + " " + reason);
            BattleSyncTrace.EndBattleSession("MatchEnd");
            ApplyMatchSettlementLocal(pld, dropPeers: true);
        }

        /// <summary>Mark LAN battle settled so quit/scene-leave does not AbortMatch remaining peers.</summary>
        public void NoteMatchSettled()
        {
            MatchSettled = true;
            _log.LogInfo("[Authority] MatchSettled=true (quit will not abort peers)");
        }

        /// <summary>
        /// Cache settlement, leave battle scene, optional peer drop — no vanilla in-battle EndGame UI.
        /// INV: Host-only judge already encoded in <paramref name="end"/> (ADR-001 / ADR-004).
        /// </summary>
        private void ApplyMatchSettlementLocal(MatchEndPayload end, bool dropPeers)
        {
            if (end == null)
                return;
            if (MatchSettled)
                return;

            // Resolve while GS_Battle / seats still exist — per local seat/fraction, not Host victory bool.
            LastLocalVictory = ResolveLocalMatchVictory(end);
            LastMatchEnd = end;
            NoteMatchSettled();

            _log.LogInfo(
                $"[Authority] Local settlement victory={LastLocalVictory} role={_net.Role} " +
                $"(HostFlag={end.victory} winnerFrac={end.winnerFraction})");

            // Immediate feedback — mid-defeat spectate never settles; only MatchEnd reaches here.
            try
            {
                var row = MatchEndRules.FindLocalResult(
                    end, GetLocalHumanSlotIndex(), _net.LocalPeerId);
                if (LastLocalVictory && row != null && row.defeated)
                    UiFeedback.Push("阵营胜利（本席已淘汰·观战至终局）");
                else if (LastLocalVictory)
                    UiFeedback.Push("战斗胜利");
                else
                    UiFeedback.Push("战斗失败");
            }
            catch { /* ignore */ }

            UnhookBattleEvents();
            InLanBattle = false;
            GatesArmed = false;
            PendingBattleId = null;
            _lobby.ClearBattleAuthorization();

            // Defer TCP drop one tick — MatchEnd bytes must reach Guests first.
            if (dropPeers)
                _deferredDropPeersAfterMatchEnd = true;

            try
            {
                var scene = SceneManager.GetActiveScene().name ?? "";
                if (scene.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) >= 0)
                    SceneManager.LoadScene("ANNW_Menu");
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Authority] MatchEnd LoadScene: " + ex.Message);
            }

            _openLobbyAfterSettlement = true;
            _log.LogInfo(
                $"[Authority] Match settlement localVictory={LastLocalVictory} — left battle, deferDrop={dropPeers}");
        }

        /// <summary>Abort in-battle session (host left / guest left / leave). Ends battle UI and returns to lobby.</summary>
        public void AbortMatch(string reason, string detail, bool broadcast, bool loadMenu = true)
        {
            if (broadcast && _net.Role == PeerRole.Host && _net.IsConnected)
            {
                var dto = new MatchAbortDto
                {
                    reason = reason ?? "abort",
                    detail = detail ?? "",
                    battleId = _lobby.BattleId ?? PendingBattleId ?? ""
                };
                try
                {
                    _net.TryBroadcast(new Envelope
                    {
                        Type = MsgType.MatchAbort,
                        BattleId = dto.battleId,
                        PayloadJson = JsonUtil.ToJson(dto)
                    });
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Authority] MatchAbort send: " + ex.Message);
                }
            }

            ApplyMatchAbortLocal(reason, detail, loadMenu);
        }

        private void ApplyMatchAbortLocal(string reason, string detail, bool loadMenu = true)
        {
            if (_abortApplied && !InLanBattle)
                return;

            _abortApplied = true;
            _log.LogInfo($"[Authority] MatchAbort reason={reason} detail={detail} loadMenu={loadMenu}");
            BattleSyncTrace.Ev("MatchAbort", detail: reason + ":" + detail);
            BattleSyncTrace.EndBattleSession("MatchAbort:" + reason);
            UnhookBattleEvents();
            InLanBattle = false;
            GatesArmed = false;
            PendingBattleId = null;
            _lobby.ClearBattleAuthorization();

            var msg = MapAbortMessage(reason, detail);
            if (!string.IsNullOrEmpty(msg))
                UiFeedback.Push(msg);

            if (loadMenu)
            {
                try
                {
                    var scene = SceneManager.GetActiveScene().name ?? "";
                    if (scene.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) >= 0)
                        SceneManager.LoadScene("ANNW_Menu");
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Authority] Abort LoadScene: " + ex.Message);
                }

                // Defer lobby open one tick — scene may still be unloading.
                _openLobbyAfterAbort = true;
            }
        }

        /// <summary>Call before vanilla DoQuitOut so peer is notified; vanilla still loads menu.</summary>
        public void NotifyLeavingBattle(string reason)
        {
            if ((!InLanBattle && string.IsNullOrEmpty(PendingBattleId)) || MatchSettled)
                return;

            var host = _net.Role == PeerRole.Host;
            if (host)
            {
                // Host exit ends the match for everyone (sim lives on Host).
                AbortMatch("host-left", reason ?? "quit-out", broadcast: true, loadMenu: false);
                return;
            }

            // Guest exit: tear down local session only — Host continues (seat→AI).
            _log.LogInfo("[Authority] Guest leaving battle — Host keeps match reason=" + (reason ?? ""));
            ApplyMatchAbortLocal("leave-room", reason ?? "quit-out", loadMenu: false);
            try { _net.Disconnect(reason ?? "quit-out"); }
            catch { /* ignore */ }
        }

        private static string MapAbortMessage(string reason, string detail)
        {
            switch (reason)
            {
                case "host-left":
                    return "主机已离开，对局结束";
                case "guest-left":
                    return "有客机离开，对局结束";
                case "guest-seat-ai":
                    return "客机已离开，该席位由 AI 接管";
                case "leave-room":
                    return "已离开对局";
                case "disconnect":
                case "remote-disconnect":
                case "remote-eof":
                case "read-error":
                case "heartbeat-timeout":
                case "heartbeat-send-fail":
                case "plugin-stop":
                case "app-quit":
                    return "连接已断开，对局结束";
                case "broadcast-failed":
                    return "同步失败（操作未能发给全体客机），对局结束";
                default:
                    return string.IsNullOrEmpty(detail)
                        ? ("对局中止：" + (reason ?? "unknown"))
                        : detail;
            }
        }

        private void OnPeerDisconnected(string peerId, string reason)
        {
            // Lobby seat release is LobbySession's job.
            if (_net.Role != PeerRole.Host || !InLanBattle || MatchSettled)
                return;

            // Guest quit / drop: keep Host sim running — convert that seat to AI (spectate design).
            _log.LogWarning("[Authority] Guest left mid-battle peer=" + peerId +
                            " — converting seat to AI (match continues)");
            try
            {
                HandleGuestLeftContinue(peerId, reason);
            }
            catch (Exception ex)
            {
                _log.LogError("[Authority] HandleGuestLeftContinue: " + ex);
            }
        }

        /// <summary>
        /// Guest TCP gone: RemoteHuman → AI, optional EndTurn if it was their turn.
        /// Does not AbortMatch (Host exit still ends the match).
        /// </summary>
        private void HandleGuestLeftContinue(string peerId, string reason)
        {
            SlotBinding slot = null;
            foreach (var s in _slots)
            {
                if (s != null && s.Kind == SlotKind.RemoteHuman &&
                    string.Equals(s.OwnerPeerId, peerId, StringComparison.Ordinal))
                {
                    slot = s;
                    break;
                }
            }

            Player player = null;
            if (slot != null)
            {
                slot.Kind = SlotKind.AI;
                slot.OwnerPeerId = null;
                player = TryGetPlayerByIndex(slot.PosInd);
            }
            else
            {
                // Draft fallback when _slots missed peer (map rebuild edge).
                var draft = _lobby.Draft;
                if (draft?.seats != null)
                {
                    for (var i = 0; i < draft.seats.Length; i++)
                    {
                        var seat = draft.seats[i];
                        if (seat != null && string.Equals(seat.peerId, peerId, StringComparison.Ordinal))
                        {
                            player = TryGetPlayerByIndex(i);
                            break;
                        }
                    }
                }
            }

            if (player != null && !player.defeated)
            {
                player.is_ai = true;
                if (player.ai == null)
                {
                    try
                    {
                        AccessTools.Method(typeof(Player), "InitAI")?.Invoke(player, null);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning("[Authority] InitAI: " + ex.Message);
                    }
                }
            }

            UiFeedback.Push(MapAbortMessage("guest-seat-ai", peerId));
            BattleSyncTrace.Ev("GuestLeftContinue", detail: peerId + ":" + (reason ?? ""));

            var battle = GS_Battle.self;
            if (player != null && battle?.cur_player != null &&
                battle.cur_player.index == player.index && !player.defeated)
            {
                try
                {
                    var sync = LanMpPlugin.Instance?.Sync;
                    if (sync != null)
                    {
                        _log.LogInfo("[Authority] Forcing EndTurn after guest drop on their turn");
                        sync.SubmitIntent(sync.BuildIntent("EndTurn"), guestOptimisticApply: false);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Authority] Force EndTurn: " + ex.Message);
                }
            }
        }

        private static Player TryGetPlayerByIndex(int index)
        {
            var battle = GS_Battle.self;
            if (battle?.all_player?.players == null || index < 0)
                return null;
            foreach (var p in battle.all_player.players)
            {
                if (p != null && p.index == index)
                    return p;
            }
            if (index < battle.all_player.players.Count)
                return battle.all_player.players[index];
            return null;
        }

        private void OnNetDisconnected(string reason)
        {
            // Settled MatchEnd already left battle / may drop peers — not an abort.
            if (MatchSettled)
                return;

            if (!InLanBattle && !_abortApplied)
            {
                // Lobby-only disconnect: nothing to abort in battle.
                return;
            }
            if (!InLanBattle && _abortApplied)
                return;

            // Host peer drops no longer FireDisconnected (see NetSession) — handled by OnPeerDisconnected.
            if (_net.Role == PeerRole.Host)
                return;

            // Guest lost host / connection (Role may already be None after Disconnect).
            var hostGone = reason != null && (
                reason.IndexOf("host", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason == "remote-disconnect" ||
                reason == "remote-eof" ||
                reason == "read-error" ||
                reason == "heartbeat-timeout" ||
                reason == "heartbeat-send-fail" ||
                reason == "plugin-stop" ||
                reason == "app-quit" ||
                reason == "match-abort-peer-left");
            ApplyMatchAbortLocal(hostGone ? "host-left" : "disconnect", reason, loadMenu: true);
        }

        private void HookBattleEvents()
        {
            if (_battleEventsHooked)
                return;
            try
            {
                BattleEventBus.self.OnPlayerTurnStarted += OnPlayerTurnStarted;
                BattleEventBus.self.OnPlayerTurnEnded += OnPlayerTurnEnded;
                BattleEventBus.self.OnPlayerDefeat += OnPlayerDefeat;
                _battleEventsHooked = true;
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Authority] HookBattleEvents: " + ex.Message);
            }
        }

        private void UnhookBattleEvents()
        {
            if (!_battleEventsHooked)
                return;
            try
            {
                BattleEventBus.self.OnPlayerTurnStarted -= OnPlayerTurnStarted;
                BattleEventBus.self.OnPlayerTurnEnded -= OnPlayerTurnEnded;
                BattleEventBus.self.OnPlayerDefeat -= OnPlayerDefeat;
            }
            catch { /* ignore */ }
            _battleEventsHooked = false;
        }

        private void OnPlayerDefeat(Player defeated)
        {
            if (!InLanBattle || defeated == null)
                return;
            var local = TryGetLocalHumanPlayer();
            if (local == null || defeated.index != local.index)
                return;

            _log.LogInfo("[Authority] Local human defeated — entering spectate idx=" + local.index);
            // Spectate only — no MatchEnd / settlement until Host EndGame (faction may still win).
            UiFeedback.Push("已战败，进入观战");
            ApplyLocalViewBinding("local-defeated");
            AnnW.LanMp.Presentation.RemoteTurnPresentation.ClearSpectateUxOverlays(
                "local-defeated", _log);
        }

        private void OnPlayerTurnStarted(Player player, int turn)
        {
            ApplyLocalViewBinding("turn-start");
            // Host+Guest: entering a foreign/AI seat must drop local MOVE_SELECT threat overlays
            // (INV-VIEW spectate). Otherwise hover shows own attack range instead of enemy threat.
            if (IsLocalHumanDefeated() ||
                (player != null && (player.is_ai || !IsLocalPlayersTurn(player.index))))
            {
                AnnW.LanMp.Presentation.RemoteTurnPresentation.ClearSpectateUxOverlays(
                    "turn-start-spectate", _log);
            }
        }

        private void OnPlayerTurnEnded(Player player, int turn)
        {
            ApplyLocalViewBinding("turn-end");
            if (player != null && IsLocalPlayersTurn(player.index) && !player.is_ai)
            {
                AnnW.LanMp.Presentation.RemoteTurnPresentation.ClearSpectateUxOverlays(
                    "local-turn-end", _log);
            }
        }

        private void OnLobbyStart(LobbyStartPayload payload)
        {
            PendingBattleId = payload.battleId;
            _abortApplied = false;
            ArmGatesFromDraft();

            if (!BattleBootstrap.TryApplyLobbyStart(payload, _net, _log))
            {
                _log.LogError("[Authority] Abort LoadScene — bootstrap failed");
                return;
            }

            InLanBattle = true;
            _log.LogInfo("[Authority] Loading ANNW_Battle after LobbyStart");
            try
            {
                SceneManager.LoadScene("ANNW_Battle");
            }
            catch (Exception ex)
            {
                _log.LogError("[Authority] LoadScene failed: " + ex);
            }
        }

        private void ApplyBattleIdOverride()
        {
            if (GS_Battle.self == null || string.IsNullOrEmpty(PendingBattleId))
                return;
            GS_Battle.self.battle_id = PendingBattleId;
            _log.LogInfo("[Authority] Overrode GS_Battle.battle_id=" + PendingBattleId);
        }

        private void ArmGatesFromDraft()
        {
            _slots.Clear();
            var draft = _lobby.Draft;

            void Add(int pos, string peer)
            {
                var kind = SlotKind.Empty;
                if (!string.IsNullOrEmpty(peer))
                    kind = peer == _net.LocalPeerId ? SlotKind.LocalHuman : SlotKind.RemoteHuman;
                _slots.Add(new SlotBinding { PosInd = pos, OwnerPeerId = peer, Kind = kind });
            }

            if (draft?.seats != null)
            {
                for (var i = 0; i < draft.seats.Length; i++)
                {
                    var seat = draft.seats[i];
                    if (LobbySeatLogic.GetState(seat) != LobbySeatState.HumanSeated)
                        continue;
                    Add(i, seat.peerId);
                }
            }
            else
            {
                var hostPeer = string.IsNullOrEmpty(draft?.hostPeerId)
                    ? (_net.Role == PeerRole.Host ? _net.LocalPeerId : _net.RemotePeerId)
                    : draft.hostPeerId;
                var guestPeer = string.IsNullOrEmpty(draft?.guestPeerId)
                    ? (_net.Role == PeerRole.Guest ? _net.LocalPeerId : _net.RemotePeerId)
                    : draft.guestPeerId;
                Add(draft?.hostSlotIndex ?? 0, hostPeer);
                if (draft != null && draft.guestSlotIndex >= 0)
                    Add(draft.guestSlotIndex, guestPeer);
            }

            GatesArmed = _slots.Exists(s => s.Kind == SlotKind.LocalHuman);
            _log.LogInfo($"[Authority] GatesArmed={GatesArmed} slots={_slots.Count} localSlot={GetLocalHumanSlotIndex()}");
        }

        private void OnEnvelope(Envelope env)
        {
            if (env.Type == MsgType.MatchAbort)
            {
                var p = JsonUtil.FromJson<MatchAbortDto>(env.PayloadJson);
                ApplyMatchAbortLocal(p?.reason ?? "abort", p?.detail, loadMenu: true);
                return;
            }

            if (env.Type != MsgType.MatchEnd)
                return;
            var end = JsonUtil.FromJson<MatchEndPayload>(env.PayloadJson);
            if (end == null)
            {
                _log.LogWarning("[Authority] MatchEnd payload parse failed — settling with fallback");
                end = new MatchEndPayload
                {
                    victory = false,
                    victoryFlag = false,
                    reason = "parse-fail",
                    battleId = env.BattleId ?? ""
                };
            }
            _log.LogInfo($"[Authority] MatchEnd received victory={end.victory} reason={end.reason}");
            BattleSyncTrace.Ev("MatchEnd", detail: "recv victory=" + end.victory + " " + (end.reason ?? ""));
            BattleSyncTrace.EndBattleSession("MatchEnd");
            // No vanilla EndGame — leave battle and show settlement from payload (network may drop).
            ApplyMatchSettlementLocal(end, dropPeers: true);
        }

        private bool ResolveLocalMatchVictory(MatchEndPayload end)
        {
            int? localFrac = null;
            try
            {
                var p = TryGetLocalHumanPlayer();
                if (p != null)
                    localFrac = (int)p.fraction;
            }
            catch { /* battle may be mid-teardown */ }

            // Guest must never use Host EndGame(bool); Host may as last resort only.
            var allowHostFlag = _net.Role == PeerRole.Host;
            return MatchEndRules.ResolveLocalVictory(
                end,
                GetLocalHumanSlotIndex(),
                _net.LocalPeerId,
                localFrac,
                allowHostFlag);
        }
    }
}
