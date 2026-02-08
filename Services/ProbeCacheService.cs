using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace ModernIPTVPlayer.Services
{
    public class ProbeData
    {
        public string Resolution { get; set; }
        public string Fps { get; set; }
        public string Codec { get; set; }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class ProbeCacheService
    {
        private static ProbeCacheService _instance;
        public static ProbeCacheService Instance => _instance ??= new ProbeCacheService();

        private ConcurrentDictionary<string, ProbeData> _cache = new();
        private const string CacheFileName = "ProbeCache.json.gz";
        private const int TTL_DAYS = 3;
        private bool _isDirty = false;
        private DateTime _lastSaveTime = DateTime.MinValue;
        private System.Threading.Timer _saveTimer;
        
        public event EventHandler CacheCleared;

        // Initialization
        private TaskCompletionSource<bool> _initTcs = new TaskCompletionSource<bool>();
        private bool _isLoaded = false;
        private bool _warnedBeforeLoad = false;

        private ProbeCacheService()
        {
            _saveTimer = new System.Threading.Timer(async _ => await SaveIfDirtyAsync(), null, -1, -1);
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            await LoadCacheAsync();
            _isLoaded = true;
            _initTcs.TrySetResult(true);
        }

        public Task EnsureLoadedAsync() => _initTcs.Task;

        public ProbeData Get(string url)
        {
            if (!_isLoaded)
            {
                // Only log once to avoid spam (race condition is common during list rendering)
                if (!_warnedBeforeLoad) {
                    CacheLogger.Warning(CacheLogger.Category.Probe, "Get called before load (Suppressed subsequent)", url);
                    _warnedBeforeLoad = true;
                }
            }

            if (_cache.TryGetValue(url, out var data))
            {
                // Check TTL
                if ((DateTime.Now - data.LastUpdated).TotalDays > TTL_DAYS)
                {
                    _cache.TryRemove(url, out _);
                    _isDirty = true;
                    CacheLogger.Info(CacheLogger.Category.Probe, "Expired Entry Removed", url);
                    return null;
                }
                
                // Don't log every hit to avoid spam, or log as verbose if needed
                // CacheLogger.Success(CacheLogger.Category.Probe, "Cache HIT", url); 
                return data;
            }
            return null;
        }

        public void Update(string url, string res, string fps, string codec, long bitrate, bool isHdr)
        {
            var data = new ProbeData
            {
                Resolution = res,
                Fps = fps,
                Codec = codec,
                Bitrate = bitrate,
                IsHdr = isHdr,
                LastUpdated = DateTime.Now
            };

            _cache[url] = data;
            _isDirty = true;
            
            CacheLogger.Success(CacheLogger.Category.Probe, "Cache Updated", $"{res} | {codec} | HDR:{isHdr} | {url}");

            // Debounce save: Reset timer to fire in 5 seconds
            _saveTimer.Change(5000, -1);
        }

        private async Task LoadCacheAsync()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(CacheFileName);
                if (item == null) 
                {
                    CacheLogger.Info(CacheLogger.Category.Probe, "No cache file found, starting fresh.");
                    return;
                }

                using var stream = await folder.OpenStreamForReadAsync(CacheFileName);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, ProbeData>>(gzip, options);
                
                if (loaded != null)
                {
                    _cache = new ConcurrentDictionary<string, ProbeData>(loaded);
                    CacheLogger.Info(CacheLogger.Category.Probe, "Loaded", $"{_cache.Count} entries.");
                }
            }
            catch (Exception ex)
            {
                CacheLogger.Error(CacheLogger.Category.Probe, "Load Failed", ex.Message);
                
                // If corrupted (JsonException), delete it to start fresh
                if (ex is JsonException || ex is InvalidDataException)
                {
                    try
                    {
                        var folder = ApplicationData.Current.LocalFolder;
                        var item = await folder.TryGetItemAsync(CacheFileName);
                        if (item != null)
                        {
                            await item.DeleteAsync();
                            CacheLogger.Warning(CacheLogger.Category.Probe, "Corrupted cache file deleted.");
                        }
                    }
                    catch { /* Ignore delete error */ }
                }
                
                // Ensure TCS is set even on error so we don't block forever
            }
            finally
            {
                 // Safety net: If InitializeAsync crashes before this, we are in trouble, 
                 // but LoadCacheAsync is called by it. Ideally InitializeAsync ensures this invalidation.
            }
        }

        private async Task SaveIfDirtyAsync()
        {
            if (!_isDirty) return;
            if ((DateTime.Now - _lastSaveTime).TotalSeconds < 5) return; // Debounce check safety

            try
            {
                // Snapshot for thread safety
                var snapshot = new Dictionary<string, ProbeData>(_cache);
                
                var folder = ApplicationData.Current.LocalFolder;
                using var stream = await folder.OpenStreamForWriteAsync(CacheFileName, CreationCollisionOption.ReplaceExisting);
                using var gzip = new GZipStream(stream, CompressionLevel.Fastest);
                
                await JsonSerializer.SerializeAsync(gzip, snapshot);
                
                _isDirty = false;
                _lastSaveTime = DateTime.Now;
                CacheLogger.Info(CacheLogger.Category.Probe, "Saved to Disk", $"{snapshot.Count} entries.");
            }
            catch (Exception ex)
            {
                CacheLogger.Error(CacheLogger.Category.Probe, "Save Failed", ex.Message);
            }
        }
        
        // Manual Flush
        public async Task FlushAsync() => await SaveIfDirtyAsync();

        public void Remove(string url)
        {
            if (_cache.TryRemove(url, out _))
            {
                _isDirty = true;
                _saveTimer.Change(5000, -1);
            }
        }

        public void PruneOrphans(HashSet<string> validUrls)
        {
            var keys = _cache.Keys;
            int removed = 0;
            foreach (var key in keys)
            {
                if (!validUrls.Contains(key))
                {
                    if (_cache.TryRemove(key, out _))
                    {
                        removed++;
                        _isDirty = true;
                    }
                }
            }
            if (removed > 0)
            {
                CacheLogger.Info(CacheLogger.Category.Probe, "Pruned Orphans", $"{removed} entries removed.");
            }
        }
        
        public async Task ClearCacheAsync()
        {
            _cache.Clear();
            _isDirty = true;
            await SaveIfDirtyAsync();
            CacheCleared?.Invoke(this, EventArgs.Empty);
            CacheLogger.Info(CacheLogger.Category.Probe, "Cache Cleared");
        }
    }
}
