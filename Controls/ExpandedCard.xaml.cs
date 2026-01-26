using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Threading.Tasks;
using Windows.UI;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class ExpandedCard : UserControl
    {
        public event EventHandler PlayClicked;
        public event EventHandler DetailsClicked;
        public event EventHandler AddListClicked;
        
        // Hold data
        private LiveStream _stream;
        private TmdbMovieResult _tmdbInfo;
        
        // Pre-initialization state
        private bool _webViewInitialized = false;
        private bool _youtubePlayerReady = false;
        private string _trailerFolder;
        private string _virtualHost = "trailers.moderniptv.local";

        public ExpandedCard()
        {
            this.InitializeComponent();
            // Note: WebView2 is NOT pre-initialized here to save RAM
            // Use PrepareForTrailer() for predictive prefetch during hover delay
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
            ResetState();
        }

        /// <summary>
        /// Resets the card to initial state before loading new data
        /// </summary>
        private void ResetState()
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
            
            // Reset description skeleton
            DescText.Opacity = 0;
            DescText.Text = "";
            DescSkeleton.Visibility = Visibility.Visible;
            
            // Reset badges
            TechBadgesPanel.Children.Clear();
            
            // Reset mood tag
            MoodTag.Visibility = Visibility.Collapsed;
            
            // Reset ratings
            YearText.Visibility = Visibility.Visible;
            RatingText.Visibility = Visibility.Visible;
            RatingText.Text = "";
            YearText.Text = "";
            
            // Reset loading ring
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;
            
            // CRITICAL: DO NOT clear BackdropImage.Source here!
            // If we are morphing, we want to show the OLD image flying until the NEW image loads.
            // Clearing it causes a "blink" or invisible (black) animation.
        }

        public async Task LoadDataAsync(LiveStream stream)
        {
            // Reset all state first (except image)
            ResetState();
            
            _stream = stream;
            TitleText.Text = stream.Name;
            
            // Set initial low-res image (or clear it if none)
            if (!string.IsNullOrEmpty(stream.IconUrl))
            {
                BackdropImage.Source = new BitmapImage(new Uri(stream.IconUrl));
            }
            else
            {
                BackdropImage.Source = null;
            }
            
            // Initial Tooltip (Static parse)
            UpdateTooltip(stream);

            try
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Searching TMDB for: {stream.Name}");
                
                // Parallel Execution: TMDB + Probing
                var tmdbTask = TmdbHelper.SearchMovieAsync(stream.Name);
                var probeTask = ProbeStreamInternal(stream);
                
                await Task.WhenAll(tmdbTask, probeTask);
                
                var tmdb = tmdbTask.Result;
                
                if (tmdb != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExpandedCard] TMDB Match: {tmdb.Title} (ID: {tmdb.Id})");
                    _tmdbInfo = tmdb;
                    UpdateUiWithTmdb(tmdb);
                    
                    // Fetch and play trailer
                    var trailerKey = await TmdbHelper.GetTrailerKeyAsync(tmdb.Id);
                    if (!string.IsNullOrEmpty(trailerKey))
                    {
                        System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Playing Trailer: {trailerKey}");
                        PlayTrailer(trailerKey);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[ExpandedCard] No trailer key returned.");
                    }
                }
                else
                {
                    // Fallback UI - hide skeleton, show fallback text
                    DescText.Text = "No additional details found.";
                    DescSkeleton.Visibility = Visibility.Collapsed;
                    DescText.Opacity = 1;
                    
                    YearText.Visibility = Visibility.Collapsed;
                    RatingText.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Error: {ex.Message}");
                
                // Show error state
                DescText.Text = "Error loading details.";
                DescSkeleton.Visibility = Visibility.Collapsed;
                DescText.Opacity = 1;
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }
        
        private async Task ProbeStreamInternal(LiveStream stream)
        {
            if (stream.HasMetadata || stream.IsProbing) return;
            
            try
            {
                stream.IsProbing = true;
                var result = await _prober.ProbeAsync(stream.StreamUrl);
                
                stream.Resolution = result.Res;
                stream.Fps = result.Fps;
                stream.Codec = result.Codec;
                stream.Bitrate = result.Bitrate;
                stream.IsOnline = result.Success;
                
                // Update Tooltip on UI thread (we are already on UI thread in this context usually, 
                // but ProbeAsync might context switch).
                UpdateTooltip(stream);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Probing Failed: {ex.Message}");
            }
            finally
            {
                stream.IsProbing = false;
            }
        }

        private void UpdateTooltip(LiveStream stream)
        {
            // We now update VISIBLE badges instead of just a tooltip
            TechBadgesPanel.Children.Clear();

            // 1. Resolution Badge
            if (!string.IsNullOrEmpty(stream.Resolution))
            {
                AddBadge(stream.Resolution, Colors.Teal);
            }
            else
            {
                // Guess
                if (stream.Name.Contains("4K") || stream.Name.Contains("UHD")) AddBadge("4K UHD", Colors.Purple);
                else if (stream.Name.Contains("FHD") || stream.Name.Contains("1080")) AddBadge("1080p", Colors.Teal);
            }

            // 2. Codec Badge
            if (!string.IsNullOrEmpty(stream.Codec))
            {
                AddBadge(stream.Codec.ToUpper(), Colors.Orange); // e.g. HEVC
            }
            else if (stream.Name.Contains("HEVC") || stream.Name.Contains("H.265"))
            {
                 AddBadge("HEVC", Colors.Orange);
            }
            
            // 3. HDR / Audio (Future)
            
            // 4. Bitrate
            if (stream.Bitrate > 0)
            {
                double mbps = stream.Bitrate / 1_000_000.0;
                AddBadge($"{mbps:F1} Mbps", Colors.Gray);
            }
            
            // Also keep standard tooltip for Play Button
             var techInfo = "Format: " + (stream.ContainerExtension?.ToUpper() ?? "MP4");
             Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(PlayButton, techInfo);
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
                
                // Show WebView, hide backdrop
                BackdropContainer.Visibility = Visibility.Collapsed;
                TrailerWebView.Visibility = Visibility.Visible;
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
                    // Video started playing - show mute button
                    DispatcherQueue.TryEnqueue(() =>
                    {
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
            TitleText.Text = tmdb.Title;
            DescText.Text = tmdb.Overview;
            RatingText.Text = $"★ {tmdb.VoteAverage:F1}";
            
            // Hide skeleton and show description
            DescSkeleton.Visibility = Visibility.Collapsed;
            DescText.Opacity = 1;
            
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

        private void PlayButton_Click(object sender, RoutedEventArgs e) => PlayClicked?.Invoke(this, EventArgs.Empty);
        private void DetailsButton_Click(object sender, RoutedEventArgs e) => DetailsClicked?.Invoke(this, EventArgs.Empty);
        private void DetailsArea_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => DetailsClicked?.Invoke(this, EventArgs.Empty);
        private void FavButton_Click(object sender, RoutedEventArgs e) => AddListClicked?.Invoke(this, EventArgs.Empty);
    }
}
