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

        private EpisodeItem _selectedEpisode;
        private SeasonItem _selectedSeason;
        private TmdbMovieResult _cachedTmdb;
        private SolidColorBrush _themeTintBrush;
        private bool _isInitializingSeriesUi;
        private readonly Dictionary<string, StremioSourcesCacheEntry> _stremioSourcesCache = new();
        private int _sourcesRequestVersion;
        private string _currentStremioVideoId;
        private bool _isSourcesFetchInProgress;
        private bool _isCurrentSourcesComplete;
        private bool _areSourcesVisible = false; // <--- New Field
        private bool _shouldAutoResume = false;
        
        private Models.Metadata.UnifiedMetadata _unifiedMetadata;
        private FFmpegProber _ffprober = new();
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

        public MediaInfoPage()
        {
            this.InitializeComponent();
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

            // UI Audio Feedback Setup
            this.ElementSoundMode = global::Microsoft.UI.Xaml.ElementSoundMode.Off;
            BackButton.ElementSoundMode = global::Microsoft.UI.Xaml.ElementSoundMode.Default;
            
            // Manual Layout Management
            this.SizeChanged += MediaInfoPage_SizeChanged;
            
            // Critical: Also listen to the ScrollViewer's size to sync height
            RootScrollViewer.SizeChanged += (s, e) => 
            {
                if (_isWideModeIndex == 1) // Wide mode
                {
                    ContentGrid.Height = e.NewSize.Height > 0 ? e.NewSize.Height : double.NaN;
                }
            };

            System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Constructor completed.");
            this.NavigationCacheMode = NavigationCacheMode.Required;
            SetupProfessionalAnimations();
        }

        private int _isWideModeIndex = -1; // -1: undefined, 0: narrow, 1: wide
        private bool _isSelectionSyncing = false;
        private bool _isHandoffInProgress = false;
        private DispatcherTimer _slideshowTimer;
        private string _slideshowId;
        private List<string> _backdropUrls = new List<string>();
        private int _currentBackdropIndex = 0;
        private bool _isHeroImage1Active = true;

        private void MediaInfoPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                EnsureTrailerOverlayBounds();
                if (TrailerOverlay?.Visibility == Visibility.Visible)
                {
                    ApplyTrailerFullscreenLayout(_isTrailerFullscreen);
                }

                double width = e.NewSize.Width;
                double height = e.NewSize.Height;
                bool isWide = width >= 900;
                int newState = isWide ? 1 : 0;

                System.Diagnostics.Debug.WriteLine($"[LayoutDebug] SizeChanged: {width}x{height}, isWide: {isWide}");

                if (_isWideModeIndex != newState)
                {
                    System.Diagnostics.Debug.WriteLine($"[LayoutDebug] State Change: {(_isWideModeIndex == 1 ? "Wide" : "Narrow")} -> {(isWide ? "Wide" : "Narrow")}");
                    _isWideModeIndex = newState;
                    UpdateLayoutState(isWide);
                }
                else if (isWide)
                {
                    SyncWideHeights();
                }
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[LayoutDebug] CRITICAL ERROR in SizeChanged: {ex}");
            }
        }

        private void EnsureTrailerOverlayBounds()
        {
            if (TrailerOverlay == null || TrailerScrim == null)
            {
                return;
            }

            double rootWidth = RootGrid?.ActualWidth ?? 0;
            double rootHeight = RootGrid?.ActualHeight ?? 0;
            double pageWidth = ActualWidth;
            double pageHeight = ActualHeight;
            double xamlRootWidth = XamlRoot?.Size.Width ?? 0;
            double xamlRootHeight = XamlRoot?.Size.Height ?? 0;

            double targetWidth = Math.Max(Math.Max(rootWidth, pageWidth), xamlRootWidth);
            double targetHeight = Math.Max(Math.Max(rootHeight, pageHeight), xamlRootHeight);

            if (targetWidth <= 0 || targetHeight <= 0)
            {
                return;
            }

            // Aggressive overdraw to prevent title bar/top edge leaks on fractional scaling.
            TrailerOverlay.Width = targetWidth + 48;
            TrailerOverlay.Height = targetHeight + 48;
            TrailerScrim.Width = targetWidth + 48;
            TrailerScrim.Height = targetHeight + 48;
        }

        private void ApplyTrailerFullscreenLayout(bool enable)
        {
            if (TrailerContent == null)
            {
                return;
            }

            if (!enable)
            {
                TrailerContent.Width = TrailerDefaultWidth;
                TrailerContent.Height = TrailerDefaultHeight;
                
                // Reset close button to default position
                if (CloseTrailerButton != null)
                {
                    CloseTrailerButton.Margin = new Thickness(16, 16, 16, 0);
                }
                return;
            }

            double overlayWidth = TrailerOverlay?.ActualWidth > 0 ? TrailerOverlay.ActualWidth : (RootGrid?.ActualWidth > 0 ? RootGrid.ActualWidth : ActualWidth);
            double overlayHeight = TrailerOverlay?.ActualHeight > 0 ? TrailerOverlay.ActualHeight : (RootGrid?.ActualHeight > 0 ? RootGrid.ActualHeight : ActualHeight);

            // Keep 16:9 while maximizing visible area with safe margins.
            double maxWidth = Math.Max(320, overlayWidth - 64);
            double maxHeight = Math.Max(180, overlayHeight - 64);

            double width = maxWidth;
            double height = width * 9.0 / 16.0;
            if (height > maxHeight)
            {
                height = maxHeight;
                width = height * 16.0 / 9.0;
            }

            TrailerContent.Width = width;
            TrailerContent.Height = height;
            TrailerContent.UpdateLayout();

            // Position close button at top-right of the overlay
            if (CloseTrailerButton != null)
            {
                CloseTrailerButton.Margin = new Thickness(16, 16, 16, 0);
            }

            var visual = ElementCompositionPreview.GetElementVisual(TrailerContent);
            visual.StopAnimation("Offset");
            visual.Offset = Vector3.Zero;
            float centerX = (float)(TrailerContent.ActualWidth > 0 ? TrailerContent.ActualWidth / 2.0 : width / 2.0);
            float centerY = (float)(TrailerContent.ActualHeight > 0 ? TrailerContent.ActualHeight / 2.0 : height / 2.0);
            visual.CenterPoint = new Vector3(centerX, centerY, 0);
        }



        private void SyncWideHeights()
        {
            if (LayoutRoot == null || ContentGrid == null) return;
            
            double targetHeight = LayoutRoot.ActualHeight;
            if (targetHeight <= 0) targetHeight = (App.MainWindow.Content as FrameworkElement)?.ActualHeight ?? 800;

            ContentGrid.Height = targetHeight;
            ContentGrid.MaxHeight = targetHeight;

            if (EpisodesPanel != null)
            {
                double margin = 100; // Account for page padding
                double maxPanelHeight = targetHeight - margin;
                
                // CRITICAL: Unset fixed height and use MaxHeight
                EpisodesPanel.Height = double.NaN; 
                EpisodesPanel.MaxHeight = maxPanelHeight;
                EpisodesPanel.VerticalAlignment = VerticalAlignment.Center;

                if (EpisodesListView != null)
                {
                    // The list should scroll if it hits the panel's limit
                    EpisodesListView.MaxHeight = maxPanelHeight - 100; 
                }
            }

            if (SourcesPanel != null)
            {
                double margin = 100;
                double maxPanelHeight = targetHeight - margin;

                SourcesPanel.Height = double.NaN;
                SourcesPanel.MaxHeight = maxPanelHeight;
                SourcesPanel.VerticalAlignment = VerticalAlignment.Center;

                if (SourcesListView != null)
                {
                    SourcesListView.MaxHeight = maxPanelHeight - 100;
                }
            }
            System.Diagnostics.Debug.WriteLine($"[LayoutDebug] SyncWideHeights: Grid={targetHeight}, Panel={EpisodesPanel?.Height}, ListMax={EpisodesListView?.MaxHeight}");
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
                if (NarrowSourcesSection != null) NarrowSourcesSection.Visibility = Visibility.Collapsed;
                if (EpisodesPanel != null) EpisodesPanel.Visibility = Visibility.Collapsed;
                if (NarrowEpisodesSection != null) NarrowEpisodesSection.Visibility = Visibility.Collapsed;
                
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
            
            // Compare by ID
            string id1 = item1.IMDbId ?? item1.Id.ToString();
            string id2 = item2.IMDbId ?? item2.Id.ToString();
            
            return id1 == id2;
        }

        private void UpdateLayoutState(bool isWide)
        {
            try
            {
                if (_item == null) return; // Data not loaded yet

                bool isSeries = false;
                if (_item is SeriesStream)
                {
                    isSeries = true;
                }
                else if (_item is StremioMediaStream sms && (sms.Meta.Type == "series" || sms.Meta.Type == "tv"))
                {
                    isSeries = true;
                }

                System.Diagnostics.Debug.WriteLine($"[LayoutDebug] UpdateLayoutState START. Wide: {isWide}, Series: {isSeries}, ItemType: {_item?.GetType().Name}, MetaType: {(_item as StremioMediaStream)?.Meta?.Type ?? "N/A"}");

                if (isWide)
                {
                    // WIDE MODE - AGGRESSIVE LOCK
                    if (RootScrollViewer != null)
                    {
                        RootScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
                        RootScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        RootScrollViewer.IsVerticalScrollChainingEnabled = false;
                    }

                    SyncWideHeights();

                    if (isSeries)
                    {
                        if (EpisodesPanel != null) EpisodesPanel.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        if (EpisodesPanel != null) EpisodesPanel.Visibility = Visibility.Collapsed;
                    }
                    
                    // Handle sources visibility in Wide mode
                    if (_areSourcesVisible)
                    {
                        // Sources are active
                        if (SourcesPanel != null) SourcesPanel.Visibility = Visibility.Visible;
                        if (NarrowSourcesSection != null) NarrowSourcesSection.Visibility = Visibility.Collapsed;
                        if (EpisodesPanel != null) EpisodesPanel.Visibility = Visibility.Collapsed;
                    }

                    if (NarrowEpisodesSection != null) NarrowEpisodesSection.Visibility = Visibility.Collapsed;
                    if (NarrowCastSection != null) NarrowCastSection.Visibility = Visibility.Collapsed;
                    if (CastSection != null) CastSection.Visibility = (CastList != null && CastList.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    // NARROW MODE
                    if (RootScrollViewer != null)
                    {
                        RootScrollViewer.VerticalScrollMode = ScrollMode.Auto;
                        RootScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                        RootScrollViewer.IsVerticalScrollChainingEnabled = true;
                    }

                    if (ContentGrid != null) 
                    {
                        ContentGrid.Height = double.NaN;
                        ContentGrid.MaxHeight = double.PositiveInfinity;
                    }

                    if (EpisodesPanel != null)
                    {
                        EpisodesPanel.Height = double.NaN;
                        EpisodesPanel.Visibility = Visibility.Collapsed;
                        if (EpisodesListView != null) EpisodesListView.MaxHeight = double.PositiveInfinity;
                    }
                    
                    if (isSeries)
                    {
                        if (NarrowEpisodesSection != null) NarrowEpisodesSection.Visibility = Visibility.Visible;
                        if (NarrowCastSection != null) NarrowCastSection.Visibility = (CastList != null && CastList.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
                    }
                    else
                    {
                         if (NarrowEpisodesSection != null) NarrowEpisodesSection.Visibility = Visibility.Collapsed;
                         if (NarrowCastSection != null) NarrowCastSection.Visibility = Visibility.Collapsed;
                    }

                    // Handle sources visibility in Narrow mode
                    if (_areSourcesVisible)
                    {
                        if (NarrowSourcesSection != null) NarrowSourcesSection.Visibility = Visibility.Visible;
                        if (SourcesPanel != null) SourcesPanel.Visibility = Visibility.Collapsed;
                        if (NarrowEpisodesSection != null) NarrowEpisodesSection.Visibility = Visibility.Collapsed;
                    }

                    if (CastSection != null) CastSection.Visibility = Visibility.Collapsed;
                }
                
                // Final Check after a short delay to allow layout to settle
                _ = Task.Delay(500).ContinueWith(_ => {
                    DispatcherQueue.TryEnqueue(() => {
                        if (RootScrollViewer != null)
                            System.Diagnostics.Debug.WriteLine($"[LayoutDebug] FINAL CHECK: Viewport={RootScrollViewer.ViewportHeight}, Extent={RootScrollViewer.ExtentHeight}, Scrollable={RootScrollViewer.ScrollableHeight}");
                    });
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LayoutDebug] ERROR in UpdateLayoutState: {ex}");
            }
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            StopBackgroundSlideshow();
            
            // CLEANUP ON EXIT: Ensure the cached page is blank for the next movie
            if (e.NavigationMode != NavigationMode.Back)
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
                if (TrailerOverlay?.Visibility == Visibility.Visible)
                {
                    CloseTrailer();
                }
            } catch {}

            // Detach player - but only cleanup if NOT handing off to PlayerPage
            if (MediaInfoPlayer != null && PlayerHost != null)
            {
                 // If we're handing off to PlayerPage, don't cleanup - let PlayerPage use it
                 if (App.HandoffPlayer != null || _isHandoffInProgress)
                 {
                     // Just detach from visual tree, don't destroy
                     PlayerHost.Content = null;
                     System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Detached player for handoff (not cleaning up).");
                 }
                 else
                 {
                     // No handoff - safe to cleanup
                     try
                     {
                         System.Diagnostics.Debug.WriteLine("[MediaInfoPage] CLEANUP: Destroying MpvPlayer instance on exit.");
                         PlayerHost.Content = null;
                         await MediaInfoPlayer.CleanupAsync(); 
                     }
                     catch (Exception ex)
                     {
                         System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] CleanupAsync Error: {ex.Message}");
                     }
                     finally
                     {
                         MediaInfoPlayer = null;
                         System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Cleaned up MpvPlayer on exit.");
                     }
                 }
            }
            else 
            {
                 System.Diagnostics.Debug.WriteLine("[MediaInfoPage] OnNavigatedFrom: No player to clean up.");
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            try
            {
                base.OnNavigatedTo(e);

                // IMMEDIATE CLEANUP - Clear all UI state BEFORE rendering to avoid showing stale data
                // This runs BEFORE any async operations so cached page doesn't show old content
                System.Diagnostics.Debug.WriteLine("[MediaInfoPage] IMMEDIATE CLEANUP - Clearing cached UI state");
                _addonResults?.Clear();
                SourcesPanel.Visibility = Visibility.Collapsed;
                NarrowSourcesSection.Visibility = Visibility.Collapsed;
                EpisodesPanel.Visibility = Visibility.Collapsed;
                NarrowEpisodesSection.Visibility = Visibility.Collapsed;
                _currentStremioVideoId = null;
                _areSourcesVisible = false;
                _shouldAutoResume = false; // Reset auto-resume flag
                if (SourcesListView != null) SourcesListView.ItemsSource = null;
                if (NarrowSourcesListView != null) NarrowSourcesListView.ItemsSource = null;
                if (AddonSelectorList != null) AddonSelectorList.ItemsSource = null;
                if (NarrowAddonSelector != null) NarrowAddonSelector.ItemsSource = null;
                
                // Debug: log current item state
                string cachedItemId = _item?.IMDbId ?? _item?.Id.ToString();
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Current cached _item ID: {cachedItemId}");

                // STALE CONTENT FIX: Stop animations and clear images immediately
                StopBackgroundSlideshow();
                StopKenBurnsEffect();
                if (HeroImage != null) HeroImage.Source = null;
                if (HeroImage2 != null) HeroImage2.Source = null;

                bool isBackNav = e.NavigationMode == NavigationMode.Back;

                // Force layout update on each navigation (fixes cached page state issues)
                _isWideModeIndex = -1;
                if (ActualWidth > 0)
                {
                    bool isWide = ActualWidth >= 900;
                    _isWideModeIndex = isWide ? 1 : 0;
                    UpdateLayoutState(isWide);
                }
                
                // SEEDING: Immediately set the image for ConnectedAnimation
                if (e.Parameter is MediaNavigationArgs args)
                {
                    _item = args.Stream;
                    // [FIX] Ignore auto-resume if we are navigating BACK from the player to prevent infinite loop
                    _shouldAutoResume = args.AutoResume && e.NavigationMode != NavigationMode.Back;
                    System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] OnNavigatedTo: AutoResume={_shouldAutoResume} (NavMode={e.NavigationMode})");

                    // --- CINEMATIC SEEDING (CRITICAL FOR ASPECT RATIO) ---
                    // Priority 1: Use Backdrop from Args (if passed)
                    // Priority 2: Use Banner/Background from Stream
                    // Priority 3: Fallback to Poster
                    string seedUrl = null;
                    
                    if (args.Stream is StremioMediaStream sms && !string.IsNullOrEmpty(sms.Meta.Background))
                        seedUrl = sms.Meta.Background;
                    else if (args.TmdbInfo != null && !string.IsNullOrEmpty(args.TmdbInfo.BackdropPath))
                        seedUrl = args.TmdbInfo.FullBackdropUrl;
                    else if (!string.IsNullOrEmpty(args.Stream?.PosterUrl))
                        seedUrl = args.Stream.PosterUrl;

                    if (!string.IsNullOrEmpty(seedUrl))
                    {
                        HeroImage.Source = new BitmapImage(new Uri(seedUrl));
                        HeroImage.Opacity = 1;
                    }

                    // Prepare Parallax & Effects
                    SetupParallax();
                }
                else if (e.Parameter is IMediaStream streamParam)
                {
                    if (!string.IsNullOrEmpty(streamParam.PosterUrl))
                    {
                        HeroImage.Source = new BitmapImage(new Uri(streamParam.PosterUrl));
                        HeroImage.Opacity = 1;
                    }
                }

                if (!isBackNav)
                {
                    // Start animation immediately while loading metadata
                    StartHeroConnectedAnimation();
                }

                SetupParallax();
                _isHandoffInProgress = false;

                // RE-ATTACH: If the player was handed off, it might be detached from its original host.
                if (App.HandoffPlayer != null)
                {
                    // Use the player returned from PlayerPage
                    MediaInfoPlayer = App.HandoffPlayer;
                    App.HandoffPlayer = null;
                    if (PlayerHost != null)
                    {
                        PlayerHost.Content = MediaInfoPlayer;
                        MediaInfoPlayer.Visibility = Visibility.Visible;
                        MediaInfoPlayer.Opacity = 1;
                        System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Re-attached returned HandoffPlayer.");
                    }
                }
                else if (MediaInfoPlayer == null && PlayerHost != null)
                {
                     // Only create new player if no handoff player exists
                     // Skip this for now - let user manually play again
                     System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Skipping player recreation (user can play again).");
                }
                else if (MediaInfoPlayer != null && MediaInfoPlayer.Parent == null && PlayerHost != null)
                {
                    PlayerHost.Content = MediaInfoPlayer;
                    System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Re-attached player to original host.");
                }

                // Load History
                await HistoryManager.Instance.InitializeAsync();
                
                await CloseTrailer(); // Ensure trailer is closed on navigation

                // Force restore UI visibility in case cached page has hidden elements
                RestoreUIVisibility();

                // Determine the new item from navigation parameters
                IMediaStream newItem = null;
                if (e.Parameter is MediaNavigationArgs navArgs)
                    newItem = navArgs.Stream;
                else if (e.Parameter is IMediaStream mediaStream)
                    newItem = mediaStream;

                // Check if this is a NEW item (different from cached _item)
                string newItemId = newItem?.IMDbId ?? newItem?.Id.ToString();
                string currentItemId = _item?.IMDbId ?? _item?.Id.ToString();
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Navigation check: _item={currentItemId}, newItem={newItemId}");
                
                // Always do full load - the minimal refresh path doesn't load UI content properly
                // This simplifies the logic and ensures consistent behavior
                bool isNewItem = true;

                if (isNewItem)
                {
                    // NEW ITEM - do full load
                    System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] New item detected: {newItem?.Title}");
                    _item = newItem;
                    await LoadDetailsAsync(newItem);
                }
                else if (_item != null)
                {
                    // Same item - just refresh state
                    System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Refreshing UI state for existing item");
                    
                    if (_item is Models.Stremio.StremioMediaStream stremioItem)
                    {
                        if (stremioItem.Meta.Type == "movie")
                        {
                            var history = HistoryManager.Instance.GetProgress(stremioItem.Meta.Id);
                            if (history != null && !history.IsFinished && history.Position > 0)
                            {
                                PlayButtonText.Text = "Devam Et";
                                var remaining = TimeSpan.FromSeconds(history.Duration - history.Position);
                                PlayButtonSubtext.Text = remaining.TotalHours >= 1 
                                    ? $"{remaining.Hours}sa {remaining.Minutes}dk Kaldı"
                                    : $"{remaining.Minutes}dk Kaldı";
                                PlayButtonSubtext.Visibility = Visibility.Visible;
                                RestartButton.Visibility = Visibility.Visible;
                                
                                _streamUrl = history.StreamUrl;
            // FAST START: Start pre-buffering since we are offering "Continue"
            // [FIX] Skip pre-buffering if we are in an auto-resume flow to avoid race conditions during handoff
            if (!_shouldAutoResume)
            {
                StartPrebuffering(history.StreamUrl, history.Position);
            }
            _ = UpdateTechnicalBadgesAsync(_streamUrl);
                             }
                             else
                             {
                                 UpdateTechnicalSectionVisibility(false);
                                 PlayButtonSubtext.Visibility = Visibility.Collapsed;
                                 RestartButton.Visibility = Visibility.Collapsed;
                             }
                        }
                    }

                    // Restore Visuals
                    StartHeroConnectedAnimation();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] CRITICAL ERROR in OnNavigatedTo: {ex}");
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

        private async Task LoadDetailsAsync(IMediaStream item, TmdbMovieResult preFetchedTmdb = null)
        {
            // Clear sources panel immediately to avoid showing stale data
            _addonResults?.Clear();
            SourcesPanel.Visibility = Visibility.Collapsed;
            NarrowSourcesSection.Visibility = Visibility.Collapsed;
            _currentStremioVideoId = null;
            _stremioSourcesCache.Clear();
            _areSourcesVisible = false; // Reset sources visibility flag
            
            // Clear list views to avoid showing stale data
            if (SourcesListView != null) SourcesListView.ItemsSource = null;
            if (NarrowSourcesListView != null) NarrowSourcesListView.ItemsSource = null;
            if (AddonSelectorList != null) AddonSelectorList.ItemsSource = null;
            if (NarrowAddonSelector != null) NarrowAddonSelector.ItemsSource = null;

            // 2. Determine if we already have data
            var existingTmdb = preFetchedTmdb ?? item.TmdbInfo;
            bool hasBasicData = existingTmdb != null;
            _cachedTmdb = existingTmdb;

            // 3. Immediate UI Update (Show Shimmers)
            SetLoadingState(true); 
            
            // Setup Alive Buttons (Micro-interactions)
            SetupButtonInteractions(PlayButton, RestartButton, TrailerButton, DownloadButton, CopyLinkButton, StickyPlayButton);
            SetupMagneticEffect(PlayButton, 0.15f);
            SetupMagneticEffect(TrailerButton, 0.2f);
            SetupMagneticEffect(DownloadButton, 0.2f);
            SetupMagneticEffect(CopyLinkButton, 0.2f);
            SetupVortexEffect(BackButton, BackIconVisual);
            
            // Setup Sticky Header Scroll Logic
            SetupStickyScroller();

            // Start timer in parallel (Short delay if cached, longer if new to allow layout settlement)
            // Start timer in parallel (Short delay if cached, longer if new to allow layout settlement)
            // var aestheticDelayTask = Task.Delay(hasBasicData ? 50 : 400); // REMOVED: Unused and unnecessary blocking feeling

            // --- NEW UNIFIED METADATA FETCH ---
            string metadataId = item.IMDbId;
            string metadataType = (item is SeriesStream || (item is Models.Stremio.StremioMediaStream smsType && (smsType.Meta.Type == "series" || smsType.Meta.Type == "tv"))) ? "series" : "movie";
            
            // If it's Stremio, we might have more than just ID (e.g. name for fallback)
            _unifiedMetadata = await MetadataProvider.Instance.GetMetadataAsync(item);
            var unified = _unifiedMetadata;

            // Update UI with Unified Data
            TitleText.Text = unified.Title;
            StickyTitle.Text = unified.Title;
            
            // Show Original Title for movies if available and different
            if (!unified.IsSeries && !string.IsNullOrEmpty(unified.OriginalTitle) && 
                !string.Equals(unified.Title, unified.OriginalTitle, StringComparison.OrdinalIgnoreCase))
            {
                if (SuperTitleText != null)
                {
                    SuperTitleText.Text = unified.OriginalTitle.ToUpperInvariant();
                    SuperTitleText.Visibility = Visibility.Visible;
                }
            }
            else if (!unified.IsSeries)
            {
                // Ensure hidden for movies if no original title
                if (SuperTitleText != null) SuperTitleText.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Series starts hidden (shows series name here later when an episode is selected)
                if (SuperTitleText != null) SuperTitleText.Visibility = Visibility.Collapsed;
            }

            OverviewText.Text = !string.IsNullOrEmpty(unified.Overview) ? unified.Overview : "Açıklama mevcut değil.";
            YearText.Text = unified.Year?.Split('–')[0] ?? "";
            GenresText.Text = unified.Genres;
            
            if (unified.IsSeries)
            {
                OverviewText.MaxLines = 4;
                OverviewText.TextTrimming = TextTrimming.CharacterEllipsis;
                RuntimeText.Text = "Dizi";
            }
            else
            {
                OverviewText.MaxLines = 0;
                RuntimeText.Text = unified.Runtime;
            }

            if (!string.IsNullOrEmpty(unified.BackdropUrl))
            {
                // Only update if backdrop is different from what we seeded (poster)
                // This prevents flicker mid-animation.
                var newSource = new BitmapImage(new Uri(unified.BackdropUrl));
                if (HeroImage.Source == null || (HeroImage.Source as BitmapImage)?.UriSource?.ToString() != unified.BackdropUrl)
                {
                    HeroImage.Source = newSource;
                }
                
                if (HeroImage.Opacity < 1) HeroImage.Opacity = 1;
            }
            
            System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Unified Metadata Loaded via {unified.DataSource}");

            // Adjust shimmers
            AdjustTitleShimmer();
            this.UpdateLayout();
            AdjustOverviewShimmer(OverviewText.Text);

            // Reveal Interactive Elements
            PlayButton.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(unified.TrailerUrl)) TrailerButton.Visibility = Visibility.Visible;
            DownloadButton.Visibility = Visibility.Visible;
            CopyLinkButton.Visibility = Visibility.Visible;
            
            // Reveal Technical Sections if already probed or available
            if (_streamUrl != null) UpdateTechnicalSectionVisibility(true);

            // Initial History UI Update for Movies
            if (metadataType == "movie")
            {
                var history = HistoryManager.Instance.GetProgress(metadataId);
                UpdateMovieHistoryUi(history);
            }

            // For non-series IPTV, trigger technical probe immediately
            if (item is LiveStream live)
            {
                _streamUrl = live.StreamUrl;
                _ = UpdateTechnicalBadgesAsync(_streamUrl);
            }
            else if (item is VodStream vod && !string.IsNullOrEmpty(vod.StreamUrl))
            {
                _streamUrl = vod.StreamUrl;
                _ = UpdateTechnicalBadgesAsync(_streamUrl);
            }
    
                // Watchlist State Update
                UpdateWatchlistState();

            // Fetch Cast (Enriched by unified metadata if possible)
            try
            {
                CastList.Clear();
                if (unified.TmdbInfo != null && AppSettings.IsTmdbEnabled)
                {
                    var credits = await TmdbHelper.GetCreditsAsync(unified.TmdbInfo.Id, unified.IsSeries);
                    if (credits?.Cast != null)
                    {
                        foreach(var c in credits.Cast.Take(10))
                        {
                            CastList.Add(new CastItem { Name = c.Name, Character = c.Character, FullProfileUrl = c.FullProfileUrl });
                        }
                    }
                }
                
                // FALLBACK: Use Stremio Cast (Names only) if TMDB didn't provide any
                if (CastList.Count == 0 && unified.Cast != null && unified.Cast.Count > 0)
                {
                    foreach (var name in unified.Cast.Take(10))
                    {
                        CastList.Add(new CastItem { Name = name, Character = "", FullProfileUrl = null });
                    }
                }
                
                if (CastList.Count > 0) 
                {
                    CastSection.Visibility = Visibility.Visible;
                    if (NarrowCastSection != null) NarrowCastSection.Visibility = Visibility.Visible;
                    CastListView.ItemsSource = CastList;
                    if (NarrowCastListView != null) NarrowCastListView.ItemsSource = CastList;
                    AdjustCastShimmer(CastList.Count);
                }
                else
                {
                    CastSection.Visibility = Visibility.Collapsed;
                    if (NarrowCastSection != null) NarrowCastSection.Visibility = Visibility.Collapsed;
                    AdjustCastShimmer(0);
                }

                // --- SLIDESHOW & SOURCE INFO ---
                if (unified.BackdropUrls != null && unified.BackdropUrls.Count > 0)
                {
                    StartBackgroundSlideshow(unified.BackdropUrls);
                }
                if (SourceAttributionText != null)
                {
                    SourceAttributionText.Text = unified.MetadataSourceInfo;
                }
            }
            catch { 
                CastSection.Visibility = Visibility.Collapsed; 
                if (NarrowCastSection != null) NarrowCastSection.Visibility = Visibility.Collapsed;
                AdjustCastShimmer(0); 
            }

            // Fallback for Ambience if no slideshow/backdrops
            if (string.IsNullOrEmpty(unified.BackdropUrl))
            {
                 // Fallback if no backdrop: default to blue/theme
                 ApplyPremiumAmbience(Color.FromArgb(255, 0, 120, 215));
            }

            // Resume Logic (Unified for Movies/Live)
            if (!unified.IsSeries)
            {
                var historyId = metadataId ?? item.Title; // Fallback to title for cache key if no ID
                var history = HistoryManager.Instance.GetProgress(historyId);
                
                if (history != null && history.Position > 0 && !history.IsFinished)
                {
                    string resumeText = "Devam Et";
                    PlayButtonText.Text = resumeText;
                    StickyPlayButtonText.Text = resumeText;
                    RestartButton.Visibility = Visibility.Visible;
                    
                    _streamUrl = history.StreamUrl;
                    if (item is LiveStream || item is VodStream) 
                        StartPrebuffering(_streamUrl, history.Position);
                }
                else
                {
                    PlayButtonText.Text = metadataType == "movie" && item is Models.Stremio.StremioMediaStream ? "Kaynak Bul" : "Oynat";
                    RestartButton.Visibility = Visibility.Collapsed;

                    // IPTV/VoD: prebuffer immediately (URL is always available)
                    if (item is LiveStream liveStream && !string.IsNullOrEmpty(liveStream.StreamUrl))
                    {
                        _streamUrl = liveStream.StreamUrl;
                        StartPrebuffering(_streamUrl);
                    }
                    else if (item is VodStream vod && !string.IsNullOrEmpty(vod.StreamUrl))
                    {
                        _streamUrl = vod.StreamUrl;
                        StartPrebuffering(_streamUrl);
                    }
                }
            }

            // Series Specific Branching
            bool isWide = ActualWidth >= 900;
            if (unified.IsSeries)
            {
                UpdateLayoutState(isWide);
                await LoadSeriesDataAsync(unified);
            }
            else
            {
                UpdateLayoutState(isWide);
                
                // Auto-Fetch Sources for Stremio Movies (if not already handled by history resume)
                // We only do this if we didn't just resume from history (which sets _streamUrl)
                if (item is Models.Stremio.StremioMediaStream stremioItem && 
                    stremioItem.Meta.Type == "movie")
                {
                   // FORCE SHIMMER VISIBILITY IMMEDIATELY
                   _areSourcesVisible = true;
                   if (SourcesPanel != null) SourcesPanel.Visibility = Visibility.Visible;
                   if (SourcesInlineShimmerOverlay != null) 
                   {
                       SourcesInlineShimmerOverlay.Visibility = Visibility.Visible;
                       ElementCompositionPreview.GetElementVisual(SourcesInlineShimmerOverlay).Opacity = 1f;
                   }

                   // Trigger fetch regardless of resume state to ensure panel is populated
                   // [FIX] BUT if we are AutoResuming, DO NOT TRIGGER LAYOUT CHANGES that might clear _streamUrl
                   if (!_shouldAutoResume)
                   {
                        _ = PlayStremioContent(stremioItem.Meta.Id, showGlobalLoading: false, autoPlay: false);
                   }
                   else
                   {
                        System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Skipping auto-fetch sources due to AutoResume pending.");
                   }
                    if (string.IsNullOrEmpty(_streamUrl)) PlayButtonText.Text = "Oynat";
                }

                // AUTO-RESUME TRIGGER (Movies)
                if (_shouldAutoResume && !unified.IsSeries)
                {
                    _shouldAutoResume = false; // Reset to prevent repeated trigger
                    System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Auto-Resume triggered for movie.");
                    PlayButton_Click(null, null);
                }
            }

            // Reveal
            StaggeredRevealContent();
        }

        private void ResetPageState()
        {
            _streamUrl = null;
            _areSourcesVisible = false;

            StopBackgroundSlideshow();
            StopKenBurnsEffect();
            
            // Clear Images & Video - AGGRESSIVE
            if (HeroImage != null)
            {
                HeroImage.Source = null;
                HeroImage.Opacity = 0;
            }
            if (HeroImage2 != null)
            {
                HeroImage2.Source = null;
                HeroImage2.Opacity = 0;
            }
            
            // Clear Metadata
            if (TitleText != null) TitleText.Text = "";
            if (StickyTitle != null) StickyTitle.Text = "";
            if (OverviewText != null) OverviewText.Text = "";
            if (GenresText != null) GenresText.Text = "";
            if (YearText != null) YearText.Text = "";
            if (RuntimeText != null) RuntimeText.Text = "";
            
            // Clear Collections
            Seasons?.Clear();
            CurrentEpisodes?.Clear();
            CastList?.Clear();
            _addonResults?.Clear();

            _currentStremioVideoId = null;
            _unifiedMetadata = null;

            if (CastListView != null) CastListView.ItemsSource = null;
            if (NarrowCastListView != null) NarrowCastListView.ItemsSource = null;

            // Visibility Cleanup - Hide interactive units until data is ready
            PlayButton.Visibility = Visibility.Collapsed;
            TrailerButton.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Collapsed;
            CopyLinkButton.Visibility = Visibility.Collapsed;
            PlayButtonSubtext.Visibility = Visibility.Collapsed;
            StickyPlayButton.Visibility = Visibility.Collapsed;
            StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
            RestartButton.Visibility = Visibility.Collapsed;
            SourcesPanel.Visibility = Visibility.Collapsed;
            NarrowSourcesSection.Visibility = Visibility.Collapsed;
            EpisodesPanel.Visibility = Visibility.Collapsed;
            NarrowEpisodesSection.Visibility = Visibility.Collapsed;
            CastSection.Visibility = Visibility.Collapsed;
            
            // Badge Cleanup
            Badge4K.Visibility = Visibility.Collapsed;
            BadgeRes.Visibility = Visibility.Collapsed;
            BadgeHDR.Visibility = Visibility.Collapsed;
            BadgeSDR.Visibility = Visibility.Collapsed;
            BadgeCodecContainer.Visibility = Visibility.Collapsed;
            if (TechBadgesContent != null) TechBadgesContent.Visibility = Visibility.Collapsed;
            if (TechBadgeSection != null) TechBadgeSection.Visibility = Visibility.Collapsed;
            if (MetadataShimmer != null) MetadataShimmer.Visibility = Visibility.Collapsed;
            if (MetadataPanel != null) MetadataPanel.Margin = new Thickness(0);

            System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Page state reset complete.");
        }

        private void SetLoadingState(bool isLoading)
        {
            if (isLoading)
            {
                // Reset Opacities for Loading
                TitlePanel.Opacity = 0;
                MetadataPanel.Opacity = 0;
                ActionBarPanel.Opacity = 0;
                OverviewPanel.Opacity = 0;
                CastSection.Opacity = 0;

                // Show Shimmers & Reset Opacities
                TitleShimmer.Visibility = Visibility.Visible;
                // Note: MetadataShimmer is managed by SetBadgeLoadingState, not here
                ActionBarShimmer.Visibility = Visibility.Visible;
                OverviewShimmer.Visibility = Visibility.Visible;
                CastShimmer.Visibility = Visibility.Visible;

                ElementCompositionPreview.GetElementVisual(TitleShimmer).Opacity = 1f;
                ElementCompositionPreview.GetElementVisual(ActionBarShimmer).Opacity = 1f;
                ElementCompositionPreview.GetElementVisual(OverviewShimmer).Opacity = 1f;
                ElementCompositionPreview.GetElementVisual(CastShimmer).Opacity = 1f;

                TechBadgesContent.Visibility = Visibility.Collapsed;
                ElementCompositionPreview.GetElementVisual(TechBadgesContent).Opacity = 0f;
            }
        }

        private void StaggeredRevealContent()
        {
            // helper for cross-fade
            void AnimatePair(UIElement content, UIElement shimmer, int delay)
            {
                if (content == null || shimmer == null) return;

                // 1. Fade In Content
                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f);
                fadeIn.InsertKeyFrame(1f, 1f);
                fadeIn.Duration = TimeSpan.FromMilliseconds(400);
                fadeIn.DelayTime = TimeSpan.FromMilliseconds(delay);

                var visualContent = ElementCompositionPreview.GetElementVisual(content);
                visualContent.Opacity = 0f; // Ensure start
                visualContent.StartAnimation("Opacity", fadeIn);
                content.Opacity = 1; // logical sync

                // 2. Fade Out Shimmer
                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, 1f);
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromMilliseconds(300);
                fadeOut.DelayTime = TimeSpan.FromMilliseconds(delay); // Sync start

                var visualShimmer = ElementCompositionPreview.GetElementVisual(shimmer);
                
                // Cleanup after animation
                var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                visualShimmer.StartAnimation("Opacity", fadeOut);
                batch.Completed += (s, e) => 
                {
                    shimmer.Visibility = Visibility.Collapsed;
                };
                batch.End();
            }

            // Sequence: Metadata(Year/Runtime) -> Title -> Actions -> Overview -> Cast
            // Note: MetadataShimmer is now decoupled and handled by UpdateTechnicalBadgesAsync
            var mFade = _compositor.CreateScalarKeyFrameAnimation();
            mFade.InsertKeyFrame(0f, 0f); mFade.InsertKeyFrame(1f, 1f);
            mFade.Duration = TimeSpan.FromMilliseconds(400);
            ElementCompositionPreview.GetElementVisual(MetadataPanel).StartAnimation("Opacity", mFade);
            MetadataPanel.Opacity = 1;

            AnimatePair(TitlePanel, TitleShimmer, 50);
            AnimatePair(ActionBarPanel, ActionBarShimmer, 100);
            AnimatePair(OverviewPanel, OverviewShimmer, 150);

            // Sync badge section alignment with current badge state
            UpdateTechnicalSectionVisibility(HasVisibleBadges());

            if (CastSection.Visibility == Visibility.Visible)
            {
                AnimatePair(CastSection, CastShimmer, 200);
            }
            else
            {
                CastShimmer.Visibility = Visibility.Collapsed;
            }
        }
        
        private void AdjustTitleShimmer()
        {
            if (TitleShimmer == null || TitlePanel == null) return;
            
            // If SuperTitle is visible, it adds approx 12-16px of height.
            // We want the Shimmer to take the EXACT height of the Panel to keep layout stable.
            double h = TitlePanel.ActualHeight;
            if (h > 0)
            {
                TitleShimmer.Height = h;
                
                // If it's a multi-line title or has supertitle, align shimmer box correctly
                // TitleShimmer is a single box in XAML, let's keep it that way but match height.
            }
            else
            {
                // Fallback for Title only
                TitleShimmer.Height = (SuperTitleText.Visibility == Visibility.Visible) ? 72 : 56;
            }
        }

        private void AdjustOverviewShimmer(string text)
        {
            if (OverviewShimmer == null || OverviewText == null) return;
            
            // Critical: Match XAML OverviewPanel structure perfectly
            OverviewShimmer.Spacing = 0; 
            OverviewShimmer.Children.Clear();
            
            // 1. Genres Shim (Matches GenresText height 22px + 4px Bottom Margin)
            double genresH = GenresText?.ActualHeight ?? 22;
            if (genresH <= 0) genresH = 22;

            OverviewShimmer.Children.Add(new ShimmerControl 
            { 
                Width = 220, Height = (float)genresH, CornerRadius = new CornerRadius(4), 
                HorizontalAlignment = HorizontalAlignment.Left,
                // Margin 0 because the next item (first line) will have Top Margin of 4.
                // Total Gap = 0 + 4 = 4px. Matches GenresText (Margin-Bottom: 4) + OverviewText (Margin-Top: 0).
                Margin = new Thickness(0, 0, 0, 0)
            });

            if (string.IsNullOrWhiteSpace(text))
            {
                OverviewShimmer.Visibility = Visibility.Collapsed;
                return;
            }

            // 2. Determine Overview Line Count
            double h = OverviewText.ActualHeight;
            int lines;

            if (h > 0)
            {
                // LineHeight forced to 24px in LoadDetailsAsync
                // We use Ceiling here to ensure we cover the full height, 
                // but since we forced 24px, it should be an integer multiple or very close.
                // h might be slightly larger due to font rendering variance, so Round is safer if we trust the sync.
                lines = (int)Math.Round(h / 24.0);
            }
            else
            {
                // Heuristic fallback: Use more conservative pixels-per-char for Segoe UI 16pt (~11.5px)
                double availableWidth = this.ActualWidth > 0 ? this.ActualWidth : 1200;
                double infoWidth = (availableWidth > 900) ? (availableWidth - 570) : (availableWidth - 40);
                infoWidth = Math.Max(300, infoWidth);
                
                // average chars per line = infoWidth / 11.5
                lines = (int)Math.Ceiling(text.Length / (infoWidth / 11.5));
            }
            
            lines = Math.Clamp(lines, 1, 6);

            // 3. Rebuild Shimmer Lines
            for (int i = 0; i < lines; i++)
            {
                double width = 650; 
                if (i == lines - 1 && lines > 1) width = 450; 

                // Each line is Height 16 + Margins 4,4 = 24px (Matches LineHeight 24)
                OverviewShimmer.Children.Add(new ShimmerControl 
                { 
                    Height = 16, 
                    Width = width, 
                    CornerRadius = new CornerRadius(4), 
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 4, 0, 4) 
                });
            }
            
            OverviewShimmer.Visibility = Visibility.Visible;
        }

        private void AdjustCastShimmer(int count)
        {
            if (CastShimmer == null) return;
            
            if (count <= 0)
            {
                CastShimmer.Visibility = Visibility.Collapsed;
                return;
            }

            CastShimmer.Visibility = Visibility.Visible;
            
            // The first child is the "Oyuncular" text header shimmer
            // We want to keep that, and specificially rebuild the HORIZONTAL stack panel (index 1)
            if (CastShimmer.Children.Count >= 2 && CastShimmer.Children[1] is StackPanel horizontalPanel)
            {
                horizontalPanel.Children.Clear();
                
                // Limit to 5 placeholders max (screen width)
                int displayCount = Math.Min(count, 5); 

                for (int i = 0; i < displayCount; i++)
                {
                    var itemStack = new StackPanel { Spacing = 8 };
                    itemStack.Children.Add(new ShimmerControl { Width = 110, Height = 140, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left });
                    itemStack.Children.Add(new ShimmerControl { Width = 110, Height = 15, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left });
                    
                    horizontalPanel.Children.Add(itemStack);
                }
            }
        }




        private void StartHeroConnectedAnimation()
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                try
                {
                    var localBgVisual = ElementCompositionPreview.GetElementVisual(LocalInfoGradient);
                    if (localBgVisual != null)
                    {
                        var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                        fadeIn.InsertKeyFrame(1f, 1f);
                        fadeIn.Duration = TimeSpan.FromSeconds(1);
                        localBgVisual.StartAnimation("Opacity", fadeIn);
                    }

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

            var visual = ElementCompositionPreview.GetElementVisual(element);
            element.Opacity = 1; 
            
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
            _backdropUrls = null;
        }

        private async void HeroImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is Image img)
            {
                await ExtractAndApplyAmbienceAsync(img);
            }
        }

        private async Task ExtractAndApplyAmbienceAsync(Image sourceImage = null)
        {
            try
            {
                var img = sourceImage ?? HeroImage;
                if (img == null) return;

                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(img);
                var pixelBuffer = await rtb.GetPixelsAsync();
                var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixelBuffer);

                var colors = ImageHelper.ExtractColorsFromPixels(pixels, rtb.PixelWidth, rtb.PixelHeight, (_item?.PosterUrl ?? "hero"));
                ApplyPremiumAmbience(colors.Primary);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Hero color extraction failed: {ex.Message}");
                // Fallback to default blue/theme
                ApplyPremiumAmbience(Color.FromArgb(255, 0, 120, 215));
            }
        }

        private void ApplyPremiumAmbience(Color primary)
        {
            // Use provided color directly
            if (true) // Execute always
            {
                

                // 2. Adaptive Glass Tint (Episodes Panel)
                if (EpisodesPanel.Background is SolidColorBrush solid)
                {
                    // Mix black with primary color
                    var mixed = Color.FromArgb(180, (byte)(primary.R * 0.2), (byte)(primary.G * 0.2), (byte)(primary.B * 0.2));
                    EpisodesPanel.Background = new SolidColorBrush(mixed);
                }

                // 3. Adaptive Buttons (Frosted Matte Tint - Subtle)
                // Use a very low opacity for the "Glass" feel (50/255)
                var btnTint = Color.FromArgb(50, primary.R, primary.G, primary.B);
                var tintBrush = new SolidColorBrush(btnTint);
                
                TrailerButton.Background = tintBrush;
                DownloadButton.Background = tintBrush;
                CopyLinkButton.Background = tintBrush;
                RestartButton.Background = tintBrush;
                _themeTintBrush = tintBrush;
                
                // Sync WatchlistButton if not in list
                UpdateWatchlistState(false);
                
                // Play Button Tint (Premium Glass-Vivid)
                // Design: Same glass base as others, but slightly higher alpha and colored border
                var playBrush = new SolidColorBrush(Color.FromArgb(90, primary.R, primary.G, primary.B));
                PlayButton.Background = playBrush;
                PlayButton.Foreground = new SolidColorBrush(Colors.White);
                
                // Details to distinguish from others: Colored/Vivid border
                PlayButton.BorderThickness = new Thickness(1.5);
                PlayButton.BorderBrush = new SolidColorBrush(Color.FromArgb(140, primary.R, primary.G, primary.B));

                // Sticky version sync
                StickyPlayButton.Background = playBrush;
                StickyPlayButton.BorderThickness = new Thickness(1);
                StickyPlayButton.BorderBrush = PlayButton.BorderBrush;
                
                // Hover is now handled by XAML Style (HoverOverlay)
                // No manual pointer events needed for color swaps here.
            }
        }

        private async Task LoadSeriesDataAsync(UnifiedMetadata unified)
        {
            try
            {
                // Show shimmer while loading
                if (EpisodesShimmerPanel != null) EpisodesShimmerPanel.Visibility = Visibility.Visible;
                if (EpisodesListView != null) EpisodesListView.Visibility = Visibility.Collapsed;
                
                // 1. Clear UI Lists
                Seasons.Clear();
                CurrentEpisodes.Clear();

                if (unified.Seasons == null || unified.Seasons.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[SERIES] No seasons found in unified metadata.");
                    return;
                }

                // 2. Map Unified Seasons to UI SeasonItems
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
                            ReleaseDate = e.AirDate,
                            IsReleased = e.AirDate.HasValue ? e.AirDate.Value <= DateTime.Now : true,
                            StreamUrl = e.StreamUrl // For IPTV, this might be pre-filled. For Stremio, it's empty until source search.
                        };
                        epItem.RefreshHistoryState();
                        epList.Add(epItem);
                    }

                    if (epList.Count > 0)
                    {
                        Seasons.Add(new SeasonItem
                        {
                            Name = s.SeasonNumber == 0 ? "Özel Bölümler" : $"{s.SeasonNumber}. Sezon",
                            SeasonName = s.SeasonNumber == 0 ? "Özel Bölümler" : $"{s.SeasonNumber}. Sezon",
                            SeasonNumber = s.SeasonNumber,
                            Episodes = epList
                        });
                    }
                }

                // 3. Handle Resume Logic (Initial Season Selection)
                int targetSeasonIndex = 0;
                EpisodeItem episodeToSelect = null;
                var lastWatched = HistoryManager.Instance.GetLastWatchedEpisode(unified.MetadataId);
                
                if (lastWatched != null)
                {
                    // Find actual episode in unified data to check progress
                    var lastEpUnified = unified.Seasons.SelectMany(s => s.Episodes).FirstOrDefault(e => e.Id == lastWatched.Id);
                    
                    if (lastWatched.IsFinished)
                    {
                        // Logic for "Next Episode"
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
                            // No next episode, stick to last watched if nothing else
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
                        // Not finished, resume the same one
                        var foundSeason = Seasons.FirstOrDefault(s => s.SeasonNumber == lastWatched.SeasonNumber);
                        if (foundSeason != null)
                        {
                            targetSeasonIndex = Seasons.IndexOf(foundSeason);
                            episodeToSelect = foundSeason.Episodes.FirstOrDefault(e => e.Id == lastWatched.Id);
                        }
                    }
                }

                if (episodeToSelect != null)
                {
                    _pendingAutoSelectEpisode = episodeToSelect;
                }

                // Apply ItemsSource AFTER logic is computed to prevent early auto-select race condition 
                SeasonComboBox.ItemsSource = Seasons;

                if (Seasons.Count > 0)
                {
                    SeasonComboBox.SelectedIndex = targetSeasonIndex;
                }

                // 4. Refresh Progress UI (Watched checkmarks)
                if (_item is Models.Stremio.StremioMediaStream stremioItem)
                {
                    await RefreshStremioSeriesProgressAsync(stremioItem);
                }
                else if (_item is SeriesStream iptvSeries)
                {
                    await RefreshIptvSeriesProgressAsync(iptvSeries);
                }

                System.Diagnostics.Debug.WriteLine($"[SERIES] Data Loaded. Seasons: {Seasons.Count}, Provider: {unified.DataSource}");

                // --- SLIDESHOW & SOURCE INFO ---
                if (unified.BackdropUrls != null && unified.BackdropUrls.Count > 0)
                {
                    StartBackgroundSlideshow(unified.BackdropUrls);
                }
                if (SourceAttributionText != null)
                {
                    SourceAttributionText.Text = unified.MetadataSourceInfo;
                }
                
                // Hide shimmer, show actual list
                if (EpisodesShimmerPanel != null) EpisodesShimmerPanel.Visibility = Visibility.Collapsed;
                if (EpisodesListView != null) EpisodesListView.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SERIES] Error: {ex.Message}");
            }
        }



        private EpisodeItem _pendingAutoSelectEpisode;

        private void SeasonComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SeasonComboBox.SelectedItem is SeasonItem season)
            {
                CurrentEpisodes.Clear();
                foreach(var ep in season.Episodes) CurrentEpisodes.Add(ep);
                EpisodesListView.ItemsSource = CurrentEpisodes;
                if (NarrowEpisodesListView != null)
                    NarrowEpisodesListView.ItemsSource = CurrentEpisodes;

                // SEASON SYNC FIX:
                // Check if this season has TMDB data (e.g. check a sample episode for valid Runtime or non-empty Overview if it should have one)
                // or simpler: Check if we have cached TMDB season details for this season
                // The issue: "First season ok, others not".
                // If we are Stremio/IPTV, we might have basic data.
                // We want to ENRICH it if missing.
                
                if (_cachedTmdb != null && season.Episodes.Count > 0)
                {
                    // Check if the first episode of this season has "enriched" data (like DurationFormatted having 'dk' from TMDB)
                    // or just check if we have season details in cache
                    // Let's just TRY to load it if we think it's missing.
                    // A simple heuristic: If DurationFormatted is empty (and it's not a special case), try fetch.
                    
                    bool seemsMissing = season.Episodes.Any(ep => string.IsNullOrEmpty(ep.DurationFormatted));
                    
                    // Also check for virtual episodes count mismatch?
                    // If unified season (which we don't have easy access to here inside the loop var, but we know it's _unifiedMetadata)
                    // has DIFFERENT count than UI season?
                    // Or simply: If TMDB is enabled, and we haven't "enriched" this season yet, do it.
                    // "seemsMissing" is a good proxy for "Basic Stremio Data".
                    
                    if (seemsMissing)
                    {
                        // Trigger async loads
                        _ = LoadTmdbSeasonDataAsync(season.SeasonNumber);
                    }
                }
                
                if (_pendingAutoSelectEpisode != null)
                {
                    var matchingEpisode = season.Episodes.FirstOrDefault(e => e.Id == _pendingAutoSelectEpisode.Id);
                     
                    if (matchingEpisode != null)
                    {
                        _isProgrammaticSelection = true;
                        try
                        {
                            EpisodesListView.SelectedItem = matchingEpisode;
                            if (NarrowEpisodesListView != null)
                            {
                                NarrowEpisodesListView.SelectedItem = matchingEpisode;
                                NarrowEpisodesListView.ScrollIntoView(matchingEpisode);
                            }
                            EpisodesListView.ScrollIntoView(matchingEpisode);
                            _pendingAutoSelectEpisode = null;
                        }
                        catch {} // List might not be ready
                        finally
                        {
                            _isProgrammaticSelection = false;
                        }
                    }
                }
                else if (CurrentEpisodes.Count > 0)
                {
                    // Select first by default if no history
                    _isProgrammaticSelection = true;
                    try
                    {
                        EpisodesListView.SelectedItem = CurrentEpisodes[0];
                        if (NarrowEpisodesListView != null)
                            NarrowEpisodesListView.SelectedItem = CurrentEpisodes[0];
                    }
                    finally
                    {
                        _isProgrammaticSelection = false;
                    }
                }
            }
        }

        private async Task LoadTmdbSeasonDataAsync(int seasonNumber)
        {
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
                                 StreamUrl = e.StreamUrl,
                                 ReleaseDate = e.AirDate,
                                 IsReleased = (e.AirDate ?? DateTime.MinValue) <= DateTime.Now,
                                 DurationFormatted = (!string.IsNullOrEmpty(e.RuntimeFormatted)) ? e.RuntimeFormatted : ""
                             };
                             epItem.RefreshHistoryState();
                             newEpList.Add(epItem);
                         }

                         uiSeason.Episodes = newEpList;
                         
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
                                  if (toSelect != null) EpisodesListView.SelectedItem = toSelect;
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

        private void EpisodesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
             if (_isSelectionSyncing) return;
             
             if (EpisodesListView.SelectedItem is EpisodeItem ep)
             {
                 _isSelectionSyncing = true;
                 try
                 {
                     // Update IsSelected for visual binding
                     foreach (var item in CurrentEpisodes) item.IsSelected = (item == ep);

                     _selectedEpisode = ep;
                     _streamUrl = ep.StreamUrl;

                     // [Fix] Restore StreamUrl from history if missing (Stremio Series Resume)
                     var history = HistoryManager.Instance.GetProgress(ep.Id);
                     if (string.IsNullOrEmpty(_streamUrl) && history != null && !string.IsNullOrEmpty(history.StreamUrl))
                     {
                          _streamUrl = history.StreamUrl;
                          System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Restored StreamUrl from history: {_streamUrl}");
                     }
                     
                     // Sync narrow list
                     if (NarrowEpisodesListView != null)
                        NarrowEpisodesListView.SelectedItem = ep;
                     
                     // UPDATE UI FOR SELECTED EPISODE
                     if (TitleText != null) 
                     {
                         TitleText.Text = ep.Title;
                         
                         // Fix: Always show Series Name as SuperTitle when looking at an episode
                         if (SuperTitleText != null)
                         {
                             SuperTitleText.Text = _item.Title.ToUpperInvariant();
                             SuperTitleText.Visibility = Visibility.Visible;
                         }
                     }

                     // Update Overview with Episode Synopsis
                     if (OverviewText != null)
                     {
                         OverviewText.Text = !string.IsNullOrEmpty(ep.Overview) ? ep.Overview : _unifiedMetadata?.Overview ?? "";
                     }
                     
                     // Update Play Button
                     if (PlayButtonText != null)
                     {
                         if (ep.HasProgress && ep.ProgressPercent < 95)
                         {
                             PlayButtonText.Text = "Devam Et";
                             if (PlayButtonSubtext != null)
                             {
                                 PlayButtonSubtext.Visibility = Visibility.Visible;
                                 PlayButtonSubtext.Text = ep.ProgressText;
                             }
                             if (RestartButton != null) RestartButton.Visibility = Visibility.Visible;
                         }
                         else
                         {
                             PlayButtonText.Text = _item is StremioMediaStream ? "Kaynak Bul" : "Oynat";
                             if (PlayButtonSubtext != null) PlayButtonSubtext.Visibility = Visibility.Collapsed;
                             if (RestartButton != null) RestartButton.Visibility = Visibility.Collapsed;
                         }
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
                     else if (_item is Models.Stremio.StremioMediaStream && !_isProgrammaticSelection && !_isInitializingSeriesUi)
                          _ = PlayStremioContent(ep.Id, false);
                 }
                 finally
                 {
                     _isSelectionSyncing = false;
                 }
             }
        }

        private void NarrowEpisodesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSelectionSyncing) return;

            if (NarrowEpisodesListView?.SelectedItem is EpisodeItem ep)
            {
                _isSelectionSyncing = true;
                try
                {
                    // Update IsSelected for visual binding
                    foreach (var item in CurrentEpisodes) item.IsSelected = (item == ep);

                    _selectedEpisode = ep;
                    _streamUrl = ep.StreamUrl;
                    
                    // [Fix] Restore StreamUrl from history if missing (Stremio Series Resume)
                    var history = HistoryManager.Instance.GetProgress(ep.Id);
                    if (string.IsNullOrEmpty(_streamUrl) && history != null && !string.IsNullOrEmpty(history.StreamUrl))
                    {
                         _streamUrl = history.StreamUrl;
                    }
                    
                    // Sync selection with main list
                    if (EpisodesListView != null)
                         EpisodesListView.SelectedItem = ep;
                         
                    // Fix: Ensure SuperTitle is updated here too (Redundant but safe)
                    if (SuperTitleText != null)
                    {
                        SuperTitleText.Text = _item.Title.ToUpperInvariant();
                        SuperTitleText.Visibility = Visibility.Visible;
                    }
                    
                    // Update Play Button
                    if (PlayButtonText != null)
                    {
                        if (ep.HasProgress && ep.ProgressPercent < 95)
                        {
                            PlayButtonText.Text = "Devam Et";
                            if (PlayButtonSubtext != null)
                            {
                                PlayButtonSubtext.Visibility = Visibility.Visible;
                                PlayButtonSubtext.Text = ep.ProgressText;
                            }
                            if (RestartButton != null) RestartButton.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            PlayButtonText.Text = "Oynat";
                            if (PlayButtonSubtext != null) PlayButtonSubtext.Visibility = Visibility.Collapsed;
                            if (RestartButton != null) RestartButton.Visibility = Visibility.Collapsed;
                        }
                    }

                    // PREVIEW
                    StartPrebuffering(_streamUrl, history?.Position ?? 0);
                }
                finally
                {
                    _isSelectionSyncing = false;
                }
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
            ElementCompositionPreview.SetIsTranslationEnabled(btn, true);

            var props = visual.Properties;
            props.InsertVector2("TouchPoint", new Vector2(0, 0));

            // Expression: (PointerPosition - Center) * intensity
            // For simplicity and high perf, we use the TouchPoint updated in PointerMoved
            var leanExpr = _compositor.CreateExpressionAnimation("Vector2(props.TouchPoint.X * intensity, props.TouchPoint.Y * intensity)");
            leanExpr.SetReferenceParameter("props", props);
            leanExpr.SetScalarParameter("intensity", intensity);
            visual.StartAnimation("Translation.XY", leanExpr);

            btn.PointerMoved += (s, e) =>
            {
                var ptr = e.GetCurrentPoint(btn).Position;
                var cx = btn.ActualWidth / 2;
                var cy = btn.ActualHeight / 2;
                props.InsertVector2("TouchPoint", new Vector2((float)(ptr.X - cx), (float)(ptr.Y - cy)));
            };

            btn.PointerExited += (s, e) =>
            {
                var reset = _compositor.CreateVector2KeyFrameAnimation();
                reset.InsertKeyFrame(1f, new Vector2(0, 0));
                reset.Duration = TimeSpan.FromMilliseconds(400);
                visual.StartAnimation("Translation.XY", reset);
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


        
        private async Task ExtractTechInfoAsync(string overrideUrl = null)
        {
             // 0. Resolve URL
             string streamUrl = overrideUrl;
             if (string.IsNullOrEmpty(streamUrl)) streamUrl = _streamUrl;
             // Rename 'ls' to 'lsCheck' to avoid conflict with downstream 'ls'
             if (string.IsNullOrEmpty(streamUrl) && _item is LiveStream lsCheck) streamUrl = lsCheck.StreamUrl;
             
             if (string.IsNullOrEmpty(streamUrl))
             {
                 // No URL available to probe (yet) -> Stop Shimmer
                 DispatcherQueue.TryEnqueue(() => SetBadgeLoadingState(false));
                 return;
             }

             if (!string.IsNullOrEmpty(streamUrl))
             {
                 // Fix Method Name: GetProbeData -> Get
                 var cachedProbe = Services.ProbeCacheService.Instance.Get(streamUrl);
                 if (cachedProbe != null)
                 {
                     System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Badge Cache HIT for {streamUrl}");
                     ApplyMetadataToUi(cachedProbe);
                     // Success - Ensure shimmer off
                     DispatcherQueue.TryEnqueue(() => SetBadgeLoadingState(false));
                     return;
                 }
             }


            // 1. Check if we already have metadata from ExpandedPage (or previous probe)
             if (_item is LiveStream live && live.HasMetadata)
             {
                 // ... (Keep existing logic for LiveStream metadata)
                 // Resolution
                 if (!string.IsNullOrEmpty(live.Resolution) && live.Resolution.Contains("x"))
                 {
                     var parts = live.Resolution.Split('x');
                     if (parts.Length == 2 && int.TryParse(parts[0], out int w))
                     {
                          if (w >= 3800) Badge4K.Visibility = Visibility.Visible;
                          else Badge4K.Visibility = Visibility.Collapsed;
                     }
                 }
                 // ...
                 return;
             }
             
              // 2. Check Cache before Probing (Prevent Duplicate Probes)
               // 2. Check Cache before Probing (Prevent Duplicate Probes)
               await Services.ProbeCacheService.Instance.EnsureLoadedAsync(); // Fix Race Condition
               if (Services.ProbeCacheService.Instance.Get(_streamUrl) is Services.ProbeData cached)
               {
                   Services.CacheLogger.Success(Services.CacheLogger.Category.MediaInfo, "Pre-buffer Probe Cache Hit", _streamUrl);
                   // Apply Cached Result
                   ApplyMetadataToUi(cached); 
                   return;
               }
             
             // 3. Perform Probe if Cache Miss
            // Use FFmpegProber for faster metadata extraction
            try
            {
                var prober = new FFmpegProber();
                var result = await prober.ProbeAsync(_streamUrl);

                if (result.Success)
                {
                    // Cache the result for UI usage

                    Services.ProbeCacheService.Instance.Update(_streamUrl, result.Res, result.Fps, result.Codec, result.Bitrate, result.IsHdr);

                    // Update the model so we don't probe again next time
                    if (_item is LiveStream ls)
                    {
                         ls.Resolution = result.Res;
                         ls.Codec = result.Codec;
                         ls.Bitrate = result.Bitrate;
                         ls.IsHdr = result.IsHdr;
                         ls.IsOnline = result.Success;
                    }
                    
                    // Resolution
                    if (result.Res.Contains("x"))
                    {
                        var parts = result.Res.Split('x');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int w))
                        {
                             if (w >= 3800) Badge4K.Visibility = Visibility.Visible;
                             else Badge4K.Visibility = Visibility.Collapsed;
                        }
                    }


                    // Codec
                    if (!string.IsNullOrEmpty(result.Codec) && result.Codec != "-") 
                    {
                        BadgeCodec.Text = result.Codec.ToUpper();
                    }

                    // HDR
                    if (result.IsHdr) 
                    {
                        BadgeHDR.Visibility = Visibility.Visible;
                        BadgeSDR.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        BadgeSDR.Visibility = Visibility.Visible;
                        BadgeHDR.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch { } // Fail silently, fallback to name below
            
            try
            {
                // Fallback: Parse from Title (always run as safety net or if probe missed specific tags)
                if (_item != null || _selectedEpisode != null)
                {
                    string name = (_selectedEpisode?.Title ?? _item?.Title ?? "").ToUpperInvariant();
                    
                    // 4K
                    if (Badge4K.Visibility == Visibility.Collapsed && (name.Contains("4K") || name.Contains("UHD"))) 
                        Badge4K.Visibility = Visibility.Visible;
                    
                    // HDR
                    if (name.Contains("HDR") || name.Contains("DOLBY") || name.Contains("DV")) 
                    {
                        BadgeHDR.Visibility = Visibility.Visible;
                        BadgeSDR.Visibility = Visibility.Collapsed;
                    }
                    else if (BadgeHDR.Visibility == Visibility.Collapsed && BadgeSDR.Visibility == Visibility.Collapsed)
                    {
                        // If no HDR detected (Name or Prober), and we have ANY quality/codec info, assume SDR.
                         bool hasVideoInfo = Badge4K.Visibility == Visibility.Visible || 
                                           !string.IsNullOrEmpty(BadgeCodec.Text) ||
                                           name.Contains("1080") || name.Contains("720") || name.Contains("FHD") || name.Contains("HD");

                         if (hasVideoInfo)
                         {
                             BadgeSDR.Visibility = Visibility.Visible;
                         }
                    }
                        
                    // Codec Fallback
                    if (BadgeCodec.Text == "HEVC" || BadgeCodec.Text == "AVC") { /* already set */ }
                    else
                    {
                        if (name.Contains("HEVC") || name.Contains("H.265") || name.Contains("X265")) 
                            BadgeCodec.Text = "HEVC";
                        else if (name.Contains("AVC") || name.Contains("H.264")) 
                            BadgeCodec.Text = "AVC";
                    }
                }
            }
            catch { }
            
            // Critical: Ensure Shimmer stops in all cases (Probe success, fail, or fallback)
            DispatcherQueue.TryEnqueue(() => SetBadgeLoadingState(false));
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
            _isTrailerFullscreen = false;
            
            // First apply layout to get correct measurements
            ApplyTrailerFullscreenLayout(enable: false);
            TrailerOverlay.UpdateLayout();
            TrailerContent.UpdateLayout();
            EnsureTrailerOverlayBounds();
            
            // CRITICAL: Reset visual state completely before animation
            if (TrailerContent != null)
            {
                var contentVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(TrailerContent);
                
                // Stop ALL animations and reset completely
                contentVisual.StopAnimation("Scale");
                contentVisual.StopAnimation("Offset");
                contentVisual.StopAnimation("Opacity");
                
                // Reset Scale and Offset to center
                contentVisual.Scale = new System.Numerics.Vector3(0.1f, 0.1f, 1f);
                contentVisual.Offset = System.Numerics.Vector3.Zero;
                
                // Use explicit Width/Height for center point calculation
                double centerX = TrailerContent.Width > 0 ? TrailerContent.Width / 2 : TrailerDefaultWidth / 2;
                double centerY = TrailerContent.Height > 0 ? TrailerContent.Height / 2 : TrailerDefaultHeight / 2;
                contentVisual.CenterPoint = new System.Numerics.Vector3((float)centerX, (float)centerY, 0);
                
                System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Visual reset - CenterPoint: {centerX}, {centerY}, Scale: 0.1, Offset: 0");
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
                
                // Ensure center point is correct
                double centerX = TrailerContent.Width > 0 ? TrailerContent.Width / 2 : TrailerDefaultWidth / 2;
                double centerY = TrailerContent.Height > 0 ? TrailerContent.Height / 2 : TrailerDefaultHeight / 2;
                visual.CenterPoint = new System.Numerics.Vector3((float)centerX, (float)centerY, 0);
                
                // Reset Offset to ensure it starts from center
                visual.Offset = System.Numerics.Vector3.Zero;
                
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
                await TrailerWebView.EnsureCoreWebView2Async();
                System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] CoreWebView2 Initialized.");

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
                    System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Navigation Completed. Success: {e.IsSuccess}");
                    tcs.TrySetResult(e.IsSuccess);
                }
                TrailerWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

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
                host: 'https://www.youtube-nocookie.com',
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
            if (_isTrailerWebViewInitialized && TrailerWebView.CoreWebView2 != null)
            {
                System.Diagnostics.Debug.WriteLine("[TRAILER_DEBUG] Stopping video via JS...");
                try 
                {
                    await TrailerWebView.CoreWebView2.ExecuteScriptAsync("stopVideo();");
                    // NAVIGATE to blank to ensure audio process is killed
                    TrailerWebView.CoreWebView2.Navigate("about:blank");
                    _isTrailerWebViewInitialized = false; // Checkmate.
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
                visual.Scale = new System.Numerics.Vector3(1f, 1f, 1f);
                visual.Offset = new System.Numerics.Vector3(0, 0, 0);
                
                // Use captured center point
                if (cx > 0) visual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0);
                else visual.CenterPoint = new System.Numerics.Vector3(500f, 281f, 0); // Absolute fallback
                
                System.Diagnostics.Debug.WriteLine($"[TRAILER_DEBUG] Close Complete. CenterPoint Reset: {cx},{cy}");
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

        private async void StartPrebuffering(string url, double startTime = 0)
        {
            StartPrebufferingV2(url, startTime); 
            return; 

            if (string.IsNullOrEmpty(url)) return;
            if (!AppSettings.IsPrebufferEnabled) return;

            // Don't prebuffer if we already have a valid player with this URL
            if (MediaInfoPlayer != null && MediaInfoPlayer.Parent != null)
            {
                try
                {
                    var currentPath = await MediaInfoPlayer.GetPropertyAsync("path");
                    if (!string.IsNullOrEmpty(currentPath) && 
                        (currentPath == url || currentPath.EndsWith(url) || url.EndsWith(currentPath)))
                    {
                        Debug.WriteLine($"[FastStart] Skipping - already buffering {url}");
                        return;
                    }
                }
                catch {}
            }

            Debug.WriteLine($"[FastStart] Prebuffering: {url} | Resume: {startTime}s | Buffer: {AppSettings.PrebufferSeconds}s");

            if (MediaInfoPlayer == null)
            {
                MediaInfoPlayer = new MpvWinUI.MpvPlayer();
                if (PlayerHost != null) PlayerHost.Content = MediaInfoPlayer;
            }

            try
            {
                // 1. Initialize via shared helper (HW accel, cookies, headers, network)
                await MpvSetupHelper.ConfigurePlayerAsync(MediaInfoPlayer, url, isSecondary: true);

                // 2. Override buffer settings AFTER helper (only limit by seconds, no MB limit)
                int preSeconds = AppSettings.PrebufferSeconds;
                await MediaInfoPlayer.SetPropertyAsync("demuxer-readahead-secs", preSeconds.ToString());
                await MediaInfoPlayer.SetPropertyAsync("demuxer-max-bytes", "2000MiB");
                await MediaInfoPlayer.SetPropertyAsync("demuxer-max-back-bytes", "50MiB");

                // 3. Make player host visible (required for rendering on handoff)
                if (PlayerOverlayContainer != null)
                {
                    PlayerOverlayContainer.Visibility = Visibility.Visible;
                    PlayerOverlayContainer.Opacity = 0;
                }

                // 4. Open stream first (demuxer needs the stream open to seek)
                await MediaInfoPlayer.OpenAsync(url);

                // 5. Wait for stream to be ready, then seek if needed
                if (startTime > 0)
                {
                    // Wait for seekability (max 3 seconds, 50ms intervals)
                    bool seekReady = false;
                    for (int i = 0; i < 60; i++)
                    {
                        var seekable = await MediaInfoPlayer.GetPropertyAsync("seekable");
                        if (seekable == "yes")
                        {
                            seekReady = true;
                            break;
                        }
                        await Task.Delay(50);
                    }

                    if (seekReady)
                    {
                Debug.WriteLine($"[FastStart] Seeking to {startTime}s before buffering");
                        await MediaInfoPlayer.ExecuteCommandAsync("seek", startTime.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
                        Debug.WriteLine($"[FastStart] Seek command sent for {startTime}s");
                    }
                    else
                    {
                        Debug.WriteLine($"[FastStart] Seek NOT READY after wait. Starting at 0.");
                    }
                }
                else
                {
                    Debug.WriteLine($"[FastStart] startTime is 0 or less ({startTime}). No seek needed.");
                }

                // 6. Mute + Start playback (buffer fills while muted)
                await MediaInfoPlayer.SetPropertyAsync("mute", "yes");
                await MediaInfoPlayer.SetPropertyAsync("pause", "no");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FastStart] Error: {ex.Message}");
            }
        }

        private async void StartPrebufferingV2(string url, double startTime = 0)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!AppSettings.IsPrebufferEnabled) return;

            // Cancel any previous prebuffering
            try { _prebufferCts?.Cancel(); _prebufferCts?.Dispose(); } catch {}
            _prebufferCts = new CancellationTokenSource();
            var ct = _prebufferCts.Token;

            Debug.WriteLine($"[FastStart] PrebufferingV2: {url} | Resume: {startTime}s | Buffer: {AppSettings.PrebufferSeconds}s");

            // 1. Ensure Player Instance Exists & is Attached
            bool isNew = false;
            // Only create if we don't have one
            if (MediaInfoPlayer == null)
            {
                MediaInfoPlayer = new MpvWinUI.MpvPlayer();
                isNew = true;
                if (PlayerHost != null) PlayerHost.Content = MediaInfoPlayer;
            }
            else
            {
                // If we have one but it's not appropriately attached yet
                if (PlayerHost != null && PlayerHost.Content != MediaInfoPlayer)
                {
                    // [WINUI FIX] An element can only have one parent.
                    // If it was temporarily detached or returned from handoff, we must 
                    // ensure it has no parent before setting it as Content.
                    var parent = VisualTreeHelper.GetParent(MediaInfoPlayer) as ContentControl;
                    if (parent != null)
                    {
                        parent.Content = null;
                        Debug.WriteLine("[FastStart] Detached MediaInfoPlayer from previous parent.");
                    }
                    
                    PlayerHost.Content = MediaInfoPlayer;
                    Debug.WriteLine("[FastStart] Re-attached MediaInfoPlayer to PlayerHost.");
                }
            }

            // 2. WAIT for RenderControl (Critical for Re-entry)
            if (isNew)
            {
                var tcs = new TaskCompletionSource<bool>();
                RoutedEventHandler handler = null;
                handler = (s, e) =>
                {
                    MediaInfoPlayer.Loaded -= handler;
                    tcs.TrySetResult(true);
                };
                MediaInfoPlayer.Loaded += handler;

                // Safety timeout
                var timeoutTask = Task.Delay(2000);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                if (completed == timeoutTask)
                {
                    Debug.WriteLine("[FastStart] Warning: Player Loaded event timed out. Attempting to proceed...");
                }
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                // NEW: CHECK IF ALREADY PLAYING THIS CONTENT (Reuse for seamless transition)
                string currentPath =  await MediaInfoPlayer.GetPropertyAsync("path");
                // Allow fuzzy match (http vs https, or slight var) or exact match
                if (!string.IsNullOrEmpty(currentPath) && currentPath == url)
                {
                     Debug.WriteLine($"[FastStart] Player already loaded with {url}. Reusing instance (Paused).");
                     
                     // Just ensure state is correct for "background wait"
                     await MediaInfoPlayer.SetPropertyAsync("mute", "yes");
                     await MediaInfoPlayer.SetPropertyAsync("pause", "yes"); // User requested PAUSE state
                     
                     // Ensure visibility
                     if (PlayerOverlayContainer != null)
                     {
                        PlayerOverlayContainer.Visibility = Visibility.Visible;
                        PlayerOverlayContainer.Opacity = 0; 
                     }
                     return; // SKIP RE-INITIALIZATION
                }

                // 3. Configure Player
                ct.ThrowIfCancellationRequested();
                await MpvSetupHelper.ConfigurePlayerAsync(MediaInfoPlayer, url, isSecondary: true);

                // 4. PRE-SEEK using 'start' property (Native MPV Feature)
                if (startTime > 0)
                {
                    Debug.WriteLine($"[FastStart] Setting start time to {startTime}s");
                    await MediaInfoPlayer.SetPropertyAsync("start", startTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    await MediaInfoPlayer.SetPropertyAsync("start", "0");
                }

                // 5. Override buffer settings for fast start
                int preSeconds = AppSettings.PrebufferSeconds;
                await MediaInfoPlayer.SetPropertyAsync("cache", "yes"); // Enable cache explicitly
                await MediaInfoPlayer.SetPropertyAsync("cache-pause", "yes"); // Buffer while paused
                await MediaInfoPlayer.SetPropertyAsync("demuxer-readahead-secs", preSeconds.ToString());
                await MediaInfoPlayer.SetPropertyAsync("demuxer-max-bytes", "2000MiB"); 
                await MediaInfoPlayer.SetPropertyAsync("demuxer-max-back-bytes", "5MiB");

                // [RESTORE AUDIO/SUBTITLE PREFERENCES]
                // Attempt to retrieve history item to restore track preferences.
                if (_item is Models.Stremio.StremioMediaStream sItem)
                {
                     var h = HistoryManager.Instance.GetProgress(sItem.Meta.Id);
                     // Also check for episode...
                     if (sItem.Meta.Type == "series" && _selectedEpisode != null)
                     {
                         h = HistoryManager.Instance.GetProgress(_selectedEpisode.Id);
                     }

                     if (h != null)
                     {
                         if (!string.IsNullOrEmpty(h.AudioTrackId)) await MediaInfoPlayer.SetPropertyAsync("aid", h.AudioTrackId);
                         if (!string.IsNullOrEmpty(h.SubtitleTrackId)) await MediaInfoPlayer.SetPropertyAsync("sid", h.SubtitleTrackId);
                     }
                }
                else if (!string.IsNullOrEmpty(url)) // Iptv or explicit URL
                {
                     // Try to key by URL
                     var h = HistoryManager.Instance.GetProgress(url);
                     if (h != null)
                     {
                         if (!string.IsNullOrEmpty(h.AudioTrackId)) await MediaInfoPlayer.SetPropertyAsync("aid", h.AudioTrackId);
                         if (!string.IsNullOrEmpty(h.SubtitleTrackId)) await MediaInfoPlayer.SetPropertyAsync("sid", h.SubtitleTrackId);
                     }
                }

                // 6. Make player host visible
                if (PlayerOverlayContainer != null)
                {
                    PlayerOverlayContainer.Visibility = Visibility.Visible;
                    PlayerOverlayContainer.Opacity = 0;
                }

                // 7. Open & Wait (Paused)
                // We pause immediately so it doesn't advance history, but fills buffer.
                ct.ThrowIfCancellationRequested();
                await MediaInfoPlayer.SetPropertyAsync("pause", "yes"); 
                ct.ThrowIfCancellationRequested();
                await MediaInfoPlayer.OpenAsync(url);
                ct.ThrowIfCancellationRequested();
                await MediaInfoPlayer.SetPropertyAsync("mute", "yes");
                
                // Note: Setting pause=yes BEFORE OpenAsync helps, but repeating it after ensures it sticks.
                await MediaInfoPlayer.SetPropertyAsync("pause", "yes");

                Debug.WriteLine("[FastStart] Pre-buffering started (PAUSED). Buffer filling in background...");
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
                    await PlayStremioContent(_selectedEpisode.Id, showGlobalLoading: false, autoPlay: true);
                }
                return;
            }

            if (!string.IsNullOrEmpty(_streamUrl))
            {
                // Series Episode
                if (_selectedEpisode != null)
                {
                     string parentId = _item is SeriesStream ss ? ss.SeriesId.ToString() : null;
                     await PerformHandoverAndNavigate(_streamUrl, _selectedEpisode.Title, _selectedEpisode.Id, parentId, _item.Title, _selectedEpisode.SeasonNumber, _selectedEpisode.EpisodeNumber, -1, _item.PosterUrl, "series");
                }
                else if (_item is LiveStream live)
                {
                    // Movie / Live
                    await PerformHandoverAndNavigate(_streamUrl, live.Title, live.StreamId.ToString(), null, null, 0, 0, -1, live.PosterUrl, "iptv");
                }
                else
                {
                    // Fallback
                    await PerformHandoverAndNavigate(_streamUrl, TitleText.Text);
                }
            }
        }
        
        private async Task PerformHandoverAndNavigate(string url, string title, string id = null, string parentId = null, string seriesName = null, int season = 0, int episode = 0, double startSeconds = -1, string posterUrl = null, string type = null)
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
                            var path = await MediaInfoPlayer.GetPropertyAsync("path");
                            if (!string.IsNullOrEmpty(path) && path != "N/A")
                            {
                                isPlayerActive = true;
                                App.HandoffPlayer = MediaInfoPlayer; // Valid Handoff
                            
                            Debug.WriteLine($"[MediaInfoPage:Handoff] Player State BEFORE: Pause={await MediaInfoPlayer.GetPropertyAsync("pause")}, Mute={await MediaInfoPlayer.GetPropertyAsync("mute")}");
                            
                            // APPLY MAIN BUFFER SETTINGS
                            int mainBuffer = AppSettings.BufferSeconds;
                            await MediaInfoPlayer.SetPropertyAsync("demuxer-readahead-secs", mainBuffer.ToString());
                            await MediaInfoPlayer.SetPropertyAsync("demuxer-max-bytes", "2000MiB"); 
                            
                            await MediaInfoPlayer.SetPropertyAsync("pause", "no");
                            MediaInfoPlayer.EnableHandoffMode();
                            
                            // Detach from parent
                            var parent = MediaInfoPlayer.Parent;
                            if (parent is Panel p) p.Children.Remove(MediaInfoPlayer);
                            else if (parent is ContentControl cc) cc.Content = null;
                            Debug.WriteLine("[MediaInfoPage:Handoff] Detached from visual tree.");
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
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title, id, parentId, seriesName, season, episode, startSeconds, posterUrl, type));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaInfoPage:Handoff] ERROR: {ex}");
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title, id, parentId, seriesName, season, episode, startSeconds, posterUrl, type));
            }
        }
        
        private async Task PlayStremioContent(string videoId, bool showGlobalLoading = true, bool autoPlay = false, double startSeconds = -1)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return;

            // Check if we're viewing the same video AND same item
            string currentItemId = _item is Models.Stremio.StremioMediaStream sms ? sms.Meta.Id : null;
            bool isSameItem = currentItemId != null && _currentStremioVideoId == videoId;

            if (isSameItem)
            {
                bool hasVisibleSources = _addonResults != null &&
                                         _addonResults.Any(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0);

                if (hasVisibleSources)
                {
                    ShowSourcesPanel(true);
                    if (SourcesInlineShimmerOverlay != null) SourcesInlineShimmerOverlay.Visibility = Visibility.Collapsed;
                    if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = Visibility.Collapsed;
                    return;
                }
            }
            else
            {
                // New item - clear old sources to avoid showing stale data
                _addonResults?.Clear();
                SourcesPanel.Visibility = Visibility.Collapsed;
                NarrowSourcesSection.Visibility = Visibility.Collapsed;
            }

            int requestVersion = Interlocked.Increment(ref _sourcesRequestVersion);
            try
            {
                if (showGlobalLoading) SetLoadingState(true);

                string type = (_item as Models.Stremio.StremioMediaStream).Meta.Type;
                string cacheKey = $"{type}|{videoId}";
                bool hasCachedAddons = false;
                StremioSourcesCacheEntry cacheEntry = null;

                if (_stremioSourcesCache.TryGetValue(cacheKey, out cacheEntry) &&
                    cacheEntry?.Addons != null &&
                    cacheEntry.Addons.Count > 0)
                {
                    if (requestVersion != Volatile.Read(ref _sourcesRequestVersion)) return;

                    _currentStremioVideoId = videoId;
                    _isCurrentSourcesComplete = cacheEntry.IsComplete;
                    _isSourcesFetchInProgress = !cacheEntry.IsComplete;
                    hasCachedAddons = true;

                    _addonResults = new ObservableCollection<StremioAddonViewModel>(cacheEntry.Addons.Select(CloneAddonViewModel));
                    AddonSelectorList.ItemsSource = _addonResults;
                    NarrowAddonSelector.ItemsSource = _addonResults;

                    ShowSourcesPanel(true);
                    if (SourcesInlineShimmerOverlay != null) SourcesInlineShimmerOverlay.Visibility = Visibility.Collapsed;
                    if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = Visibility.Collapsed;

                    var activeAddon = _addonResults.FirstOrDefault(a => !a.IsLoading && a.Streams != null && a.Streams.Any(s => s.IsActive));
                    var firstAddon = _addonResults.FirstOrDefault(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0);
                    
                    if (activeAddon != null)
                    {
                         AddonSelectorList.SelectedItem = activeAddon;
                    }
                    else if (firstAddon != null && AddonSelectorList.SelectedItem == null)
                    {
                        AddonSelectorList.SelectedItem = firstAddon;
                    }

                    if (autoPlay)
                    {
                        var firstStream = firstAddon?.Streams?.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));
                        {
                            var stremioMeta = (_item as Models.Stremio.StremioMediaStream)?.Meta;
                            string autoParentIdStr = stremioMeta != null && (stremioMeta.Type == "series" || stremioMeta.Type == "tv") ? stremioMeta.Id : null;
                            string autoStreamType = (stremioMeta?.Type == "series" || stremioMeta?.Type == "tv") ? "series" : "movie";
                            Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(firstStream.Url, _item.Title, videoId, autoParentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, startSeconds, _item.PosterUrl, autoStreamType));
                            return;
                        }
                    }

                    if (cacheEntry.IsComplete)
                    {
                        if (showGlobalLoading) SetLoadingState(false);
                        return;
                    }
                }

                ShowSourcesPanel(true);
                if (SourcesInlineShimmerOverlay != null) SourcesInlineShimmerOverlay.Visibility = hasCachedAddons ? Visibility.Collapsed : Visibility.Visible;
                if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = Visibility.Collapsed;
                _currentStremioVideoId = videoId;
                _isCurrentSourcesComplete = false;
                _isSourcesFetchInProgress = true;

                var addons = Services.Stremio.StremioAddonManager.Instance.GetAddons();
                var allStreams = new List<StremioStreamViewModel>();

                // Initialize ObservableCollection for Incremental Updates
                if (!hasCachedAddons || _addonResults == null)
                {
                    _addonResults = new System.Collections.ObjectModel.ObservableCollection<StremioAddonViewModel>();
                    AddonSelectorList.ItemsSource = _addonResults;
                    NarrowAddonSelector.ItemsSource = _addonResults; // Ensure Narrow selector also updated
                }
                var activeCollection = _addonResults; // Capture for safe updates
                
                // WinUI 3: Use DispatcherQueue instead of Dispatcher (which is null in Desktop apps)
                var dispatcherQueue = this.DispatcherQueue;

                // Add a single "Loading..." placeholder at the end to indicate background activity
                var loadingPlaceholder = _addonResults.FirstOrDefault(a => a.IsLoading);
                if (loadingPlaceholder == null)
                {
                    loadingPlaceholder = new StremioAddonViewModel
                    {
                        Name = "",
                        IsLoading = true,
                        SortIndex = int.MaxValue // Always at the end
                    };
                    _addonResults.Add(loadingPlaceholder);
                }

                System.Diagnostics.Debug.WriteLine($"[Stremio] Fetching sources for {videoId} ({type}) from {addons.Count} addons.");

                // Get Last Played Stream for "Active" Indication
                string lastStreamUrl = HistoryManager.Instance.GetProgress(videoId)?.StreamUrl;

                var tasks = new List<Task>();
                
                for (int i = 0; i < addons.Count; i++)
                {
                    int sortIndex = i;
                    string baseUrl = addons[i];

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            // 1. Get Manifest
                            var manifest = await Services.Stremio.StremioService.Instance.GetManifestAsync(baseUrl);
                            if (manifest == null) return;

                            // [FIX] Relaxed check: Only block if we are SURE it doesn't support streams.
                            // Default to TRUE (supported) if resources is null or complex.
                            bool handled = true;
                            
                            if (manifest.Resources != null && manifest.Resources.Count > 0)
                            {
                                // Check if it's a simple list of strings/types and "stream" is missing
                                // If it contains objects, we assume it MIGHT support streams unless we parse them all.
                                // For safety, we only filter out if we see a list of resource names and "stream" isn't one of them.
                                
                                bool hasStream = false;
                                bool isSimpleList = true;

                                foreach (var r in manifest.Resources)
                                {
                                    if (r is string s)
                                    {
                                        if (s == "stream") hasStream = true;
                                    }
                                    else if (r is JsonElement je)
                                    {
                                        if (je.ValueKind == JsonValueKind.String)
                                        {
                                            if (je.GetString() == "stream") hasStream = true;
                                        }
                                        else if (je.ValueKind == JsonValueKind.Object)
                                        {
                                            // It's a resource object (ResultResource). 
                                            // We assume complex objects might define "stream" or "name": "stream"
                                            // Checking deeper is safer:
                                            if (je.TryGetProperty("name", out var nameProp) && nameProp.GetString() == "stream")
                                            {
                                                hasStream = true;
                                            }
                                            // If it's an object but not obviously "stream", we mark it as complex/unknown
                                            // BUT strictly speaking, a resource object MUST have a name. 
                                            // If we found objects, let's just allow it to be safe unless we are sure.
                                            isSimpleList = false;
                                        }
                                    }
                                    else
                                    {
                                        isSimpleList = false; // Unknown type
                                    }
                                }

                                // If it was a simple list of strings and NONE were "stream", then we block.
                                // If it had complex objects we failed to parse perfectly, we allow.
                                if (isSimpleList && !hasStream)
                                {
                                    handled = false;
                                }
                            }

                            if (!handled)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Stremio] Skipping non-stream addon: {manifest.Name ?? baseUrl}");
                                return;
                            }

                            string addonDisplayName = manifest.Name ?? baseUrl.Replace("https://", "").Replace("http://", "").Split('/')[0];
                            addonDisplayName = NormalizeAddonText(addonDisplayName);
                            
                            // 2. Get Streams
                            var streams = await Services.Stremio.StremioService.Instance.GetStreamsAsync(new List<string> { baseUrl }, type, videoId);
                            
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

                                    // Identify Filename and Metadata parts from Description
                                    if (!string.IsNullOrEmpty(rawDesc))
                                    {
                                        var lines = rawDesc.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                                        var metaParts = new List<string>();
                                        
                                        foreach (var line in lines)
                                        {
                                            string trimmed = line.Trim();
                                            if (string.IsNullOrEmpty(trimmed)) continue;

                                            if (trimmed.StartsWith("Name:", StringComparison.OrdinalIgnoreCase) || 
                                                trimmed.StartsWith("File:", StringComparison.OrdinalIgnoreCase) ||
                                                trimmed.StartsWith("📄") ||
                                                trimmed.StartsWith("ðŸ“„") ||
                                                trimmed.StartsWith("ğŸ“„"))
                                            {
                                                displayFileName = trimmed
                                                    .Replace("Name:", "")
                                                    .Replace("File:", "")
                                                    .Replace("📄", "")
                                                    .Replace("ðŸ“„", "")
                                                    .Replace("ğŸ“„", "")
                                                    .Trim();
                                            }
                                            else
                                            {
                                                metaParts.Add(trimmed);
                                            }
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
                                    
                                    string providerLine = rawName.Split('\n')[0].Trim();
                                    string shortProvider = providerLine;
                                    string[] qualityMarkers = { "4K", "2160p", "1080p", "720p", "480p", "HDR", "DV" };
                                    foreach(var q in qualityMarkers) 
                                        shortProvider = System.Text.RegularExpressions.Regex.Replace(shortProvider, $@"\b{q}\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                                    
                                    string sizeInfo = ExtractSize(displayDescription) ?? ExtractSize(rawTitle) ?? ExtractSize(rawName);
                                    string finalDescription = displayDescription;
                                    if (!string.IsNullOrEmpty(sizeInfo) && !string.IsNullOrEmpty(finalDescription))
                                    {
                                       finalDescription = finalDescription
                                           .Replace(sizeInfo, "")
                                           .Replace("[]", "")
                                           .Replace("  •    •  ", "  •  ")
                                           .Trim(' ', '•');
                                    }
                                    
                                    bool isActive = !string.IsNullOrEmpty(lastStreamUrl) && s.Url == lastStreamUrl;
                                    
                                    // [FIX] Fuzzy match for dynamic Debrid URLs (match by filename)
                                    if (!isActive && !string.IsNullOrEmpty(lastStreamUrl))
                                    {
                                        try
                                        {
                                            string lastFileName = System.IO.Path.GetFileName(new Uri(lastStreamUrl).LocalPath);
                                            string currentFileName = System.IO.Path.GetFileName(new Uri(s.Url).LocalPath);
                                            if (!string.IsNullOrEmpty(lastFileName) && lastFileName == currentFileName)
                                            {
                                                isActive = true;
                                            }
                                        }
                                        catch { /* Ignore Uri parsing errors */ }
                                    }

                                    processedStreams.Add(new StremioStreamViewModel
                                    {
                                        Title = finalTitle,
                                        Name = finalDescription,
                                        ProviderText = rawName.Trim(),
                                        AddonName = addonDisplayName,
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
                                    var addonVM = new StremioAddonViewModel 
                                    { 
                                        Name = addonDisplayName.ToUpper(), 
                                        Streams = processedStreams,
                                        IsLoading = false,
                                        SortIndex = sortIndex
                                    };

                                    // Insert into UI Collection in correct order
                                    var tcs = new TaskCompletionSource<bool>();
                                    dispatcherQueue.TryEnqueue(() =>
                                    {
                                        try
                                        {
                                            if (requestVersion != Volatile.Read(ref _sourcesRequestVersion)) return;
                                            if (_addonResults != activeCollection) return;

                                            // Find insertion point (keep placeholder at end)
                                            int insertAt = 0;
                                            while (insertAt < _addonResults.Count && _addonResults[insertAt].SortIndex < sortIndex)
                                            {
                                                insertAt++;
                                            }

                                            var existing = _addonResults.FirstOrDefault(a => !a.IsLoading && a.SortIndex == sortIndex);
                                            if (existing != null)
                                            {
                                                existing.Name = addonVM.Name;
                                                existing.Streams = addonVM.Streams;
                                                existing.IsLoading = false;
                                            }
                                            else
                                            {
                                                _addonResults.Insert(insertAt, addonVM);
                                            }

                                            // Ensure panel is visible when we have results
                                            if (SourcesPanel != null && _addonResults.Any(a => !a.IsLoading))
                                            {
                                                SourcesPanel.Visibility = Visibility.Visible;
                                                UpdateLayoutState(ActualWidth >= 900); // Re-trigger layout logic
                                            }

                                            if (SourcesInlineShimmerOverlay != null && SourcesInlineShimmerOverlay.Visibility == Visibility.Visible)
                                            {
                                                SourcesInlineShimmerOverlay.Visibility = Visibility.Collapsed;
                                            }

                                            var partialSnapshot = _addonResults
                                                .Where(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0)
                                                .Select(CloneAddonViewModel)
                                                .ToList();
                                            if (partialSnapshot.Count > 0)
                                            {
                                                _stremioSourcesCache[cacheKey] = new StremioSourcesCacheEntry
                                                {
                                                    Addons = partialSnapshot,
                                                    IsComplete = false
                                                };
                                            }

                                            // AUTO-PLAY LOGIC: If requested, pick the very first stream from the first responding addon
                                            if (autoPlay && addonVM.Streams.Count > 0)
                                            {
                                                var firstStream = addonVM.Streams.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));
                                                if (firstStream != null)
                                                {
                                                    // Stop loading and navigate
                                                    autoPlay = false; // Prevent multiple navigations
                                                    SetLoadingState(false);
                                                    
                                                    // [FIX] Use direct navigation for new sources that haven't been pre-buffered.
                                                    // PerformHandoverAndNavigate requires the player to already be playing the content.
                                                    string parentIdStr = _item is Models.Stremio.StremioMediaStream sms && (sms.Meta.Type == "series" || sms.Meta.Type == "tv") ? sms.Meta.Id : null;
                                                    string autoStreamType = (_item is Models.Stremio.StremioMediaStream sms2 && (sms2.Meta.Type == "series" || sms2.Meta.Type == "tv")) ? "series" : "movie";
                                                    Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(firstStream.Url, _item.Title, videoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, startSeconds, _item.PosterUrl, autoStreamType));
                                                    return;
                                                }
                                            }

                                            // Select Active Addon if available
                                            var activeInUpdate = addonVM.Streams.Any(s => s.IsActive);
                                            if (activeInUpdate)
                                            {
                                                AddonSelectorList.SelectedItem = addonVM;
                                            }
                                            // If nothing selected (and not just placeholder), select this
                                            else if (AddonSelectorList.SelectedIndex == -1 || (AddonSelectorList.SelectedItem as StremioAddonViewModel)?.IsLoading == true)
                                            {
                                                AddonSelectorList.SelectedItem = addonVM;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            System.Diagnostics.Debug.WriteLine($"[Stremio] UI Error: {ex}");
                                        }
                                        finally
                                        {
                                            tcs.TrySetResult(true);
                                        }
                                    });
                                    await tcs.Task;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Stremio] Error fetching from {baseUrl}: {ex.Message}");
                        }
                    }));
                }

                await Task.WhenAll(tasks);
                
                // Final Cleanup (UI Thread)
                var tcsFinal = new TaskCompletionSource<bool>();
                dispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        if (requestVersion != Volatile.Read(ref _sourcesRequestVersion)) return;
                        if (_addonResults != activeCollection) return;

                        // Remove placeholder
                        if (_addonResults.Contains(loadingPlaceholder))
                            _addonResults.Remove(loadingPlaceholder);

                        var cacheSnapshot = _addonResults
                            .Where(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0)
                            .Select(CloneAddonViewModel)
                            .ToList();
                        if (cacheSnapshot.Count > 0)
                        {
                            _stremioSourcesCache[cacheKey] = new StremioSourcesCacheEntry
                            {
                                Addons = cacheSnapshot,
                                IsComplete = true
                            };
                        }

                        if (showGlobalLoading) SetLoadingState(false);
                        if (SourcesInlineShimmerOverlay != null) SourcesInlineShimmerOverlay.Visibility = Visibility.Collapsed;
                        if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = Visibility.Collapsed;
                        _isSourcesFetchInProgress = false;
                        _isCurrentSourcesComplete = true;

                        if (_addonResults.Count == 0)
                        {
                            var err = new ContentDialog { Title = "Kaynak Bulunamadı", Content = "Eklentilerinizde bu içerik için uygun bir kaynak bulunamadı.", CloseButtonText = "Tamam", XamlRoot = this.XamlRoot };
                            await err.ShowAsync();
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
                    if (SourcesInlineShimmerOverlay != null) SourcesInlineShimmerOverlay.Visibility = Visibility.Collapsed;
                    if (SourcesShimmerPanel != null) SourcesShimmerPanel.Visibility = Visibility.Collapsed;
                    _isSourcesFetchInProgress = false;
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

        private sealed class StremioSourcesCacheEntry
        {
            public List<StremioAddonViewModel> Addons { get; set; } = new();
            public bool IsComplete { get; set; }
        }

        private void ShowSourcesPanel(bool show)
        {
            _areSourcesVisible = show; // <--- Set Flag
            // Determine which panel to show based on width
            bool isWide = _isWideModeIndex == 1;
            System.Diagnostics.Debug.WriteLine($"[LayoutDebug] ShowSourcesPanel({show}) - IsWide: {isWide}, Item: {_item?.Title}");

            bool canGoBackToEpisodes =
                _item is SeriesStream ||
                (_item is Models.Stremio.StremioMediaStream smsType && (smsType.Meta.Type == "series" || smsType.Meta.Type == "tv"));

            if (BtnBackToEpisodes != null) BtnBackToEpisodes.Visibility = canGoBackToEpisodes ? Visibility.Visible : Visibility.Collapsed;
            if (BtnBackToEpisodesNarrow != null) BtnBackToEpisodesNarrow.Visibility = canGoBackToEpisodes ? Visibility.Visible : Visibility.Collapsed;

            if (show)
            {
                EpisodesPanel.Visibility = Visibility.Collapsed;
                NarrowEpisodesSection.Visibility = Visibility.Collapsed;
                
                if (isWide)
                {
                    SourcesPanel.Visibility = Visibility.Visible;
                    NarrowSourcesSection.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SourcesPanel.Visibility = Visibility.Collapsed;
                    NarrowSourcesSection.Visibility = Visibility.Visible;
                }
            }
            else
            {
                Interlocked.Increment(ref _sourcesRequestVersion);
                SourcesPanel.Visibility = Visibility.Collapsed;
                NarrowSourcesSection.Visibility = Visibility.Collapsed;
                if (SourcesInlineShimmerOverlay != null) SourcesInlineShimmerOverlay.Visibility = Visibility.Collapsed;
                _isSourcesFetchInProgress = false;
                
                if (_item is Models.Stremio.StremioMediaStream sms && (sms.Meta.Type == "series" || sms.Meta.Type == "tv"))
                {
                    if (isWide) EpisodesPanel.Visibility = Visibility.Visible;
                    else NarrowEpisodesSection.Visibility = Visibility.Visible;
                }
                else if (_item is SeriesStream)
                {
                    EpisodesPanel.Visibility = Visibility.Visible;
                    NarrowEpisodesSection.Visibility = Visibility.Visible;
                }
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
                NarrowSourcesListView.ItemsSource = addon.Streams;

                // Sync the other list if one changes
                if (lv == AddonSelectorList) NarrowAddonSelector.SelectedItem = addon;
                else if (lv == NarrowAddonSelector) AddonSelectorList.SelectedItem = addon;

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
                    await Task.Delay(50);
                    
                    var list = _isWideModeIndex == 1 ? SourcesListView : NarrowSourcesListView;
                    System.Diagnostics.Debug.WriteLine($"[Stremio] ScrollToActiveSource: List={list?.Name}, Items={list?.Items?.Count}");

                    if (list.ItemsSource is List<StremioStreamViewModel> streams)
                    {
                        var active = streams.FirstOrDefault(s => s.IsActive);
                        System.Diagnostics.Debug.WriteLine($"[Stremio] Active Item: {active?.Title ?? "NULL"}");
                        if (active != null)
                        {
                            list.ScrollIntoView(active);
                        }
                    }
                }
                catch { }
            });
        }

        private void SourcesListView_ItemClick(object sender, ItemClickEventArgs e)
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

                string title = _selectedEpisode?.Title ?? _item.Title;
                string videoId = _selectedEpisode?.Id ?? (_item as Models.Stremio.StremioMediaStream).Meta.Id;

                if (!string.IsNullOrEmpty(vm.Url))
                {
                    _streamUrl = vm.Url; // Save for return/reuse
                    
                    // [FIX] Check history for resume position on manual source select
                    double resumeSeconds = -1;
                    var history = HistoryManager.Instance.GetProgress(videoId);
                    if (history != null && !history.IsFinished && history.Position > 0)
                    {
                        resumeSeconds = history.Position;
                    }

                    // [FIX] Direct Navigation for Stremio Sources (No Handoff)
                    // We cannot use Handoff because MediaInfoPlayer has not pre-buffered this specific URL.
                    // Doing Handoff would pass an empty/uninitialized player to PlayerPage.
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

                    Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(vm.Url, title, videoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, resumeSeconds, _item.PosterUrl, streamType));
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

        private void BtnCloseSources_Click(object sender, RoutedEventArgs e)
        {
            ShowSourcesPanel(false);
        }

        private void BtnBackToEpisodes_Click(object sender, RoutedEventArgs e)
        {
            ShowSourcesPanel(false);
        }

        private void ShowObsidianTray(string title, List<Models.Stremio.StremioStream> streams)
        {
            // Deprecated - using SourcesPanel now
        }

        private void ObsidianTray_TrayClosed(object sender, EventArgs e)
        {
             AnimateMainContentRecede(false);
        }

        private void ObsidianTray_SourceSelected(object sender, Models.Stremio.StremioStream stream)
        {
            // Deprecated
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
                     
                     HistoryManager.Instance.UpdateProgress(_selectedEpisode.Id, _selectedEpisode.Title, _streamUrl, 0, 0, parentId, _item.Title, _selectedEpisode.SeasonNumber, _selectedEpisode.EpisodeNumber, null, null, null, _item.PosterUrl, "series");
                     PerformHandoverAndNavigate(_streamUrl, _selectedEpisode.Title, _selectedEpisode.Id, parentId, _item.Title, _selectedEpisode.SeasonNumber, _selectedEpisode.EpisodeNumber, 0);
                }
                else if (_item is LiveStream live)
                {
                    // Update History to 0
                    HistoryManager.Instance.UpdateProgress(live.StreamId.ToString(), live.Title, live.StreamUrl, 0, 0, null, null, 0, 0, null, null, null, live.PosterUrl, "iptv");
                    
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
                 var result = await dialog.ShowAsync();
                 if (result == ContentDialogResult.Primary)
                 {
                     var pkg = new DataPackage();
                     pkg.SetText(_streamUrl);
                     Clipboard.SetContent(pkg);
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
            if (MetadataShimmer == null || TechBadgesContent == null) return;

            if (isLoading)
            {
                if (TechBadgeSection != null) TechBadgeSection.Visibility = Visibility.Visible;
                MetadataShimmer.Width = double.NaN;
                MetadataShimmer.Visibility = Visibility.Visible;
                ElementCompositionPreview.GetElementVisual(MetadataShimmer).Opacity = 1f;

                TechBadgesContent.Visibility = Visibility.Collapsed;
                ElementCompositionPreview.GetElementVisual(TechBadgesContent).Opacity = 0f;
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
                    visContent.Opacity = 0f;

                    var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(0f, 0f);
                    fadeIn.InsertKeyFrame(1f, 1f);
                    fadeIn.Duration = TimeSpan.FromMilliseconds(400);
                    visContent.StartAnimation("Opacity", fadeIn);
                    TechBadgesContent.Opacity = 1;

                    if (TechBadgeSection != null) TechBadgeSection.Visibility = Visibility.Visible;
                }

                // Fade Out Shimmer
                var visShimmer = ElementCompositionPreview.GetElementVisual(MetadataShimmer);
                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, 1f);
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromMilliseconds(300);

                var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                visShimmer.StartAnimation("Opacity", fadeOut);
                batch.Completed += (s, e) =>
                {
                    MetadataShimmer.Visibility = Visibility.Collapsed;
                    MetadataShimmer.Width = double.NaN;
                    UpdateTechnicalSectionVisibility(spansSpace);
                };
                batch.End();
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
                UpdateTechnicalSectionVisibility(false);
                return;
            }

            // Cancel previous probe
            try
            {
                _probeCts?.Cancel();
                _probeCts?.Dispose(); // Dispose old CTS
            }
            catch { } // Ignore cancellation/dispose errors

            _probeCts = new CancellationTokenSource();
            var token = _probeCts.Token;

            try
            {
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
                    // SMART SHIMMER: Fast Fade-In if cached
                    Services.CacheLogger.Success(Services.CacheLogger.Category.MediaInfo, "TechBadges Cache Hit", url);

                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        // Even if cached, we do a very quick fade for smoothness (50ms), not 500ms
                        TechBadgesContent.Opacity = 0;
                        TechBadgesContent.Visibility = Visibility.Visible;
                        ApplyMetadataToUi(cached);

                        // Quick Fade In
                        var visContent = ElementCompositionPreview.GetElementVisual(TechBadgesContent);
                        var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                        fadeIn.InsertKeyFrame(0f, 0f);
                        fadeIn.InsertKeyFrame(1f, 1f);
                        fadeIn.Duration = TimeSpan.FromMilliseconds(50); // Fast!
                        visContent.StartAnimation("Opacity", fadeIn);
                        TechBadgesContent.Opacity = 1;

                        // Ensure shimmer is hidden
                        SetBadgeLoadingState(false);
                    });
                    return;
                }

                // 2. Show Shimmer (Reset state to Shimmer)
                Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "TechBadges Cache Miss - Probing...", url);
                SetBadgeLoadingState(true);

                // 3. Perform Probe
                var result = await _ffprober.ProbeAsync(url);
                
                if (token.IsCancellationRequested) 
                {
                    Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "Probe Cancelled");
                    return;
                }

                Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "Probe Result", $"Success: {result.Success} | {result.Res}");

                if (result.Success)
                {
                     Services.ProbeCacheService.Instance.Update(url, result.Res, result.Fps, result.Codec, result.Bitrate, result.IsHdr);
                }

                var probeData = new Services.ProbeData
                {
                    Resolution = result.Res,
                    Fps = result.Fps,
                    Codec = result.Codec,
                    Bitrate = result.Bitrate,
                    IsHdr = result.IsHdr
                };

                // 4. Update UI
                if (!token.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        // 1. Capture Shimmer Width for Stability (Prevent Left Shift)
                        double shimmerWidth = MetadataShimmer.ActualWidth;

                        // 2. Prepare Content
                        TechBadgesContent.Visibility = Visibility.Visible;
                        ApplyMetadataToUi(probeData);
                        
                        // 4. PREVENT LAYOUT SHIFT: 
                        bool hasVisibleBadges = HasVisibleBadges();

                        if (hasVisibleBadges && shimmerWidth > 0 && TechBadgesContent.ActualWidth < shimmerWidth)
                        {
                            TechBadgesContent.MinWidth = shimmerWidth;
                            if (TechBadgeSection != null) TechBadgeSection.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            TechBadgesContent.MinWidth = 0; // Reset
                            if (TechBadgeSection != null) TechBadgeSection.Visibility = hasVisibleBadges ? Visibility.Visible : Visibility.Collapsed;
                        }
                        
                        // Sync Shimmer to Content only if Content is WIDER (to cover it)
                        if (TechBadgesContent.ActualWidth > shimmerWidth)
                        {
                            MetadataShimmer.Width = TechBadgesContent.ActualWidth;
                        }

                        // 5. Trigger Cross-Fade Animation
                        SetBadgeLoadingState(false);
                    });
                }
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.MediaInfo, "Technical Probe Failed", ex.Message);
                // Ensure we exit loading state so text/layout doesn't stay hidden/ghosted
                DispatcherQueue.TryEnqueue(() => 
                {
                    UpdateTechnicalSectionVisibility(false);
                    SetBadgeLoadingState(false);
                });
            }
        }

        private void ApplyMetadataToUi(Services.ProbeData result)
        {
            if (result == null) return;

            // Resolution / 4K
            bool is4K = result.Resolution.Contains("3840") || result.Resolution.Contains("4096") || result.Resolution.ToUpperInvariant().Contains("4K");
            Badge4K.Visibility = is4K ? Visibility.Visible : Visibility.Collapsed;

            if (!is4K && !string.IsNullOrWhiteSpace(result.Resolution) && result.Resolution != "Unknown" && result.Resolution != "Error" && result.Resolution.Trim().Length > 0)
            {
                // Show resolution badge (e.g. 1080P)
                string displayRes = result.Resolution;
                if (displayRes.Contains("x"))
                {
                    var h = displayRes.Split('x').LastOrDefault();
                    if (h != null) displayRes = h + "P";
                }
                
                if (!string.IsNullOrWhiteSpace(displayRes))
                {
                    BadgeResText.Text = displayRes.ToUpperInvariant();
                    BadgeRes.Visibility = Visibility.Visible;
                }
                else
                {
                    BadgeRes.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                BadgeRes.Visibility = Visibility.Collapsed;
            }

            // HDR / SDR
            BadgeHDR.Visibility = result.IsHdr ? Visibility.Visible : Visibility.Collapsed;
            BadgeSDR.Visibility = !result.IsHdr ? Visibility.Visible : Visibility.Collapsed;

            // Codec
            if (!string.IsNullOrWhiteSpace(result.Codec) && result.Codec != "-" && result.Codec.Trim().Length > 0)
            {
                BadgeCodec.Text = result.Codec;
                BadgeCodecContainer.Visibility = Visibility.Visible;
            }
            else
            {
                BadgeCodecContainer.Visibility = Visibility.Collapsed;
            }

            UpdateTechnicalSectionVisibility(HasVisibleBadges());

            // Disable dynamic shimmer adjustment: We are cross-fading, not morphing.
            // AdjustMetadataShimmer();
        }

        /// <summary>
        /// Returns true if any technical badge (4K, Resolution, HDR, SDR, Codec) is currently visible.
        /// </summary>
        private bool HasVisibleBadges() =>
            Badge4K.Visibility == Visibility.Visible ||
            BadgeRes.Visibility == Visibility.Visible ||
            BadgeHDR.Visibility == Visibility.Visible ||
            BadgeSDR.Visibility == Visibility.Visible ||
            BadgeCodecContainer.Visibility == Visibility.Visible;

        /// <summary>
        /// Manages TechBadgeSection visibility and MetadataPanel margin based on badge presence.
        /// When badges are visible, adds a 16px left gap before MetadataPanel.
        /// When no badges, collapses the entire section and resets margin to 0.
        /// </summary>
        private void UpdateTechnicalSectionVisibility(bool hasBadges)
        {
            if (hasBadges)
            {
                if (TechBadgeSection != null) TechBadgeSection.Visibility = Visibility.Visible;
                if (MetadataPanel != null) MetadataPanel.Margin = new Thickness(16, 0, 0, 0);
            }
            else
            {
                if (MetadataShimmer != null) MetadataShimmer.Visibility = Visibility.Collapsed;
                if (TechBadgesContent != null) TechBadgesContent.Visibility = Visibility.Collapsed;
                if (TechBadgeSection != null) TechBadgeSection.Visibility = Visibility.Collapsed;
                if (MetadataPanel != null) MetadataPanel.Margin = new Thickness(0);
            }
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
             }
             Attach(HeroImage);
             Attach(HeroImage2);

             // Also ensure ImageOpened is attached for dynamic ambience on HeroImage2
             if (HeroImage2 != null)
             {
                 HeroImage2.ImageOpened -= HeroImage_ImageOpened;
                 HeroImage2.ImageOpened += HeroImage_ImageOpened;
             }
        }
        
        private void OnHeroSizeChanged(object sender, SizeChangedEventArgs e)
        {
             if (sender is Image img)
             {
                 var v = ElementCompositionPreview.GetElementVisual(img);
                 v.CenterPoint = new Vector3((float)img.ActualWidth / 2f, (float)img.ActualHeight / 2f, 0);
             }
        }

        private void StartBackgroundSlideshow(List<string> images)
        {
            if (images == null || images.Count == 0 || HeroImage == null) return;
            
            // Deduplicate: Compare with current slideshow ID
            string currentId = _item?.IMDbId ?? _item?.Title ?? "Unknown";
            if (_slideshowId == currentId && _slideshowTimer != null && _backdropUrls?.Count == images.Count)
            {
                 // Already running for this item with same count
                 return;
            }

            _slideshowId = currentId;

            // Stop existing timer
            if (_slideshowTimer != null)
            {
                _slideshowTimer.Stop();
                _slideshowTimer = null;
            }

            _backdropUrls = images;
            _currentBackdropIndex = 0;
            _isHeroImage1Active = true; // Start with HeroImage (as checked below)

            System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Starting with {images.Count} images. Total unique backdrops gathered.");

            // Ensure Composition CenterPoints are ready
            EnsureHeroVisuals();

            // INITIALIZATION LOGIC
            // Check if we already have an image (e.g. Poster from navigation)
            bool hasExistingImage = HeroImage.Source != null && HeroImage.Opacity > 0.1;

            if (!hasExistingImage)
            {
                // No existing image, just set the first one directly
                try
                {
                    HeroImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(images[0]));
                    HeroImage.Opacity = 1;
                    if (HeroImage2 != null) HeroImage2.Opacity = 0;
                    StartKenBurnsEffect(HeroImage);
                }
                catch { }
            }
            else
            {
                // We have an existing image. Assume it's on HeroImage and Active.
                _isHeroImage1Active = true; 
                if (HeroImage2 != null) HeroImage2.Opacity = 0;
                
                // Ensure animation is running on current header
                HeroImage.Opacity = 1; // [FIX] Ensure it's visible if seeding happened
                StartKenBurnsEffect(HeroImage);

                // DEDUPLICATION: If the first backdrop is the same as seeded image, skip index 0
                bool firstIsSame = false;
                if (HeroImage.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bmi && bmi.UriSource != null)
                {
                    string seededUrl = bmi.UriSource.ToString();
                    if (seededUrl.Equals(images[0], StringComparison.OrdinalIgnoreCase))
                    {
                        firstIsSame = true;
                    }
                }

                // Start from 0 if first is same (so next tick is 1), else -1 (so next tick is 0)
                _currentBackdropIndex = firstIsSame ? 0 : -1;
            }

            // If only 1 image, don't run timer
            if (images.Count <= 1) return;

            // 2. Setup Timer for cycling
            _slideshowTimer = new DispatcherTimer();
            _slideshowTimer.Interval = TimeSpan.FromSeconds(8);
            _slideshowTimer.Tick += (s, e) =>
            {
                if (HeroImage == null || HeroImage2 == null || _backdropUrls == null || _backdropUrls.Count == 0)
                {
                    _slideshowTimer?.Stop();
                    return;
                }

                _currentBackdropIndex = (_currentBackdropIndex + 1) % _backdropUrls.Count;
                string nextImgUrl = _backdropUrls[_currentBackdropIndex];

                // Toggle Logic:
                // If Hero1 is active, we load into Hero2, start Hero2 anim, fade Hero2 IN, Hero1 OUT.
                // Then Hero2 becomes active.
                
                Image incoming = _isHeroImage1Active ? HeroImage2 : HeroImage;
                Image outgoing = _isHeroImage1Active ? HeroImage : HeroImage2;

                // 1. Load Next Image
                try { incoming.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(nextImgUrl)); } catch { return; }

                // 2. Start Ken Burns on Incomng (starts at Scale 1.0)
                StartKenBurnsEffect(incoming);

                // 3. Animate Crossfade
                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f);
                fadeIn.InsertKeyFrame(1f, 1f);
                fadeIn.Duration = TimeSpan.FromSeconds(1.2);

                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, 1f);
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromSeconds(1.2);

                var visualIncoming = ElementCompositionPreview.GetElementVisual(incoming);
                var visualOutgoing = ElementCompositionPreview.GetElementVisual(outgoing);

                // Ensure XAML Opacity is 1
                incoming.Opacity = 1; 
                outgoing.Opacity = 1; 
                
                // Start Opacity Animations
                visualIncoming.Opacity = 0; 
                visualIncoming.StartAnimation("Opacity", fadeIn);
                visualOutgoing.StartAnimation("Opacity", fadeOut);

                // 4. Update State
                _isHeroImage1Active = !_isHeroImage1Active;
                
                // Note: We do NOT stop KenBurns on outgoing immediately, let it fade out while moving.
                // We don't need a cleanup task to swap sources anymore.
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
        }

        private void SetupAnticipationPulse(Button btn, FrameworkElement content)
        {
            if (btn == null || content == null) return;
            
            // 1. Content Visual (Scale Pulse)
            var contentVisual = ElementCompositionPreview.GetElementVisual(content);
            
            // 2. Button Visual (Magnetic Positional Tracking)
            var btnVisual = ElementCompositionPreview.GetElementVisual(btn);
            ElementCompositionPreview.SetIsTranslationEnabled(btn, true);

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
                     ep.EpisodeNumber, null, null, null, _item?.PosterUrl, "series");

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
                         HistoryManager.Instance.UpdateProgress(ep.Id, 
                             ep.Title, 
                             ep.StreamUrl ?? "", 
                             1000, 1000, 
                             seriesId,
                             seriesName,
                             ep.SeasonNumber, 
                             ep.EpisodeNumber, null, null, null, _item?.PosterUrl, "series");
                             
                         ep.IsWatched = true;
                         ep.ProgressPercent = 0;
                         ep.ProgressText = "";
                         ep.HasProgress = false;
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
                     ep.EpisodeNumber, null, null, null, _item?.PosterUrl, "series");
                     
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
            if (_item == null || WatchlistButton == null) return;

            bool isInList = Services.WatchlistManager.Instance.IsOnWatchlist(_item);
            var icon = (FontIcon)WatchlistButton.Content;
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

            WatchlistButton.Background = isInList 
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(50, 33, 150, 243)) // Subtle Blue Tint
                : (_themeTintBrush ?? new SolidColorBrush(Windows.UI.Color.FromArgb(37, 255, 255, 255))); // Use Theme Tint if available
            
            ToolTipService.SetToolTip(WatchlistButton, isInList ? "İzleme Listesinden Çıkar" : "İzleme Listesine Ekle");
        }
    }
    public class SeasonItem
    {
        public string Name { get; set; }
        public string SeasonName { get; set; } // Alias for binding
        public int SeasonNumber { get; set; }
        public List<EpisodeItem> Episodes { get; set; }
    }

    public class EpisodeItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Name { get; set; } // Alias
        public string Overview { get; set; }
        public string Duration { get; set; }
        
        public string ImageUrl { get; set; }
        
        public string StreamUrl { get; set; }
        public string Container { get; set; }
        public int SeasonNumber { get; set; }

        public int EpisodeNumber { get; set; }
        
        // Formatted Metadata
        public string EpisodeNumberFormatted => $"{EpisodeNumber}. Bölüm";
        public string DurationFormatted { get; set; }
        public bool IsReleased { get; set; } = true;
        public DateTime? ReleaseDate { get; set; }
        public string ReleaseDateFormatted => ReleaseDate.HasValue ? ReleaseDate.Value.ToString("d MMMM yyyy", new System.Globalization.CultureInfo("tr-TR")) : "";
        private bool _isWatched;
        public bool IsWatched
        {
            get => _isWatched;
            set { if (_isWatched != value) { _isWatched = value; OnPropertyChanged(nameof(IsWatched)); } }
        }
        
        // Progress UI
        private bool _hasProgress;
        public bool HasProgress
        {
            get => _hasProgress;
            set { if (_hasProgress != value) { _hasProgress = value; OnPropertyChanged(nameof(HasProgress)); } }
        }

        private double _progressPercent;
        public double ProgressPercent
        {
            get => _progressPercent;
            set { if (Math.Abs(_progressPercent - value) > 0.01) { _progressPercent = value; OnPropertyChanged(nameof(ProgressPercent)); } }
        }
        public string ProgressText { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        public void RefreshHistoryState()
        {
            var history = HistoryManager.Instance.GetProgress(Id);
            if (history != null)
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
                        ProgressText = remaining.TotalHours >= 1 
                            ? $"{(int)remaining.TotalHours}sa {(int)remaining.Minutes}dk Kaldı"
                            : $"{(int)remaining.TotalMinutes}dk Kaldı";
                    }
                }
                
                OnPropertyChanged(nameof(IsWatched));
                OnPropertyChanged(nameof(HasProgress));
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }
    

    
    public class CastItem
    {
        public string Name { get; set; }
        public string Character { get; set; }
        public string FullProfileUrl { get; set; }



    }

    public class StremioStreamViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public string Title { get; set; }
        public string Name { get; set; }
        public string ProviderText { get; set; }
        public string AddonName { get; set; }
        public string Url { get; set; }
        public string ExternalUrl { get; set; }
        public bool IsExternalLink => !string.IsNullOrEmpty(ExternalUrl) && string.IsNullOrEmpty(Url);
        public string Quality { get; set; }
        public bool HasQuality => !string.IsNullOrEmpty(Quality);
        public string Size { get; set; }
        public bool HasSize => !string.IsNullOrEmpty(Size);
        public bool IsCached { get; set; }
        public ModernIPTVPlayer.Models.Stremio.StremioStream OriginalStream { get; set; }

        private bool _isActive;
        public bool IsActive 
        { 
            get => _isActive; 
            set 
            { 
                if (_isActive != value) 
                { 
                    _isActive = value; 
                    OnPropertyChanged(nameof(IsActive)); 
                } 
            } 
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public class StremioAddonViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name;
        public string Name 
        { 
            get => _name; 
            set { if(_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } } 
        }

        private List<StremioStreamViewModel> _streams;
        public List<StremioStreamViewModel> Streams
        {
            get => _streams;
            set { if(_streams != value) { _streams = value; OnPropertyChanged(nameof(Streams)); } }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set 
            { 
                if(_isLoading != value) 
                { 
                    _isLoading = value; 
                    OnPropertyChanged(nameof(IsLoading)); 
                    OnPropertyChanged(nameof(IsLoaded));
                } 
            }
        }
        
        public bool IsLoaded => !IsLoading;
        
        public int SortIndex { get; set; }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}







