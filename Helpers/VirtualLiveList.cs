using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Virtualized flyweight collection for live streams.
    /// Keeps fixed records and strings memory-mapped; hydrates LiveStream only when UI asks for a row.
    /// </summary>
    public sealed class VirtualLiveList : IReadOnlyList<LiveStream>, IDisposable
    {
        private readonly BinaryCacheSession _session;
        private readonly WeakReference<LiveStream>[] _identityMap;
        private readonly int _count;

        public VirtualLiveList(BinaryCacheSession session)
        {
            _session = session;
            _count = session.RecordCount;
            _identityMap = new WeakReference<LiveStream>[_count];
        }

        public int Count => _count;

        public LiveStream this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count) throw new IndexOutOfRangeException();

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

                    var stream = new LiveStream();
                    if (_session.TryReadRecord<LiveStreamData>(index, out var record))
                    {
                        stream.LoadFromData(record);
                        stream.SetCacheSession(_session, index);
                    }

                    _identityMap[index] = new WeakReference<LiveStream>(stream);
                    return stream;
                }
            }
        }

        public IEnumerator<LiveStream> GetEnumerator()
        {
            for (int i = 0; i < _count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() => _session.Dispose();
    }
}
