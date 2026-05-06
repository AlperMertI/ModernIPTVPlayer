using MessagePack;
using System.Collections.Generic;

namespace ModernIPTVPlayer.Models.Common
{
    /// <summary>
    /// Represents a high-performance binary snapshot of a Stremio catalog.
    /// Optimized for MessagePack serialization.
    /// </summary>
    [MessagePackObject]
    public sealed class CatalogCacheDTO
    {
        [Key(0)]
        public string ETag { get; set; } = string.Empty;
        
        [Key(1)]
        public long Timestamp { get; set; }
        
        [Key(2)]
        public List<MediaItemDTO> Items { get; set; } = new();
    }

    /// <summary>
    /// A robust, pure-data representation of a media item for caching.
    /// Decoupled from the complex StremioMediaStream UI model to ensure 
    /// zero-allocation serialization and binary stability.
    /// </summary>
    [MessagePackObject]
    public sealed class MediaItemDTO
    {
        // Core Identity
        [Key(0)] public string Id { get; set; } = string.Empty;
        [Key(1)] public string Title { get; set; } = string.Empty;
        
        // Visual Assets
        [Key(2)] public string Poster { get; set; } = string.Empty;
        [Key(3)] public string Background { get; set; } = string.Empty;
        [Key(4)] public string Logo { get; set; } = string.Empty;
        
        // General Metadata
        [Key(5)] public string Type { get; set; } = string.Empty;
        [Key(6)] public string Year { get; set; } = string.Empty;
        [Key(7)] public double Rating { get; set; }
        [Key(8)] public string Overview { get; set; } = string.Empty;
        [Key(9)] public string Genres { get; set; } = string.Empty;
        [Key(10)] public string Trailer { get; set; } = string.Empty;
        
        // Technical Stream Info (Used for Discovery Badges)
        [Key(11)] public string Resolution { get; set; } = string.Empty;
        [Key(12)] public string Codec { get; set; } = string.Empty;
        [Key(13)] public bool IsHdr { get; set; }
        [Key(14)] public long Bitrate { get; set; }
        [Key(15)] public string Fps { get; set; } = string.Empty;

        // Origin and State
        [Key(16)] public string SourceAddon { get; set; } = string.Empty;
        [Key(17)] public int Progress { get; set; }
        [Key(18)] public bool IsAvailableOnIptv { get; set; }
        [Key(19)] public bool IsIptv { get; set; }
        [Key(20)] public string SeriesName { get; set; } = string.Empty;
        [Key(21)] public string EpisodeSubtext { get; set; } = string.Empty;
    }
}
