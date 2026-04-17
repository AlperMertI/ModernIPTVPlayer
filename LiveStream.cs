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

    /// <summary>
    /// Blittable struct for high-speed Binary I/O.
    /// No strings, no objects, just raw memory.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct LiveStreamData
    {
        public int StreamId;
        public int NameOff, NameLen;
        public int IconOff, IconLen;
        public int ImdbOff, ImdbLen;
        public int DescOff, DescLen;
        public int BgOff, BgLen;
        public int GenreOff, GenreLen;
        public int CastOff, CastLen;
        public int DirOff, DirLen;
        public int TrailOff, TrailLen;
        public int YearOff, YearLen;
        public int ExtOff, ExtLen;
        public int CatOff, CatLen;
        public int RatOff, RatLen;
    }

    public class LiveStream : INotifyPropertyChanged, IMediaStream
    {
        private object? _metaLock;
        private object MetaLock => _metaLock ??= new object();
        public int MetadataPriority { get; set; } = 0;
        public int PriorityScore { get => MetadataPriority; set => MetadataPriority = value; }
        public uint Fingerprint { get; set; }
        [JsonIgnore]
        public bool IsLoading { get; set; } = false;
        
        // IMediaStream Implementation
        public int Id => StreamId;
        
        // Compact storage (Offsets + Lengths)
        private int _nameOff = -1, _nameLen = 0;
        private int _iconOff = -1, _iconLen = 0;
        private int _imdbOff = -1, _imdbLen = 0;
        private int _descOff = -1, _descLen = 0;
        private int _bgOff = -1, _bgLen = 0;
        private int _genreOff = -1, _genreLen = 0;
        private int _castOff = -1, _castLen = 0;
        private int _dirOff = -1, _dirLen = 0;
        private int _trailOff = -1, _trailLen = 0;
        private int _yearOff = -1, _yearLen = 0;
        private int _extOff = -1, _extLen = 0;
        private int _catOff = -1, _catLen = 0;
        private int _ratOff = -1, _ratLen = 0;

        /// <summary>
        /// PROJECT ZERO: Zero-Allocation Fast Initialization.
        /// Bypasses property setters to avoid re-storing strings in MetadataBuffer.
        /// </summary>
        public void LoadFromData(LiveStreamData data, int baseOffset = 0)
        {
            this.StreamId = data.StreamId;
            this._nameOff = data.NameOff + baseOffset; this._nameLen = data.NameLen;
            this._iconOff = data.IconOff + baseOffset; this._iconLen = data.IconLen;
            this._imdbOff = data.ImdbOff + baseOffset; this._imdbLen = data.ImdbLen;
            this._descOff = data.DescOff + baseOffset; this._descLen = data.DescLen;
            this._bgOff = data.BgOff + baseOffset; this._bgLen = data.BgLen;
            this._genreOff = data.GenreOff + baseOffset; this._genreLen = data.GenreLen;
            this._castOff = data.CastOff + baseOffset; this._castLen = data.CastLen;
            this._dirOff = data.DirOff + baseOffset; this._dirLen = data.DirLen;
            this._trailOff = data.TrailOff + baseOffset; this._trailLen = data.TrailLen;
            this._yearOff = data.YearOff + baseOffset; this._yearLen = data.YearLen;
            this._extOff = data.ExtOff + baseOffset; this._extLen = data.ExtLen;
            this._catOff = data.CatOff + baseOffset; this._catLen = data.CatLen;
            this._ratOff = data.RatOff + baseOffset; this._ratLen = data.RatLen;
        }

        /// <summary>
        /// PROJECT ZERO: Fast Data Extraction.
        /// </summary>
        public LiveStreamData ToData() => new LiveStreamData {
            StreamId = this.StreamId,
            NameOff = _nameOff, NameLen = _nameLen,
            IconOff = _iconOff, IconLen = _iconLen,
            ImdbOff = _imdbOff, ImdbLen = _imdbLen,
            DescOff = _descOff, DescLen = _descLen,
            BgOff = _bgOff, BgLen = _bgLen,
            GenreOff = _genreOff, GenreLen = _genreLen,
            CastOff = _castOff, CastLen = _castLen,
            DirOff = _dirOff, DirLen = _dirLen,
            TrailOff = _trailOff, TrailLen = _trailLen,
            YearOff = _yearOff, YearLen = _yearLen,
            ExtOff = _extOff, ExtLen = _extLen,
            CatOff = _catOff, CatLen = _catLen,
            RatOff = _ratOff, RatLen = _ratLen
        };

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
        public string? SourceTitle { get; set; }

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
            if (IsLoading) return; // Suppress during bulk load

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
            
            lock (MetaLock)
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
