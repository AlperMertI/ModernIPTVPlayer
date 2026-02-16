using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Stremio;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class HeroSectionControl : UserControl
    {
        // Events
        public event EventHandler<IMediaStream> PlayAction;
        public event EventHandler<IMediaStream> DetailsAction;
        public event EventHandler<(Windows.UI.Color Primary, Windows.UI.Color Secondary)> ColorExtracted;

        // Data
        private List<StremioMediaStream> _heroItems = new();
        private int _currentHeroIndex = 0;
        private bool _heroTransitioning = false;
        private DispatcherTimer _heroAutoTimer;

        // Composition API
        private Microsoft.UI.Composition.SpriteVisual _heroVisual;
        private Microsoft.UI.Composition.CompositionSurfaceBrush _heroImageBrush;
        private Microsoft.UI.Composition.CompositionLinearGradientBrush _heroAlphaMask;
        private Microsoft.UI.Composition.CompositionMaskBrush _heroMaskBrush;

        public HeroSectionControl()
        {
            this.InitializeComponent();

            // Setup composition-based hero image with true alpha mask
            HeroImageHost.Loaded += (s, e) =>
            {
                if (_heroVisual == null)
                {
                    SetupHeroCompositionMask();
                }
                else
                {
                    // Re-attach existing visual if cached
                    ElementCompositionPreview.SetElementChildVisual(HeroImageHost, _heroVisual);
                }
            };
            
            this.Unloaded += HeroSectionControl_Unloaded;
        }

        private void HeroSectionControl_Unloaded(object sender, RoutedEventArgs e)
        {
            StopAutoRotation();
        }

        public void SetItems(IEnumerable<StremioMediaStream> items)
        {
            _heroItems = items.ToList();
            if (_heroItems.Count > 0)
            {
                _currentHeroIndex = 0;
                UpdateHeroSection(_heroItems[0]);
                StartHeroAutoRotation();
            }
        }

        private void SetupHeroCompositionMask()
        {
            var compositor = ElementCompositionPreview.GetElementVisual(HeroImageHost).Compositor;

            // 1. Image source brush
            _heroImageBrush = compositor.CreateSurfaceBrush();
            _heroImageBrush.Stretch = Microsoft.UI.Composition.CompositionStretch.UniformToFill;
            _heroImageBrush.HorizontalAlignmentRatio = 0.5f;
            _heroImageBrush.VerticalAlignmentRatio = 0.0f; // Top-aligned

            // 2. Alpha gradient mask
            _heroAlphaMask = compositor.CreateLinearGradientBrush();
            _heroAlphaMask.StartPoint = new Vector2(0, 0);
            _heroAlphaMask.EndPoint = new Vector2(0, 1);
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Windows.UI.Color.FromArgb(255, 255, 255, 255)));
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.40f, Windows.UI.Color.FromArgb(255, 255, 255, 255)));
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.65f, Windows.UI.Color.FromArgb(140, 255, 255, 255)));
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.85f, Windows.UI.Color.FromArgb(30, 255, 255, 255)));
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Windows.UI.Color.FromArgb(0, 255, 255, 255)));

            // 3. Mask brush
            _heroMaskBrush = compositor.CreateMaskBrush();
            _heroMaskBrush.Source = _heroImageBrush;
            _heroMaskBrush.Mask = _heroAlphaMask;

            // 4. SpriteVisual
            _heroVisual = compositor.CreateSpriteVisual();
            _heroVisual.Brush = _heroMaskBrush;
            _heroVisual.Size = new Vector2((float)HeroImageHost.ActualWidth, (float)HeroImageHost.ActualHeight);

            // 5. Attach
            ElementCompositionPreview.SetElementChildVisual(HeroImageHost, _heroVisual);

            // 6. Size sync
            HeroImageHost.SizeChanged += (s, e) =>
            {
                if (_heroVisual != null)
                    _heroVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            };

            // 7. Ken Burns
            ApplyKenBurnsComposition(compositor);
        }

        private void StopAutoRotation()
        {
            _heroAutoTimer?.Stop();
        }

        private void StartHeroAutoRotation()
        {
            _heroAutoTimer?.Stop();
            _heroAutoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _heroAutoTimer.Tick += (s, e) =>
            {
                if (_heroItems.Count > 1 && !_heroTransitioning && this.XamlRoot != null)
                {
                    _currentHeroIndex = (_currentHeroIndex + 1) % _heroItems.Count;
                    UpdateHeroSection(_heroItems[_currentHeroIndex], animate: true);
                }
            };
            _heroAutoTimer.Start();
        }

        private void ResetHeroAutoTimer()
        {
            _heroAutoTimer?.Stop();
            _heroAutoTimer?.Start();
        }

        public void SetLoading(bool isLoading)
        {
            if (isLoading)
            {
                HeroShimmer.Visibility = Visibility.Visible;
                HeroTextShimmer.Visibility = Visibility.Visible;
                HeroRealContent.Opacity = 0;
            }
            else
            {
                // We don't automatically hide here because UpdateHeroSection handles the transition
                // effectively when data arrives. But strictly speaking, if we wanted to force hide:
                // HeroShimmer.Visibility = Visibility.Collapsed;
                // HeroTextShimmer.Visibility = Visibility.Collapsed;
                // HeroRealContent.Opacity = 1;
            }
        }
        
        private async void UpdateHeroSection(StremioMediaStream item, bool animate = false)
        {
             // Transition from Shimmer to Real Content
            if (HeroShimmer.Visibility == Visibility.Visible)
            {
                HeroShimmer.Visibility = Visibility.Collapsed;
                HeroTextShimmer.Visibility = Visibility.Collapsed;
                HeroRealContent.Opacity = 1; 
                AnimateTextIn();
            }

            string imgUrl = item.Meta?.Background ?? item.PosterUrl;

            if (animate && _heroVisual != null && !_heroTransitioning)
            {
                _heroTransitioning = true;
                var compositor = _heroVisual.Compositor;

                // Phase 1: Fade out image
                var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f)));
                fadeOut.Duration = TimeSpan.FromMilliseconds(400);
                _heroVisual.StartAnimation("Opacity", fadeOut);

                AnimateTextOut();
                await Task.Delay(420);

                // Phase 2: Swap content
                PopulateHeroData(item);

                if (!string.IsNullOrEmpty(imgUrl))
                {
                    var surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(imgUrl));
                    _heroImageBrush.Surface = surface;
                }

                // Phase 3: Fade in image
                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.2f, 1f)));
                fadeIn.Duration = TimeSpan.FromMilliseconds(600);
                _heroVisual.StartAnimation("Opacity", fadeIn);

                AnimateTextIn();

                // Phase 4: Bleed colors
                if (!string.IsNullOrEmpty(imgUrl))
                {
                    var colors = await ImageHelper.GetOrExtractColorAsync(imgUrl);
                    if (colors.HasValue)
                    {
                        ColorExtracted?.Invoke(this, colors.Value);
                    }
                }

                _heroTransitioning = false;
            }
            else
            {
                // No animation (first load / shimmer exit)
                PopulateHeroData(item);

                if (!string.IsNullOrEmpty(imgUrl))
                {
                    var surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(imgUrl));
                    if (_heroImageBrush != null)
                        _heroImageBrush.Surface = surface;

                    var colors = await ImageHelper.GetOrExtractColorAsync(imgUrl);
                    if (colors.HasValue)
                    {
                        ColorExtracted?.Invoke(this, colors.Value);
                    }
                }
            }
        }

        private void PopulateHeroData(StremioMediaStream item)
        {
            HeroTitle.Text = item.Title;
            HeroOverview.Text = item.Meta?.Description ?? "Sinematik bir serüven sizi bekliyor.";
            HeroYear.Text = item.Meta?.ReleaseInfo ?? "";
            HeroGenres.Text = (item.Meta?.Genres != null && item.Meta.Genres.Count > 0) ? string.Join(", ", item.Meta.Genres.Take(2)) : "";
            HeroRating.Text = item.Meta?.ImdbRating != null ? $"{item.Meta.ImdbRating} ★" : "";

            HeroYearDot.Visibility = (!string.IsNullOrEmpty(HeroYear.Text) && !string.IsNullOrEmpty(HeroGenres.Text)) ? Visibility.Visible : Visibility.Collapsed;
            HeroRatingDot.Visibility = (!string.IsNullOrEmpty(HeroGenres.Text) && !string.IsNullOrEmpty(HeroRating.Text)) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AnimateTextOut()
        {
            var sb = new Storyboard();
            var fadeOut = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            var slideOut = new DoubleAnimation { To = -30, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(fadeOut, HeroContentPanel);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            Storyboard.SetTarget(slideOut, HeroContentPanel);
            Storyboard.SetTargetProperty(slideOut, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
            sb.Children.Add(fadeOut);
            sb.Children.Add(slideOut);
            HeroContentPanel.RenderTransform = new CompositeTransform();
            sb.Begin();
        }

        private void AnimateTextIn()
        {
            var ct = new CompositeTransform { TranslateY = 30 };
            HeroContentPanel.RenderTransform = ct;
            HeroContentPanel.Opacity = 0;

            var sb = new Storyboard();
            var fadeIn = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var slideIn = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(fadeIn, HeroContentPanel);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            Storyboard.SetTarget(slideIn, HeroContentPanel);
            Storyboard.SetTargetProperty(slideIn, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
            sb.Children.Add(fadeIn);
            sb.Children.Add(slideIn);
            sb.Begin();
        }

        private void ApplyKenBurnsComposition(Microsoft.UI.Composition.Compositor compositor)
        {
            var hostVisual = ElementCompositionPreview.GetElementVisual(HeroImageHost);
            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f));
            offsetAnim.InsertKeyFrame(0f, Vector3.Zero, easing);
            offsetAnim.InsertKeyFrame(0.5f, new Vector3(-12f, -4f, 0f), easing);
            offsetAnim.InsertKeyFrame(1f, Vector3.Zero, easing);
            offsetAnim.Duration = TimeSpan.FromSeconds(25);
            offsetAnim.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            hostVisual.StartAnimation("Offset", offsetAnim);
        }

        private void HeroPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItems.Count > _currentHeroIndex)
            {
                PlayAction?.Invoke(this, _heroItems[_currentHeroIndex]);
            }
        }

        private void HeroDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItems.Count > _currentHeroIndex)
            {
                DetailsAction?.Invoke(this, _heroItems[_currentHeroIndex]);
            }
        }

        private void HeroNext_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItems.Count == 0 || _heroTransitioning) return;
            _currentHeroIndex = (_currentHeroIndex + 1) % _heroItems.Count;
            UpdateHeroSection(_heroItems[_currentHeroIndex], animate: true);
            ResetHeroAutoTimer();
        }

        private void HeroPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItems.Count == 0 || _heroTransitioning) return;
            _currentHeroIndex--;
            if (_currentHeroIndex < 0) _currentHeroIndex = _heroItems.Count - 1;
            UpdateHeroSection(_heroItems[_currentHeroIndex], animate: true);
            ResetHeroAutoTimer();
        }
    }
}
