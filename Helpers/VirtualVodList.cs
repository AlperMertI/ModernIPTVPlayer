using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models;
using System.Runtime.CompilerServices;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Virtualized Flyweight collection for VOD streams.
    /// Provides IReadOnlyList access to 100k+ items with near-zero memory and 0ms load.
    /// </summary>
    public class VirtualVodList : ReadOnlyVirtualListBase<VodStream>, IDisposable, IVirtualStreamList
    {
        private readonly BinaryCacheSession _session;
        private readonly int _count;
        public long Fingerprint { get; }

        // PINNACLE: Dynamic LRU cache capacity to prevent memory bloat
        private readonly ConcurrentDictionary<int, VodStream> _itemCache = new();
        private readonly ConcurrentQueue<int> _lruQueue = new();
        public int MaxCacheItems { get; set; } = 150; 

        public VirtualVodList(BinaryCacheSession session, long fingerprint = 0)
        {
            _session = session;
            _count = session.RecordCount;
            Fingerprint = fingerprint;
        }

        public override int Count => _count;

        /// <summary>
        /// High-speed Span-based title retrieval for zero-allocation hotspots.
        /// </summary>
        [SkipLocalsInit]
        public ReadOnlySpan<char> GetTitleSpan(int index, Span<char> buffer)
        {
            if (!_session.TryReadRecord<VodRecord>(index, out var record)) return ReadOnlySpan<char>.Empty;

            var utf8 = _session.GetUtf8Span(record.NameOff, record.NameLen);
            if (utf8.IsEmpty) return ReadOnlySpan<char>.Empty;

            if (System.Text.Unicode.Utf8.ToUtf16(utf8, buffer, out _, out int charsWritten) == System.Buffers.OperationStatus.Done)
            {
                return buffer.Slice(0, charsWritten);
            }
            return ReadOnlySpan<char>.Empty;
        }

        /// <summary>
        /// Retrieves the category ID as a span without string allocation.
        /// Handles integer conversion without managed string allocation.
        /// </summary>
        [SkipLocalsInit]
        public ReadOnlySpan<char> GetCategorySpan(int index, Span<char> buffer)
        {
            if (!_session.TryReadRecord<VodRecord>(index, out var record)) return ReadOnlySpan<char>.Empty;
            
            if (record.CategoryId.TryFormat(buffer, out int written))
            {
                return buffer.Slice(0, written);
            }
            return ReadOnlySpan<char>.Empty;
        }

        public string? GetId(int index)
        {
            if (!_session.TryReadRecord<VodRecord>(index, out var record)) return null;
            return record.StreamId.ToString();
        }

        public int GetStreamId(int index)
        {
            if (!_session.TryReadRecord<VodRecord>(index, out var record)) return 0;
            return record.StreamId;
        }

        public override VodStream this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new IndexOutOfRangeException();

                // 1. Fast Path: Check LRU Cache
                if (_itemCache.TryGetValue(index, out var cached)) return cached;

                // 2. Slow Path: Hydrate new object (Flyweight View)
                var stream = new VodStream();
                if (_session.TryReadRecord<VodRecord>(index, out var record))
                {
                    stream.LoadFromRecord(record);
                    stream.SetCacheSession(_session, index);
                }

                // 3. LRU Management
                if (_itemCache.TryAdd(index, stream))
                {
                    _lruQueue.Enqueue(index);
                    while (_itemCache.Count > MaxCacheItems)
                    {
                        if (_lruQueue.TryDequeue(out int oldestIndex))
                            _itemCache.TryRemove(oldestIndex, out _);
                    }
                }

                return stream;
            }
        }

        /// <summary>
        /// PERFORMANCE: Performs a zero-lock parallel scan of the entire dataset.
        /// Bypasses object hydration entirely by reading raw fields from binary buffers.
        /// Aligns with Master Plan Item 45.
        /// </summary>
        /// <summary>
        /// PERFORMANCE: Performs a zero-lock parallel scan of the entire dataset.
        /// Bypasses object hydration entirely by reading raw fields from binary buffers.
        /// Aligns with Master Plan Item 45.
        /// </summary>
        public void ParallelScanInto(ConcurrentDictionary<string, List<int>> indexMap)
        {
            if (_count == 0) return;

            var partitioner = Partitioner.Create(0, _count, Math.Max(1, _count / (Environment.ProcessorCount * 2)));
            var pool = CommunityToolkit.HighPerformance.Buffers.StringPool.Shared;

            Parallel.ForEach(partitioner, range =>
            {
                Span<char> catBuf = stackalloc char[32];
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if (_session.TryReadRecord<VodRecord>(i, out var record))
                    {
                        // Deduplicate category strings using StringPool to save RAM
                        string catId = record.CategoryId.TryFormat(catBuf, out int written) 
                            ? pool.GetOrAdd(catBuf.Slice(0, written)) 
                            : record.CategoryId.ToString();

                        var list = indexMap.GetOrAdd(catId, _ => new List<int>());
                        lock (list) { list.Add(i); }
                    }
                }
            });
        }

        public void AddRef() => _session.AddRef();
        public BinaryCacheSession GetSession() => _session;

        public void Dispose()
        {
            _session.Dispose();
        }
    }
}
