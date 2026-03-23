using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Services.Iptv; // NEW
using Windows.Storage;
using ModernIPTVPlayer;

namespace ModernIPTVPlayer.Services
{
    public class ContentCacheService
    {
        private static ContentCacheService _instance;
        public static ContentCacheService Instance => _instance ??= new ContentCacheService();

        private const string HASH_EXT = ".hash";
        private bool _isSyncing = false;
        
        // In-Memory Search Index (Simple Dictionary for now, can be Trie later)
        private ConcurrentDictionary<string, List<object>> _searchIndex = new();

        // **NEW: RAM Cache Layer for Series Info**
        // Key: cacheKey, Value: (Data, Expiration)
        private ConcurrentDictionary<string, (object Data, DateTime LastAccessed)> _memoryCache = new();

        // **NEW: RAM Cache for Full Stream Lists (VOD/Series/Live)**
        private ConcurrentDictionary<string, object> _streamListsCache = new();

        private CancellationTokenSource _syncCts;
        public Dictionary<string, List<IMediaStream>> GlobalTokenIndex { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        private ConcurrentDictionary<string, byte> _pendingSaveCategories = new();
        private Timer _throttledSaveTimer;

        private bool _isIndexing = false;
        public bool IsIndexing 
        { 
            get => _isIndexing; 
            private set { 
                if (_isIndexing != value) { 
                    _isIndexing = value; 
                    // Safe Invoke: If this belongs to UI, Marshall it.
                    // ContentCacheService is a singleton often used by UI.
                    if (App.MainWindow.DispatcherQueue != null)
                    {
                        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            IndexingStatusChanged?.Invoke(this, value);
                        });
                    }
                    else
                    {
                        IndexingStatusChanged?.Invoke(this, value);
                    }
                } 
            } 
        }
        public event EventHandler<bool> IndexingStatusChanged;
        
        private readonly SemaphoreSlim _diskSemaphore = new(1, 1);

        private ContentCacheService()
        {
            AppSettings.CacheSettingsChanged += () => _ = ScheduleNextSyncAsync();
            App.LoginChanged += (login) => 
            {
                if (login != null)
                {
                    AppLogger.Info($"[ContentCache] Login detected for {login.PlaylistName}. Triggering background sync.");
                    _ = SyncPlaylistAsync(login);
                    _ = RefreshIptvMatchIndexAsync();
                }
            };

            // Handle current login if already set during initialization
            if (App.CurrentLogin != null)
            {
                AppLogger.Info($"[ContentCache] Current login already set for {App.CurrentLogin.PlaylistName}. Triggering initial sync.");
                _ = SyncPlaylistAsync(App.CurrentLogin);
            }

            _throttledSaveTimer = new Timer(async _ => await ProcessThrottledSavesAsync(), null, -1, -1);
            _ = StartBackgroundSyncAsync();
        }

        private async Task StartBackgroundSyncAsync()
        {
            // 1. Initial Index Build (from disk cache)
            _ = Task.Run(async () => {
                await Task.Delay(3000); // Wait 3s for stability
                System.Diagnostics.Debug.WriteLine("[BackgroundSync] Triggering initial index refresh...");
                await RefreshIptvMatchIndexAsync();
            });

            // 2. Continuous Pruning (Independent of sync)
            _ = Task.Run(async () => {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(30));
                    PruneMemoryCache();
                }
            });

            // 3. Start Reactive Sync Loop
            await ScheduleNextSyncAsync();
        }

        private void PruneMemoryCache()
        {
            if (_memoryCache.Count > 100)
            {
                var now = DateTime.UtcNow;
                var keysToRemove = _memoryCache.Where(k => (now - k.Value.LastAccessed).TotalMinutes > 30).Select(k => k.Key).ToList();
                foreach (var key in keysToRemove) _memoryCache.TryRemove(key, out _);
            }
        }

        private async Task ScheduleNextSyncAsync()
        {
            _syncCts?.Cancel();
            _syncCts = new CancellationTokenSource();
            var ct = _syncCts.Token;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!AppSettings.IsAutoCacheEnabled)
                    {
                        // Wait indefinitely until settings change (which will cancel this delay)
                        await Task.Delay(-1, ct);
                        return;
                    }

                    var lastUpdate = AppSettings.LastLiveCacheTime;
                    var interval = TimeSpan.FromMinutes(AppSettings.CacheIntervalMinutes);
                    var nextSync = lastUpdate.Add(interval);
                    var delay = nextSync - DateTime.Now;

                    if (delay <= TimeSpan.Zero)
                    {
                        // Interval passed! Trigger sync.
                        System.Diagnostics.Debug.WriteLine($"[Sync] Threshold reached. Triggering CacheExpired event. (Interval: {AppSettings.CacheIntervalMinutes}m)");
                        CacheExpired?.Invoke(this, EventArgs.Empty);
                        
                        // After trigger, wait for the FULL next interval
                        delay = interval;
                    }

                    System.Diagnostics.Debug.WriteLine($"[Sync] Next sync scheduled in {delay.TotalMinutes:F1} minutes.");
                    await Task.Delay(delay, ct);
                }
            }
            catch (TaskCanceledException) { /* Setting changed, rescheduling... */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sync] Error in sync loop: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(15), ct); // Fallback retry
            }
        }

        public async Task RefreshIptvMatchIndexAsync(string? playlistId = null)
        {
            var login = App.CurrentLogin;
            if (login == null) return;
            string targetId = playlistId ?? login.PlaylistId ?? AppSettings.LastPlaylistId?.ToString() ?? "default";

            await Task.Run(async () => {
                IsIndexing = true;
                AppLogger.Info($"[Index] Refreshing Global Index for Playlist: {targetId}");
                try
                {
                    // 1. Get Current Hashes to check if index is stale
                    string vodHash = await ReadTextAsync($"cache_{targetId}_vod.json.gz" + HASH_EXT) ?? "";
                    string seriesHash = await ReadTextAsync($"cache_{targetId}_series.json.gz" + HASH_EXT) ?? "";

                    // 2. Load data from disk (Objects needed for both cache-hit and cache-miss)
                    var vods = await LoadCacheAsync<VodStream>(targetId, "vod") ?? new List<VodStream>();
                    var series = await LoadCacheAsync<SeriesStream>(targetId, "series") ?? new List<SeriesStream>();

                    if (vods.Count == 0 && series.Count == 0)
                    {
                         AppLogger.Error($"[Index] CRITICAL: Both VOD and Series lists are EMPTY. Indexing aborted.");
                         return;
                    }

                    // 3. Try Loading Persistent Index
                    string indexFile = $"cache_{targetId}_match_index.json.gz";
                    var persisted = await LoadCacheObjectAsync<PersistentIptvIndex>(targetId, "match_index");
                    
                    if (persisted != null && persisted.VodHash == vodHash && persisted.SeriesHash == seriesHash)
                    {
                        AppLogger.Warn($"[Index] Persisted Index Match! Loading from disk for '{targetId}' (VodHash: {vodHash.Substring(0, 6)}...)");
                        
                        var imdbIndex = new Dictionary<string, List<IMediaStream>>(StringComparer.OrdinalIgnoreCase);
                        var tokenIndex = new Dictionary<string, List<IMediaStream>>(StringComparer.OrdinalIgnoreCase);

                        // Helper to map IDs back to objects
                        var vodMap = vods.ToDictionary(v => v.StreamId);
                        var seriesMap = series.ToDictionary(s => s.SeriesId);

                        // Reconstruct ImdbIndex
                        foreach(var kvp in persisted.ImdbIndex)
                        {
                            var list = new List<IMediaStream>();
                            foreach(var idRef in kvp.Value)
                            {
                                if (idRef.IsSeries && seriesMap.TryGetValue(idRef.Id, out var s)) list.Add(s);
                                else if (!idRef.IsSeries && vodMap.TryGetValue(idRef.Id, out var v)) list.Add(v);
                            }
                            if (list.Any()) imdbIndex[kvp.Key] = list;
                        }

                        // Reconstruct TokenIndex
                        foreach(var kvp in persisted.TokenIndex)
                        {
                            var list = new List<IMediaStream>();
                            foreach(var idRef in kvp.Value)
                            {
                                if (idRef.IsSeries && seriesMap.TryGetValue(idRef.Id, out var s)) list.Add(s);
                                else if (!idRef.IsSeries && vodMap.TryGetValue(idRef.Id, out var v)) list.Add(v);
                            }
                            if (list.Any()) tokenIndex[kvp.Key] = list;
                        }

                        GlobalTokenIndex = tokenIndex;
                        IptvMatchService.Instance.UpdateIndices(vods, series, imdbIndex, tokenIndex);
                        AppLogger.Info($"[Index] Persisted Index Loaded successfully in RAM.");
                        return;
                    }

                    AppLogger.Warn($"[Index] Building Index from scratch for '{targetId}' (Hash mismatch or no index found)...");
                    
                    // 4. Build Indices in one pass
                    var imdbIndexNew = new Dictionary<string, List<IMediaStream>>(StringComparer.OrdinalIgnoreCase);
                    var tokenIndexNew = new Dictionary<string, List<IMediaStream>>(StringComparer.OrdinalIgnoreCase);
                    
                    // Persistence structure
                    var persistImdb = new Dictionary<string, List<StreamIdRef>>(StringComparer.OrdinalIgnoreCase);
                    var persistToken = new Dictionary<string, List<StreamIdRef>>(StringComparer.OrdinalIgnoreCase);

                    int count = 0;
                    foreach (var item in vods.Cast<IMediaStream>().Concat(series))
                    {
                        if (++count % 10000 == 0) await Task.Yield();
                        
                        bool isSeries = item is SeriesStream;
                        int id = isSeries ? (item as SeriesStream).SeriesId : (item as VodStream).StreamId;
                        var idRef = new StreamIdRef { Id = id, IsSeries = isSeries };

                        // IMDb Index
                        if (!string.IsNullOrEmpty(item.IMDbId))
                        {
                            string normalizedId = item.IMDbId;
                            if (normalizedId.Contains(":")) normalizedId = normalizedId.Split(':').Last();

                            if (!imdbIndexNew.TryGetValue(normalizedId, out var list))
                            {
                                list = new List<IMediaStream>();
                                imdbIndexNew[normalizedId] = list;
                                persistImdb[normalizedId] = new List<StreamIdRef>();
                            }
                            list.Add(item);
                            persistImdb[normalizedId].Add(idRef);
                        }

                        // Token Index
                        if (!string.IsNullOrEmpty(item.Title))
                        {
                            var tokens = TitleHelper.GetSignificantTokens(item.Title);
                            foreach (var t in tokens)
                            {
                                if (!tokenIndexNew.TryGetValue(t, out var list))
                                {
                                    list = new List<IMediaStream>();
                                    tokenIndexNew[t] = list;
                                    persistToken[t] = new List<StreamIdRef>();
                                }
                                list.Add(item);
                                persistToken[t].Add(idRef);
                            }
                        }
                    }

                    // 5. Save for next time
                    var newPersisted = new PersistentIptvIndex {
                        VodHash = vodHash,
                        SeriesHash = seriesHash,
                        ImdbIndex = persistImdb,
                        TokenIndex = persistToken
                    };
                    _ = SaveSingularCacheAsync(targetId, "match_index", newPersisted);

                    // 6. Atomically Update
                    GlobalTokenIndex = tokenIndexNew;
                    IptvMatchService.Instance.UpdateIndices(vods, series, imdbIndexNew, tokenIndexNew);
                    AppLogger.Info($"[Index] Global Index Refreshed & Saved: {vods.Count} VODs, {series.Count} Series. Total Tokens: {tokenIndexNew.Count}");
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"[Index] Refresh Failed: {ex.Message}", ex);
                }
                finally
                {
                    IsIndexing = false;
                }
            });
        }

        public async Task SyncPlaylistAsync(LoginParams login)
        {
            if (login == null) { AppLogger.Warn("[ContentCache] Sync: null login"); return; }
            if (string.IsNullOrEmpty(login.Host)) { AppLogger.Warn("[ContentCache] Sync: No host for Xtream login"); return; }

            string playlistId = login.PlaylistId;
            var interval = TimeSpan.FromMinutes(AppSettings.CacheIntervalMinutes);
            
            var lastVod = AppSettings.LastVodCacheTime;
            var lastSeries = AppSettings.LastSeriesCacheTime;
            
            bool needsVod = (DateTime.Now - lastVod) > interval;
            bool needsSeries = (DateTime.Now - lastSeries) > interval;
            
            AppLogger.Info($"[ContentCache] Sync Check: playlist={playlistId}, lastVod={lastVod}, lastSeries={lastSeries}, interval={interval.TotalMinutes}m, needsVod={needsVod}, needsSeries={needsSeries}");

            if (needsVod || needsSeries)
            {
                AppLogger.Info($"[ContentCache] Background Sync: Fetching data for {login.PlaylistName} (VOD: {needsVod}, Series: {needsSeries})");
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var tasks = new List<Task>();

                if (needsVod)
                {
                    tasks.Add(Task.Run(async () => {
                        try
                        {
                            string api = $"{login.Host}/player_api.php?username={login.Username}&password={login.Password}&action=get_vod_streams";
                            string json = await HttpHelper.Client.GetStringAsync(api);
                            var vods = JsonSerializer.Deserialize<List<VodStream>>(json, options) ?? new();
                            if (vods.Count > 0)
                            {
                                await SaveCacheAsync(playlistId, "vod", vods);
                                AppSettings.LastVodCacheTime = DateTime.Now;
                            }
                        }
                        catch (Exception ex) { AppLogger.Error("[ContentCache] BG VOD Sync Failed", ex); }
                    }));
                }

                if (needsSeries)
                {
                    tasks.Add(Task.Run(async () => {
                        try
                        {
                            string api = $"{login.Host}/player_api.php?username={login.Username}&password={login.Password}&action=get_series";
                            string json = await HttpHelper.Client.GetStringAsync(api);
                            AppLogger.Info($"[ContentCache] Series API Downloaded. Length: {json?.Length ?? 0}");
                            
                            var series = JsonSerializer.Deserialize<List<SeriesStream>>(json, options) ?? new();
                            AppLogger.Info($"[ContentCache] Series Deserialized: {series.Count} items.");
                            
                            if (series.Count > 0)
                            {
                                await SaveCacheAsync(playlistId, "series", series);
                                AppSettings.LastSeriesCacheTime = DateTime.Now;
                            }
                        }
                        catch (Exception ex) { AppLogger.Error("[ContentCache] BG Series Sync Failed", ex); }
                    }));
                }

                await Task.WhenAll(tasks);
                
                // Refresh index ONCE after both are potentially updated
                _ = RefreshIptvMatchIndexAsync(playlistId);
                AppLogger.Info("[ContentCache] Background Sync Cycle Complete. Index Refresh Triggered.");
            }
        }

        public event EventHandler CacheExpired;
        public event EventHandler<string> UpdateAvailable; // Args: Message

        // GENERIC SAVE/LOAD
        
        public async Task<List<T>> LoadCacheAsync<T>(string playlistId, string category)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string cacheKey = $"{safeId}_{category}";
            if (_streamListsCache.TryGetValue(cacheKey, out var cached) && cached is List<T> list)
            {
                return list;
            }

            string fileName = $"cache_{safeId}_{category}.json.gz";
            
            await _diskSemaphore.WaitAsync();
            try 
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return null;

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = await JsonSerializer.DeserializeAsync<List<T>>(gzip, options);
                
                if (result != null)
                {
                    _streamListsCache[cacheKey] = result;
                    AppLogger.Info($"[ContentCache] Loaded {result.Count} {category} items from {fileName}");
                }
                
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[ContentCache] LOAD FAILED for {category} (Playlist: {playlistId}, File: {fileName}): {ex.Message}", ex);
                return null;
            }
            finally
            {
                _diskSemaphore.Release();
            }
        }

        public async Task SaveCacheAsync<T>(string playlistId, string category, List<T> data)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_{category}.json.gz";
            try
            {
                // Run heavy serialization & hashing on background thread to prevent UI freeze
                await Task.Run(async () =>
                {
                    var folder = ApplicationData.Current.LocalFolder;
                    
                    // 1. Serialize to buffer to calc Hash + GZip
                    using var ms = new MemoryStream();
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    await JsonSerializer.SerializeAsync(ms, data, options);
                    ms.Position = 0;

                    // 2. Calculate Hash
                    string newHash = ComputeHash(ms);
                    ms.Position = 0;

                    // 3. Compare with Old Hash
                    string hashFile = fileName + HASH_EXT;
                    string oldHash = await ReadTextAsync(hashFile);

                    if (newHash != oldHash)
                    {
                        // DATA CHANGED
                        System.Diagnostics.Debug.WriteLine($"[ContentCache] Data Changed for {category}. New Hash: {newHash}");
                        
                        await _diskSemaphore.WaitAsync();
                        try
                        {
                            // 3. Write to Disk
                            using var fileStream = await folder.OpenStreamForWriteAsync(fileName, CreationCollisionOption.ReplaceExisting);
                            ms.Position = 0;
                            using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
                            await ms.CopyToAsync(gzipStream);
                            
                            AppLogger.Info($"[ContentCache] File Saved: {fileName} (Hash: {newHash}, Size: {ms.Length} bytes)");

                            // Save Hash
                            await WriteTextAsync(hashFile, newHash);
                        }
                        finally
                        {
                            _diskSemaphore.Release();
                        }
                        
                        // Update Memory Cache
                        string cacheKey = $"{safeId}_{category}";
                        _streamListsCache[cacheKey] = data;

                        // Notify User if not first run (OldHash exists)
                        // Note: Events should be invoked on UI thread if they touch UI, but string message is safe
                        if (!string.IsNullOrEmpty(oldHash))
                        {
                            UpdateAvailable?.Invoke(this, $"Yeni {category} İçeriği Mevcut!");
                        }

                        // Update Timestamp
                        if (category == "live") AppSettings.LastLiveCacheTime = DateTime.Now;
                        else if (category == "vod") AppSettings.LastVodCacheTime = DateTime.Now;
                        else if (category == "series") AppSettings.LastSeriesCacheTime = DateTime.Now;
                    }
                    else
                    {
                         System.Diagnostics.Debug.WriteLine($"[ContentCache] No Changes for {category}. (Hash Match)");
                         if (category == "live") AppSettings.LastLiveCacheTime = DateTime.Now;
                         else if (category == "vod") AppSettings.LastVodCacheTime = DateTime.Now;
                         else if (category == "series") AppSettings.LastSeriesCacheTime = DateTime.Now;
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ContentCache] Save Failed for {category}: {ex.Message}");
            }
        }

        private string ComputeHash(Stream stream)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(stream);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private string GetSafePlaylistId(string playlistId)
        {
            if (string.IsNullOrEmpty(playlistId)) return "default";
            
            // Check for invalid path characters or URLs
            if (playlistId.Contains("://") || playlistId.Length > 40 || playlistId.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            {
                using (var md5 = MD5.Create())
                {
                    byte[] inputBytes = Encoding.UTF8.GetBytes(playlistId);
                    byte[] hashBytes = md5.ComputeHash(inputBytes);
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant().Substring(0, 16);
                }
            }
            return playlistId;
        }

        private async Task<string> ReadTextAsync(string filename)
        {
            try 
            {
                var folder = ApplicationData.Current.LocalFolder;
                var files = await folder.GetFilesAsync();
                if (files.Any(f => f.Name == filename))
                {
                    var file = await folder.GetFileAsync(filename);
                    return await FileIO.ReadTextAsync(file);
                }
                return null;
            }
            catch { return null; }
        }

        private async Task WriteTextAsync(string filename, string content)
        {
            try 
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, content);
            }
            catch { }
        }
        
        public async Task ClearCacheAsync()
        {
            try
            {
                _memoryCache.Clear(); // Clear RAM

                var folder = ApplicationData.Current.LocalFolder;
                var files = await folder.GetFilesAsync();
                foreach (var file in files)
                {
                    if (file.Name.StartsWith("cache_") && (file.Name.EndsWith(".json.gz") || file.Name.EndsWith(".hash")))
                    {
                        await file.DeleteAsync();
                    }
                }
                
                // Reset Timestamps
                AppSettings.LastLiveCacheTime = DateTime.MinValue;
                AppSettings.LastVodCacheTime = DateTime.MinValue;
                AppSettings.LastSeriesCacheTime = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[ContentCache] Clear Failed: {ex.Message}");
            }
        }

        // SEARCH INDEXING
        public void BuildSearchIndex(string category, IEnumerable<dynamic> items)
        {
            // Background Task
            Task.Run(() => 
            {
                // Simple implementation: Just store reference or build trie
            });
        }
        // SERIES INFO CACHING
        public async Task<SeriesInfoResult> GetSeriesInfoAsync(int seriesId, LoginParams login)
        {
            string cacheKey = $"series_info_{seriesId}";
            string playlistId = login.PlaylistUrl ?? "default";

            // 0. SEARCH MEMORY CACHE
            if (_memoryCache.TryGetValue(cacheKey, out var memEntry))
            {
                // Update access time
                _memoryCache[cacheKey] = (memEntry.Data, DateTime.UtcNow);
                Services.CacheLogger.Info(Services.CacheLogger.Category.Content, "RAM HIT", cacheKey);
                return memEntry.Data as SeriesInfoResult;
            }
            
            // 1. Try Disk Cache
            var cached = await LoadCacheObjectAsync<SeriesInfoResult>(playlistId, cacheKey);

            if (cached != null) 
            {
                Services.CacheLogger.Info(Services.CacheLogger.Category.Content, "DISK HIT", cacheKey);
                // [LOG FOR USER] Log the cached data as JSON
                try {
                    string cachedJson = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true });
                    AppLogger.Warn($"[IPTV_CACHE_SERIES] SeriesId: {seriesId} | CACHED JSON: {cachedJson}");
                } catch { }

                // Promote to RAM
                _memoryCache[cacheKey] = (cached, DateTime.UtcNow);
                return cached;
            }
            
            System.Diagnostics.Debug.WriteLine($"[ContentCache] MISS for key: {cacheKey}. Fetching from network...");

            // 2. Fetch Network
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string url = $"{login.Host.TrimEnd('/')}/player_api.php?username={login.Username}&password={login.Password}&action=get_series_info&series_id={seriesId}";
                using var client = new System.Net.Http.HttpClient();
                var json = await client.GetStringAsync(url);
                
                // [NEW] Robust RAW Debug Log
                await WriteDebugJsonAsync($"series_{seriesId}_raw.json", json);
                AppLogger.Warn($"[IPTV_RAW_SERIES] SeriesId: {seriesId} | Raw JSON saved to debug file.");

                sw.Stop();
                Services.CacheLogger.Info(Services.CacheLogger.Category.Content, "Network Fetch", $"{sw.ElapsedMilliseconds}ms | {json.Length} bytes");
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
                var result = JsonSerializer.Deserialize<SeriesInfoResult>(json, options);

                if (result != null)
                {
                    Services.CacheLogger.Success(Services.CacheLogger.Category.Content, "Fetch Success", $"Episodes: {result.Episodes?.Count ?? 0}");
                    
                    // Save Disk + RAM
                    _memoryCache[cacheKey] = (result, DateTime.UtcNow); // RAM
                    await SaveSingularCacheAsync(playlistId, cacheKey, result); // Disk

                    return result;
                }
                else
                {
                     Services.CacheLogger.Warning(Services.CacheLogger.Category.Content, "Fetch Result NULL");
                }
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.Content, "SeriesFetch Error", ex.Message);
            }

            return null;
        }

        private async Task SaveSingularCacheAsync<T>(string playlistId, string key, T data)
        {
             string safeId = GetSafePlaylistId(playlistId);
             string fileName = $"cache_{safeId}_{key}.json.gz";
             try
             {
                 var folder = ApplicationData.Current.LocalFolder;
                 using var ms = new MemoryStream();
                 var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                 await JsonSerializer.SerializeAsync(ms, data, options);
                 ms.Position = 0;
                 
                 using var fileStream = await folder.OpenStreamForWriteAsync(fileName, CreationCollisionOption.ReplaceExisting);
                 using var gzip = new GZipStream(fileStream, CompressionLevel.Fastest);
                 await ms.CopyToAsync(gzip);
             }
             catch { }
        }

        // VOD INFO CACHING
        public async Task<MovieInfoResult> GetMovieInfoAsync(int streamId, LoginParams login)
        {
            string cacheKey = $"vod_info_{streamId}";
            string playlistId = login.PlaylistUrl ?? "default";

            // 0. SEARCH MEMORY CACHE
            if (_memoryCache.TryGetValue(cacheKey, out var memEntry))
            {
                _memoryCache[cacheKey] = (memEntry.Data, DateTime.UtcNow);
                Services.CacheLogger.Info(Services.CacheLogger.Category.Content, "RAM HIT", cacheKey);
                return memEntry.Data as MovieInfoResult;
            }
            
            // 1. Try Disk Cache
            var cached = await LoadCacheObjectAsync<MovieInfoResult>(playlistId, cacheKey);
            if (cached != null) 
            {
                Services.CacheLogger.Info(Services.CacheLogger.Category.Content, "DISK HIT", cacheKey);
                // [LOG FOR USER] Log the cached data as JSON
                try {
                    string cachedJson = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true });
                    AppLogger.Warn($"[IPTV_CACHE_VOD] StreamId: {streamId} | CACHED JSON: {cachedJson}");
                } catch { }

                _memoryCache[cacheKey] = (cached, DateTime.UtcNow);
                return cached;
            }
            
            // 2. Fetch Network
            try
            {
                string url = $"{login.Host.TrimEnd('/')}/player_api.php?username={login.Username}&password={login.Password}&action=get_vod_info&vod_id={streamId}";
                using var client = new System.Net.Http.HttpClient();
                var json = await client.GetStringAsync(url);
                
                // [NEW] Robust RAW Debug Log
                await WriteDebugJsonAsync($"vod_{streamId}_raw.json", json);
                AppLogger.Warn($"[IPTV_RAW_VOD] StreamId: {streamId} | Raw JSON saved to debug file.");
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
                var result = JsonSerializer.Deserialize<MovieInfoResult>(json, options);

                if (result != null)
                {
                    Services.CacheLogger.Success(Services.CacheLogger.Category.Content, "Fetch Success", $"Movie: {result.Info?.Name ?? "Unknown"}");
                    
                    // Save Disk + RAM
                    _memoryCache[cacheKey] = (result, DateTime.UtcNow);
                    await SaveSingularCacheAsync(playlistId, cacheKey, result);
                    return result;
                }
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.Content, "VodFetch Error", ex.Message);
            }

            return null;
        }

        public async Task<T> LoadCacheObjectAsync<T>(string playlistId, string key)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_{key}.json.gz";
            try 
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) 
                {
                    return default;
                }

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var obj = await JsonSerializer.DeserializeAsync<T>(gzip, options);
                return obj;
            }
            catch (Exception ex)
            { 
                System.Diagnostics.Debug.WriteLine($"[ContentCache] Load Error ({fileName}): {ex.Message}");
                return default; 
            }
        }

        private async Task WriteDebugJsonAsync(string filename, string content)
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var debugFolder = await folder.CreateFolderAsync("DebugLogs", CreationCollisionOption.OpenIfExists);
                var file = await debugFolder.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, content);
                
                // Also log first 500 chars to console just in case
                string snippet = content.Length > 500 ? content.Substring(0, 500) + "..." : content;
                System.Diagnostics.Debug.WriteLine($"[DEBUG_RAW_JSON] {filename}: {snippet}");
            }
            catch { }
        }

        // ==========================================
        // MATCH PERSISTENCE (THROTTLED)
        // ==========================================

        public Task TriggerThrottledSaveAsync(string category)
        {
            _pendingSaveCategories.TryAdd(category, 0);
            _throttledSaveTimer.Change(15000, -1); // Save in 15 seconds to batch multiple matches
            return Task.CompletedTask;
        }

        private async Task ProcessThrottledSavesAsync()
        {
            var categories = _pendingSaveCategories.Keys.ToList();
            _pendingSaveCategories.Clear();

            var login = App.CurrentLogin;
            if (login == null) return;

            AppLogger.Info($"[ContentCache] Processing throttled saves for categories: {string.Join(", ", categories)}");

            foreach (var cat in categories)
            {
                try
                {
                    string safeId = GetSafePlaylistId(login.PlaylistId);
                    string cacheKey = $"{safeId}_{cat}";

                    if (_streamListsCache.TryGetValue(cacheKey, out var data))
                    {
                        if (cat == "vod" && data is List<VodStream> vodList)
                        {
                            await SaveCacheAsync(login.PlaylistId, "vod", vodList);
                        }
                        else if (cat == "series" && data is List<SeriesStream> seriesList)
                        {
                            await SaveCacheAsync(login.PlaylistId, "series", seriesList);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"[ContentCache] Throttled Save Error ({cat}): {ex.Message}");
                }
            }
        }
    }

    public class SeriesInfoResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("episodes")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<Dictionary<string, List<SeriesEpisodeDef>>>))]
        public Dictionary<string, List<SeriesEpisodeDef>> Episodes { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("info")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<SeriesInfoDetails>))]
        public SeriesInfoDetails Info { get; set; }
    }

    public class SeriesInfoDetails
    {
        [System.Text.Json.Serialization.JsonPropertyName("tmdb_id")]
        public object TmdbId { get; set; } // Can be int or string
        
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("cover")]
        public string Cover { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("plot")]
        public string Plot { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("cast")]
        public string Cast { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("genre")]
        public string Genre { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("director")]
        public string Director { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("rating")]
        public string Rating { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("air_date")]
        public string AirDate { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("age")]
        public string Age { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("country")]
        public string Country { get; set; }
    }

    public class SeriesEpisodeDef
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("episode_num")]
        public object EpisodeNum { get; set; } // Can be int or string
        
        [System.Text.Json.Serialization.JsonPropertyName("season")]
        public object Season { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string Title { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("info")]
        public SeriesEpisodeInfo Info { get; set; }
    }

    public class SeriesEpisodeInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("movie_image")]
        public string MovieImage { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("plot")]
        public string Plot { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("duration")]
        public string Duration { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("air_date")]
        public string AirDate { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("video")]
        public TechnicalVideoInfo Video { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("audio")]
        public TechnicalAudioInfo Audio { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("bitrate")]
        public object Bitrate { get; set; } // Can be int or string
    }

    public class TechnicalVideoInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("codec_name")]
        public string CodecName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("width")]
        public int Width { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("height")]
        public int Height { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("display_aspect_ratio")]
        public object? AspectRatio { get; set; }
    }

    public class TechnicalAudioInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("codec_name")]
        public string CodecName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("channels")]
        public int Channels { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("channel_layout")]
        public string ChannelLayout { get; set; }
    }

    public class MovieInfoResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("info")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<MovieInfoDetails>))]
        public MovieInfoDetails Info { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("movie_data")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<MovieDataDetails>))]
        public MovieDataDetails MovieData { get; set; }
    }

    public class MovieInfoDetails
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("movie_image")]
        public string MovieImage { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("cover_big")]
        public string CoverBig { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("plot")]
        public string Plot { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("cast")]
        public string Cast { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("genre")]
        public string Genre { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("director")]
        public string Director { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("rating")]
        public object? RatingRaw { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string Rating => RatingRaw?.ToString() ?? "";
        
        [System.Text.Json.Serialization.JsonPropertyName("releasedate")]
        public string ReleaseDate { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("air_date")]
        public string AirDate { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("released")]
        public string Released { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("youtube_trailer")]
        public string YoutubeTrailer { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("backdrop_path")]
        public string[] BackdropPath { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("age")]
        public string Age { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("mpaa_rating")]
        public string MpaaRating { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("country")]
        public string Country { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("duration")]
        public string Duration { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("video")]
        public TechnicalVideoInfo Video { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("audio")]
        public TechnicalAudioInfo Audio { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("bitrate")]
        public object Bitrate { get; set; }
    }
    
    public class MovieDataDetails
    {
        [System.Text.Json.Serialization.JsonPropertyName("stream_id")]
        public int StreamId { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; }
    }

    // [FIX] Generic converter to handle Xtream Codes API inconsistency (empty array [] instead of object {})
    public class SafeObjectConverter<T> : JsonConverter<T> where T : class
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                reader.Skip();
                return null;
            }
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                return JsonSerializer.Deserialize<T>(ref reader, options);
            }
            return null;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }

    public class PersistentIptvIndex
    {
        public string VodHash { get; set; }
        public string SeriesHash { get; set; }
        public Dictionary<string, List<StreamIdRef>> ImdbIndex { get; set; }
        public Dictionary<string, List<StreamIdRef>> TokenIndex { get; set; }
    }

    public class StreamIdRef
    {
        public int Id { get; set; }
        public bool IsSeries { get; set; }
    }
}
