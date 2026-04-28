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
    /// Virtualized Flyweight collection for Series streams.
    /// Provides IReadOnlyList access to 100k+ items with near-zero memory.
    /// </summary>
    public class VirtualSeriesList : ReadOnlyVirtualListBase<SeriesStream>, IDisposable, IVirtualStreamList
    {
        private readonly BinaryCacheSession _session;
        private readonly WeakReference<SeriesStream>[] _identityMap;
        private readonly int _count;
        public long Fingerprint { get; }

        public VirtualSeriesList(BinaryCacheSession session, long fingerprint = 0)
        {
            _session = session;
            _count = session.RecordCount;
            Fingerprint = fingerprint;
            _identityMap = new WeakReference<SeriesStream>[_count];
        }

        public override int Count => _count;

        /// <summary>
        /// Retrieves the title as a span for high-speed indexing.
        /// </summary>
        [SkipLocalsInit]
        public ReadOnlySpan<char> GetTitleSpan(int index, Span<char> buffer)
        {
            if (!_session.TryReadRecord<SeriesRecord>(index, out var record)) return ReadOnlySpan<char>.Empty;

            var utf8 = _session.GetUtf8Span(record.NameOff, record.NameLen);
            if (utf8.IsEmpty) return ReadOnlySpan<char>.Empty;

            if (System.Text.Unicode.Utf8.ToUtf16(utf8, buffer, out _, out int charsWritten) == System.Buffers.OperationStatus.Done)
            {
                return buffer.Slice(0, charsWritten);
            }
            return ReadOnlySpan<char>.Empty;
        }

        /// <summary>
        /// Retrieves the category ID as a span.
        /// Handles integer conversion without managed string allocation.
        /// </summary>
        [SkipLocalsInit]
        public ReadOnlySpan<char> GetCategorySpan(int index, Span<char> buffer)
        {
            if (!_session.TryReadRecord<SeriesRecord>(index, out var record)) return ReadOnlySpan<char>.Empty;
            
            if (record.CategoryId.TryFormat(buffer, out int written))
            {
                return buffer.Slice(0, written);
            }
            return ReadOnlySpan<char>.Empty;
        }

        public string? GetId(int index)
        {
            if (!_session.TryReadRecord<SeriesRecord>(index, out var record)) return null;
            return record.SeriesId.ToString();
        }

        public int GetStreamId(int index)
        {
            if (!_session.TryReadRecord<SeriesRecord>(index, out var record)) return 0;
            return record.SeriesId;
        }

        public override SeriesStream this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new IndexOutOfRangeException();

                // 1. Identity Check (Double-Checked Locking with WeakReference)
                var weakRef = Volatile.Read(ref _identityMap[index]);
                if (weakRef != null && weakRef.TryGetTarget(out var existing)) return existing;

                lock (_identityMap)
                {
                    weakRef = _identityMap[index];
                    if (weakRef != null && weakRef.TryGetTarget(out var existing2)) return existing2;

                    // 2. Just-In-Time Hydration (Flyweight)
                    var stream = new SeriesStream();
                    if (_session.TryReadRecord<SeriesRecord>(index, out var record))
                    {
                        stream.LoadFromRecord(record);
                        stream.SetCacheSession(_session, index);
                    }

                    _identityMap[index] = new WeakReference<SeriesStream>(stream);
                    return stream;
                }
            }
        }


        /// <summary>
        /// PERFORMANCE: Performs a zero-lock parallel scan of the entire dataset.
        /// Bypasses object hydration entirely by reading raw CategoryId from the pointer.
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
                    if (_session.TryReadRecord<SeriesRecord>(i, out var record))
                    {
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
