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
        // PROJECT ZERO: ArrayPool-backed storage for raw UTF-8 metadata.
        private static byte[] _buffer = ArrayPool<byte>.Shared.Rent(10 * 1024 * 1024); // Start with 10MB pooled
        private static int _position = 0;
        private static readonly System.Threading.Lock _lock = new();
        private static int _storeCount = 0;

        // PROJECT ZERO: Dedicated storage for large JSON blocks (AppExtras, Genres, etc.)
        private static readonly ConcurrentDictionary<string, (int Offset, int Length)> _jsonBlockCache = new();

        // PROJECT ZERO: String Interning & Deduplication
        private static readonly ConcurrentDictionary<string, (int Offset, int Length)> _internPool = new();
        private static readonly ConcurrentDictionary<(int Offset, int Length), string> _stringCache = new();
        private const int MAX_INTERN_LENGTH = 16;
        private const int MAX_INTERN_KEYS = 50000;
        private const int MAX_CACHE_SIZE = 10000;
         
        private static int _internKeysCount = 0;
        private static int _stringCacheCount = 0;

        /// <summary>
        /// Stores a string in the UTF-8 buffer. High-performance, zero-allocation via SpanOwner.
        /// </summary>
        public static (int Offset, int Length) Store(string? s)
        {
            if (string.IsNullOrEmpty(s)) return (-1, 0);

            // 1. PROJECT ZERO: Reactive Interning (O(1) hit check)
            if (s.Length <= MAX_INTERN_LENGTH)
            {
                if (_internPool.TryGetValue(s, out var existing)) return existing;
            }

            Interlocked.Increment(ref _storeCount);

            // Predict max bytes and use pooled rental
            int maxBytes = Encoding.UTF8.GetMaxByteCount(s.Length);
            using var owner = CommunityToolkit.HighPerformance.Buffers.SpanOwner<byte>.Allocate(maxBytes);
            int len = Encoding.UTF8.GetBytes(s, owner.Span);
            
            int offset;
            lock (_lock)
            {
                if (_position + len > _buffer.Length)
                {
                    // 2x Growth strategy using ArrayPool
                    int newSize = _buffer.Length * 2;
                    while (newSize < _position + len) newSize *= 2;
                    
                    byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
                    Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _position);
                    
                    var oldBuffer = _buffer;
                    _buffer = newBuffer;
                    ArrayPool<byte>.Shared.Return(oldBuffer);
                }

                offset = _position;
                owner.Span[..len].CopyTo(_buffer.AsSpan(offset));
                _position += len;
            }

            // Add to Intern Pool if eligible
            if (s.Length <= MAX_INTERN_LENGTH && Volatile.Read(ref _internKeysCount) < MAX_INTERN_KEYS)
            {
                if (_internPool.TryAdd(s, (offset, len)))
                {
                    Interlocked.Increment(ref _internKeysCount);
                }
            }

            return (offset, len);
        }

        /// <summary>
        /// Stores a raw JSON block or large metadata segment.
        /// Implements block-level deduplication to prevent object bloat.
        /// </summary>
        public static (int Offset, int Length) StoreJson(string? json)
        {
            if (string.IsNullOrEmpty(json)) return (-1, 0);

            // 1. PROJECT ZERO: Block-level cache check
            if (_jsonBlockCache.TryGetValue(json, out var existing)) return existing;

            var result = Store(json);
            
            // Limit cache size for very large blocks to prevent cache-bloat
            if (json.Length < 10000 && _jsonBlockCache.Count < 5000)
            {
                _jsonBlockCache.TryAdd(json, result);
            }

            return result;
        }

        public static (int Offset, int Length) StoreRaw(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return (-1, 0);

            int len = data.Length;
            int offset;

            lock (_lock)
            {
                if (_position + len > _buffer.Length)
                {
                    int newSize = _buffer.Length * 2;
                    while (newSize < _position + len) newSize *= 2;
                    
                    byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
                    Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _position);
                    
                    var oldBuffer = _buffer;
                    _buffer = newBuffer;
                    ArrayPool<byte>.Shared.Return(oldBuffer);
                }

                offset = _position;
                data.CopyTo(_buffer.AsSpan(offset, len));
                _position += len;
            }

            return (offset, len);
        }

        /// <summary>
        /// Vectorized comparison without string allocations.
        /// </summary>
        public static bool IsEqual(int offset, int length, string? value)
        {
            if (offset < 0) return string.IsNullOrEmpty(value);
            if (value == null) return false;

            if (Encoding.UTF8.GetByteCount(value) != length) return false;

            byte[] current = _buffer; // Thread-safe local snapshot
            ReadOnlySpan<byte> bufferSpan = current.AsSpan(offset, length);
            
            if (length <= 512)
            {
                Span<byte> valueSpan = stackalloc byte[length];
                Encoding.UTF8.GetBytes(value, valueSpan);
                return bufferSpan.SequenceEqual(valueSpan);
            }
            else
            {
                // Safety: Always use using-pattern with SpanOwner to prevent memory leaks
                using var owner = CommunityToolkit.HighPerformance.Buffers.SpanOwner<byte>.Allocate(length);
                Encoding.UTF8.GetBytes(value, owner.Span);
                return bufferSpan.SequenceEqual(owner.Span[..length]);
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

            byte[] current = _buffer; // Thread-safe local snapshot
            s = Encoding.UTF8.GetString(current, offset, length);

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
            byte[] current = _buffer; // Thread-safe local snapshot
            return new ReadOnlySpan<byte>(current, offset, length);
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
                    System.Diagnostics.Debug.WriteLine($"[MetadataBuffer] Appending: Resizing pooled buffer to {newSize / 1024 / 1024}MB");
                    byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
                    Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _position);
                    
                    var oldBuffer = _buffer;
                    _buffer = newBuffer;
                    ArrayPool<byte>.Shared.Return(oldBuffer);
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
                Interlocked.Exchange(ref _internKeysCount, 0);
                Interlocked.Exchange(ref _stringCacheCount, 0);
            }
        }
    }
}
