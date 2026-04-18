using System;
using System.Collections;
using System.Collections.Generic;
using ModernIPTVPlayer.Models.Metadata;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// PROJECT ZERO: Phase 4 — Virtualized Flyweight collection for Categories.
    /// Provides IReadOnlyList access to hundreds of categories with 0ms load and near-zero memory footprint.
    /// </summary>
    public class VirtualCategoryList : IReadOnlyList<LiveCategory>, IDisposable
    {
        private readonly BinaryCacheSession _session;
        private readonly WeakReference<LiveCategory>[] _identityMap;
        private readonly int _count;

        public VirtualCategoryList(BinaryCacheSession session)
        {
            _session = session;
            _count = session.RecordCount;
            _identityMap = new WeakReference<LiveCategory>[_count];
        }

        public int Count => _count;

        public LiveCategory this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new IndexOutOfRangeException();

                // 1. Identity Check (Weak-Identity Map)
                lock (_identityMap)
                {
                    var weakRef = _identityMap[index];
                    if (weakRef != null && weakRef.TryGetTarget(out var existing))
                    {
                        return existing;
                    }

                    // 2. Just-In-Time Hydration (Flyweight)
                    var category = new LiveCategory();
                    if (_session.TryReadRecord<CategoryRecord>(index, out var record))
                    {
                        category.LoadFromRecord(record);
                        category.SetCacheSession(_session);
                    }

                    _identityMap[index] = new WeakReference<LiveCategory>(category);

                    return category;
                }
            }
        }

        public IEnumerator<LiveCategory> GetEnumerator()
        {
            for (int i = 0; i < _count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
            _session.Dispose();
        }
    }
}
