using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
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
        public event EventHandler<(Windows.UI.Color Primary, Windows.UI.Color Secondary)> ColorExtracted;

        // Use a private field to store the BitmapImage for navigation since LoadedImageSurface is not an ImageSource
        private ImageSource _currentLogoSource;
        public ImageSource LogoSource => _currentLogoSource;

        private string? _currentBgUrl;
        private string? _currentLogoUrl;
        private string? _currentHeroId;
        private bool _heroTransitioning = false;
        private DispatcherTimer _heroAutoTimer;

        // Data
        private List<StremioMediaStream> _heroItems = new();
        private int _currentHeroIndex = 0;

        // Composition API
        private Microsoft.UI.Composition.SpriteVisual _heroVisual;
        private Microsoft.UI.Composition.CompositionSurfaceBrush _heroImageBrush;
        private Microsoft.UI.Composition.CompositionLinearGradientBrush _heroAlphaMask;
        private Microsoft.UI.Composition.CompositionMaskBrush _heroMaskBrush;

        private Microsoft.UI.Composition.SpriteVisual _heroLogoVisual;
        private Microsoft.UI.Composition.CompositionSurfaceBrush _heroLogoBrush;
        private Dictionary<string, Microsoft.UI.Xaml.Media.LoadedImageSurface> _heroLogoSurfaces = new();
        private System.Threading.CancellationTokenSource? _heroCts;

        // Trailer state
        private Microsoft.UI.Xaml.Controls.WebView2? _webView;
        private bool _isTrailerPlaying = false;
        private string _instanceId = Guid.NewGuid().ToString("N");
        private string? _pendingTrailerId = null;
        private TaskCompletionSource _compositionReadyTcs = new();

        public HeroSectionControl()
        {
            this.InitializeComponent();

            // Initial state
            HeroRealContent.Visibility = Visibility.Collapsed;
            HeroRealContent.Opacity = 0;

            // Pre-initialize WebView2 for fast trailer playback on load
            this.Loaded += (s, e) => _ = PreInitializeWebViewAsync();

            // Setup composition-based hero image with true alpha mask
            HeroImageHost.Loaded += (s, e) =>
            {
                SetupHeroCompositionMask();
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
            _heroCts?.Cancel();
            _heroCts = null;
            StopAutoRotation();
            CleanupWebView();
            if (_lastSubscribedItem != null)
            {
                _lastSubscribedItem.PropertyChanged -= Item_PropertyChanged;
                _lastSubscribedItem = null;
            }
        }

        // Infrastructure
        private void SetText(TextBlock control, string? value) { if (control.Text != (value ?? "")) control.Text = value ?? ""; }
        private void SetVisibility(FrameworkElement control, Visibility visibility) { if (control.Visibility != visibility) control.Visibility = visibility; }
        private void SetOpacity(FrameworkElement control, double opacity) { if (Math.Abs(control.Opacity - opacity) > 0.01) control.Opacity = opacity; }
        private void SetGlyph(FontIcon control, string glyph) { if (control.Glyph != glyph) control.Glyph = glyph; }

        public void SetItems(IEnumerable<StremioMediaStream> items, bool animate = false)
        {
            if (items == null) return;
            var newItems = items.ToList();
            if (newItems.Count == 0) return;

            // Check if the top hero item is basically the same.
            // If so, we just update the background list and do NOT trigger a visual transition.
            string newTopId = newItems[0].IMDbId ?? newItems[0].Id.ToString();
            
            bool isInvisible = _heroVisual != null && _heroVisual.Opacity <= 0.01f;
            // [FIX] Allow shortcut even if shimmer is visible, to prevent "stationary flicker" 
            // when multiple chunks of metadata arrive for the same top item.
            if (_heroItems.Count > 0 && newTopId == _currentHeroId && !isInvisible)
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
            for (int i = 0; i < Math.Min(5, _heroItems.Count); i++)
            {
                PreloadSurface(_heroItems[i].LogoUrl);

                // [PERFORMANCE] Pre-fetch colors in the background
                // Use the Backdrop Image (imgUrl) for maximum color accuracy as requested
                string? imgUrl = _heroItems[i].Meta?.Background ?? _heroItems[i].PosterUrl;
                if (!string.IsNullOrEmpty(imgUrl))
                    _ = ImageHelper.GetOrExtractColorAsync(imgUrl);
            }

            // 3. Initial Reveal: Update with requested animation state
            UpdateHeroSection(_heroItems[0], animate: animate);

            UpdateNavigationVisibility();
            StartHeroAutoRotation();
        }

        private void UpdateNavigationVisibility()
        {
            var vis = (_heroItems.Count > 1 && !_isTrailerPlaying) ? Visibility.Visible : Visibility.Collapsed;
            HeroPrevButton.Visibility = vis;
            HeroNextButton.Visibility = vis;
        }

        private void ClearSurfaces()
        {
            try
            {
                // [FIX] Avoid disposing the CURRENTLY VISIBLE logo to prevent stationary flicker
                // during metadata enrichment chunks.
                var toRemove = _heroLogoSurfaces.Keys.Where(k => k != _currentLogoUrl).ToList();
                foreach (var url in toRemove)
                {
                    if (_heroLogoSurfaces.TryGetValue(url, out var surface))
                    {
                        surface?.Dispose();
                        _heroLogoSurfaces.Remove(url);
                    }
                }
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
            try
            {
                if (HeroImageHost.XamlRoot == null) return;
                
                // If already initialized, just ensure it's still attached (on re-entry)
                if (_heroVisual != null)
                {
                    ElementCompositionPreview.SetElementChildVisual(HeroImageHost, _heroVisual);
                    _compositionReadyTcs.TrySetResult();
                    return;
                }

                var hostVisual = ElementCompositionPreview.GetElementVisual(HeroImageHost);
                if (hostVisual == null) return;
                var compositor = hostVisual.Compositor;

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
            
            // [FIX] Sync opacity with current state (visible if search is done)
            _heroVisual.Opacity = _isFirstLoad ? 0f : 1f;

            // 5. Attach
            ElementCompositionPreview.SetElementChildVisual(HeroImageHost, _heroVisual);

            // [FIX] Required for stable Offset/Translation animations
            ElementCompositionPreview.SetIsTranslationEnabled(HeroImageHost, true);

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
            _compositionReadyTcs.TrySetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HeroControl] Setup Composition Error: {ex.Message}");
        }
    }

        public void StopAutoRotation()
        {
            AppLogger.Info("[HeroControl] StopAutoRotation called");
            if (_heroAutoTimer != null)
            {
                _heroAutoTimer.Stop();
                _heroAutoTimer = null; // [FIX] Forcefully neutralize to prevent any rogue ticks
                
                // [FIX] Only cleanup if a trailer was actually active to prevent log spam during auto-rotation
                if (_isTrailerPlaying || _pendingTrailerId != null) CleanupWebView();
            }
        }

        public void StartHeroAutoRotation()
        {
            StopAutoRotation(); // [FIX] Ensure old one is truly GC'd before starting new
            _heroAutoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _heroAutoTimer.Tick += (s, e) =>
            {
                // [FIX] Global Visibility Check: Stop rotation if the control is collapsed (IPTV mode)
                // [NEW] Skip rotation if a trailer is playing or loading
                if (this.Visibility != Visibility.Visible || this.ActualWidth <= 0 || this.XamlRoot == null || _isTrailerPlaying || _pendingTrailerId != null)
                    return;

                if (_heroItems.Count > 1 && !_heroTransitioning)
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
                
                // [ROBUST] Purge stale visual data immediately
                if (_heroVisual != null) _heroVisual.Opacity = 0;
                if (_heroImageBrush != null) _heroImageBrush.Surface = null;
                if (_heroLogoBrush != null) _heroLogoBrush.Surface = null;

                // [ROBUST] Cancel any pending transitions IMMEDIATELY
                _heroCts?.Cancel();
                _heroCts = null;

                _currentHeroId = null;
                _currentBgUrl = null;
                _currentLogoUrl = null;
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
                    // Use TaskScheduler.Default to avoid SynchronizationContext issues
                    _ = Task.Delay(2500).ContinueWith(_ => {
                        if (!_firstLogoReadyTcs.Task.IsCompleted) _firstLogoReadyTcs.TrySetResult(false);
                        if (!_firstBackgroundReadyTcs.Task.IsCompleted) _firstBackgroundReadyTcs.TrySetResult(false);
                    }, TaskScheduler.Default);

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

            // Step 1: Hide Shimmers
            SetVisibility(HeroShimmer, Visibility.Collapsed);
            SetVisibility(HeroTextShimmer, Visibility.Collapsed);
            
            // Step 2: Prepare Content (Sync with Parent Animation)
            SetVisibility(HeroRealContent, Visibility.Visible);
            HeroRealContent.Opacity = 1.0; // Child is ready, parent HeroContentPanel will handle the fade
            
            // Step 3: Synchronized Entrance (1.5s Cinematic)
            AnimateTextIn();

            if (_heroVisual != null)
            {
                _heroVisual.Opacity = 0f;
                var compositor = _heroVisual.Compositor;
                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                // 1.5s matching the text
                fadeIn.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.2f, 1f)));
                fadeIn.Duration = TimeSpan.FromMilliseconds(1500);
                _heroVisual.StartAnimation("Opacity", fadeIn);
            }
            
            _isFirstLoad = false;
        }
        
        private async void UpdateHeroSection(StremioMediaStream item, bool animate = false)
        {
            string itemId = item.IMDbId ?? item.Id.ToString();
            string imgUrl = item.Meta?.Background ?? item.PosterUrl;

            // [ROBUST] If already showing this item and visibly active, skip
            if (_currentHeroId == itemId && HeroRealContent.Visibility == Visibility.Visible && !_heroTransitioning)
                return;

            _currentHeroId = itemId;

            // [ROBUST] Cleanup trailer when content changes
            if (_isTrailerPlaying || _pendingTrailerId != null) CleanupWebView();

            _heroCts?.Cancel();
            _heroCts = new System.Threading.CancellationTokenSource();
            var token = _heroCts.Token;

            // [ROBUST] Trigger Color Extraction immediately (Background)
            if (!string.IsNullOrEmpty(imgUrl))
            {
                _ = Task.Run(async () =>
                {
                    var colors = await ImageHelper.GetOrExtractColorAsync(imgUrl);
                    if (colors != null && !token.IsCancellationRequested)
                    {
                        DispatcherQueue.TryEnqueue(() => ColorExtracted?.Invoke(this, colors.Value));
                    }
                });
            }

            try
            {
                // [ROBUST] Wait for composition layer before ANY visual update
                if (_heroVisual == null) SetupHeroCompositionMask();
                await _compositionReadyTcs.Task;

                if (animate && !_heroTransitioning)
                {
                    _heroTransitioning = true;
                    var compositor = _heroVisual.Compositor;

                    // Fade Out
                    var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                    fadeOut.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f)));
                    fadeOut.Duration = TimeSpan.FromMilliseconds(300);
                    _heroVisual.StartAnimation("Opacity", fadeOut);
                    AnimateTextOut();

                    await Task.Delay(250, token);
                    if (token.IsCancellationRequested) return;

                    PopulateHeroData(item);
                    SubscribeToItemChanges(item);
                    await ApplyBackdropAsync(imgUrl, token);

                    if (token.IsCancellationRequested) return;

                    // Fade In
                    var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.2f, 1f)));
                    fadeIn.Duration = TimeSpan.FromMilliseconds(500);
                    _heroVisual.StartAnimation("Opacity", fadeIn);
                    AnimateTextIn();
                }
                else
                {
                    // Non-animated (Startup/Fallback)
                    PopulateHeroData(item);
                    SubscribeToItemChanges(item);
                    await ApplyBackdropAsync(imgUrl, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Hero] Update Error: {ex.Message}"); }
            finally { _heroTransitioning = false; }
        }

        private async Task ApplyBackdropAsync(string? url, System.Threading.CancellationToken token)
        {
            if (string.IsNullOrEmpty(url))
            {
                _currentBgUrl = null;
                _firstBackgroundReadyTcs.TrySetResult(true);
                return;
            }

            if (url == _currentBgUrl && _heroImageBrush?.Surface != null) return;

            _currentBgUrl = url;
            var tcs = new TaskCompletionSource<bool>();
            var surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(url));
            
            TypedEventHandler<Microsoft.UI.Xaml.Media.LoadedImageSurface, Microsoft.UI.Xaml.Media.LoadedImageSourceLoadCompletedEventArgs> handler = null;
            handler = (s, ev) => {
                surface.LoadCompleted -= handler;
                tcs.TrySetResult(ev.Status == Microsoft.UI.Xaml.Media.LoadedImageSourceLoadStatus.Success);
            };
            surface.LoadCompleted += handler;

            // Wait for load (800ms max for transitions, longer for first load)
            int timeout = _isFirstLoad ? 3000 : 800;
            await Task.WhenAny(tcs.Task, Task.Delay(timeout, token));

            if (token.IsCancellationRequested) return;

            // [ROBUST] Final brush assignment
            if (_heroImageBrush != null) _heroImageBrush.Surface = surface;
            
            if (_isFirstLoad) _firstBackgroundReadyTcs.TrySetResult(true);
        }

        private StremioMediaStream? _lastSubscribedItem = null;

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
                    // 1. Metadata Enrichment (Logo, Text, etc.)
                    if (e.PropertyName == nameof(item.Description) || e.PropertyName == nameof(item.Rating) || 
                        e.PropertyName == nameof(item.Year) || e.PropertyName == nameof(item.Genres) ||
                        e.PropertyName == nameof(item.LogoUrl))
                    {
                        PopulateHeroData(item);
                    }
                    
                    // 2. Backdrop Enrichment (Banner, Landscape, Meta)
                    if (e.PropertyName == nameof(item.Banner) || e.PropertyName == nameof(item.LandscapeImageUrl) || e.PropertyName == "Meta")
                    {
                        string newImgUrl = item.Meta?.Background ?? item.PosterUrl;
                        if (!string.IsNullOrEmpty(newImgUrl) && newImgUrl != _currentBgUrl)
                        {
                            // Trigger a non-animated update to the new backdrop
                            // We use a dedicated CTS for property-change reloads to avoid killing main transitions
                            _ = ApplyBackdropAsync(newImgUrl, System.Threading.CancellationToken.None);
                        }
                    }
                });
            }
        }

        private void PopulateHeroData(StremioMediaStream item)
        {
            // 1. Logo or Title
            if (!string.IsNullOrEmpty(item.LogoUrl))
            {
                if (item.LogoUrl != _currentLogoUrl)
                {
                    _currentLogoUrl = item.LogoUrl;
                    if (_heroLogoBrush != null)
                    {
                        try
                        {
                            if (!_heroLogoSurfaces.TryGetValue(item.LogoUrl, out var surface))
                            {
                                surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(item.LogoUrl));
                                _heroLogoSurfaces[item.LogoUrl] = surface;
                            }
                            _heroLogoBrush.Surface = surface;
                        }
                        catch { }
                    }
                    _currentLogoSource = ImageHelper.GetCachedLogo(item.LogoUrl);
                }

                SetVisibility(HeroLogoHost, Visibility.Visible);
                SetOpacity(HeroLogoHost, 1.0);
                SetVisibility(HeroTitle, Visibility.Collapsed);
            }
            else
            {
                _currentLogoUrl = null;
                _currentLogoSource = null;
                SetVisibility(HeroLogoHost, Visibility.Collapsed);
                SetVisibility(HeroTitle, Visibility.Visible);
                SetText(HeroTitle, item.Title);
            }

            // 2. Metadata (Year, Rating, Genres)
            SetText(HeroYear, item.Year);
            SetText(HeroRating, item.Rating);
            SetText(HeroOverview, !string.IsNullOrEmpty(item.Description) ? item.Description : "Sinematik bir serüven sizi bekliyor.");
            
            SetVisibility(HeroOverview, string.IsNullOrEmpty(HeroOverview.Text) ? Visibility.Collapsed : Visibility.Visible);

            bool hasRating = !string.IsNullOrEmpty(item.Rating) && item.Rating != "0" && item.Rating != "0.0";
            SetVisibility(HeroRating, hasRating ? Visibility.Visible : Visibility.Collapsed);
            SetVisibility(HeroRatingDot, hasRating ? Visibility.Visible : Visibility.Collapsed);

            bool hasGenres = !string.IsNullOrEmpty(item.Genres);
            SetText(HeroGenres, item.Genres);
            SetVisibility(HeroGenres, hasGenres ? Visibility.Visible : Visibility.Collapsed);
            SetVisibility(HeroYearDot, hasGenres ? Visibility.Visible : Visibility.Collapsed);

            // 3. Trailer State
            bool hasTrailer = !string.IsNullOrEmpty(item.TrailerUrl) || (item.Meta?.Trailers != null && item.Meta.Trailers.Any(t => !string.IsNullOrEmpty(t.Source)));
            SetVisibility(HeroTrailerButton, hasTrailer ? Visibility.Visible : Visibility.Collapsed);
            SetGlyph(HeroTrailerIcon, "\uE714");
            SetText(HeroTrailerText, "Fragmanı İzle");
        }


        private void AnimateTextOut()
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(HeroContentPanel);
                if (visual != null)
                {
                    var compositor = visual.Compositor;
                    var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

                    // 1. Opacity Animation (300ms)
                    var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                    fadeOut.InsertKeyFrame(0f, visual.Opacity, easing); // Start from current
                    fadeOut.InsertKeyFrame(1f, 0f, easing);
                    fadeOut.Duration = TimeSpan.FromMilliseconds(300);
                    
                    // 2. Translation Animation (300ms - Slide down)
                    var slideOut = compositor.CreateVector3KeyFrameAnimation();
                    slideOut.InsertKeyFrame(1f, new System.Numerics.Vector3(0, -30, 0), easing);
                    slideOut.Duration = TimeSpan.FromMilliseconds(300);

                    visual.StartAnimation("Opacity", fadeOut);
                    visual.StartAnimation("Translation", slideOut);
                }
            }
            catch
            {
                HeroContentPanel.Opacity = 0;
            }
        }

        private void AnimateTextIn()
        {
            // [ROBUST] Use Composition to animate XAML opacity for perfect sync with backdrop
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(HeroContentPanel);
                if (visual != null)
                {
                    var compositor = visual.Compositor;
                    var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.2f, 1f));

                    // 1. Prepare State
                    ElementCompositionPreview.SetIsTranslationEnabled(HeroContentPanel, true);
                    HeroRealContent.Opacity = 1.0; // Ensure child is not hidden

                    // 2. Opacity Animation (Match backdrop 1.5s)
                    var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(0f, 0f, easing); // Force start from 0
                    fadeIn.InsertKeyFrame(1f, 1f, easing);
                    fadeIn.Duration = TimeSpan.FromMilliseconds(1500);
                    
                    // 3. Slide Up Animation (Match backdrop 1.5s)
                    var slideIn = compositor.CreateVector3KeyFrameAnimation();
                    slideIn.InsertKeyFrame(0f, new System.Numerics.Vector3(0, 40, 0), easing); // Force start from bottom
                    slideIn.InsertKeyFrame(1f, new System.Numerics.Vector3(0, 0, 0), easing);
                    slideIn.Duration = TimeSpan.FromMilliseconds(1500);

                    visual.StartAnimation("Opacity", fadeIn);
                    visual.StartAnimation("Translation", slideIn);
                }
            }
            catch
            {
                // Fallback to snap if composition fails
                HeroContentPanel.Opacity = 1.0;
            }
        }

        private void ApplyKenBurnsComposition(Microsoft.UI.Composition.Compositor compositor)
        {
            try
            {
                if (HeroImageHost.XamlRoot == null) return;
                var hostVisual = ElementCompositionPreview.GetElementVisual(HeroImageHost);
                if (hostVisual == null) return;
                
                var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
                var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f));
                offsetAnim.InsertKeyFrame(0f, Vector3.Zero, easing);
                offsetAnim.InsertKeyFrame(0.5f, new Vector3(-12f, -4f, 0f), easing);
                offsetAnim.InsertKeyFrame(1f, Vector3.Zero, easing);
                offsetAnim.Duration = TimeSpan.FromSeconds(25);
                offsetAnim.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                hostVisual.StartAnimation("Offset", offsetAnim);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HeroControl] KenBurns Animation Error: {ex.Message}");
            }
        }

        private void HeroPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItems.Count > _currentHeroIndex)
            {
                PlayAction?.Invoke(this, _heroItems[_currentHeroIndex]);
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

        private async void HeroTrailerButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("[HeroTrailer] Click detected");
            if (_heroItems.Count <= _currentHeroIndex) 
            {
                AppLogger.Warn("[HeroTrailer] Index out of range");
                return;
            }
            var item = _heroItems[_currentHeroIndex];
            AppLogger.Info($"[HeroTrailer] Item: {item.Title}, TrailerUrl: {item.TrailerUrl}");

            if (_isTrailerPlaying)
            {
                AppLogger.Info("[HeroTrailer] Trailer already playing, cleaning up");
                CleanupWebView();
                return;
            }

            string? trailerUrl = item.TrailerUrl;

            // [NEW] Fetch metadata if trailer is missing
            if (string.IsNullOrEmpty(trailerUrl))
            {
                AppLogger.Info("[HeroTrailer] TrailerUrl missing, fetching metadata...");
                HeroTrailerText.Text = "Yükleniyor...";
                try
                {
                    var unified = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(item, Models.Metadata.MetadataContext.Spotlight);
                    if (unified != null)
                    {
                        // [CONSOLIDATION] Synchronize the underlying stream with high-quality metadata
                        if (item != null) item.UpdateFromUnified(unified);

                        if (!string.IsNullOrEmpty(unified.TrailerUrl))
                        {
                            trailerUrl = unified.TrailerUrl;
                            AppLogger.Info($"[HeroTrailer] Fetched TrailerUrl: {trailerUrl}");
                        }
                    }
                    else
                    {
                        AppLogger.Warn("[HeroTrailer] Metadata fetch returned no trailer");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("[HeroTrailer] Metadata fetch error", ex);
                }
            }

            string? ytId = ExtractYouTubeId(trailerUrl);
            AppLogger.Info($"[HeroTrailer] Extracted YT ID: {ytId}");

            if (string.IsNullOrEmpty(ytId))
            {
                AppLogger.Warn("[HeroTrailer] No valid YouTube ID found");
                HeroTrailerText.Text = "Fragman Yok";
                await Task.Delay(1500);
                HeroTrailerText.Text = "Fragmanı İzle";
                return;
            }

            // Visual feedback
            HeroTrailerText.Text = "Hazırlanıyor...";

            // [FIX] Prepare container EARLY to avoid blocking autoplay
            VideoContainer.Visibility = Visibility.Visible;
            VideoContainer.Opacity = 0;
            VideoContainer.IsHitTestVisible = false;

            // [NEW] Stop auto-rotation immediately to avoid switching while loading
            StopAutoRotation();

            _pendingTrailerId = ytId;
            AppLogger.Info("[HeroTrailer] Calling InitializeWebView");
            InitializeWebView(ytId);
        }

        private string? ExtractYouTubeId(string? source)
        {
            if (string.IsNullOrEmpty(source)) return null;

            // Simple check: if it's already an 11-char ID and doesn't look like a URL
            if (source.Length == 11 && !source.Contains("/") && !source.Contains(".")) 
                return source;

            try
            {
                // Robust Regex for all common YouTube formats
                var regex = new System.Text.RegularExpressions.Regex(
                    @"(?:v=|\/be\/|\/embed\/|youtu\.be\/|\/live\/|\/shorts\/)([^#\&\?\/]{11})", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                var match = regex.Match(source);
                if (match.Success) return match.Groups[1].Value;
            }
            catch { }

            // Last resort: manual splits for legacy formats if regex missed something
            if (source.Contains("v="))
            {
                var split = source.Split("v=");
                if (split.Length > 1) return split[1].Split('&', '?', '#')[0];
            }

            return null; // Return null if not a valid YouTube ID/URL
        }

        private async Task PreInitializeWebViewAsync()
        {
            try
            {
                var env = await WebView2Service.GetSharedEnvironmentAsync();
                if (_webView != null) return;

                _webView = new Microsoft.UI.Xaml.Controls.WebView2();
                _webView.HorizontalAlignment = HorizontalAlignment.Stretch;
                _webView.VerticalAlignment = VerticalAlignment.Stretch;
                _webView.IsHitTestVisible = false;
                _webView.Opacity = 0;
                _webView.Visibility = Visibility.Collapsed;
                VideoContainer.Children.Insert(0, _webView);

                await _webView.EnsureCoreWebView2Async(env);
                if (_webView?.CoreWebView2 == null) return;

                // Harden for non-interactive playback
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _webView.CoreWebView2.Settings.IsWebMessageEnabled = true; // Required for resets
                _webView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                _webView.CoreWebView2.NewWindowRequested += (s, ev) => { ev.Handled = true; };

                // [FIX] Apply 100% Clean UI Script (Hides Title Flash, Logos, More Videos)
                await WebView2Service.ApplyYouTubeCleanUISettingsAsync(_webView.CoreWebView2);
                
                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // [OPTIMIZATION] Switch to WebResourceRequested Filter (Memory-only, no disk I/O)
                string virtualHost = $"hero-trailer-{_instanceId}.moderniptv.local";
                string baseUri = $"https://{virtualHost}/";
                
                _webView.CoreWebView2.AddWebResourceRequestedFilter(baseUri + "*", CoreWebView2WebResourceContext.All);
                _webView.CoreWebView2.WebResourceRequested += (s, args) =>
                {
                    if (args.Request.Uri.EndsWith("index.html"))
                    {
                        string html = GetHtmlContent(_pendingTrailerId ?? "");
                        var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
                        var stream = ms.AsRandomAccessStream();
                        args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                            stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
                    }
                };

                _webView.Source = new Uri(baseUri + "index.html");
            }
            catch (Exception ex)
            {
                AppLogger.Error("[HeroTrailer] Pre-init Error", ex);
            }
        }

        private string GetHtmlContent(string ytId)
        {
            string virtualHost = $"hero-trailer-{_instanceId}.moderniptv.local";
            return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        html, body {{ width: 100%; height: 100%; background: #000; overflow: hidden; }}
        #player {{ position: absolute; top: 0; left: 0; width: 100%; height: 100%; }}
    </style>
</head>
<body>
    <div id='player'></div>
    <script>
        window.addEventListener('message', function(e) {{
            if (e.data && e.data.type === 'LOG_FORWARD') {{
                try {{ window.chrome.webview.postMessage(e.data.msg); }} catch(ex) {{}}
            }}
        }});

        function log(msg) {{ try {{ window.chrome.webview.postMessage('LOG:' + msg); }} catch(ex) {{}} }}
        
        var tag = document.createElement('script');
        tag.src = 'https://www.youtube.com/iframe_api';
        var firstScriptTag = document.getElementsByTagName('script')[0];
        firstScriptTag.parentNode.insertBefore(tag, firstScriptTag);

        var player;
        var isReady = false;
        var pendingVideoId = null;
        
        function log(msg) {{ try {{ window.chrome.webview.postMessage('LOG:' + msg); }} catch(ex) {{}} }}

        window.loadVideo = function(id) {{
            log('loadVideo called for ' + id);
            if (!id) return;

            // [MODERN] Reset Smart Crop state for new video (Broadcast to all frames)
            try {{ 
                log('Broadcasting RESET_SMART_CROP...'); 
                window.chrome.webview.postMessage('RESET_SMART_CROP'); 
                
                // [NEW] Forward to YouTube iframe via standard postMessage
                var iframe = document.querySelector('iframe');
                if (iframe && iframe.contentWindow) {{
                    iframe.contentWindow.postMessage('RESET_SMART_CROP', '*');
                }}
            }} catch(e) {{
                 log('Manual transform reset fallback: ' + e);
                 var video = document.querySelector('video');
                 if (video) video.style.transform = 'scale(1)';
            }}
            
            if (isReady && player && player.loadVideoById) {{
                try {{
                    player.loadVideoById({{ videoId: id, startSeconds: 0 }});
                    player.playVideo();
                    log('loadVideoById triggered');
                }} catch(ex) {{
                    log('loadVideoById failed: ' + ex);
                }}
            }} else {{
                log('Player not ready, caching ID: ' + id);
                pendingVideoId = id;
            }}
        }};

        function onYouTubeIframeAPIReady() {{
            log('onYouTubeIframeAPIReady');
            player = new YT.Player('player', {{
                height: '100%', width: '100%',
                host: 'https://www.youtube.com',
                playerVars: {{
                    autoplay: 0, mute: 1, controls: 0, disablekb: 1,
                    fs: 0, rel: 0, modestbranding: 1, showinfo: 0,
                    iv_load_policy: 3, playsinline: 1, loop: 1
                }},
                events: {{
                    'onReady': onPlayerReady,
                    'onStateChange': onPlayerStateChange,
                    'onError': onPlayerError
                }}
            }});
        }}

        function onPlayerReady(event) {{
            log('onPlayerReady');
            isReady = true;
            try {{ player.mute(); }} catch (e) {{}}
            window.chrome.webview.postMessage('PLAYER_READY');
            
            if (pendingVideoId) {{
                log('Processing pending video: ' + pendingVideoId);
                loadVideo(pendingVideoId);
                pendingVideoId = null;
            }} else if ('{ytId}') {{
                log('Processing initial video: {ytId}');
                loadVideo('{ytId}');
            }}
        }}

        function onPlayerStateChange(event) {{
            log('onStateChange: ' + event.data);
            
            // [MODERN] Handle ENDED (0) to auto-return to backdrop
            if (event.data === 0) {{
                 log('Video ENDED, signaling auto-stop');
                 try {{ window.chrome.webview.postMessage('ENDED'); }} catch(ex) {{}}
            }}

            // [FIX] Signal READY on Playing (1) OR Buffering (3)
            if (event.data === 1 || event.data === 3) {{ 
                 log('Video reached ACTIVE state (Playing/Buffering)');
                 if (event.data === 1) event.target.unMute(); 
                 try {{ window.chrome.webview.postMessage('READY'); }} catch(ex) {{}}
            }}
        }}

        function onPlayerError(event) {{
            log('onError: ' + event.data);
            try {{ window.chrome.webview.postMessage('ERROR'); }} catch(ex) {{}}
        }}
    </script>
</body>
</html>";
        }

        private async void InitializeWebView(string ytId)
        {
            // Guard against disposal/unload
            if (!this.IsLoaded || _webView == null) return;
            
            AppLogger.Info($"[HeroTrailer] InitializeWebView called for {ytId}");
            if (_isTrailerPlaying)
            {
                AppLogger.Info("[HeroTrailer] Already playing, ignoring init");
                return;
            }

            try
            {
                if (_webView == null || _webView.CoreWebView2 == null)
                {
                    AppLogger.Info("[HeroTrailer] WebView or CoreWebView2 is null, calling Pre-init");
                    await PreInitializeWebViewAsync();
                }

                // Guard after await
                if (!this.IsLoaded || _webView == null || _webView.CoreWebView2 == null)
                {
                    AppLogger.Warn("[HeroTrailer] Pre-init failed or timed out");
                    return;
                }

                string virtualHost = $"hero-trailer-{_instanceId}.moderniptv.local";
                string currentSource = _webView.Source?.ToString() ?? "";
                
                if (currentSource.Contains(virtualHost))
                {
                    AppLogger.Info("[HeroTrailer] Persistent player found, sending loadVideo message");
                    string result = await _webView.CoreWebView2.ExecuteScriptAsync($@"
                        if (window.loadVideo) {{
                            window.loadVideo('{ytId}');
                            'OK';
                        }} else {{
                            'WAIT';
                        }}");

                    if (result == "\"OK\"") return;
                }

                // Fallback: Full page load via memory-only filter
                _pendingTrailerId = ytId;
                _webView.Source = new Uri($"https://{virtualHost}/index.html");
                AppLogger.Info("[HeroTrailer] Source set to bootstrap URL (Memory Filter)");
            }
            catch (Exception ex)
            {
                AppLogger.Error("[HeroTrailer] Init Error", ex);
            }
        }

        private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string msg = args.TryGetWebMessageAsString();
                
                if (msg.StartsWith("LOG:"))
                {
                    AppLogger.Info($"[HeroTrailer] JS: {msg.Substring(4)}");
                    return;
                }

                AppLogger.Info($"[HeroTrailer] Web Message Received: {msg}");
                
                if (msg == "RESET_SMART_CROP")
                {
                    // [BRIDGE] Relay the reset signal to ALL frames (including the YT iframe)
                    if (sender != null) sender.PostWebMessageAsString("RESET_SMART_CROP");
                    return;
                }

                if (msg == "PLAYER_READY")
                {
                    AppLogger.Info("[HeroTrailer] JS Player Ready");
                }
                else if (msg == "READY")
                {
                    if (_isTrailerPlaying) return;
                    _isTrailerPlaying = true;
                    AppLogger.Info("[HeroTrailer] Video READY, showing container");
                    
                    // 1. Show WebView and Container
                    if (_webView != null)
                    {
                        _webView.Visibility = Visibility.Visible;
                        _webView.Opacity = 1;
                    }

                    VideoContainer.Visibility = Visibility.Visible;
                    VideoContainer.IsHitTestVisible = false; 

                    // 2. Cinematic Fade-In for Trailer Container
                    var sb = new Storyboard();
                    var trailerFade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(800), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    Storyboard.SetTarget(trailerFade, VideoContainer);
                    Storyboard.SetTargetProperty(trailerFade, "Opacity");
                    sb.Children.Add(trailerFade);
                    sb.Begin();

                    // 3. Fade out the static background image
                    if (_heroVisual != null)
                    {
                        var compositor = _heroVisual.Compositor;
                        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                        fadeOut.InsertKeyFrame(1f, 0f);
                        fadeOut.Duration = TimeSpan.FromMilliseconds(500);
                        _heroVisual.StartAnimation("Opacity", fadeOut);
                    }

                    // 4. Update UI Controls
                    UpdateNavigationVisibility();
                    HeroTrailerIcon.Glyph = "\uE71A"; // Stop icon
                    HeroTrailerText.Text = "Durdur";

                    // 5. Inform parent about color (Solid Black during trailer)
                    ColorExtracted?.Invoke(this, (Windows.UI.Color.FromArgb(255, 0, 0, 0), Windows.UI.Color.FromArgb(255, 0, 0, 0)));
                }
                else if (msg == "ERROR" || msg == "ENDED")
                {
                    AppLogger.Info($"[HeroTrailer] Video {msg} received from JS, cleaning up");
                    CleanupWebView();
                    
                    if (msg == "ERROR")
                    {
                        // Show error on button temporarily
                        HeroTrailerText.Text = "Fragman Kullanılamıyor";
                        _ = Task.Delay(3000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => {
                            if (!_isTrailerPlaying) HeroTrailerText.Text = "Fragmanı İzle";
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("[HeroTrailer] WebMessage Error", ex);
            }
        }

        private async void CleanupWebView()
        {
            if (!_isTrailerPlaying && _webView == null) return;

            try
            {
                _isTrailerPlaying = false;
                _pendingTrailerId = null;

                if (_webView != null && _webView.CoreWebView2 != null)
                {
                        // [FIX] Pause AND Reset Smart Crop state across ALL frames
                        await _webView.CoreWebView2.ExecuteScriptAsync("if(window.player && player.pauseVideo) player.pauseVideo();");
                        _webView.CoreWebView2.PostWebMessageAsString("RESET_SMART_CROP");
                    _webView.Opacity = 0;
                    _webView.Visibility = Visibility.Collapsed;
                }

                // UI Reset
                VideoContainer.Opacity = 0;
                VideoContainer.IsHitTestVisible = false;
                VideoContainer.Visibility = Visibility.Collapsed;

                UpdateNavigationVisibility(); // [NEW] Restore arrows if needed
                StartHeroAutoRotation(); // [NEW] Resume rotation

                HeroTrailerIcon.Glyph = "\uE714"; // Search/Trailer icon
                HeroTrailerText.Text = "Fragmanı İzle";

                // Restore Backdrop
                if (_heroVisual != null)
                {
                    var compositor = _heroVisual.Compositor;
                    var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(1f, 1f);
                    fadeIn.Duration = TimeSpan.FromMilliseconds(500);
                    _heroVisual.StartAnimation("Opacity", fadeIn);
                }

                // Restore dynamic color (Manually re-triggering since content hasn't changed)
                if (!string.IsNullOrEmpty(_currentHeroId) && _heroItems.Count > _currentHeroIndex)
                {
                    var item = _heroItems[_currentHeroIndex];
                    string imgUrl = item.Meta?.Background ?? item.PosterUrl;
                    if (!string.IsNullOrEmpty(imgUrl))
                    {
                        _ = Task.Run(async () => {
                            var colors = await ImageHelper.GetOrExtractColorAsync(imgUrl);
                            if (colors != null) DispatcherQueue.TryEnqueue(() => ColorExtracted?.Invoke(this, colors.Value));
                        });
                    }
                }
            }
            catch { }
        }
    }
}
