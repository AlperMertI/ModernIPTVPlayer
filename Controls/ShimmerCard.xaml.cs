using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class ShimmerCard : UserControl
    {
        private bool _callbackRegistered = false;

        public ShimmerCard()
        {
            this.InitializeComponent();

            this.Loaded += (s, e) =>
            {
                if (!_callbackRegistered)
                {
                    this.RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
                    _callbackRegistered = true;
                }
                
                if (Visibility == Visibility.Visible)
                    StartShimmer();
            };

            this.Unloaded += (s, e) => ShimmerStoryboard.Stop();
        }

        private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (Visibility == Visibility.Visible)
                StartShimmer();
            else
                ShimmerStoryboard.Stop();
        }

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register("CornerRadius", typeof(CornerRadius), typeof(ShimmerCard), new PropertyMetadata(new CornerRadius(12)));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        private void StartShimmer()
        {
            // Random staggering (up to 300ms to ensure snappiness)
            ShimmerStoryboard.BeginTime = TimeSpan.FromMilliseconds(new System.Random().NextDouble() * 300);
            ShimmerStoryboard.Begin();
        }
    }
}
