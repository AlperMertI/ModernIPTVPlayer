using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace ModernIPTVPlayer.Services
{
    public class TmdbCacheEntry
    {
        public string JsonData { get; set; }
        public DateTime LastUpdated { get; set; }
        // We handle expiration (7 days) logic in service
    }

    public class TmdbCacheService
    {
        private static Lazy<TmdbCacheService> _instance = new(() => new TmdbCacheService());
        public static TmdbCacheService Instance => _instance.Value;

        private ConcurrentDictionary<string, TmdbCacheEntry> _cache = new();
        private bool _isDirty = false;
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private const string CACHE_FILE = "TmdbCache.json.gz";
        private const int EXPIRE_DAYS = 7;

        private Task _initTask;
        private Timer _saveTimer;

        public TmdbCacheService()
        {
            _initTask = LoadCacheAsync();
            // Debounce save timer, initially disabled (-1, -1)
            _saveTimer = new Timer(async _ => await SaveIfDirtyAsync(), null, -1, -1);
        }

        public Task EnsureLoadedAsync() => _initTask;

        public async Task LoadCacheAsync()
        {
            try
            {
                await _fileLock.WaitAsync();
                var folder = ApplicationData.Current.LocalFolder;
                if (await folder.TryGetItemAsync(CACHE_FILE) is StorageFile file)
                {
                    using var stream = await file.OpenStreamForReadAsync();
                    using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip);
                    var json = await reader.ReadToEndAsync();
                    
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, TmdbCacheEntry>>(json);
                    if (loaded != null)
                    {
                        var now = DateTime.UtcNow;
                        // Prune expired
                        foreach (var kvp in loaded)
                        {
                            if ((now - kvp.Value.LastUpdated).TotalDays < EXPIRE_DAYS)
                            {
                                _cache[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    Services.CacheLogger.Info(Services.CacheLogger.Category.TMDB, "Loaded Cache", $"{_cache.Count} entries.");
                }
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.TMDB, "Load Failed", ex.Message);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public async Task SaveIfDirtyAsync()
        {
            if (!_isDirty) return;

            try
            {
                await _fileLock.WaitAsync();
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(CACHE_FILE, CreationCollisionOption.ReplaceExisting);

                using var stream = await file.OpenStreamForWriteAsync();
                using var gzip = new GZipStream(stream, CompressionLevel.Optimal);
                using var writer = new StreamWriter(gzip);
                
                var json = JsonSerializer.Serialize(_cache);
                await writer.WriteAsync(json);
                
                _isDirty = false;
                Services.CacheLogger.Info(Services.CacheLogger.Category.TMDB, "Saved to Disk", $"{_cache.Count} entries.");
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.TMDB, "Save Failed", ex.Message);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public void Remove(string key)
        {
            if (_cache.TryRemove(key, out _))
            {
                _isDirty = true;
                _saveTimer.Change(5000, -1);
            }
        }

        public T? Get<T>(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if ((DateTime.UtcNow - entry.LastUpdated).TotalDays < EXPIRE_DAYS)
                {
                    try
                    {
                        var data = JsonSerializer.Deserialize<T>(entry.JsonData);
                        Services.CacheLogger.Info(Services.CacheLogger.Category.TMDB, "HIT", key);
                        return data;
                    }
                    catch (Exception ex)
                    {
                        Services.CacheLogger.Error(Services.CacheLogger.Category.TMDB, "Deserialization Error", ex.Message);
                        // Heal structure: Remove corrupt entry so we fetch fresh next time
                        _cache.TryRemove(key, out _);
                        _isDirty = true;
                        return default;
                    }
                }
                else
                {
                    // Expired
                    Services.CacheLogger.Info(Services.CacheLogger.Category.TMDB, "EXPIRED", key);
                    _cache.TryRemove(key, out _);
                    _isDirty = true;
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB Cache] MISS: {key}");
            }
            return default;
        }

        public void Set<T>(string key, T data)
        {
            if (data == null) return;
            
            var json = JsonSerializer.Serialize(data);
            _cache[key] = new TmdbCacheEntry
            {
                JsonData = json,
                LastUpdated = DateTime.UtcNow
            };
            _isDirty = true;
            
            // Debounce Save (5s)
            _saveTimer.Change(5000, -1);
        }

        public async Task ClearCacheAsync()
        {
            _cache.Clear();
            _isDirty = true;
            await SaveIfDirtyAsync();
        }
    }
}
