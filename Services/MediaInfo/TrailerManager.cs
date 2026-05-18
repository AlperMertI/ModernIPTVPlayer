using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.Web.WebView2.Core;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Manages trailer overlay lifecycle: initialization, playback, fullscreen toggle, cleanup.
    /// Extracted from MediaInfoPage.xaml.cs to isolate trailer concerns.
    /// 
    /// Responsibilities:
    /// - Manages WebView2 initialization for trailer playback
    /// - Handles play/close animations
    /// - Handles fullscreen layout
    /// - Processes WebView2 pool messages
    /// 
    /// Does NOT:
    /// - Fetch trailer URLs (that's MetadataProvider)
    /// - Manage WebView2 pool (that's TrailerPoolService)
    /// </summary>
    internal sealed class TrailerManager : IDisposable
    {
        private readonly MediaInfoPage _page;
        private readonly Compositor _compositor;
        private CancellationTokenSource _cts;
        private bool _isFullscreen;
        private int _uiVersion;
        private string _currentTrailerKey;
        private bool _disposed;

        public bool IsFullscreen => _isFullscreen;
        public string CurrentTrailerKey => _currentTrailerKey;

        public TrailerManager(MediaInfoPage page, Compositor compositor)
        {
            _page = page ?? throw new ArgumentNullException(nameof(page));
            _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
            Debug.WriteLine("[TRAILER] Initialized");
        }

        public async Task PlayTrailerAsync(string videoKey)
        {
            if (_disposed || string.IsNullOrEmpty(videoKey))
            {
                Debug.WriteLine($"[TRAILER] PlayTrailerAsync skipped (disposed or empty URL)");
                return;
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Debug.WriteLine($"[TRAILER] PlayTrailer START. Key: {videoKey}");

            videoKey = TrailerPoolService.ExtractYouTubeId(videoKey);
            _currentTrailerKey = videoKey;
            Debug.WriteLine($"[TRAILER] Cleaned Video ID: {videoKey}");

            Interlocked.Increment(ref _uiVersion);

            // 1. IMMEDIATE UI FEEDBACK
            _page.SetTrailerOverlayVisibility(Visibility.Visible);
            _page.SetTrailerScrimOpacity(1);
            _page.SetTrailerLoadingRing(true);
            ApplyFullscreenLayout(enable: true);
            EnsureTrailerOverlayBounds();

            // Reset visual state
            if (_page.TrailerContentControl != null)
            {
                var contentVisual = ElementCompositionPreview.GetElementVisual(_page.TrailerContentControl);
                contentVisual.StopAnimation("Scale");
                contentVisual.StopAnimation("Offset");
                contentVisual.StopAnimation("Opacity");

                double targetW = _page.TrailerContentControl.ActualWidth > 0
                    ? _page.TrailerContentControl.ActualWidth
                    : (_page.TrailerContentControl.Width > 0 ? _page.TrailerContentControl.Width : 1000);
                double targetH = _page.TrailerContentControl.ActualHeight > 0
                    ? _page.TrailerContentControl.ActualHeight
                    : (_page.TrailerContentControl.Height > 0 ? _page.TrailerContentControl.Height : 562);
                float centerX = (float)(targetW / 2.0);
                float centerY = (float)(targetH / 2.0);

                contentVisual.CenterPoint = new Vector3(centerX, centerY, 0);
                contentVisual.Scale = new Vector3(0.1f, 0.1f, 1f);
                Debug.WriteLine($"[TRAILER] Visual reset - CenterPoint: {centerX}, {centerY}, ActualSize: {targetW}x{targetH}");
            }

            if (token.IsCancellationRequested) return;

            // 2. ANIMATION (Expand)
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(_page.TrailerContentControl);
                double centerX = _page.TrailerContentControl.ActualWidth > 0
                    ? _page.TrailerContentControl.ActualWidth / 2
                    : (_page.TrailerContentControl.Width > 0 ? _page.TrailerContentControl.Width / 2 : 500);
                double centerY = _page.TrailerContentControl.ActualHeight > 0
                    ? _page.TrailerContentControl.ActualHeight / 2
                    : (_page.TrailerContentControl.Height > 0 ? _page.TrailerContentControl.Height / 2 : 281);
                visual.CenterPoint = new Vector3((float)centerX, (float)centerY, 0);

                var scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(0f, new Vector3(0.1f, 0.1f, 1f));
                scaleAnim.InsertKeyFrame(1f, Vector3.One);
                scaleAnim.Duration = TimeSpan.FromMilliseconds(250);
                visual.StartAnimation("Scale", scaleAnim);

                var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
                opacityAnim.InsertKeyFrame(0f, 0f);
                opacityAnim.InsertKeyFrame(1f, 1f);
                opacityAnim.Duration = TimeSpan.FromMilliseconds(250);
                visual.StartAnimation("Opacity", opacityAnim);

                _page.ResetTrailerTransform();
                _page.SetTrailerContentOpacity(1);

                var scrimVisual = ElementCompositionPreview.GetElementVisual(_page.TrailerScrimControl);
                var fadeAnim = _compositor.CreateScalarKeyFrameAnimation();
                fadeAnim.InsertKeyFrame(0f, 0f);
                fadeAnim.InsertKeyFrame(1f, 1f);
                fadeAnim.Duration = TimeSpan.FromMilliseconds(220);
                scrimVisual.StartAnimation("Opacity", fadeAnim);
                _page.SetTrailerScrimOpacity(1);

                Debug.WriteLine("[TRAILER] Animation started, Opacity=1.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TRAILER] ANIMATION FATAL ERROR: {ex}");
                _page.SetTrailerContentOpacity(1);
                _page.SetTrailerOverlayVisibility(Visibility.Visible);
            }

            if (token.IsCancellationRequested) return;

            // 3. LOAD CONTENT
            await LoadTrailerContentAsync(videoKey, token);
        }

        private async Task LoadTrailerContentAsync(string videoKey, CancellationToken token)
        {
            try
            {
                Debug.WriteLine($"[TRAILER] Load Content via Pool: {videoKey}");

                var webView = await TrailerPoolService.Instance.AcquireAsync(_page.TrailerContentControl);
                if (webView == null) return;

                webView.Opacity = 0;
                webView.Visibility = Visibility.Visible;

                TrailerPoolService.Instance.TrailerMessageReceived -= OnPoolMessageReceived;
                TrailerPoolService.Instance.TrailerMessageReceived += OnPoolMessageReceived;

                if (token.IsCancellationRequested) return;

                await TrailerPoolService.Instance.PlayTrailerAsync(webView, videoKey, unmute: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TRAILER] Load Error: {ex}");
            }
        }

        private void OnPoolMessageReceived(object sender, string message)
        {
            if (TrailerPoolService.Instance.CurrentContainer != _page.TrailerContentControl) return;

            _page.DispatcherQueue.TryEnqueue(() =>
            {
                var parts = message.Split(':');
                string cmd = parts[0];
                string msgId = parts.Length > 1 ? parts[1] : null;

                if (msgId != null && _currentTrailerKey != null && msgId != _currentTrailerKey)
                {
                    Debug.WriteLine($"[TRAILER] Ignoring stale message {cmd} for ID {msgId} (Current: {_currentTrailerKey})");
                    return;
                }

                if (cmd == "READY")
                {
                    Debug.WriteLine($"[TRAILER] Video Ready! ID: {msgId}");
                    _page.SetTrailerLoadingRing(false);
                    _page.SetTrailerWebViewOpacity(1);
                }
                else if (cmd == "ENDED")
                {
                    Debug.WriteLine($"[TRAILER] Video Ended. ID: {msgId}");
                    _ = CloseTrailerAsync();
                }
                else if (cmd == "ERROR")
                {
                    string errCode = parts.Length > 2 ? parts[2] : "Unknown";
                    Debug.WriteLine($"[TRAILER] Video Error: {errCode}. ID: {msgId}");
                    _ = CloseTrailerAsync();
                }
            });
        }

        public async Task CloseTrailerAsync()
        {
            if (_disposed) return;

            Debug.WriteLine("[TRAILER] CloseTrailer START.");

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            int closeVersion = Interlocked.Increment(ref _uiVersion);
            _isFullscreen = false;

            TrailerPoolService.Instance.TrailerMessageReceived -= OnPoolMessageReceived;
            TrailerPoolService.Instance.Release(_page.TrailerContentControl);
            _page.ResetTrailerWebViewInitialized();

            // Animate Scrim Fade Out
            if (_page.TrailerScrimControl != null)
            {
                var scrimVisual = ElementCompositionPreview.GetElementVisual(_page.TrailerScrimControl);
                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromMilliseconds(250);
                scrimVisual.StartAnimation("Opacity", fadeOut);
            }

            if (_page.TrailerContentControl != null)
            {
                var contentVisual = ElementCompositionPreview.GetElementVisual(_page.TrailerContentControl);
                var shrink = _compositor.CreateVector3KeyFrameAnimation();
                shrink.InsertKeyFrame(1f, new Vector3(0.1f, 0.1f, 1f));
                shrink.Duration = TimeSpan.FromMilliseconds(250);
                contentVisual.StartAnimation("Scale", shrink);

                var opacityOut = _compositor.CreateScalarKeyFrameAnimation();
                opacityOut.InsertKeyFrame(1f, 0f);
                opacityOut.Duration = TimeSpan.FromMilliseconds(250);
                contentVisual.StartAnimation("Opacity", opacityOut);

                await Task.Delay(250);

                if (closeVersion != _uiVersion) return;

                _page.SetTrailerOverlayVisibility(Visibility.Collapsed);
                _page.SetTrailerScrimOpacity(0);
                _page.SetTrailerContentOpacity(0);
                ApplyFullscreenLayout(enable: false);
            }
        }

        public void ToggleFullscreen()
        {
            if (_disposed) return;
            _isFullscreen = !_isFullscreen;
            _uiVersion++;
            ApplyFullscreenLayout(_isFullscreen);
            Debug.WriteLine($"[TRAILER] Fullscreen toggled: {_isFullscreen}");
        }

        public void ApplyFullscreenLayout(bool enable)
        {
            var trailerContent = _page.TrailerContentControl;
            var overlay = _page.TrailerOverlayControl;
            if (trailerContent == null || overlay == null) return;

            if (!enable)
            {
                trailerContent.Width = 1000;
                trailerContent.Height = 562;
                if (_page.CloseTrailerButtonControl != null)
                {
                    _page.CloseTrailerButtonControl.Margin = new Thickness(16, 16, 16, 0);
                }
                return;
            }

            double overlayWidth = _page.XamlRootSizeWidth ?? overlay.ActualWidth;
            double overlayHeight = _page.XamlRootSizeHeight ?? overlay.ActualHeight;

            double maxWidth = Math.Max(320, overlayWidth - 200);
            double maxHeight = Math.Max(180, overlayHeight - 160);

            double width = maxWidth;
            double height = width * 9.0 / 16.0;
            if (height > maxHeight)
            {
                height = maxHeight;
                width = height * 16.0 / 9.0;
            }

            trailerContent.Width = width;
            trailerContent.Height = height;

            if (_page.CloseTrailerButtonControl != null)
            {
                _page.CloseTrailerButtonControl.Margin = new Thickness(0, 16, 16, 0);
                _page.CloseTrailerButtonControl.HorizontalAlignment = HorizontalAlignment.Right;
                _page.CloseTrailerButtonControl.VerticalAlignment = VerticalAlignment.Top;
            }

            var visual = ElementCompositionPreview.GetElementVisual(trailerContent);
            visual.StopAnimation("Offset");
            float centerX = (float)(width / 2.0);
            float centerY = (float)(height / 2.0);
            visual.CenterPoint = new Vector3(centerX, centerY, 0);
        }

        private void EnsureTrailerOverlayBounds()
        {
            var overlay = _page.TrailerOverlayControl;
            var scrim = _page.TrailerScrimControl;
            if (overlay == null || scrim == null) return;

            overlay.Width = double.NaN;
            overlay.Height = double.NaN;
            scrim.Width = double.NaN;
            scrim.Height = double.NaN;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            Debug.WriteLine("[TRAILER] Disposed");
        }
    }
}
