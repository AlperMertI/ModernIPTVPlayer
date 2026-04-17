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
    /// PROJECT ZERO: Phase 4 — Manages a Memory-Mapped File "Session" for a specific playlist.
    /// Handles zero-copy access to records and demand-paged string retrieval.
    /// </summary>
    public unsafe class BinaryCacheSession : IDisposable
    {
        private string _filePath;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private byte* _ptr;
        private long _currentCapacity;
        
        private readonly long _stringBufferOffset;
        private int _stringBufferLen;
        private readonly long _recordsOffset;
        private readonly int _recordCount;
        private readonly int _recordSize;

        private long _heapTail; // Start of growth area
        private const int GROWTH_CHUNK = 5 * 1024 * 1024; // 5MB

        private readonly ConcurrentDictionary<(int, int), string> _stringCache = new();
        private int _useCount = 0;
        private readonly bool _readOnlySession;

        public int RecordCount => _recordCount;
        public long RecordsOffset => _recordsOffset;
        public byte* BasePointer => _ptr;
        public bool IsReadOnly => _readOnlySession;
        public long HeapTail => _heapTail;
        public long StringBufferOffset => _stringBufferOffset;

        public static MemoryMappedFile OpenMemoryMappedFile(string filePath, MemoryMappedFileAccess access, long capacity = 0)
        {
            var fileAccess = access == MemoryMappedFileAccess.Read ? FileAccess.Read : FileAccess.ReadWrite;
            var fs = new FileStream(filePath, FileMode.Open, fileAccess, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
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

            // Start heap tail at the current stringsLen end
            _heapTail = stringsOffset + stringsLen;

            if (!readOnlySession) SetDirty(true);
            _useCount = 1;
        }

        private void SetDirty(bool dirty)
        {
            if (_ptr != null && !_readOnlySession)
            {
                *(_ptr + 16) = (byte)(dirty ? 1 : 0);
            }
        }

        public void UpdateHeaderStringsLen(int newLen)
        {
            if (_ptr != null && !_readOnlySession)
            {
                *(int*)(_ptr + 12) = newLen;
                _stringBufferLen = newLen;
            }
        }

        public void UpdateHeaderFingerprint(long fingerprint)
        {
            if (_ptr != null && !_readOnlySession)
            {
                *(long*)(_ptr + 24) = fingerprint;
            }
        }

        public long GetHeaderFingerprint()
        {
            if (_ptr != null)
            {
                return *(long*)(_ptr + 24);
            }
            return 0;
        }

        public string GetString(int offset, int length)
        {
            if (offset < 0 || length <= 0) return string.Empty;
            if (_ptr == null) return "Error: Disposed";

            // [PHASE 2.4] Security Guard: Prevent massive RAM spike from corrupted/misaligned binary records
            if (length > 65536) 
            {
                AppLogger.Warn($"[BinarySession] Suppressing giant string allocation: off={offset}, len={length}. Binary layout may be misaligned.");
                return string.Empty;
            }

            var key = (offset, length);
            if (_stringCache.TryGetValue(key, out var cached)) return cached;

            long absOffset = _stringBufferOffset + offset;
            if (absOffset + length > _currentCapacity || absOffset < 0) 
            {
                AppLogger.Warn($"[BinarySession] Offset out of bounds: off={absOffset}, len={length}, cap={_currentCapacity}");
                return string.Empty;
            }

            var s = Encoding.UTF8.GetString(_ptr + absOffset, length).TrimEnd('\0');
            if (_stringCache.Count < 5000) _stringCache.TryAdd(key, s);
            return s;
        }

        public (int NewOffset, int NewLength) PokeString(int offset, int length, string value)
        {
            if (_readOnlySession) throw new InvalidOperationException("BinaryCacheSession is read-only.");
            if (value == null) return (offset, length);
            
            int byteCount = Encoding.UTF8.GetByteCount(value);
            
            // Case A: Fits In-Place (Standard Zero-Waste)
            if (offset >= 0 && byteCount <= length)
            {
                long absOffset = _stringBufferOffset + offset;
                Span<byte> buffer = new Span<byte>(_ptr + absOffset, length);
                Encoding.UTF8.GetBytes(value, buffer);
                if (byteCount < length) buffer.Slice(byteCount).Clear();
                _stringCache.TryRemove((offset, length), out _);
                return (offset, length);
            }

            // Case B: Enrichment (Larger data) or New entry
            return AppendString(value);
        }

        public (int NewOffset, int NewLength) AppendString(string value)
        {
            if (_readOnlySession) throw new InvalidOperationException("BinaryCacheSession is read-only.");
            if (string.IsNullOrEmpty(value)) return (-1, 0);

            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            int len = utf8.Length;

            EnsureCapacity(len);

            long absOffset = _heapTail;
            Marshal.Copy(utf8, 0, (IntPtr)(_ptr + absOffset), len);
            
            int relativeOffset = (int)(_heapTail - _stringBufferOffset);
            _heapTail += len;
            
            // Update the string area length in header
            UpdateHeaderStringsLen((int)(_heapTail - _stringBufferOffset));
            
            return (relativeOffset, len);
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
            
            AppLogger.Info($"[BinarySession] Expanded Capacity to {newSize} bytes.");
        }

        public void PokeRecord<T>(int index, T record) where T : struct
        {
            if (_readOnlySession) return;
            if (index < 0 || index >= _recordCount) return;
            
            long offset = _recordsOffset + (index * _recordSize);
            Marshal.StructureToPtr(record, (IntPtr)(_ptr + offset), false);
        }

        public T* GetRecordPointer<T>(int index) where T : struct
        {
            if (index < 0 || index >= _recordCount || _ptr == null) return null;
            
            long offset = _recordsOffset + (index * (long)_recordSize);
            // Safety: Ensure the entire record struct fits within the current file capacity
            if (offset + _recordSize > _currentCapacity)
            {
                AppLogger.Error($"[BinarySession] Record pointer out of bounds: index={index}, offset={offset}, size={_recordSize}, cap={_currentCapacity}");
                return null;
            }

            return (T*)(_ptr + offset);
        }

        public void AddRef() => Interlocked.Increment(ref _useCount);

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _useCount) <= 0)
            {
                if (!_readOnlySession) SetDirty(false);
                if (_accessor != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    _accessor.Dispose();
                }
                _mmf?.Dispose();
                _stringCache.Clear();
            }
        }
    }
}
