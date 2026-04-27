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
    /// <summary>
    /// Represents comprehensive technical metadata for a media stream.
    /// Used for both main UI badges and advanced technical analysis.
    /// </summary>
    public class ProbeData
    {
        // Basic Info (Main Badges)
        public string Resolution { get; set; }
        public string Fps { get; set; }
        public string Codec { get; set; }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }

        // Advanced Video Info
        public string AspectRatio { get; set; }
        public string PixelFormat { get; set; }
        public string ColorSpace { get; set; }
        public string ColorRange { get; set; }
        public string ChromaSubsampling { get; set; }
        public string ScanType { get; set; } // "p" (Progressive) or "i" (Interlaced)
        public string Encoder { get; set; }
        
        // Audio Info
        public string AudioCodec { get; set; }
        public string AudioChannels { get; set; }
        public string AudioSampleRate { get; set; }
        public string AudioLanguages { get; set; }

        // Metadata & Network
        public string Container { get; set; }
        public string Protocol { get; set; }
        public string Server { get; set; }
        public string MimeType { get; set; }
        public int Latency { get; set; }

        // Performance & Buffer
        public long BufferSize { get; set; }
        public double BufferDuration { get; set; }
        public double AvSync { get; set; }

        // Security & Tracks
        public bool IsEncrypted { get; set; }
        public string DrmType { get; set; }
        public string SubtitleTracks { get; set; }

        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// High-performance binary cache service for stream analysis data.
    /// Uses Zstandard compression and binary serialization for minimal I/O overhead.
    /// </summary>
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
        public int CacheVersion { get; private set; } = 0;

        // Magic Constants for binary versioning
        private const int MAGIC_PRB1 = 0x50524231; // Legacy
        private const int MAGIC_PRB3 = 0x50524233; // Latest Schema

        // PROJECT ZERO: UI Filter Bitmask Constants (Synchronized with LiveTVPage)
        public const ushort CF_RES_4K = 1 << 0;
        public const ushort CF_RES_1080 = 1 << 1;
        public const ushort CF_RES_720 = 1 << 2;
        public const ushort CF_RES_SD = 1 << 3;
        public const ushort CF_ONLINE = 1 << 4;
        public const ushort CF_UNSTABLE = 1 << 5;
        public const ushort CF_HEVC = 1 << 6;
        public const ushort CF_AVC = 1 << 7;
        public const ushort CF_HDR = 1 << 8;
        public const ushort CF_HAS_BITRATE = 1 << 9;
        public const ushort CF_HIGH_FPS = 1 << 10;

        /// <summary>
        /// Computes UI filter flags directly from cached probe data.
        /// Optimized for large collections (50k+ items).
        /// </summary>
        public ushort GetFlags(int streamId)
        {
            if (!_cache.TryGetValue(streamId, out var data)) return 0;

            ushort flags = CF_ONLINE; 
            
            if (data.Resolution != null)
            {
                var res = data.Resolution;
                if (res.Contains("4K") || res.Contains("2160") || res.Contains("3840")) flags |= CF_RES_4K;
                else if (res.Contains("1080") || res.Contains("FHD")) flags |= CF_RES_1080;
                else if (res.Contains("720")) flags |= CF_RES_720;
                else if (res.Contains("576") || res.Contains("480") || res.Contains("SD")) flags |= CF_RES_SD;
            }

            if (data.Codec != null)
            {
                var codec = data.Codec;
                if (codec.Contains("HEVC") || codec.Contains("H265")) flags |= CF_HEVC;
                else if (codec.Contains("AVC") || codec.Contains("H264")) flags |= CF_AVC;
            }

            if (data.IsHdr) flags |= CF_HDR;
            if (data.Bitrate > 0) flags |= CF_HAS_BITRATE;

            if (data.Fps != null && (data.Fps.Contains("50") || data.Fps.Contains("60")))
            {
                flags |= CF_HIGH_FPS;
            }

            return flags;
        }

        private TaskCompletionSource<bool> _initTcs = new TaskCompletionSource<bool>();
        private bool _isLoaded = false;

        private ProbeCacheService()
        {
            _saveTimer = new System.Threading.Timer(async _ => await SaveIfDirtyAsync(), null, -1, -1);
        }

        public async Task InitializeForPlaylistAsync(string playlistId)
        {
            if (_currentPlaylistId == playlistId && _isLoaded) return;
            
            await SaveIfDirtyAsync(); 
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

        public void Update(int streamId, ProbeData data)
        {
            if (data == null) return;
            
            // PROJECT ZERO: Intern technical strings during update
            data.Resolution = Helpers.StringInterner.Intern(data.Resolution);
            data.Fps = Helpers.StringInterner.Intern(data.Fps);
            data.Codec = Helpers.StringInterner.Intern(data.Codec);
            data.ScanType = Helpers.StringInterner.Intern(data.ScanType);
            data.AudioCodec = Helpers.StringInterner.Intern(data.AudioCodec);

            data.LastUpdated = DateTime.Now;

            _cache[streamId] = data;
            _isDirty = true;
            
            CacheLogger.Success(CacheLogger.Category.Probe, "Cache Updated", $"{data.Resolution} | ID: {streamId}");
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
                if (magic != MAGIC_PRB3)
                {
                    CacheLogger.Warning(CacheLogger.Category.Probe, "Unknown cache version or old format. Ignoring.");
                    return;
                }

                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int id = reader.ReadInt32();
                    var data = new ProbeData
                    {
                        // PROJECT ZERO: Intern technical strings during binary load
                        Resolution = Helpers.StringInterner.Intern(reader.ReadString()),
                        Fps = Helpers.StringInterner.Intern(reader.ReadString()),
                        Codec = Helpers.StringInterner.Intern(reader.ReadString()),
                        Bitrate = reader.ReadInt64(),
                        IsHdr = reader.ReadBoolean(),
                        AspectRatio = Helpers.StringInterner.Intern(reader.ReadString()),
                        PixelFormat = Helpers.StringInterner.Intern(reader.ReadString()),
                        ColorSpace = Helpers.StringInterner.Intern(reader.ReadString()),
                        ColorRange = Helpers.StringInterner.Intern(reader.ReadString()),
                        ChromaSubsampling = Helpers.StringInterner.Intern(reader.ReadString()),
                        ScanType = Helpers.StringInterner.Intern(reader.ReadString()),
                        Encoder = Helpers.StringInterner.Intern(reader.ReadString()),
                        AudioCodec = Helpers.StringInterner.Intern(reader.ReadString()),
                        AudioChannels = Helpers.StringInterner.Intern(reader.ReadString()),
                        AudioSampleRate = Helpers.StringInterner.Intern(reader.ReadString()),
                        AudioLanguages = Helpers.StringInterner.Intern(reader.ReadString()),
                        Container = Helpers.StringInterner.Intern(reader.ReadString()),
                        Protocol = Helpers.StringInterner.Intern(reader.ReadString()),
                        Server = Helpers.StringInterner.Intern(reader.ReadString()),
                        MimeType = Helpers.StringInterner.Intern(reader.ReadString()),
                        Latency = reader.ReadInt32(),
                        BufferSize = reader.ReadInt64(),
                        BufferDuration = reader.ReadDouble(),
                        AvSync = reader.ReadDouble(),
                        IsEncrypted = reader.ReadBoolean(),
                        DrmType = Helpers.StringInterner.Intern(reader.ReadString()),
                        SubtitleTracks = Helpers.StringInterner.Intern(reader.ReadString()),
                        LastUpdated = DateTime.FromBinary(reader.ReadInt64())
                    };
                    _cache[id] = data;
                }
                
                CacheLogger.Info(CacheLogger.Category.Probe, "Loaded Technical Cache (PRB2)", $"{_cache.Count} entries.");
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
                
                writer.Write(MAGIC_PRB3);
                writer.Write(_cache.Count);
                
                foreach (var kvp in _cache)
                {
                    var v = kvp.Value;
                    writer.Write(kvp.Key);
                    writer.Write(v.Resolution ?? "");
                    writer.Write(v.Fps ?? "");
                    writer.Write(v.Codec ?? "");
                    writer.Write(v.Bitrate);
                    writer.Write(v.IsHdr);
                    writer.Write(v.AspectRatio ?? "");
                    writer.Write(v.PixelFormat ?? "");
                    writer.Write(v.ColorSpace ?? "");
                    writer.Write(v.ColorRange ?? "");
                    writer.Write(v.ChromaSubsampling ?? "");
                    writer.Write(v.ScanType ?? "");
                    writer.Write(v.Encoder ?? "");
                    writer.Write(v.AudioCodec ?? "");
                    writer.Write(v.AudioChannels ?? "");
                    writer.Write(v.AudioSampleRate ?? "");
                    writer.Write(v.AudioLanguages ?? "");
                    writer.Write(v.Container ?? "");
                    writer.Write(v.Protocol ?? "");
                    writer.Write(v.Server ?? "");
                    writer.Write(v.MimeType ?? "");
                    writer.Write(v.Latency);
                    writer.Write(v.BufferSize);
                    writer.Write(v.BufferDuration);
                    writer.Write(v.AvSync);
                    writer.Write(v.IsEncrypted);
                    writer.Write(v.DrmType ?? "");
                    writer.Write(v.SubtitleTracks ?? "");
                    writer.Write(v.LastUpdated.ToBinary());
                }
                
                _isDirty = false;
                _lastSaveTime = DateTime.Now;
                CacheLogger.Info(CacheLogger.Category.Probe, "Persistent Binary Save (PRB2)", $"{_cache.Count} entries.");
            }
            catch (Exception ex)
            {
                CacheLogger.Error(CacheLogger.Category.Probe, "Save Failed", ex.Message);
            }
        }
        
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
            if (removed > 0) CacheLogger.Info(CacheLogger.Category.Probe, "Pruned Orphans", $"{removed} entries.");
        }
        
        public async Task ClearCacheAsync()
        {
            _cache.Clear();
            _isDirty = true;
            CacheVersion++;
            await SaveIfDirtyAsync();
            CacheCleared?.Invoke(this, EventArgs.Empty);
            CacheLogger.Info(CacheLogger.Category.Probe, "Cache Cleared");
        }
    }
}
