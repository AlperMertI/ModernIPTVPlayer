using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Numerics;
using System.Threading.Tasks;
using Windows.UI;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class PosterCard : UserControl
    {
        public bool IsHovered { get; private set; }

        public event EventHandler<(Color Primary, Color Secondary)> ColorsExtracted;
        public event EventHandler HoverStarted;

        public static readonly DependencyProperty ImageUrlProperty =
            DependencyProperty.Register("ImageUrl", typeof(string), typeof(PosterCard), new PropertyMetadata(null, OnImageUrlChanged));

        public string ImageUrl
        {
            get { return (string)GetValue(ImageUrlProperty); }
            set { SetValue(ImageUrlProperty, value); }
        }

        public static readonly DependencyProperty IsTiltEnabledProperty =
            DependencyProperty.Register("IsTiltEnabled", typeof(bool), typeof(PosterCard), new PropertyMetadata(true));

        public bool IsTiltEnabled
        {
            get { return (bool)GetValue(IsTiltEnabledProperty); }
            set { SetValue(IsTiltEnabledProperty, value); }
        }

        private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PosterCard card)
            {
                card.UpdateImage();
            }
        }
        
        public PosterCard()
        {
            this.InitializeComponent();
        }



        private void UpdateImage()
        {
            if (string.IsNullOrEmpty(ImageUrl))
            {
                PosterImage.Source = null;
                PosterImage.Opacity = 0;
            }
            else
            {
                // Image loading is handled by XAML binding, but we trigger the opacity anim in ImageOpened
                var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(ImageUrl));
                PosterImage.Source = bitmapImage;
            }
        }

        private void Image_ImageOpened(object sender, RoutedEventArgs e)
        {
             // Fade in
             DoubleAnimation fadeIn = new DoubleAnimation() { To = 1, Duration = TimeSpan.FromSeconds(0.6) };
             CubicEase ease = new CubicEase() { EasingMode = EasingMode.EaseOut };
             fadeIn.EasingFunction = ease;
             Storyboard sb = new Storyboard();
             sb.Children.Add(fadeIn);
             Storyboard.SetTarget(fadeIn, PosterImage);
             Storyboard.SetTargetProperty(fadeIn, "Opacity");
             sb.Begin();
        }

        private async void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            IsHovered = true;
            HoverInStoryboard.Begin();
            HoverStarted?.Invoke(this, EventArgs.Empty);
            
            // Still extract colors for internal use/events, but no visual glow here
            if (!string.IsNullOrEmpty(ImageUrl))
            {
                var colors = await ImageHelper.GetOrExtractColorAsync(ImageUrl);
                if (colors.HasValue)
                {
                    ColorsExtracted?.Invoke(this, colors.Value);
                }
            }
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
             IsHovered = false;
             HoverOutStoryboard.Begin();
             
             TiltProjection.RotationX = 0;
             TiltProjection.RotationY = 0;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (IsHovered && IsTiltEnabled)
            {
                var pointerPosition = e.GetCurrentPoint(RootGrid).Position;
                var center = new Windows.Foundation.Point(RootGrid.ActualWidth / 2, RootGrid.ActualHeight / 2);
                
                var xDiff = pointerPosition.X - center.X;
                var yDiff = pointerPosition.Y - center.Y;

                // Simple Tilt
                TiltProjection.RotationY = -xDiff / 15.0;
                TiltProjection.RotationX = yDiff / 15.0;
            }
        }
    }
}
