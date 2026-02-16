using System;
using System.Text.Json.Serialization;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Models
{
    public class WatchlistItem : IMediaStream
    {
        public string Id { get; set; }
        public string? IMDbId => Id; // For Stremio, Id is the IMDb ID
        public string Title { get; set; }

        [JsonIgnore]
        int IMediaStream.Id => Id?.GetHashCode() ?? 0;

        public string PosterUrl { get; set; }
        public string BackgroundUrl { get; set; }
        public string Description { get; set; }
        public string StreamUrl { get; set; }
        
        public string Year { get; set; }
        public double Rating { get; set; }

        [JsonIgnore]
        string IMediaStream.Rating => Rating.ToString("0.0");

        public string Type { get; set; }
        public DateTime DateAdded { get; set; } = DateTime.Now;
        public StremioMeta StremioMeta { get; set; }

        [JsonIgnore]
        public double ProgressValue { get; set; }
        [JsonIgnore]
        public string BadgeText { get; set; }
        [JsonIgnore]
        public bool ShowProgress => ProgressValue > 0;
        [JsonIgnore]
        public bool ShowBadge => !string.IsNullOrEmpty(BadgeText);

        public string SourceBadgeText { get; set; }
        public bool ShowSourceBadge { get; set; }

        [JsonIgnore]
        public TmdbMovieResult TmdbInfo { get; set; }

        // Metadata Properties (for ExpandedCard compatibility)
        public string Resolution { get; set; }
        public string Codec { get; set; }
        public bool IsHdr { get; set; }
        
        [JsonIgnore]
        public bool IsProbing { get; set; }
        
        [JsonIgnore]
        public bool? IsOnline { get; set; }

        [JsonIgnore]
        public long Bitrate { get; set; }

        [JsonIgnore]
        public string Fps { get; set; }
        
        [JsonIgnore]
        public bool HasMetadata => !string.IsNullOrEmpty(Resolution) || !string.IsNullOrEmpty(Codec);

        public WatchlistItem() { }

        public WatchlistItem(IMediaStream stream)
        {
            Title = stream.Title;
            PosterUrl = stream.PosterUrl;
            
            if (stream is StremioMediaStream s)
            {
                Id = s.IMDbId;
                Type = s.Meta.Type;
                Year = s.Year;
                if(double.TryParse(s.Rating, out double r)) Rating = r;
                BackgroundUrl = s.Banner;
                Description = s.Meta.Description;
                StremioMeta = s.Meta;
            }
            else if (stream is LiveStream l)
            {
                 Id = l.StreamId.ToString();
                 Type = "movie";
                 StreamUrl = l.StreamUrl;
            }
            else if (stream is SeriesStream ss)
            {
                Id = ss.SeriesId.ToString();
                Type = "series";
            }

            SourceBadgeText = stream.SourceBadgeText;
            ShowSourceBadge = stream.ShowSourceBadge;

            // Sync Metadata
            if (stream is LiveStream l2)
            {
                Resolution = l2.Resolution;
                Codec = l2.Codec;
                IsHdr = l2.IsHdr;
            }
            else if (stream is SeriesStream s2)
            {
                Resolution = s2.Resolution;
                Codec = s2.Codec;
                IsHdr = s2.IsHdr;
            }
            else if (stream is StremioMediaStream st2)
            {
                Resolution = st2.Resolution;
                Codec = st2.Codec;
                IsHdr = st2.IsHdr;
            }
            
            if(string.IsNullOrEmpty(Id)) Id = Guid.NewGuid().ToString();
        }

        public StremioMediaStream ToStremioStream()
        {
            if (StremioMeta != null)
            {
                return new StremioMediaStream(StremioMeta);
            }
            return null;
        }
    }
}
