using ANNW;
using HarmonyLib;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// Vanilla「新遭遇战」is single-player only. LAN room is a dedicated page — do not intercept or toast here.
    /// Patches kept as no-ops so old Harmony IDs do not leave half-applied state; logic intentionally empty.
    /// </summary>
    [HarmonyPatch(typeof(UI_MENU_LevelSelect_InfoSkm), nameof(UI_MENU_LevelSelect_InfoSkm.StartLevel))]
    internal static class Patch_SkirmishStartLevel
    {
        private static bool Prefix() => true; // always allow vanilla StartLevel
    }

    [HarmonyPatch(typeof(UI_MENU_POP_SkirmishSelect), nameof(UI_MENU_POP_SkirmishSelect.Show))]
    internal static class Patch_SkirmishSelect_Show
    {
        private static void Postfix() { /* no LAN tips / no confirm relabel */ }
    }

    [HarmonyPatch(typeof(UI_MENU_POP_SkirmishSelect), "Hide")]
    internal static class Patch_SkirmishSelect_Hide
    {
        private static void Prefix() { }
    }

    [HarmonyPatch(typeof(UI_MENU_POP_SkirmishSelect), "UpdateConfirmBtn")]
    internal static class Patch_SkirmishSelect_UpdateConfirmBtn
    {
        private static void Postfix() { }
    }
}
