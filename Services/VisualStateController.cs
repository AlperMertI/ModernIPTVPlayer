using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Single authority for Visibility and Opacity state on tracked UI elements.
    /// Prevents race conditions from multiple code paths setting these properties independently.
    /// 
    /// Responsibilities:
    /// - Owns all Visibility/Opacity changes for MediaInfoPage elements
    /// - Batches changes and applies atomically via ApplyPending()
    /// - Prevents conflicting state (e.g., Visibility=Visible + Opacity=0)
    /// 
    /// Does NOT:
    /// - Manage layout positions (that's LayoutEngine)
    /// - Manage composition animations (those still work, but final state goes through here)
    /// - Own panel mode state (that's PanelOwner)
    /// </summary>
    internal sealed class VisualStateController : IDisposable
    {
        #region Fields

        private readonly Dictionary<FrameworkElement, ElementState> _trackedElements = new(ReferenceEqualityComparer.Instance);
        private bool _disposed;

        #endregion

        #region Element State Tracking

        private sealed class ElementState
        {
            public Visibility? PendingVisibility;
            public double? PendingOpacity;
            public Visibility CurrentVisibility;
            public double CurrentOpacity = 1.0;
        }

        #endregion

        #region Public API — Direct Setters

        /// <summary>
        /// Sets visibility for an element. Applies immediately.
        /// </summary>
        public void SetVisibility(FrameworkElement element, Visibility visibility)
        {
            ThrowIfDisposed();
            if (element == null) return;

            if (element.Visibility != visibility)
            {
                Debug.WriteLine($"[VSC] SetVisibility {element.Name}: {element.Visibility} -> {visibility}");
                element.Visibility = visibility;
            }

            var state = GetOrCreateState(element);
            state.CurrentVisibility = visibility;
            state.PendingVisibility = null;
        }

        /// <summary>
        /// Sets opacity for an element. Applies immediately.
        /// Also stops any running composition opacity animation to prevent conflicts.
        /// </summary>
        public void SetOpacity(FrameworkElement element, double opacity)
        {
            ThrowIfDisposed();
            if (element == null) return;

            opacity = Math.Max(0.0, Math.Min(1.0, opacity));

            if (Math.Abs(element.Opacity - opacity) > 0.001)
            {
                Debug.WriteLine($"[VSC] SetOpacity {element.Name}: {element.Opacity} -> {opacity}");
                element.Opacity = opacity;
            }

            var state = GetOrCreateState(element);
            state.CurrentOpacity = opacity;
            state.PendingOpacity = null;
        }

        /// <summary>
        /// Sets both visibility and opacity atomically.
        /// Visibility is set first, then opacity, to prevent rendering glitches.
        /// </summary>
        public void SetState(FrameworkElement element, Visibility visibility, double opacity)
        {
            ThrowIfDisposed();
            if (element == null) return;

            opacity = Math.Max(0.0, Math.Min(1.0, opacity));

            var state = GetOrCreateState(element);
            bool visibilityChanged = element.Visibility != visibility;
            bool opacityChanged = Math.Abs(element.Opacity - opacity) > 0.001;

            if (visibilityChanged)
            {
                Debug.WriteLine($"[VSC] SetState {element.Name}: Vis={element.Visibility}->{visibility}, Opacity={element.Opacity}->{opacity}");
                element.Visibility = visibility;
            }

            if (opacityChanged)
            {
                element.Opacity = opacity;
            }

            state.CurrentVisibility = visibility;
            state.CurrentOpacity = opacity;
            state.PendingVisibility = null;
            state.PendingOpacity = null;
        }

        #endregion

        #region Public API — Batch Operations

        /// <summary>
        /// Queues a visibility change. Applied on next ApplyPending() call.
        /// </summary>
        public void QueueVisibility(FrameworkElement element, Visibility visibility)
        {
            ThrowIfDisposed();
            if (element == null) return;

            var state = GetOrCreateState(element);
            state.PendingVisibility = visibility;
        }

        /// <summary>
        /// Queues an opacity change. Applied on next ApplyPending() call.
        /// </summary>
        public void QueueOpacity(FrameworkElement element, double opacity)
        {
            ThrowIfDisposed();
            if (element == null) return;

            opacity = Math.Max(0.0, Math.Min(1.0, opacity));
            var state = GetOrCreateState(element);
            state.PendingOpacity = opacity;
        }

        /// <summary>
        /// Queues both visibility and opacity. Applied atomically on next ApplyPending() call.
        /// </summary>
        public void QueueState(FrameworkElement element, Visibility visibility, double opacity)
        {
            ThrowIfDisposed();
            if (element == null) return;

            opacity = Math.Max(0.0, Math.Min(1.0, opacity));
            var state = GetOrCreateState(element);
            state.PendingVisibility = visibility;
            state.PendingOpacity = opacity;
        }

        /// <summary>
        /// Applies all queued changes in a single pass.
        /// Visibility is applied before opacity to prevent rendering glitches.
        /// </summary>
        public void ApplyPending()
        {
            ThrowIfDisposed();

            // Phase 1: Apply all visibility changes first
            foreach (var kvp in _trackedElements)
            {
                var element = kvp.Key;
                var state = kvp.Value;

                if (state.PendingVisibility.HasValue && element.Visibility != state.PendingVisibility.Value)
                {
                    Debug.WriteLine($"[VSC] ApplyPending {element.Name}: Vis={element.Visibility}->{state.PendingVisibility.Value}");
                    element.Visibility = state.PendingVisibility.Value;
                    state.CurrentVisibility = state.PendingVisibility.Value;
                    state.PendingVisibility = null;
                }
            }

            // Phase 2: Apply all opacity changes
            foreach (var kvp in _trackedElements)
            {
                var element = kvp.Key;
                var state = kvp.Value;

                if (state.PendingOpacity.HasValue && Math.Abs(element.Opacity - state.PendingOpacity.Value) > 0.001)
                {
                    Debug.WriteLine($"[VSC] ApplyPending {element.Name}: Opacity={element.Opacity}->{state.PendingOpacity.Value}");
                    element.Opacity = state.PendingOpacity.Value;
                    state.CurrentOpacity = state.PendingOpacity.Value;
                    state.PendingOpacity = null;
                }
            }
        }

        #endregion

        #region Public API — Convenience Methods

        /// <summary>
        /// Sets visibility to Visible and opacity to 1.0 for multiple elements in one call.
        /// </summary>
        public void Reveal(params FrameworkElement[] elements)
        {
            foreach (var element in elements)
            {
                if (element != null) SetState(element, Visibility.Visible, 1.0);
            }
        }

        /// <summary>
        /// Sets visibility to Collapsed for multiple elements in one call.
        /// Opacity is NOT changed (caller manages opacity separately if needed).
        /// </summary>
        public void Collapse(params FrameworkElement[] elements)
        {
            foreach (var element in elements)
            {
                if (element != null) SetVisibility(element, Visibility.Collapsed);
            }
        }

        /// <summary>
        /// Sets visibility to Collapsed and opacity to 0 for multiple elements.
        /// </summary>
        public void Hide(params FrameworkElement[] elements)
        {
            foreach (var element in elements)
            {
                if (element != null) SetState(element, Visibility.Collapsed, 0.0);
            }
        }

        #endregion

        #region Internal Helpers

        private ElementState GetOrCreateState(FrameworkElement element)
        {
            if (!_trackedElements.TryGetValue(element, out var state))
            {
                state = new ElementState
                {
                    CurrentVisibility = element.Visibility,
                    CurrentOpacity = element.Opacity
                };
                _trackedElements[element] = state;
            }
            return state;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VisualStateController));
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _disposed = true;
            _trackedElements.Clear();
        }

        #endregion
    }
}
