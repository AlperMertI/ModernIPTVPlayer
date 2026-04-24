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
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class HeroSectionControl : UserControl
    {
        // Events
        public event EventHandler<IMediaStream> PlayAction;
        public event EventHandler<(Windows.UI.Color Primary, Windows.UI.Color Secondary)> ColorExtracted;

        private static void HeroLog(string msg) => Helpers.HeroTracer.Log(msg);
        private enum HeroPhase { Loading, Ready, TrailerPlaying }

        private sealed class HeroAssets
        {
            public LoadedImageSurface? Backdrop;
            public LoadedImageSurface? Logo;
            public bool HasLogoUrl;
        }

        private readonly SemaphoreSlim _reconcilerLock = new(1, 1);
        private long _currentSessionTicket = 0;
        private HeroPhase _phase = HeroPhase.Loading;
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

        private volatile bool _isStoppingRotation = false;
        private volatile bool _isStartingRotation = false;
        private DateTime _lastColorChangeTime = DateTime.MinValue;
        private const int MIN_COLOR_CHANGE_INTERVAL_MS = 100;
        private const int BACKDROP_BUDGET_MS = 6000;
        private List<string> _activeTrailerCandidates = new();
        private int _activeTrailerIndex = -1;
        private bool _isHandlingTrailerFallback = false;
        private HeroAssets? _activeAssets;

        public HeroSectionControl()
        {
            // #region agent log
            try { ModernIPTVPlayer.App.DebugNdjson("HeroSectionControl.xaml.cs:ctor", "enter", null, "H6-H7-H8"); } catch { }
            // #endregion
            this.InitializeComponent();
            // #region agent log
            try { ModernIPTVPlayer.App.DebugNdjson("HeroSectionControl.xaml.cs:ctor", "InitializeComponent done", null, "H6-H7-H8"); } catch { }
            // #endregion
            _assetManager = new HeroAssetManager(this.DispatcherQueue, HeroLog);
            HeroRealContent.Visibility = Visibility.Collapsed;
            HeroRealContent.Opacity = 0;

            TrailerView.PlayStateChanged += (s, isPlaying) => {
                if (isPlaying)
                {
                    _phase = HeroPhase.TrailerPlaying;
                    var sb = new Storyboard();
                    var trailerFade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(800), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    Storyboard.SetTarget(trailerFade, VideoContainer);
                    Storyboard.SetTargetProperty(trailerFade, "Opacity");
                    sb.Children.Add(trailerFade);
                    sb.Begin();

                    VideoContainer.IsHitTestVisible = false;
                    if (_heroVisual != null)
                    {
                        var compositor = _heroVisual.Compositor;
                        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                        fadeOut.InsertKeyFrame(1f, 0f);
                        fadeOut.Duration = TimeSpan.FromMilliseconds(500);
                        _heroVisual.StartAnimation("Opacity", fadeOut);
                    }
                    SetGlyph(HeroTrailerIcon, "\uE71A");
                    SetText(HeroTrailerText, "Durdur");
                    StopAutoRotation();
                    UpdateNavigationVisibility();
                    TrailerView.Focus(FocusState.Programmatic);
                    NotifyColorChanged(Windows.UI.Color.FromArgb(255, 0, 0, 0), Windows.UI.Color.FromArgb(255, 0, 0, 0));
                }
                else
                {
                    _phase = HeroPhase.Ready;
                    var sb = new Storyboard();
                    var trailerFade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    Storyboard.SetTarget(trailerFade, VideoContainer);
                    Storyboard.SetTargetProperty(trailerFade, "Opacity");
                    sb.Children.Add(trailerFade);
                    sb.Begin();
                    VideoContainer.IsHitTestVisible = false;
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
                    if (_heroItems.Count > _currentHeroIndex)
                    {
                        var item = _heroItems[_currentHeroIndex];
                        string? bgUrl = item.Meta?.Background ?? item.PosterUrl;
                        if (!string.IsNullOrEmpty(bgUrl))
                        {
                            _ = Task.Run(async () => {
                                var colors = await ImageHelper.GetOrExtractColorAsync(bgUrl);
                                if (colors != null) DispatcherQueue.TryEnqueue(() => NotifyColorChanged(colors.Value.Primary, colors.Value.Secondary));
                            });
                        }
                    }
                }
            };

            TrailerView.VideoEnded += (s, e) => ResetHeroAutoTimer();
            TrailerView.PlaybackError += async (s, code) => {
                if (_isHandlingTrailerFallback) return;
                _isHandlingTrailerFallback = true;
                try {
                    bool embedBlocked = code == 150 || code == 101;
                    bool moved = await TryPlayNextTrailerCandidateAsync();
                    if (!moved) {
                        SetText(HeroTrailerText, embedBlocked ? "Bu fragman oynatılamıyor" : "Fragman açılamadı");
                        await Task.Delay(1500);
                        if (!TrailerView.IsPlaying) SetText(HeroTrailerText, "Fragmanı İzle");
                    }
                } finally { _isHandlingTrailerFallback = false; }
            };
            
            this.Loaded += (s, e) =>
            {
                // #region agent log
                try { ModernIPTVPlayer.App.DebugNdjson("HeroSectionControl.xaml.cs:Loaded", "enter", null, "H10"); } catch { }
                // #endregion
                SetupHeroCompositionMask();
                // #region agent log
                try { ModernIPTVPlayer.App.DebugNdjson("HeroSectionControl.xaml.cs:Loaded", "SetupHeroCompositionMask done", null, "H10"); } catch { }
                // #endregion
            };
            this.Unloaded += HeroSectionControl_Unloaded;
        }

        private void HeroSectionControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _heroCts?.Cancel();
            _heroCts = null;
            StopAutoRotation();
            if (_lastSubscribedItem != null) { _lastSubscribedItem.PropertyChanged -= Item_PropertyChanged; _lastSubscribedItem = null; }
            
            // PROJECT ZERO: Unpin items on unload
            foreach(var item in _heroItems) item.Unpin();
        }

        private void SetText(TextBlock control, string? value) { if (control.Text != (value ?? "")) control.Text = value ?? ""; }
        private void SetVisibility(FrameworkElement control, Visibility visibility) { if (control.Visibility != visibility) control.Visibility = visibility; }
        private void SetOpacity(FrameworkElement control, double opacity) { if (Math.Abs(control.Opacity - opacity) > 0.01) control.Opacity = opacity; }
        private void SetGlyph(FontIcon control, string glyph) { if (control.Glyph != glyph) control.Glyph = glyph; }

        public async Task ReconcileAsync(List<StremioMediaStream>? newItems = null, bool forceHardReset = false)
        {
            try {
                if (newItems != null) {
                    foreach(var old in _heroItems) old.Unpin();
                    _heroItems = newItems;
                    foreach(var item in _heroItems) item.Pin();
                    HeroLog($"Data Sync: Received {_heroItems.Count} items");
                }
                if (_heroItems.Count == 0) return;
                await ApplyTransitionInternalAsync(forceHardReset);
            } catch (Exception ex) { HeroLog($"[RECONCILE] Exception: {ex.Message}"); }
        }

        private void EnqueueNavigationStep(int delta) {
            if (delta == 0 || _heroItems.Count == 0) return;
            lock (_navigationQueueLock) { _navigationQueue.Enqueue(delta); if (_isNavigationPumpActive) return; _isNavigationPumpActive = true; }
            _ = PumpNavigationQueueAsync();
        }

        private async Task PumpNavigationQueueAsync() {
            while (true) {
                int step;
                lock (_navigationQueueLock) { if (_navigationQueue.Count == 0) { _isNavigationPumpActive = false; return; } step = _navigationQueue.Dequeue(); }
                if (_heroItems.Count == 0) continue;
                _currentHeroIndex = NormalizeIndex(_currentHeroIndex + step, _heroItems.Count);
                _currentHeroId = _heroItems[_currentHeroIndex].IMDbId ?? _heroItems[_currentHeroIndex].Id.ToString();
                await ReconcileAsync();
            }
        }

        private static int NormalizeIndex(int index, int count) { if (count <= 0) return 0; int mod = index % count; return mod < 0 ? mod + count : mod; }

        private void SetupHeroCompositionMask() {
            try {
                if (HeroImageHost.XamlRoot == null) return;
                if (_heroVisual != null) { ElementCompositionPreview.SetElementChildVisual(HeroImageHost, _heroVisual); _compositionReadyTcs.TrySetResult(); return; }
                var hostVisual = ElementCompositionPreview.GetElementVisual(HeroImageHost);
                var compositor = hostVisual.Compositor;
                _heroImageBrush = compositor.CreateSurfaceBrush();
                _heroImageBrush.Stretch = Microsoft.UI.Composition.CompositionStretch.UniformToFill;
                _heroAlphaMask = compositor.CreateLinearGradientBrush();
                _heroAlphaMask.EndPoint = new Vector2(0, 1);
                _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Windows.UI.Color.FromArgb(255, 255, 255, 255)));
                _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.85f, Windows.UI.Color.FromArgb(255, 255, 255, 255)));
                _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Windows.UI.Color.FromArgb(0, 255, 255, 255)));
                _heroMaskBrush = compositor.CreateMaskBrush();
                _heroMaskBrush.Source = _heroImageBrush;
                _heroMaskBrush.Mask = _heroAlphaMask;
                _heroVisual = compositor.CreateSpriteVisual();
                _heroVisual.Brush = _heroMaskBrush;
                _heroVisual.Size = new Vector2((float)HeroImageHost.ActualWidth, (float)HeroImageHost.ActualHeight);
                _heroVisual.Opacity = 0f;
                ElementCompositionPreview.SetElementChildVisual(HeroImageHost, _heroVisual);
                ElementCompositionPreview.SetIsTranslationEnabled(HeroImageHost, true);
                HeroImageHost.SizeChanged += (s, e) => { if (_heroVisual != null) _heroVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height); };
                HeroAnimationHelper.ApplyKenBurns(_heroVisual);
                _heroLogoBrush = compositor.CreateSurfaceBrush();
                _heroLogoBrush.Stretch = Microsoft.UI.Composition.CompositionStretch.Uniform;
                _heroLogoVisual = compositor.CreateSpriteVisual();
                _heroLogoVisual.Brush = _heroLogoBrush;
                _heroLogoVisual.Size = new Vector2(500f, 120f);
                ElementCompositionPreview.SetElementChildVisual(HeroLogoHost, _heroLogoVisual);
                _compositionReadyTcs.TrySetResult();
            } catch (Exception ex) { HeroLog($"Composition Error: {ex.Message}"); }
        }

        private const int HERO_EXIT_DURATION_MS = 300;
        private const int MIN_REVEAL_HOLD_MS = 650;
        private const int SHIMMER_REVEAL_THRESHOLD_MS = 500;

        private async Task ApplyTransitionInternalAsync(bool forceHardReset = false) {
            await _compositionReadyTcs.Task;
            await _reconcilerLock.WaitAsync();
            try {
                int targetIndex = ResolveTargetIndex(forceHardReset);
                if (targetIndex < 0) return;
                var item = _heroItems[targetIndex];
                string? newBg = item.Meta?.Background ?? item.PosterUrl;
                if (!forceHardReset && item.LogoUrl == _currentLogoUrl && newBg == _currentBgUrl && !string.IsNullOrEmpty(_currentHeroId)) {
                    _currentHeroIndex = targetIndex; PopulateHeroData(item); UpdateNavigationVisibility(); return;
                }
                long ticket = ++_currentSessionTicket;
                _currentHeroIndex = targetIndex;
                _currentHeroId = item.IMDbId ?? item.Id.ToString();
                _currentBgUrl = newBg; _currentLogoUrl = item.LogoUrl;
                var assets = new HeroAssets { HasLogoUrl = !string.IsNullOrEmpty(item.LogoUrl) };
                _activeAssets = assets; _phase = HeroPhase.Loading; StopAutoRotation();
                _heroCts?.Cancel(); _heroCts = new CancellationTokenSource();
                var token = _heroCts.Token; _assetManager.Clear(_currentLogoUrl, _currentBgUrl);
                var exitTask = PlayExitAsync(ticket);
                var backdropTask = _assetManager.GetBackdropSurfaceAsync(_currentBgUrl, token);
                var logoTask = assets.HasLogoUrl ? _assetManager.GetLogoSurfaceAsync(_currentLogoUrl, token) : Task.FromResult<LoadedImageSurface?>(null);
                var assetsTask = Task.WhenAll(backdropTask, logoTask);
                _ = ShowShimmerIfSlowAsync(ticket, assetsTask);
                var budget = Task.Delay(BACKDROP_BUDGET_MS, token);
                await Task.WhenAny(assetsTask, budget).ConfigureAwait(true);
                await exitTask.ConfigureAwait(true);
                if (ticket != _currentSessionTicket) return;
                if (!assetsTask.IsCompleted) ShowShimmerWithIntro();
                if (!assetsTask.IsCompleted) { try { await assetsTask.ConfigureAwait(true); } catch { return; } if (ticket != _currentSessionTicket) return; }
                assets.Backdrop = backdropTask.IsCompletedSuccessfully ? backdropTask.Result : null;
                assets.Logo = logoTask.IsCompletedSuccessfully ? logoTask.Result : null;
                await CommitRevealAsync(ticket, assets, item, token).ConfigureAwait(true);
                UpdateNavigationVisibility();
            } finally { _reconcilerLock.Release(); }
        }

        private int ResolveTargetIndex(bool force) {
            if (_heroItems.Count == 0) return -1;
            if (force || string.IsNullOrEmpty(_currentHeroId)) return 0;
            int idx = _heroItems.FindIndex(x => (x.IMDbId ?? x.Id.ToString()) == _currentHeroId);
            return idx == -1 ? 0 : idx;
        }

        private async Task PlayExitAsync(long ticket) {
            bool liveC = HeroRealContent.Visibility == Visibility.Visible;
            if (liveC) HeroAnimationHelper.AnimateTextOut(HeroRealContent);
            if (_heroVisual != null) HeroAnimationHelper.FadeVisualOpacity(_heroVisual, 0f, 300);
            if (_heroLogoVisual != null) HeroAnimationHelper.FadeVisualOpacity(_heroLogoVisual, 0f, 300);
            await Task.Delay(300).ConfigureAwait(true);
            if (ticket == _currentSessionTicket) ResetOutgoingStateSync();
        }

        private void ResetOutgoingStateSync() {
            HeroRealContent.Visibility = Visibility.Collapsed;
            if (_heroImageBrush != null) _heroImageBrush.Surface = null;
            if (_heroLogoBrush != null) _heroLogoBrush.Surface = null;
            if (_heroVisual != null) _heroVisual.Opacity = 0f;
            if (_heroLogoVisual != null) _heroLogoVisual.Opacity = 0f;
        }

        private async Task ShowShimmerIfSlowAsync(long t, Task a) {
            if (await Task.WhenAny(a, Task.Delay(SHIMMER_REVEAL_THRESHOLD_MS)) != a && t == _currentSessionTicket) ShowShimmerWithIntro();
        }

        private void ShowShimmerWithIntro() {
            HeroShimmer.Visibility = Visibility.Visible; HeroTextShimmer.Visibility = Visibility.Visible;
            if (!_shimmerIsVisible) { HeroAnimationHelper.AnimateShimmerIn(HeroShimmer); HeroAnimationHelper.AnimateShimmerIn(HeroTextShimmer); _shimmerIsVisible = true; }
        }

        private async Task CommitRevealAsync(long t, HeroAssets a, StremioMediaStream item, CancellationToken tok) {
            if (t !=_currentSessionTicket) return; _phase = HeroPhase.Ready;
            if (_heroImageBrush != null) _heroImageBrush.Surface = a.Backdrop;
            if (_heroLogoBrush != null) _heroLogoBrush.Surface = a.Logo;
            PopulateHeroData(item); SubscribeToItemChanges(item);

            if (!string.IsNullOrEmpty(_currentBgUrl)) {
                var bg = _currentBgUrl;
                _ = Task.Run(async () => {
                    var colors = await ImageHelper.GetOrExtractColorAsync(bg);
                    if (colors != null && t == _currentSessionTicket) DispatcherQueue.TryEnqueue(() => NotifyColorChanged(colors.Value.Primary, colors.Value.Secondary));
                });
            }

            if (_shimmerIsVisible) { HeroAnimationHelper.FadeElement(HeroShimmer, 0, 400); HeroAnimationHelper.FadeElement(HeroTextShimmer, 0, 400); }
            HeroRealContent.Visibility = Visibility.Visible; HeroRealContent.Opacity = 1;
            HeroAnimationHelper.AnimateTextIn(HeroRealContent);
            if (_heroVisual != null) HeroAnimationHelper.FadeVisualOpacity(_heroVisual, 1f, 1000);
            if (_heroLogoVisual != null && a.Logo != null) HeroAnimationHelper.FadeVisualOpacity(_heroLogoVisual, 1f, 600);

            _ = Task.Delay(450).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => {
                if (t == _currentSessionTicket) { HeroShimmer.Visibility = Visibility.Collapsed; HeroTextShimmer.Visibility = Visibility.Collapsed; _shimmerIsVisible = false; }
            }));

            UpdateNavigationVisibility(); StartHeroAutoRotation();
            if (_heroItems.Count > 1) _ = _assetManager.ProcessSecondaryHeroAssetsAsync(_heroItems.Skip(1).Take(4).ToList(), tok);
            try { await Task.Delay(MIN_REVEAL_HOLD_MS, tok).ConfigureAwait(true); } catch { }
        }

        public void SetItems(IEnumerable<StremioMediaStream>? items, bool animate = false) => ReconcileAsync(items?.ToList() ?? new List<StremioMediaStream>(), animate);

        private void UpdateNavigationVisibility() {
            var v = (_heroItems.Count > 1 && _phase != HeroPhase.TrailerPlaying) ? Visibility.Visible : Visibility.Collapsed;
            HeroPrevButton.Visibility = v; HeroNextButton.Visibility = v;
        }

        public void StopAutoRotation() { if (_heroAutoTimer != null) { _heroAutoTimer.Stop(); _heroAutoTimer = null; } }
        public void StartHeroAutoRotation() {
            StopAutoRotation(); if (this.Visibility != Visibility.Visible) return;
            _heroAutoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _heroAutoTimer.Tick += (s, e) => { if (this.Visibility == Visibility.Visible && !TrailerView.IsPlaying) EnqueueNavigationStep(1); };
            _heroAutoTimer.Start();
        }

        private void ResetHeroAutoTimer() { _heroAutoTimer?.Stop(); _heroAutoTimer?.Start(); }
        private void NotifyColorChanged(Windows.UI.Color p, Windows.UI.Color s) { if (this.XamlRoot != null && this.Visibility == Visibility.Visible) ColorExtracted?.Invoke(this, (p, s)); }

        public void SetLoading(bool isLoading, bool silent = false, bool reset = true) {
            if (!isLoading || silent) return;
            ShowShimmerWithIntro();
            if (_heroVisual != null) _heroVisual.Opacity = 0f;
            if (_heroLogoVisual != null) _heroLogoVisual.Opacity = 0f;
        }

        private StremioMediaStream? _lastSubscribedItem = null;
        private void SubscribeToItemChanges(StremioMediaStream item) {
            if (_lastSubscribedItem != null) _lastSubscribedItem.PropertyChanged -= Item_PropertyChanged;
            _lastSubscribedItem = item; if (item != null) item.PropertyChanged += Item_PropertyChanged;
        }

        private void Item_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) {
            if (sender is not StremioMediaStream item) return;
            DispatcherQueue.TryEnqueue(() => {
                if (_heroItems.Count > _currentHeroIndex && _heroItems[_currentHeroIndex] == item) PopulateHeroData(item);
            });
        }

        private void PopulateHeroData(StremioMediaStream item) {
            SetText(HeroYear, item.Year); 
            
            // [FIX] Format Rating to N1 (e.g., 8.5) and handle 10x multiplier cases (94.0 -> 9.4)
            string rawRating = item.Rating ?? "";
            if (double.TryParse(rawRating.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                if (val > 10) val /= 10.0;
                SetText(HeroRating, val.ToString("N1", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                SetText(HeroRating, rawRating);
            }

            SetText(HeroOverview, !string.IsNullOrEmpty(item.Description) ? item.Description : "Sinematik bir serüven sizi bekliyor.");
            SetVisibility(HeroLogoContainer, !string.IsNullOrEmpty(item.LogoUrl) ? Visibility.Visible : Visibility.Collapsed);
            SetVisibility(HeroTitle, string.IsNullOrEmpty(item.LogoUrl) ? Visibility.Visible : Visibility.Collapsed);
            if (string.IsNullOrEmpty(item.LogoUrl)) SetText(HeroTitle, item.Title);
            SetText(HeroGenres, item.Genres);
            SetVisibility(HeroGenres, !string.IsNullOrEmpty(item.Genres) ? Visibility.Visible : Visibility.Collapsed);

            // [RESTORATION FIX] Ensure trailer button is visible if ANY metadata indicates a trailer exists.
            // Some items might only have the TrailerUrl field populated after background enrichment.
            bool hasTrailer = !string.IsNullOrEmpty(item.TrailerUrl) || 
                              (item.Meta?.Trailers != null && item.Meta.Trailers.Count > 0) ||
                              !string.IsNullOrEmpty(item.IMDbId); // Fallback: Assume IMDb items might have trailers

            SetVisibility(HeroTrailerButton, hasTrailer ? Visibility.Visible : Visibility.Collapsed);
        }

        private void HeroPlayButton_Click(object sender, RoutedEventArgs e) { if (_heroItems.Count > _currentHeroIndex) PlayAction?.Invoke(this, _heroItems[_currentHeroIndex]); }
        private void HeroNext_Click(object sender, RoutedEventArgs e) { EnqueueNavigationStep(1); ResetHeroAutoTimer(); }
        private void HeroPrev_Click(object sender, RoutedEventArgs e) { EnqueueNavigationStep(-1); ResetHeroAutoTimer(); }

        private async void HeroTrailerButton_Click(object sender, RoutedEventArgs e) {
            if (_heroItems.Count <= _currentHeroIndex) return;
            if (TrailerView.IsPlaying) { await TrailerView.StopAsync(); return; }
            
            // [OPTIMIZATION] Eagerly warm up the trailer control
            TrailerView.WarmUpIfIdle();

            _activeTrailerCandidates = BuildTrailerCandidates(_heroItems[_currentHeroIndex]);
            if (!_activeTrailerCandidates.Any()) 
            {
                 // Try one last-ditch effort: get detailed metadata if not enriched
                 var detail = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(_heroItems[_currentHeroIndex]);
                 if (detail != null && !string.IsNullOrEmpty(detail.TrailerUrl))
                 {
                     _activeTrailerCandidates.Add(detail.TrailerUrl);
                 }
            }

            if (!_activeTrailerCandidates.Any()) return;
            StopAutoRotation(); await TryPlayNextTrailerCandidateAsync();
        }

        private async Task<bool> TryPlayNextTrailerCandidateAsync() {
            while (_activeTrailerIndex + 1 < _activeTrailerCandidates.Count) {
                _activeTrailerIndex++; 
                string id = ExtractYouTubeId(_activeTrailerCandidates[_activeTrailerIndex]);
                await TrailerView.PlayTrailerAsync(id); 
                return true;
            }
            return false;
        }

        private string ExtractYouTubeId(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            if (!source.Contains("/") && !source.Contains(".")) return source; // Already an ID

            try {
                if (source.Contains("v=")) {
                    var split = source.Split("v=");
                    if (split.Length > 1) return split[1].Split('&')[0];
                } else if (source.Contains("be/")) {
                    var split = source.Split("be/");
                    if (split.Length > 1) return split[1].Split('?')[0];
                } else if (source.Contains("embed/")) {
                    var split = source.Split("embed/");
                    if (split.Length > 1) return split[1].Split('?')[0];
                }
            } catch { }
            return source;
        }

        private List<string> BuildTrailerCandidates(StremioMediaStream item) {
            var l = new List<string>();
            if (!string.IsNullOrEmpty(item.TrailerUrl)) l.Add(item.TrailerUrl);
            if (item.Meta?.Trailers != null) 
            {
                foreach(var t in item.Meta.Trailers) 
                {
                    if (!string.IsNullOrEmpty(t.Source)) l.Add(t.Source);
                }
            }
            return l.Distinct().ToList();
        }

        public void ResumeHeroAutoRotationIfRevealed() { if (_phase == HeroPhase.Ready && this.Visibility == Visibility.Visible) StartHeroAutoRotation(); }
    }
}
