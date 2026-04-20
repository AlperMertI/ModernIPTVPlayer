using System;
using System.Collections;
using System.Collections.Generic;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// PROJECT ZERO: Abstract base class to eliminate boilerplate redundacy across virtualized collections.
    /// Safely handles WinUI 3 virtualization requirements (IList) by delegating to core indexed access.
    /// </summary>
    public abstract class ReadOnlyVirtualListBase<T> : IList<T>, IReadOnlyList<T>, System.Collections.IList where T : class
    {
        public abstract T this[int index] { get; }
        public abstract int Count { get; }

        public bool IsReadOnly => true;

        // --- System.Collections.Generic.ICollection/IList ---
        public virtual IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public virtual int IndexOf(T item) => -1;
        public virtual bool Contains(T item) => false;

        void ICollection<T>.Add(T item) => throw new NotSupportedException();
        void ICollection<T>.Clear() => throw new NotSupportedException();
        void ICollection<T>.CopyTo(T[] array, int arrayIndex) => throw new NotSupportedException();
        bool ICollection<T>.Remove(T item) => throw new NotSupportedException();
        void IList<T>.Insert(int index, T item) => throw new NotSupportedException();
        void IList<T>.RemoveAt(int index) => throw new NotSupportedException();

        T IList<T>.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException();
        }

        // --- System.Collections.IList (Non-Generic) for WinUI 3 Virtualization ---
        object? System.Collections.IList.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException();
        }

        bool System.Collections.IList.IsFixedSize => true;
        bool System.Collections.IList.IsReadOnly => true;

        int System.Collections.IList.Add(object? value) => throw new NotSupportedException();
        void System.Collections.IList.Clear() => throw new NotSupportedException();
        bool System.Collections.IList.Contains(object? value) => value is T item && Contains(item);
        int System.Collections.IList.IndexOf(object? value) => value is T item ? IndexOf(item) : -1;
        void System.Collections.IList.Insert(int index, object? value) => throw new NotSupportedException();
        void System.Collections.IList.Remove(object? value) => throw new NotSupportedException();
        void System.Collections.IList.RemoveAt(int index) => throw new NotSupportedException();

        // --- System.Collections.ICollection (Non-Generic) ---
        void System.Collections.ICollection.CopyTo(Array array, int index) => throw new NotSupportedException();
        bool System.Collections.ICollection.IsSynchronized => false;
        object System.Collections.ICollection.SyncRoot => this;
    }
}
