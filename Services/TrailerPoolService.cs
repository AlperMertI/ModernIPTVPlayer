using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Composition;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Web.WebView2.Core;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;
using Windows.Foundation;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// [SENIOR ARCHITECTURE] Global Resource Engine.
    /// Manages exactly ONE Warm WebView2 instance for the entire application using a State Machine.
    /// </summary>
    public sealed class TrailerPoolService
    {
        private static readonly Lazy<TrailerPoolService> _instance = new(() => new TrailerPoolService());
        public static TrailerPoolService Instance => _instance.Value;

        private WebView2 _sharedWebView;
        private Grid _currentContainer;

        public enum EngineState { Idle, Initializing, Ready, Faulted, NotInstalled }
        private EngineState _state = EngineState.Idle;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _lastQualitySetting = -1;

        public EngineState State => _state;
        public Grid? CurrentContainer => _currentContainer;
        public WebView2? SharedWebView => _sharedWebView;
        
        public event EventHandler<string> TrailerMessageReceived;

        private TrailerPoolService() { }
        
        /// <summary>
        /// Global helper to resolve YouTube IDs from various string formats
        /// </summary>
        public static string ExtractYouTubeId(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            if (!source.Contains("/") && !source.Contains(".")) return source; // Already an ID

            try {
                if (source.Contains("v=")) {
                    var split = source.Split("v=");
                    if (split.Length > 1) return split[1].Split('&')[0];
                } else if (source.Contains("be/")) {
                    var split = source.Split("be/");
                    if (split.Length > 1) return split[1].Split('?')[0];
                } else if (source.Contains("embed/")) {
                    var split = source.Split("embed/");
                    if (split.Length > 1) return split[1].Split('?')[0];
                }
            } catch { }
            return source;
        }

        /// <summary>
        /// Ensures the engine is fully initialized before proceeding.
        /// If initialization is already in progress, it will await the existing task.
        /// </summary>
        public async Task<bool> EnsureReadyAsync()
        {
            await _initLock.WaitAsync();
            try
            {
                // [DYNAMIC RECOVERY] If quality changed, invalidate current ready engine
                if (_state == EngineState.Ready && _lastQualitySetting != AppSettings.TrailerQuality)
                {
                    Debug.WriteLine("[TrailerPool] Quality mismatch detected. Refreshing engine...");
                    _state = EngineState.Idle;
                    _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                if (_state == EngineState.Ready) return true;
                if (_state == EngineState.Faulted || _state == EngineState.NotInstalled) return false;

                if (_state == EngineState.Idle)
                {
                    // [CRITICAL] Set state to Initializing BEFORE enqueuing to prevent duplicate inits
                    _state = EngineState.Initializing;

                    if (App.MainWindow?.DispatcherQueue != null)
                    {
                        App.MainWindow.DispatcherQueue.TryEnqueue(async () => await InitializeAsync());
                    }
                    else
                    {
                        // Fallback
                        _ = InitializeAsync();
                    }
                }
            }
            finally
            {
                _initLock.Release();
            }

            return await _readyTcs.Task;
        }

        public async Task<WebView2> AcquireAsync(Grid container, string trailerSource = "YouTube")
        {
            if (container == null) return null;

            bool isReady = await EnsureReadyAsync();
            if (!isReady || _sharedWebView == null) return null;

            try
            {
                // Verify if WebView2 process is still alive by accessing a property
                if (_sharedWebView.CoreWebView2 == null) 
                {
                    Debug.WriteLine("[TrailerPool] CoreWebView2 is null during acquisition. Resetting...");
                    _state = EngineState.Idle;
                    _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return await AcquireAsync(container, trailerSource);
                }

                if (_sharedWebView.Parent is Grid oldParent)
                {
                    oldParent.Children.Remove(_sharedWebView);
                }

                _currentContainer = container;
                _sharedWebView.Visibility = Visibility.Visible;
                _sharedWebView.Opacity = 1; 
                _sharedWebView.HorizontalAlignment = HorizontalAlignment.Stretch;
                _sharedWebView.VerticalAlignment = VerticalAlignment.Stretch;
                
                if (!container.Children.Contains(_sharedWebView))
                {
                    container.Children.Add(_sharedWebView);
                }

                return _sharedWebView;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrailerPool] ACQUIRE ERROR: ({ex.GetType().Name}) {ex.Message}");
                // If it's a COM error, the engine might be dead. Reset state to allow one-time re-init.
                if (ex is System.Runtime.InteropServices.COMException)
                {
                    _state = EngineState.Idle;
                    _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                return null;
            }
        }

        public async Task PlayTrailerAsync(WebView2 webView, string trailerId)
        {
            if (webView == null || string.IsNullOrEmpty(trailerId)) return;
            
            try 
            {
                string json = $"{{\"type\":\"SET_VIDEO\", \"id\":\"{trailerId}\"}}";
                webView.CoreWebView2.PostWebMessageAsJson(json);
                Debug.WriteLine($"[TRAILER_POOL_DEBUG] Live Switch Command Sent: {trailerId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TRAILER_POOL_DEBUG] PlayTrailer Failed: {ex.Message}");
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                // [PRE-CHECK] Check if WebView2 Runtime is installed
                try 
                {
                    string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                    Debug.WriteLine($"[TrailerPool] WebView2 Runtime detected: {version}");
                }
                catch 
                {
                    Debug.WriteLine("[TrailerPool] FATAL: WebView2 Runtime NOT INSTALLED.");
                    _state = EngineState.NotInstalled;
                    _readyTcs.TrySetResult(false);
                    return;
                }

                if (_sharedWebView == null)
                {
                    _sharedWebView = new WebView2();
                }
                
                int qualityAtInit = AppSettings.TrailerQuality;
                int attempt = 0;
                while (attempt < 2)
                {
                    try 
                    {
                        var env = await WebView2Service.GetSharedEnvironmentAsync();
                        await _sharedWebView.EnsureCoreWebView2Async(env);
                        break; 
                    }
                    catch (Exception ex) when (attempt == 0)
                    {
                        Debug.WriteLine($"[TrailerPool] Early init exception ({ex.GetType().Name}). Waiting for UI pump...");
                        await Task.Delay(500); 
                        attempt++;
                    }
                }

                if (_sharedWebView.CoreWebView2 == null)
                {
                    throw new Exception("CoreWebView2 failed to initialize after 2 attempts.");
                }

                await WebView2Service.ApplyYouTubeCleanUISettingsAsync(_sharedWebView.CoreWebView2);

                string virtualHost = "trailer-engine.local";
                
                _sharedWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // [DIAGNOSTICS] Comprehensive navigation tracking
                _sharedWebView.CoreWebView2.NavigationStarting += (s, args) => Debug.WriteLine($"[TrailerPool] Navigation Starting: {args.Uri}");
                _sharedWebView.CoreWebView2.NavigationCompleted += (s, args) => Debug.WriteLine($"[TrailerPool] Navigation Completed: Success={args.IsSuccess}, Status={args.WebErrorStatus}");

                string htmlContent = GetYouTubeBootstrapHtml("");
                
                // Use SetVirtualHostNameToFolderMapping but serve the file dynamically if possible?
                // For now, let's use the WebResourceRequested pattern which is more flexible.
                _sharedWebView.CoreWebView2.AddWebResourceRequestedFilter($"https://{virtualHost}/*", CoreWebView2WebResourceContext.All);
                _sharedWebView.CoreWebView2.WebResourceRequested += (s, args) =>
                {
                    if (args.Request.Uri.EndsWith("/index.html"))
                    {
                        string html = GetYouTubeBootstrapHtml("");
                        var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
                        args.Response = _sharedWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                            ms.AsRandomAccessStream(), 200, "OK", "Content-Type: text/html; charset=utf-8");
                    }
                };

                _sharedWebView.Source = new Uri($"https://{virtualHost}/index.html");

                _sharedWebView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                
                var settings = _sharedWebView.CoreWebView2.Settings;
                settings.IsZoomControlEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsWebMessageEnabled = true;

                // Ensure the background is pure black to avoid grey bars
                _sharedWebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);

                Debug.WriteLine("[TrailerPool] Global Engine Initialized (State: Ready).");
                _lastQualitySetting = qualityAtInit;
                _state = EngineState.Ready;
                _readyTcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrailerPool] FATAL Init Failed: ({ex.GetType().Name}) {ex.Message}");
                _state = EngineState.Faulted;
                _readyTcs.TrySetResult(false);
            }
        }

        private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try 
            { 
                string msg = args.TryGetWebMessageAsString();
                if (msg.StartsWith("JS_LOG:"))
                {
                    Debug.WriteLine($"[TRAILER_POOL_JS] {msg.Substring(7).Trim()}");
                    return;
                }

                if (msg == "BOOTSTRAP_READY")
                {
                    Debug.WriteLine("[TrailerPool] JS Bootstrap Ready");
                    return;
                }

                _sharedWebView.DispatcherQueue.TryEnqueue(() => 
                {
                    TrailerMessageReceived?.Invoke(this, msg); 
                });
            } catch { }
        }

        private string GetYouTubeBootstrapHtml(string ytId)
        {
            // [GHOST RESOLUTION] Determine target virtual size based on user preference
            int qualitySetting = AppSettings.TrailerQuality; // 0=Auto, 1=720p, 2=1080p, 3=4K
            
            bool isGhost = qualitySetting > 0;
            int targetW = 1280;
            int targetH = 720;
            string suggestedQ = "hd720";
            string playerStyle = "width: 100%; height: 100%;";

            if (qualitySetting == 2) { targetW = 1920; targetH = 1080; suggestedQ = "hd1080"; }
            else if (qualitySetting == 3) { targetW = 3840; targetH = 2160; suggestedQ = "highres"; }
            else if (qualitySetting == 0) { suggestedQ = "default"; }

            if (isGhost) 
            {
                playerStyle = $@"width: {targetW}px; height: {targetH}px; transform-origin: top left;";
            }

            return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        html, body {{ width: 100%; height: 100%; background: #000; overflow: hidden; }}
        
        #player {{ 
            position: absolute; 
            top: 0; left: 0; 
            {playerStyle}
        }}
        
        /* Direct CSS override for the host page to hide any iframe bleed-through */
        iframe {{ border: none !important; }}
        .ytp-chrome-top, .ytp-chrome-bottom, .ytp-show-cards-title, .ytp-pause-overlay, .ytp-ce-element {{
            display: none !important;
        }}
    </style>
</head>
<body>
    <div id='player'></div>
    <script>
        function log(msg) {{ try {{ window.chrome.webview.postMessage('JS_LOG: ' + msg); }} catch(ex) {{}} }}
        
        var isGhost = {isGhost.ToString().ToLower()};
        var targetW = {targetW};
        var targetH = {targetH};
        
        function updateScale() {{
            if (!isGhost) return;
            var player = document.getElementById('player');
            if (!player) return;
            
            // [DYNAMIC GHOST] Match the container aspect ratio exactly but at high-res scale
            var containerW = window.innerWidth;
            var containerH = window.innerHeight;
            var aspectRatio = containerW / containerH;
            
            // Determine virtual height based on quality setting
            var virtualH = targetH; // e.g. 1080
            var virtualW = Math.round(virtualH * aspectRatio);
            
            // Apply the virtual size to the player to trick YouTube's bitrate logic
            player.style.width = virtualW + 'px';
            player.style.height = virtualH + 'px';
            
            // Scale it back down to fit the container (this scale will be perfectly uniform)
            var scale = containerH / virtualH;
            player.style.transform = 'scale(' + scale + ')';
            
            // Centering is implicit because ratios match, but let's be safe
            player.style.left = '0px';
            player.style.top = '0px';
        }}
        window.addEventListener('resize', updateScale);
        window.addEventListener('load', updateScale);
        setTimeout(updateScale, 100);

        var tag = document.createElement('script');
        tag.src = 'https://www.youtube.com/iframe_api';
        var firstScriptTag = document.getElementsByTagName('script')[0];
        firstScriptTag.parentNode.insertBefore(tag, firstScriptTag);

        var player;
        var isReady = false;
        var pendingVideoId = null;
        
        window.loadVideo = function(id) {{
            log('loadVideo called for ' + id);
            if (!id) return;

            // 1. Reset Host State
            if (typeof window.resetSmartCrop === 'function') window.resetSmartCrop();
            
            // 2. Broadcast to YouTube Iframe(s)
            var iframes = document.getElementsByTagName('iframe');
            for (var i = 0; i < iframes.length; i++) {{
                try {{ iframes[i].contentWindow.postMessage('RESET_SMART_CROP', '*'); }} catch(e) {{}}
            }}

            // 3. Inform C# side if needed
            try {{ window.chrome.webview.postMessage('RESET_SMART_CROP'); }} catch(e) {{}}
            
            if (isReady && player && player.loadVideoById) {{
                player.loadVideoById({{ 
                    videoId: id, 
                    startSeconds: 0,
                    suggestedQuality: '{suggestedQ}'
                }});
                player.playVideo();
            }} else {{
                pendingVideoId = id;
            }}
        }};

        function onYouTubeIframeAPIReady() {{
            player = new YT.Player('player', {{
                height: '100%', width: '100%',
                host: 'https://www.youtube.com',
                playerVars: {{
                    autoplay: 1, mute: 1, controls: 0, disablekb: 1,
                    fs: 0, rel: 0, modestbranding: 1, showinfo: 0,
                    iv_load_policy: 3, autohide: 1, enablejsapi: 1,
                    origin: window.location.origin,
                    widget_referrer: window.location.href,
                    playsinline: 1, loop: 1
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
            updateScale();
            try {{ player.mute(); }} catch (e) {{}}
            window.chrome.webview.postMessage('PLAYER_READY');
            
            if (pendingVideoId) {{
                loadVideo(pendingVideoId);
                pendingVideoId = null;
            }}

        }}

        function onPlayerStateChange(event) {{
            // event.data: 0=ended, 1=playing, 2=paused, 3=buffering, 5=video cued
            if (event.data === 0) {{
                 window.chrome.webview.postMessage('ENDED');
            }}
            if (event.data === 1 || event.data === 3) {{ 
                 if (event.data === 1) {{
                    try {{ event.target.setPlaybackQuality('{suggestedQ}'); }} catch(e) {{}}
                 }}
                 window.chrome.webview.postMessage('READY');
            }}
        }}

        function onPlayerError(event) {{
            window.chrome.webview.postMessage('ERROR:' + event.data);
        }}

        window.chrome.webview.addEventListener('message', event => {{
            const data = event.data;
            if (data.type === 'SET_VIDEO') {{
                window.loadVideo(data.id);
            }} else if (data.type === 'STOP_VIDEO' && player && player.stopVideo) {{
                player.stopVideo();
            }}
        }});

        log('BOOTSTRAP_READY');
        window.chrome.webview.postMessage('BOOTSTRAP_READY');
    </script>
</body>
</html>";
        }

        public void Release(Grid container)
        {
            if (_sharedWebView != null && _currentContainer == container)
            {
                Debug.WriteLine($"[TrailerPool] Releasing WebView from container.");
                
                try { _sharedWebView.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"STOP_VIDEO\"}"); } catch { }

                _sharedWebView.Visibility = Visibility.Collapsed;
                _sharedWebView.Opacity = 0;
                
                if (container.Children.Contains(_sharedWebView))
                {
                    container.Children.Remove(_sharedWebView);
                }
                
                _currentContainer = null;
            }
        }
    }
}
