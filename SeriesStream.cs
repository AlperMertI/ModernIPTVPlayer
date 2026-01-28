using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer
{
    public class SeriesStream : IMediaStream, System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) => 
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

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

        // IMediaStream Implementation
        public int Id => SeriesId;
        public string Title => Name;
        public string PosterUrl => Cover;
        
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

        [JsonPropertyName("last_modified")]
        public string? LastModified { get; set; }

        [JsonPropertyName("rating")]
        public string? Rating { get; set; } // Can be "5", "5.0", etc.

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
        public BitmapImage? CoverImage
        {
            get
            {
                if (string.IsNullOrEmpty(Cover)) return null;
                try
                {
                    return new BitmapImage(new Uri(Cover));
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
