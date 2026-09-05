namespace AnnW.LanMp.Patches
{
    using AnnW.LanMp.Protocol;
    using ANNW;

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
