using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Explicit state machine for the MediaInfoPage load lifecycle.
    /// Replaces the scattered _pageLoadState assignments with validated transitions.
    /// 
    /// State flow: Idle → Preparing → Fetching → Committing → Revealing → Ready
    /// Any state can transition to Error.
    /// LayoutReady is a side-channel notification, not a state transition.
    /// 
    /// Responsibilities:
    /// - Owns the page load state
    /// - Validates state transitions
    /// - Notifies observers of state changes
    /// - Tracks timing per state for performance monitoring
    /// 
    /// Does NOT:
    /// - Manage layout computations (that's LayoutEngine)
    /// - Manage panel state (that's PanelOwner)
    /// - Manage animations (that's AnimationCoordinator)
    /// </summary>
    internal sealed class LoadPipeline : IDisposable
    {
        #region State Enum

        public enum State
        {
            Idle,
            Preparing,
            Fetching,
            Committing,
            Revealing,
            Ready,
            Error
        }

        #endregion

        #region Fields

        private State _currentState = State.Idle;
        private bool _layoutReady;
        private readonly Dictionary<State, long> _stateEntryTimes = new();
        private readonly Dictionary<State, long> _stateDurations = new();
        private bool _disposed;

        #endregion

        #region Events

        public event Action<State, State> StateChanged;

        #endregion

        #region Public API

        public State CurrentState
        {
            get
            {
                lock (this) return _currentState;
            }
        }

        public bool IsLayoutReady
        {
            get
            {
                lock (this) return _layoutReady;
            }
        }

        /// <summary>
        /// Transitions to a new state. Validates the transition is legal.
        /// Returns false if the transition is invalid.
        /// </summary>
        public bool TransitionTo(State newState)
        {
            lock (this)
            {
                if (_disposed) return false;

                if (!IsValidTransition(_currentState, newState))
                {
                    Debug.WriteLine($"[LOAD-PIPELINE] Invalid transition: {_currentState} -> {newState}");
                    return false;
                }

                var oldState = _currentState;
                if (oldState == newState) return true;

                RecordDuration(oldState);
                _currentState = newState;
                _stateEntryTimes[newState] = Stopwatch.GetTimestamp();

                Debug.WriteLine($"[LOAD-PIPELINE] {oldState} -> {newState}");
                StateChanged?.Invoke(oldState, newState);
                return true;
            }
        }

        /// <summary>
        /// Notifies that the layout system is ready. This does NOT change the load state,
        /// but unblocks any pending load operations that were waiting for layout.
        /// </summary>
        public void NotifyLayoutReady()
        {
            lock (this)
            {
                if (_disposed) return;
                _layoutReady = true;
                Debug.WriteLine("[LOAD-PIPELINE] LayoutReady notified");
            }
        }

        /// <summary>
        /// Resets the pipeline to Idle state. Called when navigating to a new item.
        /// </summary>
        public void Reset()
        {
            lock (this)
            {
                if (_disposed) return;

                RecordDuration(_currentState);
                var oldState = _currentState;
                _currentState = State.Idle;
                _layoutReady = false;
                _stateEntryTimes.Clear();
                _stateDurations.Clear();
                _stateEntryTimes[State.Idle] = Stopwatch.GetTimestamp();

                Debug.WriteLine("[LOAD-PIPELINE] Reset to Idle");
                StateChanged?.Invoke(oldState, State.Idle);
            }
        }

        /// <summary>
        /// Forces transition to Error state.
        /// </summary>
        public void SetError(string reason = "")
        {
            lock (this)
            {
                if (_disposed) return;
                TransitionTo(State.Error);
                if (!string.IsNullOrEmpty(reason))
                {
                    Debug.WriteLine($"[LOAD-PIPELINE] Error: {reason}");
                }
            }
        }

        /// <summary>
        /// Returns the duration (in ms) spent in a given state, or -1 if not recorded.
        /// </summary>
        public long GetStateDurationMs(State state)
        {
            lock (this)
            {
                return _stateDurations.TryGetValue(state, out var ms) ? ms : -1;
            }
        }

        /// <summary>
        /// Returns a summary of state transitions for debugging.
        /// </summary>
        public string GetTimingSummary()
        {
            lock (this)
            {
                var parts = new List<string>();
                foreach (var kvp in _stateDurations)
                {
                    parts.Add($"{kvp.Key}={kvp.Value}ms");
                }
                return string.Join(", ", parts);
            }
        }

        #endregion

        #region Internal Helpers

        private static bool IsValidTransition(State from, State to)
        {
            if (from == to) return true;
            if (to == State.Error) return true; // Can error from any state

            return (from, to) switch
            {
                (State.Idle, State.Preparing) => true,
                (State.Preparing, State.Fetching) => true,
                (State.Fetching, State.Committing) => true,
                (State.Committing, State.Revealing) => true,
                (State.Revealing, State.Ready) => true,
                _ => false
            };
        }

        private void RecordDuration(State state)
        {
            if (_stateEntryTimes.TryGetValue(state, out var entryTime))
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - entryTime) * 1000 / Stopwatch.Frequency;
                _stateDurations[state] = elapsedMs;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _disposed = true;
        }

        #endregion
    }
}
