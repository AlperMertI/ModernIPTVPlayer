using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models.Metadata;
using ZstdSharp;

namespace ModernIPTVPlayer.Services.Metadata
{
    public record struct EnrichedField<T>(T Value, byte SourceIndex, int Priority, long Timestamp);

    /// <summary>
    /// High-performance, Native AOT compatible binary cache for enriched metadata.
    /// Uses Zstandard compression and an append-only log structure with periodic vacuuming.
    /// </summary>
    public sealed class BinaryEnrichmentCache
    {
        private static readonly System.Threading.Lock _instanceLock = new();
        private static BinaryEnrichmentCache? _instance;
        public static BinaryEnrichmentCache Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new BinaryEnrichmentCache();
                    }
                }
                return _instance;
            }
        }

        private const string CACHE_FILE_NAME = "enriched_metadata.bin";
        private const int VERSION = 5; // Incremented to support persistent serialization of Seasons, Cast, and technical media info.
        private readonly string _cachePath;
        
        // String interning for sources (RAM optimization)
        private readonly List<string> _sourceRegistry = new();
        private readonly Dictionary<string, byte> _sourceMap = new(StringComparer.OrdinalIgnoreCase);

        // Memory index: IMDbID -> (File Offset, Timestamp)
        private readonly ConcurrentDictionary<string, (long Offset, long Timestamp)> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly Channel<CacheEntry> _writeChannel = Channel.CreateUnbounded<CacheEntry>(new UnboundedChannelOptions { SingleReader = true });
        private readonly System.Threading.Lock _fileLock = new();
        private readonly Task _initializationTask;

        private BinaryEnrichmentCache()
        {
            _cachePath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, CACHE_FILE_NAME);
            
            // Seed registry with common sources
            GetSourceIndex("Unknown");
            GetSourceIndex("TMDB");
            GetSourceIndex("Cinemeta");
            GetSourceIndex("Fanart");
            
            _initializationTask = InitializeAsync().ContinueWith(_ => Task.Run(ProcessWritesAsync));
        }

        private byte GetSourceIndex(string? source)
        {
            if (string.IsNullOrEmpty(source)) return 0;
            lock (_sourceRegistry)
            {
                if (_sourceMap.TryGetValue(source, out byte index)) return index;
                if (_sourceRegistry.Count >= 255) return 0; // Guard against overflow

                byte newIndex = (byte)_sourceRegistry.Count;
                _sourceRegistry.Add(source);
                _sourceMap[source] = newIndex;
                return newIndex;
            }
        }

        private string GetSourceName(byte index)
        {
            lock (_sourceRegistry)
            {
                return index < _sourceRegistry.Count ? _sourceRegistry[index] : "Unknown";
            }
        }

        private async Task InitializeAsync()
        {
            if (!File.Exists(_cachePath)) return;

            await Task.Run(() =>
            {
                try
                {
                    lock (_fileLock)
                    {
                        using var fs = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new BinaryReader(fs);

                        if (fs.Length < 4) return;
                        int version = reader.ReadInt32();
                        if (version != VERSION)
                        {
                            fs.Close();
                            File.Delete(_cachePath);
                            return;
                        }

                        int liveCount = 0;
                        int totalCount = 0;

                        while (fs.Position < fs.Length)
                        {
                            long currentOffset = fs.Position;
                            string id = reader.ReadString();
                            long timestamp = reader.ReadInt64();
                            int dataLength = reader.ReadInt32();
                            fs.Seek(dataLength, SeekOrigin.Current); // Skip compressed data for indexing

                            _index[id] = (currentOffset, timestamp);
                            totalCount++;
                        }

                        // Vacuuming logic: If fragmentation > 30% and enough records exist
                        if (totalCount > 100 && (totalCount - _index.Count) > (totalCount * 0.3))
                        {
                            _ = Task.Run(VacuumAsync);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BinaryCache] Init Error: {ex.Message}");
                }
            });
        }

        public async Task SaveAsync(string id, UnifiedMetadata data)
        {
            await _initializationTask;
            System.Diagnostics.Debug.WriteLine($"[BinaryCache] Queueing save for {id}");
            var entry = new CacheEntry(id, data, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await _writeChannel.Writer.WriteAsync(entry);
        }

        public async Task<bool> TryPatchAsync(string id, UnifiedMetadata target)
        {
            await _initializationTask;
            if (!_index.TryGetValue(id, out var info)) 
            {
                // System.Diagnostics.Debug.WriteLine($"[BinaryCache] MISS for {id}");
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"[BinaryCache] HIT for {id} at offset {info.Offset}");

            return await Task.Run(() =>
            {
                try
                {
                    lock (_fileLock)
                    {
                        using var fs = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fs.Seek(info.Offset, SeekOrigin.Begin);
                        using var reader = new BinaryReader(fs);

                        _ = reader.ReadString(); // Skip ID
                        _ = reader.ReadInt64();  // Skip Timestamp
                        int dataLength = reader.ReadInt32();
                        byte[] compressed = reader.ReadBytes(dataLength);

                        using var decompressor = new Decompressor();
                        byte[] decompressed = decompressor.Unwrap(compressed).ToArray();

                        using var ms = new MemoryStream(decompressed);
                        using var br = new BinaryReader(ms);

                        // Native AOT safe field-by-field hydration with provenance
                        target.LogoUrl = ReadStringSafe(br);
                        target.TrailerUrl = ReadStringSafe(br);
                        target.Rating = br.ReadDouble();
                        target.BackdropUrl = ReadStringSafe(br);
                        target.Overview = ReadStringSafe(br);
                        
                        // Metadata Source & Priority (Provenance)
                        target.MetadataSourceInfo = GetSourceName(br.ReadByte());
                        target.PriorityScore = br.ReadInt32();
                        target.MaxEnrichmentContext = (MetadataContext)br.ReadInt32();

                        // Additional fields for full hydration
                        target.Genres = ReadStringSafe(br);
                        target.Year = ReadStringSafe(br);
                        target.Certification = ReadStringSafe(br);
                        target.Writers = ReadStringSafe(br);
                        
                        // Gallery (BackdropUrls)
                        int galleryCount = br.ReadInt32();
                        for (int i = 0; i < galleryCount; i++)
                        {
                            target.BackdropUrls.Add(br.ReadString());
                        }

                        // Memory: CheckedFields and ProbedAddons
                        target.CheckedFields = (ModernIPTVPlayer.Models.Metadata.MetadataField)br.ReadInt64();
                        
                        int probedCount = br.ReadInt32();
                        for (int i = 0; i < probedCount; i++)
                        {
                            target.ProbedAddons.Add(br.ReadString());
                        }

                        // Hydrate IsSeries flag and complex list collections (Cast, Directors, Seasons)
                        target.IsSeries = br.ReadBoolean();
                        
                        string castJson = br.ReadString();
                        if (!string.IsNullOrEmpty(castJson))
                        {
                            target.Cast = System.Text.Json.JsonSerializer.Deserialize(castJson, Services.Json.AppJsonContext.Default.ListUnifiedCast);
                        }

                        string dirJson = br.ReadString();
                        if (!string.IsNullOrEmpty(dirJson))
                        {
                            target.Directors = System.Text.Json.JsonSerializer.Deserialize(dirJson, Services.Json.AppJsonContext.Default.ListUnifiedCast);
                        }

                        string seaJson = br.ReadString();
                        if (!string.IsNullOrEmpty(seaJson))
                        {
                            target.Seasons = System.Text.Json.JsonSerializer.Deserialize(seaJson, Services.Json.AppJsonContext.Default.ListUnifiedSeason);
                        }

                        // Hydrate technical media metadata
                        target.Bitrate = br.ReadInt64();
                        target.IsHdr = br.ReadBoolean();
                        target.Resolution = ReadStringSafe(br);
                        target.VideoCodec = ReadStringSafe(br);
                        target.AudioCodec = ReadStringSafe(br);
                        target.Status = ReadStringSafe(br);
                        target.Country = ReadStringSafe(br);
                        target.Runtime = ReadStringSafe(br);
                        
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BinaryCache] Patch Error for {id}: {ex.Message}");
                    return false;
                }
            });
        }

        private async Task ProcessWritesAsync()
        {
            using var compressor = new Compressor(3); 
            FileStream? fs = null;
            BinaryWriter? writer = null;

            try
            {
                await foreach (var entry in _writeChannel.Reader.ReadAllAsync())
                {
                    try
                    {
                        byte[] data;
                        using (var ms = new MemoryStream())
                        {
                            using (var bw = new BinaryWriter(ms))
                            {
                                bw.Write(entry.Metadata.LogoUrl ?? "");
                                bw.Write(entry.Metadata.TrailerUrl ?? "");
                                bw.Write(entry.Metadata.Rating);
                                bw.Write(entry.Metadata.BackdropUrl ?? "");
                                bw.Write(entry.Metadata.Overview ?? "");

                                // Persist Provenance
                                // [FIX] Fallback to DataSource if MetadataSourceInfo is empty (e.g. for Seeds)
                                bw.Write(GetSourceIndex(entry.Metadata.MetadataSourceInfo ?? entry.Metadata.DataSource));
                                bw.Write(entry.Metadata.PriorityScore);
                                bw.Write((int)entry.Metadata.MaxEnrichmentContext);

                                // Additional fields
                                bw.Write(entry.Metadata.Genres ?? "");
                                bw.Write(entry.Metadata.Year ?? "");
                                bw.Write(entry.Metadata.Certification ?? "");
                                bw.Write(entry.Metadata.Writers ?? "");

                                // Gallery
                                var backdrops = entry.Metadata.BackdropUrls?.ToList() ?? new List<string>();
                                bw.Write(backdrops.Count);
                                foreach (var b in backdrops) bw.Write(b);

                                // Memory: CheckedFields and ProbedAddons
                                bw.Write((long)entry.Metadata.CheckedFields);
                                
                                var probed = entry.Metadata.ProbedAddons?.ToList() ?? new List<string>();
                                bw.Write(probed.Count);
                                foreach (var p in probed) bw.Write(p);

                                // Persist complex metadata collections and technical properties
                                bw.Write(entry.Metadata.IsSeries);
                                
                                string castJson = (entry.Metadata.Cast != null && entry.Metadata.Cast.Count > 0) 
                                    ? System.Text.Json.JsonSerializer.Serialize(entry.Metadata.Cast, Services.Json.AppJsonContext.Default.ListUnifiedCast) 
                                    : "";
                                bw.Write(castJson);

                                string dirJson = (entry.Metadata.Directors != null && entry.Metadata.Directors.Count > 0) 
                                    ? System.Text.Json.JsonSerializer.Serialize(entry.Metadata.Directors, Services.Json.AppJsonContext.Default.ListUnifiedCast) 
                                    : "";
                                bw.Write(dirJson);

                                string seaJson = (entry.Metadata.Seasons != null && entry.Metadata.Seasons.Count > 0) 
                                    ? System.Text.Json.JsonSerializer.Serialize(entry.Metadata.Seasons, Services.Json.AppJsonContext.Default.ListUnifiedSeason) 
                                    : "";
                                bw.Write(seaJson);

                                bw.Write(entry.Metadata.Bitrate);
                                bw.Write(entry.Metadata.IsHdr);
                                bw.Write(entry.Metadata.Resolution ?? "");
                                bw.Write(entry.Metadata.VideoCodec ?? "");
                                bw.Write(entry.Metadata.AudioCodec ?? "");
                                bw.Write(entry.Metadata.Status ?? "");
                                bw.Write(entry.Metadata.Country ?? "");
                                bw.Write(entry.Metadata.Runtime ?? "");
                            }
                            data = ms.ToArray();
                        }

                        byte[] compressed = compressor.Wrap(data).ToArray();

                        lock (_fileLock)
                        {
                            // Open stream only once or re-open if closed/disposed
                            if (fs == null || !fs.CanWrite)
                            {
                                writer?.Dispose();
                                fs?.Dispose();
                                fs = new FileStream(_cachePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                                writer = new BinaryWriter(fs);
                                
                                if (fs.Length == 4) // Version already written
                                {
                                }
                                else if (fs.Length == 0)
                                {
                                    writer.Write(VERSION);
                                }
                            }

                            long offset = fs.Position;
                            writer.Write(entry.Id);
                            writer.Write(entry.Timestamp);
                            writer.Write(compressed.Length);
                            writer.Write(compressed);
                            writer.Flush(); // Ensure data is on disk but keep stream open

                            _index[entry.Id] = (offset, entry.Timestamp);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BinaryCache] Item Write Error: {ex.Message}");
                        // Close stream on error to force re-open next time
                        writer?.Dispose(); writer = null;
                        fs?.Dispose(); fs = null;
                    }
                }
            }
            finally
            {
                writer?.Dispose();
                fs?.Dispose();
            }
        }

        private async Task VacuumAsync()
        {
            string tempPath = _cachePath + ".tmp";
            try
            {
                await Task.Run(() =>
                {
                    lock (_fileLock)
                    {
                        using var sourceFs = new FileStream(_cachePath, FileMode.Open, FileAccess.Read);
                        using var targetFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
                        using var reader = new BinaryReader(sourceFs);
                        using var writer = new BinaryWriter(targetFs);

                        writer.Write(VERSION);

                        // Only copy the latest records found in our index
                        var activeOffsets = _index.Values.Select(v => v.Offset).ToHashSet();
                        
                        sourceFs.Seek(4, SeekOrigin.Begin); // Skip original header
                        while (sourceFs.Position < sourceFs.Length)
                        {
                            long currentPos = sourceFs.Position;
                            string id = reader.ReadString();
                            long ts = reader.ReadInt64();
                            int len = reader.ReadInt32();
                            byte[] data = reader.ReadBytes(len);

                            if (activeOffsets.Contains(currentPos))
                            {
                                long newOffset = targetFs.Position;
                                writer.Write(id);
                                writer.Write(ts);
                                writer.Write(len);
                                writer.Write(data);
                                
                                // Update index to point to new file locations
                                if (_index.TryGetValue(id, out var oldInfo) && oldInfo.Offset == currentPos)
                                {
                                    _index[id] = (newOffset, ts);
                                }
                            }
                        }
                    }

                    File.Move(tempPath, _cachePath, overwrite: true);
                    System.Diagnostics.Debug.WriteLine("[BinaryCache] Vacuum complete.");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BinaryCache] Vacuum Error: {ex.Message}");
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private string? ReadStringSafe(BinaryReader br)
        {
            string s = br.ReadString();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private record CacheEntry(string Id, UnifiedMetadata Metadata, long Timestamp);
    }
}
