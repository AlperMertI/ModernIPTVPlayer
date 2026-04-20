using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
        private readonly WeakReference<LiveStream>[] _identityMap;
        private readonly int _count;
        public long Fingerprint { get; }

        public VirtualLiveList(BinaryCacheSession session, long fingerprint = 0)
        {
            _session = session;
            _count = session.RecordCount;
            Fingerprint = fingerprint;
            _identityMap = new WeakReference<LiveStream>[_count];
        }

        public override int Count => _count;

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

        public override LiveStream this[int index]
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
                    var stream = new LiveStream();
                    if (_session.TryReadRecord<LiveStreamData>(index, out var record))
                    {
                        stream.LoadFromData(record);
                        stream.SetCacheSession(_session, index);
                        
                        // Propagate shared context for lazy URL reconstruction
                        if (_xtreamContext != null)
                        {
                            stream.SetXtreamContext(_xtreamContext);
                        }
                    }

                    _identityMap[index] = new WeakReference<LiveStream>(stream);
                    return stream;
                }
            }
        }

        /// <summary>
        /// Performs a high-speed parallel scan of the entire dataset to build an index map
        /// without hydrating any managed objects. Aligns with Master Plan Item 45.
        /// </summary>
        public void ParallelScanInto(ConcurrentDictionary<string, List<int>> indexMap)
        {
            var partitioner = Partitioner.Create(0, _count, Math.Max(1, _count / (Environment.ProcessorCount * 2)));

            Parallel.ForEach(partitioner, range =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if (_session.TryReadRecord<LiveStreamData>(i, out var record))
                    {
                        var utf8 = _session.GetUtf8Span(record.CatOff, record.CatLen);
                        string catId = !utf8.IsEmpty ? _session.GetString(record.CatOff, record.CatLen) : "Genel";
                        
                        var list = indexMap.GetOrAdd(catId, _ => new List<int>());
                        lock (list) { list.Add(i); }
                    }
                }
            });
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
