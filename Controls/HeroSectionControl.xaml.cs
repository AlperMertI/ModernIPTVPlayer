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
using System.IO;
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

        // Perf logging helper
        private static void HeroLog(string msg)
        {
            var line = $"[HERO PERF] {DateTime.Now:HH:mm:ss.fff} | {msg}";
            System.Diagnostics.Debug.WriteLine(line);
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "hero_perf_log.txt"), line + "\n"); } catch { }
        }

        // Unified State Controller
        private enum HeroTransitionType { HardReset, Navigation, SilentSync }
        private System.Threading.SemaphoreSlim _reconcilerLock = new(1, 1);
        private long _currentSessionTicket = 0;

        private string? _currentBgUrl;
        private string? _currentLogoUrl;
        private string? _currentHeroId;
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
        private HeroAssetManager _assetManager;
        private System.Threading.CancellationTokenSource? _heroCts;
        private TaskCompletionSource _compositionReadyTcs = new();


        // Infrastructure State
        private volatile bool _isStoppingRotation = false;
        private volatile bool _isStartingRotation = false;
        private DateTime _lastColorChangeTime = DateTime.MinValue;
        private const int MIN_COLOR_CHANGE_INTERVAL_MS = 100; // Debounce color changes

        public HeroSectionControl()
        {
            this.InitializeComponent();
            _assetManager = new HeroAssetManager(this.DispatcherQueue, HeroLog);

            // Initial state
            HeroRealContent.Visibility = Visibility.Collapsed;
            HeroRealContent.Opacity = 0;

            TrailerView.PlayStateChanged += (s, isPlaying) => {
                System.Diagnostics.Debug.WriteLine($"[HeroSection] PlayStateChanged Received: {isPlaying}");
                try 
                {
                    if (isPlaying)
                    {
                        // 1. Cinematic Fade-In for Trailer Container
                        var sb = new Storyboard();
                        var trailerFade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(800), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                        Storyboard.SetTarget(trailerFade, VideoContainer);
                        Storyboard.SetTargetProperty(trailerFade, "Opacity");
                        sb.Children.Add(trailerFade);
                        sb.Begin();

                        VideoContainer.IsHitTestVisible = false;

                        // 2. Fade out the backdrop image
                        if (_heroVisual != null)
                        {
                            var compositor = _heroVisual.Compositor;
                            var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                            fadeOut.InsertKeyFrame(1f, 0f);
                            fadeOut.Duration = TimeSpan.FromMilliseconds(500);
                            _heroVisual.StartAnimation("Opacity", fadeOut);
                        }

                        SetGlyph(HeroTrailerIcon, "\uE71A"); // Stop icon
                        SetText(HeroTrailerText, "Durdur");
                        StopAutoRotation();
                        UpdateNavigationVisibility();

                        // [FIX] Ensure focus to WebView might help with autoplay policy
                        TrailerView.Focus(FocusState.Programmatic);

                        // 3. Inform parent about color (Solid Black during trailer)
                        NotifyColorChanged(Windows.UI.Color.FromArgb(255, 0, 0, 0), Windows.UI.Color.FromArgb(255, 0, 0, 0));
                    }
                    else
                    {
                        // 1. Cinematic Fade-Out for Trailer Container
                        var sb = new Storyboard();
                        var trailerFade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                        Storyboard.SetTarget(trailerFade, VideoContainer);
                        Storyboard.SetTargetProperty(trailerFade, "Opacity");
                        sb.Children.Add(trailerFade);
                        sb.Begin();

                        VideoContainer.IsHitTestVisible = false;

                        // 2. Restore Backdrop and Navigation
                        if (_heroVisual != null)
                        {
                            var compositor = _heroVisual.Compositor;
                            var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                            fadeIn.InsertKeyFrame(1f, 1f);
                            fadeIn.Duration = TimeSpan.FromMilliseconds(500);
                            _heroVisual.StartAnimation("Opacity", fadeIn);
                        }

                        SetGlyph(HeroTrailerIcon, "\uE714");
                        SetText(HeroTrailerText, "Fragmanı İzle");
                        StartHeroAutoRotation();
                        UpdateNavigationVisibility();

                        // 3. Restore colors from current item
                        if (_heroItems.Count > _currentHeroIndex)
                        {
                            var item = _heroItems[_currentHeroIndex];
                            string? bgUrl = item.Meta?.Background ?? item.PosterUrl;
                            if (!string.IsNullOrEmpty(bgUrl))
                            {
                                _ = Task.Run(async () =>
                                {
                                    var colors = await ImageHelper.GetOrExtractColorAsync(bgUrl);
                                    if (colors != null)
                                        DispatcherQueue.TryEnqueue(() => NotifyColorChanged(colors.Value.Primary, colors.Value.Secondary));
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HeroSection] PlayStateChanged Error: {ex.Message}");
                }
            };

            TrailerView.VideoEnded += (s, e) => {
                ResetHeroAutoTimer();
            };
            
            this.Loaded += (s, e) => SetupHeroCompositionMask();

            this.Unloaded += HeroSectionControl_Unloaded;
        }


        private void HeroSectionControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _heroCts?.Cancel();
            _heroCts = null;
            StopAutoRotation();
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

        // --- [CORE RECONCILER] ---
        public async Task ReconcileAsync(List<StremioMediaStream>? newItems = null, bool forceHardReset = false)
        {
            if (newItems != null)
            {
                _heroItems = newItems;
                HeroLog($"Data Sync: Received {_heroItems.Count} items (ForceReset={forceHardReset})");
            }

            if (_heroItems.Count == 0) return;

            // We now delegate the transition detection to the serialized worker
            // to prevent race conditions with background catalog refreshes.
            _ = ApplyTransitionInternalAsync(forceHardReset);
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

                // 2. Alpha gradient mask (Normalized 0,0 to 0,1)
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
                
                // [FIX] Sync opacity with current state
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
                    {
                        _heroLogoVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
                        // Refresh center point for Ken Burns and animations if they were applied to logo
                        _heroLogoVisual.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
                    }
                };
                
                _compositionReadyTcs.TrySetResult();
            }
            catch (Exception ex)
            {
                HeroLog($"[HeroControl] Setup Composition Error: {ex.Message}");
            }
        }

        private async Task ApplyTransitionInternalAsync(bool forceHardReset = false)
        {
            // Ensure composition is ready before we start manipulating brushes
            await _compositionReadyTcs.Task;

            await _reconcilerLock.WaitAsync();
            try
            {
                // 1. Determine Target Item and Transition Type (Atomic State Check)
                int targetIndex = -1;
                HeroTransitionType type = HeroTransitionType.Navigation;

                if (forceHardReset || string.IsNullOrEmpty(_currentHeroId))
                {
                    targetIndex = 0;
                    type = HeroTransitionType.HardReset;
                }
                else
                {
                    targetIndex = _heroItems.FindIndex(x => (x.IMDbId ?? x.Id.ToString()) == _currentHeroId);
                    if (targetIndex == -1)
                    {
                        HeroLog($"Persistence Lost: {_currentHeroId} not found. Resetting to 0.");
                        targetIndex = 0;
                        type = HeroTransitionType.HardReset;
                    }
                    else 
                    {
                        var targetItem = _heroItems[targetIndex];
                        string? newBg = targetItem.Meta?.Background ?? targetItem.PosterUrl;
                        bool changed = targetItem.LogoUrl != _currentLogoUrl || newBg != _currentBgUrl;
                        
                        if (changed) type = HeroTransitionType.Navigation;
                        else
                        {
                            HeroLog($"[RECONCILER] Silent Sync for {targetItem.Title}");
                            _currentHeroIndex = targetIndex;
                            PopulateHeroData(targetItem);
                            UpdateNavigationVisibility();
                            return; 
                        }
                    }
                }

                StremioMediaStream item = _heroItems[targetIndex];
                long sessionTicket = ++_currentSessionTicket;
                var startTime = DateTime.Now;
                HeroLog($"[TRANSITION] Starting {type} for {item.Title} (Ticket: {sessionTicket})");

                _currentHeroId = item.IMDbId ?? item.Id.ToString();
                _currentHeroIndex = targetIndex;

                if (type != HeroTransitionType.SilentSync) StopAutoRotation();

                if (type == HeroTransitionType.HardReset)
                {
                    SetLoading(true, silent: false);
                    _assetManager.Clear(_currentLogoUrl, _currentBgUrl);
                }
                else if (type == HeroTransitionType.Navigation)
                {
                    AnimateTextOut();
                    if (_heroVisual != null)
                    {
                        var compositor = _heroVisual.Compositor;
                        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                        fadeOut.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f)));
                        fadeOut.Duration = TimeSpan.FromMilliseconds(300);
                        _heroVisual.StartAnimation("Opacity", fadeOut);
                    }
                    await Task.Delay(300);
                }

                if (sessionTicket != _currentSessionTicket) return;

                _heroCts?.Cancel();
                _heroCts = new System.Threading.CancellationTokenSource();
                var token = _heroCts.Token;

                string? bgUrl = item.Meta?.Background ?? item.PosterUrl;
                HeroLog($"[TRANSITION] Loading Assets: BG={bgUrl}, Logo={item.LogoUrl}");
                
                // Color Extraction Restoration
                if (!string.IsNullOrEmpty(bgUrl))
                {
                    var sessionTicketCapture = sessionTicket;
                    _ = Task.Run(async () =>
                    {
                        var colors = await ImageHelper.GetOrExtractColorAsync(bgUrl);
                        if (colors != null && sessionTicketCapture == _currentSessionTicket)
                        {
                            DispatcherQueue.TryEnqueue(() => NotifyColorChanged(colors.Value.Primary, colors.Value.Secondary));
                        }
                    });
                }
                Task<Microsoft.UI.Xaml.Media.LoadedImageSurface?> backdropTask = _assetManager.GetBackdropSurfaceAsync(bgUrl, token);
                Task<Microsoft.UI.Xaml.Media.LoadedImageSurface?> logoTask = _assetManager.GetLogoSurfaceAsync(item.LogoUrl, token);

                // --- [STRICT WAITING LOGIC] ---
                if (type == HeroTransitionType.HardReset || type == HeroTransitionType.Navigation)
                {
                    HeroLog("[TRANSITION] Waiting for Backdrop...");
                    await Task.WhenAny(backdropTask, Task.Delay(3000, token));
                }

                if (sessionTicket != _currentSessionTicket) return;

                // Capture surfaces (handle late arrivals via ContinueWith if not ready)
                var bgSurface = backdropTask.IsCompleted ? await backdropTask : null;
                var logoSurface = logoTask.IsCompleted ? await logoTask : null;

                if (bgSurface == null && !backdropTask.IsCompleted)
                {
                    HeroLog("[TRANSITION] Backdrop slow, attaching late-reveal handler.");
                    _ = backdropTask.ContinueWith(async t => {
                        var surface = await t;
                        DispatcherQueue.TryEnqueue(() => {
                            if (sessionTicket == _currentSessionTicket && _heroImageBrush != null && surface != null)
                            {
                                HeroLog("[TRANSITION] Backdrop arrived LATE, applying to brush.");
                                _heroImageBrush.Surface = surface;
                                if (HeroRealContent.Visibility == Visibility.Visible) ShowRealContent();
                            }
                        });
                    }, token);
                }

                if (logoSurface == null && !logoTask.IsCompleted)
                {
                    HeroLog("[TRANSITION] Logo slow, attaching late-reveal handler.");
                    _ = logoTask.ContinueWith(async t => {
                        var surface = await t;
                        DispatcherQueue.TryEnqueue(() => {
                            if (sessionTicket == _currentSessionTicket && _heroLogoBrush != null && surface != null)
                            {
                                HeroLog("[TRANSITION] Logo arrived LATE, applying to brush.");
                                _heroLogoBrush.Surface = surface;
                                if (_heroLogoVisual != null) _heroLogoVisual.Opacity = 1.0f;
                            }
                        });
                    }, token);
                }

                if (bgSurface != null && _heroImageBrush != null) _heroImageBrush.Surface = bgSurface;
                if (logoSurface != null && _heroLogoBrush != null) _heroLogoBrush.Surface = logoSurface;
                
                HeroLog($"[VISUAL-SURFACE] Applied Brushes. BG={(bgSurface != null ? "READY" : "NULL")}, Logo={(logoSurface != null ? "READY" : "NULL")}");
                
                _currentBgUrl = bgUrl;
                _currentLogoUrl = item.LogoUrl;

                PopulateHeroData(item);
                SubscribeToItemChanges(item);

                if (sessionTicket != _currentSessionTicket) return;

                if (type == HeroTransitionType.HardReset || type == HeroTransitionType.Navigation)
                {
                    // For a cinematic experience, ONLY reveal if we actually have the backdrop surface.
                    // If bgSurface is null here, it means it was slow (it will be handled by ContinueWith)
                    // OR it failed (which we handle by checking backdropTask.IsCompleted).
                    
                    bool isFailed = (backdropTask.IsCompleted && backdropTask.Result == null);
                    bool isTimedOut = !backdropTask.IsCompleted && (DateTime.Now - startTime).TotalMilliseconds >= 2500; // Close to the 3s window

                    if (bgSurface != null || isFailed || isTimedOut)
                    {
                        HeroLog($"[TRANSITION] Revealing Content Panel (AssetReady={bgSurface != null}).");
                        ShowRealContent();
                    }
                    else 
                    {
                        HeroLog("[TRANSITION] Backdrop still pending. Waiting for late-arrival handler to reveal.");
                    }
                }

                UpdateNavigationVisibility();
                StartHeroAutoRotation();
            }
            finally
            {
                _reconcilerLock.Release();
            }
        }


        public void SetItems(IEnumerable<StremioMediaStream> items, bool animate = false)
        {
            if (items == null) return;
            _ = ReconcileAsync(items.ToList(), forceHardReset: animate);
        }

        private void UpdateNavigationVisibility()
        {
            var vis = (_heroItems.Count > 1 && !TrailerView.IsPlaying) ? Visibility.Visible : Visibility.Collapsed;
            HeroPrevButton.Visibility = vis;
            HeroNextButton.Visibility = vis;
        }


        public void StopAutoRotation()
        {
            if (_isStoppingRotation) return;

            _isStoppingRotation = true;
            try
            {
                if (_heroAutoTimer != null)
                {
                    _heroAutoTimer.Stop();
                    _heroAutoTimer = null;
                }
            }
            finally
            {
                _isStoppingRotation = false;
            }
        }

        public void StartHeroAutoRotation()
        {
            if (_isStartingRotation) return;

            _isStartingRotation = true;
            try
            {
                StopAutoRotation();
                _heroAutoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                _heroAutoTimer.Tick += (s, e) =>
                {
                    if (this.Visibility != Visibility.Visible || this.ActualWidth <= 0 || this.XamlRoot == null || TrailerView.IsPlaying)
                        return;

                    if (_heroItems.Count > 1)
                    {
                        _currentHeroIndex = (_currentHeroIndex + 1) % _heroItems.Count;
                        _currentHeroId = _heroItems[_currentHeroIndex].IMDbId ?? _heroItems[_currentHeroIndex].Id.ToString();
                        _ = ReconcileAsync();
                    }
                };
                _heroAutoTimer.Start();
            }
            finally
            {
                _isStartingRotation = false;
            }
        }

        private void ResetHeroAutoTimer()
        {
            _heroAutoTimer?.Stop();
            _heroAutoTimer?.Start();
        }

        private void NotifyColorChanged(Windows.UI.Color primary, Windows.UI.Color secondary)
        {
            var now = DateTime.Now;
            var elapsed = (now - _lastColorChangeTime).TotalMilliseconds;

            if (elapsed < MIN_COLOR_CHANGE_INTERVAL_MS) return;

            _lastColorChangeTime = now;

            // Only notify the parent (MediaLibraryPage) to update the DynamicBackdrop
            ColorExtracted?.Invoke(this, (primary, secondary));
        }

        private bool _isFirstLoad = true;
        
        public void SetLoading(bool isLoading, bool silent = false)
        {
            if (isLoading)
            {
                if (!silent)
                {
                    HeroShimmer.Visibility = Visibility.Visible;
                    HeroTextShimmer.Visibility = Visibility.Visible;
                    
                    HeroAnimationHelper.AnimateShimmerIn(HeroShimmer);
                    HeroAnimationHelper.AnimateShimmerIn(HeroTextShimmer);
                    
                    // Handle transition out of the current content gracefully
                    if (HeroRealContent.Visibility == Visibility.Visible)
                    {
                        HeroAnimationHelper.FadeElement(HeroRealContent, 0.0, 300);
                        _ = Task.Delay(300).ContinueWith(_ => {
                            DispatcherQueue.TryEnqueue(() => {
                                // Only collapse if we are still in a loading/silent state for the SAME session
                                if (_heroCts == null || _heroCts.IsCancellationRequested)
                                    HeroRealContent.Visibility = Visibility.Collapsed;
                            });
                        });
                    }
                }

                if (_heroVisual != null) _heroVisual.Opacity = 0;
                if (_heroLogoVisual != null) _heroLogoVisual.Opacity = 0;
                
                _heroCts?.Cancel();
                _heroCts = null;

                _isFirstLoad = true;
            }
            else
            {
                ShowRealContent();
            }
        }

        private void ShowRealContent()
        {
            // --- THE CINEMATIC CROSS-FADE ---
            
            // 1. Exit Shimmer
            HeroAnimationHelper.FadeElement(HeroShimmer, 0, 400);
            HeroAnimationHelper.FadeElement(HeroTextShimmer, 0, 400);

            // 2. Prepare & Slide-In Real Content
            HeroRealContent.Visibility = Visibility.Visible;
            HeroAnimationHelper.AnimateTextIn(HeroRealContent); // Refined slide + fade
            
            if (_heroVisual != null)
            {
                // 3. Deep 1200ms Backdrop Dissolve
                var compositor = _heroVisual.Compositor;
                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.2f, 1f)));
                fadeIn.Duration = TimeSpan.FromMilliseconds(1200);
                _heroVisual.StartAnimation("Opacity", fadeIn);
                HeroLog("[VISUAL-OPACITY] Animated Deep Dissolve: Backdrop.");
            }

            if (_heroLogoVisual != null)
            {
                // 4. Logo Entry
                var compositor = _heroLogoVisual.Compositor;
                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.2f, 1f)));
                fadeIn.Duration = TimeSpan.FromMilliseconds(800);
                _heroLogoVisual.StartAnimation("Opacity", fadeIn);
                HeroLog("[VISUAL-OPACITY] Animated Reveal: Logo.");
            }
            
            // 5. Cleanup visibility after dissolve completes
            _ = Task.Delay(500).ContinueWith(_ => {
                DispatcherQueue.TryEnqueue(() => {
                    HeroShimmer.Visibility = Visibility.Collapsed;
                    HeroTextShimmer.Visibility = Visibility.Collapsed;
                });
            });

            _isFirstLoad = false;
        }

        private async Task ApplyBackdropAsync(string? url, System.Threading.CancellationToken token)
        {
            var surface = await _assetManager.GetBackdropSurfaceAsync(url, token);
            if (surface != null && !token.IsCancellationRequested && _heroImageBrush != null)
            {
                _heroImageBrush.Surface = surface;
            }
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
                    bool isActive = (_heroItems.Count > _currentHeroIndex && _heroItems[_currentHeroIndex] == item);

                    if (e.PropertyName == nameof(item.LogoUrl))
                    {
                        if (isActive) UpdateLogoWithTransition(item);
                    }

                    if (!isActive) return;

                    if (e.PropertyName == nameof(item.Description) || e.PropertyName == nameof(item.Rating) || 
                        e.PropertyName == nameof(item.Year) || e.PropertyName == nameof(item.Genres))
                    {
                        PopulateHeroData(item);
                    }
                    
                    if (e.PropertyName == nameof(item.Banner) || e.PropertyName == nameof(item.LandscapeImageUrl) || e.PropertyName == "Meta")
                    {
                        string newImgUrl = item.Meta?.Background ?? item.PosterUrl;
                        if (!string.IsNullOrEmpty(newImgUrl) && newImgUrl != _currentBgUrl)
                        {
                            _ = ApplyBackdropAsync(newImgUrl, System.Threading.CancellationToken.None);
                        }
                    }
                });
            }
        }

        private async void UpdateLogoWithTransition(StremioMediaStream item)
        {
            if (string.IsNullOrEmpty(item.LogoUrl)) return;
            
            var surface = await _assetManager.GetLogoSurfaceAsync(item.LogoUrl, System.Threading.CancellationToken.None);
            if (surface != null && _heroLogoBrush != null)
            {
                _heroLogoBrush.Surface = surface;
                _currentLogoUrl = item.LogoUrl;
                PopulateHeroData(item);
                SetVisibility(HeroLogoHost, Visibility.Visible);
            }
        }

        private void PopulateHeroData(StremioMediaStream item)
        {
            SetText(HeroYear, item.Year);
            SetText(HeroRating, item.Rating);
            SetText(HeroOverview, !string.IsNullOrEmpty(item.Description) ? item.Description : "Sinematik bir serüven sizi bekliyor.");
            SetVisibility(HeroOverview, !string.IsNullOrEmpty(item.Description) ? Visibility.Visible : Visibility.Collapsed);

            if (!string.IsNullOrEmpty(item.LogoUrl))
            {
                SetVisibility(HeroLogoContainer, Visibility.Visible);
                SetOpacity(HeroLogoContainer, 1.0);
                SetVisibility(HeroTitle, Visibility.Collapsed);
            }
            else
            {
                _currentLogoUrl = null;
                SetVisibility(HeroLogoContainer, Visibility.Collapsed);
                SetVisibility(HeroTitle, Visibility.Visible);
                SetText(HeroTitle, item.Title);
            }

            bool hasGenres = !string.IsNullOrEmpty(item.Genres);
            SetText(HeroGenres, item.Genres);
            SetVisibility(HeroGenres, hasGenres ? Visibility.Visible : Visibility.Collapsed);
            SetVisibility(HeroYearDot, hasGenres ? Visibility.Visible : Visibility.Collapsed);

            bool hasRating = !string.IsNullOrEmpty(item.Rating);
            SetVisibility(HeroRatingDot, hasRating ? Visibility.Visible : Visibility.Collapsed);

            bool hasTrailer = !string.IsNullOrEmpty(item.TrailerUrl) || (item.Meta?.Trailers != null && item.Meta.Trailers.Any(t => !string.IsNullOrEmpty(t.Source)));
            SetVisibility(HeroTrailerButton, hasTrailer ? Visibility.Visible : Visibility.Collapsed);
            SetGlyph(HeroTrailerIcon, "\uE714");
            SetText(HeroTrailerText, "Fragmanı İzle");
        }

        private void AnimateTextOut()
        {
            HeroAnimationHelper.AnimateTextOut(HeroRealContent);
        }

        private void AnimateTextIn()
        {
            HeroAnimationHelper.AnimateTextIn(HeroRealContent);
        }

        private void ApplyKenBurnsComposition(Microsoft.UI.Composition.Compositor compositor)
        {
            if (_heroVisual != null) HeroAnimationHelper.ApplyKenBurns(_heroVisual);
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
            if (_heroItems.Count == 0 || _reconcilerLock.CurrentCount == 0) return;
            _currentHeroIndex = (_currentHeroIndex + 1) % _heroItems.Count;
            _currentHeroId = _heroItems[_currentHeroIndex].IMDbId ?? _heroItems[_currentHeroIndex].Id.ToString();
            _ = ReconcileAsync();
            ResetHeroAutoTimer();
        }

        private void HeroPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItems.Count == 0 || _reconcilerLock.CurrentCount == 0) return;
            _currentHeroIndex--;
            if (_currentHeroIndex < 0) _currentHeroIndex = _heroItems.Count - 1;
            _currentHeroId = _heroItems[_currentHeroIndex].IMDbId ?? _heroItems[_currentHeroIndex].Id.ToString();
            _ = ReconcileAsync();
            ResetHeroAutoTimer();
        }

        private async void HeroTrailerButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[HeroSection] HeroTrailerButton_Click");
            if (_heroItems.Count <= _currentHeroIndex) return;
            var item = _heroItems[_currentHeroIndex];

            if (TrailerView.IsPlaying)
            {
                await TrailerView.StopAsync();
                return;
            }

            string? ytId = item.TrailerUrl;

            // [NEW/RESTORED] Fetch metadata if trailer is missing
            if (string.IsNullOrEmpty(ytId))
            {
                SetText(HeroTrailerText, "Yükleniyor...");
                try
                {
                    var unified = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(item, Models.Metadata.MetadataContext.Spotlight);
                    if (unified != null)
                    {
                        item.UpdateFromUnified(unified);
                        if (!string.IsNullOrEmpty(unified.TrailerUrl))
                        {
                            ytId = unified.TrailerUrl;
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(ytId))
            {
                SetText(HeroTrailerText, "Fragman Yok");
                await Task.Delay(1500);
                if (!TrailerView.IsPlaying) SetText(HeroTrailerText, "Fragmanı İzle");
                return;
            }

            // Visual feedback
            SetText(HeroTrailerText, "Hazırlanıyor...");
            StopAutoRotation();
            
            _ = TrailerView.PlayTrailerAsync(ytId);
        }
    }
}
