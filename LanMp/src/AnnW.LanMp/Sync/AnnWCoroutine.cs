using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace AnnW.LanMp.Sync
{
    /// <summary>
    /// AnnW's <see cref="CoroutineObject"/> is NOT Unity's scheduler (ADR-004 / INV-T10):
    /// <c>yield return null</c> continues in the same <c>ExecuteContext</c> (busy-spin);
    /// only <c>float</c>/<c>int</c> wait times return to <c>Update</c>.
    /// Waiting on TurnLoop / EndTurnReady / apply bodies with null deadlocks the main thread.
    /// </summary>
    internal static class AnnWCoroutine
    {
        /// <summary>Yield once so CoroutineObject.Update can pump other contexts (e.g. TurnLoop).</summary>
        public static readonly object NextTick = 0f;

        /// <summary>
        /// Default wall-clock budget for one Command apply / Accept body on CoroutineObject.
        /// </summary>
        public const float DefaultApplyTimeoutSec = 45f;

        /// <summary>
        /// Architecture chokepoint: flatten nested <see cref="IEnumerator"/> and map
        /// <c>null</c> → <see cref="NextTick"/> before anything reaches CoroutineObject.
        /// Vanilla animators (<c>DoMoveWithAni</c> / action procs) commonly yield null;
        /// nesting them under <c>GameController.StartCoroutine</c> busy-spins Apply forever
        /// (<c>ApplyingRemoteCommand</c> stuck → Guest false spectate).
        /// </summary>
        public static IEnumerator SafePump(
            IEnumerator inner,
            float timeoutSec = DefaultApplyTimeoutSec,
            ManualLogSource log = null,
            string tag = null)
        {
            if (inner == null)
                yield break;

            var stack = new Stack<IEnumerator>();
            stack.Push(inner);
            var guard = 0f;
            var label = string.IsNullOrEmpty(tag) ? "pump" : tag;

            while (stack.Count > 0 && guard < timeoutSec)
            {
                var top = stack.Peek();
                bool moved;
                object cur = null;
                SyncContext.InApplyEnumerator = true;
                try
                {
                    moved = top.MoveNext();
                    if (moved)
                        cur = top.Current;
                }
                catch (Exception ex)
                {
                    SyncContext.InApplyEnumerator = false;
                    log?.LogWarning("[Coroutine] SafePump " + label + ": " + ex.Message);
                    yield break;
                }
                SyncContext.InApplyEnumerator = false;

                if (!moved)
                {
                    stack.Pop();
                    continue;
                }

                // Nested enumerator: flatten here — never yield IEnumerator to CoroutineObject.
                if (cur is IEnumerator nested)
                {
                    stack.Push(nested);
                    continue;
                }

                guard += Time.unscaledDeltaTime;

                if (cur is float f)
                {
                    // Vanilla CoroutineObject: any float yield returns to Update — including 0.
                    if (f <= 0f)
                    {
                        yield return NextTick;
                        continue;
                    }

                    var waited = 0f;
                    while (waited < f && guard < timeoutSec)
                    {
                        waited += Time.unscaledDeltaTime;
                        guard += Time.unscaledDeltaTime;
                        yield return NextTick;
                    }
                    continue;
                }

                if (cur is int ii)
                {
                    // Vanilla move/attack animators yield boxed int 0 each lerp frame
                    // (UnitData.proc_MoveAnimation, DoAction_*). Treating 0 as "wait 0s"
                    // without yielding busy-completes the whole path in one ApplyQueue
                    // MoveNext → Guest teleport / missing attack VFX (INV-T10 regression).
                    if (ii <= 0)
                    {
                        yield return NextTick;
                        continue;
                    }

                    var waited = 0f;
                    var limit = (float)ii;
                    while (waited < limit && guard < timeoutSec)
                    {
                        waited += Time.unscaledDeltaTime;
                        guard += Time.unscaledDeltaTime;
                        yield return NextTick;
                    }
                    continue;
                }

                // null or unsupported YieldInstruction-like objects → never busy-spin.
                yield return NextTick;
            }

            if (stack.Count > 0)
            {
                log?.LogWarning(
                    $"[Coroutine] SafePump timeout tag={label} after {timeoutSec:0}s " +
                    $"(stack={stack.Count}) — releasing caller");
            }
        }
    }
}
