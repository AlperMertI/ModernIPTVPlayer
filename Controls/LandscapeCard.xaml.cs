using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using ModernIPTVPlayer.Models;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class LandscapeCard : UserControl
    {
        public bool IsHovered { get; private set; }
        public Image ImageElement => PosterImage;

        public event EventHandler? HoverStarted;
        public event EventHandler? HoverEnded;
        public event EventHandler<IMediaStream>? Clicked;

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

        public static readonly DependencyProperty MediaStreamProperty =
            DependencyProperty.Register("MediaStream", typeof(IMediaStream), typeof(LandscapeCard), new PropertyMetadata(null, OnMediaStreamChanged));

        public IMediaStream MediaStream
        {
            get { return (IMediaStream)GetValue(MediaStreamProperty); }
            set { SetValue(MediaStreamProperty, value); }
        }

        private static void OnMediaStreamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LandscapeCard card && e.NewValue is IMediaStream stream)
            {
                // [ROOT FIX] Self-hydrate all display properties from the managed stream.
                card.ImageUrl = stream.LandscapeImageUrl;
                card.Title = stream.Title;
                card.Subtext = stream.DisplaySubtext;
                card.Year = stream.Year;
                card.RatingText = stream.Rating;
                card.ShowProgress = stream.ShowProgress;
                card.ShowMeta = stream.IsNotContinueWatching;
                card.ProgressValue = stream.ProgressValue;
            }
        }

        public Visibility GetVisibility(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GetVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

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
        }

        private void UpdateImage()
        {
            if (string.IsNullOrEmpty(ImageUrl))
            {
                PosterImage.Source = null;
            }
            else
            {
                // If it's the same source, don't re-trigger shimmer/hide
                if (PosterImage.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage current && current.UriSource?.ToString() == ImageUrl)
                    return;

                var bitmapImage = Helpers.SharedImageManager.GetOptimizedImage(ImageUrl, 480);
                PosterImage.Source = bitmapImage;
            }
        }

        private void Image_ImageOpened(object sender, RoutedEventArgs e)
        {
        }

        private void Image_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // Optional fallback
        }

        private void Card_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateImage();
            
            // [PINNACLE] Initialize the Visual layer properties
            var visual = ElementCompositionPreview.GetElementVisual(MainBorder);
            visual.CenterPoint = new Vector3((float)MainBorder.ActualWidth / 2, (float)MainBorder.ActualHeight / 2, 0);
        }

        private void Card_Unloaded(object sender, RoutedEventArgs e)
        {
            // [FIX] Ensure hover state is reset when card is recycled/unloaded
            ResetHoverState();
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            IsHovered = true;
            Canvas.SetZIndex(this, 10);
            HoverInStoryboard.Begin();
            StartCompositionTilt();
            HoverStarted?.Invoke(this, EventArgs.Empty);
            PlayButtonContainer.Opacity = 1.0;
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
             // Safety: Check if we are really outside the bounds
             // This prevents the card from closing when the ExpandedCard overlay appears and "steals" focus
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
            Canvas.SetZIndex(this, 0);
            HoverOutStoryboard.Begin();
            StopCompositionTilt();
            HoverEnded?.Invoke(this, EventArgs.Empty);
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            // [PINNACLE] Handled by the Compositor thread.
        }

        private void StartCompositionTilt()
        {
            var visual = ElementCompositionPreview.GetElementVisual(MainBorder);
            var compositor = visual.Compositor;
            var pointerPropSet = ElementCompositionPreview.GetPointerPositionPropertySet(MainBorder);

            var tiltAnim = compositor.CreateExpressionAnimation(
                "Matrix4x4.CreateTranslation(-center.X, -center.Y, 0) * " +
                "Matrix4x4.CreateRotationY(-((pointer.Position.X - center.X) / 60.0) * (3.14159 / 180.0)) * " +
                "Matrix4x4.CreateRotationX(((pointer.Position.Y - center.Y) / 60.0) * (3.14159 / 180.0)) * " +
                "Matrix4x4.CreateTranslation(center.X, center.Y, 0)"
            );

            tiltAnim.SetReferenceParameter("pointer", pointerPropSet);
            tiltAnim.SetVector2Parameter("center", new Vector2((float)MainBorder.ActualWidth / 2, (float)MainBorder.ActualHeight / 2));

            visual.StartAnimation("TransformMatrix", tiltAnim);
        }

        private void StopCompositionTilt()
        {
            var visual = ElementCompositionPreview.GetElementVisual(MainBorder);
            visual.StopAnimation("TransformMatrix");
            visual.TransformMatrix = Matrix4x4.Identity;
        }

        private void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (MediaStream != null)
            {
                Clicked?.Invoke(this, MediaStream);
            }
        }
    }
}
