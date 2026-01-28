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

            // Start 500ms timer in parallel with initial populating
            var aestheticDelayTask = Task.Delay(500);

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
                // MaxLines already set earlier for layout sync
                
                await LoadSeriesDataAsync(series);
            }
            else if (item is LiveStream live)
            {
                // MOVIE / LIVE MODE
                _streamUrl = live.StreamUrl;
                EpisodesPanel.Visibility = Visibility.Collapsed;
                PlayButtonText.Text = "Oynat";
                
                // History Check (Resuming Movie?)
                var history = HistoryManager.Instance.GetProgress(live.StreamId.ToString());
                if (history != null && !history.IsFinished && history.Duration > 0)
                {
                    double percent = (history.Position / history.Duration) * 100;
                    if (percent > 5 && percent < 95)
                    {
                        PlayButtonText.Text = "Devam Et";
                        PlayButtonText.Text = "Devam Et";
                        PlayButtonSubtext.Visibility = Visibility.Visible;
                        RestartButton.Visibility = Visibility.Visible;
                        
                        var remaining = TimeSpan.FromSeconds(history.Duration - history.Position);
                        // Format: "23dk Kaldı" or "1sa 12dk Kaldı"
                        PlayButtonSubtext.Text = remaining.TotalHours >= 1 
                            ? $"{remaining.Hours}sa {remaining.Minutes}dk Kaldı"
                            : $"{remaining.Minutes}dk Kaldı";
                    }
                }
                else
                {
                     PlayButtonText.Text = "Oynat";
                     PlayButtonText.Text = "Oynat";
                     PlayButtonSubtext.Visibility = Visibility.Collapsed;
                     RestartButton.Visibility = Visibility.Collapsed;
                }

                InitializePrebufferPlayer(_streamUrl, history?.Position ?? 0);
            }

            // Wait for at least 500ms aesthetic delay to pass before reveal
            await aestheticDelayTask;

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
            // Prepare smooth fade-in for all background effects
            var localBgVisual = ElementCompositionPreview.GetElementVisual(LocalInfoGradient);
            
            var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
            fadeIn.InsertKeyFrame(1f, 1f);
            fadeIn.Duration = TimeSpan.FromSeconds(1); // Cinematic fade
            
            // Execute Fade In (Always)
            localBgVisual.StartAnimation("Opacity", fadeIn);

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
             // 1. Fetch JSON from API
             try
             {
                 var playlistsJson = AppSettings.PlaylistsJson;
                 var lastId = AppSettings.LastPlaylistId;
                 if (string.IsNullOrEmpty(playlistsJson) || lastId == null) return;

                 var playlists = System.Text.Json.JsonSerializer.Deserialize<List<Playlist>>(playlistsJson);
                 var activePlaylist = playlists?.Find(p => p.Id == lastId);
                 if (activePlaylist == null) return;

                 string baseUrl = activePlaylist.Host.TrimEnd('/');
                 string api = $"{baseUrl}/player_api.php?username={activePlaylist.Username}&password={activePlaylist.Password}&action=get_series_info&series_id={series.SeriesId}";
                 
                 var httpClient = HttpHelper.Client;
                 string json = await httpClient.GetStringAsync(api);
                 if (string.IsNullOrEmpty(json)) return;

                 // 2. Parse Seasons
                 Seasons.Clear();
                 CurrentEpisodes.Clear();
                 
                 using (var doc = System.Text.Json.JsonDocument.Parse(json))
                 {
                     var root = doc.RootElement;
                     
                     // 2. Accurate TMDB Identification (Highest Priority)
                     System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] Loading info for series: {series.Name} (ID: {series.SeriesId})");
                     if (root.TryGetProperty("info", out var infoNode))
                     {
                         if (infoNode.TryGetProperty("tmdb_id", out var tProp))
                         {
                             string tidStr = tProp.ValueKind == System.Text.Json.JsonValueKind.String ? tProp.GetString() : tProp.GetRawText();
                             System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] IPTV Provider TMDB_ID: {tidStr}");
                             if (int.TryParse(tidStr, out int tid) && tid > 0)
                             {
                                 if (_cachedTmdb == null || _cachedTmdb.Id != tid)
                                 {
                                     System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] Fetching TMDB details for ID: {tid}");
                                     _cachedTmdb = await TmdbHelper.GetTvByIdAsync(tid);
                                     if (_cachedTmdb != null) System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] TMDB Found: {_cachedTmdb.DisplayTitle}");
                                 }
                             }
                         }
                         else { System.Diagnostics.Debug.WriteLine("[SERIES_DEBUG] No tmdb_id property in 'info' node."); }
                     }
                     else { System.Diagnostics.Debug.WriteLine("[SERIES_DEBUG] No 'info' node found in series JSON."); }

                     if (root.TryGetProperty("episodes", out var episodesNode))
                     {
                         // Properties "1", "2" are seasons
                         foreach (var seasonProp in episodesNode.EnumerateObject())
                         {
                             if (int.TryParse(seasonProp.Name, out int seasonNum))
                             {
                                 var seasonEpisodes = new List<EpisodeItem>();
                                 
                                 // Fetch TMDB Season Info (if available) - Do this once per season
                                 TmdbSeasonDetails tmdbSeason = null;
                                 if (_cachedTmdb != null)
                                 {
                                     System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] Fetching TMDB Season {seasonNum} for {_cachedTmdb.DisplayTitle}");
                                     tmdbSeason = await TmdbHelper.GetSeasonDetailsAsync(_cachedTmdb.Id, seasonNum);
                                     if (tmdbSeason != null) System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] Found TMDB Season {seasonNum} with {tmdbSeason.Episodes?.Count ?? 0} episodes.");
                                 }

                                 foreach(var ep in seasonProp.Value.EnumerateArray())
                                 {
                                     string id = ep.GetProperty("id").GetString();
                                     string container = ep.GetProperty("container_extension").GetString();
                                     string title = ep.GetProperty("title").GetString();
                                     // Try to get Episode Number from JSON to match with TMDB
                                     int epNum = 0;
                                     if (ep.TryGetProperty("episode_num", out var en)) 
                                     {
                                         if (en.ValueKind == System.Text.Json.JsonValueKind.Number) epNum = en.GetInt32();
                                         else int.TryParse(en.GetString(), out epNum);
                                     }
                                     
                                     // Match TMDB
                                     if (tmdbSeason != null && tmdbSeason.Episodes != null)
                                     {
                                         var match = tmdbSeason.Episodes.FirstOrDefault(x => x.EpisodeNumber == epNum);
                                         if (match != null)
                                         {
                                             System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] MATCH SUCCESS: S{seasonNum}E{epNum} -> {match.Name}");
                                                                                          // Determine best title (TMDB name vs IPTV cleaned name)
                                             string cleanIptv = TmdbHelper.CleanEpisodeTitle(title);
                                             bool isGeneric = string.IsNullOrEmpty(match.Name) || match.Name.Contains("Bölüm") || match.Name.Contains("Episode") || match.Name == epNum.ToString();
                                             
                                             if (isGeneric && !string.IsNullOrEmpty(cleanIptv) && cleanIptv.Length > 2)
                                             {
                                                 title = cleanIptv;
                                                 System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] MATCH (GENERIC TMDB): Using IPTV name: {title}");
                                             }
                                             else
                                             {
                                                 title = match.Name;
                                                 System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] MATCH SUCCESS: {title}");
                                             }

                                         }
                                         else 
                                         {
                                                                                          title = TmdbHelper.CleanEpisodeTitle(title);
                                             System.Diagnostics.Debug.WriteLine($"[SERIES_DEBUG] MATCH FAIL: Using Cleaned IPTV Title -> {title}");

                                         }
                                     }

                                     string finalUrl = $"{baseUrl}/series/{activePlaylist.Username}/{activePlaylist.Password}/{id}.{container}";
                                     
                                     // Metadata
                                     string thumb = null;
                                     if (ep.TryGetProperty("info", out var info) && info.TryGetProperty("movie_image", out var img))
                                         thumb = img.GetString();
                                     if (string.IsNullOrEmpty(thumb)) thumb = series.Cover;

                                     // History Check
                                     var hist = HistoryManager.Instance.GetProgress(id); // Using ID as key
                                     string progText = "";
                                     bool hasProg = false;
                                     double pct = 0;
                                     if (hist != null && hist.Duration > 0)
                                     {
                                         pct = (hist.Position / hist.Duration) * 100;
                                         hasProg = pct > 1; // 1%
                                         progText = $"{TimeSpan.FromSeconds(hist.Duration - hist.Position).TotalMinutes:F0}dk Kaldı";
                                     }

                                     seasonEpisodes.Add(new EpisodeItem 
                                     {
                                         Id = id,
                                         Title = title,
                                         Container = container,
                                         StreamUrl = finalUrl,
                                         ImageUrl = thumb,
                                         HasProgress = hasProg,
                                         ProgressPercent = pct,
                                         ProgressText = progText,
                                         Duration = "24m", // Placeholder
                                         SeasonNumber = seasonNum
                                     });
                                 }
                                 
                                 Seasons.Add(new SeasonItem 
                                 { 
                                     SeasonNumber = seasonNum, 
                                     Name = $"Sezon {seasonNum}",
                                     Episodes = seasonEpisodes 
                                 });
                             }
                         }
                     }
                 }

                 // 3. Select Initial Season (Logic: Continue Watching)
                 // Check history for LAST watched episode of this series
                 var lastWatched = HistoryManager.Instance.GetLastWatchedEpisode(series.SeriesId.ToString());
                 
                 SeasonItem targetSeason = Seasons.FirstOrDefault();
                 EpisodeItem targetEpisode = null;

                 if (lastWatched != null)
                 {
                     // Find season containing this episode
                     targetSeason = Seasons.FirstOrDefault(s => s.Episodes.Any(e => e.Id == lastWatched.Id));
                     if (targetSeason != null)
                     {
                         targetEpisode = targetSeason.Episodes.FirstOrDefault(e => e.Id == lastWatched.Id);
                     }
                 }

                 SeasonComboBox.ItemsSource = Seasons;
                 if (targetSeason != null)
                 {
                     SeasonComboBox.SelectedItem = targetSeason;
                     // UI binding handles the list update via SelectionChanged
                     // But we want to auto-select the episode too for "Next Up" logic
                     
                     // If finished, maybe next episode?
                     // For now, simpler: Just select the resume point.
                     if (targetEpisode != null)
                     {
                         // We defer this selection until list is populated in SelectionChanged
                         _pendingAutoSelectEpisode = targetEpisode;
                     }
                 }
             }
             catch { }
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
        
        private async Task ExtractTechInfoAsync()
        {
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
             if (ProbeCacheManager.TryGet(_streamUrl, out var cached))
             {
                 System.Diagnostics.Debug.WriteLine($"[MediaInfo] Pre-buffer Probe Cache Hit: {_streamUrl}");
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
                    var probeResult = new ProbeResult
                    {
                        Res = result.Res,
                        Fps = result.Fps,
                        Codec = result.Codec,
                        Bitrate = result.Bitrate,
                        Success = result.Success,
                        IsHdr = result.IsHdr
                    };
                    ProbeCacheManager.Cache(_streamUrl, probeResult);

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
            if (MediaInfoPlayer != null && App.HandoffPlayer != MediaInfoPlayer) 
            {
                 _ = MediaInfoPlayer.ExecuteCommandAsync("stop");
                 MediaInfoPlayer.DisableHandoffMode();
                 _ = MediaInfoPlayer.CleanupAsync();
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

            // Cancel previous probe
            _probeCts?.Cancel();
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
                // 1. Check Cache
                if (ProbeCacheManager.TryGet(url, out var cached))
                {
                    // Aesthetic Sync: Behave like the rest of the page (500ms delay)
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        SetBadgeLoadingState(true); // Show Shimmer
                        
                        // Prepare Content (Visible but Transparent)
                        TechBadgesContent.Visibility = Visibility.Visible;
                        ApplyMetadataToUi(cached);
                        this.UpdateLayout();
                        
                        // Wait for sync with other elements
                        await Task.Delay(500);

                        // Capture Shimmer Width
                        double shimmerWidth = MetadataShimmer.ActualWidth;

                        // Prevent Layout Shift logic
                        if (shimmerWidth > 0 && TechBadgesContent.ActualWidth < shimmerWidth)
                        {
                            TechBadgesContent.MinWidth = shimmerWidth;
                        }
                        else
                        {
                            TechBadgesContent.MinWidth = 0;
                        }
                        
                        SetBadgeLoadingState(false); // Reveal
                    });
                    return;
                }

                // 2. Show Shimmer (Reset state to Shimmer)
                SetBadgeLoadingState(true);

                // 3. Perform Probe
                System.Diagnostics.Debug.WriteLine($"[TechBadges] Starting Probe for: {url}");
                var result = await _ffprober.ProbeAsync(url);
                System.Diagnostics.Debug.WriteLine($"[TechBadges] Probe Result: Success={result.Success}, Res={result.Res}, Codec={result.Codec}");
                
                if (token.IsCancellationRequested) 
                {
                    System.Diagnostics.Debug.WriteLine("[TechBadges] Probe Cancelled.");
                    return;
                }

                var probeResult = new ProbeResult
                {
                    Res = result.Res,
                    Fps = result.Fps,
                    Codec = result.Codec,
                    Bitrate = result.Bitrate,
                    Success = result.Success,
                    IsHdr = result.IsHdr
                };

                ProbeCacheManager.Cache(url, probeResult);

                // 4. Update UI
                if (!token.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        System.Diagnostics.Debug.WriteLine("[TechBadges] Applying Probe Results on UI Thread...");

                        // 1. Capture Shimmer Width for Stability (Prevent Left Shift)
                        double shimmerWidth = MetadataShimmer.ActualWidth;

                        // 2. Prepare Content
                        TechBadgesContent.Visibility = Visibility.Visible;
                        ApplyMetadataToUi(probeResult);
                        
                        // 3. Force Layout
                        this.UpdateLayout();
                        System.Diagnostics.Debug.WriteLine($"[TechBadges] Widths - Shimmer: {shimmerWidth}, Content: {TechBadgesContent.ActualWidth}");
                        
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
                System.Diagnostics.Debug.WriteLine($"[MediaInfo] Technical Probe Failed: {ex}");
                // Ensure we exit loading state so text/layout doesn't stay hidden/ghosted if we decide to show fallback
                DispatcherQueue.TryEnqueue(() => SetBadgeLoadingState(false));
            }
        }

        private void ApplyMetadataToUi(ProbeResult result)
        {
            if (result == null) return;

            // Resolution / 4K
            bool is4K = result.Res.Contains("3840") || result.Res.Contains("4096") || result.Res.ToUpperInvariant().Contains("4K");
            Badge4K.Visibility = is4K ? Visibility.Visible : Visibility.Collapsed;

            if (!is4K && !string.IsNullOrEmpty(result.Res) && result.Res != "Unknown" && result.Res != "Error")
            {
                // Show resolution badge (e.g. 1080P)
                string displayRes = result.Res;
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
    }

    public class SeasonItem
    {
        public string Name { get; set; }
        public int SeasonNumber { get; set; }
        public List<EpisodeItem> Episodes { get; set; }
    }

    public class EpisodeItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Duration { get; set; }
        public string ImageUrl { get; set; }
        public string StreamUrl { get; set; }
        public string Container { get; set; }
        public int SeasonNumber { get; set; }
        
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
