using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Models.Iptv
{
    [JsonConverter(typeof(VodStreamConverter))]
    public class VodStream : INotifyPropertyChanged, IMediaStream
    {
        private Helpers.BinaryCacheSession? _session;
        // [PHASE 4.5] Removed per-object _metaLock. Using global LockPool for minimal RAM footprint.
        public int MetadataPriority { get; set; } = 0;
        [JsonIgnore]
        public bool IsLoading { get; set; } = false;
        public int RecordIndex { get => _recordIndex; set => _recordIndex = value; }
        private int _recordIndex = -1;

        // Compact storage (Offsets + Lengths)
        private int _nameOffset, _nameLen;
        private int _iconOffset, _iconLen;
        private int _catOffset, _catLen;
        private int _extOffset, _extLen;
        private int _imdbOffset, _imdbLen;
        private int _descOff, _descLen;
        private int _bgOff, _bgLen;
        private int _genreOff, _genreLen;
        private int _castOff, _castLen;
        private int _dirOff, _dirLen;
        private int _trailOff, _trailLen;
        private int _ratOff, _ratLen;
        private int _yearOff, _yearLen;

        // When MMF session is read-only, enriched strings are stored in MetadataBuffer; mask marks which slots use _roBuf* not disk offsets.
        public const int VodSlotName = 0;
        public const int VodSlotIcon = 1;
        public const int VodSlotImdb = 2;
        public const int VodSlotDesc = 3;
        public const int VodSlotGenre = 4;
        public const int VodSlotCast = 5;
        public const int VodSlotDir = 6;
        public const int VodSlotTrail = 7;
        public const int VodSlotRat = 8;
        public const int VodSlotBg = 9;
        public const int VodSlotSourceTitle = 10;
        public const int VodSlotExt = 11;
        public const int VodSlotYear = 12;
        private const int VodSlotCount = 13;
        
        // [PHASE 4.5] Lazy-allocated metadata buffers. 
        // For 165k items, these stay NULL unless the item is actually patched in memory.
        private int[]? _roBufOff;
        private int[]? _roBufLen;
        private int _roMask;

        /// <summary>
        /// Reads a string from either the MMF disk session or the in-memory metadata buffer (ROI).
        /// Optimized to bypass array checks if the memory mask is zero.
        /// </summary>
        private string VodReadString(int slot, int diskOff, int diskLen)
        {
            // If the buffer mask bit is set, we MUST have a buffer and read from it.
            if ((_roMask & (1 << slot)) != 0 && _roBufOff != null && _roBufLen != null)
                return MetadataBuffer.GetString(_roBufOff[slot], _roBufLen[slot]);
            
            if (_session != null)
                return _session.GetString(diskOff, diskLen);
                
            return MetadataBuffer.GetString(diskOff, diskLen);
        }

        /// <summary>
        /// Writes a string to the in-memory metadata buffer (ROI) or the MMF poke-session.
        /// Lazily initializes buffer arrays if needed.
        /// </summary>
        private void VodWriteString(int slot, ref int diskOff, ref int diskLen, string? value, string? changed1 = null, string? changed2 = null)
        {
            if (_session != null && _session.IsReadOnly)
            {
                // Ensure arrays are initialized before writing to them
                _roBufOff ??= new int[VodSlotCount];
                _roBufLen ??= new int[VodSlotCount];

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
                    _roMask |= (1 << slot);
                }
                if (changed1 != null) OnPropertyChanged(changed1);
                if (changed2 != null) OnPropertyChanged(changed2);
                return;
            }
            if (_session != null)
            {
                var (no, nl) = _session.PokeString(diskOff, diskLen, value);
                diskOff = no;
                diskLen = nl;
                if (changed1 != null) OnPropertyChanged(changed1);
                if (changed2 != null) OnPropertyChanged(changed2);
                return;
            }
            if (MetadataBuffer.IsEqual(diskOff, diskLen, value)) return;
            var res = MetadataBuffer.Store(value);
            diskOff = res.Offset;
            diskLen = res.Length;
            if (changed1 != null) OnPropertyChanged(changed1);
            if (changed2 != null) OnPropertyChanged(changed2);
        }

        // Bit-packed flags (1: IsFavorite, 2: IsHdr, 4: IsProbing, 8: IsOnline (null as false), 16: IsAvailableOnIptv)
        private byte _bitFlags = 16; 
        
        // IMediaStream Implementation
        public int Id => StreamId;
        
        public string? ImdbId 
        { 
            get => VodReadString(VodSlotImdb, _imdbOffset, _imdbLen);
            set => VodWriteString(VodSlotImdb, ref _imdbOffset, ref _imdbLen, value, nameof(ImdbId));
        }
        public string? IMDbId => !string.IsNullOrEmpty(ImdbId) ? ImdbId : string.Empty;
        
        [JsonIgnore]
        public string Title 
        { 
            get => Name; 
            set => Name = value; 
        }

        public string? Description 
        { 
            get => VodReadString(VodSlotDesc, _descOff, _descLen);
            set => VodWriteString(VodSlotDesc, ref _descOff, ref _descLen, value);
        }

        [JsonIgnore]
        public string PosterUrl 
        { 
            get => StreamIcon; 
            set => StreamIcon = value; 
        }

        public string? BackdropUrl 
        { 
            get 
            {
                var all = VodReadString(VodSlotBg, _bgOff, _bgLen);
                if (string.IsNullOrEmpty(all)) return null;
                if (all.Contains('|')) return all.Split('|')[0];
                return all;
            }
            set => VodWriteString(VodSlotBg, ref _bgOff, ref _bgLen, value);
        }

        [JsonIgnore]
        public System.Collections.Generic.List<string> BackdropUrls
        {
            get
            {
                var all = VodReadString(VodSlotBg, _bgOff, _bgLen);
                if (string.IsNullOrEmpty(all)) return new System.Collections.Generic.List<string>();
                return all.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            set
            {
                if (value == null || value.Count == 0) BackdropUrl = null;
                else BackdropUrl = string.Join("|", value);
            }
        }
        public string? Type => "movie";
        
        public string? Genres 
        { 
            get => VodReadString(VodSlotGenre, _genreOff, _genreLen);
            set => VodWriteString(VodSlotGenre, ref _genreOff, ref _genreLen, value);
        }

        public string? Cast 
        { 
            get => VodReadString(VodSlotCast, _castOff, _castLen);
            set => VodWriteString(VodSlotCast, ref _castOff, ref _castLen, value);
        }

        public string? Director 
        { 
            get => VodReadString(VodSlotDir, _dirOff, _dirLen);
            set => VodWriteString(VodSlotDir, ref _dirOff, ref _dirLen, value);
        }

        public string? TrailerUrl 
        { 
            get => VodReadString(VodSlotTrail, _trailOff, _trailLen);
            set => VodWriteString(VodSlotTrail, ref _trailOff, ref _trailLen, value);
        }

        private string? _streamUrlOverride;
        [JsonIgnore]
        public string StreamUrl 
        { 
            get 
            {
                if (!string.IsNullOrEmpty(_streamUrlOverride)) return _streamUrlOverride;
                
                // [PHASE 2.4] Dynamic IPTV URL Generation: Avoids pre-calculating 200k strings in RAM
                var login = App.CurrentLogin;
                if (login != null && !string.IsNullOrEmpty(login.Host))
                {
                    string ext = string.IsNullOrEmpty(ContainerExtension) ? "mp4" : ContainerExtension;
                    return $"{login.Host}/movie/{login.Username}/{login.Password}/{StreamId}.{ext}";
                }
                return string.Empty;
            }
            set => _streamUrlOverride = value; 
        }

        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Rating_json 
        { 
            get => VodReadString(VodSlotRat, _ratOff, _ratLen);
            set => VodWriteString(VodSlotRat, ref _ratOff, ref _ratLen, value, nameof(Rating_json), nameof(Rating));
        }

        [JsonIgnore]
        public string Rating { get => Rating_json ?? ""; set => Rating_json = value; }

        // UI Binding Implementation
        public double ProgressValue => 0;
        public bool ShowProgress => false;
        public string BadgeText => "";
        public bool ShowBadge => false;

        public string SourceBadgeText => "IPTV";
        public bool ShowSourceBadge => true;
        public bool IsAvailableOnIptv 
        { 
            get => (_bitFlags & 16) != 0; 
            set { if (value) _bitFlags |= 16; else _bitFlags &= 239; } 
        }
        
        // Custom
        
        [JsonIgnore]
        public TmdbMovieResult TmdbInfo { get; set; }


        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (IsLoading) return; // Suppress during bulk load

            if (PropertyChanged == null) return; 

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

        public string Name 
        { 
            get => VodReadString(VodSlotName, _nameOffset, _nameLen);
            set => VodWriteString(VodSlotName, ref _nameOffset, ref _nameLen, value, nameof(Name), nameof(Title));
        }
        
        public int StreamId { get; set; }
        
        public string? StreamIcon 
        { 
            get => VodReadString(VodSlotIcon, _iconOffset, _iconLen);
            set => VodWriteString(VodSlotIcon, ref _iconOffset, ref _iconLen, value, nameof(StreamIcon), nameof(PosterUrl));
        }

        public string? ContainerExtension 
        { 
            get => VodReadString(VodSlotExt, _extOffset, _extLen);
            set => VodWriteString(VodSlotExt, ref _extOffset, ref _extLen, value);
        }

        public string? CategoryId 
        { 
            get => _categoryId;
            set { if (_categoryId != value) { _categoryId = value; OnPropertyChanged(); } }
        }
        private string? _categoryId;
        
        public string? Dateadded { get; set; }
        public string? Airdate { get; set; }
        public string? Released { get; set; }
        public string? Releasedate { get; set; }

        public string? LastModified { get; set; }

        public string Year 
        { 
            get 
            {
                string? stored = null;
                if ((_roMask & (1 << VodSlotYear)) != 0 && _roBufOff != null && _roBufLen != null)
                    stored = MetadataBuffer.GetString(_roBufOff[VodSlotYear], _roBufLen[VodSlotYear]);
                else if (_session != null)
                    stored = _session.GetString(_yearOff, _yearLen);
                else if (!string.IsNullOrEmpty(_year))
                    stored = _year;
                if (!string.IsNullOrEmpty(stored)) return stored;

                var yearSpan = Helpers.TitleHelper.ExtractYear(Releasedate.AsSpan());
                if (yearSpan.IsEmpty) yearSpan = Helpers.TitleHelper.ExtractYear(Airdate.AsSpan());
                if (yearSpan.IsEmpty) yearSpan = Helpers.TitleHelper.ExtractYear(Released.AsSpan());
                
                if (!yearSpan.IsEmpty) return yearSpan.ToString();
                return Helpers.TitleHelper.ExtractYear(Name.AsSpan()).ToString();
            }
            set 
            { 
                if (_session != null && _session.IsReadOnly)
                {
                    _roBufOff ??= new int[VodSlotCount];
                    _roBufLen ??= new int[VodSlotCount];

                    if (string.IsNullOrEmpty(value))
                    {
                        _roMask &= ~(1 << VodSlotYear);
                        _year = value;
                    }
                    else
                    {
                        var r = MetadataBuffer.Store(value);
                        _roBufOff[VodSlotYear] = r.Offset;
                        _roBufLen[VodSlotYear] = r.Length;
                        _roMask |= (1 << VodSlotYear);
                        _year = value;
                    }
                    OnPropertyChanged();
                    return;
                }
                if (_session != null)
                {
                    var (no, nl) = _session.PokeString(_yearOff, _yearLen, value);
                    _yearOff = no;
                    _yearLen = nl;
                }
                _year = value;
                OnPropertyChanged();
            } 
        }
        private string? _year;

        public int PriorityScore { get => MetadataPriority; set => MetadataPriority = value; }
        public uint Fingerprint { get; set; }
        
        public bool IsFavorite 
        { 
            get => (_bitFlags & 1) != 0; 
            set { if (value) _bitFlags |= 1; else _bitFlags &= 254; } 
        }

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

        private bool _isActive = false;
        [JsonIgnore]
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(); }
        }
        
        private bool? _isOnline;
        public bool? IsOnline
        {
            get => (_bitFlags & 8) != 0;
            set
            {
                if (value == true) _bitFlags |= 8; else _bitFlags &= 247;
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

        public void UpdateFromUnified(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata unified)
        {
            if (unified == null) return;
            
            // [PHASE 4.5] Using Striped Locking for thread-safe hydration without object bloat.
            lock (LockPool.GetLock(StreamId))
            {
                bool isDowngrade = unified.PriorityScore < this.MetadataPriority;
                Models.Metadata.MetadataSync.Sync(this, unified, isDowngrade);
                
                if (!isDowngrade)
                {
                    this.MetadataPriority = unified.PriorityScore;
                }
            }
        }

        internal byte GetBitFlagsForBinarySave() => _bitFlags;

        public uint CalculateFingerprint()
        {
            Span<char> normBuf = stackalloc char[(Name?.Length ?? 0) + 16];
            int normLen = Helpers.TitleHelper.NormalizeToBuffer(Name.AsSpan(), normBuf);
            ReadOnlySpan<char> cleanTitle = normBuf[..normLen];
            
            ReadOnlySpan<char> cleanYear = Year.AsSpan();
            ReadOnlySpan<char> cleanImdb = ImdbId.AsSpan();
            
            // FNV-1a like hash
            uint hash = 2166136261;
            foreach (char c in cleanTitle) hash = (hash ^ c) * 16777619;
            foreach (char c in cleanYear) hash = (hash ^ c) * 16777619;
            foreach (char c in cleanImdb) hash = (hash ^ c) * 16777619;
            return hash;
        }

        public Models.Metadata.VodRecord ToRecord()
        {
            float ratingValue = 0;
            if (double.TryParse(Rating_json, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
                ratingValue = (float)r;

            return new Models.Metadata.VodRecord
            {
                StreamId = StreamId,
                CategoryId = int.TryParse(_categoryId, out int catId) ? catId : 0,
                PriorityScore = MetadataPriority,
                Fingerprint = CalculateFingerprint(),
                LastModified = DateTime.UtcNow.Ticks,

                NameOff = _nameOffset, NameLen = _nameLen,
                IconOff = _iconOffset, IconLen = _iconLen,
                ImdbIdOff = _imdbOffset, ImdbIdLen = _imdbLen,
                PlotOff = _descOff, PlotLen = _descLen,
                YearOff = _yearOff, YearLen = _yearLen,
                GenresOff = _genreOff, GenresLen = _genreLen,
                CastOff = _castOff, CastLen = _castLen,
                DirectorOff = _dirOff, DirectorLen = _dirLen,
                TrailerOff = _trailOff, TrailerLen = _trailLen,
                BackdropOff = _bgOff, BackdropLen = _bgLen,
                SourceTitleOff = _catOffset, SourceTitleLen = _catLen,
                RatingOff = _ratOff, RatingLen = _ratLen,

                RatingScaled = (short)(ratingValue * 100),
                Flags = _bitFlags,
                Duration = 0, // TODO: Map duration if available
                ExtOff = _extOffset, ExtLen = _extLen
            };
        }

        public void LoadFromRecord(Models.Metadata.VodRecord data)
        {
            StreamId = data.StreamId;
            _categoryId = data.CategoryId.ToString();
            MetadataPriority = data.PriorityScore;
            Fingerprint = data.Fingerprint;
            
            _nameOffset = data.NameOff; _nameLen = data.NameLen;
            _iconOffset = data.IconOff; _iconLen = data.IconLen;
            _imdbOffset = data.ImdbIdOff; _imdbLen = data.ImdbIdLen;
            _descOff = data.PlotOff; _descLen = data.PlotLen;
            _yearOff = data.YearOff; _yearLen = data.YearLen;
            _genreOff = data.GenresOff; _genreLen = data.GenresLen;
            _castOff = data.CastOff; _castLen = data.CastLen;
            _dirOff = data.DirectorOff; _dirLen = data.DirectorLen;
            _trailOff = data.TrailerOff; _trailLen = data.TrailerLen;
            _bgOff = data.BackdropOff; _bgLen = data.BackdropLen;
            _catOffset = data.SourceTitleOff; _catLen = data.SourceTitleLen;
            _ratOff = data.RatingOff; _ratLen = data.RatingLen;
            _extOffset = data.ExtOff; _extLen = data.ExtLen;

            _bitFlags = data.Flags;
            
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(StreamIcon));
            OnPropertyChanged(nameof(Year));
            OnPropertyChanged(nameof(Rating));
            OnPropertyChanged(nameof(SourceTitle));
        }

        [JsonIgnore]
        public string? SourceTitle
        {
            get => VodReadString(VodSlotSourceTitle, _catOffset, _catLen);
            set => VodWriteString(VodSlotSourceTitle, ref _catOffset, ref _catLen, value, nameof(SourceTitle));
        }

        public void SetCacheSession(Helpers.BinaryCacheSession session, int recordIndex)
        {
            _session = session;
            _recordIndex = recordIndex;
        }
    }

    public class VodStreamConverter : JsonConverter<VodStream>
    {
        public override VodStream Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) return null;
            var stream = new VodStream { IsLoading = true };
            try
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    
                    var propName = reader.ValueSpan;
                    reader.Read();

                    if (reader.TokenType == JsonTokenType.Null) continue;

                    // PROJECT ZERO: Span-based matching (Zero-alloc)
                    if (propName.SequenceEqual("name"u8))
                    {
                        stream.Name = reader.GetString();
                    }
                    else if (propName.SequenceEqual("stream_id"u8))
                    {
                        stream.StreamId = reader.GetInt32();
                    }
                    else if (propName.SequenceEqual("stream_icon"u8))
                    {
                        stream.StreamIcon = reader.GetString();
                    }
                    else if (propName.SequenceEqual("container_extension"u8))
                    {
                        stream.ContainerExtension = FastStringPool.Intern(reader.GetString());
                    }
                    else if (propName.SequenceEqual("category_id"u8))
                    {
                        stream.CategoryId = FastStringPool.Intern(reader.GetString());
                    }
                    else if (propName.SequenceEqual("imdb_id"u8))
                    {
                        stream.ImdbId = reader.GetString();
                    }
                    else if (propName.SequenceEqual("rating"u8))
                    {
                        stream.Rating_json = reader.TokenType == JsonTokenType.Number ? reader.GetDouble().ToString() : reader.GetString();
                    }
                    else if (propName.SequenceEqual("added"u8))
                    {
                        stream.Dateadded = FastStringPool.Intern(reader.GetString());
                    }
                }
            }
            finally
            {
                stream.IsLoading = false;
            }
            return stream;
        }

        public override void Write(Utf8JsonWriter writer, VodStream value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);
            writer.WriteNumber("stream_id", value.StreamId);
            writer.WriteString("stream_icon", value.StreamIcon);
            writer.WriteString("container_extension", value.ContainerExtension);
            writer.WriteString("category_id", value.CategoryId);
            writer.WriteString("imdb_id", value.ImdbId);
            writer.WriteString("rating", value.Rating_json);
            writer.WriteString("stream_url", value.StreamUrl);
            writer.WriteEndObject();
        }
    }
}
