using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// PROJECT ZERO: Phase 4 — Virtualized Flyweight collection for Series streams.
    /// Provides IReadOnlyList access to 100k+ items with near-zero memory and 0ms load.
    /// </summary>
    public class VirtualSeriesList : IReadOnlyList<SeriesStream>, IDisposable
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

        public int Count => _count;

        public unsafe SeriesStream this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new IndexOutOfRangeException();
                if (_session.BasePointer == null) return new SeriesStream { Name = "Error: Disposed" };

                // 1. Identity Check (Double-Checked Locking with WeakReference)
                var weakRef = Volatile.Read(ref _identityMap[index]);
                if (weakRef != null && weakRef.TryGetTarget(out var existing))
                {
                    return existing;
                }

                lock (_identityMap)
                {
                    weakRef = _identityMap[index];
                    if (weakRef != null && weakRef.TryGetTarget(out var existing2))
                    {
                        return existing2;
                    }

                    // 2. Just-In-Time Hydration (Flyweight)
                    var stream = new SeriesStream();
                    var record = _session.GetRecordPointer<SeriesRecord>(index);
                    if (record != null)
                    {
                        stream.LoadFromRecord(*record);
                        
                        // [PHASE 4] Link stream to the session for string/poke access
                        stream.SetCacheSession(_session, index);
                    }

                    _identityMap[index] = new WeakReference<SeriesStream>(stream);
                    return stream;
                }
            }
        }

        public IEnumerator<SeriesStream> GetEnumerator()
        {
            for (int i = 0; i < _count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// PERFORMANCE: Performs a zero-lock parallel scan of the entire dataset.
        /// Bypasses object hydration entirely by reading raw CategoryId from the pointer.
        /// </summary>
        public unsafe void ParallelScanInto(ConcurrentDictionary<string, ConcurrentBag<int>> indexMap)
        {
            if (_session.BasePointer == null) return;

            var partitioner = Partitioner.Create(0, _count, Math.Max(1, _count / (Environment.ProcessorCount * 2)));

            Parallel.ForEach(partitioner, range =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    var record = _session.GetRecordPointer<SeriesRecord>(i);
                    if (record != null)
                    {
                        string catId = record->CategoryId.ToString();
                        var bag = indexMap.GetOrAdd(catId, _ => new ConcurrentBag<int>());
                        bag.Add(i);
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
