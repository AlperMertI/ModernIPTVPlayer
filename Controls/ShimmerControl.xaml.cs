using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class ShimmerControl : UserControl
    {
        private Storyboard _storyboard;
        private bool _callbackRegistered = false;

        public ShimmerControl()
        {
            this.InitializeComponent();
            SetupAnimation();

            // Register for loaded/unloaded to manage animation lifecycle
            this.Loaded += (s, e) =>
            {
                // Register visibility callback once, on first load
                if (!_callbackRegistered)
                {
                    this.RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
                    _callbackRegistered = true;
                }
                // Start if already visible when loaded
                if (Visibility == Visibility.Visible)
                    BeginShimmer();
            };

            this.Unloaded += (s, e) => _storyboard?.Stop();
        }

        private void SetupAnimation()
        {
            _storyboard = new Storyboard();

            var anim = new DoubleAnimation
            {
                From = -400,
                To = 400,
                Duration = new Duration(TimeSpan.FromSeconds(1.6)),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            // Stagger each instance slightly so multiple shimmers don't pulse in sync
            anim.BeginTime = TimeSpan.FromMilliseconds(new Random().NextDouble() * 800);

            Storyboard.SetTarget(anim, ShimmerTransform);
            Storyboard.SetTargetProperty(anim, "X");
            _storyboard.Children.Add(anim);
        }

        private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (Visibility == Visibility.Visible)
                BeginShimmer();
            else
                _storyboard?.Stop();
        }

        /// <summary>
        /// Starts the shimmer, always dispatched to the UI thread to be safe.
        /// </summary>
        private void BeginShimmer()
        {
            if (DispatcherQueue != null)
                DispatcherQueue.TryEnqueue(() => _storyboard?.Begin());
            else
                _storyboard?.Begin();
        }

        public void Stop() => _storyboard?.Stop();
        public void Start() => BeginShimmer();
    }
}
