using System;

namespace ModernIPTVPlayer.Models.Tmdb
{
    public class TmdbCacheEntry
    {
        public string JsonData { get; set; }
        public DateTime LastUpdated { get; set; }
        // We handle expiration (7 days) logic in service
    }
}
