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
        private Windows.UI.Color _currentLeftColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        private Windows.UI.Color _currentRightColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

        private static Random _random = new Random();

        public DynamicBackdrop()
        {
            this.InitializeComponent();
            InitializeBrushes();
            StartBreathingAnimation();
        }

        private void InitializeBrushes()
        {
            // Create brushes initial state
            AmbientLayer.Background = CreateRadialBrush(0, 0, 0, 0, 0, 0.5, 1.0, 2.0, 1.5);
            PrimaryGlowLayer.Background = CreateRadialBrush(0, 0, 0, 0, 0, 0.5, 1.0, 1.5, 1.0);
            BloomLayer.Background = CreateRadialBrush(0, 0, 0, 0, 0, 0.5, 0.9, 1.0, 0.8);
        }

        private void StartBreathingAnimation()
        {
            // GPU-optimized: Use Composition animations instead of DispatcherTimer
            // Composition animations run on the compositor thread — zero CPU cost
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

            // Primary glow: gentle opacity breathing
            var primaryVisual = ElementCompositionPreview.GetElementVisual(PrimaryGlowLayer);
            var primaryAnim = compositor.CreateScalarKeyFrameAnimation();
            primaryAnim.InsertKeyFrame(0f, 0.35f);
            primaryAnim.InsertKeyFrame(0.5f, 0.55f);
            primaryAnim.InsertKeyFrame(1f, 0.35f);
            primaryAnim.Duration = TimeSpan.FromSeconds(6);
            primaryAnim.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            primaryVisual.StartAnimation("Opacity", primaryAnim);

            // Ambient: inverse phase
            var ambientVisual = ElementCompositionPreview.GetElementVisual(AmbientLayer);
            var ambientAnim = compositor.CreateScalarKeyFrameAnimation();
            ambientAnim.InsertKeyFrame(0f, 0.30f);
            ambientAnim.InsertKeyFrame(0.5f, 0.20f);
            ambientAnim.InsertKeyFrame(1f, 0.30f);
            ambientAnim.Duration = TimeSpan.FromSeconds(6);
            ambientAnim.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            ambientVisual.StartAnimation("Opacity", ambientAnim);

            // Bloom: slightly faster cycle
            var bloomVisual = ElementCompositionPreview.GetElementVisual(BloomLayer);
            var bloomAnim = compositor.CreateScalarKeyFrameAnimation();
            bloomAnim.InsertKeyFrame(0f, 0.10f);
            bloomAnim.InsertKeyFrame(0.5f, 0.20f);
            bloomAnim.InsertKeyFrame(1f, 0.10f);
            bloomAnim.Duration = TimeSpan.FromSeconds(4);
            bloomAnim.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            bloomVisual.StartAnimation("Opacity", bloomAnim);
        }

        public void TransitionTo(Color targetLeft, Color targetRight)
        {
            _backdropAnimationTimer?.Stop();

            var startL = _currentLeftColor;
            var startR = _currentRightColor;
            var steps = 20;
            int currentStep = 0;

            _backdropAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };

            _backdropAnimationTimer.Tick += (s, e) =>
            {
                currentStep++;
                float t = (float)currentStep / steps;
                t = 1 - (float)Math.Pow(1 - t, 3); // Cubic EaseOut

                byte rL = (byte)(startL.R + (targetLeft.R - startL.R) * t);
                byte gL = (byte)(startL.G + (targetLeft.G - startL.G) * t);
                byte bL = (byte)(startL.B + (targetLeft.B - startL.B) * t);

                byte rR = (byte)(startR.R + (targetRight.R - startR.R) * t);
                byte gR = (byte)(startR.G + (targetRight.G - startR.G) * t);
                byte bR = (byte)(startR.B + (targetRight.B - startR.B) * t);

                _currentLeftColor = Windows.UI.Color.FromArgb(255, rL, gL, bL);
                _currentRightColor = Windows.UI.Color.FromArgb(255, rR, gR, bR);

                byte avgR = (byte)((rL + rR) / 2);
                byte avgG = (byte)((gL + gR) / 2);
                byte avgB = (byte)((bL + bR) / 2);

                // FORCE UPDATE: Create NEW brushes to bypass PropertyChanged optimization
                AmbientLayer.Background = CreateRadialBrush(rL, gL, bL, 200, 0, 0.5, 1.0, 2.0, 1.5);
                PrimaryGlowLayer.Background = CreateRadialBrush(rR, gR, bR, 220, 0, 0.5, 1.0, 1.5, 1.0);
                BloomLayer.Background = CreateRadialBrush(avgR, avgG, avgB, 150, 0, 0.5, 0.9, 1.0, 0.8);

                // Floating accent
                FloatingStop.Color = Windows.UI.Color.FromArgb(120, avgR, avgG, avgB);

                if (currentStep >= steps)
                {
                    _backdropAnimationTimer?.Stop();
                    _backdropAnimationTimer = null;
                }
            };
            _backdropAnimationTimer.Start();
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
            // Parallax disabled to keep bottom light source fixed
            // AmbientLayer.RenderTransform = new TranslateTransform { Y = -offset * 0.05 };
            // PrimaryGlowLayer.RenderTransform = new TranslateTransform { Y = -offset * 0.12 };
            // BloomLayer.RenderTransform = new TranslateTransform { Y = -offset * 0.22 };
            
            // FloatingTransform.Y = -offset * 0.18;
        }
    }
}
