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
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;

namespace ModernIPTVPlayer.Services.Iptv
{
    /// <summary>
    /// PINNACLE: High-performance Search Index Manager.
    /// Manages MMF V4 indexing and zero-allocation search execution.
    /// </summary>
    public sealed class FastSearchIndex
    {
        private BinarySearchSession? _session;
        private IndexSnapshot _snapshot = new(
            FrozenDictionary<string, int[]>.Empty, 
            Array.Empty<string>(), 
            string.Empty, 
            DateTime.MinValue);

        private sealed record IndexSnapshot(
            FrozenDictionary<string, int[]> TokenMap,
            string[] SortedTokens,
            string Fingerprint,
            DateTime CreatedAt);

        public string Fingerprint
        {
            get
            {
                var fp = _session?.Fingerprint ?? Volatile.Read(ref _snapshot).Fingerprint;
                return string.IsNullOrEmpty(fp) ? string.Empty : "v8_" + fp;
            }
        }

        public async Task RebuildAsync<T>(IReadOnlyList<T> streams, string newFingerprint, CancellationToken ct = default) where T : IMediaStream
        {
            if (string.IsNullOrEmpty(newFingerprint)) return;

            if (Fingerprint == newFingerprint)
            {
                AppLogger.Info($"[FastSearchIndex] Index up to date (FP: {newFingerprint}).");
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await Task.Run(() => BuildIndexInternal(streams, newFingerprint, ct), ct).ConfigureAwait(false);
                
                _session?.Dispose();
                _session = null;

                Volatile.Write(ref _snapshot, result);
                AppLogger.Info($"[PERF] [FastSearchIndex] Rebuild: {sw.ElapsedMilliseconds}ms. Tokens: {result.TokenMap.Count}");
            }
            catch (Exception ex) { AppLogger.Error("FastSearchIndex.RebuildAsync failed.", ex); }
        }

        [SkipLocalsInit]
        private IndexSnapshot BuildIndexInternal<T>(IReadOnlyList<T> streams, string fingerprint, CancellationToken ct) where T : IMediaStream
        {
            var tokenRegistry = new ConcurrentDictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            if (streams is IVirtualStreamList virtualList)
            {
                if (virtualList.Count == 0) return new IndexSnapshot(FrozenDictionary<string, int[]>.Empty, Array.Empty<string>(), fingerprint, DateTime.UtcNow);

                Parallel.ForEach(Partitioner.Create(0, virtualList.Count), new ParallelOptions { CancellationToken = ct }, range =>
                {
                    Span<char> titleBuffer = stackalloc char[512];
                    Span<char> normBuffer = stackalloc char[512];

                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        var titleSpan = virtualList.GetTitleSpan(i, titleBuffer);
                        if (!titleSpan.IsEmpty)
                        {
                            int normLen = TitleHelper.NormalizeForSearch(titleSpan, normBuffer);
                            var normalized = normBuffer.Slice(0, normLen);

                            foreach (var token in TitleHelper.GetTokens(normalized))
                            {
                                if (token.Length < 2) continue;
                                var list = tokenRegistry.GetOrAdd(token.ToString(), _ => new List<int>());
                                lock (list) { list.Add(i); }
                            }
                        }
                    }
                });
            }

            var finalMap = tokenRegistry.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            var sortedTokens = finalMap.Keys.ToArray();
            Array.Sort(sortedTokens, StringComparer.OrdinalIgnoreCase);

            return new IndexSnapshot(finalMap, sortedTokens, fingerprint, DateTime.UtcNow);
        }

        /// <summary>
        /// Performs a Pinnacle Zero-Allocation search.
        /// </summary>
        public int[] GetMatchingIndices<T>(string query, IReadOnlyList<T> source) where T : IMediaStream
        {
            if (string.IsNullOrWhiteSpace(query)) return Array.Empty<int>();
            
            if (_session != null)
            {
                Span<int> sink = stackalloc int[1000]; // Max 1000 results
                int count = _session.Search(query.AsSpan(), sink, source.Count);
                if (count == 0) return Array.Empty<int>();
                return sink.Slice(0, count).ToArray();
            }

            // Fallback to legacy snapshot search if MMF is not loaded
            return GetSnapshotIndices(query);
        }

        private int[] GetSnapshotIndices(string query)
        {
            var snap = Volatile.Read(ref _snapshot);
            if (snap.TokenMap.Count == 0) return Array.Empty<int>();

            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            HashSet<int>? intersectSet = null;

            foreach (var term in terms)
            {
                var termSet = new HashSet<int>();
                foreach (var kvp in snap.TokenMap)
                {
                    if (kvp.Key.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var idx in kvp.Value) termSet.Add(idx);
                    }
                }

                if (intersectSet == null) intersectSet = termSet;
                else intersectSet.IntersectWith(termSet);

                if (intersectSet.Count == 0) break;
            }

            return intersectSet?.ToArray() ?? Array.Empty<int>();
        }

        public IEnumerable<T> Search<T>(string query, IReadOnlyList<T> source) where T : IMediaStream
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<T>();
            
            var indices = GetMatchingIndices(query, source);
            if (indices.Length == 0) return Enumerable.Empty<T>();
            
            return MaterializeResults(indices, source);
        }

        private IEnumerable<T> MaterializeResults<T>(int[] indices, IReadOnlyList<T> source) where T : IMediaStream
        {
            foreach (var idx in indices)
            {
                if (idx >= 0 && idx < source.Count)
                {
                    yield return source[idx];
                }
            }
        }

        public async Task SaveToDiskAsync(string path)
        {
            var snap = Volatile.Read(ref _snapshot);
            if (string.IsNullOrEmpty(snap.Fingerprint)) return;

            try
            {
                await Task.Run(() =>
                {
                    // 1. Prepare Data
                    var sortedTokens = snap.TokenMap.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
                    
                    // Trigrams: trigramHash -> list of indices
                    var trigramMap = new Dictionary<uint, HashSet<int>>();
                    Span<uint> trigramBuf = stackalloc uint[128];

                    foreach (var kvp in snap.TokenMap)
                    {
                        int len = TitleHelper.GetTrigrams(kvp.Key.AsSpan(), trigramBuf);
                        for (int i = 0; i < len; i++)
                        {
                            if (!trigramMap.TryGetValue(trigramBuf[i], out var set))
                            {
                                set = new HashSet<int>();
                                trigramMap[trigramBuf[i]] = set;
                            }
                            foreach (var idx in kvp.Value) set.Add(idx);
                        }
                    }
                    var sortedTrigrams = trigramMap.OrderBy(x => x.Key).ToList();

                    // 2. Write to MMF Format
                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var writer = new BinaryWriter(fs, Encoding.UTF8);

                    using var indexStream = new MemoryStream();
                    using var stringStream = new MemoryStream();
                    using var indexWriter = new BinaryWriter(indexStream);
                    using var stringWriter = new BinaryWriter(stringStream);

                    // Tokens
                    var tokenRecords = new IndexTokenRecord[sortedTokens.Count];
                    for (int i = 0; i < sortedTokens.Count; i++)
                    {
                        var t = sortedTokens[i];
                        int stringOff = (int)stringStream.Position;
                        byte[] utf8 = Encoding.UTF8.GetBytes(t.Key);
                        stringStream.Write(utf8);

                        int indicesOff = (int)indexStream.Position / 4;
                        foreach (var idx in t.Value) indexWriter.Write(idx);

                        tokenRecords[i] = new IndexTokenRecord(stringOff, (ushort)utf8.Length, indicesOff, (ushort)t.Value.Length);
                    }

                    // Trigrams
                    var trigramRecords = new TrigramRecord[sortedTrigrams.Count];
                    for (int i = 0; i < sortedTrigrams.Count; i++)
                    {
                        var t = sortedTrigrams[i];
                        int indicesOff = (int)indexStream.Position / 4;
                        var idxArray = t.Value.ToArray();
                        Array.Sort(idxArray);
                        foreach (var idx in idxArray) indexWriter.Write(idx);

                        trigramRecords[i] = new TrigramRecord(t.Key, indicesOff, (ushort)idxArray.Length);
                    }

                    // Layout offsets
                    int headerSize = 64;
                    int tokenTableOff = headerSize;
                    int trigramTableOff = tokenTableOff + (tokenRecords.Length * 16);
                    int indicesHeapOff = trigramTableOff + (trigramRecords.Length * 12);
                    int stringHeapOff = indicesHeapOff + (int)indexStream.Length;

                    // Header
                    var header = new IndexHeader(tokenRecords.Length, tokenTableOff, trigramRecords.Length, trigramTableOff, indicesHeapOff, stringHeapOff);
                    byte[] headerBuf = new byte[64];
                    unsafe { fixed (byte* p = headerBuf) { *(IndexHeader*)p = header; } }
                    writer.Write(headerBuf);

                    // Tables
                    foreach (var rec in tokenRecords) 
                    {
                        byte[] buf = new byte[16];
                        unsafe { fixed (byte* p = buf) { *(IndexTokenRecord*)p = rec; } }
                        writer.Write(buf);
                    }
                    foreach (var rec in trigramRecords) 
                    {
                        byte[] buf = new byte[12];
                        unsafe { fixed (byte* p = buf) { *(TrigramRecord*)p = rec; } }
                        writer.Write(buf);
                    }

                    // Heaps
                    writer.Write(indexStream.ToArray());
                    writer.Write(stringStream.ToArray());
                });
            }
            catch (Exception ex) { AppLogger.Error($"[FastSearchIndex] SaveToDisk failed: {path}", ex); }
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
                    Volatile.Write(ref _snapshot, new IndexSnapshot(FrozenDictionary<string, int[]>.Empty, Array.Empty<string>(), string.Empty, DateTime.MinValue));
                    AppLogger.Info($"[FastSearchIndex] Pinnacle MMF Session Activated: {path} (Tokens: {session.TokenCount}, Trigrams: {session.TrigramCount})");
                    return true;
                }
            }
            catch (Exception ex) 
            { 
                AppLogger.Warn($"[FastSearchIndex] MMF load FAILED: {ex.Message}"); 
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
            return false;
        }

        public void Clear()
        {
            _session?.Dispose();
            _session = null;
            Volatile.Write(ref _snapshot, new IndexSnapshot(FrozenDictionary<string, int[]>.Empty, Array.Empty<string>(), string.Empty, DateTime.MinValue));
        }
    }
}
