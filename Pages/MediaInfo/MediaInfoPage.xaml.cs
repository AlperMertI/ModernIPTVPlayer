using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using ModernIPTVPlayer.Controls;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Metadata;
using ModernIPTVPlayer.Services.Stremio;
using ModernIPTVPlayer.Services.Iptv;
using ModernIPTVPlayer.Services.MediaInfo;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models; // Ensure Models namespace is included
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Input;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using Windows.UI;
using Microsoft.UI.Xaml.Media;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace ModernIPTVPlayer
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class MediaInfoPage : Page, IMediaInfoUIProxy, IBackgroundView
    {
        private MediaInfoCommitService _commitService;
        private VisualStateController _visualStateController;
        private AnimationCoordinator _animationCoordinator;
        private LoadPipeline _loadPipeline;
        private LayoutScheduler _layoutScheduler;
        private Services.MediaInfo.PageOrchestrator _pageOrchestrator;
        private Services.MediaInfo.PlayerHandoffManager _playerHandoffManager;
        private Services.MediaInfo.TrailerManager _trailerManager;
        private Services.MediaInfo.ParallaxController _parallaxController;
        private Services.MediaInfo.ConnectedAnimationManager _connectedAnimationManager;
        private Services.MediaInfo.EpisodesManager _episodesManager;
        private Services.MediaInfo.CastDirectorManager _castDirectorManager;
        private Services.MediaInfo.ActionHandlerManager _actionHandlerManager;
        private Services.MediaInfo.DetailPanelController _detailPanelController;
        private Services.MediaInfo.SourcesManager _sourcesManager;
        private Services.MediaInfo.BackgroundManager _backgroundManager;
        private Services.MediaInfo.StremioSourcesService _stremioSourcesService;
        
        private IMediaStream _item;
        private bool _isProgrammaticSelection;
        private bool _isApplyingSourceSelection;
        private bool _suppressSourceSelectionUi;
        private System.Collections.ObjectModel.ObservableCollection<StremioAddonViewModel> _addonResults;
        private readonly System.Collections.ObjectModel.ObservableCollection<StremioStreamViewModel> _visibleSourceStreams = new();
        private Compositor _compositor;
        private string _streamUrl;
        private DispatcherTimer? _personHoverTimer;
        private FrameworkElement? _pendingPersonSource;
        private bool _isPointerOverPersonCard;
        private CancellationTokenSource? _personCloseCts;
        private FrameworkElement? _activePersonAnchorSource;
        private CastItem? _activePersonCardItem;
        private readonly HashSet<Button> _initializedButtonInteractions = new();
        private readonly HashSet<Button> _initializedMagneticButtons = new();
        
        // Series Data
        public ObservableCollection<SeasonItem> Seasons { get; private set; } = new();
        public ObservableCollection<EpisodeItem> CurrentEpisodes => _episodesManager?.CurrentEpisodes ?? _episodesFallback;
        private readonly ObservableCollection<EpisodeItem> _episodesFallback = new();
        public ObservableCollection<CastItem> CastList => _castDirectorManager?.CastList ?? _castFallback;
        private readonly ObservableCollection<CastItem> _castFallback = new();
        public ObservableCollection<CastItem> DirectorList => _castDirectorManager?.DirectorList ?? _directorFallback;
        private readonly ObservableCollection<CastItem> _directorFallback = new();

        // [UI PROXY IMPLEMENTATION]
        Microsoft.UI.Dispatching.DispatcherQueue IMediaInfoUIProxy.DispatcherQueue => this.DispatcherQueue;
        string IMediaInfoUIProxy.StreamUrl { get => _streamUrl; set { _streamUrl = value; } }
        ModernIPTVPlayer.Models.Metadata.UnifiedMetadata IMediaInfoUIProxy.Metadata { set => _unifiedMetadata = value; }
        bool IMediaInfoUIProxy.IsLogoImageLoaded => _isLogoImageLoaded;
        bool IMediaInfoUIProxy.IsLogoPending { get => _isLogoPending; set => _isLogoPending = value; }
        bool IMediaInfoUIProxy.IsLogoReady { get => _isLogoReady; set => _isLogoReady = value; }
        bool IMediaInfoUIProxy.IsLogoFallbackActive { get => _isLogoFallbackActive; set => _isLogoFallbackActive = value; }
        string IMediaInfoUIProxy.CurrentLogoUrl { get => _currentLogoUrl; set => _currentLogoUrl = value; }
        DateTime IMediaInfoUIProxy.NavigationStartTime => _navigationStartTime;

        // [BACKGROUND VIEW IMPLEMENTATION]
        Microsoft.UI.Xaml.Controls.Image IBackgroundView.HeroImage => HeroImage;
        Microsoft.UI.Xaml.Controls.Image IBackgroundView.HeroImage2 => HeroImage2;
        string? IBackgroundView.ItemTitle => _item?.Title;
        string? IBackgroundView.ItemImdbId => _item?.IMDbId;

        void IBackgroundView.SetHeroImageSource(ImageSource? source) { if (HeroImage != null) HeroImage.Source = source; }
        void IBackgroundView.SetHeroImage2Source(ImageSource? source) { if (HeroImage2 != null) HeroImage2.Source = source; }
        void IBackgroundView.SetHeroImageOpacity(double opacity) { if (HeroImage != null) HeroImage.Opacity = opacity; }
        void IBackgroundView.SetHeroImage2Opacity(double opacity) { if (HeroImage2 != null) HeroImage2.Opacity = opacity; }
        void IBackgroundView.SetActiveHeroOpacity(double opacity) { if (HeroImage != null && HeroImage.Opacity >= HeroImage2?.Opacity) HeroImage.Opacity = opacity; else if (HeroImage2 != null) HeroImage2.Opacity = opacity; }
        void IBackgroundView.SetInactiveHeroOpacity(double opacity) { if (HeroImage != null && HeroImage.Opacity < HeroImage2?.Opacity) HeroImage.Opacity = opacity; else if (HeroImage2 != null) HeroImage2.Opacity = opacity; }
        void IBackgroundView.SetOutgoingHeroSource(ImageSource? source) { if (HeroImage != null && HeroImage.Opacity < HeroImage2?.Opacity) HeroImage.Source = source; else if (HeroImage2 != null) HeroImage2.Source = source; }
        double IBackgroundView.GetActiveHeroOpacity() => (HeroImage?.Opacity ?? 0) >= (HeroImage2?.Opacity ?? 0) ? (HeroImage?.Opacity ?? 0) : (HeroImage2?.Opacity ?? 0);
        double IBackgroundView.GetInactiveHeroOpacity() => (HeroImage?.Opacity ?? 0) < (HeroImage2?.Opacity ?? 0) ? (HeroImage?.Opacity ?? 0) : (HeroImage2?.Opacity ?? 0);
        string? IBackgroundView.GetActiveHeroUrl() => ((HeroImage?.Opacity ?? 0) >= (HeroImage2?.Opacity ?? 0) ? HeroImage : HeroImage2)?.Source is BitmapImage bi ? bi.UriSource?.ToString() : null;
        string? IBackgroundView.GetInactiveHeroUrl() => ((HeroImage?.Opacity ?? 0) < (HeroImage2?.Opacity ?? 0) ? HeroImage : HeroImage2)?.Source is BitmapImage bi ? bi.UriSource?.ToString() : null;

        void IBackgroundView.SetHeroShimmerVisibility(Visibility visibility) { if (HeroShimmer != null) HeroShimmer.Visibility = visibility; }
        void IBackgroundView.SetHeroShimmerOpacity(float opacity) { if (HeroShimmer != null) HeroShimmer.Opacity = opacity; }

        void IBackgroundView.SetGradientOpacity(string gradientName, float opacity, int durationMs)
        {
            var element = gradientName switch
            {
                "LocalInfoGradient" => LocalInfoGradient,
                "ExtraReadabilityGradient" => ExtraReadabilityGradient,
                "BottomReadabilityGradient" => BottomReadabilityGradient,
                _ => null
            };
            if (element != null) AnimateOpacity(element, opacity, durationMs);
        }

        void IBackgroundView.SetTitleColor(Color color)
        {
            if (IdentityControl?.TitleTextBlock != null)
                IdentityControl.TitleTextBlock.Foreground = new SolidColorBrush(color);
        }

        void IBackgroundView.SetOverviewColor(Color color)
        {
            if (OverviewText != null)
                OverviewText.Foreground = new SolidColorBrush(color);
        }

        string IBackgroundView.PrimaryColorHex => _backgroundManager?.PrimaryColorHex ?? "#FF00BFA5";

        private EpisodeItem _selectedEpisode;
        private SeasonItem _selectedSeason;
        private TmdbMovieResult _cachedTmdb;
        private SolidColorBrush _themeTintBrush;
        private bool _isInitializingSeriesUi;
        private static readonly Dictionary<string, Services.MediaInfo.StremioSourcesService.StremioSourcesCacheEntry> _stremioSourcesCache = new();
        private int _sourcesRequestVersion;

        private string _currentStremioVideoId;
        private TaskCompletionSource<bool> _logoReadyTcs;
        private bool _isSourcesFetchInProgress;
        private bool _isEpisodesLoading;
        private bool _isCurrentSourcesComplete;
        private bool _shouldAutoResume = false;
        private string? _sourceAddonUrl;
        private Vector3 _sourcesPanelOriginalScale = new Vector3(1f, 1f, 1f);
        private double _sourcesPanelOriginalWidth = 0;
        
        private Models.Metadata.UnifiedMetadata _unifiedMetadata;
        private string _lastUsedTmdbLanguage;
        private string _prebufferUrl;
        private CancellationTokenSource _probeCts;
        private CancellationTokenSource _prebufferCts;
        private CancellationTokenSource? _sourcesCts;

        // Trailer State
        private bool _isTrailerWebViewInitialized = false;
        private string _trailerFolder;
        private string _trailerVirtualHost = "trailers.moderniptv.local";
        private bool _isTrailerInitializing = false;
        private bool _isTrailerFullscreen = false;
        private int _trailerUiVersion = 0;
        private string? _currentTrailerKey;
        private const double TrailerDefaultWidth = 1000;
        private const double TrailerDefaultHeight = 562;
        private CancellationTokenSource _trailerCts;
        private CancellationTokenSource? _seasonEnrichCts;

        private CancellationTokenSource? _pageCts;
        private CancellationTokenSource? _feedbackCts;
        private bool _isNavigatingAway;
        private TaskCompletionSource<bool>? _pageLoadedTcs;

        internal string ResolveBestContentId(string? rawId)
        {
            return ModernIPTVPlayer.Services.Metadata.IdMappingService.Instance.ResolveBestContentId(rawId, _unifiedMetadata?.ImdbId);
        }

        // PageLoadState moved to Services/IMediaInfoUIProxy.cs and made public.

        private bool _isHandoffInProgress = false;
        private bool _isHandoffReturn = false;
        private bool _isProcessingSizeChanged = false;
        private int _lastAdjustedCastCount = -1;
        private int _lastAdjustedDirectorCount = -1;
        private PageLoadState _pageLoadState = PageLoadState.Initial;
        private bool _isResettingPageState;
        private bool _isRevealingInProgress = false; // [STABILITY] Prevent LayoutCycle during fast partial updates
        private int _revealedCastGeneration;
        private int _revealedDirectorGeneration;
        private string _lastSyncedEpisodeId = "";
        private IMediaStream _pendingLoadItem;  // Item waiting for layout

        // Composition Logo System
        private SpriteVisual _logoVisual;
        private CompositionSurfaceBrush _logoBrush;
        private LoadedImageSurface _logoSurface;
        private string _currentLogoUrl;
        private bool _isLogoReady;
        private bool _isLogoPending;
        private bool _isLogoFallbackActive;

        // Ambience logic moved to Background.cs

        private Dictionary<string, string> _urlToSignatureCache = new Dictionary<string, string>();
        private DateTime _navigationStartTime;
        private bool _isLogoImageLoaded;
        private readonly object _metadataSyncRoot = new();
        // Mouse Drag-to-Scroll State
        private bool _isMainDragging = false;
        private Windows.Foundation.Point _lastMainPointerPos;
        private bool _isCastDragging = false;
        private Windows.Foundation.Point _lastCastPointerPos;
        
        // Resize Optimization
        private int _isWideModeIndex = -1; // -1: Unknown, 0: Narrow, 1: Wide
        private bool _isFirstLayoutApplied = false;
        private string _currentContentStateName = "";


        private double _lastReportedWidth;
        private double _lastReportedHeight;
        private double _lastAppliedWidth;
        private double _lastAppliedHeight;
        private int _lastResponsiveWidthBucket = -1;
        private int _lastResponsiveHeightBucket = -1;
        private int _lastInfoLayoutSignature = 0;
        private bool _isResponsiveLayoutQueued;
        // Layout Perfection Constants
        private const double LayoutAdaptiveThreshold = 950.0;
        private const double WideSourcesColumnMinWidth = 400.0;
        private const double WideSourcesColumnMaxWidth = 600.0;
        private const double WideEpisodesColumnWidth = 420.0;
        private const double WideInfoCompactThreshold = 950.0;
        private const double WideInfoCastThreshold = 620.0;
        private const double WidePeopleComfortHeight = 720.0;
        private const double PrimaryActionMinExpandedWidth = 104.0;
        private const double RestartActionMinExpandedWidth = 122.0;
        private const double PrimaryActionIconWidth = 24.0;
        private const double ActionBarOverflowGuard = 4.0;
        private const int ActionButtonExpandTextDelayMs = 280;
        private const int ActionButtonCollapseTextDelayMs = 115;
        private bool _lastPlayIconOnlyState = true;
        private bool _lastRestartIconOnlyState = true;
        private int _playActionTransitionVersion;
        private int _restartActionTransitionVersion;
        private string _lastPanelSyncLogSignature = "";
        private const double ContentGridPaddingBottom = 30.0;
        private const double InfoInnerMarginBottom = 25.0;
        private const double LayoutBufferBottom = 5.0;
        private const double TotalBottomGap = ContentGridPaddingBottom + InfoInnerMarginBottom + LayoutBufferBottom;

        private bool? _lastIsWideForPanels;
        private SpringVector3NaturalMotionAnimation _driftAnimation;
        private const bool DisableMediaInfoRevealAnimationsForCrashIsolation = false;
        private const bool DisableReadyPeopleRevealForCrashIsolation = false;

        private MediaInfoActionService _actionService;

        private static void TraceMediaInfo(string message, IDictionary<string, object?>? data = null)
        {
            App.LastMediaInfoAction = message;
            App.DebugNdjson("MediaInfoPage.xaml.cs", message, data, "media-info");
        }

        private static void TraceMediaInfoException(string scope, Exception ex, IDictionary<string, object?>? data = null)
        {
            var payload = data != null
                ? new Dictionary<string, object?>(data)
                : new Dictionary<string, object?>();
            payload["type"] = ex.GetType().FullName;
            payload["hresult"] = ex.HResult;
            payload["message"] = ex.Message;
            payload["stack"] = ex.StackTrace;
            payload["inner"] = ex.InnerException?.ToString();
            TraceMediaInfo($"{scope} exception", payload);
        }

        private bool TryTraceMediaInfoEnqueue(string scope, Action action, Microsoft.UI.Dispatching.DispatcherQueuePriority? priority = null)
        {
            bool Invoke()
            {
                TraceMediaInfo($"{scope} queued");
                return priority.HasValue
                    ? DispatcherQueue.TryEnqueue(priority.Value, Run)
                    : DispatcherQueue.TryEnqueue(Run);
            }

            void Run()
            {
                TraceMediaInfo($"{scope} enter");
                try
                {
                    action();
                    TraceMediaInfo($"{scope} exit");
                }
                catch (Exception ex)
                {
                    TraceMediaInfoException(scope, ex);
                    throw;
                }
            }

            try
            {
                return Invoke();
            }
            catch (Exception ex)
            {
                TraceMediaInfoException($"{scope} enqueue", ex);
                throw;
            }
        }

        private static async void ObserveMediaInfoTask(Task? task, string operation)
        {
            if (task == null) return;
            try
            {
                await task;
                TraceMediaInfo($"observed task completed: {operation}");
            }
            catch (Exception ex)
            {
                TraceMediaInfo($"observed task failed: {operation}", new Dictionary<string, object?>
                {
                    ["type"] = ex.GetType().FullName,
                    ["hresult"] = ex.HResult,
                    ["message"] = ex.Message,
                    ["stack"] = ex.StackTrace,
                    ["inner"] = ex.InnerException?.ToString()
                });
            }
        }

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(EpisodeItem))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SeasonItem))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CastItem))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(StremioAddonViewModel))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(StremioStreamViewModel))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(StremioMediaStream))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(StreamTemplateSelector))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(EpisodeTemplateSelector))]
        public MediaInfoPage()
        {
            var ctorSw = Stopwatch.StartNew();
            TraceMediaInfo("ctor enter");
            
            var sw = Stopwatch.StartNew();
            this.InitializeComponent();
            sw.Stop();
            Debug.WriteLine($"[NAV-TIMING] InitializeComponent (XAML parse): {sw.ElapsedMilliseconds}ms");
            TraceMediaInfo("ctor InitializeComponent done");
            
            sw.Restart();
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            sw.Stop();
            Debug.WriteLine($"[NAV-TIMING] Compositor acquire: {sw.ElapsedMilliseconds}ms");
            TraceMediaInfo("ctor compositor acquired");
            
            sw.Restart();
            _commitService = new MediaInfoCommitService(this);
            _actionService = new MediaInfoActionService(this);
            _visualStateController = new VisualStateController();
            _animationCoordinator = new AnimationCoordinator();
            _loadPipeline = new LoadPipeline();
            _loadPipeline.StateChanged += (oldState, newState) => TraceMediaInfo("LoadPipeline state", new Dictionary<string, object?> { ["from"] = oldState.ToString(), ["to"] = newState.ToString() });
            _layoutScheduler = new LayoutScheduler(DispatcherQueue, reason => ExecuteLayout(reason));
            SectionController.SetAnimationCoordinator(_animationCoordinator);

            try { _playerHandoffManager = new Services.MediaInfo.PlayerHandoffManager(); }
            catch (Exception ex) { Debug.WriteLine($"[CTOR] PlayerHandoffManager init failed: {ex.Message}"); }

            try { _trailerManager = new Services.MediaInfo.TrailerManager(this, _compositor); }
            catch (Exception ex) { Debug.WriteLine($"[CTOR] TrailerManager init failed: {ex.Message}"); }

            try { _parallaxController = new Services.MediaInfo.ParallaxController(this, _compositor); }
            catch (Exception ex) { Debug.WriteLine($"[CTOR] ParallaxController init failed: {ex.Message}"); }

            try { _connectedAnimationManager = new Services.MediaInfo.ConnectedAnimationManager(this); }
            catch (Exception ex) { Debug.WriteLine($"[CTOR] ConnectedAnimationManager init failed: {ex.Message}"); }

            try
            {
                _detailPanelController = new Services.MediaInfo.DetailPanelController(this);
                _sourcesManager = _detailPanelController.SourcesManager;
            }
            catch (Exception ex) { Debug.WriteLine($"[CTOR] DetailPanelController init failed: {ex.Message}"); }

            try { _episodesManager = new Services.MediaInfo.EpisodesManager(this, _detailPanelController?.EpisodesState); }
            catch (Exception ex) { Debug.WriteLine($"[CTOR] EpisodesManager init failed: {ex.Message}"); }

            try { _castDirectorManager = new Services.MediaInfo.CastDirectorManager(this, _compositor); }
            catch (Exception ex) { Debug.WriteLine($"[CTOR] CastDirectorManager init failed: {ex.Message}"); }

            try { _actionHandlerManager = new Services.MediaInfo.ActionHandlerManager(this); }
            catch (Exception ex) { Debug.WriteLine($"[CTOR] ActionHandlerManager init failed: {ex.Message}"); }

            try { _pageOrchestrator = new Services.MediaInfo.PageOrchestrator(this, _loadPipeline, _commitService, _visualStateController, _layoutScheduler, _episodesManager, _castDirectorManager, _parallaxController, _trailerManager, _playerHandoffManager); }
            catch (Exception ex) { Debug.WriteLine($"[CTOR] PageOrchestrator init failed: {ex.Message}"); }

            try { _backgroundManager = new BackgroundManager(this, _compositor, DispatcherQueue); }
            catch (Exception ex) { Debug.WriteLine($"[CTOR] BackgroundManager init failed: {ex.Message}"); }

            try { _stremioSourcesService = new StremioSourcesService(); }
            catch (Exception ex) { Debug.WriteLine($"[CTOR] StremioSourcesService init failed: {ex.Message}"); }

            sw.Stop();
            Debug.WriteLine($"[NAV-TIMING] Service construction (orchestrator+commit+action): {sw.ElapsedMilliseconds}ms");

            sw.Restart();
            InitializeSectionArchitecture();
            sw.Stop();
            Debug.WriteLine($"[NAV-TIMING] Section architecture initialization: {sw.ElapsedMilliseconds}ms");

            sw.Restart();
            if (IdentityControl?.LogoImage != null)
            {
                IdentityControl.LogoImage.ImageOpened += (s, e) => 
                {
                    _isLogoImageLoaded = true;
                    _logoReadyTcs?.TrySetResult(true);
                    _commitService.RetryIdentityReveal();
                    System.Diagnostics.Debug.WriteLine("[INFO-PAGE] Logo opened.");
                };
                IdentityControl.LogoImage.ImageFailed += (s, e) => 
                {
                    _isLogoImageLoaded = false;
                    _logoReadyTcs?.TrySetResult(false);
                };
            }
            sw.Stop();
            Debug.WriteLine($"[NAV-TIMING] Logo event wiring: {sw.ElapsedMilliseconds}ms");

            sw.Restart();
            _pageLoadedTcs = new TaskCompletionSource<bool>();
            this.Loaded += (s, e) => 
            {
                Services.NavigationService.LogPageLoaded();
                _pageLoadedTcs?.TrySetResult(true);
                SetupParallax();
            };
            sw.Stop();
            Debug.WriteLine($"[NAV-TIMING] Page loaded event wiring: {sw.ElapsedMilliseconds}ms");

            // UI Audio Feedback Setup
            this.ElementSoundMode = global::Microsoft.UI.Xaml.ElementSoundMode.Off;
            BackButton.ElementSoundMode = global::Microsoft.UI.Xaml.ElementSoundMode.Default;
            
            this.SizeChanged += MediaInfoPage_SizeChanged;

            // [EVENT-DRIVEN] Subscribe to specific panel size changes to update shimmers accurately
            if (SourcesScrollViewer != null) SourcesScrollViewer.SizeChanged += PanelScrollViewer_SizeChanged;
            if (EpisodesScrollViewer != null) EpisodesScrollViewer.SizeChanged += PanelScrollViewer_SizeChanged;
            this.NavigationCacheMode = NavigationCacheMode.Disabled;
            
            sw.Restart();
            SetupProfessionalAnimations();
            sw.Stop();
            Debug.WriteLine($"[NAV-TIMING] SetupProfessionalAnimations: {sw.ElapsedMilliseconds}ms");

            sw.Restart();
            // Robust Drag-to-Scroll Registration (Vertical)
            RootScrollViewer.AddHandler(PointerPressedEvent, new PointerEventHandler(OnMainPointerPressed), true);
            RootScrollViewer.AddHandler(PointerMovedEvent, new PointerEventHandler(OnMainPointerMoved), true);
            RootScrollViewer.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnMainPointerReleased), true);
            RootScrollViewer.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnMainPointerReleased), true);
            RootScrollViewer.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnMainPointerReleased), true);

            // Enable smooth morphing transitions
            ElementCompositionPreview.SetIsTranslationEnabled(ActivePersonCard, true);
            ActivePersonCard.SizeChanged += ActivePersonCard_SizeChanged;

            // Project Zero: Mandatory Cleanup Registration
            this.Unloaded += (s, e) => Cleanup();
            sw.Stop();
            Debug.WriteLine($"[NAV-TIMING] Event handlers + misc setup: {sw.ElapsedMilliseconds}ms");
            
            ctorSw.Stop();
            Debug.WriteLine($"[NAV-TIMING] ===== TOTAL MediaInfoPage.ctor: {ctorSw.ElapsedMilliseconds}ms =====");
            TraceMediaInfo("ctor exit");
        }

        /// <summary>
        /// Explicitly release resources and break reference chains to prevent memory leaks.
        /// </summary>
        public void Cleanup()
        {
            try
            {
                if (ActivePersonCard != null)
                {
                    ActivePersonCard.SizeChanged -= ActivePersonCard_SizeChanged;
                }

                if (SourcesScrollViewer != null) SourcesScrollViewer.SizeChanged -= PanelScrollViewer_SizeChanged;
                if (EpisodesScrollViewer != null) EpisodesScrollViewer.SizeChanged -= PanelScrollViewer_SizeChanged;

                System.Diagnostics.Debug.WriteLine($"[INFO-CLEANUP] MediaInfoPage releasing resources for: {_item?.Title ?? "None"}");

                // 1. Kill Heavy Engines
                TrailerPoolService.Instance.Release(TrailerContent);

                // 2. Clear Managed Collections (Frees strings and model objects)
                Seasons?.Clear();
                CurrentEpisodes?.Clear();
                CastList?.Clear();
                DirectorList?.Clear();
                _revealedCastGeneration = 0;
                _revealedDirectorGeneration = 0;
                _commitService?.Reset();
                _addonResults?.Clear();
                _backgroundManager?.Reset();

                // 2.5 Kill & Dispose Pre-buffer Player (MPV Native RAM)
                if (MediaInfoPlayer != null)
                {
                    try 
                    { 
                        // Project Zero - Phase 5: FIX HANDOVER CRASH (RACECONDITION)
                        // If we are currently handing off this player, DO NOT destroy it!
                        // App.HandoffPlayer might be cleared by PlayerPage already, so we use our local flag.
                        if (_isHandoffInProgress)
                        {
                            Debug.WriteLine("[INFO-CLEANUP] MediaInfoPage: Handoff in progress. Preserving player instance.");
                        }
                        else 
                        {
                            var pToCleanup = MediaInfoPlayer;
                            _ = Task.Run(async () => {
                                try { await pToCleanup.CleanupAsync(); } catch { }
                            });
                            Debug.WriteLine("[INFO-CLEANUP] MediaInfoPage: CleanupAsync started.");
                        }
                    } catch { }
                    MediaInfoPlayer = null;
                }
                if (PlayerHost != null) PlayerHost.Content = null;
                _prebufferUrl = null;
                _prebufferCts?.Cancel();
                _prebufferCts?.Dispose();
                _prebufferCts = null;

                // 3. Nullify Image/Composition Resources

                if (IdentityControl?.LogoHost != null) ElementCompositionPreview.SetElementChildVisual(IdentityControl.LogoHost, null);
                
                _logoVisual = null;
                _logoBrush = null;
                if (_logoSurface != null)
                {
                    _logoSurface.Dispose();
                    _logoSurface = null;
                }

                // 4. Break Metadata & Interop Links
                _item = null;
                _unifiedMetadata = null;
                _compositor = null;
                _trailerCts?.Cancel();
                _trailerCts?.Dispose();
                _trailerCts = null;



                _sourcesCts?.Cancel();
                _sourcesCts?.Dispose();
                _sourcesCts = null;

                _seasonEnrichCts?.Cancel();
                _seasonEnrichCts?.Dispose();
                _seasonEnrichCts = null;

                _probeCts?.Cancel();
                _probeCts?.Dispose();
                _probeCts = null;

                // 5. Unsubscribe from manual event handlers
                this.SizeChanged -= MediaInfoPage_SizeChanged;
                
                // Project Zero: Explicitly remove Pointer Handlers (Fixes EventSourceCache leaks)
                RootScrollViewer.RemoveHandler(PointerPressedEvent, new PointerEventHandler(OnMainPointerPressed));
                RootScrollViewer.RemoveHandler(PointerMovedEvent, new PointerEventHandler(OnMainPointerMoved));
                RootScrollViewer.RemoveHandler(PointerReleasedEvent, new PointerEventHandler(OnMainPointerReleased));
                RootScrollViewer.RemoveHandler(PointerCanceledEvent, new PointerEventHandler(OnMainPointerReleased));
                RootScrollViewer.RemoveHandler(PointerCaptureLostEvent, new PointerEventHandler(OnMainPointerReleased));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[INFO-CLEANUP] MediaInfoPage error: {ex.Message}");
            }
        }

        private bool _isSelectionSyncing = false;
        private int _loadingVersion = 0;
        private long _historyChangedToken; // New field


        private UIElement _lastKenBurnsElement;

        private System.Threading.SemaphoreSlim _validationLock = new System.Threading.SemaphoreSlim(1, 1);





        /// <summary>
        /// Starts a new logical load session. Any async continuation that mutates page UI should
        /// validate its captured session before applying results.
        /// </summary>
        internal int BeginLoadSession() => Interlocked.Increment(ref _loadingVersion);

        private bool IsCurrentLoadSession(int loadSession) => loadSession == Volatile.Read(ref _loadingVersion);

        /// <summary>
        /// Queues UI work that belongs to a specific load session. Stale work is ignored when
        /// navigation has moved the page to a newer item.
        /// </summary>
        private bool TryEnqueueForLoadSession(int loadSession, Action action)
        {
            return TryTraceMediaInfoEnqueue($"load-session callback {loadSession}", () =>
            {
                if (!IsCurrentLoadSession(loadSession)) return;
                action();
            });
        }



        private void PanelScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Only refresh if height has changed significantly (more than 10 pixels)
            // to avoid spamming updates during subtle animations.
            if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 10)
            {
                RefreshAllShimmers();
            }
        }
        
        private void MediaInfoPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isProcessingSizeChanged) return;
            _isProcessingSizeChanged = true;
            try
            {
                _lastReportedWidth = e.NewSize.Width;
                _lastReportedHeight = e.NewSize.Height;

                int layoutIndex = e.NewSize.Width >= LayoutAdaptiveThreshold ? 1 : 0;
                int widthBucket = (int)Math.Round(e.NewSize.Width / 48.0);
                int heightBucket = (int)Math.Round(e.NewSize.Height / 48.0);
                bool forceSync = !_isFirstLayoutApplied || (_panelOwner != null && _panelOwner.PanelMode == MediaDetailPanelMode.Sources && SourcesPanel?.Visibility != Visibility.Visible);

                if (_isWideModeIndex != layoutIndex || forceSync)
                {
                    _isWideModeIndex = layoutIndex;
                    _isFirstLayoutApplied = true;
                    OnViewportChanged();
                    QueueInfoPriorityLayout(layoutIndex == 1);
                    RefreshAllShimmers();
                }
                else if (_lastResponsiveWidthBucket != widthBucket || _lastResponsiveHeightBucket != heightBucket)
                {
                    _lastResponsiveWidthBucket = widthBucket;
                    _lastResponsiveHeightBucket = heightBucket;
                    QueueInfoPriorityLayout(layoutIndex == 1);
                    RefreshAllShimmers();
                }

                // State Machine: Initial -> LayoutReady
                if (_pageLoadState == PageLoadState.Initial)
                {
                    _pageLoadState = PageLoadState.LayoutReady;
                    _loadPipeline?.NotifyLayoutReady();
                    if (_pendingLoadItem != null)
                    {
                        var pendingItem = _pendingLoadItem;
                        _pendingLoadItem = null;
                        _ = LoadDetailsAsync(pendingItem, loadSession: Volatile.Read(ref _loadingVersion));
                    }
                }

                if (TrailerOverlay?.Visibility == Visibility.Visible)
                {
                    _trailerManager?.ApplyFullscreenLayout(true);
                }
            }
            finally
            {
                _isProcessingSizeChanged = false;
            }
        }

        private void RestoreUIVisibility()
        {
            try
            {
                if (RootScrollViewer != null) RootScrollViewer.Visibility = Visibility.Visible;
                
                if (PlayButton != null) PlayButton.Visibility = Visibility.Visible;
                if (TrailerButton != null) TrailerButton.Visibility = Visibility.Visible;
                if (DownloadButton != null) DownloadButton.Visibility = Visibility.Visible;
                if (CopyLinkButton != null) CopyLinkButton.Visibility = Visibility.Visible;

                if (_pageLoadState != PageLoadState.Ready) OnViewportChanged();
                
                System.Diagnostics.Debug.WriteLine("[INFO-PAGE] UI Visibility restored.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] RestoreUIVisibility Error: {ex.Message}");
            }
        }

        private bool IsSameItem(IMediaStream item1, IMediaStream item2)
        {
            return ModernIPTVPlayer.Helpers.StreamHelper.IsSameItem(item1, item2);
        }

        private bool IsSeriesItem()
        {
            return ModernIPTVPlayer.Helpers.StreamHelper.IsSeriesItem(_item, _unifiedMetadata);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ModernIPTVPlayer.Services.AppLogger.Info($"=== OnNavigatedFrom START === Target: {e.SourcePageType.Name}");
            _isNavigatingAway = true;
            // #region agent log
            App.DebugSessionNdjson("MediaInfoPage.xaml.cs:OnNavigatedFrom",
                "MediaInfoPage navigation away started",
                new Dictionary<string, object?>
                {
                    ["target"] = e.SourcePageType?.FullName,
                    ["isLoaded"] = IsLoaded,
                    ["xamlRootReady"] = XamlRoot != null,
                    ["hasLogoReadyTcs"] = _logoReadyTcs != null,
                    ["logoReadyCompleted"] = _logoReadyTcs?.Task.IsCompleted,
                    ["hasMediaInfoPlayer"] = MediaInfoPlayer != null
                },
                "H6");
            // #endregion
            HistoryManager.Instance.HistoryChanged -= OnHistoryChanged;

            // CLEANUP ON EXIT: Ensure the cached page is blank for the next movie
            if (e.NavigationMode != NavigationMode.Back && e.SourcePageType != typeof(PlayerPage))
            {
                ResetPageState(); 
            }
            
            // Cancel any active probe
            try { 
                _probeCts?.Cancel(); 
                _probeCts?.Dispose(); 
            } catch {}

            // Cancel page-wide tasks
            try {
                _pageCts?.Cancel();
                _pageCts?.Dispose();
                _pageCts = null;
            } catch { }

            // Cancel all pending animations
            try
            {
                _animationCoordinator?.CancelAll();
            }
            catch (Exception ex) { ModernIPTVPlayer.Services.AppLogger.Error("Cancel animations error", ex); }

            // Cancel load pipeline
            try
            {
                _loadPipeline?.Dispose();
            }
            catch (Exception ex) { ModernIPTVPlayer.Services.AppLogger.Error("Dispose load pipeline error", ex); }

            // Dispose layout scheduler
            try
            {
                _layoutScheduler?.Dispose();
            }
            catch (Exception ex) { ModernIPTVPlayer.Services.AppLogger.Error("Dispose layout scheduler error", ex); }

            // Dispose extracted services
            try
            {
                _pageOrchestrator?.OnNavigatedFrom(e);
                _pageOrchestrator?.Dispose();
                _playerHandoffManager?.Dispose();
                _trailerManager?.Dispose();
                _parallaxController?.Dispose();
                _connectedAnimationManager?.Dispose();
                _episodesManager?.Dispose();
                _castDirectorManager?.Dispose();
                _actionHandlerManager?.Dispose();
                _detailPanelController?.Dispose();
            }
            catch (Exception ex) { ModernIPTVPlayer.Services.AppLogger.Error("Dispose services error", ex); }

            // [FIX] Kill ghost timers for TeachingTips/Feedback
            try {
                _feedbackCts?.Cancel();
                _feedbackCts?.Dispose();
                _feedbackCts = null;
            } catch { }

            // Cancel any active prebuffering BEFORE cleanup
            try { 
                _prebufferCts?.Cancel(); 
                _prebufferCts?.Dispose(); 
                _prebufferCts = null;
            } catch {}

            // Cancel other page tasks
            try { _sourcesCts?.Cancel(); } catch {}
            try { _seasonEnrichCts?.Cancel(); } catch {}
            try { _trailerCts?.Cancel(); } catch {}

            // Close Trailer Overlay
            try {
                if (TrailerOverlay != null && TrailerOverlay.Visibility == Visibility.Visible)
                {
                    // Fire and forget trailer close on navigation
                    _ = CloseTrailer();
                }
            } catch (Exception ex) {
                ModernIPTVPlayer.Services.AppLogger.Error("Error closing trailer on navigate", ex);
            }

            if (MediaInfoPlayer != null && PlayerHost != null)
            {
                 if (e.SourcePageType == typeof(PlayerPage) || _isHandoffInProgress)
                 {
                     DetachMediaInfoPlayerFromVisualTree(MediaInfoPlayer);
                     ModernIPTVPlayer.Services.AppLogger.Info("Detached player for handover (preserving instance).");
                 }
                 else
                 {
                     try
                     {
                         ModernIPTVPlayer.Services.AppLogger.Info("STRICT CLEANUP: Destroying MpvPlayer instance on exit (non-player destination).");
                         var pToCleanup = MediaInfoPlayer;
                         DetachMediaInfoPlayerFromVisualTree(pToCleanup);
                         MediaInfoPlayer = null; 
                         _prebufferUrl = null;
                         
                         CleanupMpvPlayerInBackground(pToCleanup, "MediaInfo.OnNavigatedFrom");
                     }
                     catch (Exception ex)
                     {
                         ModernIPTVPlayer.Services.AppLogger.Error("CleanupAsync Error", ex);
                     }
                 }
            }

            ModernIPTVPlayer.Services.AppLogger.Info("Cleaning up Repeaters...");
            if (SourcesRepeater != null) { SourcesRepeater.ItemsSource = null; }
            if (EpisodesRepeater != null) { EpisodesRepeater.ItemsSource = null; }
            if (CastListView != null) { CastListView.ItemsSource = null; }
            if (DirectorListView != null) { DirectorListView.ItemsSource = null; }
            if (NarrowCastListView != null) { NarrowCastListView.ItemsSource = null; }
            if (NarrowDirectorListView != null) { NarrowDirectorListView.ItemsSource = null; }
            ModernIPTVPlayer.Services.AppLogger.Info("Repeater cleanup FINISHED");
        }

        private void DetachMediaInfoPlayerFromVisualTree(MpvWinUI.MpvPlayer player)
        {
            if (player == null) return;

            try
            {
                ModernIPTVPlayer.Services.AppLogger.Info("Detaching MediaInfoPlayer from visual tree");
                player.Visibility = Visibility.Collapsed;

                if (PlayerHost?.Content == player)
                {
                    PlayerHost.Content = null;
                }

                if (player.Parent is Panel panel)
                {
                    panel.Children.Remove(player);
                }
                else if (player.Parent is ContentControl contentControl && contentControl.Content == player)
                {
                    contentControl.Content = null;
                }

                ModernIPTVPlayer.Services.AppLogger.Info("MediaInfoPlayer visual detach completed");
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("MediaInfoPlayer visual detach failed", ex);
                throw;
            }
        }

        private void CleanupMpvPlayerInBackground(MpvWinUI.MpvPlayer player, string reason)
        {
            if (player == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    ModernIPTVPlayer.Services.AppLogger.Info($"Background CleanupAsync START | Reason: {reason}");
                    await player.CleanupAsync();
                    ModernIPTVPlayer.Services.AppLogger.Info($"Background CleanupAsync END | Reason: {reason}");
                }
                catch (Exception ex)
                {
                    ModernIPTVPlayer.Services.AppLogger.Error($"Background CleanupAsync FAILED | Reason: {reason}", ex);
                }
            });
        }
           private void OnHistoryChanged(object sender, EventArgs e)
        {
            TryTraceMediaInfoEnqueue("OnHistoryChanged callback", () => {
                if (_selectedEpisode != null) _selectedEpisode.RefreshHistoryState();
            });
        }


        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
        {
            try
            {
                return base.MeasureOverride(availableSize);
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("[MediaInfoPage] MeasureOverride CRASH", ex);
                throw;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            var navSw = Stopwatch.StartNew();
            TraceMediaInfo("OnNavigatedTo enter", new Dictionary<string, object?> { ["mode"] = e.NavigationMode.ToString() });
            base.OnNavigatedTo(e);

            IMediaStream incomingItem = null;
            IMediaStream previousItem = _item;
            bool isBackNav = e.NavigationMode == NavigationMode.Back;

            if (e.Parameter is MediaNavigationArgs args)
            {
                var sw = Stopwatch.StartNew();
                incomingItem = args.Stream;
                _shouldAutoResume = args.AutoResume && !isBackNav;
                
                if (args.PreloadedLogo != null)
                {
                    IdentityControl?.SetLogo(args.PreloadedLogo);
                }

                if (args.PreloadedImage != null)
                {
                    _backgroundManager?.SetHero(args.PreloadedImage, "navigation-handoff");
                }
                else if (incomingItem != null && !string.IsNullOrEmpty(incomingItem.PosterUrl))
                {
                    _backgroundManager?.SetHero(incomingItem.PosterUrl, "navigation-sync-prime");
                }
                sw.Stop();
            }
            else if (e.Parameter is IMediaStream stream && !string.IsNullOrEmpty(stream.PosterUrl))
            {
                incomingItem = stream;
                _backgroundManager?.SetHero(stream.PosterUrl, "navigation-sync-prime-direct");
            }

            if (incomingItem == null) return;

            var sw2 = Stopwatch.StartNew();
            _item = incomingItem;
            _navigationStartTime = DateTime.Now;

            // Trigger the orchestrated load session. 
            // This service handles visual preparation, reset, fetch, and reveal.
            _ = LoadDetailsAsync(incomingItem, previousItem: previousItem);
            sw2.Stop();
            
            sw2.Restart();
            StartHeroConnectedAnimation();
            SetupParallax();
            sw2.Stop();
            
            // Handoff logic (Logic-only/Safe)
            if (App.HandoffPlayer != null)
            {
                MediaInfoPlayer = App.HandoffPlayer;
                App.HandoffPlayer = null;
            }

            navSw.Stop();
        }

        // Legacy nested class extracted to MediaInfoCommitService.cs
        
        #region IMediaInfoUIProxy Implementation
        ModernIPTVPlayer.Controls.MediaIdentityControl IMediaInfoUIProxy.IdentityControl => IdentityControl;
        TextBlock IMediaInfoUIProxy.StickyTitle => StickyTitle;
        TextBlock IMediaInfoUIProxy.OverviewText => OverviewText;
        TextBlock IMediaInfoUIProxy.YearText => YearText;
        TextBlock IMediaInfoUIProxy.GenresText => GenresText;
        TextBlock IMediaInfoUIProxy.RuntimeText => RuntimeText;
        TextBlock IMediaInfoUIProxy.PlayButtonText => PlayButtonText;
        TextBlock IMediaInfoUIProxy.StickyPlayButtonText => StickyPlayButtonText;
        TextBlock IMediaInfoUIProxy.PlayButtonSubtext => PlayButtonSubtext;
        TextBlock IMediaInfoUIProxy.StickyPlayButtonSubtext => StickyPlayButtonSubtext;
        TextBlock IMediaInfoUIProxy.SourceAttributionText => SourceAttributionText;
        Button IMediaInfoUIProxy.PlayButton => PlayButton;
        Button IMediaInfoUIProxy.RestartButton => RestartButton;
        Button IMediaInfoUIProxy.TrailerButton => TrailerButton;
        Button IMediaInfoUIProxy.DownloadButton => DownloadButton;
        Button IMediaInfoUIProxy.CopyLinkButton => CopyLinkButton;

        void IMediaInfoUIProxy.SetLoadState(PageLoadState state) {
            ModernIPTVPlayer.Services.AppLogger.Info($"IMediaInfoUIProxy.SetLoadState({state}) called. IsNavigatingAway: {_isNavigatingAway}");
            SetLoadStateInternal(state);
        }
        void IMediaInfoUIProxy.SyncLayout() => OnViewportChanged();
        void IMediaInfoUIProxy.ApplyOverviewTextLayout(bool isWide) => ApplyOverviewTextLayoutInternal(isWide);
        void IMediaInfoUIProxy.StartPrebuffering(string url, double position) {
            ModernIPTVPlayer.Services.AppLogger.Info($"IMediaInfoUIProxy.StartPrebuffering called. IsNavigatingAway: {_isNavigatingAway}");
            StartPrebufferingInternal(url, position);
        }
        void IMediaInfoUIProxy.RefreshAllAddonActiveFlags() => RefreshAllAddonActiveFlagsInternal();
        void IMediaInfoUIProxy.SyncAddonSelectionToActive() => SyncAddonSelectionToActiveInternal();
        void IMediaInfoUIProxy.UpdateWatchlistState(bool? state) => UpdateWatchlistStateInternal(state);
        void IMediaInfoUIProxy.SyncActionButtons(HistoryItem history) => SyncActionButtonsInternal(history);
        void IMediaInfoUIProxy.AddBackdropToSlideshow(string url) => _backgroundManager?.StartSlideshow(new[] { url });
        void IMediaInfoUIProxy.StartBackgroundSlideshow(List<string> urls) => _backgroundManager?.StartSlideshow(urls);
        void IMediaInfoUIProxy.ApplyHeroSeedImage(string url, string reason) => _backgroundManager?.SetHero(url, reason);
        void IMediaInfoUIProxy.PlayButton_Click(object sender, RoutedEventArgs e) => PlayButton_ClickInternal(sender, e);
        void IMediaInfoUIProxy.MatchTitleSkeletonToContent() => MatchTitleSkeletonToContentInternal();
        Task IMediaInfoUIProxy.PlayStremioContent(string videoId, bool showGlobalLoading, bool autoPlay, double startSeconds) => PlayStremioContent(videoId, showGlobalLoading, autoPlay, startSeconds);
        Task IMediaInfoUIProxy.PerformHandoverAndNavigate(string url, string title, string id, string parentId, string seriesName, int season, int episode, double startSeconds, string posterUrl, string type, string backdropUrl) => PerformHandoverAndNavigate(url, title, id, parentId, seriesName, season, episode, startSeconds, posterUrl, type, backdropUrl);
        void IMediaInfoUIProxy.OpenEpisodesPanel(PanelChangeReason reason) => OpenEpisodesPanelInternal(reason);
        void IMediaInfoUIProxy.ShowActionFeedback(string title, string subtitle, object target, Microsoft.UI.Xaml.Controls.Symbol? symbol) => ShowActionFeedbackInternal(title, subtitle, target as FrameworkElement, symbol);
        string IMediaInfoUIProxy.ResolveBestContentId(string id) => ResolveBestContentId(id);
        string IMediaInfoUIProxy.GetCurrentBackdrop() => _backgroundManager?.GetCurrentBackdrop();
        Task IMediaInfoUIProxy.PlayTrailer(string videoKey) => PlayTrailer(videoKey);
        Task IMediaInfoUIProxy.DownloadSingle() => DownloadSingle();
        Task IMediaInfoUIProxy.DownloadSeason() => DownloadSeason();
        void IMediaInfoUIProxy.CopyToClipboard(string text)
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        void IMediaInfoUIProxy.SetWatchlistIcon(bool isInList, bool animate) => UpdateWatchlistStateInternal(animate);
        
        async Task IMediaInfoUIProxy.UpdateTechnicalBadgesAsync(string url) => await UpdateTechnicalBadgesAsyncInternal(url);
        void IMediaInfoUIProxy.ShowTechBadgesShimmer() => SetBadgeLoadingState(true);
        async Task IMediaInfoUIProxy.WaitForPageLoadedAsync()
        {
            if (this.IsLoaded) return;
            var tcs = _pageLoadedTcs;
            if (tcs != null) await tcs.Task;
        }
        void IMediaInfoUIProxy.ShowInfoContainerSkeleton()
        {
            _visualStateController.SetState(InfoContainer, Visibility.Visible, 1.0);
        }

        // Internal Helpers for Thread-Safe Proxy Implementation
        internal void SyncActionButtonsInternal(HistoryItem history)
        {
            if (!this.DispatcherQueue.HasThreadAccess) { this.DispatcherQueue.TryEnqueue(() => SyncActionButtonsInternal(history)); return; }
            _actionService?.SyncActionButtons(_item, _selectedEpisode, history);
        }

        internal void SetLoadStateInternal(PageLoadState state)
        {
            if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(() => SetLoadStateInternal(state)); return; }
            _pageLoadState = state;
            TraceMediaInfo("SetLoadState", new Dictionary<string, object?> { ["state"] = state.ToString() });
        }

        private void OpenEpisodesPanelInternal(PanelChangeReason reason)
        {
            if (!this.DispatcherQueue.HasThreadAccess) { this.DispatcherQueue.TryEnqueue(() => OpenEpisodesPanelInternal(reason)); return; }
            OpenEpisodesPanel(reason);
        }

        private void ShowActionFeedbackInternal(string title, string subtitle, FrameworkElement target = null, Microsoft.UI.Xaml.Controls.Symbol? symbol = null)
        {
            if (!this.DispatcherQueue.HasThreadAccess) { this.DispatcherQueue.TryEnqueue(() => ShowActionFeedbackInternal(title, subtitle, target, symbol)); return; }
            
            // Cancel previous auto-close task if it exists
            _feedbackCts?.Cancel();
            _feedbackCts = new CancellationTokenSource();
            var token = _feedbackCts.Token;

            if (ActionFeedbackTip != null)
            {
                ActionFeedbackTip.Title = title;
                ActionFeedbackTip.Subtitle = subtitle;
                ActionFeedbackTip.Target = target;

                // Use the explicitly passed Symbol, defaulting to ReportHacked fallback if none provided
                Symbol iconSymbol = symbol ?? Symbol.ReportHacked;

                ActionFeedbackTip.IconSource = new Microsoft.UI.Xaml.Controls.SymbolIconSource { Symbol = iconSymbol };
                ActionFeedbackTip.IsOpen = true;

                // Auto-close after 2 seconds
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000, token);
                        if (!token.IsCancellationRequested && !_isNavigatingAway)
                        {
                            this.DispatcherQueue.TryEnqueue(() => 
                            {
                                try 
                                {
                                    if (!token.IsCancellationRequested && !_isNavigatingAway && ActionFeedbackTip != null) 
                                        ActionFeedbackTip.IsOpen = false;
                                } catch { }
                            });
                        }
                    }
                    catch (OperationCanceledException) { }
                });
            }
        }

        private void SyncLayoutInternal() { if (this.DispatcherQueue.HasThreadAccess) OnViewportChanged(); else this.DispatcherQueue.TryEnqueue(() => OnViewportChanged()); }
        private void ApplyOverviewTextLayoutInternal(bool isWide) { if (this.DispatcherQueue.HasThreadAccess) ApplyOverviewTextLayout(isWide); else this.DispatcherQueue.TryEnqueue(() => ApplyOverviewTextLayout(isWide)); }
        private void StartPrebufferingInternal(string url, double position) { 
            ModernIPTVPlayer.Services.AppLogger.Info($"StartPrebufferingInternal called. Thread: {Environment.CurrentManagedThreadId}");
            if (this.DispatcherQueue.HasThreadAccess) StartPrebuffering(url, position); 
            else this.DispatcherQueue.TryEnqueue(() => {
                ModernIPTVPlayer.Services.AppLogger.Info($"StartPrebuffering ENQUEUED task starting. IsNavigatingAway: {_isNavigatingAway}");
                StartPrebuffering(url, position);
            }); 
        }
        private void RefreshAllAddonActiveFlagsInternal() { if (this.DispatcherQueue.HasThreadAccess) RefreshAllAddonActiveFlags(); else this.DispatcherQueue.TryEnqueue(() => RefreshAllAddonActiveFlags()); }
        private void SyncAddonSelectionToActiveInternal() { if (this.DispatcherQueue.HasThreadAccess) SyncAddonSelectionToActive(); else this.DispatcherQueue.TryEnqueue(() => SyncAddonSelectionToActive()); }
        private void UpdateWatchlistStateInternal(bool? state) { if (this.DispatcherQueue.HasThreadAccess) UpdateWatchlistState(state ?? false); else this.DispatcherQueue.TryEnqueue(() => UpdateWatchlistState(state ?? false)); }

        private void PlayButton_ClickInternal(object sender, RoutedEventArgs e) { if (this.DispatcherQueue.HasThreadAccess) PlayButton_Click(sender, e); else this.DispatcherQueue.TryEnqueue(() => PlayButton_Click(sender, e)); }
        private void MatchTitleSkeletonToContentInternal() { if (this.DispatcherQueue.HasThreadAccess) MatchTitleSkeletonToContent(); else this.DispatcherQueue.TryEnqueue(() => MatchTitleSkeletonToContent()); }

        private async Task UpdateTechnicalBadgesAsyncInternal(string url) 
        { 
            if (this.DispatcherQueue.HasThreadAccess) await UpdateTechnicalBadgesAsync(url); 
            else { var tcs = new TaskCompletionSource(); this.DispatcherQueue.TryEnqueue(async () => { try { await UpdateTechnicalBadgesAsync(url); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } }); await tcs.Task; }
        }
        #endregion

        internal void IdentityControl_LogoLoadCompleted(object sender, bool success)
        {
            _logoReadyTcs?.TrySetResult(success);
        }

        private void UpdateTechnicalSectionVisibility(bool hasExtra)
        {
            if (MetadataRibbon == null || MetadataSeparator == null || TechBadgesContent == null) return;

            DispatcherQueue.TryEnqueue(() => {
                var target = hasExtra ? Visibility.Visible : Visibility.Collapsed;
                if (TechBadgesContent.Visibility != target) TechBadgesContent.Visibility = target;
                if (MetadataSeparator.Visibility != target) MetadataSeparator.Visibility = target;
                
                // [FIX] Centralized "No URL" safety: hide technical skeletons if we have nothing to probe
                if (string.IsNullOrEmpty(_streamUrl) && TechBadgesShimmer != null)
                {
                    TechBadgesShimmer.Visibility = Visibility.Collapsed;
                }

                if (hasExtra)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(TechBadgesContent);
                    if (visual != null) visual.Opacity = 1f;
                }
            });
        }





        private void StartHeroConnectedAnimation()
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                try
                {
                    StartKenBurnsEffect();
                }
                catch (Exception ex)
                {
                    ModernIPTVPlayer.Services.AppLogger.Error("Animation ERROR", ex);
                    StartKenBurnsEffect();
                }
            });
        }
        

        #region Actions

        private async void EpisodePlayButton_Click(object sender, RoutedEventArgs e)
        {
             if (sender is Button btn && btn.Tag is EpisodeItem ep)
                 _actionHandlerManager?.OnEpisodePlayButtonClick(ep);
        }

        private async void EpisodeArrowButton_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.Tag is EpisodeItem ep)
                _actionHandlerManager?.OnEpisodeArrowButtonTapped(ep);
        }

        private async void TrailerButton_Click(object sender, RoutedEventArgs e)
        {
            await _actionService.HandleTrailerClickAsync(_unifiedMetadata?.TrailerUrl, _item, _unifiedMetadata, sender);
        }

        private async Task PlayTrailer(string videoKey)
        {
            await _trailerManager?.PlayTrailerAsync(videoKey);
        }

        private async Task CloseTrailer()
        {
            if (_trailerManager != null)
                await _trailerManager.CloseTrailerAsync();
        }

        private void SetupParallax()
        {
            if (_logoVisual == null && _compositor != null && IdentityControl?.LogoHost != null)
            {
                _logoVisual = _compositor.CreateSpriteVisual();
                _logoBrush = _compositor.CreateSurfaceBrush();
                _logoBrush.Stretch = CompositionStretch.Uniform;
                _logoBrush.HorizontalAlignmentRatio = 0.0f;
                _logoBrush.VerticalAlignmentRatio = 1.0f;
                _logoVisual.Brush = _logoBrush;
                _logoVisual.RelativeSizeAdjustment = System.Numerics.Vector2.One;
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(IdentityControl.LogoHost, _logoVisual);
            }

            _parallaxController?.SetupParallax();
        }

        private void CloseTrailerButton_Click(object sender, RoutedEventArgs e)
        {
            CloseTrailer();
        }

        private void TrailerScrim_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CloseTrailer();
        }

        internal void ClosePersonCard() => _castDirectorManager?.ClosePersonCard();

        private void SyncIdentityVisibility(bool showEpisode)
        {
            if (IdentityControl?.TitleTextBlock == null) return;

            DispatcherQueue.TryEnqueue(() => 
            {
                // Only handle text content here
                string targetTitle = showEpisode && _selectedEpisode != null 
                    ? (!string.IsNullOrEmpty(_selectedEpisode.Title) ? _selectedEpisode.Title : $"Bölüm {_selectedEpisode.EpisodeNumber}")
                    : (_unifiedMetadata?.Title ?? _item?.Title ?? "");

                if (IdentityControl.TitleTextBlock.Text != targetTitle) IdentityControl.TitleTextBlock.Text = targetTitle;
                if (StickyTitle != null && StickyTitle.Text != targetTitle) StickyTitle.Text = targetTitle;

                // Handle logo-vs-title visibility based on selection state
                bool hasLogo = IdentityControl.LogoHost.Visibility == Visibility.Visible;
                bool isEpisodeSelected = showEpisode && _selectedEpisode != null;

                if (hasLogo)
                {
                    // If logo is present, title text only shows for episodes
                    IdentityControl.TitleTextBlock.Visibility = isEpisodeSelected ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    // No logo, title text must be visible
                    IdentityControl.TitleTextBlock.Visibility = Visibility.Visible;
                }

                // Let SyncLayout handle all Visibility and Opacity (only if state changed)
                if (_pageLoadState != PageLoadState.Ready || _lastSyncedEpisodeId != (_selectedEpisode?.Id ?? ""))
                {
                    OnIdentityChanged();
                }
            });
        }

        internal void UpdateInfoPanelVisibility(bool showEpisode)
        {
            SyncIdentityVisibility(showEpisode);

            if (showEpisode && _selectedEpisode != null)
            {
                if (YearText != null)
                {
                    var epYear = _selectedEpisode.ReleaseDate.HasValue ? _selectedEpisode.ReleaseDate.Value.ToString("yyyy-MM-dd") : (_unifiedMetadata?.Year ?? "");
                    if (YearText.Text != epYear) YearText.Text = epYear;
                }

                if (OverviewText != null)
                {
                    var epOverview = !string.IsNullOrEmpty(_selectedEpisode.Overview) ? _selectedEpisode.Overview : (_unifiedMetadata?.Overview ?? "");
                    if (OverviewText.Text != epOverview) OverviewText.Text = epOverview;
                }
                
                // Preserve genres if available
                if (GenresText != null)
                {
                    var sGenres = _unifiedMetadata?.Genres ?? _item?.Genres ?? "";
                    bool hasGenres = !string.IsNullOrEmpty(sGenres);
                    GenresText.Text = sGenres;
                    GenresText.Visibility = hasGenres ? Visibility.Visible : Visibility.Collapsed;
                    if (GenresText.Parent is Grid pGrid1) pGrid1.Visibility = hasGenres ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            else
            {
                if (YearText != null)
                {
                    var sYear = _unifiedMetadata?.Year ?? _item?.Year ?? "";
                    if (YearText.Text != sYear) YearText.Text = sYear;
                }

                if (OverviewText != null)
                {
                    var sOverview = _unifiedMetadata?.Overview ?? _item?.Description ?? "";
                    if (OverviewText.Text != sOverview) OverviewText.Text = sOverview;
                }

                if (GenresText != null)
                {
                    var sGenres = _unifiedMetadata?.Genres ?? _item?.Genres ?? "";
                    if (GenresText.Text != sGenres) { GenresText.Text = sGenres; }
                    
                    bool hasGenres = !string.IsNullOrEmpty(sGenres);
                    if (GenresText.Visibility != (hasGenres ? Visibility.Visible : Visibility.Collapsed))
                    {
                        GenresText.Visibility = hasGenres ? Visibility.Visible : Visibility.Collapsed;
                        if (GenresText.Parent is Grid pGrid2) pGrid2.Visibility = hasGenres ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
            if (IdentityControl?.TitleTextBlock == null) return;

            string targetTitle = string.Empty;
            if (_selectedEpisode != null)
            {
                targetTitle = !string.IsNullOrWhiteSpace(_selectedEpisode.Title) 
                    ? _selectedEpisode.Title 
                    : $"Bölüm {_selectedEpisode.EpisodeNumber}";
            }
            else if (_item != null)
            {
                targetTitle = _item.Title;
            }

            if (!string.IsNullOrEmpty(targetTitle))
            {
                if (IdentityControl.TitleTextBlock.Text != targetTitle) IdentityControl.TitleTextBlock.Text = targetTitle;
            }
        }

        internal void EnsureEpisodeTitleVisibleUnderLogo()
        {
            if (IdentityControl == null || _selectedEpisode == null) return;

            SyncIdentityVisibility(true);
            _visualStateController.SetState(IdentityControl, Visibility.Visible, 1.0);

            CompositionService.Run(IdentityControl, visual => 
            {
                visual.StopAnimation(nameof(Visual.Opacity));
                visual.Opacity = 1f;
                visual.Clip = null;
            });
        }


        private void FinalizeLogoReady(string url)
        {
            bool stateChanged = !_isLogoReady;
            _isLogoReady = true;
            _isLogoPending = false;
            _isLogoFallbackActive = false;
            if (IdentityControl?.TitleTextBlock != null && _selectedEpisode == null) IdentityControl.TitleTextBlock.Visibility = Visibility.Collapsed;
            if (stateChanged) OnIdentityChanged();
        }

        private void HandleLogoLoadFailure(string url)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_currentLogoUrl != url) return;
                bool stateChanged = !_isLogoFallbackActive;
                _currentLogoUrl = null;
                _isLogoReady = false;
                _isLogoPending = false;
                _isLogoFallbackActive = true;
                if (IdentityControl != null)
                {
                    if (IdentityControl.LogoHost != null) IdentityControl.LogoHost.Visibility = Visibility.Collapsed;
                    if (IdentityControl.TitleTextBlock != null) IdentityControl.TitleTextBlock.Visibility = Visibility.Visible;
                }
                if (stateChanged) OnIdentityChanged();
                System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] Logo load failed - falling back to title text.");
            });
        }


        private T FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            if (obj == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                if (child != null && child is T t) return t;
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null) return childOfChild;
            }
            return null;
        }

        private T FindParentInternal<T>(DependencyObject element) where T : DependencyObject
        {
            while (element != null)
            {
                if (element is T t) return t;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        #endregion

        #region Internal Accessors for Extracted Services

        internal IMediaStream Item => _item;
        internal string StreamUrl { get => _streamUrl; set => _streamUrl = value; }
        internal string LastSyncedEpisodeId { get => _lastSyncedEpisodeId; set => _lastSyncedEpisodeId = value; }
        internal bool IsEpisodesLoading { get => _isEpisodesLoading; set => _isEpisodesLoading = value; }
        internal bool IsResettingPageState => _isResettingPageState;
        internal bool IsNavigatingAway => _isNavigatingAway;
        internal bool IsApplyingSourceSelection { get => _isApplyingSourceSelection; set => _isApplyingSourceSelection = value; }
        internal bool SuppressSourceSelectionUi { get => _suppressSourceSelectionUi; set => _suppressSourceSelectionUi = value; }
        internal string CurrentStremioVideoId { get => _currentStremioVideoId; set => _currentStremioVideoId = value; }
        internal Compositor CompositorInstance => _compositor;
        internal ObservableCollection<StremioAddonViewModel> AddonResults { get => _addonResults; set => _addonResults = value; }
        internal ObservableCollection<StremioStreamViewModel> VisibleSourceStreamsCollection => _visibleSourceStreams;
        public ObservableCollection<StremioStreamViewModel> VisibleSourceStreams => _visibleSourceStreams;
        internal Grid SourcesPanelField => SourcesPanel;
        internal ItemsRepeater SourcesRepeaterField => SourcesRepeater;
        internal ListView AddonSelectorListField => AddonSelectorList;
        internal Microsoft.UI.Xaml.Shapes.Rectangle AddonSelectionUnderlineField => AddonSelectionUnderline;
        internal ScrollViewer SourcesScrollViewerField => SourcesScrollViewer;
        internal ComboBox SeasonComboBoxControl => SeasonComboBox;
        internal CancellationTokenSource? SeasonEnrichCts { get => _seasonEnrichCts; set => _seasonEnrichCts = value; }
        internal ScrollViewer RootScrollViewerControl => RootScrollViewer;
        internal Image HeroImageControl => HeroImage;
        internal Grid TrailerContentControl => TrailerContent;
        internal Grid TrailerScrimControl => TrailerScrim;
        internal Grid TrailerOverlayControl => TrailerOverlay;
        internal Button CloseTrailerButtonControl => CloseTrailerButton;
        internal double? XamlRootSizeWidth => XamlRoot?.Size.Width;
        internal double? XamlRootSizeHeight => XamlRoot?.Size.Height;
        internal void SetTrailerOverlayVisibility(Visibility v) { if (TrailerOverlay != null) TrailerOverlay.Visibility = v; }
        internal void SetTrailerScrimOpacity(double o) { if (TrailerScrim != null) TrailerScrim.Opacity = o; }
        internal void SetTrailerLoadingRing(bool active) { if (TrailerLoadingRing != null) { TrailerLoadingRing.IsActive = active; TrailerLoadingRing.Visibility = active ? Visibility.Visible : Visibility.Collapsed; } }
        internal void SetTrailerContentOpacity(double o) { if (TrailerContent != null) TrailerContent.Opacity = o; }
        internal void ResetTrailerTransform() { if (TrailerTransform != null) { TrailerTransform.TranslateX = 0; TrailerTransform.TranslateY = 0; TrailerTransform.ScaleX = 1; TrailerTransform.ScaleY = 1; } }
        internal void SetTrailerWebViewOpacity(double o) { if (TrailerContent != null) { foreach (var c in TrailerContent.Children) { if (c is WebView2 wv) wv.Opacity = o; } } }
        internal void ResetTrailerWebViewInitialized() { _isTrailerWebViewInitialized = false; }
        internal ListViewBase CastListViewControl => CastListView;
        internal bool IsPersonCardVisible => PersonCardOverlay?.Visibility == Visibility.Visible;
        internal void SetCastListItemsSource(IEnumerable<CastItem> source) { if (CastListView != null) CastListView.ItemsSource = source; }
        internal void SetNarrowCastListItemsSource(IEnumerable<CastItem> source) { if (NarrowCastListView != null) NarrowCastListView.ItemsSource = source; }
        internal void SetDirectorListItemsSource(IEnumerable<CastItem> source) { if (DirectorListView != null) DirectorListView.ItemsSource = source; }
        internal void SetNarrowDirectorListItemsSource(IEnumerable<CastItem> source) { if (NarrowDirectorListView != null) NarrowDirectorListView.ItemsSource = source; }
        internal void SetDirectorHeaderText(string text) { if (DirectorHeader != null) DirectorHeader.Text = text; if (NarrowDirectorHeader != null) NarrowDirectorHeader.Text = text; }
        internal Grid PersonCardOverlayControl => PersonCardOverlay;
        internal Controls.PersonExpandedCard ActivePersonCardControl => ActivePersonCard;
        internal CastItem ActivePersonCardItem { get => _activePersonCardItem; set => _activePersonCardItem = value; }
        internal void SetPersonCardOverlayVisibility(Visibility v) { if (PersonCardOverlay != null) PersonCardOverlay.Visibility = v; }
        internal void SetPersonCardOverlayHitTestVisible(bool v) { if (PersonCardOverlay != null) PersonCardOverlay.IsHitTestVisible = v; }
        internal void LoadPersonCardAsync(string name, string character, string profileUrl, string imdbId, Models.Tmdb.TmdbMovieResult tmdbInfo, Action<IMediaStream> onPersonClick)
        { ActivePersonCard.LoadPersonAsync(name, character, profileUrl, imdbId, tmdbInfo, onPersonClick); }
        internal Point GetCurrentPoint(FrameworkElement element, PointerRoutedEventArgs e) => e.GetCurrentPoint(element).Position;
        internal void AbortMainDragging() { _isMainDragging = false; }
        internal void NavigateToMediaInfoPage(IMediaStream stream) { Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(stream)); }
        internal ScrollViewer GetScrollViewer(DependencyObject element) => GetScrollViewerInternal(element);

        internal void SetOverviewText(string text)
        {
            if (OverviewText != null)
                OverviewText.Text = text;
        }

        internal int CurrentLoadingVersion => Volatile.Read(ref _loadingVersion);
        internal void ResetPageState() => ResetPageStateInternal();
        internal async Task PrepareInfoSkeletonForRevealAsync() => await PrepareInfoSkeletonAsync();
        internal CancellationTokenSource? PageCts => _pageCts;
        internal UnifiedMetadata UnifiedMetadata { get => _unifiedMetadata; set => _unifiedMetadata = value; }
        internal Services.MediaInfo.TrailerManager TrailerManager => _trailerManager;
        internal EpisodeItem SelectedEpisodeItem => _selectedEpisode;
        internal void SetSelectedEpisode(EpisodeItem ep) { _selectedEpisode = ep; _lastSyncedEpisodeId = ep?.Id ?? ""; }
        internal Button WatchlistButtonControl => WatchlistButton;
        internal Microsoft.UI.Xaml.Media.Brush ThemeTintBrush => _themeTintBrush;
        internal string TitleText => IdentityControl?.TitleTextBlock?.Text;
        internal string YearTextValue => YearText?.Text;
        internal string RatingTextValue => _unifiedMetadata?.Rating.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        internal string DurationTextValue => _unifiedMetadata?.Runtime;
        internal string OverviewTextValue => _unifiedMetadata?.Overview;
        internal string PrimaryColorHex => _backgroundManager?.PrimaryColorHex ?? "#FF00BFA5";
        internal MpvWinUI.MpvPlayer MediaInfoPlayerInstance { get => MediaInfoPlayer; set => MediaInfoPlayer = value; }
        internal MpvWinUI.MpvPlayer MediaInfoPlayerHandoff { get => App.HandoffPlayer; set => App.HandoffPlayer = value; }
        internal void UpdateMovieHistoryVisibility(bool visible) { /* HistoryBadge not in XAML */ }
        internal void SetHistoryProgressText(string text) { /* HistoryProgressText not in XAML */ }
        internal void SetHistoryProgressBarValue(double value) { /* HistoryProgressBar not in XAML */ }
        internal void AnimateButtonBrushColor(Control control, Windows.UI.Color color, double duration) => AnimateBrushColor(control, color, duration);
        internal string StreamUrlForAction => _streamUrl;
        internal string LogoUrlForAction => _item?.PosterUrl;

        #endregion
    }
}

