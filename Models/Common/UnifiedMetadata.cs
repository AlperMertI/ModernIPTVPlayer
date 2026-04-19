using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ModernIPTVPlayer;

namespace ModernIPTVPlayer.Models.Metadata
{
    public class UnifiedMetadata
    {
        private readonly System.Threading.Lock _lock = new();
        [JsonIgnore]
        public object SyncRoot => _lock;

        public string Title { get; set; }
        public string SourceTitle { get; set; } // [NEW] Original IPTV/Source title
        public string OriginalTitle { get; set; } // Feature #OriginalTitleForMovies
        public string SubTitle { get; set; } // Secondary title (e.g. English title when Turkish is primary)
        public string Overview { get; set; }
        public string PosterUrl { get; set; }
        public string BackdropUrl { get; set; }
        public List<string> BackdropUrls { get; set; } = new List<string>();
        public string LogoUrl { get; set; }
        public double Rating { get; set; }
        public string Year { get; set; }
        public string Runtime { get; set; }
        public string AgeRating { get; set; } // [NEW] Age rating (MPAA, etc.)
        public string Country { get; set; } // [NEW] Production country
        public string Writers { get; set; } // [NEW] Writers (comma-separated)
        public string Status { get; set; } // [NEW] Series status (e.g. "Continuing", "Ended")
        public string Certification { get; set; } // [NEW] Age certification (e.g. "TV-MA", "R")
        public string Genres { get; set; }

        // [NEW] Technical Metadata from Source
        public string Resolution { get; set; }
        public string VideoCodec { get; set; }
        public string AudioCodec { get; set; }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }
        public bool? IsOnline { get; set; }

        public List<UnifiedCast> Cast { get; set; } = new List<UnifiedCast>();
        public List<UnifiedCast> Directors { get; set; } = new List<UnifiedCast>();
        public string TrailerUrl { get; set; }
        public List<string> TrailerCandidates { get; set; } = new List<string>();
        public string ImdbId { get; set; }
        public string MetadataId { get; set; } // e.g. "tt1234567" or Stremio internal ID
        public bool IsSeries { get; set; }
        public TmdbMovieResult TmdbInfo { get; set; } // Optional enrichment
        public MetadataContext MaxEnrichmentContext { get; set; } // [NEW] Track highest successful enrichment level
        public int PriorityScore { get; set; } // [NEW] Rule-based priority score

        // [NEW] IPTV Integration Properties
        public string StreamUrl { get; set; }
        public bool IsAvailableOnIptv { get; set; }
        public bool IsFromIptv { get; set; }
        public List<VodStream> IptvVods { get; set; } = new List<VodStream>();
        public List<SeriesStream> IptvSeries { get; set; } = new List<SeriesStream>();

        // Tracking source
        public string DataSource { get; set; } // e.g. "Stremio", "Stremio + TMDB"
        public string MetadataSourceInfo { get; set; } // Detailed attribution (e.g. "AioStreams (TMDB)")
        public string CatalogSourceInfo { get; set; }
        public string CatalogSourceAddonUrl { get; set; }
        public string PrimaryMetadataAddonUrl { get; set; }

        public List<UnifiedSeason> Seasons { get; set; } = new List<UnifiedSeason>();
        
        [JsonIgnore]
        public HashSet<string> ProbedAddons { get; set; } = new HashSet<string>();

        [JsonIgnore] 
        public string DurationFormatted => Runtime; // Alias for compatibility

        /// <summary>
        /// Highly-performant factory method to create a unified seed from any stream object.
        /// This ensures the UI has a consistent model to render at 0ms, even before network enrichment.
        /// </summary>
        public static UnifiedMetadata FromStream(IMediaStream stream)
        {
            if (stream == null) return null;

            var meta = new UnifiedMetadata
            {
                Title = stream.Title,
                Overview = stream.Description,
                PosterUrl = stream.PosterUrl,
                BackdropUrl = stream.BackdropUrl ?? stream.PosterUrl,
                Year = stream.Year,
                Genres = stream.Genres,
                MetadataId = stream.Id.ToString(),
                ImdbId = stream.IMDbId,
                IsAvailableOnIptv = stream.IsAvailableOnIptv,
                DataSource = "Stream Seed",
                IsFromIptv = stream is VodStream || stream is SeriesStream,
                SourceTitle = stream.SourceTitle
            };

            // Map Rating with InvariantCulture
            if (double.TryParse(stream.Rating, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
                meta.Rating = r;

            // Map Cast if available
            if (!string.IsNullOrEmpty(stream.Cast))
            {
                meta.Cast = stream.Cast.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(s => new UnifiedCast { Name = s.Trim() }).ToList();
            }

            // Map Director if available
            if (!string.IsNullOrEmpty(stream.Director))
            {
                meta.Directors = stream.Director.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                .Select(s => new UnifiedCast { Name = s.Trim(), Character = "YÃ¶netmen" }).ToList();
            }

            // Map Technical Data
            meta.Resolution = stream.Resolution;
            meta.VideoCodec = stream.Codec;

            return meta;
        }
    }

    public class UnifiedSeason
    {
        public int SeasonNumber { get; set; }
        public string Name { get; set; }
        public string PosterUrl { get; set; } // [NEW] Season poster
        public List<UnifiedEpisode> Episodes { get; set; } = new List<UnifiedEpisode>();
        public bool IsEnrichedByTmdb { get; set; }
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
        public System.DateTime? Releasedate { get; set; } // [NEW] Alias for AirDate compatibility
        public bool IsAvailable { get; set; } = true; // [NEW] From AIOMetadata videos[].available
        public string Runtime { get; set; } // [NEW] Per-episode runtime

        public string StreamUrl { get; set; } // For virtual episodes, this might be null until resolved
        
        public string RuntimeFormatted { get; set; } 
        
        // [NEW] Technical Metadata
        public string Resolution { get; set; }
        public string VideoCodec { get; set; }
        public string AudioCodec { get; set; }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }
        public string IptvSourceTitle { get; set; } // [NEW] Store the IPTV-specific title for source panel naming
        public int IptvSeriesId { get; set; } // [NEW] Store the IPTV series ID for deduplication
    }

    public class UnifiedCast
    {
        public string Name { get; set; }
        public string Character { get; set; }
        public string ProfileUrl { get; set; }
        public int? TmdbId { get; set; } // [NEW] TMDB person ID for direct enrichment
    }
}
