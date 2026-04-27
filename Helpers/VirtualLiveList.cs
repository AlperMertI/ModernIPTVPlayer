using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Models;
using System.Runtime.CompilerServices;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Virtualized Flyweight collection for Live TV streams.
    /// Provides IReadOnlyList access to 50k+ items with near-zero memory footprint.
    /// Strictly adheres to Project Zero standards.
    /// </summary>
    public class VirtualLiveList : ReadOnlyVirtualListBase<LiveStream>, IDisposable, IVirtualStreamList
    {
        private readonly BinaryCacheSession _session;
        private readonly int _count;
        public long Fingerprint { get; }

        private readonly ConcurrentDictionary<int, LiveStream> _itemCache = new();
        private readonly ConcurrentQueue<int> _lruQueue = new();
        
        // PROJECT ZERO: ID-to-Index map for O(1) lookups across 50k items
        private FrozenDictionary<int, int> _idToIndexMap = FrozenDictionary<int, int>.Empty;
        
        public int MaxCacheItems { get; set; } = 400; // Increased for better stability on high-res screens

        public VirtualLiveList(BinaryCacheSession session, long fingerprint = 0)
        {
            _session = session;
            _count = session.RecordCount;
            Fingerprint = fingerprint;
        }

        public override int Count => _count;

        private static readonly ConcurrentStack<LiveStream> _pool = new();
        private static long _globalHydrationCount = 0;

        public override LiveStream this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new IndexOutOfRangeException();

                // 1. Fast Path: Check LRU Cache
                if (_itemCache.TryGetValue(index, out var cached)) return cached;

                // 2. Slow Path: Rent from Pool or Hydrate
                if (!_pool.TryPop(out var stream))
                {
                    stream = new LiveStream();
                    
                    // Track hydration for telemetry
                    var count = Interlocked.Increment(ref _globalHydrationCount);
                    if (count % 10000 == 0)
                    {
                        AppLogger.Info($"[PERF] VirtualLiveList: {count} objects created. Pool: {_pool.Count}");
                    }
                }
                else
                {
                    stream.Reset(); // Clear state before reuse
                }

                if (_session.TryReadRecord<LiveStreamData>(index, out var record))
                {
                    stream.LoadFromData(record);
                    stream.SetCacheSession(_session, index);
                    
                    if (_xtreamContext != null)
                        stream.SetXtreamContext(_xtreamContext);
                }

                // 3. LRU Management
                if (_itemCache.TryAdd(index, stream))
                {
                    _lruQueue.Enqueue(index);
                    while (_itemCache.Count > MaxCacheItems)
                    {
                        if (_lruQueue.TryDequeue(out int oldestIndex))
                        {
                            if (_itemCache.TryRemove(oldestIndex, out var evicted))
                            {
                                // PROJECT ZERO: Return to pool for reuse
                                _pool.Push(evicted);
                            }
                        }
                    }
                }

                return stream;
            }
        }

        public int FindIndexByStreamId(int streamId)
        {
            return _idToIndexMap.TryGetValue(streamId, out int index) ? index : -1;
        }

        /// <summary>
        /// Retrieves the stream title using high-speed UTF8-to-UTF16 conversion.
        /// Zero allocation on the managed heap.
        /// </summary>
        [SkipLocalsInit]
        public ReadOnlySpan<char> GetTitleSpan(int index, Span<char> buffer)
        {
            if (!_session.TryReadRecord<LiveStreamData>(index, out var record)) return ReadOnlySpan<char>.Empty;
            return ReadUtf8Field(record.NameOff, record.NameLen, buffer);
        }

        /// <summary>
        /// Retrieves the category ID as a span without string allocation.
        /// </summary>
        [SkipLocalsInit]
        public ReadOnlySpan<char> GetCategorySpan(int index, Span<char> buffer)
        {
            if (!_session.TryReadRecord<LiveStreamData>(index, out var record)) return ReadOnlySpan<char>.Empty;
            return ReadUtf8Field(record.CatOff, record.CatLen, buffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ReadOnlySpan<char> ReadUtf8Field(int offset, int length, Span<char> buffer)
        {
            var utf8 = _session.GetUtf8Span(offset, length);
            if (utf8.IsEmpty) return ReadOnlySpan<char>.Empty;

            if (System.Text.Unicode.Utf8.ToUtf16(utf8, buffer, out _, out int charsWritten) == System.Buffers.OperationStatus.Done)
            {
                return buffer.Slice(0, charsWritten);
            }
            return ReadOnlySpan<char>.Empty;
        }

        private LiveStream.XtreamContext? _xtreamContext;

        public void SetXtreamContext(string baseUrl, string username, string password)
        {
            _xtreamContext = new LiveStream.XtreamContext(baseUrl, username, password);
        }

        public string? GetId(int index)
        {
            if (!_session.TryReadRecord<LiveStreamData>(index, out var record)) return null;
            return record.StreamId.ToString();
        }

        public int GetStreamId(int index)
        {
            if (!_session.TryReadRecord<LiveStreamData>(index, out var record)) return 0;
            return record.StreamId;
        }

        /// <summary>
        /// Performs a high-speed parallel scan of the entire dataset to build indexing metadata.
        /// Zero-allocation category mapping and ID indexing.
        /// </summary>
        public void ParallelScanInto(ConcurrentDictionary<string, List<int>> indexMap)
        {
            var partitioner = Partitioner.Create(0, _count, Math.Max(1, _count / (Environment.ProcessorCount * 2)));
            var tempIdMap = new ConcurrentDictionary<int, int>(Environment.ProcessorCount * 2, _count);

            Parallel.ForEach(partitioner, range =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if (_session.TryReadRecord<LiveStreamData>(i, out var record))
                    {
                        // 1. Populate ID Map
                        tempIdMap.TryAdd(record.StreamId, i);

                        // 2. Map Category (Uses session's StringPool internally)
                        string catId = _session.GetString(record.CatOff, record.CatLen);
                        if (string.IsNullOrEmpty(catId)) catId = "Genel";
                        
                        var list = indexMap.GetOrAdd(catId, _ => new List<int>());
                        lock (list) { list.Add(i); }
                    }
                }
            });

            _idToIndexMap = tempIdMap.ToFrozenDictionary();
            AppLogger.Info($"[PERF] VirtualLiveList: Parallel scan completed. Indexed {_count} streams.");
        }

        /// <summary>
        /// PROJECT ZERO: Parallel scan to build technical flags without hydrating objects.
        /// Bypasses 50k+ managed object allocations.
        /// </summary>
        public void ParallelScanFlagsInto(ushort[] flags, ProbeCacheService probeCache)
        {
            if (flags == null || flags.Length < _count) return;

            Parallel.For(0, _count, i =>
            {
                if (_session.TryReadRecord<LiveStreamData>(i, out var record))
                {
                    flags[i] = probeCache.GetFlags(record.StreamId);
                }
            });
        }

        public void AddRef() => _session.AddRef();
        public BinaryCacheSession GetSession() => _session;

        public void Dispose() => _session.Dispose();
    }
}
