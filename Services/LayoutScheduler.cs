using System;
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Debounces and coalesces layout computation requests.
    /// Prevents redundant LayoutEngine.Compute() calls within a single frame.
    /// 
    /// Responsibilities:
    /// - Coalesces multiple layout requests into one per frame (~16ms)
    /// - Tracks request generation to reject stale decisions
    /// - Provides the single entry point for triggering layout
    /// 
    /// Does NOT:
    /// - Compute layouts (that's LayoutEngine)
    /// - Apply layouts (that's LayoutApplier)
    /// - Manage visibility state (that's VisualStateController)
    /// </summary>
    internal sealed class LayoutScheduler : IDisposable
    {
        #region Fields

        private readonly DispatcherQueue _dispatcher;
        private readonly Action<LayoutRequestReason> _onExecuteLayout;
        private readonly object _lock = new();
        private DispatcherQueueHandler _pendingHandler;
        private bool _isPending;
        private long _currentGeneration;
        private long _lastExecutedGeneration;
        private bool _disposed;

        #endregion

        #region Constructor

        public LayoutScheduler(DispatcherQueue dispatcher, Action<LayoutRequestReason> onExecuteLayout)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _onExecuteLayout = onExecuteLayout ?? throw new ArgumentNullException(nameof(onExecuteLayout));
        }

        #endregion

        #region Public API

        /// <summary>
        /// Requests a layout computation. If a request is already pending,
        /// this call is coalesced into the pending request.
        /// </summary>
        public void RequestLayout(LayoutRequestReason reason)
        {
            ThrowIfDisposed();

            lock (_lock)
            {
                _currentGeneration++;

                if (_isPending)
                {
                    Debug.WriteLine($"[LAYOUT-SCHED] Coalesced request: {reason} (gen {_currentGeneration})");
                    return;
                }

                _isPending = true;
                Debug.WriteLine($"[LAYOUT-SCHED] Scheduled layout: {reason} (gen {_currentGeneration})");

                _pendingHandler = () =>
                {
                    lock (_lock)
                    {
                        _isPending = false;
                        _lastExecutedGeneration = _currentGeneration;
                    }

                    _onExecuteLayout(reason);
                    Debug.WriteLine($"[LAYOUT-SCHED] Layout executed (gen {_lastExecutedGeneration})");
                };

                _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, _pendingHandler);
            }
        }

        /// <summary>
        /// Returns the current generation number. Use this to check if a layout
        /// decision is still valid before applying it.
        /// </summary>
        public long CurrentGeneration
        {
            get
            {
                lock (_lock) return _currentGeneration;
            }
        }

        /// <summary>
        /// Returns the generation of the last executed layout.
        /// </summary>
        public long LastExecutedGeneration
        {
            get
            {
                lock (_lock) return _lastExecutedGeneration;
            }
        }

        /// <summary>
        /// Returns true if the given generation is still current (not superseded).
        /// </summary>
        public bool IsGenerationValid(long generation)
        {
            lock (_lock) return generation >= _lastExecutedGeneration;
        }

        /// <summary>
        /// Forces an immediate layout computation, bypassing the debounce.
        /// </summary>
        public void ForceLayout(LayoutRequestReason reason)
        {
            ThrowIfDisposed();

            lock (_lock)
            {
                _currentGeneration++;
                _lastExecutedGeneration = _currentGeneration;
                _isPending = false;
            }

            _onExecuteLayout(reason);
            Debug.WriteLine($"[LAYOUT-SCHED] Force layout (gen {_lastExecutedGeneration})");
        }

        #endregion

        #region Internal Helpers

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LayoutScheduler));
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _disposed = true;
        }

        #endregion
    }

    /// <summary>
    /// Describes why a layout computation was requested.
    /// </summary>
    internal enum LayoutRequestReason
    {
        ViewportChanged,
        PanelChanged,
        DataCommitted,
        IdentityChanged,
        SizeChanged,
        Force
    }
}
