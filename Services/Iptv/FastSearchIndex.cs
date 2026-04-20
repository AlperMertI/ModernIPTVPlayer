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
    /// A high-performance prefix search index for IPTV streams.
    /// Implements lock-free reads using immutable snapshots and StringPool for memory efficiency.
    /// Fully NativeAOT compatible.
    /// </summary>
    /// <summary>
    /// A high-performance prefix search index for IPTV streams.
    /// Implements lock-free reads/writes using immutable snapshots and sorted-array BinarySearch for memory efficiency.
    /// Fully NativeAOT compatible and optimized for large datasets.
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

        public string Fingerprint => _session?.Fingerprint ?? Volatile.Read(ref _snapshot).Fingerprint;

        public async Task RebuildAsync<T>(IReadOnlyList<T> streams, string newFingerprint) where T : IMediaStream
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
                var result = await Task.Run(() => BuildIndexInternal(streams, newFingerprint)).ConfigureAwait(false);
                
                _session?.Dispose();
                _session = null;

                Volatile.Write(ref _snapshot, result);
                AppLogger.Info($"[PERF] [FastSearchIndex] Rebuild: {sw.ElapsedMilliseconds}ms. Tokens: {result.TokenMap.Count}");
            }
            catch (Exception ex) { AppLogger.Error("FastSearchIndex.RebuildAsync failed.", ex); }
        }

        [SkipLocalsInit]
        private IndexSnapshot BuildIndexInternal<T>(IReadOnlyList<T> streams, string fingerprint) where T : IMediaStream
        {
            var tokenRegistry = new ConcurrentDictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var pool = StringPool.Shared;

            if (streams is IVirtualStreamList virtualList)
            {
                Parallel.ForEach(Partitioner.Create(0, virtualList.Count), range =>
                {
                    Span<char> titleBuffer = stackalloc char[512];
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        var titleSpan = virtualList.GetTitleSpan(i, titleBuffer);
                        if (!titleSpan.IsEmpty)
                        {
                            foreach (var token in TitleHelper.GetTokens(titleSpan))
                            {
                                if (token.Length < 2) continue;
                                var list = tokenRegistry.GetOrAdd(pool.GetOrAdd(token), _ => new List<int>());
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

        public int[] GetMatchingIndices(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Array.Empty<int>();
            string q = TitleHelper.Normalize(query);
            if (string.IsNullOrEmpty(q)) return Array.Empty<int>();

            var results = new List<int>();
            if (_session != null)
            {
                _session.FindByPrefix(q.AsSpan(), results);
                return results.ToArray();
            }

            var snap = Volatile.Read(ref _snapshot);
            var lookup = snap.TokenMap.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(q, out int[]? exactIndices)) return exactIndices;

            int index = Array.BinarySearch(snap.SortedTokens, q, StringComparer.OrdinalIgnoreCase);
            if (index < 0) index = ~index;

            var aggregatedIndices = new HashSet<int>();
            for (int i = index; i < snap.SortedTokens.Length; i++)
            {
                var token = snap.SortedTokens[i];
                if (!token.StartsWith(q, StringComparison.OrdinalIgnoreCase)) break;
                if (snap.TokenMap.TryGetValue(token, out int[]? prefixIndices))
                {
                    for (int j = 0; j < prefixIndices.Length; j++) aggregatedIndices.Add(prefixIndices[j]);
                }
                if (aggregatedIndices.Count > 500) break;
            }
            return aggregatedIndices.ToArray();
        }

        public IEnumerable<T> Search<T>(string query, IReadOnlyList<T> source) where T : IMediaStream
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<T>();
            string q = TitleHelper.Normalize(query);
            if (string.IsNullOrEmpty(q)) return Enumerable.Empty<T>();

            var indices = new List<int>();
            if (_session != null)
            {
                _session.FindByPrefix(q.AsSpan(), indices);
            }
            else
            {
                var indicesArr = GetMatchingIndices(query);
                indices.AddRange(indicesArr);
            }

            if (indices.Count > 0) return MaterializeResults(indices.Distinct(), source);
            return SubstringSearchFallback(q, source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IEnumerable<T> MaterializeResults<T>(IEnumerable<int> indices, IReadOnlyList<T> source) where T : IMediaStream
        {
            var result = new List<T>();
            foreach (var idx in indices)
            {
                if (idx >= 0 && idx < source.Count) result.Add(source[idx]);
            }
            return result;
        }

        private IEnumerable<T> SubstringSearchFallback<T>(string q, IReadOnlyList<T> source) where T : IMediaStream
        {
            var results = new List<T>();
            if (source is IVirtualStreamList vList)
            {
                Span<char> buffer = stackalloc char[256];
                for (int i = 0; i < vList.Count; i++)
                {
                    var title = vList.GetTitleSpan(i, buffer);
                    if (!title.IsEmpty && title.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(source[i]);
                        if (results.Count > 50) break;
                    }
                }
            }
            return results;
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

                    var sortedTokens = snap.TokenMap.Select(kvp => new { Key = kvp.Key, Indices = kvp.Value })
                                                 .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();

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

                        int indicesOff = (int)indexStream.Position / 4;
                        foreach (var idx in t.Indices) indexWriter.Write(idx);

                        tokenRecords[i] = new IndexTokenRecord(stringOff, (ushort)utf8.Length, indicesOff, (ushort)t.Indices.Length);
                    }

                    int tokenTableOff = 64;
                    int indicesHeapOff = tokenTableOff + (tokenRecords.Length * Marshal.SizeOf<IndexTokenRecord>());
                    int stringHeapOff = indicesHeapOff + (int)indexStream.Length;

                    var header = new IndexHeader(tokenRecords.Length, tokenTableOff, indicesHeapOff, stringHeapOff);
                    byte[] headerBuf = new byte[64];
                    unsafe { fixed (byte* p = headerBuf) { *(IndexHeader*)p = header; } }
                    writer.Write(headerBuf);

                    foreach (var record in tokenRecords)
                    {
                        byte[] recordBuf = new byte[Marshal.SizeOf<IndexTokenRecord>()];
                        unsafe { fixed (byte* p = recordBuf) { *(IndexTokenRecord*)p = record; } }
                        writer.Write(recordBuf);
                    }

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
                    AppLogger.Info($"[FastSearchIndex] Native MMF Session Activated: {path} (Tokens: {session.TokenCount})");
                    return true;
                }
                else
                {
                    AppLogger.Warn($"[FastSearchIndex] Sidecar INVALID (Magic mismatch). Deleting {path}");
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
            }
            catch (Exception ex) 
            { 
                AppLogger.Warn($"[FastSearchIndex] MMF load FAILED (Corrupted?): {ex.Message}. Deleting sidecar to trigger rebuild."); 
                try { if (File.Exists(path)) File.Delete(path); } catch { /* Ignore delete errors */ }
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
