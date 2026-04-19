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

        private ConcurrentDictionary<int, ProbeData> _cache = new();
        private string _currentPlaylistId = "default";
        
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
        }

        public async Task InitializeForPlaylistAsync(string playlistId)
        {
            if (_currentPlaylistId == playlistId && _isLoaded) return;
            
            await SaveIfDirtyAsync(); // Save old one before switching
            _currentPlaylistId = playlistId;
            _cache.Clear();
            await LoadCacheAsync();
            _isLoaded = true;
            
            if (!_initTcs.Task.IsCompleted)
                _initTcs.TrySetResult(true);
        }

        public Task EnsureLoadedAsync() => _initTcs.Task;

        public ProbeData Get(int streamId)
        {
            if (!_isLoaded) return null;

            if (_cache.TryGetValue(streamId, out var data))
            {
                // Check TTL
                if ((DateTime.Now - data.LastUpdated).TotalDays > TTL_DAYS)
                {
                    _cache.TryRemove(streamId, out _);
                    _isDirty = true;
                    return null;
                }
                
                return data;
            }
            return null;
        }

        public void Update(int streamId, string res, string fps, string codec, long bitrate, bool isHdr)
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

            _cache[streamId] = data;
            _isDirty = true;
            
            CacheLogger.Success(CacheLogger.Category.Probe, "Cache Updated", $"{res} | ID: {streamId}");

            // Debounce save: Reset timer to fire in 5 seconds
            _saveTimer.Change(5000, -1);
        }

        private async Task LoadCacheAsync()
        {
            string fileName = $"cache_{_currentPlaylistId}_probe.bin.zst";
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return;

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var buffered = new BufferedStream(stream, 128 * 1024);
                using var decompressor = new ZstandardStream(buffered, CompressionMode.Decompress);
                using var reader = new BinaryReader(decompressor, System.Text.Encoding.UTF8);
                
                int magic = reader.ReadInt32();
                if (magic != 0x50524231) // Magic: PRB1
                {
                    CacheLogger.Warning(CacheLogger.Category.Probe, "Invalid magic, skipping cache load.");
                    return;
                }

                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int id = reader.ReadInt32();
                    var data = new ProbeData
                    {
                        Resolution = reader.ReadString(),
                        Fps = reader.ReadString(),
                        Codec = reader.ReadString(),
                        Bitrate = reader.ReadInt64(),
                        IsHdr = reader.ReadBoolean(),
                        LastUpdated = DateTime.FromBinary(reader.ReadInt64())
                    };
                    _cache[id] = data;
                }
                
                CacheLogger.Info(CacheLogger.Category.Probe, "Loaded Binary Cache", $"{_cache.Count} entries for {_currentPlaylistId}");
            }
            catch (Exception ex)
            {
                CacheLogger.Error(CacheLogger.Category.Probe, "Binary Load Failed", ex.Message);
            }
        }

        private async Task SaveIfDirtyAsync()
        {
            if (!_isDirty || _cache.IsEmpty) return;
            if ((DateTime.Now - _lastSaveTime).TotalSeconds < 5) return; 

            string fileName = $"cache_{_currentPlaylistId}_probe.bin.zst";
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                using var stream = await folder.OpenStreamForWriteAsync(fileName, CreationCollisionOption.ReplaceExisting);
                using var buffered = new BufferedStream(stream, 128 * 1024);
                using var compressor = new ZstandardStream(buffered, CompressionLevel.Optimal);
                using var writer = new BinaryWriter(compressor, System.Text.Encoding.UTF8);
                
                writer.Write(0x50524231); // Magic: PRB1
                writer.Write(_cache.Count);
                
                foreach (var kvp in _cache)
                {
                    writer.Write(kvp.Key);
                    writer.Write(kvp.Value.Resolution ?? "");
                    writer.Write(kvp.Value.Fps ?? "");
                    writer.Write(kvp.Value.Codec ?? "");
                    writer.Write(kvp.Value.Bitrate);
                    writer.Write(kvp.Value.IsHdr);
                    writer.Write(kvp.Value.LastUpdated.ToBinary());
                }
                
                _isDirty = false;
                _lastSaveTime = DateTime.Now;
                CacheLogger.Info(CacheLogger.Category.Probe, "Persistent Binary Save", $"{_cache.Count} entries.");
            }
            catch (Exception ex)
            {
                CacheLogger.Error(CacheLogger.Category.Probe, "Save Failed", ex.Message);
            }
        }
        
        // Manual Flush
        public async Task FlushAsync() => await SaveIfDirtyAsync();

        public void Remove(int streamId)
        {
            if (_cache.TryRemove(streamId, out _))
            {
                _isDirty = true;
                _saveTimer.Change(5000, -1);
            }
        }

        public void PruneOrphans(HashSet<int> validIds)
        {
            var keys = _cache.Keys;
            int removed = 0;
            foreach (var key in keys)
            {
                if (!validIds.Contains(key))
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
