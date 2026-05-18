using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using ModernIPTVPlayer.Models.Iptv;

namespace ModernIPTVPlayer.Services
{
    public sealed class SyncService : IDisposable
    {
        private const int DetailCacheMagic = 0x53455249;
        private const int MovieCacheMagic = 0x4D4F5649;
        private const int BackgroundLoopPollMinutes = 5;
        private const int InitialSyncDelayMs = 1000;

        private static readonly Lazy<SyncService> _instance = new(() => new SyncService());
        public static SyncService Instance => _instance.Value;

        private readonly object _syncLock = new();
        private bool _isSyncing;
        private CancellationTokenSource? _schedulerCts;
        private CancellationTokenSource? _shutdownCts;

        private SyncService()
        {
            _shutdownCts = new CancellationTokenSource();
            AppSettings.CacheSettingsChanged += OnCacheSettingsChanged;
            App.LoginChanged += OnLoginChanged;

            if (App.CurrentLogin != null)
            {
                AppLogger.Info($"[SyncService] CurrentLogin already set for {App.CurrentLogin.PlaylistName}. Triggering initial sync.");
                _ = SyncPlaylistAsync(App.CurrentLogin);
            }

            _ = ScheduleNextSyncAsync(_shutdownCts.Token);
        }

        private void OnCacheSettingsChanged()
        {
            _ = ScheduleNextSyncAsync(_shutdownCts?.Token ?? CancellationToken.None);
        }

        private void OnLoginChanged(LoginParams login)
        {
            if (login != null)
            {
                AppLogger.Info($"[SyncService] Login detected for {login.PlaylistName}. Triggering background sync.");
                _ = SyncPlaylistAsync(login);
            }
            else
            {
                AppLogger.Info("[SyncService] Logout detected. Cancelling active syncs.");
                _schedulerCts?.Cancel();
            }
        }

        public void Dispose()
        {
            _shutdownCts?.Cancel();
            _shutdownCts?.Dispose();
            _schedulerCts?.Dispose();
            AppSettings.CacheSettingsChanged -= OnCacheSettingsChanged;
            App.LoginChanged -= OnLoginChanged;
        }

        private async Task ScheduleNextSyncAsync(CancellationToken shutdownToken)
        {
            _schedulerCts?.Cancel();
            _schedulerCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            var ct = _schedulerCts.Token;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!AppSettings.IsAutoCacheEnabled || App.CurrentLogin == null)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(BackgroundLoopPollMinutes), ct);
                        continue;
                    }

                    var login = App.CurrentLogin;
                    var interval = TimeSpan.FromMinutes(AppSettings.CacheIntervalMinutes);
                    var lastUpdate = AppSettings.LastVodCacheTime > AppSettings.LastSeriesCacheTime
                        ? AppSettings.LastVodCacheTime
                        : AppSettings.LastSeriesCacheTime;

                    var nextSync = lastUpdate.Add(interval);
                    var delay = nextSync - DateTime.Now;

                    if (delay <= TimeSpan.Zero)
                    {
                        AppLogger.Info($"[SyncService] Cycle triggered: Cache interval ({AppSettings.CacheIntervalMinutes}m) reached.");
                        _ = SyncPlaylistAsync(login);
                        await Task.Delay(interval, ct);
                    }
                    else
                    {
                        AppLogger.Info($"[SyncService] Next cycle scheduled for {DateTime.Now.Add(delay):HH:mm:ss} (in {delay.TotalMinutes:F1} min).");
                        await Task.Delay(delay, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Warn($"[SyncService] Error in background scheduler loop: {ex.Message}");
            }
        }

        public Task SyncPlaylistAsync(LoginParams login)
        {
            return SyncInternalAsync(login, force: false);
        }

        public Task SyncNowAsync(LoginParams login)
        {
            return SyncInternalAsync(login, force: true);
        }

        private async Task SyncInternalAsync(LoginParams login, bool force)
        {
            if (login == null)
            {
                AppLogger.Warn("[SyncService] Sync aborted: Login parameters are null.");
                return;
            }
            if (string.IsNullOrEmpty(login.Host))
            {
                AppLogger.Warn("[SyncService] Sync aborted: Host is empty.");
                return;
            }

            lock (_syncLock)
            {
                if (_isSyncing)
                {
                    AppLogger.Info("[SyncService] Sync already in progress. Skipping duplicate call.");
                    return;
                }
                _isSyncing = true;
            }

            string playlistId = login.PlaylistId;
            var startSw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var interval = TimeSpan.FromMinutes(AppSettings.CacheIntervalMinutes);
                var lastVod = AppSettings.LastVodCacheTime;
                var lastSeries = AppSettings.LastSeriesCacheTime;

                bool hasVod = ContentCacheService.Instance.HasCacheInRam(playlistId, "vod");
                bool hasSeries = ContentCacheService.Instance.HasCacheInRam(playlistId, "series");

                bool needsVod = force || !hasVod || (DateTime.Now - lastVod) > interval;
                bool needsSeries = force || !hasSeries || (DateTime.Now - lastSeries) > interval;

                if ((!needsVod && !hasVod && lastVod != DateTime.MinValue) || (!needsSeries && !hasSeries && lastSeries != DateTime.MinValue))
                {
                    AppLogger.Info("[SyncService] RAM cache empty but disk cache is fresh. Warming up indices.");
                    _ = Task.Run(async () =>
                    {
                        if (!hasVod && lastVod != DateTime.MinValue) await ContentCacheService.Instance.LoadCacheAsync<VodStream>(playlistId, "vod");
                        if (!hasSeries && lastSeries != DateTime.MinValue) await ContentCacheService.Instance.LoadCacheAsync<SeriesStream>(playlistId, "series");
                    });
                }

                if (needsVod || needsSeries)
                {
                    var tasks = new List<Task>();

                    if (needsVod)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                string vodApi = BuildApiUrl(login, "get_vod_streams");
                                await FetchCatalogWithConditionalHeadersAsync(playlistId, "vod", vodApi, async (stream) =>
                                {
                                    await ContentCacheService.Instance.SaveVodStreamsBinaryFromJsonStreamAsync(playlistId, stream);
                                });

                                string catApi = BuildApiUrl(login, "get_vod_categories");
                                await FetchCatalogWithConditionalHeadersAsync(playlistId, "vod_categories", catApi, async (stream) =>
                                {
                                    var categories = await JsonSerializer.DeserializeAsync(stream, Json.AppJsonContext.Default.ListLiveCategory);
                                    if (categories != null)
                                    {
                                        await ContentCacheService.Instance.SaveCacheAsync(playlistId, "vod_categories", categories);
                                    }
                                });

                                AppSettings.LastVodCacheTime = DateTime.Now;
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Error("[SyncService] VOD catalog sync failed.", ex);
                            }
                        }));
                    }

                    if (needsSeries)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                string seriesApi = BuildApiUrl(login, "get_series");
                                await FetchCatalogWithConditionalHeadersAsync(playlistId, "series", seriesApi, async (stream) =>
                                {
                                    await ContentCacheService.Instance.SaveSeriesStreamsBinaryFromJsonStreamAsync(playlistId, stream);
                                });

                                string catApi = BuildApiUrl(login, "get_series_categories");
                                await FetchCatalogWithConditionalHeadersAsync(playlistId, "series_categories", catApi, async (stream) =>
                                {
                                    var categories = await JsonSerializer.DeserializeAsync(stream, Json.AppJsonContext.Default.ListLiveCategory);
                                    if (categories != null)
                                    {
                                        await ContentCacheService.Instance.SaveCacheAsync(playlistId, "series_categories", categories);
                                    }
                                });

                                AppSettings.LastSeriesCacheTime = DateTime.Now;
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Error("[SyncService] Series catalog sync failed.", ex);
                            }
                        }));
                    }

                    if (tasks.Count > 0)
                    {
                        await Task.WhenAll(tasks);
                    }
                }

                if (App.CurrentLogin?.PlaylistId != playlistId)
                {
                    AppLogger.Warn("[SyncService] Active playlist changed during sync cycle. Index activation aborted.");
                    return;
                }

                await ContentCacheService.Instance.TriggerIndexActivationAsync(playlistId);
                AppLogger.Info($"[SyncService] Synchronization completed successfully in {startSw.ElapsedMilliseconds}ms.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("[SyncService] Unified sync execution failed.", ex);
            }
            finally
            {
                lock (_syncLock)
                {
                    _isSyncing = false;
                }
            }
        }

        private async Task<(HttpResponseMessage Response, string ETag, string LastModified, bool NotModified)> SendWithConditionalHeadersAsync(string url, string etag, string lastModified)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(etag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }
            if (!string.IsNullOrEmpty(lastModified))
            {
                request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
            }

            var response = await HttpHelper.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                response.Dispose();
                return (null!, "", "", true);
            }

            response.EnsureSuccessStatusCode();

            string newEtag = response.Headers.ETag?.ToString() ?? "";
            string newLastModified = "";
            if (response.Content.Headers.TryGetValues("Last-Modified", out var values))
            {
                newLastModified = values.FirstOrDefault() ?? "";
            }

            return (response, newEtag, newLastModified, false);
        }

        private async Task<bool> FetchCatalogWithConditionalHeadersAsync(string playlistId, string type, string url, Func<Stream, Task> onSuccess)
        {
            var (etag, lastModified) = await LoadCatalogHeadersAsync(playlistId, type);
            var result = await SendWithConditionalHeadersAsync(url, etag, lastModified);

            if (result.NotModified)
            {
                AppLogger.Info($"[SyncService] Catalog '{type}' is unchanged (304 Not Modified). Reusing local cache.");
                return false;
            }

            using (result.Response)
            using (var stream = await result.Response.Content.ReadAsStreamAsync())
            {
                await onSuccess(stream);
            }

            if (!string.IsNullOrEmpty(result.ETag) || !string.IsNullOrEmpty(result.LastModified))
            {
                await SaveCatalogHeadersAsync(playlistId, type, result.ETag, result.LastModified);
            }

            return true;
        }

        private async Task RevalidateDetailAsync<T>(string cacheKey, int itemId, LoginParams login, string apiAction, string logType, Action<T> onUpdate)
        {
            try
            {
                int magic = typeof(T) == typeof(SeriesInfoResult) ? DetailCacheMagic : MovieCacheMagic;
                var (etag, lastModified) = await ContentCacheService.Instance.GetDetailHeadersAsync(cacheKey, magic);

                string api = BuildApiUrl(login, apiAction);
                var result = await SendWithConditionalHeadersAsync(api, etag, lastModified);

                if (result.NotModified)
                {
                    AppLogger.Info($"[SyncService] {logType} {itemId} details unchanged (304). Extending local cache TTL.");
                    await ContentCacheService.Instance.TouchDetailCacheFileAsync(cacheKey);
                    return;
                }

                using (result.Response)
                using (var stream = await result.Response.Content.ReadAsStreamAsync())
                {
                    var fresh = await JsonSerializer.DeserializeAsync(stream, Json.AppJsonContext.Default.GetTypeInfo(typeof(T)));
                    if (fresh is T typed)
                    {
                        if (typeof(T) == typeof(SeriesInfoResult))
                        {
                            await ContentCacheService.Instance.SaveSeriesInfoBinaryWithHeadersAsync(cacheKey, (SeriesInfoResult)(object)typed, result.ETag, result.LastModified);
                        }
                        else if (typeof(T) == typeof(MovieInfoResult))
                        {
                            await ContentCacheService.Instance.SaveMovieInfoBinaryWithHeadersAsync(cacheKey, (MovieInfoResult)(object)typed, result.ETag, result.LastModified);
                        }
                        onUpdate?.Invoke(typed);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[SyncService] Background {logType.ToLower()} details revalidation failed for ID {itemId}: {ex.Message}");
            }
        }

        public Task RevalidateSeriesInfoAsync(int seriesId, LoginParams login, SeriesInfoResult cached, Action<SeriesInfoResult> onUpdate)
        {
            string cacheKey = $"series_info_{seriesId}";
            return RevalidateDetailAsync(cacheKey, seriesId, login, $"get_series_info&series_id={seriesId}", "Series", onUpdate);
        }

        public Task RevalidateMovieInfoAsync(int movieId, LoginParams login, MovieInfoResult cached, Action<MovieInfoResult> onUpdate)
        {
            string cacheKey = $"movie_info_{movieId}";
            return RevalidateDetailAsync(cacheKey, movieId, login, $"get_vod_info&vod_id={movieId}", "Movie", onUpdate);
        }

        private async Task<(string ETag, string LastModified)> LoadCatalogHeadersAsync(string playlistId, string type)
        {
            try
            {
                string safeId = GetSafePlaylistId(playlistId);
                string fileName = $"headers_{safeId}_{type}.json";
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.TryGetItemAsync(fileName) as IStorageFile;
                if (file != null)
                {
                    string json = await FileIO.ReadTextAsync(file);
                    var headers = JsonSerializer.Deserialize<CatalogHeaders>(json);
                    if (headers != null)
                    {
                        return (headers.ETag ?? "", headers.LastModified ?? "");
                    }
                }
            }
            catch { }
            return ("", "");
        }

        private async Task SaveCatalogHeadersAsync(string playlistId, string type, string etag, string lastModified)
        {
            try
            {
                string safeId = GetSafePlaylistId(playlistId);
                string fileName = $"headers_{safeId}_{type}.json";
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                var headers = new CatalogHeaders { ETag = etag, LastModified = lastModified };
                string json = JsonSerializer.Serialize(headers);
                await FileIO.WriteTextAsync(file, json);
            }
            catch { }
        }

        private sealed record CatalogHeaders
        {
            public string? ETag { get; init; }
            public string? LastModified { get; init; }
        }

        private static string BuildApiUrl(LoginParams login, string action)
        {
            var uri = new UriBuilder(login.Host)
            {
                Query = $"username={Uri.EscapeDataString(login.Username)}&password={Uri.EscapeDataString(login.Password)}&action={Uri.EscapeDataString(action)}"
            };
            return uri.ToString();
        }

        private static string GetSafePlaylistId(string playlistId)
        {
            if (string.IsNullOrEmpty(playlistId)) return "default";
            var sb = new StringBuilder(playlistId.Length);
            foreach (char c in playlistId)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
