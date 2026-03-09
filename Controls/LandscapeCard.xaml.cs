using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class LandscapeCard : UserControl
    {
        public bool IsHovered { get; private set; }
        public Image ImageElement => PosterImage;

        public event EventHandler HoverStarted;
        public event EventHandler HoverEnded;

        public static readonly DependencyProperty ImageUrlProperty =
            DependencyProperty.Register("ImageUrl", typeof(string), typeof(LandscapeCard), new PropertyMetadata(null, OnImageUrlChanged));

        public string ImageUrl
        {
            get { return (string)GetValue(ImageUrlProperty); }
            set { SetValue(ImageUrlProperty, value); }
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(LandscapeCard), new PropertyMetadata(string.Empty));

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public static readonly DependencyProperty ProgressValueProperty =
            DependencyProperty.Register("ProgressValue", typeof(double), typeof(LandscapeCard), new PropertyMetadata(0.0));

        public double ProgressValue
        {
            get { return (double)GetValue(ProgressValueProperty); }
            set { SetValue(ProgressValueProperty, value); }
        }

        public static readonly DependencyProperty BadgeTextProperty =
            DependencyProperty.Register("BadgeText", typeof(string), typeof(LandscapeCard), new PropertyMetadata(string.Empty));

        public string BadgeText
        {
            get { return (string)GetValue(BadgeTextProperty); }
            set { SetValue(BadgeTextProperty, value); }
        }

        public static readonly DependencyProperty ShowProgressProperty =
            DependencyProperty.Register("ShowProgress", typeof(bool), typeof(LandscapeCard), new PropertyMetadata(false));

        public bool ShowProgress
        {
            get { return (bool)GetValue(ShowProgressProperty); }
            set { SetValue(ShowProgressProperty, value); }
        }

        public static readonly DependencyProperty ShowBadgeProperty =
            DependencyProperty.Register("ShowBadge", typeof(bool), typeof(LandscapeCard), new PropertyMetadata(false));

        public bool ShowBadge
        {
            get { return (bool)GetValue(ShowBadgeProperty); }
            set { SetValue(ShowBadgeProperty, value); }
        }

        public static readonly DependencyProperty YearProperty =
            DependencyProperty.Register("Year", typeof(string), typeof(LandscapeCard), new PropertyMetadata(string.Empty));

        public string Year
        {
            get { return (string)GetValue(YearProperty); }
            set { SetValue(YearProperty, value); }
        }

        public static readonly DependencyProperty RatingTextProperty =
            DependencyProperty.Register("RatingText", typeof(string), typeof(LandscapeCard), new PropertyMetadata(string.Empty));

        public string RatingText
        {
            get { return (string)GetValue(RatingTextProperty); }
            set { SetValue(RatingTextProperty, value); }
        }

        public static readonly DependencyProperty SubtextProperty =
            DependencyProperty.Register("Subtext", typeof(string), typeof(LandscapeCard), new PropertyMetadata(string.Empty));

        public string Subtext
        {
            get { return (string)GetValue(SubtextProperty); }
            set { SetValue(SubtextProperty, value); }
        }
        
        public static readonly DependencyProperty ShowMetaProperty =
            DependencyProperty.Register("ShowMeta", typeof(bool), typeof(LandscapeCard), new PropertyMetadata(true));

        public bool ShowMeta
        {
            get { return (bool)GetValue(ShowMetaProperty); }
            set { SetValue(ShowMetaProperty, value); }
        }

        public bool HasRating => !string.IsNullOrEmpty(RatingText);
        public bool HasSubtext => !string.IsNullOrEmpty(Subtext);

        private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LandscapeCard card)
            {
                card.UpdateImage();
            }
        }

        public LandscapeCard()
        {
            this.InitializeComponent();
            MainBorder.SizeChanged += (s, e) => UpdateClip();
        }

        private void UpdateClip()
        {
            var visual = ElementCompositionPreview.GetElementVisual(MainBorder);
            var compositor = visual.Compositor;
            var clip = compositor.CreateInsetClip();
            visual.Clip = clip;
        }

        private void UpdateImage()
        {
            if (string.IsNullOrEmpty(ImageUrl))
            {
                PosterImage.Source = null;
                PosterImage.Opacity = 0;
                PosterShimmer.Visibility = Visibility.Visible;
            }
            else
            {
                // If it's the same source, don't re-trigger shimmer/hide
                if (PosterImage.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage current && current.UriSource?.ToString() == ImageUrl)
                    return;

                PosterImage.Opacity = 0; // Prepare for fade in
                PosterShimmer.Visibility = Visibility.Visible;

                var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bitmapImage.DecodePixelWidth = 480; // Higher quality for landscape
                bitmapImage.UriSource = new Uri(ImageUrl);
                
                PosterImage.Source = bitmapImage;
            }
        }

        private void Image_ImageOpened(object sender, RoutedEventArgs e)
        {
            PosterShimmer.Visibility = Visibility.Collapsed;
            
            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, PosterImage);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
            
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }

        private void Card_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateImage();
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            IsHovered = true;
            HoverInStoryboard.Begin();
            HoverStarted?.Invoke(this, EventArgs.Empty);
            
            PlayButtonContainer.Opacity = 1.0;
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
             var point = e.GetCurrentPoint(RootGrid).Position;
             if (point.X >= 0 && point.Y >= 0 && point.X <= RootGrid.ActualWidth && point.Y <= RootGrid.ActualHeight)
                 return;

             IsHovered = false;
             HoverOutStoryboard.Begin();
             HoverEnded?.Invoke(this, EventArgs.Empty);
             
             TiltProjection.RotationX = 0;
             TiltProjection.RotationY = 0;
             PlayButtonContainer.Opacity = 0.8;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (IsHovered)
            {
                var pointerPosition = e.GetCurrentPoint(RootGrid).Position;
                var center = new Windows.Foundation.Point(RootGrid.ActualWidth / 2, RootGrid.ActualHeight / 2);
                
                var xDiff = pointerPosition.X - center.X;
                var yDiff = pointerPosition.Y - center.Y;

                TiltProjection.RotationY = -xDiff / 50.0; // Subtle tilt for wide cards
                TiltProjection.RotationX = yDiff / 50.0;
            }
        }
    }
}
