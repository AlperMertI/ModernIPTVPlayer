using System;
using System.Collections;
using System.Collections.Generic;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// PROJECT ZERO: A lightweight, index-based proxy collection for WinUI 3.
    /// Prevents 50k+ object hydration by only materializing items on demand.
    /// Implements IList to satisfy GridView requirement for virtualization.
    /// </summary>
    public sealed class VirtualizedView<T> : ReadOnlyVirtualListBase<T> where T : class
    {
        private readonly IReadOnlyList<T> _source;
        private readonly int[] _indices;

        public VirtualizedView(IReadOnlyList<T> source, int[] indices)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _indices = indices ?? Array.Empty<int>();
        }

        public override T this[int index] => _source[_indices[index]];
        public override int Count => _indices.Length;

        /// <summary>
        /// Gets the raw indices mapped by this view.
        /// </summary>
        public int[] Indices => _indices;
    }
}
