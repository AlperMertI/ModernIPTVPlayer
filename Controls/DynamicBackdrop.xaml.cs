using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class DynamicBackdrop : UserControl
    {
        private DispatcherTimer? _backdropAnimationTimer;
        private Windows.UI.Color _currentLeftColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        private Windows.UI.Color _currentRightColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        private DispatcherTimer? _breathingTimer;
        private double _breathPhase = 0;

        // Spotlight state for smooth interpolation
        private double _curLRadX = 1.5, _curLRadY = 1.2, _curLCentX = 0.0, _curLCentY = 0.0;
        private double _curRRadX = 1.5, _curRRadY = 1.2, _curRCentX = 1.0, _curRCentY = 0.0;

        private static Random _random = new Random();

        public DynamicBackdrop()
        {
            this.InitializeComponent();
            StartBreathingAnimation();
        }

        private void StartBreathingAnimation()
        {
            _breathingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _breathingTimer.Tick += (s, e) =>
            {
                _breathPhase += 0.025; // Slow, subtle breathing
                if (_breathPhase > Math.PI * 2) _breathPhase = 0;

                // Subtle opacity pulsing for layers
                // Primary: 0.35 to 0.55 (main glow breathes)
                PrimaryGlowLayer.Opacity = 0.45 + 0.10 * Math.Sin(_breathPhase);

                // Ambient: inverse pulse (creates depth perception)
                AmbientLayer.Opacity = 0.25 + 0.05 * Math.Sin(_breathPhase + Math.PI);

                // Bloom: faster, subtle pulse
                BloomLayer.Opacity = 0.15 + 0.05 * Math.Sin(_breathPhase * 1.5);

                // Secondary (top-right): different phase for variety
                SecondaryGlowLayer.Opacity = 0.12 + 0.04 * Math.Sin(_breathPhase * 0.7 + Math.PI / 2);
            };
            _breathingTimer.Start();
        }

        public void TransitionTo(Color targetLeft, Color targetRight)
        {
            // Stop any existing animation
            if (_backdropAnimationTimer != null)
            {
                _backdropAnimationTimer.Stop();
                _backdropAnimationTimer = null;
            }

            var startL = _currentLeftColor;
            var startR = _currentRightColor;
            var steps = 25; // Smooth motion
            int currentStep = 0;

            _backdropAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // 60fps

            // Generate random targets for light sources
            double targetLRadX = 1.2 + _random.NextDouble() * 0.8;
            double targetLRadY = 1.0 + _random.NextDouble() * 0.6;
            double targetLCentX = -0.2 + _random.NextDouble() * 0.4;
            double targetLCentY = -0.2 + _random.NextDouble() * 0.4;

            double targetRRadX = 1.0 + _random.NextDouble() * 1.0;
            double targetRRadY = 0.8 + _random.NextDouble() * 0.8;
            double targetRCentX = 0.8 + _random.NextDouble() * 0.4;
            double targetRCentY = -0.1 + _random.NextDouble() * 0.3;

            // Capture starting values for interpolation
            double startLRadX = _curLRadX, startLRadY = _curLRadY, startLCentX = _curLCentX, startLCentY = _curLCentY;
            double startRRadX = _curRRadX, startRRadY = _curRRadY, startRCentX = _curRCentX, startRCentY = _curRCentY;

            _backdropAnimationTimer.Tick += (s, e) =>
            {
                currentStep++;
                float t = (float)currentStep / steps;
                t = 1 - (float)Math.Pow(1 - t, 3); // Cubic EaseOut

                // 1. Color Interpolation
                byte rL = (byte)(startL.R + (targetLeft.R - startL.R) * t);
                byte gL = (byte)(startL.G + (targetLeft.G - startL.G) * t);
                byte bL = (byte)(startL.B + (targetLeft.B - startL.B) * t);

                byte rR = (byte)(startR.R + (targetRight.R - startR.R) * t);
                byte gR = (byte)(startR.G + (targetRight.G - startR.G) * t);
                byte bR = (byte)(startR.B + (targetRight.B - startR.B) * t);

                _currentLeftColor = Windows.UI.Color.FromArgb(255, rL, gL, bL);
                _currentRightColor = Windows.UI.Color.FromArgb(255, rR, gR, bR);

                // 2. Shape Interpolation
                _curLRadX = startLRadX + (targetLRadX - startLRadX) * t;
                _curLRadY = startLRadY + (targetLRadY - startLRadY) * t;
                _curLCentX = startLCentX + (targetLCentX - startLCentX) * t;
                _curLCentY = startLCentY + (targetLCentY - startLCentY) * t;

                _curRRadX = startRRadX + (targetRRadX - startRRadX) * t;
                _curRRadY = startRRadY + (targetRRadY - startRRadY) * t;
                _curRCentX = startRCentX + (targetRCentX - startRCentX) * t;
                _curRCentY = startRCentY + (targetRCentY - startRCentY) * t;

                // --- APPLY STAGE LIGHTING EFFECT ---
                AmbientLayer.Background = CreateRadialBrush(rL, gL, bL, 255, 0, _curLCentX, _curLCentY, _curLRadX, _curLRadY);
                PrimaryGlowLayer.Background = CreateRadialBrush(rR, gR, bR, 255, 0, _curRCentX, _curRCentY, _curRRadX, _curRRadY);

                // Blend/Bloom at top center
                byte avgR = (byte)((rL + rR) / 2);
                byte avgG = (byte)((gL + gR) / 2);
                byte avgB = (byte)((bL + bR) / 2);
                BloomLayer.Background = CreateRadialBrush(avgR, avgG, avgB, 200, 0, 0.5, 0.05, 1.0, 0.8);

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
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(alpha1, r, g, b), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb((byte)(alpha1 * 0.85), r, g, b), Offset = 0.15 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb((byte)(alpha1 * 0.60), r, g, b), Offset = 0.35 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(alpha2, r, g, b), Offset = 0.55 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb((byte)(alpha2 * 0.35), r, g, b), Offset = 0.8 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, r, g, b), Offset = 1 });
            return brush;
        }
    }
}
