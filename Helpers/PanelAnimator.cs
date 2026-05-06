using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Numerics;

namespace ModernIPTVPlayer.Helpers
{
    public enum PanelMorphStyle
    {
        Spring,
        Cubic
    }

    public sealed class PanelAnimator : IDisposable
    {
        private readonly FrameworkElement _panel;
        private readonly Compositor _compositor;
        private readonly CompositeTransform _transform;

        private DateTime _lastRevealTime;
        private bool _isVisible;
        private EventHandler<object>? _pendingMorphHandler;
        private bool _disposed;

        public DateTime LastRevealTime => _lastRevealTime;
        public PanelMorphStyle MorphStyle { get; set; } = PanelMorphStyle.Spring;

        public PanelAnimator(FrameworkElement panel, Compositor compositor, CompositeTransform? transform = null)
        {
            _panel = panel ?? throw new ArgumentNullException(nameof(panel));
            _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
            _transform = transform!;

            // Ensure Translation is enabled immediately
            try { ElementCompositionPreview.SetIsTranslationEnabled(_panel, true); } catch { }
        }

        /// <summary>
        /// Idempotent visibility API for layout code. The owner provides the desired state;
        /// this animator decides whether a reveal or dismiss transition is needed.
        /// </summary>
        public void ApplyVisible(bool visible, bool isHorizontalReveal, double startOffset = 800, int durationMs = 900)
        {
            ThrowIfDisposed();

            if (visible)
            {
                if (!_isVisible || _panel.Visibility != Visibility.Visible || _panel.Opacity < 0.99)
                {
                    Reveal(isHorizontalReveal, startOffset, durationMs);
                }
                else
                {
                    ResetVisibleSurface();
                }
                return;
            }

            if (_isVisible || _panel.Visibility == Visibility.Visible)
            {
                Dismiss();
            }
        }

        public void Reveal(bool isHorizontalReveal, double startOffset = 800, int durationMs = 900)
        {
            ThrowIfDisposed();
            if (_panel == null || _compositor == null) return;

            _isVisible = true;
            _lastRevealTime = DateTime.Now;

            // [ROOT FIX] Re-ensure translation is enabled before starting visual property manipulation
            try { ElementCompositionPreview.SetIsTranslationEnabled(_panel, true); } catch { }

            var visual = ElementCompositionPreview.GetElementVisual(_panel);
            try { visual.StopAnimation("Opacity"); } catch { }
            try { visual.StopAnimation("Translation"); } catch { }

            // Ensure panel is visible before animating so the visual is connected to the compositor tree
            if (_panel.Visibility != Visibility.Visible)
                _panel.Visibility = Visibility.Visible;
            ResetVisibleSurface();

            // Reset transform so only composition drives position during the glide
            if (_transform != null)
            {
                _transform.TranslateX = 0;
                _transform.TranslateY = 0;
            }

            // Set initial state explicitly
            visual.Opacity = 0f;
            Vector3 startPos = isHorizontalReveal ? new Vector3((float)startOffset, 0, 0) : new Vector3(0, (float)startOffset, 0);
            visual.Properties.InsertVector3("Translation", startPos);

            // Opacity Animation — explicit keyframes for reliability
            var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
            fadeIn.InsertKeyFrame(0f, 0f);
            fadeIn.InsertKeyFrame(1f, 1f, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(0.2f, 1f)));
            fadeIn.Duration = TimeSpan.FromMilliseconds(500);
            visual.StartAnimation("Opacity", fadeIn);

            // Translation Animation — spring glide for a smooth, flowing entry
            var glide = _compositor.CreateSpringVector3Animation();
            glide.FinalValue = Vector3.Zero;
            glide.DampingRatio = 0.75f;
            glide.Period = TimeSpan.FromMilliseconds(120);
            visual.StartAnimation("Translation", glide);
        }

        public void Dismiss()
        {
            if (_disposed && !_isVisible) return; // Allow running during Dispose, but not twice
            if (_panel == null) return;

            _isVisible = false;
            CancelPendingMorph();

            var visual = ElementCompositionPreview.GetElementVisual(_panel);
            try { visual.StopAnimation("Opacity"); } catch { }
            try { visual.StopAnimation("Scale"); } catch { }
            try { visual.StopAnimation("Translation"); } catch { }

            visual.Opacity = 1f;
            visual.Scale = Vector3.One;
            _panel.Opacity = 1;

            TryResetTranslation(visual);

            if (_transform != null)
            {
                _transform.TranslateX = 0;
                _transform.TranslateY = 0;
                _transform.ScaleX = 1;
                _transform.ScaleY = 1;
            }

            _panel.Visibility = Visibility.Collapsed;
        }

        public void MorphIfNeeded(FrameworkElement layoutRoot)
        {
            ThrowIfDisposed();
            if (_panel == null || _compositor == null || layoutRoot == null) return;
            if (!_isVisible || !_panel.IsLoaded) return;

            // Removed the 800ms delay to ensure layout changes are always animated
            StartMorph(layoutRoot);
        }

        public void ResetVisuals()
        {
            if (_disposed) return;
            if (_panel == null) return;

            CancelPendingMorph();

            var visual = ElementCompositionPreview.GetElementVisual(_panel);
            try { visual.StopAnimation("Opacity"); } catch { }
            try { visual.StopAnimation("Scale"); } catch { }
            try { visual.StopAnimation("Translation"); } catch { }

            visual.Opacity = 1f;
            visual.Scale = Vector3.One;
            visual.Clip = null;
            _panel.Opacity = 1;

            TryResetTranslation(visual);

            if (_transform != null)
            {
                _transform.TranslateX = 0;
                _transform.TranslateY = 0;
                _transform.ScaleX = 1;
                _transform.ScaleY = 1;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelPendingMorph();
            Dismiss();
        }

        private void StartMorph(FrameworkElement layoutRoot)
        {
            if (layoutRoot == null) return;
            try
            {
                CancelPendingMorph();

                var visual = ElementCompositionPreview.GetElementVisual(_panel);
                ElementCompositionPreview.SetIsTranslationEnabled(_panel, true);

                // 1. Capture current visual position BEFORE layout change
                Vector3 currentTranslation = Vector3.Zero;
                try { visual.Properties.TryGetVector3("Translation", out currentTranslation); }
                catch { }

                var ttv = _panel.TransformToVisual(layoutRoot);
                var posBefore = ttv.TransformPoint(new Windows.Foundation.Point(0, 0));

                double effectiveBeforeX = posBefore.X + currentTranslation.X;
                double effectiveBeforeY = posBefore.Y + currentTranslation.Y;

                // 2. Stop any current animation to "freeze" the visual at this captured position
                try { visual.StopAnimation("Translation"); } catch { }
                visual.Properties.InsertVector3("Translation", currentTranslation);

                EventHandler<object> handler = null!;
                handler = (s, e) =>
                {
                    _panel.LayoutUpdated -= handler;
                    if (_pendingMorphHandler == handler) _pendingMorphHandler = null;

                    try
                    {
                        if (!_isVisible || !_panel.IsLoaded) return;

                        // 3. Capture new layout position
                        var ttvAfter = _panel.TransformToVisual(layoutRoot);
                        var posAfter = ttvAfter.TransformPoint(new Windows.Foundation.Point(0, 0));

                        double dx = effectiveBeforeX - posAfter.X;
                        double dy = effectiveBeforeY - posAfter.Y;

                        // Use a smaller threshold for sensitivity
                        if (Math.Abs(dx) < 0.2 && Math.Abs(dy) < 0.2)
                        {
                            visual.Properties.InsertVector3("Translation", Vector3.Zero);
                            return;
                        }

                        // Set the new offset and start the smoothing animation
                        visual.Properties.InsertVector3("Translation", new Vector3((float)dx, (float)dy, 0));

                        if (MorphStyle == PanelMorphStyle.Spring)
                        {
                            var spring = _compositor.CreateSpringVector3Animation();
                            spring.FinalValue = Vector3.Zero;
                            spring.DampingRatio = 0.82f;
                            spring.Period = TimeSpan.FromMilliseconds(65);
                            visual.StartAnimation("Translation", spring);
                        }
                        else
                        {
                            var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
                            var anim = _compositor.CreateVector3KeyFrameAnimation();
                            anim.InsertKeyFrame(1f, Vector3.Zero, easing);
                            anim.Duration = TimeSpan.FromMilliseconds(450);
                            visual.StartAnimation("Translation", anim);
                        }
                    }
                    catch { }
                };

                _pendingMorphHandler = handler;
                _panel.LayoutUpdated += handler;
            }
            catch { }
        }

        private void CancelPendingMorph()
        {
            if (_pendingMorphHandler != null && _panel != null)
            {
                try { _panel.LayoutUpdated -= _pendingMorphHandler; }
                catch { }
                _pendingMorphHandler = null;
            }
        }

        private static void TryResetTranslation(Visual visual)
        {
            try
            {
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
            catch { }
        }

        private void ResetVisibleSurface()
        {
            _panel.Opacity = 1;
            var visual = ElementCompositionPreview.GetElementVisual(_panel);
            visual.Opacity = Math.Max(visual.Opacity, 1f);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PanelAnimator));
        }
    }
}
