using System;
using System.Buffers;
using System.Text;
using System.Collections.Concurrent;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Project Zero Core: High-performance UTF-8 storage for millions of metadata strings.
    /// Replaces individual string objects with a single, contiguous byte buffer.
    /// This drastically reduces RAM overhead and eliminates GC pressure.
    /// </summary>
    public static class MetadataBuffer
    {
        private static byte[] _buffer = new byte[10 * 1024 * 1024]; // Start with 10MB
        private static int _position = 0;
        private static readonly object _lock = new();
        private static long _storeCount = 0;

        /// <summary>
        /// Stores a string in the UTF-8 buffer and returns its offset and length.
        /// Deduplicates common strings automatically via FastStringPool if possible.
        /// </summary>
        public static (int Offset, int Length) Store(string? s)
        {
            if (string.IsNullOrEmpty(s)) return (-1, 0);

            _storeCount++;
            if (_storeCount % 10000 == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataBuffer] Store Count: {_storeCount}, Current Pos: {_position / 1024 / 1024}MB");
            }

            // 1. Check if we need to intern the string itself before UTF8 conversion (fast check)
            // But for zero-object, the aim is to not even have the string.
            // However, we'll use a local pool for very common small strings (extensions, years).

            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            int len = utf8.Length;
            int offset;

            lock (_lock)
            {
                if (_position + len > _buffer.Length)
                {
                    // Grow buffer exponentially
                    int newSize = _buffer.Length * 2;
                    System.Diagnostics.Debug.WriteLine($"[MetadataBuffer] CRITICAL: Resizing buffer to {newSize / 1024 / 1024}MB (Triggered by string of length {len})");
                    Array.Resize(ref _buffer, newSize);
                }

                offset = _position;
                Buffer.BlockCopy(utf8, 0, _buffer, offset, len);
                _position += len;
            }

            return (offset, len);
        }

        /// <summary>
        /// Compares a string against the stored UTF-8 bytes without allocating a new string object.
        /// This is used as a high-performance "loop breaker" in property setters.
        /// </summary>
        public static bool IsEqual(int offset, int length, string? value)
        {
            if (offset < 0) return string.IsNullOrEmpty(value);
            if (value == null) return false;

            // Optimization: If lengths (roughly) don't match, it can't be equal.
            // Using GetByteCount is faster than blindly copying/comparing if strings are vastly different.
            if (Encoding.UTF8.GetByteCount(value) != length) return false;

            // Byte-by-byte comparison using vectorized SequenceEqual
            var bufferSpan = new ReadOnlySpan<byte>(_buffer, offset, length);
            
            // For typical IPTV strings (titles, IDs), use stackalloc to avoid any heap allocation
            if (length <= 512)
            {
                Span<byte> valueSpan = stackalloc byte[length];
                Encoding.UTF8.GetBytes(value, valueSpan);
                return bufferSpan.SequenceEqual(valueSpan);
            }
            else
            {
                // For rare very long strings, use ArrayPool
                byte[] temp = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    Encoding.UTF8.GetBytes(value, 0, value.Length, temp, 0);
                    return bufferSpan.SequenceEqual(new ReadOnlySpan<byte>(temp, 0, length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(temp);
                }
            }
        }

        /// <summary>
        /// Retrieves a string from the buffer at the specified offset and length.
        /// This should only be called lazily when displaying items in the UI.
        /// </summary>
        public static string GetString(int offset, int length)
        {
            if (offset < 0 || length <= 0) return string.Empty;
            return Encoding.UTF8.GetString(_buffer, offset, length);
        }

        /// <summary>
        /// Returns a ReadOnlySpan of the raw UTF-8 bytes for comparison or search.
        /// This is the key to Zero-Allocation matching.
        /// </summary>
        public static ReadOnlySpan<byte> GetSpan(int offset, int length)
        {
            if (offset < 0 || length <= 0) return ReadOnlySpan<byte>.Empty;
            return new ReadOnlySpan<byte>(_buffer, offset, length);
        }

        /// <summary>
        /// Sets the internal buffer (useful for loading from BinaryBundle).
        /// </summary>
        public static void SetRawBuffer(byte[] raw, int position)
        {
            lock (_lock)
            {
                _buffer = raw;
                _position = position;
            }
        }

        public static byte[] GetRawBuffer() => _buffer;
        public static int GetPosition() => _position;

        /// <summary>
        /// Clears all stored data.
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _position = 0;
                // Don't shrink the buffer to avoid re-allocations on next use.
            }
        }
    }
}
