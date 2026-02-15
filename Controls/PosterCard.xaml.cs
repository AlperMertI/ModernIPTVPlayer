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
        public Image ImageElement => PosterImage;

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
        
        public PosterCard()
        {
            this.InitializeComponent();
            MainBorder.SizeChanged += (s, e) => UpdateClip();
        }

        private void UpdateClip()
        {
            var visual = ElementCompositionPreview.GetElementVisual(MainBorder);
            var compositor = visual.Compositor;
            
            // WinUI 3: RoundedRectangleClip requires newer SDK. 
            // Fallback to InsetClip to match bounds, Border.CornerRadius handles visual rounding.
            var clip = compositor.CreateInsetClip();
            visual.Clip = clip;
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
            // 1. Hide shimmer
            PosterShimmer.Visibility = Visibility.Collapsed;
            
            // 2. Premium Diagonal Reveal Animation (Composition API)
            try
            {
                var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
                var imageVisual = ElementCompositionPreview.GetElementVisual(PosterImage);
                
                // Ensure image is visible in XAML so composition works
                PosterImage.Opacity = 1;

                // Create a VisualSurface to capture the Image content
                var surface = compositor.CreateVisualSurface();
                surface.SourceVisual = imageVisual;
                surface.SourceSize = new Vector2((float)MainBorder.ActualWidth, (float)MainBorder.ActualHeight);

                var surfaceBrush = compositor.CreateSurfaceBrush(surface);
                surfaceBrush.Stretch = CompositionStretch.UniformToFill;

                // Create Diagonal Gradient Mask
                var gradient = compositor.CreateLinearGradientBrush();
                gradient.StartPoint = new Vector2(0, 0);
                gradient.EndPoint = new Vector2(1, 1);

                // Stop 1: Visible part (White)
                var stop1 = compositor.CreateColorGradientStop(0f, Microsoft.UI.Colors.White);
                // Stop 2: Transition part (Soft edge)
                var stop2 = compositor.CreateColorGradientStop(0f, Microsoft.UI.Colors.White);
                // Stop 3: Hidden part (Transparent)
                var stop3 = compositor.CreateColorGradientStop(0.1f, Microsoft.UI.Colors.Transparent);
                
                gradient.ColorStops.Add(stop1);
                gradient.ColorStops.Add(stop2);
                gradient.ColorStops.Add(stop3);

                var maskBrush = compositor.CreateMaskBrush();
                maskBrush.Source = surfaceBrush;
                maskBrush.Mask = gradient;


                // SpriteVisual to host the masked content
                var revealVisual = compositor.CreateSpriteVisual();
                revealVisual.Brush = maskBrush;
                revealVisual.Size = new Vector2((float)MainBorder.ActualWidth, (float)MainBorder.ActualHeight);

                // Attach to MainBorder
                ElementCompositionPreview.SetElementChildVisual(MainBorder, revealVisual);

                // Create Easing function
                var cubicBezier = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.2f, 1f));

                // ANIMATION: Move the gradient stops from 0 to 1
                var revealAnim = compositor.CreateScalarKeyFrameAnimation();
                revealAnim.Duration = TimeSpan.FromMilliseconds(1200);
                revealAnim.InsertKeyFrame(0f, 0f);
                revealAnim.InsertKeyFrame(1f, 1.2f, cubicBezier); 

                stop1.StartAnimation("Offset", revealAnim);
                
                var revealAnim2 = compositor.CreateScalarKeyFrameAnimation();
                revealAnim2.Duration = TimeSpan.FromMilliseconds(1200);
                revealAnim2.InsertKeyFrame(0f, 0.05f);
                revealAnim2.InsertKeyFrame(1f, 1.25f, cubicBezier);
                stop2.StartAnimation("Offset", revealAnim2);

                var revealAnim3 = compositor.CreateScalarKeyFrameAnimation();
                revealAnim3.Duration = TimeSpan.FromMilliseconds(1200);
                revealAnim3.InsertKeyFrame(0f, 0.3f);
                revealAnim3.InsertKeyFrame(1f, 1.5f, cubicBezier);
                stop3.StartAnimation("Offset", revealAnim3);


                // Final cleanup: Remove MaskBrush after animation to save GPU
                var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                batch.Completed += (s, args) =>
                {
                    ElementCompositionPreview.SetElementChildVisual(MainBorder, null);
                    PosterImage.Opacity = 1; // Pure XAML now
                };
                batch.End();
            }
            catch
            {
                // Fallback for safety
                PosterImage.Opacity = 1;
            }
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
             // Fix: Check if we are really outside the bounds
             // This prevents the card from closing when the ExpandedCard overlay appears and "steals" focus
             var point = e.GetCurrentPoint(RootGrid).Position;
             if (point.X >= 0 && point.Y >= 0 && point.X <= RootGrid.ActualWidth && point.Y <= RootGrid.ActualHeight)
             {
                 // Still inside (likely moved over child element or the ExpandedCard overlay covers us), ignore
                 return;
             }

             IsHovered = false;
             HoverOutStoryboard.Begin();
             HoverEnded?.Invoke(this, EventArgs.Empty);
             
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
        public void PrepareConnectedAnimation()
        {
            ConnectedAnimationService.GetForCurrentView()
                .PrepareToAnimate("ForwardConnectedAnimation", PosterImage);
        }
    }
}
