using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services;
using Windows.Storage.Streams;

namespace ModernIPTVPlayer.Controls
{
    /// <summary>
    /// Hero asset loader.
    /// - Single shared HTTP fetch per URL (ignores caller cancellations so one viewer's nav doesn't tear down another's load).
    /// - LoadedImageSurface is created from an in-memory stream (no temp file disk round-trip).
    /// - The raw bytes are kept briefly for a one-shot color extraction so backdrop colors do not re-download the same image.
    /// - Logo and backdrop paths are symmetric.
    /// </summary>
    public class HeroAssetManager
    {
        public enum MediaKind { Backdrop, Logo }

        internal sealed class Entry
        {
            public Task<LoadedImageSurface?> SurfaceTask = null!;
            public Task<(Windows.UI.Color Primary, Windows.UI.Color Secondary)?>? ColorsTask;
            public bool Failed;

            // ROOT FIX for the "logo stuck forever" bug:
            // LoadedImageSurface is a WinRT/COM object whose LoadCompleted event is required for the
            // SurfaceTask to resolve. If the managed RCW is garbage-collected before the native decode
            // completes (observed on large PNGs ~2+ MB under GC pressure), the callback chain breaks
            // silently and the Task stays pending forever.
            //
            // By parking the surface *and* the backing stream on the cache Entry itself, they share the
            // lifetime of the dictionary entry — which only disappears when we explicitly prune it. This
            // is the canonical WinRT pattern: keep a managed reference to any COM object whose async
            // callback you depend on.
            public LoadedImageSurface? SurfaceInstance;
            public IDisposable? BackingStream;
        }

        private static readonly HttpClient HeroHttp = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            try
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd("ModernIPTVPlayer/1.0 (Windows NT 10.0; WinUI)");
                c.DefaultRequestHeaders.Accept.ParseAdd("image/*,*/*");
            }
            catch { /* headers best-effort */ }
            return c;
        }

        private readonly Action<string> _logger;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

        private readonly ConcurrentDictionary<string, Entry> _logos = new();
        private readonly ConcurrentDictionary<string, Entry> _backdrops = new();

        // Bound the number of concurrent in-flight fetches so background prewarm can't starve the hero critical path.
        private static readonly SemaphoreSlim _fetchGate = new(4, 4);

        public HeroAssetManager(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action<string> logger)
        {
            _dispatcher = dispatcher;
            _logger = logger;
        }

        /// <summary>Drop cache entries that FAILED (so a retry is allowed), but NEVER touch entries that are
        /// still in flight or that succeeded. Disposing a surface that's currently bound to a composition brush
        /// causes the backdrop to flash or go black.</summary>
        public void Clear(string? currentLogoUrl, string? currentBgUrl)
        {
            try
            {
                PruneFailed(_logos, currentLogoUrl);
                PruneFailed(_backdrops, currentBgUrl);
            }
            catch (Exception ex)
            {
                _logger($"[AssetManager] Clear error: {ex.Message}");
            }
        }

        private static void PruneFailed(ConcurrentDictionary<string, Entry> cache, string? preserveUrl)
        {
            foreach (var key in cache.Keys.ToList())
            {
                if (key == preserveUrl) continue;
                if (!cache.TryGetValue(key, out var entry)) continue;
                if (entry.SurfaceTask.IsCompleted && entry.Failed)
                {
                    if (cache.TryRemove(key, out var removed))
                    {
                        DisposeEntryResources(removed);
                    }
                }
            }
        }

        public Task<LoadedImageSurface?> GetBackdropSurfaceAsync(string? url, CancellationToken callerToken)
        {
            if (string.IsNullOrEmpty(url)) return Task.FromResult<LoadedImageSurface?>(null);
            var sanitized = SanitizeImageUrl(url, isLogo: false);
            if (string.IsNullOrEmpty(sanitized)) return Task.FromResult<LoadedImageSurface?>(null);
            return GetSurfaceAsync(sanitized, MediaKind.Backdrop, callerToken);
        }

        public Task<LoadedImageSurface?> GetLogoSurfaceAsync(string? url, CancellationToken callerToken)
        {
            if (string.IsNullOrEmpty(url)) return Task.FromResult<LoadedImageSurface?>(null);
            var sanitized = SanitizeImageUrl(url, isLogo: true);
            if (string.IsNullOrEmpty(sanitized)) return Task.FromResult<LoadedImageSurface?>(null);
            return GetSurfaceAsync(sanitized, MediaKind.Logo, callerToken);
        }

        /// <summary>Color pre-extraction for a backdrop. Reuses the same bytes from the backdrop fetch so we
        /// don't hit the network twice for the same URL.</summary>
        public async Task<(Windows.UI.Color Primary, Windows.UI.Color Secondary)?> GetBackdropColorsAsync(string? url, CancellationToken callerToken)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var sanitized = SanitizeImageUrl(url, isLogo: false);
            if (string.IsNullOrEmpty(sanitized)) return null;

            var entry = _backdrops.GetOrAdd(sanitized, u => BuildEntry(u, MediaKind.Backdrop));
            if (entry.ColorsTask == null) return null;

            try { return await entry.ColorsTask.ConfigureAwait(false); }
            catch { return null; }
        }

        public void PreloadLogo(string url) => _ = GetLogoSurfaceAsync(url, CancellationToken.None);
        public void PreloadBackdrop(string url) => _ = GetBackdropSurfaceAsync(url, CancellationToken.None);

        /// <summary>Preload spotlight items 1..n with stagger so they do not compete with the active hero or discovery.</summary>
        public async Task ProcessSecondaryHeroAssetsAsync(List<StremioMediaStream> items, CancellationToken token)
        {
            if (items == null || items.Count == 0) return;
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (token.IsCancellationRequested) break;
                    if (i > 0) await Task.Delay(1200, token).ConfigureAwait(false);

                    var item = items[i];
                    string? bgUrl = item.Meta?.Background ?? item.PosterUrl;
                    _ = GetLogoSurfaceAsync(item.LogoUrl, CancellationToken.None);
                    if (!string.IsNullOrEmpty(bgUrl))
                    {
                        _ = GetBackdropSurfaceAsync(bgUrl, CancellationToken.None);
                        _ = GetBackdropColorsAsync(bgUrl, CancellationToken.None);
                    }
                }
            }
            catch { /* background preload best-effort */ }
        }

        /// <summary>Legacy name — forwards to <see cref="ProcessSecondaryHeroAssetsAsync"/>.</summary>
        public Task ProcessAssetQueueAsync(List<StremioMediaStream> items, CancellationToken token)
            => ProcessSecondaryHeroAssetsAsync(items, token);

        private Task<LoadedImageSurface?> GetSurfaceAsync(string url, MediaKind kind, CancellationToken callerToken)
        {
            var cache = kind == MediaKind.Logo ? _logos : _backdrops;
            var entry = cache.GetOrAdd(url, u => BuildEntry(u, kind));

            // If the caller cancels we don't abort the shared pipeline — we just return null locally.
            // This protects concurrent viewers that share the same URL.
            if (!callerToken.CanBeCanceled) return entry.SurfaceTask;

            return AwaitWithCallerCancel(entry.SurfaceTask, callerToken);
        }

        private static async Task<LoadedImageSurface?> AwaitWithCallerCancel(Task<LoadedImageSurface?> task, CancellationToken callerToken)
        {
            var cancelTcs = new TaskCompletionSource<LoadedImageSurface?>();
            using (callerToken.Register(() => cancelTcs.TrySetResult(null)))
            {
                var winner = await Task.WhenAny(task, cancelTcs.Task).ConfigureAwait(false);
                if (winner == task) return await task.ConfigureAwait(false);
                return null; // caller cancelled — leave the shared task running and its surface alive in cache.
            }
        }

        private Entry BuildEntry(string url, MediaKind kind)
        {
            var entry = new Entry();
            var bytesTask = FetchBytesAsync(url);
            entry.SurfaceTask = CreateSurfaceFromBytesAsync(url, kind, bytesTask, entry);
            if (kind == MediaKind.Backdrop)
            {
                entry.ColorsTask = ExtractColorsFromBytesAsync(url, bytesTask);
            }
            return entry;
        }

        private async Task<byte[]?> FetchBytesAsync(string url)
        {
            var filename = Path.GetFileName(url);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _fetchGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await HeroHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var data = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                sw.Stop();
                _logger($"[HERO-IMG] FETCH ok {filename} {sw.ElapsedMilliseconds}ms bytes={data.Length}");
                return data;
            }
            catch (Exception ex)
            {
                _logger($"[HERO-IMG] fetch FAIL {filename}: {ex.Message}");
                return null;
            }
            finally
            {
                _fetchGate.Release();
            }
        }

        // Upper bound on decode+GPU upload time. Observed healthy completions are sub-50ms; 10s gives huge
        // headroom. Anything longer is almost certainly a platform-level stall that no amount of waiting
        // will recover — we mark the entry failed so a retry is permitted on the next transition.
        private const int DECODE_WATCHDOG_MS = 10_000;

        private async Task<LoadedImageSurface?> CreateSurfaceFromBytesAsync(string url, MediaKind kind, Task<byte[]?> bytesTask, Entry entry)
        {
            var bytes = await bytesTask.ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0)
            {
                entry.Failed = true;
                return null;
            }

            var filename = Path.GetFileName(url);
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            var tcs = new TaskCompletionSource<LoadedImageSurface?>(TaskCreationOptions.RunContinuationsAsynchronously);

            bool queued = _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                LoadedImageSurface? surface = null;
                InMemoryRandomAccessStream? ms = null;
                try
                {
                    ms = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(ms.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(bytes);
                        writer.StoreAsync().AsTask().Wait();
                        writer.FlushAsync().AsTask().Wait();
                        writer.DetachStream();
                    }
                    ms.Seek(0);

                    var decodeSw = System.Diagnostics.Stopwatch.StartNew();
                    surface = LoadedImageSurface.StartLoadFromStream(ms);

                    // Park both the surface and the stream on the cache Entry. As long as the Entry is in
                    // the dictionary, the managed RCW stays alive and the LoadCompleted callback chain
                    // cannot be severed by GC. This is the actual root fix.
                    entry.SurfaceInstance = surface;
                    entry.BackingStream = ms;

                    void OnLoadCompleted(LoadedImageSurface s, LoadedImageSourceLoadCompletedEventArgs args)
                    {
                        s.LoadCompleted -= OnLoadCompleted;
                        decodeSw.Stop();
                        if (args.Status == LoadedImageSourceLoadStatus.Success)
                        {
                            _logger($"[HERO-IMG] DONE {filename} decode+gpu {decodeSw.ElapsedMilliseconds}ms total\u2248{totalSw.ElapsedMilliseconds}ms");
                            tcs.TrySetResult(s);
                        }
                        else
                        {
                            _logger($"[HERO-IMG] FAIL {filename} Status={args.Status} after {decodeSw.ElapsedMilliseconds}ms");
                            entry.Failed = true;
                            DisposeEntryResources(entry);
                            tcs.TrySetResult(null);
                        }
                    }

                    surface.LoadCompleted += OnLoadCompleted;
                }
                catch (Exception ex)
                {
                    _logger($"[HERO-IMG] UI StartLoad CRITICAL {filename}: {ex.Message}");
                    entry.Failed = true;
                    try { surface?.Dispose(); } catch { }
                    try { ms?.Dispose(); } catch { }
                    entry.SurfaceInstance = null;
                    entry.BackingStream = null;
                    tcs.TrySetResult(null);
                }
            });

            if (!queued)
            {
                _logger($"[HERO-IMG] FAIL enqueue {filename}");
                entry.Failed = true;
                return null;
            }

            // Platform-level recovery net — if the decode never reports back (extremely rare, but observed
            // on WinUI 3 with large PNGs under GC pressure even with proper anchoring), release the caller
            // instead of leaving the hero pinned in shimmer. The entry is marked failed so the next
            // transition's Clear() prunes it and a retry is allowed.
            var watchdog = Task.Delay(DECODE_WATCHDOG_MS);
            var winner = await Task.WhenAny(tcs.Task, watchdog).ConfigureAwait(false);
            if (winner == watchdog && !tcs.Task.IsCompleted)
            {
                _logger($"[HERO-IMG] WATCHDOG {filename} decode stalled > {DECODE_WATCHDOG_MS}ms — releasing caller, marking failed.");
                entry.Failed = true;
                DisposeEntryResources(entry);
                tcs.TrySetResult(null);
            }

            return await tcs.Task.ConfigureAwait(false);
        }

        private static void DisposeEntryResources(Entry entry)
        {
            try { entry.SurfaceInstance?.Dispose(); } catch { }
            try { entry.BackingStream?.Dispose(); } catch { }
            entry.SurfaceInstance = null;
            entry.BackingStream = null;
        }

        private static async Task<(Windows.UI.Color Primary, Windows.UI.Color Secondary)?> ExtractColorsFromBytesAsync(string url, Task<byte[]?> bytesTask)
        {
            var bytes = await bytesTask.ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return null;
            return await ImageHelper.GetOrExtractColorFromBytesAsync(url, bytes).ConfigureAwait(false);
        }

        public string SanitizeImageUrl(string? url, bool isLogo)
        {
            if (string.IsNullOrEmpty(url)) return "";
            if (url.Contains("cinemeta-live.strem.io")) return "";
            if (isLogo && url.Contains("epguides.com")) return "";
            return url;
        }
    }
}
