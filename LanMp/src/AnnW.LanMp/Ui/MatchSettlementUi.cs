using AnnW.LanMp.Protocol;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Legacy MatchEnd cache hook. Settlement presentation is vanilla <c>proc_EndGame</c>
    /// (<c>SyncContext.AllowVanillaEndGameUi</c>) after <c>MatchSettlementBridge</c> stamps Host
    /// statics/turn_snaps. Do not draw IMGUI — it triggers BepInEx console. Do not reopen LAN room.
    /// </summary>
    internal static class MatchSettlementUi
    {
        public static bool IsVisible => false;

        public static void Show(
            MatchEndPayload end,
            bool localVictory,
            int? localSeatIndex = null,
            string localPeerId = null)
        {
            // No-op — vanilla EndGame owns settlement UI.
        }

        public static void Hide()
        {
        }

        public static void Draw()
        {
        }
    }
}
