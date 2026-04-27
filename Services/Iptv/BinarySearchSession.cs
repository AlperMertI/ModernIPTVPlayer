using System;
using System.IO;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Helpers;
using System.Diagnostics;

namespace ModernIPTVPlayer.Services.Iptv
{
    /// <summary>
    /// PINNACLE: Memory-Mapped Search Engine V4.
    /// Operates directly on unmanaged memory with ZERO-ALLOCATION search pipeline.
    /// Uses SIMD intersections and Trigram indices for sub-millisecond performance.
    /// </summary>
    public unsafe sealed class BinarySearchSession : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly byte* _basePtr = null;
        private readonly IndexHeader* _header = null;
        private readonly IndexTokenRecord* _tokenTable = null;
        private readonly TrigramRecord* _trigramTable = null;
        private readonly int* _indicesHeap = null;
        private readonly byte* _stringHeap = null;

        public string Fingerprint { get; }
        public int TokenCount => _header != null ? _header->TokenCount : 0;
        public int TrigramCount => _header != null ? _header->TrigramCount : 0;

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

            if (_header->Magic != IndexHeader.V4Magic)
            {
                throw new InvalidDataException($"Invalid index magic (Pinnacle V4 required). Found: 0x{_header->Magic:X}");
            }

            _tokenTable = (IndexTokenRecord*)(_basePtr + _header->TokenTableOff);
            _trigramTable = (TrigramRecord*)(_basePtr + _header->TrigramTableOff);
            _indicesHeap = (int*)(_basePtr + _header->IndicesHeapOff);
            _stringHeap = _basePtr + _header->StringHeapOff;

            Fingerprint = expectedFingerprint;
        }

        /// <summary>
        /// Executes a multi-term search with ZERO-ALLOCATION.
        /// Uses Dynamic Mode: Sorted List intersection for small results, SIMD Bitset for large ones.
        /// </summary>
        [SkipLocalsInit]
        public int Search(ReadOnlySpan<char> query, Span<int> resultsSink, int totalChannelCount)
        {
            if (query.IsEmpty) return 0;
            
            long tStart = Stopwatch.GetTimestamp();
            long tNorm = 0, tPrefix = 0, tTrigram = 0, tBitset = 0, tLoop = 0;
            long tMove = 0, tClear = 0, tUtf8 = 0, tEmpty = 0;

            // 1. Normalize Query (Pinnacle V4 Optimized)
            long sw = Stopwatch.GetTimestamp();
            Span<char> normalized = stackalloc char[query.Length + 16];
            int normLen = TitleHelper.NormalizeForSearch(query, normalized);
            normalized = normalized[..normLen];
            tNorm = Stopwatch.GetTimestamp() - sw;

            // 3. Dynamic Search Context
            Span<int> listResults = stackalloc int[1024];
            int listCount = -1;

            var bitset = new SearchBitset();
            var tokenBitset = new SearchBitset(); // Reusable bitset slot on stack

            bool isUsingBitset = false;
            bool hasTerms = false;

            // 4. Process Loop
            long swInit = Stopwatch.GetTimestamp();
            var iterator = TitleHelper.GetTokens(normalized);
            long tInit = Stopwatch.GetTimestamp() - swInit;

            long swLoop = Stopwatch.GetTimestamp();
            Span<byte> utf8Buf = stackalloc byte[256]; 
            
            long swCurrent = Stopwatch.GetTimestamp();
            while (true)
            {
                // Measure MoveNext
                if (!iterator.MoveNext()) 
                {
                    tMove += (Stopwatch.GetTimestamp() - swCurrent);
                    break; 
                }
                tMove += (Stopwatch.GetTimestamp() - swCurrent);
                
                var token = iterator.Current;
                hasTerms = true;
                
                // Measure Clear
                swCurrent = Stopwatch.GetTimestamp();
                tokenBitset.Clear(); 
                tClear += (Stopwatch.GetTimestamp() - swCurrent);

                // Measure UTF8
                swCurrent = Stopwatch.GetTimestamp();
                int tokenUtf8Len = Encoding.UTF8.GetBytes(token, utf8Buf);
                tUtf8 += (Stopwatch.GetTimestamp() - swCurrent);

                // Measure Prefix
                swCurrent = Stopwatch.GetTimestamp();
                CollectPrefixMatches(utf8Buf[..tokenUtf8Len], ref tokenBitset);
                tPrefix += (Stopwatch.GetTimestamp() - swCurrent);

                // Measure Trigram
                swCurrent = Stopwatch.GetTimestamp();
                if (token.Length >= 3) CollectTrigramMatches(token, ref tokenBitset);
                tTrigram += (Stopwatch.GetTimestamp() - swCurrent);

                // Measure IsEmpty
                swCurrent = Stopwatch.GetTimestamp();
                bool isEmpty = tokenBitset.IsEmpty();
                tEmpty += (Stopwatch.GetTimestamp() - swCurrent);

                // Measure Bitset Logic
                swCurrent = Stopwatch.GetTimestamp();
                if (isEmpty)
                {
                    listCount = 0;
                    isUsingBitset = false;
                    tBitset += (Stopwatch.GetTimestamp() - swCurrent);
                    break;
                }

                if (listCount == -1)
                {
                    listCount = tokenBitset.FillIndices(listResults);
                    if (listCount >= listResults.Length)
                    {
                        bitset.Clear();
                        bitset.Or(ref tokenBitset);
                        isUsingBitset = true;
                    }
                }
                else if (isUsingBitset)
                {
                    bitset.Intersect(ref tokenBitset);
                    if (bitset.IsEmpty()) 
                    { 
                        isUsingBitset = false; 
                        listCount = 0; 
                        tBitset += (Stopwatch.GetTimestamp() - swCurrent);
                        break; 
                    }
                }
                else
                {
                    int newCount = 0;
                    for (int i = 0; i < listCount; i++)
                    {
                        if (tokenBitset.IsSet(listResults[i]))
                            listResults[newCount++] = listResults[i];
                    }
                    listCount = newCount;
                    if (listCount == 0) 
                    {
                        tBitset += (Stopwatch.GetTimestamp() - swCurrent);
                        break;
                    }
                }
                tBitset += (Stopwatch.GetTimestamp() - swCurrent);
                
                // Prepare for next iteration's MoveNext
                swCurrent = Stopwatch.GetTimestamp();
            }
            tLoop = Stopwatch.GetTimestamp() - swLoop;

            int finalCount = 0;
            if (hasTerms)
            {
                long swF = Stopwatch.GetTimestamp();
                if (isUsingBitset)
                {
                    finalCount = bitset.FillIndices(resultsSink);
                }
                else if (listCount > 0)
                {
                    finalCount = Math.Min(listCount, resultsSink.Length);
                    listResults.Slice(0, finalCount).CopyTo(resultsSink);
                }
                tBitset += (Stopwatch.GetTimestamp() - swF);
            }

            double totalUs = Stopwatch.GetElapsedTime(tStart).TotalMilliseconds * 1000;
            double normUs = (double)tNorm / Stopwatch.Frequency * 1_000_000;
            double initUs = (double)tInit / Stopwatch.Frequency * 1_000_000;
            double loopUs = (double)tLoop / Stopwatch.Frequency * 1_000_000;
            double moveUs = (double)tMove / Stopwatch.Frequency * 1_000_000;
            double clearUs = (double)tClear / Stopwatch.Frequency * 1_000_000;
            double utfUs = (double)tUtf8 / Stopwatch.Frequency * 1_000_000;
            double emptyUs = (double)tEmpty / Stopwatch.Frequency * 1_000_000;
            double prefixUs = (double)tPrefix / Stopwatch.Frequency * 1_000_000;
            double trigramUs = (double)tTrigram / Stopwatch.Frequency * 1_000_000;
            double bitsetUs = (double)tBitset / Stopwatch.Frequency * 1_000_000;

            AppLogger.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture, 
                $"[PERF] [Pinnacle] Query: '{query.ToString()}' | Results: {finalCount} | Total: {totalUs:N1}us\n" +
                $"[INIT] Norm: {normUs:N1}us | IteratorInit: {initUs:N1}us | Loop: {loopUs:N1}us\n" +
                $"[LOOP DETAIL] Move: {moveUs:N1}us | Clear: {clearUs:N1}us | UTF8: {utfUs:N1}us | EmptyCheck: {emptyUs:N1}us | P:{prefixUs:N1} T:{trigramUs:N1} B:{bitsetUs:N1}"));
            
            return finalCount;
        }

        [SkipLocalsInit]
        private void CollectPrefixMatches(ReadOnlySpan<byte> querySpan, ref SearchBitset sink)
        {
            int low = 0;
            int high = _header->TokenCount - 1;

            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                var entry = _tokenTable[mid];
                var entrySpan = new ReadOnlySpan<byte>(_stringHeap + entry.NameOff, entry.NameLen);

                int cmp = querySpan.SequenceCompareTo(entrySpan.Slice(0, Math.Min(querySpan.Length, entrySpan.Length)));
                
                if (cmp == 0)
                {
                    // Found a match, scan range (Tokens are sorted)
                    int start = mid;
                    while (start > 0 && IsPrefix(querySpan, _stringHeap + _tokenTable[start - 1].NameOff, _tokenTable[start - 1].NameLen)) start--;
                    
                    for (int i = start; i < _header->TokenCount; i++)
                    {
                        var rec = _tokenTable[i];
                        if (!IsPrefix(querySpan, _stringHeap + rec.NameOff, rec.NameLen)) break;
                        
                        // Bulk-populate bitset from index heap (Zero-Allocation)
                        int* indices = _indicesHeap + rec.IndicesOff;
                        sink.SetRange(indices, rec.IndicesLen);
                    }
                    return;
                }

                if (cmp < 0) high = mid - 1;
                else low = mid + 1;
            }
        }

        [SkipLocalsInit]
        private unsafe void CollectTrigramMatches(ReadOnlySpan<char> token, ref SearchBitset sink)
        {
            // Extract trigrams from token
            Span<uint> trigrams = stackalloc uint[token.Length];
            int trigramCount = TitleHelper.GetTrigrams(token, trigrams);
            if (trigramCount == 0) return;

            // PERFORMANCE: Selectivity-based intersection
            int shortestIdx = -1;
            int shortestLen = int.MaxValue;
            TrigramList* lists = stackalloc TrigramList[trigramCount];

            for (int t = 0; t < trigramCount; t++)
            {
                int entryIdx = FindTrigramEntryIndex(trigrams[t]);
                if (entryIdx < 0) return; 

                var entry = _trigramTable[entryIdx];
                lists[t] = new TrigramList { Ptr = _indicesHeap + entry.IndicesOff, Len = entry.IndicesLen };

                if (lists[t].Len < shortestLen)
                {
                    shortestLen = lists[t].Len;
                    shortestIdx = t;
                }
            }

            // 2. Build local match set using a temporary bitset (stack-allocated once)
            var tokenMatch = new SearchBitset(); // Zero-initialized by constructor now
            tokenMatch.SetRange(lists[shortestIdx].Ptr, lists[shortestIdx].Len);

            for (int t = 0; t < trigramCount; t++)
            {
                if (t == shortestIdx) continue;

                var otherBitset = new SearchBitset();
                otherBitset.SetRange(lists[t].Ptr, lists[t].Len);
                tokenMatch.Intersect(ref otherBitset);
                if (tokenMatch.IsEmpty()) break;
            }

            // 4. Merge into master sink
            sink.Or(ref tokenMatch);
        }

        private int FindTrigramEntryIndex(uint hash)
        {
            int low = 0;
            int high = _header->TrigramCount - 1;

            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                var entry = _trigramTable[mid];
                if (entry.TrigramHash == hash) return mid;
                if (entry.TrigramHash < hash) low = mid + 1;
                else high = mid - 1;
            }
            return -1;
        }

        private ReadOnlySpan<int> FindTrigramIndices(uint hash)
        {
            int idx = FindTrigramEntryIndex(hash);
            if (idx < 0) return ReadOnlySpan<int>.Empty;
            var entry = _trigramTable[idx];
            return new ReadOnlySpan<int>(_indicesHeap + entry.IndicesOff, entry.IndicesLen);
        }

        /// <summary>
        /// Performs an O(log N) binary search for an exact token match.
        /// Zero-allocation.
        /// </summary>
        [SkipLocalsInit]
        public ReadOnlySpan<int> FindByToken(ReadOnlySpan<char> token)
        {
            if (token.IsEmpty || _tokenTable == null) return ReadOnlySpan<int>.Empty;

            Span<byte> tokenUtf8 = stackalloc byte[token.Length * 3];
            int utf8Len = Encoding.UTF8.GetBytes(token, tokenUtf8);
            return FindByToken(tokenUtf8[..utf8Len]);
        }

        public ReadOnlySpan<int> FindByToken(ReadOnlySpan<byte> querySpan)
        {
            if (querySpan.IsEmpty || _tokenTable == null) return ReadOnlySpan<int>.Empty;

            int low = 0;
            int high = _header->TokenCount - 1;

            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                var entry = _tokenTable[mid];
                var entrySpan = new ReadOnlySpan<byte>(_stringHeap + entry.NameOff, entry.NameLen);

                int cmp = querySpan.SequenceCompareTo(entrySpan);
                if (cmp == 0) return new ReadOnlySpan<int>(_indicesHeap + entry.IndicesOff, entry.IndicesLen);
                
                if (cmp < 0) high = mid - 1;
                else low = mid + 1;
            }

            return ReadOnlySpan<int>.Empty;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsPrefix(ReadOnlySpan<byte> query, byte* entryPtr, int entryLen)
        {
            if (entryLen < query.Length) return false;
            return query.SequenceEqual(new ReadOnlySpan<byte>(entryPtr, query.Length));
        }

        public void Dispose()
        {
            if (_basePtr != null) _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
            _mmf.Dispose();
        }

        private unsafe struct TrigramList { public int* Ptr; public int Len; }
    }
}
