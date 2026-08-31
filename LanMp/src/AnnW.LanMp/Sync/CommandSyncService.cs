using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using AnnW.LanMp.Authority;
using AnnW.LanMp.Core;
using AnnW.LanMp.Patches;
using AnnW.LanMp.Presentation;
using AnnW.LanMp.Protocol;
using ANNW;
using HarmonyLib;
using UnityEngine;

namespace AnnW.LanMp.Sync
{
    /// <summary>
    /// M04: Intent → Host Validate+Apply → Command (ADR-001 + ADR-004).
    ///
    /// Host emit checklist:
    /// - Move: OnUnitMoved → UnitMoved
    /// - Action: OnPreDoAction+OnUnitActioned → DoAction + attachment
    /// - EndTurn: TurnAuthority OnPlayerTurnStarted → EndTurn(nextPlayer)  [NOT Bus TurnEnded alone]
    /// - Skill / Create / Remove: as before
    /// Guest applies via CommandApplyQueue only; never MannualEndTurn to discover next.
    /// </summary>
    public sealed class CommandSyncService : ILanMpModule
    {
        public string Name => "M04-Sync";

        private readonly NetSession _net;
        private readonly AuthorityService _authority;
        private readonly ConfigEntry<bool> _attachResults;
        private readonly ManualLogSource _log;
        private readonly HashSet<string> _seenIntentIds = new HashSet<string>();
        private readonly HashSet<string> _guestOptimisticDone = new HashSet<string>();
        private bool _eventsHooked;
        private CommandDto _pendingSkillCommand;
        private bool _skillCastSuppressEmit;
        private int _actionEmitDepth;
        private int _pendingActionUnitId = -1;
        private ActionCate _pendingActionCate;
        private Inctor2 _pendingActionTarget;
        private bool _pendingActionHasTarget;
        private string _pendingActionTemplateId;
        private string _lastEmittedDoActionKey;
        private float _lastEmittedDoActionAt;
        private bool _handlingBroadcastFail;
        private CommandApplyQueue _applyQueue;

        /// <summary>Guest: one mutating Intent in flight until matching Command/Nack (ADR-001).</summary>
        private string _guestAwaitIntentId;
        private string _guestAwaitKind;
        private float _guestAwaitSince;
        private IntentDto _guestPendingFollowUp;
        private int _guestUndoAvailable;
        private const float GuestAwaitTimeoutSec = 20f;

        public TurnAuthority TurnAuth { get; set; }

        /// <summary>True when Guest apply queue is drained and no remote apply in flight.</summary>
        public bool IsApplyQueueIdle =>
            (_applyQueue == null || _applyQueue.Count == 0) && !SyncContext.ApplyingRemoteCommand;

        public event Action<IntentNackDto> OnIntentNack;

        public CommandSyncService(
            NetSession net,
            AuthorityService authority,
            ConfigEntry<bool> attachResults,
            ManualLogSource log)
        {
            _net = net;
            _authority = authority;
            _attachResults = attachResults;
            _log = log;
        }

        public void Start()
        {
            _net.Subscribe(OnEnvelope);
            _applyQueue = new CommandApplyQueue(_log, CoApplyQueuedCommand);
            if (TurnAuth != null)
                TurnAuth.OnHostEndTurnReady += OnTurnAuthEndTurnReady;
        }

        public void Stop()
        {
            if (TurnAuth != null)
                TurnAuth.OnHostEndTurnReady -= OnTurnAuthEndTurnReady;
            _applyQueue?.Clear();
            UnhookBattleEvents();
            _seenIntentIds.Clear();
            _guestOptimisticDone.Clear();
            ClearGuestAwait();
        }

        public void Tick(float dt) { }

        public void OnSceneChanged(string sceneName)
        {
            var isBattle = sceneName != null &&
                           sceneName.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) >= 0;
            // Solo / campaign Battle: do NOT hook EventBus (zero LAN side-effects).
            var lanBattle = isBattle && (
                (_authority != null && _authority.InLanBattle) ||
                (LanMpPlugin.Instance?.Lobby != null && LanMpPlugin.Instance.Lobby.StartAuthorized));

            if (lanBattle)
                HookBattleEvents();
            else
            {
                UnhookBattleEvents();
                _seenIntentIds.Clear();
                _guestOptimisticDone.Clear();
                ClearGuestAwait();
            }
        }

        private void HookBattleEvents()
        {
            if (_eventsHooked)
                return;
            try
            {
                // UX / AI / AutoCmd use UnitData.DoAction → PreDoAction + UnitActioned (NOT ActionExecuted).
                BattleEventBus.self.OnPreDoAction += OnPreDoAction;
                BattleEventBus.self.OnUnitActioned += OnUnitActioned;
                BattleEventBus.self.OnActionExecuted += OnActionExecuted;
                BattleEventBus.self.OnUnitMoved += OnUnitMoved;
                BattleEventBus.self.OnSkillCastDone += OnSkillCastDone;
                BattleEventBus.self.OnUnitCreated += OnUnitCreated;
                BattleEventBus.self.OnUnitRemoved += OnUnitRemoved;
                BattleEventBus.self.OnUnitBuildCompleted += OnUnitBuildCompleted;
                // EndTurn emit is TurnAuthority (ADR-004), not OnPlayerTurnEndedBus.
                _eventsHooked = true;
                _log.LogInfo("[Sync] Battle events hooked (PreDo/Actioned/move/skill/create/remove/build)");
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] HookBattleEvents: " + ex.Message);
            }
        }

        private void UnhookBattleEvents()
        {
            if (!_eventsHooked)
                return;
            try
            {
                BattleEventBus.self.OnPreDoAction -= OnPreDoAction;
                BattleEventBus.self.OnUnitActioned -= OnUnitActioned;
                BattleEventBus.self.OnActionExecuted -= OnActionExecuted;
                BattleEventBus.self.OnUnitMoved -= OnUnitMoved;
                BattleEventBus.self.OnSkillCastDone -= OnSkillCastDone;
                BattleEventBus.self.OnUnitCreated -= OnUnitCreated;
                BattleEventBus.self.OnUnitRemoved -= OnUnitRemoved;
                BattleEventBus.self.OnUnitBuildCompleted -= OnUnitBuildCompleted;
            }
            catch { /* ignore */ }
            _eventsHooked = false;
            _actionEmitDepth = 0;
        }

        /// <summary>Guest: RemoteWatch until Host EndTurn (TurnAuthority).</summary>
        public IEnumerator CoGuestWatchRemoteTurn()
        {
            if (TurnAuth != null)
            {
                yield return TurnAuth.CoGuestWatchRemoteTurn();
                yield break;
            }
            yield break;
        }

        private void OnTurnAuthEndTurnReady(CommandDto cmd)
        {
            if (cmd == null || _net.Role != PeerRole.Host)
                return;
            // Bus path (not HostAccept suppress): capture board then broadcast.
            TurnAuth?.ConsumePendingEndTurn();
            TurnAuth?.AttachBoardSnapshot(cmd);
            HostBroadcastCommand(cmd);
        }

        // Removed: OnPlayerTurnEndedBus / EmitEndTurnCommand — ADR-004 TurnAuthority owns EndTurn emit.

        private void OnPreDoAction(UnitData unit, ActionCate cate, GameTileData target)
        {
            if (unit == null)
                return;
            // Always nest-track while hooks are live so mid-action CreateUnit folds into DoAction
            // even when SuppressNetworkEmit (Host AcceptIntent path).
            _actionEmitDepth++;
            _pendingActionUnitId = unit.unit_id;
            _pendingActionCate = cate;
            _pendingActionHasTarget = target != null;
            _pendingActionTarget = target != null ? target.pos : default(Inctor2);
            _pendingActionTemplateId = null;
            try
            {
                var action = unit.GetAction(cate);
                if (action?.train_template?.sd_unit != null)
                    _pendingActionTemplateId = action.train_template.sd_unit.name;
                else if (GS_Battle.self?.ux_unit_template?.sd_unit != null &&
                         (cate == ActionCate.TRAIN || cate == ActionCate.BUILD))
                    _pendingActionTemplateId = GS_Battle.self.ux_unit_template.sd_unit.name;
            }
            catch { /* ignore */ }

            if (!ShouldEmitFromBus())
                return;
        }

        private void OnUnitActioned(UnitData unit, ActionCate cate)
        {
            var hasTarget = _pendingActionHasTarget;
            var target = _pendingActionTarget;
            var templateId = _pendingActionTemplateId;
            var pendingMatched = unit != null &&
                                 _pendingActionUnitId == unit.unit_id &&
                                 _pendingActionCate == cate;

            if (_actionEmitDepth > 0)
                _actionEmitDepth--;

            if (!ShouldEmitFromBus())
                return;
            if (unit == null)
                return;

            if (!pendingMatched)
            {
                hasTarget = false;
                target = default(Inctor2);
                templateId = null;
                try
                {
                    var action = unit.GetAction(cate);
                    if (action?.train_template?.sd_unit != null)
                        templateId = action.train_template.sd_unit.name;
                }
                catch { /* ignore */ }
            }

            EmitDoActionCommand(unit, cate, target, hasTarget, templateId);
        }

        private void OnActionExecuted(UnitData unit, ActionCate cate, GameTileData target)
        {
            if (!ShouldEmitFromBus())
                return;
            if (unit == null)
                return;
            var key = unit.unit_id + ":" + (int)cate + ":" + (GS_Battle.self != null ? GS_Battle.self.turns : 0);
            if (key == _lastEmittedDoActionKey && Time.unscaledTime - _lastEmittedDoActionAt < 0.5f)
                return;
            string templateId = null;
            try
            {
                var action = unit.GetAction(cate);
                if (action?.train_template?.sd_unit != null)
                    templateId = action.train_template.sd_unit.name;
            }
            catch { /* ignore */ }
            EmitDoActionCommand(
                unit, cate,
                target != null ? target.pos : default(Inctor2),
                target != null,
                templateId);
        }

        private void EmitDoActionCommand(UnitData unit, ActionCate cate, Inctor2 target, bool hasTarget, string templateId)
        {
            var battle = GS_Battle.self;
            var key = unit.unit_id + ":" + (int)cate + ":" + (battle != null ? battle.turns : 0);
            _lastEmittedDoActionKey = key;
            _lastEmittedDoActionAt = Time.unscaledTime;

            HostBroadcastCommand(new CommandDto
            {
                battleId = LanMpPlugin.Instance?.Lobby?.BattleId,
                turn = battle != null ? battle.turns : 0,
                playerIndex = battle?.cur_player != null ? battle.cur_player.index : -1,
                kind = "DoAction",
                netUnitId = unit.unit_id,
                actionCate = (int)cate,
                targetX = target.x,
                targetY = target.y,
                hasTarget = hasTarget,
                templateId = templateId ?? ""
            });
        }

        private void OnUnitMoved(UnitData unit, Inctor2 from, Inctor2 to)
        {
            if (!ShouldEmitFromBus())
                return;
            if (unit == null)
                return;

            float dur = 0.2f;
            try
            {
                if (unit.template?.sd_unit != null)
                    dur = unit.template.sd_unit.ani_speed;
            }
            catch { /* default */ }

            var battle = GS_Battle.self;
            HostBroadcastCommand(new CommandDto
            {
                battleId = LanMpPlugin.Instance?.Lobby?.BattleId,
                turn = battle != null ? battle.turns : 0,
                playerIndex = battle?.cur_player != null ? battle.cur_player.index : -1,
                kind = "UnitMoved",
                netUnitId = unit.unit_id,
                fromX = from.x,
                fromY = from.y,
                targetX = to.x,
                targetY = to.y,
                moveDuration = dur
            });
        }

        private void OnUnitCreated(UnitData unit, CREATE_REASON reason)
        {
            if (!ShouldEmitFromBus())
                return;
            // BUILD/TRAIN creates happen mid-DoAction; fold into DoAction attachment after Actioned.
            if (_actionEmitDepth > 0)
                return;
            // Hard skip: never emit standalone CreateUnit for factory/eng builds (depth can race).
            if (reason == CREATE_REASON.BUILD || reason == CREATE_REASON.TRAIN)
                return;
            if (unit == null || unit.template?.sd_unit == null)
                return;
            // Setup / precreate is local on both peers from the same seed start.
            if (reason == CREATE_REASON.PRECREATE || reason == CREATE_REASON.QUICK_START || reason == CREATE_REASON.MAP_EDIT)
                return;
            if (GS_Battle.self != null && GS_Battle.self.turns < 1)
                return;

            var battle = GS_Battle.self;
            HostBroadcastCommand(new CommandDto
            {
                battleId = LanMpPlugin.Instance?.Lobby?.BattleId,
                turn = battle != null ? battle.turns : 0,
                playerIndex = battle?.cur_player != null ? battle.cur_player.index : -1,
                kind = "CreateUnit",
                netUnitId = unit.unit_id,
                templateId = unit.template.sd_unit.name,
                createReason = (int)reason,
                ownerIndex = unit.player != null ? unit.player.index : -1,
                targetX = unit.pos.x,
                targetY = unit.pos.y,
                building = unit.building,
                spawned = false
            });
        }

        private void OnUnitRemoved(UnitData unit)
        {
            if (!ShouldEmitFromBus())
                return;
            if (_actionEmitDepth > 0)
                return; // deaths during DoAction → attachment
            if (unit == null)
                return;
            if (GS_Battle.self != null && GS_Battle.self.turns < 1)
                return;

            var battle = GS_Battle.self;
            HostBroadcastCommand(new CommandDto
            {
                battleId = LanMpPlugin.Instance?.Lobby?.BattleId,
                turn = battle != null ? battle.turns : 0,
                playerIndex = battle?.cur_player != null ? battle.cur_player.index : -1,
                kind = "RemoveUnit",
                netUnitId = unit.unit_id
            });
        }

        private void OnUnitBuildCompleted(UnitData unit)
        {
            // Progress/completion is included in the enclosing DoAction ResultAttachment.
            if (_actionEmitDepth > 0 || !ShouldEmitFromBus() || unit == null)
                return;
            _log.LogInfo("[Sync] BuildCompleted outside action unit=" + unit.unit_id);
        }

        private bool ShouldEmitFromBus()
        {
            if (SyncContext.SuppressNetworkEmit || _skillCastSuppressEmit)
                return false;
            if (_authority == null || !_authority.InLanBattle)
                return false;
            if (_net.Role != PeerRole.Host)
                return false;
            // Host UX/AI already mutated locally; if peer is gone, Guest never gets Command.
            if (!_net.IsConnected)
            {
                FailBroadcastAfterApply("bus", "not-connected");
                return false;
            }
            return true;
        }

        private void OnSkillCastDone()
        {
            if (_net.Role != PeerRole.Host)
                return;
            if (_authority == null || !_authority.InLanBattle)
                return;
            if (!_net.IsConnected)
            {
                FailBroadcastAfterApply("CastSkill", "not-connected");
                return;
            }

            CommandDto cmd;
            if (_pendingSkillCommand != null)
            {
                cmd = _pendingSkillCommand;
                _pendingSkillCommand = null;
                _skillCastSuppressEmit = false;
                SyncContext.SuppressNetworkEmit = false;
            }
            else if (ShouldEmitFromBus())
            {
                var battle = GS_Battle.self;
                cmd = new CommandDto
                {
                    battleId = LanMpPlugin.Instance?.Lobby?.BattleId,
                    turn = battle != null ? battle.turns : 0,
                    playerIndex = battle?.cur_player != null ? battle.cur_player.index : -1,
                    kind = "CastSkill",
                    targetX = 0,
                    targetY = 0
                };
            }
            else
                return;

            HostBroadcastCommand(cmd);
            _log.LogInfo("[Sync] CastSkill command after cast done");
        }

        public IntentDto BuildIntent(string kind, UnitData unit = null, ActionCate? cate = null, Inctor2? target = null, Inctor2? from = null)
        {
            var battle = GS_Battle.self;
            return new IntentDto
            {
                intentId = Guid.NewGuid().ToString("N"),
                battleId = LanMpPlugin.Instance?.Lobby?.BattleId ?? "",
                turn = battle != null ? battle.turns : -1,
                playerIndex = battle?.cur_player != null ? battle.cur_player.index : -1,
                kind = kind,
                netUnitId = unit != null ? unit.unit_id : -1,
                actionCate = cate.HasValue ? (int)cate.Value : 0,
                targetX = target.HasValue ? target.Value.x : 0,
                targetY = target.HasValue ? target.Value.y : 0,
                fromX = from.HasValue ? from.Value.x : 0,
                fromY = from.HasValue ? from.Value.y : 0,
                extrasJson = "",
                // HasValue=false means vanilla null GameTileData (TRAIN AutoSetPos). Never encode as (0,0).
                hasTarget = target.HasValue
            };
        }

        /// <summary>
        /// Guest may submit a mutating Intent only when none is awaiting Host Command/Nack.
        /// Prevents click-spam → Host multi-Accept (十连开火 / 双建筑).
        /// </summary>
        public bool GuestCanEmitIntent(out string blockReason)
        {
            blockReason = null;
            if (_net.Role != PeerRole.Guest)
                return true;
            if (string.IsNullOrEmpty(_guestAwaitIntentId))
                return true;
            if (Time.unscaledTime - _guestAwaitSince >= GuestAwaitTimeoutSec)
            {
                _log.LogWarning("[Sync] Guest await Intent timeout — clearing " + _guestAwaitIntentId);
                ClearGuestAwait();
                return true;
            }
            blockReason = InputGateRules.WaitingHostConfirm;
            return false;
        }

        private void BeginGuestAwait(string intentId, string kind)
        {
            if (_net.Role != PeerRole.Guest)
                return;
            if (kind != "DoAction" && kind != "UnitMoved" && kind != "CastSkill" && kind != "Undo"
                && kind != "AutoCmd" && kind != "RemoveUnit")
                return;
            _guestAwaitIntentId = intentId;
            _guestAwaitKind = kind;
            _guestAwaitSince = Time.unscaledTime;
            BattleSyncTrace.Ev("GuestAwaitBegin", kind: kind, intentId: intentId);
        }

        private void ClearGuestAwait(string reason = null)
        {
            if (_guestAwaitIntentId == null && _guestPendingFollowUp == null)
                return;
            BattleSyncTrace.Ev("GuestAwaitEnd", intentId: _guestAwaitIntentId, detail: reason);
            var awaitKind = _guestAwaitKind;
            _guestAwaitIntentId = null;
            _guestAwaitKind = null;
            _guestAwaitSince = 0f;

            var pending = _guestPendingFollowUp;
            _guestPendingFollowUp = null;
            if (pending != null && reason != null &&
                (reason.StartsWith("cmd-match") || reason == "cmd-UnitMoved"))
            {
                _log.LogInfo("[Sync] Guest follow-up Intent " + pending.kind + " after " + reason);
                SubmitIntent(pending, guestOptimisticApply: false);
            }
        }

        /// <summary>
        /// Guest EQ chain: UnitMoved in-flight; stash DoAction to send when await clears.
        /// </summary>
        public bool TryStashGuestFollowUp(IntentDto intent)
        {
            if (_net.Role != PeerRole.Guest || intent == null)
                return false;
            if (string.IsNullOrEmpty(_guestAwaitIntentId))
                return false;
            if (intent.kind != "DoAction")
                return false;
            _guestPendingFollowUp = intent;
            BattleSyncTrace.EvIntent("IntentStashFollowUp", intent);
            _log.LogInfo("[Sync] Guest stashed follow-up DoAction unit=" + intent.netUnitId);
            return true;
        }

        public int GuestUndoAvailable => _guestUndoAvailable;

        public void NoteGuestUndoAvailable(int count)
        {
            if (_net.Role != PeerRole.Guest)
                return;
            if (count < 0)
                count = 0;
            _guestUndoAvailable = count;
        }

        private void NoteGuestCommandResolved(CommandDto cmd)
        {
            if (_net.Role != PeerRole.Guest || string.IsNullOrEmpty(_guestAwaitIntentId))
                return;
            if (!string.IsNullOrEmpty(cmd?.sourceIntentId) &&
                string.Equals(cmd.sourceIntentId, _guestAwaitIntentId, StringComparison.Ordinal))
            {
                ClearGuestAwait("cmd-match");
                return;
            }

            // AutoCmd: keep flight lock until AutoCmd ack (Bus UnitMoved/DoAction lack sourceIntentId).
            if (_guestAwaitKind == "AutoCmd")
            {
                if (cmd != null && cmd.kind == "AutoCmd")
                    ClearGuestAwait("cmd-AutoCmd");
                return;
            }

            if (cmd != null &&
                (cmd.kind == "DoAction" || cmd.kind == "UnitMoved" || cmd.kind == "CastSkill" ||
                 cmd.kind == "EndTurn" || cmd.kind == "Undo" || cmd.kind == "RemoveUnit" ||
                 cmd.kind == "AutoCmd"))
                ClearGuestAwait("cmd-" + cmd.kind);
        }

        public void SubmitIntent(IntentDto intent, bool guestOptimisticApply = true)
        {
            if (intent == null)
                return;
            if (string.IsNullOrEmpty(intent.intentId))
                intent.intentId = Guid.NewGuid().ToString("N");

            if (_net.Role == PeerRole.Host)
            {
                BattleSyncTrace.EvIntent("IntentHostLocal", intent);
                HostAcceptIntent(intent);
                return;
            }

            if (!GuestCanEmitIntent(out var waitReason))
            {
                _log.LogInfo("[Sync] Guest Intent suppressed (awaiting Host): " + intent.kind);
                return;
            }

            _net.Send(new Envelope
            {
                Type = MsgType.Intent,
                BattleId = intent.battleId ?? "",
                PayloadJson = JsonUtil.ToJson(intent)
            });
            BattleSyncTrace.EvIntent("IntentSend", intent);
            _log.LogInfo($"[Sync] Intent sent kind={intent.kind} id={intent.intentId}");
            BeginGuestAwait(intent.intentId, intent.kind);

            // DoAction/UnitMoved: wait for Host Command so IDs + animations stay authoritative.
            if (guestOptimisticApply && CanOptimistic(intent.kind))
            {
                _guestOptimisticDone.Add(intent.intentId);
                ApplyCommandLocally(ToCommand(intent), fromOptimistic: true);
            }
        }

        private static void GateUtilToast(string msg)
        {
            try
            {
                AnnW.LanMp.Patches.GateUtil.Toast(msg);
            }
            catch { /* ignore */ }
        }

        private static bool CanOptimistic(string kind)
        {
            // ADR-001: Guest never mutates before Host Command.
            return false;
        }

        public void HostBroadcastCommand(CommandDto cmd)
        {
            if (_net.Role != PeerRole.Host)
                return;
            if (cmd == null)
                return;
            if (string.IsNullOrEmpty(cmd.cmdId))
                cmd.cmdId = Guid.NewGuid().ToString("N");

            StampPresentationHints(cmd);
            MaybeAttachResults(cmd);
            if (cmd.kind == "EndTurn")
                LanMpPlugin.Instance?.Checksum?.StampEndTurnHash(cmd);

            try
            {
                var undo = GS_Battle.self?.undo_move;
                cmd.undoAvailable = undo != null ? undo.GetUndoMoveCount() : 0;
            }
            catch { cmd.undoAvailable = 0; }

            var ok = false;
            try
            {
                ok = _net.TrySend(new Envelope
                {
                    Type = MsgType.Command,
                    BattleId = cmd.battleId ?? "",
                    PayloadJson = JsonUtil.ToJson(cmd)
                });
            }
            catch (Exception ex)
            {
                _log.LogError("[Sync] Command broadcast exception: " + ex.Message);
                ok = false;
            }

            if (!ok)
            {
                BattleSyncTrace.EvCommand("CmdBroadcastFail", cmd);
                // Host already applied (Accept or local Bus). Guest would desync — abort match.
                FailBroadcastAfterApply(cmd.kind ?? "cmd", "send-failed");
                return;
            }

            var hasAttach = !string.IsNullOrEmpty(cmd.resultAttachmentJson);
            BattleSyncTrace.EvCommand("CmdBroadcast", cmd);
            _log.LogInfo($"[Sync] Command broadcast kind={cmd.kind} unit={cmd.netUnitId} attach={hasAttach}");
        }

        private static void StampPresentationHints(CommandDto cmd)
        {
            if (cmd == null)
                return;
            var battle = GS_Battle.self;
            if (battle == null)
                return;

            var skipping = PresentationRules.IsHostSkippingPresentation(
                battle.skipping_all,
                battle.cur_player != null && battle.cur_player.is_ai,
                battle.cur_player?.ai != null && battle.cur_player.ai.skipping);

            if (skipping)
            {
                cmd.moveDuration = 0f;
                return;
            }

            if (cmd.kind == "UnitMoved" && cmd.moveDuration <= 0.001f)
            {
                var unit = ResultAttachmentBridge.FindUnit(cmd.netUnitId);
                var dur = 0.2f;
                try
                {
                    if (unit?.template?.sd_unit != null && unit.template.sd_unit.ani_speed > 0.001f)
                        dur = unit.template.sd_unit.ani_speed;
                }
                catch { /* default */ }
                cmd.moveDuration = dur;
            }

            if (cmd.kind == "DoAction" && cmd.moveDuration <= 0.001f)
                cmd.moveDuration = 1f;
        }

        private void ApplyResultAttachment(ResultAttachmentDto attach, string commandKind, bool snapPositions)
        {
            if (!ResultAttachmentCodec.HasPayload(attach))
                return;
            var battle = GS_Battle.self;
            var local = _authority?.GetLocalHumanSlotIndex();
            var localTurn = local.HasValue && battle?.cur_player != null &&
                            battle.cur_player.index == local.Value;
            var mode = AttachmentApplyPolicy.GetResourceApplyMode(
                commandKind,
                _net.Role == PeerRole.Guest,
                localTurn,
                local.HasValue);
            int? seatFilter = mode == AttachmentApplyPolicy.ResourceApplyMode.LocalSeatOnly
                ? local
                : null;
            ResultAttachmentBridge.Apply(
                attach,
                _log,
                snapPositions,
                AttachmentApplyPolicy.ShouldApplyPlayerResources(mode),
                seatFilter);
        }

        /// <summary>
        /// Host mutated battle state but Command could not reach Guest.
        /// Abort both sides (local Host + disconnect Guest if still half-open).
        /// </summary>
        private void FailBroadcastAfterApply(string kind, string detail)
        {
            if (_handlingBroadcastFail)
                return;
            if (_authority == null || !_authority.InLanBattle)
            {
                _log.LogWarning($"[Sync] Broadcast fail outside LAN battle kind={kind} detail={detail}");
                return;
            }

            _handlingBroadcastFail = true;
            try
            {
                _log.LogError($"[Sync] Applied-not-broadcast kind={kind} detail={detail} — aborting match");
                _authority.AbortMatch("broadcast-failed", kind + ":" + (detail ?? ""), broadcast: false, loadMenu: true);
                try { _net.Disconnect("broadcast-failed"); }
                catch { /* ignore */ }
            }
            finally
            {
                _handlingBroadcastFail = false;
            }
        }

        private void MaybeAttachResults(CommandDto cmd)
        {
            if (_attachResults == null || !_attachResults.Value)
                return;
            if (cmd == null)
                return;
            if (!string.IsNullOrEmpty(cmd.resultAttachmentJson))
                return;
            if (cmd.kind != "DoAction" && cmd.kind != "UnitMoved" && cmd.kind != "Undo"
                && cmd.kind != "CastSkill" && cmd.kind != "CreateUnit" && cmd.kind != "RemoveUnit"
                && cmd.kind != "EndTurn")
                return;

            try
            {
                var board = ResultAttachmentBridge.CaptureBoard(_log);
                if (ResultAttachmentCodec.HasPayload(board))
                    cmd.resultAttachmentJson = ResultAttachmentCodec.ToJson(board);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] Capture attachment failed: " + ex.Message);
            }
        }

        public void HostAcceptIntent(IntentDto intent)
        {
            if (intent == null)
                return;

            if (!string.IsNullOrEmpty(intent.intentId) && !_seenIntentIds.Add(intent.intentId))
            {
                _log.LogInfo("[Sync] Duplicate intent ignored " + intent.intentId);
                return;
            }

            string err;
            if (!TryValidateAgainstBattle(intent, out err))
            {
                // EndTurn: tolerate minor turn desync if the acting player still matches —
                // otherwise Guest EndTurn is randomly Nack'd and Host never ends their turn.
                if (intent.kind == "EndTurn" && err == "turn-mismatch")
                {
                    var battle = GS_Battle.self;
                    var cur = battle?.cur_player != null ? battle.cur_player.index : -1;
                    if (intent.playerIndex >= 0 && intent.playerIndex == cur)
                    {
                        _log.LogWarning("[Sync] EndTurn turn-mismatch tolerated (player ok)");
                        err = null;
                    }
                }
            }
            if (!string.IsNullOrEmpty(err))
            {
                _log.LogWarning("[Sync] Intent rejected: " + err);
                BattleSyncTrace.EvIntent("IntentNack", intent, detail: err);
                var msg = MapNackMessage(err);
                SendIntentNack(intent.intentId, err, msg ?? "");
                return;
            }

            var cmd = ToCommand(intent);
            BattleSyncTrace.EvIntent("IntentAccept", intent);

            if (intent.kind == "CastSkill")
            {
                if (!TryBeginHostSkillCast(cmd))
                {
                    SendIntentNack(intent.intentId, "skill-unavailable", "无法释放指挥官技能");
                    return;
                }
                return;
            }

            if (intent.kind == "AutoCmd")
            {
                HostRunAutoCmd(intent);
                return;
            }

            if (intent.kind == "RemoveUnit")
            {
                HostApplyGuestRemoveUnit(intent, cmd);
                return;
            }

            if (NeedsAnimatedApply(cmd.kind) && TryStartCoroutine(CoHostAcceptAnimated(cmd)))
                return;

            // ADR-004: wait for TurnAuthority EndTurn (nextPlayer) after Host MannualEndTurn.
            if (cmd.kind == "EndTurn")
            {
                if (!TryStartCoroutine(CoHostAcceptEndTurn(cmd)))
                {
                    SyncContext.SuppressNetworkEmit = true;
                    try { ApplyEndTurnHostLocal(); }
                    finally { SyncContext.SuppressNetworkEmit = false; }
                    var ready = TurnAuth?.ConsumePendingEndTurn();
                    if (ready != null)
                    {
                        ready.sourceIntentId = cmd.sourceIntentId;
                        TurnAuth?.AttachBoardSnapshot(ready);
                        HostBroadcastCommand(ready);
                    }
                }
                return;
            }

            SyncContext.SuppressNetworkEmit = true;
            SyncContext.ApplyingRemoteCommand = true;
            try
            {
                ApplyCommandBodyInstant(cmd);
            }
            finally
            {
                SyncContext.SuppressNetworkEmit = false;
                SyncContext.ApplyingRemoteCommand = false;
            }

            HostBroadcastCommand(cmd);
        }

        private void HostApplyGuestRemoveUnit(IntentDto intent, CommandDto cmd)
        {
            var unit = ResultAttachmentBridge.FindUnit(intent.netUnitId);
            if (unit == null)
            {
                SendIntentNack(intent.intentId, "unit-missing", "");
                return;
            }
            var cur = GS_Battle.self?.cur_player?.index ?? -1;
            var owner = unit.player != null ? unit.player.index : -1;
            if (owner != cur)
            {
                SendIntentNack(intent.intentId, "unit-not-owned", InputGateRules.BlockReasonNotYourUnit);
                return;
            }

            SyncContext.SuppressNetworkEmit = true;
            try
            {
                var die = HarmonyLib.AccessTools.Method(typeof(UnitData), "Die");
                if (die != null)
                    die.Invoke(unit, new object[] { DieReason.DELETE, 0f, null, null, true, null });
                else
                    GameAPI.self?.RemoveUnit(unit);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] Host RemoveUnit Die: " + ex.Message);
                try { GameAPI.self?.RemoveUnit(unit); }
                catch { /* ignore */ }
            }
            finally
            {
                SyncContext.SuppressNetworkEmit = false;
            }

            cmd.kind = "RemoveUnit";
            cmd.netUnitId = intent.netUnitId;
            HostBroadcastCommand(cmd);
        }

        private void HostRunAutoCmd(IntentDto intent)
        {
            var battle = GS_Battle.self;
            if (battle?.cur_player == null)
            {
                SendIntentNack(intent.intentId, "no-player", "");
                return;
            }

            var extras = intent.extrasJson ?? "unacted";
            try
            {
                var ag = SingletonMono<SS_ANNW_Game>.self?.auto_guide;
                if (ag == null)
                {
                    SendIntentNack(intent.intentId, "no-autoguide", "自动决策不可用");
                    return;
                }

                if (extras == "unacted" || string.IsNullOrEmpty(extras))
                {
                    ag.TryAutoCommandUnactedUnits();
                }
                else
                {
                    try
                    {
                        battle.selected_units?.Clear();
                        foreach (var part in extras.Split(','))
                        {
                            if (!int.TryParse(part.Trim(), out var id))
                                continue;
                            var u = ResultAttachmentBridge.FindUnit(id);
                            if (u != null)
                                battle.selected_units?.Add(u);
                        }
                    }
                    catch { /* ignore */ }
                    ag.TryAutoCommandSelectedUnits();
                }

                _log.LogInfo("[Sync] Host AutoCmd ran extras=" + extras);
                HostBroadcastCommand(new CommandDto
                {
                    cmdId = Guid.NewGuid().ToString("N"),
                    sourceIntentId = intent.intentId,
                    battleId = intent.battleId,
                    turn = battle.turns,
                    playerIndex = battle.cur_player.index,
                    kind = "AutoCmd",
                    extrasJson = extras
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] Host AutoCmd: " + ex.Message);
                SendIntentNack(intent.intentId, "autocmd-fail", "自动决策失败");
            }
        }

        private IEnumerator CoHostAcceptEndTurn(CommandDto intentCmd)
        {
            // Host EndTurn is authoritative TurnLoop — Suppress only.
            // ApplyingRemoteCommand here conflates "local sim" with "Guest replay" and poisons
            // StartPlayerTurn / CreateUnit / FOW paths during the transition.
            SyncContext.SuppressNetworkEmit = true;
            TurnAuth?.ConsumePendingEndTurn();
            BattleSyncTrace.Ev("EndTurnAcceptBegin",
                kind: "EndTurn",
                intentId: intentCmd?.sourceIntentId,
                turn: GS_Battle.self != null ? GS_Battle.self.turns : (int?)null,
                curPlayer: GS_Battle.self?.cur_player != null ? GS_Battle.self.cur_player.index : (int?)null);
            ApplyEndTurnHostLocal();
            BattleSyncTrace.Ev("EndTurnMannualStarted",
                kind: "EndTurn",
                turn: GS_Battle.self != null ? GS_Battle.self.turns : (int?)null,
                curPlayer: GS_Battle.self?.cur_player != null ? GS_Battle.self.cur_player.index : (int?)null);
            var t0 = Time.unscaledTime;
            // CRITICAL: CoroutineObject treats yield null as same-frame spin — must use float wait
            // or TurnLoop never gets Update and EndTurnReady can never arrive (Host white-screen).
            while (TurnAuth != null && !TurnAuth.EndTurnReady && Time.unscaledTime - t0 < 45f)
                yield return AnnWCoroutine.NextTick;
            SyncContext.SuppressNetworkEmit = false;

            var ready = TurnAuth?.ConsumePendingEndTurn();
            if (ready == null)
            {
                _log.LogError("[Sync] Host EndTurn Accept — TurnAuthority produced no EndTurn");
                FailBroadcastAfterApply("EndTurn", "no-turn-auth");
                yield break;
            }
            ready.sourceIntentId = intentCmd.sourceIntentId;
            // Breathe one tick then snapshot — keep CaptureBoard off the turn-started bus stack.
            yield return AnnWCoroutine.NextTick;
            TurnAuth?.AttachBoardSnapshot(ready);
            HostBroadcastCommand(ready);
            BattleSyncTrace.EvCommand("EndTurnAcceptBroadcast", ready);
            _log.LogInfo(
                $"[Sync] Host EndTurn Accept broadcast ended={ready.endedPlayerIndex}→{ready.nextPlayerIndex}");
        }

        private IEnumerator CoHostAcceptAnimated(CommandDto cmd)
        {
            // Keep Suppress through HostBroadcastCommand so Bus OnUnitActioned cannot emit a twin
            // Command after Suppress drops (was: Accept broadcast + Bus broadcast → Guest double apply).
            SyncContext.SuppressNetworkEmit = true;
            SyncContext.ApplyingRemoteCommand = true;
            try
            {
                yield return CoApplyCommandBody(cmd);
                if (cmd.kind != "EndTurn")
                    HostBroadcastCommand(cmd);
            }
            finally
            {
                SyncContext.SuppressNetworkEmit = false;
                SyncContext.ApplyingRemoteCommand = false;
            }
        }

        private bool TryBeginHostSkillCast(CommandDto cmd)
        {
            var battle = GS_Battle.self;
            var ux = UX_Manager.self;
            var co = battle?.cur_player?.co_data;
            if (ux == null || co?.skill_action == null)
            {
                _log.LogWarning("[Sync] CastSkill missing UX/CO skill_action");
                return false;
            }

            _pendingSkillCommand = cmd;
            _skillCastSuppressEmit = true;
            SyncContext.SuppressNetworkEmit = true;
            try
            {
                var pos = new Inctor2(cmd.targetX, cmd.targetY);
                var tile = battle.terrain != null ? battle.terrain.GetTile(pos) : null;
                if (tile == null && GameAPI.self != null)
                    tile = GameAPI.self.GetTile(pos);
                if (tile == null && battle.terrain != null)
                    tile = battle.terrain.GetTile(Inctor2.Zero);

                battle.selected_skill = co.skill_action;
                ux.SetUXState_Skill(co.skill_action);
                ux.coroutineObject.StartCoroutine(ux.proc_SkillDoAction(tile));
                _log.LogInfo($"[Sync] Host casting skill for intent at ({cmd.targetX},{cmd.targetY})");
                return true;
            }
            catch (Exception ex)
            {
                _pendingSkillCommand = null;
                _skillCastSuppressEmit = false;
                SyncContext.SuppressNetworkEmit = false;
                _log.LogError("[Sync] BeginHostSkillCast: " + ex);
                return false;
            }
        }

        private bool TryValidateAgainstBattle(IntentDto intent, out string error)
        {
            var battle = GS_Battle.self;
            var bid = LanMpPlugin.Instance?.Lobby?.BattleId ?? "";
            var turn = battle != null ? battle.turns : -1;
            var cur = battle?.cur_player != null ? battle.cur_player.index : -1;
            if (!IntentValidateRules.TryValidateBasics(
                    _authority != null && _authority.InLanBattle,
                    _authority != null && _authority.GatesArmed,
                    bid, intent, turn, cur, out error))
                return false;

            if (intent.kind == "Undo")
                return true;

            if (intent.kind == "DoAction" || intent.kind == "UnitMoved")
            {
                var unit = ResultAttachmentBridge.FindUnit(intent.netUnitId);
                var owner = unit?.player != null ? unit.player.index : -1;
                if (!IntentValidateRules.TryValidateUnitOwner(intent.kind, owner, cur, out error))
                    return false;
                if (unit == null)
                {
                    error = "unit-missing";
                    return false;
                }
                // Reject spam after the unit already spent its move/action this turn.
                if (intent.kind == "UnitMoved" && unit.moved)
                {
                    error = "already-moved";
                    return false;
                }
                if (intent.kind == "DoAction" && unit.actioned)
                {
                    error = "already-actioned";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private void SendIntentNack(string intentId, string code, string message)
        {
            if (_net.Role != PeerRole.Host || !_net.IsConnected)
                return;
            var nack = new IntentNackDto
            {
                intentId = intentId ?? "",
                code = code ?? "reject",
                message = message ?? "操作被拒绝"
            };
            _net.Send(new Envelope
            {
                Type = MsgType.IntentNack,
                BattleId = LanMpPlugin.Instance?.Lobby?.BattleId ?? "",
                PayloadJson = JsonUtil.ToJson(nack)
            });
        }

        private static string MapNackMessage(string code)
        {
            switch (code)
            {
                case "already-moved":
                    return "该单位本回合已移动";
                case "already-actioned":
                    return "该单位本回合已行动";
                case "not-current-player":
                case "turn-mismatch":
                    return null;
                case "unit-not-owned":
                    return InputGateRules.BlockReasonNotYourUnit;
                case "unit-missing":
                    return null;
                default:
                    return "操作被主机拒绝（" + code + "）";
            }
        }

        private static CommandDto ToCommand(IntentDto intent)
        {
            return new CommandDto
            {
                cmdId = Guid.NewGuid().ToString("N"),
                sourceIntentId = intent.intentId,
                battleId = intent.battleId,
                turn = intent.turn,
                playerIndex = intent.playerIndex,
                kind = intent.kind,
                netUnitId = intent.netUnitId,
                actionCate = intent.actionCate,
                targetX = intent.targetX,
                targetY = intent.targetY,
                fromX = intent.fromX,
                fromY = intent.fromY,
                extrasJson = intent.extrasJson,
                templateId = intent.extrasJson ?? "",
                hasTarget = intent.hasTarget,
                resultAttachmentJson = ""
            };
        }

        public void ApplyCommandLocally(CommandDto cmd, bool fromOptimistic = false)
        {
            if (cmd == null)
                return;

            // Guest: serial queue (INV-T3).
            if (!fromOptimistic && _net.Role == PeerRole.Guest && _applyQueue != null)
            {
                _applyQueue.Enqueue(cmd);
                return;
            }

            if (!fromOptimistic && NeedsAnimatedApply(cmd.kind) && TryStartCoroutine(CoApplyCommandLocally(cmd)))
                return;

            ApplyCommandLocallyImmediate(cmd, fromOptimistic);
        }

        private IEnumerator CoApplyQueuedCommand(CommandDto cmd)
        {
            // Flags already set by CommandApplyQueue.
            if (cmd.kind != "EndTurn")
                BattleSyncTrace.EvCommand("CmdApply", cmd);

            if (cmd.kind == "EndTurn")
            {
                ApplyGuestEndTurn(cmd);
                yield break;
            }

            if (NeedsAnimatedApply(cmd.kind))
            {
                yield return CoApplyCommandBody(cmd);
                try
                {
                    var attach = ResultAttachmentCodec.FromJson(cmd.resultAttachmentJson);
                    if (ResultAttachmentCodec.HasPayload(attach) &&
                        cmd.kind != "DoAction") // DoAction attach-only may already apply
                    {
                        // DoAction BUILD attach-only already applied; other DoAction may need snap after anim
                    }
                    if (ResultAttachmentCodec.HasPayload(attach) && cmd.kind == "UnitMoved")
                        ApplyResultAttachment(attach, cmd.kind, snapPositions: false);
                    else if (ResultAttachmentCodec.HasPayload(attach) && cmd.kind == "DoAction")
                    {
                        var cate = (ActionCate)cmd.actionCate;
                        if (cate != ActionCate.BUILD && cate != ActionCate.TRAIN &&
                            cate != ActionCate.QUICK_BUILD_MINER)
                            ApplyResultAttachment(attach, cmd.kind, snapPositions: false);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Sync] queue post-attach: " + ex.Message);
                }
                yield break;
            }

            ApplyCommandBodyInstant(cmd);
            try
            {
                var attach = ResultAttachmentCodec.FromJson(cmd.resultAttachmentJson);
                if (ResultAttachmentCodec.HasPayload(attach))
                    ApplyResultAttachment(attach, cmd.kind, snapPositions: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] queue instant attach: " + ex.Message);
            }
        }

        private void ApplyGuestEndTurn(CommandDto cmd)
        {
            BattleSyncTrace.EvCommand("CmdApply", cmd, detail: "EndTurn");
            try
            {
                var attach = ResultAttachmentCodec.FromJson(cmd.resultAttachmentJson);
                if (ResultAttachmentCodec.HasPayload(attach))
                    ApplyResultAttachment(attach, cmd.kind, snapPositions: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] EndTurn attach: " + ex.Message);
            }

            if (TurnAuth != null)
            {
                TurnAuth.ApplyCursorFromCommand(cmd);
                TurnAuth.BeginGuestWatchIfNeeded();
            }
            else
                _log.LogWarning("[Sync] EndTurn without TurnAuth");

            LanMpPlugin.Instance?.Checksum?.GuestVerifyEndTurn(cmd);
        }

        private void ApplyEndTurnHostLocal()
        {
            if (GameAPI.self == null)
            {
                _log.LogWarning("[Sync] GameAPI.self null on EndTurn");
                return;
            }
            _log.LogInfo("[Sync] Host applying EndTurn (local TurnLoop)");
            GameAPI.self.MannualEndTurn();
        }

        private void ApplyCommandLocallyImmediate(CommandDto cmd, bool fromOptimistic)
        {
            SyncContext.SuppressNetworkEmit = true;
            SyncContext.ApplyingRemoteCommand = true;
            try
            {
                ApplyCommandBodyInstant(cmd);
                var attach = ResultAttachmentCodec.FromJson(cmd.resultAttachmentJson);
                // Instant path may still be mid-move elsewhere; snap positions to Host truth.
                if (ResultAttachmentCodec.HasPayload(attach))
                    ApplyResultAttachment(attach, cmd.kind, snapPositions: true);
            }
            catch (Exception ex)
            {
                _log.LogError("[Sync] ApplyCommandLocally: " + ex);
            }
            finally
            {
                SyncContext.SuppressNetworkEmit = false;
                SyncContext.ApplyingRemoteCommand = false;
            }

            if (fromOptimistic)
                _log.LogInfo($"[Sync] Optimistic apply kind={cmd.kind} intent={cmd.sourceIntentId}");
        }

        private IEnumerator CoApplyCommandLocally(CommandDto cmd)
        {
            SyncContext.SuppressNetworkEmit = true;
            SyncContext.ApplyingRemoteCommand = true;
            Exception error = null;
            var apply = CoApplyCommandBody(cmd);
            while (true)
            {
                object current = null;
                bool moved;
                try
                {
                    moved = apply.MoveNext();
                    if (moved)
                        current = apply.Current;
                }
                catch (Exception ex)
                {
                    error = ex;
                    break;
                }
                if (!moved)
                    break;
                yield return current;
            }

            if (error == null)
            {
                try
                {
                    var attach = ResultAttachmentCodec.FromJson(cmd.resultAttachmentJson);
                    if (ResultAttachmentCodec.HasPayload(attach))
                        ApplyResultAttachment(attach, cmd.kind, snapPositions: false);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }

            SyncContext.SuppressNetworkEmit = false;
            SyncContext.ApplyingRemoteCommand = false;

            if (error != null)
                _log.LogError("[Sync] CoApplyCommandLocally: " + error);
        }

        private static bool NeedsAnimatedApply(string kind)
        {
            return kind == "DoAction" || kind == "UnitMoved";
        }

        private bool TryStartCoroutine(IEnumerator routine)
        {
            try
            {
                var gc = GameController.self;
                if (gc != null)
                {
                    gc.StartCoroutine(routine);
                    return true;
                }
                var ux = UX_Manager.self;
                if (ux?.coroutineObject != null)
                {
                    ux.coroutineObject.StartCoroutine(routine);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] StartCoroutine failed: " + ex.Message);
            }
            return false;
        }

        private IEnumerator CoApplyCommandBody(CommandDto cmd)
        {
            switch (cmd.kind)
            {
                case "DoAction":
                    yield return CoApplyDoAction(cmd);
                    break;
                case "UnitMoved":
                    yield return CoApplyUnitMoved(cmd);
                    break;
                default:
                    ApplyCommandBodyInstant(cmd);
                    break;
            }
        }

        private void ApplyCommandBodyInstant(CommandDto cmd)
        {
            switch (cmd.kind)
            {
                case "EndTurn":
                    // Host Accept path only — Guest uses ApplyGuestEndTurn via queue.
                    ApplyEndTurnHostLocal();
                    break;
                case "DoAction":
                    ApplyDoActionInstant(cmd);
                    break;
                case "UnitMoved":
                    ApplyUnitMovedInstant(cmd);
                    break;
                case "Undo":
                    ApplyUndo();
                    break;
                case "CastSkill":
                    _log.LogInfo("[Sync] CastSkill apply = attachment only");
                    break;
                case "CreateUnit":
                    ApplyCreateUnit(cmd);
                    break;
                case "RemoveUnit":
                    ApplyRemoveUnit(cmd);
                    break;
                case "AutoCmd":
                    _log.LogInfo("[Sync] AutoCmd ack (Host already emitted moves/actions)");
                    break;
                default:
                    _log.LogWarning("[Sync] Unknown command kind " + cmd.kind);
                    break;
            }
        }

        private IEnumerator CoApplyDoAction(CommandDto cmd)
        {
            var unit = ResultAttachmentBridge.FindUnit(cmd.netUnitId);
            if (unit == null)
            {
                _log.LogWarning("[Sync] DoAction missing unit " + cmd.netUnitId);
                yield break;
            }

            if (GameAPI.self == null)
                yield break;

            // Spawn buildings/units from Host attachment BEFORE replay so BUILD doesn't hit occupied tile.
            ResultAttachmentDto attachEarly = null;
            try
            {
                attachEarly = ResultAttachmentCodec.FromJson(cmd.resultAttachmentJson);
                ResultAttachmentBridge.PreSpawnMissing(attachEarly, _log);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] PreSpawn: " + ex.Message);
            }

            var cate = (ActionCate)cmd.actionCate;
            var isBuildLike = cate == ActionCate.BUILD || cate == ActionCate.TRAIN ||
                              cate == ActionCate.QUICK_BUILD_MINER;
            var isGuest = LanMpPlugin.Instance?.Net.Role == PeerRole.Guest;
            var hasAttach = ResultAttachmentCodec.HasPayload(attachEarly);
            var idsBefore = ActionPresentation.SnapshotAliveIds();

            // Guest + Host attachment: never re-simulate (BUILD double-spawn / ATTACK RNG ghosts).
            // Host Accept has no attachment yet and must ExecuteAction.
            if (AttachmentApplyPolicy.ShouldGuestAttachOnlyDoAction(isGuest, hasAttach) ||
                (isBuildLike && hasAttach && isGuest))
            {
                TryLookAtUnit(unit);
                ApplyResultAttachment(attachEarly, "DoAction", snapPositions: false);
                ActionPresentation.AfterAttachApply(attachEarly, _log, cmd, idsBefore);
                EnsureUnitActed(unit);
                ResultAttachmentBridge.RefreshUnactionedLists(_log);
                _log.LogInfo($"[Sync] Applied DoAction(attach-only) unit={cmd.netUnitId} cate={cate}");
                yield break;
            }

            // Host BUILD/TRAIN with attachment (rare) — still attach-only to avoid double create.
            if (isBuildLike && hasAttach)
            {
                TryLookAtUnit(unit);
                ApplyResultAttachment(attachEarly, "DoAction", snapPositions: false);
                ActionPresentation.AfterAttachApply(attachEarly, _log, cmd, idsBefore);
                EnsureUnitActed(unit);
                ResultAttachmentBridge.RefreshUnactionedLists(_log);
                _log.LogInfo($"[Sync] Applied DoAction(attach-only) unit={cmd.netUnitId} cate={cate}");
                yield break;
            }

            // Guest must never ExecuteAction without Host attachment (RNG / unit-id divergence).
            if (AttachmentApplyPolicy.ShouldGuestSkipDoActionWithoutAttach(isGuest, hasAttach))
            {
                _log.LogWarning(
                    $"[Sync] Guest DoAction without attachment — skip ExecuteAction unit={cmd.netUnitId} cate={cate}");
                EnsureUnitActed(unit);
                ResultAttachmentBridge.RefreshUnactionedLists(_log);
                yield break;
            }

            EnsureTrainTemplate(unit, cate, cmd);

            TryLookAtUnit(unit);

            var skipAnim = PresentationRules.ShouldFastPresent(cmd.moveDuration, "DoAction");
            var tile = ResolveActionTile(cmd);
            IEnumerator exec = null;
            try
            {
                if (GameController.self != null)
                    exec = GameController.self.ExecuteAction(unit, cate, tile, skipAnim);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] ExecuteAction start: " + ex.Message);
            }

            if (exec != null)
            {
                yield return exec;
                _log.LogInfo(
                    $"[Sync] Applied DoAction(animated) unit={cmd.netUnitId} cate={cmd.actionCate} hasTarget={cmd.hasTarget}");
            }
            else
            {
                ApplyDoActionInstant(cmd);
            }

            TryLookAtUnit(unit);
        }

        private static void EnsureUnitActed(UnitData unit)
        {
            if (unit == null)
                return;
            unit.actioned = true;
            unit.moved = true;
        }

        private static GameTileData ResolveActionTile(CommandDto cmd)
        {
            if (cmd == null || !cmd.hasTarget)
                return null;
            if (GameAPI.self == null)
                return null;
            return GameAPI.self.GetTile(new Inctor2(cmd.targetX, cmd.targetY));
        }

        private void EnsureTrainTemplate(UnitData unit, ActionCate cate, CommandDto cmd)
        {
            if (unit == null || cmd == null)
                return;
            if (cate != ActionCate.TRAIN && cate != ActionCate.BUILD)
                return;

            var name = !string.IsNullOrEmpty(cmd.templateId)
                ? cmd.templateId
                : (!string.IsNullOrEmpty(cmd.extrasJson) ? cmd.extrasJson : null);
            if (string.IsNullOrEmpty(name))
            {
                try
                {
                    if (GS_Battle.self?.ux_unit_template?.sd_unit != null)
                        name = GS_Battle.self.ux_unit_template.sd_unit.name;
                }
                catch { /* ignore */ }
            }
            if (string.IsNullOrEmpty(name))
                return;

            try
            {
                var action = unit.GetAction(cate);
                if (action == null)
                    return;
                var tpl = UnitTemplate.Acquire(name);
                if (tpl != null)
                    action.train_template = tpl;
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] train_template: " + ex.Message);
            }
        }

        private static void TryLookAtUnit(UnitData unit)
        {
            if (unit == null)
                return;
            if (!ViewUtil.ShouldFollowUnitCamera(unit))
                return;
            try
            {
                var game = SingletonMono<SS_ANNW_Game>.self;
                if (game?.cam_control == null)
                    return;
                // GetWP is internal — approximate via world transform on unit events if available.
                var mi = typeof(SS_ANNW_Game).GetMethod(
                    "GetWP",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null,
                    new[] { typeof(Inctor2) },
                    null);
                if (mi == null)
                    return;
                var wp = (Vector3)mi.Invoke(null, new object[] { unit.pos });
                game.cam_control.LookAt(wp, 0.35f);
            }
            catch { /* ignore */ }
        }

        private void ApplyDoActionInstant(CommandDto cmd)
        {
            var unit = ResultAttachmentBridge.FindUnit(cmd.netUnitId);
            if (unit == null)
            {
                _log.LogWarning("[Sync] DoAction missing unit " + cmd.netUnitId);
                return;
            }
            var cate = (ActionCate)cmd.actionCate;
            EnsureTrainTemplate(unit, cate, cmd);
            // Do NOT use GameAPI.DoActionInstant — it GetValid's coords and drops null-target AutoSetPos.
            var tile = ResolveActionTile(cmd);
            unit.DoActionInstant(tile, cate);
            _log.LogInfo(
                $"[Sync] Applied DoAction(instant) unit={cmd.netUnitId} cate={cate} hasTarget={cmd.hasTarget}");
        }

        private IEnumerator CoApplyUnitMoved(CommandDto cmd)
        {
            var unit = ResultAttachmentBridge.FindUnit(cmd.netUnitId);
            if (unit == null)
            {
                _log.LogWarning("[Sync] UnitMoved missing unit " + cmd.netUnitId);
                yield break;
            }

            var from = new Inctor2(cmd.fromX, cmd.fromY);
            var to = new Inctor2(cmd.targetX, cmd.targetY);
            float tplSpeed = 0.2f;
            try
            {
                if (unit.template?.sd_unit != null)
                    tplSpeed = unit.template.sd_unit.ani_speed;
            }
            catch { /* keep */ }

            var fast = PresentationRules.ShouldFastPresent(cmd.moveDuration, "UnitMoved");
            var dur = PresentationRules.ResolveMoveDuration(cmd.moveDuration, tplSpeed);

            TryLookAtUnit(unit);

            // Host AcceptIntent: run real DoMove so game logic (undo/transport) matches.
            // Guest replay: visual move is enough; attachment reconciles.
            if (_net.Role == PeerRole.Host)
            {
                Exception moveErr = null;
                try
                {
                    if (unit.pos.x != from.x || unit.pos.y != from.y)
                        GameAPI.self.MoveUnitInstantly(unit, from);
                    unit.DoMove(to);
                }
                catch (Exception ex)
                {
                    moveErr = ex;
                }

                if (moveErr != null)
                {
                    _log.LogWarning("[Sync] Host DoMove: " + moveErr.Message);
                    ApplyUnitMovedInstant(cmd);
                    yield break;
                }

                var guard = 0f;
                while (unit.in_animation && guard < 10f)
                {
                    guard += Time.unscaledDeltaTime;
                    yield return AnnWCoroutine.NextTick;
                }
                _log.LogInfo($"[Sync] Applied UnitMoved(DoMove) unit={cmd.netUnitId} -> ({cmd.targetX},{cmd.targetY})");
                yield break;
            }

            if (GameAPI.self != null)
            {
                if (fast)
                {
                    ApplyUnitMovedInstant(cmd);
                    try { BattleEventBus.self.TriggerFOWChanged(); }
                    catch { /* ignore */ }
                    _log.LogInfo($"[Sync] Applied UnitMoved(fast) unit={cmd.netUnitId} -> ({cmd.targetX},{cmd.targetY})");
                    yield break;
                }

                if (unit.pos.x != from.x || unit.pos.y != from.y)
                    GameAPI.self.MoveUnitInstantly(unit, from);

                IEnumerator moveAni = null;
                try { moveAni = unit.DoMoveWithAni(to, 1f); }
                catch (System.Exception ex) { _log.LogWarning("[Sync] DoMoveWithAni: " + ex.Message); }

                if (moveAni != null)
                {
                    yield return moveAni;
                    var guard = 0f;
                    while (unit.in_animation && guard < 10f)
                    {
                        guard += Time.unscaledDeltaTime;
                        yield return AnnWCoroutine.NextTick;
                    }
                }
                else
                {
                    GameAPI.self.MoveUnitVisual(unit, from, to, dur);
                    yield return dur;
                }

                try { BattleEventBus.self.TriggerFOWChanged(); }
                catch { /* ignore */ }
                TryLookAtUnit(unit);
                _log.LogInfo($"[Sync] Applied UnitMoved(animated) unit={cmd.netUnitId} -> ({cmd.targetX},{cmd.targetY})");
            }
            else
            {
                ApplyUnitMovedInstant(cmd);
            }
        }

        private void ApplyUnitMovedInstant(CommandDto cmd)
        {
            var unit = ResultAttachmentBridge.FindUnit(cmd.netUnitId);
            if (unit == null)
            {
                _log.LogWarning("[Sync] UnitMoved missing unit " + cmd.netUnitId);
                return;
            }
            GameAPI.self.MoveUnitInstantly(unit, new Inctor2(cmd.targetX, cmd.targetY));
            _log.LogInfo($"[Sync] Applied UnitMoved(instant) unit={cmd.netUnitId}");
        }

        private void ApplyCreateUnit(CommandDto cmd)
        {
            if (GameAPI.self == null || string.IsNullOrEmpty(cmd.templateId))
            {
                _log.LogWarning("[Sync] CreateUnit missing api/template");
                return;
            }

            if (ResultAttachmentBridge.FindUnit(cmd.netUnitId) != null)
            {
                _log.LogInfo("[Sync] CreateUnit already present id=" + cmd.netUnitId);
                return;
            }

            UnitTemplate tpl;
            try { tpl = UnitTemplate.Acquire(cmd.templateId); }
            catch { tpl = null; }
            if (tpl == null)
            {
                _log.LogWarning("[Sync] CreateUnit Acquire failed " + cmd.templateId);
                return;
            }

            Player owner = null;
            var battle = GS_Battle.self;
            if (battle?.all_player?.players != null)
            {
                foreach (var p in battle.all_player.players)
                {
                    if (p != null && p.index == cmd.ownerIndex)
                    {
                        owner = p;
                        break;
                    }
                }
            }
            if (owner == null)
            {
                _log.LogWarning("[Sync] CreateUnit owner missing " + cmd.ownerIndex);
                return;
            }

            SyncContext.AllowForcedCreate = true;
            SyncContext.ForcedUnitId = cmd.netUnitId;
            try
            {
                var unit = GameAPI.self.CreateUnit(
                    (CREATE_REASON)cmd.createReason,
                    tpl,
                    new Inctor2(cmd.targetX, cmd.targetY),
                    owner,
                    cmd.building,
                    cmd.spawned,
                    trigger_ps: true);
                if (unit != null)
                    SyncContext.ForceUnitId(unit, cmd.netUnitId);
                _log.LogInfo($"[Sync] Applied CreateUnit id={cmd.netUnitId} tpl={cmd.templateId}");
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] CreateUnit apply: " + ex.Message);
            }
            finally
            {
                SyncContext.AllowForcedCreate = false;
                SyncContext.ForcedUnitId = null;
            }
        }

        private void ApplyRemoveUnit(CommandDto cmd)
        {
            var unit = ResultAttachmentBridge.FindUnit(cmd.netUnitId);
            if (unit == null)
                return;
            try
            {
                GameAPI.self.RemoveUnit(unit);
                _log.LogInfo("[Sync] Applied RemoveUnit id=" + cmd.netUnitId);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] RemoveUnit: " + ex.Message);
            }
        }

        private void ApplyUndo()
        {
            // Guest never builds a local undo stack (moves are Intent-only) — board snap is truth.
            if (_net.Role == PeerRole.Guest)
            {
                _log.LogInfo("[Sync] Applied Undo (Guest attach-only; no local stack)");
                return;
            }
            try
            {
                var undo = GS_Battle.self?.undo_move;
                if (undo == null)
                {
                    _log.LogWarning("[Sync] GS_Battle.undo_move null");
                    return;
                }
                undo.UndoLastMove();
                _log.LogInfo("[Sync] Applied Undo");
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] Undo apply: " + ex.Message);
            }
        }

        private void OnEnvelope(Envelope env)
        {
            if (env.Type == MsgType.Intent && _net.Role == PeerRole.Host)
            {
                var intent = JsonUtil.FromJson<IntentDto>(env.PayloadJson);
                if (intent != null)
                    HostAcceptIntent(intent);
                return;
            }

            if (env.Type == MsgType.IntentNack && _net.Role == PeerRole.Guest)
            {
                var nack = JsonUtil.FromJson<IntentNackDto>(env.PayloadJson);
                if (nack == null)
                    return;
                if (!string.IsNullOrEmpty(nack.intentId))
                {
                    _guestOptimisticDone.Remove(nack.intentId);
                    if (string.Equals(nack.intentId, _guestAwaitIntentId, StringComparison.Ordinal))
                        ClearGuestAwait("nack");
                }
                _log.LogWarning("[Sync] IntentNack: " + (nack.message ?? nack.code));
                BattleSyncTrace.Ev("IntentNackRecv", kind: nack.code, intentId: nack.intentId, detail: nack.message);
                OnIntentNack?.Invoke(nack);
                return;
            }

            if (env.Type == MsgType.Command)
            {
                var cmd = JsonUtil.FromJson<CommandDto>(env.PayloadJson);
                if (cmd == null)
                    return;
                if (_net.Role == PeerRole.Host)
                    return;

                // All Guest Commands (including EndTurn) go through ApplyQueue — INV-T3/T4.
                BattleSyncTrace.EvCommand("CmdRecv", cmd);
                _log.LogInfo($"[Sync] Command received kind={cmd.kind}");
                NoteGuestUndoAvailable(cmd.undoAvailable);
                NoteGuestCommandResolved(cmd);
                ApplyCommandLocally(cmd);
            }
        }
    }
}
