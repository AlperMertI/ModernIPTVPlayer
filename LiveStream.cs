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

    [Microsoft.UI.Xaml.Data.Bindable]
    public class LiveStream : INotifyPropertyChanged, IMediaStream
    {
        private BinaryCacheSession? _session;
        private object? _metaLock;
        private object MetaLock => _metaLock ??= new object();
        public int MetadataPriority { get; set; } = 0;
        public int PriorityScore { get => MetadataPriority; set => MetadataPriority = value; }
        public int RecordIndex { get; private set; } = -1;
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

        private const int LiveSlotName = 0;
        private const int LiveSlotIcon = 1;
        private const int LiveSlotImdb = 2;
        private const int LiveSlotDesc = 3;
        private const int LiveSlotBg = 4;
        private const int LiveSlotGenre = 5;
        private const int LiveSlotCast = 6;
        private const int LiveSlotDir = 7;
        private const int LiveSlotTrail = 8;
        private const int LiveSlotYear = 9;
        private const int LiveSlotExt = 10;
        private const int LiveSlotCat = 11;
        private const int LiveSlotRat = 12;
        private const int LiveSlotCount = 13;
        private readonly int[] _roBufOff = new int[LiveSlotCount];
        private readonly int[] _roBufLen = new int[LiveSlotCount];
        private int _roMask;

        private string LiveReadString(int slot, int diskOff, int diskLen)
        {
            if ((_roMask & (1 << slot)) != 0)
            {
                return MetadataBuffer.GetString(_roBufOff[slot], _roBufLen[slot]);
            }

            if (_session != null)
            {
                return _session.GetString(diskOff, diskLen);
            }

            return MetadataBuffer.GetString(diskOff, diskLen);
        }

        private void LiveWriteString(int slot, ref int diskOff, ref int diskLen, string? value, string? changed1 = null, string? changed2 = null)
        {
            if (_session != null && _session.IsReadOnly)
            {
                if (string.IsNullOrEmpty(value))
                {
                    _roMask &= ~(1 << slot);
                    _roBufOff[slot] = -1;
                    _roBufLen[slot] = 0;
                }
                else
                {
                    var r = MetadataBuffer.Store(value);
                    _roBufOff[slot] = r.Offset;
                    _roBufLen[slot] = r.Length;
                    _roMask |= 1 << slot;
                }

                if (changed1 != null) OnPropertyChanged(changed1);
                if (changed2 != null) OnPropertyChanged(changed2);
                return;
            }

            if (_session != null)
            {
                var (newOffset, newLength) = _session.PokeString(diskOff, diskLen, value);
                diskOff = newOffset;
                diskLen = newLength;
                if (changed1 != null) OnPropertyChanged(changed1);
                if (changed2 != null) OnPropertyChanged(changed2);
                return;
            }

            if (MetadataBuffer.IsEqual(diskOff, diskLen, value)) return;
            var stored = MetadataBuffer.Store(value);
            diskOff = stored.Offset;
            diskLen = stored.Length;
            if (changed1 != null) OnPropertyChanged(changed1);
            if (changed2 != null) OnPropertyChanged(changed2);
        }

        /// <summary>
        /// PROJECT ZERO: Zero-Allocation Fast Initialization.
        /// Bypasses property setters to avoid re-storing strings in MetadataBuffer.
        /// </summary>
        public void LoadFromData(LiveStreamData data, int baseOffset = 0)
        {
            this.StreamId = data.StreamId;
            this._nameOff = AddBaseOffset(data.NameOff, baseOffset); this._nameLen = data.NameLen;
            this._iconOff = AddBaseOffset(data.IconOff, baseOffset); this._iconLen = data.IconLen;
            this._imdbOff = AddBaseOffset(data.ImdbOff, baseOffset); this._imdbLen = data.ImdbLen;
            this._descOff = AddBaseOffset(data.DescOff, baseOffset); this._descLen = data.DescLen;
            this._bgOff = AddBaseOffset(data.BgOff, baseOffset); this._bgLen = data.BgLen;
            this._genreOff = AddBaseOffset(data.GenreOff, baseOffset); this._genreLen = data.GenreLen;
            this._castOff = AddBaseOffset(data.CastOff, baseOffset); this._castLen = data.CastLen;
            this._dirOff = AddBaseOffset(data.DirOff, baseOffset); this._dirLen = data.DirLen;
            this._trailOff = AddBaseOffset(data.TrailOff, baseOffset); this._trailLen = data.TrailLen;
            this._yearOff = AddBaseOffset(data.YearOff, baseOffset); this._yearLen = data.YearLen;
            this._extOff = AddBaseOffset(data.ExtOff, baseOffset); this._extLen = data.ExtLen;
            this._catOff = AddBaseOffset(data.CatOff, baseOffset); this._catLen = data.CatLen;
            this._ratOff = AddBaseOffset(data.RatOff, baseOffset); this._ratLen = data.RatLen;
        }

        private static int AddBaseOffset(int offset, int baseOffset)
            => offset < 0 ? offset : offset + baseOffset;

        public void SetCacheSession(BinaryCacheSession session, int recordIndex)
        {
            _session = session;
            RecordIndex = recordIndex;
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
            get => LiveReadString(LiveSlotImdb, _imdbOff, _imdbLen); 
            set => LiveWriteString(LiveSlotImdb, ref _imdbOff, ref _imdbLen, value, nameof(ImdbId));
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
            get => LiveReadString(LiveSlotDesc, _descOff, _descLen); 
            set => LiveWriteString(LiveSlotDesc, ref _descOff, ref _descLen, value, nameof(Description));
        }

        [JsonIgnore]
        public string PosterUrl 
        { 
            get => IconUrl; 
            set => IconUrl = value; 
        }

        public string? BackdropUrl 
        { 
            get => LiveReadString(LiveSlotBg, _bgOff, _bgLen); 
            set => LiveWriteString(LiveSlotBg, ref _bgOff, ref _bgLen, value, nameof(BackdropUrl));
        }
        public string? Type => "live";

        public string? Genres 
        { 
            get => LiveReadString(LiveSlotGenre, _genreOff, _genreLen); 
            set => LiveWriteString(LiveSlotGenre, ref _genreOff, ref _genreLen, value, nameof(Genres));
        }
        public string? Cast 
        { 
            get => LiveReadString(LiveSlotCast, _castOff, _castLen); 
            set => LiveWriteString(LiveSlotCast, ref _castOff, ref _castLen, value, nameof(Cast));
        }
        public string? Director 
        { 
            get => LiveReadString(LiveSlotDir, _dirOff, _dirLen); 
            set => LiveWriteString(LiveSlotDir, ref _dirOff, ref _dirLen, value, nameof(Director));
        }
        public string? TrailerUrl 
        { 
            get => LiveReadString(LiveSlotTrail, _trailOff, _trailLen); 
            set => LiveWriteString(LiveSlotTrail, ref _trailOff, ref _trailLen, value, nameof(TrailerUrl));
        }
        
        public string Year 
        { 
            get => LiveReadString(LiveSlotYear, _yearOff, _yearLen); 
            set => LiveWriteString(LiveSlotYear, ref _yearOff, ref _yearLen, value, nameof(Year));
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
            get => LiveReadString(LiveSlotName, _nameOff, _nameLen); 
            set => LiveWriteString(LiveSlotName, ref _nameOff, ref _nameLen, value, nameof(Name), nameof(Title));
        }
        
        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }
        
        [JsonPropertyName("series_id")] // Alternate for Series
        public int SeriesId { get => StreamId; set => StreamId = value; }

        [JsonPropertyName("stream_icon")]
        public string? IconUrl 
        { 
            get => LiveReadString(LiveSlotIcon, _iconOff, _iconLen); 
            set => LiveWriteString(LiveSlotIcon, ref _iconOff, ref _iconLen, value, nameof(IconUrl), nameof(PosterUrl));
        }

        [JsonPropertyName("cover")] // Alternate for Series
        public string? Cover { get => IconUrl; set => IconUrl = value; }
        
        [JsonPropertyName("container_extension")]
        public string? ContainerExtension 
        { 
            get => LiveReadString(LiveSlotExt, _extOff, _extLen); 
            set => LiveWriteString(LiveSlotExt, ref _extOff, ref _extLen, value, nameof(ContainerExtension));
        }

        [JsonPropertyName("category_id")]
        public string? CategoryId 
        { 
            get => LiveReadString(LiveSlotCat, _catOff, _catLen); 
            set => LiveWriteString(LiveSlotCat, ref _catOff, ref _catLen, value, nameof(CategoryId));
        }

        [JsonPropertyName("rating")]
        public string Rating 
        { 
            get => LiveReadString(LiveSlotRat, _ratOff, _ratLen); 
            set => LiveWriteString(LiveSlotRat, ref _ratOff, ref _ratLen, value, nameof(Rating));
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
