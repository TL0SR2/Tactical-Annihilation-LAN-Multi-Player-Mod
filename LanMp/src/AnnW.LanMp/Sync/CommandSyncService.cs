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
        /// <summary>PL2: UnitMoved keys already broadcast ahead (Host local or Accept).</summary>
        private readonly HashSet<string> _presentationAheadMoveKeys = new HashSet<string>();
        private bool _eventsHooked;
        private CommandDto _pendingSkillCommand;
        private bool _skillCastSuppressEmit;
        private float _skillCastSuppressSince;
        private bool _hostSkillHasTarget;
        private Inctor2 _hostSkillTarget;
        private string _hostSkillExtras;
        /// <summary>Host: Guest Intent cast must finish or watchdog clears Suppress (B4).</summary>
        private const float HostSkillCastWatchdogSec = 45f;
        private int _actionEmitDepth;
        private int _pendingActionUnitId = -1;
        private ActionCate _pendingActionCate;
        private Inctor2 _pendingActionTarget;
        private bool _pendingActionHasTarget;
        private string _pendingActionTemplateId;
        private string _pendingActionExtras;
        private string _lastEmittedDoActionKey;
        private float _lastEmittedDoActionAt;
        private bool _handlingBroadcastFail;
        private CommandApplyQueue _applyQueue;

        /// <summary>
        /// Host outbound Commands: CaptureBoard + TCP off TurnLoop / turn-started stacks
        /// (INV-T + AnnW CoroutineObject — sync capture during AI EndTurn white-screens).
        /// </summary>
        private readonly Queue<CommandDto> _outboundCmds = new Queue<CommandDto>();
        private bool _outboundPumping;

        /// <summary>Guest: one mutating Intent in flight until matching Command/Nack (ADR-001).</summary>
        private string _guestAwaitIntentId;
        private string _guestAwaitKind;
        private float _guestAwaitSince;
        private bool _guestAwaitSoftWarned;
        private IntentDto _guestPendingFollowUp;
        private int _guestUndoAvailable;
        /// <summary>Warn Guest once; keep await (do not clear — Host may still Accept).</summary>
        private const float GuestAwaitSoftTimeoutSec = 20f;
        /// <summary>Only then allow a new Intent; premature clear caused double-Accept under lag.</summary>
        private const float GuestAwaitHardTimeoutSec = 60f;
        /// <summary>When true, Intent reject may wire IntentNack to Guest (network Intent only).</summary>
        private bool _nackGuestOnReject;
        /// <summary>Host: Intent.intentId → SourcePeerId for directed Nack.</summary>
        private readonly Dictionary<string, string> _intentSourcePeer =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private string _nackTargetPeerId;

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
            _outboundCmds.Clear();
            _outboundPumping = false;
            UnhookBattleEvents();
            _seenIntentIds.Clear();
            _guestOptimisticDone.Clear();
            _presentationAheadMoveKeys.Clear();
            ClearGuestAwait();
            SyncContext.HostEndTurnAcceptWaiting = false;
            SyncContext.SkillCastSuppressEmit = false;
            SyncContext.SuppressNetworkEmit = false;
        }

        public void Tick(float dt)
        {
            TickHostSkillCastWatchdog();
        }

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
                _outboundCmds.Clear();
                _outboundPumping = false;
                _seenIntentIds.Clear();
                _guestOptimisticDone.Clear();
                _presentationAheadMoveKeys.Clear();
                ClearGuestAwait();
                SyncContext.HostEndTurnAcceptWaiting = false;
                SyncContext.SkillCastSuppressEmit = false;
                SyncContext.SuppressNetworkEmit = false;
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
                BattleEventBus.self.OnSkillCastStarted += OnSkillCastStarted;
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
                BattleEventBus.self.OnSkillCastStarted -= OnSkillCastStarted;
                BattleEventBus.self.OnUnitCreated -= OnUnitCreated;
                BattleEventBus.self.OnUnitRemoved -= OnUnitRemoved;
                BattleEventBus.self.OnUnitBuildCompleted -= OnUnitBuildCompleted;
            }
            catch { /* ignore */ }
            _eventsHooked = false;
            _actionEmitDepth = 0;
            AbortHostSkillCastWatchdog("unhook", nackGuest: false);
        }

        /// <summary>
        /// Host local / AI / Guest-Intent: suppress CreateUnit bus emit during skill so summons
        /// fold into CastSkill CaptureBoard (audit B3).
        /// </summary>
        private void OnSkillCastStarted()
        {
            if (_net.Role != PeerRole.Host)
                return;
            if (_authority == null || !_authority.InLanBattle)
                return;
            BeginSkillCastSuppress("cast-started");
        }

        private void BeginSkillCastSuppress(string reason)
        {
            // Skill suppress is independent of Accept SuppressNetworkEmit (INV-T9) —
            // CastDone must not clear Host Accept / ApplyQueue suppress.
            SyncContext.SkillCastSuppressEmit = true;
            _skillCastSuppressEmit = true;
            _skillCastSuppressSince = Time.unscaledTime;
            _log.LogInfo("[Sync] Skill cast suppress ON (" + reason + ")");
        }

        /// <summary>
        /// Host local UX: arm skill suppress + stamp target before
        /// <c>StartCoroutine(proc_SkillDoAction)</c> so CoroutineObject SafeWrap applies.
        /// </summary>
        public void ArmHostLocalSkillCast(GameTileData tile)
        {
            if (_net.Role != PeerRole.Host)
                return;
            if (_authority == null || !_authority.InLanBattle)
                return;
            NoteHostSkillCastTarget(tile);
            BeginSkillCastSuppress("host-local-ux");
        }

        private void ClearSkillCastSuppress(string reason)
        {
            if (!_skillCastSuppressEmit && !SyncContext.SkillCastSuppressEmit)
                return;
            _skillCastSuppressEmit = false;
            SyncContext.SkillCastSuppressEmit = false;
            _skillCastSuppressSince = 0f;
            _log.LogInfo("[Sync] Skill cast suppress OFF (" + reason + ")");
            // EndTurn may have been deferred while skill suppress was on (AI skill → turn border).
            try { TurnAuth?.TryEmitDeferredEndTurnIfReady(); }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] deferred EndTurn flush: " + ex.Message);
            }
        }

        private void TickHostSkillCastWatchdog()
        {
            if (_net.Role != PeerRole.Host)
                return;
            if (!_skillCastSuppressEmit && _pendingSkillCommand == null)
                return;
            if (_skillCastSuppressSince <= 0.01f)
                return;
            if (Time.unscaledTime - _skillCastSuppressSince < HostSkillCastWatchdogSec)
                return;
            AbortHostSkillCastWatchdog("watchdog-timeout", nackGuest: true);
        }

        /// <summary>B4: clear Suppress / pending; Nack Guest Intent if still waiting.</summary>
        private void AbortHostSkillCastWatchdog(string reason, bool nackGuest)
        {
            var pending = _pendingSkillCommand;
            _pendingSkillCommand = null;
            ClearSkillCastSuppress(reason);
            if (!nackGuest || pending == null || string.IsNullOrEmpty(pending.sourceIntentId))
                return;
            _nackGuestOnReject = true;
            try
            {
                if (_intentSourcePeer.TryGetValue(pending.sourceIntentId, out var peer))
                    _nackTargetPeerId = peer;
                SendIntentNack(pending.sourceIntentId, "skill-timeout", "技能施放超时，请重试");
            }
            finally
            {
                _nackGuestOnReject = false;
                _nackTargetPeerId = null;
                _intentSourcePeer.Remove(pending.sourceIntentId);
            }
            _log.LogWarning("[Sync] CastSkill aborted (" + reason + ") intent=" + pending.sourceIntentId);
        }

        private void OnSkillCastDone()
        {
            if (_net.Role != PeerRole.Host)
                return;
            if (_authority == null || !_authority.InLanBattle)
                return;
            if (!_net.IsConnected || _net.ConnectedPeerCount == 0)
            {
                // Guest left mid-cast: clear suppress — do NOT AbortMatch as broadcast-failed
                // (same false positive as Bus ShouldEmitFromBus / 0.19.6 toast regression).
                var hadGuestIntent = _pendingSkillCommand != null &&
                                     !string.IsNullOrEmpty(_pendingSkillCommand.sourceIntentId);
                AbortHostSkillCastWatchdog("not-connected", nackGuest: hadGuestIntent);
                _log.LogInfo("[Sync] CastSkill done — no guests; skip broadcast (no Abort)");
                return;
            }

            CommandDto cmd;
            if (_pendingSkillCommand != null)
            {
                cmd = _pendingSkillCommand;
                _pendingSkillCommand = null;
            }
            else
            {
                var battle = GS_Battle.self;
                var co = battle?.cur_player?.co_data;
                var extras = _hostSkillExtras;
                if (string.IsNullOrEmpty(extras) && co?.skill != null)
                    extras = co.skill.name;
                cmd = new CommandDto
                {
                    battleId = LanMpPlugin.Instance?.Lobby?.BattleId,
                    turn = battle != null ? battle.turns : 0,
                    playerIndex = battle?.cur_player != null ? battle.cur_player.index : -1,
                    kind = "CastSkill",
                    targetX = _hostSkillHasTarget ? _hostSkillTarget.x : 0,
                    targetY = _hostSkillHasTarget ? _hostSkillTarget.y : 0,
                    hasTarget = _hostSkillHasTarget,
                    extrasJson = extras ?? ""
                };
            }

            _hostSkillHasTarget = false;
            _hostSkillExtras = null;

            // Must clear suppress before ShouldEmitFromBus / Broadcast (Host local path).
            ClearSkillCastSuppress("cast-done");
            HostBroadcastCommand(cmd);
            _log.LogInfo(
                $"[Sync] CastSkill command after cast done hasTarget={cmd.hasTarget} " +
                $"({cmd.targetX},{cmd.targetY}) extras={cmd.extrasJson}");
        }

        /// <summary>Host UX: stamp skill tile before proc_CastSkill so Guest VFX aims correctly.</summary>
        public void NoteHostSkillCastTarget(GameTileData lt)
        {
            if (_net.Role != PeerRole.Host)
                return;
            _hostSkillHasTarget = lt != null;
            if (lt != null)
                _hostSkillTarget = lt.pos;
            try
            {
                var skill = GS_Battle.self?.selected_skill ?? GS_Battle.self?.cur_player?.co_data?.skill_action;
                if (skill?.sd_skill != null)
                    _hostSkillExtras = skill.sd_skill.name;
                else if (GS_Battle.self?.cur_player?.co_data?.skill != null)
                    _hostSkillExtras = GS_Battle.self.cur_player.co_data.skill.name;
            }
            catch { /* ignore */ }
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
            // Bus / AI path: never CaptureBoard on the turn-started stack — outbound pump
            // attaches after NextTick so TurnLoop can Update (Host AI EndTurn white-screen).
            TurnAuth?.ConsumePendingEndTurn();
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
            _pendingActionExtras = null;
            try
            {
                var action = unit.GetAction(cate);
                if (action?.train_template?.sd_unit != null)
                    _pendingActionTemplateId = action.train_template.sd_unit.name;
                else if (GS_Battle.self?.ux_unit_template?.sd_unit != null &&
                         (cate == ActionCate.TRAIN || cate == ActionCate.BUILD))
                    _pendingActionTemplateId = GS_Battle.self.ux_unit_template.sd_unit.name;
                _pendingActionExtras = ActionLegality.CaptureExtrasForIntent(cate, action);
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
            var extras = _pendingActionExtras;
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
                extras = null;
                try
                {
                    var action = unit.GetAction(cate);
                    if (action?.train_template?.sd_unit != null)
                        templateId = action.train_template.sd_unit.name;
                    extras = ActionLegality.CaptureExtrasForIntent(cate, action);
                }
                catch { /* ignore */ }
            }

            EmitDoActionCommand(unit, cate, target, hasTarget, templateId, extras);
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
            string extras = null;
            try
            {
                var action = unit.GetAction(cate);
                if (action?.train_template?.sd_unit != null)
                    templateId = action.train_template.sd_unit.name;
                extras = ActionLegality.CaptureExtrasForIntent(cate, action);
            }
            catch { /* ignore */ }
            EmitDoActionCommand(
                unit, cate,
                target != null ? target.pos : default(Inctor2),
                target != null,
                templateId,
                extras);
        }

        private void EmitDoActionCommand(
            UnitData unit, ActionCate cate, Inctor2 target, bool hasTarget, string templateId,
            string extrasJson = null)
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
                templateId = templateId ?? "",
                extrasJson = extrasJson ?? ""
            });
        }

        private void OnUnitMoved(UnitData unit, Inctor2 from, Inctor2 to)
        {
            if (!ShouldEmitFromBus())
                return;
            if (unit == null)
                return;

            // PL2: already broadcast at DoMove start — skip duplicate end-of-move Command.
            var aheadKey = MakePresentationAheadMoveKey(
                GS_Battle.self != null ? GS_Battle.self.turns : 0,
                unit.unit_id, to.x, to.y);
            if (_presentationAheadMoveKeys.Remove(aheadKey))
            {
                _log.LogInfo($"[Sync] UnitMoved Bus skipped (presentation-ahead) unit={unit.unit_id}");
                return;
            }

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

        /// <summary>
        /// PL2: Host-local DoMove Prefix — broadcast UnitMoved geometry before Host anim finishes.
        /// Deduped against <see cref="OnUnitMoved"/>. Does not mutate Guest (ADR-001).
        /// </summary>
        internal bool TryBroadcastHostLocalMoveAhead(UnitData unit, Inctor2 from, Inctor2 to)
        {
            if (_net.Role != PeerRole.Host || unit == null)
                return false;
            if (SyncContext.SuppressNetworkEmit || SyncContext.ApplyingRemoteCommand || SyncContext.InApplyEnumerator)
                return false;
            if (!ShouldEmitFromBus())
                return false;

            var battle = GS_Battle.self;
            var turn = battle != null ? battle.turns : 0;
            var key = MakePresentationAheadMoveKey(turn, unit.unit_id, to.x, to.y);
            if (!_presentationAheadMoveKeys.Add(key))
                return false;

            float dur = 0.2f;
            try
            {
                if (unit.template?.sd_unit != null)
                    dur = unit.template.sd_unit.ani_speed;
            }
            catch { /* default */ }

            var cmd = new CommandDto
            {
                battleId = LanMpPlugin.Instance?.Lobby?.BattleId,
                turn = turn,
                playerIndex = battle?.cur_player != null ? battle.cur_player.index : -1,
                kind = "UnitMoved",
                netUnitId = unit.unit_id,
                fromX = from.x,
                fromY = from.y,
                targetX = to.x,
                targetY = to.y,
                moveDuration = dur,
                skipResultAttachment = true
            };
            HostBroadcastCommand(cmd);
            _log.LogInfo($"[Sync] UnitMoved presentation-ahead (Host local) unit={unit.unit_id} -> ({to.x},{to.y})");
            return true;
        }

        private static string MakePresentationAheadMoveKey(int turn, int unitId, int toX, int toY)
            => turn + ":" + unitId + ":" + toX + ":" + toY;

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
            if (SyncContext.SuppressNetworkEmit ||
                SyncContext.SkillCastSuppressEmit ||
                _skillCastSuppressEmit)
                return false;
            if (_authority == null || !_authority.InLanBattle)
                return false;
            if (_net.Role != PeerRole.Host)
                return false;
            // Solo Host after Guest left: skip emit — do NOT AbortMatch as broadcast-failed
            // (0.19.6 false positive when peer quits mid-AI / mid-turn).
            if (!_net.IsConnected || _net.ConnectedPeerCount == 0)
            {
                _log.LogInfo("[Sync] Bus emit skipped — no connected guests");
                return false;
            }
            return true;
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
            var waited = Time.unscaledTime - _guestAwaitSince;
            if (waited >= GuestAwaitHardTimeoutSec)
            {
                _log.LogWarning("[Sync] Guest await Intent hard-timeout — clearing " + _guestAwaitIntentId);
                GateUtilToast("主机确认超时，可重试操作");
                ClearGuestAwait("hard-timeout");
                return true;
            }
            if (waited >= GuestAwaitSoftTimeoutSec && !_guestAwaitSoftWarned)
            {
                _guestAwaitSoftWarned = true;
                _log.LogWarning("[Sync] Guest await Intent slow — keeping lock " + _guestAwaitIntentId);
                GateUtilToast("主机响应较慢，请稍候");
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
            _guestAwaitSoftWarned = false;
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
            _guestAwaitSoftWarned = false;

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

            // AutoCmd: Host Bus emits UnitMoved/DoAction without sourceIntentId; wait for AutoCmd ack.
            if (_guestAwaitKind == "AutoCmd")
            {
                if (cmd != null && cmd.kind == "AutoCmd")
                    ClearGuestAwait("cmd-AutoCmd");
                return;
            }

            // Multi-guest: never clear await on another peer's Command kind alone.
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
                // Host-local Accept must not wire IntentNack to Guest (INV-VIEW spectate toast).
                HostAcceptIntent(intent, fromGuestNetwork: false);
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

            // Stamp only — CaptureBoard + TCP run on outbound pump after NextTick.
            StampPresentationHints(cmd);
            _outboundCmds.Enqueue(cmd);
            EnsureOutboundPump();
        }

        private void EnsureOutboundPump()
        {
            if (_outboundPumping || _outboundCmds.Count == 0)
                return;
            if (TryStartCoroutine(CoOutboundPump()))
                return;

            // No GameController yet — flush sync (lobby/bootstrap edge).
            while (_outboundCmds.Count > 0)
            {
                var cmd = _outboundCmds.Dequeue();
                if (!FlushOutboundCommand(cmd))
                {
                    BattleSyncTrace.EvCommand("CmdBroadcastFail", cmd);
                    FailBroadcastAfterApply(cmd.kind ?? "cmd", "send-failed");
                    _outboundCmds.Clear();
                    return;
                }
            }
        }

        private IEnumerator CoOutboundPump()
        {
            _outboundPumping = true;
            try
            {
                while (_outboundCmds.Count > 0)
                {
                    // Breathe before CaptureBoard so TurnLoop / AI coroutine can Update.
                    yield return AnnWCoroutine.NextTick;
                    if (_outboundCmds.Count == 0)
                        break;

                    var cmd = _outboundCmds.Dequeue();
                    var ok = FlushOutboundCommand(cmd);
                    if (!ok)
                    {
                        // One retry after another tick — transient TCP under Host hitch.
                        yield return AnnWCoroutine.NextTick;
                        ok = TrySendPreparedCommand(cmd);
                    }

                    if (!ok)
                    {
                        BattleSyncTrace.EvCommand("CmdBroadcastFail", cmd);
                        FailBroadcastAfterApply(cmd.kind ?? "cmd", "send-failed");
                        _outboundCmds.Clear();
                        yield break;
                    }

                    var hasAttach = !string.IsNullOrEmpty(cmd.resultAttachmentJson);
                    BattleSyncTrace.EvCommand("CmdBroadcast", cmd);
                    _log.LogInfo(
                        $"[Sync] Command broadcast kind={cmd.kind} unit={cmd.netUnitId} attach={hasAttach}");
                }
            }
            finally
            {
                _outboundPumping = false;
                // Commands enqueued while we were finishing — restart pump.
                if (_outboundCmds.Count > 0)
                    EnsureOutboundPump();
            }
        }

        /// <summary>Attach payload + stamp hash/undo + send. Returns false on send failure.</summary>
        private bool FlushOutboundCommand(CommandDto cmd)
        {
            if (cmd == null)
                return true;

            // EndTurn board capture deferred here (bus AI path + Accept enqueue).
            if (cmd.kind == "EndTurn" && string.IsNullOrEmpty(cmd.resultAttachmentJson))
                TurnAuth?.AttachBoardSnapshot(cmd);
            else if (!cmd.skipResultAttachment)
                MaybeAttachResults(cmd);

            if (cmd.kind == "EndTurn")
                LanMpPlugin.Instance?.Checksum?.StampEndTurnHash(cmd);

            try
            {
                var undo = GS_Battle.self?.undo_move;
                cmd.undoAvailable = undo != null ? undo.GetUndoMoveCount() : 0;
            }
            catch { cmd.undoAvailable = 0; }

            return TrySendPreparedCommand(cmd);
        }

        private bool TrySendPreparedCommand(CommandDto cmd)
        {
            try
            {
                if (_net.ConnectedPeerCount == 0)
                    return true;

                return _net.TryBroadcast(new Envelope
                {
                    Type = MsgType.Command,
                    BattleId = cmd.battleId ?? "",
                    PayloadJson = JsonUtil.ToJson(cmd)
                });
            }
            catch (Exception ex)
            {
                _log.LogError("[Sync] Command broadcast exception: " + ex.Message);
                return false;
            }
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

        private void ApplyResultAttachment(
            ResultAttachmentDto attach,
            string commandKind,
            bool snapPositions,
            bool removeMissingUnits = true,
            bool applyWrecks = true)
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
                seatFilter,
                removeMissingUnits,
                applyWrecks);
            // Board truth moved — refresh INV-VIEW FOW + combat UX (no soft TARGET_NOT_VISIBLE).
            RemoteTurnPresentation.RefreshLocalVision(_log);
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
                _outboundCmds.Clear();
                _log.LogError($"[Sync] Applied-not-broadcast kind={kind} detail={detail} — aborting match");
                // Notify remaining Guests then drop them; keep Host listener for lobby return.
                _authority.AbortMatch("broadcast-failed", kind + ":" + (detail ?? ""), broadcast: true, loadMenu: true);
                try { _net.DropAllPeersKeepHosting("broadcast-failed"); }
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
                && cmd.kind != "EndTurn" && cmd.kind != "Surrender")
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

        /// <param name="fromGuestNetwork">
        /// True only for Intents received over the wire. Host-local SubmitIntent failures
        /// must not SendIntentNack — Guest would toast while spectating Host Undo.
        /// </param>
        public void HostAcceptIntent(IntentDto intent, bool fromGuestNetwork = false)
        {
            if (intent == null)
                return;

            var prevNack = _nackGuestOnReject;
            var prevTarget = _nackTargetPeerId;
            _nackGuestOnReject = fromGuestNetwork;
            if (fromGuestNetwork &&
                !string.IsNullOrEmpty(intent.intentId) &&
                _intentSourcePeer.TryGetValue(intent.intentId, out var src))
                _nackTargetPeerId = src;
            else
                _nackTargetPeerId = null;
            try
            {
                HostAcceptIntentCore(intent);
            }
            finally
            {
                _nackGuestOnReject = prevNack;
                _nackTargetPeerId = prevTarget;
                if (!string.IsNullOrEmpty(intent.intentId))
                    _intentSourcePeer.Remove(intent.intentId);
            }
        }

        private void HostAcceptIntentCore(IntentDto intent)
        {
            if (!string.IsNullOrEmpty(intent.intentId) && !_seenIntentIds.Add(intent.intentId))
            {
                _log.LogInfo("[Sync] Duplicate intent ignored " + intent.intentId);
                // Still Nack so Guest clears await (multi-guest: silent drop left Guest stuck 20s).
                if (_nackGuestOnReject)
                    SendIntentNack(intent.intentId, "duplicate", null);
                return;
            }

            if (_nackGuestOnReject)
            {
                if (string.IsNullOrEmpty(_nackTargetPeerId))
                {
                    _log.LogWarning("[Sync] Guest Intent missing SourcePeerId — reject");
                    SendIntentNack(intent.intentId, "no-source-peer", null);
                    return;
                }
                // Surrender: peer must own the surrendering seat (pause menu — not necessarily current turn).
                if (intent.kind == "Surrender")
                {
                    if (!TryValidateGuestPeerOwnsSeat(_nackTargetPeerId, intent.playerIndex, out var seatErr))
                    {
                        SendIntentNack(intent.intentId, seatErr, MapNackMessage(seatErr) ?? "");
                        return;
                    }
                }
                else if (!TryValidateGuestPeerOwnsCurrentTurn(_nackTargetPeerId, out var peerErr))
                {
                    _log.LogWarning("[Sync] Guest peer not current operator: " + peerErr);
                    BattleSyncTrace.EvIntent("IntentNack", intent, detail: peerErr);
                    SendIntentNack(intent.intentId, peerErr, MapNackMessage(peerErr) ?? "");
                    return;
                }
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

            if (intent.kind == "Surrender")
            {
                var seat = FindPlayerByIndex(intent.playerIndex);
                if (seat == null || seat.defeated)
                {
                    SendIntentNack(intent.intentId, "already-defeated", "该席位已战败");
                    return;
                }
                HostApplySurrender(seat, intent.intentId);
                return;
            }

            if (intent.kind == "CastSkill")
            {
                if (!TryBeginHostSkillCast(cmd))
                {
                    SendIntentNack(intent.intentId, "skill-unavailable", "无法释放指挥官技能（能量未满或技能不可用）");
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
            // Host EndTurn Accept (INV-T9 Suppress-only for MannualEndTurn entry).
            // Dual-timeout tradeoff: turn-span wait for EndTurnReady stays unbounded, but
            // SuppressNetworkEmit must NOT span that wait — StartPlayerTurn FOW/OnTurnStart/
            // unit StartTurn (and later AI Bus) would be silenced → Guest never sees actions
            // or EndTurn (0.19.6 regression). Ownership of EndTurn Command uses
            // HostEndTurnAcceptWaiting instead.
            TurnAuth?.ConsumePendingEndTurn();
            SyncContext.HostEndTurnAcceptWaiting = true;
            BattleSyncTrace.Ev("EndTurnAcceptBegin",
                kind: "EndTurn",
                intentId: intentCmd?.sourceIntentId,
                turn: GS_Battle.self != null ? GS_Battle.self.turns : (int?)null,
                curPlayer: GS_Battle.self?.cur_player != null ? GS_Battle.self.cur_player.index : (int?)null);
            try
            {
                SyncContext.SuppressNetworkEmit = true;
                try
                {
                    // Prefix sees Suppress → runs vanilla MannualEndTurn (no re-SubmitIntent).
                    ApplyEndTurnHostLocal();
                }
                finally
                {
                    SyncContext.SuppressNetworkEmit = false;
                }

                BattleSyncTrace.Ev("EndTurnMannualStarted",
                    kind: "EndTurn",
                    turn: GS_Battle.self != null ? GS_Battle.self.turns : (int?)null,
                    curPlayer: GS_Battle.self?.cur_player != null ? GS_Battle.self.cur_player.index : (int?)null);

                // CRITICAL: CoroutineObject treats yield null as same-frame spin — must use float wait
                // or TurnLoop never gets Update and EndTurnReady can never arrive (Host white-screen).
                while (TurnAuth != null && !TurnAuth.EndTurnReady)
                {
                    if (!_authority.InLanBattle || _authority.MatchSettled)
                        break;
                    yield return AnnWCoroutine.NextTick;
                }

                var ready = TurnAuth?.ConsumePendingEndTurn();
                if (ready == null)
                {
                    _log.LogError("[Sync] Host EndTurn Accept — TurnAuthority produced no EndTurn");
                    FailBroadcastAfterApply("EndTurn", "no-turn-auth");
                    yield break;
                }
                ready.sourceIntentId = intentCmd.sourceIntentId;
                // Outbound pump attaches + sends after NextTick (same INV as AI bus EndTurn).
                HostBroadcastCommand(ready);
                BattleSyncTrace.EvCommand("EndTurnAcceptBroadcast", ready);
                _log.LogInfo(
                    $"[Sync] Host EndTurn Accept broadcast ended={ready.endedPlayerIndex}→{ready.nextPlayerIndex}");
            }
            finally
            {
                SyncContext.HostEndTurnAcceptWaiting = false;
                SyncContext.SuppressNetworkEmit = false;
                try { TurnAuth?.TryEmitDeferredEndTurnIfReady(); }
                catch { /* ignore */ }
            }
        }

        private IEnumerator CoHostAcceptAnimated(CommandDto cmd)
        {
            // Keep Suppress through enqueue so Bus cannot emit a twin while Accept is finishing.
            // CaptureBoard+TCP run on outbound pump after Suppress drops (no Bus twin from attach).
            // INV: Host Accept is local sim (Suppress only for emit) — still need presentation stamps
            // before ExecuteAction so Guest SHIELD_GEN / ATTACK are not fast-skipped on Host
            // (moveDuration was 0 until HostBroadcastCommand).
            // INV-T10: SafePump — Host Accept also runs on CoroutineObject; raw nested animators hang.
            // PL2: UnitMoved broadcasts ahead of Host anim so Guest starts lerp in parallel.
            // INV-T9: Host Accept uses SuppressNetworkEmit only — never ApplyingRemoteCommand
            // (that flag is Guest/replay). Bus twin emit blocked by Suppress.
            SyncContext.SuppressNetworkEmit = true;
            try
            {
                StampPresentationHints(cmd);
                var ahead = PresentationRules.ShouldBroadcastAheadOfHostAccept(cmd.kind);
                string aheadKey = null;
                if (ahead)
                {
                    var turn = GS_Battle.self != null ? GS_Battle.self.turns : 0;
                    aheadKey = MakePresentationAheadMoveKey(turn, cmd.netUnitId, cmd.targetX, cmd.targetY);
                    _presentationAheadMoveKeys.Add(aheadKey);
                    cmd.skipResultAttachment = true;
                    HostBroadcastCommand(cmd);
                }

                yield return AnnWCoroutine.SafePump(
                    CoApplyCommandBody(cmd),
                    AnnWCoroutine.DefaultApplyTimeoutSec,
                    _log,
                    "HostAccept:" + (cmd.kind ?? "?"));
                if (!ahead && cmd.kind != "EndTurn")
                    HostBroadcastCommand(cmd);
                // Host Accept DoAction skipped anim for RTT — kick local multi-shot VFX after broadcast
                // so Host still sees the strike while Guest receives attach (non-blocking).
                if (cmd.kind == "DoAction" &&
                    PresentationRules.ShouldPresentAttachOnlyDoAction(cmd.moveDuration))
                {
                    try
                    {
                        var u = ResultAttachmentBridge.FindUnit(cmd.netUnitId);
                        var t = ResolveActionTile(cmd);
                        var cate = (ActionCate)cmd.actionCate;
                        if (u != null && GameController.self != null)
                        {
                            GameController.self.StartCoroutine(AnnWCoroutine.SafePump(
                                ActionPresentation.CoKickDoActionVisual(u, cate, t, _log),
                                AnnWCoroutine.DefaultApplyTimeoutSec,
                                _log,
                                "HostAcceptDoActionVfx"));
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning("[Sync] HostAccept DoAction VFX: " + ex.Message);
                    }
                }
                // Accept suppresses Bus OnUnitMoved — drop ahead key so later moves can re-use tile.
                if (aheadKey != null)
                    _presentationAheadMoveKeys.Remove(aheadKey);
            }
            finally
            {
                SyncContext.SuppressNetworkEmit = false;
                SyncContext.InApplyEnumerator = false;
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
            try
            {
                // Keep energy_max in sync before gate (Host local AfterSkillCast already does).
                co.energy_max = CoEnergyRules.ComputeEnergyMax(co.skill_used_times);
                if (!CoEnergyRules.IsEnergyFull(co.energy, co.energy_max))
                {
                    _log.LogWarning("[Sync] CastSkill rejected — energy not full");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] CastSkill energy gate: " + ex.Message);
                return false;
            }

            GameTileData tile = null;
            if (cmd.hasTarget)
            {
                var pos = new Inctor2(cmd.targetX, cmd.targetY);
                tile = battle.terrain != null ? battle.terrain.GetTile(pos) : null;
                if (tile == null && GameAPI.self != null)
                    tile = GameAPI.self.GetTile(pos);
                // Do NOT fall back to (0,0) — that mis-aims orbital strike / targeted skills.
                // Null tile + vanilla PrepareAction → AutoSetPos().Value Nullable crash.
                if (tile == null)
                {
                    _log.LogWarning(
                        $"[Sync] CastSkill rejected — missing tile ({cmd.targetX},{cmd.targetY})");
                    return false;
                }
            }

            _pendingSkillCommand = cmd;
            BeginSkillCastSuppress("guest-intent");
            NoteHostSkillCastTarget(tile);
            if (!string.IsNullOrEmpty(cmd.extrasJson))
                _hostSkillExtras = cmd.extrasJson;

            // INV-T10: never fire-and-forget proc_SkillDoAction on CoroutineObject.
            // SafePump maps null→NextTick; CastDone-or-abort always clears suppress.
            if (!TryStartCoroutine(CoHostSkillCastPipeline(cmd, tile)))
            {
                _pendingSkillCommand = null;
                ClearSkillCastSuppress("guest-intent-no-host");
                return false;
            }

            _log.LogInfo(
                $"[Sync] Host casting skill for intent hasTarget={cmd.hasTarget} " +
                $"({cmd.targetX},{cmd.targetY}) extras={cmd.extrasJson}");
            return true;
        }

        /// <summary>
        /// Host Accept CastSkill body — SafePump vanilla cast; if CastDone never fires
        /// (Nullable / NRE inside DoActionAni), abort suppress + Nack Guest so the match
        /// does not soft-lock until the 45s watchdog.
        /// </summary>
        private IEnumerator CoHostSkillCastPipeline(CommandDto cmd, GameTileData tile)
        {
            var ux = UX_Manager.self;
            var battle = GS_Battle.self;
            var co = battle?.cur_player?.co_data;
            if (ux == null || co?.skill_action == null)
            {
                AbortHostSkillCastWatchdog("cast-missing-ux", nackGuest: true);
                yield break;
            }

            IEnumerator body = null;
            try
            {
                battle.selected_skill = co.skill_action;
                ux.SetUXState_Skill(co.skill_action);
                body = ux.proc_SkillDoAction(tile);
            }
            catch (Exception ex)
            {
                _log.LogError("[Sync] HostSkillCast setup: " + ex);
                AbortHostSkillCastWatchdog("cast-setup-fail", nackGuest: true);
                yield break;
            }

            yield return AnnWCoroutine.SafePump(
                body,
                HostSkillCastWatchdogSec,
                _log,
                "HostSkillCast");

            // Success path: OnSkillCastDone already ClearSkillCastSuppress + broadcast.
            if (SyncContext.SkillCastSuppressEmit || _pendingSkillCommand != null)
            {
                _log.LogWarning("[Sync] Host skill cast ended without CastDone — abort");
                AbortHostSkillCastWatchdog("cast-incomplete", nackGuest: true);
            }
        }

        /// <summary>
        /// Multi-guest: Intent source peer must own the current human seat (ADR-001).
        /// </summary>
        private bool TryValidateGuestPeerOwnsCurrentTurn(string sourcePeerId, out string error)
        {
            error = null;
            var battle = GS_Battle.self;
            var cur = battle?.cur_player;
            if (cur == null)
            {
                error = "no-player";
                return false;
            }
            if (cur.is_ai)
            {
                error = "not-current-player";
                return false;
            }
            var owner = _authority?.GetOwnerPeerIdForSeat(cur.index);
            if (string.IsNullOrEmpty(owner) ||
                !string.Equals(owner, sourcePeerId, StringComparison.Ordinal))
            {
                error = "not-current-player";
                return false;
            }
            return true;
        }

        private bool TryValidateGuestPeerOwnsSeat(string sourcePeerId, int seatIndex, out string error)
        {
            error = null;
            if (seatIndex < 0)
            {
                error = "no-player";
                return false;
            }
            var owner = _authority?.GetOwnerPeerIdForSeat(seatIndex);
            if (string.IsNullOrEmpty(owner) ||
                !string.Equals(owner, sourcePeerId, StringComparison.Ordinal))
            {
                error = "not-your-seat";
                return false;
            }
            return true;
        }

        private static Player FindPlayerByIndex(int index)
        {
            var players = GS_Battle.self?.all_player?.players;
            if (players == null)
                return null;
            foreach (var p in players)
            {
                if (p != null && p.index == index)
                    return p;
            }
            return null;
        }

        /// <summary>
        /// Mark seat defeated, clear army (sync-friendly), spectate path via TriggerPlayerDefeat.
        /// MatchEnd only when LAN skirmish has ≤1 living faction (see <c>SkirmishEndRules</c>) —
        /// allied AI / remote humans keep fighting; vanilla "no humans left" hotseat end is skipped.
        /// </summary>
        public void HostApplySurrender(Player seat, string sourceIntentId)
        {
            if (_net.Role != PeerRole.Host || seat == null)
                return;
            if (seat.defeated)
                return;
            if (_authority != null && _authority.MatchSettled)
                return;

            _log.LogInfo("[Sync] HostApplySurrender seat=" + seat.index + " intent=" + (sourceIntentId ?? "-"));

            SyncContext.SuppressNetworkEmit = true;
            try
            {
                seat.defeated = true;
                try { BattleEventBus.self.TriggerPlayerDefeat(seat); }
                catch (Exception ex)
                {
                    _log.LogWarning("[Sync] TriggerPlayerDefeat: " + ex.Message);
                }

                try
                {
                    var msg = SingletonMono<SS_ANNW_Game>.self?.ui?.messages;
                    if (msg != null)
                    {
                        var text = string.Format(
                            LAN.Get("MSG_PlayerDefeat") ?? "Player {0} defeated",
                            seat.index + 1);
                        msg.AddMessage(text);
                    }
                }
                catch { /* ignore */ }

                // Instant clear — animated wipe would race CaptureBoard (Guest would still see army).
                var doomed = new List<UnitData>();
                if (seat.units != null)
                    doomed.AddRange(seat.units);
                foreach (var u in doomed)
                {
                    if (u == null || u.dead)
                        continue;
                    try { GameAPI.self?.RemoveUnit(u); }
                    catch (Exception ex)
                    {
                        _log.LogWarning("[Sync] Surrender RemoveUnit: " + ex.Message);
                    }
                }
                try
                {
                    var reinforce = GS_Battle.self?.all_reinforce;
                    if (reinforce != null)
                        AccessTools.Method(typeof(AllReinforceData), "RemoveByPlayer", new[] { typeof(Player) })
                            ?.Invoke(reinforce, new object[] { seat });
                }
                catch { /* ignore */ }
            }
            finally
            {
                SyncContext.SuppressNetworkEmit = false;
            }

            // SkirmishLogic may have EndGame → MatchSettled; otherwise sync defeated board to Guests.
            if (_authority != null && _authority.MatchSettled)
                return;

            var cmd = new CommandDto
            {
                cmdId = Guid.NewGuid().ToString("N"),
                sourceIntentId = sourceIntentId ?? "",
                battleId = LanMpPlugin.Instance?.Lobby?.BattleId ?? "",
                turn = GS_Battle.self != null ? GS_Battle.self.turns : 0,
                playerIndex = seat.index,
                kind = "Surrender",
                hasTarget = false
            };
            MaybeAttachResults(cmd);
            HostBroadcastCommand(cmd);
            _log.LogInfo("[Sync] Surrender broadcast seat=" + seat.index);
        }

        private bool TryValidateAgainstBattle(IntentDto intent, out string error)
        {
            var battle = GS_Battle.self;
            var bid = LanMpPlugin.Instance?.Lobby?.BattleId ?? "";
            var turn = battle != null ? battle.turns : -1;
            var cur = battle?.cur_player != null ? battle.cur_player.index : -1;

            // Surrender is pause-menu — not tied to current turn cursor.
            if (intent.kind == "Surrender")
            {
                error = null;
                if (_authority == null || !_authority.InLanBattle || !_authority.GatesArmed)
                {
                    error = "gates-inactive";
                    return false;
                }
                if (!string.IsNullOrEmpty(bid) &&
                    !string.IsNullOrEmpty(intent.battleId) &&
                    intent.battleId != bid)
                {
                    error = "battle-mismatch";
                    return false;
                }
                var seat = FindPlayerByIndex(intent.playerIndex);
                if (seat == null)
                {
                    error = "no-player";
                    return false;
                }
                if (seat.defeated)
                {
                    error = "already-defeated";
                    return false;
                }
                return true;
            }

            if (!IntentValidateRules.TryValidateBasics(
                    _authority != null && _authority.InLanBattle,
                    _authority != null && _authority.GatesArmed,
                    bid, intent, turn, cur, out error))
                return false;

            if (intent.kind == "Undo")
            {
                var n = 0;
                try { n = GS_Battle.self?.undo_move?.GetUndoMoveCount() ?? 0; }
                catch { n = 0; }
                var actionedOnStack = HostUndoStackContainsActionedUnit();
                if (!IntentValidateRules.CanAcceptUndo(n, actionedOnStack))
                {
                    if (actionedOnStack)
                        HostClearUndoStack("validate-actioned");
                    error = "nothing-to-undo";
                    return false;
                }
                return true;
            }

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
                if (intent.kind == "DoAction" &&
                    IntentValidateRules.IsUnitSpentForIntent(
                        "DoAction", unit.moved, unit.actioned, intent.actionCate))
                {
                    error = "already-actioned";
                    return false;
                }

                // INV-ACCEPT: Host ActionLegality only (geometry + owner-FOW SEEN hard).
                // Bind BUILD/TRAIN/UNLOAD UX from extras before CanDoAction.
                if (intent.kind == "DoAction")
                {
                    if (!ActionLegality.TryValidateDoAction(
                            unit, intent.actionCate, intent.hasTarget,
                            intent.targetX, intent.targetY, intent.extrasJson, out error))
                        return false;
                }
                else if (intent.kind == "UnitMoved")
                {
                    if (!ActionLegality.TryValidateUnitMoved(
                            unit, intent.targetX, intent.targetY, out error))
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
            // Host-local Undo/EndTurn reject must stay Host-only — Guest toast during spectate.
            if (!_nackGuestOnReject)
            {
                _log.LogInfo("[Sync] Host-local Intent reject suppressed (no Nack wire) code=" + (code ?? ""));
                return;
            }
            var nack = new IntentNackDto
            {
                intentId = intentId ?? "",
                code = code ?? "reject",
                message = message ?? "操作被拒绝"
            };
            var env = new Envelope
            {
                Type = MsgType.IntentNack,
                BattleId = LanMpPlugin.Instance?.Lobby?.BattleId ?? "",
                PayloadJson = JsonUtil.ToJson(nack)
            };
            if (!string.IsNullOrEmpty(_nackTargetPeerId))
            {
                if (!_net.TrySendTo(_nackTargetPeerId, env))
                    _log.LogWarning("[Sync] IntentNack send failed peer=" + _nackTargetPeerId);
            }
            else
            {
                // Never broadcast Nack — other Guests would clear await / toast wrongly.
                _log.LogWarning("[Sync] IntentNack suppressed — no target peer code=" + (code ?? ""));
            }
        }

        private static string MapNackMessage(string code)
        {
            var legality = ActionLegality.MapUserMessage(code);
            if (!string.IsNullOrEmpty(legality))
                return legality;

            switch (code)
            {
                case "already-moved":
                    return "该单位本回合已移动";
                case "already-actioned":
                    return "该单位本回合已行动";
                case "nothing-to-undo":
                    return "没有可撤回的移动";
                case "not-current-player":
                case "turn-mismatch":
                case "duplicate":
                case "no-source-peer":
                    return null;
                case "unit-not-owned":
                    return InputGateRules.BlockReasonNotYourUnit;
                case "not-your-seat":
                    return "只能投降本席";
                case "already-defeated":
                    return "该席位已战败";
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
                templateId = ActionExtrasCodec.TryGetTrainTemplateId(intent.extrasJson, out var tpl)
                    ? tpl
                    : "",
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
            // Flags already set by CommandApplyQueue; body is SafePump'd at the queue boundary
            // (INV-T10) — may yield nested IEnumerator / null from vanilla animators freely.
            try
            {
                if (cmd.kind != "EndTurn")
                    BattleSyncTrace.EvCommand("CmdApply", cmd);

                if (cmd.kind == "EndTurn")
                {
                    // LifeTime skill summons expire on Host UnitData.EndTurn → Die → absent from
                    // CaptureBoard. Present death then remove (same pattern as CastSkill orphans).
                    yield return CoApplyGuestEndTurn(cmd);
                    yield break;
                }

                if (NeedsAnimatedApply(cmd.kind))
                {
                    yield return CoApplyCommandBody(cmd);
                    try
                    {
                        var attach = ResultAttachmentCodec.FromJson(cmd.resultAttachmentJson);
                        if (ResultAttachmentCodec.HasPayload(attach) &&
                            cmd.kind != "DoAction" && cmd.kind != "CastSkill")
                        {
                            // DoAction / CastSkill apply inside their attach-only coroutines.
                        }
                        // UnitMoved: body animates then board stamp (positions already lerped).
                        if (ResultAttachmentCodec.HasPayload(attach) && cmd.kind == "UnitMoved")
                            ApplyResultAttachment(attach, cmd.kind, snapPositions: false);
                        // Do NOT re-Apply DoAction here — CoApplyDoAction attach-only already did
                        // (double Apply caused ownership/HP flicker and MindControl thrash).
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
                    {
                        ApplyResultAttachment(attach, cmd.kind, snapPositions: true);
                        if (cmd.kind == "Undo")
                            ActionPresentation.AfterAttachApply(attach, _log, cmd, null);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("[Sync] queue instant attach: " + ex.Message);
                }
            }
            finally
            {
                // Release Guest in-flight only after this Command's apply finished (or failed).
                NoteGuestCommandResolved(cmd);
            }
        }

        private IEnumerator CoApplyGuestEndTurn(CommandDto cmd)
        {
            BattleSyncTrace.EvCommand("CmdApply", cmd, detail: "EndTurn");
            var idsBefore = ActionPresentation.SnapshotAliveIds();
            ResultAttachmentDto attach = null;
            try { attach = ResultAttachmentCodec.FromJson(cmd.resultAttachmentJson); }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] EndTurn attach: " + ex.Message);
            }

            if (ResultAttachmentCodec.HasPayload(attach))
            {
                // Orphans = Host LifeTime / combat / wipe removals this turn boundary.
                var doomed = ActionPresentation.CollectMissingUnits(idsBefore, attach);
                var deferOrphans = doomed != null && doomed.Count > 0;

                ApplyResultAttachment(attach, cmd.kind, snapPositions: true,
                    removeMissingUnits: !deferOrphans, applyWrecks: !deferOrphans);

                if (deferOrphans)
                    yield return CoPresentDeferredDeathsAndWrecks(doomed, attach, attacker: null);
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

        /// <summary>
        /// Guest death presentation then authoritative remove + wreck puddle stamp.
        /// Waits size-aware lead so buildings reach Event_DieExplode (debris) before Dispose.
        /// </summary>
        private IEnumerator CoPresentDeferredDeathsAndWrecks(
            List<UnitData> doomed,
            ResultAttachmentDto attach,
            UnitData attacker)
        {
            var lead = ActionPresentation.KickUnitDeathVisuals(doomed, attacker, _log);
            if (lead > 0.001f)
                yield return lead;

            var hostIds = new HashSet<int>();
            if (attach?.units != null)
            {
                foreach (var us in attach.units)
                {
                    if (us != null)
                        hostIds.Add(us.unitId);
                }
            }

            using (SyncContext.BeginRemoteApply())
            {
                ResultAttachmentBridge.RemoveUnitsMissingFromHost(hostIds, GS_Battle.self, _log);
                // Puddle after Dispose so WreckRenderer sees cleared tiles (ADR-003 R4).
                ResultAttachmentBridge.ApplyWrecks(attach?.wrecks, GS_Battle.self, _log);
            }
        }

        /// <summary>Legacy sync entry (Host-local / non-queue); prefer <see cref="CoApplyGuestEndTurn"/>.</summary>
        private void ApplyGuestEndTurn(CommandDto cmd)
        {
            // Keep for any non-coroutine callers; no death lead-in.
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
            // Game update: MannualEndTurn is private — invoke via AccessTools.
            var mi = AccessTools.Method(typeof(GameAPI), "MannualEndTurn");
            if (mi == null)
            {
                _log.LogError("[Sync] GameAPI.MannualEndTurn missing after game update");
                return;
            }
            mi.Invoke(GameAPI.self, null);
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
            try
            {
                yield return AnnWCoroutine.SafePump(
                    CoApplyCommandBody(cmd),
                    AnnWCoroutine.DefaultApplyTimeoutSec,
                    _log,
                    "Local:" + (cmd.kind ?? "?"));

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
            finally
            {
                SyncContext.SuppressNetworkEmit = false;
                SyncContext.ApplyingRemoteCommand = false;
                SyncContext.InApplyEnumerator = false;
            }

            if (error != null)
                _log.LogError("[Sync] CoApplyCommandLocally: " + error);
        }

        private static bool NeedsAnimatedApply(string kind)
        {
            return kind == "DoAction" || kind == "UnitMoved" || kind == "CastSkill";
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
                case "CastSkill":
                    yield return CoApplyCastSkillAttachOnly(cmd);
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
                    // Host Accept path only — Guest uses CoApplyGuestEndTurn via queue.
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
                case "Surrender":
                    // Board truth in attachment (defeated + wiped units); spectate via player.defeated.
                    _log.LogInfo("[Sync] Surrender apply seat=" + cmd.playerIndex);
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
            // Presentation-only: fire Event_DoActionAni before attach so Guest still sees attack/build cues.
            if (AttachmentApplyPolicy.ShouldGuestAttachOnlyDoAction(isGuest, hasAttach) ||
                (isBuildLike && hasAttach && isGuest))
            {
                yield return CoApplyDoActionAttachOnly(cmd, unit, cate, attachEarly, idsBefore, "guest");
                yield break;
            }

            // Host BUILD/TRAIN with attachment (rare) — still attach-only to avoid double create.
            if (isBuildLike && hasAttach)
            {
                yield return CoApplyDoActionAttachOnly(cmd, unit, cate, attachEarly, idsBefore, "host-build");
                HostClearUndoStack("DoAction-host-build");
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

            EnsureActionUxContext(unit, cate, cmd);

            TryLookAtUnit(unit);

            // Host Accept: skip ExecuteAction anim so Command+attach broadcasts promptly (Guest lag).
            // Local VFX kicked after Accept body without blocking broadcast (CoHostAcceptAnimated).
            var skipAnim = PresentationRules.ShouldFastPresent(cmd.moveDuration, "DoAction")
                           || PresentationRules.ShouldSkipHostAcceptDoActionAnim(
                               SyncContext.SuppressNetworkEmit);
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
                // GameController.ExecuteAction does NOT clear undo (unlike UX_Manager.proc_UnitsDoAction).
                // Without this, Guest can Undo after acting and move/act again (ADR-001).
                HostClearUndoStack("DoAction-ExecuteAction");
                _log.LogInfo(
                    $"[Sync] Applied DoAction(animated) unit={cmd.netUnitId} cate={cmd.actionCate} hasTarget={cmd.hasTarget} skipAnim={skipAnim}");
            }
            else
            {
                ApplyDoActionInstant(cmd);
                HostClearUndoStack("DoAction-instant-fallback");
            }

            TryLookAtUnit(unit);
        }

        /// <summary>
        /// Attach-only apply with optional visual kick (Event_DoActionAni) — no ExecuteAction / DoActionCell.
        /// Upholds ADR-001 attachment truth + ADR-003 R4 (presentation may diverge).
        /// Stamp attach BEFORE presentation so Guest FOW (radar) + Intent unlock are timely;
        /// VFX continues under <see cref="SyncContext.PresentationUnlockIntent"/>.
        /// </summary>
        private IEnumerator CoApplyDoActionAttachOnly(
            CommandDto cmd,
            UnitData unit,
            ActionCate cate,
            ResultAttachmentDto attachEarly,
            HashSet<int> idsBefore,
            string tag)
        {
            EnsureActionUxContext(unit, cate, cmd);
            TryLookAtUnit(unit);

            var skipAnim = !PresentationRules.ShouldPresentAttachOnlyDoAction(cmd.moveDuration);
            var tile = ResolveActionTile(cmd);

            // Combat kills are absent from CaptureBoard (Host already removed them) — present death
            // before RemoveUnit/Dispose. BUILD/TRAIN orphans stay silent instant remove.
            var isBuildLike = cate == ActionCate.BUILD || cate == ActionCate.TRAIN ||
                              cate == ActionCate.QUICK_BUILD_MINER;
            var doomed = !isBuildLike
                ? ActionPresentation.CollectMissingUnits(idsBefore, attachEarly)
                : null;
            var deferOrphans = doomed != null && doomed.Count > 0;

            // Board + FOW first (radar / build vision) — do not wait on attack VFX.
            ApplyResultAttachment(attachEarly, "DoAction", snapPositions: false,
                removeMissingUnits: !deferOrphans, applyWrecks: !deferOrphans);

            // Unlock Guest Intent as soon as attach stamped (still inside ApplyQueue).
            NoteGuestCommandResolved(cmd);
            SyncContext.PresentationUnlockIntent = true;

            if (deferOrphans)
                yield return CoPresentDeferredDeathsAndWrecks(doomed, attachEarly, attacker: unit);

            ActionPresentation.AfterAttachApply(attachEarly, _log, cmd, idsBefore);
            // ADR-001: Host attachment owns actioned/moved (factories may keep acting while bp_left>0).
            // Never force-spent after attach — that blocked Guest SET_TRAIN_POS / multi-TRAIN.
            EnsureUnitActedIfAbsentFromAttach(unit, attachEarly);
            ResultAttachmentBridge.RefreshUnactionedLists(_log);

            if (!skipAnim)
            {
                yield return ActionPresentation.CoKickDoActionVisual(unit, cate, tile, _log);
                ActionPresentation.FinishDoActionVisual(unit, cate, tile, _log);
            }
            else
            {
                ActionPresentation.FinishDoActionVisual(unit);
            }

            _log.LogInfo(
                $"[Sync] Applied DoAction(attach-only/{tag}) unit={cmd.netUnitId} cate={cate} deaths={doomed?.Count ?? 0}");
        }

        /// <summary>
        /// Guest CastSkill: attach-only + death/summon presentation (audit B1/B7/B8) + FOW refresh (B6).
        /// </summary>
        private IEnumerator CoApplyCastSkillAttachOnly(CommandDto cmd)
        {
            ResultAttachmentDto attach = null;
            try { attach = ResultAttachmentCodec.FromJson(cmd.resultAttachmentJson); }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] CastSkill attach parse: " + ex.Message);
            }

            if (!ResultAttachmentCodec.HasPayload(attach))
            {
                _log.LogWarning("[Sync] CastSkill without attachment — skip");
                yield break;
            }

            var idsBefore = ActionPresentation.SnapshotAliveIds();
            // Cast-process VFX (DoActionAni + banner) before attach — M07 B7 / ADR-003 R4.
            yield return ActionPresentation.CoKickSkillCastVisual(cmd, _log);

            try { ResultAttachmentBridge.PreSpawnMissing(attach, _log); }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] CastSkill PreSpawn: " + ex.Message);
            }

            var doomed = ActionPresentation.CollectMissingUnits(idsBefore, attach);
            var deferOrphans = doomed != null && doomed.Count > 0;

            ApplyResultAttachment(attach, "CastSkill", snapPositions: true,
                removeMissingUnits: !deferOrphans, applyWrecks: !deferOrphans);

            if (deferOrphans)
                yield return CoPresentDeferredDeathsAndWrecks(doomed, attach, attacker: null);

            ActionPresentation.AfterAttachApply(attach, _log, cmd, idsBefore);
            ClearGuestSkillUxAfterCast();
            RemoteTurnPresentation.RefreshLocalVision(_log);
            ResultAttachmentBridge.RefreshUnactionedLists(_log);
            _log.LogInfo(
                $"[Sync] Applied CastSkill(attach-only) deaths={doomed?.Count ?? 0} units={attach.units?.Length ?? 0}");
        }

        /// <summary>
        /// Guest never runs AfterSkillCast / proc_SkillDoAction epilogue — clear UX skill selection
        /// so the next turn does not keep a stale selected_skill highlight.
        /// </summary>
        private static void ClearGuestSkillUxAfterCast()
        {
            try
            {
                var battle = GS_Battle.self;
                if (battle != null)
                {
                    battle.selected_skill = null;
                    battle.ux_action_cate = ActionCate.NONE;
                }
                try { BattleEventBus.self.TriggerUXStateChanged(); }
                catch { /* ignore */ }
            }
            catch { /* ignore */ }
        }

        private static void EnsureUnitActed(UnitData unit)
        {
            if (unit == null)
                return;
            unit.actioned = true;
            unit.moved = true;
        }

        /// <summary>
        /// Fallback only when Host attachment omitted this unit. Otherwise trust stamped actioned/moved.
        /// </summary>
        private static void EnsureUnitActedIfAbsentFromAttach(UnitData unit, ResultAttachmentDto attach)
        {
            if (unit == null)
                return;
            if (attach?.units != null && ResultAttachmentCodec.FindUnit(attach, unit.unit_id) != null)
                return;
            EnsureUnitActed(unit);
        }

        private static GameTileData ResolveActionTile(CommandDto cmd)
        {
            if (cmd == null || !cmd.hasTarget)
                return null;
            if (GameAPI.self == null)
                return null;
            return GameAPI.self.GetTile(new Inctor2(cmd.targetX, cmd.targetY));
        }

        /// <summary>
        /// Rebind UX-only action context (train_template / ux_unload_unit) before
        /// ExecuteAction / attach-only visuals (Validate uses ActionLegality.Prepare*).
        /// </summary>
        private void EnsureActionUxContext(UnitData unit, ActionCate cate, CommandDto cmd)
        {
            if (unit == null || cmd == null)
                return;

            var extras = cmd.extrasJson;
            if (string.IsNullOrEmpty(extras) && !string.IsNullOrEmpty(cmd.templateId))
                extras = ActionExtrasCodec.FromTrainTemplate(cmd.templateId);
            else if (!string.IsNullOrEmpty(cmd.templateId) &&
                     ActionExtrasCodec.NeedsTrainTemplate((int)cate) &&
                     !ActionExtrasCodec.TryGetTrainTemplateId(extras, out _))
                extras = ActionExtrasCodec.FromTrainTemplate(cmd.templateId);

            if (string.IsNullOrEmpty(extras) &&
                ActionExtrasCodec.NeedsTrainTemplate((int)cate))
            {
                try
                {
                    if (GS_Battle.self?.ux_unit_template?.sd_unit != null)
                        extras = ActionExtrasCodec.FromTrainTemplate(
                            GS_Battle.self.ux_unit_template.sd_unit.name);
                }
                catch { /* ignore */ }
            }

            ActionLegality.PrepareActionUxContext(unit, cate, extras);
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
            EnsureActionUxContext(unit, cate, cmd);
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
            // Guest Intent moves never hit EQ AddUndoMoveBatch — push Host stack here or Undo is a no-op.
            if (_net.Role == PeerRole.Host)
            {
                Exception moveErr = null;
                try
                {
                    if (unit.pos.x != from.x || unit.pos.y != from.y)
                        GameAPI.self.MoveUnitInstantly(unit, from);
                    HostPushUndoForAcceptedMove(unit, from, to);
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
                    RemoteTurnPresentation.RefreshLocalVision(_log);
                    yield break;
                }

                var guard = 0f;
                while (unit.in_animation && guard < 10f)
                {
                    guard += Time.unscaledDeltaTime;
                    yield return AnnWCoroutine.NextTick;
                }
                _log.LogInfo($"[Sync] Applied UnitMoved(DoMove) unit={cmd.netUnitId} -> ({cmd.targetX},{cmd.targetY})");
                RemoteTurnPresentation.RefreshLocalVision(_log);
                yield break;
            }

            if (GameAPI.self != null)
            {
                if (fast)
                {
                    ApplyUnitMovedInstant(cmd);
                    try { BattleEventBus.self.TriggerFOWChanged(); }
                    catch { /* ignore */ }
                    RemoteTurnPresentation.RefreshLocalVision(_log);
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
                RemoteTurnPresentation.RefreshLocalVision(_log);
                _log.LogInfo($"[Sync] Applied UnitMoved(animated) unit={cmd.netUnitId} -> ({cmd.targetX},{cmd.targetY})");
            }
            else
            {
                ApplyUnitMovedInstant(cmd);
                RemoteTurnPresentation.RefreshLocalVision(_log);
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
            var to = new Inctor2(cmd.targetX, cmd.targetY);
            // Prefer boarding when landing on a transporter — MoveUnitInstantly alone desyncs cargo.
            try
            {
                var tile = GameAPI.self != null ? GameAPI.self.GetTile(to) : null;
                var other = tile != null ? tile.GetUnit() : null;
                if (other != null && other != unit && other.wp_transport != null)
                {
                    unit.UnRegPos(null);
                    other.wp_transport.TransportLoad(unit);
                    unit.pos = to;
                    try { unit.Event_UpdatePos?.Invoke(); }
                    catch { /* ignore */ }
                    _log.LogInfo($"[Sync] Applied UnitMoved(instant+load) unit={cmd.netUnitId}");
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] UnitMoved transport load: " + ex.Message);
            }
            GameAPI.self.MoveUnitInstantly(unit, to);
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
                RemoteTurnPresentation.RefreshLocalVision(_log);
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
                var before = undo.GetUndoMoveCount();
                undo.UndoLastMove();
                var after = undo.GetUndoMoveCount();
                if (before <= 0 || after >= before)
                    _log.LogWarning($"[Sync] UndoLastMove no-op stack before={before} after={after}");
                else
                    _log.LogInfo($"[Sync] Applied Undo stack {before}→{after}");
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] Undo apply: " + ex.Message);
            }
        }

        /// <summary>
        /// Guest Intent UnitMoved never goes through EQ AddUndoMoveBatch.
        /// Host must push the batch before DoMove so Undo Intent can UndoLastMove (ADR-001).
        /// </summary>
        private void HostPushUndoForAcceptedMove(UnitData unit, Inctor2 from, Inctor2 to)
        {
            if (unit == null)
                return;
            var undo = GS_Battle.self?.undo_move;
            if (undo == null)
                return;
            try
            {
                if (GS_Battle.self.functions != null &&
                    GS_Battle.self.functions.Querry(GAME_FUNCTION.NoUndoMove))
                    return;
            }
            catch { /* ignore */ }

            UnitData transporter = null;
            try
            {
                var gtd = GameAPI.self != null ? GameAPI.self.GetTile(to) : null;
                var other = gtd != null ? gtd.GetUnit() : null;
                if (other != null && other != unit && other.wp_transport != null)
                    transporter = other;
            }
            catch { /* ignore */ }

            var batch = new List<UndoableMoveInfo>
            {
                new UndoableMoveInfo
                {
                    unit = unit,
                    pos = from,
                    transporter = transporter
                }
            };
            undo.AddUndoMoveBatch(batch);
            _log.LogInfo(
                $"[Sync] Host undo stack push unit={unit.unit_id} from=({from.x},{from.y}) depth={undo.GetUndoMoveCount()}");
        }

        /// <summary>
        /// Mirror vanilla UX_Manager.proc_UnitsDoAction: acting clears undo.
        /// Host Accept uses GameController.ExecuteAction which never clears.
        /// </summary>
        private void HostClearUndoStack(string reason)
        {
            if (_net.Role != PeerRole.Host)
                return;
            try
            {
                var undo = GS_Battle.self?.undo_move;
                if (undo == null)
                    return;
                var before = undo.GetUndoMoveCount();
                if (before <= 0)
                    return;
                undo.ClearUndoableMoveList();
                _log.LogInfo($"[Sync] Cleared undo stack ({reason}) depth {before}→0");
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Sync] ClearUndo: " + ex.Message);
            }
        }

        /// <summary>True if any unit referenced by Host undo batches is already actioned.</summary>
        private static bool HostUndoStackContainsActionedUnit()
        {
            try
            {
                var undo = GS_Battle.self?.undo_move;
                if (undo == null)
                    return false;
                var field = AccessTools.Field(typeof(UndoMoveData), "undo_move_batchs");
                var batches = field?.GetValue(undo) as System.Collections.IList;
                if (batches == null || batches.Count == 0)
                    return false;
                foreach (var batchObj in batches)
                {
                    if (!(batchObj is System.Collections.IEnumerable batch))
                        continue;
                    foreach (var infoObj in batch)
                    {
                        if (infoObj == null)
                            continue;
                        var unitField = AccessTools.Field(infoObj.GetType(), "unit")
                                        ?? AccessTools.Field(infoObj.GetType(), "Unit");
                        var u = unitField?.GetValue(infoObj) as UnitData;
                        if (u != null && u.actioned)
                            return true;
                    }
                }
            }
            catch { /* ignore */ }
            return false;
        }

        private void OnEnvelope(Envelope env)
        {
            if (env.Type == MsgType.Intent && _net.Role == PeerRole.Host)
            {
                var intent = JsonUtil.FromJson<IntentDto>(env.PayloadJson);
                if (intent != null)
                {
                    if (!string.IsNullOrEmpty(intent.intentId) && !string.IsNullOrEmpty(env.SourcePeerId))
                        _intentSourcePeer[intent.intentId] = env.SourcePeerId;
                    HostAcceptIntent(intent, fromGuestNetwork: true);
                }
                return;
            }

            if (env.Type == MsgType.IntentNack && _net.Role == PeerRole.Guest)
            {
                var nack = JsonUtil.FromJson<IntentNackDto>(env.PayloadJson);
                if (nack == null)
                    return;
                var matchesAwait = !string.IsNullOrEmpty(nack.intentId) &&
                    string.Equals(nack.intentId, _guestAwaitIntentId, StringComparison.Ordinal);
                if (!string.IsNullOrEmpty(nack.intentId))
                    _guestOptimisticDone.Remove(nack.intentId);
                if (matchesAwait)
                    ClearGuestAwait("nack");
                // Multi-guest: ignore Nacks for other peers' intents.
                if (!matchesAwait && !string.IsNullOrEmpty(nack.intentId))
                    return;
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
                // Await clears AFTER ApplyQueue finishes this Command (see CoApplyQueuedCommand).
                // Clearing on recv allowed a second CastSkill before energy attach (Host L1638).
                ApplyCommandLocally(cmd);
            }
        }
    }
}
