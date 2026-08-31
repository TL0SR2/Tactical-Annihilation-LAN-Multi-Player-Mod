using System;
using System.Collections;
using System.Collections.Generic;
using AnnW.LanMp.Protocol;
using BepInEx.Logging;
using UnityEngine;

namespace AnnW.LanMp.Sync
{
    /// <summary>
    /// ADR-004 INV-T3: single-consumer serial apply of battle Commands.
    /// Prevents DoAction animation overlapping EndTurn and corrupting SyncContext flags.
    /// </summary>
    public sealed class CommandApplyQueue
    {
        private readonly Queue<CommandDto> _queue = new Queue<CommandDto>();
        private readonly ManualLogSource _log;
        private readonly Func<CommandDto, IEnumerator> _applyBody;
        private bool _running;

        public CommandApplyQueue(ManualLogSource log, Func<CommandDto, IEnumerator> applyBody)
        {
            _log = log;
            _applyBody = applyBody ?? throw new ArgumentNullException(nameof(applyBody));
        }

        public int Count
        {
            get { lock (_queue) return _queue.Count; }
        }

        public void Enqueue(CommandDto cmd)
        {
            if (cmd == null)
                return;
            lock (_queue)
            {
                _queue.Enqueue(cmd);
            }
            EnsurePump();
        }

        public void Clear()
        {
            lock (_queue)
            {
                _queue.Clear();
            }
        }

        private void EnsurePump()
        {
            if (_running)
                return;
            var gc = GameController.self;
            var ux = UX_Manager.self;
            if (gc != null)
            {
                _running = true;
                gc.StartCoroutine(CoPump());
                return;
            }
            if (ux?.coroutineObject != null)
            {
                _running = true;
                ux.coroutineObject.StartCoroutine(CoPump());
                return;
            }
            _log?.LogWarning("[ApplyQueue] No coroutine host — deferred");
        }

        private IEnumerator CoPump()
        {
            try
            {
                while (true)
                {
                    CommandDto cmd;
                    lock (_queue)
                    {
                        if (_queue.Count == 0)
                            break;
                        cmd = _queue.Dequeue();
                    }

                    SyncContext.SuppressNetworkEmit = true;
                    SyncContext.ApplyingRemoteCommand = true;
                    Exception error = null;
                    var body = _applyBody(cmd);
                    while (true)
                    {
                        object current = null;
                        bool moved;
                        try
                        {
                            moved = body.MoveNext();
                            if (moved)
                                current = body.Current;
                        }
                        catch (Exception ex)
                        {
                            error = ex;
                            break;
                        }
                        if (!moved)
                            break;
                        yield return current;
                    }

                    SyncContext.SuppressNetworkEmit = false;
                    SyncContext.ApplyingRemoteCommand = false;

                    if (error != null)
                        _log?.LogError("[ApplyQueue] apply failed kind=" + cmd.kind + ": " + error);
                    else
                        _log?.LogInfo("[ApplyQueue] done kind=" + cmd.kind);
                }
            }
            finally
            {
                SyncContext.SuppressNetworkEmit = false;
                SyncContext.ApplyingRemoteCommand = false;
                _running = false;
                // More items may have arrived while finishing.
                bool more;
                lock (_queue) more = _queue.Count > 0;
                if (more)
                    EnsurePump();
            }
        }
    }
}
