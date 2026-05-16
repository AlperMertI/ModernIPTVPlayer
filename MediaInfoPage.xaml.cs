using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    public sealed partial class MediaInfoPage : Page, IMediaInfoUIProxy
    {
        private MediaInfoCommitService _commitService;
        
        private IMediaStream _item;
        private bool _isProgrammaticSelection;
        private System.Collections.ObjectModel.ObservableCollection<StremioAddonViewModel> _addonResults;
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
        public ObservableCollection<EpisodeItem> CurrentEpisodes { get; private set; } = new();
        public ObservableCollection<CastItem> CastList { get; private set; } = new();
        public ObservableCollection<CastItem> DirectorList { get; private set; } = new();

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

        private EpisodeItem _selectedEpisode;
        private SeasonItem _selectedSeason;
        private TmdbMovieResult _cachedTmdb;
        private SolidColorBrush _themeTintBrush;
        private bool _isInitializingSeriesUi;
        private static readonly Dictionary<string, StremioSourcesCacheEntry> _stremioSourcesCache = new();
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

        private string ResolveBestContentId(string? rawId)
        {
            if (string.IsNullOrEmpty(rawId)) return rawId;

            // 1. If we have a resolved IMDb ID for the parent show, use it to reconstruct the ID
            if (_unifiedMetadata != null && !string.IsNullOrEmpty(_unifiedMetadata.ImdbId) && _unifiedMetadata.ImdbId.StartsWith("tt"))
            {
                // If the rawId is a TMDB episode ID (e.g. tmdb:79788:1:1)
                if (rawId.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase) && rawId.Contains(":"))
                {
                    var parts = rawId.Split(':');
                    if (parts.Length >= 3) // tmdb:id:s:e
                    {
                        var resolved = $"{_unifiedMetadata.ImdbId}:{parts[parts.Length - 2]}:{parts[parts.Length - 1]}";
                        return resolved;
                    }
                }
                
                // If it's just the show ID
                if (rawId.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase) && !rawId.Contains(":"))
                {
                    return _unifiedMetadata.ImdbId;
                }
            }

            // 2. Fallback to IdMappingService for persistent cross-references
            if (rawId.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = rawId.Split(':');
                if (parts.Length > 1)
                {
                    string tmdbIdOnly = parts[1];
                    string resolved = ModernIPTVPlayer.Services.Metadata.IdMappingService.Instance.GetImdbForTmdb(tmdbIdOnly);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        if (rawId.Contains(":") && parts.Length >= 3)
                        {
                            return $"{resolved}:{parts[parts.Length-2]}:{parts[parts.Length-1]}";
                        }
                        return resolved;
                    }
                }
            }

            return rawId;
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
            _sourcesPanelController = new SourcesPanelController(this);
            sw.Stop();
            Debug.WriteLine($"[NAV-TIMING] SourcesPanelController creation: {sw.ElapsedMilliseconds}ms");

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

            // Robust Drag-to-Scroll Registration (Horizontal - Cast)
            CastListView.AddHandler(PointerPressedEvent, new PointerEventHandler(OnCastPointerPressed), true);
            CastListView.AddHandler(PointerMovedEvent, new PointerEventHandler(OnCastPointerMoved), true);
            CastListView.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnCastPointerReleased), true);
            CastListView.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnCastPointerReleased), true);
            CastListView.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnCastPointerReleased), true);

            _personHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _personHoverTimer.Tick += PersonHoverTimer_Tick;

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
                
                _slideshowTimer?.Stop();
                _slideshowTimer = null;

                // 2. Clear Managed Collections (Frees strings and model objects)
                Seasons?.Clear();
                CurrentEpisodes?.Clear();
                CastList?.Clear();
                DirectorList?.Clear();
                _revealedCastGeneration = 0;
                _revealedDirectorGeneration = 0;
                _commitService?.Reset();
                _addonResults?.Clear();
                ResetBackground();

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

                CastListView.RemoveHandler(PointerPressedEvent, new PointerEventHandler(OnCastPointerPressed));
                CastListView.RemoveHandler(PointerMovedEvent, new PointerEventHandler(OnCastPointerMoved));
                CastListView.RemoveHandler(PointerReleasedEvent, new PointerEventHandler(OnCastPointerReleased));
                CastListView.RemoveHandler(PointerCanceledEvent, new PointerEventHandler(OnCastPointerReleased));
                CastListView.RemoveHandler(PointerCaptureLostEvent, new PointerEventHandler(OnCastPointerReleased));
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
        private int BeginLoadSession() => Interlocked.Increment(ref _loadingVersion);

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
                int widthBucket = (int)Math.Round(e.NewSize.Width / 8.0);
                int heightBucket = (int)Math.Round(e.NewSize.Height / 8.0);
                bool forceSync = !_isFirstLayoutApplied || (_panelOwner != null && _panelOwner.PanelMode == MediaDetailPanelMode.Sources && SourcesPanel?.Visibility != Visibility.Visible);

                if (_isWideModeIndex != layoutIndex || forceSync)
                {
                    _isWideModeIndex = layoutIndex;
                    _isFirstLayoutApplied = true;
                    OnViewportChanged();
                    QueueInfoPriorityLayout(layoutIndex == 1);
                }
                else if (_lastResponsiveWidthBucket != widthBucket || _lastResponsiveHeightBucket != heightBucket)
                {
                    _lastResponsiveWidthBucket = widthBucket;
                    _lastResponsiveHeightBucket = heightBucket;
                    QueueInfoPriorityLayout(layoutIndex == 1);
                }

                RefreshAllShimmers();

                // State Machine: Initial -> LayoutReady
                if (_pageLoadState == PageLoadState.Initial)
                {
                    _pageLoadState = PageLoadState.LayoutReady;
                    if (_pendingLoadItem != null)
                    {
                        var pendingItem = _pendingLoadItem;
                        _pendingLoadItem = null;
                        _ = LoadDetailsAsync(pendingItem, loadSession: Volatile.Read(ref _loadingVersion));
                    }
                }

                if (TrailerOverlay?.Visibility == Visibility.Visible)
                {
                    ApplyTrailerFullscreenLayout(enable: true);
                }
            }
            finally
            {
                _isProcessingSizeChanged = false;
            }
        }

        #region Info Layout Helpers

        private void SetActionTextVisible(FrameworkElement textHost, bool visible)
        {
            if (textHost == null) return;

            textHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            textHost.Opacity = visible ? 1 : 0;
            ElementCompositionPreview.GetElementVisual(textHost).Opacity = visible ? 1f : 0f;
        }

        private double EstimateActionTextWidth(TextBlock primaryText, TextBlock secondaryText, bool showSecondary)
        {
            double primaryWidth = EstimateTextWidth(primaryText?.Text, primaryText?.FontSize ?? 14, 0.58);
            if (!showSecondary)
            {
                return primaryWidth;
            }

            double secondaryWidth = EstimateTextWidth(secondaryText?.Text, secondaryText?.FontSize ?? 11, 0.54);
            return Math.Max(primaryWidth, secondaryWidth);
        }

        private static double EstimateTextWidth(string text, double fontSize, double averageGlyphFactor)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            return Math.Ceiling(text.Length * fontSize * averageGlyphFactor);
        }

        private double GetExpandedActionWidth(double actionSize, double textWidth, double padding, double textGap, double minWidth)
        {
            double rawWidth = PrimaryActionIconWidth + textGap + textWidth + (padding * 2);
            return Math.Ceiling(Math.Max(minWidth, rawWidth));
        }

        private bool ShouldUseIconOnlyActions(
            bool isWide,
            double availableWidth,
            double actionSize,
            double spacing,
            double playExpandedWidth,
            double restartExpandedWidth)
        {
            if (!isWide)
            {
                return true;
            }

            int secondaryButtonCount = CountVisibleActionButtons(TrailerButton, DownloadButton, CopyLinkButton, WatchlistButton);
            bool restartVisible = RestartButton?.Visibility == Visibility.Visible;
            int visibleButtonCount = 1 + secondaryButtonCount + (restartVisible ? 1 : 0);
            double totalSpacing = Math.Max(0, visibleButtonCount - 1) * spacing;
            double secondaryWidth = secondaryButtonCount * actionSize;
            double desiredWidth = playExpandedWidth + secondaryWidth + totalSpacing;

            if (restartVisible)
            {
                desiredWidth += restartExpandedWidth;
            }

            return desiredWidth > availableWidth - ActionBarOverflowGuard;
        }

        private static int CountVisibleActionButtons(params Button[] buttons)
        {
            int count = 0;
            foreach (var button in buttons)
            {
                if (button?.Visibility == Visibility.Visible)
                {
                    count++;
                }
            }

            return count;
        }

        private void AnimateActionTextIn(FrameworkElement textHost)
        {
            if (textHost == null || _compositor == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(textHost);
            visual.StopAnimation(nameof(visual.Opacity));
            
            visual.Opacity = 0f;

            var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 0.9f), new Vector2(0.24f, 1f));

            var opacity = _compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(1f, 1f, easing);
            opacity.Duration = TimeSpan.FromMilliseconds(180);
            visual.StartAnimation(nameof(visual.Opacity), opacity);

            var translation = _compositor.CreateVector3KeyFrameAnimation();
            translation.InsertKeyFrame(1f, Vector3.Zero, easing);
            translation.Duration = TimeSpan.FromMilliseconds(220);
            CompositionService.StartTranslationAnimation(textHost, translation, new Vector3(12f, 0f, 0f));
        }

        private void AnimateActionTextOut(FrameworkElement textHost)
        {
            if (textHost == null || _compositor == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(textHost);
            visual.StopAnimation(nameof(visual.Opacity));

            var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 0.4f));

            var opacity = _compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(1f, 0f, easing);
            opacity.Duration = TimeSpan.FromMilliseconds(90);
            visual.StartAnimation(nameof(visual.Opacity), opacity);

            var translation = _compositor.CreateVector3KeyFrameAnimation();
            translation.InsertKeyFrame(1f, new Vector3(8f, 0f, 0f), easing);
            translation.Duration = TimeSpan.FromMilliseconds(110);
            CompositionService.StartTranslationAnimation(textHost, translation, Vector3.Zero);
        }

        private void AnimateActionButtonSettle(Button button)
        {
            if (button == null || _compositor == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(button);
            visual.StopAnimation(nameof(visual.Scale));
            visual.CenterPoint = new Vector3((float)button.ActualWidth / 2f, (float)button.ActualHeight / 2f, 0f);
            visual.Scale = new Vector3(0.96f, 0.96f, 1f);

            var spring = _compositor.CreateSpringVector3Animation();
            spring.FinalValue = new Vector3(1f, 1f, 1f);
            spring.DampingRatio = 0.78f;
            spring.Period = TimeSpan.FromMilliseconds(55);
            visual.StartAnimation(nameof(visual.Scale), spring);
        }

        private void AnimateOpacity(UIElement element, float opacity, int milliseconds)
        {
            if (element == null)
            {
                return;
            }

            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation(nameof(visual.Opacity));
            element.Opacity = 1;

            if (_compositor == null || milliseconds <= 0)
            {
                element.Opacity = opacity;
                visual.Opacity = opacity;
                return;
            }

            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1.0f, opacity);
            animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
            visual.StartAnimation(nameof(visual.Opacity), animation);
        }

        private void ApplyPrimaryActionButton(
            Button button,
            FrameworkElement textHost,
            double actionSize,
            double expandedWidth,
            bool iconOnly,
            double expandedPadding,
            ref bool lastIconOnlyState,
            ref int transitionVersion)
        {
            if (button == null) return;

            bool modeChanged = lastIconOnlyState != iconOnly;
            int version = ++transitionVersion;

            if (modeChanged)
            {
                if (iconOnly)
                {
                    // Transitioning to Icon-Only: Start with expanded width and fade out text
                    AnimateActionTextOut(textHost);
                    AnimateButtonWidth(button, actionSize, 300);
                    
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        // Hide text early in the collapse to prevent overlap as width shrinks
                        await Task.Delay(80);
                        
                        int currentVersion = ReferenceEquals(button, PlayButton)
                            ? _playActionTransitionVersion
                            : _restartActionTransitionVersion;

                        if (currentVersion != version) return;

                        SetActionTextVisible(textHost, false);
                        button.Padding = new Thickness(0);
                        AnimateActionButtonSettle(button);
                    });
                }
                else
                {
                    // Transitioning to Expanded: Smoothly expand
                    AnimateButtonWidth(button, expandedWidth, 300);
                    button.Padding = new Thickness(expandedPadding, 0, expandedPadding, 0);
                    
                    // Show textHost immediately and start fade-in animation
                    SetActionTextVisible(textHost, true);
                    AnimateActionTextIn(textHost);
                }
                
                lastIconOnlyState = iconOnly;
            }
            else
            {
                // Stable state update (resizing within the same mode)
                // Only update if we are not currently in the middle of a transition (version check is hard here, so we check physical state)
                bool isTransitioning = Math.Abs(button.Width - (iconOnly ? actionSize : expandedWidth)) > 5.0 && !double.IsNaN(button.Width);
                
                if (!isTransitioning)
                {
                    double targetWidth = iconOnly ? actionSize : expandedWidth;
                    if (Math.Abs(button.Width - targetWidth) > 0.5 && !double.IsNaN(button.Width))
                    {
                        button.Width = targetWidth;
                    }
                    
                    button.Padding = iconOnly ? new Thickness(0) : new Thickness(expandedPadding, 0, expandedPadding, 0);
                    
                    if (textHost != null)
                    {
                        if (!iconOnly && textHost.Visibility == Visibility.Collapsed)
                            SetActionTextVisible(textHost, true);
                        else if (iconOnly && textHost.Visibility == Visibility.Visible)
                            SetActionTextVisible(textHost, false);
                    }
                }
            }

            button.MinWidth = actionSize;
            button.Height = actionSize;
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.CornerRadius = new CornerRadius(actionSize / 2);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
        }

        private double GetInfoPanelWidth()
        {
            if (InfoContainer != null && InfoContainer.ActualWidth > 0)
            {
                return InfoContainer.ActualWidth;
            }
            return _lastReportedWidth > 0 ? _lastReportedWidth : 400;
        }

        private void ApplyInfoPriorityLayout(bool isWide)
        {
            double infoWidth = GetInfoPanelWidth();
            double viewportHeight = GetViewportHeight();
            const double comfortableInfoWidth = 760.0;
            double layoutWidth = isWide ? infoWidth : Math.Min(infoWidth, 430.0);

            double widthFactor = 1.0; // Remove adaptive scaling in Wide mode to keep elements at 100%
            double visualFactor = widthFactor;

            bool compactActions = !isWide || layoutWidth < WideInfoCompactThreshold;
            // Keep the primary action controls visually stable in wide mode. Text can collapse
            // when space is tight, but the button diameter should not shrink during panel changes.
            double actionSize = 52; // Keep button diameter consistent across all modes to avoid "shrinking" look
            double actionSpacing = isWide ? 12 : 8; // Stable spacing in Wide mode
            double playPadding = isWide ? 18 : 14;  // Stable padding in Wide mode
            double restartPadding = isWide ? 16 : 14;
            bool showPlaySubtext = !string.IsNullOrWhiteSpace(PlayButtonSubtext?.Text);
            double playExpandedWidth = GetExpandedActionWidth(
                actionSize,
                EstimateActionTextWidth(PlayButtonText, PlayButtonSubtext, showPlaySubtext),
                playPadding,
                12,
                PrimaryActionMinExpandedWidth);
            double restartExpandedWidth = GetExpandedActionWidth(
                actionSize,
                EstimateActionTextWidth(RestartButtonText, null, false),
                restartPadding,
                10,
                RestartActionMinExpandedWidth);
            bool iconOnlyPlay = ShouldUseIconOnlyActions(
                isWide,
                Math.Clamp(layoutWidth, 320, 800),
                actionSize,
                actionSpacing,
                playExpandedWidth,
                restartExpandedWidth);
            bool hasLogoIdentity = !string.IsNullOrWhiteSpace(_currentLogoUrl) && !_isLogoFallbackActive;
            bool hasEpisodeTitleUnderLogo = hasLogoIdentity && _selectedEpisode != null;
            double logoWidth = isWide ? Math.Round(372 * widthFactor) : 320;
            double logoHeight = hasLogoIdentity
                ? (isWide ? Math.Round(86 * widthFactor) : 78)
                : (isWide ? Math.Round(104 * widthFactor) : 94);
            double peopleHeight = 145;
            bool showPeopleList = isWide && viewportHeight >= WidePeopleComfortHeight;
            double visiblePeopleHeight = showPeopleList ? peopleHeight : 0;
            double peopleSectionWidth = Math.Clamp(layoutWidth, 360, 800);
            double titleFontSize = isWide ? Math.Round(42 * visualFactor) : 28;
            int overviewMaxLines = isWide ? (viewportHeight < 660 ? 5 : 7) : 0;
            int layoutSignature = HashCode.Combine(
                HashCode.Combine(
                    HashCode.Combine(
                        HashCode.Combine(
                            HashCode.Combine(
                                isWide,
                                compactActions,
                                iconOnlyPlay,
                                (int)Math.Round(actionSize),
                                (int)Math.Round(playExpandedWidth),
                                (int)Math.Round(restartExpandedWidth),
                                (int)Math.Round(logoWidth),
                                (int)Math.Round(logoHeight)),
                            hasLogoIdentity,
                            hasEpisodeTitleUnderLogo),
                        (int)Math.Round(layoutWidth / 8.0),
                        (int)Math.Round(visiblePeopleHeight)),
                    showPeopleList),
                (int)Math.Round(titleFontSize),
                overviewMaxLines);

            if (_lastInfoLayoutSignature == layoutSignature)
            {
                return;
            }

            _lastInfoLayoutSignature = layoutSignature;

            if (AdaptiveInfoHost != null)
            {
                AdaptiveInfoHost.Width = double.NaN;
                AdaptiveInfoHost.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
                AdaptiveInfoHost.VerticalAlignment = isWide ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            }

            if (InfoContainerInner != null)
            {
                InfoContainerInner.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            }

            if (InfoColumn != null)
            {
                InfoColumn.Width = double.NaN;
                InfoColumn.MaxWidth = isWide ? Math.Clamp(layoutWidth, 360, 800) : 800;
                InfoColumn.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                InfoColumn.Spacing = isWide
                    ? Math.Round((hasLogoIdentity
                        ? (hasEpisodeTitleUnderLogo ? 6 : 10)
                        : (compactActions ? 12 : 16)) * visualFactor)
                    : (hasLogoIdentity ? (hasEpisodeTitleUnderLogo ? 6 : 8) : 12);
            }

            if (IdentityControl != null)
            {
                if (IdentityControl.LogoHost != null)
                {
                    IdentityControl.LogoHost.Width = logoWidth;
                    IdentityControl.LogoHost.Height = logoHeight;
                    IdentityControl.LogoHost.MaxHeight = logoHeight;
                    IdentityControl.LogoHost.MaxWidth = IdentityControl.LogoHost.Width;
                    IdentityControl.LogoHost.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                }

                if (IdentityControl.LogoImage != null)
                {
                    IdentityControl.LogoImage.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                }

                if (IdentityControl.TitlePanelElement != null)
                {
                    IdentityControl.TitlePanelElement.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                    IdentityControl.TitlePanelElement.Spacing = hasEpisodeTitleUnderLogo ? 4 : 0;
                }

                if (IdentityControl.IdentityPanel != null)
                {
                    IdentityControl.IdentityPanel.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                }
            }

            if (_logoBrush != null)
            {
                _logoBrush.HorizontalAlignmentRatio = isWide ? 0.0f : 0.5f;
            }

            if (MetadataRibbon != null)
            {
                MetadataRibbon.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            }

            if (ActionBarGroup != null)
            {
                ActionBarGroup.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                ActionBarGroup.MaxWidth = Math.Clamp(layoutWidth, 320, 800);
            }
            if (ActionBarPanel != null)
            {
                ActionBarPanel.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                ActionBarPanel.Spacing = actionSpacing;
                ActionBarPanel.MaxWidth = Math.Clamp(layoutWidth, 320, 800);
            }

            ApplyPrimaryActionButton(
                PlayButton,
                PlayButtonTextStack,
                actionSize,
                playExpandedWidth,
                iconOnlyPlay,
                playPadding,
                ref _lastPlayIconOnlyState,
                ref _playActionTransitionVersion);

            if (PlayButtonSubtext != null)
            {
                PlayButtonSubtext.Visibility = !showPlaySubtext || iconOnlyPlay
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            ApplyPrimaryActionButton(
                RestartButton,
                RestartButtonTextStack,
                actionSize,
                restartExpandedWidth,
                iconOnlyPlay,
                restartPadding,
                ref _lastRestartIconOnlyState,
                ref _restartActionTransitionVersion);

            foreach (var btn in new[] { TrailerButton, DownloadButton, CopyLinkButton, WatchlistButton })
            {
                if (btn == null) continue;
                btn.Width = actionSize;
                btn.Height = actionSize;
                btn.HorizontalContentAlignment = HorizontalAlignment.Center;
                btn.VerticalContentAlignment = VerticalAlignment.Center;
                btn.CornerRadius = new CornerRadius(actionSize / 2);
            }

            if (OverviewText != null)
            {
                ApplyOverviewTextLayout(isWide, visualFactor, overviewMaxLines);
            }

            if (OverviewPanel != null)
            {
                OverviewPanel.Width = double.NaN;
            }

            if (CastSection != null)
            {
                CastSection.Width = peopleSectionWidth;
                CastSection.MaxWidth = peopleSectionWidth;
                CastSection.MinHeight = 0;
                CastSection.Height = double.NaN;
                CastSection.Visibility = (showPeopleList && (CastList?.Count > 0 || CastShimmer?.Visibility == Visibility.Visible)) ? Visibility.Visible : Visibility.Collapsed;
                CastSection.IsHitTestVisible = isWide;
            }

            if (DirectorSection != null)
            {
                DirectorSection.Width = peopleSectionWidth;
                DirectorSection.MaxWidth = peopleSectionWidth;
                DirectorSection.MinHeight = 0;
                DirectorSection.Height = double.NaN;
                bool showDirectorSkeleton = _pageLoadState != PageLoadState.Ready && DirectorShimmer?.Visibility == Visibility.Visible;
                DirectorSection.Visibility = (showPeopleList && (DirectorList?.Count > 0 || showDirectorSkeleton)) ? Visibility.Visible : Visibility.Collapsed;
                DirectorSection.IsHitTestVisible = isWide;
            }

            ApplyPeopleListState(CastListView, peopleSectionWidth, peopleHeight, showPeopleList);
            ApplyPeopleListState(DirectorListView, peopleSectionWidth, peopleHeight, showPeopleList);

            if (GenresText != null)
            {
                GenresText.TextAlignment = isWide ? TextAlignment.Left : TextAlignment.Center;
            }

            if (IdentityControl != null && IdentityControl.TitleTextBlock != null)
            {
                var titleText = IdentityControl.TitleTextBlock;
                titleText.FontSize = titleFontSize;
                titleText.LineHeight = Math.Round(titleFontSize * 1.04);
                titleText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                titleText.Margin = hasEpisodeTitleUnderLogo ? new Thickness(0, -2, 0, -6) : new Thickness(0);
                titleText.TextAlignment = isWide ? TextAlignment.Left : TextAlignment.Center;
            }

            if (MetadataRibbon != null)
            {
                double topPull = hasLogoIdentity
                    ? (hasEpisodeTitleUnderLogo ? 0 : (isWide ? -4 * visualFactor : -2))
                    : 0;
                MetadataRibbon.Margin = isWide
                    ? new Thickness(2, topPull, 0, Math.Round(8 * visualFactor))
                    : new Thickness(0, topPull, 0, 12);
            }
        }

        private void QueueInfoPriorityLayout(bool isWide)
        {
            if (_isResponsiveLayoutQueued)
            {
                return;
            }

            _isResponsiveLayoutQueued = true;
            CompositionTarget.Rendering += ApplyQueuedInfoPriorityLayout;
        }

        private void ApplyQueuedInfoPriorityLayout(object sender, object e)
        {
            CompositionTarget.Rendering -= ApplyQueuedInfoPriorityLayout;
            _isResponsiveLayoutQueued = false;
            ApplyInfoPriorityLayout(ActualWidth >= LayoutAdaptiveThreshold);
        }

        private void ApplyOverviewTextLayout(bool isWide)
        {
            double infoWidth = GetInfoPanelWidth();
            double layoutWidth = isWide ? infoWidth : Math.Min(infoWidth, 430.0);
            double visualFactor = isWide ? Math.Clamp(layoutWidth / 760.0, 0.86, 1.0) : 1.0;
            double viewportHeight = GetViewportHeight();
            int overviewMaxLines = isWide ? (viewportHeight < 660 ? 5 : 7) : 0;
            ApplyOverviewTextLayout(isWide, visualFactor, overviewMaxLines);
        }

        private void ApplyOverviewTextLayout(bool isWide, double visualFactor, int overviewMaxLines)
        {
            if (OverviewText == null) return;

            OverviewText.FontSize = isWide ? Math.Round(15 * visualFactor) : 15;
            OverviewText.LineHeight = isWide ? Math.Round(24 * visualFactor) : 24;
            OverviewText.TextAlignment = TextAlignment.Left;
            OverviewText.MaxLines = overviewMaxLines;
            OverviewText.TextWrapping = TextWrapping.Wrap;
            OverviewText.TextTrimming = isWide ? TextTrimming.CharacterEllipsis : TextTrimming.None;
            OverviewText.Width = double.NaN;
        }

        private void ApplyPeopleListState(ListView listView, double width, double expandedHeight, bool showList)
        {
            if (listView == null) return;
            
            double targetHeight = showList ? expandedHeight : 0;
            
            // 1. Layout: Set height directly (Instant layout sync to prevent 'dependent' warnings)
            listView.Width = width;
            listView.MaxWidth = width;

            // 2. Composition: Animate the visual properties for a smooth glide
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(listView);
            var compositor = visual.Compositor;
            
            // Ensure translation is enabled
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(listView, true);

            if (showList)
            {
                // Instant layout update for showing (Expand is usually fine being instant in this context)
                listView.Height = targetHeight;
                listView.Visibility = Visibility.Visible;

                // Only animate if not already visible/opaque to prevent resize triggers
                if (visual.Opacity > 0.9f) return;

                // Slide Up + Fade In
                visual.Opacity = 0f;
                // Handled by StartTranslationAnimation below

                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(1f, 1f);
                fadeIn.Duration = TimeSpan.FromMilliseconds(450);

                var slideUp = compositor.CreateVector3KeyFrameAnimation();
                slideUp.InsertKeyFrame(1f, System.Numerics.Vector3.Zero);
                slideUp.Duration = TimeSpan.FromMilliseconds(450);

                visual.StartAnimation("Opacity", fadeIn);
                CompositionService.StartTranslationAnimation(StickyHeader, slideUp, new System.Numerics.Vector3(0, 20, 0));
            }
            else
            {
                // Only animate if not already hidden
                if (visual.Opacity < 0.1f) 
                {
                    listView.Height = 0;
                    listView.Visibility = Visibility.Collapsed;
                    return;
                }

                // NATIVE AOT SAFE SYNCHRONIZED COLLAPSE
                // We use InsetClip to visually shrink the element without reflection-based Storyboards
                var duration = TimeSpan.FromMilliseconds(350);
                var easing = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.4f, 0f), new System.Numerics.Vector2(0.2f, 1f));

                // 1. Create a clip to handle the "Accordion" shrink visually
                var clip = compositor.CreateInsetClip();
                visual.Clip = clip;

                var wipeAnim = compositor.CreateScalarKeyFrameAnimation();
                wipeAnim.InsertKeyFrame(0f, 0f);
                wipeAnim.InsertKeyFrame(1f, (float)listView.ActualHeight, easing);
                wipeAnim.Duration = duration;

                // 2. Opacity and Slide
                var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(1f, 0f, easing);
                fadeOut.Duration = duration;

                var slideDown = compositor.CreateVector3KeyFrameAnimation();
                slideDown.InsertKeyFrame(1f, new System.Numerics.Vector3(0, 15, 0), easing);
                slideDown.Duration = duration;

                // 3. Orchestrate with a Batch (No Reflection/Storyboards)
                var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                
                clip.StartAnimation(nameof(InsetClip.BottomInset), wipeAnim);
                visual.StartAnimation("Opacity", fadeOut);
                CompositionService.StartTranslationAnimation(StickyHeader, slideDown);
                
                batch.Completed += (s, e) => {
                    if (!showList) 
                    {
                        listView.Height = 0;
                        listView.Visibility = Visibility.Collapsed;
                        visual.Clip = null; // Clean up
                    }
                };
                batch.End();
            }
        }
        #endregion





        private void EnsureTrailerOverlayBounds()
        {
            if (TrailerOverlay == null || TrailerScrim == null)
            {
                return;
            }

            // Let XAML Stretch handle it. Explicitly unset any fixed sizes that might have been set.
            TrailerOverlay.Width = double.NaN;
            TrailerOverlay.Height = double.NaN;
            TrailerScrim.Width = double.NaN;
            TrailerScrim.Height = double.NaN;
        }

        private void ApplyTrailerFullscreenLayout(bool enable)
        {
            if (TrailerContent == null || TrailerOverlay == null)
            {
                return;
            }

            if (!enable)
            {
                TrailerContent.Width = TrailerDefaultWidth;
                TrailerContent.Height = TrailerDefaultHeight;
                
                // Reset close button
                if (CloseTrailerButton != null)
                {
                    CloseTrailerButton.Margin = new Thickness(16, 16, 16, 0);
                }
                return;
            }

            // Use XamlRoot size for the absolute bounds
            double overlayWidth = XamlRoot?.Size.Width ?? ActualWidth;
            double overlayHeight = XamlRoot?.Size.Height ?? ActualHeight;

            // Keep 16:9 while leaving comfortable margins around the trailer (200px total).
            double maxWidth = Math.Max(320, overlayWidth - 200);
            double maxHeight = Math.Max(180, overlayHeight - 160);

            double width = maxWidth;
            double height = width * 9.0 / 16.0;
            if (height > maxHeight)
            {
                height = maxHeight;
                width = height * 16.0 / 9.0;
            }

            TrailerContent.Width = width;
            TrailerContent.Height = height;

            if (CloseTrailerButton != null)
            {
                // Button is in Row 1 (below 48px title bar). 
                // A fixed margin of 16px provides a consistent, professional look regardless of window size.
                CloseTrailerButton.Margin = new Thickness(0, 16, 16, 0);
                CloseTrailerButton.HorizontalAlignment = HorizontalAlignment.Right;
                CloseTrailerButton.VerticalAlignment = VerticalAlignment.Top;
            }

            var visual = ElementCompositionPreview.GetElementVisual(TrailerContent);
            visual.StopAnimation("Offset");
            
            float centerX = (float)(width / 2.0);
            float centerY = (float)(height / 2.0);
            visual.CenterPoint = new Vector3(centerX, centerY, 0);
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
            if (item1 == null || item2 == null) return false;
            
            // 1. Exact ID match (IMDb, TMDB, or internal)
            string id1 = item1.IMDbId ?? item1.Id.ToString();
            string id2 = item2.Id.ToString(); // Use raw ID as secondary fallback
            if (!string.IsNullOrEmpty(item1.IMDbId)) id1 = item1.IMDbId;
            
            string rawId1 = item1.IMDbId;
            string rawId2 = item2.IMDbId;

            bool hasStableId1 = !string.IsNullOrEmpty(rawId1) && (rawId1.StartsWith("tt") || rawId1.StartsWith("tmdb:"));
            bool hasStableId2 = !string.IsNullOrEmpty(rawId2) && (rawId2.StartsWith("tt") || rawId2.StartsWith("tmdb:"));

            // [CRITICAL] If both have stable IDs and they differ -> Definitely different items.
            if (hasStableId1 && hasStableId2 && rawId1 != rawId2) 
            {
                return false;
            }

            // 2. ID Equality check (including non-stable IDs or cases where one is missing)
            if (!string.IsNullOrEmpty(rawId1) && !string.IsNullOrEmpty(rawId2) && rawId1 == rawId2) return true;

            // 3. Robust Fallback: Title + Year + Type
            // This handles cases where one item (usually IPTV) lacks a stable ID.
            if (!string.IsNullOrEmpty(item1.Title) && !string.IsNullOrEmpty(item2.Title))
            {
                bool titleMatch = item1.Title.Trim().Equals(item2.Title.Trim(), StringComparison.OrdinalIgnoreCase);
                
                string y1 = StremioService.GetYearDigits(item1.Year);
                string y2 = StremioService.GetYearDigits(item2.Year);
                bool yearMatch = (string.IsNullOrEmpty(y1) || string.IsNullOrEmpty(y2)) || (y1 == y2);
                
                // Content Type check (Movie vs Series)
                string t1 = item1.Type?.ToLowerInvariant() ?? "";
                string t2 = item2.Type?.ToLowerInvariant() ?? "";
                bool typeMatch = (string.IsNullOrEmpty(t1) || string.IsNullOrEmpty(t2)) || (t1 == t2 || (t1 == "tv" && t2 == "series") || (t1 == "series" && t2 == "tv"));

                if (titleMatch && yearMatch && typeMatch)
                {
                    return true;
                }
            }
            
            return false;
        }

        private bool IsSeriesItem()
        {
            if (_item == null) return false;
            if (_item is SeriesStream) return true;
            if (_item is StremioMediaStream sms && (sms.Meta.Type == "series" || sms.Meta.Type == "tv")) return true;
            if (!string.IsNullOrEmpty(_item.Type) && (_item.Type.Equals("SERIES", StringComparison.OrdinalIgnoreCase) || _item.Type.Equals("TV", StringComparison.OrdinalIgnoreCase))) return true;
            if (_unifiedMetadata != null && _unifiedMetadata.IsSeries) return true;
            return false;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] === OnNavigatedFrom START === Target: {e.SourcePageType.Name}");
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
                System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] Error closing trailer on navigate: {ex.Message}");
            }

            if (MediaInfoPlayer != null && PlayerHost != null)
            {
                 if (e.SourcePageType == typeof(PlayerPage) || _isHandoffInProgress)
                 {
                     DetachMediaInfoPlayerFromVisualTree(MediaInfoPlayer);
                     System.Diagnostics.Debug.WriteLine("[INFO-PAGE] Detached player for handover (preserving instance).");
                 }
                 else
                 {
                     try
                     {
                         System.Diagnostics.Debug.WriteLine("[INFO-PAGE] STRICT CLEANUP: Destroying MpvPlayer instance on exit (non-player destination).");
                         var pToCleanup = MediaInfoPlayer;
                         DetachMediaInfoPlayerFromVisualTree(pToCleanup);
                         MediaInfoPlayer = null; 
                         _prebufferUrl = null;
                         
                         CleanupMpvPlayerInBackground(pToCleanup, "MediaInfo.OnNavigatedFrom");
                     }
                     catch (Exception ex)
                     {
                         System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] CleanupAsync Error: {ex.Message}");
                     }
                 }
            }

            // [STRUCTURAL FIX] KILL LAYOUT ENGINE FOR REPEATERS
            // Setting ItemsSource to null synchronously prevents MeasureOverride crashes 
            // when the page is being disassembled during navigation.
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] Cleaning up Repeaters...");
            if (SourcesRepeater != null) { SourcesRepeater.ItemsSource = null; Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] SourcesRepeater cleaned"); }
            if (EpisodesRepeater != null) { EpisodesRepeater.ItemsSource = null; Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] EpisodesRepeater cleaned"); }
            if (CastListView != null) { CastListView.ItemsSource = null; Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] CastListView cleaned"); }
            if (DirectorListView != null) { DirectorListView.ItemsSource = null; Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] DirectorListView cleaned"); }
            if (NarrowCastListView != null) { NarrowCastListView.ItemsSource = null; Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] NarrowCastListView cleaned"); }
            if (NarrowDirectorListView != null) { NarrowDirectorListView.ItemsSource = null; Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] NarrowDirectorListView cleaned"); }
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] Repeater cleanup FINISHED");
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [RACE_PROBE] OnNavigatedFrom COMPLETED");
        }

        private void DetachMediaInfoPlayerFromVisualTree(MpvWinUI.MpvPlayer player)
        {
            if (player == null) return;

            try
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] Detaching MediaInfoPlayer from visual tree");
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

                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] MediaInfoPlayer visual detach completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] !!! MediaInfoPlayer visual detach failed !!!: {ex.Message} (0x{ex.HResult:X8})");
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
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] Background CleanupAsync START | Reason: {reason}");
                    await player.CleanupAsync();
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] Background CleanupAsync END | Reason: {reason}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] Background CleanupAsync FAILED | Reason: {reason}: {ex.Message}");
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
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] [MediaInfoPage] MeasureOverride START | Size: {availableSize.Width}x{availableSize.Height} | NavigatingAway: {_isNavigatingAway}");
            try
            {
                var result = base.MeasureOverride(availableSize);
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] [MediaInfoPage] MeasureOverride END | Result: {result.Width}x{result.Height}");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] !!! [MediaInfoPage] MeasureOverride CRASH !!!: {ex.Message} (0x{ex.HResult:X8})");
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
                    // [Senior] Instant visual feedback: Apply the poster from the previous page immediately.
                    ApplyHeroBackgroundAction(args.PreloadedImage, "navigation-handoff");
                }
                else if (incomingItem != null && !string.IsNullOrEmpty(incomingItem.PosterUrl))
                {
                    // [Senior] Prime the background immediately on the UI thread even if no source object was passed.
                    ApplyHeroBackgroundAction(incomingItem.PosterUrl, "navigation-sync-prime");
                }
                sw.Stop();
                Debug.WriteLine($"[NAV-TIMING] OnNavigatedTo: parameter extraction + hero background: {sw.ElapsedMilliseconds}ms");
            }
            else if (e.Parameter is IMediaStream stream && !string.IsNullOrEmpty(stream.PosterUrl))
            {
                incomingItem = stream;
                // [Senior] Sync prime for direct stream navigation too.
                ApplyHeroBackgroundAction(stream.PosterUrl, "navigation-sync-prime-direct");
            }

            if (incomingItem == null) return;

            var sw2 = Stopwatch.StartNew();
            _item = incomingItem;
            _navigationStartTime = DateTime.Now;

            // Trigger the orchestrated load session. 
            // This service handles visual preparation, reset, fetch, and reveal.
            _ = LoadDetailsAsync(incomingItem, previousItem: previousItem);
            sw2.Stop();
            Debug.WriteLine($"[NAV-TIMING] OnNavigatedTo: LoadDetailsAsync (fire-and-forget): {sw2.ElapsedMilliseconds}ms");
            
            sw2.Restart();
            StartHeroConnectedAnimation();
            SetupParallax();
            sw2.Stop();
            Debug.WriteLine($"[NAV-TIMING] OnNavigatedTo: ConnectedAnimation + Parallax: {sw2.ElapsedMilliseconds}ms");
            
            // Handoff logic (Logic-only/Safe)
            if (App.HandoffPlayer != null)
            {
                MediaInfoPlayer = App.HandoffPlayer;
                App.HandoffPlayer = null;
            }

            navSw.Stop();
            Debug.WriteLine($"[NAV-TIMING] ===== TOTAL OnNavigatedTo: {navSw.ElapsedMilliseconds}ms =====");
        }

        private async Task LoadDetailsAsync(IMediaStream item, TmdbMovieResult preFetchedTmdb = null, IMediaStream previousItem = null, int? loadSession = null, UnifiedMetadata prePeekedMetadata = null)
        {
            TraceMediaInfo("LoadDetailsAsync start", new Dictionary<string, object?> { ["title"] = item?.Title });
            
            if (item == null) return;

            await LoadDetailsInternalAsync(item, prePeekedMetadata, previousItem);

            if (item is Models.Stremio.StremioMediaStream stremioMovie && stremioMovie.Meta?.Type == "movie")
            {
                _ = PlayStremioContent(stremioMovie.Meta.Id, showGlobalLoading: false, autoPlay: false);
            }
        }

        private async Task LoadDetailsInternalAsync(IMediaStream item, UnifiedMetadata prePeekedMetadata, IMediaStream previousItem)
        {
            int session = BeginLoadSession();

            ResetPageState();
            
            _pageLoadState = PageLoadState.Initial;
            SetLoadStateInternal(PageLoadState.Loading);

            await PrepareInfoSkeletonForRevealAsync();

            UnifiedMetadata metadata = null;
            if (prePeekedMetadata != null)
            {
                metadata = prePeekedMetadata;
            }
            else if (item != null)
            {
                try
                {
                    metadata = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(
                        item,
                        Models.Metadata.MetadataContext.Detail,
                        ct: _pageCts?.Token ?? default);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] Metadata fetch failed: {ex.Message}");
                }
            }

            if (metadata != null && Volatile.Read(ref _loadingVersion) == session)
            {
                _unifiedMetadata = metadata;
                await _commitService.CommitAsync(metadata, item);
            }

            if (Volatile.Read(ref _loadingVersion) == session)
            {
                StaggeredRevealContent();
            }
        }

        private void ApplyMetadataToUI(UnifiedMetadata metadata)
        {
            try
            {
                if (metadata == null) return;

                if (YearText != null && !string.IsNullOrEmpty(metadata.Year))
                {
                    YearText.Text = metadata.Year;
                }

                if (OverviewText != null && !string.IsNullOrEmpty(metadata.Overview))
                {
                    OverviewText.Text = metadata.Overview;
                }

                if (GenresText != null && !string.IsNullOrEmpty(metadata.Genres))
                {
                    GenresText.Text = metadata.Genres;
                }

                _ = PopulateCastAndDirectors(metadata);

                if (_item is Models.Stremio.StremioMediaStream stremioStream && stremioStream.Meta?.Type == "series")
                {
                    _ = LoadSeriesDataAsync(metadata);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] ApplyMetadataToUI error: {ex.Message}");
            }
        }

        private async Task PopulateCastAndDirectors(UnifiedMetadata unified)
        {
            try
            {
                var newCast = new List<CastItem>();
                if (unified.Cast != null && unified.Cast.Count > 0)
                {
                    foreach (var c in unified.Cast.Take(10)) 
                    {
                        newCast.Add(new CastItem 
                        { 
                            Name = c.Name, 
                            Character = c.Character, 
                            FullProfileUrl = c.ProfileUrl,
                            ProfileImage = ImageHelper.GetImage(c.ProfileUrl, 80, 100)
                        });
                    }
                }
                else if (unified.TmdbInfo != null && AppSettings.IsTmdbEnabled)
                {
                    var credits = await TmdbHelper.GetCreditsAsync(unified.TmdbInfo.Id, unified.IsSeries);
                    if (credits?.Cast != null)
                    {
                        foreach (var c in credits.Cast.Take(10)) 
                        {
                            newCast.Add(new CastItem 
                            { 
                                Name = c.Name, 
                                Character = c.Character, 
                                FullProfileUrl = c.FullProfileUrl,
                                ProfileImage = ImageHelper.GetImage(c.FullProfileUrl, 80, 100)
                            });
                        }
                    }
                }

                // [FLICKER PREVENTION] Only update if content changed
                bool castChanged = CastList.Count != newCast.Count || 
                                   (CastList.Count > 0 && newCast.Count > 0 && (CastList[0].Name != newCast[0].Name || CastList[0].FullProfileUrl != newCast[0].FullProfileUrl));

                System.Diagnostics.Debug.WriteLine($"[INFO-UI] Population: newCast={newCast.Count}, currentCast={CastList.Count}, changed={castChanged}");

                // [ATOMIC UPDATE] Populate collections once and refresh bindings
                if (castChanged)
                {
                    DispatcherQueue.TryEnqueue(() => 
                    {
                        try
                        {
                            CastList.Clear();
                            foreach (var c in newCast) CastList.Add(c);

                            if (CastListView != null) CastListView.ItemsSource = CastList;
                            if (NarrowCastListView != null) NarrowCastListView.ItemsSource = CastList;
                        }
                        catch (Exception ex)
                        {
                            TraceMediaInfo("Cast population failed", new Dictionary<string, object?> { ["error"] = ex.Message });
                        }
                    });
                }

                var newDirectors = new List<CastItem>();

                // 1. Build Base Directors/Writers from Metadata
                bool hasWritersString = !string.IsNullOrEmpty(unified.Writers);
                if (unified.IsSeries && hasWritersString)
                {
                    var writers = unified.Writers.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var w in writers.Take(3))
                    {
                        newDirectors.Add(new CastItem { Name = w.Trim(), Character = "Yazar", ProfileImage = ImageHelper.GetImage(null, 80, 100) });
                    }
                }

                if (unified.Directors != null && unified.Directors.Count > 0)
                {
                    foreach (var d in unified.Directors.Take(5)) 
                    {
                        var existing = newDirectors.FirstOrDefault(nd => nd.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                        if (existing != null) 
                        { 
                            existing.Character = "Yönetmen / Yazar";
                            existing.FullProfileUrl = d.ProfileUrl;
                            existing.ProfileImage = ImageHelper.GetImage(d.ProfileUrl, 80, 100);
                            continue; 
                        }
                        newDirectors.Add(new CastItem { Name = d.Name, Character = "Yönetmen", FullProfileUrl = d.ProfileUrl, ProfileImage = ImageHelper.GetImage(d.ProfileUrl, 80, 100) });
                    }
                }

                // 2. Fetch TMDB Credits for Images/Extra Info
                if (unified.TmdbInfo != null && AppSettings.IsTmdbEnabled)
                {
                    var credits = await TmdbHelper.GetCreditsAsync(unified.TmdbInfo.Id, unified.IsSeries);
                    if (credits?.Crew != null)
                    {
                        var tmdbDirectors = credits.Crew.Where(c => c.Job == "Director").ToList();
                        foreach (var d in newDirectors)
                        {
                            if (string.IsNullOrEmpty(d.FullProfileUrl))
                            {
                                var match = tmdbDirectors.FirstOrDefault(tc => tc.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                                if (match != null) 
                                {
                                    d.FullProfileUrl = match.FullProfileUrl;
                                    d.ProfileImage = ImageHelper.GetImage(match.FullProfileUrl, 80, 100);
                                }
                            }
                        }
                    }
                }

                // 3. Update Headers
                bool hasDirectors = newDirectors.Any(d => d.Character.Contains("Yönetmen"));
                bool hasWriters = newDirectors.Any(d => d.Character.Contains("Yazar"));
                string headerText = hasDirectors && hasWriters ? "Yönetmen / Yazar" : (hasWriters ? (unified.IsSeries ? "Yaratıcı" : "Yazar") : "Yönetmen");
                
                if (DirectorHeader != null) DirectorHeader.Text = headerText;
                if (NarrowDirectorHeader != null) NarrowDirectorHeader.Text = headerText;

                // 4. Atomic Update
                bool directorChanged = DirectorList.Count != newDirectors.Count || (DirectorList.Count > 0 && newDirectors.Count > 0 && DirectorList[0].Name != newDirectors[0].Name);
                if (directorChanged)
                {
                    DispatcherQueue.TryEnqueue(() => 
                    {
                        try
                        {
                            DirectorList.Clear();
                            foreach (var d in newDirectors) DirectorList.Add(d);
                            
                            if (DirectorListView != null) DirectorListView.ItemsSource = DirectorList;
                            if (NarrowDirectorListView != null) NarrowDirectorListView.ItemsSource = DirectorList;
                        }
                        catch (Exception ex)
                        {
                            TraceMediaInfo("Director population failed", new Dictionary<string, object?> { ["error"] = ex.Message });
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                TraceMediaInfo("PopulateCastAndDirectors outer catch", new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        private void RevealPeopleSectionIfReady(FrameworkElement? section, FrameworkElement? shimmer, int itemCount, ref int revealedGeneration)
        {
            if (section == null || itemCount <= 0 || _pageLoadState != PageLoadState.Ready)
            {
                return;
            }

            if (revealedGeneration == itemCount && section.Opacity >= 0.99)
            {
                return;
            }

            if (section.Opacity >= 0.99 && shimmer?.Visibility != Visibility.Visible)
            {
                revealedGeneration = itemCount;
                return;
            }

            revealedGeneration = itemCount;
            section.Visibility = Visibility.Visible;
            section.Opacity = 1;
            if (shimmer != null) shimmer.Visibility = Visibility.Collapsed;

            CompositionService.Run(section, visual => 
            {
                var compositor = visual.Compositor;
                CompositionService.StopAll(visual);
                
                visual.Opacity = 0f;
                visual.Offset = new Vector3(0, 10, 0);

                var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 0.86f), new Vector2(0.16f, 1f));
                var opacity = compositor.CreateScalarKeyFrameAnimation();
                opacity.InsertKeyFrame(0f, 0f);
                opacity.InsertKeyFrame(1f, 1f, easing);
            opacity.Duration = TimeSpan.FromMilliseconds(360);

            var offset = compositor.CreateVector3KeyFrameAnimation();
            offset.InsertKeyFrame(0f, new Vector3(0, 10, 0));
            offset.InsertKeyFrame(1f, Vector3.Zero, easing);
            offset.Duration = TimeSpan.FromMilliseconds(460);

                visual.StartAnimation(nameof(Visual.Opacity), opacity);
                visual.StartAnimation(nameof(Visual.Offset), offset);
            });
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
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] IMediaInfoUIProxy.SetLoadState({state}) called. IsNavigatingAway: {_isNavigatingAway}");
            SetLoadStateInternal(state);
        }
        void IMediaInfoUIProxy.SyncLayout() => OnViewportChanged();
        void IMediaInfoUIProxy.ApplyOverviewTextLayout(bool isWide) => ApplyOverviewTextLayoutInternal(isWide);
        void IMediaInfoUIProxy.StartPrebuffering(string url, double position) {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] IMediaInfoUIProxy.StartPrebuffering called. IsNavigatingAway: {_isNavigatingAway}");
            StartPrebufferingInternal(url, position);
        }
        void IMediaInfoUIProxy.RefreshAllAddonActiveFlags() => RefreshAllAddonActiveFlagsInternal();
        void IMediaInfoUIProxy.SyncAddonSelectionToActive() => SyncAddonSelectionToActiveInternal();
        void IMediaInfoUIProxy.UpdateWatchlistState(bool? state) => UpdateWatchlistStateInternal(state);
        void IMediaInfoUIProxy.SyncActionButtons(HistoryItem history) => SyncActionButtonsInternal(history);
        void IMediaInfoUIProxy.AddBackdropToSlideshow(string url) => AddBackdropToSlideshowInternal(url);
        void IMediaInfoUIProxy.StartBackgroundSlideshow(List<string> urls) => StartBackgroundSlideshowInternal(urls);
        void IMediaInfoUIProxy.ApplyHeroSeedImage(string url, string reason) => ApplyHeroSeedImageInternal(url, reason);
        void IMediaInfoUIProxy.PlayButton_Click(object sender, RoutedEventArgs e) => PlayButton_ClickInternal(sender, e);
        void IMediaInfoUIProxy.MatchTitleSkeletonToContent() => MatchTitleSkeletonToContentInternal();
        Task IMediaInfoUIProxy.PlayStremioContent(string videoId, bool showGlobalLoading, bool autoPlay, double startSeconds) => PlayStremioContent(videoId, showGlobalLoading, autoPlay, startSeconds);
        Task IMediaInfoUIProxy.PerformHandoverAndNavigate(string url, string title, string id, string parentId, string seriesName, int season, int episode, double startSeconds, string posterUrl, string type, string backdropUrl) => PerformHandoverAndNavigate(url, title, id, parentId, seriesName, season, episode, startSeconds, posterUrl, type, backdropUrl);
        void IMediaInfoUIProxy.OpenEpisodesPanel(PanelChangeReason reason) => OpenEpisodesPanelInternal(reason);
        void IMediaInfoUIProxy.ShowActionFeedback(string title, string subtitle, object target) => ShowActionFeedbackInternal(title, subtitle, target as FrameworkElement);
        string IMediaInfoUIProxy.ResolveBestContentId(string id) => ResolveBestContentId(id);
        string IMediaInfoUIProxy.GetCurrentBackdrop() => GetCurrentBackdrop();
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
        
        async Task IMediaInfoUIProxy.PopulateCastAndDirectors(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata) => await PopulateCastAndDirectorsInternal(metadata);
        async Task IMediaInfoUIProxy.LoadSeriesDataAsync(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata) => await LoadSeriesDataAsyncInternal(metadata);
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
            if (InfoContainer != null)
            {
                InfoContainer.Visibility = Visibility.Visible;
                InfoContainer.Opacity = 1.0;
            }
        }

        // Internal Helpers for Thread-Safe Proxy Implementation
        private void SyncActionButtonsInternal(HistoryItem history)
        {
            if (!this.DispatcherQueue.HasThreadAccess) { this.DispatcherQueue.TryEnqueue(() => SyncActionButtonsInternal(history)); return; }
            _actionService?.SyncActionButtons(_item, _selectedEpisode, history);
        }

        private void SetLoadStateInternal(PageLoadState state)
        {
            if (!this.DispatcherQueue.HasThreadAccess) { this.DispatcherQueue.TryEnqueue(() => SetLoadStateInternal(state)); return; }
            _pageLoadState = state;
            TraceMediaInfo("SetLoadState", new Dictionary<string, object?> { ["state"] = state.ToString() });
        }

        private void OpenEpisodesPanelInternal(PanelChangeReason reason)
        {
            if (!this.DispatcherQueue.HasThreadAccess) { this.DispatcherQueue.TryEnqueue(() => OpenEpisodesPanelInternal(reason)); return; }
            OpenEpisodesPanel(reason);
        }

        private void ShowActionFeedbackInternal(string title, string subtitle, FrameworkElement target = null)
        {
            if (!this.DispatcherQueue.HasThreadAccess) { this.DispatcherQueue.TryEnqueue(() => ShowActionFeedbackInternal(title, subtitle, target)); return; }
            
            // Cancel previous auto-close task if it exists
            _feedbackCts?.Cancel();
            _feedbackCts = new CancellationTokenSource();
            var token = _feedbackCts.Token;

            if (ActionFeedbackTip != null)
            {
                ActionFeedbackTip.Title = title;
                ActionFeedbackTip.Subtitle = subtitle;
                ActionFeedbackTip.Target = target;
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
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] StartPrebufferingInternal called. Thread: {Environment.CurrentManagedThreadId}");
            if (this.DispatcherQueue.HasThreadAccess) StartPrebuffering(url, position); 
            else this.DispatcherQueue.TryEnqueue(() => {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] StartPrebuffering ENQUEUED task starting. IsNavigatingAway: {_isNavigatingAway}");
                StartPrebuffering(url, position);
            }); 
        }
        private void RefreshAllAddonActiveFlagsInternal() { if (this.DispatcherQueue.HasThreadAccess) RefreshAllAddonActiveFlags(); else this.DispatcherQueue.TryEnqueue(() => RefreshAllAddonActiveFlags()); }
        private void SyncAddonSelectionToActiveInternal() { if (this.DispatcherQueue.HasThreadAccess) SyncAddonSelectionToActive(); else this.DispatcherQueue.TryEnqueue(() => SyncAddonSelectionToActive()); }
        private void UpdateWatchlistStateInternal(bool? state) { if (this.DispatcherQueue.HasThreadAccess) UpdateWatchlistState(state ?? false); else this.DispatcherQueue.TryEnqueue(() => UpdateWatchlistState(state ?? false)); }

        private void PlayButton_ClickInternal(object sender, RoutedEventArgs e) { if (this.DispatcherQueue.HasThreadAccess) PlayButton_Click(sender, e); else this.DispatcherQueue.TryEnqueue(() => PlayButton_Click(sender, e)); }
        private void MatchTitleSkeletonToContentInternal() { if (this.DispatcherQueue.HasThreadAccess) MatchTitleSkeletonToContent(); else this.DispatcherQueue.TryEnqueue(() => MatchTitleSkeletonToContent()); }

        private async Task PopulateCastAndDirectorsInternal(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata) 
        { 
            if (this.DispatcherQueue.HasThreadAccess) await PopulateCastAndDirectors(metadata); 
            else { var tcs = new TaskCompletionSource(); this.DispatcherQueue.TryEnqueue(async () => { try { await PopulateCastAndDirectors(metadata); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } }); await tcs.Task; }
        }
        private async Task LoadSeriesDataAsyncInternal(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata) 
        { 
            if (this.DispatcherQueue.HasThreadAccess) await LoadSeriesDataAsync(metadata); 
            else { var tcs = new TaskCompletionSource(); this.DispatcherQueue.TryEnqueue(async () => { try { await LoadSeriesDataAsync(metadata); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } }); await tcs.Task; }
        }
        private async Task UpdateTechnicalBadgesAsyncInternal(string url) 
        { 
            if (this.DispatcherQueue.HasThreadAccess) await UpdateTechnicalBadgesAsync(url); 
            else { var tcs = new TaskCompletionSource(); this.DispatcherQueue.TryEnqueue(async () => { try { await UpdateTechnicalBadgesAsync(url); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } }); await tcs.Task; }
        }
        #endregion

        private void ApplyHeroSeedImage(Microsoft.UI.Xaml.Media.ImageSource source, string reason) => ApplyHeroBackgroundAction(source, reason);
        private void ApplyHeroSeedImage(string imageUrl, string reason) => ApplyHeroBackgroundAction(imageUrl, reason);




        private void ResetPageState()
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] ResetPageState START");
            ResetCollectionsAndBindings();
            ResetActionAndBadgeVisibility();
            ClearMetadataUI();

            // [Senior] Background lifecycle reset
            ResetBackground();


            if (HeroShimmer != null)
            {
                HeroShimmer.Opacity = 0.15;
                HeroShimmer.Visibility = Visibility.Visible;
                
                // [FIX] Use safe CompositionService instead of direct GetElementVisual to avoid 0x80004002 during navigation
                CompositionService.Run(HeroShimmer, visual => {
                    visual.Opacity = 0.15f;
                });
            }
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] ResetPageState END");
        }




        private void ResetCollectionsAndBindings()
        {
            // [STRUCTURAL FIX] Stop Repeaters from listening to collection changes during reset
            if (SourcesRepeater != null) SourcesRepeater.ItemsSource = null;
            if (EpisodesRepeater != null) EpisodesRepeater.ItemsSource = null;

            Seasons?.Clear();
            CurrentEpisodes?.Clear();
            CastList?.Clear();
            DirectorList?.Clear();
            _addonResults?.Clear();

            _currentStremioVideoId = null;
            _unifiedMetadata = null;
            _streamUrl = null;
            _prebufferUrl = null;

            if (CastListView != null) CastListView.ItemsSource = null;
            ClearSourcesPresentationState();
            if (AddonSelectorList != null) AddonSelectorList.ItemsSource = null;
        }

        private void ResetActionAndBadgeVisibility()
        {
            PlayButton.Visibility = Visibility.Collapsed;
            TrailerButton.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Collapsed;
            CopyLinkButton.Visibility = Visibility.Collapsed;

            System.Diagnostics.Debug.WriteLine("[INFO-AMBIENCE] Readability scrims reset to 0 during page state reset.");
            PlayButtonSubtext.Visibility = Visibility.Collapsed;
            StickyPlayButton.Visibility = Visibility.Collapsed;
            StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
            RestartButton.Visibility = Visibility.Collapsed;

            Badge4K.Visibility = Visibility.Collapsed;
            BadgeRes.Visibility = Visibility.Collapsed;
            BadgeHDR.Visibility = Visibility.Collapsed;
            BadgeSDR.Visibility = Visibility.Collapsed;
            BadgeCodecContainer.Visibility = Visibility.Collapsed;
            if (TechBadgesContent != null) TechBadgesContent.Visibility = Visibility.Collapsed;
            if (MetadataRibbon != null) MetadataRibbon.Opacity = 1;
            if (MetadataSeparator != null) MetadataSeparator.Visibility = Visibility.Collapsed;
            if (MetadataShimmer != null) MetadataShimmer.Visibility = Visibility.Collapsed;
            if (TechBadgesShimmer != null) TechBadgesShimmer.Visibility = Visibility.Collapsed;

            if (IdentityControl?.TitleShimmerElement != null) IdentityControl.TitleShimmerElement.Visibility = Visibility.Collapsed;
            if (ActionBarShimmer != null) ActionBarShimmer.Visibility = Visibility.Collapsed;
            if (OverviewShimmer != null) OverviewShimmer.Visibility = Visibility.Collapsed;
            if (MetadataShimmer != null) MetadataShimmer.Visibility = Visibility.Collapsed;
            if (TechBadgesShimmer != null) TechBadgesShimmer.Visibility = Visibility.Collapsed;
            if (CastShimmer != null) CastShimmer.Visibility = Visibility.Collapsed;
            if (DirectorShimmer != null) DirectorShimmer.Visibility = Visibility.Collapsed;
            if (CastSection != null) CastSection.Visibility = Visibility.Collapsed;
            if (DirectorSection != null) DirectorSection.Visibility = Visibility.Collapsed;
        }

        private void ResetLoadingFlags()
        {
            _currentStremioVideoId = null;
            _addonResults?.Clear();
            _isSourcesFetchInProgress = false;
            _isEpisodesLoading = false;
            _isCurrentSourcesComplete = false;

            // Identity & Logo Reset
            _isLogoReady = false;
            _isLogoPending = false;
            _isLogoFallbackActive = false;
            _logoReadyTcs = null; // Essential: Kill the link to the previous item's load state
        }

        private void ClearMetadataUI()
        {
            if (IdentityControl != null)
            {
                IdentityControl.SetTitle("");
                IdentityControl.SetSuperTitle("");
            }
            if (YearText != null) YearText.Text = "";
            if (OverviewText != null) OverviewText.Text = "";
            
            if (!_isResettingPageState) SyncIdentityVisibility(false); // Reset to base state
            
            if (GenresText != null) { GenresText.Text = ""; GenresText.Visibility = Visibility.Collapsed; }
            if (RuntimeText != null) RuntimeText.Text = "";
        }



        private void ShowShimmer(FrameworkElement shimmer)
        {
            if (shimmer == null) return;
            DispatcherQueue.TryEnqueue(() => {
                CompositionService.Run(shimmer, v => v.Opacity = 1f);
                if (shimmer.Visibility != Visibility.Visible) shimmer.Visibility = Visibility.Visible;
            });
        }
        /// <summary>
        /// Prepares the UI for loading a new media item by showing skeletons and hiding content.
        /// </summary>
        private void SetLoadingState(bool isLoading, IMediaStream? item = null, bool skipSync = false)
        {
            if (isLoading)
            {
                System.Diagnostics.Debug.WriteLine($"[INFO-UI] State: LOADING (Item: {item?.Title ?? "Unknown"})");
                
                // Clear state from previous item to avoid "ghost" skeletons
                _streamUrl = null;
                _prebufferUrl = null;
                
                // Reset technical badges
                if (BadgeBitrate != null) BadgeBitrate.Visibility = Visibility.Collapsed;
                if (Badge4K != null) Badge4K.Visibility = Visibility.Collapsed;
                if (BadgeRes != null) BadgeRes.Visibility = Visibility.Collapsed;
                if (BadgeHDR != null) BadgeHDR.Visibility = Visibility.Collapsed;
                if (BadgeSDR != null) BadgeSDR.Visibility = Visibility.Collapsed;
                if (BadgeCodecContainer != null) BadgeCodecContainer.Visibility = Visibility.Collapsed;
                UpdateTechnicalSectionVisibility(false);

                // Transition to Loading State (Skeletons)
                _currentContentStateName = "LoadingState";
                // VisualStateManager.GoToState(this, "LoadingState", false);
                _currentContentStateName = "LoadingState";

                // Avoid showing technical skeletons if there is no URL to probe yet
                if (string.IsNullOrEmpty(_streamUrl) && TechBadgesShimmer != null)
                {
                    TechBadgesShimmer.Visibility = Visibility.Collapsed;
                }

                // [FIX] Avoid showing metadata skeletons if we already have partial metadata (like Year) from the seed
                if (YearText != null && !string.IsNullOrEmpty(YearText.Text))
                {
                    if (MetadataShimmer != null) MetadataShimmer.Visibility = Visibility.Collapsed;
                    if (MetadataPanel != null) MetadataPanel.Opacity = 1;
                }
                
                // Synchronize structural layout (Wide/Narrow)
                if (!skipSync) OnViewportChanged();

                // Trigger panel-specific shimmers
                bool isSeries = IsSeriesItem();
                if (item != null)
                {
                    if (isSeries)
                    {
                        _isEpisodesLoading = true;
                        // Use dynamic placeholders for episodes as well
                        var placeholders = CreateEpisodePlaceholders(5);
                        CurrentEpisodes.Clear();
                        foreach (var p in placeholders) CurrentEpisodes.Add(p);
                    }
                    // Sources shimmer handled via placeholders in the SourcesRepeater via PrepareEarlyMovieSourcesPanel
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[INFO-UI] State: LOAD_COMPLETE");
            }
        }

        #region Content State Management

        /// <summary>
        /// Instantly reveals the content without transitions (used for seamless re-entry).
        /// </summary>
        private void ImmediateRevealContent()
        {
            System.Diagnostics.Debug.WriteLine($"[INFO-UI] State: READY (Immediate)");
            _currentContentStateName = "ReadyState";
            // VisualStateManager.GoToState(this, "ReadyState", false);
            _currentContentStateName = "ReadyState";
            
            if (_pageLoadState != PageLoadState.Ready)
            {
                _pageLoadState = PageLoadState.Ready;
                OnDataCommitted();
            }
            UpdateTechnicalSectionVisibility(HasVisibleBadges());
        }

        /// <summary>
        /// Performs a smooth, staggered reveal sequence from skeletons to content.
        /// </summary>

        private async void StaggeredRevealContent()
        {
            try
            {
                TraceMediaInfo("StaggeredRevealContent enter", new Dictionary<string, object?> { ["state"] = _pageLoadState });
                if (_pageLoadState == PageLoadState.Ready) return;

                if (!DispatcherQueue.HasThreadAccess)
                {
                    DispatcherQueue.TryEnqueue(() => StaggeredRevealContent());
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[INFO-UI] State: REVEALING (Staggered)");
                _pageLoadState = PageLoadState.Revealing;

                _currentContentStateName = "ReadyState";
                _currentContentStateName = "ReadyState";
                
                OnDataCommitted();

                if (InfoContainer != null)
                {
                    InfoContainer.Visibility = Visibility.Visible;
                    InfoContainer.Opacity = 1;
                }

                if (RootScrollViewer != null)
                {
                    RootScrollViewer.Visibility = Visibility.Visible;
                    RootScrollViewer.Opacity = 1;
                }

                RevealAllContentPanels();

                await Task.Delay(200);

                CollapseAllShimmers();

                await Task.Delay(100);

                _pageLoadState = PageLoadState.Ready;
                CollapseEmptyPeopleSkeletons();
                FlushDeferredPanelRequest();
                OnDataCommitted();
                RevealReadyPeopleSections();
                _currentContentStateName = "ReadyState";
                System.Diagnostics.Debug.WriteLine($"[INFO-UI] State: READY");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[INFO-UI] Reveal fallback: {ex.Message}");
                _pageLoadState = PageLoadState.Ready;
                _currentContentStateName = "ReadyState";
                _currentContentStateName = "ReadyState";
                CollapseEmptyPeopleSkeletons();
                CollapseAllShimmers();
                FlushDeferredPanelRequest();
                OnDataCommitted();
                RevealReadyPeopleSections();
            }
        }

        private void CollapseAllShimmers()
        {
            if (TechBadgesShimmer != null) TechBadgesShimmer.Visibility = Visibility.Collapsed;
            if (MetadataShimmer != null) MetadataShimmer.Visibility = Visibility.Collapsed;
            if (ActionBarShimmer != null) ActionBarShimmer.Visibility = Visibility.Collapsed;
            if (OverviewShimmer != null) OverviewShimmer.Visibility = Visibility.Collapsed;
            if (CastShimmer != null) CastShimmer.Visibility = Visibility.Collapsed;
            if (DirectorShimmer != null) DirectorShimmer.Visibility = Visibility.Collapsed;
            if (HeroShimmer != null) HeroShimmer.Visibility = Visibility.Collapsed;
        }

        private void RevealAllContentPanels()
        {
            if (TechBadgesContent != null) TechBadgesContent.Visibility = Visibility.Visible;
            if (MetadataPanel != null) MetadataPanel.Visibility = Visibility.Visible;
            if (ActionBarPanel != null) ActionBarPanel.Visibility = Visibility.Visible;
            if (OverviewPanel != null) OverviewPanel.Visibility = Visibility.Visible;
            if (GenresText != null && !string.IsNullOrEmpty(GenresText.Text)) GenresText.Visibility = Visibility.Visible;
            if (OverviewText != null && !string.IsNullOrEmpty(OverviewText.Text)) OverviewText.Visibility = Visibility.Visible;
            if (CastSection != null && CastList?.Count > 0) CastSection.Visibility = Visibility.Visible;
            if (DirectorSection != null && DirectorList?.Count > 0) DirectorSection.Visibility = Visibility.Visible;
            if (IdentityControl != null) IdentityControl.Visibility = Visibility.Visible;

            System.Diagnostics.Debug.WriteLine($"[INFO-UI] RevealAllContentPanels: Metadata={MetadataPanel?.Visibility}, Overview={OverviewPanel?.Visibility}, Identity={IdentityControl?.Visibility}, GenresText='{GenresText?.Text}', OverviewText='{OverviewText?.Text}'");
        }

        private void CollapseEmptyPeopleSkeletons()
        {
            if (CastList?.Count == 0 && CastShimmer != null)
            {
                CastShimmer.Visibility = Visibility.Collapsed;
                AdjustCastShimmer(0);
            }

            if (DirectorList?.Count == 0 && DirectorShimmer != null)
            {
                DirectorShimmer.Visibility = Visibility.Collapsed;
                AdjustDirectorShimmer(0);
            }
        }

        private void RevealReadyPeopleSections()
        {
            TraceMediaInfo("RevealReadyPeopleSections enter", new Dictionary<string, object?>
            {
                ["disabled"] = DisableReadyPeopleRevealForCrashIsolation,
                ["castCount"] = CastList?.Count ?? 0,
                ["directorCount"] = DirectorList?.Count ?? 0
            });

            if (DisableReadyPeopleRevealForCrashIsolation)
            {
                TraceMediaInfo("RevealReadyPeopleSections exit isolation");
                return;
            }

            if (CastList?.Count > 0)
            {
                RevealPeopleSectionIfReady(CastSection, CastShimmer, CastList.Count, ref _revealedCastGeneration);
            }

            if (DirectorList?.Count > 0)
            {
                RevealPeopleSectionIfReady(DirectorSection, DirectorShimmer, DirectorList.Count, ref _revealedDirectorGeneration);
            }

            TraceMediaInfo("RevealReadyPeopleSections exit");
        }

        private async Task PrepareInfoSkeletonForRevealAsync()
        {
            if (!this.IsLoaded || _pageCts?.IsCancellationRequested == true) return;

            OnViewportChanged();
            if (!TryUpdateLayout(this, nameof(PrepareInfoSkeletonForRevealAsync))) return;
            
            // Deterministic check: If ActualWidth is still 0, WinUI hasn't performed the layout pass yet.
            if (this.ActualWidth <= 0)
            {
                await Task.Yield();
                if (!this.IsLoaded || _pageCts?.IsCancellationRequested == true) return;
                TryUpdateLayout(this, nameof(PrepareInfoSkeletonForRevealAsync));
            }

            MatchTitleSkeletonToContent();
            MatchSkeletonToContent(TechBadgesShimmer, TechBadgesContent, minWidth: 0, minHeight: 22, collapseWhenContentHidden: true);
            MatchSkeletonToContent(MetadataShimmer, MetadataPanel, minWidth: 108, minHeight: 22);
            RebuildActionBarSkeletonFromButtons();
            RebuildOverviewSkeletonFromText();
            
            if (_pageCts?.IsCancellationRequested == true) return;

            if (CastSection != null && CastShimmer != null)
            {
                AdjustCastShimmer(CastList.Count);
                MatchSkeletonToContent(CastShimmer, CastSection, minWidth: 180, minHeight: 145);
                CastShimmer.Opacity = 1;
                CastShimmer.Visibility = Visibility.Visible;
            }
            else if (CastShimmer != null)
            {
                CastShimmer.Visibility = Visibility.Collapsed;
            }

            if (DirectorSection != null && DirectorShimmer != null)
            {
                AdjustDirectorShimmer(DirectorList.Count);
                MatchSkeletonToContent(DirectorShimmer, DirectorSection, minWidth: 180, minHeight: 145);
                DirectorShimmer.Opacity = 1;
                DirectorShimmer.Visibility = Visibility.Visible;
            }
            else if (DirectorShimmer != null)
            {
                DirectorShimmer.Visibility = Visibility.Collapsed;
            }
        }



        private static bool TryUpdateLayout(FrameworkElement element, string caller)
        {
            if (element == null || !element.IsLoaded) return false;
            try
            {
                element.UpdateLayout();
                return true;
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80004002)
            {
                System.Diagnostics.Debug.WriteLine($"[INFO-UI] UpdateLayout skipped in {caller}: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[INFO-UI] UpdateLayout error in {caller}: {ex.Message}");
                return false;
            }
        }

        #endregion

        private void ShowInitialPeopleSkeletons()
        {
            bool isWide = ActualWidth >= LayoutAdaptiveThreshold;
            if (!isWide) return;

            // [ATOMIC READINESS] Only show skeletons if we have data to hide or if we're in a fresh loading state.
            bool isLoading = _pageLoadState == PageLoadState.Loading;
            if (CastShimmer != null && (CastList?.Count > 0 || isLoading))
            {
                AdjustCastShimmer(CastList?.Count ?? 0);
                CastShimmer.Opacity = 1;
                CastShimmer.Visibility = Visibility.Visible;
            }
            else if (CastShimmer != null)
            {
                CastShimmer.Visibility = Visibility.Collapsed;
            }

            if (DirectorShimmer != null && (DirectorList?.Count > 0 || isLoading))
            {
                AdjustDirectorShimmer(DirectorList?.Count ?? 0);
                DirectorShimmer.Opacity = 1;
                DirectorShimmer.Visibility = Visibility.Visible;
            }
            else if (DirectorShimmer != null)
            {
                DirectorShimmer.Visibility = Visibility.Collapsed;
            }
        }

        private void RebuildActionBarSkeletonFromButtons()
        {
            if (ActionBarShimmer == null || ActionBarPanel == null) return;

            ActionBarShimmer.Children.Clear();
            ActionBarShimmer.Spacing = ActionBarPanel.Spacing;
            ActionBarShimmer.HorizontalAlignment = ActionBarPanel.HorizontalAlignment;
            ActionBarShimmer.VerticalAlignment = ActionBarPanel.VerticalAlignment;
            ActionBarShimmer.Margin = ActionBarPanel.Margin;

            var buttons = new[] { PlayButton, RestartButton, TrailerButton, DownloadButton, CopyLinkButton, WatchlistButton }
                .Where(b => b != null && b.Visibility == Visibility.Visible)
                .ToList();

            if (buttons.Count == 0 && PlayButton != null)
            {
                buttons.Add(PlayButton);
            }

            foreach (var button in buttons)
            {
                double width = button.ActualWidth > 1 ? button.ActualWidth : button.Width;
                double height = button.ActualHeight > 1 ? button.ActualHeight : button.Height;

                if (double.IsNaN(width) || width <= 1)
                {
                    bool isPrimary = button == PlayButton || button == RestartButton;
                    width = isPrimary ? 142 : 52;
                }

                if (double.IsNaN(height) || height <= 1)
                {
                    height = 52;
                }

                var radius = button.CornerRadius.TopLeft > 0
                    ? button.CornerRadius
                    : new CornerRadius(height / 2);

                ActionBarShimmer.Children.Add(new ShimmerControl
                {
                    Width = Math.Ceiling(width),
                    Height = Math.Ceiling(height),
                    CornerRadius = radius,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            ActionBarShimmer.Width = ActionBarPanel.ActualWidth > 1
                ? Math.Ceiling(ActionBarPanel.ActualWidth)
                : double.NaN;
            ActionBarShimmer.Height = ActionBarPanel.ActualHeight > 1
                ? Math.Ceiling(ActionBarPanel.ActualHeight)
                : (PlayButton?.ActualHeight > 1 ? Math.Ceiling(PlayButton.ActualHeight) : 52);
            ActionBarShimmer.Opacity = 1;
            ActionBarShimmer.Visibility = Visibility.Visible;
        }

        private void RebuildOverviewSkeletonFromText()
        {
            if (OverviewShimmer == null || OverviewPanel == null || OverviewText == null) return;

            OverviewShimmer.Children.Clear();
            OverviewShimmer.HorizontalAlignment = OverviewPanel.HorizontalAlignment;
            OverviewShimmer.VerticalAlignment = OverviewPanel.VerticalAlignment;
            OverviewShimmer.Margin = new Thickness(0, 4, 0, 0);

            double panelWidth = OverviewPanel.ActualWidth > 1 ? OverviewPanel.ActualWidth : InfoColumn?.ActualWidth ?? 0;
            if (panelWidth <= 1)
            {
                panelWidth = ActualWidth >= LayoutAdaptiveThreshold ? 620 : Math.Max(280, ActualWidth - 40);
            }

            double lineHeight = OverviewText.LineHeight > 0 ? OverviewText.LineHeight : OverviewText.FontSize * 1.45;
            double textHeight = OverviewText.ActualHeight;
            if (textHeight <= 1)
            {
                OverviewText.Measure(new Windows.Foundation.Size(panelWidth, double.PositiveInfinity));
                textHeight = OverviewText.DesiredSize.Height;
            }

            int textLineCount = Math.Max(1, (int)Math.Ceiling(textHeight / Math.Max(1, lineHeight)));
            if (OverviewText.MaxLines > 0)
            {
                textLineCount = Math.Min(textLineCount, OverviewText.MaxLines);
            }

            double genreWidth = 0;
            if (GenresText != null && GenresText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(GenresText.Text))
            {
                genreWidth = GenresText.ActualWidth > 1 ? GenresText.ActualWidth : Math.Min(panelWidth * 0.55, 280);
                OverviewShimmer.Children.Add(new ShimmerControl
                {
                    Width = Math.Max(96, Math.Ceiling(genreWidth)),
                    Height = Math.Max(14, Math.Ceiling(GenresText.ActualHeight > 1 ? GenresText.ActualHeight : GenresText.FontSize + 4)),
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            }

            for (int i = 0; i < textLineCount; i++)
            {
                bool isLast = i == textLineCount - 1;
                double widthFactor = isLast ? 0.68 : (i % 3 == 1 ? 0.94 : 1.0);
                OverviewShimmer.Children.Add(new ShimmerControl
                {
                    Width = Math.Max(120, Math.Ceiling(panelWidth * widthFactor)),
                    Height = Math.Max(12, Math.Ceiling(lineHeight * 0.58)),
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            }

            OverviewShimmer.Width = Math.Ceiling(panelWidth);
            OverviewShimmer.Height = double.NaN;
            OverviewShimmer.Opacity = 1;
            OverviewShimmer.Visibility = Visibility.Visible;
        }

        private void RebuildDefaultOverviewSkeleton()
        {
            if (OverviewShimmer == null) return;

            bool isWide = ActualWidth >= LayoutAdaptiveThreshold;
            double width = InfoColumn?.ActualWidth > 1
                ? InfoColumn.ActualWidth
                : (isWide ? 620 : Math.Max(280, ActualWidth - 40));
            double lineHeight = isWide ? 14 : 13;
            int lines = isWide ? 4 : 5;

            OverviewShimmer.Children.Clear();
            for (int i = 0; i < lines; i++)
            {
                bool isLast = i == lines - 1;
                OverviewShimmer.Children.Add(new ShimmerControl
                {
                    Width = Math.Max(120, Math.Ceiling(width * (isLast ? 0.68 : (i % 2 == 0 ? 1.0 : 0.92)))),
                    Height = lineHeight,
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            }

            OverviewShimmer.Width = Math.Ceiling(width);
            OverviewShimmer.Height = double.NaN;
            OverviewShimmer.Opacity = 1;
            OverviewShimmer.Visibility = Visibility.Visible;
        }

        public void MatchTitleSkeletonToContent()
        {
            if (IdentityControl == null) return;
            var titleShimmer = IdentityControl.TitleShimmerElement;
            var titlePanel = IdentityControl.TitlePanelElement;
            var logoHost = IdentityControl.LogoHost;

            if (titleShimmer == null) return;

            bool hasLogoSlot = !string.IsNullOrWhiteSpace(_currentLogoUrl) && logoHost != null;
            if (hasLogoSlot)
            {
                double logoWidth = logoHost.ActualWidth > 1 ? logoHost.ActualWidth : logoHost.Width;
                double logoHeight = logoHost.ActualHeight > 1 ? logoHost.ActualHeight : logoHost.Height;
                titleShimmer.Width = Math.Max(220, Math.Ceiling(logoWidth));
                titleShimmer.Height = Math.Max(72, Math.Ceiling(logoHeight));
                titleShimmer.HorizontalAlignment = logoHost.HorizontalAlignment;
                titleShimmer.VerticalAlignment = logoHost.VerticalAlignment;
                titleShimmer.Opacity = 1;
                titleShimmer.Visibility = Visibility.Visible;
                return;
            }

            MatchSkeletonToContent(titleShimmer, titlePanel, minWidth: 260, minHeight: 56);
        }

        private static void MatchSkeletonToContent(
            FrameworkElement skeleton,
            FrameworkElement content,
            double minWidth,
            double minHeight,
            bool collapseWhenContentHidden = false)
        {
            if (skeleton == null || content == null) return;
            if (collapseWhenContentHidden && content.Visibility != Visibility.Visible)
            {
                skeleton.Visibility = Visibility.Collapsed;
                return;
            }

            double width = content.ActualWidth;
            double height = content.ActualHeight;

            if (width <= 1 && content is TextBlock tb)
            {
                width = tb.DesiredSize.Width;
                height = tb.DesiredSize.Height;
            }

            if (width > 1)
            {
                skeleton.Width = Math.Max(minWidth, Math.Ceiling(width));
            }

            if (height > 1)
            {
                skeleton.Height = Math.Max(minHeight, Math.Ceiling(height));
            }

            skeleton.HorizontalAlignment = content.HorizontalAlignment;
            skeleton.VerticalAlignment = content.VerticalAlignment;
            skeleton.Opacity = 1;
            skeleton.Visibility = Visibility.Visible;
        }


        internal void IdentityControl_LogoLoadCompleted(object sender, bool success)
        {
            _logoReadyTcs?.TrySetResult(success);
            System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] Logo load completed. Success: {success}");
        }
        


        private void ApplyTextShadow(TextBlock textBlock)
        {
            // Shadow removed: ThemeShadow on TextBlocks produces a visible elevated-surface
            // "box" effect in WinUI 3, not per-glyph shadows. Text readability is instead
            // handled by the LocalInfoGradient scrim rectangle in XAML.
        }


        private void AdjustCastShimmer(int count)
        {
            if (CastShimmer == null) return;
            
            int effectiveCount = count <= 0 ? 5 : count;
            int displayCount = Math.Min(effectiveCount, 8); 

            if (_lastAdjustedCastCount == displayCount) return;
            _lastAdjustedCastCount = displayCount;

            DispatcherQueue.TryEnqueue(() => 
            {
                if (CastShimmer.Children.Count >= 2 && CastShimmer.Children[1] is StackPanel horizontalPanel)
                {
                    horizontalPanel.Children.Clear();
                    for (int i = 0; i < displayCount; i++)
                    {
                        var itemStack = new StackPanel { Spacing = 6 };
                        itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 100, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left });
                        itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left });
                        horizontalPanel.Children.Add(itemStack);
                    }
                }
            });
        }

        private void AdjustDirectorShimmer(int count)
        {
            if (DirectorShimmer == null) return;
            
            int effectiveCount = count <= 0 ? 2 : count;
            int displayCount = Math.Min(effectiveCount, 4); 

            if (_lastAdjustedDirectorCount == displayCount) return;
            _lastAdjustedDirectorCount = displayCount;

            DispatcherQueue.TryEnqueue(() => 
            {
                if (DirectorShimmer.Children.Count >= 2 && DirectorShimmer.Children[1] is StackPanel horizontalPanel)
                {
                    horizontalPanel.Children.Clear();
                    for (int i = 0; i < displayCount; i++)
                    {
                        var itemStack = new StackPanel { Spacing = 6 };
                        itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 100, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left });
                        itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left });
                        horizontalPanel.Children.Add(itemStack);
                    }
                }
            });
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
                    // [FIX] Remove hardcoded 1.0 gradient fade. Let ExtractAndApplyAmbienceAsync handle it 
                    // based on background content to avoid the "gradient disappearance" pop.
                    // (Visuals are initialized in SetupStabilityComposition)

                    System.Diagnostics.Debug.WriteLine("[INFO-PAGE] Starting KenBurns effect (using slide transition).");
                    StartKenBurnsEffect();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] Animation ERROR: {ex.Message}");
                    StartKenBurnsEffect();
                }
            });
        }

        private void StartKenBurnsEffect(UIElement target = null)
        {
            if (_compositor == null) return;
            var element = target ?? HeroImage;
            if (element == null) return;

            // [FIX] Prevent reset if already animating the same element for the same item
            if (element == _lastKenBurnsElement && _slideshowId != null)
            {
                return;
            }
            _lastKenBurnsElement = element;

            var visual = ElementCompositionPreview.GetElementVisual(element);
            
            // Note: We don't have a direct "IsAnimationRunning" here, 
            // but since we call this mostly from slideshow transitions, 
            // the reset usually occurs when the whole slideshow restarts.
            // By fixing StartBackgroundSlideshow's early return, we prevent the reset.

            // element.Opacity = 1; // [REM] Removed to prevent flashes during crossfade. Caller handles visibility.
            
            // CenterPoint handled by EnsureHeroVisuals now, but safe to set initial
            if (element is FrameworkElement fe)
            {
                // [FIX] Use ExpressionAnimation for CenterPoint to avoid C# re-entrancy during KenBurns transitions
                var centerExpr = _compositor.CreateExpressionAnimation("Vector3(this.Target.Size.X * 0.5f, this.Target.Size.Y * 0.5f, 0)");
                visual.StartAnimation("CenterPoint", centerExpr);
            }

            var scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(0f, new System.Numerics.Vector3(1.0f, 1.0f, 1.0f));
            scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1.08f, 1.08f, 1.0f));
            scaleAnim.Duration = TimeSpan.FromSeconds(25);
            scaleAnim.IterationBehavior = AnimationIterationBehavior.Forever;
            scaleAnim.Direction = AnimationDirection.Alternate;
            
            visual.StartAnimation("Scale", scaleAnim);
        }

        private void StopKenBurnsEffect(UIElement target = null)
        {
            try
            {
                var element = target ?? HeroImage;
                if (element == null) return;

                var visual = ElementCompositionPreview.GetElementVisual(element);
                visual.StopAnimation("Scale");
                visual.Scale = new System.Numerics.Vector3(1.0f, 1.0f, 1.0f);
            }
            catch { }
        }










        private void UpdateIconColor(IconElement icon, Color color, double durationSeconds = 2.0)
        {
            if (icon == null) return;
            
            // WinUI 3'te XAML'den gelen fırçalar frozen (değiştirilemez) olabilir
            // Bu fırçaları animasyona sokmaya çalışmak "Unknown" color hatasına neden olur
            // Her zaman yeni mutable bir fırça oluşturuyoruz
            var newBrush = new SolidColorBrush(color);
            icon.Foreground = newBrush;

            if (durationSeconds <= 0.01)
            {
                newBrush.Color = color;
                return;
            }

            AnimateBrushColor(newBrush, color, durationSeconds);
        }

        private void UpdateTextColor(TextBlock textBlock, Color color, double durationSeconds = 2.0)
        {
            if (textBlock == null) return;
            
            // WinUI 3'te XAML'den gelen fırçalar frozen (değiştirilemez) olabilir
            // Bu fırçaları animasyona sokmaya çalışmak "Unknown" color hatasına neden olur
            // Her zaman yeni mutable bir fırça oluşturuyoruz
            var newBrush = new SolidColorBrush(color);
            textBlock.Foreground = newBrush;

            if (durationSeconds <= 0.01)
            {
                newBrush.Color = color;
                return;
            }

            AnimateBrushColor(newBrush, color, durationSeconds);
        }

        private void AnimateOpacity(UIElement element, double opacity, double durationSeconds = 2.0)
        {
            if (element == null) return;

            if (opacity <= 0.01)
            {
                element.Opacity = 0;
                var visual = ElementCompositionPreview.GetElementVisual(element);
                visual?.StopAnimation("Opacity");
                if (visual != null) visual.Opacity = 0f;
                return;
            }
            
            var animation = new DoubleAnimation
            {
                To = opacity,
                Duration = TimeSpan.FromSeconds(durationSeconds),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, "Opacity");

            var sb = new Storyboard();
            sb.Children.Add(animation);
            sb.Begin();
        }

        private void AnimateButtonWidth(Button? button, double targetWidth, double durationMs = 250)
        {
            if (button == null) return;

            // Use a smooth DoubleAnimation for the Width property
            var animation = new DoubleAnimation
            {
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop // Crucial: Don't lock the property
            };

            var storyboard = new Storyboard();
            Storyboard.SetTarget(animation, button);
            Storyboard.SetTargetProperty(animation, "Width");
            
            // Set the final value explicitly when the animation ends to ensure stability
            storyboard.Completed += (s, e) => { button.Width = targetWidth; };
            storyboard.Begin();
        }

        private void AnimateReadabilityGradientOpacity(UIElement element, string label, double targetOpacity, double durationMs = 650.0)
        {
            if (element == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(element);
            var currentOpacity = element.Opacity;
            var normalizedTarget = Math.Clamp(targetOpacity, 0.0, 1.0);


            if (Math.Abs(currentOpacity - normalizedTarget) < 0.01)
            {
                if (element.Opacity != normalizedTarget) element.Opacity = normalizedTarget;
                if (visual != null && visual.Opacity != (float)normalizedTarget) visual.Opacity = (float)normalizedTarget;
                return;
            }

            visual?.StopAnimation("Opacity");
            if (visual != null) visual.Opacity = (float)currentOpacity;

            var animation = new DoubleAnimation
            {
                From = currentOpacity,
                To = normalizedTarget,
                Duration = TimeSpan.FromMilliseconds(Math.Max(1.0, durationMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, "Opacity");

            var sb = new Storyboard();
            sb.Children.Add(animation);
            sb.Begin();

            System.Diagnostics.Debug.WriteLine($"[INFO-AMBIENCE][{label}] animating {currentOpacity:F2} -> {normalizedTarget:F2} over {Math.Max(1.0, durationMs):F0}ms.");
        }

        private void AnimateBrushColor(Control control, Color targetColor, double durationSeconds = 2.0)
        {
            if (control == null) return;
            
            // Ensure we have a mutable SolidColorBrush assigned
            if (!(control.Background is SolidColorBrush scb) || IsBrushSealed(control.Background))
            {
                Color startColor = (control.Background is SolidColorBrush oldScb) ? oldScb.Color : Microsoft.UI.Colors.Transparent;
                control.Background = new SolidColorBrush(startColor);
            }

            if (durationSeconds <= 0.01)
            {
                if (control.Background is SolidColorBrush directBrush)
                {
                    directBrush.Color = targetColor;
                }
                else
                {
                    control.Background = new SolidColorBrush(targetColor);
                }
                return;
            }

            StartColorAnimation(control, "(Control.Background).(SolidColorBrush.Color)", targetColor, durationSeconds);
        }

        private void AnimateBrushColor(Panel panel, Color targetColor, double durationSeconds = 2.0)
        {
            if (panel == null) return;
            
            // Ensure we have a mutable SolidColorBrush assigned
            if (!(panel.Background is SolidColorBrush scb) || IsBrushSealed(panel.Background))
            {
                Color startColor = (panel.Background is SolidColorBrush oldScb) ? oldScb.Color : Microsoft.UI.Colors.Transparent;
                panel.Background = new SolidColorBrush(startColor);
            }

            if (durationSeconds <= 0.01)
            {
                if (panel.Background is SolidColorBrush directBrush)
                {
                    directBrush.Color = targetColor;
                }
                else
                {
                    panel.Background = new SolidColorBrush(targetColor);
                }
                return;
            }

            StartColorAnimation(panel, "(Panel.Background).(SolidColorBrush.Color)", targetColor, durationSeconds);
        }

        private void AnimateBrushColor(Border border, Color targetColor, double durationSeconds = 2.0)
        {
            if (border == null) return;
            
            // Ensure we have a mutable SolidColorBrush assigned
            if (!(border.Background is SolidColorBrush scb) || IsBrushSealed(border.Background))
            {
                Color startColor = (border.Background is SolidColorBrush oldScb) ? oldScb.Color : Microsoft.UI.Colors.Transparent;
                border.Background = new SolidColorBrush(startColor);
            }

            if (durationSeconds <= 0.01)
            {
                if (border.Background is SolidColorBrush directBrush)
                {
                    directBrush.Color = targetColor;
                }
                else
                {
                    border.Background = new SolidColorBrush(targetColor);
                }
                return;
            }

            StartColorAnimation(border, "(Border.Background).(SolidColorBrush.Color)", targetColor, durationSeconds);
        }

        private bool IsBrushSealed(Brush brush)
        {
            if (brush is SolidColorBrush scb)
            {
                try { scb.Color = scb.Color; return false; }
                catch { return true; }
            }
            return true;
        }

        private void StartColorAnimation(DependencyObject target, string propertyPath, Color targetColor, double durationSeconds)
        {
            try
            {
                if (durationSeconds <= 0.01)
                {
                    if (target is Control directControl)
                    {
                        directControl.Background = new SolidColorBrush(targetColor);
                    }
                    else if (target is Panel directPanel)
                    {
                        directPanel.Background = new SolidColorBrush(targetColor);
                    }
                    else if (target is Border directBorder)
                    {
                        directBorder.Background = new SolidColorBrush(targetColor);
                    }
                    return;
                }

                Color fromColor = Microsoft.UI.Colors.Transparent;
                if (target is Control animControl && animControl.Background is SolidColorBrush controlBrush) fromColor = controlBrush.Color;
                else if (target is Panel animPanel && animPanel.Background is SolidColorBrush panelBrush) fromColor = panelBrush.Color;
                else if (target is Border animBorder && animBorder.Background is SolidColorBrush borderBrush) fromColor = borderBrush.Color;
                
                if (fromColor == targetColor) return;

                var animation = new ColorAnimation
                {
                    From = fromColor,
                    To = targetColor,
                    Duration = TimeSpan.FromSeconds(durationSeconds),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                Storyboard.SetTarget(animation, target);
                Storyboard.SetTargetProperty(animation, propertyPath);

                var sb = new Storyboard();
                sb.Children.Add(animation);
                sb.Begin();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AnimateBrushColor] Animation failed: {ex.Message}");
                // Fallback
                if (target is Control c) c.Background = new SolidColorBrush(targetColor);
                else if (target is Panel p) p.Background = new SolidColorBrush(targetColor);
                else if (target is Border b) b.Background = new SolidColorBrush(targetColor);
            }
        }

        private void AnimateBrushColor(SolidColorBrush brush, Color targetColor, double durationSeconds = 2.0)
        {
            if (brush == null || brush.Color == targetColor) return;

            if (durationSeconds <= 0.01)
            {
                brush.Color = targetColor;
                return;
            }
            
            try
            {
                var animation = new ColorAnimation
                {
                    From = brush.Color,
                    To = targetColor,
                    Duration = TimeSpan.FromSeconds(durationSeconds),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                Storyboard.SetTarget(animation, brush);
                Storyboard.SetTargetProperty(animation, "Color");

                var sb = new Storyboard();
                sb.Children.Add(animation);
                sb.Begin();
            }
            catch { brush.Color = targetColor; }
        }


        private async Task LoadSeriesDataAsync(UnifiedMetadata unified)
        {
            try
            {
                // [STABILITY] Removed Force Stretch override to respect XAML/LayoutState Top alignment
                // if (EpisodesPanel != null) EpisodesPanel.VerticalAlignment = VerticalAlignment.Stretch;
                // if (SourcesPanel != null) SourcesPanel.VerticalAlignment = VerticalAlignment.Stretch;

                System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] STEP 1: LoadSeriesDataAsync ENTER for: {unified.Title}. Seasons: {(unified.Seasons?.Count ?? 0)}");
                
                // [TRACE] Log current panel states before population
                System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] Pre-Load: EpisodesPanel.VA={EpisodesPanel?.VerticalAlignment}, Visible={EpisodesPanel?.Visibility}");
                
                // [FLICKER PREVENTION] If we already have content (from Cache-First), don't show shimmer again
                if (Seasons.Count == 0)
                {
                    _isEpisodesLoading = true;
                    RefreshAllShimmers();
                }
                
                if (unified.Seasons == null || unified.Seasons.Count == 0)
                {
                    _isEpisodesLoading = false;
                    RefreshAllShimmers();
                    return;
                }

                var newSeasons = new List<SeasonItem>();
                foreach (var s in unified.Seasons)
                {
                    var epList = new List<EpisodeItem>();
                    foreach (var e in s.Episodes)
                    {
                        var epItem = new EpisodeItem
                        {
                            Id = e.Id,
                            SeasonNumber = e.SeasonNumber,
                            EpisodeNumber = e.EpisodeNumber,
                            Title = e.Title,
                            Name = e.Title,
                            Overview = e.Overview,
                            ImageUrl = !string.IsNullOrEmpty(e.ThumbnailUrl) ? e.ThumbnailUrl : (unified.PosterUrl ?? ""),
                            Thumbnail = ImageHelper.GetImage(!string.IsNullOrEmpty(e.ThumbnailUrl) ? e.ThumbnailUrl : (unified.PosterUrl ?? ""), 150, 80),
                            ReleaseDate = e.AirDate,
                            IsReleased = e.AirDate.HasValue ? e.AirDate.Value <= DateTime.Now : true,
                            StreamUrl = e.StreamUrl,
                            Resolution = e.Resolution,
                            VideoCodec = e.VideoCodec,
                            Bitrate = e.Bitrate,
                            IsHdr = e.IsHdr,
                            IptvSeriesId = e.IptvSeriesId,
                            IptvSourceTitle = e.IptvSourceTitle
                        };
                        epItem.RefreshHistoryState();
                        epList.Add(epItem);
                    }

                    if (epList.Count > 0)
                    {
                        var seasonItem = new SeasonItem
                        {
                            SeasonNumber = s.SeasonNumber,
                            Name = s.Name ?? (s.SeasonNumber == 0 ? "Özel Bölümler" : $"{s.SeasonNumber}. Sezon"),
                            SeasonName = s.Name ?? (s.SeasonNumber == 0 ? "Özel Bölümler" : $"{s.SeasonNumber}. Sezon"),
                            Episodes = epList,
                            IsEnrichedByTmdb = s.IsEnrichedByTmdb
                        };
                        newSeasons.Add(seasonItem);
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] STEP 1.2: Population finished. Seasons.Count={newSeasons.Count}");

                // [FLICKER PREVENTION] Compare with existing to avoid full clear/reset
                // Check if counts changed OR if primary season's first episode title changed (e.g. generic -> real)
                bool contentChanged = Seasons.Count > 0 && newSeasons.Count > 0 && Seasons[0].Episodes.Count > 0 && newSeasons[0].Episodes.Count > 0 && 
                                     Seasons[0].Episodes[0].Title != newSeasons[0].Episodes[0].Title;

                bool seriesChanged = Seasons.Count != newSeasons.Count || contentChanged ||
                                    (Seasons.Count > 0 && Seasons[0].Episodes.Count != newSeasons[0].Episodes.Count);

                if (seriesChanged)
                {
                    System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] STEP 1.1: Clearing Seasons/Episodes. VA={EpisodesPanel?.VerticalAlignment}");
                    Seasons.Clear();
                    CurrentEpisodes.Clear();
                    foreach(var s in newSeasons) Seasons.Add(s);
                }

                int targetSeasonIndex = 0;
                System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] STEP 1.2: Population starting. Seasons.Count={Seasons.Count}");
                EpisodeItem episodeToSelect = null;
                var lastWatched = HistoryManager.Instance.GetLastWatchedEpisode(unified.MetadataId);
                
                if (lastWatched != null)
                {
                    if (lastWatched.IsFinished)
                    {
                        var nextEp = unified.Seasons
                            .SelectMany(s => s.Episodes)
                            .OrderBy(e => e.SeasonNumber)
                            .ThenBy(e => e.EpisodeNumber)
                            .FirstOrDefault(e => 
                                (e.SeasonNumber == lastWatched.SeasonNumber && e.EpisodeNumber > lastWatched.EpisodeNumber) ||
                                (e.SeasonNumber > lastWatched.SeasonNumber));
                        
                        if (nextEp != null)
                        {
                            var foundSeason = Seasons.FirstOrDefault(s => s.SeasonNumber == nextEp.SeasonNumber);
                            if (foundSeason != null)
                            {
                                targetSeasonIndex = Seasons.IndexOf(foundSeason);
                                episodeToSelect = foundSeason.Episodes.FirstOrDefault(e => e.Id == nextEp.Id);
                            }
                        }
                        else
                        {
                            var foundSeason = Seasons.FirstOrDefault(s => s.SeasonNumber == lastWatched.SeasonNumber);
                            if (foundSeason != null)
                            {
                                targetSeasonIndex = Seasons.IndexOf(foundSeason);
                                episodeToSelect = foundSeason.Episodes.FirstOrDefault(e => e.Id == lastWatched.Id);
                            }
                        }
                    }
                    else
                    {
                        var foundSeason = Seasons.FirstOrDefault(s => s.SeasonNumber == lastWatched.SeasonNumber);
                        if (foundSeason != null)
                        {
                            targetSeasonIndex = Seasons.IndexOf(foundSeason);
                            episodeToSelect = foundSeason.Episodes.FirstOrDefault(e => e.Id == lastWatched.Id);
                        }
                    }
                }

                if (episodeToSelect != null) _pendingAutoSelectEpisode = episodeToSelect;

                SeasonComboBox.ItemsSource = Seasons;
                if (Seasons.Count > 0) SeasonComboBox.SelectedIndex = targetSeasonIndex;

                if (_item is Models.Stremio.StremioMediaStream stremioItem)
                {
                    await RefreshStremioSeriesProgressAsync(stremioItem);
                }
                else if (_item is SeriesStream iptvSeries)
                {
                    await RefreshIptvSeriesProgressAsync(iptvSeries);
                }

                if (unified.BackdropUrls != null && unified.BackdropUrls.Count > 0)
                {
                    StartBackgroundSlideshow(unified.BackdropUrls);
                }
                if (SourceAttributionText != null)
                {
                    SourceAttributionText.Text = unified.MetadataSourceInfo;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SERIES] LoadSeriesData Error: {ex.Message}");
            }
                _isEpisodesLoading = false;
            }

        private void SetupStickyScroller()
        {
            RootScrollViewer.ViewChanged += (s, e) =>
            {
                var offset = RootScrollViewer.VerticalOffset;
                if (offset > 150 && _isWideModeIndex != 1) // Only show sticky header in Narrow Mode
                {
                    double progress = Math.Clamp((offset - 150) / 100.0, 0, 1);
                    StickyHeader.Opacity = progress;
                    // Slide down from -80 to 0
                    StickyHeaderTranslate.Y = -80 * (1.0 - progress);
                    StickyHeader.IsHitTestVisible = progress > 0.5;
                    
                    StickyPlayButtonText.Text = PlayButtonText.Text;
                }
                else
                {
                    StickyHeader.Opacity = 0;
                    StickyHeaderTranslate.Y = -80;
                    StickyHeader.IsHitTestVisible = false;
                }
            };
        }

        private void SetupActionBarImplicitAnimations()
        {
            try
            {
                if (_compositor == null) _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

                // Create shared implicit animation collection
                var implicitAnimations = _compositor.CreateImplicitAnimationCollection();

                var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
                offsetAnimation.Target = "Offset";
                var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
                offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
                offsetAnimation.Duration = TimeSpan.FromMilliseconds(260);

                implicitAnimations["Offset"] = offsetAnimation;

                var buttonAnimations = _compositor.CreateImplicitAnimationCollection();
                buttonAnimations["Offset"] = offsetAnimation;

                var sizeAnimation = _compositor.CreateVector2KeyFrameAnimation();
                sizeAnimation.Target = "Size";
                sizeAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
                sizeAnimation.Duration = TimeSpan.FromMilliseconds(220);
                buttonAnimations["Size"] = sizeAnimation;

                if (ActionBarPanel != null)
                {
                    var panelVisual = ElementCompositionPreview.GetElementVisual(ActionBarPanel);
                    panelVisual.ImplicitAnimations = null;
                    panelVisual.Offset = Vector3.Zero;
                }

                var actionButtons = new Button[] { PlayButton, RestartButton, TrailerButton, DownloadButton, CopyLinkButton, WatchlistButton };
                
                // Add children to glide as well
                UIElement[] glideChildren = { 
                    PlayButtonIcon, PlayButtonTextStack, 
                    RestartButtonIcon, RestartButtonTextStack 
                };

                var centerPointExpression = _compositor.CreateExpressionAnimation("Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0)");

                foreach (var btn in actionButtons)
                {
                    if (btn == null) continue;
                    var visual = ElementCompositionPreview.GetElementVisual(btn);
                    visual.StartAnimation("CenterPoint", centerPointExpression);
                    visual.ImplicitAnimations = buttonAnimations;
                }

                foreach (var child in glideChildren)
                {
                    if (child == null) continue;
                    var visual = ElementCompositionPreview.GetElementVisual(child);
                    visual.ImplicitAnimations = buttonAnimations;
                }
            }
            catch { }
        }

        private void SetupButtonInteractions(params Button[] buttons)
        {
            foreach (var btn in buttons)
            {
                if (btn == null) continue;
                if (!_initializedButtonInteractions.Add(btn)) continue;
                
                var visual = ElementCompositionPreview.GetElementVisual(btn);
                btn.SizeChanged += (s, e) => 
                {
                    visual.CenterPoint = new Vector3((float)btn.ActualWidth / 2f, (float)btn.ActualHeight / 2f, 0);
                };

                btn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
                {
                    var scale = _compositor.CreateVector3KeyFrameAnimation();
                    scale.InsertKeyFrame(1f, new Vector3(0.92f, 0.92f, 1f));
                    scale.Duration = TimeSpan.FromMilliseconds(100);
                    visual.StartAnimation("Scale", scale);
                }), true);

                btn.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, e) =>
                {
                    var spring = _compositor.CreateSpringVector3Animation();
                    spring.FinalValue = new Vector3(1f, 1f, 1f);
                    spring.DampingRatio = 0.5f;
                    spring.Period = TimeSpan.FromMilliseconds(40);
                    visual.StartAnimation("Scale", spring);
                }), true);
                
                btn.PointerExited += (s, e) =>
                {
                    var spring = _compositor.CreateSpringVector3Animation();
                    spring.FinalValue = new Vector3(1f, 1f, 1f);
                    spring.DampingRatio = 0.7f;
                    visual.StartAnimation("Scale", spring);
                };
            }
        }

        private void SetupMagneticEffect(Button btn, float intensity)
        {
            if (btn == null) return;
            if (!_initializedMagneticButtons.Add(btn)) return;
            var visual = ElementCompositionPreview.GetElementVisual(btn);
            try
            {
                ElementCompositionPreview.SetIsTranslationEnabled(btn, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MagneticEffect] Translation disabled for {btn.Name ?? "<button>"}: {ex.Message}");
                return;
            }

            var props = visual.Properties;
            props.InsertVector2("TouchPoint", new Vector2(0, 0));

            // Expression: (PointerPosition - Center) * intensity
            // For simplicity and high perf, we use the TouchPoint updated in PointerMoved
            var leanExpr = _compositor.CreateExpressionAnimation("Vector3(props.TouchPoint.X * intensity, props.TouchPoint.Y * intensity, 0)");
            leanExpr.SetReferenceParameter("props", props);
            leanExpr.SetScalarParameter("intensity", intensity);
            try
            {
                CompositionService.StartTranslationAnimation(HeroContainer, leanExpr);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MagneticEffect] StartAnimation failed for {btn.Name ?? "<button>"}: {ex.Message}");
                return;
            }

            btn.PointerMoved += (s, e) =>
            {
                var ptr = e.GetCurrentPoint(btn).Position;
                var cx = btn.ActualWidth / 2;
                var cy = btn.ActualHeight / 2;
                props.InsertVector2("TouchPoint", new Vector2((float)(ptr.X - cx), (float)(ptr.Y - cy)));
            };

            btn.PointerExited += (s, e) =>
            {
                try
                {
                    var reset = _compositor.CreateVector3KeyFrameAnimation();
                    reset.InsertKeyFrame(1f, new System.Numerics.Vector3(0, 0, 0));
                    reset.Duration = TimeSpan.FromMilliseconds(400);
                    CompositionService.StartTranslationAnimation(HeroContainer, reset);
                }
                catch { }
            };
        }

        private void SetupVortexEffect(Button btn, FrameworkElement target)
        {
            if (btn == null || target == null) return;
            var visual = ElementCompositionPreview.GetElementVisual(target);
            
            target.SizeChanged += (s, e) => {
                visual.CenterPoint = new Vector3((float)target.ActualWidth / 2f, (float)target.ActualHeight / 2f, 0);
            };

            btn.PointerEntered += (s, e) =>
            {
                // 1. Vortex Rotation with Overshoot
                var spin = _compositor.CreateScalarKeyFrameAnimation();
                spin.InsertKeyFrame(0.7f, 380f, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0f, 1f)));
                spin.InsertKeyFrame(1f, 360f);
                spin.Duration = TimeSpan.FromMilliseconds(700);
                visual.StartAnimation("RotationAngleInDegrees", spin);

                // 2. Anticipation Scale Pulse
                var pulse = _compositor.CreateVector3KeyFrameAnimation();
                pulse.InsertKeyFrame(0.3f, new Vector3(0.85f, 0.85f, 1f));
                pulse.InsertKeyFrame(1f, new Vector3(1.1f, 1.1f, 1f));
                pulse.Duration = TimeSpan.FromMilliseconds(300);
                visual.StartAnimation("Scale", pulse);

                // 3. AnimatedIcon State
                AnimatedIcon.SetState(BackIconVisual, "PointerOver");
            };

            btn.PointerExited += (s, e) =>
            {
                var reset = _compositor.CreateScalarKeyFrameAnimation();
                reset.InsertKeyFrame(1f, 0f);
                reset.Duration = TimeSpan.FromMilliseconds(500);
                visual.StartAnimation("RotationAngleInDegrees", reset);

                var scaleReset = _compositor.CreateSpringVector3Animation();
                scaleReset.FinalValue = new Vector3(1f, 1f, 1f);
                scaleReset.DampingRatio = 0.6f;
                visual.StartAnimation("Scale", scaleReset);

                AnimatedIcon.SetState(BackIconVisual, "Normal");
            };

            btn.PointerPressed += (s, e) =>
            {
                AnimatedIcon.SetState(BackIconVisual, "Pressed");
            };
            btn.PointerReleased += (s, e) =>
            {
                AnimatedIcon.SetState(BackIconVisual, "PointerOver");
            };
        }


        

        #region Actions

        private static bool IsGenericEpisodeTitle(string title, int episodeNumber)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;

            string t = title.Trim().ToLowerInvariant();
            if (t == episodeNumber.ToString()) return true;
            if (t == $"e{episodeNumber}" || t == $"ep {episodeNumber}" || t == $"ep. {episodeNumber}") return true;
            if (t.Contains("episode") || t.Contains("bölüm") || t.Contains("bolum")) return true;
            return false;
        }

        
        private async Task RefreshStremioSeriesProgressAsync(Models.Stremio.StremioMediaStream series)
        {
             // Refresh watched badges/checkmarks on all episodes
             foreach (var season in Seasons)
             {
                 foreach (var ep in season.Episodes)
                 {
                     ep.RefreshHistoryState();
                 }
             }

             // NOTE: Episode auto-selection and prebuffering is handled by:
             // LoadSeriesDataAsync → SeasonComboBox_SelectionChanged → EpisodesRepeater_SelectionChanged → StartPrebuffering
             // We do NOT override selection here to avoid conflicts with the next-episode logic.
        }



        private async void EpisodePlayButton_Click(object sender, RoutedEventArgs e)
        {
             if (sender is Button btn && btn.Tag is EpisodeItem ep)
             {
                 // Check if selection creates a change
                 bool isSelectionChange = _selectedEpisode != ep;
                 
                 // Ensure this episode is selected
                 // This triggers EpisodesRepeater_SelectionChanged which:
                 // 1. Updates UI (Play button text, badges)
                 // 2. For Stremio: Calls PlayStremioContent (Loads sources)
                 // 3. For IPTV: Updates Technical Badges & Prebuffers
                 _selectedEpisode = ep;
                 SelectEpisode(ep);

                 // STREMIO LOGIC
                 if (_item is Models.Stremio.StremioMediaStream)
                 {
                     // Only manually trigger if selection didn't change (because if it did change, the event handler already called it)
                     if (!isSelectionChange)
                     {
                         await PlayStremioContent(ep.Id, showGlobalLoading: false);
                     }
                     return;
                 }
                 
                 // IPTV Logic
                 // For IPTV, SelectionChanged ONLY updates UI/Badges. 
                 // Clicking "Play" on the card implies we want to Navigate to Player, so we ALWAYS do this.
                 if (_item is SeriesStream ss)
                 {
                      string parentId = ss.SeriesId.ToString();
                      await PerformHandoverAndNavigate(ep.StreamUrl, ep.Title, ep.Id, parentId, _item.Title, ep.SeasonNumber, ep.EpisodeNumber);
                 }
             }
        }

        private async void EpisodeArrowButton_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            e.Handled = true; // Stop bubbling to EpisodeItem_Tapped
            if (sender is FrameworkElement fe && fe.Tag is EpisodeItem ep)
            {
                // [FIX] Force selection without the "toggle off" behavior for the arrow button
                if (_selectedEpisode != ep)
                {
                    _selectedEpisode = null; // Reset to ensure SelectEpisode doesn't see it as a match and toggle off
                    SelectEpisode(ep);
                }

                // The arrow is the explicit "show sources" affordance. Row selection remains
                // a pure selected-episode update, while this path drills into sources.
                OpenSourcesPanel(PanelChangeReason.SourcesRequested);

                if (_item is Models.Stremio.StremioMediaStream)
                {
                    await PlayStremioContent(ep.Id, showGlobalLoading: false);
                }
                else if (_item is SeriesStream ss)
                {
                    if (!string.IsNullOrEmpty(ss.IMDbId))
                    {
                        string videoId = $"{ss.IMDbId}:{ep.SeasonNumber}:{ep.EpisodeNumber}";
                        await PlayStremioContent(videoId, showGlobalLoading: false);
                    }
                    else
                    {
                        string parentId = ss.SeriesId.ToString();
                        await PerformHandoverAndNavigate(ep.StreamUrl, ep.Title, ep.Id, parentId, _item.Title, ep.SeasonNumber, ep.EpisodeNumber);
                    }
                }
            }
        }

        private async void TrailerButton_Click(object sender, RoutedEventArgs e)
        {
            await _actionService.HandleTrailerClickAsync(_unifiedMetadata?.TrailerUrl, _item, _unifiedMetadata, sender);
        }

        private async Task PlayTrailer(string videoKey)
        {
            // Cancel previous Play requests
            _trailerCts?.Cancel();
            _trailerCts?.Dispose();
            _trailerCts = new System.Threading.CancellationTokenSource();
            var token = _trailerCts.Token;

            System.Diagnostics.Debug.WriteLine($"[INFO-TRAILER] PlayTrailer START. Key: {videoKey}");

            if (string.IsNullOrEmpty(videoKey))
            {
                System.Diagnostics.Debug.WriteLine("[INFO-TRAILER] videoKey is null or empty, aborting.");
                return;
            }

            // Extract ID if full URL is passed
            videoKey = TrailerPoolService.ExtractYouTubeId(videoKey);
            _currentTrailerKey = videoKey;
            
            System.Diagnostics.Debug.WriteLine($"[INFO-TRAILER] Cleaned Video ID: {videoKey}");

            Interlocked.Increment(ref _trailerUiVersion);

            // 1. IMMEDIATE UI FEEDBACK
            TrailerOverlay.Visibility = Visibility.Visible;
            TrailerScrim.Opacity = 1; 
            TrailerLoadingRing.IsActive = true;
            TrailerLoadingRing.Visibility = Visibility.Visible;
            // [ROOT FIX] Removed UpdateLayout() - triggers cycles if called during size transitions
            ApplyTrailerFullscreenLayout(enable: true);
            EnsureTrailerOverlayBounds();
            
            // CRITICAL: Reset visual state completely before animation
            // Read Width/Height AFTER XAML layout has run to get accurate values
            if (TrailerContent != null)
            {
                var contentVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(TrailerContent);
                
                // Stop ALL animations
                contentVisual.StopAnimation("Scale");
                contentVisual.StopAnimation("Offset");
                contentVisual.StopAnimation("Opacity");
                
                // CenterPoint uses the ACTUAL post-layout dimensions
                double targetW = TrailerContent.ActualWidth > 0 ? TrailerContent.ActualWidth : (TrailerContent.Width > 0 ? TrailerContent.Width : TrailerDefaultWidth);
                double targetH = TrailerContent.ActualHeight > 0 ? TrailerContent.ActualHeight : (TrailerContent.Height > 0 ? TrailerContent.Height : TrailerDefaultHeight);
                float centerX = (float)(targetW / 2.0);
                float centerY = (float)(targetH / 2.0);
                
                contentVisual.CenterPoint = new System.Numerics.Vector3(centerX, centerY, 0);
                contentVisual.Scale = new System.Numerics.Vector3(0.1f, 0.1f, 1f);
                // NOTE: Do NOT set Offset - let XAML layout compute the centered position
                
                System.Diagnostics.Debug.WriteLine($"[INFO-TRAILER] Visual reset - CenterPoint: {centerX}, {centerY}, ActualSize: {targetW}x{targetH}, Scale: 0.1");
            }
            
            // Start HIDDEN to avoid black screen / loading artifacts.
            System.Diagnostics.Debug.WriteLine("[INFO-TRAILER] Overlay Visible, LoadingRing Active.");

            if (token.IsCancellationRequested) return;

            // 2. ANIMATION (Expand from Button)
            try
            {
                // Using Composition for smoother performance
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(TrailerContent);
                
                // Ensure center point is correct (reading from ActualWidth/Height post-layout)
                double centerX = TrailerContent.ActualWidth > 0 ? TrailerContent.ActualWidth / 2 : (TrailerContent.Width > 0 ? TrailerContent.Width / 2 : TrailerDefaultWidth / 2);
                double centerY = TrailerContent.ActualHeight > 0 ? TrailerContent.ActualHeight / 2 : (TrailerContent.Height > 0 ? TrailerContent.Height / 2 : TrailerDefaultHeight / 2);
                visual.CenterPoint = new System.Numerics.Vector3((float)centerX, (float)centerY, 0);
                // NOTE: Do NOT set visual.Offset - XAML centering is authoritative
                
                System.Diagnostics.Debug.WriteLine($"[INFO-TRAILER] Animation Start - Center: {centerX}, {centerY}");
                
                // Animate Scale from 0.1 to 1
                var scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(0f, new System.Numerics.Vector3(0.1f, 0.1f, 1f));
                scaleAnim.InsertKeyFrame(1f, System.Numerics.Vector3.One);
                scaleAnim.Duration = TimeSpan.FromMilliseconds(250);
                visual.StartAnimation("Scale", scaleAnim);
                
                // Animate Opacity
                var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
                opacityAnim.InsertKeyFrame(0f, 0f);
                opacityAnim.InsertKeyFrame(1f, 1f);
                opacityAnim.Duration = TimeSpan.FromMilliseconds(250);
                visual.StartAnimation("Opacity", opacityAnim);
                
                // Reset XAML transform as we're using Composition animation
                TrailerTransform.TranslateX = 0;
                TrailerTransform.TranslateY = 0;
                TrailerTransform.ScaleX = 1;
                TrailerTransform.ScaleY = 1;

                TrailerContent.Opacity = 1; 

                // Animate Scrim Fade In
                var scrimVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(TrailerScrim);
                var fadeAnim = _compositor.CreateScalarKeyFrameAnimation();
                fadeAnim.InsertKeyFrame(0f, 0f);
                fadeAnim.InsertKeyFrame(1f, 1f);
                fadeAnim.Duration = TimeSpan.FromMilliseconds(220);
                scrimVisual.StartAnimation("Opacity", fadeAnim);
                TrailerScrim.Opacity = 1;
                
                System.Diagnostics.Debug.WriteLine("[INFO-TRAILER] Animation started, Opacity=1.");
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[INFO-TRAILER] ANIMATION FATAL ERROR: {ex}");
                 TrailerContent.Opacity = 1;
                 TrailerOverlay.Visibility = Visibility.Visible;
            }

            if (token.IsCancellationRequested) return;

            // 3. LOAD CONTENT
            await LoadTrailerContentAsync(videoKey, token);
        }

        private async Task LoadTrailerContentAsync(string videoKey, CancellationToken token)
        {
             try
             {
                 System.Diagnostics.Debug.WriteLine($"[INFO-TRAILER] Load Content via Pool: {videoKey}");
                 
                 // Acquire shared WebView
                 var webView = await TrailerPoolService.Instance.AcquireAsync(TrailerContent);
                 if (webView == null) return;

                 // Ensure Opacity/Visibility for the pooled webView
                 webView.Opacity = 0; 
                 webView.Visibility = Visibility.Visible;

                 // Hook messages if not already hooked (TrailerPoolService handles global broadcast, but we might want local logic)
                 // Actually, TrailerPoolService already has an event. Let's use it.
                 TrailerPoolService.Instance.TrailerMessageReceived -= OnPoolMessageReceived;
                 TrailerPoolService.Instance.TrailerMessageReceived += OnPoolMessageReceived;

                 if (token.IsCancellationRequested) return;

                 await TrailerPoolService.Instance.PlayTrailerAsync(webView, videoKey, unmute: true);
             }
             catch(Exception ex)
             {
                  System.Diagnostics.Debug.WriteLine($"[INFO-TRAILER] Load Error: {ex}");
             }
        }

        private void OnPoolMessageReceived(object sender, string message)
        {
            // [OWNERSHIP GUARD] Only process if we are the current owner in the pool
            if (TrailerPoolService.Instance.CurrentContainer != TrailerContent) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                var parts = message.Split(':');
                string cmd = parts[0];
                string msgId = parts.Length > 1 ? parts[1] : null;

                // [ID GUARD] Ignore messages for previous videos
                if (msgId != null && _currentTrailerKey != null && msgId != _currentTrailerKey)
                {
                    System.Diagnostics.Debug.WriteLine($"[INFO-TRAILER] Ignoring stale message {cmd} for ID {msgId} (Current: {_currentTrailerKey})");
                    return;
                }

                if (cmd == "READY")
                {
                    System.Diagnostics.Debug.WriteLine($"[INFO-TRAILER] Video Ready! ID: {msgId}");
                    TrailerLoadingRing.IsActive = false;
                    TrailerLoadingRing.Visibility = Visibility.Collapsed;
                    
                    // The shared WebView is inside TrailerContent
                    foreach (var child in TrailerContent.Children)
                    {
                        if (child is WebView2 wv) wv.Opacity = 1;
                    }
                }
                else if (cmd == "ENDED")
                {
                    System.Diagnostics.Debug.WriteLine($"[INFO-TRAILER] Video Ended. ID: {msgId}");
                    CloseTrailer();
                }
                else if (cmd == "ERROR")
                {
                    string errCode = parts.Length > 2 ? parts[2] : "Unknown";
                    System.Diagnostics.Debug.WriteLine($"[INFO-TRAILER] Video Error: {errCode}. ID: {msgId}");
                    CloseTrailer();
                }
            });
        }

        private void SetupParallax()
        {
            if (_compositor == null || RootScrollViewer == null) return;

            // Initialize Logo Composition System if needed
            if (_logoVisual == null)
            {
                _logoVisual = _compositor.CreateSpriteVisual();
                _logoBrush = _compositor.CreateSurfaceBrush();
                _logoBrush.Stretch = CompositionStretch.Uniform;
                _logoBrush.HorizontalAlignmentRatio = 0.0f; // Left align by default
                _logoBrush.VerticalAlignmentRatio = 1.0f;   // Bottom align by default
                _logoVisual.Brush = _logoBrush;
                _logoVisual.RelativeSizeAdjustment = System.Numerics.Vector2.One;
                
                if (IdentityControl?.LogoHost != null)
                {
                    Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(IdentityControl.LogoHost, _logoVisual);
                }
            }

            var scrollProp = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(RootScrollViewer);
            
            // 1. Backdrop Parallax (0.3x speed)
            if (HeroImage != null)
            {
                var visual = ElementCompositionPreview.GetElementVisual(HeroImage);
                var parallax = _compositor.CreateExpressionAnimation("Scrolling.Translation.Y * 0.3");
                parallax.SetReferenceParameter("Scrolling", scrollProp);
                visual.StartAnimation("Offset.Y", parallax);
            }

            // Info layout must stay physically anchored during resize and narrow scrolling.
        }

        private void CloseTrailerButton_Click(object sender, RoutedEventArgs e)
        {
            CloseTrailer();
        }

        private void TrailerScrim_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CloseTrailer();
        }

        // [COMPATIBILITY] Keep this to prevent TypeLoadException if XAML generated code is stale
        [System.Obsolete("Use TrailerScrim_Tapped instead")]
        private void TrailerScrim_PointerPressed(object sender, PointerRoutedEventArgs e) => CloseTrailer();

        // [COMPATIBILITY] Keep this to prevent TypeLoadException if XAML generated code is stale
        [System.Obsolete("Use PersonCardOverlay_Tapped instead")]
        private void PersonCardOverlay_PointerPressed(object sender, PointerRoutedEventArgs e) => ClosePersonCard();

        private async Task CloseTrailer()
        {
            System.Diagnostics.Debug.WriteLine("[INFO-TRAILER] CloseTrailer START.");
            
            _trailerCts?.Cancel();
            _trailerCts?.Dispose();
            _trailerCts = null;

            int closeVersion = Interlocked.Increment(ref _trailerUiVersion);
            _isTrailerFullscreen = false;
            
            // Release from Pool
            TrailerPoolService.Instance.TrailerMessageReceived -= OnPoolMessageReceived;
            TrailerPoolService.Instance.Release(TrailerContent);
            _isTrailerWebViewInitialized = false; 

            // Animate Scrim Fade Out
            if (TrailerScrim != null)
            {
                var scrimVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(TrailerScrim);
                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromMilliseconds(250);
                scrimVisual.StartAnimation("Opacity", fadeOut);
            }
            
            if (TrailerContent != null)
            {
                var contentVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(TrailerContent);
                var shrink = _compositor.CreateVector3KeyFrameAnimation();
                shrink.InsertKeyFrame(1f, new System.Numerics.Vector3(0.1f, 0.1f, 1f));
                shrink.Duration = TimeSpan.FromMilliseconds(250);
                contentVisual.StartAnimation("Scale", shrink);

                var opacityOut = _compositor.CreateScalarKeyFrameAnimation();
                opacityOut.InsertKeyFrame(1f, 0f);
                opacityOut.Duration = TimeSpan.FromMilliseconds(250);
                contentVisual.StartAnimation("Opacity", opacityOut);

                await Task.Delay(250);

                if (closeVersion != _trailerUiVersion) return;

                TrailerOverlay.Visibility = Visibility.Collapsed;
                TrailerScrim.Opacity = 0;
                TrailerContent.Opacity = 0;
                ApplyTrailerFullscreenLayout(enable: false);
            }
        }

        private void RefreshHistoryVisibility()
        {
             if (_item == null) return;

             // Logic mirrored from LoadDetailsAsync for refreshing subtexts
             if (_item is StremioMediaStream stremioItem)
             {
                 if (stremioItem.Meta.Type == "series" || stremioItem.Meta.Type == "tv")
                 {
                     _ = RefreshStremioSeriesProgressAsync(stremioItem);
                 }
                 else
                 {
                     var history = HistoryManager.Instance.GetProgress(stremioItem.Meta.Id);
                     UpdateMovieHistoryUi(history);
                 }
             }
             else if (_item is SeriesStream series)
             {
                 _ = RefreshIptvSeriesProgressAsync(series);
             }
             else if (_item is LiveStream live)
             {
                 var history = HistoryManager.Instance.GetProgress(live.StreamId.ToString());
                     UpdateMovieHistoryUi(history);
             }
        }

        private void StartPrebuffering(string url, double startTime = 0)
        {
            StartPrebufferingV2(url, startTime);
        }

        private async void StartPrebufferingV2(string url, double startTime = 0)
        {
            if (string.IsNullOrEmpty(url)) return;
            
            // [OPTIMIZATION] If player is set to Native (Media Foundation), do not start the MPV pre-buffer player.
            // This prevents ghost MPV processes and network contention during 4K playback.
            if (AppSettings.PlayerSettings.Engine == Models.PlayerEngine.Native)
            {
                Debug.WriteLine("[INFO-PAGE] Native Mode detected: Skipping MPV pre-buffering to prevent contention.");
                return;
            }

            if (!AppSettings.IsPrebufferEnabled) return;
            if (_prebufferUrl == url && MediaInfoPlayer != null) return; // Already prebuffering this url

            // Cancel any previous prebuffering
            try { _prebufferCts?.Cancel(); _prebufferCts?.Dispose(); } catch {}
            _prebufferCts = new CancellationTokenSource();
            var ct = _prebufferCts.Token;
            _prebufferUrl = url;

            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "START Prebuffering", $"{url} | Resume: {startTime}s");

            // 1. Ensure Player Instance Exists & is Attached
            bool isNew = false;
            if (MediaInfoPlayer == null)
            {
                try { 
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] CREATING NEW MpvPlayer in StartPrebuffering (IsNavigatingAway: {_isNavigatingAway})");
                    MediaInfoPlayer = new MpvWinUI.MpvPlayer(); 
                }
                catch (Exception ex) { Debug.WriteLine($"[Mpv] Fatal creation error: {ex.Message}"); }
                isNew = true;
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Mpv Control Constructor Done.");
            }

            try 
            {
               long startInit = swTotal.ElapsedMilliseconds;
               var pSettings = AppSettings.PlayerSettings;
               MediaInfoPlayer.RenderApi = pSettings.VideoOutput == ModernIPTVPlayer.Models.VideoOutput.GpuNext ? "gpu-next" : "dxgi";
               
               // Phase 1: Essential
               Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Starting Phase 1 (Essential Settings)...");
               await MpvSetupHelper.ApplyEssentialSettingsAsync(MediaInfoPlayer, url, isSecondary: true);
               Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Phase 1 Complete.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[INFO-PAGE] Failed to init pre-buffer player: {ex.Message}");
            }

            if (isNew)
            {
                MediaInfoPlayer.Width = 100;
                MediaInfoPlayer.Height = 100;
                if (PlayerHost != null) {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SOURCE_FINDER] ATTACHING Player to Host in StartPrebuffering (IsNavigatingAway: {_isNavigatingAway})");
                    PlayerHost.Content = MediaInfoPlayer;
                }
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Player attached to Host.");
            }

            // 2. WAIT for RenderControl (Critical for Re-entry)
            if (isNew)
            {
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Waiting for Player.Loaded event...");
                var tcs = new TaskCompletionSource<bool>();
                RoutedEventHandler handler = null;
                handler = (s, e) =>
                {
                    MediaInfoPlayer.Loaded -= handler;
                    tcs.TrySetResult(true);
                };
                MediaInfoPlayer.Loaded += handler;

                var timeoutTask = Task.Delay(2000);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Player.Loaded event {(completed == timeoutTask ? "TIMED OUT" : "RECEIVED")}.");
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                
                // [DEEP_DIAG_MARK] Timing analysis enabled.

                // 3. Configure Player
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Starting Phase 2 (Configuration)...");
                await MpvSetupHelper.ConfigurePlayerAsync(MediaInfoPlayer, url, isSecondary: true);
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Phase 2 Complete.");

                // 4. PRE-SEEK
                if (startTime > 0)
                {
                    await MediaInfoPlayer.SetPropertyAsync("start", startTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                // 5. Buffer settings
                bool isExplicitVod = _item is Models.Stremio.StremioMediaStream sms_pre && (sms_pre.Meta.Type == "movie" || sms_pre.Meta.Type == "series" || sms_pre.Meta.Type == "tv");
                if (_item is SeriesStream) isExplicitVod = true;
                bool isExplicitLive = (_item is LiveStream) || (_item is Models.Stremio.StremioMediaStream sms_l && sms_l.Meta.Type == "live");

                bool isLive = isExplicitLive || (_streamUrl != null && (_streamUrl.Contains("/live/") || _streamUrl.Contains(".m3u8") || _streamUrl.Contains(":8080") || _streamUrl.Contains("/ts")) && !isExplicitVod);
                await MpvSetupHelper.ApplyBufferSettingsAsync(MediaInfoPlayer, isSecondary: true, isLive: isLive);
                
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Buffer/Seek properties set.");

                // 6. Final UI Prep
                if (PlayerOverlayContainer != null)
                {
                    PlayerOverlayContainer.Visibility = Visibility.Visible;
                    PlayerOverlayContainer.Opacity = 0;
                }

                // 7. OpenAsync
                ct.ThrowIfCancellationRequested();
                await MediaInfoPlayer.SetPropertyAsync("pause", "yes"); 
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Calling OpenAsync (loadfile)...");
                await MediaInfoPlayer.OpenAsync(url);
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - OpenAsync CALL returned.");
                
                await MediaInfoPlayer.SetPropertyAsync("mute", "yes");
                await MediaInfoPlayer.SetPropertyAsync("pause", "yes");

                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Pre-buffering STARTED. Monitoring handshake...");
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[FastStart] Prebuffering CANCELLED (user navigated away).");
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    Debug.WriteLine("[FastStart] Prebuffering CANCELLED (user navigated away).");
                else
                    Debug.WriteLine($"[FastStart] Error: {ex.Message}");
            }
        }



        private void UpdateMovieHistoryUi(HistoryItem history)
        {
            if (history != null && history.Position > 0)
            {
                SyncActionButtonsInternal(history);

                double percent = history.Duration > 0 ? (history.Position / history.Duration) * 100 : 0;
                if (!history.IsFinished && percent < 98)
                {
                    // FAST START: Start pre-buffering since we are offering "Continue"
                    _streamUrl = history.StreamUrl;
                    if (!_shouldAutoResume)
                    {
                        StartPrebuffering(history.StreamUrl, history.Position);
                    }
                    _ = UpdateTechnicalBadgesAsync(_streamUrl);
                }
            }
        }

        private async Task RefreshIptvSeriesProgressAsync(SeriesStream series)
        {
             // Refresh watched badges/checkmarks on all episodes
             foreach (var season in Seasons)
             {
                 foreach (var ep in season.Episodes)
                 {
                     ep.RefreshHistoryState();
                 }
             }

             // NOTE: Episode auto-selection and prebuffering is handled by:
             // LoadSeriesDataAsync → SeasonComboBox_SelectionChanged → EpisodesRepeater_SelectionChanged → StartPrebuffering
        }

        public async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            await _actionService.HandlePlayClickAsync(_item, _selectedEpisode, _streamUrl, sender);
        }
        
        private async Task PerformHandoverAndNavigate(string url, string title, string id = null, string parentId = null, string seriesName = null, int season = 0, int episode = 0, double startSeconds = -1, string posterUrl = null, string type = null, string backdropUrl = null)
        {
            _isHandoffInProgress = false; // [FIX] Reset state
            _isNavigatingAway = true;
            // [OPTIMIZATION] Skip handoff for Native (Media Foundation) Mode
            if (AppSettings.PlayerSettings.Engine != Models.PlayerEngine.Native)
            {
                // Handoff Logic (MPV Only)
                try
                {
                    bool isPlayerActive = false;
                    if (MediaInfoPlayer != null)
                    {
                        try
                        {
                            if (_shouldAutoResume)
                            {
                                 isPlayerActive = false;
                                 Debug.WriteLine("[MediaInfoPage:Handoff] AutoResume active -> Forcing FRESH START (Skipping Handoff).");
                            }
                            else
                            {
                                string path = null;
                                try { path = await MediaInfoPlayer.GetPropertyAsync("path"); } catch { }

                                if (!string.IsNullOrEmpty(path) && path != "N/A")
                                {
                                    isPlayerActive = true;
                                    _isHandoffInProgress = true; // Confirmed Handoff
                                    App.HandoffPlayer = MediaInfoPlayer; 
                                    Debug.WriteLine($"[MediaInfoPage:Handoff] Player matched path: {path}");
                                    
                                    // PRE-WARM VISUALS
                                    _ = MpvSetupHelper.ApplyVisualSettingsAsync(MediaInfoPlayer);
                                    
                                    try 
                                    {
                                        MediaInfoPlayer.EnableHandoffMode();
                                        MediaInfoPlayer.EnsureSwapChainLinked();
                                    } catch { }

                                    try 
                                    {
                                        bool isExplicitVod = _item is Models.Stremio.StremioMediaStream sms_h && (sms_h.Meta.Type == "movie" || sms_h.Meta.Type == "series" || sms_h.Meta.Type == "tv");
                                        if (_item is SeriesStream) isExplicitVod = true;
                                        bool isExplicitLive = (_item is LiveStream) || (_item is Models.Stremio.StremioMediaStream sms_lh && sms_lh.Meta.Type == "live");

                                        bool isLive = isExplicitLive || (_streamUrl != null && (_streamUrl.Contains("/live/") || _streamUrl.Contains(".m3u8") || _streamUrl.Contains(":8080") || _streamUrl.Contains("/ts")) && !isExplicitVod);
                                        _ = MpvSetupHelper.ApplyBufferSettingsAsync(MediaInfoPlayer, isSecondary: false, isLive: isLive);
                                        _ = MediaInfoPlayer.SetPropertyAsync("pause", "no");
                                    } catch { }
                                    
                                    MediaInfoPlayer.EnsureSwapChainLinked();
                                    MediaInfoPlayer.EnableHandoffMode();
                                    
                                    var parent = MediaInfoPlayer.Parent;
                                    if (parent is Panel p) p.Children.Remove(MediaInfoPlayer);
                                    else if (parent is ContentControl cc) cc.Content = null;
                                }
                            }
                        }
                        catch (Exception ex) 
                        {
                            Debug.WriteLine($"[MediaInfoPage:Handoff] Player Check Failed: {ex.Message}");
                        }
                    }

                    if (!isPlayerActive)
                    {
                        Debug.WriteLine("[MediaInfoPage:Handoff] Player is not active or empty. Forcing FRESH START.");
                        
                        // Rejected preview players must leave the XAML tree before async cleanup
                        // starts; otherwise WinUI can measure a control whose native swapchain is
                        // being torn down on the dispatcher.
                        var pToDispose = MediaInfoPlayer;
                        App.HandoffPlayer = null;
                        MediaInfoPlayer = null;
                        _prebufferUrl = null;
                        
                        if (pToDispose != null)
                        {
                            DetachMediaInfoPlayerFromVisualTree(pToDispose);
                            CleanupMpvPlayerInBackground(pToDispose, "MediaInfo.RejectedHandoff");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaInfoPage:Handoff] ERROR: {ex}");
                }
            }
            else
            {
                Debug.WriteLine("[MediaInfoPage:Handoff] Native Mode detected: Skipping Handoff logic.");
                App.HandoffPlayer = null;
                _isHandoffInProgress = false;
            }

            // [FINAL NAVIGATION] Always happens here
            Debug.WriteLine($"[MediaInfoPage:Handoff] Navigating to PlayerPage for {url} | StartSeconds: {startSeconds} | HasHandoff: {App.HandoffPlayer != null}");
            
            string yearStr = _unifiedMetadata?.Year;
            string ratingStr = _unifiedMetadata?.Rating.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            string durationStr = _selectedEpisode?.Duration ?? _unifiedMetadata?.Runtime;
            string overviewStr = _unifiedMetadata?.Overview;

            Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(
                url, title, id, parentId, seriesName, season, episode, startSeconds, posterUrl, type, backdropUrl, 
                GetLogoUrl(), _primaryColorHex, _sourceAddonUrl, yearStr, ratingStr, durationStr, overviewStr));
        }

        private string GetLogoUrl()
        {
            if (_unifiedMetadata != null && !string.IsNullOrEmpty(_unifiedMetadata.LogoUrl)) return _unifiedMetadata.LogoUrl;
            return null;
        }


        
        private async Task PlayStremioContent(string videoId, bool showGlobalLoading = true, bool autoPlay = false, double startSeconds = -1)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return;
            System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] PlayStremioContent START for {videoId}");
            _isSourcesFetchInProgress = true;

            string type = (_item as Models.Stremio.StremioMediaStream)?.Meta?.Type ?? "movie";

            // [FIX] ID RESOLUTION: If videoId is NOT an IMDb ID, try to resolve it to one using unified metadata.
            string resolvedVideoId = videoId;
            if (!videoId.StartsWith("tt") && _unifiedMetadata != null)
            {
                if (type == "movie" && !string.IsNullOrEmpty(_unifiedMetadata.ImdbId) && _unifiedMetadata.ImdbId.StartsWith("tt"))
                {
                    resolvedVideoId = _unifiedMetadata.ImdbId;
                }
                else if (type == "series" && !string.IsNullOrEmpty(_unifiedMetadata.ImdbId) && _unifiedMetadata.ImdbId.StartsWith("tt"))
                {
                    var parts = videoId.Split(':');
                    if (parts.Length >= 3)
                    {
                        string season = parts[parts.Length - 2];
                        string episode = parts[parts.Length - 1];
                        resolvedVideoId = $"{_unifiedMetadata.ImdbId}:{season}:{episode}";
                    }
                }
            }
            
            if (resolvedVideoId != videoId) System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] Resolved ID: {videoId} -> {resolvedVideoId}");

            // Check if we're viewing the same video AND same item
            string currentItemId = _item is Models.Stremio.StremioMediaStream sms ? sms.Meta.Id : null;
            bool isSameItem = currentItemId != null && _currentStremioVideoId == resolvedVideoId;

            if (isSameItem)
            {
                bool hasVisibleSources = _addonResults != null &&
                                         _addonResults.Any(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0);

                if (hasVisibleSources)
                {
                    if (AddonSelectorList != null && AddonSelectorList.ItemsSource == null) AddonSelectorList.ItemsSource = _addonResults;
                    RefreshAllAddonActiveFlags();
                    SyncAddonSelectionToActive();
                    OpenSourcesPanel(PanelChangeReason.SourceCache);
                    ScrollToActiveSource();
                    
                    _isSourcesFetchInProgress = !_isCurrentSourcesComplete; 
                    return;
                }
            }
            else
            {
                _addonResults?.Clear();
            }

            _sourcesCts?.Cancel();
            _sourcesCts?.Dispose();
            _sourcesCts = new CancellationTokenSource();
            var sourcesToken = _sourcesCts.Token;

            int requestVersion = Interlocked.Increment(ref _sourcesRequestVersion);
            try
            {
                if (showGlobalLoading) SetLoadingState(true);

                string cacheKey = $"{type}|{resolvedVideoId}";
                bool hasCachedAddons = false;
                StremioSourcesCacheEntry cacheEntry = null;

                if (_stremioSourcesCache.TryGetValue(cacheKey, out cacheEntry) &&
                    cacheEntry?.Addons != null &&
                    cacheEntry.Addons.Count > 0)
                {
                    if (requestVersion != Volatile.Read(ref _sourcesRequestVersion)) return;

                    _currentStremioVideoId = resolvedVideoId;
                    _isCurrentSourcesComplete = cacheEntry.IsComplete;
                    _isSourcesFetchInProgress = !cacheEntry.IsComplete;
                    
                    // [ROOT CAUSE FIX] Use controller to load cached addons
                    foreach (var addon in cacheEntry.Addons)
                    {
                        _sourcesPanelController.AddOrUpdatePriorityAddon(CloneAddonViewModel(addon));
                    }
                    
                    var firstAddon = _addonResults.FirstOrDefault(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0);
                    if (firstAddon != null) OpenSourcesPanel(PanelChangeReason.SourceCache);
                    RefreshAllAddonActiveFlags();
                    SyncAddonSelectionToActive();
                    if (AddonSelectorList.SelectedItem == null) AddonSelectorList.SelectedItem = firstAddon;
                    if (AddonSelectorList.SelectedItem != null) OpenSourcesPanel(PanelChangeReason.SourceCache);

                    ScrollToActiveSource();

                    if (autoPlay)
                    {
                        var firstStream = firstAddon?.Streams?.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));
                        if (firstStream != null)
                        {
                            SetLoadingState(false);
                            string parentIdStr = (_item is Models.Stremio.StremioMediaStream sMediaStream && (sMediaStream.Meta.Type == "series" || sMediaStream.Meta.Type == "tv")) ? sMediaStream.Meta.Id : null;
                            string autoStreamType = (_item is Models.Stremio.StremioMediaStream sMediaStream2 && (sMediaStream2.Meta.Type == "series" || sMediaStream2.Meta.Type == "tv")) ? "series" : "movie";
                            _sourceAddonUrl = firstStream.AddonUrl;
                            
                            string yearStr = _unifiedMetadata?.Year;
                            string ratingStr = _unifiedMetadata?.Rating.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                            string durationStr = _selectedEpisode?.Duration ?? _unifiedMetadata?.Runtime;
                            string overviewStr = _unifiedMetadata?.Overview;

                            Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(
                                firstStream.Url, _item.Title, resolvedVideoId, parentIdStr, null, 
                                _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, 
                                startSeconds, _item.PosterUrl, autoStreamType, GetCurrentBackdrop(), 
                                GetLogoUrl(), _primaryColorHex, _sourceAddonUrl, yearStr, ratingStr, durationStr, overviewStr));
                            return;
                        }
                    }

                    if (cacheEntry.IsComplete)
                    {
                        if (showGlobalLoading) SetLoadingState(false);
                        return;
                    }
                }

                _currentStremioVideoId = resolvedVideoId;
                _isCurrentSourcesComplete = false;

                var addons = Services.Stremio.StremioAddonManager.Instance.GetAddons();

                // [USER REQUEST] Initialize List with Shimmers and Priority IPTV Source
                if (!hasCachedAddons || _addonResults == null)
                {
                    _addonResults = new System.Collections.ObjectModel.ObservableCollection<StremioAddonViewModel>();
                    AddonSelectorList.ItemsSource = _addonResults;
                }
                var activeCollection = _addonResults; 
                var dispatcherQueue = this.DispatcherQueue;

                System.Diagnostics.Debug.WriteLine($"[Stremio] Fetching sources for {resolvedVideoId} (Original: {videoId}) ({type}) from {addons.Count} addons.");

                // Get Last Played Stream for "Active" Indication
                string lastStreamUrl = _streamUrl;
                if (string.IsNullOrEmpty(lastStreamUrl))
                {
                    lastStreamUrl = HistoryManager.Instance.GetProgress(resolvedVideoId)?.StreamUrl;
                }

                StremioAddonViewModel iptvAddonToSelect = null;
                
                // [DEBUG] IPTV Match Logic
                AppLogger.Warn($"[IPTV_UI_MATCH] START: ItemTitle={_item?.Title}, UnifiedTitle={_unifiedMetadata?.Title}, Year={_unifiedMetadata?.Year}, IMDb={(_item as StremioMediaStream)?.IMDbId}");

                var iptvMatches = (_item is StremioMediaStream stremioStream) 
                    ? IptvMatchService.Instance.FindPotentialMatchesInIptv(_unifiedMetadata?.Title ?? stremioStream.Title, stremioStream.Meta?.Type ?? "movie") 
                    : new List<IMediaStream>();

                AppLogger.Warn($"[IPTV_UI_MATCH] MatchStremioItemAll count: {iptvMatches?.Count ?? 0}");

                if (iptvMatches != null && iptvMatches.Any())
                {
                    try
                    {
                        var iptvStreams = new List<StremioStreamViewModel>();
                        foreach (var match in iptvMatches)
                        {
                            string iptvUrl = match.StreamUrl;
                            string displayTitle = match.Title;

                            // [NEW] Resolve series match to specific episode if it's the current one
                            if (match is SeriesStream sMatch && _selectedEpisode != null && sMatch.SeriesId == _selectedEpisode.IptvSeriesId)
                            {
                                iptvUrl = _selectedEpisode.StreamUrl;
                                if (!string.IsNullOrEmpty(_selectedEpisode.IptvSourceTitle))
                                    displayTitle = _selectedEpisode.IptvSourceTitle;
                            }

                            if (string.IsNullOrEmpty(iptvUrl) && App.CurrentLogin != null)
                            {
                                string host = App.CurrentLogin.Host?.TrimEnd('/') ?? "";
                                string user = App.CurrentLogin.Username ?? "";
                                string pass = App.CurrentLogin.Password ?? "";

                                if (match is VodStream vStream)
                                {
                                    string ext = vStream.ContainerExtension ?? "mp4";
                                    iptvUrl = $"{host}/movie/{user}/{pass}/{vStream.StreamId}.{ext}";
                                }
                                else if (match is SeriesStream sStream)
                                {
                                    iptvUrl = $"iptv://series/{sStream.SeriesId}";
                                }
                                else
                                {
                                    iptvUrl = match.Id.ToString();
                                }
                            }

                            if (!string.IsNullOrEmpty(iptvUrl) && !iptvUrl.Contains("://") && !iptvUrl.StartsWith("/")) 
                                iptvUrl = $"iptv://{iptvUrl}";

                            if (iptvStreams.Any(s => s.Url == iptvUrl)) continue;

                            iptvStreams.Add(new StremioStreamViewModel
                            {
                                Title = displayTitle,
                                ProviderText = App.CurrentLogin?.PlaylistName?.ToUpperInvariant() ?? "IPTV",
                                AddonName = "IPTV",
                                Url = iptvUrl,
                                IptvStreamId = (match is VodStream vod) ? (int?)vod.StreamId : null,
                                IptvSeriesId = (match is SeriesStream series) ? (int?)series.SeriesId : null,
                                IsCached = true,
                                Quality = !string.IsNullOrEmpty(match.Resolution) ? match.Resolution : "VOD",
                                IsActive = !string.IsNullOrEmpty(lastStreamUrl) && iptvUrl == lastStreamUrl
                            });
                        }

                        // [ADD] Explicitly add selected episode's IPTV stream if it exists but wasn't in list above (Cross-check by URL)
                        if (_selectedEpisode != null && !string.IsNullOrEmpty(_selectedEpisode.StreamUrl) && _selectedEpisode.StreamUrl.Contains("/series/"))
                        {
                            bool alreadyIn = iptvStreams.Any(s => s.Url == _selectedEpisode.StreamUrl);
                            if (!alreadyIn)
                            {
                                iptvStreams.Insert(0, new StremioStreamViewModel
                                {
                                    Title = !string.IsNullOrEmpty(_selectedEpisode.IptvSourceTitle) ? _selectedEpisode.IptvSourceTitle : _selectedEpisode.Title,
                                    ProviderText = App.CurrentLogin?.PlaylistName?.ToUpperInvariant() ?? "IPTV",
                                    AddonName = "IPTV",
                                    Url = _selectedEpisode.StreamUrl,
                                    IptvStreamId = _selectedEpisode.IptvStreamId,
                                    IptvSeriesId = _selectedEpisode.IptvSeriesId,
                                    IsCached = true,
                                    Quality = _selectedEpisode.Resolution,
                                    IsActive = !string.IsNullOrEmpty(lastStreamUrl) && _selectedEpisode.StreamUrl == lastStreamUrl
                                });
                            }
                        }

                        if (iptvStreams.Any())
                        {
                            var iptvAddon = new StremioAddonViewModel
                            {
                                Name = "IPTV",
                                AddonUrl = "iptv://internal",
                                Streams = iptvStreams,
                                IsLoading = false,
                                SortIndex = -1
                            };

                            _addonResults.Add(iptvAddon);
                            iptvAddonToSelect = iptvAddon;
                        }
                    }
                    catch { }
                }

                // 2. PRE-POPULATE SHIMMERS FOR STREMIO ADDONS
                _sourcesPanelController.InitializeLoading(addons, GetDynamicShimmerCount());

                // Initial selection and layout refresh
                if (iptvAddonToSelect != null) AddonSelectorList.SelectedItem = iptvAddonToSelect;
                else if (_addonResults.Count > 0) AddonSelectorList.SelectedIndex = 0;

                // Show panel AFTER population to avoid global shimmer flicker
                OpenSourcesPanel(PanelChangeReason.SourceFetch);

                var tasks = new List<Task>();
                for (int i = 0; i < addons.Count; i++)
                {
                    int sortIndex = i;
                    string baseUrl = addons[i];

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var manifest = await Services.Stremio.StremioService.Instance.GetManifestAsync(baseUrl, sourcesToken);
                            if (manifest == null) 
                            {
                                // Remove failed addon from list
                                dispatcherQueue.TryEnqueue(() => {
                                    _sourcesPanelController.RemoveFailedAddon(baseUrl);
                                });
                                return;
                            }

                            if (!Services.Stremio.StremioAddonManager.Instance.SupportsResource(baseUrl, "stream"))
                            {
                                dispatcherQueue.TryEnqueue(() => {
                                    var failed = _addonResults.FirstOrDefault(a => a.AddonUrl == baseUrl && a.IsLoading);
                                    if (failed != null) _addonResults.Remove(failed);
                                });
                                return;
                            }

                            string addonDisplayName = NormalizeAddonText(manifest.Name ?? baseUrl.Replace("https://", "").Replace("http://", "").Split('/')[0]);
                            var streams = await Services.Stremio.StremioService.Instance.GetStreamsAsync(new List<string> { baseUrl }, type, resolvedVideoId, includeIptv: false, cancellationToken: sourcesToken);
                            
                            if (streams != null && streams.Count > 0)
                            {
                                var processedStreams = new List<StremioStreamViewModel>();
                                foreach (var s in streams)
                                {
                                    string displayFileName = "";
                                    string displayDescription = "";
                                    string rawName = NormalizeAddonText(s.Name ?? "");
                                    string rawTitle = NormalizeAddonText(s.Title ?? "");
                                    string rawDesc = NormalizeAddonText(s.Description ?? "");

                                    if (!string.IsNullOrEmpty(rawDesc))
                                    {
                                        var lines = rawDesc.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                                        var metaParts = new List<string>();
                                        foreach (var line in lines)
                                        {

                                            string trimmed = line.Trim();
                                            if (string.IsNullOrEmpty(trimmed)) continue;
                                            if (trimmed.StartsWith("Name:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("File:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("📄"))
                                                displayFileName = trimmed.Replace("Name:", "").Replace("File:", "").Replace("📄", "").Trim();
                                            else metaParts.Add(trimmed);
                                        }
                                        if (string.IsNullOrEmpty(displayFileName) && lines.Length > 0)
                                        {
                                            string lastLine = lines.Last().Trim();
                                            if (lastLine.Contains(".") && lastLine.Split('.').Last().Length <= 4)
                                            {
                                                displayFileName = lastLine;
                                                metaParts.RemoveAt(metaParts.Count - 1);
                                            }
                                        }
                                        displayDescription = string.Join("  •  ", metaParts);
                                    }

                                    string finalTitle = displayFileName;
                                    if (string.IsNullOrEmpty(finalTitle)) finalTitle = rawTitle;
                                    if (string.IsNullOrEmpty(finalTitle) || finalTitle.Length < 3) finalTitle = rawName.Split('\n')[0];
                                    if (string.IsNullOrEmpty(finalTitle)) finalTitle = addonDisplayName;

                                    bool isCached = IsStreamCached(s) || addonDisplayName.ToLower().Contains("debrid") || rawName.ToLower().Contains("rd+");
                                    string sizeInfo = ExtractSize(displayDescription) ?? ExtractSize(rawTitle) ?? ExtractSize(rawName);
                                    
                                    bool isActive = !string.IsNullOrEmpty(lastStreamUrl) && s.Url == lastStreamUrl;
                                    if (!isActive && !string.IsNullOrEmpty(lastStreamUrl))
                                    {
                                        try
                                        {
                                            string lastFileName = System.IO.Path.GetFileName(new Uri(lastStreamUrl).LocalPath);
                                            string currentFileName = System.IO.Path.GetFileName(new Uri(s.Url).LocalPath);
                                            if (!string.IsNullOrEmpty(lastFileName) && lastFileName == currentFileName) isActive = true;
                                        }
                                        catch { }
                                    }

                                    processedStreams.Add(new StremioStreamViewModel
                                    {
                                        Title = finalTitle,
                                        Name = displayDescription,
                                        ProviderText = rawName.Trim(),
                                        AddonName = addonDisplayName,
                                        AddonUrl = baseUrl,
                                        Url = s.Url,
                                        Externalurl = s.Externalurl,
                                        Quality = ParseQuality(rawName + " " + rawTitle + " " + rawDesc),
                                        Size = sizeInfo,
                                        IsCached = isCached,
                                        OriginalStream = s,
                                        IsActive = isActive
                                    });
                                }

                                if (processedStreams.Count > 0)
                                {
                                    var addonVM = new StremioAddonViewModel { Name = addonDisplayName.ToUpper(), AddonUrl = baseUrl, Streams = processedStreams, IsLoading = false, SortIndex = sortIndex };
                                    var tcs = new TaskCompletionSource<bool>();
                                    dispatcherQueue.TryEnqueue(() =>
                                    {
                                        try
                                        {
                                            if (requestVersion != Volatile.Read(ref _sourcesRequestVersion)) return;
                                            if (_addonResults != activeCollection) return;

                                            // [ROOT CAUSE FIX] Use controller to apply result and trigger correct staggered reveal
                                            var existing = _sourcesPanelController.ApplyAddonResult(baseUrl, addonDisplayName.ToUpper(), processedStreams, sortIndex);

                                            var partialSnapshot = _addonResults.Where(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0).Select(CloneAddonViewModel).ToList();
                                            if (partialSnapshot.Count > 0) _stremioSourcesCache[cacheKey] = new StremioSourcesCacheEntry { Addons = partialSnapshot, IsComplete = false };

                                            if (autoPlay && addonVM.Streams.Count > 0)
                                            {
                                                var firstStream = addonVM.Streams.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));
                                                if (firstStream != null)
                                                {
                                                    SetLoadingState(false);
                                                    string parentIdStr = (_item is Models.Stremio.StremioMediaStream sMediaStream3 && (sMediaStream3.Meta.Type == "series" || sMediaStream3.Meta.Type == "tv")) ? sMediaStream3.Meta.Id : null;
                                                     string autoStreamType = (_item is Models.Stremio.StremioMediaStream sMediaStream4 && (sMediaStream4.Meta.Type == "series" || sMediaStream4.Meta.Type == "tv")) ? "series" : "movie";
                                                    _sourceAddonUrl = firstStream.AddonUrl;

                                                    string yearStr = _unifiedMetadata?.Year;
                                                    string ratingStr = _unifiedMetadata?.Rating.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                                                    string durationStr = _selectedEpisode?.Duration ?? _unifiedMetadata?.Runtime;
                                                    string overviewStr = _unifiedMetadata?.Overview;

                                                    Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(
                                                        firstStream.Url, _item.Title, resolvedVideoId, parentIdStr, null, 
                                                        _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, 
                                                        startSeconds, _item.PosterUrl, autoStreamType, GetCurrentBackdrop(), 
                                                        GetLogoUrl(), _primaryColorHex, _sourceAddonUrl, yearStr, ratingStr, durationStr, overviewStr));
                                                    return;
                                                }
                                            }

                                            // [FIX] Always refresh active flags using the improved logic to ensure consistent highlighting
                                            RefreshAllAddonActiveFlags();

                                            // Select Active Addon if available
                                            bool activeInUpdate = addonVM.Streams.Any(s => s.IsActive);
                                            if (activeInUpdate) AddonSelectorList.SelectedItem = existing ?? addonVM;
                                            else if (AddonSelectorList.SelectedIndex == -1) AddonSelectorList.SelectedItem = existing ?? addonVM;

                                            if (activeInUpdate) ScrollToActiveSource();
                                        }
                                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Stremio] UI Error: {ex}"); }
                                        finally { tcs.TrySetResult(true); }
                                    });
                                    await tcs.Task;
                                }
                                else
                                {
                                    // No streams found for this addon - remove it
                                    dispatcherQueue.TryEnqueue(() => {
                                        var existing = _addonResults.FirstOrDefault(a => a.AddonUrl == baseUrl);
                                        if (existing != null) _addonResults.Remove(existing);
                                    });
                                }
                            }
                            else
                            {
                                // No streams - remove it
                                dispatcherQueue.TryEnqueue(() => {
                                    var existing = _addonResults.FirstOrDefault(a => a.AddonUrl == baseUrl);
                                    if (existing != null) _addonResults.Remove(existing);
                                });
                            }
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Stremio] Error fetching from {baseUrl}: {ex.Message}"); }
                    }));
                }

                // Safety Timeout: Don't wait forever if one addon hangs
                await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(15000, sourcesToken));
                
                var tcsFinal = new TaskCompletionSource<bool>();
                dispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        if (requestVersion != Volatile.Read(ref _sourcesRequestVersion)) return;
                        if (_addonResults != activeCollection) return;
                        
                        // Cleanup any remaining loading placeholders that didn't finish (due to error or no results)
                        var loadingLeft = _addonResults.Where(a => a.IsLoading).ToList();
                        foreach (var l in loadingLeft) _addonResults.Remove(l);

                        var cacheSnapshot = _addonResults.Where(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0).Select(CloneAddonViewModel).ToList();
                        if (cacheSnapshot.Count > 0) _stremioSourcesCache[cacheKey] = new StremioSourcesCacheEntry { Addons = cacheSnapshot, IsComplete = true };

                        if (showGlobalLoading) SetLoadingState(false);
                        _isSourcesFetchInProgress = false; _isCurrentSourcesComplete = true;
                        
                        _sourcesPanelController.CompleteLoading();
                        
                        System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] Source fetch COMPLETE for {resolvedVideoId}. Results: {_addonResults.Count} addons. Visible streams: {_visibleSourceStreams.Count}");
                        
                        if (_visibleSourceStreams.Count > 0)
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (SourcesRepeater != null)
                                {
                                    SourcesRepeater.ItemsSource = null;
                                    SourcesRepeater.ItemsSource = _visibleSourceStreams;
                                    System.Diagnostics.Debug.WriteLine($"[SOURCES] ItemsRepeater rebound with {_visibleSourceStreams.Count} items");
                                }
                            });
                        }
                        
                        OnDataCommitted();

                        if (!_addonResults.Any(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0))
                        {
                            if (ResolveCurrentContentKind() == MediaContentKind.Series)
                            {
                                OpenEpisodesPanel(PanelChangeReason.NoSources);
                            }
                            else
                            {
                                CloseDetailPanel(PanelChangeReason.NoSources);
                            }
                            try
                            {
                                if (this.XamlRoot != null)
                                {
                                    var err = new ContentDialog { 
                                        Title = "Kaynak Bulunamadı", 
                                        Content = "Eklentilerinizde bu içerik için uygun bir kaynak bulunamadı.", 
                                        CloseButtonText = "Tamam", 
                                        XamlRoot = this.XamlRoot 
                                    };
                                    await Services.DialogService.ShowAsync(err);
                                }
                            }
                            catch { }
                        }
                    }
                    finally { tcsFinal.TrySetResult(true); }
                });
                await tcsFinal.Task;
            }
            catch (Exception ex)
            {
                if (requestVersion == Volatile.Read(ref _sourcesRequestVersion))
                {
                    if (showGlobalLoading) SetLoadingState(false);
                    _isSourcesFetchInProgress = false;
                    OnDataCommitted();
                }
                System.Diagnostics.Debug.WriteLine($"PlayStremio Error: {ex}");
            }
        }

        private static StremioAddonViewModel CloneAddonViewModel(StremioAddonViewModel source)
        {
            return new StremioAddonViewModel
            {
                Name = source.Name,
                AddonUrl = source.AddonUrl,
                IsLoading = source.IsLoading,
                SortIndex = source.SortIndex,
                Streams = source.Streams?.Select(CloneStreamViewModel).ToList() ?? new List<StremioStreamViewModel>()
            };
        }

        private static StremioStreamViewModel CloneStreamViewModel(StremioStreamViewModel source)
        {
            return new StremioStreamViewModel
            {
                Title = source.Title,
                Name = source.Name,
                ProviderText = source.ProviderText,
                AddonName = source.AddonName,
                Url = source.Url,
                Externalurl = source.Externalurl,
                Quality = source.Quality,
                Size = source.Size,
                IsCached = source.IsCached,
                OriginalStream = source.OriginalStream,
                IsActive = source.IsActive
            };
        }

        private sealed class StremioSourcesCacheEntry
        {
            public List<StremioAddonViewModel> Addons { get; set; } = new();
            public bool IsComplete { get; set; }
        }

        private static string NormalizeAddonText(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;

            var text = input
                .Replace("â€¢", "•")
                .Replace("â€”", "-")
                .Replace("â€“", "-")
                .Replace("â€˜", "'")
                .Replace("â€™", "'")
                .Replace("â€œ", "\"")
                .Replace("â€ ", "\"")
                .Replace("Â", "")
                .Replace("ðŸ“„", "📄")
                .Replace("ğŸ“„", "📄")
                .Replace("âš¡", "⚡")
                .Replace("ğŸ“¥", "📥");

            if (LooksLikeMojibake(text))
            {
                try
                {
                    // Repair common UTF-8->Latin mojibake in addon metadata.
                    var bytes = Encoding.GetEncoding(28591).GetBytes(text);
                    text = Encoding.UTF8.GetString(bytes);
                    text = text.Replace("Â", "");
                }
                catch
                {
                    // Keep original text if conversion fails.
                }
            }

            return text;
        }

        private static bool LooksLikeMojibake(string text)
        {
            return text.Contains("Ã") ||
                   text.Contains("Ä") ||
                   text.Contains("Å") ||
                   text.Contains("â") ||
                   text.Contains("ðŸ") ||
                   text.Contains("ğŸ");
        }

        private string ParseQuality(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Contains("4K", StringComparison.OrdinalIgnoreCase) || text.Contains("2160p", StringComparison.OrdinalIgnoreCase)) return "4K";
            if (text.Contains("1080p", StringComparison.OrdinalIgnoreCase)) return "1080P";
            if (text.Contains("720p", StringComparison.OrdinalIgnoreCase)) return "720P";
            return "";
        }

        private bool IsStreamCached(ModernIPTVPlayer.Models.Stremio.StremioStream s)
        {
            string all = NormalizeAddonText((s.Name ?? "") + (s.Title ?? "") + (s.Description ?? "")).ToLowerInvariant();
            return all.Contains("⚡") || all.Contains("[rd+]") || all.Contains("[ad+]") || all.Contains("[pm+]") || 
                   all.Contains("cached") || all.Contains("downloaded") || all.Contains("tb+") || 
                   all.Contains("📥") || all.Contains("instant") || all.Contains("[debrid]") ||
                   all.Contains("real-debrid") || all.Contains("all-debrid") || all.Contains("premiumize");
        }

        private string ExtractSize(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            // Focus on common sizes, avoiding single 'B' false positives unless it's clearly Bytes
            var match = System.Text.RegularExpressions.Regex.Match(input, @"\d+(\.\d+)?\s*(GB|MB|MiB|GiB|TB)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Value : null;
        }


        private void RefreshAllAddonActiveFlags()
        {
            try
            {
                if (_addonResults == null) return;

                string activeUrl = _streamUrl;
                if (string.IsNullOrEmpty(activeUrl) && !string.IsNullOrEmpty(_currentStremioVideoId))
                {
                    activeUrl = HistoryManager.Instance.GetProgress(_currentStremioVideoId)?.StreamUrl;
                }

                if (string.IsNullOrEmpty(activeUrl)) return;

                string activeFileName = null;
                try { activeFileName = System.IO.Path.GetFileName(new Uri(activeUrl).LocalPath); } catch { }

                foreach (var addon in _addonResults)
                {
                    if (addon.Streams == null) continue;
                    foreach (var stream in addon.Streams)
                    {
                        string streamUrl = stream.Url ?? "";
                        string normActive = (activeUrl ?? "").Replace("iptv://", "").TrimEnd('/').ToLowerInvariant();
                        string normStream = (streamUrl ?? "").Replace("iptv://", "").TrimEnd('/').ToLowerInvariant();
                        bool isActive = !string.IsNullOrEmpty(normActive) && normStream == normActive;
                        // 1. Try match by filename in URL

                        string currentFileName = "";
                        try { currentFileName = System.IO.Path.GetFileName(new Uri(streamUrl).LocalPath); } catch { }
                        
                        if (!isActive && !string.IsNullOrEmpty(currentFileName) && !string.IsNullOrEmpty(activeFileName) && currentFileName == activeFileName) isActive = true;

                        // 2. Try match by Title (often contains the filename in debrid addons)
                        if (!isActive && !string.IsNullOrEmpty(stream.Title) && !string.IsNullOrEmpty(activeFileName))
                        {
                            if (stream.Title.ToLowerInvariant().Contains(activeFileName.ToLowerInvariant()) || activeFileName.ToLowerInvariant().Contains(stream.Title.ToLowerInvariant()))

                            {
                                isActive = true;
                            }
                        }
                        
                        // 3. Try match if current URL contains the active filename (some links are stream?file=...)
                        if (!isActive && !string.IsNullOrEmpty(streamUrl) && !string.IsNullOrEmpty(activeFileName) && streamUrl.Contains(activeFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            isActive = true;
                        }
                        stream.IsActive = isActive;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Stremio] RefreshAllAddonActiveFlags Error: {ex.Message}");
            }
        }

        private void SyncAddonSelectionToActive()
        {
            try
            {
                if (_addonResults == null) return;
                var activeAddon = _addonResults.FirstOrDefault(a => !a.IsLoading && a.Streams != null && a.Streams.Any(s => s.IsActive));
                if (activeAddon != null)
                {
                    if (AddonSelectorList != null) 
                    {
                        AddonSelectorList.SelectedItem = activeAddon;
                        ScrollToActiveSource();
                    }

                }
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[Stremio] SyncAddonSelectionToActive Error: {ex.Message}");
            }
        }



        private void ObsidianTray_TrayClosed(object sender, EventArgs e)
        {
             AnimateMainContentRecede(false);
        }

        private void AnimateMainContentRecede(bool recede)
        {
            var visual = ElementCompositionPreview.GetElementVisual(MainContentWrapper);
            var compositor = visual.Compositor;

            // 1. Scale Animation (0.98 for recede)
            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1.0f, recede ? new Vector3(0.98f, 0.98f, 1f) : Vector3.One);
            scaleAnim.Duration = TimeSpan.FromMilliseconds(500);
            visual.StartAnimation("Scale", scaleAnim);

            // 2. Blur / Dim Overlay (We use the Rectangle scrim if complex effects are too slow)
            // But let's try a simple dimming/opacity for now to be safe with performance
            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(1.0f, recede ? 0.6f : 1.0f);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(500);
            visual.StartAnimation("Opacity", opacityAnim);
        }

        private async void RestartButton_Click(object sender, RoutedEventArgs e)
        {
             if (!string.IsNullOrEmpty(_streamUrl))
            {
                // Series Episode
                if (_selectedEpisode != null)
                {
                     string parentId = _item is SeriesStream ss ? ss.SeriesId.ToString() : null;
                     if (_item is Models.Stremio.StremioMediaStream stremioItem && (stremioItem.Meta.Type == "series" || stremioItem.Meta.Type == "tv"))
                     {
                         parentId = stremioItem.Meta.Id;
                     }
                     
                     // [Fix] Force seek to 0 before handoff
                     if (MediaInfoPlayer != null)
                     {
                         try 
                         {
                             var path = await MediaInfoPlayer.GetPropertyAsync("path");
                             if (!string.IsNullOrEmpty(path))
                             {
                                 await MediaInfoPlayer.ExecuteCommandAsync("seek", "0", "absolute");
                                 System.Diagnostics.Debug.WriteLine("[Restart] Seeked to 0 before handoff.");
                             }
                         }
                         catch {}
                     }
                     
                     HistoryManager.Instance.UpdateProgress(_selectedEpisode.Id, _selectedEpisode.Title, _streamUrl, 0, 0, parentId, _item.Title, _selectedEpisode.SeasonNumber, _selectedEpisode.EpisodeNumber, null, null, null, _item.PosterUrl, "series", _item.BackdropUrl);
                     PerformHandoverAndNavigate(_streamUrl, _selectedEpisode.Title, _selectedEpisode.Id, parentId, _item.Title, _selectedEpisode.SeasonNumber, _selectedEpisode.EpisodeNumber, 0);
                }
                else if (_item is LiveStream live)
                {
                    // Update History to 0
                    HistoryManager.Instance.UpdateProgress(live.StreamId.ToString(), live.Title, live.StreamUrl, 0, 0, null, null, 0, 0, null, null, null, live.PosterUrl, "iptv", live.BackdropUrl);
                    
                     // [Fix] Force seek to 0 before handoff
                     if (MediaInfoPlayer != null)
                     {
                         try 
                         {
                             var path = await MediaInfoPlayer.GetPropertyAsync("path");
                             if (!string.IsNullOrEmpty(path)) await MediaInfoPlayer.ExecuteCommandAsync("seek", "0", "absolute");
                         }
                         catch {}
                     }

                    PerformHandoverAndNavigate(_streamUrl, live.Title, live.StreamId.ToString(), startSeconds: 0);
                }
            }
        }

        private void CopyLinkButton_Click(object sender, RoutedEventArgs e)
        {
            _actionService.HandleCopyLinkClick(_streamUrl, sender);
        }

        private List<System.Threading.CancellationTokenSource> _activeDownloads = new();

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
             if (string.IsNullOrEmpty(_streamUrl))
             {
                 await _actionService.HandleDownloadClickAsync(_item, _streamUrl, sender);
                 return;
             }

             if (_item is SeriesStream)
             {
                  var flyout = new MenuFlyout();

                  var singleItem = new MenuFlyoutItem { Text = "Bu Bölümü İndir", Icon = new FontIcon { Glyph = "\uE896" } };
                  singleItem.Click += async (s, args) => await DownloadSingle();
                  flyout.Items.Add(singleItem);

                  var seasonItem = new MenuFlyoutItem { Text = "Tüm Sezonu İndir", Icon = new FontIcon { Glyph = "\uE8B7" } };
                  seasonItem.Click += async (s, args) => await DownloadSeason();
                  flyout.Items.Add(seasonItem);

                  flyout.ShowAt(sender as FrameworkElement);
             }
             else
             {
                 await _actionService.HandleDownloadClickAsync(_item, _streamUrl, sender);
             }
        }
        
        private async Task DownloadSingle()
        {
             // Smart Download
             if (_streamUrl.Contains(".m3u8") || _streamUrl.Contains(".ts"))
             {
                 // Stream Dialog
                 var dialog = new ContentDialog
                 {
                     Title = "Canlı Yayın / Akış İndirme",
                     Content = "Bu içerik bir akış (HLS) formatındadır. Doğrudan dosya olarak indirilemez. Linki kopyalayıp IDM veya JDownloader gibi araçlar kullanmanızı öneririz.",
                     PrimaryButtonText = "Linki Kopyala",
                     CloseButtonText = "Kapat",
                     XamlRoot = this.XamlRoot
                 };

                 try
                  {
                      var result = await Services.DialogService.ShowAsync(dialog);
                      if (result == ContentDialogResult.Primary)
                      {
                          var pkg = new DataPackage();
                          pkg.SetText(_streamUrl);
                          Clipboard.SetContent(pkg);
                      }
                  }
                  catch (Exception ex)
                  {
                      System.Diagnostics.Debug.WriteLine($"[Download] Dialog Error: {ex.Message}");
                  }
             }
             else
             {
                 // Direct File
                 var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                 
                 // Initialize with Window Handle (Required for WinUI 3)
                 var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                 WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);

                 savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
                 savePicker.FileTypeChoices.Add("Video File", new List<string>() { ".mp4", ".mkv", ".avi" });
                 
                  // Try to get original filename from URL, fallback to Title
                  string fileName = IdentityControl?.TitleTextBlock?.Text ?? "Media";
                  try {
                      var uri = new Uri(_streamUrl);
                      string lastSegment = uri.Segments.Last();
                      if (lastSegment.Contains(".") && lastSegment.Length > 4) {
                          fileName = System.Net.WebUtility.UrlDecode(lastSegment);
                      }
                  } catch { }

                  // Sanitize filename
                  foreach (char c in System.IO.Path.GetInvalidFileNameChars()) {
                      fileName = fileName.Replace(c, '_');
                  }
                  
                  savePicker.SuggestedFileName = fileName;
                 
                  var file = await savePicker.PickSaveFileAsync();
                  if (file != null)
                  {
                      // Use Global Download Manager
                      Services.DownloadManager.Instance.StartDownload(file, _streamUrl, IdentityControl?.TitleTextBlock?.Text ?? "Media");
                  }
              }
        }

        private async Task DownloadSeason()
        {
            if (CurrentEpisodes == null || CurrentEpisodes.Count == 0) return;

            var folderPicker = new Windows.Storage.Pickers.FolderPicker();
            
            // Initialize with Window Handle
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hWnd);
            
            folderPicker.SuggestedStartLocation = PickerLocationId.Downloads;
            folderPicker.FileTypeFilter.Add("*"); // Required

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                // Find visible season number for naming
                string seriesName = _item?.Title ?? "Series";
                
                int enqueuedCount = 0;
                foreach(var ep in CurrentEpisodes)
                {
                    if (string.IsNullOrEmpty(ep.StreamUrl)) continue;
                    if (ep.StreamUrl.Contains(".m3u8") || ep.StreamUrl.Contains(".ts")) continue; // Skip streams

                    // Prepare Filename
                    string ext = ".mp4";
                    try {
                        var uri = new Uri(ep.StreamUrl);
                        string last = uri.Segments.Last();
                        if (last.Contains(".")) ext = Path.GetExtension(last);
                    } catch {}
                    
                    // Format: Series - S01E01 - Title.mp4
                    string sNum = ep.SeasonNumber.ToString().PadLeft(2, '0');
                    
                    // Use index as episode number fallback
                    int epNum = CurrentEpisodes.IndexOf(ep) + 1;
                    string eNum = epNum.ToString().PadLeft(2, '0');
                    
                    string fileName = $"{seriesName} - S{sNum}E{eNum} - {ep.Title}{ext}";

                    // Sanitize
                    foreach (char c in System.IO.Path.GetInvalidFileNameChars()) {
                         fileName = fileName.Replace(c, '_');
                    }
                    
                    try
                    {
                        var file = await folder.CreateFileAsync(fileName, Windows.Storage.CreationCollisionOption.GenerateUniqueName);
                        Services.DownloadManager.Instance.StartDownload(file, ep.StreamUrl, fileName.Replace(ext, ""));
                        enqueuedCount++;
                    }
                    catch { /* Skip failed file creation */ }
                }
                
                // Optional: Show small toast/notification "X episodes added to queue"
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) 
        { 
            ElementSoundPlayer.Play(ElementSoundKind.GoBack);
            if (Frame.CanGoBack) Frame.GoBack(); 
        }


        #endregion

              private void SetBadgeLoadingState(bool isLoading)
        {
            if (TechBadgesShimmer == null || TechBadgesContent == null || _compositor == null) return;

            if (isLoading)
            {
                if (MetadataRibbon != null) MetadataRibbon.Visibility = Visibility.Visible;
                TechBadgesShimmer.Width = double.NaN;
                TechBadgesShimmer.Visibility = Visibility.Visible;
                
                var visShim = ElementCompositionPreview.GetElementVisual(TechBadgesShimmer);
                if (visShim != null) visShim.Opacity = 1f;

                TechBadgesContent.Visibility = Visibility.Collapsed;
                var visContent = ElementCompositionPreview.GetElementVisual(TechBadgesContent);
                if (visContent != null) visContent.Opacity = 0f;
            }
            else
            {
                // Loaded: Cross-fade to Badges or Collapse if empty
                bool spansSpace = HasVisibleBadges();

                if (spansSpace)
                {
                    // Fade In Badges
                    TechBadgesContent.Visibility = Visibility.Visible;
                    var visContent = ElementCompositionPreview.GetElementVisual(TechBadgesContent);
                    if (visContent != null)
                    {
                        visContent.Opacity = 0f;

                        var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                        fadeIn.InsertKeyFrame(0f, 0f);
                        fadeIn.InsertKeyFrame(1f, 1f);
                        fadeIn.Duration = TimeSpan.FromMilliseconds(400);
                        visContent.StartAnimation("Opacity", fadeIn);
                        TechBadgesContent.Opacity = 1;
                    }

                    if (MetadataRibbon != null) MetadataRibbon.Visibility = Visibility.Visible;
                }

                // Fade Out Shimmer
                var visShimmer = ElementCompositionPreview.GetElementVisual(TechBadgesShimmer);
                if (visShimmer != null)
                {
                    var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                    fadeOut.InsertKeyFrame(0f, 1f);
                    fadeOut.InsertKeyFrame(1f, 0f);
                    fadeOut.Duration = TimeSpan.FromMilliseconds(300);

                    var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                    visShimmer.StartAnimation("Opacity", fadeOut);
                    batch.Completed += (s, e) =>
                    {
                        if (TechBadgesShimmer != null)
                        {
                            TechBadgesShimmer.Visibility = Visibility.Collapsed;
                            TechBadgesShimmer.Width = double.NaN;
                        }
                        UpdateTechnicalSectionVisibility(spansSpace);
                    };
                    batch.End();
                }
                else
                {
                    TechBadgesShimmer.Visibility = Visibility.Collapsed;
                    UpdateTechnicalSectionVisibility(spansSpace);
                }
            }
        }

        private void AdjustTechBadgesShimmer()
        {
            if (TechBadgesShimmer == null || TechBadgesContent == null) return;
            
            var visibleBorders = TechBadgesContent.Children.OfType<Border>()
                                   .Where(c => c.Visibility == Visibility.Visible)
                                   .ToList();

            for (int i = 0; i < TechBadgesShimmer.Children.Count; i++)
            {
                var shim = TechBadgesShimmer.Children[i] as FrameworkElement;
                if (shim == null) continue;

                if (i < visibleBorders.Count)
                {
                    var border = visibleBorders[i];
                    shim.Visibility = Visibility.Visible;
                    shim.Width = 50; // Use stable fallback
                }
                else
                {
                    shim.Visibility = Visibility.Collapsed;
                }
            }
        }

        private async Task UpdateTechnicalBadgesAsync(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "UpdateTechnicalBadges CANCELLED", "Null URL");
                UpdateTechnicalSectionVisibility(false);
                return;
            }

            Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "UpdateTechnicalBadges START", url);
            
            // Ensure shimmer is visible while probing
            SetBadgeLoadingState(true);
            UpdateTechnicalSectionVisibility(false);

            int currentVersion = Volatile.Read(ref _loadingVersion);

            // Cancel previous probe
            try
            {
                _probeCts?.Cancel();
                _probeCts?.Dispose(); 
            }
            catch { } 

            _probeCts = new CancellationTokenSource();
            var token = _probeCts.Token;

            try
            {
                // 0. CHECK IPTV METADATA FIRST (USER REQUEST) - Avoid Probing if possible
                string metadataRes = null;
                string metadataCodec = null;
                long metadataBitrate = 0;
                bool? metadataHdr = null;

                if (_selectedEpisode != null)
                {
                    metadataRes = _selectedEpisode.Resolution;
                    metadataCodec = _selectedEpisode.VideoCodec;
                    metadataBitrate = _selectedEpisode.Bitrate;
                    metadataHdr = _selectedEpisode.IsHdr;
                }
                else if (_unifiedMetadata != null)
                {
                    metadataRes = _unifiedMetadata.Resolution;
                    metadataCodec = _unifiedMetadata.VideoCodec;
                    metadataBitrate = _unifiedMetadata.Bitrate;
                    metadataHdr = _unifiedMetadata.IsHdr;
                }

                if (!string.IsNullOrEmpty(metadataRes) || !string.IsNullOrEmpty(metadataCodec))
                {
                    Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "IPTV METADATA: Skipping probe, using provided info", url);
                    var probeData = new Services.ProbeData
                    {
                        Resolution = metadataRes,
                        Codec = metadataCodec,
                        Bitrate = metadataBitrate,
                        IsHdr = metadataHdr ?? false
                    };
                    TryEnqueueForLoadSession(currentVersion, () =>
                    {
                        TechBadgesContent.Visibility = Visibility.Visible;
                        ApplyMetadataToUi(probeData, currentVersion);
                        SetBadgeLoadingState(false);
                    });
                    return;
                }

                // UI Reset for new probe
                Badge4K.Visibility = Visibility.Collapsed;
                BadgeRes.Visibility = Visibility.Collapsed;
                BadgeHDR.Visibility = Visibility.Collapsed;
                BadgeSDR.Visibility = Visibility.Collapsed;
                BadgeCodecContainer.Visibility = Visibility.Collapsed;

                // 1. Check ID-Based Binary Cache (v2.4)
                await Services.ProbeCacheService.Instance.EnsureLoadedAsync();
                if (Services.ProbeCacheService.Instance.Get(_item.Id) is Services.ProbeData cached)
                {
                    Services.CacheLogger.Success(Services.CacheLogger.Category.MediaInfo, "Badges Cache Hit", url);

                    TryEnqueueForLoadSession(currentVersion, () =>
                    {
                        TechBadgesContent.Opacity = 0;
                        TechBadgesContent.Visibility = Visibility.Visible;
                        ApplyMetadataToUi(cached, currentVersion);

                        // Quick Fade In
                        var visContent = ElementCompositionPreview.GetElementVisual(TechBadgesContent);
                        var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                        fadeIn.InsertKeyFrame(0f, 0f);
                        fadeIn.InsertKeyFrame(1f, 1f);
                        fadeIn.Duration = TimeSpan.FromMilliseconds(50);
                        visContent.StartAnimation("Opacity", fadeIn);
                        TechBadgesContent.Opacity = 1;

                        SetBadgeLoadingState(false);
                    });
                    return;
                }

                // 2. Show Shimmer
                SetBadgeLoadingState(true);

                // 3. SMART PROBE: Check if existing player is already opening this URL
                Services.ProbeResult probeResult;

                if (MediaInfoPlayer != null && _prebufferUrl == url)
                {
                    Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "SMART PROBE: Reusing prebuffer player", url);
                    // Wait for the active player to get metadata
                    probeResult = await Services.StreamProberService.ExtractProbeDataAsync(MediaInfoPlayer, token);
                }
                else
                {
                    Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "DEDICATED PROBE: Starting prober service", url);
                    probeResult = await Services.StreamProberService.Instance.ProbeAsync(_item.Id, url, progress: null, token);
                }

                if (token.IsCancellationRequested) return;

                if (probeResult.Success)
                {
                    // Manual cache update for SMART PROBE if needed
                    if (MediaInfoPlayer != null && _prebufferUrl == url)
                    {
                        Services.ProbeCacheService.Instance.Update(_item.Id, new Services.ProbeData 
                        { 
                            Resolution = probeResult.Resolution, 
                            Fps = probeResult.Fps, 
                            Codec = probeResult.Codec, 
                            Bitrate = probeResult.Bitrate, 
                            IsHdr = probeResult.IsHdr 
                        });
                    }

                    var probeData = new Services.ProbeData
                    {
                        Resolution = probeResult.Resolution,
                        Fps = probeResult.Fps,
                        Codec = probeResult.Codec,
                        Bitrate = probeResult.Bitrate,
                        IsHdr = probeResult.IsHdr
                    };

                    TryEnqueueForLoadSession(currentVersion, () =>
                    {
                        double shimmerWidth = MetadataShimmer.ActualWidth;
                        TechBadgesContent.Visibility = Visibility.Visible;
                        ApplyMetadataToUi(probeData, currentVersion);
                        
                        // [REMOVED] Manual width syncing between content and shimmer (Caused LayoutCycleException)
                        // if (hasVisibleBadges && shimmerWidth > 0 && TechBadgesContent.ActualWidth < shimmerWidth) TechBadgesContent.MinWidth = shimmerWidth;
                        // if (TechBadgesContent.ActualWidth > shimmerWidth && MetadataShimmer != null) MetadataShimmer.Width = TechBadgesContent.ActualWidth;
                        SetBadgeLoadingState(false);
                    });
                }
                else
                {
                    Services.CacheLogger.Warning(Services.CacheLogger.Category.MediaInfo, "Probe Failed", url);
                    TryEnqueueForLoadSession(currentVersion, () => SetBadgeLoadingState(false));
                }
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.MediaInfo, "Probe Error", ex.Message);
                TryEnqueueForLoadSession(currentVersion, () => SetBadgeLoadingState(false));
            }
        }


        private void ApplyMetadataToUi(Services.ProbeData result, int currentVersion)
        {
            if (result == null) return;

            // 1. Prepare display values
            bool is4K = !string.IsNullOrEmpty(result.Resolution) && (result.Resolution.Contains("3840") || result.Resolution.Contains("4096") || result.Resolution.ToUpperInvariant().Contains("4K"));
            
            string displayRes = result.Resolution;
            if (!string.IsNullOrEmpty(displayRes) && displayRes.Contains("x"))
            {
                var h = displayRes.Split('x').LastOrDefault();
                if (h != null) displayRes = h + "P";
            }
            
            string finalResBadge = is4K ? "4K" : (string.IsNullOrWhiteSpace(displayRes) || displayRes == "Unknown" || displayRes == "N/A" ? null : displayRes.ToUpperInvariant());

            // 2. Sync to Source List (StremioStreamViewModel)
            if (_addonResults != null)
            {
                var activeStream = _addonResults.SelectMany(a => a.Streams ?? new List<StremioStreamViewModel>()).FirstOrDefault(s => s.IsActive);
                if (activeStream != null)
                {
                    if (!string.IsNullOrEmpty(finalResBadge)) activeStream.Quality = finalResBadge;
                    activeStream.IsHdr = result.IsHdr;
                    activeStream.Codec = result.Codec;
                }
            }

            // 3. Update Info Panel Badges
            if (Badge4K != null) Badge4K.Visibility = is4K ? Visibility.Visible : Visibility.Collapsed;

            if (!is4K && !string.IsNullOrEmpty(finalResBadge))
            {
                if (BadgeResText != null) BadgeResText.Text = finalResBadge;
                if (BadgeRes != null) BadgeRes.Visibility = Visibility.Visible;
            }
            else if (BadgeRes != null)
            {
                BadgeRes.Visibility = Visibility.Collapsed;
            }

            // HDR / SDR
            if (BadgeHDR != null) BadgeHDR.Visibility = result.IsHdr ? Visibility.Visible : Visibility.Collapsed;
            if (BadgeSDR != null) BadgeSDR.Visibility = !result.IsHdr ? Visibility.Visible : Visibility.Collapsed;

            // Codec
            if (!string.IsNullOrWhiteSpace(result.Codec) && 
                result.Codec != "-" && result.Codec != "Unknown" && result.Codec != "Error" && result.Codec != "N/A" &&
                result.Codec.Trim().Length > 0)
            {
                if (BadgeCodec != null) BadgeCodec.Text = result.Codec;
                if (BadgeCodecContainer != null) BadgeCodecContainer.Visibility = Visibility.Visible;
            }
            else if (BadgeCodecContainer != null)
            {
                BadgeCodecContainer.Visibility = Visibility.Collapsed;
            }

            // Bitrate
            if (result.Bitrate > 0)
            {
                double mbps = result.Bitrate / 1000000.0;
                string formatted = mbps >= 1.0 ? $"{mbps:F1} Mbps" : $"{result.Bitrate / 1000} kbps";
                if (BadgeBitrateText != null) BadgeBitrateText.Text = formatted;
                if (BadgeBitrate != null) BadgeBitrate.Visibility = Visibility.Visible;
            }
            else if (BadgeBitrate != null)
            {
                BadgeBitrate.Visibility = Visibility.Collapsed;
            }

            // Age Rating & Country (from UnifiedMetadata)
            // [RACE CONDITION GUARD] If user navigated away or changed items, stop here.
            if (!IsCurrentLoadSession(currentVersion)) return;

            if (_unifiedMetadata != null)
            {
                if (BadgeAge != null)
                {
                    bool hasAge = !string.IsNullOrEmpty(_unifiedMetadata.AgeRating);
                    BadgeAge.Visibility = hasAge ? Visibility.Visible : Visibility.Collapsed;
                    if (hasAge && BadgeAgeText != null) BadgeAgeText.Text = _unifiedMetadata.AgeRating;
                }
                if (BadgeCountry != null)
                {
                    bool hasCountry = !string.IsNullOrEmpty(_unifiedMetadata.Country);
                    BadgeCountry.Visibility = hasCountry ? Visibility.Visible : Visibility.Collapsed;
                    if (hasCountry && BadgeCountryText != null) BadgeCountryText.Text = _unifiedMetadata.Country;
                }
            }

            UpdateTechnicalSectionVisibility(HasVisibleBadges());
        }

        /// <summary>
        /// Returns true if any technical badge (4K, Resolution, HDR, SDR, Codec) is currently visible.
        /// </summary>
        private bool HasVisibleBadges() =>
            (BadgeAge != null && BadgeAge.Visibility == Visibility.Visible) ||
            (BadgeCountry != null && BadgeCountry.Visibility == Visibility.Visible) ||
            Badge4K.Visibility == Visibility.Visible ||
            BadgeRes.Visibility == Visibility.Visible ||
            BadgeHDR.Visibility == Visibility.Visible ||
            BadgeSDR.Visibility == Visibility.Visible ||
            BadgeCodecContainer.Visibility == Visibility.Visible ||
            BadgeBitrate.Visibility == Visibility.Visible;


        private void AnimateOpacity(UIElement element, double toOpacity, TimeSpan duration)
        {
            if (element == null) return;
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = toOpacity,
                Duration = duration,
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, element);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
            storyboard.Children.Add(anim);
            storyboard.Begin();
        }

        private void SetupProfessionalAnimations()

        {
            // 1. Back Button Vortex + Morph
            SetupVortexEffect(BackButton, BackIconVisual);

            // 2. Play Button Anticipation
            SetupAnticipationPulse(PlayButton, PlayButtonIcon);
            SetupAnticipationPulse(StickyPlayButton, StickyPlayButtonIcon);
            
            // 3. Action Bar Buttons
            var actionButtons = new Button[] { DownloadButton, TrailerButton, CopyLinkButton, RestartButton, WatchlistButton };
            foreach (var btn in actionButtons)
            {
                if (btn != null) SetupAnticipationPulse(btn, (FrameworkElement)btn.Content);
            }

            // 4. Alive System: Organic Breathing
            ApplyOrganicBreathing(PlayButtonIcon);

            // 5. Modern Layout: Implicit Animations (Force smooth resizing)
            SetupImplicitAnimations();
        }

        private void SetupImplicitAnimations()
        {
            try
            {
                // 1. Offset/Translation Animation
                // 1. Offset/Translation Animation
                var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
                offsetAnimation.Target = "Offset";
                offsetAnimation.Duration = TimeSpan.FromMilliseconds(400); 
                var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));
                offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);

                // 2. Scale Animation
                var scaleAnimation = _compositor.CreateVector3KeyFrameAnimation();
                scaleAnimation.Target = "Scale";
                scaleAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
                scaleAnimation.Duration = TimeSpan.FromMilliseconds(350);

                // 3. Opacity Animation
                var opacityAnimation = _compositor.CreateScalarKeyFrameAnimation();
                opacityAnimation.Target = "Opacity";
                opacityAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
                opacityAnimation.Duration = TimeSpan.FromMilliseconds(400);

                var implicitAnimationCollection = _compositor.CreateImplicitAnimationCollection();
                implicitAnimationCollection["Offset"] = offsetAnimation;
                implicitAnimationCollection["Scale"] = scaleAnimation;
                implicitAnimationCollection["Opacity"] = opacityAnimation;

                var opacityOnlyCollection = _compositor.CreateImplicitAnimationCollection();
                opacityOnlyCollection["Opacity"] = opacityAnimation;

                var scaleOpacityCollection = _compositor.CreateImplicitAnimationCollection();
                scaleOpacityCollection["Scale"] = scaleAnimation;
                scaleOpacityCollection["Opacity"] = opacityAnimation;

                // Elements that should "glide" and "morph" during resize/layout changes
                UIElement[] glideElements = { 
                    InfoContainer, InfoContainerInner, AdaptiveInfoHost, InfoColumn, MetadataRibbon, ActionBarPanel, 
                    CastSection, DirectorSection, 
                    IdentityControl?.TitlePanelElement, IdentityControl?.LogoHost, IdentityControl?.TitleTextBlock,
                    IdentityControl?.IdentityPanel, OverviewPanel, GenresText, MetadataPanel,
                    IdentityControl?.TitleShimmerElement, MetadataShimmer, ActionBarShimmer, OverviewShimmer,
                    EpisodesPanel, SourcesPanel 
                };
                
                // Define which elements get the full Offset animation (gliding/morphing)
                HashSet<UIElement> offsetGlideElements = new() { 
                    EpisodesPanel, SourcesPanel, 
                    InfoContainer, InfoContainerInner, AdaptiveInfoHost, InfoColumn,
                    ActionBarPanel, MetadataPanel, OverviewPanel, GenresText, 
                    IdentityControl?.IdentityPanel, IdentityControl?.TitlePanelElement,
                    MetadataRibbon
                };

                foreach (var element in glideElements)
                {
                    if (element == null) continue;
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    
                    if (offsetGlideElements.Contains(element))
                    {
                        visual.ImplicitAnimations = implicitAnimationCollection;
                    }
                    else
                    {
                        visual.ImplicitAnimations = scaleOpacityCollection;
                    }

                    // Enable translation facade
                    ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LayoutDebug] SetupImplicitAnimations Error: {ex.Message}");
            }
        }

        private void SetupAnticipationPulse(Button btn, FrameworkElement content)
        {
            if (btn == null || content == null) return;
            var compositor = _compositor;
            if (compositor == null) return;
            
            // 1. Content Visual (Scale Pulse)
            var contentVisual = ElementCompositionPreview.GetElementVisual(content);
            
            // 2. Button Visual (Magnetic Positional Tracking)
            var btnVisual = ElementCompositionPreview.GetElementVisual(btn);
            try
            {
                ElementCompositionPreview.SetIsTranslationEnabled(btn, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AnticipationPulse] Translation disabled for {btn.Name ?? "<button>"}: {ex.Message}");
                return;
            }

            void UpdateCenter()
            {
                contentVisual.CenterPoint = new Vector3((float)content.ActualWidth / 2f, (float)content.ActualHeight / 2f, 0);
            }

            content.SizeChanged += (s, e) => UpdateCenter();
            if (content.ActualWidth > 0) UpdateCenter();

            btn.PointerMoved += (s, e) => 
            {
                try
                {
                    // Calculate Magnetic Offset
                    var ptr = e.GetCurrentPoint(btn);
                    var center = new Windows.Foundation.Point(btn.ActualWidth / 2, btn.ActualHeight / 2);
                    var deltaX = (float)(ptr.Position.X - center.X);
                    var deltaY = (float)(ptr.Position.Y - center.Y);
                    
                    // Limit movement (Magnetic strength)
                    float limit = 12f;
                    float moveX = Math.Clamp(deltaX * 0.35f, -limit, limit);
                    float moveY = Math.Clamp(deltaY * 0.35f, -limit, limit);

                    // Stop any reset animation and apply direct offset from Pointer
                    CompositionService.StopTranslationAnimation(btn);
                    CompositionService.SetTranslation(btn, new Vector3(moveX, moveY, 0));
                }
                catch {}
            };

            btn.PointerEntered += (s, e) => {
                try {
                    // Pulse Scale on Content
                    contentVisual.StopAnimation("Scale");
                    var pulse = compositor.CreateVector3KeyFrameAnimation();
                    pulse.InsertKeyFrame(0.2f, new Vector3(0.85f, 0.85f, 1f));
                    pulse.InsertKeyFrame(0.6f, new Vector3(1.25f, 1.25f, 1f));
                    pulse.InsertKeyFrame(1f, new Vector3(1.15f, 1.15f, 1f));
                    pulse.Duration = TimeSpan.FromMilliseconds(500);
                    contentVisual.StartAnimation("Scale", pulse);
                } catch {}
            };

            btn.PointerExited += (s, e) => {
                try {
                    // Reset Scale
                    contentVisual.StopAnimation("Scale");
                    var resetScale = compositor.CreateVector3KeyFrameAnimation();
                    resetScale.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
                    resetScale.Duration = TimeSpan.FromMilliseconds(300);
                    contentVisual.StartAnimation("Scale", resetScale);
                    
                    // Reset Position (Spring back)
                    btnVisual.StopAnimation("Translation");
                    var resetPos = compositor.CreateVector3KeyFrameAnimation();
                    resetPos.InsertKeyFrame(1f, Vector3.Zero);
                    resetPos.Duration = TimeSpan.FromMilliseconds(400);
                    // Use Cubic Bezier for smooth return
                    resetPos.InsertKeyFrame(0.5f, Vector3.Zero, compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0f, 1f))); 
                    // Actually simple keyframe to 0 is fine
                    CompositionService.StartTranslationAnimation(btn, resetPos);
                } catch {}
            };
        }

        private void ApplyOrganicBreathing(FrameworkElement element)
        {
            if (element == null) return;
            var visual = ElementCompositionPreview.GetElementVisual(element);
            
            element.SizeChanged += (s, e) => {
                visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
            };

            var breath = _compositor.CreateVector3KeyFrameAnimation();
            breath.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
            breath.InsertKeyFrame(0.5f, new Vector3(1.04f, 1.04f, 1f));
            breath.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
            breath.Duration = TimeSpan.FromSeconds(4);
            breath.IterationBehavior = AnimationIterationBehavior.Forever;
            
            visual.StartAnimation("Scale", breath);
        }

        private async void MarkWatched_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is EpisodeItem ep)
            {
                 // Resolve Series ID and Name
                 string seriesId = "";
                 string seriesName = "";
                 
                 // Use SeriesStream instead of IPTVSERIES
                 if (_item is SeriesStream iptv)
                 {
                     seriesId = iptv.SeriesId.ToString();
                     seriesName = iptv.Name;
                 }
                 else if (_item is StremioMediaStream st)
                 {
                     seriesId = st.IMDbId ?? st.Id.ToString();
                     seriesName = st.Title; 
                 }

                 // Mark as completed
                 HistoryManager.Instance.UpdateProgress(ep.Id, 
                     ep.Title, 
                     ep.StreamUrl ?? "", 
                     1000, 1000, 
                     seriesId,
                     seriesName,
                     ep.SeasonNumber, 
                                          ep.EpisodeNumber, null, null, null, _item?.PosterUrl, "series", _item?.BackdropUrl);


                 // Update UI
                 ep.IsWatched = true;
                 ep.ProgressPercent = 0;
                 ep.ProgressText = "";
                 ep.HasProgress = false;
                 
                 await HistoryManager.Instance.SaveAsync();
            }
        }
        
        private async void MarkRemainingWatched_Click(object sender, RoutedEventArgs e)
        {
            // Determine start episode from context menu context
            EpisodeItem startEpisode = null;
            if (sender is MenuFlyoutItem item && item.Tag is EpisodeItem epTag)
            {
                startEpisode = epTag;
            }
            
            if (EpisodesRepeater.ItemsSource is IEnumerable<EpisodeItem> episodes)
            {
                 string seriesId = "";
                 string seriesName = "";
                 
                 if (_item is SeriesStream iptv)
                 {
                     seriesId = iptv.SeriesId.ToString();
                     seriesName = iptv.Name;
                 }
                 else if (_item is StremioMediaStream st)
                 {
                     seriesId = st.IMDbId ?? st.Id.ToString();
                     seriesName = st.Title;
                 }
                 
                 bool shouldMark = (startEpisode == null); // If no specific start, mark all (fallback)
                 
                 foreach (var ep in episodes)
                 {
                     if (ep == startEpisode) shouldMark = true;
                     
                     if (shouldMark && !ep.IsWatched)
                     {
                         // [FIX] Validating if the episode has actually aired/is available before marking watched
                         bool isAired = ep.ReleaseDate.HasValue ? (ep.ReleaseDate.Value <= DateTime.Now.AddDays(1)) : ep.IsReleased;
                         bool hasStream = !string.IsNullOrEmpty(ep.StreamUrl);

                         if (isAired || hasStream)
                         {
                             HistoryManager.Instance.UpdateProgress(ep.Id, 
                                 ep.Title, 
                                 ep.StreamUrl ?? "", 
                                 1000, 1000, 
                                 seriesId,
                                 seriesName,
                                 ep.SeasonNumber, 
                                 ep.EpisodeNumber, null, null, null, _item?.PosterUrl, "series", _item?.BackdropUrl);
                                 
                             ep.IsWatched = true;
                             ep.ProgressPercent = 0;
                             ep.ProgressText = "";
                             ep.HasProgress = false;
                         }
                     }
                 }
                 
                 await HistoryManager.Instance.SaveAsync();
            }
        }

        private async void MarkUnwatched_Click(object sender, RoutedEventArgs e)
        {
             if (sender is MenuFlyoutItem item && item.Tag is EpisodeItem ep)
            {
                // Resolve Series ID
                 string seriesId = "";
                 string seriesName = "";
                 
                 if (_item is SeriesStream iptv)
                 {
                     seriesId = iptv.SeriesId.ToString();
                     seriesName = iptv.Name;
                 }
                 else if (_item is StremioMediaStream st)
                 {
                      seriesId = st.IMDbId ?? st.Id.ToString();
                      seriesName = st.Title;
                 }

                // Reset
                HistoryManager.Instance.UpdateProgress(ep.Id, 
                     ep.Title, 
                     ep.StreamUrl ?? "", 
                     0, 1000, 
                     seriesId,
                     seriesName,
                     ep.SeasonNumber, 
                                          ep.EpisodeNumber, null, null, null, _item?.PosterUrl, "series", _item?.BackdropUrl);

                     
                // Update UI
                ep.IsWatched = false;
                ep.ProgressPercent = 0;
                ep.ProgressText = "";
                ep.HasProgress = false;
                
                await HistoryManager.Instance.SaveAsync();
            }
        }

        private async void WatchlistButton_Click(object sender, RoutedEventArgs e)
        {
            await _actionService.HandleWatchlistClickAsync(_item, sender);
        }

        private void UpdateWatchlistState(bool animate = false)
        {
            if (WatchlistButton == null) return;

            bool isInList = _item != null && Services.WatchlistManager.Instance.IsOnWatchlist(_item);
            var icon = WatchlistButton.Content as FontIcon;
            if (icon == null) return;

            string newGlyph = isInList ? "\uE73E" : "\uE710"; // Checkmark vs Plus
            
            if (animate)
            {
                var visual = ElementCompositionPreview.GetElementVisual(icon);
                var compositor = visual.Compositor;

                var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(0f, new System.Numerics.Vector3(1f, 1f, 1f));
                scaleAnim.InsertKeyFrame(0.5f, new System.Numerics.Vector3(1.4f, 1.4f, 1f));
                scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1f, 1f, 1f));
                scaleAnim.Duration = TimeSpan.FromMilliseconds(300);
                scaleAnim.Target = "Scale";

                visual.StartAnimation("Scale", scaleAnim);
            }

            icon.Glyph = newGlyph;
            icon.Foreground = isInList 
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 33, 150, 243)) // Blue Icon when added
                : new SolidColorBrush(Microsoft.UI.Colors.White);

            if (isInList)
            {
                var targetBg = Windows.UI.Color.FromArgb(50, 33, 150, 243); // Subtle Blue Tint
                if (animate) AnimateBrushColor(WatchlistButton, targetBg, 1.0);
                else WatchlistButton.Background = new SolidColorBrush(targetBg);
            }
            else
            {
                // [CONS] Link to global theme brush for uniform management
                if (_themeTintBrush != null) WatchlistButton.Background = _themeTintBrush;
                else WatchlistButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(37, 255, 255, 255));
            }
            
            ToolTipService.SetToolTip(WatchlistButton, isInList ? "İzleme Listesinden Çıkar" : "İzleme Listesine Ekle");
        }

        private void AnimateOpacity(UIElement element, float targetOpacity, TimeSpan duration)
        {
            if (element == null) return;
            if (_compositor == null) EnsureHeroVisuals();

            if (targetOpacity <= 0.01f)
            {
                element.Opacity = 0;
                var elementVisual = ElementCompositionPreview.GetElementVisual(element);
                elementVisual?.StopAnimation("Opacity");
                if (elementVisual != null) elementVisual.Opacity = 0f;
                return;
            }
            
            var visual = ElementCompositionPreview.GetElementVisual(element);
            
            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1f, targetOpacity);
            animation.Duration = duration;
            
            visual.StartAnimation("Opacity", animation);
            element.Opacity = 1; // Ensure XAML visibility but use Composition for the actual fade
        }
        #region Mouse Drag-to-Scroll Logic
        private void OnMainPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var ptr = e.GetCurrentPoint(null); // Use window coords for smoothness
            
            if (ActualWidth >= LayoutAdaptiveThreshold)
            {
                var localPtr = e.GetCurrentPoint(RootGrid);
                if (localPtr.Position.X > (RootGrid.ActualWidth - 500)) 
                {
                    return; 
                }
            }
            
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && ptr.Properties.IsLeftButtonPressed)
            {
                _isMainDragging = true;
                _lastMainPointerPos = ptr.Position;
                // [FIX] Don't capture yet! Wait for movement.
                // RootScrollViewer.CapturePointer(e.Pointer);
                // e.Handled = true;
            }
        }

        private void OnMainPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isMainDragging)
            {
                // Conflict resolution: if cast dragging is active, abort main drag
                if (_isCastDragging)
                {
                    _isMainDragging = false;
                    return;
                }

                var ptr = e.GetCurrentPoint(null); // Use window coords for smoothness
                
                // Safety: check if left button is still pressed
                if (!ptr.Properties.IsLeftButtonPressed)
                {
                    _isMainDragging = false;
                    try { RootScrollViewer.ReleasePointerCapture(e.Pointer); } catch {}
                    return;
                }

                double deltaY = _lastMainPointerPos.Y - ptr.Position.Y;
                
                // [FIX] Threshold-based capture: only handle if we've actually moved enough. 
                // This allows static clicks to fall through to child items.
                if (Math.Abs(deltaY) > 3.0) 
                {
                    if (RootScrollViewer.PointerCaptures == null || !RootScrollViewer.PointerCaptures.Any(c => c.PointerId == e.Pointer.PointerId))
                    {
                        RootScrollViewer.CapturePointer(e.Pointer);
                    }

                    RootScrollViewer.ChangeView(null, RootScrollViewer.VerticalOffset + deltaY, null, true);
                    _lastMainPointerPos = ptr.Position;
                    e.Handled = true;
                }
            }
        }

        private void OnMainPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isMainDragging)
            {
                _isMainDragging = false;
                RootScrollViewer.ReleasePointerCapture(e.Pointer);
            }
        }

        // Horizontal (Cast) Drag Logic
        private void OnCastPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var ptr = e.GetCurrentPoint(null); // Use window coords for smoothness
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && ptr.Properties.IsLeftButtonPressed)
            {
                _isCastDragging = true;
                _lastCastPointerPos = ptr.Position;
                // [FIX] Don't capture yet! Wait for movement.
                // CastListView.CapturePointer(e.Pointer);
                
                // Abort main scroll to prevent simultaneous dragging
                _isMainDragging = false;
                // e.Handled = true; // Handled only after movement threshold
            }
        }

        private void OnCastPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isCastDragging)
            {
                var ptr = e.GetCurrentPoint(null); // Use window coords for smoothness

                // Safety check
                if (!ptr.Properties.IsLeftButtonPressed)
                {
                    _isCastDragging = false;
                    try { CastListView.ReleasePointerCapture(e.Pointer); } catch {}
                    return;
                }

                double deltaX = _lastCastPointerPos.X - ptr.Position.X;
                
                // [FIX] Threshold-based capture for cast scrolling too
                if (Math.Abs(deltaX) > 3.0)
                {
                    if (CastListView.PointerCaptures == null || !CastListView.PointerCaptures.Any(c => c.PointerId == e.Pointer.PointerId))
                    {
                        CastListView.CapturePointer(e.Pointer);
                    }

                    var scroll = GetScrollViewer(CastListView);
                    if (scroll != null)
                    {
                        scroll.ChangeView(scroll.HorizontalOffset + deltaX, null, null, true);
                        _lastCastPointerPos = ptr.Position;
                        e.Handled = true;
                    }
                }
            }
        }

        private void OnCastPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isCastDragging)
            {
                _isCastDragging = false;
                CastListView.ReleasePointerCapture(e.Pointer);
            }
        }

        private ScrollViewer GetScrollViewer(DependencyObject element)
        {
            if (element is ScrollViewer sv) return sv;
            int childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                var result = GetScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }
        #endregion
        private void CastItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                // Cancel any pending close
                _personCloseCts?.Cancel();
                _personCloseCts?.Dispose();
                _personCloseCts = null;

                var container = FindParent<ListViewItem>(element);
                if (container != null) Canvas.SetZIndex(container, 100);

                // Immediate visual feedback
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var scaleAnim = visual.Compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1.08f, 1.08f, 1.0f));
                scaleAnim.Duration = TimeSpan.FromMilliseconds(250);
                visual.StartAnimation("Scale", scaleAnim);

                // Start hover timer for person card (same pattern as ExpandedCardOverlayController)
                _pendingPersonSource = element;
                
                bool isCardVisible = ActivePersonCard.Visibility == Visibility.Visible;
                _personHoverTimer.Interval = TimeSpan.FromMilliseconds(isCardVisible ? 700 : 400);

                _personHoverTimer?.Stop();
                _personHoverTimer?.Start();
            }
        }

        private void CastItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                try
                {
                    var point = e.GetCurrentPoint(element).Position;
                    if (point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth && point.Y <= element.ActualHeight)
                    {
                        return;
                    }
                }
                catch { }

                var container = FindParent<ListViewItem>(element);
                if (container != null) Canvas.SetZIndex(container, 0);

                var visual = ElementCompositionPreview.GetElementVisual(element);
                var scaleAnim = visual.Compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1.0f, 1.0f, 1.0f));
                scaleAnim.Duration = TimeSpan.FromMilliseconds(200);
                visual.StartAnimation("Scale", scaleAnim);

                _personHoverTimer?.Stop();
                _ = ClosePersonCardAsync();
            }
        }

        private void CastItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            // Only trigger if the section is actually visible (prevents ghost triggers in narrow mode)
            bool isCastVisible = CastSection?.Visibility == Visibility.Visible;
            bool isDirectorVisible = DirectorSection?.Visibility == Visibility.Visible;
            bool isSectionVisible = isCastVisible || isDirectorVisible;

            if (isSectionVisible && sender is FrameworkElement element && element.DataContext is CastItem castItem)
            {
                e.Handled = true;
                _personHoverTimer?.Stop();
                _pendingPersonSource = element;
                System.Diagnostics.Debug.WriteLine($"[PersonCard] Opening for: {castItem.Name}");
                _ = ShowPersonCardAsync(castItem);
            }
        }

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (PersonCardOverlay.Visibility == Visibility.Visible && !_isPointerOverPersonCard)
            {
                ClosePersonCard();
            }
        }

        private bool IsPointerOverElement(FrameworkElement element, PointerRoutedEventArgs e)
        {
            if (element == null || element.Visibility != Visibility.Visible || element.XamlRoot == null) return false;
            try
            {
                var point = e.GetCurrentPoint(element).Position;
                return (point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth && point.Y <= element.ActualHeight);
            }
            catch { return false; }
        }

        private void ActivePersonCard_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPointerOverPersonCard = true;
            _personCloseCts?.Cancel();
            _personCloseCts?.Dispose();
            _personCloseCts = null;
        }

        private void ActivePersonCard_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPointerOverPersonCard = false;
            _ = ClosePersonCardAsync();
        }
    
        private void PersonHoverTimer_Tick(object sender, object e)
        {
            _personHoverTimer?.Stop();
            if (_pendingPersonSource != null && _pendingPersonSource.DataContext is CastItem castItem && PersonCardOverlay.XamlRoot != null)
            {
                _ = ShowPersonCardAsync(castItem);
            }
        }

        private async Task ShowPersonCardAsync(CastItem castItem)
        {
            if (PersonCardOverlay == null || ActivePersonCard == null || _pendingPersonSource == null) return;

            var sourceAtRequest = _pendingPersonSource;
            _activePersonAnchorSource = sourceAtRequest;
            _activePersonCardItem = castItem;

            var visual = ElementCompositionPreview.GetElementVisual(ActivePersonCard);
            bool isAlreadyVisible = ActivePersonCard.Visibility == Visibility.Visible && visual.Opacity > 0.1f;

            ActivePersonCard.LoadPersonAsync(castItem.Name, castItem.Character, castItem.FullProfileUrl,
                _unifiedMetadata?.ImdbId, _item?.TmdbInfo, (stream) => { ClosePersonCard(); Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(stream)); }, 
                _lastApplyPrimary);

            if (!isAlreadyVisible) ActivePersonCard.Opacity = 0;
            PersonCardOverlay.Visibility = Visibility.Visible;
            ActivePersonCard.Visibility = Visibility.Visible;
            await WaitForPersonCardLayoutAsync();
            if (_pendingPersonSource != sourceAtRequest || sourceAtRequest == null) return;

            PlacePersonCard(sourceAtRequest, animateMove: isAlreadyVisible);

            ActivePersonCard.Opacity = 1;
            if (!isAlreadyVisible && visual != null)
            {
                var compositor = visual.Compositor;
                try { visual.StopAnimation("Translation"); } catch { }
                try { visual.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero); } catch { }
                visual.Scale = new System.Numerics.Vector3(0.85f, 0.85f, 1f);
                visual.Opacity = 0f;

                var springAnim = compositor.CreateSpringVector3Animation();
                springAnim.Target = "Scale"; springAnim.FinalValue = System.Numerics.Vector3.One;
                springAnim.DampingRatio = 0.7f; springAnim.Period = TimeSpan.FromMilliseconds(50);

                var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
                fadeAnim.Target = "Opacity"; fadeAnim.InsertKeyFrame(1f, 1f);
                fadeAnim.Duration = TimeSpan.FromMilliseconds(150);

                visual.StartAnimation("Scale", springAnim);
                visual.StartAnimation("Opacity", fadeAnim);
            }
        }

        private async Task WaitForPersonCardLayoutAsync()
        {
            for (int i = 0; i < 3; i++)
            {
                TryUpdateLayout(PersonCardOverlay, nameof(WaitForPersonCardLayoutAsync));
                TryUpdateLayout(ActivePersonCard, nameof(WaitForPersonCardLayoutAsync));
                await Task.Yield();
            }
        }

        private void ActivePersonCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ActivePersonCard?.Visibility != Visibility.Visible || _activePersonAnchorSource == null) return;
            PlacePersonCard(_activePersonAnchorSource, animateMove: true);
        }

        private void PlacePersonCard(FrameworkElement anchor, bool animateMove)
        {
            if (PersonCardOverlay == null || ActivePersonCard == null || anchor == null) return;

            double overlayWidth = PersonCardOverlay.ActualWidth;
            double overlayHeight = PersonCardOverlay.ActualHeight;
            if ((overlayWidth <= 0 || overlayHeight <= 0) && PersonCardOverlay.XamlRoot != null)
            {
                overlayWidth = PersonCardOverlay.XamlRoot.Size.Width;
                overlayHeight = PersonCardOverlay.XamlRoot.Size.Height;
            }

            try
            {
                var transform = anchor.TransformToVisual(PersonCardOverlay);
                var position = transform.TransformPoint(new Point(0, 0));

                double cardWidth = ActivePersonCard.ActualWidth > 0 ? ActivePersonCard.ActualWidth : (ActivePersonCard.Width > 0 ? ActivePersonCard.Width : 420);
                double cardHeight = ActivePersonCard.ActualHeight > 0 ? ActivePersonCard.ActualHeight : (ActivePersonCard.Height > 0 ? ActivePersonCard.Height : 560);
                
                double targetX = position.X + (anchor.ActualWidth / 2) - (cardWidth / 2);
                double targetY = position.Y - cardHeight - 16;

                const double edgeMargin = 24.0;

                if (targetY < edgeMargin + 48) 
                {
                    targetY = position.Y + anchor.ActualHeight + 16;
                }

                // Horizontal safety check
                if (targetX < edgeMargin) targetX = edgeMargin;
                if (targetX + cardWidth > overlayWidth - edgeMargin) targetX = overlayWidth - cardWidth - edgeMargin;

                // Final vertical safety check
                if (targetY + cardHeight > overlayHeight - edgeMargin) targetY = overlayHeight - cardHeight - edgeMargin;
                if (targetY < edgeMargin + 20) targetY = edgeMargin + 20;

                double oldLeft = Canvas.GetLeft(ActivePersonCard);
                double oldTop = Canvas.GetTop(ActivePersonCard);
                if (double.IsNaN(oldLeft)) oldLeft = targetX;
                if (double.IsNaN(oldTop)) oldTop = targetY;

                Canvas.SetLeft(ActivePersonCard, targetX);
                Canvas.SetTop(ActivePersonCard, targetY);

                if (!animateMove) return;

                double deltaX = targetX - oldLeft;
                double deltaY = targetY - oldTop;
                if (Math.Abs(deltaX) <= 0.1 && Math.Abs(deltaY) <= 0.1) return;

                var visual = ElementCompositionPreview.GetElementVisual(ActivePersonCard);
                var compositor = visual.Compositor;
                visual.StopAnimation("Offset");
                // Set via initialTranslation parameter in StartTranslationAnimation

                var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
                offsetAnim.Target = "Translation";
                var cubic = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.33f, 1f), new System.Numerics.Vector2(0.67f, 1f));
                offsetAnim.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, cubic);
                offsetAnim.Duration = TimeSpan.FromMilliseconds(280);
                try { CompositionService.StartTranslationAnimation(ActivePersonCard, offsetAnim, new System.Numerics.Vector3((float)-deltaX, (float)-deltaY, 0)); } catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PersonCard] Placement failed: {ex.Message}");
            }
        }

        private async Task ClosePersonCardAsync(int delayMs = 600)
        {
            _personCloseCts?.Cancel();
            _personCloseCts?.Dispose();
            _personCloseCts = new CancellationTokenSource();
            var token = _personCloseCts.Token;

            try
            {
                await Task.Delay(delayMs, token);

                if (_isPointerOverPersonCard) return;
                
                ClosePersonCard();
            }
            catch (TaskCanceledException) { }
        }

        private void ClosePersonCard()
        {
            _personHoverTimer?.Stop(); 

            // Ensure anchor card's scale is reset
            if (_activePersonAnchorSource != null)
            {
                var anchorVisual = ElementCompositionPreview.GetElementVisual(_activePersonAnchorSource);
                if (anchorVisual != null)
                {
                    var resetScale = anchorVisual.Compositor.CreateVector3KeyFrameAnimation();
                    resetScale.InsertKeyFrame(1f, new System.Numerics.Vector3(1.0f, 1.0f, 1.0f));
                    resetScale.Duration = TimeSpan.FromMilliseconds(200);
                    anchorVisual.StartAnimation("Scale", resetScale);

                    var container = FindParent<ListViewItem>(_activePersonAnchorSource);
                    if (container != null) Canvas.SetZIndex(container, 0);
                }
            }

            _pendingPersonSource = null;
            _activePersonAnchorSource = null;
            _activePersonCardItem = null;

            // Immediately disable hit-testing so users can click/hover content behind
            PersonCardOverlay.IsHitTestVisible = false;
            
            var visual = ElementCompositionPreview.GetElementVisual(ActivePersonCard);
            if (visual != null && ActivePersonCard.Visibility == Visibility.Visible)
            {
                var compositor = visual.Compositor;
                
                var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                fadeOut.Target = "Opacity"; fadeOut.InsertKeyFrame(1f, 0f); fadeOut.Duration = TimeSpan.FromMilliseconds(200);
                
                var scaleDown = compositor.CreateVector3KeyFrameAnimation();
                scaleDown.Target = "Scale"; scaleDown.InsertKeyFrame(1f, new System.Numerics.Vector3(0.9f, 0.9f, 1.0f)); scaleDown.Duration = TimeSpan.FromMilliseconds(200);

                var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                visual.StartAnimation("Opacity", fadeOut);
                visual.StartAnimation("Scale", scaleDown);
                
                batch.Completed += (s, e) =>
                {
                    // The animation to 0 is done, now physically collapse
                    if (visual.Opacity < 0.1f)
                    {
                        ActivePersonCard.Visibility = Visibility.Collapsed;
                        PersonCardOverlay.Visibility = Visibility.Collapsed;
                        
                        // Ready for next show
                        PersonCardOverlay.IsHitTestVisible = true;
                    }
                };
                batch.End();
            }
            else
            {
                ActivePersonCard.Visibility = Visibility.Collapsed;
                PersonCardOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindParent<T>(parentObject);
        }

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

        private void UpdateInfoPanelVisibility(bool showEpisode)
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

        private void EnsureEpisodeTitleVisibleUnderLogo()
        {
            if (IdentityControl == null || _selectedEpisode == null) return;

            // SyncIdentityVisibility handles the title text and visibility logic based on Logo state
            SyncIdentityVisibility(true);
            IdentityControl.Visibility = Visibility.Visible;
            IdentityControl.Opacity = 1;

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


        private async Task HideSourcesPanelAsync()
        {
            if (_panelOwner == null) return;
            await _panelOwner.HideSourcesPanelAnimatedAsync();
        }

        private async Task ShowSourcesPanelAsync()
        {
            if (_panelOwner == null) return;
            await _panelOwner.ShowSourcesPanelAnimatedAsync();
        }

        private async void SourcesShowHandle_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await ShowSourcesPanelAsync();
        }

        private void SourcesShowHandle_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            _panelOwner?.ReassertUnsquashExpression();
        }

        private void SourcesShowHandle_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            _panelOwner?.HandleSourcesShowHandlePull(e.Delta.Translation.X);
        }

        private async void BtnHideSources_Click(object sender, RoutedEventArgs e)
        {
            if (ActualWidth < LayoutAdaptiveThreshold)
            {
                if (ResolveCurrentContentKind() == MediaContentKind.Series)
                {
                    OpenEpisodesPanel(PanelChangeReason.BackToEpisodes);
                }
                else
                {
                    CloseDetailPanel(PanelChangeReason.SourcesClosed);
                }
                return;
            }

            if (_panelOwner != null && !_panelOwner.IsSourcesPanelHidden)
            {
                await HideSourcesPanelAsync();
            }
        }

        private void BtnCloseSources_Click(object sender, RoutedEventArgs e)
        {
            DeselectEpisode();
        }

        private void BtnBackToEpisodes_Click(object sender, RoutedEventArgs e)
        {
            DeselectEpisode();
        }

        private async void SourcesShowHandle_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            if (_panelOwner == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(SourcesPanel);
            visual.Properties.TryGetVector3("Translation", out var currentTrans);

            if (currentTrans.X < 850)
            {
                await ShowSourcesPanelAsync();
            }
            else
            {
                await HideSourcesPanelAsync();
            }
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
    }
}

