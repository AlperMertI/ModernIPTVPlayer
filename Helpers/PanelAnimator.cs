using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Diagnostics;

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
        private readonly string _name;

        private DateTime _lastRevealTime;
        private bool _isVisible;
        private EventHandler<object>? _pendingMorphHandler;
        private bool _disposed;

        public DateTime LastRevealTime => _lastRevealTime;
        public PanelMorphStyle MorphStyle { get; set; } = PanelMorphStyle.Spring;

        public PanelAnimator(FrameworkElement panel, Compositor compositor, CompositeTransform? transform = null, string? name = null)
        {
            _panel = panel ?? throw new ArgumentNullException(nameof(panel));
            _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
            _transform = transform!;
            _name = string.IsNullOrWhiteSpace(name) ? panel.Name : name!;

            if (_panel.IsLoaded)
            {
                CompositionService.EnableTranslation(_panel);
            }
            else
            {
                _panel.Loaded += (s, e) => CompositionService.EnableTranslation(_panel);
            }
            Trace("ctor");
        }

        /// <summary>
        /// Idempotent visibility API for layout code. The owner provides the desired state;
        /// this animator decides whether a reveal or dismiss transition is needed.
        /// </summary>
        public void ApplyVisible(bool visible, bool isHorizontalReveal, double startOffset = 800, int durationMs = 900)
        {
            ThrowIfDisposed();
            Trace("ApplyVisible enter", new Dictionary<string, object?>
            {
                ["visible"] = visible,
                ["isVisible"] = _isVisible,
                ["panelVisibility"] = _panel.Visibility.ToString(),
                ["panelOpacity"] = _panel.Opacity,
                ["isHorizontalReveal"] = isHorizontalReveal,
                ["startOffset"] = startOffset,
                ["durationMs"] = durationMs
            });

            if (visible)
            {
                if (!_isVisible || _panel.Visibility != Visibility.Visible || _panel.Opacity < 0.99)
                {
                    Trace("ApplyVisible before Reveal");
                    Reveal(isHorizontalReveal, startOffset, durationMs);
                    Trace("ApplyVisible after Reveal");
                }
                else
                {
                    Trace("ApplyVisible before ResetVisibleSurface");
                    ResetVisibleSurface();
                    Trace("ApplyVisible after ResetVisibleSurface");
                }
                Trace("ApplyVisible exit visible");
                return;
            }

            if (_isVisible || _panel.Visibility == Visibility.Visible)
            {
                Trace("ApplyVisible before Dismiss");
                Dismiss();
                Trace("ApplyVisible after Dismiss");
            }
            Trace("ApplyVisible exit collapsed");
        }

        public void Reveal(bool isHorizontalReveal, double startOffset = 800, int durationMs = 900)
        {
            ThrowIfDisposed();
            if (_panel == null || _compositor == null) return;
            Trace("Reveal enter", new Dictionary<string, object?>
            {
                ["isHorizontalReveal"] = isHorizontalReveal,
                ["startOffset"] = startOffset,
                ["durationMs"] = durationMs,
                ["panelVisibility"] = _panel.Visibility.ToString(),
                ["panelOpacity"] = _panel.Opacity
            });

            _isVisible = true;
            _lastRevealTime = DateTime.Now;

            // [ROOT FIX] Re-ensure translation is enabled before starting visual property manipulation
            CompositionService.EnableTranslation(_panel);
            Trace("Reveal after SetIsTranslationEnabled");

            CompositionService.Run(_panel, visual => 
            {
                Trace("Reveal after GetElementVisual");
                CompositionService.StopAll(visual);
                Trace("Reveal after StopAnimation");

                // Ensure panel is visible before animating so the visual is connected to the compositor tree
                if (_panel.Visibility != Visibility.Visible)
                    _panel.Visibility = Visibility.Visible;
                ResetVisibleSurface();
                Trace("Reveal after ResetVisibleSurface");

                // Reset transform so only composition drives position during the glide
                if (_transform != null)
                {
                    _transform.TranslateX = 0;
                    _transform.TranslateY = 0;
                }

                // Set initial state explicitly
                visual.Opacity = 0f;
                Vector3 startPos = isHorizontalReveal ? new Vector3((float)startOffset, 0, 0) : new Vector3(0, (float)startOffset, 0);
                
                try 
                {
                    visual.Properties.InsertVector3(CompositionService.TranslationProperty, startPos);
                }
                catch { visual.Offset = startPos; } // Fallback to Offset if Translation facade fails
                
                Trace("Reveal after initial visual state", new Dictionary<string, object?>
                {
                    ["translationX"] = startPos.X,
                    ["translationY"] = startPos.Y,
                    ["translationZ"] = startPos.Z
                });

                // Opacity Animation — explicit keyframes for reliability
                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f);
                fadeIn.InsertKeyFrame(1f, 1f, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(0.2f, 1f)));
                fadeIn.Duration = TimeSpan.FromMilliseconds(500);
                try { visual.StartAnimation("Opacity", fadeIn); }
                catch (Exception ex) { Trace("Reveal opacity StartAnimation failed", new Dictionary<string, object?> { ["hresult"] = ex.HResult, ["msg"] = ex.Message }); }
                Trace("Reveal after opacity StartAnimation");

                // Translation Animation — spring glide for a smooth, flowing entry
                var glide = _compositor.CreateSpringVector3Animation();
                glide.FinalValue = Vector3.Zero;
                glide.DampingRatio = 0.75f;
                glide.Period = TimeSpan.FromMilliseconds(120);
                
                try 
                { 
                    visual.StartAnimation(CompositionService.TranslationProperty, glide); 
                }
                catch 
                { 
                    try { visual.StartAnimation(CompositionService.OffsetProperty, glide); } catch { }
                }
                Trace("Reveal exit after translation StartAnimation");
            });
        }

        public void Dismiss()
        {
            if (_disposed && !_isVisible) return; // Allow running during Dispose, but not twice
            if (_panel == null) return;
            Trace("Dismiss enter", new Dictionary<string, object?>
            {
                ["disposed"] = _disposed,
                ["isVisible"] = _isVisible,
                ["panelVisibility"] = _panel.Visibility.ToString()
            });

            _isVisible = false;
            CancelPendingMorph();
            Trace("Dismiss after CancelPendingMorph");

            CompositionService.Run(_panel, visual => 
            {
                Trace("Dismiss after GetElementVisual");
                CompositionService.StopAll(visual);
                Trace("Dismiss after StopAnimation");

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
                Trace("Dismiss exit");
            });
        }

        public void MorphIfNeeded(FrameworkElement layoutRoot)
        {
            ThrowIfDisposed();
            Trace("MorphIfNeeded enter", new Dictionary<string, object?>
            {
                ["layoutRootNull"] = layoutRoot == null,
                ["isVisible"] = _isVisible,
                ["isLoaded"] = _panel?.IsLoaded
            });
            if (_panel == null || _compositor == null || layoutRoot == null) return;
            if (!_isVisible || !_panel.IsLoaded) return;

            // Removed the 800ms delay to ensure layout changes are always animated
            Trace("MorphIfNeeded before StartMorph");
            StartMorph(layoutRoot);
            Trace("MorphIfNeeded after StartMorph");
        }

        public void ResetVisuals()
        {
            if (_disposed) return;
            if (_panel == null) return;
            Trace("ResetVisuals enter");

            CancelPendingMorph();

            CompositionService.Run(_panel, visual => 
            {
                CompositionService.StopAll(visual);

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
                Trace("ResetVisuals exit");
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            Trace("Dispose enter");
            _disposed = true;
            CancelPendingMorph();
            Dismiss();
            Trace("Dispose exit");
        }

        private void StartMorph(FrameworkElement layoutRoot)
        {
            if (layoutRoot == null || _panel == null) return;
            Trace("StartMorph enter");
            
            try
            {
                CancelPendingMorph();

                CompositionService.Run(_panel, visual => 
                {
                    CompositionService.EnableTranslation(_panel);
                    
                    // 1. Capture current visual position BEFORE layout change
                    Vector3 currentTranslation = Vector3.Zero;
                    try { visual.Properties.TryGetVector3(CompositionService.TranslationProperty, out currentTranslation); }
                    catch { }

                    var ttv = _panel.TransformToVisual(layoutRoot);
                    var posBefore = ttv.TransformPoint(new Windows.Foundation.Point(0, 0));

                    double effectiveBeforeX = posBefore.X + currentTranslation.X;
                    double effectiveBeforeY = posBefore.Y + currentTranslation.Y;
                    
                    // 2. Stop any current animation to "freeze" the visual at this captured position
                    CompositionService.StopAll(visual);
                    try { visual.Properties.InsertVector3(CompositionService.TranslationProperty, currentTranslation); } catch { }

                    EventHandler<object> handler = null!;
                    handler = (s, e) =>
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] [PanelAnimator] LayoutUpdated fired for {_name}");
                        _panel.LayoutUpdated -= handler;
                        if (_pendingMorphHandler == handler) _pendingMorphHandler = null;

                        CompositionService.Run(_panel, v => 
                        {
                            try
                            {
                                if (!_isVisible || !_panel.IsLoaded) return;

                                // 3. Capture new layout position
                                var ttvAfter = _panel.TransformToVisual(layoutRoot);
                                var posAfter = ttvAfter.TransformPoint(new Windows.Foundation.Point(0, 0));

                                double dx = effectiveBeforeX - posAfter.X;
                                double dy = effectiveBeforeY - posAfter.Y;

                                if (Math.Abs(dx) < 0.2 && Math.Abs(dy) < 0.2)
                                {
                                    try { v.Properties.InsertVector3(CompositionService.TranslationProperty, Vector3.Zero); } catch { }
                                    return;
                                }

                                // Set the new offset and start the smoothing animation
                                try { v.Properties.InsertVector3(CompositionService.TranslationProperty, new Vector3((float)dx, (float)dy, 0)); } 
                                catch { v.Offset = new Vector3((float)dx, (float)dy, 0); }

                                if (MorphStyle == PanelMorphStyle.Spring)
                                {
                                    var spring = _compositor.CreateSpringVector3Animation();
                                    spring.FinalValue = Vector3.Zero;
                                    spring.DampingRatio = 0.82f;
                                    spring.Period = TimeSpan.FromMilliseconds(65);
                                    try { v.StartAnimation(CompositionService.TranslationProperty, spring); } catch { v.StartAnimation(CompositionService.OffsetProperty, spring); }
                                }
                                else
                                {
                                    var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
                                    var anim = _compositor.CreateVector3KeyFrameAnimation();
                                    anim.InsertKeyFrame(1f, Vector3.Zero, easing);
                                    anim.Duration = TimeSpan.FromMilliseconds(450);
                                    try { v.StartAnimation(CompositionService.TranslationProperty, anim); } catch { v.StartAnimation(CompositionService.OffsetProperty, anim); }
                                }
                            }
                            catch (Exception ex) { Trace("StartMorph LayoutUpdated catch", new Dictionary<string, object?> { ["msg"] = ex.Message }); }
                        });
                    };

                    _pendingMorphHandler = handler;
                    _panel.LayoutUpdated += handler;
                });
            }
            catch (Exception ex) { Trace("StartMorph catch", new Dictionary<string, object?> { ["msg"] = ex.Message }); }
        }

        private void CancelPendingMorph()
        {
            Trace("CancelPendingMorph enter", new Dictionary<string, object?>
            {
                ["hasPendingMorphHandler"] = _pendingMorphHandler != null
            });
            if (_pendingMorphHandler != null && _panel != null)
            {
                try { _panel.LayoutUpdated -= _pendingMorphHandler; }
                catch { }
                _pendingMorphHandler = null;
            }
            Trace("CancelPendingMorph exit");
        }

        private static void TryResetTranslation(Visual visual)
        {
            try
            {
                visual.Properties.InsertVector3(CompositionService.TranslationProperty, Vector3.Zero);
            }
            catch { }
        }

        private void ResetVisibleSurface()
        {
            Trace("ResetVisibleSurface enter");
            _panel.Opacity = 1;
            CompositionService.Run(_panel, visual => 
            {
                visual.Opacity = Math.Max(visual.Opacity, 1f);
                Trace("ResetVisibleSurface applied", new Dictionary<string, object?>
                {
                    ["visualOpacity"] = visual.Opacity,
                    ["panelOpacity"] = _panel.Opacity
                });
            });
            Trace("ResetVisibleSurface exit");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PanelAnimator));
        }

        private void Trace(string message, IDictionary<string, object?>? data = null)
        {
            try
            {
                var payload = data == null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>(data);
                payload["panel"] = _name;
                App.DebugNdjson("PanelAnimator.cs", message, payload, "panel-animator");
            }
            catch { }
        }
    }
}
