using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer.Controls
{
    /// <summary>
    /// Decoupled manager for Hero asset loading and memory management.
    /// Handles surface caching, sanitization, and sequential download queueing.
    /// </summary>
    public class HeroAssetManager
    {
        private readonly Action<string> _logger;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

        // Cache for loaded surfaces to prevent redundant network requests and memory duplication
        private readonly Dictionary<string, Task<LoadedImageSurface?>> _logoSurfaces = new();
        private readonly Dictionary<string, Task<LoadedImageSurface?>> _backdropSurfaces = new();

        public HeroAssetManager(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action<string> logger)
        {
            _dispatcher = dispatcher;
            _logger = logger;
        }

        public void Clear(string? currentLogoUrl, string? currentBgUrl)
        {
            try
            {
                lock (_logoSurfaces)
                {
                    var toRemove = _logoSurfaces.Keys.Where(k => k != currentLogoUrl).ToList();
                    foreach (var url in toRemove)
                    {
                        if (_logoSurfaces.TryGetValue(url, out var t) && t.IsCompleted)
                        {
                            try { t.Result?.Dispose(); } catch { }
                            _logoSurfaces.Remove(url);
                        }
                    }
                }

                lock (_backdropSurfaces)
                {
                    var backdropToRemove = _backdropSurfaces.Keys.Where(k => k != currentBgUrl).ToList();
                    foreach (var url in backdropToRemove)
                    {
                        if (_backdropSurfaces.TryGetValue(url, out var t) && t.IsCompleted)
                        {
                            try { t.Result?.Dispose(); } catch { }
                        }
                        _backdropSurfaces.Remove(url);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[AssetManager] Clear error: {ex.Message}");
            }
        }

        public Task<LoadedImageSurface?> GetBackdropSurfaceAsync(string url, CancellationToken token)
        {
            if (string.IsNullOrEmpty(url)) return Task.FromResult<LoadedImageSurface?>(null);
            var sanitized = SanitizeImageUrl(url, isLogo: false);
            if (string.IsNullOrEmpty(sanitized)) return Task.FromResult<LoadedImageSurface?>(null);

            return GetSurfaceCachedAsync(sanitized, _backdropSurfaces, token);
        }

        public Task<LoadedImageSurface?> GetLogoSurfaceAsync(string url, CancellationToken token)
        {
            if (string.IsNullOrEmpty(url)) return Task.FromResult<LoadedImageSurface?>(null);
            var sanitized = SanitizeImageUrl(url, isLogo: true);
            if (string.IsNullOrEmpty(sanitized)) return Task.FromResult<LoadedImageSurface?>(null);

            return GetSurfaceCachedAsync(sanitized, _logoSurfaces, token);
        }

        public void PreloadLogo(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            var sanitized = SanitizeImageUrl(url, isLogo: true);
            if (string.IsNullOrEmpty(sanitized)) return;
            _ = GetLogoSurfaceAsync(sanitized, CancellationToken.None);
        }

        public void PreloadBackdrop(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            var sanitized = SanitizeImageUrl(url, isLogo: false);
            if (string.IsNullOrEmpty(sanitized)) return;
            _ = GetBackdropSurfaceAsync(sanitized, CancellationToken.None);
        }

        private async Task<LoadedImageSurface?> GetSurfaceCachedAsync(string url, Dictionary<string, Task<LoadedImageSurface?>> cache, CancellationToken token)
        {
            Task<LoadedImageSurface?>? existingTask = null;
            lock (cache)
            {
                if (cache.TryGetValue(url, out existingTask))
                {
                    // Task found, but we must await it OUTSIDE the lock to avoid CS1996
                }
                else
                {
                    var tcs = new TaskCompletionSource<LoadedImageSurface?>();
                    cache[url] = tcs.Task;

                var filename = Path.GetFileName(url);
                _ = Task.Run(() =>
                {
                    bool queued = _dispatcher.TryEnqueue(() =>
                    {
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var surface = LoadedImageSurface.StartLoadFromUri(new Uri(url));

                            void OnLoadCompleted(LoadedImageSurface s, LoadedImageSourceLoadCompletedEventArgs args)
                            {
                                s.LoadCompleted -= OnLoadCompleted;
                                if (args.Status == LoadedImageSourceLoadStatus.Success)
                                {
                                    _logger($"[NET-IMG] DONE: {filename} in {sw.ElapsedMilliseconds}ms");
                                    tcs.TrySetResult(s);
                                }
                                else
                                {
                                    _logger($"[NET-IMG] FAIL: {filename} | Status: {args.Status}");
                                    tcs.TrySetResult(null);
                                }
                            }

                            surface.LoadCompleted += OnLoadCompleted;
                        }
                        catch (Exception ex)
                        {
                            _logger($"[NET-IMG] CRITICAL ERROR for {url}: {ex.Message}");
                            tcs.TrySetResult(null);
                        }
                    });

                    if (!queued) tcs.TrySetResult(null);
                });

                    existingTask = tcs.Task;
                    
                    // If it results in null (failure), remove from cache so we can retry later.
                    _ = existingTask.ContinueWith(t => {
                        if (t.IsCompleted && t.Result == null)
                        {
                            lock (cache) { cache.Remove(url); }
                        }
                    });
                }
            }

            return await existingTask;
        }

        public string SanitizeImageUrl(string? url, bool isLogo)
        {
            if (string.IsNullOrEmpty(url)) return "";
            if (url.Contains("cinemeta-live.strem.io")) return ""; // Skip broken cinemeta icons

            // Prefer Fanart.tv logos if available via URL patterns
            if (isLogo && url.Contains("epguides.com")) return ""; 

            return url;
        }

        public async Task ProcessAssetQueueAsync(List<StremioMediaStream> items, CancellationToken token)
        {
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var item = items[i];
                    string? bgUrl = item.Meta?.Background ?? item.PosterUrl;

                    var logoTask = GetLogoSurfaceAsync(item.LogoUrl, CancellationToken.None);
                    Task backdropTask = string.IsNullOrEmpty(bgUrl) ? Task.CompletedTask : GetBackdropSurfaceAsync(bgUrl, CancellationToken.None);

                    if (i == 0) await Task.WhenAny(logoTask, Task.Delay(1000, token));
                    else await Task.Delay(600, token);

                    if (!string.IsNullOrEmpty(bgUrl) && !token.IsCancellationRequested)
                    {
                        _ = ImageHelper.GetOrExtractColorAsync(bgUrl);
                    }
                }
            }
            catch { }
        }
    }
}
