using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Threading.Tasks;
using Windows.UI;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class PosterCard : UserControl
    {
        public bool IsHovered { get; private set; }
        public Image ImageElement => PosterImage;
        public (Color Primary, Color Secondary)? HeroColors { get; private set; }

        public event EventHandler<(Color Primary, Color Secondary)> ColorsExtracted;
        public event EventHandler HoverStarted;
        public event EventHandler HoverEnded;

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

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(PosterCard), new PropertyMetadata(string.Empty));

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public static readonly DependencyProperty ProgressValueProperty =
            DependencyProperty.Register("ProgressValue", typeof(double), typeof(PosterCard), new PropertyMetadata(0.0));

        public string Subtext
        {
            get => (string)GetValue(SubtextProperty);
            set => SetValue(SubtextProperty, value);
        }

        public static readonly DependencyProperty SubtextProperty =
            DependencyProperty.Register("Subtext", typeof(string), typeof(PosterCard), new PropertyMetadata(null, OnSubtextChanged));

        public Visibility HasSubtext
        {
            get => (Visibility)GetValue(HasSubtextProperty);
            private set => SetValue(HasSubtextProperty, value);
        }

        public static readonly DependencyProperty HasSubtextProperty =
            DependencyProperty.Register("HasSubtext", typeof(Visibility), typeof(PosterCard), new PropertyMetadata(Visibility.Collapsed));

        private static void OnSubtextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PosterCard card)
            {
                card.HasSubtext = string.IsNullOrEmpty(card.Subtext) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        public double ProgressValue
        {
            get { return (double)GetValue(ProgressValueProperty); }
            set { SetValue(ProgressValueProperty, value); }
        }

        public static readonly DependencyProperty BadgeTextProperty =
            DependencyProperty.Register("BadgeText", typeof(string), typeof(PosterCard), new PropertyMetadata(string.Empty));

        public string BadgeText
        {
            get { return (string)GetValue(BadgeTextProperty); }
            set { SetValue(BadgeTextProperty, value); }
        }

        public static readonly DependencyProperty ShowProgressProperty =
            DependencyProperty.Register("ShowProgress", typeof(bool), typeof(PosterCard), new PropertyMetadata(false));

        public bool ShowProgress
        {
            get { return (bool)GetValue(ShowProgressProperty); }
            set { SetValue(ShowProgressProperty, value); }
        }

        public static readonly DependencyProperty IsAvailableOnIptvProperty =
            DependencyProperty.Register("IsAvailableOnIptv", typeof(bool), typeof(PosterCard), new PropertyMetadata(false, OnIPTVStateChanged));

        public bool IsAvailableOnIptv
        {
            get { return (bool)GetValue(IsAvailableOnIptvProperty); }
            set { SetValue(IsAvailableOnIptvProperty, value); }
        }

        public static readonly DependencyProperty ShowIptvBadgeProperty =
            DependencyProperty.Register("ShowIptvBadge", typeof(bool), typeof(PosterCard), new PropertyMetadata(true, OnIPTVStateChanged));

        public bool ShowIptvBadge
        {
            get { return (bool)GetValue(ShowIptvBadgeProperty); }
            set { SetValue(ShowIptvBadgeProperty, value); }
        }

        public static readonly DependencyProperty ShowBadgeProperty =
            DependencyProperty.Register("ShowBadge", typeof(bool), typeof(PosterCard), new PropertyMetadata(false));

        public bool ShowBadge
        {
            get { return (bool)GetValue(ShowBadgeProperty); }
            set { SetValue(ShowBadgeProperty, value); }
        }

        public static readonly DependencyProperty IsTitleVisibleProperty =
            DependencyProperty.Register("IsTitleVisible", typeof(bool), typeof(PosterCard), new PropertyMetadata(false));

        public bool IsTitleVisible
        {
            get { return (bool)GetValue(IsTitleVisibleProperty); }
            set { SetValue(IsTitleVisibleProperty, value); }
        }

        private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PosterCard card)
            {
                card.UpdateImage();
            }
        }
        
        private void PrepareForLoading()
        {
            PosterImage.Opacity = 0;
            PosterShimmer.Opacity = 1;
            PosterShimmer.Visibility = Visibility.Visible;
            FadeInStoryboard?.Stop();
        }
        
        private DispatcherTimer? _hoverTimer;
        private System.Threading.CancellationTokenSource? _renderCts;

        public PosterCard()
        {
            this.InitializeComponent();
            this.DataContextChanged += (s, e) => UpdateIPTVBadgeVisibility();
        }


        private void UpdateImage()
        {
            if (string.IsNullOrEmpty(ImageUrl))
            {
                PosterImage.Source = null;
                PosterImage.Opacity = 0;
                PosterShimmer.Visibility = Visibility.Collapsed;
                // No valid URL -> Only show centered placeholder
                TitleOverlay.Visibility = Visibility.Collapsed;
                PlaceholderTitle.Visibility = Visibility.Visible;
            }
            else
            {
                PrepareForLoading();
                // Optimize: Set DecodePixelWidth to save memory (Card width is ~160)
                var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bitmapImage.DecodePixelWidth = 200; // Slightly larger than 160 for quality
                bitmapImage.UriSource = new Uri(ImageUrl);
                
                PosterImage.Source = bitmapImage;
                
                // Note: Animation will be triggered in Image_ImageOpened
                UpdateIPTVBadgeVisibility();
            }
        }

        private static void OnIPTVStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PosterCard card)
            {
                card.UpdateIPTVBadgeVisibility();
            }
        }

        private void UpdateIPTVBadgeVisibility()
        {
            if (IptvBadge == null) return;
            
            bool isIptvSource = false;
            if (DataContext is StremioMediaStream stream)
            {
                isIptvSource = stream.IsAvailableOnIptv || stream.IsIptv;
            }

            IptvBadge.Visibility = (ShowIptvBadge && (IsAvailableOnIptv || isIptvSource)) ? Visibility.Visible : Visibility.Collapsed;
        }


        private void Image_ImageOpened(object sender, RoutedEventArgs e)
        {
            FadeInStoryboard?.Begin();
            UpdateIPTVBadgeVisibility();
        }

    private void Image_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // If the image fails to load, only show centered placeholder
            TitleOverlay.Visibility = Visibility.Collapsed;
            PlaceholderTitle.Visibility = Visibility.Visible;
            PosterShimmer.Visibility = Visibility.Collapsed;
            PosterImage.Opacity = 0;
        }

        private void PosterCard_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateImage();
        }

        private void PosterCard_Unloaded(object sender, RoutedEventArgs e)
        {
            _renderCts?.Cancel();
            _renderCts = null;
            ResetHoverState();
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            IsHovered = true;
            Canvas.SetZIndex(this, 10); // Bring to front
            HoverInStoryboard.Begin();
            HoverStarted?.Invoke(this, EventArgs.Empty);
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
             // [FIX] Bounds Check relative to MainBorder
             var point = e.GetCurrentPoint(MainBorder).Position;
             
             if (point.X >= 0 && point.Y >= 0 && 
                 point.X <= MainBorder.ActualWidth && 
                 point.Y <= MainBorder.ActualHeight)
             {
                 return;
             }

             ResetHoverState();
        }

        public void ResetHoverState()
        {
            if (!IsHovered) return;
            IsHovered = false;
            Canvas.SetZIndex(this, 0); // Reset ZIndex
            HoverOutStoryboard.Begin();
            HoverEnded?.Invoke(this, EventArgs.Empty);
             
            TiltProjection.RotationX = 0;
            TiltProjection.RotationY = 0;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (IsHovered)
            {
                var pointerPosition = e.GetCurrentPoint(MainBorder).Position;
                var center = new Windows.Foundation.Point(MainBorder.ActualWidth / 2, MainBorder.ActualHeight / 2);
                
                var xDiff = pointerPosition.X - center.X;
                var yDiff = pointerPosition.Y - center.Y;

                TiltProjection.RotationY = -xDiff / 25.0; 
                TiltProjection.RotationX = yDiff / 25.0;
            }
        }
        public double GetProgressScale(double progress)
        {
            return Math.Clamp(progress / 100.0, 0, 1.0);
        }

        public void PrepareConnectedAnimation()
        {
            ConnectedAnimationService.GetForCurrentView()
                .PrepareToAnimate("ForwardConnectedAnimation", PosterImage);
        }
    }
}
