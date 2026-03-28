using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Helpers;

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
        
        // Compact storage (Offsets + Lengths)
        private int _nameOff, _nameLen;
        private int _iconOff, _iconLen;
        private int _imdbOff, _imdbLen;
        private int _descOff, _descLen;
        private int _bgOff, _bgLen;
        private int _genreOff, _genreLen;
        private int _castOff, _castLen;
        private int _dirOff, _dirLen;
        private int _trailOff, _trailLen;
        private int _yearOff, _yearLen;
        private int _extOff, _extLen;
        private int _catOff, _catLen;
        private int _ratOff, _ratLen;

        [JsonPropertyName("imdb_id")]
        public string? ImdbId 
        { 
            get => MetadataBuffer.GetString(_imdbOff, _imdbLen); 
            set { if (MetadataBuffer.IsEqual(_imdbOff, _imdbLen, value)) return; var r = MetadataBuffer.Store(value); _imdbOff = r.Offset; _imdbLen = r.Length; OnPropertyChanged(); } 
        }
        public string? IMDbId => ImdbId;
        
        [JsonIgnore]
        public string Title 
        { 
            get => Name; 
            set => Name = value; 
        }

        public string? Description 
        { 
            get => MetadataBuffer.GetString(_descOff, _descLen); 
            set { if (MetadataBuffer.IsEqual(_descOff, _descLen, value)) return; var r = MetadataBuffer.Store(value); _descOff = r.Offset; _descLen = r.Length; OnPropertyChanged(); } 
        }

        [JsonIgnore]
        public string PosterUrl 
        { 
            get => IconUrl; 
            set => IconUrl = value; 
        }

        public string? BackdropUrl 
        { 
            get => MetadataBuffer.GetString(_bgOff, _bgLen); 
            set { if (MetadataBuffer.IsEqual(_bgOff, _bgLen, value)) return; var r = MetadataBuffer.Store(value); _bgOff = r.Offset; _bgLen = r.Length; OnPropertyChanged(); } 
        }
        public string? Type => "live";

        public string? Genres 
        { 
            get => MetadataBuffer.GetString(_genreOff, _genreLen); 
            set { if (MetadataBuffer.IsEqual(_genreOff, _genreLen, value)) return; var r = MetadataBuffer.Store(value); _genreOff = r.Offset; _genreLen = r.Length; OnPropertyChanged(); } 
        }
        public string? Cast 
        { 
            get => MetadataBuffer.GetString(_castOff, _castLen); 
            set { if (MetadataBuffer.IsEqual(_castOff, _castLen, value)) return; var r = MetadataBuffer.Store(value); _castOff = r.Offset; _castLen = r.Length; OnPropertyChanged(); } 
        }
        public string? Director 
        { 
            get => MetadataBuffer.GetString(_dirOff, _dirLen); 
            set { if (MetadataBuffer.IsEqual(_dirOff, _dirLen, value)) return; var r = MetadataBuffer.Store(value); _dirOff = r.Offset; _dirLen = r.Length; OnPropertyChanged(); } 
        }
        public string? TrailerUrl 
        { 
            get => MetadataBuffer.GetString(_trailOff, _trailLen); 
            set { if (MetadataBuffer.IsEqual(_trailOff, _trailLen, value)) return; var r = MetadataBuffer.Store(value); _trailOff = r.Offset; _trailLen = r.Length; OnPropertyChanged(); } 
        }
        
        public string Year 
        { 
            get => MetadataBuffer.GetString(_yearOff, _yearLen); 
            set { if (MetadataBuffer.IsEqual(_yearOff, _yearLen, value)) return; var r = MetadataBuffer.Store(value); _yearOff = r.Offset; _yearLen = r.Length; OnPropertyChanged(); } 
        }
        
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
        public string Name 
        { 
            get => MetadataBuffer.GetString(_nameOff, _nameLen); 
            set { if (MetadataBuffer.IsEqual(_nameOff, _nameLen, value)) return; var r = MetadataBuffer.Store(value); _nameOff = r.Offset; _nameLen = r.Length; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); } 
        }
        
        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }
        
        [JsonPropertyName("series_id")] // Alternate for Series
        public int SeriesId { get => StreamId; set => StreamId = value; }

        [JsonPropertyName("stream_icon")]
        public string? IconUrl 
        { 
            get => MetadataBuffer.GetString(_iconOff, _iconLen); 
            set { if (MetadataBuffer.IsEqual(_iconOff, _iconLen, value)) return; var r = MetadataBuffer.Store(value); _iconOff = r.Offset; _iconLen = r.Length; OnPropertyChanged(); OnPropertyChanged(nameof(PosterUrl)); } 
        }

        [JsonPropertyName("cover")] // Alternate for Series
        public string? Cover { get => IconUrl; set => IconUrl = value; }
        
        [JsonPropertyName("container_extension")]
        public string? ContainerExtension 
        { 
            get => MetadataBuffer.GetString(_extOff, _extLen); 
            set { if (MetadataBuffer.IsEqual(_extOff, _extLen, value)) return; var r = MetadataBuffer.Store(value); _extOff = r.Offset; _extLen = r.Length; OnPropertyChanged(); } 
        }

        [JsonPropertyName("category_id")]
        public string? CategoryId 
        { 
            get => MetadataBuffer.GetString(_catOff, _catLen); 
            set { if (MetadataBuffer.IsEqual(_catOff, _catLen, value)) return; var r = MetadataBuffer.Store(value); _catOff = r.Offset; _catLen = r.Length; OnPropertyChanged(); } 
        }

        [JsonPropertyName("rating")]
        public string Rating 
        { 
            get => MetadataBuffer.GetString(_ratOff, _ratLen); 
            set { if (MetadataBuffer.IsEqual(_ratOff, _ratLen, value)) return; var r = MetadataBuffer.Store(value); _ratOff = r.Offset; _ratLen = r.Length; OnPropertyChanged(); } 
        }

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
