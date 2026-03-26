using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer
{
    public enum LiveStreamStatus
    {
        Unknown,
        Online,
        Offline,
        Unstable
    }

    public class LiveStream : INotifyPropertyChanged, IMediaStream
    {
        private readonly object _metaLock = new();
        public int MetadataPriority { get; set; } = 0;
        
        // IMediaStream Implementation
        public int Id => StreamId;
        
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }
        public string? IMDbId => ImdbId;
        public string Title { get => Name; set { if (Name != value) { Name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Name)); } } }

        private string? _description;
        public string? Description { get => _description; set { if (_description != value) { _description = value; OnPropertyChanged(); } } }

        public string PosterUrl { get => IconUrl; set { if (IconUrl != value) { IconUrl = value; OnPropertyChanged(); OnPropertyChanged(nameof(IconUrl)); } } }

        private string? _backdropUrl;
        public string? BackdropUrl { get => _backdropUrl; set { if (_backdropUrl != value) { _backdropUrl = value; OnPropertyChanged(); } } }
        public string? Type => "live";

        public string? Genres { get; set; }
        public string? Cast { get; set; }
        public string? Director { get; set; }
        private string? _trailerUrl;
        public string? TrailerUrl { get => _trailerUrl; set { if (_trailerUrl != value) { _trailerUrl = value; OnPropertyChanged(); } } }
        
        // Custom
        public string Year { get; set; } // Added for TMDB filtering
        
        [JsonIgnore]
        public TmdbMovieResult TmdbInfo { get; set; }


        public event PropertyChangedEventHandler? PropertyChanged;

        public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            var queue = App.MainWindow?.DispatcherQueue;
            if (queue == null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                return;
            }

            if (queue.HasThreadAccess)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
            else
            {
                queue.TryEnqueue(() =>
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                });
            }
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
        public string Rating { get; set; } = "";

        // Bu alan JSON'dan gelmez, biz oluşturacağız
        public string StreamUrl { get; set; } = "";

        // UI Binding Helpers (Fixing Binding Errors)
        public double ProgressValue => 0; 
        public bool ShowProgress => false;
        public string BadgeText => "";
        public bool ShowBadge => false;

        public string SourceBadgeText => "IPTV";
        public bool ShowSourceBadge => true;
        public bool IsAvailableOnIptv { get; set; } = true;
        
        // ==========================================
        // PROBING METADATA (Dynamic)
        // ==========================================
        
        private string _resolution = "";
        public string Resolution
        {
            get => _resolution;
            set { _resolution = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMetadata)); OnPropertyChanged(nameof(ShowTechnicalBadges)); OnPropertyChanged(nameof(StatusToolTip)); }
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
            set { _isHdr = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowHdrBadge)); OnPropertyChanged(nameof(StatusToolTip)); }
        }

        public bool ShowHdrBadge => IsHdr && ShowTechnicalBadges;

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
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(IsOffline));
                OnPropertyChanged(nameof(StatusToolTip));
                OnPropertyChanged(nameof(ShowTechnicalBadges));
                OnPropertyChanged(nameof(ShowStatusDot));
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
                OnPropertyChanged(nameof(FormattedBitrate));
                OnPropertyChanged(nameof(HasBitrate));
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(IsUnstable));
                OnPropertyChanged(nameof(StatusToolTip));
            }
        }

        public bool HasBitrate => !string.IsNullOrEmpty(FormattedBitrate);

        public string FormattedBitrate
        {
            get
            {
                if (Bitrate <= 0) return "";
                double mbps = Bitrate / 1000000.0;
                if (mbps >= 1.0)
                    return $"{mbps:F1} Mbps";
                else
                    return $"{Bitrate / 1000} kbps";
            }
        }

        [JsonIgnore]
        public LiveStreamStatus Status
        {
            get
            {
                if (IsOnline == false) return LiveStreamStatus.Offline;
                if (IsOnline == true)
                {
                    if (Bitrate > 0 && Bitrate < 200000) return LiveStreamStatus.Unstable;
                    return LiveStreamStatus.Online;
                }
                return LiveStreamStatus.Unknown;
            }
        }

        public bool IsOffline => IsOnline == false;
        public bool IsUnstable => IsOnline == true && Bitrate > 0 && Bitrate < 200000;

        public bool HasMetadata => !string.IsNullOrEmpty(Resolution) && 
                                   Resolution != "Aborted" && 
                                   Resolution != "Error" && 
                                   Resolution != "No Data" && 
                                   Resolution != "Unknown" && 
                                   Resolution != "No Probe";

        public bool ShowTechnicalBadges => IsOnline == true && HasMetadata;
        public bool ShowStatusDot => IsOnline != null;

        public string StatusToolTip
        {
            get
            {
                if (IsOnline == false) return "Kanal Çevrimdışı (Hata)";
                if (IsOnline == true)
                {
                    if (IsUnstable) return $"Düşük Akış / Siyah Ekran Şüphesi ({Bitrate / 1000} kbps)";
                    string hdrStr = IsHdr ? " [HDR]" : "";
                    return $"Yayın Aktif (Çözünürlük: {Resolution}, Bitrate: {Bitrate / 1000} kbps{hdrStr})";
                }
                return "Durum Bilinmiyor (Analiz Bekleniyor)";
            }
        }

        public void UpdateFromUnified(Models.Metadata.UnifiedMetadata unified)
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
