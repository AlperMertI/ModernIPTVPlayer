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
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using System.IO;

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

        public MediaInfoPage()
        {
            this.InitializeComponent();
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
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
            // 1. Immediate UI Update (Show what we already know)
            // Initially, we show the skeleton and keep the real panel hidden
            FullSkeleton.Visibility = Visibility.Visible;
            RealContentPanel.Visibility = Visibility.Collapsed;

            TitleText.Text = item.Title;
            if (!string.IsNullOrEmpty(item.PosterUrl))
            {
                // Reset opacity to avoid "ghost" flashes
                HeroImage.Opacity = 0;
                HeroImage.Source = new BitmapImage(new Uri(item.PosterUrl));
                
                // 50ms delay is enough for the page to render the skeleton
                await Task.Delay(50);
                HeroImage.Opacity = 1; 
                StartHeroConnectedAnimation();
            }

            // TMDB Fetch (Skip if already have it)
            if (preFetchedTmdb != null)
            {
                _cachedTmdb = preFetchedTmdb;
            }
            else
            {
                if (item is SeriesStream)
                {
                     // TV Search
                     _cachedTmdb = await TmdbHelper.SearchTvAsync(item.Title);
                }
                else
                {
                     // Movie Search
                     _cachedTmdb = await TmdbHelper.SearchMovieAsync(item.Title, (item as LiveStream)?.Year);
                }
            }
            
            if (_cachedTmdb != null)
            {
                // Override Title with clean TMDB title
                TitleText.Text = _cachedTmdb.DisplayTitle;
                
                OverviewText.Text = _cachedTmdb.Overview;
                YearText.Text = _cachedTmdb.DisplayDate?.Split('-')[0] ?? "";
                
                if (!string.IsNullOrEmpty(_cachedTmdb.FullBackdropUrl))
                {
                    // Swap to high-res backdrop gracefully
                    var highResSource = new BitmapImage(new Uri(_cachedTmdb.FullBackdropUrl));
                    HeroImage.Source = highResSource;
                }

                // Fetch Deep Details (Runtime, Genres)
                var details = await TmdbHelper.GetDetailsAsync(_cachedTmdb.Id, item is SeriesStream);
                if (details != null)
                {
                    RuntimeText.Text = (item is SeriesStream) ? "Dizi" : $"{details.Runtime / 60}sa {details.Runtime % 60}dk";
                    GenresText.Text = string.Join(" • ", details.Genres.Select(g => g.Name).Take(3));
                }

                // Fetch Cast
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
                    }
                }
            }
            else
            {
                // FALLBACK: Use Provider Data if TMDB is not found
                OverviewText.Text = (item is SeriesStream ss) ? (ss.Plot ?? "Açıklama mevcut değil.") : "Açıklama mevcut değil.";
                YearText.Text = (item is LiveStream ls) ? (ls.Year ?? "") : "";
                
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
            }

            // Determine Stream Type Logic
            if (item is SeriesStream series)
            {
                // SERIES MODE
                EpisodesPanel.Visibility = Visibility.Visible;
                PlayButtonText.Text = "Oynat";
                OverviewText.MaxLines = 4; // Save space
                
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

            // 3. Final Step: Smooth Staggered Crossfade (Skeleton -> Real UI)
            if (FullSkeleton.Visibility == Visibility.Visible)
            {
                FullSkeleton.Visibility = Visibility.Collapsed;
                RealContentPanel.Visibility = Visibility.Visible;
                StaggeredRevealContent();
            }
        }

        private void StaggeredRevealContent()
        {
            double delay = 0;
            const double staggerIncrement = 0.08; 

            foreach (var child in RealContentPanel.Children)
            {
                if (child is UIElement element)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    
                    // CRITICAL: Set Visual Opacity to 0 initially, 
                    // and keep UIElement.Opacity at 1 to avoid the 0 * 1 = 0 issue.
                    visual.Opacity = 0f;

                    var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(1f, 1f);
                    fadeIn.Duration = TimeSpan.FromMilliseconds(500);
                    fadeIn.DelayTime = TimeSpan.FromSeconds(delay);

                    // Add subtle lift
                    ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                    var moveUp = _compositor.CreateVector3KeyFrameAnimation();
                    moveUp.InsertKeyFrame(0f, new System.Numerics.Vector3(0, 12, 0));
                    moveUp.InsertKeyFrame(1f, System.Numerics.Vector3.Zero);
                    moveUp.Duration = TimeSpan.FromMilliseconds(600);
                    moveUp.DelayTime = TimeSpan.FromSeconds(delay);

                    visual.StartAnimation("Opacity", fadeIn);
                    visual.StartAnimation("Translation", moveUp);

                    delay += staggerIncrement;
                }
            }
        }

        private void StartHeroConnectedAnimation()
        {
            var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("ForwardConnectedAnimation");
            if (anim != null)
            {
                // We target the Container to make sure Image + Effects fly together
                anim.Configuration = new DirectConnectedAnimationConfiguration();
                
                // Manual 1s Fade-in for effects (starts exactly with morph)
                var vVisual = ElementCompositionPreview.GetElementVisual(VignetteEffect);
                var verVisual = ElementCompositionPreview.GetElementVisual(VerticalEffect);
                
                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(1f, 1f);
                fadeIn.Duration = TimeSpan.FromSeconds(1); // 1 Second fade
                
                vVisual.StartAnimation("Opacity", fadeIn);
                verVisual.StartAnimation("Opacity", fadeIn);

                // Start the morph
                anim.TryStart(HeroContainer);
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
                                     tmdbSeason = await TmdbHelper.GetSeasonDetailsAsync(_cachedTmdb.Id, seasonNum);
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
                                         // Match by Episode Number (preferred) or clean title fuzzy match?
                                         // Xtream Codes usually returns 'episode_num' accurately.
                                         var match = tmdbSeason.Episodes.FirstOrDefault(x => x.EpisodeNumber == epNum);
                                         if (match != null)
                                         {
                                             title = match.Name; // Use TMDB Title!
                                             // Could also use match.Overview, match.StillUrl...
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
                
                if (_pendingAutoSelectEpisode != null && season.Episodes.Contains(_pendingAutoSelectEpisode))
                {
                    EpisodesListView.SelectedItem = _pendingAutoSelectEpisode;
                    EpisodesListView.ScrollIntoView(_pendingAutoSelectEpisode);
                    _pendingAutoSelectEpisode = null;
                }
                else if (CurrentEpisodes.Count > 0)
                {
                    // Select first by default if no history
                     EpisodesListView.SelectedItem = CurrentEpisodes[0];
                }
            }
        }

        private void EpisodesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
             if (EpisodesListView.SelectedItem is EpisodeItem ep)
             {
                 _selectedEpisode = ep;
                 _streamUrl = ep.StreamUrl;
                 
                 // UPDATE UI FOR SELECTED EPISODE
                 TitleText.Text = ep.Title; // Update main title to episode title? Or keep series?
                 // Usually series title + "S1 E2 - Episode Title"
                 
                 // Update Play Button
                 // Update Play Button
                 if (ep.HasProgress && ep.ProgressPercent < 95)
                 {
                      PlayButtonText.Text = "Devam Et";
                      PlayButtonText.Text = "Devam Et";
                      PlayButtonSubtext.Visibility = Visibility.Visible;
                      PlayButtonSubtext.Text = ep.ProgressText;
                      RestartButton.Visibility = Visibility.Visible;
                  }
                  else
                  {
                      PlayButtonText.Text = "Oynat";
                      PlayButtonText.Text = "Oynat";
                      PlayButtonSubtext.Visibility = Visibility.Collapsed;
                      RestartButton.Visibility = Visibility.Collapsed;
                  }

                 // PREVIEW
                 var history = HistoryManager.Instance.GetProgress(ep.Id);
                 InitializePrebufferPlayer(ep.StreamUrl, history?.Position ?? 0);
             }
        }

        private void InitializePrebufferPlayer(string url, double startTime = 0)
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

                 // Codec
                 if (!string.IsNullOrEmpty(live.Codec) && live.Codec != "-") 
                 {
                     BadgeCodec.Text = live.Codec.ToUpper();
                 }

                 // HDR / SDR from existing metadata
                 if (live.IsHdr)
                 {
                     BadgeHDR.Visibility = Visibility.Visible;
                     BadgeSDR.Visibility = Visibility.Collapsed;
                 }
                 else
                 {
                     // Explicit SDR
                     BadgeSDR.Visibility = Visibility.Visible;
                     BadgeHDR.Visibility = Visibility.Collapsed;
                 }

                 // No probing needed
                 return;
             }
             
             // ... Continue to probing if no metadata ...
             
            // Use FFmpegProber for faster metadata extraction
            try
            {
                var prober = new FFmpegProber();
                var result = await prober.ProbeAsync(_streamUrl);

                if (result.Success)
                {
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
        
        private void PerformHandoverAndNavigate(string url, string title, string id = null, string parentId = null, string seriesName = null, int season = 0, int episode = 0)
        {
            // Handoff Logic
            try
            {
                // Ensure Handoff is set
                App.HandoffPlayer = MediaInfoPlayer;
                MediaInfoPlayer.EnableHandoffMode();
                
                // Detach
                if (MediaInfoPlayer.Parent is Panel parent) parent.Children.Remove(MediaInfoPlayer);
                
                // Navigate
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title, id, parentId, seriesName, season, episode));
            }
            catch
            {
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title, id, parentId, seriesName, season, episode));
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
                     HistoryManager.Instance.UpdateProgress(_selectedEpisode.Id, _selectedEpisode.Title, _streamUrl, 0, 0, parentId, _item.Title, _selectedEpisode.SeasonNumber);
                     PerformHandoverAndNavigate(_streamUrl, _selectedEpisode.Title, _selectedEpisode.Id, parentId, _item.Title, _selectedEpisode.SeasonNumber);
                }
                else if (_item is LiveStream live)
                {
                    // Update History to 0
                    HistoryManager.Instance.UpdateProgress(live.StreamId.ToString(), live.Title, live.StreamUrl, 0, 0, null, null, 0, 0);
                    PerformHandoverAndNavigate(_streamUrl, live.Title, live.StreamId.ToString());
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
        private void BackButton_Click(object sender, RoutedEventArgs e) { if (Frame.CanGoBack) Frame.GoBack(); }

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
    }

    public class SeasonItem
    {
        public string Name { get; set; }
        public int SeasonNumber { get; set; }
        public List<EpisodeItem> Episodes { get; set; }
    }

    public class EpisodeItem
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
    }
    

    
    public class CastItem
    {
        public string Name { get; set; }
        public string Character { get; set; }
        public string FullProfileUrl { get; set; }
    }
}
namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }
