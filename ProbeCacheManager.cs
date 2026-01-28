using System.Collections.Concurrent;

namespace ModernIPTVPlayer
{
    public class ProbeResult
    {
        public string Res { get; set; }
        public string Fps { get; set; }
        public string Codec { get; set; }
        public long Bitrate { get; set; }
        public bool Success { get; set; }
        public bool IsHdr { get; set; }
    }

    public static class ProbeCacheManager
    {
        private static readonly ConcurrentDictionary<string, ProbeResult> _cache = new();
        
        public static void Cache(string url, ProbeResult result)
        {
            if (string.IsNullOrEmpty(url)) return;
            _cache[url] = result;
        }

        public static bool TryGet(string url, out ProbeResult result)
        {
            if (string.IsNullOrEmpty(url))
            {
                result = null;
                return false;
            }
            return _cache.TryGetValue(url, out result);
        }
    }
}
