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

        private void ExpandButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleCinemaMode(!_isCinemaMode);
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

        /// <summary>
        /// Public method to stop trailer playback when card is hidden
        /// </summary>
        public void StopTrailer()
        {
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
            if (TrailerWebView.CoreWebView2 != null && _webViewInitialized)
            {
                // Stop video via JavaScript - don't navigate away to preserve the player
                _ = TrailerWebView.CoreWebView2.ExecuteScriptAsync("stopVideo()");
            }
            
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
                    
                    // Ensure Skeleton is visible and ready to fade in
                    MainSkeleton.Visibility = Visibility.Visible;
                    BadgeSkeleton.Visibility = Visibility.Visible;
                    
                    visualSkeleton.Opacity = 0f;

                    try
                    {
                        var animOut = _compositor.CreateScalarKeyFrameAnimation();
                        animOut.InsertKeyFrame(1.0f, 0f);
                        animOut.Duration = TimeSpan.FromMilliseconds(200);
                        
                        var animIn = _compositor.CreateScalarKeyFrameAnimation();
                        animIn.InsertKeyFrame(1.0f, 1f);
                        animIn.Duration = TimeSpan.FromMilliseconds(200);
                        
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
                    BadgeSkeleton.Visibility = Visibility.Visible;
                    Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(MainSkeleton).Opacity = 1f;
                }
                
            // Reset badges
            TechBadgesPanel.Children.Clear();
            
            // Reset Play Button Subtext
            PlayButtonSubtext.Visibility = Visibility.Collapsed;
            PlayButtonSubtext.Text = "";
                
                // Reset mood tag
                MoodTag.Visibility = Visibility.Collapsed;
                
                // Reset ratings
                YearText.Visibility = Visibility.Visible;
                RatingText.Visibility = Visibility.Visible;
                RatingText.Text = "";
                YearText.Text = "";
                
                // Reset Ambience
                AmbienceGrid.Visibility = Visibility.Visible;
            }
            
            // Reset loading ring
            if (!isStopping)
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
            }
            // Start Shimmers explicitly (Loaded event doesn't fire on Visibility toggle)
            // UPDATE: Moved logic to ShimmerControl.cs (Loaded/Unloaded) for cleaner approach.
            // if (!isStopping)
            // {
            //    RestartShimmers(FullSkeleton);
            // }
            if (isStopping)
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
            UpdatePlayButton(stream);

            try
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Searching TMDB for: {stream.Title}");
                
                string extractedYear = TmdbHelper.ExtractYear(stream.Title);
                Task<TmdbMovieResult?> tmdbTask = (stream is SeriesStream) 
                    ? TmdbHelper.SearchTvAsync(stream.Title, extractedYear)
                    : TmdbHelper.SearchMovieAsync(stream.Title, extractedYear);
                
                // Fire and forget probing (or await it later) BUT don't block TMDB UI update
                // Current issue: The user waits for 5s probe before seeing ANY text.
                // Fix: Await TMDB, Update UI, THEN await Probe.
                
                var tmdb = await tmdbTask;
                if (loadNonce != _loadNonce) return;
                
                if (tmdb != null)
                {
                    _tmdbInfo = tmdb;
                    if (stream != null) stream.TmdbInfo = tmdb;

                    string displayTitle = tmdb.DisplayTitle;
                    string displaySubtitle = tmdb.GetGenreNames();
                    string displayOverview = tmdb.Overview;
                    string displayBackdrop = tmdb.FullBackdropUrl;

                    // --- EPISODE RESUME LOGIC (Might interact with cache, but usually fast) ---
                    // If this logic is slow, it should also be separated, but HistoryManager is usually memory/fast disk.
                    if (stream is SeriesStream series)
                    {
                        var history = HistoryManager.Instance.GetLastWatchedEpisode(series.SeriesId.ToString());
                        if (history != null)
                        {
                            var season = await TmdbHelper.GetSeasonDetailsAsync(tmdb.Id, history.SeasonNumber);
                            if (season?.Episodes != null)
                            {
                                var ep = season.Episodes.FirstOrDefault(e => e.EpisodeNumber == history.EpisodeNumber);
                                if (ep == null && history.EpisodeNumber == 0)
                                    ep = season.Episodes.FirstOrDefault(e => e.EpisodeNumber == 1);

                                if (ep != null)
                                {
                                    string epName = ep.Name;
                                    string cleanIptv = TmdbHelper.CleanEpisodeTitle(history.Title);
                                    bool isGeneric = string.IsNullOrEmpty(epName) || epName.Contains("BÃ¶lÃ¼m") || epName.Contains("Episode") || epName == ep.EpisodeNumber.ToString();

                                    if (isGeneric && !string.IsNullOrEmpty(cleanIptv) && cleanIptv.Length > 2)
                                        displayTitle = cleanIptv;
                                    else
                                        displayTitle = !string.IsNullOrEmpty(epName) ? epName : $"BÃ¶lÃ¼m {ep.EpisodeNumber}";

                                    if (!string.IsNullOrEmpty(ep.Overview)) displayOverview = ep.Overview;
                                    if (!string.IsNullOrEmpty(ep.StillUrl)) displayBackdrop = ep.StillUrl;
                                }
                            }
                        }
                    }

                    // Update UI IMMEDIATELY with TMDB info
                    UpdateUiWithTmdb(tmdb, displayTitle, displaySubtitle, displayOverview, displayBackdrop);

                    // NOW Fetch Trailer (Async)
                    var trailerKey = await TmdbHelper.GetTrailerKeyAsync(tmdb.Id, stream is SeriesStream);
                    if (loadNonce != _loadNonce) return;
                    if (!string.IsNullOrEmpty(trailerKey))
                    {
                         PlayTrailer(trailerKey);
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
                        var loginParams = new Services.LoginParams
                        {
                            Host = App.CurrentLogin.Host,
                            Username = App.CurrentLogin.Username,
                            Password = App.CurrentLogin.Password,
                            PlaylistUrl = App.CurrentLogin.PlaylistUrl
                        };
                        var info = await Services.ContentCacheService.Instance.GetSeriesInfoAsync(series.SeriesId, loginParams);
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

            if (string.IsNullOrEmpty(url)) return;

            try
            {
                // 1. Check Cache
                if (Services.ProbeCacheService.Instance.Get(url) is Services.ProbeData cached)
                {
                    Services.CacheLogger.Success(Services.CacheLogger.Category.Probe, "ExpandedCard Cache Hit", url);
                    if (loadNonce == _loadNonce)
                    {
                        BadgeSkeleton.Visibility = Visibility.Collapsed;
                        ApplyProbeResult(stream, cached, loadNonce);
                    }
                    return;
                }

                // 2. Probe Network
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
            if (stream is LiveStream live) live.IsProbing = isProbing;
            else if (stream is SeriesStream series) series.IsProbing = isProbing;
        }

        private void ApplyProbeResult(IMediaStream stream, Services.ProbeData result, long loadNonce)
        {
            if (loadNonce != _loadNonce) return;

            if (stream is LiveStream live)
            {
                live.Resolution = result.Resolution;
                live.Fps = result.Fps;
                live.Codec = result.Codec;
                live.Bitrate = result.Bitrate;
                live.IsOnline = true; // Cached implies success
                live.IsHdr = result.IsHdr;
            }
            else if (stream is SeriesStream series)
            {
                series.Resolution = result.Resolution;
                series.Fps = result.Fps;
                series.Codec = result.Codec;
                series.Bitrate = result.Bitrate;
                series.IsOnline = true;
                series.IsHdr = result.IsHdr;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (loadNonce != _loadNonce) return;
                BadgeSkeleton.Visibility = Visibility.Collapsed;
                UpdateTooltip(stream);
                UpdatePlayButton(stream);
            });
        }

        private void UpdateTooltip(ModernIPTVPlayer.Models.IMediaStream stream)
        {
            TechBadgesPanel.Children.Clear();
            
            // 1. Logic moved to Play Button subtext for premium look
            if (stream is LiveStream live)
            {
                 var hist = HistoryManager.Instance.GetProgress(live.StreamId.ToString());
                 if (hist != null && !hist.IsFinished && hist.Duration > 0)
                 {
                     double pct = (hist.Position / hist.Duration) * 100;
                     if (pct > 2) 
                     {
                         var remaining = TimeSpan.FromSeconds(hist.Duration - hist.Position);
                         string timeLeft = remaining.TotalHours >= 1 
                             ? $"{remaining.Hours}sa {remaining.Minutes}dk KaldÄ±"
                             : $"{remaining.Minutes}dk KaldÄ±";
                             
                         AddBadge(timeLeft, Colors.Crimson);
                     }
                 }

                 // Probing Results - Handled in Dedup sections below
            }

            // Fallback / Additional Guesses from Name
            string name = stream.Title.ToUpperInvariant();

            // 2. Metadata Extraction (Unified)
            string res = null;
            string codecLabel = null;
            bool? isHdrMetadata = null;
            bool hasMetadata = false;

            if (stream is LiveStream l)
            {
                res = l.Resolution;
                codecLabel = l.Codec;
                isHdrMetadata = l.IsHdr;
                hasMetadata = l.HasMetadata;
            }
            else if (stream is SeriesStream s)
            {
                res = s.Resolution;
                codecLabel = s.Codec;
                isHdrMetadata = s.IsHdr;
                hasMetadata = s.HasMetadata;
            }

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

        private async void PlayTrailer(string videoKey)
        {
            try 
            {
                // Wait for pre-initialization if not ready
                if (!_webViewInitialized)
                {
                    await PreInitializeWebViewAsync();
                }
                
                // Just call JavaScript to load the video - player is already ready
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Loading video: {videoKey}");
                await TrailerWebView.CoreWebView2.ExecuteScriptAsync($"loadVideo('{videoKey}')");
                
                // Hide Ambience during trailer
                AmbienceGrid.Visibility = Visibility.Collapsed;
                
                // Show WebView, hide backdrop and loading IMMEDIATELY
                // Waiting for message is too risky if API doesn't report it soon enough
                BackdropContainer.Visibility = Visibility.Collapsed;
                TrailerWebView.Visibility = Visibility.Visible;
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;

                _isMuted = true;
                UpdateMuteIcon();
                _ = RefreshMuteStateFromPlayerAsync(defaultMutedWhenUnknown: true);
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

        private void UpdateUiWithTmdb(TmdbMovieResult tmdb, string? overrideTitle = null, string? overrideSubtitle = null, string? overrideOverview = null, string? overrideBackdrop = null)
        {
            if (tmdb == null) return;
            
            TitleText.Text = overrideTitle ?? tmdb.DisplayTitle;
            GenresText.Text = overrideSubtitle ?? tmdb.GetGenreNames();
            DescText.Text = overrideOverview ?? tmdb.Overview;

            RatingText.Text = $"â˜… {tmdb.VoteAverage:F1}";
            YearText.Text = tmdb.DisplayDate?.Split('-')[0] ?? "";
            
            // Hide skeleton and reveal description with staggered reveal
            MainSkeleton.Visibility = Visibility.Collapsed;
            // Note: BadgeSkeleton remains visible until Probe finishes (in UpdateTooltip)
            
            RealContentPanel.Opacity = 1; 
            
            var visualPanel = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(RealContentPanel);
            visualPanel.Opacity = 1f;

            StaggeredRevealContent();
            
            string backdropUrl = overrideBackdrop ?? tmdb.FullBackdropUrl;
            if (!string.IsNullOrEmpty(backdropUrl))
            {
                BackdropImage.Source = new BitmapImage(new Uri(backdropUrl));
            }

            // Mood Tag Logic (Mock)
            if (tmdb.VoteAverage > 8.0)
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
                if (hist != null && !hist.IsFinished && (hist.Position / (double)hist.Duration) > 0.05) isResume = true;
            }
            else if (stream is SeriesStream series)
            {
                var history = HistoryManager.Instance.GetLastWatchedEpisode(series.SeriesId.ToString());
                if (history != null) 
                {
                    isResume = true;
                    // Fix: If it's 0, display as 01 for the label (or keep 00 if provider really has 0)
                    // But usually 0 means "start". Let's use D2 and if 0, maybe shift.
                    // Actually, let's keep it raw but if it was 0, and we show it as episode 1 in title, 
                    // maybe we should be consistent.
                    int displayEp = history.EpisodeNumber;
                    if (displayEp == 0) displayEp = 1; // User said "BÃ¶lÃ¼m 1'deyim" but it showed 0.
                    
                    subtext = $"S{history.SeasonNumber:D2}E{displayEp:D2}";
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

        private void FavButton_Click(object sender, RoutedEventArgs e) => AddListClicked?.Invoke(this, EventArgs.Empty);
    }
}
