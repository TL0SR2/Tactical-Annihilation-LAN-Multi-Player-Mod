using System;
using AnnW.LanMp.Protocol;
using HarmonyLib;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// Vanilla Human seats always GetPlayerDisplayName → localized 「人类」.
    /// In LAN, substitute lobby usernames (draft.occupantName / peer display name).
    /// </summary>
    [HarmonyPatch(typeof(Player), "GetPlayerDisplayName")]
    internal static class Patch_GetPlayerDisplayName
    {
        private static bool Prefix(Player __instance, ref string __result)
        {
            try
            {
                var plugin = LanMpPlugin.Instance;
                if (plugin == null || plugin.Enabled == null || !plugin.Enabled.Value)
                    return true;
                if (__instance == null)
                    return true;

                var auth = plugin.Authority;
                var lobby = plugin.Lobby;
                if (auth == null || lobby == null)
                    return true;
                // Battle, start transition, or brief MatchEnd teardown before Menu.
                if (!auth.InLanBattle && !auth.MatchSettled && !lobby.StartAuthorized)
                    return true;

                if (__instance.fraction == Fraction.NEUTRAL)
                    return true;

                // Only replace human/remote seats — leave AI to vanilla.
                if (__instance.is_ai)
                    return true;

                var name = LanPlayerNames.TryResolveSeatDisplayName(__instance.index);
                if (string.IsNullOrEmpty(name))
                    return true;

                var n = __instance.index >= 0 ? __instance.index + 1 : 0;
                string circle;
                try
                {
                    circle = StringUtils.GetBlackCircleNumChar(n).ToString();
                }
                catch
                {
                    circle = n > 0 ? (n + ".") : "0.";
                }

                __result = circle + name;
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
