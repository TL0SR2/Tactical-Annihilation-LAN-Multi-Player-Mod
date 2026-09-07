using System;

namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Mirrors private <c>CO_Data.GetEnergyMax</c> / internal <c>IsEnergyMax</c> (game DLL).
    /// Guest attach cannot call those members — keep formula here for ADR-005 apply + Host gate.
    /// </summary>
    public static class CoEnergyRules
    {
        /// <summary>Vanilla: Min(used*(used*5+40)+80, 500).</summary>
        public static float ComputeEnergyMax(int skillUsedTimes)
        {
            var used = skillUsedTimes < 0 ? 0 : skillUsedTimes;
            var raw = used * (used * 5 + 40) + 80;
            return Math.Min(raw, 500);
        }

        public static bool IsEnergyFull(float energy, float energyMax) =>
            energyMax > 0.01f && energy + 0.001f >= energyMax;
    }
}
