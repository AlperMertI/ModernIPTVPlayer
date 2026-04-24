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

        public EngineState State => _state;
        public Task ReadyTask => _readyTcs.Task;
        public Grid CurrentContainer => _currentContainer;
        
        public event EventHandler<string> TrailerMessageReceived;

        private TrailerPoolService() { }

        /// <summary>
        /// Ensures the engine is fully initialized before proceeding.
        /// If initialization is already in progress, it will await the existing task.
        /// </summary>
        public async Task<bool> EnsureReadyAsync()
        {
            if (_state == EngineState.Ready) return true;
            if (_state == EngineState.Faulted || _state == EngineState.NotInstalled) return false;

            await _initLock.WaitAsync();
            try
            {
                if (_state == EngineState.Idle)
                {
                    // [SENIOR] Critical: WebView2 MUST be initialized on the UI Thread.
                    if (App.MainWindow?.DispatcherQueue != null)
                    {
                        App.MainWindow.DispatcherQueue.TryEnqueue(async () => await InitializeAsync());
                    }
                    else
                    {
                        // Fallback to background if dispatcher is not ready (though unlikely in WinUI 3 startup)
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
            _state = EngineState.Initializing;

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
                    // Ensure we are on the UI thread for WebView2 creation
                    _sharedWebView = new WebView2();
                }
                
                int attempt = 0;
                while (attempt < 2)
                {
                    try 
                    {
                        // [SENIOR FIX] Explicitly set UserDataFolder to avoid permission/path COM errors
                        string userDataFolder = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "WebView2_Trailers");
                        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, null);
                        
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

                string virtualHost = "trailer-engine.local";
                string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ModernIPTV_Trailers");
                System.IO.Directory.CreateDirectory(tempDir);

                string htmlContent = @"
<!DOCTYPE html>
<html>
<head>
    <style>
        body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: black; }
        #player { width: 100%; height: 100%; }
    </style>
</head>
<body>
    <div id='player'></div>
    <script src='https://www.youtube.com/iframe_api'></script>
    <script>
        var player;
        var currentVideoId = '';
        var isPlayerReady = false;
        var pendingId = null;

        function log(msg) {
            try { window.chrome.webview.postMessage('JS_LOG: ' + msg); } catch(ex) {}
        }

        log('Script starting...');

        function onYouTubeIframeAPIReady() {
            log('onYouTubeIframeAPIReady called');
            player = new YT.Player('player', {
                height: '100%',
                width: '100%',
                videoId: '', 
                playerVars: {
                    'autoplay': 1,
                    'controls': 0,
                    'modestbranding': 1,
                    'rel': 0,
                    'iv_load_policy': 3,
                    'mute': 1,
                    'enablejsapi': 1
                },
                events: {
                    'onReady': function(e) {
                        log('YouTube Player READY');
                        isPlayerReady = true;
                        if (pendingId) {
                            log('Processing Pending ID: ' + pendingId);
                            currentVideoId = pendingId;
                            player.loadVideoById(pendingId);
                            player.playVideo();
                            pendingId = null;
                        }
                    },
                    'onStateChange': function(e) {
                         log('Player State: ' + e.data);
                         if(e.data === 1) { 
                             log('Playback Started: ' + currentVideoId);
                             try { window.chrome.webview.postMessage('READY:' + currentVideoId); } catch(ex) {} 
                         }
                         if(e.data === 2) { 
                             log('Player Paused - Attempting Auto-Resume');
                             e.target.playVideo(); 
                         } 
                    },
                    'onError': function(e) {
                        log('YouTube Player ERROR: ' + e.data);
                        try { window.chrome.webview.postMessage('ERROR'); } catch(ex) {}
                    }
                }
            });
        }

        window.chrome.webview.addEventListener('message', event => {
            const data = event.data;
            log('Message Received: ' + data.type + (data.id ? (' ID: ' + data.id) : ''));
            if (data.type === 'SET_VIDEO') {
                if (isPlayerReady && player && player.loadVideoById) {
                    currentVideoId = data.id;
                    player.loadVideoById(data.id);
                    player.playVideo();
                } else {
                    log('Player NOT ready yet, queueing ID: ' + data.id);
                    pendingId = data.id;
                }
            } else if (data.type === 'STOP_VIDEO' && player && player.stopVideo) {
                log('Stop Command Received');
                player.stopVideo();
            }
        });

        log('JS Engine fully bootstrapped. Sending BOOTSTRAP_READY...');
        try { window.chrome.webview.postMessage('BOOTSTRAP_READY'); } catch(ex) {}
    </script>
</body>
</html>";
                string htmlPath = System.IO.Path.Combine(tempDir, "index.html");
                await System.IO.File.WriteAllTextAsync(htmlPath, htmlContent);

                _sharedWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    virtualHost, tempDir, CoreWebView2HostResourceAccessKind.Allow);
                
                var bootstrapTcs = new TaskCompletionSource<bool>();
                TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs> bootstrapHandler = null;
                bootstrapHandler = (s, e) => 
                {
                    if (e.TryGetWebMessageAsString() == "BOOTSTRAP_READY")
                    {
                        _sharedWebView.CoreWebView2.WebMessageReceived -= bootstrapHandler;
                        bootstrapTcs.TrySetResult(true);
                    }
                };
                _sharedWebView.CoreWebView2.WebMessageReceived += bootstrapHandler;

                _sharedWebView.Source = new Uri($"https://{virtualHost}/index.html");
                
                // Wait for the JS layer to confirm it's ready for messages (Max 5s)
                var completedTask = await Task.WhenAny(bootstrapTcs.Task, Task.Delay(5000));
                if (completedTask != bootstrapTcs.Task)
                {
                    Debug.WriteLine("[TrailerPool] Bootstrap timeout! Proceeding anyway...");
                }

                _sharedWebView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                
                _sharedWebView.CoreWebView2.WebMessageReceived += (s, e) => 
                {
                    try 
                    { 
                        string msg = e.TryGetWebMessageAsString();
                        if (msg.StartsWith("JS_LOG:"))
                        {
                            Debug.WriteLine($"[TRAILER_POOL_JS] {msg.Substring(7).Trim()}");
                            return;
                        }

                        _sharedWebView.DispatcherQueue.TryEnqueue(() => 
                        {
                            TrailerMessageReceived?.Invoke(this, msg); 
                        });
                    } catch { }
                };

                var settings = _sharedWebView.CoreWebView2.Settings;
                settings.IsZoomControlEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsWebMessageEnabled = true;

                Debug.WriteLine("[TrailerPool] Global Engine Initialized (State: Ready).");
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

        public void Release(Grid container)
        {
            if (_sharedWebView != null && _currentContainer == container)
            {
                Debug.WriteLine($"[TRAILER_POOL_DEBUG] Releasing WebView from container session.");
                
                // [AUDIO FIX] Stop playback before removing from tree
                try { _sharedWebView.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"STOP_VIDEO\"}"); } catch { }

                _sharedWebView.Visibility = Visibility.Collapsed;
                
                if (container.Children.Contains(_sharedWebView))
                {
                    container.Children.Remove(_sharedWebView);
                }
                
                _currentContainer = null;
            }
        }
    }
}
