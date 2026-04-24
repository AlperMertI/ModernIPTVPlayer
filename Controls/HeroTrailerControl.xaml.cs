using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using ModernIPTVPlayer.Services;
using System;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class HeroTrailerControl : UserControl
    {
        public event EventHandler<bool> PlayStateChanged;
        public event EventHandler VideoEnded;
        public event EventHandler<int?> PlaybackError;

        private bool _isTrailerPlaying = false;
        private string _instanceId = Guid.NewGuid().ToString("N");
        private string? _pendingTrailerId = null;
        private bool _isInitialized = false;

        public bool IsPlaying => _isTrailerPlaying;

        public HeroTrailerControl()
        {
            this.InitializeComponent();
            this.Unloaded += (s, e) => _ = CleanupAsync();
            // Lazy init: WebView2 initialization is ~200-400ms and steals CPU from the hero reveal.
            // We no longer kick off PreInitializeAsync in the constructor. Instead it runs on the
            // first PlayTrailerAsync call, or via an idle warm-up once the hero is idle (see WarmUpIfIdle).
        }

        /// <summary>
        /// Optional hook for the parent to request warm-up *after* the hero has finished its reveal.
        /// Safe to call multiple times; only the first call triggers initialization.
        /// </summary>
        public void WarmUpIfIdle()
        {
            if (_isInitialized) return;
            _ = PreInitializeAsync();
        }

        public async Task PlayTrailerAsync(string? videoId)
        {
            if (string.IsNullOrEmpty(videoId)) return;

            try
            {
                if (!_isInitialized)
                {
                    await PreInitializeAsync();
                }

                if (WebView.CoreWebView2 == null) return;

                string virtualHost = $"hero-trailer-{_instanceId}.moderniptv.local";
                string currentSource = WebView.Source?.ToString() ?? "";
                
                if (currentSource.Contains(virtualHost))
                {
                    // [MODERN] Direct JS injection for persistent player
                    string result = await WebView.CoreWebView2.ExecuteScriptAsync($@"
                        if (window.loadVideo) {{
                            window.loadVideo('{videoId}');
                            'OK';
                        }} else {{
                            'WAIT';
                        }}");

                    // [FIX] Even if WAIT, do NOT navigate again if we are already on the right host.
                    // The in-progress navigation will pick up _pendingTrailerId once ready.
                    if (result == "\"OK\"") return;
                    _pendingTrailerId = videoId;
                    return; 
                }

                _pendingTrailerId = videoId;
                string targetUrl = $"https://{virtualHost}/index.html";
                System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Navigating to: {targetUrl} (ID: {videoId})");
                WebView.Source = new Uri(targetUrl);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Play Error: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            await CleanupAsync();
        }

        public async Task PreInitializeAsync()
        {
            if (_isInitialized) return;

            try
            {
                var env = await WebView2Service.GetSharedEnvironmentAsync();
                await WebView.EnsureCoreWebView2Async(env);
                
                await WebView2Service.ApplyYouTubeCleanUISettingsAsync(WebView.CoreWebView2);

                string virtualHost = $"hero-trailer-{_instanceId}.moderniptv.local";
                string baseUri = $"https://{virtualHost}/";
                
                // [FIX] Match original filter and protocol exactly
                WebView.CoreWebView2.AddWebResourceRequestedFilter(baseUri + "*", CoreWebView2WebResourceContext.All);
                WebView.CoreWebView2.WebResourceRequested += (s, args) =>
                {
                    if (args.Request.Uri.Contains("index.html"))
                    {
                        System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Resource Requested: {args.Request.Uri}");
                        string html = GetYouTubeBootstrapHtml(_pendingTrailerId ?? "");
                        var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
                        var stream = ms.AsRandomAccessStream();
                        args.Response = WebView.CoreWebView2.Environment.CreateWebResourceResponse(
                            stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
                        System.Diagnostics.Debug.WriteLine("[HeroTrailer] Resource Response Sent");
                    }
                };

                WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // [DIAGNOSTICS] Comprehensive navigation tracking
                WebView.CoreWebView2.NavigationStarting += (s, args) => System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Navigation Starting: {args.Uri}");
                WebView.CoreWebView2.NavigationCompleted += (s, args) => System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Navigation Completed: Success={args.IsSuccess}, Status={args.WebErrorStatus}");
                WebView.CoreWebView2.SourceChanged += (s, args) => System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Source Changed: {WebView.Source}");
                WebView.CoreWebView2.ProcessFailed += (s, args) => System.Diagnostics.Debug.WriteLine($"[HeroTrailer] CRITICAL: Process Failed! Reason: {args.Reason}, ExitCode: {args.ExitCode}");

                _isInitialized = true;
                System.Diagnostics.Debug.WriteLine("[HeroTrailer] WebView2 Initialization Complete (Shared Env)");

                // [FIX] Trigger immediate navigation to warm up the network and YouTube API
                string targetUrl = $"https://{virtualHost}/index.html";
                System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Warming up navigation: {targetUrl}");
                WebView.Source = new Uri(targetUrl);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Init Failed: {ex.Message}");
            }
        }

        private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string msg = args.TryGetWebMessageAsString();
                System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Web Message Received: {msg}");
                
                if (msg == "READY")
                {
                    if (_isTrailerPlaying) return;
                    _isTrailerPlaying = true;
                    WebView.Opacity = 1;
                    PlayStateChanged?.Invoke(this, true);
                }
                else if (msg == "ENDED")
                {
                    _ = CleanupAsync();
                    VideoEnded?.Invoke(this, EventArgs.Empty);
                }
                else if (msg == "ERROR" || msg.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                {
                    int? errorCode = null;
                    if (msg.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    {
                        string raw = msg.Substring("ERROR:".Length);
                        if (int.TryParse(raw, out int parsed)) errorCode = parsed;
                    }

                    PlaybackError?.Invoke(this, errorCode);
                    _ = CleanupAsync();
                    VideoEnded?.Invoke(this, EventArgs.Empty);
                }
                else if (msg == "LOG_READY")
                {
                     System.Diagnostics.Debug.WriteLine("[HeroTrailer] JS Player Initialized");
                }
            }
            catch { }
        }

        private async Task CleanupAsync()
        {
            _isTrailerPlaying = false;
            _pendingTrailerId = null;

            try 
            {
                if (WebView.CoreWebView2 != null)
                {
                    // [FIX] Robust cleanup: Pause video via JS before collapsing
                    await WebView.CoreWebView2.ExecuteScriptAsync("if(window.player && player.pauseVideo) player.pauseVideo();");
                    
                    // [FIX] Do NOT navigate to about:blank as it destroys the warm-up state (YouTube API load)
                    // Instead, navigate back to the base bootstrap page to stay 'Warm'
                    string virtualHost = $"hero-trailer-{_instanceId}.moderniptv.local";
                    WebView.Source = new Uri($"https://{virtualHost}/index.html");
                }
            }
            catch { }

            WebView.Opacity = 0;
            PlayStateChanged?.Invoke(this, false);
        }

        private string GetYouTubeBootstrapHtml(string ytId)
        {
            return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        html, body {{ width: 100%; height: 100%; background: #000; overflow: hidden; }}
        #player {{ position: absolute; top: 0; left: 0; width: 100%; height: 100%; }}
    </style>
</head>
<body>
    <div id='player'></div>
    <script>
        window.addEventListener('message', function(e) {{
            if (e.data && e.data.type === 'LOG_FORWARD') {{
                try {{ window.chrome.webview.postMessage(e.data.msg); }} catch(ex) {{}}
            }}
        }});

        function log(msg) {{ try {{ window.chrome.webview.postMessage('LOG:' + msg); }} catch(ex) {{}} }}
        
        var tag = document.createElement('script');
        tag.src = 'https://www.youtube.com/iframe_api';
        var firstScriptTag = document.getElementsByTagName('script')[0];
        firstScriptTag.parentNode.insertBefore(tag, firstScriptTag);

        var player;
        var isReady = false;
        var pendingVideoId = null;
        
        function normalizeYouTubeId(input) {{
            if (!input) return null;
            var raw = ('' + input).trim();
            if (!raw) return null;

            if (raw.indexOf('/') === -1 && raw.indexOf('.') === -1) return raw;

            try {{
                var url = new URL(raw);
                if (url.hostname.indexOf('youtu.be') >= 0) {{
                    var pathId = url.pathname.replace(/^\/+/, '').split('/')[0];
                    if (pathId) return pathId;
                }}

                if (url.hostname.indexOf('youtube.com') >= 0) {{
                    var v = url.searchParams.get('v');
                    if (v) return v;

                    var segments = url.pathname.replace(/^\/+/, '').split('/');
                    var embedIndex = segments.indexOf('embed');
                    if (embedIndex >= 0 && embedIndex + 1 < segments.length) return segments[embedIndex + 1];
                }}
            }} catch (e) {{
                log('normalizeYouTubeId failed: ' + e);
            }}

            return raw;
        }}
        
        window.loadVideo = function(id) {{
            log('loadVideo called for ' + id);
            if (!id) return;
            id = normalizeYouTubeId(id);
            if (!id) return;

            // [MODERN] Reset Smart Crop state for new video (Broadcast to all frames)
            try {{ 
                log('Broadcasting RESET_SMART_CROP...'); 
                window.chrome.webview.postMessage('RESET_SMART_CROP'); 
                
                // [NEW] Forward to YouTube iframe via standard postMessage
                var iframe = document.querySelector('iframe');
                if (iframe && iframe.contentWindow) {{
                    iframe.contentWindow.postMessage('RESET_SMART_CROP', '*');
                }}
            }} catch(e) {{
                 log('Manual transform reset fallback: ' + e);
                 var video = document.querySelector('video');
                 if (video) video.style.transform = 'scale(1)';
            }}
            
            if (isReady && player && player.loadVideoById) {{
                try {{
                    player.loadVideoById({{ 
                        videoId: id, 
                        startSeconds: 0,
                        suggestedQuality: 'highres' // prioritize highest available (4K support)
                    }});
                    player.playVideo();
                    log('loadVideoById triggered (HD prioritized)');
                }} catch(ex) {{
                    log('loadVideoById failed: ' + ex);
                }}
            }} else {{
                log('Player not ready, caching ID: ' + id);
                pendingVideoId = id;
            }}
        }};

        function onYouTubeIframeAPIReady() {{
            log('onYouTubeIframeAPIReady');
            player = new YT.Player('player', {{
                height: '100%', width: '100%',
                host: 'https://www.youtube.com',
                playerVars: {{
                    autoplay: 0, mute: 1, controls: 0, disablekb: 1,
                    fs: 0, rel: 0, modestbranding: 1, showinfo: 0,
                    iv_load_policy: 3, playsinline: 1, loop: 1
                }},
                events: {{
                    'onReady': onPlayerReady,
                    'onStateChange': onPlayerStateChange,
                    'onError': onPlayerError
                }}
            }});
        }}

        function onPlayerReady(event) {{
            log('onPlayerReady');
            isReady = true;
            try {{ player.mute(); }} catch (e) {{}}
            window.chrome.webview.postMessage('PLAYER_READY');
            
            if (pendingVideoId) {{
                log('Processing pending video: ' + pendingVideoId);
                loadVideo(pendingVideoId);
                pendingVideoId = null;
            }} else if ('{ytId}') {{
                log('Processing initial video: {ytId}');
                loadVideo('{ytId}');
            }}
        }}

        function onPlayerStateChange(event) {{
            log('onStateChange: ' + event.data);
            
            // [MODERN] Handle ENDED (0) to auto-return to backdrop
            if (event.data === 0) {{
                 log('Video ENDED, signaling auto-stop');
                 try {{ window.chrome.webview.postMessage('ENDED'); }} catch(ex) {{}}
            }}

            // [FIX] Signal READY on Playing (1) OR Buffering (3)
            if (event.data === 1 || event.data === 3) {{ 
                 log('Video reached ACTIVE state (Playing/Buffering)');
                 // [FIX] restored unMute as the environment now allows no-user-gesture autoplay
                 if (event.data === 1) {{
                    event.target.unMute(); 
                    // Attempt to prioritize quality once playing starts
                    try {{ event.target.setPlaybackQuality('highres'); }} catch(e) {{}}
                 }}
                 try {{ window.chrome.webview.postMessage('READY'); }} catch(ex) {{}}
            }}
        }}

        function onPlayerError(event) {{
            log('onError: ' + event.data);
            try {{ window.chrome.webview.postMessage('ERROR:' + event.data); }} catch(ex) {{}}
            try {{ window.chrome.webview.postMessage('ERROR'); }} catch(ex) {{}}
        }}
    </script>
</body>
</html>";
        }
    }
}
