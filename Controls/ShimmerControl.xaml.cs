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
        private const double DefaultTravel = 220.0;

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
            if (ShimmerTransform == null) return;
            
            _storyboard = new Storyboard();
            _storyboard.Children.Clear();

            // [Senior] Calculate travel based on width for full coverage
            double width = this.ActualWidth > 0 ? this.ActualWidth : 1200;
            double travel = width * 1.5;

            var anim = new DoubleAnimation
            {
                From = -travel,
                To = travel,
                Duration = new Duration(ShimmerDuration),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            Storyboard.SetTarget(anim, ShimmerTransform);
            Storyboard.SetTargetProperty(anim, "X");
            _storyboard.Children.Add(anim);

            // Re-bind size changed to keep animation accurate

            this.SizeChanged -= OnSizeChanged;
            this.SizeChanged += OnSizeChanged;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width != e.PreviousSize.Width) SetupAnimation();
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
            DependencyProperty.Register(nameof(BaseBrush), typeof(Brush), typeof(ShimmerControl), new PropertyMetadata(new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255))));

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
            DependencyProperty.Register(nameof(SweepWidth), typeof(double), typeof(ShimmerControl), new PropertyMetadata(200.0));

        public double SweepWidth
        {
            get => (double)GetValue(SweepWidthProperty);
            set => SetValue(SweepWidthProperty, value);
        }

        public static readonly DependencyProperty ShimmerDurationProperty =
            DependencyProperty.Register(nameof(ShimmerDuration), typeof(TimeSpan), typeof(ShimmerControl), new PropertyMetadata(TimeSpan.FromMilliseconds(3500), OnTimingChanged));

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
