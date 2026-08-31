using ANNW;
using AnnW.LanMp.Ui;
using HarmonyLib;

namespace AnnW.LanMp.Patches
{
    [HarmonyPatch(typeof(UI_MENU_MainMenu), nameof(UI_MENU_MainMenu.Show))]
    internal static class Patch_MainMenu_Show
    {
        private static void Postfix(UI_MENU_MainMenu __instance)
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null || !plugin.Enabled.Value)
                return;
            MainMenuLanEntry.EnsureInjected(__instance);
        }
    }

    [HarmonyPatch(typeof(UI_MENU_MainMenu), nameof(UI_MENU_MainMenu.OnBtn_Skirmish))]
    internal static class Patch_MainMenu_OnBtn_Skirmish
    {
        private static void Postfix(UI_MENU_MainMenu __instance)
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null || !plugin.Enabled.Value)
                return;
            MainMenuLanEntry.EnsureInjected(__instance);
        }
    }
}
