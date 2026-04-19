using System;

namespace ModernIPTVPlayer.Models.Common
{
    public class HistoryItem
    {
        public string Id { get; set; } // Movie ID or SeriesID_EpisodeID
        public string Title { get; set; }
        public string StreamUrl { get; set; }
        public double Position { get; set; } // Seconds
        public double Duration { get; set; } // Seconds
        public DateTime Timestamp { get; set; }
        public bool IsFinished { get; set; } // > 95%
        
        public string SeriesName { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        // To track "Next Up", we might need to know the parent Series ID
        public string ParentSeriesId { get; set; }

        public string Type { get; set; } // "movie", "series", etc.
        public string PosterUrl { get; set; }
        public string BackdropUrl { get; set; }

        public string AudioTrackId { get; set; }
        public string SubtitleTrackId { get; set; }
        public string SubtitleTrackUrl { get; set; } // For Addon/External subs
    }
}
