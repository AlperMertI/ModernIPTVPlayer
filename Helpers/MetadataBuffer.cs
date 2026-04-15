using System;
using System.Buffers;
using System.Text;
using System.Collections.Concurrent;
using System.Threading;

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
        private static int _storeCount = 0;

        // PROJECT ZERO: String Interning Pool
        // Reuses offsets for identical strings to save massive RAM & Lock contention
        private static readonly ConcurrentDictionary<string, (int Offset, int Length)> _internPool = new();
        private static readonly ConcurrentDictionary<(int Offset, int Length), string> _stringCache = new();
        private const int MAX_INTERN_LENGTH = 256; // Increased to cover most titles/URLs while preventing multi-MB strings in dictionary
        private const int MAX_CACHE_SIZE = 10000; // Limit string cache to prevent RAM bloat

        // [FIX] Thread-safe counter to avoid accessing ConcurrentDictionary.Count (causes reentrancy)
        private static int _stringCacheCount = 0;

        /// <summary>
        /// Stores a string in the UTF-8 buffer and returns its offset and length.
        /// Deduplicates common strings automatically via FastStringPool if possible.
        /// </summary>
        public static (int Offset, int Length) Store(string? s)
        {
            if (string.IsNullOrEmpty(s)) return (-1, 0);

            // 1. PROJECT ZERO: Reactive Interning
            // If the string is short and already stored, just return the existing offset.
            if (s.Length <= MAX_INTERN_LENGTH)
            {
                if (_internPool.TryGetValue(s, out var existing)) return existing;
            }

            Interlocked.Increment(ref _storeCount);
            int currentStoreCount = _storeCount;
            if (currentStoreCount % 50000 == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataBuffer] Store Count: {currentStoreCount}, Current Pos: {_position / 1024 / 1024}MB");
            }

            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            int len = utf8.Length;
            int offset;

            lock (_lock)
            {
                if (_position + len > _buffer.Length)
                {
                    // Grow buffer exponentially
                    int newSize = Math.Max(_buffer.Length * 2, _position + len + 1024 * 1024);
                    System.Diagnostics.Debug.WriteLine($"[MetadataBuffer] CRITICAL: Resizing buffer to {newSize / 1024 / 1024}MB");
                    Array.Resize(ref _buffer, newSize);
                }

                offset = _position;
                Buffer.BlockCopy(utf8, 0, _buffer, offset, len);
                _position += len;
            }

            // 2. Add to Intern Pool if eligible
            if (s.Length <= MAX_INTERN_LENGTH)
            {
                _internPool.TryAdd(s, (offset, len));
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

            var key = (offset, length);
            if (_stringCache.TryGetValue(key, out var s)) return s;

            s = Encoding.UTF8.GetString(_buffer, offset, length);

            // [FIX] Use thread-safe counter instead of accessing ConcurrentDictionary.Count
            int currentCount = Thread.VolatileRead(ref _stringCacheCount);
            if (currentCount < MAX_CACHE_SIZE)
            {
                if (_stringCache.TryAdd(key, s))
                {
                    Interlocked.Increment(ref _stringCacheCount);
                }
            }
            else if (currentCount > MAX_CACHE_SIZE + 1000)
            {
                // Simple cache-clear if too big (O(1) approximation of LRU)
                _stringCache.Clear();
                Interlocked.Exchange(ref _stringCacheCount, 0);
            }

            return s;
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
        /// PROJECT ZERO: Safe Multithreaded Append.
        /// Appends a raw buffer (e.g., from a binary bundle) to the global store without 
        /// invalidating existing offsets. Returns the base offset where it was placed.
        /// </summary>
        public static int AppendRawBuffer(byte[] data, int length)
        {
            if (data == null || length <= 0) return 0;
            
            lock (_lock)
            {
                int baseOffset = _position;
                if (_position + length > _buffer.Length)
                {
                    int newSize = Math.Max(_buffer.Length + length + 1024 * 1024, _buffer.Length * 2);
                    System.Diagnostics.Debug.WriteLine($"[MetadataBuffer] Appending: Resizing buffer to {newSize / 1024 / 1024}MB");
                    Array.Resize(ref _buffer, newSize);
                }

                Buffer.BlockCopy(data, 0, _buffer, baseOffset, length);
                _position += length;
                return baseOffset;
            }
        }

        public static byte[] GetRawBuffer() => _buffer;
        public static int GetPosition() => _position;

        public static void Reset()
        {
            lock (_lock)
            {
                _position = 0;
                _internPool.Clear();
                _stringCache.Clear();
                _storeCount = 0;
                Interlocked.Exchange(ref _stringCacheCount, 0);
            }
        }
    }
}
