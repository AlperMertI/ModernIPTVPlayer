using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer.Models.Metadata
{
    public class UnifiedMetadata
    {
        public string Title { get; set; }
        public string Overview { get; set; }
        public string PosterUrl { get; set; }
        public string BackdropUrl { get; set; }
        public List<string> BackdropUrls { get; set; } = new List<string>();
        public string LogoUrl { get; set; }
        public double Rating { get; set; }
        public string Year { get; set; }
        public string Runtime { get; set; }
        public string Genres { get; set; }
        public List<string> Cast { get; set; }
        public List<string> Directors { get; set; }
        public string TrailerUrl { get; set; }
        public string ImdbId { get; set; }
        public string MetadataId { get; set; } // e.g. "tt1234567" or Stremio internal ID
        public bool IsSeries { get; set; }
        public TmdbMovieResult TmdbInfo { get; set; } // Optional enrichment

        // Tracking source
        public string DataSource { get; set; } // e.g. "Stremio", "Stremio + TMDB"
        public string MetadataSourceInfo { get; set; } // Detailed attribution (e.g. "AioStreams (TMDB)")

        public List<UnifiedSeason> Seasons { get; set; } = new List<UnifiedSeason>();
        
        [JsonIgnore] 
        public string DurationFormatted => Runtime; // Alias for compatibility
    }

    public class UnifiedSeason
    {
        public int SeasonNumber { get; set; }
        public string Name { get; set; }
        public List<UnifiedEpisode> Episodes { get; set; } = new List<UnifiedEpisode>();
    }

    public class UnifiedEpisode
    {
        public string Id { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string Title { get; set; }
        public string Overview { get; set; }
        public string ThumbnailUrl { get; set; }
        
        // Use DateTime? for proper date handling and comparison
        public System.DateTime? AirDate { get; set; }
        
        public string StreamUrl { get; set; } // For virtual episodes, this might be null until resolved
        
        public string RuntimeFormatted { get; set; } 
    }
}
