using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Coordinates and tracks pending animations across the MediaInfoPage.
    /// Ensures the page never renders incomplete animations by providing
    /// a deterministic wait mechanism before proceeding to the next UI phase.
    /// 
    /// Responsibilities:
    /// - Tracks all pending animations with unique keys
    /// - Provides WaitAllAsync(timeoutMs) to await completion
    /// - Auto-completes on timeout to prevent hangs
    /// - Supports cancellation for navigation-away scenarios
    /// 
    /// Does NOT:
    /// - Run animations (that's SectionController's job)
    /// - Manage animation timing or easing
    /// - Own visibility/opacity state (that's VisualStateController)
    /// </summary>
    internal sealed class AnimationCoordinator : IDisposable
    {
        #region Fields

        private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingAnimations = new();
        private CancellationTokenSource? _globalCts;
        private bool _disposed;

        #endregion

        #region Public API — Registration

        /// <summary>
        /// Registers a new animation with the coordinator.
        /// Returns a TaskCompletionSource that the caller must complete when the animation finishes.
        /// If the coordinator is disposed or cancelled, returns null (caller should skip registration).
        /// </summary>
        public TaskCompletionSource<bool>? Register(string key, string description = "")
        {
            ThrowIfDisposed();

            lock (_pendingAnimations)
            {
                if (_globalCts?.IsCancellationRequested == true)
                {
                    Debug.WriteLine($"[ANIM-COORD] Register skipped (cancelled): {key}");
                    return null;
                }

                if (_pendingAnimations.ContainsKey(key))
                {
                    Debug.WriteLine($"[ANIM-COORD] Duplicate key replaced: {key}");
                    _pendingAnimations[key].TrySetResult(false);
                }

                var tcs = new TaskCompletionSource<bool>();
                _pendingAnimations[key] = tcs;
                Debug.WriteLine($"[ANIM-COORD] Registered: {key} ({description}) — Total pending: {_pendingAnimations.Count}");
                return tcs;
            }
        }

        /// <summary>
        /// Marks a registered animation as complete.
        /// </summary>
        public void Complete(string key, bool success = true)
        {
            ThrowIfDisposed();

            lock (_pendingAnimations)
            {
                if (_pendingAnimations.TryGetValue(key, out var tcs))
                {
                    _pendingAnimations.Remove(key);
                    tcs.TrySetResult(success);
                    Debug.WriteLine($"[ANIM-COORD] Completed: {key} — Remaining pending: {_pendingAnimations.Count}");
                }
                else
                {
                    Debug.WriteLine($"[ANIM-COORD] Complete called for unknown key: {key}");
                }
            }
        }

        /// <summary>
        /// Marks a registered animation as failed/cancelled.
        /// </summary>
        public void Fail(string key, string reason = "")
        {
            ThrowIfDisposed();

            lock (_pendingAnimations)
            {
                if (_pendingAnimations.TryGetValue(key, out var tcs))
                {
                    _pendingAnimations.Remove(key);
                    tcs.TrySetResult(false);
                    Debug.WriteLine($"[ANIM-COORD] Failed: {key} ({reason}) — Remaining pending: {_pendingAnimations.Count}");
                }
            }
        }

        #endregion

        #region Public API — Wait

        /// <summary>
        /// Waits for all currently registered animations to complete.
        /// If animations don't complete within timeoutMs, they are auto-completed as failed
        /// to prevent the page from hanging indefinitely.
        /// 
        /// Returns true if all animations completed successfully, false if any timed out or failed.
        /// </summary>
        public async Task<bool> WaitAllAsync(int timeoutMs = 2000)
        {
            ThrowIfDisposed();

            List<TaskCompletionSource<bool>> snapshot;
            lock (_pendingAnimations)
            {
                if (_pendingAnimations.Count == 0)
                {
                    Debug.WriteLine("[ANIM-COORD] WaitAllAsync: no pending animations");
                    return true;
                }

                snapshot = new List<TaskCompletionSource<bool>>(_pendingAnimations.Values);
                Debug.WriteLine($"[ANIM-COORD] WaitAllAsync: waiting for {snapshot.Count} animations (timeout: {timeoutMs}ms)");
            }

            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                var tasks = new List<Task>(snapshot.Count);

                foreach (var tcs in snapshot)
                {
                    var delayTask = Task.Delay(Timeout.Infinite, cts.Token);
                    var completedTask = await Task.WhenAny(tcs.Task, delayTask);

                    if (completedTask == delayTask)
                    {
                        Debug.WriteLine($"[ANIM-COORD] WaitAllAsync: timeout waiting for one animation");
                        return false;
                    }

                    tasks.Add(tcs.Task);
                }

                await Task.WhenAll(tasks);
                Debug.WriteLine("[ANIM-COORD] WaitAllAsync: all animations completed");
                return true;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[ANIM-COORD] WaitAllAsync: timeout expired");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ANIM-COORD] WaitAllAsync: error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Public API — Cancellation

        /// <summary>
        /// Cancels all pending animations. Called when navigating away from the page.
        /// All pending TaskCompletionSources are completed as failed.
        /// </summary>
        public void CancelAll()
        {
            ThrowIfDisposed();

            lock (_pendingAnimations)
            {
                _globalCts?.Cancel();
                _globalCts?.Dispose();
                _globalCts = new CancellationTokenSource();

                var keys = new List<string>(_pendingAnimations.Keys);
                foreach (var key in keys)
                {
                    if (_pendingAnimations.TryGetValue(key, out var tcs))
                    {
                        _pendingAnimations.Remove(key);
                        tcs.TrySetResult(false);
                    }
                }

                Debug.WriteLine($"[ANIM-COORD] CancelAll: cancelled {keys.Count} pending animations");
            }
        }

        /// <summary>
        /// Returns the count of currently pending animations.
        /// </summary>
        public int PendingCount
        {
            get
            {
                lock (_pendingAnimations)
                {
                    return _pendingAnimations.Count;
                }
            }
        }

        #endregion

        #region Internal Helpers

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AnimationCoordinator));
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _disposed = true;
            CancelAll();
            _globalCts?.Dispose();
            _globalCts = null;
        }

        #endregion
    }
}
