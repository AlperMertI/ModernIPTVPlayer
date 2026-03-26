using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer
{
    public class SeriesStream : IMediaStream, System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            var queue = App.MainWindow?.DispatcherQueue;
            if (queue == null)
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
                return;
            }
            if (queue.HasThreadAccess)
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
            else
            {
                queue.TryEnqueue(() =>
                {
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
                });
            }
        }

        // Metadata for the currently active/probed episode
        private string _resolution = "";
        public string Resolution { get => _resolution; set { _resolution = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMetadata)); } }

        private string _fps = "";
        public string Fps { get => _fps; set { _fps = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMetadata)); } }

        private string _codec = "";
        public string Codec { get => _codec; set { _codec = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMetadata)); } }

        private bool _isHdr = false;
        public bool IsHdr { get => _isHdr; set { _isHdr = value; OnPropertyChanged(); } }

        private bool _isProbing = false;
        public bool IsProbing { get => _isProbing; set { _isProbing = value; OnPropertyChanged(); } }

        public bool HasMetadata => !string.IsNullOrEmpty(Resolution);
        
        private long _bitrate;
        public long Bitrate { get => _bitrate; set { _bitrate = value; OnPropertyChanged(); } }

        private bool? _isOnline;
        public bool? IsOnline { get => _isOnline; set { _isOnline = value; OnPropertyChanged(); } }

        private readonly object _metaLock = new();
        public int MetadataPriority { get; set; } = 0;
        
        // IMediaStream Implementation
        public int Id => SeriesId;

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("tmdb")]
        public string? TmdbIdRaw { get; set; }

        [JsonPropertyName("tmdb_id")]
        public string? TmdbIdAlt { get; set; }

        public string? IMDbId => !string.IsNullOrEmpty(ImdbId) ? ImdbId : (!string.IsNullOrEmpty(TmdbIdRaw) ? TmdbIdRaw : TmdbIdAlt);
        
        public string Title { get => Name; set { if (Name != value) { Name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Name)); } } }
        public string? Description { get => Plot; set { if (Plot != value) { Plot = value; OnPropertyChanged(); OnPropertyChanged(nameof(Plot)); } } }
        public string PosterUrl { get => Cover; set { if (Cover != value) { Cover = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cover)); } } }
        
        private string? _backdropUrl;
        public string? BackdropUrl { get => _backdropUrl; set { if (_backdropUrl != value) { _backdropUrl = value; OnPropertyChanged(); } } }
        public string? Type => "series";

        public string? Genres { get => Genre; set { if (Genre != value) { Genre = value; OnPropertyChanged(); OnPropertyChanged(nameof(Genre)); } } }
        public string? TrailerUrl { get => YoutubeTrailer; set { if (YoutubeTrailer != value) { YoutubeTrailer = value; OnPropertyChanged(); OnPropertyChanged(nameof(YoutubeTrailer)); } } }
        
        string? IMediaStream.Cast { get => Cast; set { if (Cast != value) { Cast = value; OnPropertyChanged(); OnPropertyChanged(nameof(Cast)); } } }
        string? IMediaStream.Director { get => Director; set { if (Director != value) { Director = value; OnPropertyChanged(); OnPropertyChanged(nameof(Director)); } } }

        public string Year 
        { 
            get 
            {
                if (!string.IsNullOrEmpty(_year)) return _year;
                string? dateYear = Helpers.TitleHelper.ExtractYear(ReleaseDate ?? AirDate);
                if (!string.IsNullOrEmpty(dateYear)) return dateYear;
                // Fallback to title extraction for bulk list items
                return Helpers.TitleHelper.ExtractYear(Name) ?? "";
            }
            set => _year = value; 
        }
        private string? _year;
        public string StreamUrl { get; set; } = "";
        
        // IMediaStream.Rating implementation
        [JsonIgnore]
        public string Rating { get => string.IsNullOrEmpty(RatingRaw?.ToString()) || RatingRaw?.ToString() == "N/A" || RatingRaw?.ToString() == "Unknown" ? "" : RatingRaw?.ToString(); set { if (RatingRaw != value) { RatingRaw = value; OnPropertyChanged(); } } }

        // UI Binding Implementation
        public double ProgressValue => 0;
        public bool ShowProgress => false;
        public string BadgeText => "";
        public bool ShowBadge => false;

        public string SourceBadgeText => "IPTV";
        public bool ShowSourceBadge => true;
        public bool IsAvailableOnIptv { get; set; } = true;
        
        [JsonIgnore]
        public TmdbMovieResult TmdbInfo { get; set; }

        [JsonPropertyName("num")]
        public object Num { get; set; } // Sometimes int, sometimes string

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("series_id")]
        public int SeriesId { get; set; }

        [JsonPropertyName("cover")]
        public string? Cover { get; set; }

        [JsonPropertyName("plot")]
        public string? Plot { get; set; }

        [JsonPropertyName("cast")]
        public string? Cast { get; set; }

        [JsonPropertyName("director")]
        public string? Director { get; set; }

        [JsonPropertyName("genre")]
        public string? Genre { get; set; }

        [JsonPropertyName("releaseDate")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("air_date")]
        public string? AirDate { get; set; }

        [JsonPropertyName("last_modified")]
        public string? LastModified { get; set; }

        [JsonPropertyName("rating")]
        public object? RatingRaw { get; set; }

        [JsonPropertyName("rating_5based")]
        public object Rating5Based { get; set; }

        [JsonPropertyName("backdrop_path")]
        public object[]? BackdropPath { get; set; }

        [JsonPropertyName("youtube_trailer")]
        public string? YoutubeTrailer { get; set; }

        [JsonPropertyName("episode_run_time")]
        public string? EpisodeRunTime { get; set; }

        [JsonPropertyName("category_id")]
        public string? CategoryId { get; set; }

        // Helper for UI binding
        [JsonIgnore]
        public BitmapImage? CoverImage
        {
            get
            {
                if (string.IsNullOrEmpty(Cover)) return null;
                return null; // SAFE: Always return null on background, or use a Converter for UI. 
            }
        }
        public void UpdateFromUnified(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata unified)
        {
            if (unified == null) return;
            
            lock (_metaLock)
            {
                bool isDowngrade = unified.PriorityScore < this.MetadataPriority;
                Models.Metadata.MetadataSync.Sync(this, unified, isDowngrade);
                
                if (!isDowngrade)
                {
                    this.MetadataPriority = unified.PriorityScore;
                }
            }
        }
    }
}
