using System;
using System.Reflection;
using HarmonyLib;

namespace AnnW.LanMp.Patches
{
    /// <summary>Reflection for UnitData private UX/rank fields (game assembly).</summary>
    internal static class UnitDataAccess
    {
        private static readonly FieldInfo CanAttackAnyoneFi =
            AccessTools.Field(typeof(UnitData), "can_attack_anyone");

        private static readonly FieldInfo EffectRankFi =
            AccessTools.Field(typeof(UnitData), "effect_rank");

        private static readonly MethodInfo InitRankLogicMi =
            AccessTools.Method(typeof(UnitData), "InitRankLogic");

        internal static void ClearCanAttackAnyone(UnitData unit)
        {
            if (unit == null || CanAttackAnyoneFi == null)
                return;
            try { CanAttackAnyoneFi.SetValue(unit, null); }
            catch { /* ignore */ }
        }

        internal static bool IsRankEffectMissing(UnitData unit)
        {
            if (unit == null || EffectRankFi == null)
                return true;
            try
            {
                var fx = EffectRankFi.GetValue(unit) as UnitEffect;
                return fx == null || fx.host == null;
            }
            catch
            {
                return true;
            }
        }

        internal static void EnsureRankLogic(UnitData unit)
        {
            if (unit == null)
                return;
            try
            {
                if (unit.GetRankLogic() != null)
                    return;
                InitRankLogicMi?.Invoke(unit, null);
            }
            catch
            {
                /* ignore */
            }
        }
    }
}
