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
        
        // Hold data
        private ModernIPTVPlayer.Models.IMediaStream _stream;
        private TmdbMovieResult _tmdbInfo;
        
        // Pre-initialization state
        private bool _webViewInitialized = false;
        private bool _youtubePlayerReady = false;
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
                
                // OPTIMIZATION: Don't rewrite file if it exists (saves I/O)
                if (!System.IO.File.Exists(htmlFilePath))
                {
                    string htmlContent = CreateYouTubePlayerHtml();
                    await System.IO.File.WriteAllTextAsync(htmlFilePath, htmlContent);
                }
                
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
                    playsinline: 1
                },
                events: {
                    'onReady': onPlayerReady,
                    'onStateChange': onPlayerStateChange
                }
            });
        }
        
        function onPlayerReady(event) {
            isReady = true;
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
                window.chrome.webview.postMessage('VIDEO_PLAYING');
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
                suggestedQuality: 'hd720'
            });
            player.playVideo();
        }
        
        // Called from C# to stop playback
        function stopVideo() {
            if (player && isReady) {
                player.stopVideo();
            }
        }
        
        function toggleMute() {
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

        /// <summary>
        /// Public method to stop trailer playback when card is hidden
        /// </summary>
        public void StopTrailer()
        {
            ResetState(isStopping: true);
        }

        /// <summary>
        /// Resets the card to initial state before loading new data
        /// </summary>
        private void ResetState(bool isStopping = false)
        {
            // Reset trailer/WebView - stop video via JavaScript (preserve pre-initialized player)
            TrailerWebView.Visibility = Visibility.Collapsed;
            MuteButton.Visibility = Visibility.Collapsed;
            if (TrailerWebView.CoreWebView2 != null && _webViewInitialized)
            {
                // Stop video via JavaScript - don't navigate away to preserve the player
                _ = TrailerWebView.CoreWebView2.ExecuteScriptAsync("stopVideo()");
            }
            
            // Show backdrop container again
            BackdropContainer.Visibility = Visibility.Visible;
            
            // Reset content visibility
            RealContentPanel.Opacity = 0;
            FullSkeleton.Visibility = Visibility.Visible;
            
            // Reset badges
            TechBadgesPanel.Children.Clear();
            
            // Reset mood tag
            MoodTag.Visibility = Visibility.Collapsed;
            
            // Reset ratings
            YearText.Visibility = Visibility.Visible;
            RatingText.Visibility = Visibility.Visible;
            RatingText.Text = "";
            YearText.Text = "";
            
            // Reset Ambience
            AmbienceGrid.Visibility = Visibility.Visible;
            
            // Reset loading ring
            if (!isStopping)
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
            }
            else
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
            
            // CRITICAL: DO NOT clear BackdropImage.Source here!
            // If we are morphing, we want to show the OLD image flying until the NEW image loads.
            // Clearing it causes a "blink" or invisible (black) animation.
        }

        public async Task LoadDataAsync(ModernIPTVPlayer.Models.IMediaStream stream)
        {
            // Reset all state first (except image)
            ResetState();
            
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
            
            // Ensure History is Ready
            await HistoryManager.Instance.InitializeAsync();
            
            // Initial Tooltip (Static parse)
            UpdateTooltip(stream);
            UpdatePlayButton(stream);

            try
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Searching TMDB for: {stream.Title}");
                
                // Parallel Execution: TMDB + Probing
                Task<TmdbMovieResult?> tmdbTask;
                string extractedYear = TmdbHelper.ExtractYear(stream.Title);
                
                if (stream is SeriesStream)
                {
                     tmdbTask = TmdbHelper.SearchTvAsync(stream.Title, extractedYear);
                }
                else
                {
                     tmdbTask = TmdbHelper.SearchMovieAsync(stream.Title, extractedYear);
                }
                
                // Probing for everything (if applicable)
                Task probeTask = ProbeStreamInternal(stream);
                
                await Task.WhenAll(tmdbTask, probeTask);
                
                var tmdb = tmdbTask.Result;
                
                if (tmdb != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExpandedCard] TMDB Match: {tmdb.DisplayTitle} (ID: {tmdb.Id})");
                    _tmdbInfo = tmdb;
                    if (stream != null) stream.TmdbInfo = tmdb; // Save to stream for later navigation
                    UpdateUiWithTmdb(tmdb);
                    
                    // Fetch and play trailer
                    var trailerKey = await TmdbHelper.GetTrailerKeyAsync(tmdb.Id, stream is SeriesStream);
                    if (!string.IsNullOrEmpty(trailerKey))
                    {
                         PlayTrailer(trailerKey);
                    }
                }
                else
                {
                    // Fallback UI
                    DescText.Text = "No additional details found.";
                    FullSkeleton.Visibility = Visibility.Collapsed;
                    RealContentPanel.Opacity = 1;
                    
                    YearText.Visibility = Visibility.Collapsed;
                    RatingText.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Error: {ex.Message}");
                DescText.Text = "Error loading details.";
                FullSkeleton.Visibility = Visibility.Collapsed;
                RealContentPanel.Opacity = 1;
            }
            finally
            {
                // Simple hide - if a trailer is starting, it will handle hiding the ring later
                // But for safety, if no trailer was found or it failed, we hide it here
                if (_tmdbInfo == null || !(_tmdbInfo.Id > 0))
                {
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                }
            }
        }
        
        private async Task ProbeStreamInternal(IMediaStream stream)
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
                if (history != null) url = history.StreamUrl;
            }

            if (string.IsNullOrEmpty(url)) return;

            try
            {
                // Check Cache FIRST
                if (ProbeCacheManager.TryGet(url, out var cached))
                {
                    ApplyProbeResult(stream, cached);
                    return;
                }

                SetProbing(stream, true);
                var result = await _prober.ProbeAsync(url);
                
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
                ApplyProbeResult(stream, probeResult);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Probing Failed: {ex.Message}");
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

        private void ApplyProbeResult(IMediaStream stream, ProbeResult result)
        {
            if (stream is LiveStream live)
            {
                live.Resolution = result.Res;
                live.Fps = result.Fps;
                live.Codec = result.Codec;
                live.Bitrate = result.Bitrate;
                live.IsOnline = result.Success;
                live.IsHdr = result.IsHdr;
            }
            else if (stream is SeriesStream series)
            {
                series.Resolution = result.Res;
                series.Fps = result.Fps;
                series.Codec = result.Codec;
                series.Bitrate = result.Bitrate;
                series.IsOnline = result.Success;
                series.IsHdr = result.IsHdr;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateTooltip(stream);
                UpdatePlayButton(stream);
            });
        }

        private void UpdateTooltip(ModernIPTVPlayer.Models.IMediaStream stream)
        {
            TechBadgesPanel.Children.Clear();
            
            // SERIES RESUME BADGE
            if (stream is SeriesStream series)
            {
                var history = HistoryManager.Instance.GetLastWatchedEpisode(series.SeriesId.ToString());
                if (history != null)
                {
                    AddBadge($"S{history.SeasonNumber} E{history.EpisodeNumber}", Colors.Crimson);
                    
                    // Also guess 4K if series name says so, but give history priority
                }
            }
            else if (stream is LiveStream live)
            {
                 var hist = HistoryManager.Instance.GetProgress(live.StreamId.ToString());
                 if (hist != null && !hist.IsFinished && hist.Duration > 0)
                 {
                     double pct = (hist.Position / hist.Duration) * 100;
                     if (pct > 2) 
                     {
                         var remaining = TimeSpan.FromSeconds(hist.Duration - hist.Position);
                         string timeLeft = remaining.TotalHours >= 1 
                             ? $"{remaining.Hours}sa {remaining.Minutes}dk Kaldı"
                             : $"{remaining.Minutes}dk Kaldı";
                             
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
                    _youtubePlayerReady = true;
                    System.Diagnostics.Debug.WriteLine("[ExpandedCard] YouTube player ready for instant video loading");
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
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] WebMessage Error: {ex.Message}");
            }
        }
        
        private bool _isMuted = true;
        
        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TrailerWebView.CoreWebView2 != null)
                {
                    await TrailerWebView.CoreWebView2.ExecuteScriptAsync("toggleMute()");
                    _isMuted = !_isMuted;
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

        private void UpdateUiWithTmdb(TmdbMovieResult tmdb)
        {
            TitleText.Text = tmdb.DisplayTitle;
            DescText.Text = tmdb.Overview;
            GenresText.Text = tmdb.GetGenreNames();
            RatingText.Text = $"★ {tmdb.VoteAverage:F1}";
            YearText.Text = tmdb.DisplayDate?.Split('-')[0] ?? "";
            
            // Hide skeleton and reveal description with staggered reveal
            FullSkeleton.Visibility = Visibility.Collapsed;
            RealContentPanel.Opacity = 1; // Parent is opaque
            StaggeredRevealContent();
            
            // High-res backdrop
            if (!string.IsNullOrEmpty(tmdb.FullBackdropUrl))
            {
                BackdropImage.Source = new BitmapImage(new Uri(tmdb.FullBackdropUrl));
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

                    var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(1f, 1f);
                    fadeIn.Duration = TimeSpan.FromMilliseconds(400);
                    fadeIn.DelayTime = TimeSpan.FromSeconds(delay);

                    // Add slight lift
                    Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                    var moveUp = _compositor.CreateVector3KeyFrameAnimation();
                    moveUp.InsertKeyFrame(0f, new System.Numerics.Vector3(0, 8, 0));
                    moveUp.InsertKeyFrame(1f, System.Numerics.Vector3.Zero);
                    moveUp.Duration = TimeSpan.FromMilliseconds(500);
                    moveUp.DelayTime = TimeSpan.FromSeconds(delay);

                    visual.StartAnimation("Opacity", fadeIn);
                    visual.StartAnimation("Translation", moveUp);

                    delay += staggerIncrement;
                }
            }
        }

        private void UpdatePlayButton(IMediaStream stream)
        {
            if (stream == null) return;
            
            bool isResume = false;
            if (stream is LiveStream live)
            {
                var hist = HistoryManager.Instance.GetProgress(live.StreamId.ToString());
                if (hist != null && !hist.IsFinished && (hist.Position / (double)hist.Duration) > 0.05) isResume = true;
            }
            else if (stream is SeriesStream series)
            {
                var history = HistoryManager.Instance.GetLastWatchedEpisode(series.SeriesId.ToString());
                if (history != null && history.Duration > 0 && (history.Position / (double)history.Duration) > 0.05) isResume = true;
            }

            if (isResume)
            {
                PlayButtonText.Text = "Devam Et";
            }
            else
            {
                PlayButtonText.Text = "Play";
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
