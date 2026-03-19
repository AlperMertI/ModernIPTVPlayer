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

        // Use a private field to store the BitmapImage for navigation since LoadedImageSurface is not an ImageSource
        private ImageSource _currentLogoSource;
        public ImageSource LogoSource => _currentLogoSource;

        // Data
        private List<StremioMediaStream> _heroItems = new();
        private int _currentHeroIndex = 0;
        private string _currentHeroId = string.Empty;
        private bool _heroTransitioning = false;
        private DispatcherTimer _heroAutoTimer;

        // Composition API
        private Microsoft.UI.Composition.SpriteVisual _heroVisual;
        private Microsoft.UI.Composition.CompositionSurfaceBrush _heroImageBrush;
        private Microsoft.UI.Composition.CompositionLinearGradientBrush _heroAlphaMask;
        private Microsoft.UI.Composition.CompositionMaskBrush _heroMaskBrush;

        private Microsoft.UI.Composition.SpriteVisual _heroLogoVisual;
        private Microsoft.UI.Composition.CompositionSurfaceBrush _heroLogoBrush;
        private Dictionary<string, Microsoft.UI.Xaml.Media.LoadedImageSurface> _heroLogoSurfaces = new();

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
                    ElementCompositionPreview.SetElementChildVisual(HeroImageHost, _heroVisual);
                }
            };

            HeroLogoHost.Loaded += (s, e) =>
            {
                if (_heroLogoVisual != null)
                {
                    ElementCompositionPreview.SetElementChildVisual(HeroLogoHost, _heroLogoVisual);
                }
            };
            
            this.Unloaded += HeroSectionControl_Unloaded;
        }

        private void HeroSectionControl_Unloaded(object sender, RoutedEventArgs e)
        {
            StopAutoRotation();
            if (_lastSubscribedItem != null)
            {
                _lastSubscribedItem.PropertyChanged -= Item_PropertyChanged;
                _lastSubscribedItem = null;
            }
        }

        public void SetItems(IEnumerable<StremioMediaStream> items)
        {
            var newItems = items.ToList();
            if (newItems.Count == 0) return;

            // Check if the top hero item is basically the same.
            // If so, we just update the background list and do NOT trigger a visual transition.
            string newTopId = newItems[0].IMDbId ?? newItems[0].Id.ToString();
            
            if (_heroItems.Count > 0 && newTopId == _currentHeroId && !HeroShimmer.Visibility.Equals(Visibility.Visible))
            {
                // Items match visually, just quietly update the rotation list behind the scenes
                // We must preserve the currently active item's index in the NEW list to not break rotation
                int newIndex = newItems.FindIndex(x => (x.IMDbId ?? x.Id.ToString()) == _currentHeroId);
                
                _heroItems = newItems;
                _currentHeroIndex = newIndex >= 0 ? newIndex : 0;
                
                // Ensure timer is running if we now have more items
                if (_heroItems.Count > 1 && (_heroAutoTimer == null || !_heroAutoTimer.IsEnabled))
                {
                    StartHeroAutoRotation();
                }
                return;
            }

            // A genuinely new top item arrived (or first load)
            _heroItems = newItems;
            
            _currentHeroIndex = 0;
            
            // 1. Clear and dispose old surfaces to avoid GPU leaks (prevents 0xc000027b)
            ClearSurfaces();

            // 2. Pre-load only the first few to avoid overwhelming the engine on startup
            for (int i = 0; i < Math.Min(3, _heroItems.Count); i++)
            {
                PreloadSurface(_heroItems[i].LogoUrl);
            }

            // 3. Initial Reveal: Update immediately so content is ready before shimmer is hidden
            UpdateHeroSection(_heroItems[0]);

            StartHeroAutoRotation();
        }

        private void ClearSurfaces()
        {
            try
            {
                foreach (var surface in _heroLogoSurfaces.Values)
                {
                    surface?.Dispose();
                }
                _heroLogoSurfaces.Clear();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Hero] Surface Disposal Error: {ex.Message}");
            }
        }

        private void PreloadSurface(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (_heroLogoSurfaces.ContainsKey(url)) return;

            try
            {
                var surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(url));
                
                // If it's the first load and we are waiting, listen for completion
                if (_isFirstLoad && !_firstLogoReadyTcs.Task.IsCompleted && _heroItems != null && _heroItems.Count > 0 && url == _heroItems[0].LogoUrl)
                {
                    surface.LoadCompleted += (s, e) => {
                        _firstLogoReadyTcs.TrySetResult(true);
                    };
                }

                _heroLogoSurfaces[url] = surface;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Hero] Preload Error for {url}: {ex.Message}");
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
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.75f, Windows.UI.Color.FromArgb(255, 255, 255, 255)));
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.90f, Windows.UI.Color.FromArgb(100, 255, 255, 255)));
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

            // 8. Logo Visual Setup
            _heroLogoBrush = compositor.CreateSurfaceBrush();
            _heroLogoBrush.Stretch = Microsoft.UI.Composition.CompositionStretch.Uniform;
            _heroLogoBrush.HorizontalAlignmentRatio = 0f; // Left aligned
            
            _heroLogoVisual = compositor.CreateSpriteVisual();
            _heroLogoVisual.Brush = _heroLogoBrush;
            
            // Use explicit size if possible, otherwise fallback
            float lWidth = (float)HeroLogoHost.Width;
            float lHeight = (float)HeroLogoHost.Height;
            if (double.IsNaN(lWidth) || lWidth <= 0) lWidth = (float)HeroLogoHost.ActualWidth;
            if (double.IsNaN(lHeight) || lHeight <= 0) lHeight = (float)HeroLogoHost.ActualHeight;
            if (lWidth <= 0) lWidth = 500f;
            if (lHeight <= 0) lHeight = 100f;
            
            _heroLogoVisual.Size = new Vector2(lWidth, lHeight);
            
            ElementCompositionPreview.SetElementChildVisual(HeroLogoHost, _heroLogoVisual);
            
            HeroLogoHost.SizeChanged += (s, e) =>
            {
                if (_heroLogoVisual != null)
                    _heroLogoVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            };
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

            // Smart Pre-load: Warm up next and previous item surfaces
            if (_heroItems != null && _heroItems.Count > 1)
            {
                int next = (_currentHeroIndex + 1) % _heroItems.Count;
                int prev = (_currentHeroIndex - 1 + _heroItems.Count) % _heroItems.Count;
                PreloadSurface(_heroItems[next].LogoUrl);
                PreloadSurface(_heroItems[prev].LogoUrl);
            }
        }

        private bool _isFirstLoad = true;
        private TaskCompletionSource<bool> _firstLogoReadyTcs = new TaskCompletionSource<bool>();
        private TaskCompletionSource<bool> _firstBackgroundReadyTcs = new TaskCompletionSource<bool>();
        private bool _pendingSetLoadingFalse = false;

        public void SetLoading(bool isLoading)
        {
            if (isLoading)
            {
                HeroShimmer.Visibility = Visibility.Visible;
                HeroTextShimmer.Visibility = Visibility.Visible;
                HeroRealContent.Opacity = 0;
                HeroRealContent.Visibility = Visibility.Collapsed;
                if (_heroVisual != null) _heroVisual.Opacity = 0; // [FIX] Prepare for fade-in
                _isFirstLoad = true;
                _firstLogoReadyTcs = new TaskCompletionSource<bool>();
                _firstBackgroundReadyTcs = new TaskCompletionSource<bool>();
            }
            else
            {
                if (_isFirstLoad && (!_firstLogoReadyTcs.Task.IsCompleted || !_firstBackgroundReadyTcs.Task.IsCompleted))
                {
                    // Delay reveal until both primary surfaces are truly locked & ready
                    _pendingSetLoadingFalse = true;
                    
                    // Safety timeout (2.5 seconds) to avoid getting stuck if a CDN is slow
                    _ = Task.Delay(2500).ContinueWith(_ => {
                        if (!_firstLogoReadyTcs.Task.IsCompleted) _firstLogoReadyTcs.TrySetResult(false);
                        if (!_firstBackgroundReadyTcs.Task.IsCompleted) _firstBackgroundReadyTcs.TrySetResult(false);
                    }, TaskScheduler.FromCurrentSynchronizationContext());

                    Task.WhenAll(_firstLogoReadyTcs.Task, _firstBackgroundReadyTcs.Task).ContinueWith(t => {
                        DispatcherQueue.TryEnqueue(() => {
                            if (_pendingSetLoadingFalse)
                            {
                                 _pendingSetLoadingFalse = false;
                                 ShowRealContent();
                            }
                        });
                    });
                    return;
                }

                ShowRealContent();
            }
        }

        private void ShowRealContent()
        {
            if (HeroShimmer.Visibility == Visibility.Collapsed) return;

            HeroShimmer.Visibility = Visibility.Collapsed;
            HeroTextShimmer.Visibility = Visibility.Collapsed;
            HeroRealContent.Visibility = Visibility.Visible;
            HeroRealContent.Opacity = 1; // [FIX] Ensure inner content is opaque
            
            // [FIX] Reveal metadata with animation
            AnimateTextIn();

            // [FIX] Reveal background image with smooth fade-in
            if (_heroVisual != null)
            {
                var compositor = _heroVisual.Compositor;
                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(1f, 1f);
                fadeIn.Duration = TimeSpan.FromMilliseconds(800);
                _heroVisual.StartAnimation("Opacity", fadeIn);
            }
            
            _isFirstLoad = false;
        }
        
        private async void UpdateHeroSection(StremioMediaStream item, bool animate = false)
        {
             // Transition from Shimmer to Real Content is now handled by ShowRealContent()
             // which is triggered after primary surfaces (Background + Logo) are synchronized and ready.
             // This prevents staggered arrival.

            string imgUrl = item.Meta?.Background ?? item.PosterUrl;
            string itemId = item.IMDbId ?? item.Id.ToString();

            // Prevent redundant assignments and flickering
            if (_currentHeroId == itemId && !HeroShimmer.Visibility.Equals(Visibility.Visible))
            {
                // We're already showing this item. No need to reload surface/extract colors again.
                return;
            }

            _currentHeroId = itemId;

            // Trigger color extraction via hidden image
            if (!string.IsNullOrEmpty(imgUrl))
            {
                // Tiny decode for extraction only - satisfies technical need for Image control
                // while keeping memory footprint negligible (kb vs mb).
                var bitmap = new BitmapImage();
                bitmap.DecodePixelWidth = 50; 
                bitmap.UriSource = new Uri(imgUrl);
                ColorExtractionImage.Source = bitmap;
            }

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
                await Task.Delay(300); // Wait for fade-out to complete
                
                // Phase 2: Swap content (while invisible)
                PopulateHeroData(item);
                SubscribeToItemChanges(item);

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

                // Phase 4: Bleed colors (Now handled by ColorExtractionImage_ImageOpened)

                _heroTransitioning = false;
            }
            else
            {
                // No animation (first load / shimmer exit)
                PopulateHeroData(item);
                SubscribeToItemChanges(item);

                if (!string.IsNullOrEmpty(imgUrl))
                {
                    try
                    {
                        var surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(imgUrl));
                        if (_isFirstLoad && !_firstBackgroundReadyTcs.Task.IsCompleted)
                        {
                            surface.LoadCompleted += (s, e) => {
                                _firstBackgroundReadyTcs.TrySetResult(e.Status == Microsoft.UI.Xaml.Media.LoadedImageSourceLoadStatus.Success);
                            };
                        }
                        if (_heroImageBrush != null)
                            _heroImageBrush.Surface = surface;
                    }
                    catch { _firstBackgroundReadyTcs.TrySetResult(false); }
                }
                else
                {
                    _firstBackgroundReadyTcs.TrySetResult(true);
                }
            }
        }

        private async void ColorExtractionImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            try
            {
                // Ensure the element is ready for rendering
                if (ColorExtractionImage.ActualWidth == 0 || ColorExtractionImage.XamlRoot == null)
                {
                    await Task.Delay(50); // Give layout a moment
                }
                
                if (ColorExtractionImage.ActualWidth == 0 || ColorExtractionImage.XamlRoot == null) return;

                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(ColorExtractionImage);
                var pixelBuffer = await rtb.GetPixelsAsync();
                var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixelBuffer);

                // Use the image URL as cache key if possible, or just "Hero"
                string cacheKey = (ColorExtractionImage.Source as BitmapImage)?.UriSource?.ToString() ?? "Hero";
                var colors = ImageHelper.ExtractColorsFromPixels(pixels, rtb.PixelWidth, rtb.PixelHeight, cacheKey);
                
                ColorExtracted?.Invoke(this, colors);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HeroControl] Color extraction failed: {ex.Message}");
            }
        }

        private StremioMediaStream _lastSubscribedItem = null;

        private void SubscribeToItemChanges(StremioMediaStream item)
        {
            if (_lastSubscribedItem != null)
                _lastSubscribedItem.PropertyChanged -= Item_PropertyChanged;
            
            _lastSubscribedItem = item;
            if (_lastSubscribedItem != null)
                _lastSubscribedItem.PropertyChanged += Item_PropertyChanged;
        }

        private void Item_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is StremioMediaStream item)
            {
                DispatcherQueue.TryEnqueue(() => 
                {
                    // Refresh fields if they might have been enriched
                    if (e.PropertyName == nameof(item.Description) || e.PropertyName == nameof(item.Rating) || 
                        e.PropertyName == nameof(item.Year) || e.PropertyName == nameof(item.Genres) ||
                        e.PropertyName == nameof(item.LogoUrl))
                    {
                        PopulateHeroData(item);
                    }
                    
                    if (e.PropertyName == nameof(item.Banner) || e.PropertyName == nameof(item.LandscapeImageUrl))
                    {
                        string newImgUrl = item.Meta?.Background ?? item.PosterUrl;
                        if (!string.IsNullOrEmpty(newImgUrl) && _heroImageBrush != null)
                        {
                            var surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new System.Uri(newImgUrl));
                            _heroImageBrush.Surface = surface;
                        }
                    }
                });
            }
        }

        private void PopulateHeroData(StremioMediaStream item)
        {
            if (!string.IsNullOrEmpty(item.LogoUrl))
            {
                // Instant load via Composition Surface (check cache first)
                if (_heroLogoBrush != null)
                {
                    try
                    {
                        Microsoft.UI.Xaml.Media.LoadedImageSurface surface = null;
                        if (!_heroLogoSurfaces.TryGetValue(item.LogoUrl, out surface))
                        {
                            // Fallback if not pre-warmed for some reason
                            surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(item.LogoUrl));
                            _heroLogoSurfaces[item.LogoUrl] = surface;
                        }

                        _heroLogoBrush.Surface = surface;
                        
                        // Ensure visual size is updated in case host layout is still zero
                        if (_heroLogoVisual != null && (_heroLogoVisual.Size.X <= 0 || _heroLogoVisual.Size.Y <= 0))
                        {
                            _heroLogoVisual.Size = new Vector2(500, 100);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[HeroControl] Logo surface load failed: {ex.Message}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[HeroControl] _heroLogoBrush is NULL!");
                }

                // Keep BitmapImage for navigation (details page)
                _currentLogoSource = ImageHelper.GetCachedLogo(item.LogoUrl);

                HeroLogoHost.Visibility = Visibility.Visible;
                HeroLogoHost.Opacity = 1; // Ensure it's opaque
                HeroTitle.Visibility = Visibility.Collapsed;
            }
            else
            {
                _currentLogoSource = null;
                HeroLogoHost.Visibility = Visibility.Collapsed;
                HeroTitle.Visibility = Visibility.Visible;
                HeroTitle.Text = item.Title;
            }

            HeroOverview.Text = !string.IsNullOrEmpty(item.Description) ? item.Description : "Sinematik bir serüven sizi bekliyor.";
            HeroYear.Text = item.Year ?? "";
            
            if (!string.IsNullOrEmpty(item.Genres))
            {
                HeroGenres.Text = item.Genres;
                HeroYearDot.Visibility = Visibility.Visible;
            }
            else HeroYearDot.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrEmpty(item.Rating) && item.Rating != "0" && item.Rating != "0.0")
            {
                HeroRating.Text = item.Rating;
                HeroRatingDot.Visibility = Visibility.Visible;
            }
            else HeroRatingDot.Visibility = Visibility.Collapsed;

            // Update visible indicators
            if (string.IsNullOrEmpty(HeroOverview.Text)) HeroOverview.Visibility = Visibility.Collapsed;
            else HeroOverview.Visibility = Visibility.Visible;
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
