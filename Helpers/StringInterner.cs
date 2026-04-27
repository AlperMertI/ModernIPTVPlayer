using System;
using System.Collections.Concurrent;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// PROJECT ZERO: High-performance string interner for dynamic metadata.
    /// Prevents RAM pollution by deduplicating strings like "1920x1080", "h264", etc.
    /// Unlike string.Intern, this can be cleared to prevent leak across sessions.
    /// </summary>
    public static class StringInterner
    {
        private static readonly ConcurrentDictionary<string, string> _pool = new();
        private const int MAX_ENTRIES = 10000;

        public static string Intern(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Length > 64) return value; // Don't intern long unique strings

            if (_pool.Count > MAX_ENTRIES) _pool.Clear();

            return _pool.GetOrAdd(value, value);
        }

        public static void Clear() => _pool.Clear();
    }
}
