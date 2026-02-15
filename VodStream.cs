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
        public string? IMDbId => null; // VODs usually don't have IMDB ID directly unless parsed
        public string Title => Name;
        public string PosterUrl => IconUrl;
        public string StreamUrl { get; set; } = "";

        // UI Binding Implementation
        public double ProgressValue => 0;
        public bool ShowProgress => false;
        public string BadgeText => "";
        public bool ShowBadge => false;
        
        // Custom
        public string Year { get; set; } 
        
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

        [JsonPropertyName("rating")]
        public string? Rating { get; set; }
        
        [JsonPropertyName("added")]
        public string? DateAdded { get; set; }

        
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
