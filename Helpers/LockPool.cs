using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Senior-level high-performance striped locking pool.
    /// Provides 1024 static Lock instances to eliminate per-object lock overhead for massive stream collections.
    /// Reduces RAM by ~1.3MB for 165k items by removing individual lock pointers.
    /// </summary>
    public static class LockPool
    {
        // 1024 locks provide an excellent balance between memory (32KB total) and contention risk.
        private const int PoolSize = 1024;
        private static readonly Lock[] Locks;

        static LockPool()
        {
            Locks = new Lock[PoolSize];
            for (int i = 0; i < PoolSize; i++)
            {
                Locks[i] = new Lock();
            }
        }

        /// <summary>
        /// Retrieves a stable lock for a specific stream ID.
        /// Guaranteed to return the same lock instance for the same ID.
        /// </summary>
        /// <param name="streamId">The unique ID of the stream (VOD, Live, or Series).</param>
        /// <returns>A System.Threading.Lock instance for synchronization.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Lock GetLock(string? streamId)
        {
            if (string.IsNullOrEmpty(streamId)) return Locks[0];
            
            // Use a simple and fast hash-based mapping
            uint hash = (uint)streamId.GetHashCode(StringComparison.OrdinalIgnoreCase);
            return Locks[hash % PoolSize];
        }

        /// <summary>
        /// Retrieves a stable lock for an integer-based index/ID.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Lock GetLock(int id)
        {
            return Locks[(uint)id % PoolSize];
        }
    }
}
