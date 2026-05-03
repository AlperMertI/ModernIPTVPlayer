using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class ShimmerControl : UserControl
    {
        private Storyboard _storyboard;
        private bool _callbackRegistered = false;
        private const double DefaultTravel = 520.0;

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
            _storyboard.Children.Clear();

            var anim = new DoubleAnimation
            {
                From = -DefaultTravel,
                To = DefaultTravel,
                Duration = new Duration(ShimmerDuration),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

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

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(ShimmerControl), new PropertyMetadata(new CornerRadius(4)));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public static readonly DependencyProperty BaseBrushProperty =
            DependencyProperty.Register(nameof(BaseBrush), typeof(Brush), typeof(ShimmerControl), new PropertyMetadata(new SolidColorBrush(Windows.UI.Color.FromArgb(34, 255, 255, 255))));

        public Brush BaseBrush
        {
            get => (Brush)GetValue(BaseBrushProperty);
            set => SetValue(BaseBrushProperty, value);
        }

        public static readonly DependencyProperty HighlightOpacityProperty =
            DependencyProperty.Register(nameof(HighlightOpacity), typeof(double), typeof(ShimmerControl), new PropertyMetadata(1.0));

        public double HighlightOpacity
        {
            get => (double)GetValue(HighlightOpacityProperty);
            set => SetValue(HighlightOpacityProperty, value);
        }

        public static readonly DependencyProperty SweepWidthProperty =
            DependencyProperty.Register(nameof(SweepWidth), typeof(double), typeof(ShimmerControl), new PropertyMetadata(360.0));

        public double SweepWidth
        {
            get => (double)GetValue(SweepWidthProperty);
            set => SetValue(SweepWidthProperty, value);
        }

        public static readonly DependencyProperty ShimmerDurationProperty =
            DependencyProperty.Register(nameof(ShimmerDuration), typeof(TimeSpan), typeof(ShimmerControl), new PropertyMetadata(TimeSpan.FromMilliseconds(1450), OnTimingChanged));

        public TimeSpan ShimmerDuration
        {
            get => (TimeSpan)GetValue(ShimmerDurationProperty);
            set => SetValue(ShimmerDurationProperty, value);
        }

        private static void OnTimingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ShimmerControl shimmer)
            {
                shimmer.SetupAnimation();
                if (shimmer.Visibility == Visibility.Visible) shimmer.BeginShimmer();
            }
        }
    }
}
