using Microsoft.UI;
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
using ModernIPTVPlayer.Models; // Ensure Models namespace is included
using ModernIPTVPlayer.Services;
using System;
using MpvWinUI;
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
using System.Text.Json;
using MpvWinUI;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage : Page
    {
        private IMediaStream _item;
        private bool _isProgrammaticSelection;
        private System.Collections.ObjectModel.ObservableCollection<StremioAddonViewModel> _addonResults;
        private Compositor _compositor;
        private string _streamUrl;
        
        // Series Data
        public ObservableCollection<SeasonItem> Seasons { get; private set; } = new();
        public ObservableCollection<EpisodeItem> CurrentEpisodes { get; private set; } = new();
        public ObservableCollection<CastItem> CastList { get; private set; } = new();
        public ObservableCollection<CastItem> DirectorList { get; private set; } = new();

        private EpisodeItem _selectedEpisode;
        private SeasonItem _selectedSeason;
        private TmdbMovieResult _cachedTmdb;
        private SolidColorBrush _themeTintBrush;
        private bool _isInitializingSeriesUi;
        private readonly Dictionary<string, StremioSourcesCacheEntry> _stremioSourcesCache = new();
        private int _sourcesRequestVersion;
        private string _primaryColorHex = "#FF00BFA5"; // Default teal
        private string _currentStremioVideoId;
        private TaskCompletionSource<bool> _logoReadyTcs;
        private bool _isSourcesFetchInProgress;
        private bool _isCurrentSourcesComplete;
        private bool _areSourcesVisible = false; // <--- New Field
        private bool _shouldAutoResume = false;
        private string? _sourceAddonUrl; // New: tracking the source addon URL for the current stream
        private bool _isSourcesPanelHidden = false; // <--- New: Tracking hidden (stashed) state
        private Vector3 _sourcesPanelOriginalScale = new Vector3(1f, 1f, 1f);
        private double _sourcesPanelOriginalWidth = 0;
        
        private Models.Metadata.UnifiedMetadata _unifiedMetadata;
        private string _lastUsedTmdbLanguage;
        private string _prebufferUrl;
        private CancellationTokenSource _probeCts;
        private CancellationTokenSource _prebufferCts;

        // Trailer State
        private bool _isTrailerWebViewInitialized = false;
        private string _trailerFolder;
        private string _trailerVirtualHost = "trailers.moderniptv.local";
        private bool _isTrailerInitializing = false;
        private bool _isTrailerFullscreen = false;
        private int _trailerUiVersion = 0;
        private const double TrailerDefaultWidth = 1000;
        private const double TrailerDefaultHeight = 562;
        private CancellationTokenSource _trailerCts;

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

        // Page State Machine for Layout-Aware Loading
        private enum PageLoadState
        {
            Initial,      // Page created, no layout yet
            LayoutReady,  // First SizeChanged triggered
            Loading,     // Data loading (shimmer shown)
            Revealing,   // Data arrived, animation starting
            Ready        // Everything complete
        }
        private bool _isHandoffInProgress = false;
        private bool _isProcessingSizeChanged = false;
        private PageLoadState _pageLoadState = PageLoadState.Initial;
        private IMediaStream _pendingLoadItem;  // Item waiting for layout

        // Composition Logo System
        private SpriteVisual _logoVisual;
        private CompositionSurfaceBrush _logoBrush;
        private LoadedImageSurface _logoSurface;
        private string _currentLogoUrl;

        // Ambience State Machine
        private enum AmbienceState { None, Provisional, Stable }
        // Moved _ambienceState to ambience group below
        private Dictionary<string, string> _urlToSignatureCache = new Dictionary<string, string>();
        private readonly SemaphoreSlim _ambienceLock = new SemaphoreSlim(1, 1);

        // Mouse Drag-to-Scroll State
        private bool _isMainDragging = false;
        private Windows.Foundation.Point _lastMainPointerPos;
        private bool _isCastDragging = false;
        private Windows.Foundation.Point _lastCastPointerPos;
        
        // Resize Optimization
        private int _isWideModeIndex = -1; // -1: Unknown, 0: Narrow, 1: Wide
        private Color _lastApplyPrimary;
        private Color _lastApplyArea;
        private double _lastReportedWidth;
        private double _lastReportedHeight;
        private double _lastAppliedWidth;
        private double _lastAppliedHeight;
        // Layout Perfection Constants
        private const double LayoutAdaptiveThreshold = 800.0;
        private const double ContentGridPaddingBottom = 30.0;
        private const double InfoInnerMarginBottom = 25.0;
        private const double LayoutBufferBottom = 5.0;
        private const double TotalBottomGap = ContentGridPaddingBottom + InfoInnerMarginBottom + LayoutBufferBottom;

        private SpringVector3NaturalMotionAnimation _driftAnimation;

        public MediaInfoPage()
        {
            this.InitializeComponent();
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            SetupRealTimeComposition();

            // UI Audio Feedback Setup
            this.ElementSoundMode = global::Microsoft.UI.Xaml.ElementSoundMode.Off;
            BackButton.ElementSoundMode = global::Microsoft.UI.Xaml.ElementSoundMode.Default;
            
            // Manual Layout Management
            this.SizeChanged += MediaInfoPage_SizeChanged;
            
            // Root height is now handled by XAML Stretch alignments

            System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Constructor completed.");
            this.NavigationCacheMode = NavigationCacheMode.Disabled;
            SetupProfessionalAnimations();
            SetupActionBarDynamics();
            SetupStabilityComposition();

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

            // Project Zero: Mandatory Cleanup Registration
            this.Unloaded += (s, e) => Cleanup();
        }

        /// <summary>
        /// Project Zero: Major resource reclamation. 
        /// Explicitly breaks reference chains and interop links that often cause memory leaks in WinUI 3.
        /// </summary>
        public void Cleanup()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[Cleanup] MediaInfoPage releasing resources for: {_item?.Title ?? "None"}");

                // 1. Kill Heavy Engines
                if (TrailerWebView != null)
                {
                    try { TrailerWebView.Source = new Uri("about:blank"); } catch { }
                    TrailerWebView.Visibility = Visibility.Collapsed;
                }
                
                _slideshowTimer?.Stop();
                _slideshowTimer = null;

                // 2. Clear Managed Collections (Frees strings and model objects)
                Seasons?.Clear();
                CurrentEpisodes?.Clear();
                CastList?.Clear();
                DirectorList?.Clear();
                _stremioSourcesCache?.Clear();
                _addonResults?.Clear();
                _backdropUrls?.Clear();
                _validatedBackdrops?.Clear();

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
                            Debug.WriteLine("[Cleanup] MediaInfoPage: Handoff in progress. Preserving player instance.");
                        }
                        else 
                        {
                            var pToCleanup = MediaInfoPlayer;
                            _ = Task.Run(async () => {
                                try { await pToCleanup.CleanupAsync(); } catch { }
                            });
                            Debug.WriteLine("[Cleanup] MediaInfoPage: CleanupAsync started.");
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
                if (HeroImage != null) HeroImage.Source = null;
                if (HeroImage != null) HeroImage.Opacity = 0; // Visual reset for next item
                if (ContentLogoHost != null) ElementCompositionPreview.SetElementChildVisual(ContentLogoHost, null);
                
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
                System.Diagnostics.Debug.WriteLine($"[Cleanup] MediaInfoPage error: {ex.Message}");
            }
        }

        private bool _isSelectionSyncing = false;
        private int _loadingVersion = 0; // New field
        private long _historyChangedToken; // New field
        private DispatcherTimer _slideshowTimer;
        private string _slideshowId;
        private List<string> _backdropUrls = new List<string>();
        private int _currentBackdropIndex = 0;
        private bool _isHeroImage1Active = true;
        private UIElement _lastKenBurnsElement;
        private HashSet<string> _backdropKeys = new HashSet<string>();
        private string _lastVisualSignature;
        private List<BackdropEntry> _validatedBackdrops = new List<BackdropEntry>();
        private System.Threading.SemaphoreSlim _validationLock = new System.Threading.SemaphoreSlim(1, 1);
        private bool _isFirstImageApplied = false;
        private bool _isHeroTransitionInProgress = false;
        private AmbienceState _ambienceState = AmbienceState.None;
        private string _lastAmbienceUrl = null; 
        private string _lastAmbienceSignature = null;
        private int _ambienceNavigationEpoch;

        private class BackdropEntry
        {
            public string Url { get; set; }
            public string Signature { get; set; }
            public int Area { get; set; }
        }

        private void MediaInfoPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isProcessingSizeChanged) return;
            _isProcessingSizeChanged = true;
            try
            {
                _lastReportedWidth = e.NewSize.Width;
                _lastReportedHeight = e.NewSize.Height;

            bool isWide = _lastReportedWidth >= 800;
            int newState = isWide ? 1 : 0;

            // State Machine: Initial → LayoutReady (first SizeChanged)
            if (_pageLoadState == PageLoadState.Initial)
            {
                _pageLoadState = PageLoadState.LayoutReady;
                System.Diagnostics.Debug.WriteLine($"[MediaInfo-Layout] Initial layout captured. Size: {e.NewSize.Width:F0}x{e.NewSize.Height:F0}");
                
                // Check if there's a pending item waiting for layout
                if (_pendingLoadItem != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MediaInfo-Layout] Resuming pending load for: {_pendingLoadItem.Title}");
                    var pendingItem = _pendingLoadItem;
                    _pendingLoadItem = null;
                    // Call LoadDetailsAsync to continue the flow
                    _ = LoadDetailsAsync(pendingItem);
                    return;
                }
            }

            if (_isWideModeIndex != newState)
            {
                _isWideModeIndex = newState;
                UpdateLayoutState(isWide);

                // [FIX] Re-apply ambience on layout change to ensure readability gradients are synced
                // This resolves the issue where scrims disappear during Narrow -> Wide transition.
                if (_lastApplyPrimary.A > 0 || _lastApplyArea.A > 0)
                {
                    ApplyPremiumAmbience(_lastApplyPrimary, _lastApplyArea, "layout-refresh");
                }
            }

            // Minimal Fix: InfoContainerInner log
            System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-SizeChanged] InfoContainerInner: VA={InfoContainerInner?.VerticalAlignment}, AH={InfoContainerInner?.ActualHeight}, Margin={InfoContainerInner?.Margin}");

            ApplyImmediateLayoutSync(_lastReportedHeight, isWide);
            EnsureTrailerOverlayBounds();
            if (TrailerOverlay?.Visibility == Visibility.Visible) ApplyTrailerFullscreenLayout(_isTrailerFullscreen);
            // [STABILITY FIX] Removed redundant RowDefinitions assignments. 
            // These are strictly managed by XAML VisualStates now.

            // 2. KESİN ÇÖZÜM: Ölçekleme işlemlerini render kuyruğunun sonuna it.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (this == null || InfoColumn == null) return;
                UpdateRealTimeScaling(_lastReportedWidth, _lastReportedHeight, isWide);
                UpdateActionBarRealTime(_lastReportedWidth, _lastReportedHeight, isWide);
            });

            _lastAppliedWidth = _lastReportedWidth;
            _lastAppliedHeight = _lastReportedHeight;
            }
            finally
            {
                _isProcessingSizeChanged = false;
            }
        }

        private void ApplyImmediateLayoutSync(double height, bool isWide)
        {
            // [ROOT FIX] Remove manual height assignments. 
            // XAML Stretch and Bottom alignments now handle this naturally.
            if (ContentGrid != null)
            {
                if (!isWide)
                {
                    // If it's a series, EpisodesPanel should be considered visible for layout purposes,
                    // even if its actual Visibility property is not yet set to Visible during loading.
                    bool isSeries = IsSeriesItem();
                    bool areEpisodesVisible = isSeries || (EpisodesPanel != null && EpisodesPanel.Visibility == Visibility.Visible);
                    bool areAnyPanelsVisible = _areSourcesVisible || areEpisodesVisible;

                    if (areAnyPanelsVisible)
                    {
                        // Lock layout bounds so inner lists (Sources or Episodes) scroll internally
                        if (RootScrollViewer != null) RootScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
                        
                        // [STABLE] Grid Row/Column definitions removed from C#. 
                        // Managed by VisualStateManager in XAML to prevent cycles.
                    }
                    else
                    {
                        // Allow full page scrolling for Movie Info + Cast
                        if (!double.IsNaN(ContentGrid.Height)) ContentGrid.Height = double.NaN;
                        if (ContentGrid.MaxHeight != double.PositiveInfinity) ContentGrid.MaxHeight = double.PositiveInfinity;
                        if (RootScrollViewer != null) RootScrollViewer.VerticalScrollMode = ScrollMode.Auto;
                    }
                }
                else
                {
                    // [RESET WIDE SYNC] - CRITICAL: Reset height to NaN to prevent "centered" locked layout
                    if (ContentGrid != null)
                    {
                        if (!double.IsNaN(ContentGrid.Height)) ContentGrid.Height = double.NaN;
                        if (ContentGrid.MaxHeight != double.PositiveInfinity) ContentGrid.MaxHeight = double.PositiveInfinity;
                        if (RootScrollViewer != null)
                        {
                            RootScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
                            RootScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        }
                    }
                }
            }
        }

        private void UpdateRealTimeScaling(double width, double height, bool isWide)
        {
            try
            {
                if (InfoColumn == null) return;
                bool isSeries = IsSeriesItem();
                
                // 1. Unified Scaling Factor
                double baseWidth = isWide ? 1400 : 800;
                double baseHeight = isWide ? 950 : 700;
                
                double widthFactor = width / baseWidth;
                double heightFactor = height / baseHeight;
                double rawScale = isWide ? Math.Min(widthFactor, heightFactor) : widthFactor;

                if (isWide && width < 1100)
                {
                    double squeeze = (width - 800) / 300.0;
                    rawScale *= (0.85 + (squeeze * 0.15));
                }

                double globalScale = isWide 
                    ? Math.Clamp(rawScale, 0.75, 1.25) 
                    : Math.Clamp(rawScale, 0.65, 1.0); 

                var visual = ElementCompositionPreview.GetElementVisual(InfoColumn);
                
                // [MODERN EXPRESSION SYNC]
                // Instead of calculating pivot in C#, we bind visual.CenterPoint to its own Size property.
                // This ensures the pivot point updates INSTANTLY as the layout engine resizes the element.
                if (_compositor != null)
                {
                    var pivotExpression = _compositor.CreateExpressionAnimation();
                    if (isWide)
                    {
                        // WIDE: Pivot at Bottom-Left (0, Height, 0)
                        pivotExpression.Expression = "Vector3(0, this.Target.Size.Y, 0)";
                    }
                    else
                    {
                        // NARROW: Pivot at Top-Center (Width/2, 0, 0)
                        pivotExpression.Expression = "Vector3(this.Target.Size.X * 0.5f, 0, 0)";
                    }
                    visual.StartAnimation("CenterPoint", pivotExpression);
                }

                // We still need currentHeight/Width to calculate globalScale bounds if needed, 
                // but we no longer use them for the pivot calculation.
                double currentHeight = InfoColumn.DesiredSize.Height > 0 
                    ? InfoColumn.DesiredSize.Height 
                    : (InfoColumn.ActualHeight > 0 ? InfoColumn.ActualHeight : 600);
                
                visual.Scale = new System.Numerics.Vector3((float)globalScale, (float)globalScale, 1.0f);
                double verticalMargin = isWide ? TotalBottomGap : 25; 


                // 3. Margin refinement - [PURE COMPOSITION]
                // We NO LONGER set InfoColumn.Margin here to avoid layout re-entrancy.
                // Static margins are set in XAML (TotalGap = Padding 30 + InnerMargin 25).

                // [MODERN UNIFIED SYSTEM] - Single calculation for all offsets and opacities
                float targetX = 0f;
                float targetY = 0f;

                if (isWide)
                {
                    // Wide Mode: Stable horizontal position (center-point anchored)
                    targetX = 0f; 
                    
                    double availableHeight = height - verticalMargin;
                    targetY = 0f; 
                    
                    // Root Fix for "Cropped Cast": 
                    // If targetY is negative, it moves the VISUAL up. 
                    // However, if the pivot is at the bottom, the bottom is already at availableHeight.
                    // So targetY=0 is perfect for anchoring to the bottom.
                    // Only use negative targetY if we want to "reveal" the top of a very tall column.
                    // Actually, let's keep it simple: targetY = 0 for bottom-anchoring.
                    targetY = 0f;

                    // Side Panels stabilization - Use XAML Row/Col
                    float sourcesTargetX = _isSourcesPanelHidden ? 1000f : 0f;
                    if (SourcesPanel != null) 
                    {
                        var sVisual = ElementCompositionPreview.GetElementVisual(SourcesPanel);
                        sVisual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(sourcesTargetX, 0, 0));
                    }
                    if (EpisodesPanel != null) 
                    {
                        var eVisual = ElementCompositionPreview.GetElementVisual(EpisodesPanel);
                        eVisual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 0));
                    }
                    if (SourcesShimmerPanel != null) 
                    {
                        var ssVisual = ElementCompositionPreview.GetElementVisual(SourcesShimmerPanel);
                        ssVisual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(sourcesTargetX, 0, 0));
                    }
                }
                else
                {
                    // [JITTER FIX] Removed manual Width assignment.
                    // InfoColumn.Width = Math.Min(800, width - 40); <--- REMOVED: Triggers layout cycle in Auto rows.
                    // MaxWidth is already handled in XAML VisualStates.

                // [GAP FIX] Calculate how much we shrunken by, to pull up the bottom panels.
                    // This closes the gap created by the Top-anchored scaling.
                    float pullUp = (float)(-(currentHeight * (1.0 - globalScale)));
                    System.Diagnostics.Debug.WriteLine($"[LayoutLog] Scaling InfoColumn: scale={globalScale:F2}, currentHeight={currentHeight}, pullUp={pullUp}");

                    // Translation-based stabilization (Does not trigger layout pass)
                    // We apply the same pullUp to all panels that follow InfoColumn in narrow mode
                    float sourcesNarrowTargetX = _isSourcesPanelHidden ? 1000f : 0f;
                    if (SourcesPanel != null) 
                    { 
                        var sVisual = ElementCompositionPreview.GetElementVisual(SourcesPanel);
                        sVisual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(sourcesNarrowTargetX, pullUp, 0));
                        System.Diagnostics.Debug.WriteLine($"[LayoutLog] SourcesPanel Translate: {pullUp:F2} (HiddenX: {sourcesNarrowTargetX})");
                    }

                    if (EpisodesPanel != null) 
                    { 
                        var eVisual = ElementCompositionPreview.GetElementVisual(EpisodesPanel);
                        eVisual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, pullUp, 0));
                        System.Diagnostics.Debug.WriteLine($"[LayoutLog] EpisodesPanel Translate: {pullUp:F2}");
                    }

                    if (SourcesShimmerPanel != null)
                    {
                        var ssVisual = ElementCompositionPreview.GetElementVisual(SourcesShimmerPanel);
                        ssVisual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(sourcesNarrowTargetX, pullUp, 0));
                    }
                    
                    if (NarrowSectionsContainer != null)
                    {
                        var nsVisual = ElementCompositionPreview.GetElementVisual(NarrowSectionsContainer);
                        nsVisual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, pullUp, 0));
                    }

                    // [FIX] Don't fill the screen if sources aren't actually showing content
                    if (SourcesPanel != null) 
                    { 
                        SourcesPanel.Height = double.NaN; 
                        SourcesPanel.VerticalAlignment = VerticalAlignment.Top; 
                    }
                    if (EpisodesPanel != null) 
                    { 
                        EpisodesPanel.Height = double.NaN; 
                        EpisodesPanel.VerticalAlignment = VerticalAlignment.Top; 
                    }
                }

                // [FINAL TRANSLATION SYNC] - Use Properties.InsertVector3 for facade access
                visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(targetX, targetY, 0));
                
                if (!isWide)
                {
                    System.Diagnostics.Debug.WriteLine($"[LayoutLog] Narrow Scaling Sync: InfoColumn.AW={InfoColumn?.ActualWidth}, targetX={targetX}, targetY={targetY}, HA={InfoColumn?.HorizontalAlignment}");
                }

                // 4. Panel Opacities & Micro-Layouts
                if (_compositor != null)
                {
                    // [MODERN UNIFIED OPACITY]
                    // In Narrow mode (isWide=false), both the unified panels and info sections should be fully visible.
                    // In Wide mode, we fade the side panels as we approach the adaptive threshold.
                    float panelOpacity = isWide ? (float)Math.Clamp((width - 800) / 100.0, 0, 1.0) : 1.0f;
                    float narrowSectionsOpacity = isWide ? 0f : 1.0f;

                    if (EpisodesPanel != null) ElementCompositionPreview.GetElementVisual(EpisodesPanel).Opacity = panelOpacity;
                    if (SourcesPanel != null) ElementCompositionPreview.GetElementVisual(SourcesPanel).Opacity = panelOpacity;
                    
                    UpdateActionBarRealTime(width, height, isWide);
                }
            }
            catch { /* Silent fail for performance */ }
        }


        private void SetupActionBarDynamics()
        {
            if (ActionBarPanel == null || _compositor == null) return;

            // 1. Implicit Offset Animations (For smooth wrapping)
            var elementImplicitAnimation = _compositor.CreateImplicitAnimationCollection();
            var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Target = "Offset";
            
            var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0.3f), new Vector2(0.0f, 1.0f));
            offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(400);
            
            elementImplicitAnimation["Offset"] = offsetAnimation;

            // Apply to all children of ActionBarPanel
            foreach (var child in ActionBarPanel.Children)
            {
                if (child is FrameworkElement fe)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(fe);
                    visual.ImplicitAnimations = elementImplicitAnimation;
                    
                    // Enable translation for scaling math
                    ElementCompositionPreview.SetIsTranslationEnabled(fe, true);

                    // Add pointer events for high-fidelity hover (prevents "stuck" states)
                    if (fe is Button btn)
                    {
                        btn.PointerEntered -= OnActionButtonPointerEntered;
                        btn.PointerEntered += OnActionButtonPointerEntered;
                        btn.PointerExited -= OnActionButtonPointerExited;
                        btn.PointerExited += OnActionButtonPointerExited;

                    // NEW: Robust Cleanup for stuck states
                        btn.PointerCanceled -= OnActionButtonPointerExited;
                        btn.PointerCanceled += OnActionButtonPointerExited;
                        btn.PointerCaptureLost -= OnActionButtonPointerExited;
                        btn.PointerCaptureLost += OnActionButtonPointerExited;

                        // [EVENT-DRIVEN LAYOUT] Trigger recalc when text changes (e.g. Play -> Resume)
                        btn.SizeChanged -= OnActionButtonSizeChanged;
                        btn.SizeChanged += OnActionButtonSizeChanged;
                    }
                }
            }

            // [ROOT FIX] Final layout pass once the container is fully loaded 
            // Ensures correct dimensions in fullscreen/initial entry.
            ActionBarPanel.Loaded += (s, e) => {
                DispatcherQueue.TryEnqueue(() => UpdateActionBarRealTime(ActualWidth, ActualHeight, ActualWidth >= LayoutAdaptiveThreshold));
            };
        }

        private void SetupStabilityComposition()
        {
            if (_compositor == null) return;

            // 1. Translation Facades
            var targets = new FrameworkElement[] { 
                InfoColumn, InfoContainer, SourcesPanel, EpisodesPanel, 
                SourcesShimmerPanel, NarrowSectionsContainer, IdentityContainer,
                TitlePanel, MetadataRibbon, ActionBarPanel,
                DirectorSection, CastSection, OverviewPanel, TechBadgesContent, 
                MetadataPanel, MetadataSeparator, LocalInfoGradient, ExtraReadabilityGradient
            };

            foreach (var element in targets)
            {
                if (element != null)
                {
                    ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                }
            }

            // 2. Pro Logo Visual Setup (Flicker-Free Architecture)
            if (ContentLogoHost != null)
            {
                _logoVisual = _compositor.CreateSpriteVisual();
                _logoBrush = _compositor.CreateSurfaceBrush();
                _logoBrush.Stretch = CompositionStretch.Uniform;
                _logoVisual.Brush = _logoBrush;
                ElementCompositionPreview.SetElementChildVisual(ContentLogoHost, _logoVisual);

                ContentLogoHost.SizeChanged += (s, e) =>
                {
                    if (_logoVisual != null)
                    {
                        var size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
                        _logoVisual.Size = size;
                        _logoVisual.CenterPoint = new Vector3(size.X / 2, size.Y / 2, 0);
                    }
                };
            }
        }

        private HashSet<Button> _hoveredActionButtons = new HashSet<Button>();
        private float _currentActionBarBaseScale = 1.0f;

        private void OnActionButtonPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                _hoveredActionButtons.Add(btn);
                UpdateActionBarRealTime(_lastReportedWidth, _lastReportedHeight, _lastReportedWidth >= 800);
            }
        }

        private void OnActionButtonPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                _hoveredActionButtons.Remove(btn);
                UpdateActionBarRealTime(_lastReportedWidth, _lastReportedHeight, _lastReportedWidth >= 800);
            }
        }

        private void OnActionButtonSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateActionBarRealTime(_lastReportedWidth, _lastReportedHeight, _lastReportedWidth >= 800);
        }

        private void UpdateActionBarRealTime(double width, double height, bool isWide)
        {
            if (ActionBarPanel == null || InfoColumn == null) return;

            // 1. Label Morphing Factor
            float labelOpacity = isWide ? (float)Math.Clamp((width - 950) / 250.0, 0.0, 1.0) : 0f;
            
            // 2. DYNAMIC SPACING: Extra gap in narrow mode to prevent hit-test collisions
            float spacing = isWide ? 16f : 24f; 
            
            // 3. Layout Bounds (UNSCALED coordinate space)
            double unscaledMaxWidth = width - 40;
            if (unscaledMaxWidth < 300) unscaledMaxWidth = 400;

            // 4. Wrapping Logic
            // Ensure all buttons have consistent alignment (set once)
            // Manual overrides removed to prevent layout jitter.
            List<List<Button>> rows = new List<List<Button>>();
            List<Button> currentRow = new List<Button>();
            double currentRowWidth = 0;

            foreach (var child in ActionBarPanel.Children)
            {
                if (child is Button btn && btn.Visibility == Visibility.Visible)
                {
                    // [EVENT-DRIVEN] Prefer ActualWidth if available for high accuracy
                    double btnWidth = btn.ActualWidth > 0 ? btn.ActualWidth : btn.DesiredSize.Width;
                    
                    if (btnWidth <= 0)
                    {
                        // Fallback estimation (emergency only)
                        if (btn == PlayButton)
                        {
                            bool hasSubtext = PlayButtonSubtext != null && !string.IsNullOrEmpty(PlayButtonSubtext.Text) && PlayButtonSubtext.Visibility == Visibility.Visible;
                            btnWidth = (labelOpacity > 0.5f || isWide) ? (hasSubtext ? 200 : 160) : 52;
                        }
                        else if (btn == RestartButton) btnWidth = 140;
                        else btnWidth = 52;
                    }
                    
                    if (currentRowWidth + btnWidth > unscaledMaxWidth && currentRow.Count > 0)
                    {
                        rows.Add(currentRow);
                        currentRow = new List<Button>();
                        currentRowWidth = 0;
                    }
                    currentRow.Add(btn);
                    currentRowWidth += btnWidth + spacing;
                }
            }
            if (currentRow.Count > 0) rows.Add(currentRow);

            double currentY = 0;
            double rowHeight = 52;

            foreach (var row in rows)
            {
                double rowTotalWidth = 0;
                foreach (var btn in row)
                {
                    double btnWidth = btn.DesiredSize.Width;
                    if (btnWidth <= 0)
                    {
                        // Fallback estimation
                        if (btn == PlayButton)
                        {
                            bool hasSubtext = PlayButtonSubtext != null && !string.IsNullOrEmpty(PlayButtonSubtext.Text) && PlayButtonSubtext.Visibility == Visibility.Visible;
                            btnWidth = (labelOpacity > 0.5f || isWide) ? (hasSubtext ? 200 : 160) : 52;
                        }
                        else if (btn == RestartButton) btnWidth = 140;
                        else btnWidth = 52;
                    }
                    rowTotalWidth += btnWidth + spacing;
                }
                rowTotalWidth -= spacing;

                // PERFECT CENTERING in unscaled space
                double rowOffsetX;
                if (isWide)
                {
                    rowOffsetX = 0;
                }
                else
                {
                    // Use InfoColumn's raw internal width for centering, NOT the screen width
                    double availableWidth = InfoColumn.ActualWidth;
                    if (availableWidth <= 0) availableWidth = unscaledMaxWidth;

                    // Precise middle-button centering for Narrow mode
                    if (row.Count % 2 != 0)
                    {
                        // Odd number of buttons: center the middle button exactly relative to parent
                        int middleIndex = row.Count / 2;
                        double widthBeforeMiddle = 0;
                        for (int i = 0; i < middleIndex; i++)
                        {
                            double btnW = row[i].DesiredSize.Width;
                            if (btnW <= 0) btnW = 52; // Fallback
                            widthBeforeMiddle += btnW + spacing;
                        }
                        double middleBtnWidth = row[middleIndex].DesiredSize.Width;
                        if (middleBtnWidth <= 0) middleBtnWidth = 52; // Fallback
                        
                        // Center of parent (availableWidth / 2) should correspond to the button's center
                        rowOffsetX = (availableWidth / 2.0) - (widthBeforeMiddle + middleBtnWidth / 2.0);
                    }
                    else
                    {
                        // Even number of buttons: standard row centering
                        rowOffsetX = (availableWidth - rowTotalWidth) / 2.0;
                    }
                }

                double currentX = rowOffsetX;

                foreach (var btn in row)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(btn);
                    
                    // a) Label Morphing (Opacity-only for layout stability)
                    if (btn == PlayButton && PlayButtonTextStack != null)
                    {
                        var textVisual = ElementCompositionPreview.GetElementVisual(PlayButtonTextStack);
                        textVisual.Opacity = labelOpacity;
                        
                        // [DYNAMIC SIZING] Force collapse text stack in Narrow mode (labelOpacity=0)
                        // This allows the button to shrink to a circle.
                        if (labelOpacity < 0.1f)
                        {
                            if (PlayButtonTextStack.Visibility != Visibility.Collapsed) PlayButtonTextStack.Visibility = Visibility.Collapsed;
                            btn.Width = 52;
                            btn.Padding = new Thickness(0);
                        }
                        else
                        {
                            if (PlayButtonTextStack.Visibility != Visibility.Visible) PlayButtonTextStack.Visibility = Visibility.Visible;
                            btn.Width = double.NaN;
                            btn.Padding = new Thickness(32, 0, 32, 0);
                        }
                    }

                    // b) Visual Transform (Relative to parent)
                    float hoverScale = _hoveredActionButtons.Contains(btn) ? 1.05f : 1.0f;
                    
                    double btnWidth = btn.DesiredSize.Width;
                    if (btnWidth <= 0)
                    {
                        if (btn == PlayButton)
                        {
                            bool hasSubtext = PlayButtonSubtext != null && !string.IsNullOrEmpty(PlayButtonSubtext.Text) && PlayButtonSubtext.Visibility == Visibility.Visible;
                            btnWidth = (labelOpacity > 0.5f || isWide) ? (hasSubtext ? 200 : 160) : 52;
                        }
                        else if (btn == RestartButton) btnWidth = 140;
                        else btnWidth = 52;
                    }

                    float unscaledWidth = (float)btnWidth;
                    visual.CenterPoint = new Vector3(unscaledWidth / 2.0f, 26f, 0); 
                    visual.Scale = new Vector3(hoverScale, hoverScale, 1.0f);
                    visual.Offset = new Vector3((float)currentX, (float)currentY, 0);
                    visual.Opacity = 1.0f; // Ensure visible if in a row

                    currentX += unscaledWidth + spacing;
                }
                currentY += rowHeight + spacing;
            }

            // [FIX] Reset visuals for buttons NOT in any row to prevent stale offsets from previous pages
            var buttonsInLayout = rows.SelectMany(r => r).ToHashSet();
            foreach (var child in ActionBarPanel.Children)
            {
                if (child is Button btn && !buttonsInLayout.Contains(btn))
                {
                    var visual = ElementCompositionPreview.GetElementVisual(btn);
                    visual.Offset = new Vector3(0, 0, 0);
                    visual.Opacity = 0.0f;
                }
            }
        }
        private void UpdateLabelStability(Button btn, TextBlock txt, float visible, double padding)
        {
            // Legacy - Logic moved to UpdateActionBarRealTime
        }

        private void ApplySpringDrift(Visual visual, double width, bool isWide)
        {
            if (!isWide || _compositor == null) return;
            float driftX = (width < 1400) ? (float)((1400 - width) * 0.05) : 0f;
            driftX = Math.Min(driftX, 30f); 

            if (_driftAnimation == null)
            {
                _driftAnimation = _compositor.CreateSpringVector3Animation();
                _driftAnimation.Target = "Translation";
                _driftAnimation.DampingRatio = 0.65f; 
                _driftAnimation.Period = TimeSpan.FromMilliseconds(40); // Faster reaction
            }

            _driftAnimation.FinalValue = new Vector3(driftX, 0, 0); // Horizontal only
            visual.StartAnimation("Translation", _driftAnimation);
        }

        private void ApplyCastScaling(double width)
        {
            // Cast Section Scaling (Aggressive shrink for context)
            double castFactor = (width < 1100) ? Math.Clamp(width / 1100.0, 0.75, 1.0) : 1.0;
            
            if (DirectorSectionScale != null) { DirectorSectionScale.ScaleX = castFactor; DirectorSectionScale.ScaleY = castFactor; }
            if (CastSectionScale != null) { CastSectionScale.ScaleX = castFactor; CastSectionScale.ScaleY = castFactor; }
        }
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
                double topPad = Math.Max(12, (overlayHeight - height) / 2.0 - 48);
                double rightPad = Math.Max(12, (overlayWidth - width) / 2.0 - 4);
                CloseTrailerButton.Margin = new Thickness(0, topPad, rightPad, 0);
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
            // Force restore visibility of key UI elements in case cached page has them hidden
            try
            {
                if (RootScrollViewer != null) RootScrollViewer.Visibility = Visibility.Visible;
                
                // Restore buttons
                if (PlayButton != null) PlayButton.Visibility = Visibility.Visible;
                if (TrailerButton != null) TrailerButton.Visibility = Visibility.Visible;
                if (DownloadButton != null) DownloadButton.Visibility = Visibility.Visible;
                if (CopyLinkButton != null) CopyLinkButton.Visibility = Visibility.Visible;
                
                // Restore panels (sources should be hidden initially)
                if (SourcesPanel != null) SourcesPanel.Visibility = Visibility.Collapsed;

                if (EpisodesPanel != null) EpisodesPanel.Visibility = Visibility.Collapsed;

                
                System.Diagnostics.Debug.WriteLine("[MediaInfoPage] UI Visibility restored.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] RestoreUIVisibility Error: {ex.Message}");
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

        private void UpdateLayoutState(bool isWide)
        {
            try
            {
                if (_item == null) return; // Data not loaded yet
                if (LayoutRoot == null) return;
                
                bool isSeries = IsSeriesItem();
                bool hasSources = _addonResults != null && _addonResults.Any(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0);
                bool isSourceRequired = _areSourcesVisible || (!isSeries && (_pageLoadState == PageLoadState.Loading || _isSourcesFetchInProgress));
                bool isEpisodeRequired = isSeries && !isSourceRequired; // Only show episodes if sources are not required and it's a series

                System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-UpdateLayout] START: isWide={isWide}, _item={_item?.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-UpdateLayout] ScrollViewer: AH={RootScrollViewer?.ActualHeight}, VHeight={RootScrollViewer?.ViewportHeight}, ScrollMode={RootScrollViewer?.VerticalScrollMode}");
                System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-UpdateLayout] ContentGrid: H={ContentGrid?.Height}, MaxH={ContentGrid?.MaxHeight}, AH={ContentGrid?.ActualHeight}, VA={ContentGrid?.VerticalAlignment}");
                System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-UpdateLayout] InfoContainer: VA={InfoContainer?.VerticalAlignment}, AH={InfoContainer?.ActualHeight}");

                System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 2: UpdateLayoutState ENTER. Wide: {isWide}, Series: {isSeries}, ItemType: {_item?.GetType().Name}");
                
                var targetCastVis = (CastList != null && CastList.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
                var targetDirVis = (DirectorList != null && DirectorList.Count > 0) ? Visibility.Visible : Visibility.Collapsed;

                if (isWide)
                {
                    System.Diagnostics.Debug.WriteLine("[LayoutLog] Switching to WIDE mode");
                    // WIDE MODE - AGGRESSIVE LOCK
                    if (RootScrollViewer != null)
                    {
                        if (RootScrollViewer.VerticalScrollMode != ScrollMode.Disabled) RootScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
                        if (RootScrollViewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled) RootScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        if (RootScrollViewer.IsVerticalScrollChainingEnabled != false) RootScrollViewer.IsVerticalScrollChainingEnabled = false;
                    }

                    // [STRICT WIDE ROW RESET] - Overrides Narrow Auto-rows
                    if (Row0 != null) Row0.Height = new GridLength(1, GridUnitType.Star);
                    if (Row1 != null) Row1.Height = new GridLength(0);
                    if (Row2 != null) Row2.Height = new GridLength(0);
                    if (Row3 != null) Row3.Height = new GridLength(0);


                    if (isSourceRequired)
                    {
                        System.Diagnostics.Debug.WriteLine("[LayoutLog] WIDE: Sources view active");
                        
                        // [STRICT] Parent panel must be VISIBLE to show internal shimmer or content
                        if (SourcesPanel != null)
                        {
                            if (SourcesPanel.Visibility != Visibility.Visible) SourcesPanel.Visibility = Visibility.Visible;
                            SourcesPanel.Opacity = 1;
                            ElementCompositionPreview.GetElementVisual(SourcesPanel).Opacity = 1f;
                            SourcesPanel.VerticalAlignment = VerticalAlignment.Center;
                            Grid.SetRow(SourcesPanel, 0);
                            Grid.SetColumn(SourcesPanel, 1);
                            Grid.SetColumnSpan(SourcesPanel, 1);
                            SourcesPanel.Width = 450;
                            SourcesPanel.HorizontalAlignment = HorizontalAlignment.Right;
                            SourcesPanel.Margin = new Thickness(40, 0, 0, 0); // [FIX] Reset Narrow mode margins
                        }

                        if ((_pageLoadState == PageLoadState.Loading || _isSourcesFetchInProgress) && !hasSources)
                        {
                            if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = Visibility.Visible;
                            if (SourcesListView != null) SourcesListView.Visibility = Visibility.Collapsed;
                            if (AddonSelectorList != null) AddonSelectorList.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = Visibility.Collapsed;
                            if (SourcesListView != null) SourcesListView.Visibility = Visibility.Visible;
                            if (AddonSelectorList != null) AddonSelectorList.Visibility = Visibility.Visible;
                        }

                        if (EpisodesPanel != null && EpisodesPanel.Visibility != Visibility.Collapsed) EpisodesPanel.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[LayoutLog] WIDE: Sources NOT visible. isSeries={isSeries}");
                        if (SourcesPanel != null) SourcesPanel.Visibility = Visibility.Collapsed;

                        if (isSeries)
                        {
                            if (EpisodesPanel != null)
                            {
                                if (EpisodesPanel.Visibility != Visibility.Visible) EpisodesPanel.Visibility = Visibility.Visible;
                                EpisodesPanel.Opacity = 1;
                                ElementCompositionPreview.GetElementVisual(EpisodesPanel).Opacity = 1f;

                                EpisodesPanel.VerticalAlignment = VerticalAlignment.Center;
                                Grid.SetRow(EpisodesPanel, 0);
                                Grid.SetColumn(EpisodesPanel, 1);
                                Grid.SetColumnSpan(EpisodesPanel, 1);
                                EpisodesPanel.Width = 450; EpisodesPanel.Margin = new Thickness(40, 0, 0, 0);
                                EpisodesPanel.HorizontalAlignment = HorizontalAlignment.Right;

                                if (_pageLoadState == PageLoadState.Loading)
                                {
                                    if (EpisodesShimmerPanel != null) EpisodesShimmerPanel.Visibility = Visibility.Visible;
                                    if (EpisodesListView != null) EpisodesListView.Visibility = Visibility.Collapsed;
                                }
                                else
                                {
                                    if (EpisodesShimmerPanel != null) EpisodesShimmerPanel.Visibility = Visibility.Collapsed;
                                    if (EpisodesListView != null) EpisodesListView.Visibility = Visibility.Visible;
                                }
                            }
                        }
                        else
                        {
                            if (EpisodesPanel != null && EpisodesPanel.Visibility != Visibility.Collapsed) EpisodesPanel.Visibility = Visibility.Collapsed;
                        }
                    }
                    
                    // Cleanup redundant positioning calls


                    // Since we no longer duplicate Panels, Wide mode simply relies on standard panel logic.
                    if (CastSection != null && CastSection.Visibility != targetCastVis) CastSection.Visibility = targetCastVis;
                    
                    if (DirectorSection != null && DirectorSection.Visibility != targetDirVis) DirectorSection.Visibility = targetDirVis;

                    // [STRICT WIDE ALIGNMENT]
                    // [STRICT WIDE ALIGNMENT] - Managed by VisualStateManager in XAML.
                    // Redundant C# assignments removed to prevent layout cycles.

                    if (ContentGrid != null)
                    {
                        // [FIX] Reset padding and alignment for wide mode
                        ContentGrid.VerticalAlignment = VerticalAlignment.Stretch;
                        ContentGrid.Padding = new Thickness(60, 40, 20, 40);
                    }

                    // [FIX] Reset alignments for Wide mode
                    if (InfoContainer != null) 
                    {
                        InfoContainer.VerticalAlignment = VerticalAlignment.Stretch;
                        InfoContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
                    }
                    if (InfoContainerInner != null) 
                    {
                        InfoContainerInner.VerticalAlignment = VerticalAlignment.Bottom;
                        InfoContainerInner.HorizontalAlignment = HorizontalAlignment.Stretch;
                        InfoContainerInner.Margin = new Thickness(0, 0, 0, 25);
                    }
                    if (InfoColumn != null) 
                    {
                        InfoColumn.VerticalAlignment = VerticalAlignment.Bottom;
                        InfoColumn.HorizontalAlignment = HorizontalAlignment.Left;
                        InfoColumn.Width = double.NaN;
                        InfoColumn.MaxWidth = 800; // Keep consistent with Narrow fix
                    }

                    // Reset header centering
                    if (TitleGroup != null) TitleGroup.HorizontalAlignment = HorizontalAlignment.Left;
                    if (MetadataRibbon != null) MetadataRibbon.HorizontalAlignment = HorizontalAlignment.Left;
                    if (ContentLogoHost != null) ContentLogoHost.HorizontalAlignment = HorizontalAlignment.Left;
                    if (TitlePanel != null) TitlePanel.HorizontalAlignment = HorizontalAlignment.Left;
                }
                else
                {
                    // NARROW MODE
                    System.Diagnostics.Debug.WriteLine("[LayoutLog] Switching to NARROW mode");
                    
                    if (RootScrollViewer != null)
                    {
                        if (RootScrollViewer.VerticalScrollMode != ScrollMode.Auto) RootScrollViewer.VerticalScrollMode = ScrollMode.Auto;
                        if (RootScrollViewer.VerticalScrollBarVisibility != ScrollBarVisibility.Auto) RootScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                        if (RootScrollViewer.IsVerticalScrollChainingEnabled != true) RootScrollViewer.IsVerticalScrollChainingEnabled = true;
                    }

                    if (ContentGrid != null)
                    {
                        if (!double.IsNaN(ContentGrid.Height)) ContentGrid.Height = double.NaN;
                        ContentGrid.MaxHeight = double.PositiveInfinity;
                        ContentGrid.VerticalAlignment = VerticalAlignment.Top;
                    }

                    // [STRICT NARROW ROW OVERRIDE]
                    if (Row0 != null) Row0.Height = GridLength.Auto;
                    if (Row1 != null) Row1.Height = GridLength.Auto;
                    if (Row2 != null) Row2.Height = GridLength.Auto;
                    if (Row3 != null) Row3.Height = GridLength.Auto;
                    // [FORCE NARROW POSITIONING]
                    if (InfoContainer != null) 
                    {
                        InfoContainer.VerticalAlignment = VerticalAlignment.Top;
                        InfoContainer.HorizontalAlignment = HorizontalAlignment.Stretch; // Ensure it fills the grid
                    }
                    if (InfoContainerInner != null) 
                     {
                         InfoContainerInner.VerticalAlignment = VerticalAlignment.Top;
                         InfoContainerInner.HorizontalAlignment = HorizontalAlignment.Stretch; // Fill screen to allow centering content
                         InfoContainerInner.Margin = new Thickness(0);
                     }
                     if (InfoColumn != null) 
                     {
                         InfoColumn.VerticalAlignment = VerticalAlignment.Top;
                         // [STABILITY FIX] Use Stretch + MaxWidth instead of Center to prevent width jitter during text wrapping.
                         InfoColumn.HorizontalAlignment = HorizontalAlignment.Stretch; 
                         InfoColumn.MaxWidth = 800;
                     }
                    
                    if (TitleGroup != null) TitleGroup.HorizontalAlignment = HorizontalAlignment.Center;
                    if (MetadataRibbon != null) MetadataRibbon.HorizontalAlignment = HorizontalAlignment.Center;
                    if (ContentLogoHost != null) ContentLogoHost.HorizontalAlignment = HorizontalAlignment.Center;
                    if (TitlePanel != null) TitlePanel.HorizontalAlignment = HorizontalAlignment.Center;

                    if (ContentGrid != null)
                    {
                        ContentGrid.Padding = new Thickness(20, 60, 20, 40); // [FIX] Reduced top space (was 200)
                        System.Diagnostics.Debug.WriteLine($"[LayoutLog] Narrow Padding Applied: {ContentGrid.Padding}");
                    }

                    System.Diagnostics.Debug.WriteLine($"[LayoutLog] Narrow Alignments: InfoColumn.HA={InfoColumn?.HorizontalAlignment}, InfoContainerInner.HA={InfoContainerInner?.HorizontalAlignment}");
                    
                    UpdateActionBarRealTime(ActualWidth, ActualHeight, false);

                    // Unified Panel Visibility for Narrow Mode
                    if (isSourceRequired)
                    {
                        System.Diagnostics.Debug.WriteLine("[LayoutLog] NARROW: Sources view active");
                        if (EpisodesPanel != null) EpisodesPanel.Visibility = Visibility.Collapsed;
                        
                        if (SourcesPanel != null)
                        {
                            if (SourcesPanel.Visibility != Visibility.Visible) SourcesPanel.Visibility = Visibility.Visible;
                            SourcesPanel.Opacity = 1;
                            ElementCompositionPreview.GetElementVisual(SourcesPanel).Opacity = 1f;
                            
                            Grid.SetRow(SourcesPanel, 1);
                            Grid.SetColumn(SourcesPanel, 0);
                            Grid.SetColumnSpan(SourcesPanel, 2);
                            SourcesPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                            SourcesPanel.VerticalAlignment = VerticalAlignment.Top;
                            SourcesPanel.Width = double.NaN;
                            SourcesPanel.Margin = new Thickness(20, 20, 20, 0);
                        }

                        if (_pageLoadState == PageLoadState.Loading || _isSourcesFetchInProgress)
                        {
                            if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = Visibility.Visible;
                            if (SourcesListView != null) SourcesListView.Visibility = Visibility.Collapsed;
                            if (AddonSelectorList != null) AddonSelectorList.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = Visibility.Collapsed;
                            if (SourcesListView != null) SourcesListView.Visibility = Visibility.Visible;
                            if (AddonSelectorList != null) AddonSelectorList.Visibility = Visibility.Visible;
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[LayoutLog] NARROW: Sources NOT visible. isSeries={isSeries}");
                        if (SourcesPanel != null) SourcesPanel.Visibility = Visibility.Collapsed;
                        
                        // Handle Episodes vs Shimmer
                        if (isSeries)
                        {
                            if (EpisodesPanel != null) 
                            {
                                // [UNIFIED] Keep panel visible during loading to show internal shimmer
                                if (EpisodesPanel.Visibility != Visibility.Visible) EpisodesPanel.Visibility = Visibility.Visible;
                                
                                // [FIX] In narrow mode, ensure it spans appropriately and is top-aligned to be visible.
                                // [LAYOUT GAP FIX] Set to Row 1 (same as SourcesPanel) to ensure consistent spacing below description.
                                Grid.SetRow(EpisodesPanel, 1);
                                Grid.SetColumn(EpisodesPanel, 0);
                                Grid.SetColumnSpan(EpisodesPanel, 2);
                                EpisodesPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                                EpisodesPanel.VerticalAlignment = VerticalAlignment.Top;
                                EpisodesPanel.Width = double.NaN;
                                EpisodesPanel.Margin = new Thickness(20, 20, 20, 20);

                                if (_pageLoadState == PageLoadState.Loading)
                                {
                                    if (EpisodesShimmerPanel != null) 
                                    {
                                        EpisodesShimmerPanel.Visibility = Visibility.Visible;
                                        ElementCompositionPreview.GetElementVisual(EpisodesShimmerPanel).Opacity = 1f;
                                    }
                                    if (EpisodesListView != null) EpisodesListView.Visibility = Visibility.Collapsed;
                                }
                                else
                                {
                                    if (EpisodesShimmerPanel != null) EpisodesShimmerPanel.Visibility = Visibility.Collapsed;
                                    if (EpisodesListView != null) EpisodesListView.Visibility = Visibility.Visible;
                                }
                            }
                            if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            if (EpisodesPanel != null) EpisodesPanel.Visibility = Visibility.Collapsed;
                            // Show shimmer if sources are loading but not yet visible
                            if (SourcesShimmerPanel != null && _isSourcesFetchInProgress) 
                            {
                                System.Diagnostics.Debug.WriteLine("[LayoutLog] NARROW: Sources FETCH IN PROGRESS, showing shimmer");
                                SourcesShimmerPanel.Visibility = Visibility.Visible;
                                Grid.SetRow(SourcesShimmerPanel, 1);
                                Grid.SetColumn(SourcesShimmerPanel, 0);
                                Grid.SetColumnSpan(SourcesShimmerPanel, 2);
                                SourcesShimmerPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                                SourcesShimmerPanel.VerticalAlignment = VerticalAlignment.Top;
                                SourcesShimmerPanel.Width = double.NaN;
                            }
                            else if (SourcesShimmerPanel != null)
                            {
                                SourcesShimmerPanel.Visibility = Visibility.Collapsed;
                            }
                        }
                    }

                    // [USER REQUEST] Hide Cast and Director in Narrow mode
                    if (CastSection != null) CastSection.Visibility = Visibility.Collapsed;
                    if (DirectorSection != null) DirectorSection.Visibility = Visibility.Collapsed;
                    if (NarrowSectionsContainer != null) NarrowSectionsContainer.Visibility = Visibility.Collapsed;
                }
                
                System.Diagnostics.Debug.WriteLine($"[LayoutLog] Final State Scan: InfoContainer.VA={InfoContainer?.VerticalAlignment}, InfoContainerInner.VA={InfoContainerInner?.VerticalAlignment}, Row0.H={Row0?.Height}");

                         // [ROOT FIX] Ensure Cast/Director sections stay visible ONLY in Wide mode if data exists
                         if (isWide)
                         {
                            if (CastSection != null) CastSection.Visibility = targetCastVis;
                            if (DirectorSection != null) DirectorSection.Visibility = targetDirVis;
                         }
                    
                 
                System.Diagnostics.Debug.WriteLine($"[LayoutLog] Layout updated. InfoContainer.Row={Grid.GetRow(InfoContainer)}, SourcesPanel.Row={Grid.GetRow(SourcesPanel)}, SourcesPanel.Vis={SourcesPanel?.Visibility}");
                // [ROOT FIX] Removed artificial delay. 
                // XAML engine and Composition real-time loop handle settling now.
                System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 2: UpdateLayoutState EXIT. ContentGrid.H={ContentGrid?.Height}, InfoContainer AH={InfoContainer?.ActualHeight}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LayoutDebug] ERROR in UpdateLayoutState: {ex}");
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            HistoryManager.Instance.HistoryChanged -= OnHistoryChanged;
            base.OnNavigatedFrom(e);
            
            _ambienceNavigationEpoch++; // Stop any pending extraction
            StopBackgroundSlideshow();
            
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

            // Cancel any active prebuffering BEFORE cleanup
            try { 
                _prebufferCts?.Cancel(); 
                _prebufferCts?.Dispose(); 
                _prebufferCts = null;
            } catch {}

            // Close Trailer Overlay
            try {
                if (TrailerOverlay != null && TrailerOverlay.Visibility == Visibility.Visible)
                {
                    // Fire and forget trailer close on navigation
                    _ = CloseTrailer();
                }
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Error closing trailer on navigate: {ex.Message}");
            }

            if (MediaInfoPlayer != null && PlayerHost != null)
            {
                 if (e.SourcePageType == typeof(PlayerPage) || _isHandoffInProgress)
                 {
                     PlayerHost.Content = null;
                     System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Detached player for handover (preserving instance).");
                 }
                 else
                 {
                     try
                     {
                         System.Diagnostics.Debug.WriteLine("[MediaInfoPage] STRICT CLEANUP: Destroying MpvPlayer instance on exit (non-player destination).");
                         var pToCleanup = MediaInfoPlayer;
                         PlayerHost.Content = null;
                         MediaInfoPlayer = null; 
                         _prebufferUrl = null;
                         
                         _ = Task.Run(async () => {
                             try { await pToCleanup.CleanupAsync(); } catch { }
                         });
                     }
                     catch (Exception ex)
                     {
                         System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] CleanupAsync Error: {ex.Message}");
                     }
                 }
            }
            else 
            {
                 System.Diagnostics.Debug.WriteLine("[MediaInfoPage] OnNavigatedFrom: No player to clean up.");
            }
        }
           private void OnHistoryChanged(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => {
                if (_selectedEpisode != null) _selectedEpisode.RefreshHistoryState();
            });
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            try
            {
                base.OnNavigatedTo(e);
                
                HistoryManager.Instance.HistoryChanged += OnHistoryChanged;
                _loadingVersion++;

                System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-NavTo] START: NavMode={e.NavigationMode}, ParamType={e.Parameter?.GetType().Name}");
    
                // Determine New Item coming in
                IMediaStream incomingItem = null;
                if (e.Parameter is MediaNavigationArgs navArgs) incomingItem = navArgs.Stream;
                else if (e.Parameter is IMediaStream mediaStream) incomingItem = mediaStream;

                IMediaStream previousItem = _item; // [OPTIMIZATION] Track previous to avoid redundant reloads
                bool isItemSwitching = (incomingItem != null && !IsSameItem(previousItem, incomingItem)) || (_lastUsedTmdbLanguage != AppSettings.TmdbLanguage);
                bool isBackNav = e.NavigationMode == NavigationMode.Back;

                if (e.NavigationMode != NavigationMode.Back)
                {
                    _ambienceNavigationEpoch++;
                    System.Diagnostics.Debug.WriteLine("[AMBIENCE][QUEUE] Navigation reset.");
                }

                // CRITICAL: If switching items, reset EVERYTHING immediately
                if (isItemSwitching)
                {
                    System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Item switching - Running AGGRESSIVE RESET.");
                    ResetPageState();
                    _item = incomingItem; 
                }

                // IMMEDIATE CLEANUP - Only if switching items
                if (isItemSwitching)
                {
                    System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Item switching - Clearing UI and Fetch state");
                    _addonResults?.Clear();
                    SourcesPanel.Visibility = Visibility.Collapsed;
                    EpisodesPanel.Visibility = Visibility.Collapsed;

                    _currentStremioVideoId = null;
                    _areSourcesVisible = false;
                    _unifiedMetadata = null; 
                    if (SourcesListView != null) SourcesListView.ItemsSource = null;
                    if (AddonSelectorList != null) AddonSelectorList.ItemsSource = null;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Same item or back nav - Preserving UI state/cache");
                }

                _shouldAutoResume = false; // Reset auto-resume flag

                // Layout Adjustment
                DispatcherQueue.TryEnqueue(() => 
                {
                    bool isWide = ActualWidth >= LayoutAdaptiveThreshold;
                    UpdateLayoutState(isWide);
                    UpdateRealTimeScaling(ActualWidth, ActualHeight, isWide);
                    SyncIdentityVisibility(false);
                });

                // SEEDING: Immediately set the image for ConnectedAnimation
                if (e.Parameter is MediaNavigationArgs args)
                {
                    _item = args.Stream;
                    _shouldAutoResume = args.AutoResume && e.NavigationMode != NavigationMode.Back;
                    
                    if (args.PreloadedLogo != null)
                    {
                        SyncIdentityVisibility(false);
                        if (TitleShimmer != null) TitleShimmer.Visibility = Visibility.Collapsed;
                    }

                    if (isItemSwitching || !isBackNav)
                    {
                        if (args.PreloadedImage != null)
                        {
                            HeroImage.Source = args.PreloadedImage;
                            HeroImage.Opacity = 1;
                            var v = ElementCompositionPreview.GetElementVisual(HeroImage);
                            if (v != null) v.Opacity = 1f;
                            _isFirstImageApplied = true;
                            _ = ExtractAndApplyAmbienceAsync(HeroImage, "provisional-preloaded");
                        }
                        else
                        {
                            string seedUrl = _item?.PosterUrl;
                            if (!string.IsNullOrEmpty(seedUrl) && !ImageHelper.IsPlaceholder(seedUrl))
                            {
                                if (!(HeroImage.Source is BitmapImage bi && bi.UriSource?.ToString() == seedUrl))
                                {
                                    HeroImage.Source = ImageHelper.GetImage(seedUrl);
                                }
                                HeroImage.Opacity = 1;
                                var v = ElementCompositionPreview.GetElementVisual(HeroImage);
                                if (v != null) v.Opacity = 1f;
                                _isFirstImageApplied = true;
                                _ = ExtractAndApplyAmbienceAsync(HeroImage, "provisional-seed");
                            }
                        }
                    }
                }
                else if (e.Parameter is IMediaStream streamParam)
                {
                    if (isItemSwitching || !isBackNav)
                    {
                        if (!string.IsNullOrEmpty(streamParam.PosterUrl))
                        {
                            HeroImage.Source = ImageHelper.GetImage(streamParam.PosterUrl);
                            HeroImage.Opacity = 1;
                            _isFirstImageApplied = true;
                            _ = ExtractAndApplyAmbienceAsync(HeroImage, "provisional-poster-seed");
                        }
                    }
                }

                if (!isBackNav)
                {
                    StartHeroConnectedAnimation();
                }

                SetupParallax();
                _isHandoffInProgress = false;

                if (App.HandoffPlayer != null)
                {
                    MediaInfoPlayer = App.HandoffPlayer;
                    App.HandoffPlayer = null;
                    if (PlayerHost != null)
                    {
                        PlayerHost.Content = MediaInfoPlayer;
                        MediaInfoPlayer.Visibility = Visibility.Visible;
                        MediaInfoPlayer.Opacity = 1;
                    }
                }

                await HistoryManager.Instance.InitializeAsync();
                await CloseTrailer(); 
                RestoreUIVisibility();

                IMediaStream newItem = incomingItem;
                if (newItem == null) return; 

                _item = newItem; 
                await LoadDetailsAsync(newItem, null, previousItem);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Error in OnNavigatedTo: {ex.Message}");
            }
        }




        private void SetupParallax()
        {
            try
            {
                // Parallax is subtle and handled mostly by XAML's Ken Burns, 
                // but we keep text parallax for nice feel.
                // MainScrollViewer no longer has scrolling except for text column, so parallax might be less effective
                // but we keep it safe.
            }
            catch { }
        }


        private async Task LoadDetailsAsync(IMediaStream item, TmdbMovieResult preFetchedTmdb = null, IMediaStream previousItem = null)
        {
            int currentVersion = _loadingVersion;
            bool isSwitchingItem = previousItem != null && !IsSameItem(previousItem, item);
            string navSeedTitle = item?.Title?.Trim() ?? "";

            // 1. Clear sources if switching
            if (isSwitchingItem)
            {
                System.Diagnostics.Debug.WriteLine("[MediaInfoPage] New Item loaded - Clearing sources cache");
                _addonResults?.Clear();
                _currentStremioVideoId = null;
                _stremioSourcesCache.Clear();
                _areSourcesVisible = false;

                if (SourcesListView != null) SourcesListView.ItemsSource = null;

                if (AddonSelectorList != null) AddonSelectorList.ItemsSource = null;

            }

            // [FIX] Pre-emptively set sources fetch state for movies to keep shimmer visible during reveal animation
            if (isSwitchingItem && item is Models.Stremio.StremioMediaStream strmItem && strmItem.Meta.Type == "movie")
            {
                _isSourcesFetchInProgress = true;
                _areSourcesVisible = true;
            }

            // 2. Cache Peek (Flicker Prevention)
            var existingTmdb = preFetchedTmdb ?? item.TmdbInfo;
            _cachedTmdb = existingTmdb;

            var cachedMetadata = MetadataProvider.Instance.TryPeekMetadata(item);
            bool isCached = cachedMetadata != null;
            
            System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Cache check: isCached={isCached}, Type={item.Type}, SeasonsCount={cachedMetadata?.Seasons?.Count ?? -1}");
            
            // [FIX] For series, if cached metadata doesn't have episodes (e.g., from ExpandedCard context), 
            // we need to re-fetch to get the full episode list for Detail page
            // Note: Type can be "SERIES" or "series" depending on source, use case-insensitive check
            if (isCached && !string.IsNullOrEmpty(item.Type) && item.Type.Equals("series", StringComparison.OrdinalIgnoreCase))
            {
                bool hasEpisodes = cachedMetadata.Seasons?.Any(s => s.Episodes?.Any() == true) == true;
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Series check: hasEpisodes={hasEpisodes}");
                if (!hasEpisodes)
                {
                    System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Cache doesn't have episodes (from ExpandedCard), re-fetching for Detail.");
                    cachedMetadata = null;
                    isCached = false;
                }
            }

            if (isCached)
            {
                // [OPTIMIZATION] If it's the SAME item and we already have metadata, go instant
                if (!isSwitchingItem && _unifiedMetadata != null && _pageLoadState == PageLoadState.Ready)
                {
                    System.Diagnostics.Debug.WriteLine("[MediaInfoPage] [Optimization] Seamless re-entry - Skipping reveal animation.");
                    await PopulateMetadataUI(_unifiedMetadata, item);
                    ImmediateRevealContent();
                    return;
                }

                System.Diagnostics.Debug.WriteLine("[MediaInfoPage] [Optimization] Cache HASH HIT - Shimmer + Staggered reveal.");
                _unifiedMetadata = cachedMetadata;
                _pageLoadState = PageLoadState.Loading;
                // Show shimmer briefly so the reveal animation is always perceived
                SetLoadingState(true, item);
                // Yield two frames so shimmers are rendered before we populate + reveal
                await Task.Delay(80);
                await PopulateMetadataUI(cachedMetadata, item);
                _pageLoadState = PageLoadState.Revealing;
                StaggeredRevealContent();
                _pageLoadState = PageLoadState.Ready;
            }
            else
            {
                // State Machine: Check if layout is ready before showing shimmer
                if (_pageLoadState == PageLoadState.Initial)
                {
                    // Layout not ready yet, save item for later
                    System.Diagnostics.Debug.WriteLine($"[MediaInfo-Load] Deferring shimmer until LayoutReady.");
                    _pendingLoadItem = item;
                    _pageLoadState = PageLoadState.Loading; 
                }
                else
                {
                    // Layout is ready, proceed normally
                    System.Diagnostics.Debug.WriteLine($"[MediaInfo-Load] Layout available. Showing shimmers.");
                    _pageLoadState = PageLoadState.Loading;
                    SetLoadingState(true, item); 
                    UpdateLayoutState(ActualWidth >= LayoutAdaptiveThreshold);
                }
            }
            
            // 3. Setup Interactions
            SetupButtonInteractions(PlayButton, RestartButton, TrailerButton, DownloadButton, CopyLinkButton, StickyPlayButton);
            SetupMagneticEffect(PlayButton, 0.15f);
            SetupMagneticEffect(TrailerButton, 0.2f);
            SetupMagneticEffect(DownloadButton, 0.2f);
            SetupMagneticEffect(CopyLinkButton, 0.2f);
            SetupVortexEffect(BackButton, BackIconVisual);
            SetupStickyScroller();

            // 4. Fetch Metadata if not cached
            if (!isCached && (isSwitchingItem || _unifiedMetadata == null))
            {
                // Parallelize Source Fetching for Stremio Movies (Removed redundant call - handled in branching reveal below)

                _unifiedMetadata = await MetadataProvider.Instance.GetMetadataAsync(item, onBackdropFound: (url) => 
                {
                    DispatcherQueue.TryEnqueue(() => AddBackdropToSlideshow(url));
                });
                _lastUsedTmdbLanguage = AppSettings.TmdbLanguage;

                // [CONSOLIDATION] Synchronize the underlying stream with full Detail-level metadata
                if (item != null && _unifiedMetadata != null) item.UpdateFromUnified(_unifiedMetadata);
            }
            else if (!isCached && _unifiedMetadata?.BackdropUrls != null && _unifiedMetadata.BackdropUrls.Count > 0)
            {
                StartBackgroundSlideshow(_unifiedMetadata.BackdropUrls);
            }

            // [FIX] Update _cachedTmdb with newly fetched info to enable episode enrichment
            if (_cachedTmdb == null && _unifiedMetadata?.TmdbInfo != null)
            {
                _cachedTmdb = _unifiedMetadata.TmdbInfo;
                System.Diagnostics.Debug.WriteLine("[MediaInfoPage] _cachedTmdb synchronized after metadata fetch.");
            }

            // 6. UI Population
            await PopulateMetadataUI(_unifiedMetadata, item);

            // 7. Reveal & Branching
            if (!isCached)
            {
                // [FIX] Sync layout state before reveal to ensure correct panels (Episodes/Sources) are visible for animation
                UpdateLayoutState(ActualWidth >= LayoutAdaptiveThreshold);

                _pageLoadState = PageLoadState.Revealing;
                StaggeredRevealContent();
                _pageLoadState = PageLoadState.Ready;
            }

            try
            {
                bool isWide = ActualWidth >= LayoutAdaptiveThreshold;
                if (_unifiedMetadata.IsSeries)
                {
                    _areSourcesVisible = false; // Ensure sources are hidden for series until selection

                    
                    UpdateLayoutState(isWide);
                    await LoadSeriesDataAsync(_unifiedMetadata);
                }
                else
                {
                    // [FIX] Trigger source fetch for any item with a canonical ID (IMDb or TMDB)
                    string probeId = _unifiedMetadata.ImdbId ?? (item as Models.Stremio.StremioMediaStream)?.Meta?.Id ?? item.IMDbId;
                    bool isIptvOnly = _unifiedMetadata.IsAvailableOnIptv && !MetadataProvider.IsCanonicalId(probeId);
                    
                    if (!string.IsNullOrEmpty(probeId) && (MetadataProvider.IsCanonicalId(probeId) || isIptvOnly))
                    {
                         _ = PlayStremioContent(probeId, showGlobalLoading: false, autoPlay: _shouldAutoResume);
                         if (_shouldAutoResume) _shouldAutoResume = false;
                    }
                    _areSourcesVisible = true;
                    
                    // [FIX] Immediate sync - NO DELAY to prevent "centered" flicker
                    UpdateLayoutState(ActualWidth >= LayoutAdaptiveThreshold);
                    UpdateRealTimeScaling(ActualWidth, ActualHeight, ActualWidth >= LayoutAdaptiveThreshold);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] LoadDetailsAsync Error: {ex.Message}");
            }
        }

        private async Task PopulateMetadataUI(UnifiedMetadata unified, IMediaStream item)
        {
            var sms = item as Models.Stremio.StremioMediaStream;
            string metadataId = unified.MetadataId;
            string metadataType = unified.IsSeries ? "series" : "movie";
            string navSeedTitle = item?.Title?.Trim() ?? "";

            // Seed missing urls and state from item
            if (string.IsNullOrWhiteSpace(unified.PosterUrl)) unified.PosterUrl = item.PosterUrl;
            if (string.IsNullOrWhiteSpace(unified.BackdropUrl)) unified.BackdropUrl = sms?.Meta?.Background ?? item.BackdropUrl;
            
            // [FIX] Ensure item's IPTV availability is synced with unified result early
            if (unified.IsAvailableOnIptv && sms != null)
            {
                sms.IsAvailableOnIptv = true;
            }

            TitleText.Text = string.IsNullOrEmpty(unified.Title) || unified.Title == "Unknown" ? (string.IsNullOrEmpty(navSeedTitle) ? "Unknown Content" : navSeedTitle) : unified.Title;
            StickyTitle.Text = TitleText.Text;

            // [IDENTITY LOGO VS TITLE] Consolidated at the start to stabilize measurement
            bool hasLogo = !string.IsNullOrWhiteSpace(unified.LogoUrl);
            if (hasLogo)
            {
                // Trigger Composition Surface load or SVG load
                EnsureLogoSurface(unified.LogoUrl);

                if (ContentLogoHost != null && ContentLogoHost.Visibility != Visibility.Visible) 
                    ContentLogoHost.Visibility = Visibility.Visible;
                
                // [FIX] Don't hide title immediately. Let EnsureLogoSurface fade it out when the logo is actually ready.
                // This prevents a "void" where neither is visible during async network load.
                if (_logoSurface == null && !unified.LogoUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    TitleText.Visibility = Visibility.Visible;
                    TitleText.Opacity = 1;
                    if (ContentLogoHost != null) ContentLogoHost.Opacity = 0;
                }
                else if (ContentLogoHost != null)
                {
                    // Already loaded (e.g. from cache)
                    if (ContentLogoHost.Opacity != 1) ContentLogoHost.Opacity = 1; 
                    if (_logoVisual != null) _logoVisual.Opacity = 1f;
                    if (TitleText.Visibility != Visibility.Collapsed) TitleText.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                _logoReadyTcs = null;
                if (ContentLogoHost != null) ContentLogoHost.Visibility = Visibility.Collapsed;
                TitleText.Visibility = Visibility.Visible;
                TitleText.Opacity = 1; 
                TitlePanel.Visibility = Visibility.Visible;
            }

            // [SUPER TITLE LOGIC] - Must run BEFORE TitlePanel.UpdateLayout()
            if (string.IsNullOrWhiteSpace(unified.SubTitle) && sms != null && !string.IsNullOrWhiteSpace(sms.Meta?.OriginalName) && !string.Equals(sms.Meta.OriginalName, unified.Title, StringComparison.OrdinalIgnoreCase))
                unified.SubTitle = sms.Meta.OriginalName;
            if (string.IsNullOrWhiteSpace(unified.SubTitle) && !string.IsNullOrWhiteSpace(navSeedTitle) && !string.Equals(navSeedTitle, unified.Title, StringComparison.OrdinalIgnoreCase))
                unified.SubTitle = navSeedTitle;

            string sub = !string.IsNullOrWhiteSpace(unified.SubTitle) ? unified.SubTitle : unified.OriginalTitle;
            if (!string.IsNullOrWhiteSpace(sub) && !string.Equals(unified.Title, sub, StringComparison.OrdinalIgnoreCase) && !hasLogo)
            {
                if (SuperTitleText != null)
                {
                    SuperTitleText.Text = sub.ToUpperInvariant();
                    SuperTitleText.Visibility = Visibility.Visible;
                    SuperTitleText.Margin = new Thickness(2, 0, 0, -4);
                }
                if (IdentityContainer != null) IdentityContainer.Visibility = Visibility.Visible;
            }
            else
            {
                if (SuperTitleText != null) SuperTitleText.Visibility = Visibility.Collapsed;
                if (IdentityContainer != null) IdentityContainer.Visibility = Visibility.Collapsed;
            }

            // [STABLE SHIMMER SYNC]
            if (TitleShimmer != null) TitleShimmer.Height = hasLogo ? 120 : 56;

            // [ROOT FIX] Removed TitlePanel.UpdateLayout() - let XAML handle measuring in its own pass.
            // Shimmer height will settle in the next frame.
            if (TitleShimmer != null)
            {
                double currentHeight = TitlePanel.ActualHeight > 0 ? TitlePanel.ActualHeight : (hasLogo ? 120 : 56);
                TitleShimmer.Height = currentHeight;
            }

            OverviewText.Text = !string.IsNullOrEmpty(unified.Overview) ? unified.Overview : "Açıklama mevcut değil.";
            YearText.Text = unified.Year?.Split(new char[] { '-', '–' })[0] ?? "";
            
            if (GenresText != null) 
            {
                GenresText.Text = unified.Genres;
                GenresText.Visibility = (!string.IsNullOrEmpty(GenresText.Text)) ? Visibility.Visible : Visibility.Collapsed;
                // [GAP FIX] Ensure the Grid container is collapsed if genres are empty
                if (GenresText.Parent is Grid pGrid) pGrid.Visibility = GenresText.Visibility;
            }
            
            RuntimeText.Text = unified.IsSeries ? "Dizi" : unified.Runtime;
            OverviewText.MaxLines = unified.IsSeries ? 4 : 0;

            // [FIX] Sync IPTV flags back to the item for UI/Interaction logic stability
            if (unified.IsAvailableOnIptv) 
            {
                item.IsAvailableOnIptv = true;
                if (string.IsNullOrEmpty(item.StreamUrl)) item.StreamUrl = unified.StreamUrl;
            }

            if (!string.IsNullOrEmpty(unified.BackdropUrl))
            {
                AddBackdropToSlideshow(unified.BackdropUrl);
                
                // [FIX] Immediate ambience for seed/preload
                if (!_isFirstImageApplied)
                {
                    ApplyHeroSeedImage(unified.BackdropUrl, "backdrop");
                }
            }
            else if (!string.IsNullOrEmpty(unified.PosterUrl))
            {
                if (!_isFirstImageApplied)
                {
                    ApplyHeroSeedImage(unified.PosterUrl, "poster-fallback");
                }
            }

            // [MODERN] Legacy shimmer adjustments removed.

            PlayButton.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(unified.TrailerUrl)) TrailerButton.Visibility = Visibility.Visible;
            DownloadButton.Visibility = Visibility.Visible;
            CopyLinkButton.Visibility = Visibility.Visible;

            if (_streamUrl != null) UpdateTechnicalSectionVisibility(true);
            UpdateWatchlistState();


            // 11. Consolidated History & Resume logic
            string imdbId = unified.ImdbId ?? (item as Models.Stremio.StremioMediaStream)?.Meta?.ImdbId;
            HistoryItem history = null;

            if (unified.IsSeries)
            {
                // Series check
                history = HistoryManager.Instance.GetLastWatchedEpisode(metadataId ?? item.Id.ToString());
                if (history == null && !string.IsNullOrEmpty(imdbId)) 
                    history = HistoryManager.Instance.GetLastWatchedEpisode(imdbId);
                
                // Last ditch: check by title if series
                if (history == null)
                    history = HistoryManager.Instance.GetHistoryByTitle(unified.Title, "series");

                if (history != null && !history.IsFinished)
                {
                    string resumeText = "Devam Et";
                    int displayEp = history.EpisodeNumber == 0 ? 1 : history.EpisodeNumber;
                    string subtext = $"S{history.SeasonNumber:D2}E{displayEp:D2}";
                    
                    PlayButtonText.Text = resumeText;
                    PlayButtonSubtext.Text = subtext;
                    PlayButtonSubtext.Visibility = Visibility.Visible;
                    
                    StickyPlayButtonText.Text = resumeText;
                    StickyPlayButtonSubtext.Text = subtext;
                    StickyPlayButtonSubtext.Visibility = Visibility.Visible;
                    
                    RestartButton.Visibility = Visibility.Visible;
                    
                    if (!string.IsNullOrEmpty(history.StreamUrl))
                    {
                        _streamUrl = history.StreamUrl;
                        StartPrebuffering(_streamUrl, history.Position);
                    }
                }
                else
                {
                    PlayButtonText.Text = "Oynat";
                    PlayButtonSubtext.Visibility = Visibility.Collapsed;
                    RestartButton.Visibility = Visibility.Collapsed;
                    
                    if (StickyPlayButtonText != null) StickyPlayButtonText.Text = "Oynat";
                    if (StickyPlayButtonSubtext != null) StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                // Movie/Live/Vod
                history = HistoryManager.Instance.GetProgress(metadataId ?? item.Id.ToString());
                if (history == null && !string.IsNullOrEmpty(imdbId)) 
                    history = HistoryManager.Instance.GetProgress(imdbId);

                // Last ditch: check by title
                if (history == null)
                {
                    history = HistoryManager.Instance.GetHistoryByTitle(unified.Title, "movie");
                    
                    // Zootopia Special case - sometimes IDs differ between TMDB/Cinemeta
                    if (history == null && unified.Title != null && unified.Title.Contains("Zoot"))
                    {
                         // Try general match if specific title lookup failed
                         var allMovieHistory = HistoryManager.Instance.GetContinueWatching("movie");
                         history = allMovieHistory.FirstOrDefault(x => x.Title.Contains("Zoot"));
                    }
                }
                
                if (history != null && history.Position > 0 && !history.IsFinished)
                {
                    PlayButtonText.Text = "Devam Et";
                    StickyPlayButtonText.Text = "Devam Et";
                    
                    // Add subtext for movies too if duration is known
                    if (history.Duration > 0)
                    {
                        var remaining = TimeSpan.FromSeconds(history.Duration - history.Position);
                        PlayButtonSubtext.Text = remaining.TotalHours >= 1 
                            ? $"{(int)remaining.TotalHours}sa {(int)remaining.Minutes}dk Kaldı"
                            : $"{(int)remaining.TotalMinutes}dk Kaldı";
                        PlayButtonSubtext.Visibility = Visibility.Visible;
                    }

                    RestartButton.Visibility = Visibility.Visible;
                    _streamUrl = history.StreamUrl;
                    StartPrebuffering(_streamUrl, history.Position);

                    // AUTO-RESUME TRIGGER (Non-Stremio Movies / IPTV)
                    if (_shouldAutoResume && !(item is Models.Stremio.StremioMediaStream))
                    {
                        _shouldAutoResume = false;
                        System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Auto-Resume triggered for movie: {unified.Title}");
                        PlayButton_Click(null, null);
                    }
                }
                else
                {
                    PlayButtonText.Text = (metadataType == "movie" && item is Models.Stremio.StremioMediaStream) ? "Kaynak Bul" : "Oynat";
                    PlayButtonSubtext.Visibility = Visibility.Collapsed;
                    RestartButton.Visibility = Visibility.Collapsed;
                    
                    if (item is LiveStream liveS && !string.IsNullOrEmpty(liveS.StreamUrl)) 
                    { 
                        _streamUrl = liveS.StreamUrl; 
                        StartPrebuffering(_streamUrl); 
                    }
                }
            }

            // 12. Cast, Slideshow & Attribution
            await PopulateCastAndDirectors(unified);

            if (unified.BackdropUrls != null && unified.BackdropUrls.Count > 0)
                StartBackgroundSlideshow(unified.BackdropUrls);

            if (SourceAttributionText != null) SourceAttributionText.Text = unified.MetadataSourceInfo;

// 13. Synchronize Layout & Final Probe Trigger
            bool isWide = ActualWidth >= LayoutAdaptiveThreshold;
            if (InfoColumn != null)
            {
                 double initialMeasureWidth = isWide ? (_lastReportedWidth > 1600 ? 1200 : 1000) : (ActualWidth - 40);
                 InfoColumn.MaxWidth = initialMeasureWidth;
                 
                 // [DYNAMIC SHIMMER SYNC] Sync shimmer to current content before reveal
                 // Removed this.UpdateLayout() - using manual Measure below instead

                 if (OverviewPanel != null && OverviewShimmer != null)
                 {
                     OverviewPanel.Measure(new Size(initialMeasureWidth, double.PositiveInfinity));
                     double overviewHeight = OverviewPanel.DesiredSize.Height;
                     if (overviewHeight > 10) OverviewShimmer.Height = overviewHeight;
                 }
                 
                 // Composition animasyonlarını tamamen güncel, taze boyutlarla başlat
                 UpdateRealTimeScaling(ActualWidth, ActualHeight, isWide);
                 UpdateActionBarRealTime(ActualWidth, ActualHeight, isWide);
            }

            if (!string.IsNullOrEmpty(_streamUrl))
            {
                _ = UpdateTechnicalBadgesAsync(_streamUrl);
            }
        }

        private async Task PopulateCastAndDirectors(UnifiedMetadata unified)
        {
            try
            {
                var newCast = new List<CastItem>();
                if (unified.Cast != null && unified.Cast.Count > 0 && unified.Cast.Any(c => !string.IsNullOrEmpty(c.ProfileUrl)))
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
                                   (CastList.Count > 0 && (CastList[0].Name != newCast[0].Name || CastList[0].FullProfileUrl != newCast[0].FullProfileUrl));

                if (castChanged)
                {
                    CastList.Clear();
                    foreach (var c in newCast) CastList.Add(c);
                    CastListView.ItemsSource = null;
                    CastListView.ItemsSource = CastList;
                }

                if (CastList.Count > 0)
                {
                    CastSection.Visibility = Visibility.Visible;
                    CastListView.ItemsSource = CastList;
                    AdjustCastShimmer(CastList.Count);
                }
                else
                {
                    CastSection.Visibility = Visibility.Collapsed;
                    if (CastShimmer != null) CastShimmer.Visibility = Visibility.Collapsed;
                    AdjustCastShimmer(0);
                }

                var newDirectors = new List<CastItem>();
                if (unified.Directors != null && unified.Directors.Count > 0)
                {
                    foreach (var d in unified.Directors.Take(5)) 
                    {
                        newDirectors.Add(new CastItem 
                        { 
                            Name = d.Name, 
                            Character = "Yönetmen", 
                            FullProfileUrl = d.ProfileUrl,
                            ProfileImage = ImageHelper.GetImage(d.ProfileUrl, 80, 100)
                        });
                    }
                }

                bool needsDirectorImages = newDirectors.Any(d => string.IsNullOrEmpty(d.FullProfileUrl));
                if (needsDirectorImages && unified.TmdbInfo != null && AppSettings.IsTmdbEnabled)
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

                // [FLICKER PREVENTION] Only update if content changed
                bool directorsChanged = DirectorList.Count != newDirectors.Count || 
                                       (DirectorList.Count > 0 && (DirectorList[0].Name != newDirectors[0].Name));

                if (directorsChanged)
                {
                    DirectorList.Clear();
                    foreach (var d in newDirectors) DirectorList.Add(d);
                    DirectorListView.ItemsSource = null;
                    DirectorListView.ItemsSource = DirectorList;
                }

                if (DirectorList.Count > 0)
                {
                    DirectorSection.Visibility = (ActualWidth >= LayoutAdaptiveThreshold) ? Visibility.Visible : Visibility.Collapsed;
                    DirectorListView.ItemsSource = DirectorList;
                    AdjustDirectorShimmer(DirectorList.Count);
                }
                else
                {
                    DirectorSection.Visibility = Visibility.Collapsed;
                    if (DirectorShimmer != null) DirectorShimmer.Visibility = Visibility.Collapsed;
                    AdjustDirectorShimmer(0);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] PopulateCastAndDirectors Error: {ex.Message}");
            }
        }

        private void ApplyHeroSeedImage(string imageUrl, string reason)
        {
            if (string.IsNullOrWhiteSpace(imageUrl) || HeroImage == null || HeroImage2 == null) return;
            
            string NormalizeUrl(string u) => u?.Replace("https://", "http://")?.TrimEnd('/')?.ToLowerInvariant();
            string normTarget = NormalizeUrl(imageUrl);
            
            if (HeroImage.Source is BitmapImage biCurrent)
            {
                string normCurrent = NormalizeUrl(biCurrent.UriSource?.ToString());
                if (normCurrent == normTarget && HeroImage.Opacity >= 0.9)
                {
                    // Trigger immediate ambience even if skipping source swap
                    _ = ExtractAndApplyAmbienceAsync(HeroImage, $"seed {reason}");
                    return;
                }
            }

            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)) return;

            try
            {
                var visual1 = ElementCompositionPreview.GetElementVisual(HeroImage);
                var visual2 = ElementCompositionPreview.GetElementVisual(HeroImage2);
                visual1.StopAnimation("Opacity");
                visual2.StopAnimation("Opacity");

                HeroImage.Source = ImageHelper.GetImage(imageUrl);
                HeroImage.Opacity = 1;
                visual1.Opacity = 1f;
                
                HeroImage2.Opacity = 0;
                visual2.Opacity = 0f;

                _isHeroImage1Active = true;
                _isHeroTransitionInProgress = false;
                _isFirstImageApplied = true;

                // Fire ambience immediately for the seed image
                _ = ExtractAndApplyAmbienceAsync(HeroImage, $"seed {reason}");
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Hero seed applied ({reason}): {imageUrl}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Hero seed error ({reason}): {ex.Message}");
            }
        }

        private void ResetPageState()
        {
            // State Machine Reset
            _pageLoadState = PageLoadState.Initial;
            _pendingLoadItem = null;
            
            DeselectEpisode();
            _item = null;
            _streamUrl = null;
            _areSourcesVisible = false;
            UpdateWatchlistState(false);

            StopBackgroundSlideshow();
            StopKenBurnsEffect();
            
            _ambienceState = AmbienceState.None;
            _lastAmbienceUrl = null;
            _lastAmbienceSignature = null;
            
            // Preserve the current hero surface to avoid a black flash while the next validated image loads.
            if (HeroImage != null)
            {
                var v1 = ElementCompositionPreview.GetElementVisual(HeroImage);
                if (v1 != null) v1.StopAnimation("Opacity");
            }
            if (HeroImage2 != null)
            {
                var v2 = ElementCompositionPreview.GetElementVisual(HeroImage2);
                if (v2 != null) v2.StopAnimation("Opacity");
            }
            if (_logoVisual != null)
            {
                _logoVisual.Opacity = 0f;
                if (_logoBrush != null) _logoBrush.Surface = null;
                if (_logoSurface != null)
                {
                    _logoSurface.Dispose();
                    _logoSurface = null;
                }
            }
            if (ContentLogoHost != null)
            {
                ContentLogoHost.Visibility = Visibility.Collapsed;
                ContentLogoHost.Opacity = 0;
            }
            // Ensure TitleText is visible again when logo is cleared (logo lived inside TitlePanel)
            if (TitleText != null) TitleText.Visibility = Visibility.Visible;
            
            // Clear Metadata
            ClearMetadataUI();
            if (TitleShimmer != null) TitleShimmer.Height = 56; // Reset shimmer to default height

            if (InfoColumn != null)
            {
                InfoColumn.MaxWidth = 1000; // Reset to default stable MaxWidth
                var visual = ElementCompositionPreview.GetElementVisual(InfoColumn);
                visual.Scale = new System.Numerics.Vector3(1, 1, 1);
                visual.Offset = System.Numerics.Vector3.Zero;
                visual.CenterPoint = System.Numerics.Vector3.Zero;
            }

            // Clear Collections
            Seasons?.Clear();
            CurrentEpisodes?.Clear();
            CastList?.Clear();
            DirectorList?.Clear();
            _addonResults?.Clear();

            _currentStremioVideoId = null;
            _unifiedMetadata = null;
            _slideshowId = null; // Important: Force slideshow reset
            _prebufferUrl = null; // Clear prebuffer URL to prevent logic loops
            _isHeroImage1Active = true; 
            _isFirstImageApplied = false; // [FIX] Move reset here so it's ready for the NEXT nav, but doesn't break CURRENT pre-load/meta sync.
                    _lastAmbienceUrl = null; // Important: Allow fresh extraction on new navigation
                    _lastAmbienceSignature = null;
                    _currentLogoUrl = null; // [FIX] Reset logo URL so it can be re-loaded on subsequent visits
                    _lastAreaColor = default; // Allow ambience to be reapplied after a full page reset
                    if (_themeTintBrush != null) _themeTintBrush.Color = Windows.UI.Color.FromArgb(37, 255, 255, 255);
                    // [REM] Legacy state reset removed
            _ambienceNavigationEpoch++;

            if (CastListView != null) CastListView.ItemsSource = null;
            if (SourcesListView != null) SourcesListView.ItemsSource = null;
            if (AddonSelectorList != null) AddonSelectorList.ItemsSource = null;

            // Visibility Cleanup - Hide interactive units until data is ready
            PlayButton.Visibility = Visibility.Collapsed;
            TrailerButton.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Collapsed;
            CopyLinkButton.Visibility = Visibility.Collapsed;
            
            // [FIX] Initialize readability gradients to 0. 
            // Ambiance logic will fade them IN only if required by the background content.
            if (LocalInfoGradient != null) 
            {
                LocalInfoGradient.Opacity = 0;
                var vG1 = ElementCompositionPreview.GetElementVisual(LocalInfoGradient);
                if (vG1 != null) vG1.Opacity = 0f;
            }
            if (ExtraReadabilityGradient != null)
            {
                ExtraReadabilityGradient.Opacity = 0;
                var vG2 = ElementCompositionPreview.GetElementVisual(ExtraReadabilityGradient);
                if (vG2 != null) vG2.Opacity = 0f;
            }
            if (BottomReadabilityGradient != null)
            {
                BottomReadabilityGradient.Opacity = 0;
                var vG3 = ElementCompositionPreview.GetElementVisual(BottomReadabilityGradient);
                if (vG3 != null) vG3.Opacity = 0f;
            }
            System.Diagnostics.Debug.WriteLine("[AMBIENCE] Readability scrims reset to 0 during page state reset.");
            PlayButtonSubtext.Visibility = Visibility.Collapsed;
            StickyPlayButton.Visibility = Visibility.Collapsed;
            StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
            RestartButton.Visibility = Visibility.Collapsed;
            SourcesPanel.Visibility = Visibility.Collapsed;

            EpisodesPanel.Visibility = Visibility.Collapsed;

            CastSection.Visibility = Visibility.Collapsed;
            DirectorSection.Visibility = Visibility.Collapsed;
            
            // Badge Cleanup
            Badge4K.Visibility = Visibility.Collapsed;
            BadgeRes.Visibility = Visibility.Collapsed;
            BadgeHDR.Visibility = Visibility.Collapsed;
            BadgeSDR.Visibility = Visibility.Collapsed;
            BadgeCodecContainer.Visibility = Visibility.Collapsed;
            if (TechBadgesContent != null) TechBadgesContent.Visibility = Visibility.Collapsed;
            if (MetadataRibbon != null) MetadataRibbon.Opacity = 1; // Keep visible for shimmers
            if (MetadataSeparator != null) MetadataSeparator.Visibility = Visibility.Collapsed;
            if (MetadataShimmer != null) MetadataShimmer.Visibility = Visibility.Collapsed;
            if (MetadataPanel != null) MetadataPanel.Opacity = 0;

            // Shimmer resets
            if (TitleShimmer != null) TitleShimmer.Visibility = Visibility.Collapsed;
            if (ActionBarShimmer != null) ActionBarShimmer.Visibility = Visibility.Collapsed;
            if (OverviewShimmer != null) OverviewShimmer.Visibility = Visibility.Collapsed;
            if (MetadataShimmer != null) MetadataShimmer.Visibility = Visibility.Collapsed;
            if (CastShimmer != null) CastShimmer.Visibility = Visibility.Collapsed;
            if (DirectorShimmer != null) DirectorShimmer.Visibility = Visibility.Collapsed;
            if (EpisodesShimmerPanel != null) EpisodesShimmerPanel.Visibility = Visibility.Collapsed;
            if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = Visibility.Collapsed;

            // Stremio State Cleanup
            _currentStremioVideoId = null;
            _addonResults?.Clear();
            _isSourcesFetchInProgress = false;
            _isCurrentSourcesComplete = false;

            _isHeroTransitionInProgress = false; // Reset transition flag on item switch
            System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Page state reset complete.");
        }

        private void ClearMetadataUI()
        {
            if (TitleText != null) TitleText.Text = "";
            if (YearText != null) YearText.Text = "";
            if (OverviewText != null) OverviewText.Text = "";
            
            SyncIdentityVisibility(false); // Reset to base state
            
            if (GenresText != null) { GenresText.Text = ""; GenresText.Visibility = Visibility.Collapsed; }
            if (RuntimeText != null) RuntimeText.Text = "";
            if (SuperTitleText != null) { SuperTitleText.Text = ""; SuperTitleText.Visibility = Visibility.Collapsed; SuperTitleText.Margin = new Thickness(0); }
        }

        private void ShowShimmer(UIElement shimmer)
        {
            if (shimmer == null) return;
            ElementCompositionPreview.GetElementVisual(shimmer).Opacity = 1f;
            shimmer.Visibility = Visibility.Visible;
        }

        private void SetLoadingState(bool isLoading, IMediaStream? item = null)
        {
            if (isLoading) System.Diagnostics.Debug.WriteLine($"[MediaInfo-Content] State: LOADING (Item: {item?.Title ?? "Unknown"})");
            else System.Diagnostics.Debug.WriteLine($"[MediaInfo-Content] State: LOAD_COMPLETE");
            
            if (isLoading)
            {
                // [FIX] Clear stale technical metadata to prevent bitrates from previous items
                if (BadgeBitrate != null) BadgeBitrate.Visibility = Visibility.Collapsed;
                if (Badge4K != null) Badge4K.Visibility = Visibility.Collapsed;
                if (BadgeRes != null) BadgeRes.Visibility = Visibility.Collapsed;
                if (BadgeHDR != null) BadgeHDR.Visibility = Visibility.Collapsed;
                if (BadgeSDR != null) BadgeSDR.Visibility = Visibility.Collapsed;
                if (BadgeCodecContainer != null) BadgeCodecContainer.Visibility = Visibility.Collapsed;
                UpdateTechnicalSectionVisibility(false);

                // Reset Opacities for Loading (Don't hide parents that hold shimmers)
                if (IdentityContainer != null) ElementCompositionPreview.GetElementVisual(IdentityContainer).Opacity = 1;

                if (TitlePanel != null) 
                { 
                    TitlePanel.Opacity = 1; 
                    TitleText.Opacity = 0; 
                    if (ContentLogoHost != null) ContentLogoHost.Opacity = 0; 
                    
                    // [FIX] Explicitly set visual opacities to 0 to bypass the flicker guard in AnimatePair.
                    ElementCompositionPreview.GetElementVisual(TitleText).Opacity = 0f;
                    if (_logoVisual != null) _logoVisual.Opacity = 0f;
                }
                
                if (MetadataRibbon != null) { MetadataRibbon.Opacity = 1; MetadataPanel.Opacity = 0; }
                if (ActionBarPanel != null) { ActionBarPanel.Opacity = 0; }
                if (OverviewPanel != null) { OverviewPanel.Opacity = 1; OverviewText.Opacity = 0; }
                if (CastSection != null) CastSection.Opacity = 1; 
                if (DirectorSection != null) DirectorSection.Opacity = 1;
                
                // Robust check for series to filter shimmers
                bool isSeries = IsSeriesItem();

                // [FIX] Don't hide parents that hold shimmers if they are about to be shown
                if (isSeries)
                {
                    if (EpisodesPanel != null) 
                    {
                        EpisodesPanel.Opacity = 1;
                        ElementCompositionPreview.GetElementVisual(EpisodesPanel).Opacity = 1f;
                        EpisodesPanel.Visibility = Visibility.Visible;
                    }
                    if (SourcesPanel != null) { SourcesPanel.Opacity = 0; ElementCompositionPreview.GetElementVisual(SourcesPanel).Opacity = 0f; }
                }
                else
                {
                    if (SourcesPanel != null) 
                    {
                        SourcesPanel.Opacity = 1; 
                        ElementCompositionPreview.GetElementVisual(SourcesPanel).Opacity = 1f;
                        SourcesPanel.Visibility = Visibility.Visible;
                    }
                    if (EpisodesPanel != null) { EpisodesPanel.Opacity = 0; ElementCompositionPreview.GetElementVisual(EpisodesPanel).Opacity = 0f; }
                }

                ShowShimmer(TitleShimmer);
                ShowShimmer(MetadataShimmer);
                ShowShimmer(ActionBarShimmer);
                ShowShimmer(OverviewShimmer);
                
                // [FIX] Only show director shimmer for movies/vod (to avoid "2-card glitch" on series)
                if (!isSeries) ShowShimmer(DirectorShimmer);
                else DirectorShimmer.Visibility = Visibility.Collapsed;

                ShowShimmer(CastShimmer);

                // Removed this.UpdateLayout() - let XAML handle measuring in its own pass
                
                // [FIX] Force scaling sync IMMEDIATELY to anchor shimmers to the floor
                UpdateRealTimeScaling(ActualWidth, ActualHeight, ActualWidth >= LayoutAdaptiveThreshold);

                // Early Shimmer Trigger (Panels stay Collapsed until reveal)
                if (item != null)
                {
                    if (isSeries)
                    {
                        ShowShimmer(EpisodesShimmerPanel);
                    }
                    else // It's a movie/vod
                    {
                        ShowShimmer(SourcesShimmerPanel);
                    }
                }

                TechBadgesContent.Visibility = Visibility.Collapsed;
                ElementCompositionPreview.GetElementVisual(TechBadgesContent).Opacity = 0f;
            }
        }

        private void ImmediateRevealContent()
        {
            System.Diagnostics.Debug.WriteLine($"[MediaInfo-Content] Reveal: IMMEDIATE");
            
            void ShowImmediate(UIElement content, UIElement shimmer, bool forceHide = false)
            {
                if (content == null) return;
                
                if (forceHide)
                {
                    content.Visibility = Visibility.Collapsed;
                    ElementCompositionPreview.GetElementVisual(content).Opacity = 0f;
                    if (shimmer != null) shimmer.Visibility = Visibility.Collapsed;
                    return;
                }

                content.Visibility = Visibility.Visible;
                var visual = ElementCompositionPreview.GetElementVisual(content);
                visual.Opacity = 1f;
                content.Opacity = 1;

                if (shimmer != null)
                {
                    shimmer.Visibility = Visibility.Collapsed;
                    ElementCompositionPreview.GetElementVisual(shimmer).Opacity = 0f;
                }
            }

            // TitlePanel is always visible; ContentLogoHost and TitleText are toggled inside it.
            // Show the active identity element (logo or title text) and hide the shimmer.
            bool hasLogo = ContentLogoHost != null && ContentLogoHost.Visibility == Visibility.Visible;
            ShowImmediate(TitlePanel, TitleShimmer);
            
            // [FIX] Reveal both Logo and Text if they are active, or just the text if no logo.
            if (ContentLogoHost != null) ContentLogoHost.Opacity = 1;
            if (_logoVisual != null && hasLogo) _logoVisual.Opacity = 1f;
            if (TitleText != null) TitleText.Opacity = 1;

            if (hasLogo) 
            {
                if (ContentLogoHost != null) ShowImmediate(ContentLogoHost, null);
                
                // [FIX] Only forceHide if NO episode is selected. 
                // If an episode is selected, we want its title visible under the logo.
                ShowImmediate(TitleText, null, forceHide: (_selectedEpisode == null));
            }
            else
            {
                ShowImmediate(TitleText, null);
                if (ContentLogoHost != null) ShowImmediate(ContentLogoHost, null, forceHide: true);
            }
            ShowImmediate(MetadataPanel, MetadataShimmer);
            if (MetadataRibbon != null) MetadataRibbon.Opacity = 1;
            ShowImmediate(ActionBarPanel, ActionBarShimmer);
            
            // [FIX] Explicitly reveal OverviewText as it was reset to Opacity=0 in SetLoadingState
            ShowImmediate(OverviewPanel, OverviewShimmer, string.IsNullOrWhiteSpace(_unifiedMetadata?.Overview));
            if (OverviewText != null) ShowImmediate(OverviewText, null, string.IsNullOrWhiteSpace(_unifiedMetadata?.Overview));
            
            bool hasDirectors = DirectorSection?.Visibility == Visibility.Visible;
            bool hasCast = CastSection?.Visibility == Visibility.Visible;

            ShowImmediate(DirectorSection, DirectorShimmer, !hasDirectors);
            ShowImmediate(CastSection, CastShimmer, !hasCast);
            ShowImmediate(TechBadgesContent, null);

            // Sync badge section visibility for cached items
            UpdateTechnicalSectionVisibility(HasVisibleBadges());

            // Side panels reveal (Wide Mode only)
            if (ActualWidth >= LayoutAdaptiveThreshold && _unifiedMetadata != null)
            {
                if (_unifiedMetadata.IsSeries)
                {
                    if (EpisodesPanel != null) { EpisodesPanel.Visibility = _areSourcesVisible ? Visibility.Collapsed : Visibility.Visible; ElementCompositionPreview.GetElementVisual(EpisodesPanel).Opacity = _areSourcesVisible ? 0f : 1f; }
                    if (EpisodesShimmerPanel != null) { EpisodesShimmerPanel.Visibility = Visibility.Collapsed; ElementCompositionPreview.GetElementVisual(EpisodesShimmerPanel).Opacity = 0f; }
                    
                    if (SourcesPanel != null) { SourcesPanel.Visibility = _areSourcesVisible ? Visibility.Visible : Visibility.Collapsed; ElementCompositionPreview.GetElementVisual(SourcesPanel).Opacity = _areSourcesVisible ? 1f : 0f; }
                    
                    if (_areSourcesVisible)
                    {
                        if (_isSourcesFetchInProgress)
                        {
                            if (SourcesShimmerPanel != null) { SourcesShimmerPanel.Visibility = Visibility.Visible; ElementCompositionPreview.GetElementVisual(SourcesShimmerPanel).Opacity = 1f; }
                            if (SourcesListView != null) { SourcesListView.Visibility = Visibility.Collapsed; ElementCompositionPreview.GetElementVisual(SourcesListView).Opacity = 0f; }
                            if (AddonSelectorList != null) { AddonSelectorList.Visibility = Visibility.Collapsed; ElementCompositionPreview.GetElementVisual(AddonSelectorList).Opacity = 0f; }
                        }
                        else
                        {
                            if (SourcesShimmerPanel != null) { SourcesShimmerPanel.Visibility = Visibility.Collapsed; ElementCompositionPreview.GetElementVisual(SourcesShimmerPanel).Opacity = 0f; }
                            if (SourcesListView != null) { SourcesListView.Visibility = Visibility.Visible; ElementCompositionPreview.GetElementVisual(SourcesListView).Opacity = 1f; }
                            if (AddonSelectorList != null) { AddonSelectorList.Visibility = Visibility.Visible; ElementCompositionPreview.GetElementVisual(AddonSelectorList).Opacity = 1f; }
                        }
                    }
                }
                else
                {
                    if (EpisodesPanel != null) { EpisodesPanel.Visibility = Visibility.Collapsed; ElementCompositionPreview.GetElementVisual(EpisodesPanel).Opacity = 0f; }
                    
                    if (SourcesPanel != null) { SourcesPanel.Visibility = Visibility.Visible; ElementCompositionPreview.GetElementVisual(SourcesPanel).Opacity = 1f; }
                    
                    if (_isSourcesFetchInProgress)
                    {
                        if (SourcesShimmerPanel != null) { SourcesShimmerPanel.Visibility = Visibility.Visible; ElementCompositionPreview.GetElementVisual(SourcesShimmerPanel).Opacity = 1f; }
                        if (SourcesListView != null) { SourcesListView.Visibility = Visibility.Collapsed; ElementCompositionPreview.GetElementVisual(SourcesListView).Opacity = 0f; }
                        if (AddonSelectorList != null) { AddonSelectorList.Visibility = Visibility.Collapsed; ElementCompositionPreview.GetElementVisual(AddonSelectorList).Opacity = 0f; }
                    }
                    else
                    {
                        if (SourcesShimmerPanel != null) { SourcesShimmerPanel.Visibility = Visibility.Collapsed; ElementCompositionPreview.GetElementVisual(SourcesShimmerPanel).Opacity = 0f; }
                        if (SourcesListView != null) { SourcesListView.Visibility = Visibility.Visible; ElementCompositionPreview.GetElementVisual(SourcesListView).Opacity = 1f; }
                        if (AddonSelectorList != null) { AddonSelectorList.Visibility = Visibility.Visible; ElementCompositionPreview.GetElementVisual(AddonSelectorList).Opacity = 1f; }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-ImmediateReveal] END - CastSection: Visibility={CastSection?.Visibility}, AH={CastSection?.ActualHeight}");
        }

        private void StaggeredRevealContent()
        {
            System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-Reveal] START: ScrollViewerAH={RootScrollViewer?.ActualHeight}, ContentGridAH={ContentGrid?.ActualHeight}, ContentGridH={ContentGrid?.Height}, ContentGridVA={ContentGrid?.VerticalAlignment}");
            System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-Reveal] InfoContainer: VA={InfoContainer?.VerticalAlignment}, AH={InfoContainer?.ActualHeight}");
            System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-Reveal] InfoContainerInner: VA={InfoContainerInner?.VerticalAlignment}, AH={InfoContainerInner?.ActualHeight}");
            System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-Reveal] InfoColumn: VA={InfoColumn?.VerticalAlignment}, AH={InfoColumn?.ActualHeight}, MaxH={InfoColumn?.MaxHeight}, Margin={InfoColumn?.Margin}");
            System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-Reveal] TitleShimmer: Visibility={TitleShimmer?.Visibility}, AH={TitleShimmer?.ActualHeight}");
            System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-Reveal] CastSection: Visibility={CastSection?.Visibility}, AH={CastSection?.ActualHeight}, MaxH={CastSection?.MaxHeight}, VA={CastSection?.VerticalAlignment}");
            
            // helper for cross-fade + slide-up
            void AnimatePair(UIElement content, UIElement shimmer, int delay)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 3: AnimatePair START (Content={content.GetType().Name}, Shimmer={shimmer.GetType().Name}, Delay={delay})");
                
                if (content == null || shimmer == null) return;

                var visualContent = ElementCompositionPreview.GetElementVisual(content);
                var visualShimmer = ElementCompositionPreview.GetElementVisual(shimmer);

                System.Diagnostics.Debug.WriteLine($"[MediaInfo-Trace] AnimatePair START");

                // [FLICKER GUARD] If content is already fully visible and shimmer is gone, skip animation
                if (visualContent.Opacity >= 1f && (shimmer.Visibility == Visibility.Collapsed || visualShimmer.Opacity <= 0f))
                {
                    return;
                }

                ElementCompositionPreview.SetIsTranslationEnabled(content, true);

                // 1. Fade In Content
                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f);
                fadeIn.InsertKeyFrame(1f, 1f, _compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1f)));
                fadeIn.Duration = TimeSpan.FromMilliseconds(250);
                fadeIn.DelayTime = TimeSpan.FromMilliseconds(delay);

                visualContent.Opacity = 0f;
                visualContent.StartAnimation("Opacity", fadeIn);
                content.Opacity = 1;

                // [FIX] Reset internal opacities for text blocks that might be stuck at 0
                if (content is Panel p)
                {
                    foreach (var child in p.Children)
                    {
                        if (child is TextBlock tb) tb.Opacity = 1;
                        if (child is ListView lv) lv.Opacity = 1;
                        if (child is Panel innerP) 
                        {
                            foreach(var c in innerP.Children) 
                            {
                                if (c is TextBlock innerTb) innerTb.Opacity = 1;
                                if (c is ListView innerLv) innerLv.Opacity = 1;
                            }
                        }
                    }
                }

                // 3. Fade Out Shimmer (Snappier duration)
                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, 1f);
                fadeOut.InsertKeyFrame(1f, 0f, _compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.3f, 0f), new System.Numerics.Vector2(0f, 1f)));
                fadeOut.Duration = TimeSpan.FromMilliseconds(180);
                fadeOut.DelayTime = TimeSpan.FromMilliseconds(delay);

                var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                visualShimmer.StartAnimation("Opacity", fadeOut);
                batch.Completed += (s, e) => 
                {
                    shimmer.Visibility = Visibility.Collapsed;
                };
                batch.End();
            }

            // Sequence: Identity -> Metadata -> Actions -> Overview -> Cast
            // *** STABILITY FIX: Wait for Logo if needed, and sync cross-fade perfectly ***
            _ = RevealIdentityAsync();

            async Task RevealIdentityAsync()
            {
                // [FIX] Always use TitlePanel as the reveal target because it contains both Logo and TitleText.
                UIElement idContent = TitlePanel;

                // However, we still want to wait for the logo if it's visible
                bool hasLogo = ContentLogoHost != null && ContentLogoHost.Visibility == Visibility.Visible;
                if (_logoReadyTcs != null && hasLogo)
                {
                    var completedTask = await Task.WhenAny(_logoReadyTcs.Task, Task.Delay(2000));
                    bool logoLoaded = (completedTask == _logoReadyTcs.Task) && await _logoReadyTcs.Task;
                    
                    if (!logoLoaded)
                    {
                        System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Logo load FAILED or TIMED OUT. Falling back to TitleText.");
                        hasLogo = false;
                        if (ContentLogoHost != null) ContentLogoHost.Visibility = Visibility.Collapsed;
                    }
                }

                // [TITLE REDESIGN] Use unified identity logic
                SyncIdentityVisibility(_selectedEpisode != null); // [FIX] Respect current episode selection

                // [FIX] Ensure parent TitlePanel is ready and shimmer is cleared immediately to prevent ghosting.
                if (TitlePanel != null)
                {
                    TitlePanel.Opacity = 1;
                    ElementCompositionPreview.GetElementVisual(TitlePanel).Opacity = 1f;

                    if (TitleShimmer != null)
                    {
                        TitleShimmer.Visibility = Visibility.Collapsed;
                        ElementCompositionPreview.GetElementVisual(TitleShimmer).Opacity = 0f;
                    }
                }

                // [FIX] Ensure visual opacity is 0 before calling AnimatePair.
                // This bypasses the flicker guard if the shimmer was hidden early.
                ElementCompositionPreview.GetElementVisual(idContent).Opacity = 0f;
                
                AnimatePair(idContent, TitleShimmer, 0); // 0 delay for identity to make it snap in
            }            
            // Sync badge section alignment with current badge state
            bool showExtra = HasVisibleBadges();
            UpdateTechnicalSectionVisibility(showExtra);

            // 1. Identity reveal started above
            
            // 2. Metadata Section (Badges -> Separator -> Basic Info)
            if (showExtra)
            {
                AnimatePair(TechBadgesContent, TechBadgesShimmer, 75);
                
                // Fade in separator dot
                var fadeDot = _compositor.CreateScalarKeyFrameAnimation();
                fadeDot.InsertKeyFrame(0f, 0f);
                fadeDot.InsertKeyFrame(1f, 1f);
                fadeDot.Duration = TimeSpan.FromMilliseconds(500);
                fadeDot.DelayTime = TimeSpan.FromMilliseconds(100);
                ElementCompositionPreview.GetElementVisual(MetadataSeparator).StartAnimation("Opacity", fadeDot);
                MetadataSeparator.Opacity = 1;
            }

            AnimatePair(MetadataPanel, MetadataShimmer, 125); // Slightly delayed from badges
            MetadataRibbon.Opacity = 1;

            AnimatePair(ActionBarPanel, ActionBarShimmer, 175);
            AnimatePair(OverviewPanel, OverviewShimmer, 225);

            if (DirectorSection.Visibility == Visibility.Visible)
            {
                AnimatePair(DirectorSection, DirectorShimmer, 175);
            }
            else
            {
                DirectorShimmer.Visibility = Visibility.Collapsed;
            }

            if (CastSection.Visibility == Visibility.Visible)
            {
                AnimatePair(CastSection, CastShimmer, 250);
            }
            else
            {
                CastShimmer.Visibility = Visibility.Collapsed;
            }

            // [NEW] Premium Cross-Fade for Side Panels (Enabled in both Wide and Narrow modes)
            System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 4: Side Panel Reveal Check. EpisodesVisible={EpisodesPanel?.Visibility}, ShimmerVisible={EpisodesShimmerPanel?.Visibility}");
            
            // [FIX] Target the internal ListView and Shimmer for cross-fade, NOT the parent panel.
            // If we fade the parent panel, we fade out the shimmer too!
            if (IsSeriesItem())
            {
                if (EpisodesListView != null && EpisodesShimmerPanel != null && EpisodesShimmerPanel.Visibility == Visibility.Visible)
                {
                    System.Diagnostics.Debug.WriteLine("[MediaInfo-Flow] STEP 5: Animating EpisodesListView Reveal");
                    AnimatePair(EpisodesListView, EpisodesShimmerPanel, 300);
                }
            }
            else // Movie/Sources
            {
                if (SourcesListView != null && SourcesShimmerPanel != null && SourcesShimmerPanel.Visibility == Visibility.Visible)
                {
                    bool hasSources = _addonResults != null && _addonResults.Any(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0);
                    
                    if (_isSourcesFetchInProgress && !hasSources)
                    {
                        System.Diagnostics.Debug.WriteLine("[MediaInfo-Flow] STEP 6: Maintaining SourcesShimmerPanel Visibility");
                        
                        // [FIX] Don't reset opacity to 0. Just make sure it's 1 and visible.
                        var visualShimmer = ElementCompositionPreview.GetElementVisual(SourcesShimmerPanel);
                        visualShimmer.Opacity = 1f;
                        SourcesShimmerPanel.Visibility = Visibility.Visible;
                        
                        SourcesListView.Visibility = Visibility.Collapsed;
                        if (AddonSelectorList != null) AddonSelectorList.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[MediaInfo-Flow] STEP 6: Animating SourcesListView Reveal");
                        AnimatePair(SourcesListView, SourcesShimmerPanel, 300);
                        
                        // Also reveal addon selector if we have sources
                        if (AddonSelectorList != null)
                        {
                            var visualAddons = ElementCompositionPreview.GetElementVisual(AddonSelectorList);
                            if (visualAddons.Opacity < 1f)
                            {
                                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                                fadeIn.InsertKeyFrame(1f, 1f);
                                fadeIn.Duration = TimeSpan.FromMilliseconds(250);
                                fadeIn.DelayTime = TimeSpan.FromMilliseconds(300);
                                visualAddons.StartAnimation("Opacity", fadeIn);
                                AddonSelectorList.Visibility = Visibility.Visible;
                            }
                        }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[ShimmerDebug-Reveal] END - CastSection: Visibility={CastSection?.Visibility}, AH={CastSection?.ActualHeight}");
        }

        private void ContentLogo_ImageOpened(object sender, RoutedEventArgs e)
        {
            _logoReadyTcs?.TrySetResult(true);
            System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Logo opened and decoded.");
            
            // Re-measure now
            DispatcherQueue.TryEnqueue(() => 
            {
                // legacy AdjustTitleShimmer call removed
            });
        }

        private void ContentLogo_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _logoReadyTcs?.TrySetResult(false);
            System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Logo load failed: {e.ErrorMessage}");
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
            
            // [FIX] Ensure we show at least 5 cards during loading/initial state
            int effectiveCount = count <= 0 ? 5 : count;

            if (CastShimmer.Children.Count >= 2 && CastShimmer.Children[1] is StackPanel horizontalPanel)
            {
                horizontalPanel.Children.Clear();
                int displayCount = Math.Min(effectiveCount, 8); 

                for (int i = 0; i < displayCount; i++)
                {
                    var itemStack = new StackPanel { Spacing = 6 };
                    itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 100, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left });
                    itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left });
                    
                    horizontalPanel.Children.Add(itemStack);
                }
            }
        }

        private void AdjustDirectorShimmer(int count)
        {
            if (DirectorShimmer == null) return;
            
            // [FIX] Ensure we show at least 2 cards during loading
            int effectiveCount = count <= 0 ? 2 : count;

            if (DirectorShimmer.Children.Count >= 2 && DirectorShimmer.Children[1] is StackPanel horizontalPanel)
            {
                horizontalPanel.Children.Clear();
                int displayCount = Math.Min(effectiveCount, 4); 

                for (int i = 0; i < displayCount; i++)
                {
                    var itemStack = new StackPanel { Spacing = 6 };
                    itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 100, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left });
                    itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left });
                    
                    horizontalPanel.Children.Add(itemStack);
                }
            }
        }

        private void UpdateTechnicalSectionVisibility(bool hasExtra)
        {
            if (MetadataRibbon == null || MetadataSeparator == null || TechBadgesContent == null) return;

            TechBadgesContent.Visibility = hasExtra ? Visibility.Visible : Visibility.Collapsed;
            MetadataSeparator.Visibility = hasExtra ? Visibility.Visible : Visibility.Collapsed;
            
            if (hasExtra)
            {
                var visual = ElementCompositionPreview.GetElementVisual(TechBadgesContent);
                if (visual != null) visual.Opacity = 1f;
            }
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

                    System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Starting KenBurns effect (using slide transition).");
                    StartKenBurnsEffect();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Animation ERROR: {ex.Message}");
                    StartKenBurnsEffect();
                }
            });
        }

        private void StartKenBurnsEffect(UIElement target = null)
        {
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
                visual.CenterPoint = new System.Numerics.Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0);
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

        private void StopBackgroundSlideshow()
        {
            if (_slideshowTimer != null)
            {
                _slideshowTimer.Stop();
                _slideshowTimer = null;
            }
            _backdropUrls?.Clear();
            _isHeroImage1Active = true;
            _lastKenBurnsElement = null;
            _backdropKeys.Clear();
            _lastVisualSignature = null;
            _validatedBackdrops.Clear();
            
            // [FIX] Don't null HeroImage.Source here to prevent flickers. 
            // The next item's seeding or ResetPageState already handles opacity.
        }

        private async Task<string> CalculateVisualSignatureAsync(Image img)
        {
            if (img == null || img.Source == null || img.XamlRoot == null || img.ActualWidth <= 1 || img.ActualHeight <= 1) return null;

            string url = GetImageSourceUrl(img);
            if (!string.IsNullOrEmpty(url) && _urlToSignatureCache.TryGetValue(url, out string cached))
            {
                return cached;
            }

            try
            {
                var rtb = new RenderTargetBitmap();
                
                // [STABILITY] 16x16 is stable for signature-based comparison
                await rtb.RenderAsync(img, 16, 16);
                
                var pixelBuffer = await rtb.GetPixelsAsync();
                var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixelBuffer);
                
                if (pixels.Count() < 256) return "empty";

                // Average Luma calculation for A-Hash
                long totalLuma = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    totalLuma += (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
                }
                
                int avgLuma = (int)(totalLuma / (pixels.Length / 4));

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int luma = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
                    sb.Append(luma > avgLuma ? "1" : "0");
                }
                
                string signature = sb.ToString();
                if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(signature))
                {
                    _urlToSignatureCache[url] = signature;
                }
                return signature;
            }
            catch { return null; }
        }

        private bool IsSignatureSimilar(string sig1, string sig2, double threshold = 0.90)
        {
            if (string.IsNullOrEmpty(sig1) || string.IsNullOrEmpty(sig2)) return false;
            if (sig1 == sig2) return true;
            if (sig1.Length != sig2.Length) return false;

            int matches = 0;
            for (int i = 0; i < sig1.Length; i++)
            {
                if (sig1[i] == sig2[i]) matches++;
            }

            double similarity = (double)matches / sig1.Length;
            // System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Similarity Check: {similarity:P2}");
            return similarity >= threshold;
        }

        private string GetNormalizedImageKey(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            try
            {
                string lowerUrl = url.ToLowerInvariant();

                // 1. Handle MetaHub (MetaHub uses IMDb ID in path)
                // Example: https://images.metahub.space/background/medium/tt32357218/img
                if (lowerUrl.Contains("metahub.space"))
                {
                    // Look for ttID patterns
                    var match = System.Text.RegularExpressions.Regex.Match(lowerUrl, @"(tt\d+)");
                    if (match.Success) return $"metahub_{match.Value}";
                }

                // 2. Handle TMDB (Normalize resolutions and focus on the hash)
                // Example: https://image.tmdb.org/t/p/original/nHxWyy18SvAZ8jJeemtS8k1UNjM.jpg
                if (lowerUrl.Contains("tmdb.org"))
                {
                    int lastSlash = lowerUrl.LastIndexOf('/');
                    if (lastSlash >= 0)
                    {
                        string filename = lowerUrl.Substring(lastSlash + 1);
                        int dot = filename.LastIndexOf('.');
                        if (dot >= 0) filename = filename.Substring(0, dot);
                        return filename;
                    }
                }

                // 3. Generic Handler (Clean common noise)
                int absoluteLastSlash = url.LastIndexOf('/');
                if (absoluteLastSlash >= 0)
                {
                    string filename = url.Substring(absoluteLastSlash + 1);
                    int qMark = filename.IndexOf('?');
                    if (qMark >= 0) filename = filename.Substring(0, qMark);
                    
                    // If filename is too generic (like "img", "background", "poster"), include part of the path
                    if (filename.Length < 5 || filename.StartsWith("img") || filename.StartsWith("background"))
                    {
                        string remaining = url.Substring(0, absoluteLastSlash);
                        int secondLastSlash = remaining.LastIndexOf('/');
                        if (secondLastSlash >= 0)
                        {
                            return (remaining.Substring(secondLastSlash + 1) + "_" + filename).ToLowerInvariant();
                        }
                    }

                    return filename.ToLowerInvariant();
                }
            }
            catch { }
            return url.ToLowerInvariant();
        }

        private async void HeroImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is Image img)
            {
                string openedUrl = GetImageSourceUrl(img);
                System.Diagnostics.Debug.WriteLine($"[AMBIENCE][OPEN] url={openedUrl ?? "<null>"}");

                // Trigger unified extraction logic
                // The engine handles waiting for render-ready and decode.
                await ExtractAndApplyAmbienceAsync(img, "image opened");
            }
        }

        private void HeroImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (sender is Image img)
            {
                // MetaHub Failover Logic: Try alternative domain before giving up
                string? failingUrl = (img.Source as BitmapImage)?.UriSource?.ToString();
                if (!string.IsNullOrEmpty(failingUrl) && failingUrl.Contains("metahub.space"))
                {
                    // [FIX] Prevent infinite MetaHub retry loops
                    int retryCount = (img.Tag as int?) ?? 0;
                    if (retryCount == -1)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Already failed fallback for: {failingUrl}. Stopping.");
                        return;
                    }

                    if (retryCount >= 1)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Giving up on MetaHub after retry for: {failingUrl}");
                        img.Tag = -1; // Mark as "tried alternative domain and failed"
                    }
                    else
                    {
                        string retryUrl = failingUrl;
                        if (failingUrl.Contains("live.metahub.space"))
                            retryUrl = failingUrl.Replace("live.metahub.space", "images.metahub.space");
                        else if (failingUrl.Contains("images.metahub.space"))
                            retryUrl = failingUrl.Replace("images.metahub.space", "live.metahub.space");

                        if (retryUrl != failingUrl)
                        {
                            img.Tag = retryCount + 1;
                            System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Retrying MetaHub ({img.Tag}) with alternative domain: {retryUrl}");
                            img.Source = new BitmapImage(new Uri(retryUrl));
                            return; // Wait for next load result
                        }
                    }
                }
                
                // If we have more images, try to move to the next one immediately
                if (_backdropUrls != null && _backdropUrls.Count > 1)
                {
                    System.Diagnostics.Debug.WriteLine("[SLIDESHOW] Skipping to next image due to load failure.");
                    img.Tag = 0; // Reset for next image
                    _slideshowTimer?.Stop();
                    _slideshowTimer?.Start(); // Immediate-ish tick
                    
                    // Trigger manual next if possible
                    _currentBackdropIndex = (_currentBackdropIndex + 1) % _backdropUrls.Count;
                    RotateBackdrop();
                }
                else
                {
                    // Single-image failure: keep UI alive with poster fallback instead of blank hero.
                    string fallbackPoster = _unifiedMetadata?.PosterUrl ?? _item?.PosterUrl;
                    if (!string.IsNullOrWhiteSpace(fallbackPoster))
                    {
                        string currentUrl = (img.Source as BitmapImage)?.UriSource?.ToString();
                        if (!string.Equals(currentUrl, fallbackPoster, StringComparison.OrdinalIgnoreCase))
                        {
                            img.Tag = -1; // Mark that we are now trying the absolute final fallback
                            img.Source = new BitmapImage(new Uri(fallbackPoster));
                            img.Opacity = 1;
                            System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Applied poster fallback after image failure: {fallbackPoster}");
                        }
                    }
                }
            }
        }

        private void RotateBackdrop()
        {
            _slideshowTimer?.Stop();
            _slideshowTimer?.Start();
        }

        // [REM] Removed CancelPendingHeroAmbience - redundant with unified engine

        private Image GetActiveHeroImage()
        {
            return _isHeroImage1Active ? HeroImage : HeroImage2;
        }

        private static string GetImageSourceUrl(Image image)
        {
            return (image?.Source as BitmapImage)?.UriSource?.ToString();
        }

        // [REM] Obsolete ambience methods removed

        private async Task ExtractAndApplyAmbienceAsync(Image sourceImage = null, string sourceLabel = null)
        {
            var img = sourceImage ?? GetActiveHeroImage();
            if (img == null || img.Source == null) return;

            int startEpoch = Volatile.Read(ref _ambienceNavigationEpoch);
            string currentUrl = GetImageSourceUrl(img);
            bool isValidated = sourceLabel?.Contains("validated") == true;

            // 1. Redundancy Guard
            string currentSignature = BuildAmbienceSignature(currentUrl, sourceLabel);
            if (!string.IsNullOrEmpty(currentSignature) && _lastAmbienceSignature == currentSignature) return; 

            await _ambienceLock.WaitAsync();
            try
            {
                if (startEpoch != Volatile.Read(ref _ambienceNavigationEpoch)) return;
                if (_lastAmbienceSignature == currentSignature) return;

                // 3. Wait for Render-Ready
                var timeout = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
                while ((img.ActualWidth <= 1 || img.ActualHeight <= 1) && DateTime.UtcNow < timeout)
                {
                    await Task.Delay(32);
                    if (startEpoch != Volatile.Read(ref _ambienceNavigationEpoch)) return;
                    if (GetImageSourceUrl(img) != currentUrl) return; 
                }

                if (img.ActualWidth <= 1 || img.ActualHeight <= 1) return;

                // 4. Capture and Wait for Decode
                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(img);

                int decodeRetries = 0;
                while (rtb.PixelWidth == 0 && decodeRetries < 5)
                {
                    await Task.Delay(64);
                    if (startEpoch != Volatile.Read(ref _ambienceNavigationEpoch)) return;
                    await rtb.RenderAsync(img);
                    decodeRetries++;
                }

                if (rtb.PixelWidth == 0 || startEpoch != Volatile.Read(ref _ambienceNavigationEpoch)) return;

                var pixelBuffer = await rtb.GetPixelsAsync();
                var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixelBuffer);

                // 5. Extract colors
                var colors = ImageHelper.ExtractColorsFromPixels(pixels, rtb.PixelWidth, rtb.PixelHeight, (_item?.PosterUrl ?? "hero"));
                var areaColor = ImageHelper.ExtractAreaAverageColor(pixels, rtb.PixelWidth, rtb.PixelHeight, 0.0, 0.2, 0.4, 0.6);

                // 6. Apply and Update State
                _lastAmbienceUrl = currentUrl;
                _lastAmbienceSignature = currentSignature;
                if (isValidated) _ambienceState = AmbienceState.Stable;
                else if (_ambienceState == AmbienceState.None) _ambienceState = AmbienceState.Provisional;

                ApplyPremiumAmbience(colors.Primary, areaColor, sourceLabel);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AMBIENCE] Extraction error: {ex.Message}");
            }
            finally
            {
                _ambienceLock.Release();
            }
        }

        private Color _lastAreaColor;
        private static string BuildAmbienceSignature(string url, string sourceLabel)
        {
            return $"{url ?? "<null>"}|{NormalizeAmbienceSourceLabel(sourceLabel)}";
        }

        private static string NormalizeAmbienceSourceLabel(string sourceLabel)
        {
            if (string.IsNullOrWhiteSpace(sourceLabel)) return "<null>";

            if (sourceLabel.StartsWith("provisional", StringComparison.OrdinalIgnoreCase))
            {
                return "provisional";
            }

            if (sourceLabel.StartsWith("validated", StringComparison.OrdinalIgnoreCase))
            {
                return "validated";
            }

            return sourceLabel.Trim().ToLowerInvariant();
        }

        private void ApplyPremiumAmbience(Color primary, Color areaBackground, string sourceLabel = null)
        {
            _lastAreaColor = areaBackground;
            _lastApplyPrimary = primary;
            _lastApplyArea = areaBackground;
            _primaryColorHex = $"#{primary.A:X2}{primary.R:X2}{primary.G:X2}{primary.B:X2}";
            bool isProvisionalSource = !string.IsNullOrWhiteSpace(sourceLabel) &&
                sourceLabel.StartsWith("provisional", StringComparison.OrdinalIgnoreCase);

            System.Diagnostics.Debug.WriteLine(
                $"[AMBIENCE] Apply start | primary={primary.R},{primary.G},{primary.B} | area={areaBackground.R},{areaBackground.G},{areaBackground.B}");
            
            // Final safety check to ensure _primaryColorHex is a valid hex color string
            if (string.IsNullOrEmpty(_primaryColorHex) || _primaryColorHex == "#00000000" || _primaryColorHex.Contains("Unknown"))
            {
                _primaryColorHex = "#FFFFFFFF"; // Fallback to safe white (with full opacity)
            }

            try
            {
                // 1. Prepare Base Tints for UI elements
                var btnTint = Color.FromArgb(50, primary.R, primary.G, primary.B);
                var mixedPanelTint = Color.FromArgb(180, (byte)(primary.R * 0.2), (byte)(primary.G * 0.2), (byte)(primary.B * 0.2));
                var playButtonTint = Color.FromArgb(90, primary.R, primary.G, primary.B);
                var playBorderTint = Color.FromArgb(140, primary.R, primary.G, primary.B);
                
                // 2. Analyze Background Area
                double areaL = (0.2126 * areaBackground.R + 0.7152 * areaBackground.G + 0.0722 * areaBackground.B) / 255.0;
                
                // Calculate vibrancy (Max - Min difference) to detect neutral/greyish backgrounds
                int maxV = Math.Max(areaBackground.R, Math.Max(areaBackground.G, areaBackground.B));
                int minV = Math.Min(areaBackground.R, Math.Min(areaBackground.G, areaBackground.B));
                int vibrancy = maxV - minV;

                // 3. Calculate Contrast-Safe Text Colors using APCA
                Color headerColor, descriptionColor;

                if (vibrancy < 30)
                {
                    // Neutral background (Grey/White/Black): Pick pure White or Dark-Grey for maximum clarity
                    double whiteLc = Math.Abs(ImageHelper.GetContrastAPCA(Color.FromArgb(255, 255, 255, 255), areaBackground));
                    double darkLc = Math.Abs(ImageHelper.GetContrastAPCA(Color.FromArgb(255, 20, 20, 22), areaBackground));
                    
                    // "Safety-First" Logic: 
                    // We only switch to Dark text if it offers significantly better AND safe contrast (Lc > 50).
                    // If contrast is mediocre (Lc < 50) for both, we favor White because 
                    // we can protect White text with our darkening gradients. 
                    // Darkening the background to protect Dark text is counter-productive.
                    bool useDarkText = darkLc > (whiteLc + 15) && darkLc > 50;
                    
                    if (!useDarkText)
                    {
                        headerColor = Color.FromArgb(255, 255, 255, 255);
                        descriptionColor = Color.FromArgb(255, 224, 224, 224);
                    }
                    else
                    {
                        headerColor = Color.FromArgb(255, 20, 20, 22);
                        descriptionColor = Color.FromArgb(255, 55, 55, 60);
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[AMBIENCE] Neutral branch | L:{areaL:F2} | WhiteLc: {whiteLc:F1} | DarkLc: {darkLc:F1} | Selected: {(headerColor.R > 200 ? "White" : "Dark")}");
                }
                else
                {
                    // Colorful background: Use primary-tinted text with best APCA direction
                    headerColor = ImageHelper.GetContrastSafeColor(primary, areaBackground, 92);
                    descriptionColor = ImageHelper.GetContrastSafeColor(headerColor, areaBackground, 72);
                    
                    double finalLc = Math.Abs(ImageHelper.GetContrastAPCA(headerColor, areaBackground));
                    System.Diagnostics.Debug.WriteLine($"[AMBIENCE] Colorful branch | FinalLc: {finalLc:F1}");
                }

                // DIAGNOSTIC LOG (Enhanced)
                System.Diagnostics.Debug.WriteLine($"[AMBIENCE] Area:{areaBackground.R},{areaBackground.G},{areaBackground.B} | L:{areaL:F2} | V:{vibrancy} | Text:{headerColor.R},{headerColor.G},{headerColor.B}");

                // 4. Adaptive Cinematic Gradient Scaling (The "Just Enough" Logic)
                // We scale the protective gradient based on how much contrast the COLOR choice provides.
                double rawLc = Math.Abs(ImageHelper.GetContrastAPCA(headerColor, areaBackground));
                
                // VIBRANCY BONUS: High vibrancy colors provide better perceptual separation.
                // We add a virtual contrast bonus to Lc to reduce gradient intensity on vibrant scenes.
                double vibrancyBonus = Math.Clamp((vibrancy - 40) / 4.0, 0, 25);
                double lc = rawLc + vibrancyBonus;
                
                bool isDarkText = headerColor.R < 100; // Text is dark (inverse polarity)

                // If we are using Dark text, we MUST suppress the darkening gradient 
                // because darkening the background will reduce contrast for dark text.
                double contrastFactor;
                double powerScale;

                if (isDarkText)
                {
                    contrastFactor = 0.15; // Minimum background darkening
                    powerScale = 1.8;      // Very soft curve
                }
                else
                {
                    // For White/Light text, use Lc-based protection.
                    // Slightly more aggressive slope to protect busy mid-tones.
                    contrastFactor = Math.Clamp((105 - lc) / 60.0, 0.1, 1.4);
                    powerScale = lc < 40 ? 0.8 : 1.5; // Very flat curve if contrast is truly poor
                }

                // DAMPENING FOR DARK SCENES:
                // If the background is already naturally dark (L < 0.25), we completely zero out 
                // the protective gradient as white text is perfectly readable.
                // Linear transition from 0% at L=0.25 to 100% at L=0.45
                double darkDampening = Math.Clamp((areaL - 0.25) / 0.2, 0.0, 1.0);

                // BRIGHT-VIBRANT SUPPRESSION (Yellow/Cyan):
                // On very bright and vibrant scenes (like L=0.68, V=224), even small gradients 
                // feel heavy and redundant because the color itself provides good isolation.
                if (areaL > 0.6 && vibrancy > 150) darkDampening *= 0.4;

                double gradBase = Math.Pow(areaL, powerScale) * darkDampening;
                double horizontalOpacity = Math.Clamp(gradBase * 1.1 * contrastFactor, 0.0, 0.95);
                double verticalOpacity = Math.Clamp(gradBase * 0.85 * contrastFactor, 0.0, 0.75);

                if (isProvisionalSource)
                {
                    if (horizontalOpacity <= 0.01 && verticalOpacity <= 0.01)
                    {
                        horizontalOpacity = 0.18;
                        verticalOpacity = 0.14;
                        System.Diagnostics.Debug.WriteLine("[AMBIENCE] Provisional floor applied because computed scrim target was zero.");
                    }
                    else
                    {
                        horizontalOpacity = Math.Max(horizontalOpacity, 0.16);
                        verticalOpacity = Math.Max(verticalOpacity, 0.12);
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[AMBIENCE] Gradient plan | L={areaL:F2} V={vibrancy} rawLc={rawLc:F1} bonus={vibrancyBonus:F1} damp={darkDampening:F2} | local={horizontalOpacity:F2} extra={verticalOpacity:F2} bottom={horizontalOpacity:F2}");

                // Readability scrims should start at 0 and smoothly move to the computed target.
                double gradientDurationMs = isProvisionalSource ? 220.0 : 650.0;
                AnimateReadabilityGradientOpacity(LocalInfoGradient, "LocalInfoGradient", horizontalOpacity, gradientDurationMs);
                AnimateReadabilityGradientOpacity(ExtraReadabilityGradient, "ExtraReadabilityGradient", verticalOpacity, gradientDurationMs);
                AnimateReadabilityGradientOpacity(BottomReadabilityGradient, "BottomReadabilityGradient", horizontalOpacity, gradientDurationMs);
                
                System.Diagnostics.Debug.WriteLine($"[AMBIENCE] Protection | Lc: {rawLc:F1} + Bonus: {vibrancyBonus:F1} | Damp: {darkDampening:F2} | Opacity: {horizontalOpacity:F2}");

                // 5. Animate Global Theme Brushes (used by various controls)
                if (_themeTintBrush == null) _themeTintBrush = new SolidColorBrush(btnTint);
                else if (isProvisionalSource) _themeTintBrush.Color = btnTint;
                else AnimateBrushColor(_themeTintBrush, btnTint);

                // 6. Animate Text Colors
                double themeDuration = isProvisionalSource ? 0.0 : 2.0;
                UpdateTextColor(TitleText, headerColor, themeDuration);
                UpdateTextColor(YearText, headerColor, themeDuration);
                UpdateTextColor(RuntimeText, headerColor, themeDuration);
                UpdateTextColor(GenresText, headerColor, themeDuration);
                UpdateTextColor(OverviewText, descriptionColor, themeDuration);
                UpdateTextColor(SuperTitleText, headerColor, themeDuration);
                if (BadgeResText != null) UpdateTextColor(BadgeResText, headerColor, themeDuration);
                if (BadgeCodec != null) UpdateTextColor(BadgeCodec, headerColor, themeDuration);
                UpdateTextColor(DirectorHeader, headerColor, themeDuration);
                UpdateTextColor(CastHeader, headerColor, themeDuration);
                UpdateTextColor(StickyTitle, headerColor, themeDuration);

                // Narrow Mode Headers

                // 6. Animate Specific UI Elements
                // WinUI 3 frozen brush issue: Her zaman yeni mutable fırça oluştur
                if (EpisodesPanel != null)
                {
                    AnimateBrushColor(EpisodesPanel, mixedPanelTint, themeDuration);
                }

                // Action Buttons - smooth transitions with brush safety
                AnimateBrushColor(TrailerButton, btnTint, themeDuration);
                AnimateBrushColor(DownloadButton, btnTint, themeDuration);
                AnimateBrushColor(CopyLinkButton, btnTint, themeDuration);
                if (SourcesShowHandleBorder != null) AnimateBrushColor(SourcesShowHandleBorder, btnTint, themeDuration);
                
                // [CONS] Update Watchlist state (it now uses themeDuration internally via _themeTintBrush link)
                UpdateWatchlistState(!isProvisionalSource);

                // Adaptive Play Button Foreground
                Color playForeground = isDarkText ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);

                if (RestartButton != null)
                {
                    // [RULE] Always update color even if Collapsed to prevent color persistence from previous items.
                    AnimateBrushColor(RestartButton, playButtonTint, themeDuration);
                    
                    // Update text and icon color to match PlayButton (Adaptive Dark/Light)
                    if (RestartButtonText != null) UpdateTextColor(RestartButtonText, playForeground, themeDuration);
                    
                    var restartIcon = FindVisualChild<FontIcon>(RestartButton);
                    if (restartIcon != null) UpdateIconColor(restartIcon, playForeground, themeDuration);
                }
                
                // Play Buttons (Premium Vivid)
                AnimateBrushColor(PlayButton, playButtonTint, themeDuration);
                AnimateBrushColor(StickyPlayButton, playButtonTint, themeDuration);

                if (PlayButton != null) PlayButton.BorderBrush = new SolidColorBrush(playBorderTint);
                if (StickyPlayButton != null) StickyPlayButton.BorderBrush = new SolidColorBrush(playBorderTint);

                UpdateTextColor(PlayButtonText, playForeground, themeDuration);
                UpdateIconColor(PlayButtonIcon, playForeground, themeDuration);
                UpdateTextColor(StickyPlayButtonText, playForeground, themeDuration);
                UpdateIconColor(StickyPlayButtonIcon, playForeground, themeDuration);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApplyAmbience] Error: {ex.Message}");
            }
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

        private void AnimateReadabilityGradientOpacity(UIElement element, string label, double targetOpacity, double durationMs = 650.0)
        {
            if (element == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(element);
            var currentOpacity = element.Opacity;
            var normalizedTarget = Math.Clamp(targetOpacity, 0.0, 1.0);

            System.Diagnostics.Debug.WriteLine(
                $"[AMBIENCE][{label}] opacity current={currentOpacity:F2} target={normalizedTarget:F2}");

            if (normalizedTarget <= 0.01)
            {
                visual?.StopAnimation("Opacity");
                element.Opacity = 0;
                if (visual != null) visual.Opacity = 0f;
                System.Diagnostics.Debug.WriteLine($"[AMBIENCE][{label}] target <= 0.01, pinned to 0.");
                return;
            }

            if (Math.Abs(currentOpacity - normalizedTarget) < 0.01)
            {
                element.Opacity = normalizedTarget;
                if (visual != null) visual.Opacity = (float)normalizedTarget;
                System.Diagnostics.Debug.WriteLine($"[AMBIENCE][{label}] already at target, no animation needed.");
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

            System.Diagnostics.Debug.WriteLine($"[AMBIENCE][{label}] animating {currentOpacity:F2} -> {normalizedTarget:F2} over {Math.Max(1.0, durationMs):F0}ms.");
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

                System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 1: LoadSeriesDataAsync ENTER for: {unified.Title}. Seasons: {(unified.Seasons?.Count ?? 0)}");
                
                // [TRACE] Log current panel states before population
                System.Diagnostics.Debug.WriteLine($"[MediaInfo-Trace] Pre-Load: EpisodesPanel.VA={EpisodesPanel?.VerticalAlignment}, Visible={EpisodesPanel?.Visibility}");
                
                // [FLICKER PREVENTION] If we already have content (from Cache-First), don't show shimmer again
                if (Seasons.Count == 0)
                {
                    if (EpisodesPanel != null) EpisodesPanel.Visibility = Visibility.Visible;
                    if (EpisodesShimmerPanel != null) EpisodesShimmerPanel.Visibility = Visibility.Visible;
                    if (EpisodesListView != null) EpisodesListView.Visibility = Visibility.Collapsed;
                }
                
                if (unified.Seasons == null || unified.Seasons.Count == 0)
                {
                    if (EpisodesShimmerPanel != null) EpisodesShimmerPanel.Visibility = Visibility.Collapsed;
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
                
                System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 1.2: Population finished. Seasons.Count={newSeasons.Count}");

                // [FLICKER PREVENTION] Compare with existing to avoid full clear/reset
                // Check if counts changed OR if primary season's first episode title changed (e.g. generic -> real)
                bool contentChanged = Seasons.Count > 0 && newSeasons.Count > 0 && Seasons[0].Episodes.Count > 0 && newSeasons[0].Episodes.Count > 0 && 
                                     Seasons[0].Episodes[0].Title != newSeasons[0].Episodes[0].Title;

                bool seriesChanged = Seasons.Count != newSeasons.Count || contentChanged ||
                                    (Seasons.Count > 0 && Seasons[0].Episodes.Count != newSeasons[0].Episodes.Count);

                if (seriesChanged)
                {
                    System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 1.1: Clearing Seasons/Episodes. VA={EpisodesPanel?.VerticalAlignment}");
                    Seasons.Clear();
                    CurrentEpisodes.Clear();
                    foreach(var s in newSeasons) Seasons.Add(s);
                }

                int targetSeasonIndex = 0;
                System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 1.2: Population starting. Seasons.Count={Seasons.Count}");
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
                if (EpisodesShimmerPanel != null) EpisodesShimmerPanel.Visibility = Visibility.Collapsed;
                if (EpisodesListView != null) EpisodesListView.Visibility = Visibility.Visible;
            }



        private EpisodeItem _pendingAutoSelectEpisode;

        private void SeasonComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SeasonComboBox.SelectedItem is SeasonItem season)
            {
                if (season != null)
                {
                    // 1. ALWAYS populate CurrentEpisodes with what we have immediately
                    CurrentEpisodes.Clear();
                    foreach (var ep in season.Episodes) CurrentEpisodes.Add(ep);

                    // 2. Identify if enrichment is needed
                    bool hasGenericTitles = season.Episodes.Any(ep => Services.Metadata.MetadataProvider.IsGenericEpisodeTitle(ep.Title, _unifiedMetadata?.Title));

                    var tmdbInfo = _unifiedMetadata?.TmdbInfo;
                    bool tmdbEnabled = AppSettings.IsTmdbEnabled && !string.IsNullOrWhiteSpace(AppSettings.TmdbApiKey);

                    bool needsEnrichment = season.Episodes.Count == 0 || 
                                           hasGenericTitles ||
                                           (tmdbInfo != null && !season.IsEnrichedByTmdb && (season.Episodes.Any(ep => string.IsNullOrEmpty(ep.Overview) || string.IsNullOrEmpty(ep.DurationFormatted)) || tmdbEnabled));

                    if (needsEnrichment && tmdbInfo != null)
                    {
                         System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Season {season.SeasonNumber} needs enrichment. (EnrichedByTmdb={season.IsEnrichedByTmdb}, TmdbEnabled={tmdbEnabled})");
                         _ = LoadTmdbSeasonDataAsync(season.SeasonNumber);
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 1.3: Setting EpisodesListView ItemsSource (Count={CurrentEpisodes.Count})");
                EpisodesListView.ItemsSource = CurrentEpisodes;



                // Restore selection state - always update IsSelected based on _selectedEpisode
                if (_selectedEpisode != null)
                {
                    var matchingEpisode = CurrentEpisodes.FirstOrDefault(e => e.Id == _selectedEpisode.Id);
                    if (matchingEpisode != null)
                    {
                        EpisodesListView.SelectedItem = matchingEpisode;

                        System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Restored selection: {matchingEpisode.Title}");
                    }
                }
                else if (_pendingAutoSelectEpisode != null)
                {
                    var matchingEpisode = CurrentEpisodes.FirstOrDefault(e => e.Id == _pendingAutoSelectEpisode.Id);
                     
                    if (matchingEpisode != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Auto-selecting episode: {matchingEpisode.Title} (ID: {matchingEpisode.Id})");
                        _isProgrammaticSelection = true;
                        try
                        {
                            EpisodesListView.SelectedItem = matchingEpisode;

                            {


                            }
                            EpisodesListView.ScrollIntoView(matchingEpisode);
                            _pendingAutoSelectEpisode = null;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Auto-selection failed: {ex.Message}");
                        }
                        finally
                        {
                            _isProgrammaticSelection = false;
                        }
                    }
                    else
                    {
                         System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Auto-select FAIL: Could not find episode {_pendingAutoSelectEpisode.Id} in CurrentEpisodes ({CurrentEpisodes.Count} items)");
                    }
                }
            }
        }

        private async Task LoadTmdbSeasonDataAsync(int seasonNumber)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 7: LoadTmdbSeasonDataAsync ENTER for Season: {seasonNumber}");
             try
             {
                 if (_unifiedMetadata == null) return;
                 
                 // 1. Enrich the unified model (Fetches and Merges TMDB logic)
                 await Services.Metadata.MetadataProvider.Instance.EnrichSeasonAsync(_unifiedMetadata, seasonNumber);

                 DispatcherQueue.TryEnqueue(() => 
                 {
                     // 2. Re-Sync UI from Unified Model
                     var unifiedSeason = _unifiedMetadata.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
                     var uiSeason = Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
                     
                     if (unifiedSeason != null && uiSeason != null)
                     {
                         // Re-map episodes. 
                         // We can't just clear and add because that might break selection state logic?
                         // Actually, standard practice for "Virtual Episodes" arriving is to just re-populate.
                         
                         var newEpList = new List<EpisodeItem>();
                         foreach (var e in unifiedSeason.Episodes)
                         {
                             var epItem = new EpisodeItem
                             {
                                 Id = e.Id,
                                 SeasonNumber = e.SeasonNumber,
                                 EpisodeNumber = e.EpisodeNumber,
                                 Title = e.Title,
                                 Name = e.Title,
                                 Overview = e.Overview,
                                 ImageUrl = !string.IsNullOrEmpty(e.ThumbnailUrl) ? e.ThumbnailUrl : (_item?.PosterUrl ?? ""),
                                 Thumbnail = ImageHelper.GetImage(!string.IsNullOrEmpty(e.ThumbnailUrl) ? e.ThumbnailUrl : (_item?.PosterUrl ?? ""), 150, 80),
                                 StreamUrl = e.StreamUrl,
                                 IsReleased = (e.AirDate ?? DateTime.MinValue) <= DateTime.Now,
                                 DurationFormatted = (!string.IsNullOrEmpty(e.RuntimeFormatted)) ? e.RuntimeFormatted : "",
                                 Resolution = e.Resolution,
                                 VideoCodec = e.VideoCodec,
                                 Bitrate = e.Bitrate,
                                 IsHdr = e.IsHdr,
                                 IptvSeriesId = e.IptvSeriesId,
                                 IptvSourceTitle = e.IptvSourceTitle
                             };
                             epItem.RefreshHistoryState();
                             newEpList.Add(epItem);
                         }

                         uiSeason.Episodes = newEpList;
                         uiSeason.IsEnrichedByTmdb = true;
                         System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 7.1: LoadTmdbSeasonDataAsync UI Update: Season {seasonNumber}, Count {newEpList.Count}");
                          
                           // If this is the currently selected season, update the View
                          if (SeasonComboBox.SelectedItem == uiSeason)
                          {
                               // Save selection
                               var selectedEpNum = (EpisodesListView.SelectedItem as EpisodeItem)?.EpisodeNumber;
                               
                               CurrentEpisodes.Clear();
                               foreach(var ep in newEpList) CurrentEpisodes.Add(ep);
                               
                               // Restore selection
                               if (selectedEpNum.HasValue)
                               {
                                   var toSelect = CurrentEpisodes.FirstOrDefault(x => x.EpisodeNumber == selectedEpNum.Value);
                                   if (toSelect != null) 
                                   {
                                       EpisodesListView.SelectedItem = toSelect;
                                   }
                               }
                          }
                     }
                 });
             }
             catch (Exception ex)
             {
                 System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] LoadTmdbSeasonDataAsync Error: {ex.Message}");
             }
        }

        private void EpisodesListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (sender is ListView lv)
            {
                var clicked = e.ClickedItem;
                var current = lv.SelectedItem;

                if (current != null && current.Equals(clicked))
                {
                    // Re-click detected. Use DispatcherQueue to escape the internal click-select cycle.
                    DispatcherQueue.TryEnqueue(() => 
                    {
                        lv.SelectedItem = null;
                        lv.SelectedIndex = -1;
                        System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Deselected episode via re-click.");
                    });
                }
            }
        }

        private void EpisodesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
             if (_isSelectionSyncing) return;
             
             if (EpisodesListView.SelectedItem is EpisodeItem ep)
             {
                 _isSelectionSyncing = true;
                 try
                 {
                     _selectedEpisode = ep;
                     _streamUrl = ep.StreamUrl;
 
                     System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Episode selected: S{ep.SeasonNumber}E{ep.EpisodeNumber}, Title='{ep.Title}', Overview='{ep.Overview?.Substring(0, Math.Min(50, ep.Overview?.Length ?? 0))}...'");

                     // [Fix] Restore StreamUrl from history (Prioritize last used source/addon over default IPTV stream)
                     string resolvedEpId = ResolveBestContentId(ep.Id);
                     var history = HistoryManager.Instance.GetProgress(resolvedEpId);
                     if (history != null && !string.IsNullOrEmpty(history.StreamUrl))
                     {
                          _streamUrl = history.StreamUrl;
                          System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Restored StreamUrl from history ({resolvedEpId}): {_streamUrl}");
                     }
                               // Sync narrow list



                    // UPDATE INFO PANEL
                    UpdateInfoPanelVisibility(true);

                    // Update Play Button
                    if (PlayButtonText != null)
                    {
                        ep.RefreshHistoryState(); // [FIX] Get latest position/progress
                        if (ep.HasProgress && ep.ProgressPercent < 95)
                        {
                            PlayButtonText.Text = "Devam Et";
                            if (PlayButtonSubtext != null)
                            {
                                PlayButtonSubtext.Text = ep.ProgressText;
                                PlayButtonSubtext.Visibility = Visibility.Visible;
                            }
                        }
                        else
                        {
                            PlayButtonText.Text = "Oynat";
                            if (PlayButtonSubtext != null) PlayButtonSubtext.Visibility = Visibility.Collapsed;
                        }
                        if (StickyPlayButtonText != null) StickyPlayButtonText.Text = PlayButtonText.Text;
                    }

                    // [FIX] Sync Restart Button visibility for episodes
                    if (RestartButton != null)
                    {
                        RestartButton.Visibility = (ep.HasProgress && ep.ProgressPercent < 95) ? Visibility.Visible : Visibility.Collapsed;
                    }

                    // AUTO-RESUME TRIGGER (Series Episode)
                    if (_shouldAutoResume)
                    {
                        _shouldAutoResume = false; // Reset to prevent repeated trigger
                        System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Auto-Resume triggered for series episode: {ep.Title}");
                        PlayButton_Click(null, null);
                    }
     
                     // PREVIEW
                      // History already loaded above
                      StartPrebuffering(_streamUrl, history?.Position ?? 0);

                      if (!string.IsNullOrEmpty(_streamUrl))
                           _ = UpdateTechnicalBadgesAsync(_streamUrl);
                  }
                  finally
                  {
                      _isSelectionSyncing = false;
                  }
            }
            else if (EpisodesListView.SelectedItem == null && !_isProgrammaticSelection)
            {
                // DESELECTION logic handled
                DeselectEpisode();
            }
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

        private void SetupButtonInteractions(params Button[] buttons)
        {
            foreach (var btn in buttons)
            {
                if (btn == null) continue;
                
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
            var leanExpr = _compositor.CreateExpressionAnimation("Vector2(props.TouchPoint.X * intensity, props.TouchPoint.Y * intensity)");
            leanExpr.SetReferenceParameter("props", props);
            leanExpr.SetScalarParameter("intensity", intensity);
            try
            {
                visual.StartAnimation("Translation.XY", leanExpr);
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
                    var reset = _compositor.CreateVector2KeyFrameAnimation();
                    reset.InsertKeyFrame(1f, new Vector2(0, 0));
                    reset.Duration = TimeSpan.FromMilliseconds(400);
                    visual.StartAnimation("Translation.XY", reset);
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
             // LoadSeriesDataAsync → SeasonComboBox_SelectionChanged → EpisodesListView_SelectionChanged → StartPrebuffering
             // We do NOT override selection here to avoid conflicts with the next-episode logic.
        }



        private async void EpisodePlayButton_Click(object sender, RoutedEventArgs e)
        {
             if (sender is Button btn && btn.Tag is EpisodeItem ep)
             {
                 // Check if selection creates a change
                 bool isSelectionChange = EpisodesListView.SelectedItem != ep;
                 
                 // Ensure this episode is selected
                 // This triggers EpisodesListView_SelectionChanged which:
                 // 1. Updates UI (Play button text, badges)
                 // 2. For Stremio: Calls PlayStremioContent (Loads sources)
                 // 3. For IPTV: Updates Technical Badges & Prebuffers
                 _selectedEpisode = ep;
                 EpisodesListView.SelectedItem = ep;

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

        private async void EpisodeArrowButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is EpisodeItem ep)
            {
                _selectedEpisode = ep;
                EpisodesListView.SelectedItem = ep;

                if (_item is Models.Stremio.StremioMediaStream)
                {
                    await PlayStremioContent(ep.Id, showGlobalLoading: false);
                }
                else if (_item is SeriesStream ss)
                {
                    // [FIX] If we have an IMDb ID, try searching addons instead of immediately playing the IPTV stream
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
            string trailerKey = _unifiedMetadata?.TrailerUrl;

            if (string.IsNullOrEmpty(trailerKey) && _cachedTmdb != null)
            {
                bool isTv = _item is SeriesStream || (_item is StremioMediaStream sms && (sms.Meta.Type == "series" || sms.Meta.Type == "tv"));
                trailerKey = await TmdbHelper.GetTrailerKeyAsync(_cachedTmdb.Id, isTv);
            }

            if (!string.IsNullOrEmpty(trailerKey))
            {
                await PlayTrailer(trailerKey);
            }
        }

        private async Task PlayTrailer(string videoKey)
        {
            // Cancel previous Play requests
            _trailerCts?.Cancel();
            _trailerCts?.Dispose();
            _trailerCts = new System.Threading.CancellationTokenSource();
            var token = _trailerCts.Token;

            System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] PlayTrailer START. Key: {videoKey}");

            if (string.IsNullOrEmpty(videoKey))
            {
                System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] videoKey is null or empty, aborting.");
                return;
            }

            // Extract ID if full URL is passed
            if (videoKey.Contains("youtube.com") || videoKey.Contains("youtu.be"))
            {
                try
                {
                    // Regex for v=ID
                    var match = System.Text.RegularExpressions.Regex.Match(videoKey, @"v=([^&]+)");
                    if (match.Success)
                    {
                        videoKey = match.Groups[1].Value;
                    }
                    else if (videoKey.Contains("youtu.be"))
                    {
                        var uri = new Uri(videoKey);
                        videoKey = uri.AbsolutePath.Trim('/');
                    }
                }
                catch { }
            }
            
            System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Cleaned Video ID: {videoKey}");

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
                
                System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Visual reset - CenterPoint: {centerX}, {centerY}, ActualSize: {targetW}x{targetH}, Scale: 0.1");
            }
            
            // Start HIDDEN to avoid black screen / loading artifacts.
            TrailerWebView.Opacity = 0;
            TrailerWebView.Visibility = Visibility.Collapsed;
            System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] Overlay Visible, LoadingRing Active.");

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
                
                System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Animation Start - Center: {centerX}, {centerY}");
                
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
                
                System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] Animation started, Opacity=1.");
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] ANIMATION FATAL ERROR: {ex}");
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
                 System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Load Content: {videoKey}");
                 
                 int waitCount = 0;
                 while (!_isTrailerWebViewInitialized && waitCount < 100)
                 {
                     if (token.IsCancellationRequested) return;
                     if (!_isTrailerInitializing && waitCount % 10 == 0) _ = InitializeTrailerWebViewAsync();
                     await Task.Delay(100, token);
                     waitCount++;
                 }

                 if (token.IsCancellationRequested) return;

                 if (TrailerWebView.CoreWebView2 != null)
                 {
                      System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Executing loadVideo('{videoKey}')");
                      // Slight delay to ensure player is ready
                      await Task.Delay(500, token); 
                      await TrailerWebView.CoreWebView2.ExecuteScriptAsync($"loadVideo('{videoKey}')");
                 }
                 else
                 {
                      System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] FATAL: CoreWebView2 NULL!");
                 }
             }
             catch (TaskCanceledException)
             {
                 System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] LoadTrailerContent Cancelled for {videoKey}");
             }
             catch(Exception ex)
             {
                  System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Load Error: {ex}");
             }
        }

        private async Task InitializeTrailerWebViewAsync()
        {
            if (_isTrailerInitializing) return;
            
            System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] InitializeTrailerWebViewAsync START.");
            _isTrailerInitializing = true;
            
            try
            {
                await TrailerWebView.EnsureCoreWebView2Async(await WebView2Service.GetSharedEnvironmentAsync());
                System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] CoreWebView2 Initialized.");

                // [FIX] Apply 100% Clean UI Script (Hides Title Flash, Logos, More Videos)
                await WebView2Service.ApplyYouTubeCleanUISettingsAsync(TrailerWebView.CoreWebView2);

                TrailerWebView.CoreWebView2.WebMessageReceived -= TrailerWebView_WebMessageReceived;
                TrailerWebView.CoreWebView2.WebMessageReceived += TrailerWebView_WebMessageReceived;

                TrailerWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;

                // Create a virtual host for the player assets
                _trailerFolder = Path.Combine(Path.GetTempPath(), "ModernIPTVPlayer_Trailers");
                Directory.CreateDirectory(_trailerFolder);
                System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Temporary directory: {_trailerFolder}");

                string playerHtml = CreateYouTubePlayerHtml();
                await File.WriteAllTextAsync(Path.Combine(_trailerFolder, "player.html"), playerHtml);
                System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] player.html written to temp.");

                try
                {
                    TrailerWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        _trailerVirtualHost,
                        _trailerFolder,
                        Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                }
                catch (ArgumentException) { }
                
                var tcs = new TaskCompletionSource<bool>();
                void OnNavigationCompleted(object s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
                {
                    TrailerWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                    if (!e.IsSuccess)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Navigation FAILED: {e.WebErrorStatus}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] Navigation Completed.");
                    }
                    tcs.TrySetResult(e.IsSuccess);
                }
                TrailerWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                
                // [NEW] Monitor Process Failures (Crash/Hang)
                TrailerWebView.CoreWebView2.ProcessFailed += (s, args) => {
                    System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] WebView Process Failed: {args.ProcessFailedKind}, Error: {args.Reason}");
                };

                TrailerWebView.CoreWebView2.Navigate($"https://{_trailerVirtualHost}/player.html");

                var timeoutTask = Task.Delay(8000); // 8 seconds timeout
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] WebView Navigation TIMEOUT.");
                }

                _isTrailerWebViewInitialized = true;
                System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] InitializeTrailerWebViewAsync FINISHED.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] InitializeTrailerWebViewAsync ERROR: {ex}");
            }
            finally
            {
                _isTrailerInitializing = false;
            }
        }

        private void TrailerWebView_WebMessageReceived(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                
                if (message.StartsWith("LOG:"))
                {
                    System.Diagnostics.Debug.WriteLine($"[TRAILER_JS_LOG] {message.Substring(4)}");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] WebMessageReceived: {message}");

                if (message == "RESET_SMART_CROP")
                {
                    // Relay to all frames
                    if (TrailerWebView?.CoreWebView2 != null) TrailerWebView.CoreWebView2.PostWebMessageAsString("RESET_SMART_CROP");
                    return;
                }
                
                if (message == "VIDEO_PLAYING")
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] Video Playing! Hiding Loading Ring and Showing WebView.");
                        TrailerLoadingRing.IsActive = false;
                        TrailerLoadingRing.Visibility = Visibility.Collapsed;
                        
                        // Reveal WebView only now
                        TrailerWebView.Opacity = 1;
                        TrailerWebView.Visibility = Visibility.Visible;
                    });
                }
                else if (message == "VIDEO_ENDED")
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] Video Ended - Closing.");
                        CloseTrailer();
                    });
                }
                else if (message == "VIDEO_ERROR")
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] Video Error - Closing.");
                        _isTrailerWebViewInitialized = false;
                        CloseTrailer();
                    });
                }
                else if (message.StartsWith("TOGGLE_FULLSCREEN:", StringComparison.OrdinalIgnoreCase))
                {
                    bool enable = message.EndsWith(":ON", StringComparison.OrdinalIgnoreCase);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _isTrailerFullscreen = enable;
                        EnsureTrailerOverlayBounds();
                        ApplyTrailerFullscreenLayout(enable);
                        var visual = ElementCompositionPreview.GetElementVisual(TrailerContent);
                        visual.StopAnimation("Scale");
                        visual.Scale = Vector3.One;
                        visual.Offset = Vector3.Zero;
                    });
                }
            }
            catch(Exception ex) 
            {
                System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Message Parse Error: {ex}");
            }
        }

        private void SetupRealTimeComposition()
        {
            if (InfoColumn != null)
            {
                ElementCompositionPreview.SetIsTranslationEnabled(InfoColumn, true);
                var v = ElementCompositionPreview.GetElementVisual(InfoColumn);
                v.CenterPoint = new Vector3(0, (float)InfoColumn.ActualHeight, 0);
            }

            if (ActionBarPanel != null)
            {
                // Give buttons a stable center for scaling (prevents "stretching" look)
                foreach (var child in ActionBarPanel.Children)
                {
                    if (child is FrameworkElement fe)
                    {
                        ElementCompositionPreview.SetIsTranslationEnabled(fe, true);
                        var v = ElementCompositionPreview.GetElementVisual(fe);
                        v.CenterPoint = new Vector3((float)(fe.ActualWidth / 2.0), (float)(fe.ActualHeight / 2.0), 0);
                    }
                }
            }

            if (MetadataRibbon != null)
            {
                foreach (var child in MetadataRibbon.Children)
                {
                    if (child is FrameworkElement fe) ElementCompositionPreview.SetIsTranslationEnabled(fe, true);
                }
            }

            if (EpisodesPanel != null) ElementCompositionPreview.SetIsTranslationEnabled(EpisodesPanel, true);
            if (SourcesPanel != null) ElementCompositionPreview.SetIsTranslationEnabled(SourcesPanel, true);
        }

        public double GetResValue(double wide, double narrow)
        {
            return _isWideModeIndex == 1 ? wide : narrow;
        }

        private string CreateYouTubePlayerHtml()
        {
            return @"<!DOCTYPE html>
<html>
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        html, body { width: 100%; height: 100%; margin: 0; padding: 0; background: #000; overflow: hidden; }
        
        .yt-wrapper {
            width: 100%;
            height: 100%;
            overflow: hidden;
        }

        .yt-frame-container {
            position: relative;
            width: 300%;
            height: 100%;
            left: -100%; /* Center the 300% wide container */
        }

        .yt-frame-container iframe {
            position: absolute; 
            top: 0; 
            left: 0; 
            width: 100%; 
            height: 100%;
            pointer-events: none;
        }
    </style>
</head>
<body>
    <div id=""loading"">Loading player...</div>
    <div class=""yt-wrapper"">
        <div class=""yt-frame-container"">
            <div id=""player""></div>
        </div>
    </div>
    <script>
        var tag = document.createElement('script');
        tag.src = 'https://www.youtube.com/iframe_api';
        var firstScriptTag = document.getElementsByTagName('script')[0];
        firstScriptTag.parentNode.insertBefore(tag, firstScriptTag);

        var player;
        var isReady = false;
        var pendingVideoId = null;
        
        function log(msg) { window.chrome.webview.postMessage('LOG: ' + msg); }

        function onYouTubeIframeAPIReady() {
                player = new YT.Player('player', {
                height: '100%',
                width: '100%',
                host: 'https://www.youtube.com',
                playerVars: {
                    autoplay: 0,
                    mute: 1,
                    controls: 0,
                    disablekb: 1,
                    fs: 0,
                    rel: 0,
                    modestbranding: 1,
                    showinfo: 0,
                    iv_load_policy: 3,
                    playsinline: 1,
                    loop: 1, /* Helps loops cleaner UI sometimes */
                    vq: 'hd1080'
                },
                events: {
                    'onReady': onPlayerReady,
                    'onStateChange': onPlayerStateChange,
                    'onError': onPlayerError
                }
            });
        }
        
        function onPlayerReady(event) {
            log('Player Ready');
            isReady = true;
            try {
                player.mute(); // Ensure mute initial state
            } catch (e) {}
            document.getElementById('loading').style.display = 'none';
            window.chrome.webview.postMessage('PLAYER_READY');
            
            // If a video was requested before ready, load it now
            if (pendingVideoId) {
                log('Processing pending video: ' + pendingVideoId);
                loadVideo(pendingVideoId);
                pendingVideoId = null;
            }
        }
        
        function onPlayerStateChange(event) {
            log('State Change: ' + event.data);
            if (event.data === YT.PlayerState.PLAYING || event.data === YT.PlayerState.BUFFERING) {
                applyQualityPreference();
                window.chrome.webview.postMessage('VIDEO_PLAYING');
                
                // UNMUTE after a short delay to ensure playback started
                if (event.data === YT.PlayerState.PLAYING) {
                    setTimeout(() => {
                        try {
                            log('Unmuting...');
                            player.unMute();
                            player.setVolume(100);
                        } catch(e) {}
                    }, 500); 
                }
            } else if (event.data === YT.PlayerState.ENDED) {
                window.chrome.webview.postMessage('VIDEO_ENDED');
            } else if (event.data === -1) {
                // Sometimes mobile/embedded restrictions prevent auto-start. Force it.
                log('State is -1 (Unstarted), forcing playVideo()...');
                player.playVideo();
            }
        }

        function onPlayerError(event) {
             log('Player Error: ' + event.data);
             window.chrome.webview.postMessage('VIDEO_ERROR');
        }

        function applyQualityPreference() {
            if (!player || !isReady) return;
            try {
                player.setPlaybackQualityRange('hd1080');
                player.setPlaybackQuality('hd1080');
            } catch (e) {
                // Device/network may limit this; YouTube will fallback automatically.
            }
        }
        
        // Called from C# to load a video
        function loadVideo(videoId) {
            log('loadVideo JS called for: ' + videoId + ' (isReady: ' + isReady + ')');
            
            // [MODERN] Reset Smart Crop state for new video (Broadcast to all frames)
            try {{ 
                log('Broadcasting RESET_SMART_CROP...'); 
                window.chrome.webview.postMessage('RESET_SMART_CROP'); 
            }} catch(e) {{
                 if (window.resetSmartCrop) window.resetSmartCrop();
            }}

            if (!isReady) {
                pendingVideoId = videoId;
                log('Player not ready, queuing video.');
                return;
            }
            log('Loading video ID: ' + videoId);
            player.loadVideoById({
                videoId: videoId,
                suggestedQuality: 'hd1080'
            });
            setTimeout(applyQualityPreference, 80);
            setTimeout(applyQualityPreference, 700);
            try {
                log('Attempting playVideo()...');
                player.playVideo();
            } catch(e) {
                log('playVideo exception: ' + e);
            }
        }
        
        // Called from C# to stop playback
        function stopVideo() {
            if (player && isReady) {
                player.stopVideo();
            }
        }
    </script>
</body>
</html>";
        }

        private void CloseTrailerButton_Click(object sender, RoutedEventArgs e)
        {
            CloseTrailer();
        }

        private void TrailerScrim_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            CloseTrailer();
        }

        private async Task CloseTrailer()
        {
            System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] CloseTrailer START.");
            
            // Cancel any pending start operations
            _trailerCts?.Cancel();
            _trailerCts?.Dispose();
            _trailerCts = null;

            int closeVersion = Interlocked.Increment(ref _trailerUiVersion);
            _isTrailerFullscreen = false;
            // Do NOT reset _isTrailerWebViewInitialized - keep WebView ready for reuse
            
            // Stop playback via JS immediately
            // [CRITICAL FIX] Check IsLoaded and CoreWebView2 validity before any calls
            if (_isTrailerWebViewInitialized && TrailerWebView != null && TrailerWebView.CoreWebView2 != null && this.IsLoaded)
            {
                System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] Stopping video via JS...");
                try 
                {
                    // Use a timeout to prevent hanging the UI if WebView2 process is already dying
                    var jsTask = TrailerWebView.CoreWebView2.ExecuteScriptAsync("stopVideo();").AsTask();
                    if (await Task.WhenAny(jsTask, Task.Delay(500)) == jsTask)
                    {
                        await jsTask;
                    }

                    if (TrailerWebView?.CoreWebView2 != null && this.IsLoaded)
                    {
                        // NAVIGATE to blank to ensure audio process is killed
                        TrailerWebView.CoreWebView2.Navigate("about:blank");
                    }
                    _isTrailerWebViewInitialized = false; 
                }
                catch (Exception ex)
                {
                     System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] StopJS Error: {ex.Message}");
                }
            }

            // Animate Scrim Fade Out
            if (TrailerScrim != null)
            {
                var scrimVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(TrailerScrim);
                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromMilliseconds(300);
                scrimVisual.StartAnimation("Opacity", fadeOut);
            }
            
            // Animate Content Out (Scale Down)
            if (TrailerContent != null)
            {
                var contentVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(TrailerContent);
                contentVisual.StopAnimation("Scale");
                contentVisual.Scale = System.Numerics.Vector3.One;
                
                // Capture dimensions BEFORE collapsing
                float cx = (float)(TrailerContent.ActualWidth > 0 ? TrailerContent.ActualWidth / 2f : 500f);
                float cy = (float)(TrailerContent.ActualHeight > 0 ? TrailerContent.ActualHeight / 2f : 281f);

                TrailerContent.Opacity = 0;
                System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] Animations started, Opacity=0.");

                await Task.Delay(300);

                if (closeVersion != _trailerUiVersion)
                {
                    return;
                }

                System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] Finalizing Close (UI Cleanup).");
                
                // Reset Transforms (XAML) - Critical to do BEFORE collapse for coordinates
                TrailerTransform.TranslateX = 0; TrailerTransform.TranslateY = 0;
                TrailerTransform.ScaleX = 1; TrailerTransform.ScaleY = 1;
                TrailerTransform.CenterX = 0; TrailerTransform.CenterY = 0; 

                TrailerOverlay.Visibility = Visibility.Collapsed;
                TrailerScrim.Opacity = 0;
                TrailerScrim.Width = double.NaN;
                TrailerScrim.Height = double.NaN;
                TrailerOverlay.Width = double.NaN;
                TrailerOverlay.Height = double.NaN;
                
                // Reset State
                TrailerWebView.Opacity = 0;
                TrailerLoadingRing.IsActive = false;
                TrailerLoadingRing.Visibility = Visibility.Collapsed;
                // Keep _isTrailerWebViewInitialized = true to reuse WebView on next open
                ApplyTrailerFullscreenLayout(enable: false);
                
                // Reset Composition Visual (Double safety)
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(TrailerContent);
                visual.StopAnimation("Scale");
                visual.StopAnimation("Offset");
                visual.StopAnimation("Opacity");
                visual.Scale = new System.Numerics.Vector3(1f, 1f, 1f);
                
                double finalW = TrailerContent.Width > 0 ? TrailerContent.Width : TrailerDefaultWidth;
                double finalH = TrailerContent.Height > 0 ? TrailerContent.Height : TrailerDefaultHeight;
                visual.CenterPoint = new System.Numerics.Vector3((float)(finalW / 2.0), (float)(finalH / 2.0), 0);
                
                System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Close Complete. CenterPoint Reset: {finalW/2},{finalH/2}");
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
                MediaInfoPlayer = new MpvWinUI.MpvPlayer();
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
                Debug.WriteLine($"[MediaInfoPage] Failed to init pre-buffer player: {ex.Message}");
            }

            if (isNew)
            {
                MediaInfoPlayer.Width = 100;
                MediaInfoPlayer.Height = 100;
                if (PlayerHost != null) PlayerHost.Content = MediaInfoPlayer;
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
                int preSeconds = AppSettings.PrebufferSeconds;
                await MediaInfoPlayer.SetPropertyAsync("cache", "yes");
                await MediaInfoPlayer.SetPropertyAsync("cache-pause", "yes");
                await MediaInfoPlayer.SetPropertyAsync("demuxer-readahead-secs", preSeconds.ToString());
                await MediaInfoPlayer.SetPropertyAsync("demuxer-max-bytes", "512MiB"); 
                
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
                double percent = history.Duration > 0 ? (history.Position / history.Duration) * 100 : 0;
                if (!history.IsFinished && percent < 98)
                {
                    string resumeText = "Devam Et";
                    var remaining = TimeSpan.FromSeconds(history.Duration - history.Position);
                    string subtext = remaining.TotalHours >= 1 
                        ? $"{(int)remaining.TotalHours}sa {(int)remaining.Minutes}dk Kaldı"
                        : $"{(int)remaining.TotalMinutes}dk Kaldı";

                    Debug.WriteLine($"[ResumeDebug] UpdateMovieHistoryUi: History Position={history.Position}, Duration={history.Duration}");
                    Debug.WriteLine($"[ResumeDebug] Button Text: {resumeText}, Subtext: {subtext}");

                    PlayButtonText.Text = resumeText;
                    PlayButtonSubtext.Text = subtext;
                    PlayButtonSubtext.Visibility = Visibility.Visible;

                    StickyPlayButtonText.Text = resumeText;
                    StickyPlayButtonSubtext.Text = subtext;
                    StickyPlayButtonSubtext.Visibility = Visibility.Visible;

                    RestartButton.Visibility = Visibility.Visible;
                    
                     // FAST START: Start pre-buffering since we are offering "Continue"
                     _streamUrl = history.StreamUrl;
                     Debug.WriteLine($"[ResumeDebug] History position: {history.Position}, AutoResume: {_shouldAutoResume}");
                     
                     if (!_shouldAutoResume)
                     {
                         Debug.WriteLine($"[ResumeDebug] Triggering StartPrebuffering with URL={_streamUrl} and Position={history.Position}");
                         StartPrebuffering(history.StreamUrl, history.Position);
                     }
                     else
                     {
                         Debug.WriteLine("[ResumeDebug] Skipping StartPrebuffering due to AutoResume to avoid race condition.");
                     }
                     _ = UpdateTechnicalBadgesAsync(_streamUrl);
                 }
                else
                {
                    PlayButtonText.Text = "Tekrar İzle"; 
                    PlayButtonSubtext.Visibility = Visibility.Collapsed;
                    RestartButton.Visibility = Visibility.Visible;
                    
                    if (StickyPlayButtonText != null) StickyPlayButtonText.Text = "Tekrar İzle";
                    if (StickyPlayButtonSubtext != null) StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
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
             // LoadSeriesDataAsync → SeasonComboBox_SelectionChanged → EpisodesListView_SelectionChanged → StartPrebuffering
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            // STREMIO LOGIC
            if (_item is Models.Stremio.StremioMediaStream stremioItem)
            {
                // [FIX] If _streamUrl is missing but we're in an auto-resume flow, try a last-ditch history lookup
                if (string.IsNullOrEmpty(_streamUrl))
                {
                    string historyId = stremioItem.Meta.Id;
                    if (stremioItem.Meta.Type == "series" && _selectedEpisode != null) historyId = _selectedEpisode.Id;
                    
                    var h = HistoryManager.Instance.GetProgress(historyId);
                    if (h != null && !string.IsNullOrEmpty(h.StreamUrl))
                    {
                        _streamUrl = h.StreamUrl;
                        System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Recovered _streamUrl from history: {_streamUrl}");
                    }
                }

                // If we have a cached stream URL (Resume) and user clicked "Continue", prioritize Resume
                if (!string.IsNullOrEmpty(_streamUrl))
                {
                     string videoId = stremioItem.Meta.Id;
                     string title = stremioItem.Title;
                     
                     if (stremioItem.Meta.Type == "series" && _selectedEpisode != null)
                     {
                         videoId = _selectedEpisode.Id;
                         title = $"{_selectedEpisode.SeasonNumber}x{_selectedEpisode.EpisodeNumber} - {_selectedEpisode.Title}";
                     }
                     
                     
                     // Use Handoff (or Direct Navigate) with the resumed URL
                     double resumeSeconds = -1;
                     var history = HistoryManager.Instance.GetProgress(videoId);
                     if (history != null && !history.IsFinished && history.Position > 0)
                     {
                         resumeSeconds = history.Position;
                     }

                     string parentIdStr = (stremioItem.Meta.Type == "series" || stremioItem.Meta.Type == "tv") ? stremioItem.Meta.Id : null;
                     int seasonToPass = _selectedEpisode?.SeasonNumber ?? 0;
                     int episodeToPass = _selectedEpisode?.EpisodeNumber ?? 0;

                     string handoverType = (stremioItem.Meta.Type == "series" || stremioItem.Meta.Type == "tv") ? "series" : "movie";
                     await PerformHandoverAndNavigate(_streamUrl, title, videoId, parentIdStr, null, seasonToPass, episodeToPass, resumeSeconds, _item.PosterUrl, handoverType);
                     return;
                }
                
                // Otherwise show sources or auto-play
                if (stremioItem.Meta.Type == "movie")
                {
                    // Check history for resume
                    var history = HistoryManager.Instance.GetProgress(stremioItem.Meta.Id);
                    double startSeconds = -1;
                    if (history != null && !history.IsFinished && history.Position > 0)
                    {
                        startSeconds = history.Position;
                    }
                    
                    await PlayStremioContent(stremioItem.Meta.Id, showGlobalLoading: false, autoPlay: true, startSeconds: startSeconds);
                }
                else if (_selectedEpisode != null)
                {
                    double resumeSeconds = -1;
                    var h = HistoryManager.Instance.GetProgress(_selectedEpisode.Id);
                    if (h != null && !h.IsFinished && h.Position > 0) resumeSeconds = h.Position;
                    
                    await PlayStremioContent(_selectedEpisode.Id, showGlobalLoading: false, autoPlay: true, startSeconds: resumeSeconds);
                }
                return;
            }

            // [FIX] For IPTV library items, if we have an IMDb ID, we should also offer addon source selection
            if (_item != null && !string.IsNullOrEmpty(_item.IMDbId))
            {
                string videoId = _item.IMDbId;
                if (_item is SeriesStream ss && _selectedEpisode != null)
                {
                    videoId = $"{ss.IMDbId}:{_selectedEpisode.SeasonNumber}:{_selectedEpisode.EpisodeNumber}";
                }
                
                // If we don't have a stream URL yet, or if user wants to see sources
                if (string.IsNullOrEmpty(_streamUrl))
                {
                    await PlayStremioContent(videoId, showGlobalLoading: false, autoPlay: true);
                    return;
                }
            }

            if (!string.IsNullOrEmpty(_streamUrl))
            {
                // [FIX] Prioritize resolved IMDb ID from unified metadata if available for more accurate subtitle matching in PlayerPage
                string idToPass = ResolveBestContentId(_selectedEpisode?.Id ?? (_item?.IMDbId ?? _item?.Id.ToString()));
                
                // [FIX] Get History Position for Resume
                double startSecs = -1;
                var h = HistoryManager.Instance.GetProgress(idToPass);
                if (h == null && _selectedEpisode == null) h = HistoryManager.Instance.GetProgress(_item?.Id.ToString() ?? "");
                if (h != null && !h.IsFinished && h.Position > 0) startSecs = h.Position;

                AppLogger.Info($"[MediaInfo:Play] Base ID: {idToPass} | URL: {(_streamUrl?.Length > 30 ? _streamUrl.Substring(0, 30) + "..." : _streamUrl)} | Resume: {startSecs}s");

                // Series Episode
                if (_selectedEpisode != null)
                {
                     string parentId = _item is SeriesStream ss ? ss.SeriesId.ToString() : null;
                     await PerformHandoverAndNavigate(_streamUrl, _selectedEpisode.Title, idToPass, parentId, _item.Title, _selectedEpisode.SeasonNumber, _selectedEpisode.EpisodeNumber, startSecs, _item.PosterUrl, "series", GetCurrentBackdrop());
                }
                else if (_item is LiveStream live)
                {
                    // Movie / Live
                    await PerformHandoverAndNavigate(_streamUrl, live.Title, idToPass, null, null, 0, 0, startSecs, live.PosterUrl, "iptv", GetCurrentBackdrop());
                }
                else
                {
                    // Fallback
                    await PerformHandoverAndNavigate(_streamUrl, TitleText.Text, idToPass, startSeconds: startSecs, backdropUrl: GetCurrentBackdrop());
                }
            }
        }
        
        private async Task PerformHandoverAndNavigate(string url, string title, string id = null, string parentId = null, string seriesName = null, int season = 0, int episode = 0, double startSeconds = -1, string posterUrl = null, string type = null, string backdropUrl = null)
        {
            _isHandoffInProgress = true;
            // Handoff Logic
            try
            {
                // CRITICAL SHAKE: Unpause!
                bool isPlayerActive = false;
                if (MediaInfoPlayer != null)
                {
                    try
                    {
                        // [FIX] If we are in AutoResume mode, we deliberately skipped StartPrebuffering.
                        // So the player is likely empty or holds a trailer. DO NOT HANDOFF.
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
                                _isHandoffInProgress = true;
                                App.HandoffPlayer = MediaInfoPlayer; // Valid Handoff
                            
                            Debug.WriteLine($"[MediaInfoPage:Handoff] Player matched path: {path}");
                            
                            // APPLY MAIN BUFFER SETTINGS
                            try 
                            {
                                int mainBuffer = AppSettings.BufferSeconds;
                                await MediaInfoPlayer.SetPropertyAsync("demuxer-readahead-secs", mainBuffer.ToString());
                                await MediaInfoPlayer.SetPropertyAsync("demuxer-max-bytes", "2000MiB"); 
                                await MediaInfoPlayer.SetPropertyAsync("pause", "no");
                            } catch { }
                            
                            // [FIX] Force swap chain linking BEFORE handoff detachment
                            // Without this, the swap chain may never be linked to the SwapChainPanel
                            // if the async _needsFirstFrameLink dispatch hasn't executed yet.
                            MediaInfoPlayer.EnsureSwapChainLinked();
                            MediaInfoPlayer.EnableHandoffMode();
                            
                            // Detach from parent
                            var parent = MediaInfoPlayer.Parent;
                            if (parent is Panel p) p.Children.Remove(MediaInfoPlayer);
                            else if (parent is ContentControl cc) cc.Content = null;
                            Debug.WriteLine("[MediaInfoPage:Handoff] Detached from visual tree.");
                        }
                        // [FIX] Closing the else block added for Handoff prevention
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
                    App.HandoffPlayer = null;
                }
                
                // Navigate
                Debug.WriteLine($"[MediaInfoPage:Handoff] Navigating to PlayerPage for {url} | StartSeconds: {startSeconds} | HasHandoff: {App.HandoffPlayer != null}");
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title, id, parentId, seriesName, season, episode, startSeconds, posterUrl, type, backdropUrl, GetLogoUrl(), _primaryColorHex, _sourceAddonUrl));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaInfoPage:Handoff] ERROR: {ex}");
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title, id, parentId, seriesName, season, episode, startSeconds, posterUrl, type, backdropUrl, GetLogoUrl(), _primaryColorHex, _sourceAddonUrl));
            }
        }

        private string GetLogoUrl()
        {
            if (_unifiedMetadata != null && !string.IsNullOrEmpty(_unifiedMetadata.LogoUrl)) return _unifiedMetadata.LogoUrl;
            return null;
        }

        private string GetCurrentBackdrop()
        {
            if (_unifiedMetadata != null && !string.IsNullOrEmpty(_unifiedMetadata.BackdropUrl)) return _unifiedMetadata.BackdropUrl;
            if (_item is StremioMediaStream sms && !string.IsNullOrEmpty(sms.Meta.Background)) return sms.Meta.Background;
            if (_item?.TmdbInfo != null && !string.IsNullOrEmpty(_item.TmdbInfo.FullBackdropUrl)) return _item.TmdbInfo.FullBackdropUrl;
            return null;
        }
        
        private async Task PlayStremioContent(string videoId, bool showGlobalLoading = true, bool autoPlay = false, double startSeconds = -1)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return;
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
                    ShowSourcesPanel(true);
                    ScrollToActiveSource();
                    
                    // [FIX] Reset fetch state and refresh layout on early return to hide shimmers
                    _isSourcesFetchInProgress = !_isCurrentSourcesComplete; 
                    UpdateLayoutState(ActualWidth >= LayoutAdaptiveThreshold);
                    return;
                }
            }
            else
            {
                _addonResults?.Clear();
                SourcesPanel.Visibility = Visibility.Collapsed;
            }

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
                    hasCachedAddons = true;

                    _addonResults = new ObservableCollection<StremioAddonViewModel>(cacheEntry.Addons.Select(CloneAddonViewModel));
                    AddonSelectorList.ItemsSource = _addonResults;

                    ShowSourcesPanel(true);
                    RefreshAllAddonActiveFlags();
                    
                    var activeAddon = _addonResults.FirstOrDefault(a => !a.IsLoading && a.Streams != null && a.Streams.Any(s => s.IsActive));
                    var firstAddon = _addonResults.FirstOrDefault(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0);
                    
                    if (activeAddon != null) AddonSelectorList.SelectedItem = activeAddon;
                    else if (firstAddon != null && AddonSelectorList.SelectedItem == null) AddonSelectorList.SelectedItem = firstAddon;

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
                            Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(firstStream.Url, _item.Title, resolvedVideoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, startSeconds, _item.PosterUrl, autoStreamType, GetCurrentBackdrop(), GetLogoUrl(), _primaryColorHex, _sourceAddonUrl));
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

                var iptvMatches = (_item is StremioMediaStream stremioItem) 
                    ? IptvMatchService.Instance.MatchStremioItemAll(stremioItem, null, _unifiedMetadata?.OriginalTitle, _unifiedMetadata?.SubTitle, _unifiedMetadata?.Title, _unifiedMetadata?.Year) 
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
                for (int i = 0; i < addons.Count; i++)
                {
                    _addonResults.Add(new StremioAddonViewModel
                    {
                        Name = "Loading...", // Will be replaced by Shimmer via DataTemplate's IsLoading
                        AddonUrl = addons[i],
                        IsLoading = true,
                        SortIndex = i,
                        Streams = new List<StremioStreamViewModel>()
                    });
                }

                // Initial selection and layout refresh
                if (iptvAddonToSelect != null) AddonSelectorList.SelectedItem = iptvAddonToSelect;
                else if (_addonResults.Count > 0) AddonSelectorList.SelectedIndex = 0;

                // Show panel AFTER population to avoid global shimmer flicker
                ShowSourcesPanel(true);
                
                // [FIX] Update layout AFTER population so hasSources=true for IPTV
                UpdateLayoutState(ActualWidth >= LayoutAdaptiveThreshold);

                var tasks = new List<Task>();
                for (int i = 0; i < addons.Count; i++)
                {
                    int sortIndex = i;
                    string baseUrl = addons[i];

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var manifest = await Services.Stremio.StremioService.Instance.GetManifestAsync(baseUrl);
                            if (manifest == null) 
                            {
                                // Remove failed addon from list
                                dispatcherQueue.TryEnqueue(() => {
                                    var failed = _addonResults.FirstOrDefault(a => a.AddonUrl == baseUrl && a.IsLoading);
                                    if (failed != null) _addonResults.Remove(failed);
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
                            var streams = await Services.Stremio.StremioService.Instance.GetStreamsAsync(new List<string> { baseUrl }, type, resolvedVideoId, includeIptv: false);
                            
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
                                        ExternalUrl = s.ExternalUrl,
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

                                            // [FIX] Find and Update Existing Loading Entry
                                            var existing = _addonResults.FirstOrDefault(a => a.AddonUrl == baseUrl);
                                            if (existing != null)
                                            {
                                                existing.Streams = addonVM.Streams;
                                                existing.Name = addonVM.Name;
                                                existing.IsLoading = false;

                                                // [FIX] Refresh streams list if this addon is currently selected
                                                if (AddonSelectorList.SelectedItem == existing)
                                                {
                                                    SourcesListView.ItemsSource = null;
                                                    SourcesListView.ItemsSource = existing.Streams;
                                                }
                                            }
                                            else
                                            {
                                                int insertAt = 0;
                                                while (insertAt < _addonResults.Count && _addonResults[insertAt].SortIndex < sortIndex) insertAt++;
                                                _addonResults.Insert(insertAt, addonVM);
                                            }

                                            if (SourcesPanel != null && _addonResults.Any(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0)) 
                                            { 
                                                UpdateLayoutState(ActualWidth >= LayoutAdaptiveThreshold); 
                                            }

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
                                                    Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(firstStream.Url, _item.Title, resolvedVideoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, startSeconds, _item.PosterUrl, autoStreamType, GetCurrentBackdrop(), GetLogoUrl(), _primaryColorHex, _sourceAddonUrl));
                                                    return;
                                                }
                                            }

                                            // Select Active Addon if available
                                            bool activeInUpdate = addonVM.Streams.Any(s => s.IsActive);
                                            if (activeInUpdate) AddonSelectorList.SelectedItem = existing ?? addonVM;
                                            else if (AddonSelectorList.SelectedIndex == -1) AddonSelectorList.SelectedItem = existing ?? addonVM;
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

                await Task.WhenAll(tasks);
                
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
                        
                        UpdateLayoutState(ActualWidth >= LayoutAdaptiveThreshold);

                        if (!_addonResults.Any(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0))
                        {
                            ShowSourcesPanel(false);
                            try
                            {
                                var err = new ContentDialog { Title = "Kaynak Bulunamadı", Content = "Eklentilerinizde bu içerik için uygun bir kaynak bulunamadı.", CloseButtonText = "Tamam", XamlRoot = this.XamlRoot };
                                await Services.DialogService.ShowAsync(err);
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
                    UpdateLayoutState(ActualWidth >= LayoutAdaptiveThreshold);
                }
                System.Diagnostics.Debug.WriteLine($"PlayStremio Error: {ex}");
            }
        }

        private static StremioAddonViewModel CloneAddonViewModel(StremioAddonViewModel source)
        {
            return new StremioAddonViewModel
            {
                Name = source.Name,
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
                ExternalUrl = source.ExternalUrl,
                Quality = source.Quality,
                Size = source.Size,
                IsCached = source.IsCached,
                OriginalStream = source.OriginalStream,
                IsActive = source.IsActive
            };
        }

        private void ToggleSourcesLoading(bool isLoading)
        {
            if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        }

        private sealed class StremioSourcesCacheEntry
        {
            public List<StremioAddonViewModel> Addons { get; set; } = new();
            public bool IsComplete { get; set; }
        }

        private void ShowSourcesPanel(bool show)
        {
            _areSourcesVisible = show; // <--- Set Flag
            bool isWide = ActualWidth >= LayoutAdaptiveThreshold;
            
            System.Diagnostics.Debug.WriteLine($"[LayoutDebug] ShowSourcesPanel({show}) - IsWide: {isWide}, Item: {_item?.Title}");

            // [CENTRALIZED] Let UpdateLayoutState handle primary visibilities
            UpdateLayoutState(isWide);

            if (show)
            {
                _isSourcesPanelHidden = false;
                if (SourcesShowHandle != null) SourcesShowHandle.Visibility = Visibility.Collapsed;

                if (SourcesPanel != null && SourcesPanel.Visibility == Visibility.Visible)
                {
                    // Enable high-performance translation and reset it
                    ElementCompositionPreview.SetIsTranslationEnabled(SourcesPanel, true);
                    
                    // Safely cancel any dormant Opacity animations
                    var visual = ElementCompositionPreview.GetElementVisual(SourcesPanel);
                    visual.StopAnimation("Opacity");
                    visual.StopAnimation("Scale");
                    visual.StopAnimation("Translation");
                    visual.Opacity = 1f;
                    visual.Scale = new Vector3(1f, 1f, 1f);
                    visual.Properties.InsertVector3("Translation", new Vector3(0, 0, 0));
                    visual.Clip = null;
                    SourcesPanel.Opacity = 1;

                    AnimatePanelReveal(SourcesPanel, SourcesPanelTransform, isWide, true);
                }

                bool canGoBackToEpisodes = IsSeriesItem();
                if (BtnBackToEpisodes != null) BtnBackToEpisodes.Visibility = canGoBackToEpisodes ? Visibility.Visible : Visibility.Collapsed;

                // Update info panel to show Episode Info
                UpdateInfoPanelVisibility(true);
            }
            else
            {
                Interlocked.Increment(ref _sourcesRequestVersion);
                _isSourcesFetchInProgress = false;
                
                if (BtnBackToEpisodes != null) BtnBackToEpisodes.Visibility = Visibility.Collapsed;
                
                if (IsSeriesItem())
                {
                    if (EpisodesPanel != null) EpisodesPanel.Visibility = Visibility.Visible;
                }
                else if (_item is SeriesStream)
                {
                    if (EpisodesPanel != null) EpisodesPanel.Visibility = Visibility.Visible;
                }

                // Restore Series Info
                UpdateInfoPanelVisibility(false);
            }
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
                .Replace("â€", "\"")
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

        private void AddonSelectorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListView lv && lv.SelectedItem is StremioAddonViewModel addon)
            {
                SourcesListView.ItemsSource = addon.Streams;


                // Sync the other list if one changes



                // Scroll to active item
                ScrollToActiveSource();
            }
        }

        private void ScrollToActiveSource()
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // Allow UI to populate
                    await Task.Delay(100);
                    

                    var list = SourcesListView;
                    if (list == null) return;

                    // Determine what should be "active"
                    string activeUrl = _streamUrl;
                    if (string.IsNullOrEmpty(activeUrl) && !string.IsNullOrEmpty(_currentStremioVideoId))
                    {
                        activeUrl = HistoryManager.Instance.GetProgress(_currentStremioVideoId)?.StreamUrl;
                    }

                    System.Diagnostics.Debug.WriteLine($"[Stremio] ScrollToActiveSource: List={list.Name}, Items={list.Items?.Count}, ActiveUrl={activeUrl ?? "NONE"}");

                    if (list.ItemsSource is List<StremioStreamViewModel> streams)
                    {
                        StremioStreamViewModel activeModel = null;
                        string activeFileName = null;
                        if (!string.IsNullOrEmpty(activeUrl))
                        {
                            try { activeFileName = System.IO.Path.GetFileName(new Uri(activeUrl).LocalPath); } catch { }
                        }

                        // Refresh active state for all visible items
                        foreach (var s in streams)
                        {
                            bool isActive = !string.IsNullOrEmpty(activeUrl) && s.Url == activeUrl;
                            if (!isActive && !string.IsNullOrEmpty(activeUrl) && !string.IsNullOrEmpty(activeFileName))
                            {
                                try
                                {
                                    string currentFileName = System.IO.Path.GetFileName(new Uri(s.Url).LocalPath);
                                    if (!string.IsNullOrEmpty(currentFileName) && currentFileName == activeFileName) isActive = true;
                                }
                                catch { }
                            }
                            
                            s.IsActive = isActive;
                            if (isActive) activeModel = s;
                        }

                        System.Diagnostics.Debug.WriteLine($"[Stremio] Active Item Found: {activeModel?.Title ?? "NULL"}");

                        if (activeModel != null)
                        {
                            list.ScrollIntoView(activeModel);
                        }
                    }
                }
                catch (Exception ex) 
                {
                    System.Diagnostics.Debug.WriteLine($"[Stremio] ScrollToActiveSource Error: {ex.Message}");
                }
            });
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
                        bool isActive = stream.Url == activeUrl;
                        if (!isActive && !string.IsNullOrEmpty(activeFileName))
                        {
                            try
                            {
                                string currentFileName = System.IO.Path.GetFileName(new Uri(stream.Url).LocalPath);
                                if (!string.IsNullOrEmpty(currentFileName) && currentFileName == activeFileName) isActive = true;
                            }
                            catch { }
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
                    if (AddonSelectorList != null) AddonSelectorList.SelectedItem = activeAddon;

                }
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[Stremio] SyncAddonSelectionToActive Error: {ex.Message}");
            }
        }

        private async void SourcesListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is StremioStreamViewModel vm)
            {
                // Update Active State
                if (_addonResults != null)
                {
                    foreach(var addon in _addonResults)
                    {
                        if (addon.Streams != null)
                        {
                            foreach(var stream in addon.Streams)
                            {
                                stream.IsActive = (stream == vm);
                            }
                        }
                    }
                }

                string playUrl = vm.Url;

                // [NEW] Resolve internal IPTV series references to actual stream URLs
                if (playUrl.StartsWith("iptv://series/") && App.CurrentLogin != null && _selectedEpisode != null)
                {
                    string seriesIdStr = playUrl.Replace("iptv://series/", "");
                    if (int.TryParse(seriesIdStr, out int seriesId))
                    {
                        try
                        {
                            var info = await Services.ContentCacheService.Instance.GetSeriesInfoAsync(seriesId, App.CurrentLogin);
                            if (info != null && info.Episodes != null)
                            {
                                string seasonKey = _selectedEpisode.SeasonNumber.ToString();
                                if (info.Episodes.TryGetValue(seasonKey, out var eps))
                                {
                                    var epMatch = eps.FirstOrDefault(e => e.EpisodeNum?.ToString() == _selectedEpisode.EpisodeNumber.ToString());
                                    if (epMatch != null)
                                    {
                                        string host = App.CurrentLogin.Host?.TrimEnd('/') ?? "";
                                        playUrl = $"{host}/series/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{epMatch.Id}.{epMatch.ContainerExtension ?? "mp4"}";
                                        AppLogger.Info($"[IPTV_PLAY] Resolved {vm.Url} to {playUrl} for Season {_selectedEpisode.SeasonNumber} Episode {_selectedEpisode.EpisodeNumber}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { AppLogger.Error($"[IPTV_PLAY] Series resolution failed: {ex.Message}"); }
                    }
                }

                _streamUrl = playUrl;
                if (!string.IsNullOrEmpty(_streamUrl))
                {
                    _ = UpdateTechnicalBadgesAsync(_streamUrl);
                }

                string title = _selectedEpisode?.Title ?? _item.Title;
                string videoId = ResolveBestContentId(_selectedEpisode?.Id ?? (_item as Models.Stremio.StremioMediaStream).Meta.Id);

                if (!string.IsNullOrEmpty(vm.Url))
                {
                    // [FIX] Persist choice immediately so it's remembered even if user navigates away without playing
                    var history = HistoryManager.Instance.GetProgress(videoId);
                    HistoryManager.Instance.UpdateProgress(videoId, title, _streamUrl, history?.Position ?? 0, history?.Duration ?? 0);

                    // Check history for resume position
                    double resumeSeconds = -1;
                    if (history != null && !history.IsFinished && history.Position > 0)
                    {
                        resumeSeconds = history.Position;
                    }

                    // Determine stream type and parent ID
                    string streamType = "movie";
                    string parentIdStr = null;
                    if (_item is SeriesStream ss)
                    {
                        streamType = "series";
                        parentIdStr = ss.SeriesId.ToString();
                    }
                    else if (_item is Models.Stremio.StremioMediaStream stType && (stType.Meta.Type == "series" || stType.Meta.Type == "tv"))
                    {
                        streamType = "series";
                        parentIdStr = stType.Meta.Id;
                    }

                    // --- PLAYER REUSE LOGIC ---
                    bool hasExistingPlayer = MediaInfoPlayer != null;
                    string currentPlayerPath = null;

                    if (hasExistingPlayer)
                    {
                        try { currentPlayerPath = await MediaInfoPlayer.GetPropertyAsync("path"); } catch {}
                    }

                    bool isSameSource = hasExistingPlayer 
                        && !string.IsNullOrEmpty(currentPlayerPath) 
                        && currentPlayerPath != "N/A"
                        && currentPlayerPath == playUrl;

                    if (isSameSource)
                    {
                        // CASE 1: Same source — use prebuffered player via handoff
                        Debug.WriteLine($"[SourceSelect] Same source selected — using prebuffered player via handoff.");
                        await PerformHandoverAndNavigate(playUrl, title, videoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, resumeSeconds, _item.PosterUrl, streamType);
                    }
                    else if (hasExistingPlayer && !string.IsNullOrEmpty(currentPlayerPath) && currentPlayerPath != "N/A")
                    {
                        // CASE 2: Different source — reuse existing player, just switch URL
                        Debug.WriteLine($"[SourceSelect] Different source selected — switching URL in existing player: {playUrl}");

                        try
                        {
                            // Cancel any ongoing prebuffering
                            try { _prebufferCts?.Cancel(); _prebufferCts?.Dispose(); _prebufferCts = null; } catch {}

                            // Set start time before opening new URL (native MPV feature for instant seek)
                            if (resumeSeconds > 0)
                            {
                                await MediaInfoPlayer.SetPropertyAsync("start", resumeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                await MediaInfoPlayer.SetPropertyAsync("start", "0");
                            }

                            // Configure for new URL (cookies/headers may differ)
                            await MpvSetupHelper.ConfigurePlayerAsync(MediaInfoPlayer, playUrl, isSecondary: true);

                            // Switch to new source
                            await MediaInfoPlayer.SetPropertyAsync("pause", "yes");
                            await MediaInfoPlayer.OpenAsync(playUrl);
                            await MediaInfoPlayer.SetPropertyAsync("mute", "yes");

                            Debug.WriteLine($"[SourceSelect] URL switched successfully. Performing handoff.");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SourceSelect] URL switch failed: {ex.Message}. Falling back to direct navigate.");
                            // Fallback: direct navigation
                            _sourceAddonUrl = vm.AddonUrl;
                            Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(playUrl, title, videoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, resumeSeconds, _item.PosterUrl, streamType, GetCurrentBackdrop(), GetLogoUrl(), _primaryColorHex, _sourceAddonUrl));
                            return;
                        }

                        _sourceAddonUrl = vm.AddonUrl;
                        await PerformHandoverAndNavigate(playUrl, title, videoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, resumeSeconds, _item.PosterUrl, streamType, GetCurrentBackdrop());
                    }
                    else
                    {
                        // CASE 3: No active player — direct navigation (fresh start)
                        Debug.WriteLine($"[SourceSelect] No active player — direct navigate for fresh start.");
                        _sourceAddonUrl = vm.AddonUrl;
                        Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(playUrl, title, videoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, resumeSeconds, _item.PosterUrl, streamType, GetCurrentBackdrop(), GetLogoUrl(), _primaryColorHex, _sourceAddonUrl));
                    }
                }
                else if (!string.IsNullOrEmpty(vm.ExternalUrl))
                {
                    _ = Windows.System.Launcher.LaunchUriAsync(new Uri(vm.ExternalUrl));
                }
                else if (!string.IsNullOrEmpty(vm.OriginalStream.InfoHash))
                {
                    var tip = new TeachingTip { Title = "Torrent Bilgisi", Subtitle = "Torrent akışları yakında desteklenecek. Lütfen HTTP kaynaklarını kullanın.", IsLightDismissEnabled = true };
                    tip.XamlRoot = this.XamlRoot;
                    tip.IsOpen = true;
                }
                else
                {
                    // No URL available (e.g. informative message)
                    System.Diagnostics.Debug.WriteLine($"[Stremio] Clicked item with no URL or InfoHash: {vm.Title}");
                }
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
            if (!string.IsNullOrEmpty(_streamUrl))
            {
                var pkg = new DataPackage();
                pkg.SetText(_streamUrl);
                Clipboard.SetContent(pkg);
                
                // Show Feedback
                CopyFeedbackTip.Target = sender as FrameworkElement;
                CopyFeedbackTip.IsOpen = true;
            }
        }

        private List<System.Threading.CancellationTokenSource> _activeDownloads = new();

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
             if (string.IsNullOrEmpty(_streamUrl)) return;

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
                 await DownloadSingle();
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
                  string fileName = TitleText.Text;
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
                      Services.DownloadManager.Instance.StartDownload(file, _streamUrl, TitleText.Text);
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
            if (MetadataShimmer == null || TechBadgesContent == null || _compositor == null) return;

            if (isLoading)
            {
                if (MetadataRibbon != null) MetadataRibbon.Visibility = Visibility.Visible;
                MetadataShimmer.Width = double.NaN;
                MetadataShimmer.Visibility = Visibility.Visible;
                
                var visShim = ElementCompositionPreview.GetElementVisual(MetadataShimmer);
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
                        fadeIn.InsertKeyFrame(1f, 1f);
                        fadeIn.Duration = TimeSpan.FromMilliseconds(400);
                        visContent.StartAnimation("Opacity", fadeIn);
                        TechBadgesContent.Opacity = 1;
                    }

                    if (MetadataRibbon != null) MetadataRibbon.Visibility = Visibility.Visible;
                }

                // Fade Out Shimmer
                var visShimmer = ElementCompositionPreview.GetElementVisual(MetadataShimmer);
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
                        if (MetadataShimmer != null)
                        {
                            MetadataShimmer.Visibility = Visibility.Collapsed;
                            MetadataShimmer.Width = double.NaN;
                        }
                        UpdateTechnicalSectionVisibility(spansSpace);
                    };
                    batch.End();
                }
                else
                {
                    MetadataShimmer.Visibility = Visibility.Collapsed;
                    UpdateTechnicalSectionVisibility(spansSpace);
                }
            }
        }

        private void AdjustMetadataShimmer()
        {
            if (MetadataShimmer == null || TechBadgesContent == null) return;
            
            var visibleBorders = TechBadgesContent.Children.OfType<Border>()
                                   .Where(c => c.Visibility == Visibility.Visible)
                                   .ToList();

            for (int i = 0; i < MetadataShimmer.Children.Count; i++)
            {
                var shim = MetadataShimmer.Children[i] as FrameworkElement;
                if (shim == null) continue;

                if (i < visibleBorders.Count)
                {
                    var border = visibleBorders[i];
                    shim.Visibility = Visibility.Visible;
                    
                    // Sync width from actual badge
                    if (border.ActualWidth > 0)
                    {
                        shim.Width = border.ActualWidth;
                    }
                    else
                    {
                        // Fallback estimate if not yet measured
                        shim.Width = 50; 
                    }
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

            int currentVersion = _loadingVersion;

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
                    DispatcherQueue.TryEnqueue(() => 
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

                // 1. Check Cache
                await Services.ProbeCacheService.Instance.EnsureLoadedAsync();
                if (Services.ProbeCacheService.Instance.Get(url) is Services.ProbeData cached)
                {
                    Services.CacheLogger.Success(Services.CacheLogger.Category.MediaInfo, "Badges Cache Hit", url);

                    DispatcherQueue.TryEnqueue(async () =>
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
                    var extracted = await Services.StreamProberService.ExtractProbeDataAsync(MediaInfoPlayer, token);
                    probeResult = new Services.ProbeResult
                    {
                        Resolution = extracted.Res,
                        Fps = extracted.Fps,
                        Codec = extracted.Codec,
                        Bitrate = extracted.Bitrate,
                        IsHdr = extracted.IsHdr,
                        Success = extracted.Success
                    };
                }
                else
                {
                    Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "DEDICATED PROBE: Starting prober service", url);
                    probeResult = await Services.StreamProberService.Instance.ProbeAsync(url, token);
                }

                if (token.IsCancellationRequested) return;

                if (probeResult.Success)
                {
                    // Manual cache update for SMART PROBE if needed
                    if (MediaInfoPlayer != null && _prebufferUrl == url)
                    {
                        Services.ProbeCacheService.Instance.Update(url, probeResult.Resolution, probeResult.Fps, probeResult.Codec, probeResult.Bitrate, probeResult.IsHdr);
                    }

                    var probeData = new Services.ProbeData
                    {
                        Resolution = probeResult.Resolution,
                        Fps = probeResult.Fps,
                        Codec = probeResult.Codec,
                        Bitrate = probeResult.Bitrate,
                        IsHdr = probeResult.IsHdr
                    };

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        double shimmerWidth = MetadataShimmer.ActualWidth;
                        TechBadgesContent.Visibility = Visibility.Visible;
                        ApplyMetadataToUi(probeData, currentVersion);
                        
                        bool hasVisibleBadges = HasVisibleBadges();
                        if (hasVisibleBadges && shimmerWidth > 0 && TechBadgesContent.ActualWidth < shimmerWidth)
                        {
                            TechBadgesContent.MinWidth = shimmerWidth;
                        }
                        else
                        {
                            TechBadgesContent.MinWidth = 0;
                        }
                        
                        if (TechBadgesContent.ActualWidth > shimmerWidth && MetadataShimmer != null)
                        {
                            MetadataShimmer.Width = TechBadgesContent.ActualWidth;
                        }
                        SetBadgeLoadingState(false);
                    });
                }
                else
                {
                    Services.CacheLogger.Warning(Services.CacheLogger.Category.MediaInfo, "Probe Failed", url);
                    DispatcherQueue.TryEnqueue(() => SetBadgeLoadingState(false));
                }
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.MediaInfo, "Probe Error", ex.Message);
                DispatcherQueue.TryEnqueue(() => SetBadgeLoadingState(false));
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
            if (currentVersion != _loadingVersion) return;

            if (_unifiedMetadata == null)
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

        private void EnsureHeroVisuals()
        {
             // Helper to attach SizeChanged to keep CenterPoint correct for Composition
             void Attach(Image img)
             {
                 if (img == null) return;
                 var v = ElementCompositionPreview.GetElementVisual(img);
                 // Initial
                 v.CenterPoint = new Vector3((float)img.ActualWidth / 2f, (float)img.ActualHeight / 2f, 0);
                 
                 // Event (Idempotent-ish: we use a named method check or just adding is fine if not adding duplicates...
                 // Safe approach: Remove then Add)
                 img.SizeChanged -= OnHeroSizeChanged;
                 img.SizeChanged += OnHeroSizeChanged;
                 img.ImageOpened -= HeroImage_ImageOpened;
                 img.ImageOpened += HeroImage_ImageOpened;
                 img.ImageFailed -= HeroImage_ImageFailed;
                 img.ImageFailed += HeroImage_ImageFailed;
             }
             Attach(HeroImage);
             Attach(HeroImage2);
        }
        
        private void OnHeroSizeChanged(object sender, SizeChangedEventArgs e)
        {
             if (sender is Image img)
             {
                 var v = ElementCompositionPreview.GetElementVisual(img);
                 v.CenterPoint = new Vector3((float)img.ActualWidth / 2f, (float)img.ActualHeight / 2f, 0);
             }
        }

        private void AddBackdropToSlideshow(string url)
        {
            if (string.IsNullOrEmpty(url) || _backdropUrls == null) return;
            if (ImageHelper.IsPlaceholder(url)) return;

            string key = GetNormalizedImageKey(url);
            if (_backdropKeys.Contains(key)) return;

            _backdropKeys.Add(key);
            _backdropUrls.Add(url);

            // If this is the second image and no timer is running, start it
            if (_backdropUrls.Count == 2 && (_slideshowTimer == null || !_slideshowTimer.IsEnabled))
            {
                InitializeSlideshowTimer();
            }
        }

        // [REM] Removed ValidateAndAddBackdropAsync - replaced by lazy validation in PerformHeroCrossfadeAsync

        private void StartBackgroundSlideshow(List<string> images)
        {
            if (images == null || images.Count == 0 || HeroImage == null) return;
            
            // Deduplicate using a stable ID that won't change after metadata enrichments (Title based)
            string currentId = $"{_item?.Title}";
            if (!string.IsNullOrEmpty(_item?.IMDbId)) currentId += $"_{_item.IMDbId.Replace("imdb_id:", "")}";

            // If the slideshow ID matches, add new images incrementally without resetting.
            if (_slideshowId == currentId)
            {
                System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Item match ({currentId}). Adding {images.Count} images incrementally.");
                foreach (var img in images)
                {
                    AddBackdropToSlideshow(img);
                }

                if (_backdropUrls != null && _backdropUrls.Count > 1 && (_slideshowTimer == null || !_slideshowTimer.IsEnabled))
                {
                    InitializeSlideshowTimer();
                }
                return;
            }

            _slideshowId = currentId;

            // Stop existing timer
            if (_slideshowTimer != null)
            {
                _slideshowTimer.Stop();
                _slideshowTimer = null;
            }

            // [NEW] Completely reset for a fundamentally new item
            _backdropKeys.Clear();
            _validatedBackdrops.Clear();
            _backdropUrls = new List<string>(); 
            _currentBackdropIndex = 0;
            // [FIX] Removed: _isFirstImageApplied = false; 
            // Resetting it here causes PopulateMetadataUI to trigger a hard ApplyHeroSeedImage swap (flicker).
            // Resetting is now handled correctly in ResetPageState.
            
            // [REM] HeroImage.Source = null; Removed to prevent flicker.
            // ResetPageState or a truly new navigation already handles clearing if needed.
            // Keeping the seeded poster visible until the slideshow crossfades into a high-res backdrop.

            System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] New Slideshow ID: {currentId}. Offloading {images.Count} images.");
            
            foreach (var img in images)
            {
                AddBackdropToSlideshow(img);
            }

            // Ensure visuals are ready
            EnsureHeroVisuals();
        }


        private void InitializeSlideshowTimer()
        {
            if (_backdropUrls == null || _backdropUrls.Count <= 1) return;
            if (_slideshowTimer != null) _slideshowTimer.Stop();

            _slideshowTimer = new DispatcherTimer();
            _slideshowTimer.Interval = TimeSpan.FromSeconds(8);
            _slideshowTimer.Tick += async (s, e) =>
            {
                if (HeroImage == null || HeroImage2 == null || _backdropUrls == null || _backdropUrls.Count <= 1 || _isHeroTransitionInProgress)
                {
                    return;
                }

                _currentBackdropIndex = (_currentBackdropIndex + 1) % _backdropUrls.Count;
                string nextImgUrl = _backdropUrls[_currentBackdropIndex];

                await PerformHeroCrossfadeAsync(nextImgUrl, 2.0, async (incoming) => {
                    // Final Duplicate Check
                    string signature = await CalculateVisualSignatureAsync(incoming);
                    bool isDuplicate = false;
                    foreach (var entry in _validatedBackdrops)
                    {
                        if (IsSignatureSimilar(signature, entry.Signature))
                        {
                            isDuplicate = true;
                            break;
                        }
                    }

                    if (isDuplicate)
                    {
                        System.Diagnostics.Debug.WriteLine("[SLIDESHOW] Found duplicate in loop - Skipping.");
                        // We can't easily cancel the crossfade once it's started without complex logic, 
                        // so we just let it finish but stop the timer temporarily to re-evaluate if needed.
                    }
                    else
                    {
                        _lastVisualSignature = signature;
                    }

                    // [REM] ArmValidatedBackdropAmbience removed
                });;
            };
            _slideshowTimer.Start();
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
                var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
                offsetAnimation.Target = "Offset";
                offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
                offsetAnimation.Duration = TimeSpan.FromMilliseconds(200); // Snappier glide (was 450ms)
                var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
                offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);

                // 2. Scale Animation
                var scaleAnimation = _compositor.CreateVector3KeyFrameAnimation();
                scaleAnimation.Target = "Scale";
                scaleAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
                scaleAnimation.Duration = TimeSpan.FromMilliseconds(200); // Snappier glide (was 450ms)
                
                var implicitAnimationCollection = _compositor.CreateImplicitAnimationCollection();
                implicitAnimationCollection["Offset"] = offsetAnimation;
                implicitAnimationCollection["Scale"] = scaleAnimation;
                // [FIX] REMOVED "Translation" from implicit collection as it causes WinRT ArgumentException
                // on elements where SetIsTranslationEnabled hasn't been explicitly called.

                // Elements that should "glide" during resize
                UIElement[] glideElements = { 
                    InfoContainer, InfoColumn, MetadataRibbon, ActionBarPanel, 
                    CastSection, DirectorSection, 
                    EpisodesPanel, SourcesPanel, 

                    IdentityStack, OverviewPanel, GenresText, MetadataPanel
                };

                foreach (var element in glideElements)
                {
                    if (element == null) continue;
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    
                    // [STABILITY FIX] InfoColumn and InfoContainer should NOT have implicit Offset animations
                    // because they are managed in real-time at 60fps in UpdateRealTimeScaling.
                    // Gliding them creates a lag ("drop") during resize.
                    if (element == InfoColumn || element == InfoContainer)
                    {
                        var anchorCollection = _compositor.CreateImplicitAnimationCollection();
                        anchorCollection["Scale"] = scaleAnimation; // Only scale can glide
                        visual.ImplicitAnimations = anchorCollection;
                    }
                    else
                    {
                        visual.ImplicitAnimations = implicitAnimationCollection;
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

        private void AnimatePanelReveal(FrameworkElement panel, CompositeTransform transform, bool isHorizontal, bool isShowing)
        {
            if (panel == null || transform == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(panel);
            visual.StopAnimation("Opacity");
            
            if (isShowing)
            {
                panel.Opacity = 0;
                visual.Opacity = 0f;
                
                if (isHorizontal) transform.TranslateX = 40;
                else transform.TranslateY = 40;

                panel.Visibility = Visibility.Visible; // Guard against prior collapsed states

                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(1f, 1f);
                fadeIn.Duration = TimeSpan.FromMilliseconds(500);
                visual.StartAnimation("Opacity", fadeIn);
                panel.Opacity = 1;

                var slideIn = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(600),
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(slideIn, transform);
                Storyboard.SetTargetProperty(slideIn, isHorizontal ? "TranslateX" : "TranslateY");
                
                var sb = new Storyboard();
                sb.Children.Add(slideIn);
                sb.Begin();
            }
        }

        private void SetupAnticipationPulse(Button btn, FrameworkElement content)
        {
            if (btn == null || content == null) return;
            
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
                    btnVisual.StopAnimation("Translation"); 
                    btnVisual.Properties.InsertVector3("Translation", new Vector3(moveX, moveY, 0));
                }
                catch {}
            };

            btn.PointerEntered += (s, e) => {
                try {
                    // Pulse Scale on Content
                    contentVisual.StopAnimation("Scale");
                    var pulse = _compositor.CreateVector3KeyFrameAnimation();
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
                    var resetScale = _compositor.CreateVector3KeyFrameAnimation();
                    resetScale.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
                    resetScale.Duration = TimeSpan.FromMilliseconds(300);
                    contentVisual.StartAnimation("Scale", resetScale);
                    
                    // Reset Position (Spring back)
                    btnVisual.StopAnimation("Translation");
                    var resetPos = _compositor.CreateVector3KeyFrameAnimation();
                    resetPos.InsertKeyFrame(1f, Vector3.Zero);
                    resetPos.Duration = TimeSpan.FromMilliseconds(400);
                    // Use Cubic Bezier for smooth return
                    resetPos.InsertKeyFrame(0.5f, Vector3.Zero, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0f, 1f))); 
                    // Actually simple keyframe to 0 is fine
                    btnVisual.StartAnimation("Translation", resetPos);
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
                     seriesId = st.Id.ToString(); // Stremio ID is usually string hash in IMediaStream, but st.IMDbId is string
                     // Actually st.Id is int (hash). HistoryManager expects string?
                     // Let's use st.IMDbId if available, or just _item.Id.ToString()
                     seriesId = st.IMDbId ?? st.Id.ToString();
                     seriesName = st.Title; // StremioMediaStream has Title, not Name directly exposed publicly except via Meta
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
            
            if (EpisodesListView.ItemsSource is IEnumerable<EpisodeItem> episodes)
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
            if (_item == null) return;
            
            bool alreadyIn = Services.WatchlistManager.Instance.IsOnWatchlist(_item);
            if (alreadyIn)
                await Services.WatchlistManager.Instance.RemoveFromWatchlist(_item);
            else
                await Services.WatchlistManager.Instance.AddToWatchlist(_item);

            UpdateWatchlistState(true);
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

        private async Task PerformUpgradeCrossfadeAsync(string url)
        {
            await PerformHeroCrossfadeAsync(url, 1.8);
        }

        private async Task PerformHeroCrossfadeAsync(string imageUrl, double durationSeconds = 1.8, Func<Image, Task> onOpenedAsync = null)
        {
            if (string.IsNullOrEmpty(imageUrl) || HeroImage == null || HeroImage2 == null || _isHeroTransitionInProgress) return;

            // Toggle Logic: Load into the INACTIVE image
            Image incoming = _isHeroImage1Active ? HeroImage2 : HeroImage;
            Image outgoing = _isHeroImage1Active ? HeroImage : HeroImage2;

            // [FIX] Safety check: If the target URL is already set on the visible image, skip.
            // [OPTIMIZATION] Normalize URLs for robust comparison.
            string NormalizeUrl(string url) => url?.Replace("https://", "http://")?.TrimEnd('/')?.ToLowerInvariant();
            
            string normTarget = NormalizeUrl(imageUrl);
            string normCurrent = "";
            if (outgoing.Source is BitmapImage biCurrent) normCurrent = NormalizeUrl(biCurrent.UriSource?.ToString());

            if (normCurrent == normTarget && outgoing.Opacity > 0.05)
            {
                 System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Skip transition: Source already active (Normalized): {imageUrl}");
                 return;
            }

            try
            {
                _isHeroTransitionInProgress = true;

                // Ensure compositor is ready
                if (_compositor == null) EnsureHeroVisuals();

                var visualIncoming = ElementCompositionPreview.GetElementVisual(incoming);
                var visualOutgoing = ElementCompositionPreview.GetElementVisual(outgoing);

                // [CRITICAL] Pre-hide the incoming layer completely before setting source
                visualIncoming.Opacity = 0;
                incoming.Opacity = 1; // Make it visible to XAML, but transparent at compositor level

                bool isResolved = false;
                RoutedEventHandler openedHandler = null;
                ExceptionRoutedEventHandler failedHandler = null;

                Action cleanup = () => {
                    incoming.ImageOpened -= openedHandler;
                    incoming.ImageFailed -= failedHandler;
                };

                openedHandler = async (s, e) =>
                {
                    if (isResolved) return;
                    isResolved = true;

                    // Execute custom logic while image is loaded but still invisible
                    if (onOpenedAsync != null) await onOpenedAsync(incoming);

                    cleanup();

                    // Ensure it is still hidden before we start the animation
                    visualIncoming.Opacity = 0;

                    // Start Ken Burns on Incoming
                    StartKenBurnsEffect(incoming);

                    // Crossfade
                    var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(0f, 0f);
                    fadeIn.InsertKeyFrame(1f, 1f);
                    fadeIn.Duration = TimeSpan.FromSeconds(durationSeconds);

                    var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                    fadeOut.InsertKeyFrame(0f, 1f);
                    fadeOut.InsertKeyFrame(1f, 0f);
                    fadeOut.Duration = TimeSpan.FromSeconds(durationSeconds);

                    visualIncoming.StartAnimation("Opacity", fadeIn);
                    visualOutgoing.StartAnimation("Opacity", fadeOut);

                    System.Diagnostics.Debug.WriteLine(
                        $"[SLIDESHOW][CROSSFADE] start incoming={GetImageSourceUrl(incoming) ?? "<null>"} outgoing={GetImageSourceUrl(outgoing) ?? "<null>"} inOpacity={incoming.Opacity:F2} outOpacity={outgoing.Opacity:F2} duration={durationSeconds:F2}s");

                    // Update State
                    _isHeroImage1Active = !_isHeroImage1Active;
                    int transitionEpoch = Volatile.Read(ref _ambienceNavigationEpoch);
                    _ = Task.Delay(TimeSpan.FromSeconds(durationSeconds) + TimeSpan.FromMilliseconds(80)).ContinueWith(t =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (transitionEpoch != Volatile.Read(ref _ambienceNavigationEpoch))
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[SLIDESHOW][CROSSFADE] skipped finalize because navigation changed | url={GetImageSourceUrl(incoming) ?? "<null>"}");
                                return;
                            }

                            _isHeroTransitionInProgress = false;
                            System.Diagnostics.Debug.WriteLine(
                                $"[SLIDESHOW][CROSSFADE] complete active={GetImageSourceUrl(GetActiveHeroImage()) ?? "<null>"} incomingOpacity={incoming.Opacity:F2} outgoingOpacity={outgoing.Opacity:F2}");
                        });
                    }, TaskScheduler.Default);
                };

                failedHandler = (s, e) => {
                    if (isResolved) return;
                    isResolved = true;
                    cleanup();
                    _isHeroTransitionInProgress = false;
                    System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Crossfade Failed for {imageUrl}: {e.ErrorMessage}");
                };

                incoming.ImageOpened += openedHandler;
                incoming.ImageFailed += failedHandler;
                // [LAZY VALIDATION] Increment index and allow natural crossfade.
                // The ImageOpened handler (triggered by incoming.Source assignment)
                // handles the ambience extraction and future signature deduplication.
                incoming.Source = new BitmapImage(new Uri(imageUrl));

                // Guard against hanging
                _ = Task.Delay(10000).ContinueWith(t => {
                    if (!isResolved) { 
                        isResolved = true; 
                        DispatcherQueue.TryEnqueue(() => {
                            cleanup();
                            _isHeroTransitionInProgress = false;
                        }); 
                    }
                });
            }
            catch (Exception ex)
            {
                _isHeroTransitionInProgress = false;
                System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Crossfade Error: {ex.Message}");
            }
        }

        // [REM] Duplicate CalculateVisualSignatureAsync removed. Consolidated version is at line 2896.

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
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
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
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
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
                // CRITICAL: Set ZIndex on the actual ListViewItem container to prevent sibling clipping
                var container = FindParent<ListViewItem>(element);
                if (container != null) Canvas.SetZIndex(container, 100);
                
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;

                // Ensure CenterPoint is set
                visual.CenterPoint = new System.Numerics.Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);

                var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1.08f, 1.08f, 1.0f));
                scaleAnim.Duration = TimeSpan.FromMilliseconds(250);
                visual.StartAnimation("Scale", scaleAnim);
            }
        }

        private void CastItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                var container = FindParent<ListViewItem>(element);
                if (container != null) Canvas.SetZIndex(container, 0);
                
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var scaleAnim = visual.Compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1.0f, 1.0f, 1.0f));
                scaleAnim.Duration = TimeSpan.FromMilliseconds(200);
                visual.StartAnimation("Scale", scaleAnim);
            }
        }

        private T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindParent<T>(parentObject);
        }
        private void DeselectEpisode()
        {
            _isProgrammaticSelection = true;
            try
            {
                if (EpisodesListView != null) EpisodesListView.SelectedItem = null;

                _selectedEpisode = null;
                _streamUrl = null;
                
                // Close Sources
                ShowSourcesPanel(false);
                
                // Restore Series Info
                UpdateInfoPanelVisibility(false);
                
                // Restore Play Button
                if (PlayButtonText != null)
                {
                    PlayButtonText.Text = "Oynat";
                    if (PlayButtonSubtext != null) PlayButtonSubtext.Visibility = Visibility.Collapsed;
                }
                if (RestartButton != null) RestartButton.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _isProgrammaticSelection = false;
            }
        }

        private void SyncIdentityVisibility(bool showEpisode)
        {
            if (TitleText == null) return;

            bool hasLogo = _unifiedMetadata != null && !string.IsNullOrEmpty(_unifiedMetadata.LogoUrl);
            
            // 1. Logo Visibility
            if (hasLogo)
            {
                EnsureLogoSurface(_unifiedMetadata.LogoUrl);
                if (ContentLogoHost != null && ContentLogoHost.Visibility != Visibility.Visible) ContentLogoHost.Visibility = Visibility.Visible;
                if (_logoVisual != null && _logoVisual.Opacity != 1f) _logoVisual.Opacity = 1f;
            }
            else
            {
                if (ContentLogoHost != null && ContentLogoHost.Visibility != Visibility.Collapsed) ContentLogoHost.Visibility = Visibility.Collapsed;
                if (_logoVisual != null && _logoVisual.Opacity != 0f) _logoVisual.Opacity = 0f;
            }

            // 2. Title Text Visibility & Content
            bool shouldShowTitle = showEpisode || !hasLogo;
            string targetTitle = showEpisode && _selectedEpisode != null 
                ? (!string.IsNullOrEmpty(_selectedEpisode.Title) ? _selectedEpisode.Title : $"Bölüm {_selectedEpisode.EpisodeNumber}")
                : (_unifiedMetadata?.Title ?? _item?.Title ?? "");

            if (TitleText.Text != targetTitle) TitleText.Text = targetTitle;
            if (StickyTitle != null && StickyTitle.Text != targetTitle) StickyTitle.Text = targetTitle;

            if (TitleText.Visibility != (shouldShowTitle ? Visibility.Visible : Visibility.Collapsed))
                TitleText.Visibility = shouldShowTitle ? Visibility.Visible : Visibility.Collapsed;
            
            if (shouldShowTitle && TitleText.Opacity != 1) TitleText.Opacity = 1;

            // 3. Container Visibility
            if (TitlePanel != null && TitlePanel.Visibility != Visibility.Visible) 
                TitlePanel.Visibility = Visibility.Visible;
            if (TitlePanel != null && TitlePanel.Opacity != 1) TitlePanel.Opacity = 1;
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
                
                if (GenresText != null && GenresText.Visibility != Visibility.Collapsed)
                {
                    GenresText.Visibility = Visibility.Collapsed;
                    if (GenresText.Parent is Grid pGrid1) pGrid1.Visibility = Visibility.Collapsed;
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
        }

        private void EnsureLogoSurface(string url)
        {
            if (string.IsNullOrEmpty(url) || _compositor == null) return;
            
            // Redundancy guard
            if (_currentLogoUrl == url) return;
            _currentLogoUrl = url;

            // Reset existing state
            if (_logoSurface != null)
            {
                _logoSurface.Dispose();
                _logoSurface = null;
            }
            if (ContentLogoImage != null) ContentLogoImage.Source = null;

            _logoReadyTcs = new TaskCompletionSource<bool>();

            try
            {
                if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    // [FIX] Support SVG logos using SvgImageSource (WinUI 3 native)
                    System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Loading SVG Logo: {url}");
                    var svgSource = new SvgImageSource(new Uri(url));
                    ContentLogoImage.Source = svgSource;
                    ContentLogoImage.Visibility = Visibility.Visible;
                    
                    if (_logoVisual != null) _logoVisual.Opacity = 0; // Hide composition visual for SVG
                    
                    svgSource.Opened += (s, e) => _logoReadyTcs?.TrySetResult(true);
                    svgSource.OpenFailed += (s, e) => _logoReadyTcs?.TrySetResult(false);
                }
                else
                {
                    // Standard Image Loading (Composition-based for performance/flicker-free)
                    ContentLogoImage.Visibility = Visibility.Collapsed;
                    if (_logoVisual != null) _logoVisual.Opacity = 1;

                    _logoSurface = LoadedImageSurface.StartLoadFromUri(new Uri(url));
                    _logoSurface.LoadCompleted += (s, e) => {
                        var success = e.Status == LoadedImageSourceLoadStatus.Success;
                        _logoReadyTcs?.TrySetResult(success);
                        
                        if (success)
                        {
                            // [FIX] Once logo is ready, fade it in and hide the title text
                            DispatcherQueue.TryEnqueue(() => {
                                if (_currentLogoUrl == url) // Ensure we are still on the same item
                                {
                                    if (_logoVisual != null) _logoVisual.Opacity = 1;
                                    if (ContentLogoHost != null) ContentLogoHost.Opacity = 1;
                                    
                                    // [FIX] Respect ShowEpisode state: If an episode is already selected (e.g. Continue Watching auto-select),
                                    // we MUST keep TitleText visible to show the episode name under the logo.
                                    if (TitleText != null) TitleText.Visibility = (_selectedEpisode != null) ? Visibility.Visible : Visibility.Collapsed;
                                    
                                    System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Logo load success - Transitioned UI (TitleVisible={_selectedEpisode != null}).");
                                }
                            });
                        }
                    };
                    if (_logoBrush != null) _logoBrush.Surface = _logoSurface;
                }
                
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Logo surface assigned for: {url}");
            }
            catch (Exception ex)
            {
                _logoReadyTcs?.TrySetResult(false);
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] EnsureLogoSurface Error: {ex.Message}");
            }
        }

        private async Task HideSourcesPanelAsync()
        {
            if (SourcesPanel == null || _isSourcesPanelHidden) return;

            _isSourcesPanelHidden = true;
            
            // Enable high-performance translation/scale and get visuals
            ElementCompositionPreview.SetIsTranslationEnabled(SourcesPanel, true);
            var visual = ElementCompositionPreview.GetElementVisual(SourcesPanel);
            var contentVisual = ElementCompositionPreview.GetElementVisual(SourcesPanelInnerContent);
            var listVisual = ElementCompositionPreview.GetElementVisual(SourcesListView);
            var compositor = visual.Compositor;

            // Step 1: Prepare for Perfect Unsquashed Scaling (Expression-based)
            float width = (float)SourcesPanel.ActualWidth;
            float height = (float)SourcesPanel.ActualHeight;
            visual.CenterPoint = new Vector3(width / 2f, height / 2f, 0);
            contentVisual.CenterPoint = new Vector3(width / 2f, height / 2f, 0);

            // Use ExpressionAnimation for perfect, distortion-free synchronization
            var invScaleExpr = compositor.CreateExpressionAnimation("Vector3(1, 1.0 / panel.Scale.Y, 1)");
            invScaleExpr.SetReferenceParameter("panel", visual);
            contentVisual.StartAnimation("Scale", invScaleExpr);

            float targetScale = 0.35f;
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

            // Animate only the Panel Scale
            var scaleAnim = compositor.CreateScalarKeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1f, targetScale, easing);
            scaleAnim.Duration = TimeSpan.FromMilliseconds(550);

            // Animate List Fade
            var fadeOut = compositor.CreateScalarKeyFrameAnimation();
            fadeOut.InsertKeyFrame(1f, 0f, easing);
            fadeOut.Duration = TimeSpan.FromMilliseconds(350);

            visual.StartAnimation("Scale.Y", scaleAnim);
            listVisual.StartAnimation("Opacity", fadeOut);

            await Task.Delay(450);

            // Step 2: Slide Out (Translation.X) - Slower and more elegant
            var slideOut = compositor.CreateVector3KeyFrameAnimation();
            slideOut.InsertKeyFrame(1f, new Vector3(1000, 0, 0), easing); 
            slideOut.Duration = TimeSpan.FromMilliseconds(650);
            slideOut.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            visual.StartAnimation("Translation", slideOut);
            batch.End();

            // Show handle earlier (around mid-way) with a gentle fade
            await Task.Delay(400);
            if (SourcesShowHandle != null)
            {
                SourcesShowHandle.Visibility = Visibility.Visible;
                var handleVisual = ElementCompositionPreview.GetElementVisual(SourcesShowHandle);
                var handleFade = compositor.CreateScalarKeyFrameAnimation();
                handleFade.InsertKeyFrame(0f, 0f);
                handleFade.InsertKeyFrame(1f, 1f, easing);
                handleFade.Duration = TimeSpan.FromMilliseconds(450);
                handleVisual.StartAnimation("Opacity", handleFade);
            }
        }

        private async Task ShowSourcesPanelAsync()
        {
            if (SourcesPanel == null || !_isSourcesPanelHidden) return;

            _isSourcesPanelHidden = false;
            
            // Fade out handle gracefully
            if (SourcesShowHandle != null)
            {
                var handleVisual = ElementCompositionPreview.GetElementVisual(SourcesShowHandle);
                var hFadeOut = handleVisual.Compositor.CreateScalarKeyFrameAnimation();
                hFadeOut.InsertKeyFrame(1f, 0f);
                hFadeOut.Duration = TimeSpan.FromMilliseconds(300);
                handleVisual.StartAnimation("Opacity", hFadeOut);
                await Task.Delay(250);
                SourcesShowHandle.Visibility = Visibility.Collapsed;
            }

            var visual = ElementCompositionPreview.GetElementVisual(SourcesPanel);
            var contentVisual = ElementCompositionPreview.GetElementVisual(SourcesPanelInnerContent);
            var listVisual = ElementCompositionPreview.GetElementVisual(SourcesListView);
            var compositor = visual.Compositor;
            ElementCompositionPreview.SetIsTranslationEnabled(SourcesPanel, true);

            // [FIX] Update CenterPoint for Resize/Fullscreen stability
            float width = (float)SourcesPanel.ActualWidth;
            float height = (float)SourcesPanel.ActualHeight;
            visual.CenterPoint = new Vector3(width / 2f, height / 2f, 0);
            contentVisual.CenterPoint = new Vector3(width / 2f, height / 2f, 0);

            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

            // Ensure Expression is active
            var invScaleExpr = compositor.CreateExpressionAnimation("Vector3(1, 1.0 / panel.Scale.Y, 1)");
            invScaleExpr.SetReferenceParameter("panel", visual);
            contentVisual.StartAnimation("Scale", invScaleExpr);

            // Ensure content is hidden during the slide-in phase
            listVisual.Opacity = 0f;
            visual.Scale = new Vector3(1f, 0.35f, 1f);

            // Step 1: Slide In
            var slideIn = compositor.CreateVector3KeyFrameAnimation();
            slideIn.InsertKeyFrame(1f, new Vector3(0, 0, 0), easing);
            slideIn.Duration = TimeSpan.FromMilliseconds(850); 
            slideIn.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
            
            visual.StartAnimation("Translation", slideIn);

            await Task.Delay(750);

            // Step 2: Restore Unsquashed Scaling
            var restoreScale = compositor.CreateScalarKeyFrameAnimation();
            restoreScale.InsertKeyFrame(1f, 1.0f, easing);
            restoreScale.Duration = TimeSpan.FromMilliseconds(550);

            var fadeIn = compositor.CreateScalarKeyFrameAnimation();
            fadeIn.InsertKeyFrame(1f, 1.0f, easing);
            fadeIn.Duration = TimeSpan.FromMilliseconds(450);

            visual.StartAnimation("Scale.Y", restoreScale);
            listVisual.StartAnimation("Opacity", fadeIn);
        }

        private async void SourcesShowHandle_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await ShowSourcesPanelAsync();
        }

        private void SourcesShowHandle_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            if (SourcesPanel == null) return;
            
            // Re-assert ExpressionAnimation for high-performance pulling
            var visual = ElementCompositionPreview.GetElementVisual(SourcesPanel);
            var contentVisual = ElementCompositionPreview.GetElementVisual(SourcesPanelInnerContent);
            var invScaleExpr = visual.Compositor.CreateExpressionAnimation("Vector3(1, 1.0 / panel.Scale.Y, 1)");
            invScaleExpr.SetReferenceParameter("panel", visual);
            contentVisual.StartAnimation("Scale", invScaleExpr);
        }

        private void SourcesShowHandle_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            if (SourcesPanel == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(SourcesPanel);
            var contentVisual = ElementCompositionPreview.GetElementVisual(SourcesPanelInnerContent);
            var listVisual = ElementCompositionPreview.GetElementVisual(SourcesListView);

            visual.Properties.TryGetVector3("Translation", out var currentTrans);
            float newX = currentTrans.X + (float)e.Delta.Translation.X;

            if (newX < 0) newX = 0;
            if (newX > 1000) newX = 1000;

            visual.Properties.InsertVector3("Translation", new Vector3(newX, 0, 0));

            // Keep it collapsed and content hidden during the manual pull
            // Content reveal should only happen in ShowSourcesPanelAsync after commitment
            visual.CenterPoint = new Vector3((float)SourcesPanel.ActualWidth / 2f, (float)SourcesPanel.ActualHeight / 2f, 0);
            visual.Scale = new Vector3(1f, 0.35f, 1f);
            listVisual.Opacity = 0f;
            
            var invScaleExpr = visual.Compositor.CreateExpressionAnimation("Vector3(1, 1.0 / panel.Scale.Y, 1)");
            invScaleExpr.SetReferenceParameter("panel", visual);
            contentVisual.StartAnimation("Scale", invScaleExpr);
        }

        private async void SourcesShowHandle_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            if (SourcesPanel == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(SourcesPanel);
            visual.Properties.TryGetVector3("Translation", out var currentTrans);
            
            // Lower threshold: 15% pull (150px) is enough to trigger restore
            if (currentTrans.X < 850) 
            {
                await ShowSourcesPanelAsync();
            }
            else
            {
                _isSourcesPanelHidden = false; 
                await HideSourcesPanelAsync();
            }
        }

        private async void BtnHideSources_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSourcesPanelHidden)
            {
                await HideSourcesPanelAsync();
            }
        }

        private void BtnCloseSources_Click(object sender, RoutedEventArgs e)
        {
            _isSourcesPanelHidden = false; // Reset hidden state when fully closing
            DeselectEpisode();
        }

        private void BtnBackToEpisodes_Click(object sender, RoutedEventArgs e)
        {
            DeselectEpisode();
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
    public class SeasonItem
    {
        public string Name { get; set; }
        public string SeasonName { get; set; }
        public int SeasonNumber { get; set; }
        public List<EpisodeItem> Episodes { get; set; }
        public bool IsEnrichedByTmdb { get; set; }
    }

    public class EpisodeItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Name { get; set; }
        public string Overview { get; set; }
        public string Duration { get; set; }
        public string ImageUrl { get; set; }
        public Microsoft.UI.Xaml.Media.ImageSource Thumbnail { get; set; }
        public string StreamUrl { get; set; }
        public string Container { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public int IptvSeriesId { get; set; }
        public int? IptvStreamId { get; set; }
        public string IptvSourceTitle { get; set; }
        public string Resolution { get; set; }
        public string VideoCodec { get; set; }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }
        public string EpisodeNumberFormatted => $"{EpisodeNumber}. Bölüm";
        public string DurationFormatted { get; set; }
        public bool IsReleased { get; set; } = true;
        public DateTime? ReleaseDate { get; set; }
        public string ReleaseDateFormatted => ReleaseDate.HasValue ? ReleaseDate.Value.ToString("d MMMM yyyy", new System.Globalization.CultureInfo("tr-TR")) : "";
        
        private bool _isWatched;
        public bool IsWatched { get => _isWatched; set { if (_isWatched != value) { _isWatched = value; OnPropertyChanged(nameof(IsWatched)); } } }
        
        private bool _hasProgress;
        public bool HasProgress { get => _hasProgress; set { if (_hasProgress != value) { _hasProgress = value; OnPropertyChanged(nameof(HasProgress)); } } }
        
        private double _progressPercent;
        public double ProgressPercent { get => _progressPercent; set { if (Math.Abs(_progressPercent - value) > 0.01) { _progressPercent = value; OnPropertyChanged(nameof(ProgressPercent)); } } }
        
        public string ProgressText { get; set; }
        
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } } }

        public void RefreshHistoryState()
        {
            var history = ModernIPTVPlayer.HistoryManager.Instance.GetProgress(Id);
            UpdateProgress(history);
        }

        public void UpdateProgress(ModernIPTVPlayer.HistoryItem history)
        {
            if (history == null)
            {
                IsWatched = false; HasProgress = false; ProgressPercent = 0; ProgressText = "";
            }
            else
            {
                IsWatched = history.IsFinished;
                HasProgress = history.Position > 0 && !history.IsFinished;
                if (history.Duration > 0)
                {
                    ProgressPercent = (history.Position / history.Duration) * 100;
                    if (ProgressPercent > 98) { IsWatched = true; HasProgress = false; }
                    if (HasProgress)
                    {
                        var remaining = TimeSpan.FromSeconds(history.Duration - history.Position);
                        ProgressText = remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}sa {(int)remaining.Minutes}dk Kaldı" : $"{(int)remaining.TotalMinutes}dk Kaldı";
                    }
                }
                OnPropertyChanged(nameof(IsWatched)); 
                OnPropertyChanged(nameof(HasProgress)); 
                OnPropertyChanged(nameof(ProgressPercent)); 
                OnPropertyChanged(nameof(ProgressText));
            }
        }
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public class CastItem
    {
        public string Name { get; set; }
        public string Character { get; set; }
        public string FullProfileUrl { get; set; }
        public Microsoft.UI.Xaml.Media.ImageSource ProfileImage { get; set; }
    }

    public class StremioStreamViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public string Title { get; set; }
        public string Name { get; set; }
        public string ProviderText { get; set; }
        public string AddonName { get; set; }
        public string AddonUrl { get; set; }
        public string Url { get; set; }
        public int? IptvStreamId { get; set; }
        public int? IptvSeriesId { get; set; }
        public string ExternalUrl { get; set; }
        public bool IsExternalLink => !string.IsNullOrEmpty(ExternalUrl) && string.IsNullOrEmpty(Url);
        
        private string _quality;
        public string Quality { get => _quality; set { if (_quality != value) { _quality = value; OnPropertyChanged(nameof(Quality)); OnPropertyChanged(nameof(HasQuality)); } } }
        public bool HasQuality => !string.IsNullOrEmpty(Quality);
        
        private string _size;
        public string Size { get => _size; set { if (_size != value) { _size = value; OnPropertyChanged(nameof(Size)); OnPropertyChanged(nameof(HasSize)); } } }
        public bool HasSize => !string.IsNullOrEmpty(Size);
        
        private bool _isHdr;
        public bool IsHdr { get => _isHdr; set { if (_isHdr != value) { _isHdr = value; OnPropertyChanged(nameof(IsHdr)); } } }
        
        private string _codec;
        public string Codec { get => _codec; set { if (_codec != value) { _codec = value; OnPropertyChanged(nameof(Codec)); OnPropertyChanged(nameof(HasCodec)); } } }
        public bool HasCodec => !string.IsNullOrEmpty(Codec);
        
        public bool IsCached { get; set; }
        public ModernIPTVPlayer.Models.Stremio.StremioStream OriginalStream { get; set; }
        
        private bool _isActive;
        public bool IsActive { get => _isActive; set { if (_isActive != value) { _isActive = value; OnPropertyChanged(nameof(IsActive)); } } }
        
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public class StremioAddonViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name;
        public string Name { get => _name; set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } } }
        public string AddonUrl { get; set; }
        private List<StremioStreamViewModel> _streams;
        public List<StremioStreamViewModel> Streams { get => _streams; set { if (_streams != value) { _streams = value; OnPropertyChanged(nameof(Streams)); } } }
        private bool _isLoading;
        public bool IsLoading { get => _isLoading; set { if (_isLoading != value) { _isLoading = value; OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(IsLoaded)); } } }
        public bool IsLoaded => !IsLoading;
        public int SortIndex { get; set; }
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
