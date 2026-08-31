using System.Globalization;
using System.Threading;
using ANNW;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// Same-PC dual AnnW: 2nd process Steam RestartAppIfNecessary → Quit during PreMenu.Awake.
    /// Must patch BEFORE first scene Awake (RuntimeInitialize BeforeSceneLoad), not only in plugin Awake.
    /// </summary>
    public static class DualInstanceSteamBypass
    {
        /// <summary>Set early from path / config before LanMpPlugin.Instance exists.</summary>
        public static bool ForceBypass;

        public static bool WantBypass()
        {
            if (ForceBypass)
                return true;
            var p = LanMpPlugin.Instance;
            return p?.ForceNoSteam != null && p.ForceNoSteam.Value;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void EarlyPatch()
        {
            try
            {
                var path = Application.dataPath ?? "";
                if (path.IndexOf(".Guest", System.StringComparison.OrdinalIgnoreCase) < 0
                    && path.IndexOf("ForceNoSteam", System.StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                ForceBypass = true;
                var harmony = new Harmony("annw.lanmp.early-dual");
                ApplyManualPatches(harmony);
                Debug.Log("[LanMp Dual] Early BeforeSceneLoad patches applied path=" + path);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[LanMp Dual] EarlyPatch failed: " + ex);
            }
        }

        public static void ApplyManualPatches(Harmony harmony)
        {
            var awake = AccessTools.Method(typeof(SS_ANNW_PreMenu), "Awake");
            var steamInit = AccessTools.Method(typeof(SteamInterface), "Init");
            if (awake != null)
            {
                harmony.Patch(awake,
                    prefix: new HarmonyMethod(typeof(DualInstanceSteamBypass), nameof(PreMenuAwakePrefix)));
            }
            if (steamInit != null)
            {
                harmony.Patch(steamInit,
                    prefix: new HarmonyMethod(typeof(DualInstanceSteamBypass), nameof(SteamInitPrefix)));
            }
        }

        public static bool PreMenuAwakePrefix(SS_ANNW_PreMenu __instance)
        {
            Debug.Log("[LanMp Dual] PreMenuAwakePrefix enter bypass=" + WantBypass());
            if (!WantBypass())
                return true;

            try
            {
                var cultureInfo = new CultureInfo("zh-CN");
                Thread.CurrentThread.CurrentCulture = cultureInfo;
                Thread.CurrentThread.CurrentUICulture = cultureInfo;
                Singleton<SuperSD>.self.Touch();
                DataSelfCheck.DoDataSelfCheck();

                __instance.use_steam = false;
                var overall = Singleton<GS_Overall>.self;
                if (overall != null)
                {
                    overall.dbg_enabled = __instance.debug_enabled;
                    overall.is_demo = __instance.is_demo;
                    overall.steam_appid_setting = __instance.appid_setting;
                    overall.version_name = __instance.version_name;
                    overall.use_steam = false;
                }

                SceneManager.LoadScene("ANNW_Menu");
                Debug.Log("[LanMp Dual] Forced ANNW_Menu");
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[LanMp Dual] PreMenu bypass failed: " + ex);
                return true;
            }
            return false;
        }

        public static bool SteamInitPrefix(SteamInterface __instance)
        {
            if (!WantBypass())
                return true;
            Debug.Log("[LanMp Dual] SteamInterface.Init skipped");
            __instance.steam_inited = false;
            return false;
        }
    }
}
