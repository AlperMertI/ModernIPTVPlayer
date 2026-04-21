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
using System.Threading;
using System.Threading.Tasks;
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

        // Delegates to the unified Helpers.HeroTracer — kept as a local name for readability at call sites.
        private static void HeroLog(string msg) => Helpers.HeroTracer.Log(msg);

        private enum HeroPhase { Loading, Ready, TrailerPlaying }

        // Bundled asset surfaces for one reveal. Populated right before CommitReveal.
        private sealed class HeroAssets
        {
            public LoadedImageSurface? Backdrop;
            public LoadedImageSurface? Logo;
            public bool HasLogoUrl;
        }

        private readonly SemaphoreSlim _reconcilerLock = new(1, 1);
        private long _currentSessionTicket = 0;
        private HeroPhase _phase = HeroPhase.Loading;
        // True while the shimmer UI is visible. Used to avoid replaying the intro animation when
        // multiple callers (external SetLoading, internal ShowShimmerIfSlowAsync) both want to show it.
        private bool _shimmerIsVisible = false;

        private string? _currentBgUrl;
        private string? _currentLogoUrl;
        private string? _currentHeroId;
        private DispatcherTimer? _heroAutoTimer;
        private readonly Queue<int> _navigationQueue = new();
        private readonly System.Threading.Lock _navigationQueueLock = new();
        private bool _isNavigationPumpActive = false;

        // Data
        private List<StremioMediaStream> _heroItems = new();
        private int _currentHeroIndex = 0;

        // Composition API
        private Microsoft.UI.Composition.SpriteVisual? _heroVisual;
        private Microsoft.UI.Composition.CompositionSurfaceBrush? _heroImageBrush;
        private Microsoft.UI.Composition.CompositionLinearGradientBrush? _heroAlphaMask;
        private Microsoft.UI.Composition.CompositionMaskBrush? _heroMaskBrush;

        private Microsoft.UI.Composition.SpriteVisual? _heroLogoVisual;
        private Microsoft.UI.Composition.CompositionSurfaceBrush? _heroLogoBrush;
        private HeroAssetManager _assetManager;
        private CancellationTokenSource? _heroCts;
        private readonly TaskCompletionSource _compositionReadyTcs = new();


        // Infrastructure State
        private volatile bool _isStoppingRotation = false;
        private volatile bool _isStartingRotation = false;
        private DateTime _lastColorChangeTime = DateTime.MinValue;
        private const int MIN_COLOR_CHANGE_INTERVAL_MS = 100;
        private const int BACKDROP_BUDGET_MS = 6000;
        private List<string> _activeTrailerCandidates = new();
        private int _activeTrailerIndex = -1;
        private bool _isHandlingTrailerFallback = false;

        // Active staged assets for the current session (replaces _hasHeroMetadata/_hasBackdropReady/_hasLogoReady/_stagedBackdropSurface/_stagedLogoSurface).
        private HeroAssets? _activeAssets;

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
                        _phase = HeroPhase.TrailerPlaying;
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
                        _phase = HeroPhase.Ready;
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
            TrailerView.PlaybackError += async (s, code) =>
            {
                if (_isHandlingTrailerFallback) return;
                _isHandlingTrailerFallback = true;
                try
                {
                    bool embedBlocked = code == 150 || code == 101;
                    bool moved = await TryPlayNextTrailerCandidateAsync();
                    if (!moved)
                    {
                        SetText(HeroTrailerText, embedBlocked ? "Bu fragman oynatılamıyor" : "Fragman açılamadı");
                        await Task.Delay(1500);
                        if (!TrailerView.IsPlaying) SetText(HeroTrailerText, "Fragmanı İzle");
                    }
                }
                finally
                {
                    _isHandlingTrailerFallback = false;
                }
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
            try
            {
                if (newItems != null)
                {
                    _heroItems = newItems;
                    HeroLog($"Data Sync: Received {_heroItems.Count} items (ForceReset={forceHardReset})");
                }

                if (_heroItems.Count == 0) return;

                // We now delegate the transition detection to the serialized worker
                // to prevent race conditions with background catalog refreshes.
                await ApplyTransitionInternalAsync(forceHardReset);
            }
            catch (Exception ex)
            {
                // Important: ReconcileAsync is often called fire-and-forget from UI events/timers.
                // Never let exceptions bubble as UnobservedTaskException on the finalizer thread.
                HeroLog($"[RECONCILE] Exception: {ex.GetType().Name} | {ex.Message}");
            }
        }

        private void EnqueueNavigationStep(int delta)
        {
            if (delta == 0 || _heroItems.Count == 0) return;
            lock (_navigationQueueLock)
            {
                _navigationQueue.Enqueue(delta);
                if (_isNavigationPumpActive) return;
                _isNavigationPumpActive = true;
            }
            _ = PumpNavigationQueueAsync();
        }

        private async Task PumpNavigationQueueAsync()
        {
            while (true)
            {
                int step;
                lock (_navigationQueueLock)
                {
                    if (_navigationQueue.Count == 0)
                    {
                        _isNavigationPumpActive = false;
                        return;
                    }
                    step = _navigationQueue.Dequeue();
                }

                if (_heroItems.Count == 0) continue;
                _currentHeroIndex = NormalizeIndex(_currentHeroIndex + step, _heroItems.Count);
                _currentHeroId = _heroItems[_currentHeroIndex].IMDbId ?? _heroItems[_currentHeroIndex].Id.ToString();
                await ReconcileAsync();
            }
        }

        private static int NormalizeIndex(int index, int count)
        {
            if (count <= 0) return 0;
            int mod = index % count;
            return mod < 0 ? mod + count : mod;
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
                _heroImageBrush.VerticalAlignmentRatio = 0.5f; // Middle-aligned

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
                
                // Start invisible — CommitReveal fades it in when assets land.
                _heroVisual.Opacity = 0f;

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

        private const int HERO_EXIT_DURATION_MS = 300;
        // Must outlive the primary reveal opacity ramps (text ~500ms, logo ~600ms) so rapid
        // queued next commands do not start a new exit before metadata/logo become visible.
        private const int MIN_REVEAL_HOLD_MS = 650;
        // Assets have this much time from transition start before the shimmer appears. Shorter than a
        // human "uh oh, is it broken?" threshold but long enough to cover the exit animation plus a small
        // grace window for borderline cache-warm hits, so a 310ms fetch doesn't flash shimmer for 90ms.
        private const int SHIMMER_REVEAL_THRESHOLD_MS = 500;

        /// <summary>
        /// Transition to the current _currentHeroId target. Three concurrent tracks, sequenced with Tasks:
        ///
        ///   exitTask   : play the outgoing animation (300ms) — or finish instantly if nothing was on screen.
        ///   assetsTask : fetch backdrop + logo surfaces (and wait for them to fully decode).
        ///   shimmerWatch: show the skeleton shimmer ONLY if assetsTask hasn't completed by SHIMMER_REVEAL_THRESHOLD_MS.
        ///
        /// We then await exitTask && assetsTask (with a budget ceiling on assets) and commit the reveal.
        /// No timestamps, no phase gates at call sites, no late-reveal continuation plumbing — just async
        /// composition of three independent effects. Session-ticket check is the ONLY guard needed for
        /// concurrent transitions.
        /// </summary>
        private async Task ApplyTransitionInternalAsync(bool forceHardReset = false)
        {
            await _compositionReadyTcs.Task;
            await _reconcilerLock.WaitAsync();
            try
            {
                int targetIndex = ResolveTargetIndex(forceHardReset);
                if (targetIndex < 0) return;
                var item = _heroItems[targetIndex];

                // Silent sync — same URLs, only metadata changed; no animation needed.
                string? newBg = item.Meta?.Background ?? item.PosterUrl;
                if (!forceHardReset && item.LogoUrl == _currentLogoUrl && newBg == _currentBgUrl && !string.IsNullOrEmpty(_currentHeroId))
                {
                    HeroLog($"[RECONCILER] Silent Sync for {item.Title}");
                    _currentHeroIndex = targetIndex;
                    PopulateHeroData(item);
                    UpdateNavigationVisibility();
                    return;
                }

                long ticket = ++_currentSessionTicket;
                HeroLog($"[TRANSITION] Start ticket={ticket} title={item.Title} idx={targetIndex}");

                _currentHeroIndex = targetIndex;
                _currentHeroId = item.IMDbId ?? item.Id.ToString();
                _currentBgUrl = newBg;
                _currentLogoUrl = item.LogoUrl;

                bool hasLogo = !string.IsNullOrEmpty(item.LogoUrl);
                var assets = new HeroAssets { HasLogoUrl = hasLogo };
                _activeAssets = assets;
                _phase = HeroPhase.Loading;
                StopAutoRotation();

                _heroCts?.Cancel();
                _heroCts = new CancellationTokenSource();
                var token = _heroCts.Token;
                _assetManager.Clear(_currentLogoUrl, _currentBgUrl);

                HeroLog($"[TRANSITION] Loading BG='{_currentBgUrl}' Logo='{_currentLogoUrl}'");

                // --- Three concurrent tracks ---
                var exitTask    = PlayExitAsync(ticket);
                var backdropTask= _assetManager.GetBackdropSurfaceAsync(_currentBgUrl, token);
                var logoTask    = hasLogo ? _assetManager.GetLogoSurfaceAsync(_currentLogoUrl, token) : Task.FromResult<LoadedImageSurface?>(null);
                var assetsTask  = Task.WhenAll(backdropTask, logoTask);

                _ = ShowShimmerIfSlowAsync(ticket, assetsTask); // fire-and-forget watchdog

                // Wait for assets (with budget) and exit animation. Budget applies only to assetsTask —
                // exitTask always completes in HERO_EXIT_DURATION_MS, well under any budget.
                var budget = Task.Delay(BACKDROP_BUDGET_MS, token);
                await Task.WhenAny(assetsTask, budget).ConfigureAwait(true);
                await exitTask.ConfigureAwait(true);

                if (ticket != _currentSessionTicket) return;
                if (!assetsTask.IsCompleted)
                {
                    // Exit bitti ama yeni asset henüz yoksa blank frame yerine shimmer göster.
                    ShowShimmerWithIntro();
                }

                // If budget fired first, keep shimmer visible and wait for the genuinely slow assets.
                if (!assetsTask.IsCompleted)
                {
                    HeroLog("[TRANSITION] Budget elapsed — shimmer stays, awaiting late assets.");
                    try { await assetsTask.ConfigureAwait(true); }
                    catch (OperationCanceledException) { return; }
                    if (ticket != _currentSessionTicket) return;
                }

                assets.Backdrop = backdropTask.IsCompletedSuccessfully ? backdropTask.Result : null;
                assets.Logo     = logoTask.IsCompletedSuccessfully     ? logoTask.Result     : null;

                await CommitRevealAsync(ticket, assets, item, token).ConfigureAwait(true);
                UpdateNavigationVisibility();
            }
            finally
            {
                _reconcilerLock.Release();
            }
        }

        /// <summary>
        /// Resolves which item should become the hero. Returns -1 if the list is empty.
        /// </summary>
        private int ResolveTargetIndex(bool forceHardReset)
        {
            if (_heroItems.Count == 0) return -1;
            if (forceHardReset || string.IsNullOrEmpty(_currentHeroId)) return 0;

            int idx = _heroItems.FindIndex(x => (x.IMDbId ?? x.Id.ToString()) == _currentHeroId);
            if (idx == -1)
            {
                HeroLog($"Persistence Lost: {_currentHeroId} not found. Resetting to 0.");
                return 0;
            }
            return idx;
        }

        /// <summary>
        /// Plays the exit animation (text slide+fade up, backdrop/logo opacity fade) and completes after
        /// HERO_EXIT_DURATION_MS. If nothing was on screen, completes instantly. Nulls composition surfaces
        /// at the tail so the shimmer (if shown) starts from a clean slate.
        /// </summary>
        private async Task PlayExitAsync(long ticket)
        {
            bool liveContent  = HeroRealContent.Visibility == Visibility.Visible && HeroRealContent.Opacity > 0.01;
            bool liveBackdrop = _heroVisual != null && _heroVisual.Opacity > 0.01f;
            bool liveLogo     = _heroLogoVisual != null && _heroLogoVisual.Opacity > 0.01f;

            if (!liveContent && !liveBackdrop && !liveLogo)
            {
                ResetOutgoingStateSync();
                return;
            }

            if (liveContent)  HeroAnimationHelper.AnimateTextOut(HeroRealContent);
            if (liveBackdrop) HeroAnimationHelper.FadeVisualOpacity(_heroVisual!, 0f, HERO_EXIT_DURATION_MS);
            if (liveLogo)     HeroAnimationHelper.FadeVisualOpacity(_heroLogoVisual!, 0f, HERO_EXIT_DURATION_MS);

            await Task.Delay(HERO_EXIT_DURATION_MS).ConfigureAwait(true);

            if (ticket != _currentSessionTicket) return;
            ResetOutgoingStateSync();
        }

        

        private void ResetOutgoingStateSync()
        {
            HeroRealContent.Visibility = Visibility.Collapsed;
            HeroRealContent.Opacity = 0;
            if (_heroImageBrush != null) _heroImageBrush.Surface = null;
            if (_heroLogoBrush != null) _heroLogoBrush.Surface = null;
            if (_heroVisual != null) _heroVisual.Opacity = 0f;
            if (_heroLogoVisual != null) _heroLogoVisual.Opacity = 0f;
        }

        /// <summary>
        /// Shows the shimmer IF assets haven't resolved within SHIMMER_REVEAL_THRESHOLD_MS. For warm-cache
        /// transitions (assets arrive in &lt; threshold), this returns without ever touching the shimmer UI.
        /// </summary>
        private async Task ShowShimmerIfSlowAsync(long ticket, Task assetsTask)
        {
            var grace = Task.Delay(SHIMMER_REVEAL_THRESHOLD_MS);
            var winner = await Task.WhenAny(assetsTask, grace).ConfigureAwait(true);
            if (winner == assetsTask) return;

            if (ticket != _currentSessionTicket) return;
            if (_phase != HeroPhase.Loading) return;
            ShowShimmerWithIntro();
        }

        private void ShowShimmerWithIntro()
        {
            HeroShimmer.Visibility = Visibility.Visible;
            HeroTextShimmer.Visibility = Visibility.Visible;
            HeroShimmer.Opacity = 1;
            HeroTextShimmer.Opacity = 1;

            if (!_shimmerIsVisible)
            {
                HeroAnimationHelper.AnimateShimmerIn(HeroShimmer);
                HeroAnimationHelper.AnimateShimmerIn(HeroTextShimmer);
                _shimmerIsVisible = true;
            }
        }

        /// <summary>
        /// Atomic reveal: bind surfaces (nulls allowed = intentional blank fallback), cross-fade shimmer → content,
        /// animate text and backdrop in, start auto-rotation, and prewarm subsequent items.
        /// </summary>
        private async Task CommitRevealAsync(long ticket, HeroAssets assets, StremioMediaStream item, CancellationToken token)
        {
            if (ticket != _currentSessionTicket) return;
            if (_phase == HeroPhase.TrailerPlaying) return;
            _phase = HeroPhase.Ready;

            HeroLog($"[REVEAL] ticket={ticket} bg={(assets.Backdrop != null ? "OK" : "NULL")} logo={(assets.Logo != null ? "OK" : "NULL")}");

            // Bind surfaces explicitly — nulls included so we never see a stale image through the new frame.
            if (_heroImageBrush != null) _heroImageBrush.Surface = assets.Backdrop;
            if (_heroLogoBrush != null) _heroLogoBrush.Surface = assets.Logo;

            if (_heroVisual != null) _heroVisual.Opacity = 0f;
            if (_heroLogoVisual != null) _heroLogoVisual.Opacity = 0f;

            // Metadata bind always happens at reveal so each Next transition restarts from a clean state.
            PopulateHeroData(item);
            SubscribeToItemChanges(item);

            // Dominant-color notify — reuses the shared bytes so there's no extra fetch.
            if (!string.IsNullOrEmpty(_currentBgUrl))
            {
                var capBg = _currentBgUrl;
                var capTicket = ticket;
                _ = Task.Run(async () =>
                {
                    var colors = await _assetManager.GetBackdropColorsAsync(capBg, CancellationToken.None).ConfigureAwait(false)
                                 ?? await ImageHelper.GetOrExtractColorAsync(capBg).ConfigureAwait(false);
                    if (colors != null && capTicket == _currentSessionTicket)
                    {
                        DispatcherQueue.TryEnqueue(() => NotifyColorChanged(colors.Value.Primary, colors.Value.Secondary));
                    }
                });
            }

            // Cross-fade the shimmer out only if it was actually visible (cold-cache path). For warm-cache
            // transitions the shimmer never showed, so there's nothing to fade.
            if (_shimmerIsVisible)
            {
                HeroAnimationHelper.FadeElement(HeroShimmer, 0, 400);
                HeroAnimationHelper.FadeElement(HeroTextShimmer, 0, 400);
            }

            HeroRealContent.Visibility = Visibility.Visible;
            HeroRealContent.Opacity = 1;
            // Restart text entry every reveal explicitly (no continuation from prior in-flight animation).
            var textVisual = ElementCompositionPreview.GetElementVisual(HeroRealContent);
            try { textVisual?.StopAnimation("Opacity"); } catch { }
            try { textVisual?.StopAnimation("Translation"); } catch { }
            HeroAnimationHelper.AnimateTextIn(HeroRealContent, slideDurationMs: 1200);

            if (_heroVisual != null)
            {
                var compositor = _heroVisual.Compositor;
                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.2f, 1f)));
                fadeIn.Duration = TimeSpan.FromMilliseconds(1200);
                _heroVisual.StartAnimation("Opacity", fadeIn);
            }

            // Symmetric logo fade-in so it doesn't snap-in while the text slides and backdrop fades.
            if (_heroLogoVisual != null && assets.Logo != null)
            {
                var compositor = _heroLogoVisual.Compositor;
                var fadeLogoIn = compositor.CreateScalarKeyFrameAnimation();
                fadeLogoIn.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.2f, 1f)));
                fadeLogoIn.Duration = TimeSpan.FromMilliseconds(600);
                _heroLogoVisual.StartAnimation("Opacity", fadeLogoIn);
            }

            // Hide shimmer after cross-fade is done. If it wasn't visible, collapse immediately.
            if (_shimmerIsVisible)
            {
                var capSess = ticket;
                _ = Task.Delay(450).ContinueWith(_ =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (capSess != _currentSessionTicket) return;
                        HeroShimmer.Visibility = Visibility.Collapsed;
                        HeroTextShimmer.Visibility = Visibility.Collapsed;
                        _shimmerIsVisible = false;
                    });
                });
            }
            else
            {
                HeroShimmer.Visibility = Visibility.Collapsed;
                HeroTextShimmer.Visibility = Visibility.Collapsed;
            }

            UpdateNavigationVisibility();
            StartHeroAutoRotation();

            // Prewarm the next few items in the background.
            if (_heroItems.Count > 1)
            {
                _ = _assetManager.ProcessSecondaryHeroAssetsAsync(_heroItems.Skip(1).Take(4).ToList(), CancellationToken.None);
            }

            // Defer WebView2 warm-up until after the hero reveal animation finishes. This trades a
            // ~300ms first-trailer-click latency penalty for a perfectly smooth initial reveal.
            var warmupTicket = ticket;
            _ = Task.Delay(1600).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (warmupTicket == _currentSessionTicket) TrailerView.WarmUpIfIdle();
                });
            });

            // Queue should not interrupt reveal immediately, but waiting the full 1200ms makes rapid
            // navigation feel sluggish. Keep only a short minimum hold so each item is perceptible.
            try
            {
                await Task.Delay(MIN_REVEAL_HOLD_MS, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { }
        }


        public void SetItems(IEnumerable<StremioMediaStream> items, bool animate = false)
        {
            if (items == null) return;
            _ = ReconcileAsync(items.ToList(), forceHardReset: animate);
        }

        private void UpdateNavigationVisibility()
        {
            // [FIX] Always show navigation arrows if there are items, except when trailer is ACTIVELY playing.
            // Using direct opacity/visibility check to ensure they don't 'disappear' during metadata transitions.
            var isVisible = _heroItems.Count > 1 && _phase != HeroPhase.TrailerPlaying;
            var visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            
            if (HeroPrevButton.Visibility != visibility) HeroPrevButton.Visibility = visibility;
            if (HeroNextButton.Visibility != visibility) HeroNextButton.Visibility = visibility;
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

        /// <summary>When the parent becomes visible again, only restart the timer if hero copy is already revealed (not during skeleton).</summary>
        public void ResumeHeroAutoRotationIfRevealed()
        {
            if (_phase != HeroPhase.Ready || HeroRealContent.Visibility != Visibility.Visible) return;
            if (this.Visibility != Visibility.Visible) return;
            StartHeroAutoRotation();
        }

        public void StartHeroAutoRotation()
        {
            if (_isStartingRotation) return;

            _isStartingRotation = true;
            try
            {
                StopAutoRotation();

                // [FIX] Don't start rotation if we are explicitly hidden
                if (this.Visibility != Visibility.Visible) return;

                _heroAutoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                _heroAutoTimer.Tick += (s, e) =>
                {
                    // [MODERN] We allow rotation even if ActualWidth is 0 (scrolled off-screen in some layouts)
                    // but we REQUIRE the control itself to be in the 'Visible' state.
                    if (this.Visibility != Visibility.Visible || this.XamlRoot == null || TrailerView.IsPlaying)
                        return;

                    if (_heroItems.Count > 1)
                    {
                        EnqueueNavigationStep(+1);
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
            try
            {
                // [FIX] Don't notify or extract if we are hidden or shutting down.
                // Accessing Visibility on a disposed control throws COMException.
                if (this.XamlRoot == null || this.Visibility != Visibility.Visible) return;

                var now = DateTime.Now;
                var elapsed = (now - _lastColorChangeTime).TotalMilliseconds;

                if (elapsed < MIN_COLOR_CHANGE_INTERVAL_MS) return;

                _lastColorChangeTime = now;

                // Only notify the parent (MediaLibraryPage) to update the DynamicBackdrop
                ColorExtracted?.Invoke(this, (primary, secondary));
            }
            catch (Exception ex)
            {
                // Safely ignore property access errors during shutdown
                System.Diagnostics.Debug.WriteLine($"[HeroSection] NotifyColorChanged Ignore: {ex.Message}");
            }
        }

        private bool IsSessionActive(long sessionTicket) => sessionTicket == _currentSessionTicket;

        /// <summary>
        /// External callers (discovery, page switch) request shimmer. We honor the request by just ensuring
        /// the shimmer UI is visible — the actual pipeline reset happens in <see cref="ApplyTransitionInternalAsync"/>
        /// when ReconcileAsync is invoked. When isLoading is false, we no longer force reveal here — reveal is
        /// strictly driven by the asset gate.
        /// </summary>
        public void SetLoading(bool isLoading, bool silent = false, bool resetHeroPipelineState = true)
        {
            if (!isLoading || silent) return;

            ShowShimmerWithIntro();

            if (HeroRealContent.Visibility == Visibility.Visible && HeroRealContent.Opacity > 0.01)
            {
                HeroAnimationHelper.FadeElement(HeroRealContent, 0.0, 250);
                long capTicket = _currentSessionTicket;
                _ = Task.Delay(260).ContinueWith(_ =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (capTicket == _currentSessionTicket && _phase == HeroPhase.Loading)
                            HeroRealContent.Visibility = Visibility.Collapsed;
                    });
                });
            }

            if (_heroVisual != null) _heroVisual.Opacity = 0f;
            if (_heroLogoVisual != null) _heroLogoVisual.Opacity = 0f;
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
            if (sender is not StremioMediaStream item) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                bool isActive = (_heroItems.Count > _currentHeroIndex && _heroItems[_currentHeroIndex] == item);
                if (!isActive) return;

                if (e.PropertyName == nameof(item.Description) || e.PropertyName == nameof(item.Rating) ||
                    e.PropertyName == nameof(item.Year) || e.PropertyName == nameof(item.Genres))
                {
                    PopulateHeroData(item);
                    return;
                }

                // Logo or backdrop URL arrived later via enrichment — route through the reconciler so the
                // standard shimmer-first flow handles it (no ad-hoc surface swapping which was prone to flicker).
                if (e.PropertyName == nameof(item.LogoUrl) ||
                    e.PropertyName == nameof(item.Banner) ||
                    e.PropertyName == nameof(item.LandscapeImageUrl) ||
                    e.PropertyName == "Meta")
                {
                    string? newBg = item.Meta?.Background ?? item.PosterUrl;
                    bool changed = item.LogoUrl != _currentLogoUrl || newBg != _currentBgUrl;
                    if (changed && _phase == HeroPhase.Ready)
                    {
                        // Soft update via SilentSync → the reconciler will no-op if nothing actually changed.
                        _ = ReconcileAsync();
                    }
                    else if (changed && _phase == HeroPhase.Loading)
                    {
                        _ = ReconcileAsync();
                    }
                }
            });
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
            if (!TrailerView.IsPlaying)
            {
                SetGlyph(HeroTrailerIcon, "\uE714");
                SetText(HeroTrailerText, "Fragmanı İzle");
            }
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
            if (_heroItems.Count == 0) return;
            EnqueueNavigationStep(+1);
            ResetHeroAutoTimer();
        }

        private void HeroPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItems.Count == 0) return;
            EnqueueNavigationStep(-1);
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

            _activeTrailerCandidates = BuildTrailerCandidates(item);
            _activeTrailerIndex = -1;

            // [NEW/RESTORED] Fetch metadata if trailer candidates are missing
            if (_activeTrailerCandidates.Count == 0)
            {
                SetText(HeroTrailerText, "Yükleniyor...");
                try
                {
                    var unified = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(item, Models.Metadata.MetadataContext.Spotlight);
                    if (unified != null)
                    {
                        item.UpdateFromUnified(unified);
                        _activeTrailerCandidates = BuildTrailerCandidates(item, unified);
                    }
                }
                catch { }
            }

            if (_activeTrailerCandidates.Count == 0)
            {
                SetText(HeroTrailerText, "Fragman Yok");
                await Task.Delay(1500);
                if (!TrailerView.IsPlaying) SetText(HeroTrailerText, "Fragmanı İzle");
                return;
            }

            // Visual feedback
            SetText(HeroTrailerText, "Hazırlanıyor...");
            StopAutoRotation();

            await TryPlayNextTrailerCandidateAsync();
        }

        private async Task<bool> TryPlayNextTrailerCandidateAsync()
        {
            if (_activeTrailerCandidates.Count == 0) return false;

            while (_activeTrailerIndex + 1 < _activeTrailerCandidates.Count)
            {
                _activeTrailerIndex++;
                string candidate = _activeTrailerCandidates[_activeTrailerIndex];
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                SetText(HeroTrailerText, _activeTrailerIndex == 0 ? "Hazırlanıyor..." : "Alternatif deneniyor...");
                await TrailerView.PlayTrailerAsync(candidate);
                return true;
            }

            return false;
        }

        private List<string> BuildTrailerCandidates(StremioMediaStream item, Models.Metadata.UnifiedMetadata? metadata = null)
        {
            var list = new List<string>();

            void add(string? source)
            {
                string? normalized = NormalizeTrailerCandidate(source);
                if (string.IsNullOrWhiteSpace(normalized)) return;
                string key = GetTrailerDedupKey(normalized);
                if (!list.Any(x => GetTrailerDedupKey(x) == key))
                {
                    list.Add(normalized);
                }
            }

            add(item.TrailerUrl);
            if (metadata != null)
            {
                add(metadata.TrailerUrl);
                if (metadata.TrailerCandidates != null)
                {
                    foreach (var trailer in metadata.TrailerCandidates) add(trailer);
                }
            }

            if (item.Meta?.Trailers != null)
            {
                foreach (var trailer in item.Meta.Trailers.Where(t => !string.IsNullOrWhiteSpace(t.Source)))
                    add(trailer.Source);
            }

            if (item.Meta?.TrailerStreams != null)
            {
                foreach (var trailer in item.Meta.TrailerStreams.Where(t => !string.IsNullOrWhiteSpace(t.YtId)))
                    add(trailer.YtId);
            }

            if (!string.IsNullOrWhiteSpace(item.Meta?.AppExtras?.Trailer))
                add(item.Meta.AppExtras.Trailer);

            return list;
        }

        private static string? NormalizeTrailerCandidate(string? source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            string value = source.Trim();
            if (!value.Contains("/") && !value.Contains(".")) return $"https://www.youtube.com/watch?v={value}";

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                string host = uri.Host.ToLowerInvariant();
                if (host.Contains("youtu.be"))
                {
                    string id = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(id))
                        return $"https://www.youtube.com/watch?v={id}";
                }

                if (host.Contains("youtube.com"))
                {
                    string? v = ExtractQueryParam(uri.Query, "v");
                    if (!string.IsNullOrWhiteSpace(v))
                        return $"https://www.youtube.com/watch?v={v}";

                    var parts = uri.AbsolutePath.Trim('/').Split('/');
                    int embedIndex = Array.FindIndex(parts, p => p.Equals("embed", StringComparison.OrdinalIgnoreCase));
                    if (embedIndex >= 0 && embedIndex + 1 < parts.Length)
                        return $"https://www.youtube.com/watch?v={parts[embedIndex + 1]}";
                }
            }

            return value;
        }

        private static string GetTrailerDedupKey(string normalized)
        {
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                string? v = ExtractQueryParam(uri.Query, "v");
                if (!string.IsNullOrWhiteSpace(v))
                    return $"yt:{v.Trim().ToLowerInvariant()}";
            }
            return normalized.Trim().ToLowerInvariant();
        }

        private static string? ExtractQueryParam(string query, string key)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (!part.Substring(0, eq).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }
    }
}
