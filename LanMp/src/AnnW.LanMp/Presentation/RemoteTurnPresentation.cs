using System.Collections;
using AnnW.LanMp.Patches;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using BepInEx.Logging;
using ANNW;
using UnityEngine;

namespace AnnW.LanMp.Presentation
{
    /// <summary>
    /// ADR-004 presentation layer: Guest does not run vanilla StartPlayerTurn.
    /// Host suppresses foreign-human seat UX (INV-VIEW). No game-state mutation beyond FOW refresh.
    /// </summary>
    internal static class RemoteTurnPresentation
    {
        private static bool _grantRunning;

        internal static bool ShouldRunVanillaSeatPresentation(Player seat)
        {
            if (!GateUtil.LanArmed(out var plugin))
                return true;
            var local = plugin.Authority.GetLocalHumanSlotIndex();
            return PresentationRules.ShouldRunVanillaSeatPresentation(
                plugin.Authority.InLanBattle,
                plugin.Authority.GatesArmed,
                GateUtil.IsBattlePlayPhase(),
                seat != null && seat.is_ai,
                seat != null ? seat.index : -1,
                local ?? -1,
                local.HasValue);
        }

        private static int _lastHintSeat = -1;
        private static int _lastHintTurn = -1;

        /// <summary>Called after Guest cursor write or when RemoteWatch begins on a foreign seat.</summary>
        internal static void OnSeatActivated(Player seat, bool isLocalHuman, ManualLogSource log)
        {
            if (!GateUtil.LanArmed(out _))
                return;
            var battle = GS_Battle.self;
            if (battle == null || seat == null)
                return;

            // Spectating with leftover MOVE_SELECT shows *own* attack extend instead of enemy hover threat.
            if (!isLocalHuman || seat.is_ai)
                ClearSpectateUxOverlays("seat-activated", log);

            if (battle.turns == _lastHintTurn && seat.index == _lastHintSeat)
                return;
            _lastHintTurn = battle.turns;
            _lastHintSeat = seat.index;

            RefreshLocalVision(log);

            if (battle.turns >= 1 && !battle.functions.Querry(GAME_FUNCTION.NoAutoStart))
                FireTurnHint(battle.turns, seat);

            if (isLocalHuman && !seat.is_ai)
                ScheduleControlGrant(log);
        }

        /// <summary>
        /// Drop local selection / move-attack overlays when entering spectate so hover threat
        /// (TargetSel_Hover UX_NONE) is not buried under stale own-unit MOVE_SELECT zones.
        /// </summary>
        internal static void ClearSpectateUxOverlays(string reason, ManualLogSource log = null)
        {
            if (!GateUtil.LanArmed(out _))
                return;
            try
            {
                var ux = UX_Manager.self;
                var battle = GS_Battle.self;
                var hadSel = battle?.selected_units != null && battle.selected_units.Count > 0;
                var hadUx = battle != null && battle.ux_state != UX_State.NONE;
                ux?.ClearUnitSelection(true);
                ux?.ClearHoverUnits();
                ux?.CheckUnitsAndSetUXState();

                var hover = Object.FindFirstObjectByType<TargetSel_Hover>();
                hover?.ClearAndRefresh();
                var bas = Object.FindFirstObjectByType<TargetSel_Base>();
                bas?.Clear();
                bas?.MarkDirty();

                if (hadSel || hadUx)
                    log?.LogInfo("[Presentation] Cleared spectate UX overlays (" + reason + ")");
            }
            catch (System.Exception ex)
            {
                log?.LogWarning("[Presentation] ClearSpectateUxOverlays: " + ex.Message);
            }
        }

        internal static void RefreshLocalVision(ManualLogSource log = null)
        {
            if (!GateUtil.LanArmed(out var plugin))
                return;
            var battle = GS_Battle.self;
            var local = plugin.Authority.TryGetLocalHumanPlayer();
            if (battle?.fow_maps == null || local == null)
                return;
            try
            {
                var fow = battle.fow_maps.AcquireFOWMap(local.fraction);
                fow.RefreshDetect(update: false);
                fow.RefreshVision();
                BattleEventBus.self.TriggerFOWDirty();
                plugin.Authority.ApplyLocalViewBinding("presentation-fow");
            }
            catch (System.Exception ex)
            {
                log?.LogWarning("[Presentation] FOW refresh: " + ex.Message);
            }
        }

        internal static void FireTurnHint(int turn, Player player)
        {
            if (player == null)
                return;
            PresentationContext.VanillaTurnHint = true;
            try
            {
                BattleEventBus.self.TriggerTurnHint(turn, player);
            }
            catch { /* ignore */ }
            finally
            {
                PresentationContext.VanillaTurnHint = false;
            }
        }

        internal static void ScheduleControlGrant(ManualLogSource log)
        {
            if (_grantRunning)
                return;
            var gc = GameController.self;
            if (gc == null)
            {
                LanMpPlugin.Instance?.Authority?.ApplyLocalViewBinding("control-grant-immediate");
                return;
            }
            gc.StartCoroutine(CoGrantLocalControl(log));
        }

        private static IEnumerator CoGrantLocalControl(ManualLogSource log)
        {
            _grantRunning = true;
            PresentationContext.ControlGrantPending = true;
            try
            {
                LanMpPlugin.Instance?.Authority?.ApplyLocalViewBinding("control-grant-wait");
                var guard = 0f;
                while (guard < 20f)
                {
                    var sync = LanMpPlugin.Instance?.Sync;
                    if (sync != null && sync.IsApplyQueueIdle && !AnyUnitAnimating())
                        break;
                    guard += Time.unscaledDeltaTime;
                    yield return AnnWCoroutine.NextTick;
                }
                if (guard >= 20f)
                    log?.LogWarning("[Presentation] control grant timeout — forcing");
            }
            finally
            {
                PresentationContext.ControlGrantPending = false;
                _grantRunning = false;
                LanMpPlugin.Instance?.Authority?.ApplyLocalViewBinding("control-grant");
                try { UX_Manager.self?.CheckUnitsAndSetUXState(); }
                catch { /* ignore */ }
            }
        }

        private static bool AnyUnitAnimating()
        {
            var battle = GS_Battle.self;
            if (battle?.all_unit?.units_alive == null)
                return false;
            foreach (var u in battle.all_unit.units_alive)
            {
                if (u != null && u.in_animation)
                    return true;
            }
            return false;
        }

        internal static void NotifyResourceDelta(Player player, int oldMetal, int newMetal, int oldPower, int newPower)
        {
            if (player == null || !GateUtil.LanArmed(out var plugin))
                return;
            if (!plugin.Authority.IsLocalPlayersTurn(player.index))
                return;

            var dMetal = newMetal - oldMetal;
            var dPower = newPower - oldPower;
            if (dMetal == 0 && dPower == 0)
                return;

            try
            {
                var pos = ResolveFloatPos(player);
                if (dMetal != 0)
                {
                    var txt = (dMetal > 0 ? "+" : "") + dMetal + " M";
                    FUIM_FloatNumber.CreateFloatText(pos)?.ShowAsText(txt, player.ui_color);
                }
                if (dPower != 0)
                {
                    var txt = (dPower > 0 ? "+" : "") + dPower + " P";
                    FUIM_FloatNumber.CreateFloatText(pos)?.ShowAsText(txt, player.ui_color);
                }
            }
            catch { /* ignore */ }
        }

        /// <summary>Force eco bar fill after attachment write (storage/income may have been missing).</summary>
        internal static void RefreshEcoBar(Player player)
        {
            if (player == null)
                return;
            try
            {
                var eco = UnityEngine.Object.FindFirstObjectByType<UI_EcoInfo>();
                eco?.RenderCO(player);
            }
            catch { /* ignore */ }
        }

        private static Inctor2 ResolveFloatPos(Player player)
        {
            if (player?.units != null)
            {
                foreach (var u in player.units)
                {
                    if (u != null && !u.dead)
                        return u.pos;
                }
            }
            return Inctor2.Zero;
        }
    }
}
