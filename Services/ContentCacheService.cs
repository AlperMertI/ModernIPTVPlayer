using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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

        private ContentCacheService()
        {
            // Start Background Timer
            _ = StartBackgroundSyncAsync();
        }

        private async Task StartBackgroundSyncAsync()
        {
            var timer = new System.Threading.PeriodicTimer(TimeSpan.FromMinutes(15)); // Check every 15 mins
            while (await timer.WaitForNextTickAsync())
            {
                // Prune Memory Cache randomly/periodically
                if (_memoryCache.Count > 100)
                {
                    // Remove entries older than 30 mins unused
                    var now = DateTime.UtcNow;
                    var keysToRemove = _memoryCache.Where(k => (now - k.Value.LastAccessed).TotalMinutes > 30).Select(k => k.Key).ToList();
                    foreach (var key in keysToRemove) _memoryCache.TryRemove(key, out _);
                }

                if (AppSettings.IsAutoCacheEnabled && !_isSyncing)
                {
                    // Check if interval passed
                    var lastUpdate = AppSettings.LastLiveCacheTime;
                    if ((DateTime.Now - lastUpdate).TotalMinutes > AppSettings.CacheIntervalMinutes)
                    {
                        // Needs update
                        CacheExpired?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        public event EventHandler CacheExpired;
        public event EventHandler<string> UpdateAvailable; // Args: Message

        // GENERIC SAVE/LOAD
        
        public async Task<List<T>> LoadCacheAsync<T>(string playlistId, string category)
        {
            string fileName = $"cache_{playlistId}_{category}.json.gz";
            try 
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return null;

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return await JsonSerializer.DeserializeAsync<List<T>>(gzip, options);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ContentCache] Load Failed for {category}: {ex.Message}");
                return null;
            }
        }

        public async Task SaveCacheAsync<T>(string playlistId, string category, List<T> data)
        {
            string fileName = $"cache_{playlistId}_{category}.json.gz";
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
                        
                        // Save Data
                        using var fileStream = await folder.OpenStreamForWriteAsync(fileName, CreationCollisionOption.ReplaceExisting);
                        using var gzip = new GZipStream(fileStream, CompressionLevel.Fastest);
                        await ms.CopyToAsync(gzip); // Copy JSON bytes -> GZip -> File

                        // Save Hash
                        await WriteTextAsync(hashFile, newHash);
                        
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
             string safeId = playlistId;
             if (playlistId.Contains("://") || playlistId.Length > 50)
             {
                 using (var md5 = System.Security.Cryptography.MD5.Create())
                 {
                     byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(playlistId);
                     byte[] hashBytes = md5.ComputeHash(inputBytes);
                     safeId = Convert.ToHexString(hashBytes);
                 }
             }

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
                _memoryCache[cacheKey] = (cached, DateTime.UtcNow);
                return cached;
            }
            
            // 2. Fetch Network
            try
            {
                string url = $"{login.Host.TrimEnd('/')}/player_api.php?username={login.Username}&password={login.Password}&action=get_vod_info&vod_id={streamId}";
                using var client = new System.Net.Http.HttpClient();
                var json = await client.GetStringAsync(url);
                
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
            string safeId = playlistId;
            if (playlistId.Contains("://") || playlistId.Length > 50)
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(playlistId);
                    byte[] hashBytes = md5.ComputeHash(inputBytes);
                    safeId = Convert.ToHexString(hashBytes);
                }
            }
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
    }

    public class SeriesInfoResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("episodes")]
        public Dictionary<string, List<SeriesEpisodeDef>> Episodes { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("info")]
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
    }

    public class MovieInfoResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("info")]
        public MovieInfoDetails Info { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("movie_data")]
        public MovieDataDetails MovieData { get; set; }
    }

    public class MovieInfoDetails
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("movie_image")]
        public string MovieImage { get; set; }
        
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
        
        [System.Text.Json.Serialization.JsonPropertyName("releasedate")]
        public string ReleaseDate { get; set; }
    }
    
    public class MovieDataDetails
    {
        [System.Text.Json.Serialization.JsonPropertyName("stream_id")]
        public int StreamId { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; }
    }
}
