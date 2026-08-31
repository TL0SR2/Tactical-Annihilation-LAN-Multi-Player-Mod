namespace AnnW.LanMp.Sync
{
    /// <summary>
    /// AnnW's <see cref="CoroutineObject"/> is NOT Unity's scheduler:
    /// <c>yield return null</c> continues in the same <c>ExecuteContext</c> (busy-spin);
    /// only <c>float</c>/<c>int</c> wait times return to <c>Update</c>.
    /// Waiting on TurnLoop / EndTurnReady with null deadlocks the main thread (white screen).
    /// </summary>
    internal static class AnnWCoroutine
    {
        /// <summary>Yield once so CoroutineObject.Update can pump other contexts (e.g. TurnLoop).</summary>
        public static readonly object NextTick = 0f;
    }
}
