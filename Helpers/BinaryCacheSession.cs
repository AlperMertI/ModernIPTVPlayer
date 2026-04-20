using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Binary Cache Session: High-performance memory-mapped file context.
    /// Uses CommunityToolkit.HighPerformance for zero-allocation IO and memory pooling.
    /// </summary>
    public unsafe class BinaryCacheSession : IDisposable
    {
        private string _filePath;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private byte* _ptr;
        private long _currentCapacity;
        private readonly System.Threading.Lock _accessorLock = new();
        private bool _disposed;
        
        private readonly long _stringBufferOffset;
        private int _stringBufferLen;
        private readonly long _recordsOffset;
        private readonly int _recordCount;
        private readonly int _recordSize;

        private long _heapTail; 
        private const int GROWTH_CHUNK = 8 * 1024 * 1024; // Standard 8MB chunks

        private int _useCount = 0;
        private readonly bool _readOnlySession;

        // Use StringPool for high-speed deduplication and zero-allocation char parsing
        private static readonly CommunityToolkit.HighPerformance.Buffers.StringPool _sharedStringPool = new();

        public int RecordCount => _recordCount;
        public long RecordsOffset => _recordsOffset;
        public byte* BasePointer => _ptr;
        public bool IsReadOnly => _readOnlySession;
        public long HeapTail => _heapTail;
        public long StringBufferOffset => _stringBufferOffset;

        public static MemoryMappedFile OpenMemoryMappedFile(string filePath, MemoryMappedFileAccess access, long capacity = 0)
        {
            var fileAccess = access == MemoryMappedFileAccess.Read ? FileAccess.Read : FileAccess.ReadWrite;
            var share = FileShare.ReadWrite | FileShare.Delete;
            
            var fs = new FileStream(filePath, FileMode.Open, fileAccess, share, 4096, FileOptions.RandomAccess);
            if (capacity > 0 && fs.Length < capacity) fs.SetLength(capacity);
            
            return MemoryMappedFile.CreateFromFile(fs, null, 0, access, HandleInheritability.None, false);
        }

        public BinaryCacheSession(string filePath, long stringsOffset, int stringsLen, long recordsOffset, int recordCount, int recordSize, bool readOnlySession = false)
        {
            _filePath = filePath;
            _readOnlySession = readOnlySession;
            _currentCapacity = new FileInfo(filePath).Length;
            
            var access = readOnlySession ? MemoryMappedFileAccess.Read : MemoryMappedFileAccess.ReadWrite;
            _mmf = OpenMemoryMappedFile(filePath, access);
            _accessor = _mmf.CreateViewAccessor(0, 0, access);
            
            byte* basePtr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
            _ptr = basePtr;

            _stringBufferOffset = stringsOffset;
            _stringBufferLen = stringsLen;
            _recordsOffset = recordsOffset;
            _recordCount = recordCount;
            _recordSize = recordSize;

            _heapTail = stringsOffset + stringsLen;

            if (!readOnlySession) SetDirty(true);
            _useCount = 1;
        }

        private void SetDirty(bool dirty)
        {
            if (_ptr != null && !_readOnlySession)
            {
                *(_ptr + BinaryCacheLayout.DirtyOffset) = (byte)(dirty ? 1 : 0);
            }
        }

        public void UpdateHeaderStringsLen(int newLen)
        {
            if (_ptr != null && !_readOnlySession)
            {
                *(int*)(_ptr + BinaryCacheLayout.StringsLengthOffset) = newLen;
                _stringBufferLen = newLen;
            }
        }

        public void UpdateHeaderFingerprint(long fingerprint)
        {
            if (_ptr != null && !_readOnlySession)
            {
                *(long*)(_ptr + BinaryCacheLayout.FingerprintOffset) = fingerprint;
            }
        }

        public long GetHeaderFingerprint()
        {
            if (_ptr != null)
            {
                return *(long*)(_ptr + BinaryCacheLayout.FingerprintOffset);
            }
            return 0;
        }

        /// <summary>
        /// Retrieves a raw UTF8 span directly from the MMF buffer. Zero-allocation.
        /// </summary>
        public ReadOnlySpan<byte> GetUtf8Span(int offset, int length)
        {
            if (offset < 0 || length <= 0) return ReadOnlySpan<byte>.Empty;
            if (length > 65536) return ReadOnlySpan<byte>.Empty;

            long absOffset = _stringBufferOffset + offset;
            if (absOffset + length > _currentCapacity) return ReadOnlySpan<byte>.Empty;

            return new ReadOnlySpan<byte>(_ptr + absOffset, length);
        }

        /// <summary>
        /// Retrieves a string from the heap using a zero-allocation pool.
        /// </summary>
        public string GetString(int offset, int length)
        {
            if (offset < 0 || length <= 0) return string.Empty;

            // Security: Constrain massive allocations from corruption
            if (length > 65536) return string.Empty;

            long absOffset = _stringBufferOffset + offset;
            if (absOffset + length > _currentCapacity) return string.Empty;

            // V2: StringPool deduplication (allocation-free on hits)
            ReadOnlySpan<byte> utf8 = new ReadOnlySpan<byte>(_ptr + absOffset, length);
            return _sharedStringPool.GetOrAdd(utf8, Encoding.UTF8);
        }

        /// <summary>
        /// Updates an existing string if it fits, or appends a new one. Zero-allocation.
        /// </summary>
        public (int NewOffset, int NewLength) PokeString(int offset, int length, string value)
        {
            if (_readOnlySession) throw new InvalidOperationException("Session is ReadOnly");
            if (value == null) return (offset, length);
            
            int byteCount = Encoding.UTF8.GetByteCount(value);
            
            // Re-use space if it fits perfectly or within existing boundaries
            if (offset >= 0 && byteCount <= length)
            {
                long absOffset = _stringBufferOffset + offset;
                Span<byte> buffer = new Span<byte>(_ptr + absOffset, length);
                Encoding.UTF8.GetBytes(value, buffer);
                if (byteCount < length) buffer.Slice(byteCount).Clear();
                return (offset, length);
            }

            return AppendString(value);
        }

        /// <summary>
        /// Appends a new string to the heap using pooled buffers to avoid allocations.
        /// </summary>
        public (int NewOffset, int NewLength) AppendString(string value)
        {
            if (_readOnlySession) throw new InvalidOperationException("Session is ReadOnly");
            if (string.IsNullOrEmpty(value)) return (-1, 0);

            // Max predicted size
            int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
            
            // Use CommunityToolkit's SpanOwner for safe rental/return
            using var owner = CommunityToolkit.HighPerformance.Buffers.SpanOwner<byte>.Allocate(maxBytes);
            int written = Encoding.UTF8.GetBytes(value, owner.Span);

            EnsureCapacity(written);

            long absOffsetInHeap = _heapTail;
            long relativeOffset = _heapTail - _stringBufferOffset;

            // Block copy to MMF
            fixed (byte* src = owner.Span)
            {
                Buffer.MemoryCopy(src, _ptr + absOffsetInHeap, written, written);
            }

            _heapTail += written;
            UpdateHeaderStringsLen((int)(_heapTail - _stringBufferOffset));
            
            return ((int)relativeOffset, written);
        }

        private void EnsureCapacity(int addedBytes)
        {
            if (_heapTail + addedBytes <= _currentCapacity) return;

            long newSize = _currentCapacity + GROWTH_CHUNK;
            while (_heapTail + addedBytes > newSize) newSize += GROWTH_CHUNK;

            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
            _mmf.Dispose();

            using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.SetLength(newSize);
            }

            _mmf = OpenMemoryMappedFile(_filePath, MemoryMappedFileAccess.ReadWrite);
            _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            
            byte* basePtr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
            _ptr = basePtr;
            _currentCapacity = newSize;
        }

        public void PokeRecord<T>(int index, T record) where T : unmanaged
        {
            if (_readOnlySession) return;
            if (index < 0 || index >= _recordCount) return;
            
            long offset = _recordsOffset + (index * (long)_recordSize);
            *(T*)(_ptr + offset) = record;
        }

        public T* GetRecordPointer<T>(int index) where T : unmanaged
        {
            if (index < 0 || index >= _recordCount || _ptr == null || _disposed) return null;
            
            long offset = _recordsOffset + (index * (long)_recordSize);
            if (offset + _recordSize > _currentCapacity) return null;

            return (T*)(_ptr + offset);
        }

        public bool TryReadRecord<T>(int index, out T record) where T : unmanaged
        {
            record = default;
            T* p = GetRecordPointer<T>(index);
            if (p == null) return false;
            
            record = *p;
            return true;
        }

        public void AddRef() => Interlocked.Increment(ref _useCount);

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _useCount) <= 0)
            {
                lock (_accessorLock)
                {
                    if (_disposed) return;
                    if (!_readOnlySession) SetDirty(false);
                    if (_accessor != null)
                    {
                        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                        _accessor.Dispose();
                    }
                    _mmf?.Dispose();
                    _ptr = null;
                    _disposed = true;
                }
            }
        }
    }
}
