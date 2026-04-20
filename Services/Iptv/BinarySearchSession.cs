using System;
using System.IO;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ModernIPTVPlayer.Models.Metadata;

namespace ModernIPTVPlayer.Services.Iptv
{
    /// <summary>
    /// PROJECT ZERO: Memory-Mapped Binary Search Engine.
    /// Manages an unmanaged index file and performs pointer-based lookups.
    /// Provides zero-allocation search results as ReadOnlySpan.
    /// </summary>
    public unsafe sealed class BinarySearchSession : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly byte* _basePtr = null;
        private readonly IndexHeader* _header = null;
        private readonly IndexTokenRecord* _tokenTable = null;
        private readonly int* _indicesHeap = null;
        private readonly byte* _stringHeap = null;
        public const long ExpectedMagic = 0x33564E4942584449; // "IDXBINV3"

        public string Fingerprint { get; }
        public int TokenCount => _header != null ? _header->TokenCount : 0;

        public static BinarySearchSession? OpenSafe(string path, string expectedFingerprint)
        {
            try
            {
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists || fileInfo.Length < Marshal.SizeOf<IndexHeader>()) return null;

                return new BinarySearchSession(path, expectedFingerprint);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[BinarySearchSession] Failed to open {path}: {ex.Message}");
                return null;
            }
        }

        private BinarySearchSession(string path, string expectedFingerprint)
        {
            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);

            _header = (IndexHeader*)_basePtr;

            if (_header->Magic != ExpectedMagic)
            {
                throw new InvalidDataException($"Invalid index magic. Found: 0x{_header->Magic:X}");
            }

            if (_header->Version < 3)
            {
                throw new InvalidDataException($"Unsupported version: {_header->Version}");
            }

            _tokenTable = (IndexTokenRecord*)(_basePtr + _header->TokenTableOff);
            _indicesHeap = (int*)(_basePtr + _header->IndicesHeapOff);
            _stringHeap = _basePtr + _header->StringHeapOff;

            Fingerprint = expectedFingerprint;
        }

        /// <summary>
        /// Performs an O(log N) binary search to find all indices matching the given prefix.
        /// Zero-allocation result merging.
        /// </summary>
        [SkipLocalsInit]
        public void FindByPrefix(ReadOnlySpan<char> prefix, List<int> resultSink, int maxResults = 500)
        {
            if (prefix.IsEmpty || _tokenTable == null) return;

            int low = 0;
            int high = _header->TokenCount - 1;
            int firstMatch = -1;

            // 1. Binary Search to find ANY match
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                var tokenUtf8 = new ReadOnlySpan<byte>(_stringHeap + _tokenTable[mid].NameOff, _tokenTable[mid].NameLen);
                
                Span<char> tokenChars = stackalloc char[_tokenTable[mid].NameLen];
                int charsWritten = Encoding.UTF8.GetChars(tokenUtf8, tokenChars);
                var tokenSpan = tokenChars.Slice(0, charsWritten);

                int cmp = ComparePrefix(prefix, tokenSpan);
                if (cmp == 0)
                {
                    firstMatch = mid;
                    break; 
                }
                
                if (cmp < 0) high = mid - 1;
                else low = mid + 1;
            }

            if (firstMatch == -1) return;

            // 2. Scan BACKWARDS to find the start of the prefix range
            int start = firstMatch;
            while (start > 0)
            {
                var tokenUtf8 = new ReadOnlySpan<byte>(_stringHeap + _tokenTable[start - 1].NameOff, _tokenTable[start - 1].NameLen);
                Span<char> tokenChars = stackalloc char[_tokenTable[start - 1].NameLen];
                int charsWritten = Encoding.UTF8.GetChars(tokenUtf8, tokenChars);
                if (ComparePrefix(prefix, tokenChars.Slice(0, charsWritten)) != 0) break;
                start--;
            }

            // 3. Scan FORWARDS and collect indices
            for (int i = start; i < _header->TokenCount; i++)
            {
                var tokenUtf8 = new ReadOnlySpan<byte>(_stringHeap + _tokenTable[i].NameOff, _tokenTable[i].NameLen);
                Span<char> tokenChars = stackalloc char[_tokenTable[i].NameLen];
                int charsWritten = Encoding.UTF8.GetChars(tokenUtf8, tokenChars);
                if (ComparePrefix(prefix, tokenChars.Slice(0, charsWritten)) != 0) break;

                ref IndexTokenRecord record = ref _tokenTable[i];
                int* indices = _indicesHeap + record.IndicesOff;
                for (int j = 0; j < record.IndicesLen; j++)
                {
                    resultSink.Add(indices[j]);
                    if (resultSink.Count >= maxResults) return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ComparePrefix(ReadOnlySpan<char> prefix, ReadOnlySpan<char> token)
        {
            if (token.Length < prefix.Length)
            {
                return prefix.CompareTo(token, StringComparison.OrdinalIgnoreCase);
            }
            return prefix.CompareTo(token.Slice(0, prefix.Length), StringComparison.OrdinalIgnoreCase);
        }

        [SkipLocalsInit]
        public ReadOnlySpan<int> FindByToken(ReadOnlySpan<char> query)
        {
            if (query.IsEmpty || _tokenTable == null) return ReadOnlySpan<int>.Empty;

            int low = 0;
            int high = _header->TokenCount - 1;

            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                var tokenUtf8 = new ReadOnlySpan<byte>(_stringHeap + _tokenTable[mid].NameOff, _tokenTable[mid].NameLen);
                
                Span<char> tokenChars = stackalloc char[_tokenTable[mid].NameLen];
                int charsWritten = Encoding.UTF8.GetChars(tokenUtf8, tokenChars);
                var tokenSpan = tokenChars.Slice(0, charsWritten);

                int cmp = query.CompareTo(tokenSpan, StringComparison.OrdinalIgnoreCase);
                if (cmp == 0) return new ReadOnlySpan<int>(_indicesHeap + _tokenTable[mid].IndicesOff, _tokenTable[mid].IndicesLen);
                
                if (cmp < 0) high = mid - 1;
                else low = mid + 1;
            }

            return ReadOnlySpan<int>.Empty;
        }


        public void Dispose()
        {
            if (_basePtr != null) _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
            _mmf.Dispose();
        }
    }
}
