using System;
using System.Collections.Concurrent;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// A high-performance, thread-safe string interner for metadata fields.
    /// This reduces memory usage by ensuring repetitive strings (e.g. "movie", "mkv", "2024") 
    /// share the same object reference in memory.
    /// </summary>
    public static class FastStringPool
    {
        private static readonly ConcurrentDictionary<string, string> _pool = new(StringComparer.Ordinal);

        /// <summary>
        /// Returns a canonical instance of the specified string.
        /// If the string is already in the pool, returns the existing instance.
        /// If not, adds the string to the pool and returns it.
        /// </summary>
        public static string? Intern(string? s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            
            // Fast path for common short strings often found in IPTV
            if (s.Length <= 1)
            {
                if (s == "0") return "0";
                if (s == "1") return "1";
            }

            return _pool.GetOrAdd(s, s);
        }

        /// <summary>
        /// Clears the pool. Useful when switching playlists to free memory.
        /// </summary>
        public static void Clear()
        {
            _pool.Clear();
        }
    }
}
