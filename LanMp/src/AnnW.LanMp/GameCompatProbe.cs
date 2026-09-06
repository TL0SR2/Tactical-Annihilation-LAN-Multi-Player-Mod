using System;
using System.Reflection;
using System.Text;
using AnnW.LanMp.Protocol;
using BepInEx.Logging;

namespace AnnW.LanMp
{
    /// <summary>
    /// Startup probe: detect game-update drift vs LanMp hardcodes (eco tables, PlayerControl, SGS fields).
    /// Logs warnings only — never throws.
    /// </summary>
    internal static class GameCompatProbe
    {
        public static void Run(ManualLogSource log)
        {
            if (log == null)
                return;
            try
            {
                ProbePlayerControl(log);
                ProbeSgsFields(log);
                ProbeSetupMethod(log);
                ProbeDataUtilsTables(log);
            }
            catch (Exception ex)
            {
                log.LogWarning("[Compat] probe failed: " + ex.Message);
            }
        }

        private static void ProbePlayerControl(ManualLogSource log)
        {
            var custom = (int)PlayerControl.Custom;
            var num = (int)PlayerControl.num;
            if (custom != SkirmishSeatEconomy.ControllerCustom)
            {
                log.LogError(
                    $"[Compat] PlayerControl.Custom={custom} != plugin {SkirmishSeatEconomy.ControllerCustom} — eco stamp broken");
            }
            if (num != SkirmishSeatEconomy.ControllerCustom + 1)
            {
                log.LogWarning(
                    $"[Compat] PlayerControl.num={num} (expected {SkirmishSeatEconomy.ControllerCustom + 1}) — AI diff dropdown may drift");
            }
            else
            {
                log.LogInfo($"[Compat] PlayerControl Custom={custom} num={num} OK");
            }
        }

        private static void ProbeSgsFields(ManualLogSource log)
        {
            var t = typeof(SGS_Player);
            var res = t.GetField("res_percent");
            var intelTypo = t.GetField("ai_interlligence");
            var intelFix = t.GetField("ai_intelligence");
            if (res == null)
                log.LogError("[Compat] SGS_Player.res_percent MISSING — Bootstrap eco stamp broken");
            if (intelTypo == null && intelFix == null)
                log.LogError("[Compat] SGS_Player ai intel field MISSING");
            else if (intelTypo == null && intelFix != null)
                log.LogError("[Compat] SGS_Player renamed ai_interlligence → ai_intelligence — Bootstrap must update");
            else
                log.LogInfo("[Compat] SGS_Player res_percent + ai_interlligence OK");

            var p = typeof(Player);
            if (p.GetField("res_mul") == null)
                log.LogError("[Compat] Player.res_mul MISSING");
            if (p.GetField("ai_intelligence") == null)
                log.LogError("[Compat] Player.ai_intelligence MISSING");

            // Map catalog: pack folder removed — path must be Skirmish/{sd.name}
            if (typeof(SD_ANNW_SK_MAP).GetField("pack") != null)
                log.LogInfo("[Compat] SD_ANNW_SK_MAP.pack still present (unexpected on new builds)");
            else
                log.LogInfo("[Compat] SD_ANNW_SK_MAP.pack absent — using Skirmish/{name} paths");
        }

        private static void ProbeSetupMethod(ManualLogSource log)
        {
            var m = typeof(GS_Battle).GetMethod(
                "SetupForSkirmish",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
                log.LogError("[Compat] GS_Battle.SetupForSkirmish MISSING — eco Postfix dead");
            else
                log.LogInfo($"[Compat] SetupForSkirmish found ({(m.IsPublic ? "public" : "non-public")})");
        }

        private static void ProbeDataUtilsTables(ManualLogSource log)
        {
            var res = TryReadFloatArray("SkirmishResMulOptions");
            var intel = TryReadFloatArray("SkirmishAIIntelOptions");
            CompareTable(log, "SkirmishResMulOptions", res, SkirmishSeatEconomy.ResMulOptions);
            CompareTable(log, "SkirmishAIIntelOptions", intel, SkirmishSeatEconomy.AiIntelOptions);

            // Spot-check GetAIDiff* vs plugin presets
            for (var c = 1; c <= 5; c++)
            {
                var gameRes = CallGetAIDiffFloat("GetAIDiffResMul", c);
                var plugRes = SkirmishSeatEconomy.GetPresetResMul(c);
                if (gameRes.HasValue && Math.Abs(gameRes.Value - plugRes) > 0.001f)
                {
                    log.LogWarning(
                        $"[Compat] GetAIDiffResMul({c})={gameRes:0.###} != plugin {plugRes:0.###}");
                }
                var gameIntel = CallGetAIDiffFloat("GetAIDiffIntelligence", c);
                var plugIntel = SkirmishSeatEconomy.GetPresetAiIntelligence(c);
                if (gameIntel.HasValue && Math.Abs(gameIntel.Value - plugIntel) > 0.001f)
                {
                    log.LogWarning(
                        $"[Compat] GetAIDiffIntelligence({c})={gameIntel:0.###} != plugin {plugIntel:0.###}");
                }
            }
        }

        private static void CompareTable(ManualLogSource log, string name, float[] game, float[] plugin)
        {
            if (game == null || game.Length == 0)
            {
                log.LogWarning($"[Compat] DataUtils.{name} unreadable — UI uses plugin hardcode");
                return;
            }
            if (game.Length != plugin.Length)
            {
                log.LogWarning(
                    $"[Compat] DataUtils.{name} len={game.Length} != plugin {plugin.Length} — prefer live table in UI");
                return;
            }
            var sb = new StringBuilder();
            for (var i = 0; i < game.Length; i++)
            {
                if (Math.Abs(game[i] - plugin[i]) > 0.001f)
                    sb.Append($" [{i}]{plugin[i]}→{game[i]}");
            }
            if (sb.Length > 0)
                log.LogWarning($"[Compat] DataUtils.{name} drift:{sb}");
            else
                log.LogInfo($"[Compat] DataUtils.{name} matches plugin ({game.Length} entries)");
        }

        internal static float[] TryReadFloatArray(string fieldName)
        {
            try
            {
                var f = typeof(DataUtils).GetField(
                    fieldName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return f?.GetValue(null) as float[];
            }
            catch
            {
                return null;
            }
        }

        private static float? CallGetAIDiffFloat(string method, int controller)
        {
            try
            {
                var m = typeof(DataUtils).GetMethod(
                    method,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null)
                    return null;
                var r = m.Invoke(null, new object[] { (PlayerControl)controller });
                if (r is float f)
                    return f;
            }
            catch
            {
                /* ignore */
            }
            return null;
        }

        /// <summary>UI: prefer live game tables so option lists track game updates.</summary>
        internal static float[] ResMulOptionsLive()
        {
            var live = TryReadFloatArray("SkirmishResMulOptions");
            return live != null && live.Length > 0 ? live : SkirmishSeatEconomy.ResMulOptions;
        }

        internal static float[] AiIntelOptionsLive()
        {
            var live = TryReadFloatArray("SkirmishAIIntelOptions");
            return live != null && live.Length > 0 ? live : SkirmishSeatEconomy.AiIntelOptions;
        }

        internal static int IndexOfNearest(float[] options, float value)
        {
            if (options == null || options.Length == 0)
                return 0;
            var best = 0;
            var bestDist = Math.Abs(options[0] - value);
            for (var i = 1; i < options.Length; i++)
            {
                var d = Math.Abs(options[i] - value);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        internal static float ValueAt(float[] options, int index, float fallback)
        {
            if (options == null || index < 0 || index >= options.Length)
                return fallback;
            return options[index];
        }
    }
}
