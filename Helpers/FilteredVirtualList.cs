using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// PROJECT ZERO: Zero-allocation virtualized search/filter results.
    /// Maps a subset of indices from a VirtualLiveList without hydrating managed objects.
    /// </summary>
    public class FilteredVirtualList : ReadOnlyVirtualListBase<LiveStream>
    {
        private readonly VirtualLiveList _source;
        private readonly int[] _indices;

        public FilteredVirtualList(VirtualLiveList source, IEnumerable<int> indices)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _indices = indices as int[] ?? indices.ToArray();
        }

        public override int Count => _indices.Length;

        public override LiveStream this[int index]
        {
            get
            {
                if (index < 0 || index >= _indices.Length) throw new IndexOutOfRangeException();
                return _source[_indices[index]];
            }
        }

        public int GetSourceIndex(int index)
        {
            if (index < 0 || index >= _indices.Length) return -1;
            return _indices[index];
        }

        public VirtualLiveList Source => _source;
    }
}
