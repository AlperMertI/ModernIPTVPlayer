using System;
using System.Collections;
using System.Collections.Generic;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// PROJECT ZERO: Phase 4 — Virtualized Sub-List.
    /// Provides a zero-copy view of a specific category within a larger virtual list.
    /// Does not hydrate items until they are actually accessed by the UI.
    /// </summary>
    public class VirtualStreamSubList : IReadOnlyList<IMediaStream>, ICollection
    {
        private readonly IReadOnlyList<IMediaStream> _parent;
        private readonly int[] _indices;
        private readonly object _syncRoot = new object();

        public VirtualStreamSubList(IReadOnlyList<IMediaStream> parent, IEnumerable<int> indices)
        {
            _parent = parent;
            // Materialize indices once (small memory cost: ~4 bytes per item)
            var list = new List<int>(indices);
            _indices = list.ToArray();
        }

        public int Count => _indices.Length;

        public bool IsSynchronized => false;
        public object SyncRoot => _syncRoot;

        public void CopyTo(Array array, int index)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            for (int i = 0; i < _indices.Length; i++)
            {
                array.SetValue(this[i], index + i);
            }
        }

        public IMediaStream this[int index]
        {
            get
            {
                if (index < 0 || index >= _indices.Length) throw new IndexOutOfRangeException();
                return _parent[_indices[index]];
            }
        }

        public IEnumerator<IMediaStream> GetEnumerator()
        {
            for (int i = 0; i < _indices.Length; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
