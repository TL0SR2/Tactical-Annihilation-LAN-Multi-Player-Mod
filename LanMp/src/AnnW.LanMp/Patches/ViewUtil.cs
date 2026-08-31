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
