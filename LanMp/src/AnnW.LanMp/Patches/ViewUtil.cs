namespace AnnW.LanMp.Patches
{
    using AnnW.LanMp.Protocol;
    using ANNW;
    using BepInEx.Logging;

    internal static class ViewUtil
    {
        internal static Fraction GetUxViewFraction(GS_Battle battle)
        {
            if (battle == null)
                return Fraction.NEUTRAL;
            if (!GateUtil.LanArmed(out var plugin))
                return battle.cur_player != null ? battle.cur_player.fraction : Fraction.NEUTRAL;
            var local = plugin.Authority.TryGetLocalHumanPlayer();
            if (local != null)
                return local.fraction;
            return battle.cur_player != null ? battle.cur_player.fraction : Fraction.NEUTRAL;
        }

        /// <summary>INV-VIEW local human (or cur_player outside LAN). Used for stay-attack hover ownership.</summary>
        internal static Player GetUxViewPlayer(GS_Battle battle)
        {
            if (battle == null)
                return null;
            if (!GateUtil.LanArmed(out var plugin))
                return battle.cur_player;
            return plugin.Authority.TryGetLocalHumanPlayer() ?? battle.cur_player;
        }

        /// <summary>
        /// FOW fraction for UnitData.GetMoveZone. Own/ally → local viewer; enemy → unit owner.
        /// </summary>
        internal static Fraction GetMoveZoneFowFraction(UnitData unit, GS_Battle battle)
        {
            if (unit == null)
                return Fraction.NEUTRAL;
            // Intent Accept must not INV-VIEW-rewrite ally FOW (same-faction Guest vs Host).
            if (AnnW.LanMp.Sync.SyncContext.PreferUnitOwnerFowForMoveZone)
                return unit.fraction;
            if (!GateUtil.LanArmed(out var plugin) || battle == null)
                return unit.fraction;

            var local = plugin.Authority.TryGetLocalHumanPlayer();
            var localFrac = local != null ? (int)local.fraction : -1;
            if (!PresentationRules.UseLocalViewerFowForMoveZone(
                    plugin.Authority.InLanBattle,
                    plugin.Authority.GatesArmed,
                    local != null,
                    (int)unit.fraction,
                    localFrac))
                return unit.fraction;
            return local.fraction;
        }

        internal static bool ShouldSuppressHoverThreatOverlay()
        {
            var battle = GS_Battle.self;
            if (battle == null)
                return true;
            var lan = GateUtil.LanArmed(out var plugin);
            return PresentationRules.ShouldSuppressHoverThreatOverlay(
                lan && plugin.Authority.InLanBattle,
                lan && plugin.Authority.GatesArmed,
                battle.script_processing,
                battle.is_auto_guiding,
                battle.is_ai_processing);
        }

        internal static bool ShouldRenderHoverThreatOverlay()
        {
            var battle = GS_Battle.self;
            if (battle == null)
                return false;
            var lan = GateUtil.LanArmed(out var plugin);
            return PresentationRules.ShouldRenderHoverThreatOverlay(
                lan && plugin.Authority.InLanBattle,
                lan && plugin.Authority.GatesArmed,
                battle.script_processing,
                battle.is_auto_guiding,
                battle.control_state == ControlState.Human);
        }

        internal static bool IsUnitVisibleToLocalViewer(UnitData unit)
        {
            if (unit == null)
                return false;
            var battle = GS_Battle.self;
            if (battle == null)
                return true;
            if (!GateUtil.LanArmed(out _))
                return true;
            if (battle.functions.Querry(GAME_FUNCTION.NoFOW))
                return true;
            try
            {
                var api = GameAPI.self;
                if (api == null)
                    return true;
                var fow = api.GetFOWMap(GetUxViewFraction(battle));
                return fow != null && fow.CanSeeUnit(unit.pos);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// FOW fraction for ActionData.CanDoAction / GetEffectZone.
        /// INV-SOLO: outside LAN = vanilla <c>action.player.fraction</c>.
        /// LAN unit-bound: INV-VIEW via <see cref="GetMoveZoneFowFraction"/>.
        /// LAN unbound (CO skills):
        /// - Host Accept / PreferUnitOwner → caster <c>action.player</c>
        /// - Local UX (Guest/Host own turn select) → INV-VIEW local viewer
        ///   (片区友军强化等可点空地；须与 RefreshLocalVision 对齐 SEEN).
        /// </summary>
        internal static Fraction GetActionUxFowFraction(ActionData action, GS_Battle battle)
        {
            if (!GateUtil.LanArmed(out _))
            {
                return action?.player != null
                    ? action.player.fraction
                    : Fraction.NEUTRAL;
            }

            if (action?.owner != null)
                return GetMoveZoneFowFraction(action.owner, battle);

            var accept = AnnW.LanMp.Sync.SyncContext.PreferUnitOwnerFowForMoveZone ||
                         AnnW.LanMp.Sync.SyncContext.SkillCastSuppressEmit;
            var caster = action?.player != null ? (int)action.player.fraction : (int)Fraction.NEUTRAL;
            var local = (int)GetUxViewFraction(battle);
            return (Fraction)SoloIsolationRules.UnboundActionFowFraction(caster, local, accept);
        }

        /// <summary>Clear attack-dot / build-planner caches for local-faction units (Guest miss StartTurn).</summary>
        internal static void InvalidateLocalCombatUxCaches(ManualLogSource log = null)
        {
            if (!GateUtil.LanArmed(out var plugin))
                return;
            var local = plugin.Authority.TryGetLocalHumanPlayer();
            var battle = GS_Battle.self;
            if (local == null || battle?.all_unit?.units_alive == null)
                return;
            try
            {
                local.ClearTempMove();
            }
            catch { /* ignore */ }

            foreach (var u in battle.all_unit.units_alive)
            {
                if (u?.player == null || u.player.fraction != local.fraction)
                    continue;
                try { UnitDataAccess.ClearCanAttackAnyone(u); }
                catch { /* ignore */ }
                try { u.eq?.ClearMoveOpCache(); }
                catch { /* ignore */ }
                try { u.build_planner?.Invalidate(); }
                catch { /* ignore */ }
                AnnW.LanMp.Sync.ResultAttachmentBridge.RefreshUnitUx(u);
            }
            log?.LogInfo("[View] InvalidateLocalCombatUxCaches fraction=" + local.fraction);
        }

        internal static bool ShouldFollowUnitCamera(UnitData unit)
        {
            if (unit == null)
                return false;
            if (!GateUtil.LanArmed(out var plugin))
                return true;
            var local = plugin.Authority.GetLocalHumanSlotIndex();
            var owner = unit.player != null ? unit.player.index : unit.player_index;
            return PresentationRules.ShouldFollowUnitCamera(
                plugin.Authority.InLanBattle,
                plugin.Authority.GatesArmed,
                GateUtil.IsBattlePlayPhase(),
                owner,
                local ?? -1,
                local.HasValue,
                IsUnitVisibleToLocalViewer(unit));
        }
    }
}
