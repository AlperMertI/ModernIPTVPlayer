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
        private readonly System.Threading.SemaphoreSlim _stateLock = new(1, 1);

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
            _ = TrailerPoolService.Instance.EnsureReadyAsync();
        }

        private WebView2? _webView;

        public async Task PlayTrailerAsync(string? videoId)
        {
            if (string.IsNullOrEmpty(videoId)) return;

            await _stateLock.WaitAsync();
            try
            {
                System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Play Request: {videoId}");

                if (_webView == null)
                {
                    System.Diagnostics.Debug.WriteLine("[HeroTrailer] Acquiring WebView from Global Pool...");
                    _webView = await TrailerPoolService.Instance.AcquireAsync(RootGrid);
                    if (_webView != null)
                    {
                        TrailerPoolService.Instance.TrailerMessageReceived -= OnTrailerMessageReceived;
                        TrailerPoolService.Instance.TrailerMessageReceived += OnTrailerMessageReceived;
                    }
                }

                if (_webView != null)
                {
                    string ytId = TrailerPoolService.ExtractYouTubeId(videoId);
                    await TrailerPoolService.Instance.PlayTrailerAsync(_webView, ytId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Play Error: {ex.Message}");
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private void OnTrailerMessageReceived(object? sender, string msg)
        {
            // [OWNERSHIP GUARD] Only process if we are the current owner in the pool
            if (TrailerPoolService.Instance.CurrentContainer != RootGrid) return;
            
            CoreWebView2_WebMessageReceived(null, msg);
        }

        public async Task StopAsync()
        {
            await CleanupAsync();
        }

        private void CoreWebView2_WebMessageReceived(CoreWebView2? sender, string msg)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Message: {msg}");
                
                if (msg == "READY")
                {
                    _isTrailerPlaying = true;
                    
                    // [FIX] Race Condition: Use shared instance for visibility
                    var shared = TrailerPoolService.Instance.SharedWebView;
                    if (shared != null) 
                    {
                        shared.Opacity = 1;
                        shared.Visibility = Visibility.Visible;
                    }

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
            }
            catch { }
        }

        private async Task CleanupAsync()
        {
            await _stateLock.WaitAsync();
            try 
            {
                System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Cleanup Requested");
                _isTrailerPlaying = false;
                
                if (_webView != null)
                {
                    TrailerPoolService.Instance.TrailerMessageReceived -= OnTrailerMessageReceived;
                    TrailerPoolService.Instance.Release(RootGrid);
                    _webView = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HeroTrailer] Cleanup Error: {ex.Message}");
            }
            finally
            {
                _stateLock.Release();
            }

            PlayStateChanged?.Invoke(this, false);
        }
    }
}
