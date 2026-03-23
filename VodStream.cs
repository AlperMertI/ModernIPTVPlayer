using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer
{
    public class VodStream : INotifyPropertyChanged, IMediaStream
    {
        // IMediaStream Implementation
        public int Id => StreamId;
        
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }
        public string? IMDbId => ImdbId;
        public string Title => Name;
        public string? Description => null;
        public string PosterUrl => IconUrl;
        public string? BackdropUrl => null;
        public string? Type => "movie";
        public string StreamUrl { get; set; } = "";

        [JsonPropertyName("rating")]
        public object? RatingRaw { get; set; }

        [JsonIgnore]
        public string Rating => RatingRaw?.ToString() ?? "";

        // UI Binding Implementation
        public double ProgressValue => 0;
        public bool ShowProgress => false;
        public string BadgeText => "";
        public bool ShowBadge => false;

        public string SourceBadgeText => "IPTV";
        public bool ShowSourceBadge => true;
        public bool IsAvailableOnIptv { get; set; } = true;
        
        // Custom
        
        [JsonIgnore]
        public TmdbMovieResult TmdbInfo { get; set; }


        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "Film";
        
        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }
        
        [JsonPropertyName("stream_icon")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("container_extension")]
        public string? ContainerExtension { get; set; }

        [JsonPropertyName("category_id")]
        public string? CategoryId { get; set; }
        
        [JsonPropertyName("added")]
        public string? DateAdded { get; set; }

        [JsonPropertyName("air_date")]
        public string? AirDate { get; set; }

        [JsonPropertyName("releasedate")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("released")]
        public string? Released { get; set; }

        public string Year 
        { 
            get 
            {
                if (!string.IsNullOrEmpty(_year)) return _year;
                string? dateYear = Helpers.TitleHelper.ExtractYear(ReleaseDate ?? AirDate ?? Released);
                if (!string.IsNullOrEmpty(dateYear)) return dateYear;
                // Fallback to title extraction for bulk list items
                return Helpers.TitleHelper.ExtractYear(Name) ?? "";
            }
            set => _year = value; 
        }
        private string? _year;
        
        public bool IsFavorite { get; set; } // Local state

        // ==========================================
        // PROBING METADATA (Dynamic)
        // ==========================================
        
        private string _resolution = "";
        public string Resolution
        {
            get => _resolution;
            set { _resolution = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMetadata)); OnPropertyChanged(nameof(ShowTechnicalBadges)); }
        }

        private string _fps = "";
        public string Fps
        {
            get => _fps;
            set { _fps = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMetadata)); }
        }

        private string _codec = "";
        public string Codec
        {
            get => _codec;
            set { _codec = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMetadata)); }
        }

        private bool _isHdr = false;
        public bool IsHdr
        {
            get => _isHdr;
            set { _isHdr = value; OnPropertyChanged(); }
        }

        private bool _isProbing = false;
        public bool IsProbing
        { 
            get => _isProbing;
            set { _isProbing = value; OnPropertyChanged(); }
        }
        
        private bool? _isOnline;
        public bool? IsOnline
        {
            get => _isOnline;
            set
            {
                _isOnline = value;
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(ShowTechnicalBadges));
            }
        }

        private long _bitrate;
        public long Bitrate
        {
            get => _bitrate;
            set
            {
                _bitrate = value;
                OnPropertyChanged(nameof(Bitrate));
            }
        }

        public bool HasMetadata => !string.IsNullOrEmpty(Resolution) && 
                                   Resolution != "Aborted" && 
                                   Resolution != "Error";

        public bool ShowTechnicalBadges => HasMetadata;
    }
}
