namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Pure helpers for ADR-005 CO loadout fields on <see cref="LobbySeatDto"/>.
    /// Game-table resolution lives in the plugin (<c>CoLoadoutResolver</c>).
    /// </summary>
    public static class CoLoadoutRules
    {
        public static bool HasSkillId(LobbySeatDto seat) =>
            seat != null && !string.IsNullOrEmpty(seat.skillId);

        public static bool HasAnyPs(LobbySeatDto seat) =>
            seat?.psIds != null && seat.psIds.Length > 0;

        /// <summary>Stamp Host-authored ids (null psIds → empty array).</summary>
        public static void Stamp(LobbySeatDto seat, string skillId, string[] psIds)
        {
            if (seat == null)
                return;
            seat.skillId = skillId ?? "";
            seat.psIds = psIds ?? new string[0];
        }

        /// <summary>Clear loadout when CO removed / none.</summary>
        public static void Clear(LobbySeatDto seat)
        {
            Stamp(seat, "", new string[0]);
        }

        /// <summary>True when seat has a CO but loadout was never stamped (legacy draft).</summary>
        public static bool NeedsDefaultStamp(LobbySeatDto seat)
        {
            if (seat == null || !seat.exist)
                return false;
            if (string.IsNullOrEmpty(seat.coId) || seat.coId == "__none__")
                return false;
            return string.IsNullOrEmpty(seat.skillId) &&
                   (seat.psIds == null || seat.psIds.Length == 0);
        }
    }
}
