using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;

namespace ModernIPTVPlayer.Services.Iptv
{
    /// <summary>
    /// Senior-level high-performance indexer for VOD and Series stream matching.
    /// Utilizes immutable snapshots, FrozenDictionary, and StringPool for zero-allocation performance.
    /// Fully NativeAOT compatible.
    /// </summary>
    /// <summary>
    /// Senior-level high-performance indexer for VOD and Series stream matching.
    /// Utilizes immutable snapshots, FrozenDictionary, and lock-free Volatile swaps for zero-allocation performance.
    /// Fully NativeAOT compatible and thread-safe.
    /// </summary>
    public sealed class StreamMatchIndexer
    {
        private BinarySearchSession? _session;
        private IndexSnapshot _snapshot = new(
            FrozenDictionary<string, int[]>.Empty, 
            FrozenDictionary<string, int[]>.Empty, 
            string.Empty, 
            DateTime.MinValue);

        private sealed record IndexSnapshot(
            FrozenDictionary<string, int[]> TokenMap,
            FrozenDictionary<string, int[]> IdMap,
            string Fingerprint,
            DateTime CreatedAt);

        public string Fingerprint => _session?.Fingerprint ?? Volatile.Read(ref _snapshot).Fingerprint;

        public async Task RebuildAsync<T>(IReadOnlyList<T> streams, string newFingerprint, bool clear = false) where T : IMediaStream
        {
            if (string.IsNullOrEmpty(newFingerprint)) return;

            if (!clear && Fingerprint == newFingerprint)
            {
                AppLogger.Info($"[StreamMatchIndexer] Index up to date (FP: {newFingerprint}).");
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var current = Volatile.Read(ref _snapshot);
                var result = await Task.Run(() => BuildIndexInternal(streams, newFingerprint, clear ? null : current)).ConfigureAwait(false);
                
                // Clear active session to force reload from disk in next TryLoad
                _session?.Dispose();
                _session = null;

                Volatile.Write(ref _snapshot, result);
                AppLogger.Info($"[PERF] [StreamMatchIndexer] Rebuild: {sw.ElapsedMilliseconds}ms. Tokens: {result.TokenMap.Count}");
            }
            catch (Exception ex) { AppLogger.Error("StreamMatchIndexer.RebuildAsync failed.", ex); }
        }

        [SkipLocalsInit]
        private IndexSnapshot BuildIndexInternal<T>(IReadOnlyList<T> streams, string fingerprint, IndexSnapshot? existing) where T : IMediaStream
        {
            if (streams == null || streams.Count == 0) return new IndexSnapshot(FrozenDictionary<string, int[]>.Empty, FrozenDictionary<string, int[]>.Empty, fingerprint, DateTime.UtcNow);

            var tokenRegistry = new ConcurrentDictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var idRegistry = new ConcurrentDictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var pool = StringPool.Shared;

            if (streams is IVirtualStreamList virtualList)
            {
                Parallel.ForEach(Partitioner.Create(0, virtualList.Count), range =>
                {
                    Span<char> titleBuffer = stackalloc char[1024];
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        var titleSpan = virtualList.GetTitleSpan(i, titleBuffer);
                        if (!titleSpan.IsEmpty)
                        {
                            foreach (var token in TitleHelper.GetTokens(titleSpan))
                            {
                                var list = tokenRegistry.GetOrAdd(pool.GetOrAdd(token), _ => new List<int>());
                                lock (list) { list.Add(i); }
                            }
                        }

                        string? idStr = virtualList.GetId(i);
                        if (!string.IsNullOrEmpty(idStr) && idStr != "0")
                        {
                            var list = idRegistry.GetOrAdd(pool.GetOrAdd(idStr), _ => new List<int>());
                            lock (list) { list.Add(i); }
                        }
                    }
                });
            }

            var frozenTokens = tokenRegistry.ToDictionary(k => k.Key, v => v.Value.ToArray()).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            var frozenIds = idRegistry.ToDictionary(k => k.Key, v => v.Value.ToArray()).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            
            // Reclaim StringPool memory as it's no longer needed after FrozenDictionary creation
            StringPool.Shared.Reset();

            return new IndexSnapshot(frozenTokens, frozenIds, fingerprint, DateTime.UtcNow);
        }

        public async Task SaveToDiskAsync(string path)
        {
            var snap = Volatile.Read(ref _snapshot);
            if (string.IsNullOrEmpty(snap.Fingerprint)) return;

            try
            {
                await Task.Run(() =>
                {
                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var writer = new BinaryWriter(fs, Encoding.UTF8);

                    // 1. Collect and Sort Tokens Alphabetically for Binary Search (Required for Prefix Matching)
                    var sortedTokens = snap.TokenMap.Select(kvp => new { 
                        Key = kvp.Key, 
                        Indices = kvp.Value
                    }).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();

                    // 2. Prepare Data Blocks (In-Memory first to calculate offsets)
                    using var indexStream = new MemoryStream();
                    using var stringStream = new MemoryStream();
                    using var indexWriter = new BinaryWriter(indexStream);
                    using var stringWriter = new BinaryWriter(stringStream);

                    var tokenRecords = new IndexTokenRecord[sortedTokens.Count];
                    for (int i = 0; i < sortedTokens.Count; i++)
                    {
                        var t = sortedTokens[i];
                        int stringOff = (int)stringStream.Position;
                        byte[] utf8 = Encoding.UTF8.GetBytes(t.Key);
                        stringStream.Write(utf8);

                        int indicesOff = (int)indexStream.Position / 4; // int offset
                        foreach (var idx in t.Indices) indexWriter.Write(idx);

                        tokenRecords[i] = new IndexTokenRecord(stringOff, (ushort)utf8.Length, indicesOff, (ushort)t.Indices.Length);
                    }

                    // 3. Write Padded Header
                    int tokenTableOff = 64;
                    int indicesHeapOff = tokenTableOff + (tokenRecords.Length * Marshal.SizeOf<IndexTokenRecord>());
                    int stringHeapOff = indicesHeapOff + (int)indexStream.Length;

                    var header = new IndexHeader(tokenRecords.Length, tokenTableOff, 0, 0, indicesHeapOff, stringHeapOff);
                    
                    // Direct pointer write for header
                    byte[] headerBuf = new byte[64];
                    unsafe { fixed (byte* p = headerBuf) { *(IndexHeader*)p = header; } }
                    writer.Write(headerBuf);

                    // 4. Write Token Table
                    foreach (var record in tokenRecords)
                    {
                        byte[] recordBuf = new byte[Marshal.SizeOf<IndexTokenRecord>()];
                        unsafe { fixed (byte* p = recordBuf) { *(IndexTokenRecord*)p = record; } }
                        writer.Write(recordBuf);
                    }

                    // 5. Write Heaps
                    writer.Write(indexStream.ToArray());
                    writer.Write(stringStream.ToArray());
                });
            }
            catch (Exception ex) { AppLogger.Error($"[StreamMatchIndexer] SaveToDisk failed: {path}", ex); }
        }

        public async Task<bool> TryLoadFromDiskAsync(string path, string expectedFingerprint)
        {
            if (!File.Exists(path)) return false;

            try
            {
                var session = await Task.Run(() => BinarySearchSession.OpenSafe(path, expectedFingerprint)).ConfigureAwait(false);
                if (session != null)
                {
                    _session?.Dispose();
                    _session = session;
                    // Clear memory-based maps to reclaim 30MB+ RAM
                    Volatile.Write(ref _snapshot, new IndexSnapshot(FrozenDictionary<string, int[]>.Empty, FrozenDictionary<string, int[]>.Empty, string.Empty, DateTime.MinValue));
                    AppLogger.Info($"[StreamMatchIndexer] Native MMF Session Activated: {path} (Tokens: {session.TokenCount})");
                    return true;
                }
                else
                {
                    // OpenSafe returned null, likely corrupted. Delete it.
                    AppLogger.Warn($"[StreamMatchIndexer] Sidecar INVALID (Magic mismatch). Deleting {path}");
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
            }
            catch (Exception ex) 
            { 
                AppLogger.Warn($"[StreamMatchIndexer] MMF load FAILED (Corrupted?): {ex.Message}. Deleting sidecar to trigger rebuild."); 
                try { if (File.Exists(path)) File.Delete(path); } catch { /* Ignore delete errors */ }
            }
            return false;
        }

        public void Clear()
        {
            _session?.Dispose();
            _session = null;
            Volatile.Write(ref _snapshot, new IndexSnapshot(FrozenDictionary<string, int[]>.Empty, FrozenDictionary<string, int[]>.Empty, string.Empty, DateTime.MinValue));
        }

        

        /// <summary>
        /// Performs an AND-based token lookup for a title string with ZERO memory overhead.
        /// </summary>
        public int[] FindByTokens(ReadOnlySpan<char> title)
        {
            if (title.IsEmpty) return Array.Empty<int>();

            var bitset = new SearchBitset();
            var tokenBitset = new SearchBitset();
            bool first = true;

            foreach (var token in TitleHelper.GetTokens(title))
            {
                var tokenIndices = FindByToken(token);
                if (tokenIndices.IsEmpty) return Array.Empty<int>();

                if (first)
                {
                    bitset.SetRange(tokenIndices);
                    first = false;
                }
                else
                {
                    tokenBitset.Clear();
                    tokenBitset.SetRange(tokenIndices);
                    bitset.Intersect(ref tokenBitset);
                }

                if (bitset.IsEmpty()) return Array.Empty<int>();
            }

            if (first) return Array.Empty<int>();
            
            int[] results = new int[bitset.CountSetBits()];
            bitset.FillIndices(results);
            return results;
        }

        // Compatibility overload
        public int[] FindByTokens(string title) => FindByTokens(title.AsSpan());

        public ReadOnlySpan<int> FindByToken(ReadOnlySpan<char> token)
        {
            if (token.IsEmpty) return ReadOnlySpan<int>.Empty;

            // 1. Try RAM Snapshot
            var snap = Volatile.Read(ref _snapshot);
            if (snap.TokenMap.Count > 0)
            {
                var lookup = snap.TokenMap.GetAlternateLookup<ReadOnlySpan<char>>();
                if (lookup.TryGetValue(token, out var indices)) return indices;
            }

            // 2. Try MMF Session
            if (_session != null) return _session.FindByToken(token);

            return ReadOnlySpan<int>.Empty;
        }

        // Overload for string callers
        public ReadOnlySpan<int> FindByToken(string token) => FindByToken(token.AsSpan());

        public ReadOnlySpan<int> FindById(ReadOnlySpan<char> id)
        {
            if (id.IsEmpty) return ReadOnlySpan<int>.Empty;

            // 1. Try RAM Snapshot
            var snap = Volatile.Read(ref _snapshot);
            if (snap.IdMap.Count > 0)
            {
                var lookup = snap.IdMap.GetAlternateLookup<ReadOnlySpan<char>>();
                if (lookup.TryGetValue(id, out var indices)) return indices;
            }

            // 2. Try MMF Session - Reusing binary token search logic for ID
            if (_session != null) return _session.FindByToken(id);

            return ReadOnlySpan<int>.Empty;
        }

        public ReadOnlySpan<int> FindById(string id) => FindById(id.AsSpan());
    }
}
