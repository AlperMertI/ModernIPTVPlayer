using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models.Metadata;

namespace ModernIPTVPlayer.Services.Metadata
{
    public record struct EnrichedField<T>(T Value, byte SourceIndex, int Priority, long Timestamp);

    /// <summary>
    /// High-performance, Native AOT compatible binary cache for enriched metadata.
    /// Uses Zstandard compression and an append-only log structure with periodic vacuuming.
    /// </summary>
    public sealed class BinaryEnrichmentCache
    {
        private static readonly Lazy<BinaryEnrichmentCache> _instance = new(() => new BinaryEnrichmentCache());
        public static BinaryEnrichmentCache Instance => _instance.Value;

        private const string CACHE_FILE_NAME = "enriched_metadata.bin";
        private const int VERSION = 6; // Bumped to support pure zero-allocation raw binary serialization of Seasons, Episodes, Cast, and technical media.
        private readonly string _cachePath;
        
        // String interning for sources (RAM optimization)
        private readonly System.Threading.Lock _sourceRegistryLock = new();
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
            lock (_sourceRegistryLock)
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
            lock (_sourceRegistryLock)
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

                        byte[] compressedBuffer = ArrayPool<byte>.Shared.Rent(dataLength);
                        try
                        {
                            fs.ReadExactly(compressedBuffer, 0, dataLength);

                            if (ZstandardDecoder.TryGetMaxDecompressedLength(compressedBuffer.AsSpan(0, dataLength), out long maxDecompressedLength))
                            {
                                byte[] decompressedBuffer = ArrayPool<byte>.Shared.Rent((int)maxDecompressedLength);
                                try
                                {
                                    if (ZstandardDecoder.TryDecompress(compressedBuffer.AsSpan(0, dataLength), decompressedBuffer, out int decompressedBytes))
                                    {
                                        using var ms = new MemoryStream(decompressedBuffer, 0, decompressedBytes, writable: false);
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
                                        
                                        target.Cast = ReadUnifiedCastList(br);
                                        target.Directors = ReadUnifiedCastList(br);
                                        target.Seasons = ReadUnifiedSeasonList(br);

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
                                    else
                                    {
                                        throw new InvalidOperationException("Failed to decompress metadata block natively.");
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(decompressedBuffer);
                                }
                            }
                            else
                            {
                                throw new InvalidOperationException("Failed to calculate max decompressed size.");
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(compressedBuffer);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BinaryCache] Patch Error for {id}: {ex.Message}");
                    _index.TryRemove(id, out _);
                    return false;
                }
            });
        }

        private async Task ProcessWritesAsync()
        {
            FileStream? fs = null;
            BinaryWriter? writer = null;

            try
            {
                await foreach (var entry in _writeChannel.Reader.ReadAllAsync())
                {
                    try
                    {
                        byte[] rawBuffer = ArrayPool<byte>.Shared.Rent(256 * 1024); // 256 KB max per entry metadata
                        try
                        {
                            int rawLength;
                            using (var ms = new MemoryStream(rawBuffer, 0, rawBuffer.Length, writable: true))
                            {
                                using (var bw = new BinaryWriter(ms))
                                {
                                    bw.Write(entry.Metadata.LogoUrl ?? "");
                                    bw.Write(entry.Metadata.TrailerUrl ?? "");
                                    bw.Write(entry.Metadata.Rating);
                                    bw.Write(entry.Metadata.BackdropUrl ?? "");
                                    bw.Write(entry.Metadata.Overview ?? "");

                                    // Persist Provenance
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
                                    
                                    WriteUnifiedCastList(bw, entry.Metadata.Cast);
                                    WriteUnifiedCastList(bw, entry.Metadata.Directors);
                                    WriteUnifiedSeasonList(bw, entry.Metadata.Seasons);

                                    bw.Write(entry.Metadata.Bitrate);
                                    bw.Write(entry.Metadata.IsHdr);
                                    bw.Write(entry.Metadata.Resolution ?? "");
                                    bw.Write(entry.Metadata.VideoCodec ?? "");
                                    bw.Write(entry.Metadata.AudioCodec ?? "");
                                    bw.Write(entry.Metadata.Status ?? "");
                                    bw.Write(entry.Metadata.Country ?? "");
                                    bw.Write(entry.Metadata.Runtime ?? "");

                                    rawLength = (int)ms.Position;
                                }
                            }

                            int maxCompressedLength = (int)ZstandardEncoder.GetMaxCompressedLength(rawLength);
                            byte[] compressedBuffer = ArrayPool<byte>.Shared.Rent(maxCompressedLength);
                            try
                            {
                                if (ZstandardEncoder.TryCompress(rawBuffer.AsSpan(0, rawLength), compressedBuffer, out int bytesWritten))
                                {
                                    lock (_fileLock)
                                    {
                                        if (fs == null || !fs.CanWrite)
                                        {
                                            writer?.Dispose();
                                            fs?.Dispose();
                                            fs = new FileStream(_cachePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                                            writer = new BinaryWriter(fs);
                                            if (fs.Length == 0)
                                            {
                                                writer.Write(VERSION);
                                            }
                                        }

                                        long offset = fs.Position;
                                        writer.Write(entry.Id);
                                        writer.Write(entry.Timestamp);
                                        writer.Write(bytesWritten);
                                        writer.Write(compressedBuffer, 0, bytesWritten);
                                        writer.Flush();

                                        _index[entry.Id] = (offset, entry.Timestamp);
                                    }
                                }
                                else
                                {
                                    throw new InvalidOperationException("Failed to compress metadata block natively.");
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(compressedBuffer);
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(rawBuffer);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BinaryCache] Item Write Error: {ex.Message}");
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
                            
                            byte[] dataBuffer = ArrayPool<byte>.Shared.Rent(len);
                            try
                            {
                                sourceFs.ReadExactly(dataBuffer, 0, len);

                                if (activeOffsets.Contains(currentPos))
                                {
                                    long newOffset = targetFs.Position;
                                    writer.Write(id);
                                    writer.Write(ts);
                                    writer.Write(len);
                                    writer.Write(dataBuffer, 0, len);
                                    
                                    // Update index to point to new file locations
                                    if (_index.TryGetValue(id, out var oldInfo) && oldInfo.Offset == currentPos)
                                    {
                                        _index[id] = (newOffset, ts);
                                    }
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(dataBuffer);
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

        private void WriteUnifiedCastList(BinaryWriter bw, List<UnifiedCast>? list)
        {
            int count = list?.Count ?? 0;
            bw.Write(count);
            if (list != null)
            {
                foreach (var c in list)
                {
                    bw.Write(c.Name ?? "");
                    bw.Write(c.Character ?? "");
                    bw.Write(c.ProfileUrl ?? "");
                    bw.Write(c.TmdbId ?? 0);
                }
            }
        }

        private List<UnifiedCast> ReadUnifiedCastList(BinaryReader br)
        {
            int count = br.ReadInt32();
            var list = new List<UnifiedCast>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new UnifiedCast
                {
                    Name = ReadStringSafe(br),
                    Character = ReadStringSafe(br),
                    ProfileUrl = ReadStringSafe(br),
                    TmdbId = br.ReadInt32() is int id && id > 0 ? id : null
                });
            }
            return list;
        }

        private void WriteUnifiedSeasonList(BinaryWriter bw, List<UnifiedSeason>? list)
        {
            int count = list?.Count ?? 0;
            bw.Write(count);
            if (list != null)
            {
                foreach (var s in list)
                {
                    bw.Write(s.SeasonNumber);
                    bw.Write(s.Name ?? "");
                    bw.Write(s.PosterUrl ?? "");
                    bw.Write(s.IsEnrichedByTmdb);

                    int epCount = s.Episodes?.Count ?? 0;
                    bw.Write(epCount);
                    if (s.Episodes != null)
                    {
                        foreach (var ep in s.Episodes)
                        {
                            bw.Write(ep.Id ?? "");
                            bw.Write(ep.SeasonNumber);
                            bw.Write(ep.EpisodeNumber);
                            bw.Write(ep.Title ?? "");
                            bw.Write(ep.Overview ?? "");
                            bw.Write(ep.ThumbnailUrl ?? "");

                            bw.Write(ep.AirDate.HasValue);
                            if (ep.AirDate.HasValue) bw.Write(ep.AirDate.Value.Ticks);

                            bw.Write(ep.Releasedate.HasValue);
                            if (ep.Releasedate.HasValue) bw.Write(ep.Releasedate.Value.Ticks);

                            bw.Write(ep.IsAvailable);
                            bw.Write(ep.Runtime ?? "");
                            bw.Write(ep.StreamUrl ?? "");
                            bw.Write(ep.RuntimeFormatted ?? "");
                            bw.Write(ep.Resolution ?? "");
                            bw.Write(ep.VideoCodec ?? "");
                            bw.Write(ep.AudioCodec ?? "");
                            bw.Write(ep.Bitrate);
                            bw.Write(ep.IsHdr);
                            bw.Write(ep.IptvSourceTitle ?? "");
                            bw.Write(ep.IptvSeriesId);
                        }
                    }
                }
            }
        }

        private List<UnifiedSeason> ReadUnifiedSeasonList(BinaryReader br)
        {
            int count = br.ReadInt32();
            var list = new List<UnifiedSeason>(count);
            for (int i = 0; i < count; i++)
            {
                var s = new UnifiedSeason
                {
                    SeasonNumber = br.ReadInt32(),
                    Name = ReadStringSafe(br),
                    PosterUrl = ReadStringSafe(br),
                    IsEnrichedByTmdb = br.ReadBoolean()
                };

                int epCount = br.ReadInt32();
                s.Episodes = new List<UnifiedEpisode>(epCount);
                for (int j = 0; j < epCount; j++)
                {
                    var ep = new UnifiedEpisode
                    {
                        Id = br.ReadString(),
                        SeasonNumber = br.ReadInt32(),
                        EpisodeNumber = br.ReadInt32(),
                        Title = br.ReadString(),
                        Overview = br.ReadString(),
                        ThumbnailUrl = br.ReadString()
                    };

                    if (br.ReadBoolean()) ep.AirDate = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
                    if (br.ReadBoolean()) ep.Releasedate = new DateTime(br.ReadInt64(), DateTimeKind.Utc);

                    ep.IsAvailable = br.ReadBoolean();
                    ep.Runtime = ReadStringSafe(br);
                    ep.StreamUrl = ReadStringSafe(br);
                    ep.RuntimeFormatted = ReadStringSafe(br);
                    ep.Resolution = ReadStringSafe(br);
                    ep.VideoCodec = ReadStringSafe(br);
                    ep.AudioCodec = ReadStringSafe(br);
                    ep.Bitrate = br.ReadInt64();
                    ep.IsHdr = br.ReadBoolean();
                    ep.IptvSourceTitle = ReadStringSafe(br);
                    ep.IptvSeriesId = br.ReadInt32();

                    s.Episodes.Add(ep);
                }
                list.Add(s);
            }
            return list;
        }

        private string? ReadStringSafe(BinaryReader br)
        {
            string s = br.ReadString();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private record CacheEntry(string Id, UnifiedMetadata Metadata, long Timestamp);
    }
}
