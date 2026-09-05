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

    [Serializable]
    public class SeatMatchResultDto
    {
        public int playerIndex;
        public bool defeated;
        public bool winner;
        public int fraction;
    }

    [Serializable]
    public class MatchEndPayload
    {
        public bool victory;
        public bool victoryFlag;
        public string reason;
        public string battleId;
        public int winnerFraction = -1;
        public SeatMatchResultDto[] results;
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
        public string PendingBattleId { get; private set; }

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
        }

        public void Stop()
        {
            UnhookBattleEvents();
            _net.OnDisconnected -= OnNetDisconnected;
            GatesArmed = false;
            InLanBattle = false;
        }

        public void Tick(float dt)
        {
            if (_openLobbyAfterAbort)
            {
                _openLobbyAfterAbort = false;
                try
                {
                    if (_net.Role == PeerRole.Host)
                        LanRoomPanel.Open();
                    else
                        LanLobbyNativePanel.Open();
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Authority] reopen lobby: " + ex.Message);
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
                    ArmGatesFromDraft();
                    ApplyBattleIdOverride();
                    HookBattleEvents();
                    ApplyLocalViewBinding("scene");
                    _log.LogInfo("[Authority] InLanBattle=true gates armed + view bound");
                    BattleSyncTrace.SetRole(_net.Role, _lobby.BattleId ?? PendingBattleId);
                    BattleSyncTrace.Ev("BattleEnter", detail: "InLanBattle");
                }
            }
            else if (InLanBattle && !_abortApplied)
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
            else
            {
                UnhookBattleEvents();
                InLanBattle = false;
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
            var slot = GetLocalHumanSlotIndex();
            if (!slot.HasValue)
                return true;
            return InputGateRules.IsLocalPlayersTurn(InLanBattle, GatesArmed, currentPlayerIndex, slot.Value);
        }

        public bool ShouldBlockLocalInput(int currentPlayerIndex)
        {
            return InputGateRules.ShouldBlockLocalInput(
                InLanBattle,
                GatesArmed,
                AnnW.LanMp.Sync.SyncContext.ApplyingRemoteCommand,
                IsLocalPlayersTurn(currentPlayerIndex));
        }

        public void BroadcastMatchEnd(bool victory, string reason)
        {
            if (_net.Role != PeerRole.Host || !_net.IsConnected)
                return;

            var results = new System.Collections.Generic.List<SeatMatchResultDto>();
            var battle = GS_Battle.self;
            var winnerFrac = -1;
            if (battle?.all_player?.players != null)
            {
                foreach (var p in battle.all_player.players)
                {
                    if (p == null || p.fraction == Fraction.NEUTRAL)
                        continue;
                    results.Add(new SeatMatchResultDto
                    {
                        playerIndex = p.index,
                        defeated = p.defeated,
                        winner = !p.defeated && victory,
                        fraction = (int)p.fraction
                    });
                    if (!p.defeated && victory)
                        winnerFrac = (int)p.fraction;
                }
            }

            var pld = new MatchEndPayload
            {
                victory = victory,
                victoryFlag = victory,
                reason = reason ?? "",
                battleId = _lobby.BattleId ?? "",
                winnerFraction = winnerFrac,
                results = results.ToArray()
            };
            _net.Send(new Envelope
            {
                Type = MsgType.MatchEnd,
                BattleId = pld.battleId,
                PayloadJson = JsonUtil.ToJson(pld)
            });
            _log.LogInfo(
                $"[Authority] MatchEnd victory={victory} winnerFrac={winnerFrac} seats={results.Count} reason={reason}");
            BattleSyncTrace.Ev("MatchEnd", detail: "victory=" + victory + " frac=" + winnerFrac + " " + reason);
            BattleSyncTrace.EndBattleSession("MatchEnd");
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
                    _net.Send(new Envelope
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

        private bool _abortApplied;
        private bool _openLobbyAfterAbort;

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
            if (!InLanBattle && string.IsNullOrEmpty(PendingBattleId))
                return;

            var host = _net.Role == PeerRole.Host;
            AbortMatch(
                host ? "host-left" : "guest-left",
                reason ?? "quit-out",
                broadcast: host,
                loadMenu: false);

            if (!host)
            {
                try { _net.Disconnect(reason ?? "quit-out"); }
                catch { /* ignore */ }
            }
        }

        private static string MapAbortMessage(string reason, string detail)
        {
            switch (reason)
            {
                case "host-left":
                    return "主机已离开，对局结束";
                case "guest-left":
                    return "客机已离开，对局结束";
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
                    return "同步失败（操作未能发给对方），对局结束";
                default:
                    return string.IsNullOrEmpty(detail)
                        ? ("对局中止：" + (reason ?? "unknown"))
                        : detail;
            }
        }

        private void OnNetDisconnected(string reason)
        {
            if (!InLanBattle && !_abortApplied)
            {
                // Lobby-only disconnect: nothing to abort in battle.
                return;
            }
            if (!InLanBattle && _abortApplied)
                return;

            // Host still hosting after guest drop: DropGuestKeepHosting fires OnDisconnected with Role=Host.
            if (_net.Role == PeerRole.Host)
            {
                AbortMatch("guest-left", reason, broadcast: false, loadMenu: true);
                return;
            }

            // Guest lost host / connection (Role may already be None after Disconnect).
            var hostGone = reason != null && (
                reason.IndexOf("host", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason == "remote-disconnect" ||
                reason == "remote-eof" ||
                reason == "read-error" ||
                reason == "heartbeat-timeout" ||
                reason == "heartbeat-send-fail" ||
                reason == "plugin-stop" ||
                reason == "app-quit");
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
            }
            catch { /* ignore */ }
            _battleEventsHooked = false;
        }

        private void OnPlayerTurnStarted(Player player, int turn)
        {
            ApplyLocalViewBinding("turn-start");
            // Host+Guest: entering a foreign/AI seat must drop local MOVE_SELECT threat overlays
            // (INV-VIEW spectate). Otherwise hover shows own attack range instead of enemy threat.
            if (player != null && (player.is_ai || !IsLocalPlayersTurn(player.index)))
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
            _log.LogInfo($"[Authority] MatchEnd received victory={end?.victory} reason={end?.reason}");
            BattleSyncTrace.Ev("MatchEnd", detail: "recv victory=" + (end?.victory ?? false) + " " + (end?.reason ?? ""));
            try
            {
                if (SingletonMono<SS_ANNW_Game>.self != null && end != null)
                {
                    var mi = AccessTools.Method(typeof(SS_ANNW_Game), "EndGame", new[] { typeof(bool) });
                    mi?.Invoke(SingletonMono<SS_ANNW_Game>.self, new object[] { end.victory });
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Authority] EndGame apply: " + ex.Message);
            }
            BattleSyncTrace.EndBattleSession("MatchEnd");
        }
    }
}
