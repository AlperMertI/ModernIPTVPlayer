using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class ShimmerControl : UserControl
    {
        private Storyboard _storyboard;

        public ShimmerControl()
        {
            this.InitializeComponent();
            this.Loaded += ShimmerControl_Loaded;
            this.Unloaded += ShimmerControl_Unloaded;
            
            SetupAnimation();
        }

        private void SetupAnimation()
        {
            _storyboard = new Storyboard();
            
            var anim = new DoubleAnimation
            {
                From = -300,
                To = 300,
                Duration = new Duration(TimeSpan.FromSeconds(1.5)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            
            // Randomize start time slightly for more natural look across multiple items
            double delay = new Random().NextDouble() * 500;
            anim.BeginTime = TimeSpan.FromMilliseconds(delay);

            Storyboard.SetTarget(anim, ShimmerTransform);
            Storyboard.SetTargetProperty(anim, "X");
            _storyboard.Children.Add(anim);
        }

        private void ShimmerControl_Loaded(object sender, RoutedEventArgs e)
        {
            _storyboard.Begin();
        }

        private void ShimmerControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _storyboard.Stop();
        }
        
        public void Stop() => _storyboard.Stop();
        public void Start() => _storyboard.Begin();
    }
}
