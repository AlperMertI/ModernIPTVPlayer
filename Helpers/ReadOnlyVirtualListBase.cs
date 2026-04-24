using System;
using System.Collections;
using System.Collections.Generic;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// PROJECT ZERO: Abstract base class to eliminate boilerplate redundacy across virtualized collections.
    /// Safely handles WinUI 3 virtualization requirements (IList) by delegating to core indexed access.
    /// </summary>
    public abstract partial class ReadOnlyVirtualListBase<T> : IList<T>, IReadOnlyList<T>, System.Collections.IList where T : class
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

        public virtual void CopyTo(T[] array, int arrayIndex)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (arrayIndex < 0) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            if (array.Length - arrayIndex < Count) throw new ArgumentException("Insufficient space in target array");

            for (int i = 0; i < Count; i++)
            {
                array[arrayIndex + i] = this[i];
            }
        }

        void ICollection<T>.Add(T item) => throw new NotSupportedException();
        void ICollection<T>.Clear() => throw new NotSupportedException();
        void ICollection<T>.CopyTo(T[] array, int arrayIndex) => CopyTo(array, arrayIndex);
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
        void System.Collections.ICollection.CopyTo(Array array, int index)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (array.Length - index < Count) throw new ArgumentException("Insufficient space in target array");

            for (int i = 0; i < Count; i++)
            {
                array.SetValue(this[i], index + i);
            }
        }
        bool System.Collections.ICollection.IsSynchronized => false;
        object System.Collections.ICollection.SyncRoot => this;
    }
}
