using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AnnW.LanMp.EarlyPatcher
{
    /// <summary>
    /// BepInEx preloader patcher — runs before any Unity scene Awake.
    /// Marker file next to AnnW.exe: LanMp.ForceNoSteam
    /// Rewrites SteamInterface.Init to no-op (prevents RestartAppIfNecessary → Quit)
    /// and forces SS_ANNW_PreMenu.Awake onto the non-Steam menu path.
    /// </summary>
    public static class Patcher
    {
        public static IEnumerable<string> TargetDLLs
        {
            get { yield return "Assembly-CSharp.dll"; }
        }

        public static void Initialize()
        {
            var marker = Path.Combine(Paths.GameRootPath, "LanMp.ForceNoSteam");
            Console.WriteLine("[LanMp.EarlyPatcher] marker=" + File.Exists(marker) + " root=" + Paths.GameRootPath);
        }

        public static void Patch(AssemblyDefinition assembly)
        {
            var marker = Path.Combine(Paths.GameRootPath, "LanMp.ForceNoSteam");
            if (!File.Exists(marker))
            {
                Console.WriteLine("[LanMp.EarlyPatcher] skip (no LanMp.ForceNoSteam marker)");
                return;
            }

            Console.WriteLine("[LanMp.EarlyPatcher] applying dual-instance Steam bypass IL patches");
            PatchSteamInit(assembly);
            PatchPreMenuAwake(assembly);
        }

        private static void PatchSteamInit(AssemblyDefinition assembly)
        {
            var type = assembly.MainModule.Types.FirstOrDefault(t => t.Name == "SteamInterface");
            var method = type?.Methods.FirstOrDefault(m => m.Name == "Init" && !m.IsStatic);
            if (method == null || method.Body == null)
            {
                Console.WriteLine("[LanMp.EarlyPatcher] SteamInterface.Init not found");
                return;
            }

            var steamInited = type.Fields.FirstOrDefault(f => f.Name == "steam_inited");
            if (steamInited == null)
            {
                Console.WriteLine("[LanMp.EarlyPatcher] steam_inited field missing");
                return;
            }

            method.Body.Instructions.Clear();
            method.Body.ExceptionHandlers.Clear();
            method.Body.Variables.Clear();
            var il = method.Body.GetILProcessor();
            // base.Init() may be required for Singleton — call then clear steam
            var baseInit = type.BaseType?.Resolve()?.Methods.FirstOrDefault(m => m.Name == "Init" && !m.IsStatic);
            // Keep it minimal: steam_inited = false; return;
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stfld, steamInited));
            il.Append(il.Create(OpCodes.Ret));
            Console.WriteLine("[LanMp.EarlyPatcher] SteamInterface.Init → no-op");
        }

        private static void PatchPreMenuAwake(AssemblyDefinition assembly)
        {
            var type = assembly.MainModule.Types.FirstOrDefault(t => t.Name == "SS_ANNW_PreMenu");
            var method = type?.Methods.FirstOrDefault(m => m.Name == "Awake" && !m.IsStatic);
            var useSteam = type?.Fields.FirstOrDefault(f => f.Name == "use_steam");
            if (method == null || method.Body == null || useSteam == null)
            {
                Console.WriteLine("[LanMp.EarlyPatcher] PreMenu.Awake/use_steam not found");
                return;
            }

            var il = method.Body.GetILProcessor();
            var first = method.Body.Instructions[0];
            // Insert at start: this.use_steam = false;
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
            il.InsertBefore(first, il.Create(OpCodes.Stfld, useSteam));
            Console.WriteLine("[LanMp.EarlyPatcher] PreMenu.Awake prefixes use_steam=false");
        }
    }
}
