using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Threading.Tasks;
using System.Diagnostics;
using Windows.UI;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class PosterCard : UserControl
    {
        public bool IsHovered { get; private set; }
        public Image ImageElement => PosterImage;
        public (Color Primary, Color Secondary)? HeroColors { get; private set; }

        public event EventHandler<(Color Primary, Color Secondary)> ColorsExtracted;
        public event EventHandler<IMediaStream>? Clicked;
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
        
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hoverTimer;
        private System.Threading.CancellationTokenSource? _renderCts;
        private string? _lastUrl;
        private double _lastWidth;

        public PosterCard()
        {
            this.InitializeComponent();
            this.DataContextChanged += (s, e) => 
            {
                UpdateIPTVBadgeVisibility();
                // Mandatory update when recycled with new data
                UpdateImage();
            };
            
            // Native fix for virtualization and window-resize pop-in
            this.EffectiveViewportChanged += (s, e) => UpdateImage();

            // [SENIOR FIX: First-Item Initialization Guard]
            // If the item starts in the viewport but layout hasn't finished, 
            // the width might be 0. We must force a reload once the width is known.
            this.SizeChanged += (s, e) => 
            {
                if (_lastWidth <= 160 && e.NewSize.Width > 0 && Math.Abs(e.NewSize.Width - _lastWidth) > 5)
                {
                    UpdateImage();
                }
            };
        }
    


        private void UpdateImage()
        {
            var queue = this.DispatcherQueue;
            if (queue == null) return;

            if (!queue.HasThreadAccess)
            {
                queue.TryEnqueue(UpdateImage);
                return;
            }

            // [PRECISION] Calculate target width. If layout is not ready (0), 
            // use a safe fallback for the first pass.
            double actualWidth = this.ActualWidth;
            bool isLayoutReady = actualWidth > 0;
            if (!isLayoutReady) actualWidth = 160;

            double targetWidth = Math.Round(actualWidth / 10.0) * 10.0;
            string itemTitle = Title ?? "Unknown";

            if (string.IsNullOrEmpty(ImageUrl))
            {
                _lastUrl = null;
                _lastWidth = 0;
                PosterImage.Source = null;
                PosterImage.Opacity = 0;
                PosterShimmer.Visibility = Visibility.Collapsed;
                return;
            }

            // [VIRTUALIZATION GUARD] If the current source is already correct and not null, 
            // skip the entire expensive re-initialization to avoid flickers.
            if (PosterImage.Source != null && ImageUrl == _lastUrl && targetWidth == _lastWidth)
            {
                if (PosterImage.Opacity < 1 && PosterShimmer.Visibility == Visibility.Collapsed)
                {
                    PosterImage.Opacity = 1;
                }
                return;
            }

            var bitmap = SharedImageManager.GetOptimizedImage(
                ImageUrl, 
                targetWidth: targetWidth, 
                xamlRoot: this.XamlRoot);

            if (bitmap == null) return;

            // [SENIOR OPTIMIZATION: Flicker-Free Resolve]
            bool isResolutionUpgrade = (_lastUrl == ImageUrl && PosterImage.Source != null);

            // [NATIVE FIX: Surface Integrity Check]
            // If the bitmap has been in RAM for a long time, its UriSource might be intact 
            // but its underlying rendering surface might have been evicted.
            bool isDormant = (bitmap.PixelWidth == 0 && bitmap.UriSource != null && !isResolutionUpgrade);

            if ((bitmap.PixelWidth > 0 || bitmap.PixelHeight > 0) && !isDormant)
            {
                // [SENIOR OPTIMIZATION: Zero-Blink Hit]
                _lastUrl = ImageUrl;
                _lastWidth = targetWidth;

                if (PosterImage.Source != bitmap) PosterImage.Source = bitmap;
                PosterImage.Opacity = 1;
                PosterShimmer.Visibility = Visibility.Collapsed;
                FadeInStoryboard?.Stop();
            }
            else
            {
                // [SENIOR OPTIMIZATION: Flicker-Free / Surface Refresh]
                _lastUrl = ImageUrl;
                _lastWidth = targetWidth;

                if (!isResolutionUpgrade)
                {
                    PrepareForLoading();
                }
                
                // If the surface is dormant/evicted, re-setting UriSource forces a native refresh
                if (isDormant) bitmap.UriSource = new Uri(ImageUrl);
                
                PosterImage.Source = bitmap;
            }
            
            UpdateIPTVBadgeVisibility();
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
            object ctx = DataContext;
            if (ctx is StremioMediaStream stream)
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
            
            // [STABILITY] We no longer clear PosterImage.Source here.
            // ItemsRepeater recycles these controls; clearing the source forces a 
            // re-download/re-decode on every scroll, causing "flicker".
            
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

        private void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            object ctx = DataContext;
            IMediaStream? stream = ctx as IMediaStream;
            if (stream == null && ctx is UnifiedMediaItemContext contextWrap)
            {
                stream = contextWrap.Data;
            }

            if (stream != null)
            {
                System.Diagnostics.Debug.WriteLine($"[PosterCard] Firing Clicked event for: {stream.Title}");
                Clicked?.Invoke(this, stream);
            }
        }
    }
}
