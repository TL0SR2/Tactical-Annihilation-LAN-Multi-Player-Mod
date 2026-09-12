using System;
using System.Collections;
using AnnW.LanMp.Sync;
using BepInEx.Logging;
using HarmonyLib;

namespace AnnW.LanMp.Patches
{
    /// <summary>
    /// INV-T10 structural chokepoint: AnnW <see cref="CoroutineObject"/> busy-spins on
    /// <c>yield return null</c> and NREs when a context's enumerator dies mid-frame.
    /// Vanilla <c>DoActionAni</c> / <c>proc_SkillDoAction</c> nest those yields and also
    /// <c>StartCoroutine(proc_DoActionCell)</c> siblings — under LAN skill/apply pipelines
    /// every started enumerator must be SafePump-wrapped so only float NextTick reaches
    /// CoroutineObject.
    /// </summary>
    internal static class CoroutineObjectSafetyPatches
    {
        [HarmonyPatch(typeof(CoroutineObject), nameof(CoroutineObject.StartCoroutine),
            typeof(IEnumerator), typeof(string))]
        private static class Patch_StartCoroutine_SafeWrap
        {
            private static void Prefix(ref IEnumerator enumerator)
            {
                if (enumerator == null)
                    return;
                if (!GateUtil.LanArmed(out _))
                    return;
                if (!SyncContext.ShouldSafeWrapCoroutineObject())
                    return;

                ManualLogSource log = null;
                try { log = LanMpPlugin.Log; }
                catch { /* ignore */ }

                enumerator = AnnWCoroutine.SafePump(
                    enumerator,
                    AnnWCoroutine.DefaultApplyTimeoutSec,
                    log,
                    "CoObjWrap");
            }
        }
    }
}
