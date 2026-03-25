using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;
using Windows.UI;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class DynamicBackdrop : UserControl
    {
        private DispatcherTimer _backdropAnimationTimer;
        private CompositeTransform _backdropTransform;
        private Windows.UI.Color _currentLeftColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        private Windows.UI.Color _currentRightColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

        // New fields for color transition
        private float _transitionProgress;
        private Windows.UI.Color _startColor;
        private Windows.UI.Color _targetColor;
        private Windows.UI.Color _startSecondary;
        private Windows.UI.Color _targetSecondary;


        private static Random _random = new Random();

        public DynamicBackdrop()
        {
            this.InitializeComponent();
            InitializeBrushes();
            
            this.Loaded += (s, e) => 
            {
                System.Diagnostics.Debug.WriteLine($"[DynamicBackdrop] Loaded. Opacity: {this.Opacity}, Visibility: {this.Visibility}, ActualSize: {this.ActualWidth}x{this.ActualHeight}");
                StartBreathingAnimation();
            };
        }

        private void InitializeBrushes()
        {
            // Keep the control transparent on first paint. The first real backdrop
            // state is applied only after color extraction provides a target.
        }

        private void ApplyBackdropState(Color left, Color right)
        {
            try
            {
                _currentLeftColor = left;
                _currentRightColor = right;

                // [FIX] Re-assign brushes to force WinUI 3 to redraw the layers. 
                // Position centers exactly at bottom-center (X=0.5, Y=1.0)
                AmbientLayer.Background = CreateRadialBrush(left.R, left.G, left.B, 200, 0, 0.5, 1.0, 1.8, 1.2);
                PrimaryGlowLayer.Background = CreateRadialBrush(right.R, right.G, right.B, 230, 0, 0.5, 1.0, 1.2, 0.9);
            
                Color avg = Color.FromArgb(160, (byte)((left.R + right.R) / 2), (byte)((left.G + right.G) / 2), (byte)((left.B + right.B) / 2));
                BloomLayer.Background = CreateRadialBrush(avg.R, avg.G, avg.B, 160, 0, 0.5, 1.0, 1.0, 0.7);
                FloatingAccentLayer.Background = CreateRadialBrush(avg.R, avg.G, avg.B, 120, 0, 0.5, 1.0, 0.9, 0.6);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DynamicBackdrop] ApplyState Error: {ex.Message}");
            }
        }

        private void StartBreathingAnimation()
        {
            // GPU-optimized: Use Composition animations instead of DispatcherTimer
            // Composition animations run on the compositor thread — zero CPU cost
            try 
            {
                if (this.XamlRoot == null)
                {
                    System.Diagnostics.Debug.WriteLine("[DynamicBackdrop] Skipping BreathingAnim: XamlRoot is NULL");
                    return;
                }

                System.Diagnostics.Debug.WriteLine("[DynamicBackdrop] Starting Breathing Animations...");
                var visual = ElementCompositionPreview.GetElementVisual(this);
                if (visual == null) return;
                var compositor = visual.Compositor;

                // Primary glow: gentle opacity breathing
                var primaryVisual = ElementCompositionPreview.GetElementVisual(PrimaryGlowLayer);
                if (primaryVisual != null)
                {
                    var primaryAnim = compositor.CreateScalarKeyFrameAnimation();
                    primaryAnim.InsertKeyFrame(0f, 0.35f);
                    primaryAnim.InsertKeyFrame(0.5f, 0.55f);
                    primaryAnim.InsertKeyFrame(1f, 0.35f);
                    primaryAnim.Duration = TimeSpan.FromSeconds(6);
                    primaryAnim.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                    primaryVisual.StartAnimation("Opacity", primaryAnim);
                }

                // Ambient: inverse phase
                var ambientVisual = ElementCompositionPreview.GetElementVisual(AmbientLayer);
                if (ambientVisual != null)
                {
                    var ambientAnim = compositor.CreateScalarKeyFrameAnimation();
                    ambientAnim.InsertKeyFrame(0f, 0.30f);
                    ambientAnim.InsertKeyFrame(0.5f, 0.20f);
                    ambientAnim.InsertKeyFrame(1f, 0.30f);
                    ambientAnim.Duration = TimeSpan.FromSeconds(6);
                    ambientAnim.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                    ambientVisual.StartAnimation("Opacity", ambientAnim);
                }

                // Bloom: slightly faster cycle
                var bloomVisual = ElementCompositionPreview.GetElementVisual(BloomLayer);
                if (bloomVisual != null)
                {
                    var bloomAnim = compositor.CreateScalarKeyFrameAnimation();
                    bloomAnim.InsertKeyFrame(0f, 0.10f);
                    bloomAnim.InsertKeyFrame(0.5f, 0.20f);
                    bloomAnim.InsertKeyFrame(1f, 0.10f);
                    bloomAnim.Duration = TimeSpan.FromSeconds(4);
                    bloomAnim.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                    bloomVisual.StartAnimation("Opacity", bloomAnim);
                }
                System.Diagnostics.Debug.WriteLine("[DynamicBackdrop] Breathing Animations OK");
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DynamicBackdrop] !!! COMException in StartBreathing: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DynamicBackdrop] !!! Error in StartBreathing: {ex.Message}");
            }
        }

        public void TransitionTo(Color targetLeft, Color targetRight)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[DynamicBackdrop] TransitionTo: L(#{targetLeft.R:X2}{targetLeft.G:X2}{targetLeft.B:X2}) R(#{targetRight.R:X2}{targetRight.G:X2}{targetRight.B:X2})");

                _backdropAnimationTimer?.Stop();

                // Fallback for placeholders, but ALLOW PURE BLACK (0,0,0) for trailers
                if (targetLeft.R < 5 && targetLeft.G < 5 && targetLeft.B < 5 && 
                    targetRight.R < 5 && targetRight.G < 5 && targetRight.B < 5 &&
                    (targetLeft.R != 0 || targetLeft.G != 0 || targetLeft.B != 0))
                {
                     targetLeft = Color.FromArgb(255, 30, 34, 40);
                     targetRight = Color.FromArgb(255, 20, 24, 28);
                }

                var startL = _currentLeftColor;
                var startR = _currentRightColor;
                
                // [FIX] If XamlRoot is null, we can't run a DispatcherTimer animation.
                // Apply the target state immediately so we don't stay black.
                if (this.XamlRoot == null)
                {
                    System.Diagnostics.Debug.WriteLine("[DynamicBackdrop] Applying state immediately (XamlRoot is NULL)");
                    ApplyBackdropState(targetLeft, targetRight);
                    return;
                }

                ApplyBackdropState(startL, startR);

                var steps = 24;
                int currentStep = 0;

                _backdropAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(24) };
                _backdropAnimationTimer.Tick += (s, e) =>
                {
                    try
                    {
                        if (this.XamlRoot == null)
                        {
                            System.Diagnostics.Debug.WriteLine("[DynamicBackdrop] Timer Stop: XamlRoot is null");
                            _backdropAnimationTimer?.Stop();
                            return;
                        }

                        currentStep++;
                        float t = (float)currentStep / steps;
                        t = 1 - (float)Math.Pow(1 - t, 3); // Cubic EaseOut

                        byte rL = (byte)(startL.R + (targetLeft.R - startL.R) * t);
                        byte gL = (byte)(startL.G + (targetLeft.G - startL.G) * t);
                        byte bL = (byte)(startL.B + (targetLeft.B - startL.B) * t);

                        byte rR = (byte)(startR.R + (targetRight.R - startR.R) * t);
                        byte gR = (byte)(startR.G + (targetRight.G - startR.G) * t);
                        byte bR = (byte)(startR.B + (targetRight.B - startR.B) * t);

                        ApplyBackdropState(
                            Windows.UI.Color.FromArgb(255, rL, gL, bL),
                            Windows.UI.Color.FromArgb(255, rR, gR, bR));

                        if (currentStep >= steps)
                        {
                            _backdropAnimationTimer?.Stop();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DynamicBackdrop] Timer Error: {ex.Message}");
                        _backdropAnimationTimer?.Stop();
                    }
                };
                _backdropAnimationTimer.Start();
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[DynamicBackdrop] TransitionTo Error: {ex.Message}");
            }
        }

        private RadialGradientBrush CreateRadialBrush(byte r, byte g, byte b, byte alpha1, byte alpha2,
            double centerX, double centerY, double radiusX, double radiusY)
        {
            var brush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(centerX, centerY),
                RadiusX = radiusX,
                RadiusY = radiusY,
                GradientOrigin = new Windows.Foundation.Point(centerX, centerY)
            };
            // Simpler 4-stop gradient (vs 6 stops before) — 33% less GPU work
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(alpha1, r, g, b), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb((byte)(alpha1 * 0.60), r, g, b), Offset = 0.25 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb((byte)(alpha1 * 0.25), r, g, b), Offset = 0.55 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, r, g, b), Offset = 1 });
            return brush;
        }

        public void SetVerticalShift(double offset)
        {
            // Re-enable vertical shift for a more dynamic "parallax" feel
            // We shift the entire backdrop container slightly opposite to scroll
            if (_backdropTransform == null)
            {
                _backdropTransform = new CompositeTransform();
                BackdropContainer.RenderTransform = _backdropTransform;
            }
            
            // Subtle parallax: shift backdrop up as user scrolls down (-20% ratio)
            _backdropTransform.TranslateY = -offset * 0.18;
        }
    }
}
