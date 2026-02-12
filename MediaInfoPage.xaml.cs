using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using ModernIPTVPlayer.Controls;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
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
        private bool _isInitializingSeriesUi;
        private readonly Dictionary<string, StremioSourcesCacheEntry> _stremioSourcesCache = new();
        private int _sourcesRequestVersion;
        private string _currentStremioVideoId;
        private bool _isSourcesFetchInProgress;
        private bool _isCurrentSourcesComplete;
        
        private FFmpegProber _ffprober = new();
        private CancellationTokenSource _probeCts;

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

        private void MediaInfoPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
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
                    if (SourcesPanel != null && SourcesPanel.Visibility == Visibility.Visible)
                    {
                        // Sources are active
                        SourcesPanel.Visibility = Visibility.Visible;
                        if (NarrowSourcesSection != null) NarrowSourcesSection.Visibility = Visibility.Collapsed;
                        if (EpisodesPanel != null) EpisodesPanel.Visibility = Visibility.Collapsed;
                    }

                    if (NarrowEpisodesSection != null) NarrowEpisodesSection.Visibility = Visibility.Collapsed;
                    if (NarrowCastSection != null) NarrowCastSection.Visibility = Visibility.Collapsed;
                    if (CastSection != null) CastSection.Visibility = Visibility.Visible;
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
                        if (NarrowCastSection != null) NarrowCastSection.Visibility = Visibility.Visible;
                    }
                    else
                    {
                         if (NarrowEpisodesSection != null) NarrowEpisodesSection.Visibility = Visibility.Collapsed;
                         if (NarrowCastSection != null) NarrowCastSection.Visibility = Visibility.Collapsed;
                    }

                    // Handle sources visibility in Narrow mode
                    if (NarrowSourcesSection != null && (NarrowSourcesSection.Visibility == Visibility.Visible || SourcesPanel.Visibility == Visibility.Visible))
                    {
                        NarrowSourcesSection.Visibility = Visibility.Visible;
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

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            try
            {
                base.OnNavigatedTo(e);
                SetupParallax();
                _isHandoffInProgress = false;

                // RE-ATTACH: If the player was handed off, it might be detached from its original host.
                if (MediaInfoPlayer != null && MediaInfoPlayer.Parent == null && PlayerHost != null)
                {
                    PlayerHost.Content = MediaInfoPlayer;
                    System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Re-attached player to original host.");
                }

                // Load History
                await HistoryManager.Instance.InitializeAsync();

                bool isBackNav = e.NavigationMode == NavigationMode.Back;

                if (isBackNav && _item != null)
                {
                    // BACK NAVIGATION: Refresh UI State without full reload
                    System.Diagnostics.Debug.WriteLine("[MediaInfoPage] Back Navigation Detected - Refreshing Progress Only");
                    
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
                                InitializePrebufferPlayer(_streamUrl, history.Position);
                                _ = UpdateTechnicalBadgesAsync(_streamUrl);
                            }
                            else
                            {
                                MetadataShimmer.Visibility = Visibility.Collapsed;
                                TechBadgesContent.Visibility = Visibility.Collapsed;
                                if (TechBadgeSection != null) TechBadgeSection.Visibility = Visibility.Collapsed;
                                PlayButtonSubtext.Visibility = Visibility.Collapsed;
                            }
                        }
                        else if (stremioItem.Meta.Type == "series" || stremioItem.Meta.Type == "tv")
                        {
                            // Refresh Series Progress
                            await RefreshStremioSeriesProgressAsync(stremioItem);
                        }
                    }
                    else if (_item is SeriesStream ss)
                    {
                        // Refresh IPTV Series
                         var lastWatched = HistoryManager.Instance.GetLastWatchedEpisode(ss.SeriesId.ToString());
                         if (lastWatched != null)
                         {
                             PlayButtonText.Text = "Devam Et";
                             PlayButtonSubtext.Visibility = Visibility.Visible;
                             // Update List Selection if needed
                             if (CurrentEpisodes != null)
                             {
                                 var ep = CurrentEpisodes.FirstOrDefault(x => x.EpisodeNumber == lastWatched.EpisodeNumber && x.SeasonNumber == lastWatched.SeasonNumber);
                                 if (ep != null) 
                                 {
                                     _selectedEpisode = ep;
                                     EpisodesListView.SelectedItem = ep;
                                     // Re-bind to update progress bars
                                     // (ObservableCollection updates might generally handle this if properties notify, but full replace is safer for List)
                                 }
                             }
                         }
                    }

                    // Restore Visuals
                    StartHeroConnectedAnimation();
                    return; 
                }

                // NEW NAVIGATION
                if (e.Parameter is MediaNavigationArgs args)
                {
                    _item = args.Stream;
                    await LoadDetailsAsync(args.Stream, args.TmdbInfo);

                    if (args.AutoResume)
                    {
                        PlayButton_Click(PlayButton, new RoutedEventArgs());
                    }
                }
                else if (e.Parameter is IMediaStream item)
                {
                    _item = item;
                    await LoadDetailsAsync(item);
                }
                else if (e.Parameter is string stremioId) 
                {
                    // Direct Link Support
                }
                
                StartHeroConnectedAnimation();
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
            // 1. Determine if we already have data
            var existingTmdb = preFetchedTmdb ?? item.TmdbInfo;
            bool hasBasicData = existingTmdb != null;
            _cachedTmdb = existingTmdb;

            // 2. Immediate UI Update (Always show Shimmers for aesthetic delay)
            _streamUrl = null; // Clear old state
            SetLoadingState(true); 
            SetBadgeLoadingState(true); // Explicitly reset badges to loading state
            
            // CLEAR STATE (Prevent Stale Data)
            Seasons?.Clear();
            CurrentEpisodes?.Clear();

            // Reset Progress Subtexts
            PlayButtonSubtext.Visibility = Visibility.Collapsed;
            StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
            
            // RESET PANELS (Prevent State Bleed)
            SourcesPanel.Visibility = Visibility.Collapsed;
            NarrowSourcesSection.Visibility = Visibility.Collapsed; 
            if (item is SeriesStream || (item is Models.Stremio.StremioMediaStream smsCheck && (smsCheck.Meta.Type == "series" || smsCheck.Meta.Type == "tv")))
            {
                // Series: Episodes panel is managed by UpdateLayoutState/LoadSeriesData
                // Ensure sources are hidden
            }
            else
            {
               // Movie: Episodes panel hidden
               EpisodesPanel.Visibility = Visibility.Collapsed;
               NarrowEpisodesSection.Visibility = Visibility.Collapsed;
            }
            
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

            TitleText.Text = item.Title;
            StickyTitle.Text = item.Title; 

            string heroUrl = item.PosterUrl;
            // Prefer Stremio Background (Backdrop) over Poster for Hero Image
            if (item is Models.Stremio.StremioMediaStream smsHero && !string.IsNullOrEmpty(smsHero.Meta.Background))
            {
                heroUrl = smsHero.Meta.Background;
            }

            if (!string.IsNullOrEmpty(heroUrl))
            {
                HeroImage.Opacity = 0;
                HeroImage.Source = new BitmapImage(new Uri(heroUrl));
                await Task.Delay(50);
                HeroImage.Opacity = 1; 
                StartHeroConnectedAnimation();
                ApplyPremiumAmbience(heroUrl);
                ElementSoundPlayer.Play(ElementSoundKind.Show);
            }

            // Populate what we have from Cache immediately (still hidden by Opacity=0/Shimmer)
            if (hasBasicData)
            {
                TitleText.Text = existingTmdb.DisplayTitle;
                OverviewText.Text = !string.IsNullOrEmpty(existingTmdb.Overview) ? existingTmdb.Overview : "Açıklama mevcut değil.";
                YearText.Text = existingTmdb.DisplayDate?.Split('-')[0] ?? "";
                GenresText.Text = existingTmdb.GetGenreNames();

                // FORCE LineHeight to known value for Shimmer Sync
                OverviewText.LineHeight = 24; 

                // Pre-apply MaxLines for Series to ensure UpdateLayout measures correctly clamped height
                if (item is SeriesStream) 
                {
                    OverviewText.MaxLines = 4;
                    OverviewText.TextTrimming = TextTrimming.CharacterEllipsis;
                }
                else
                {
                    OverviewText.MaxLines = 0; // Unlimited for movies
                }

                // 2. Measure everything with tiny opacity to ensure layout is ready
                TitlePanel.Opacity = 0;
                OverviewPanel.Opacity = 0;
                this.UpdateLayout(); // Global measurement

                // Match shimmers to actual rendered sizes
                AdjustTitleShimmer();
                AdjustOverviewShimmer(OverviewText.Text);

                if (!string.IsNullOrEmpty(existingTmdb.FullBackdropUrl))
                {
                    HeroImage.Source = new BitmapImage(new Uri(existingTmdb.FullBackdropUrl));
                }
            }
            else
            {
                // Fetch if we don't have it
                if (item is SeriesStream)
                {
                     string extractedYear = TmdbHelper.ExtractYear(item.Title);
                     _cachedTmdb = await TmdbHelper.SearchTvAsync(item.Title, extractedYear);
                }
                else if (item is Models.Stremio.StremioMediaStream sms)
                {
                     // Stremio items usually have IMDB ID in their ID field (e.g. tt1234567)
                     string imdbId = sms.Meta.Id ?? "";
                     string rawTitle = item.Title;
                     string rawYear = sms.Year;

                     System.Diagnostics.Debug.WriteLine($"[Stremio] Fetching details for: {rawTitle} (ID: {imdbId}, Year: {rawYear})");

                     if (!string.IsNullOrEmpty(imdbId) && imdbId.StartsWith("tt"))
                     {
                         if (sms.Meta.Type == "series" || sms.Meta.Type == "tv")
                             _cachedTmdb = await TmdbHelper.GetTvByExternalIdAsync(imdbId);
                         else
                             _cachedTmdb = await TmdbHelper.GetMovieByExternalIdAsync(imdbId);
                     }
                     
                     if (_cachedTmdb == null)
                     {
                         // Fallback to title search if ID lookup failed
                         string cleanYear = TmdbHelper.ExtractYear(rawYear) ?? rawYear;
                         System.Diagnostics.Debug.WriteLine($"[Stremio] ID lookup failed. Searching by Title: {rawTitle} Year: {cleanYear}");
                         if (sms.Meta.Type == "series" || sms.Meta.Type == "tv")
                              _cachedTmdb = await TmdbHelper.SearchTvAsync(rawTitle, cleanYear);
                         else
                              _cachedTmdb = await TmdbHelper.SearchMovieAsync(rawTitle, cleanYear);
                     }
                }
                else
                {
                     _cachedTmdb = await TmdbHelper.SearchMovieAsync(item.Title, (item as LiveStream)?.Year);
                }
            }

            if (_cachedTmdb != null)
            {
                // Override Title with clean TMDB title
                TitleText.Text = _cachedTmdb.DisplayTitle;
                
                // Show original playlist name as a super-title if different
                System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] Title Check: IPTV='{item.Title}', TMDB='{_cachedTmdb.DisplayTitle}'");
                if (!string.Equals(item.Title, _cachedTmdb.DisplayTitle, StringComparison.OrdinalIgnoreCase))
                {
                    SuperTitleText.Text = item.Title.ToUpperInvariant();
                    SuperTitleText.Visibility = Visibility.Visible;
                    System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] Setting SuperTitle: {SuperTitleText.Text}");
                }
                else
                {
                    SuperTitleText.Visibility = Visibility.Collapsed;
                }
                OverviewText.Text = !string.IsNullOrEmpty(_cachedTmdb.Overview) ? _cachedTmdb.Overview : "Açıklama mevcut değil.";
                YearText.Text = _cachedTmdb.DisplayDate?.Split('-')[0] ?? "";

                // FORCE LineHeight again in case it wasn't set (e.g. came from search path)
                OverviewText.LineHeight = 24;
                if (item is SeriesStream) 
                {
                    OverviewText.MaxLines = 4;
                }
                
                if (!string.IsNullOrEmpty(_cachedTmdb.FullBackdropUrl))
                {
                    // Swap to high-res backdrop gracefully
                    var highResSource = new BitmapImage(new Uri(_cachedTmdb.FullBackdropUrl));
                    HeroImage.Source = highResSource;
                }

                // Adjust Skeletons again in case search returned different data or layout shifted
                AdjustTitleShimmer();
                // Re-measure just to be safe with new text
                this.UpdateLayout();
                AdjustOverviewShimmer(OverviewText.Text);

                // For non-series IPTV, trigger technical probe immediately
                if (item is LiveStream live)
                {
                    _streamUrl = live.StreamUrl;
                    _ = UpdateTechnicalBadgesAsync(_streamUrl);
                }


                // Initial Cast Shimmer (Standard 4 items)
                AdjustCastShimmer(4);

                // Fetch Deep Details (Runtime, Genres)
                bool isTv = item is SeriesStream || (item is Models.Stremio.StremioMediaStream sms && (sms.Meta.Type == "series" || sms.Meta.Type == "tv"));
                
                var details = await TmdbHelper.GetDetailsAsync(_cachedTmdb.Id, isTv);
                if (details != null)
                {
                    RuntimeText.Text = isTv ? "Dizi" : $"{details.Runtime / 60}sa {details.Runtime % 60}dk";
                    GenresText.Text = string.Join(" • ", details.Genres.Select(g => g.Name).Take(3));

                    // FIX: Update Synopsis from Details if available (Search result might be incomplete)
                    if (!string.IsNullOrEmpty(details.Overview))
                    {
                        OverviewText.Text = details.Overview;
                        this.UpdateLayout(); // Force re-measure height
                        AdjustOverviewShimmer(details.Overview);
                    }
                }

                // Fetch Cast
                try
                {
                    var credits = await TmdbHelper.GetCreditsAsync(_cachedTmdb.Id, isTv);
                    if (credits != null && credits.Cast != null)
                    {
                        CastList.Clear();
                        foreach(var c in credits.Cast.Take(10))
                        {
                            CastList.Add(new CastItem { Name = c.Name, Character = c.Character, FullProfileUrl = c.FullProfileUrl });
                        }
                        if (CastList.Count > 0) 
                        {
                            CastSection.Visibility = Visibility.Visible;
                            CastListView.ItemsSource = CastList;
                            if (NarrowCastListView != null)
                                 NarrowCastListView.ItemsSource = CastList;
                                 
                            // Adjust Shimmer to match actual count
                            AdjustCastShimmer(CastList.Count);
                        }
                        else
                        {
                            CastSection.Visibility = Visibility.Collapsed;
                            AdjustCastShimmer(0); // Hide shimmer if no cast
                        }
                    }
                    else
                    {
                        CastSection.Visibility = Visibility.Collapsed;
                        AdjustCastShimmer(0);
                    }
                }
                catch
                {
                     CastSection.Visibility = Visibility.Collapsed;
                     AdjustCastShimmer(0);
                }
            }
            else
            {
                // FALLBACK: Use Provider Data if TMDB is not found
                TitleText.Text = item.Title;
                SuperTitleText.Visibility = Visibility.Collapsed;
                
                OverviewText.Text = (item is SeriesStream ss) ? (ss.Plot ?? "Açıklama mevcut değil.") : "Açıklama mevcut değil.";
                YearText.Text = (item is LiveStream ls) ? (ls.Year ?? "") : "";
                
                if (item is LiveStream live)
                {
                    _ = UpdateTechnicalBadgesAsync(live.StreamUrl);
                }
                
                if (!string.IsNullOrEmpty(item.PosterUrl))
                {
                    try
                    {
                        HeroImage.Source = new BitmapImage(new Uri(item.PosterUrl));
                    }
                    catch { }
                }
                
                RuntimeText.Text = (item is SeriesStream) ? "Dizi" : "Film";
                GenresText.Text = "Genel";
                CastSection.Visibility = Visibility.Collapsed;
                AdjustCastShimmer(0);
                CastSection.Visibility = Visibility.Collapsed;
                AdjustCastShimmer(0);
            }

            // Stremio Specifics
            if (item is Models.Stremio.StremioMediaStream stremioItem)
            {
                 // Catalog items often miss 'background' (backdrop) and 'videos' (episodes).
                 if (string.IsNullOrEmpty(stremioItem.Meta.Background) || stremioItem.Meta.Videos == null || stremioItem.Meta.Videos.Count == 0)
                 {
                      try 
                      {
                          string metaUrl = "https://v3-cinemeta.strem.io";
                          var fullMeta = await Services.Stremio.StremioService.Instance.GetMetaAsync(metaUrl, stremioItem.Meta.Type, stremioItem.Meta.Id);
                          if (fullMeta != null)
                          {
                              stremioItem.Meta = fullMeta; 
                              if (!string.IsNullOrEmpty(fullMeta.Background))
                              {
                                  HeroImage.Source = new BitmapImage(new Uri(fullMeta.Background));
                                  ApplyPremiumAmbience(fullMeta.Background);
                              }
                          }
                      }
                      catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Stremio] Failed to enrich metadata: {ex.Message}"); }
                 }

                 if (stremioItem.Meta.Type == "series" || stremioItem.Meta.Type == "tv")
                 {
                     System.Diagnostics.Debug.WriteLine($"[Stremio] Series Detected. Videos Count: {stremioItem.Meta.Videos?.Count ?? 0}");

                     _isInitializingSeriesUi = true;
                     try
                     {
                         // LOAD DATA FIRST
                         await LoadStremioSeriesDataAsync(stremioItem);

                         // THEN Sync History & UI
                         await RefreshStremioSeriesProgressAsync(stremioItem);
                     }
                     finally
                     {
                         _isInitializingSeriesUi = false;
                     }

                     // Keep episodes/seasons panel visible on initial detail open.
                     ShowSourcesPanel(false);
                 }
                 else
                 {
                     EpisodesPanel.Visibility = Visibility.Collapsed;
                     PlayButtonText.Text = "Kaynak Bul"; 
                     StickyPlayButtonText.Text = "Kaynak Bul";
                     _streamUrl = null; 
                     
                     // Check History for Resume
                     var history = HistoryManager.Instance.GetProgress(stremioItem.Meta.Id);
                     if (history != null && !history.IsFinished && history.Position > 0)
                     {
                         PlayButtonText.Text = "Devam Et";
                         PlayButtonSubtext.Text = $"{(int)(history.Duration - history.Position) / 60} dk Kaldı";
                         PlayButtonSubtext.Visibility = Visibility.Visible;
                         StickyPlayButtonText.Text = "Devam Et";
                         
                         _streamUrl = history.StreamUrl;
                         InitializePrebufferPlayer(_streamUrl, history.Position);
                         _ = UpdateTechnicalBadgesAsync(_streamUrl);
                         
                         // Also fetch fresh sources in background (UI update)
                         _ = PlayStremioContent(stremioItem.Meta.Id, false);
                     }
                     else
                     {
                         // No history: Hide badges for now until user selects a source
                         MetadataShimmer.Visibility = Visibility.Collapsed;
                         TechBadgesContent.Visibility = Visibility.Collapsed;
                         if (TechBadgeSection != null) TechBadgeSection.Visibility = Visibility.Collapsed;
                         
                         PlayButtonSubtext.Visibility = Visibility.Collapsed;
                         StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
                     }
                     
                     // Auto-show sources if not resuming?
                     if (string.IsNullOrEmpty(_streamUrl))
                     {
                         _ = PlayStremioContent(stremioItem.Meta.Id, false);
                     }
                 }
            }

            if (item is SeriesStream series)
            {
                // SERIES MODE
                EpisodesPanel.Visibility = Visibility.Visible;
                PlayButtonText.Text = "Oynat";

                // --- SERIES EPISODE RESUME LOGIC ---
                var lastWatched = HistoryManager.Instance.GetLastWatchedEpisode(series.SeriesId.ToString());
                if (lastWatched != null)
                {
                    string resumeText = "Devam Et";
                    int displayEp = lastWatched.EpisodeNumber == 0 ? 1 : lastWatched.EpisodeNumber;
                    string subtext = $"S{lastWatched.SeasonNumber:D2}E{displayEp:D2}";

                    PlayButtonText.Text = resumeText;
                    PlayButtonSubtext.Text = subtext;
                    PlayButtonSubtext.Visibility = Visibility.Visible;

                    StickyPlayButtonText.Text = resumeText;
                    StickyPlayButtonSubtext.Text = subtext;
                    StickyPlayButtonSubtext.Visibility = Visibility.Visible;
                    
                    RestartButton.Visibility = Visibility.Visible;

                    // Update Top Header with Episode Info if TMDB is available
                    if (_cachedTmdb != null)
                    {
                        var seasonDetail = await TmdbHelper.GetSeasonDetailsAsync(_cachedTmdb.Id, lastWatched.SeasonNumber);
                        if (seasonDetail?.Episodes != null)
                        {
                            var ep = seasonDetail.Episodes.FirstOrDefault(e => e.EpisodeNumber == lastWatched.EpisodeNumber);
                            // Fallback for 0-based indexing
                            if (ep == null && lastWatched.EpisodeNumber == 0)
                                ep = seasonDetail.Episodes.FirstOrDefault(e => e.EpisodeNumber == 1);

                            if (ep != null)
                            {
                                // Determine best title (TMDB name vs IPTV title from history)
                                string epName = ep.Name;
                                string cleanIptv = TmdbHelper.CleanEpisodeTitle(lastWatched.Title);
                                bool isGeneric = IsGenericEpisodeTitle(epName, ep.EpisodeNumber);

                                if (isGeneric && !string.IsNullOrEmpty(cleanIptv) && cleanIptv.Length > 2)
                                {
                                    epName = cleanIptv;
                                }
                                else if (string.IsNullOrEmpty(epName))
                                {
                                    epName = $"S{lastWatched.SeasonNumber:D2}E{ep.EpisodeNumber:D2}";
                                }

                                TitleText.Text = epName;
                                // Series name as sub-title
                                
                                if (!string.IsNullOrEmpty(ep.Overview))
                                {
                                    OverviewText.Text = ep.Overview;
                                    AdjustOverviewShimmer(ep.Overview);
                                }
                            }
                        }
                    }
                }
                else
                {
                    PlayButtonText.Text = "Oynat";
                    PlayButtonSubtext.Visibility = Visibility.Collapsed;
                    StickyPlayButtonText.Text = "Oynat";
                    StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
                    RestartButton.Visibility = Visibility.Collapsed;
                }
                // ------------------------------------
                
                await LoadSeriesDataAsync(series);
            }
            else if (item is LiveStream live)
            {
                // MOVIE / LIVE MODE
                _streamUrl = live.StreamUrl;
                EpisodesPanel.Visibility = Visibility.Collapsed;
                SourcesPanel.Visibility = Visibility.Collapsed;
                if (NarrowSourcesSection != null) NarrowSourcesSection.Visibility = Visibility.Collapsed;
                
                // History Check (Resuming Movie?)
                var history = HistoryManager.Instance.GetProgress(live.StreamId.ToString());
                if (history != null && history.Position > 0)
                {
                    double percent = history.Duration > 0 ? (history.Position / history.Duration) * 100 : 0;
                    if (!history.IsFinished && percent < 98)
                    {
                        string resumeText = "Devam Et";
                        var remaining = TimeSpan.FromSeconds(history.Duration - history.Position);
                        string subtext = remaining.TotalHours >= 1 
                            ? $"{remaining.Hours}sa {remaining.Minutes}dk Kaldı"
                            : $"{remaining.Minutes}dk Kaldı";

                        PlayButtonText.Text = resumeText;
                        PlayButtonSubtext.Text = subtext;
                        PlayButtonSubtext.Visibility = Visibility.Visible;

                        StickyPlayButtonText.Text = resumeText;
                        StickyPlayButtonSubtext.Text = subtext;
                        StickyPlayButtonSubtext.Visibility = Visibility.Visible;

                        RestartButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                         PlayButtonText.Text = "Tekrar İzle"; 
                         PlayButtonSubtext.Visibility = Visibility.Collapsed;
                         RestartButton.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    PlayButtonText.Text = "Oynat";
                    PlayButtonSubtext.Visibility = Visibility.Collapsed;
                    StickyPlayButtonText.Text = "Oynat";
                    StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
                    RestartButton.Visibility = Visibility.Collapsed;
                }

                InitializePrebufferPlayer(_streamUrl, history?.Position ?? 0);
            }

            // SHARED SLIDESHOW LOGIC (For Stremio, Movies, Live)
            // Move out of type-specific blocks so it works for everyone with TMDB data
            bool startedSlideshow = false;
            if (_cachedTmdb != null && _cachedTmdb.Images?.Backdrops != null && _cachedTmdb.Images.Backdrops.Count > 0)
            {
                // Extract URLs
                var backdrops = _cachedTmdb.Images.Backdrops.Select(i => TmdbHelper.GetImageUrl(i.FilePath, "original")).Take(10).ToList();
                if (backdrops.Count > 0)
                {
                    StartBackgroundSlideshow(backdrops);
                    startedSlideshow = true;
                }
            }
            
            if (!startedSlideshow && _item is LiveStream lsFallback && !string.IsNullOrEmpty(lsFallback.IconUrl))
            {
                // Single Image fallback (IPTV Cover)
                StartBackgroundSlideshow(new List<string> { lsFallback.IconUrl });
            }

            // Wait for at least 500ms aesthetic delay to pass before reveal
            // await aestheticDelayTask; // REMOVED

            // 3. Final Step: Smooth Staggered Crossfade (Skeleton -> Real UI)
            StaggeredRevealContent();
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
                MetadataShimmer.Visibility = Visibility.Visible;
                ActionBarShimmer.Visibility = Visibility.Visible;
                OverviewShimmer.Visibility = Visibility.Visible;
                CastShimmer.Visibility = Visibility.Visible;

                ElementCompositionPreview.GetElementVisual(TitleShimmer).Opacity = 1f;
                ElementCompositionPreview.GetElementVisual(MetadataShimmer).Opacity = 1f;
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
            try
            {
                // Prepare smooth fade-in for all background effects
                var localBgVisual = ElementCompositionPreview.GetElementVisual(LocalInfoGradient);
                if (localBgVisual != null)
                {
                    var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(1f, 1f);
                    fadeIn.Duration = TimeSpan.FromSeconds(1); // Cinematic fade
                    localBgVisual.StartAnimation("Opacity", fadeIn);
                }

                var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("ForwardConnectedAnimation");
                if (anim != null)
                {
                    // We target the Container to make sure Image + Effects fly together
                    anim.Configuration = new DirectConnectedAnimationConfiguration();
                    
                    // Start the morph
                    anim.Completed += (s, e) => StartKenBurnsEffect();
                    anim.TryStart(HeroContainer);
                }
                else
                {
                    // No connected animation - just start Ken Burns immediately
                    StartKenBurnsEffect();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Connected Animation Failed: {ex.Message}");
                // Ensure Ken Burns starts even if connected animation fails
                StartKenBurnsEffect();
            }
        }

        private void StartKenBurnsEffect()
        {
            var visual = ElementCompositionPreview.GetElementVisual(HeroImage);
            
            // Ensure Center Point is center for correct scaling
            visual.CenterPoint = new Vector3((float)HeroImage.ActualWidth / 2f, (float)HeroImage.ActualHeight / 2f, 0);
            
            // Update center point on size change to keep it centered
            HeroImage.SizeChanged += (s, e) => 
            {
                 if (visual != null)
                    visual.CenterPoint = new Vector3((float)HeroImage.ActualWidth / 2f, (float)HeroImage.ActualHeight / 2f, 0);
            };

            var scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(0f, new Vector3(1.0f, 1.0f, 1.0f));
            scaleAnim.InsertKeyFrame(1f, new Vector3(1.08f, 1.08f, 1.0f));
            scaleAnim.Duration = TimeSpan.FromSeconds(25);
            scaleAnim.IterationBehavior = AnimationIterationBehavior.Forever;
            scaleAnim.Direction = AnimationDirection.Alternate;
            
            visual.StartAnimation("Scale", scaleAnim);
        }

        private async void ApplyPremiumAmbience(string imageUrl)
        {
            var colors = await ImageHelper.GetOrExtractColorAsync(imageUrl);
            
            // Fallback: Use extracted color OR default to Deep Sky Blue
            var primary = colors.HasValue ? colors.Value.Primary : Color.FromArgb(255, 0, 120, 215);

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

        private async Task LoadSeriesDataAsync(SeriesStream series)
        {
            try
            {
                var playlistsJson = AppSettings.PlaylistsJson;
                var lastId = AppSettings.LastPlaylistId;
                if (string.IsNullOrEmpty(playlistsJson) || lastId == null) return;

                var playlists = System.Text.Json.JsonSerializer.Deserialize<List<Playlist>>(playlistsJson);
                var activePlaylist = playlists?.Find(p => p.Id == lastId);
                if (activePlaylist == null) return;

                // 1. USE CACHE SERVICE (Fastest)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                System.Diagnostics.Debug.WriteLine($"[PERF] Starting LoadSeriesDataAsync for {series.SeriesId}...");

                var result = await Services.ContentCacheService.Instance.GetSeriesInfoAsync(series.SeriesId, new Services.LoginParams 
                {
                    Host = activePlaylist.Host,
                    Username = activePlaylist.Username,
                    Password = activePlaylist.Password,
                    PlaylistUrl = activePlaylist.Id.ToString(),
                });
                
                sw.Stop();
                System.Diagnostics.Debug.WriteLine($"[PERF] Cache Service returned in {sw.ElapsedMilliseconds}ms. Result Null? {result == null}");

                if (result == null || result.Episodes == null) 
                {
                    System.Diagnostics.Debug.WriteLine($"[PERF] ABORT: Result is null or has no episodes.");
                    return;
                }

                // 2. Clear UI Lists
                Seasons.Clear();
                CurrentEpisodes.Clear();

                // 3. Accurate TMDB Identification (Highest Priority)
                System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] Loading info for series: {series.Name} (ID: {series.SeriesId})");
                
                sw.Restart();
                if (result.Info != null && result.Info.TmdbId != null)
                {
                    string tidStr = result.Info.TmdbId.ToString();
                    if (result.Info.TmdbId is System.Text.Json.JsonElement je)
                    {
                        tidStr = je.GetRawText().Trim('"');
                    }

                    if (int.TryParse(tidStr, out int tid) && tid > 0)
                    {
                        if (_cachedTmdb == null || _cachedTmdb.Id != tid)
                        {
                            System.Diagnostics.Debug.WriteLine($"[PERF] Fetching TMDB TV Show Info for ID: {tid}...");
                            _cachedTmdb = await TmdbHelper.GetTvByIdAsync(tid);
                        }
                    }
                }

                // FIX: Check for stale cache (Missing Images) - MOVED OUTSIDE to ensure it runs
                if (_cachedTmdb != null && _cachedTmdb.Images == null)
                {
                     System.Diagnostics.Debug.WriteLine($"[TMDB] Stale cache detected (No Images). Force refreshing ID: {_cachedTmdb.Id}");
                     Services.TmdbCacheService.Instance.Remove($"tv_id_{_cachedTmdb.Id}");
                     _cachedTmdb = await TmdbHelper.GetTvByIdAsync(_cachedTmdb.Id);
                }
                
                System.Diagnostics.Debug.WriteLine($"[PERF] TMDB Identification took {sw.ElapsedMilliseconds}ms. CachedTMDB Null? {_cachedTmdb == null}");
                if (_cachedTmdb != null)
                {
                     System.Diagnostics.Debug.WriteLine($"[TMDB_DEBUG] CachedTMDB ID: {_cachedTmdb.Id}, Name: {_cachedTmdb.Name}");
                     System.Diagnostics.Debug.WriteLine($"[TMDB_DEBUG] Images Object Null? {_cachedTmdb.Images == null}");
                     if (_cachedTmdb.Images != null)
                     {
                         System.Diagnostics.Debug.WriteLine($"[TMDB_DEBUG] Backdrops Count: {_cachedTmdb.Images.Backdrops?.Count ?? 0}");
                     }
                }
                
                // 4. Process Episodes
                sw.Restart();
                int totalEpisodes = 0;
                foreach (var seasonKvp in result.Episodes)
                {
                    if (int.TryParse(seasonKvp.Key, out int seasonNum))
                    {
                        var seasonEpisodes = new List<EpisodeItem>();
                        
                        bool seasonCacheInvalidated = false;

                        // Fetch TMDB Season Info (if available) - Do this once per season
                        TmdbSeasonDetails tmdbSeason = null;
                        if (_cachedTmdb != null)
                        {
                            long tSeason = System.Diagnostics.Stopwatch.GetTimestamp();
                            tmdbSeason = await TmdbHelper.GetSeasonDetailsAsync(_cachedTmdb.Id, seasonNum);
                            
                            // Check for likely stale season cache (e.g. valid result but NO stills at all in first 3 episodes?)
                            if (tmdbSeason != null && tmdbSeason.Episodes != null && tmdbSeason.Episodes.Count > 0)
                            {
                                int missingStills = tmdbSeason.Episodes.Take(5).Count(e => string.IsNullOrEmpty(e.StillPath));
                                if (missingStills > 3) // If most of the first few are missing
                                {
                                     System.Diagnostics.Debug.WriteLine($"[TMDB] Suspicious Season Cache (Many missing stills) for S{seasonNum}. Invalidating...");
                                     Services.TmdbCacheService.Instance.Remove($"season_{_cachedTmdb.Id}_s{seasonNum}");
                                     tmdbSeason = await TmdbHelper.GetSeasonDetailsAsync(_cachedTmdb.Id, seasonNum);
                                     seasonCacheInvalidated = true;
                                }
                            }

                            long tEndSeason = System.Diagnostics.Stopwatch.GetTimestamp();
                            // Only log if it takes significant time (>50ms)
                             var dMs = (tEndSeason - tSeason) / (double)System.Diagnostics.Stopwatch.Frequency * 1000;
                             if (dMs > 50) System.Diagnostics.Debug.WriteLine($"[PERF] Slow TMDB Season Fetch for S{seasonNum}: {dMs:F1}ms. Refreshed? {seasonCacheInvalidated}");
                        }

                        foreach(var epDef in seasonKvp.Value)
                        {
                            totalEpisodes++;
                            string id = epDef.Id;
                            string container = epDef.ContainerExtension;
                            string title = epDef.Title;
                            
                            // Try to get Episode Number
                            int epNum = 0;
                            if (epDef.EpisodeNum != null)
                            {
                                string enStr = epDef.EpisodeNum.ToString();
                                if (epDef.EpisodeNum is System.Text.Json.JsonElement je) enStr = je.ToString();
                                int.TryParse(enStr, out epNum);
                            }
                            
                            // Match TMDB
                            if (tmdbSeason != null && tmdbSeason.Episodes != null)
                            {
                                var match = tmdbSeason.Episodes.FirstOrDefault(x => x.EpisodeNumber == epNum);
                                if (match != null)
                                {
                                    string cleanIptv = TmdbHelper.CleanEpisodeTitle(title);
                                    bool isGeneric = IsGenericEpisodeTitle(match.Name, epNum);
                                    
                                    if (isGeneric && !string.IsNullOrEmpty(cleanIptv) && cleanIptv.Length > 2)
                                    {
                                        title = cleanIptv;
                                    }
                                    else
                                    {
                                        title = match.Name;
                                    }
                                }
                                else 
                                {
                                     title = TmdbHelper.CleanEpisodeTitle(title);
                                }
                            }

                            if (IsGenericEpisodeTitle(title, epNum))
                            {
                                title = $"S{seasonNum:D2}E{epNum:D2}";
                            }

                            string finalUrl = $"{activePlaylist.Host.TrimEnd('/')}/series/{activePlaylist.Username}/{activePlaylist.Password}/{id}.{container}";
                            
                            // Metadata
                            string thumb = epDef.Info?.MovieImage;
                            string plot = epDef.Info?.Plot;
                            
                            // Prefer TMDB Still
                            if (tmdbSeason != null)
                            {
                                var match = tmdbSeason.Episodes.FirstOrDefault(x => x.EpisodeNumber == epNum);
                                if (match != null)
                                {
                                    // Use TMDB Still IF:
                                    // 1. IPTV image is missing/empty
                                    // 2. OR IPTV image is likely generic (often provider returns icon.png or logo.png)
                                    // 3. AND we have a valid still
                                    
                                    // bool useTmdb = string.IsNullOrEmpty(thumb);
                                    // if (!useTmdb && (thumb.Contains("icon") || thumb.Contains("logo"))) useTmdb = true;
                                    
                                    // FORCE PREFER TMDB if available (User preference inferred from issues)
                                    // Check if IPTV thumb is just an IP/generic link or actually useful? 
                                    // For now, if we have a TMDB match with an image, let's use it.
                                    
                                    bool hasTmdbImg = !string.IsNullOrEmpty(match.StillUrl);
                                    
                                    if (hasTmdbImg)
                                    {
                                        var newThumb = TmdbHelper.GetImageUrl(match.StillUrl, "original");
                                        System.Diagnostics.Debug.WriteLine($"[EP_IMG] Swapping IPTV thumb ('{thumb}') with TMDB ('{newThumb}')");
                                        thumb = newThumb;
                                    }
                                    else
                                    {
                                         System.Diagnostics.Debug.WriteLine($"[EP_IMG] No TMDB Image for S{seasonNum}E{epNum}. Keeping IPTV: '{thumb}'");
                                    }
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[EP_IMG] No TMDB match for S{seasonNum}E{epNum}");
                                }
                            }

                            // FALLBACK: If thumb is still empty, use Series Backdrop or Cover
                            if (string.IsNullOrEmpty(thumb))
                            {
                                if (tmdbSeason != null && !string.IsNullOrEmpty(tmdbSeason.PosterPath))
                                     thumb = TmdbHelper.GetImageUrl(tmdbSeason.PosterPath, "w300"); // Season Poster
                                else if (_cachedTmdb != null && !string.IsNullOrEmpty(_cachedTmdb.BackdropPath))
                                     thumb = TmdbHelper.GetImageUrl(_cachedTmdb.BackdropPath, "w300"); // Series Backdrop
                                else if (_cachedTmdb != null && !string.IsNullOrEmpty(_cachedTmdb.PosterPath))
                                     thumb = TmdbHelper.GetImageUrl(_cachedTmdb.PosterPath, "w300"); // Series Poster
                                else
                                     thumb = series.Cover; // Original IPTV Cover
                            }



                            // History Check
                            var hist = HistoryManager.Instance.GetProgress(id); 
                            string progText = "";
                            bool hasProg = false;
                            double pct = 0;
                            if (hist != null && hist.Duration > 0)
                            {
                                pct = (hist.Position / hist.Duration) * 100;
                                hasProg = pct > 1; 
                                progText = $"{TimeSpan.FromSeconds(hist.Duration - hist.Position).TotalMinutes:F0}dk Kaldı";
                            }

                            seasonEpisodes.Add(new EpisodeItem 
                            { 
                                Id = id,
                                EpisodeNumber = epNum, // Assign as int
                                Name = title,
                                Title = title,
                                Container = container,
                                StreamUrl = finalUrl,
                                ImageUrl = thumb,
                                Overview = plot,
                                Duration = epDef.Info?.Duration,
                                HasProgress = hasProg,
                                ProgressPercent = pct,
                                ProgressText = progText,
                                SeasonNumber = seasonNum
                            });
                        }
                        
                        if (seasonEpisodes.Count > 0)
                        {
                            Seasons.Add(new SeasonItem 
                            { 
                                Name = $"Sezon {seasonNum}", 
                                SeasonName = $"Sezon {seasonNum}", // Set both for binding compat
                                SeasonNumber = seasonNum,
                                Episodes = seasonEpisodes.OrderBy(x => x.EpisodeNumber).ToList()
                            });
                        }
                    }
                }

                sw.Stop();
                System.Diagnostics.Debug.WriteLine($"[PERF] Episode Processing took {sw.ElapsedMilliseconds}ms for {totalEpisodes} episodes.");

                // Sort Seasons
                var sortedSeasons = Seasons.OrderBy(x => x.SeasonNumber).ToList();
                Seasons.Clear();
                foreach(var s in sortedSeasons) Seasons.Add(s);

                // FIX: Ensure UI Binding
                SeasonComboBox.ItemsSource = Seasons;

                // 5. Auto Select Season (History Logic)
                if (Seasons.Count > 0)
                {
                    SeasonComboBox.SelectedIndex = 0;
                }

                var lastWatched = HistoryManager.Instance.GetLastWatchedEpisode(series.SeriesId.ToString());
                SeasonItem targetSeason = null;
                EpisodeItem targetEpisode = null;
                
                if (lastWatched != null)
                {
                    targetSeason = Seasons.FirstOrDefault(s => s.Episodes.Any(e => e.Id == lastWatched.Id)) 
                                   ?? Seasons.FirstOrDefault(s => s.SeasonNumber == lastWatched.SeasonNumber);
                    
                    if (targetSeason != null)
                    {
                        targetEpisode = targetSeason.Episodes.FirstOrDefault(e => e.Id == lastWatched.Id);
                    }
                }

                     if (targetEpisode != null)
                    {
                         _pendingAutoSelectEpisode = targetEpisode;
                    }

                if (targetSeason != null)
                {
                    SeasonComboBox.SelectedItem = targetSeason;
                }
                
                // 6. Start Slideshow (Using TMDB Backdrops if available)
                var backdrops = _cachedTmdb?.Images?.Backdrops?.Select(b => TmdbHelper.GetImageUrl(b.FilePath, "original")).Take(10).ToList();
                if (backdrops != null && backdrops.Count > 0)
                {
                    StartBackgroundSlideshow(backdrops);
                }
                else if (!string.IsNullOrEmpty(series.Cover))
                {
                    StartBackgroundSlideshow(new List<string> { series.Cover });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Error loading Series Details: {ex.Message}");
                // Ensure shimmer is hidden on error
                DispatcherQueue.TryEnqueue(() => SetBadgeLoadingState(false));
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
                
                if (_pendingAutoSelectEpisode != null && season.Episodes.Contains(_pendingAutoSelectEpisode))
                {
                    _isProgrammaticSelection = true;
                    try
                    {
                        EpisodesListView.SelectedItem = _pendingAutoSelectEpisode;
                        if (NarrowEpisodesListView != null)
                        {
                            NarrowEpisodesListView.SelectedItem = _pendingAutoSelectEpisode;
                            NarrowEpisodesListView.ScrollIntoView(_pendingAutoSelectEpisode);
                        }
                        EpisodesListView.ScrollIntoView(_pendingAutoSelectEpisode);
                        _pendingAutoSelectEpisode = null;
                    }
                    finally
                    {
                        _isProgrammaticSelection = false;
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
                     
                     // Sync narrow list
                     if (NarrowEpisodesListView != null)
                        NarrowEpisodesListView.SelectedItem = ep;
                     
                     // UPDATE UI FOR SELECTED EPISODE
                     if (TitleText != null) TitleText.Text = ep.Title;
                     
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
                     var history = HistoryManager.Instance.GetProgress(ep.Id);
                     InitializePrebufferPlayer(ep.StreamUrl, history?.Position ?? 0);

                     if (!string.IsNullOrEmpty(ep.StreamUrl))
                          _ = UpdateTechnicalBadgesAsync(ep.StreamUrl);
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
                    
                    // Sync selection with main list
                    if (EpisodesListView != null)
                         EpisodesListView.SelectedItem = ep;
                    
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
                    var history = HistoryManager.Instance.GetProgress(ep.Id);
                    InitializePrebufferPlayer(ep.StreamUrl, history?.Position ?? 0);
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

        private void InitializePrebufferPlayer(string url, double startTime = 0)
        {
            if (string.IsNullOrEmpty(url)) return;

             _ = Task.Run(() => 
             {
                 DispatcherQueue.TryEnqueue(async () => 
                 {
                     try
                     {
                         // Suppress crash on cached page if player context is invalid
                         if (MediaInfoPlayer == null) return;
                         
                         await MediaInfoPlayer.InitializePlayerAsync();
                         
                         // Limits
                         await MediaInfoPlayer.SetPropertyAsync("cache", "yes");
                         await MediaInfoPlayer.SetPropertyAsync("demuxer-readahead-secs", "5");
                         await MediaInfoPlayer.SetPropertyAsync("demuxer-max-bytes", "5MiB");
                         await MediaInfoPlayer.SetPropertyAsync("demuxer-max-back-bytes", "1MiB");
                         await MediaInfoPlayer.SetPropertyAsync("force-window", "yes");

                         // Headers
                         string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                         await MediaInfoPlayer.SetPropertyAsync("user-agent", userAgent);

                         // Cookies
                         try {
                              var targetUri = new Uri(url);
                              var cookies = HttpHelper.CookieContainer.GetCookies(targetUri);
                              if (cookies.Count > 0) {
                                  string cookieHeader = "";
                                  foreach (System.Net.Cookie c in cookies) cookieHeader += $"{c.Name}={c.Value}; ";
                                  await MediaInfoPlayer.SetPropertyAsync("http-header-fields", $"Cookie: {cookieHeader}");
                              }
                         } catch {}

                         await MediaInfoPlayer.SetPropertyAsync("mute", "yes");
                         await MediaInfoPlayer.SetPropertyAsync("pause", "yes"); // Start paused, let it buffer
                         
                         if (startTime > 0)
                         {
                             // Use 'start' property instead of seeking after open to avoid "property unavailable" errors
                             await MediaInfoPlayer.SetPropertyAsync("start", startTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
                         }
                         
                         PlayerOverlayContainer.Visibility = Visibility.Visible;
                         PlayerOverlayContainer.Opacity = 0;
                         await MediaInfoPlayer.OpenAsync(url);
                         
                         // Optional: Detect media info
                         //_ = ExtractTechInfoAsync();
                     }
                     catch (Exception ex)
                     {
                         System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Prebuffer Error: {ex.Message}");
                     }
                 });
             });
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

        private async Task LoadStremioSeriesDataAsync(Models.Stremio.StremioMediaStream series)
        {
            try
            {
                Seasons.Clear();
                CurrentEpisodes.Clear();

                if (series.Meta.Videos == null || series.Meta.Videos.Count == 0) return;

                var grouped = series.Meta.Videos
                    .Where(v => v.Season > 0)
                    .GroupBy(v => v.Season)
                    .OrderBy(g => g.Key);

                foreach (var group in grouped)
                {
                    int seasonNum = group.Key;
                    var epList = new List<EpisodeItem>();

                    TmdbSeasonDetails tmdbSeason = null;
                    if (_cachedTmdb != null)
                    {
                        tmdbSeason = await TmdbHelper.GetSeasonDetailsAsync(_cachedTmdb.Id, seasonNum);
                    }

                    foreach (var vid in group.OrderBy(v => v.Episode))
                    {
                        int episodeNum = vid.Episode > 0 ? vid.Episode : 1;
                        var tmdbMatch = tmdbSeason?.Episodes?.FirstOrDefault(x => x.EpisodeNumber == episodeNum);

                        string resolvedTitle = vid.Title;
                        if (tmdbMatch != null && !IsGenericEpisodeTitle(tmdbMatch.Name, episodeNum))
                        {
                            resolvedTitle = tmdbMatch.Name;
                        }
                        else if (IsGenericEpisodeTitle(resolvedTitle, episodeNum))
                        {
                            resolvedTitle = $"S{seasonNum:D2}E{episodeNum:D2}";
                        }

                        string resolvedOverview = tmdbMatch?.Overview;
                        if (string.IsNullOrWhiteSpace(resolvedOverview))
                        {
                            resolvedOverview = vid.Overview;
                        }

                        string resolvedImage = vid.Thumbnail;
                        if (!string.IsNullOrEmpty(tmdbMatch?.StillPath))
                        {
                            resolvedImage = TmdbHelper.GetImageUrl(tmdbMatch.StillPath, "w300");
                        }
                        if (string.IsNullOrEmpty(resolvedImage))
                        {
                            resolvedImage = series.PosterUrl;
                        }

                        var epItem = new EpisodeItem
                        {
                            Id = vid.Id,
                            SeasonNumber = seasonNum,
                            EpisodeNumber = episodeNum,
                            Title = resolvedTitle,
                            Name = resolvedTitle,
                            Overview = resolvedOverview ?? "",
                            ImageUrl = resolvedImage,
                            StreamUrl = ""
                        };
                        epList.Add(epItem);
                    }

                    if (epList.Count > 0)
                    {
                        Seasons.Add(new SeasonItem
                        {
                            Name = $"Sezon {seasonNum}",
                            SeasonName = $"Sezon {seasonNum}",
                            SeasonNumber = seasonNum,
                            Episodes = epList
                        });
                    }
                }

                SeasonComboBox.ItemsSource = Seasons;
                if (Seasons.Count > 0) SeasonComboBox.SelectedIndex = 0;

                // Sync History & UI
                await RefreshStremioSeriesProgressAsync(series);
                
                System.Diagnostics.Debug.WriteLine($"[Stremio] Series Data Loaded. Seasons: {Seasons.Count}, First Season Episodes: {Seasons.FirstOrDefault()?.Episodes?.Count ?? 0}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Stremio] Error loading series: {ex.Message}");
            }
        }
        
        private async Task RefreshStremioSeriesProgressAsync(Models.Stremio.StremioMediaStream series)
        {
             // Check History for Series
             var lastWatched = HistoryManager.Instance.GetLastWatchedEpisode(series.Meta.Id);
             
             if (lastWatched != null)
             {
                 // Find Episode in existing lists
                 // Assuming Episodes are flattened in 'Seasons' collection which drives UI
                 // Helper to find episode item
                 EpisodeItem targetEp = null;
                 foreach(var s in Seasons) {
                     targetEp = s.Episodes.FirstOrDefault(e => e.Id == lastWatched.Id || (e.SeasonNumber == lastWatched.SeasonNumber && e.EpisodeNumber == lastWatched.EpisodeNumber));
                     if (targetEp != null) break;
                 }
                 
                 if (targetEp != null)
                 {
                     _selectedEpisode = targetEp;
                     
                     // Update UI
                     PlayButtonText.Text = "Devam Et";
                     string subtext = $"S{lastWatched.SeasonNumber:D2}E{lastWatched.EpisodeNumber:D2} - {targetEp.Title}";
                     PlayButtonSubtext.Text = subtext;
                     PlayButtonSubtext.Visibility = Visibility.Visible;
                     StickyPlayButtonText.Text = "Devam Et";
                     
                     // Select in List
                     SeasonComboBox.SelectedItem = Seasons.FirstOrDefault(s => s.SeasonNumber == lastWatched.SeasonNumber);
                     // Allow UI update
                     await Task.Delay(50); 
                     
                     _isProgrammaticSelection = true;
                     if (EpisodesListView != null) EpisodesListView.SelectedItem = targetEp;
                     _isProgrammaticSelection = false;
                     
                     // Pre-buffer
                     _streamUrl = lastWatched.StreamUrl;
                     InitializePrebufferPlayer(_streamUrl, lastWatched.Position);
                     _ = UpdateTechnicalBadgesAsync(_streamUrl);
                 }
             }
             else
             {
                  PlayButtonText.Text = "Bölüm Seçin";
                  PlayButtonSubtext.Visibility = Visibility.Collapsed;
                  StickyPlayButtonText.Text = "Bölüm Seçin";
                  
                  // No episode selected: Hide badges
                  MetadataShimmer.Visibility = Visibility.Collapsed;
                  TechBadgesContent.Visibility = Visibility.Collapsed;
              }
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
                      await PerformHandoverAndNavigate(ep.StreamUrl, ep.Title, ep.Id, parentId, _item.Title, ep.SeasonNumber);
                 }
             }
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            // STREMIO LOGIC
            if (_item is Models.Stremio.StremioMediaStream stremioItem)
            {
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
                     await PerformHandoverAndNavigate(_streamUrl, title, videoId);
                     return;
                }
                
                // Otherwise show sources or auto-play
                if (stremioItem.Meta.Type == "movie")
                {
                    await PlayStremioContent(stremioItem.Meta.Id, showGlobalLoading: false, autoPlay: true);
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
                     await PerformHandoverAndNavigate(_streamUrl, _selectedEpisode.Title, _selectedEpisode.Id, parentId, _item.Title, _selectedEpisode.SeasonNumber);
                }
                else if (_item is LiveStream live)
                {
                    // Movie / Live
                    await PerformHandoverAndNavigate(_streamUrl, live.Title, live.StreamId.ToString());
                }
                else
                {
                    // Fallback
                    await PerformHandoverAndNavigate(_streamUrl, TitleText.Text);
                }
            }
        }
        
        private async Task PerformHandoverAndNavigate(string url, string title, string id = null, string parentId = null, string seriesName = null, int season = 0, int episode = 0, double startSeconds = -1)
        {
            _isHandoffInProgress = true;
            // Handoff Logic
            try
            {
                // Ensure Handoff is set
                App.HandoffPlayer = MediaInfoPlayer;
                
                // CRITICAL SHAKE: Unpause!
                if (MediaInfoPlayer != null)
                {
                    Debug.WriteLine($"[MediaInfoPage:Handoff] Player State BEFORE: Pause={await MediaInfoPlayer.GetPropertyAsync("pause")}, Mute={await MediaInfoPlayer.GetPropertyAsync("mute")}");
                    await MediaInfoPlayer.SetPropertyAsync("pause", "no");
                    Debug.WriteLine($"[MediaInfoPage:Handoff] Player State AFTER: Pause={await MediaInfoPlayer.GetPropertyAsync("pause")}");
                    
                    MediaInfoPlayer.EnableHandoffMode();
                
                    // Detach from parent (ContentControl or Panel)
                    var parent = MediaInfoPlayer.Parent;
                    if (parent is Panel p) p.Children.Remove(MediaInfoPlayer);
                    else if (parent is ContentControl cc) cc.Content = null;
                    Debug.WriteLine("[MediaInfoPage:Handoff] Detached from visual tree.");
                }
                
                // Navigate
                Debug.WriteLine($"[MediaInfoPage:Handoff] Navigating to PlayerPage for {url}");
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title, id, parentId, seriesName, season, episode, startSeconds));
            }
            catch
            {
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title, id, parentId, seriesName, season, episode, startSeconds));
            }
        }
        
        private async Task PlayStremioContent(string videoId, bool showGlobalLoading = true, bool autoPlay = false)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return;

            if (_currentStremioVideoId == videoId)
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

                    var firstAddon = _addonResults.FirstOrDefault(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0);
                    if (firstAddon != null && AddonSelectorList.SelectedItem == null)
                    {
                        AddonSelectorList.SelectedItem = firstAddon;
                    }

                    if (autoPlay)
                    {
                        var firstStream = firstAddon?.Streams?.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));
                        if (firstStream != null)
                        {
                            Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(firstStream.Url, _item.Title, videoId));
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
                            string addonDisplayName = manifest?.Name ?? baseUrl.Replace("https://", "").Replace("http://", "").Split('/')[0];
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
                                        OriginalStream = s
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
                                                    Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(firstStream.Url, _item.Title, videoId));
                                                    return;
                                                }
                                            }

                                            // If nothing selected (and not just placeholder), select this
                                            if (AddonSelectorList.SelectedIndex == -1 || (AddonSelectorList.SelectedItem as StremioAddonViewModel)?.IsLoading == true)
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
                OriginalStream = source.OriginalStream
            };
        }

        private sealed class StremioSourcesCacheEntry
        {
            public List<StremioAddonViewModel> Addons { get; set; } = new();
            public bool IsComplete { get; set; }
        }

        private void ShowSourcesPanel(bool show)
        {
            // Determine which panel to show based on width
            bool isWide = _isWideModeIndex == 1;
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
            }
        }

        private void SourcesListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is StremioStreamViewModel vm)
            {
                string title = _selectedEpisode?.Title ?? _item.Title;
                string videoId = _selectedEpisode?.Id ?? (_item as Models.Stremio.StremioMediaStream).Meta.Id;

                if (!string.IsNullOrEmpty(vm.Url))
                {
                    _streamUrl = vm.Url; // Save for return/reuse
                    // [FIX] Direct Navigation for Stremio Sources (No Handoff)
                    // We cannot use Handoff because MediaInfoPlayer has not pre-buffered this specific URL.
                    // Doing Handoff would pass an empty/uninitialized player to PlayerPage.
                    Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(vm.Url, title, videoId));
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

        private void RestartButton_Click(object sender, RoutedEventArgs e)
        {
             if (!string.IsNullOrEmpty(_streamUrl))
            {
                // Series Episode
                if (_selectedEpisode != null)
                {
                     string parentId = _item is SeriesStream ss ? ss.SeriesId.ToString() : null;
                     PerformHandoverAndNavigate(_streamUrl, _selectedEpisode.Title, _selectedEpisode.Id, parentId, _item.Title, _selectedEpisode.SeasonNumber, 0); // Episode 0? logic needs check
                     // Actually episode argument is usually integer order.
                     // The last args are: string seriesName = null, int season = 0, int episode = 0
                     // But _selectedEpisode doesn't store 'episode number'. CurrentEpisodes list index?
                     // Let's check PerformHandoverAndNavigate definition.
                     // line 559: PerformHandoverAndNavigate(string url, string title, string id = null, string parentId = null, string seriesName = null, int season = 0, int episode = 0)
                     
                     // We need to force PlayerPage to start from 0. 
                     // PlayerPage automatically checks history on load.
                     // To FORCE restart, we might need a flag or clear history.
                     // A better way is to clear history for this ID before navigating.
                     // RESTART: Explicitly pass startSeconds: 0
                     HistoryManager.Instance.UpdateProgress(_selectedEpisode.Id, _selectedEpisode.Title, _streamUrl, 0, 0, parentId, _item.Title, _selectedEpisode.SeasonNumber);
                     PerformHandoverAndNavigate(_streamUrl, _selectedEpisode.Title, _selectedEpisode.Id, parentId, _item.Title, _selectedEpisode.SeasonNumber, 0, 0);
                }
                else if (_item is LiveStream live)
                {
                    // Update History to 0
                    HistoryManager.Instance.UpdateProgress(live.StreamId.ToString(), live.Title, live.StreamUrl, 0, 0, null, null, 0, 0);
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

        // Removed local StartDownload logic in favor of global DownloadManager

        private void TrailerButton_Click(object sender, RoutedEventArgs e) { /* ... */ }
        private void BackButton_Click(object sender, RoutedEventArgs e) 
        { 
            ElementSoundPlayer.Play(ElementSoundKind.GoBack);
            if (Frame.CanGoBack) Frame.GoBack(); 
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            
            // Cancel any active probe
            try { 
                _probeCts?.Cancel(); 
                _probeCts?.Dispose(); 
            } catch { }
            _probeCts = null; // Added null assignment for CTS

            // Stop Slideshow
            if (_slideshowTimer != null)
            {
                _slideshowTimer.Stop();
                _slideshowTimer = null;
            }

            if (MediaInfoPlayer != null && !_isHandoffInProgress) 
            {
                 // Stop playback but keep the player instance alive for Cache reuse.
                 // CleanupAsync() destroys the MpvContext, which might cause AV if re-initialized on the same control instance improperly.
                 _ = MediaInfoPlayer.ExecuteCommandAsync("stop");
                 MediaInfoPlayer.DisableHandoffMode();
            }
        }



        #endregion

        private void SetBadgeLoadingState(bool isLoading)
        {
            if (MetadataShimmer == null || TechBadgesContent == null) return;

            System.Diagnostics.Debug.WriteLine($"[TechBadges] SetBadgeLoadingState: {isLoading}");

            if (isLoading)
            {
                // Loading: Show Shimmer, Hide Badges
                if (TechBadgeSection != null) TechBadgeSection.Visibility = Visibility.Visible;
                MetadataShimmer.Width = double.NaN;
                MetadataShimmer.Visibility = Visibility.Visible;
                ElementCompositionPreview.GetElementVisual(MetadataShimmer).Opacity = 1f;

                TechBadgesContent.Visibility = Visibility.Collapsed;
                ElementCompositionPreview.GetElementVisual(TechBadgesContent).Opacity = 0f;
            }
            else
            {
                // Loaded: Cross-fade to Badges
                System.Diagnostics.Debug.WriteLine("[TechBadges] Revealing Content...");

                // 1. Fade In Badges
                TechBadgesContent.Visibility = Visibility.Visible;
                var visContent = ElementCompositionPreview.GetElementVisual(TechBadgesContent);
                visContent.Opacity = 0f; // Start at 0 on Composition layer

                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f); // Explicit start
                fadeIn.InsertKeyFrame(1f, 1f); // Explicit end
                fadeIn.Duration = TimeSpan.FromMilliseconds(400);
                
                // IMPORTANT: Start animation
                visContent.StartAnimation("Opacity", fadeIn);
                
                // Ensure Logical Opacity is 1 so XAML doesn't cull it if animation fails/finishes
                TechBadgesContent.Opacity = 1;
                visContent.StartAnimation("Opacity", fadeIn);

                // 2. Fade Out Shimmer
                var visShimmer = ElementCompositionPreview.GetElementVisual(MetadataShimmer);
                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, 1f);
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromMilliseconds(300);
                
                var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                visShimmer.StartAnimation("Opacity", fadeOut);
                batch.Completed += (s, e) => 
                { 
                    System.Diagnostics.Debug.WriteLine("[TechBadges] Shimmer Hidden.");
                    MetadataShimmer.Visibility = Visibility.Collapsed;
                    MetadataShimmer.Width = double.NaN;
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
            if (string.IsNullOrEmpty(url)) return;

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
                        bool hasVisibleBadges = Badge4K.Visibility == Visibility.Visible || 
                                                BadgeRes.Visibility == Visibility.Visible || 
                                                BadgeHDR.Visibility == Visibility.Visible || 
                                                BadgeCodecContainer.Visibility == Visibility.Visible;

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
                    if (TechBadgeSection != null) TechBadgeSection.Visibility = Visibility.Collapsed;
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

            if (!is4K && !string.IsNullOrEmpty(result.Resolution) && result.Resolution != "Unknown" && result.Resolution != "Error")
            {
                // Show resolution badge (e.g. 1080P)
                string displayRes = result.Resolution;
                if (displayRes.Contains("x"))
                {
                    var h = displayRes.Split('x').LastOrDefault();
                    if (h != null) displayRes = h + "P";
                }
                BadgeResText.Text = displayRes.ToUpperInvariant();
                BadgeRes.Visibility = Visibility.Visible;
            }
            else
            {
                BadgeRes.Visibility = Visibility.Collapsed;
            }

            // HDR / SDR
            BadgeHDR.Visibility = result.IsHdr ? Visibility.Visible : Visibility.Collapsed;
            BadgeSDR.Visibility = !result.IsHdr ? Visibility.Visible : Visibility.Collapsed;

            // Codec
            if (!string.IsNullOrEmpty(result.Codec) && result.Codec != "-")
            {
                BadgeCodec.Text = result.Codec;
                BadgeCodecContainer.Visibility = Visibility.Visible;
            }
            else
            {
                BadgeCodecContainer.Visibility = Visibility.Collapsed;
            }

            // Disable dynamic shimmer adjustment: We are cross-fading, not morphing.
            // AdjustMetadataShimmer();
            
            // Final Width Sync if cached - handled by main update loop now
            /*if (TechBadgesContent.ActualWidth > 0)
            {
                MetadataShimmer.Width = TechBadgesContent.ActualWidth;
            }*/
        }





        private DispatcherTimer _slideshowTimer;
        private List<string> _slideshowImages;
        private int _slideshowIndex = 0;

        private void StartBackgroundSlideshow(List<string> images)
        {
            if (images == null || images.Count == 0 || HeroImage == null) return;

            // Stop existing timer
            if (_slideshowTimer != null)
            {
                _slideshowTimer.Stop();
                _slideshowTimer = null;
            }

            _slideshowImages = images;
            _slideshowIndex = 0;

            System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Starting with {images.Count} images. Interval: 8s");

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
                }
                catch { }
            }
            else
            {
                // We have an existing image (Poster).
                // Do NOT crossfade immediately. Let the poster linger.
                // The timer will handle the transition to the first backdrop.
                
                // IMPORTANT: Since we want the FIRST backdrop (index 0) to be the first one shown 
                // when the timer ticks, we need to set the index so that (index + 1) % count == 0.
                // Thus, set index to -1.
                _slideshowIndex = -1;
            }

            // If only 1 image, don't run timer
            if (images.Count <= 1) return;

            // 2. Setup Timer for cycling
            _slideshowTimer = new DispatcherTimer();
            _slideshowTimer.Interval = TimeSpan.FromSeconds(8);
            _slideshowTimer.Tick += (s, e) =>
            {
                if (HeroImage == null || HeroImage2 == null || _slideshowImages == null || _slideshowImages.Count == 0)
                {
                    _slideshowTimer?.Stop();
                    return;
                }

                _slideshowIndex = (_slideshowIndex + 1) % _slideshowImages.Count;
                string nextImg = _slideshowImages[_slideshowIndex];

                // Crossfade Logic:
                // 1. Load Next Image into HeroImage2 (which is Opacity 0)
                try { HeroImage2.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(nextImg)); } catch { return; }

                // 2. Animate Crossfade
                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f);
                fadeIn.InsertKeyFrame(1f, 1f);
                fadeIn.Duration = TimeSpan.FromSeconds(1.2);

                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, 1f);
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromSeconds(1.2);

                var visual1 = ElementCompositionPreview.GetElementVisual(HeroImage);  // Current Visible
                var visual2 = ElementCompositionPreview.GetElementVisual(HeroImage2); // New Incoming

                // Ensure XAML Opacity is 1 so Composition works
                HeroImage.Opacity = 1; 
                HeroImage2.Opacity = 1; 
                // We control actual visibility via Composition Opacity
                
                // Start Animations
                visual2.Opacity = 0; 
                visual2.StartAnimation("Opacity", fadeIn); // Fade In New
                visual1.StartAnimation("Opacity", fadeOut); // Fade Out Old

                // 3. Cleanup after animation (Swap sources to keep HeroImage as primary)
                DispatcherQueue.TryEnqueue(async () => 
                {
                    await Task.Delay(1300); // Wait for animation
                    if (HeroImage != null && HeroImage2 != null)
                    {
                        // Set Primary to New Image
                        HeroImage.Source = HeroImage2.Source;
                        
                        // Reset Opacities (Visual layer)
                        visual1.Opacity = 1; // Primary Visible
                        visual2.Opacity = 0; // Secondary Hidden
                    }
                });
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
            var actionButtons = new Button[] { DownloadButton, TrailerButton, CopyLinkButton, RestartButton };
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
            var visual = ElementCompositionPreview.GetElementVisual(content);

            content.SizeChanged += (s, e) => {
                visual.CenterPoint = new Vector3((float)content.ActualWidth / 2, (float)content.ActualHeight / 2, 0);
            };

            btn.PointerEntered += (s, e) => {
                var pulse = _compositor.CreateVector3KeyFrameAnimation();
                pulse.InsertKeyFrame(0.2f, new Vector3(0.85f, 0.85f, 1f));
                pulse.InsertKeyFrame(0.6f, new Vector3(1.25f, 1.25f, 1f));
                pulse.InsertKeyFrame(1f, new Vector3(1.15f, 1.15f, 1f));
                pulse.Duration = TimeSpan.FromMilliseconds(500);
                visual.StartAnimation("Scale", pulse);
            };

            btn.PointerExited += (s, e) => {
                var reset = _compositor.CreateSpringVector3Animation();
                reset.FinalValue = new Vector3(1f, 1f, 1f);
                reset.DampingRatio = 0.5f;
                reset.Period = TimeSpan.FromMilliseconds(40);
                visual.StartAnimation("Scale", reset);
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
        
        // Progress UI
        public bool HasProgress { get; set; }
        public double ProgressPercent { get; set; }
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







