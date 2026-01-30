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

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage : Page
    {
        private IMediaStream _item;
        private Compositor _compositor;
        private string _streamUrl;
        
        // Series Data
        public ObservableCollection<SeasonItem> Seasons { get; private set; } = new();
        public ObservableCollection<EpisodeItem> CurrentEpisodes { get; private set; } = new();
        public ObservableCollection<CastItem> CastList { get; private set; } = new();

        private EpisodeItem _selectedEpisode;
        private SeasonItem _selectedSeason;
        private TmdbMovieResult _cachedTmdb;
        
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
        }

        private int _isWideModeIndex = -1; // -1: undefined, 0: narrow, 1: wide
        private bool _isSelectionSyncing = false;

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
            System.Diagnostics.Debug.WriteLine($"[LayoutDebug] SyncWideHeights: Grid={targetHeight}, Panel={EpisodesPanel?.Height}, ListMax={EpisodesListView?.MaxHeight}");
        }

        private void UpdateLayoutState(bool isWide)
        {
            try
            {
                if (_item == null) return; // Data not loaded yet

                bool isSeries = _item is SeriesStream;
                System.Diagnostics.Debug.WriteLine($"[LayoutDebug] UpdateLayoutState START. Wide: {isWide}, Series: {isSeries}");

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

                // Load History
                await HistoryManager.Instance.InitializeAsync();

                if (e.Parameter is MediaNavigationArgs args)
                {
                    _item = args.Stream;
                    await LoadDetailsAsync(args.Stream, args.TmdbInfo);
                }
                else if (e.Parameter is IMediaStream item)
                {
                    _item = item;
                    await LoadDetailsAsync(item);
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
            SetLoadingState(true); 
            SetBadgeLoadingState(true); // Explicitly reset badges to loading state
            
            // Setup Alive Buttons (Micro-interactions)
            SetupButtonInteractions(PlayButton, RestartButton, TrailerButton, DownloadButton, CopyLinkButton, StickyPlayButton);
            
            // Setup Sticky Header Scroll Logic
            SetupStickyScroller();

            // Start timer in parallel (Short delay if cached, longer if new to allow layout settlement)
            // Start timer in parallel (Short delay if cached, longer if new to allow layout settlement)
            // var aestheticDelayTask = Task.Delay(hasBasicData ? 50 : 400); // REMOVED: Unused and unnecessary blocking feeling

            TitleText.Text = item.Title;
            StickyTitle.Text = item.Title; 

            if (!string.IsNullOrEmpty(item.PosterUrl))
            {
                HeroImage.Opacity = 0;
                HeroImage.Source = new BitmapImage(new Uri(item.PosterUrl));
                await Task.Delay(50);
                HeroImage.Opacity = 1; 
                StartHeroConnectedAnimation();
                ApplyPremiumAmbience(item.PosterUrl);
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
                TitlePanel.Opacity = 0.01;
                OverviewPanel.Opacity = 0.01; 
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

                // For non-series, trigger technical probe
                if (item is LiveStream live)
                {
                    _ = UpdateTechnicalBadgesAsync(live.StreamUrl);
                }

                // Initial Cast Shimmer (Standard 4 items)
                AdjustCastShimmer(4);

                // Fetch Deep Details (Runtime, Genres)
                var details = await TmdbHelper.GetDetailsAsync(_cachedTmdb.Id, item is SeriesStream);
                if (details != null)
                {
                    RuntimeText.Text = (item is SeriesStream) ? "Dizi" : $"{details.Runtime / 60}sa {details.Runtime % 60}dk";
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
                // Fetch Cast
                try
                {
                    var credits = await TmdbHelper.GetCreditsAsync(_cachedTmdb.Id, item is SeriesStream);
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
            }

            // Determine Stream Type Logic
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
                                bool isGeneric = string.IsNullOrEmpty(epName) || epName.Contains("Bölüm") || epName.Contains("Episode") || epName == ep.EpisodeNumber.ToString();

                                if (isGeneric && !string.IsNullOrEmpty(cleanIptv) && cleanIptv.Length > 2)
                                {
                                    epName = cleanIptv;
                                }
                                else if (string.IsNullOrEmpty(epName))
                                {
                                    epName = $"Bölüm {ep.EpisodeNumber}";
                                }

                                TitleText.Text = epName;
                                // Series name as sub-title
                                
                                if (!string.IsNullOrEmpty(ep.Overview))
                                {
                                    OverviewText.Text = ep.Overview;
                                    AdjustOverviewShimmer(ep.Overview);
                                }
                                
                                // Update Hero Image to episode still if available
                                // REMOVED: User prefers Series Backdrop/Slideshow over low-res episode stills
                                // if (!string.IsNullOrEmpty(ep.StillUrl))
                                // {
                                //    HeroImage.Source = new BitmapImage(new Uri(TmdbHelper.GetImageUrl(ep.StillUrl)));
                                // }
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
                
                // History Check (Resuming Movie?)
                var history = HistoryManager.Instance.GetProgress(live.StreamId.ToString());
                if (history != null && !history.IsFinished && history.Duration > 0)
                {
                    double percent = (history.Position / history.Duration) * 100;
                    if (percent > 5 && percent < 95)
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
                        PlayButtonText.Text = "Oynat";
                        PlayButtonSubtext.Visibility = Visibility.Collapsed;
                        StickyPlayButtonText.Text = "Oynat";
                        StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
                        RestartButton.Visibility = Visibility.Collapsed;
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

                // SLIDESHOW (MOVIE)
                bool startedSlideshow = false;
                System.Diagnostics.Debug.WriteLine("[SLIDESHOW] Checking availability...");
                
                if (_cachedTmdb != null && _cachedTmdb.Images?.Backdrops != null && _cachedTmdb.Images.Backdrops.Count > 0)
                {
                    // Extract URLs
                    var backdrops = _cachedTmdb.Images.Backdrops.Select(i => TmdbHelper.GetImageUrl(i.FilePath, "original")).Take(10).ToList();
                    if (backdrops.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SLIDESHOW] Starting with {backdrops.Count} TMDB backdrops.");
                        StartBackgroundSlideshow(backdrops);
                        startedSlideshow = true;
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[SLIDESHOW] No TMDB Backdrops found.");
                }
                
                if (!startedSlideshow && _item is LiveStream ls && !string.IsNullOrEmpty(ls.IconUrl))
                {
                    // Single Image fallback (IPTV Cover)
                    // Only if we haven't started a TMDB slideshow
                    System.Diagnostics.Debug.WriteLine("[SLIDESHOW] Falling back to Series Cover.");
                    StartBackgroundSlideshow(new List<string> { ls.IconUrl });
                }

                InitializePrebufferPlayer(_streamUrl, history?.Position ?? 0);
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
                
                // 1. Bloom Effect (Breathing)
                // 1. Bloom Effect (Breathing) with Higher Opacity
                BloomColorStop.Color = Color.FromArgb(160, primary.R, primary.G, primary.B); 
                
                var bloomVisual = ElementCompositionPreview.GetElementVisual(AmbientBloom);
                var breathe = _compositor.CreateScalarKeyFrameAnimation();
                breathe.InsertKeyFrame(0f, 0f);   
                breathe.InsertKeyFrame(0.5f, 0.9f); // Pulse stronger
                breathe.InsertKeyFrame(1f, 0.6f); // Settle higher
                breathe.Duration = TimeSpan.FromSeconds(4);
                
                // Then continuous breathing
                var loopBreathe = _compositor.CreateScalarKeyFrameAnimation();
                loopBreathe.InsertKeyFrame(0f, 0.6f);
                loopBreathe.InsertKeyFrame(0.5f, 0.85f);
                loopBreathe.InsertKeyFrame(1f, 0.6f);
                loopBreathe.Duration = TimeSpan.FromSeconds(8);
                loopBreathe.IterationBehavior = AnimationIterationBehavior.Forever;

                // Initial fade in
                var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                bloomVisual.StartAnimation("Opacity", breathe);
                batch.Completed += (s, e) => bloomVisual.StartAnimation("Opacity", loopBreathe);
                batch.End();

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
                                    bool isGeneric = string.IsNullOrEmpty(match.Name) || match.Name.Contains("Bölüm") || match.Name.Contains("Episode") || match.Name == epNum.ToString();
                                    
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
                    EpisodesListView.SelectedItem = _pendingAutoSelectEpisode;
                    if (NarrowEpisodesListView != null)
                    {
                        NarrowEpisodesListView.SelectedItem = _pendingAutoSelectEpisode;
                        NarrowEpisodesListView.ScrollIntoView(_pendingAutoSelectEpisode);
                    }
                    EpisodesListView.ScrollIntoView(_pendingAutoSelectEpisode);
                    _pendingAutoSelectEpisode = null;
                }
                else if (CurrentEpisodes.Count > 0)
                {
                    // Select first by default if no history
                     EpisodesListView.SelectedItem = CurrentEpisodes[0];
                     if (NarrowEpisodesListView != null)
                         NarrowEpisodesListView.SelectedItem = CurrentEpisodes[0];
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

                     // TRIGGER TECHNICAL PROBE
                     _ = UpdateTechnicalBadgesAsync(ep.StreamUrl);
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
                
                // Ensure center point for center-scaling
                btn.SizeChanged += (s, e) => 
                {
                    visual.CenterPoint = new Vector3((float)btn.ActualWidth / 2f, (float)btn.ActualHeight / 2f, 0);
                };

                // Use AddHandler to capture events handled by Button
                btn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
                {
                    var scale = _compositor.CreateVector3KeyFrameAnimation();
                    scale.InsertKeyFrame(1f, new Vector3(0.94f, 0.94f, 1f));
                    scale.Duration = TimeSpan.FromMilliseconds(50);
                    visual.StartAnimation("Scale", scale);
                }), true);

                btn.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, e) =>
                {
                    var spring = _compositor.CreateSpringVector3Animation();
                    spring.FinalValue = new Vector3(1f, 1f, 1f);
                    spring.DampingRatio = 0.4f; // Bouncy
                    spring.Period = TimeSpan.FromMilliseconds(50);
                    visual.StartAnimation("Scale", spring);
                    
                    // Audio Feedback: ONLY for BackButton (if passed) or specifically requested
                    // The user requested NO sounds for player buttons, only BackButton.
                    // BackButton is not typically in this list (it's hardcoded in XAML)
                    // So we REMOVE all automatic sound playing here.
                }), true);
                
                btn.PointerExited += (s, e) =>
                {
                     // Reset if dragged out
                    var spring = _compositor.CreateSpringVector3Animation();
                    spring.FinalValue = new Vector3(1f, 1f, 1f);
                    spring.DampingRatio = 0.6f;
                    visual.StartAnimation("Scale", spring);
                };
            }
        }

        private void InitializePrebufferPlayer(string url, double startTime)
        {
            // Similar logic to previous, but generalized
            if (string.IsNullOrEmpty(url)) return;

             _ = Task.Run(async () => 
             {
                 DispatcherQueue.TryEnqueue(async () => 
                 {
                     try
                     {
                         PlayerOverlayContainer.Visibility = Visibility.Visible;
                         PlayerOverlayContainer.Opacity = 0; // Hidden
                         
                         MediaInfoPlayer.ApplyTemplate();
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

                         await MediaInfoPlayer.SetPropertyAsync("pause", "yes");

                         if (startTime > 0)
                         {
                             // Use 'start' property instead of seeking after open to avoid "property unavailable" errors
                             await MediaInfoPlayer.SetPropertyAsync("start", startTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
                         }

                         await MediaInfoPlayer.OpenAsync(url);
                         
                         // Get Tech INFO after short delay (once loaded)
                         _ = ExtractTechInfoAsync();
                     }
                     catch { }
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

        private void EpisodePlayButton_Click(object sender, RoutedEventArgs e)
        {
             if (sender is Button btn && btn.Tag is EpisodeItem ep)
             {
                 // Ensure this episode is selected
                 _selectedEpisode = ep;
                 EpisodesListView.SelectedItem = ep;
                 
                 // Play Logic
                 if (_item is SeriesStream ss)
                 {
                      string parentId = ss.SeriesId.ToString();
                      PerformHandoverAndNavigate(ep.StreamUrl, ep.Title, ep.Id, parentId, _item.Title, ep.SeasonNumber);
                 }
             }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_streamUrl))
            {
                // Series Episode
                if (_selectedEpisode != null)
                {
                     string parentId = _item is SeriesStream ss ? ss.SeriesId.ToString() : null;
                     PerformHandoverAndNavigate(_streamUrl, _selectedEpisode.Title, _selectedEpisode.Id, parentId, _item.Title, _selectedEpisode.SeasonNumber);
                }
                else if (_item is LiveStream live)
                {
                    // Movie / Live
                    PerformHandoverAndNavigate(_streamUrl, live.Title, live.StreamId.ToString());
                }
                else
                {
                    // Fallback
                    PerformHandoverAndNavigate(_streamUrl, TitleText.Text);
                }
            }
        }
        
        private void PerformHandoverAndNavigate(string url, string title, string id = null, string parentId = null, string seriesName = null, int season = 0, int episode = 0, double startSeconds = -1)
        {
            // Handoff Logic
            try
            {
                // Ensure Handoff is set
                App.HandoffPlayer = MediaInfoPlayer;
                MediaInfoPlayer.EnableHandoffMode();
                
                // Detach from parent (ContentControl or Panel)
                var parent = MediaInfoPlayer.Parent;
                if (parent is Panel p) p.Children.Remove(MediaInfoPlayer);
                else if (parent is ContentControl cc) cc.Content = null;
                
                // Navigate
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title, id, parentId, seriesName, season, episode, startSeconds));
            }
            catch
            {
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title, id, parentId, seriesName, season, episode, startSeconds));
            }
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

            if (MediaInfoPlayer != null && App.HandoffPlayer != MediaInfoPlayer) 
            {
                 _ = MediaInfoPlayer.ExecuteCommandAsync("stop");
                 MediaInfoPlayer.DisableHandoffMode();
                 _ = MediaInfoPlayer.CleanupAsync();
                 // Added Dispose call for MediaInfoPlayer
                 // MediaInfoPlayer.Dispose(); // REMOVED: MpvPlayer does not support Dispose
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

                // Probe returns tuple: (Res, Fps, Codec, Bitrate, Success, IsHdr)
                // Cache update is already handled inside FFmpegProber.ProbeAsync -> WAIT, it wasn't. It was handled in ExpandedCard but here it seems missing?
                // Checking previous code... 
                // In ExpandedCard: Services.ProbeCacheService.Instance.Update(...) was called.
                // In MediaInfoPage (lines 2063+ in original): It just calls _ffprober.ProbeAsync.
                // Let's look at line 1533 in Pre-buffer logic: It DOES call Update.
                // But specifically inside UpdateTechnicalBadgesAsync it seems it DID NOT call Update in previous version?
                // No, I need to check if _ffprober internally updates cache? No, it doesn't.
                // So I SHOULD add cache update here too for consistency!
                
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
                        // If Content is smaller than Shimmer, keep the container at Shimmer's width.
                        // This prevents the "Year" text from jumping to the left.
                        if (shimmerWidth > 0 && TechBadgesContent.ActualWidth < shimmerWidth)
                        {
                            TechBadgesContent.MinWidth = shimmerWidth;
                        }
                        else
                        {
                            TechBadgesContent.MinWidth = 0; // Reset if content is wider, let it expand
                        }
                        
                        // Sync Shimmer to Content only if Content is WIDER (to cover it)
                        // Otherwise leave Shimmer as is (wider than content) to mask the gap during fade
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
               // Ensure we exit loading state so text/layout doesn't stay hidden/ghosted if we decide to show fallback
               DispatcherQueue.TryEnqueue(() => SetBadgeLoadingState(false));
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


}
namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }
