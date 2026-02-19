using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;
using ModernIPTVPlayer;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class ExpandedCard : UserControl
    {
        public event EventHandler PlayClicked;
        public event EventHandler<TmdbMovieResult> DetailsClicked;
        public event EventHandler AddListClicked;
        public event EventHandler<bool> CinemaModeToggled;
        
        // Hold data
        private ModernIPTVPlayer.Models.IMediaStream _stream;
        private TmdbMovieResult _tmdbInfo;
        
        // Cinema Mode State
        private bool _isCinemaMode = false;
        
        // Pre-initialization state
        private bool _webViewInitialized = false;
        private string _trailerFolder;
        private string _virtualHost = "trailers.moderniptv.local";
        private Microsoft.UI.Composition.Compositor _compositor;
        private System.Threading.CancellationTokenSource _trailerCts;

        public Image BannerImage => BackdropImage;

        public ExpandedCard()
        {
            this.InitializeComponent();
            _compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this).Compositor;
        }
        
        /// <summary>
        /// Call this when user hovers on a poster (before card is shown).
        /// Uses the hover delay time to initialize WebView2 in background.
        /// </summary>
        public void PrepareForTrailer()
        {
            if (!_webViewInitialized && !_isInitializing)
            {
                _isInitializing = true;
                _ = PreInitializeWebViewAsync();
            }
        }
        
        private bool _isInitializing = false;
        
        /// <summary>
        /// Pre-initialize WebView2 and YouTube player for instant loading
        /// </summary>
        private async Task PreInitializeWebViewAsync()
        {
            try
            {
                await TrailerWebView.EnsureCoreWebView2Async();
                
                // Listen for messages
                TrailerWebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived; // Prevent duplicates
                TrailerWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                
                // Create trailer folder once
                _trailerFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ModernIPTVPlayer_Trailers");
                System.IO.Directory.CreateDirectory(_trailerFolder);
                
                string htmlFilePath = System.IO.Path.Combine(_trailerFolder, "player.html");
                
                // Always refresh player script so quality/behavior updates are picked up immediately.
                string htmlContent = CreateYouTubePlayerHtml();
                await System.IO.File.WriteAllTextAsync(htmlFilePath, htmlContent);
                
                // Setup virtual host mapping once
                try
                {
                    TrailerWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        _virtualHost,
                        _trailerFolder,
                        Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                }
                catch (ArgumentException) 
                {
                    // Ignore if already mapped
                }
                
                // Pre-load the player page (will be ready for any video)
                TrailerWebView.CoreWebView2.Navigate($"https://{_virtualHost}/player.html");
                
                _webViewInitialized = true;
                System.Diagnostics.Debug.WriteLine("[ExpandedCard] WebView2 pre-initialized successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Pre-init error: {ex.Message}");
                _isInitializing = false; // Allow retry
            }
        }
        
        private string CreateYouTubePlayerHtml()
        {
            return @"<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        html, body { width: 100%; height: 100%; background: #000; overflow: hidden; }
        #player { 
            position: absolute;
            top: 50%; left: 50%;
            min-width: 177.77vh;
            min-height: 56.25vw;
            width: 100vw; height: 56.25vw;
            transform: translate(-50%, -50%);
        }
        iframe { pointer-events: none; }
        #loading { 
            position: absolute; top: 50%; left: 50%; 
            transform: translate(-50%, -50%);
            color: #333; font-family: sans-serif; font-size: 14px;
        }
    </style>
</head>
<body>
    <div id='loading'>Loading player...</div>
    <div id='player'></div>
    <script>
        var tag = document.createElement('script');
        tag.src = 'https://www.youtube.com/iframe_api';
        var firstScriptTag = document.getElementsByTagName('script')[0];
        firstScriptTag.parentNode.insertBefore(tag, firstScriptTag);

        var player;
        var isReady = false;
        var pendingVideoId = null;
        
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
                    vq: 'hd1080'
                },
                events: {
                    'onReady': onPlayerReady,
                    'onStateChange': onPlayerStateChange
                }
            });
        }
        
        function onPlayerReady(event) {
            isReady = true;
            try {
                player.mute();
            } catch (e) {}
            document.getElementById('loading').style.display = 'none';
            window.chrome.webview.postMessage('PLAYER_READY');
            
            // If a video was requested before ready, load it now
            if (pendingVideoId) {
                loadVideo(pendingVideoId);
                pendingVideoId = null;
            }
        }
        
        function onPlayerStateChange(event) {
            if (event.data === YT.PlayerState.PLAYING) {
                applyQualityPreference();
                window.chrome.webview.postMessage('VIDEO_PLAYING');
            }
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
            if (!isReady) {
                pendingVideoId = videoId;
                return;
            }
            player.loadVideoById({
                videoId: videoId,
                suggestedQuality: 'hd1080'
            });
            setTimeout(applyQualityPreference, 80);
            setTimeout(applyQualityPreference, 700);
            player.playVideo();
        }
        
        // Called from C# to stop playback
        function stopVideo() {
            if (player && isReady) {
                player.stopVideo();
            }
        }

        function getMuteState() {
            if (player && isReady) {
                return player.isMuted() ? 'muted' : 'unmuted';
            }
            return 'unknown';
        }

        function setMuted(shouldMute) {
            if (player && isReady) {
                if (shouldMute) player.mute();
                else player.unMute();
            }
            return getMuteState();
        }
        
        function toggleMute() {
            if (!player || !isReady) return 'unknown';
            if (player.isMuted()) {
                player.unMute();
                return 'unmuted';
            } else {
                player.mute();
                return 'muted';
            }
        }
    </script>
</body>
</html>";
        }

        // FFmpeg Prober
        private FFmpegProber _prober = new FFmpegProber();
        private long _loadNonce = 0;

        private async void FavButton_Click(object sender, RoutedEventArgs e)
        {
            if (_stream == null) return;

            var manager = Services.WatchlistManager.Instance;
            bool isInList = manager.IsOnWatchlist(_stream);

            if (isInList)
            {
                await manager.RemoveFromWatchlist(_stream);
            }
            else
            {
                await manager.AddToWatchlist(_stream);
            }

            // Animate Icon
            UpdateWatchlistIcon(!isInList, animate: true);
        }

        private void UpdateWatchlistIcon(bool isAdded, bool animate = false)
        {
            var icon = (FontIcon)FavButton.Content;
            string newGlyph = isAdded ? "\uE73E" : "\uE710"; // Checkmark vs Plus

            if (animate)
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
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
            FavButton.Background = isAdded 
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 33, 150, 243)) // Blue when added
                : new SolidColorBrush(Windows.UI.Color.FromArgb(34, 255, 255, 255)); // Transparent white
        }

        public void ToggleCinemaMode(bool enable)
        {
            if (_isCinemaMode == enable) return;
            _isCinemaMode = enable;

            if (enable)
            {
                // Enter Cinema Mode
                // 1. Hide Content Rows
                RootGrid.RowDefinitions[1].Height = new GridLength(0);
                RootGrid.RowDefinitions[2].Height = new GridLength(0);
                
                // 2. Maximize Trailer Row
                RootGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
                
                // 3. Unmute if muted
                if (_isMuted)
                {
                    _ = SetMutedAsync(false);
                }
                
                // 4. Change Icon to Shrink
                ((FontIcon)ExpandButton.Content).Glyph = "\uE73F"; // Shrink Icon
            }
            else
            {
                // Exit Cinema Mode
                // 1. Restore Rows
                RootGrid.RowDefinitions[0].Height = new GridLength(160);
                RootGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                RootGrid.RowDefinitions[2].Height = GridLength.Auto;

                // 2. Change Icon to Expand
                ((FontIcon)ExpandButton.Content).Glyph = "\uE740"; // Expand Icon
            }

            CinemaModeToggled?.Invoke(this, _isCinemaMode);
        }

        private void ExpandButton_Click(object sender, RoutedEventArgs e) => ToggleCinemaMode(!_isCinemaMode);

        /// <summary>
        /// Public method to stop trailer playback when card is hidden
        /// </summary>
        public async Task StopTrailer(bool forceDestroy = false)
        {
            try
            {
                // Cancel any pending PlayTrailer or init
                _trailerCts?.Cancel();
                _trailerCts?.Dispose();
                _trailerCts = null;

                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] StopTrailer called. ForceDestroy={forceDestroy}");

                // Stop the player via JS explicitly before hiding
                if (_webViewInitialized && TrailerWebView.CoreWebView2 != null)
                {
                     try 
                     {
                         await TrailerWebView.CoreWebView2.ExecuteScriptAsync("stopVideo();");
                     }
                     catch(Exception ex) 
                     {
                         System.Diagnostics.Debug.WriteLine($"[ExpandedCard] StopJS Error: {ex.Message}");
                     }
                }

                if (forceDestroy)
                {
                    try
                    {
                        // Nuclear option for navigation: Kill the WebView content to ensure no audio persists
                        TrailerWebView.Source = new Uri("about:blank");
                        _webViewInitialized = false; // Mark as uninitialized so it reloads next time
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Error in StopTrailer: {ex.Message}");
            }

            if (_isCinemaMode) ToggleCinemaMode(false); // Reset mode
            ResetState(isMorphing: false, isStopping: true);
        }

        /// <summary>
        /// Resets the card to initial state before loading new data
        /// </summary>
        private void ResetState(bool isMorphing = false, bool isStopping = false)
        {
            // Reset trailer/WebView - stop video via JavaScript (preserve pre-initialized player)
            TrailerWebView.Visibility = Visibility.Collapsed;
            MuteButton.Visibility = Visibility.Collapsed;
            ExpandButton.Visibility = Visibility.Collapsed;
            _isMuted = true;
            UpdateMuteIcon();

            
            // Show backdrop container again
            BackdropContainer.Visibility = Visibility.Visible;
            
            // Content Visibility Logic
            if (!isStopping) 
            {
                BackdropImage.Opacity = 0.7;
                BackdropOverlay.Visibility = Visibility.Visible;

                if (isMorphing)
                {
                    // Crossfade: Fade Out Old Content, Fade In Skeleton
                    var visualContent = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(RealContentPanel);
                    var visualSkeleton = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(MainSkeleton);
                    
                    MainSkeleton.Visibility = Visibility.Visible;
                    visualSkeleton.Opacity = 0f;

                    try
                    {
                        var animOut = _compositor.CreateScalarKeyFrameAnimation();
                        animOut.InsertKeyFrame(1.0f, 0f); // Target value 0
                        animOut.Duration = TimeSpan.FromMilliseconds(200);
                        animOut.Target = "Opacity"; // ! IMPORTANT

                        var animIn = _compositor.CreateScalarKeyFrameAnimation();
                        animIn.InsertKeyFrame(1.0f, 1f); // Target value 1
                        animIn.Duration = TimeSpan.FromMilliseconds(200);
                        animIn.Target = "Opacity"; // ! IMPORTANT

                        visualContent.StartAnimation("Opacity", animOut);
                        visualSkeleton.StartAnimation("Opacity", animIn);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Morph Animation Failed: {ex.Message}");
                        // Fallback to instant
                        RealContentPanel.Opacity = 0;
                        if (visualSkeleton != null) visualSkeleton.Opacity = 1f;
                    }
                }
                else
                {
                    // Instant Reset
                    RealContentPanel.Opacity = 0;
                    MainSkeleton.Visibility = Visibility.Visible;
                    Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(MainSkeleton).Opacity = 1f;
                }
                
                // Reset badges
                TechBadgesPanel.Children.Clear();
                
                // Reset Play Button Subtext
                PlayButtonSubtext.Visibility = Visibility.Collapsed;
                PlayButtonSubtext.Text = "";

                // Reset Progress UI
                PlaybackProgressBar.Visibility = Visibility.Collapsed;
                ProgressPanel.Visibility = Visibility.Collapsed;
                TimeLeftText.Text = "";
                    
                // Reset tech badges
                BadgeSkeleton.Visibility = Visibility.Collapsed;
                TechBadgesPanel.Visibility = Visibility.Collapsed;

                // Reset mood tag
                MoodTag.Visibility = Visibility.Collapsed;
                
                // Reset ratings
                YearText.Visibility = Visibility.Visible;
                RatingText.Visibility = Visibility.Visible;
                RatingText.Text = "";
                YearText.Text = "";
                
                // STALE CONTENT FIX: Clear text and image immediately
                TitleText.Text = "";
                DescText.Text = "";
                GenresText.Text = "";
                BackdropImage.Source = null;
                
                // Reset Ambience
                AmbienceGrid.Visibility = Visibility.Visible;
                
                // Reset loading ring
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
            }
            else
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        public async Task LoadDataAsync(ModernIPTVPlayer.Models.IMediaStream stream, bool isMorphing = false)
        {
            var loadNonce = ++_loadNonce;

            // Reset all state first (except image)
            ResetState(isMorphing);
            
            // DEBOUNCE: Wait 300ms before doing heavy metadata/trailer work
            await Task.Delay(300);
            if (loadNonce != _loadNonce) return; // User moved to another poster
            
            _stream = stream;
            TitleText.Text = stream.Title;
            
            // Set initial low-res image (or clear it if none)
            if (!string.IsNullOrEmpty(stream.PosterUrl))
            {
                BackdropImage.Source = new BitmapImage(new Uri(stream.PosterUrl));
            }
            else
            {
                BackdropImage.Source = null;
            }
            
            // Ensure History & ProbeCache are Ready
            await HistoryManager.Instance.InitializeAsync();
             // Wait for Probe Cache to be ready (Race Condition fix)
            await Services.ProbeCacheService.Instance.EnsureLoadedAsync();
            // await Services.TmdbCacheService.Instance.EnsureLoadedAsync(); // Implied by EnsureLoadedAsync logic if updated, or not strictly needed if lazy

            
            // Initial Tooltip (Static parse)
            UpdateTooltip(stream);

            // Stremio Badge Logic: Hide technical skeleton if no history available
            if (stream is StremioMediaStream stremio)
            {
                var history = HistoryManager.Instance.GetProgress(stremio.IMDbId);
                if (history == null || string.IsNullOrEmpty(history.StreamUrl))
                {
                    // No history or URL yet - hide tech skeleton and panel
                    BadgeSkeleton.Visibility = Visibility.Collapsed;
                    TechBadgesPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    TechBadgesPanel.Visibility = Visibility.Visible;
                }
            }
            else
            {
                TechBadgesPanel.Visibility = Visibility.Visible;
            }

            UpdatePlayButton(stream);
            UpdateProgressState(stream);
            
            // Check Watchlist State
            await Services.WatchlistManager.Instance.InitializeAsync();
            UpdateWatchlistIcon(Services.WatchlistManager.Instance.IsOnWatchlist(stream), animate: false);

            try
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Fetching Metadata for: {stream.Title}");
                
                var unified = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(stream);
                if (loadNonce != _loadNonce) return;

                if (unified != null)
                {
                    _tmdbInfo = unified.TmdbInfo; // Keep for DetailsClicked compatibility
                    if (stream != null) stream.TmdbInfo = unified.TmdbInfo;

                    string displayTitle = unified.Title;
                    string displaySubtitle = unified.Genres;
                    string displayOverview = unified.Overview;
                    string displayBackdrop = unified.BackdropUrl;

                    // --- EPISODE RESUME LOGIC (Enhanced with UnifiedMetadata) ---
                    if (unified.IsSeries)
                    {
                        var history = HistoryManager.Instance.GetLastWatchedEpisode(unified.MetadataId);
                        if (history != null && unified.Seasons != null)
                        {
                            var season = unified.Seasons.FirstOrDefault(s => s.SeasonNumber == history.SeasonNumber);
                            if (season != null)
                            {
                                var ep = season.Episodes.FirstOrDefault(e => e.EpisodeNumber == history.EpisodeNumber);
                                if (ep == null && history.EpisodeNumber == 0)
                                    ep = season.Episodes.FirstOrDefault(e => e.EpisodeNumber == 1);

                                if (ep != null)
                                {
                                    string epName = ep.Title;
                                    string cleanTitle = TmdbHelper.CleanEpisodeTitle(history.Title);
                                    
                                    bool isGeneric = string.IsNullOrEmpty(epName) || 
                                                    epName.Contains("Bölüm") || 
                                                    epName.Contains("Episode") || 
                                                    epName == ep.EpisodeNumber.ToString();

                                    if (isGeneric && !string.IsNullOrEmpty(cleanTitle) && cleanTitle.Length > 2)
                                        displayTitle = cleanTitle;
                                    else
                                        displayTitle = !string.IsNullOrEmpty(epName) ? epName : $"Bölüm {ep.EpisodeNumber}";

                                    if (!string.IsNullOrEmpty(ep.Overview)) displayOverview = ep.Overview;
                                    if (!string.IsNullOrEmpty(ep.ThumbnailUrl)) displayBackdrop = ep.ThumbnailUrl;
                                    
                                    displayTitle = $"S{history.SeasonNumber:D2}E{history.EpisodeNumber:D2} - {displayTitle}";
                                }
                            }
                        }
                    }

                    // Update UI IMMEDIATELY
                    UpdateUiFromUnified(unified, displayTitle, displaySubtitle, displayOverview, displayBackdrop);

                    // NOW Fetch Trailer (Provider might have pre-filled this from TMDB if enabled)
                    string trailerKey = unified.TrailerUrl;
                    
                    // If provider didn't find one but we have TMDB info, try a quick lookup fallback?
                    // Usually MetadataProvider handles this, but for ExpandedCard we want speed.
                    if (string.IsNullOrEmpty(trailerKey) && unified.TmdbInfo != null)
                    {
                         trailerKey = await TmdbHelper.GetTrailerKeyAsync(unified.TmdbInfo.Id, unified.IsSeries);
                    }

                    if (loadNonce != _loadNonce) return;
                    if (!string.IsNullOrEmpty(trailerKey))
                    {
                         await PlayTrailer(videoKey: trailerKey);
                    }
                    else
                    {
                        LoadingRing.IsActive = false;
                        LoadingRing.Visibility = Visibility.Collapsed;
                        BackdropImage.Opacity = 1.0;
                        BackdropOverlay.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    DescText.Text = "No additional details found.";
                    MainSkeleton.Visibility = Visibility.Collapsed;
                    RealContentPanel.Opacity = 1;
                    YearText.Visibility = Visibility.Collapsed;
                    RatingText.Visibility = Visibility.Collapsed;
                    
                    LoadingRing.IsActive = false; 
                    LoadingRing.Visibility = Visibility.Collapsed;
                }
                
                // Run Probe in Background - Do NOT await it to block the UI interaction
                // BadgeSkeleton remains Visible until this finishes
                _ = ProbeStreamInternal(stream, loadNonce);

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Error: {ex.Message}");
                DescText.Text = "Error loading details.";
                MainSkeleton.Visibility = Visibility.Collapsed;
                BadgeSkeleton.Visibility = Visibility.Collapsed;
                RealContentPanel.Opacity = 1;
            }
            // Finally logic moved to inside success/fail blocks to avoid premature hiding
        }
        
        private async Task ProbeStreamInternal(IMediaStream stream, long loadNonce)
        {
            if (stream == null) return;
            
            string url = null;
            if (stream is LiveStream live)
            {
                if (live.HasMetadata || live.IsProbing) return;
                url = live.StreamUrl;
            }
            else if (stream is SeriesStream series)
            {
                if (series.HasMetadata || series.IsProbing) return;
                // For series, probe the last watched episode if any
                var history = HistoryManager.Instance.GetLastWatchedEpisode(series.SeriesId.ToString());
                if (history != null) 
                {
                    url = history.StreamUrl;
                }
                else if (App.CurrentLogin != null)
                {
                    try
                    {
                        var info = await Services.ContentCacheService.Instance.GetSeriesInfoAsync(series.SeriesId, App.CurrentLogin);
                        if (info != null && info.Episodes != null && info.Episodes.Count > 0)
                        {
                             // Find First Season
                             var firstSeasonKey = info.Episodes.Keys.OrderBy(k => 
                             {
                                 if (int.TryParse(k, out int s)) return s;
                                 return 9999;
                             }).FirstOrDefault();
                             
                             if (firstSeasonKey != null && info.Episodes.TryGetValue(firstSeasonKey, out var eps) && eps != null)
                             {
                                 var firstEp = eps.OrderBy(e => 
                                 {
                                     if (int.TryParse(e.EpisodeNum?.ToString(), out int en)) return en;
                                     return 9999;
                                 }).FirstOrDefault();
                                 
                                 if (firstEp != null)
                                 {
                                     // Construct URL: /series/{user}/{pass}/{id}.{ext}
                                     var host = App.CurrentLogin.Host.TrimEnd('/');
                                     url = $"{host}/series/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{firstEp.Id}.{firstEp.ContainerExtension}";
                                 }
                             }
                        }
                    }
                    catch (Exception ex)
                    {
                        Services.CacheLogger.Error(Services.CacheLogger.Category.Probe, "Failed fetching Series Info", ex.Message);
                    }
                }
            }
            else if (stream is StremioMediaStream stremioItem)
            {
                HistoryItem? history = null;
                if (stremioItem.Meta.Type == "series" || stremioItem.Meta.Type == "tv")
                    history = HistoryManager.Instance.GetLastWatchedEpisode(stremioItem.IMDbId);
                else
                    history = HistoryManager.Instance.GetProgress(stremioItem.IMDbId);

                if (history != null)
                {
                    url = history.StreamUrl;
                }
            }
            else if (stream is WatchlistItem w)
            {
                if (w.Type == "series")
                {
                    var history = HistoryManager.Instance.GetLastWatchedEpisode(w.Id);
                    if (history != null) url = history.StreamUrl;
                }
                else
                {
                    var history = HistoryManager.Instance.GetProgress(w.Id);
                    if (history != null) url = history.StreamUrl;
                    else if (!string.IsNullOrEmpty(w.StreamUrl)) url = w.StreamUrl;
                }
            }

            if (string.IsNullOrEmpty(url)) 
            {
                DispatcherQueue.TryEnqueue(() => BadgeSkeleton.Visibility = Visibility.Collapsed);
                return;
            }

            try
            {
                // 1. Check Cache
                var cached = Services.ProbeCacheService.Instance.Get(url);
                if (cached != null)
                {
                    Services.CacheLogger.Success(Services.CacheLogger.Category.Probe, "ExpandedCard Cache Hit", url);
                    if (loadNonce == _loadNonce)
                    {
                        BadgeSkeleton.Visibility = Visibility.Collapsed;
                        ApplyProbeResult(stream, cached, loadNonce);
                    }
                    return;
                }

                // 2. URL changed check: If this specific stream object already has metadata 
                // but the URL we are probing is DIFFERENT, it means the source changed.
                // We should clear old data if the stream object is the same but URL is new.
                // Actually, ProbeData is URL-keyed, so if URL is new, it's already a 'miss'.
                // The task is to ensure we DON'T use old data if URL changed.
                // Since Get(url) returned null, we are good. 

                // 3. Probe Network
                SetProbing(stream, true);
                Services.CacheLogger.Info(Services.CacheLogger.Category.Probe, "Probing Network (ExpandedCard)", url);
                
                var result = await _prober.ProbeAsync(url);
                
                if (result.Success)
                {
                    Services.ProbeCacheService.Instance.Update(url, result.Res, result.Fps, result.Codec, result.Bitrate, result.IsHdr);
                    
                    // Direct apply (no need to fetch back from cache immediately)
                    var data = new Services.ProbeData 
                    { 
                        Resolution = result.Res, 
                        Fps = result.Fps, 
                        Codec = result.Codec, 
                        Bitrate = result.Bitrate, 
                        IsHdr = result.IsHdr 
                    };
                    ApplyProbeResult(stream, data, loadNonce);
                }
                else
                {
                    Services.CacheLogger.Warning(Services.CacheLogger.Category.Probe, "Probing Failed (Results empty)", url);
                }
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.Probe, "ExpandedCard Probe Error", ex.Message);
            }
            finally
            {
                SetProbing(stream, false);
            }
        }

        private void SetProbing(IMediaStream stream, bool isProbing)
        {
            stream.IsProbing = isProbing;
            
            DispatcherQueue.TryEnqueue(() =>
            {
                if (isProbing)
                {
                    BadgeSkeleton.Visibility = Visibility.Visible;
                    TechBadgesPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Always ensure skeleton is hidden when probing stops
                    if (BadgeSkeleton.Visibility == Visibility.Visible)
                        BadgeSkeleton.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void ApplyProbeResult(IMediaStream stream, Services.ProbeData result, long loadNonce)
        {
            if (loadNonce != _loadNonce) return;

            stream.Resolution = result.Resolution;
            stream.Fps = result.Fps;
            stream.Codec = result.Codec;
            stream.Bitrate = result.Bitrate;
            stream.IsOnline = true;
            stream.IsHdr = result.IsHdr;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (loadNonce != _loadNonce) return;
                BadgeSkeleton.Visibility = Visibility.Collapsed;
                UpdateTooltip(stream);
                UpdatePlayButton(stream);
                UpdateProgressState(stream);
            });
        }

        private void UpdateTooltip(ModernIPTVPlayer.Models.IMediaStream stream)
        {
            TechBadgesPanel.Children.Clear();

            // Stremio Specific: Hide TechBadgesPanel if no selected stream in history
            if (stream is StremioMediaStream stremio)
            {
                HistoryItem? history = null;
                if (stremio.Meta.Type == "series" || stremio.Meta.Type == "tv")
                    history = HistoryManager.Instance.GetLastWatchedEpisode(stremio.IMDbId);
                else
                    history = HistoryManager.Instance.GetProgress(stremio.IMDbId);

                if (history == null || string.IsNullOrEmpty(history.StreamUrl))
                {
                    TechBadgesPanel.Visibility = Visibility.Collapsed;
                    // Only collapse skeleton if we aren't currently probing (shimmer active)
                    if (BadgeSkeleton.Visibility != Visibility.Visible)
                        BadgeSkeleton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    TechBadgesPanel.Visibility = Visibility.Visible;
                }
            }
            else
            {
                TechBadgesPanel.Visibility = Visibility.Visible;
            }
            
            // 1. Logic moved to Play Button subtext for premium look
            if (stream is LiveStream live)
            {
                 // Probing Results - Handled in Dedup sections below
            }

            // Metadata Extraction (Unified)
            string name = stream.Title.ToUpperInvariant();

            // Metadata Extraction (Unified)
            string res = stream.Resolution;
            string codecLabel = stream.Codec;
            bool? isHdrMetadata = stream.IsHdr;
            bool hasMetadata = stream.HasMetadata;

            // 2. RESOLUTION & 4K (DEDUPLICATION)
            bool is4K = name.Contains("4K") || name.Contains("UHD");
            if (!string.IsNullOrEmpty(res))
            {
                if (res.Contains("3840") || res.Contains("4096") || res.Contains("4K")) is4K = true;
                
                if (is4K) AddBadge("4K UHD", Colors.Purple);
                else AddBadge(res, Colors.Teal);
            }
            else
            {
                 if (is4K) AddBadge("4K UHD", Colors.Purple);
                 else if (name.Contains("FHD") || name.Contains("1080P")) AddBadge("1080p", Colors.Teal);
                 else if (name.Contains("HD") || name.Contains("720P")) AddBadge("720p", Colors.CornflowerBlue);
            }

            // Codec
            if (!string.IsNullOrEmpty(codecLabel))
            {
                 AddBadge(codecLabel.ToUpper(), Colors.Orange);
            }
            else
            {
                 bool hasCodecText = TechBadgesPanel.Children.OfType<Border>().Any(b => (b.Child as TextBlock)?.Text.Contains("HEVC") == true || (b.Child as TextBlock)?.Text.Contains("264") == true);
                 if(!hasCodecText)
                 {
                    if (name.Contains("HEVC") || name.Contains("H.265") || name.Contains("X265") || name.Contains("H265")) 
                        AddBadge("HEVC", Colors.Orange);
                    else if (name.Contains("H.264") || name.Contains("X264") || name.Contains("AVC")) 
                        AddBadge("AVC", Colors.Gray);
                 }
            }

            // 3. HDR / SDR
            bool isHdrVisible = false;
            if (hasMetadata) 
            {
                isHdrVisible = isHdrMetadata ?? false;
            }
            else 
            {
                isHdrVisible = name.Contains("HDR") || name.Contains("DOLBY") || name.Contains("DV");
            }

            if (isHdrVisible) AddBadge("HDR", Colors.Gold);
            else if (hasMetadata) AddBadge("SDR", Colors.DimGray);
            
            // Standard Tooltip
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(PlayButton, stream.Title);
        }

        private void AddBadge(string text, Color color)
        {
             var border = new Border
             {
                 CornerRadius = new CornerRadius(3),
                 Padding = new Thickness(4, 1, 4, 1),
                 Background = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B)),
                 BorderBrush = new SolidColorBrush(Color.FromArgb(100, color.R, color.G, color.B)),
                 BorderThickness = new Thickness(1),
                 VerticalAlignment = VerticalAlignment.Center
             };
             
             var tb = new TextBlock
             {
                 Text = text,
                 FontSize = 10,
                 FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                 Foreground = new SolidColorBrush(Colors.White)
             };
             
             border.Child = tb;
             TechBadgesPanel.Children.Add(border);
        }

        private async Task PlayTrailer(string videoKey)
        {
            // Cancel previous Play requests
            _trailerCts?.Cancel();
            _trailerCts?.Dispose();
            _trailerCts = new System.Threading.CancellationTokenSource();
            var token = _trailerCts.Token;

            try 
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] PlayTrailer Requested: {videoKey}");

                // Only reinitialize if not already ready
                if (!_webViewInitialized)
                {
                    await PreInitializeWebViewAsync();
                }
                
                if (token.IsCancellationRequested) return;

                // Just call JavaScript to load the video - player is already ready
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Loading video: {videoKey}");

                // Check if it is a full URL or ID
                if (videoKey.StartsWith("http") && (videoKey.Contains("youtube.com") || videoKey.Contains("youtu.be")))
                {
                     // Extract ID from URL
                     try 
                     {
                        var uri = new Uri(videoKey);
                        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                        if (query.AllKeys.Contains("v"))
                        {
                            videoKey = query["v"];
                        }
                        else if (videoKey.Contains("youtu.be"))
                        {
                            videoKey = uri.Segments.Last();
                        }
                        System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Extracted ID from URL: {videoKey}");
                     }
                     catch
                     {
                        System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Failed to extract ID from URL: {videoKey}");
                     }
                }

                await TrailerWebView.CoreWebView2.ExecuteScriptAsync($"loadVideo('{videoKey}')");
                
                if (token.IsCancellationRequested) return;

                // Hide Ambience during trailer
                AmbienceGrid.Visibility = Visibility.Collapsed;
                
                // Show WebView, hide backdrop and loading IMMEDIATELY
                BackdropContainer.Visibility = Visibility.Collapsed;
                TrailerWebView.Visibility = Visibility.Visible;
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;

                _isMuted = true;
                UpdateMuteIcon();
                _ = RefreshMuteStateFromPlayerAsync(defaultMutedWhenUnknown: true);
            }
            catch (TaskCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] PlayTrailer Cancelled for {videoKey}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] PlayTrailer Error: {ex.Message}");
            }
        }
        
        private void CoreWebView2_WebMessageReceived(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] WebMessage: {message}");
                
                if (message == "PLAYER_READY")
                {
                    System.Diagnostics.Debug.WriteLine("[ExpandedCard] YouTube player ready for instant video loading");
                    _ = RefreshMuteStateFromPlayerAsync(defaultMutedWhenUnknown: true);
                }
                else if (message == "VIDEO_PLAYING")
                {
                    // Video started playing - show mute button and switch from backdrop to video
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        LoadingRing.IsActive = false;
                        LoadingRing.Visibility = Visibility.Collapsed;
                        
                        BackdropContainer.Visibility = Visibility.Collapsed;
                        TrailerWebView.Visibility = Visibility.Visible;
                        MuteButton.Visibility = Visibility.Visible;
                        ExpandButton.Visibility = Visibility.Visible;
                        _ = RefreshMuteStateFromPlayerAsync(defaultMutedWhenUnknown: true);
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] WebMessage Error: {ex.Message}");
            }
        }
        
        private bool _isMuted = true;

        private static string? ParseScriptString(string? rawResult)
        {
            if (string.IsNullOrWhiteSpace(rawResult)) return null;
            var value = rawResult.Trim();

            // WebView2 returns JSON encoded values ("\"muted\"" etc).
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value.Substring(1, value.Length - 2).Replace("\\\"", "\"");
            }
            return value;
        }

        private bool? ParseMuteState(string? rawResult)
        {
            var state = ParseScriptString(rawResult)?.ToLowerInvariant();
            return state switch
            {
                "muted" => true,
                "unmuted" => false,
                "true" => true,
                "false" => false,
                _ => null
            };
        }

        private async Task RefreshMuteStateFromPlayerAsync(bool defaultMutedWhenUnknown = true)
        {
            try
            {
                if (TrailerWebView.CoreWebView2 == null)
                {
                    _isMuted = defaultMutedWhenUnknown;
                    UpdateMuteIcon();
                    return;
                }

                var raw = await TrailerWebView.CoreWebView2.ExecuteScriptAsync("getMuteState()");
                var parsed = ParseMuteState(raw);
                _isMuted = parsed ?? defaultMutedWhenUnknown;
                UpdateMuteIcon();
            }
            catch
            {
                _isMuted = defaultMutedWhenUnknown;
                UpdateMuteIcon();
            }
        }

        private async Task SetMutedAsync(bool shouldMute)
        {
            try
            {
                if (TrailerWebView.CoreWebView2 == null)
                {
                    return;
                }

                var raw = await TrailerWebView.CoreWebView2.ExecuteScriptAsync($"setMuted({(shouldMute ? "true" : "false")})");
                var parsed = ParseMuteState(raw);
                _isMuted = parsed ?? shouldMute;
                UpdateMuteIcon();
            }
            catch
            {
                _ = RefreshMuteStateFromPlayerAsync(defaultMutedWhenUnknown: _isMuted);
            }
        }
        
        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TrailerWebView.CoreWebView2 != null)
                {
                    var raw = await TrailerWebView.CoreWebView2.ExecuteScriptAsync("toggleMute()");
                    var parsed = ParseMuteState(raw);
                    _isMuted = parsed ?? _isMuted;
                    UpdateMuteIcon();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Mute Error: {ex.Message}");
            }
        }
        
        private void UpdateMuteIcon()
        {
            // E74F = Volume, E74E = Mute
            MuteIcon.Glyph = _isMuted ? "\uE74F" : "\uE767";
        }

        // Color Adaptation Public Method
        public void SetAmbienceColor(Color color)
        {
            if (AmbienceGrid.Background is RadialGradientBrush brush)
            {
                if (brush.GradientStops.Count > 0)
                {
                    brush.GradientStops[0].Color = color;
                }
            }
        }

        private void UpdateUiFromUnified(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata unified, string? overrideTitle = null, string? overrideSubtitle = null, string? overrideOverview = null, string? overrideBackdrop = null)
        {
            if (unified == null) return;
            
            TitleText.Text = overrideTitle ?? unified.Title;
            GenresText.Text = overrideSubtitle ?? unified.Genres;
            DescText.Text = overrideOverview ?? unified.Overview;

            RatingText.Text = unified.Rating > 0 ? $"\u2605 {unified.Rating:F1}" : "";
            YearText.Text = unified.Year;
            
            // Hide skeleton and reveal description with staggered reveal
            MainSkeleton.Visibility = Visibility.Collapsed;
            // Note: BadgeSkeleton remains visible until Probe finishes (in UpdateTooltip)
            
            RealContentPanel.Opacity = 1; 
            
            var visualPanel = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(RealContentPanel);
            visualPanel.Opacity = 1f;

            StaggeredRevealContent();
            
            string backdropUrl = overrideBackdrop ?? unified.BackdropUrl;
            if (!string.IsNullOrEmpty(backdropUrl))
            {
                BackdropImage.Source = new BitmapImage(new Uri(backdropUrl));
            }

            // Mood Tag Logic (Mock)
            if (unified.Rating > 8.0)
            {
                MoodTag.Visibility = Visibility.Visible;
                MoodText.Text = "Top Rated";
                MoodTag.Background = new SolidColorBrush(Color.FromArgb(255, 0, 180, 0));
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
                    var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
                    
                    // CRITICAL: Set Visual Opacity to 0 initially
                    visual.Opacity = 0f;

                    try 
                    {
                        var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                        fadeIn.InsertKeyFrame(1f, 1f);
                        fadeIn.Duration = TimeSpan.FromMilliseconds(400);
                        fadeIn.DelayTime = TimeSpan.FromSeconds(delay);

                        // Define MoveUp Animation
                        var moveUp = _compositor.CreateVector3KeyFrameAnimation();
                        moveUp.InsertKeyFrame(0f, new System.Numerics.Vector3(0, 8, 0));
                        moveUp.InsertKeyFrame(1f, System.Numerics.Vector3.Zero);
                        moveUp.Duration = TimeSpan.FromMilliseconds(500);
                        moveUp.DelayTime = TimeSpan.FromSeconds(delay);

                        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                        
                        visual.StartAnimation("Opacity", fadeIn);
                        visual.StartAnimation("Translation", moveUp);
                    }
                    catch (Exception ex)
                    {
                         System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Staggered Animation Error: {ex.Message}");
                         visual.Opacity = 1f; // Ensure visible if animation fails
                    }

                    delay += staggerIncrement;
                }
            }
        }

        private void UpdatePlayButton(IMediaStream stream)
        {
            if (stream == null) return;
            
            bool isResume = false;
            string subtext = null;

            if (stream is LiveStream live)
            {
                var hist = HistoryManager.Instance.GetProgress(live.StreamId.ToString());
                if (hist != null && !hist.IsFinished && (hist.Position / (double)hist.Duration) > 0.005) isResume = true;
            }
            else if (stream is SeriesStream series)
            {
                var history = HistoryManager.Instance.GetLastWatchedEpisode(series.SeriesId.ToString());
                if (history != null) 
                {
                    isResume = true;
                    int displayEp = history.EpisodeNumber == 0 ? 1 : history.EpisodeNumber;
                    subtext = $"S{history.SeasonNumber:D2}E{displayEp:D2}";
                }
            }
            else if (stream is StremioMediaStream stremio)
            {
                if (stremio.Meta.Type == "series" || stremio.Meta.Type == "tv")
                {
                    var history = HistoryManager.Instance.GetLastWatchedEpisode(stremio.Meta.Id);
                    if (history != null)
                    {
                        isResume = true;
                        int displayEp = history.EpisodeNumber == 0 ? 1 : history.EpisodeNumber;
                        subtext = $"S{history.SeasonNumber:D2}E{displayEp:D2}";
                    }
                }
                else
                {
                    var history = HistoryManager.Instance.GetProgress(stremio.Meta.Id);
                    if (history != null && !history.IsFinished && (history.Position / (double)history.Duration) > 0.005) isResume = true;
                }
            }
            else if (stream is WatchlistItem w)
            {
                if (w.Type == "series")
                {
                    var history = HistoryManager.Instance.GetLastWatchedEpisode(w.Id);
                    if (history != null)
                    {
                        isResume = true;
                        int displayEp = history.EpisodeNumber == 0 ? 1 : history.EpisodeNumber;
                        subtext = $"S{history.SeasonNumber:D2}E{displayEp:D2}";
                    }
                }
                else
                {
                    var history = HistoryManager.Instance.GetProgress(w.Id);
                    if (history != null && !history.IsFinished && (history.Position / (double)history.Duration) > 0.005) isResume = true;
                }
            }

            if (isResume)
            {
                PlayButtonText.Text = "Devam Et";
                if (!string.IsNullOrEmpty(subtext))
                {
                    PlayButtonSubtext.Text = subtext;
                    PlayButtonSubtext.Visibility = Visibility.Visible;
                }
                else
                {
                    PlayButtonSubtext.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                PlayButtonText.Text = "Oynat";
                PlayButtonSubtext.Visibility = Visibility.Collapsed;
            }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e) => PlayClicked?.Invoke(this, EventArgs.Empty);
        
        private void DetailsButton_Click(object sender, RoutedEventArgs e) 
        {
            PrepareConnectedAnimation();
            DetailsClicked?.Invoke(this, _tmdbInfo);
        }

        private void DetailsArea_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) 
        {
            PrepareConnectedAnimation();
            DetailsClicked?.Invoke(this, _tmdbInfo);
        }

        private void TrailerArea_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            // Ignore if clicked on MuteButton or its children
            if (e.OriginalSource is DependencyObject obj)
            {
                var parent = obj;
                while (parent != null && parent != TrailerArea)
                {
                    if (parent == MuteButton || parent == ExpandButton) return;
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }

            PrepareConnectedAnimation();
            DetailsClicked?.Invoke(this, _tmdbInfo);
        }

        public void PrepareConnectedAnimation()
        {
            Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView()
                .PrepareToAnimate("ForwardConnectedAnimation", BackdropImage);
        }



        private void UpdateProgressState(IMediaStream stream)
        {
            if (stream == null) return;

            HistoryItem? history = null;
            if (stream is LiveStream live)
            {
                history = HistoryManager.Instance.GetProgress(live.StreamId.ToString());
            }
            else if (stream is SeriesStream series)
            {
                history = HistoryManager.Instance.GetLastWatchedEpisode(series.SeriesId.ToString());
            }
            else if (stream is StremioMediaStream stremio)
            {
                if (stremio.Meta.Type == "series" || stremio.Meta.Type == "tv")
                    history = HistoryManager.Instance.GetLastWatchedEpisode(stremio.Meta.Id);
                else
                    history = HistoryManager.Instance.GetProgress(stremio.Meta.Id);
            }
            else if (stream is WatchlistItem w)
            {
                if (w.Type == "series")
                    history = HistoryManager.Instance.GetLastWatchedEpisode(w.Id);
                else
                    history = HistoryManager.Instance.GetProgress(w.Id);
            }

            if (history != null && !history.IsFinished && history.Duration > 0)
            {
                double pct = (history.Position / history.Duration) * 100;
                if (pct > 0.5) // Show if more than 0.5% watched
                {
                    PlaybackProgressBar.Value = pct;
                    PlaybackProgressBar.Visibility = Visibility.Visible;

                    var remaining = TimeSpan.FromSeconds(history.Duration - history.Position);
                    string timeLeft = remaining.TotalHours >= 1
                        ? $"{(int)remaining.TotalHours}sa {remaining.Minutes}dk kaldı"
                        : $"{remaining.Minutes}dk kaldı";

                    // Premium Placement: Integrate into Play Button subtext
                    if (PlayButtonText.Text == "Devam Et")
                    {
                        string baseText = PlayButtonSubtext.Text;
                        if (!string.IsNullOrEmpty(baseText) && !baseText.Contains(timeLeft))
                        {
                             // If it already has S01E05, append.
                             PlayButtonSubtext.Text = $"{baseText} • {timeLeft}";
                        }
                        else if (string.IsNullOrEmpty(baseText))
                        {
                            PlayButtonSubtext.Text = timeLeft;
                        }
                        PlayButtonSubtext.Visibility = Visibility.Visible;
                    }

                    // TimeLeftText.Text = timeLeft;
                    // ProgressPanel.Visibility = Visibility.Visible;
                    return;
                }
            }

            PlaybackProgressBar.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }
}
