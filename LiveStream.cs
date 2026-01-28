using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer
{
    public class LiveStream : INotifyPropertyChanged, IMediaStream
    {
        // IMediaStream Implementation
        public int Id => StreamId;
        public string Title => Name;
        public string PosterUrl => IconUrl;
        
        // Custom
        public string Year { get; set; } // Added for TMDB filtering
        
        [JsonIgnore]
        public TmdbMovieResult TmdbInfo { get; set; }


        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "Kanal";
        
        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }
        
        [JsonPropertyName("series_id")] // Alternate for Series
        public int SeriesId { get => StreamId; set => StreamId = value; }

        [JsonPropertyName("stream_icon")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("cover")] // Alternate for Series
        public string? Cover { get => IconUrl; set => IconUrl = value; }
        
        [JsonPropertyName("container_extension")]
        public string? ContainerExtension { get; set; }

        [JsonPropertyName("category_id")]
        public string? CategoryId { get; set; }

        [JsonPropertyName("rating")]
        public string? Rating { get; set; }

        // Bu alan JSON'dan gelmez, biz oluşturacağız
        public string StreamUrl { get; set; } = "";
        
        // Helper for XAML Binding
        public BitmapImage? StreamIcon
        {
            get
            {
                if (string.IsNullOrEmpty(IconUrl)) return null;
                try
                {
                    return new BitmapImage(new Uri(IconUrl));
                }
                catch
                {
                    return null;
                }
            }
        }

        // ==========================================
        // PROBING METADATA (Dynamic)
        // ==========================================
        
        private string _resolution = "";
        public string Resolution
        {
            get => _resolution;
            set { _resolution = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMetadata)); }
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
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(IsOffline));
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
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(IsUnstable));
            }
        }

        public string StatusColor 
        {
            get
            {
                if (IsOnline == false) return "#FF0000"; // Red
                if (IsOnline == true)
                {
                    // Bitrate < 200kbps is likely black screen or static (FAKE)
                    if (Bitrate < 200000) return "#FFCC00"; // Orange/Yellow
                    return "#00FF00"; // Green
                }
                return "#888888"; // Gray
            }
        }

        public bool IsOffline => IsOnline == false;
        public bool IsUnstable => IsOnline == true && Bitrate < 200000;

        public bool HasMetadata => !string.IsNullOrEmpty(Resolution);

        public Visibility BoolToVis(bool val) => val ? Visibility.Visible : Visibility.Collapsed;
    }
}