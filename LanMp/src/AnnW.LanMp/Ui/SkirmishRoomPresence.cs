namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Legacy tracker for native skirmish panel. No longer mutates confirm labels —
    /// default skirmish is pure single-player.
    /// </summary>
    internal static class SkirmishRoomPresence
    {
        public static bool IsOpen { get; set; }
        public static object ConfirmButton { get; set; }
        public static string VanillaConfirmLabel { get; set; }

        public static void Enter(object confirmButton)
        {
            IsOpen = true;
            ConfirmButton = confirmButton;
        }

        public static void Leave()
        {
            IsOpen = false;
            ConfirmButton = null;
        }

        public static void RefreshConfirmLabel()
        {
            // Intentionally no-op: do not stamp LAN text onto vanilla Confirm.
        }
    }
}
