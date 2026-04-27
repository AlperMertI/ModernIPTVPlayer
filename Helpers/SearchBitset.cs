using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Pinnacle: A stack-allocated bitset optimized for SIMD intersections.
    /// Manages up to 131,072 channels in 16KB of stack space.
    /// </summary>
    public unsafe ref struct SearchBitset
    {
        public const int MaxChannels = 262144;
        private const int UintCount = MaxChannels / 32;
        
        private fixed uint _bits[UintCount];
        
        // PERFORMANCE: Dirty tracking to avoid clearing the entire 32KB buffer.
        private int _minDirty = UintCount;
        private int _maxDirty = -1;

        // C# requires an explicit constructor when using field initializers in structs.
        public SearchBitset() 
        {
            fixed (uint* p = _bits)
            {
                Unsafe.InitBlock(p, 0, UintCount * sizeof(uint));
            }
            _minDirty = UintCount;
            _maxDirty = -1;
        }

        public void Clear()
        {
            if (_maxDirty < 0) return;

            fixed (uint* p = _bits)
            {
                // Sparse clearing: Only zero the words that were actually touched.
                int count = (_maxDirty - _minDirty) + 1;
                Unsafe.InitBlock(p + _minDirty, 0, (uint)count * sizeof(uint));
            }
            
            _minDirty = UintCount;
            _maxDirty = -1;
        }

        public void SetAll(int channelCount)
        {
            int fullUints = channelCount / 32;
            fixed (uint* p = _bits)
            {
                Unsafe.InitBlock(p, 0xFF, (uint)fullUints * sizeof(uint));
                if (channelCount % 32 != 0)
                {
                    p[fullUints] = (1u << (channelCount % 32)) - 1;
                }
            }
            _minDirty = 0;
            _maxDirty = UintCount - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(int index)
        {
            int wordIdx = index >> 5;
            fixed (uint* p = _bits)
            {
                p[wordIdx] |= 1u << (index & 31);
            }
            if (wordIdx < _minDirty) _minDirty = wordIdx;
            if (wordIdx > _maxDirty) _maxDirty = wordIdx;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSet(int index)
        {
            fixed (uint* p = _bits)
            {
                return (p[index >> 5] & (1u << (index & 31))) != 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetRange(int* indices, int count)
        {
            fixed (uint* p = _bits)
            {
                for (int i = 0; i < count; i++)
                {
                    int idx = indices[i];
                    if ((uint)idx < MaxChannels)
                    {
                        int wordIdx = idx >> 5;
                        p[wordIdx] |= 1u << (idx & 31);
                        if (wordIdx < _minDirty) _minDirty = wordIdx;
                        if (wordIdx > _maxDirty) _maxDirty = wordIdx;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetRange(ReadOnlySpan<int> indices)
        {
            fixed (uint* p = _bits)
            {
                foreach (int idx in indices)
                {
                    if ((uint)idx < MaxChannels)
                    {
                        int wordIdx = idx >> 5;
                        p[wordIdx] |= 1u << (idx & 31);
                        if (wordIdx < _minDirty) _minDirty = wordIdx;
                        if (wordIdx > _maxDirty) _maxDirty = wordIdx;
                    }
                }
            }
        }

        /// <summary>
        /// SIMD-Accelerated Bitwise AND (Intersection) with another bitset.
        /// </summary>
        public void Intersect(ref SearchBitset other)
        {
            fixed (uint* pTarget = _bits)
            fixed (uint* pSource = other._bits)
            {
                int i = 0;
                
                if (Vector512.IsHardwareAccelerated)
                {
                    for (; i <= UintCount - Vector512<uint>.Count; i += Vector512<uint>.Count)
                    {
                        var v1 = Vector512.Load(pTarget + i);
                        var v2 = Vector512.Load(pSource + i);
                        (v1 & v2).Store(pTarget + i);
                    }
                }
                
                if (Vector256.IsHardwareAccelerated)
                {
                    for (; i <= UintCount - Vector256<uint>.Count; i += Vector256<uint>.Count)
                    {
                        var v1 = Vector256.Load(pTarget + i);
                        var v2 = Vector256.Load(pSource + i);
                        (v1 & v2).Store(pTarget + i);
                    }
                }

                for (; i < UintCount; i++)
                {
                    pTarget[i] &= pSource[i];
                }
            }
            
            // Re-calculate dirty range after intersection (it can only shrink or stay same)
            // For simplicity and correctness, we keep it as is or we can refine it.
            // But we must ensure it doesn't exceed the other's dirty range if we want to be tight.
            if (other._minDirty > _minDirty) _minDirty = other._minDirty;
            if (other._maxDirty < _maxDirty) _maxDirty = other._maxDirty;
            if (_minDirty > _maxDirty) { _minDirty = UintCount; _maxDirty = -1; }
        }

        /// <summary>
        /// SIMD-Accelerated Bitwise AND (Intersection) with a list of indices.
        /// </summary>
        public void Intersect(ReadOnlySpan<int> indices)
        {
            // For small result sets, we might want to clear and rebuild
            // For large ones, we'd need a temporary bitset.
            // Simplified for this phase:
            Span<uint> temp = stackalloc uint[UintCount];
            temp.Clear();
            foreach (var idx in indices)
            {
                if (idx >= 0 && idx < MaxChannels)
                    temp[idx >> 5] |= 1u << (idx & 31);
            }

            fixed (uint* pTarget = _bits)
            fixed (uint* pSource = temp)
            {
                int i = 0;
                
                // Vector512 (AVX-512)
                if (Vector512.IsHardwareAccelerated)
                {
                    for (; i <= UintCount - Vector512<uint>.Count; i += Vector512<uint>.Count)
                    {
                        var v1 = Vector512.Load(pTarget + i);
                        var v2 = Vector512.Load(pSource + i);
                        (v1 & v2).Store(pTarget + i);
                    }
                }
                
                // Vector256 (AVX-2)
                if (Vector256.IsHardwareAccelerated)
                {
                    for (; i <= UintCount - Vector256<uint>.Count; i += Vector256<uint>.Count)
                    {
                        var v1 = Vector256.Load(pTarget + i);
                        var v2 = Vector256.Load(pSource + i);
                        (v1 & v2).Store(pTarget + i);
                    }
                }

                // Scalar fallback
                for (; i < UintCount; i++)
                {
                    pTarget[i] &= pSource[i];
                }
            }
            
            // Intersecting with a fixed list means dirty range is now at most what we had
            // but we don't have dirty range for the indices span. 
            // So we stay conservative but safe.
        }

        /// <summary>
        /// Fills the sink with indices of set bits.
        /// Uses bit-manipulation intrinsics (Tzcnt) for speed.
        /// </summary>
        public int FillIndices(Span<int> sink)
        {
            int count = 0;
            fixed (uint* p = _bits)
            {
                for (int i = 0; i < UintCount; i++)
                {
                    uint val = p[i];
                    while (val != 0)
                    {
                        if (count >= sink.Length) return count;
                        int bitIdx = BitOperations.TrailingZeroCount(val);
                        sink[count++] = (i << 5) | bitIdx;
                        val &= ~(1u << bitIdx);
                    }
                }
            }
            return count;
        }
        /// <summary>
        /// Checks if no bits are set. Used for short-circuiting searches.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEmpty()
        {
            fixed (uint* p = _bits)
            {
                int i = 0;
                if (Vector512.IsHardwareAccelerated)
                {
                    for (; i <= UintCount - Vector512<uint>.Count; i += Vector512<uint>.Count)
                    {
                        if (Vector512.Load(p + i) != Vector512<uint>.Zero) return false;
                    }
                }
                if (Vector256.IsHardwareAccelerated)
                {
                    for (; i <= UintCount - Vector256<uint>.Count; i += Vector256<uint>.Count)
                    {
                        if (Vector256.Load(p + i) != Vector256<uint>.Zero) return false;
                    }
                }
                for (; i < UintCount; i++)
                {
                    if (p[i] != 0) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// SIMD-Accelerated Bitwise OR (Union) with another bitset.
        /// </summary>
        public void Or(ref SearchBitset other)
        {
            fixed (uint* pTarget = _bits)
            fixed (uint* pSource = other._bits)
            {
                int i = 0;
                if (Vector512.IsHardwareAccelerated)
                {
                    for (; i <= UintCount - Vector512<uint>.Count; i += Vector512<uint>.Count)
                    {
                        var v1 = Vector512.Load(pTarget + i);
                        var v2 = Vector512.Load(pSource + i);
                        (v1 | v2).Store(pTarget + i);
                    }
                }
                if (Vector256.IsHardwareAccelerated)
                {
                    for (; i <= UintCount - Vector256<uint>.Count; i += Vector256<uint>.Count)
                    {
                        var v1 = Vector256.Load(pTarget + i);
                        var v2 = Vector256.Load(pSource + i);
                        (v1 | v2).Store(pTarget + i);
                    }
                }
                for (; i < UintCount; i++)
                {
                    pTarget[i] |= pSource[i];
                }
            }
            
            if (other._minDirty < _minDirty) _minDirty = other._minDirty;
            if (other._maxDirty > _maxDirty) _maxDirty = other._maxDirty;
        }

        /// <summary>
        /// Returns the number of set bits (population count) using SIMD or intrinsics.
        /// </summary>
        public int CountSetBits()
        {
            int count = 0;
            fixed (uint* p = _bits)
            {
                int i = 0;
                // SIMD PopCount is only in .NET 8+ and specific CPUs.
                // Standard BitOperations.PopCount is very fast (hardware instr).
                for (; i < UintCount; i++)
                {
                    count += BitOperations.PopCount(p[i]);
                }
            }
            return count;
        }
    }
}
