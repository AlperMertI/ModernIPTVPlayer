using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer
{
    [JsonConverter(typeof(SeriesStreamConverter))]
    public class SeriesStream : IMediaStream, System.ComponentModel.INotifyPropertyChanged
    {
        private int _nameOff, _nameLen;
        private int _coverOff, _coverLen;
        private int _catOff, _catLen;
        private int _genreOff, _genreLen;
        private int _castOff, _castLen;
        private int _dirOff, _dirLen;
        private int _imdbOff, _imdbLen;
        private int _descOff, _descLen;
        private int _trailOff, _trailLen;
        private int _ratOff, _ratLen;
        private int _yearOff, _yearLen;
        private int _bgOff, _bgLen;
        private int _titleOff, _titleLen;
        private int _tmdbRawOff, _tmdbRawLen;
        private int _tmdbAltOff, _tmdbAltLen;
        private int _extOff, _extLen;

        // Bit-packed flags (1: IsFavorite, 2: IsHdr, 4: IsProbing, 8: IsOnline (null as false), 16: IsAvailableOnIptv)
        private byte _bitFlags = 16;
        private Helpers.BinaryCacheSession? _session;
        [JsonIgnore]
        public bool IsLoading { get; set; } = false;
        public int RecordIndex { get => _recordIndex; set => _recordIndex = value; }
        private int _recordIndex = -1;

        public const int SerSlotImdb = 0;
        public const int SerSlotTmdbRaw = 1;
        public const int SerSlotTmdbAlt = 2;
        public const int SerSlotDesc = 3;
        public const int SerSlotBg = 4;
        public const int SerSlotGenre = 5;
        public const int SerSlotTrail = 6;
        public const int SerSlotYear = 7;
        public const int SerSlotName = 8;
        public const int SerSlotCover = 9;
        public const int SerSlotCast = 10;
        public const int SerSlotDir = 11;
        public const int SerSlotSourceTitle = 12;
        public const int SerSlotRat = 13;
        public const int SerSlotExt = 14;
        private const int SerSlotCount = 15;
        private readonly int[] _roBufOff = new int[SerSlotCount];
        private readonly int[] _roBufLen = new int[SerSlotCount];
        private int _roMask;

        private string SerReadString(int slot, int diskOff, int diskLen)
        {
            if ((_roMask & (1 << slot)) != 0)
                return MetadataBuffer.GetString(_roBufOff[slot], _roBufLen[slot]);
            if (_session != null)
                return _session.GetString(diskOff, diskLen);
            return MetadataBuffer.GetString(diskOff, diskLen);
        }

        private void SerWriteString(int slot, ref int diskOff, ref int diskLen, string? value, string? changed1 = null, string? changed2 = null)
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

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

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

        // Metadata for the currently active/probed episode
        private string _resolution = "";
        public string Resolution { get => _resolution; set { _resolution = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMetadata)); } }

        private string _fps = "";
        public string Fps { get => _fps; set { _fps = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMetadata)); } }

        private string _codec = "";
        public string Codec { get => _codec; set { _codec = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMetadata)); } }

        private bool _isHdr = false;
        public bool IsHdr 
        { 
            get => (_bitFlags & 2) != 0; 
            set { if (value) _bitFlags |= 2; else _bitFlags &= 253; OnPropertyChanged(); } 
        }

        public bool IsProbing 
        { 
            get => (_bitFlags & 4) != 0; 
            set { if (value) _bitFlags |= 4; else _bitFlags &= 251; OnPropertyChanged(); } 
        }

        public bool HasMetadata => !string.IsNullOrEmpty(Resolution);
        
        private long _bitrate;
        public long Bitrate { get => _bitrate; set { _bitrate = value; OnPropertyChanged(); } }

        public bool? IsOnline 
        { 
            get => (_bitFlags & 8) != 0; 
            set { if (value == true) _bitFlags |= 8; else _bitFlags &= 247; OnPropertyChanged(); } 
        }

        private readonly System.Threading.Lock _metaLock = new();
        public int MetadataPriority { get; set; } = 0;
        
        // IMediaStream Implementation
        public int Id => SeriesId;


        [JsonPropertyName("imdb_id")]
        public string? ImdbId 
        { 
            get => SerReadString(SerSlotImdb, _imdbOff, _imdbLen);
            set => SerWriteString(SerSlotImdb, ref _imdbOff, ref _imdbLen, value, nameof(ImdbId));
        }

        [JsonPropertyName("tmdb")]
        public string? TmdbIdRaw 
        { 
            get => SerReadString(SerSlotTmdbRaw, _tmdbRawOff, _tmdbRawLen);
            set => SerWriteString(SerSlotTmdbRaw, ref _tmdbRawOff, ref _tmdbRawLen, value, nameof(TmdbIdRaw));
        }

        [JsonPropertyName("tmdb_id")]
        public string? TmdbIdAlt 
        { 
            get => SerReadString(SerSlotTmdbAlt, _tmdbAltOff, _tmdbAltLen);
            set => SerWriteString(SerSlotTmdbAlt, ref _tmdbAltOff, ref _tmdbAltLen, value, nameof(TmdbIdAlt));
        }

        public string? IMDbId => !string.IsNullOrEmpty(ImdbId) ? ImdbId : (!string.IsNullOrEmpty(TmdbIdRaw) ? TmdbIdRaw : (!string.IsNullOrEmpty(TmdbIdAlt) ? TmdbIdAlt : string.Empty));
        
        [JsonIgnore]
        public string Title 
        { 
            get => Name; 
            set => Name = value; 
        }

        public string? Description 
        { 
            get => SerReadString(SerSlotDesc, _descOff, _descLen);
            set => SerWriteString(SerSlotDesc, ref _descOff, ref _descLen, value, nameof(Description));
        }

        [JsonIgnore]
        public string PosterUrl 
        { 
            get => Cover ?? ""; 
            set => Cover = value; 
        }
        
        public string? BackdropUrl 
        { 
            get 
            {
                var all = SerReadString(SerSlotBg, _bgOff, _bgLen);
                if (string.IsNullOrEmpty(all)) return null;
                if (all.Contains('|')) return all.Split('|')[0];
                return all;
            }
            set => SerWriteString(SerSlotBg, ref _bgOff, ref _bgLen, value, nameof(BackdropUrl));
        }

        [JsonIgnore]
        public System.Collections.Generic.List<string> BackdropUrls
        {
            get
            {
                var all = SerReadString(SerSlotBg, _bgOff, _bgLen);
                if (string.IsNullOrEmpty(all)) return new System.Collections.Generic.List<string>();
                return all.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            set
            {
                if (value == null || value.Count == 0) BackdropUrl = null;
                else BackdropUrl = string.Join("|", value);
            }
        }
        public string? Type => "series";

        [JsonIgnore]
        public string? Genres 
        { 
            get => SerReadString(SerSlotGenre, _genreOff, _genreLen);
            set => SerWriteString(SerSlotGenre, ref _genreOff, ref _genreLen, value, nameof(Genres), nameof(Genre));
        }
        
        [JsonIgnore]
        public string? TrailerUrl 
        { 
            get => SerReadString(SerSlotTrail, _trailOff, _trailLen);
            set => SerWriteString(SerSlotTrail, ref _trailOff, ref _trailLen, value, nameof(TrailerUrl));
        }
        
        string? IMediaStream.Cast 
        { 
            get => Cast; 
            set => Cast = value; 
        }
        string? IMediaStream.Director 
        { 
            get => Director; 
            set => Director = value; 
        }

        public string Year 
        { 
            get 
            {
                string? stored = null;
                if ((_roMask & (1 << SerSlotYear)) != 0)
                    stored = MetadataBuffer.GetString(_roBufOff[SerSlotYear], _roBufLen[SerSlotYear]);
                else if (_session != null)
                    stored = _session.GetString(_yearOff, _yearLen);
                else if (!string.IsNullOrEmpty(_year))
                    stored = _year;
                if (!string.IsNullOrEmpty(stored)) return stored;

                string? dateYear = Helpers.TitleHelper.ExtractYear(ReleaseDate ?? AirDate);
                if (!string.IsNullOrEmpty(dateYear)) return dateYear;
                return Helpers.TitleHelper.ExtractYear(Name) ?? "";
            }
            set 
            { 
                if (_session != null && _session.IsReadOnly)
                {
                    if (string.IsNullOrEmpty(value))
                    {
                        _roMask &= ~(1 << SerSlotYear);
                        _year = value;
                    }
                    else
                    {
                        var r = MetadataBuffer.Store(value);
                        _roBufOff[SerSlotYear] = r.Offset;
                        _roBufLen[SerSlotYear] = r.Length;
                        _roMask |= (1 << SerSlotYear);
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

        public string StreamUrl { get; set; } = "";
        
        // IMediaStream.Rating implementation
        [JsonIgnore]
        public string Rating { get => string.IsNullOrEmpty(RatingRaw) || RatingRaw == "N/A" || RatingRaw == "Unknown" ? "" : RatingRaw; set { if (RatingRaw != value) { RatingRaw = value; OnPropertyChanged(); } } }

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
        
        [JsonIgnore]
        public string? Genre 
        { 
            get => Genres;
            set => Genres = value;
        }

        [JsonPropertyName("container_extension")]
        public string? ContainerExtension
        {
            get => SerReadString(SerSlotExt, _extOff, _extLen);
            set => SerWriteString(SerSlotExt, ref _extOff, ref _extLen, value, nameof(ContainerExtension));
        }
        
        [JsonIgnore]
        public TmdbMovieResult TmdbInfo { get; set; }

        [JsonPropertyName("num")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Num { get; set; } // Sometimes int, sometimes string

        [JsonPropertyName("name")]
        public string Name 
        { 
            get => SerReadString(SerSlotName, _nameOff, _nameLen);
            set => SerWriteString(SerSlotName, ref _nameOff, ref _nameLen, value, nameof(Name), nameof(Title));
        }

        [JsonPropertyName("series_id")]
        public int SeriesId { get; set; }

        [JsonPropertyName("cover")]
        public string? Cover 
        { 
            get => SerReadString(SerSlotCover, _coverOff, _coverLen);
            set => SerWriteString(SerSlotCover, ref _coverOff, ref _coverLen, value, nameof(Cover), nameof(PosterUrl));
        }

        [JsonPropertyName("plot")]
        public string? Plot { get; set; }

        [JsonPropertyName("cast")]
        public string? Cast 
        { 
            get => SerReadString(SerSlotCast, _castOff, _castLen);
            set => SerWriteString(SerSlotCast, ref _castOff, ref _castLen, value, nameof(Cast));
        }

        [JsonPropertyName("director")]
        public string? Director 
        { 
            get => SerReadString(SerSlotDir, _dirOff, _dirLen);
            set => SerWriteString(SerSlotDir, ref _dirOff, ref _dirLen, value, nameof(Director));
        }

        

        [JsonPropertyName("releaseDate")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("air_date")]
        public string? AirDate { get; set; }

        [JsonPropertyName("last_modified")]
        public string? LastModified { get; set; }

        [JsonPropertyName("rating")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? RatingRaw
        {
            get => SerReadString(SerSlotRat, _ratOff, _ratLen);
            set => SerWriteString(SerSlotRat, ref _ratOff, ref _ratLen, value, nameof(RatingRaw), nameof(Rating));
        }

        [JsonPropertyName("rating_5based")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Rating5Based { get; set; }

        [JsonPropertyName("backdrop_path")]
        [JsonConverter(typeof(Helpers.UniversalStringListConverter))]
        public System.Collections.Generic.List<string>? BackdropPath { get; set; }

        [JsonPropertyName("youtube_trailer")]
        public string? YoutubeTrailer { get; set; }

        [JsonPropertyName("episode_run_time")]
        public string? EpisodeRunTime { get; set; }

        [JsonPropertyName("category_id")]
        public string? CategoryId 
        { 
            get => _categoryId;
            set { if (_categoryId != value) { _categoryId = value; OnPropertyChanged(); } }
        }
        private string? _categoryId;

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


        internal byte GetBitFlagsForBinarySave() => _bitFlags;

        public uint CalculateFingerprint()
        {
            string cleanTitle = Helpers.TitleHelper.Normalize(Name ?? "");
            string cleanYear = Year ?? "";
            string cleanImdb = ImdbId ?? "";
            
            // Simple robust hash for cross-sync verification
            uint hash = 2166136261;
            foreach (char c in cleanTitle) hash = (hash ^ c) * 16777619;
            foreach (char c in cleanYear) hash = (hash ^ c) * 16777619;
            foreach (char c in cleanImdb) hash = (hash ^ c) * 16777619;
            return hash;
        }

        public Models.Metadata.SeriesRecord ToRecord()
        {
            return new Models.Metadata.SeriesRecord
            {
                SeriesId = SeriesId,
                CategoryId = int.TryParse(_categoryId, out int catId) ? catId : 0,
                PriorityScore = MetadataPriority,
                Fingerprint = CalculateFingerprint(),
                LastModified = DateTime.UtcNow.Ticks,

                NameOff = _nameOff, NameLen = _nameLen,
                IconOff = _coverOff, IconLen = _coverLen,
                ImdbIdOff = _imdbOff, ImdbIdLen = _imdbLen,
                PlotOff = _descOff, PlotLen = _descLen,
                YearOff = _yearOff, YearLen = _yearLen,
                GenresOff = _genreOff, GenresLen = _genreLen,
                CastOff = _castOff, CastLen = _castLen,
                DirectorOff = _dirOff, DirectorLen = _dirLen,
                TrailerOff = _trailOff, TrailerLen = _trailLen,
                BackdropOff = _bgOff, BackdropLen = _bgLen,
                SourceTitleOff = _catOff, SourceTitleLen = _catLen,
                RatingOff = _ratOff, RatingLen = _ratLen,
                ExtOff = _extOff, ExtLen = _extLen,
                AirTime = 0, // TODO: Map episode runtime if available
                Flags = _bitFlags
            };
        }

        public void LoadFromRecord(Models.Metadata.SeriesRecord data)
        {
            SeriesId = data.SeriesId;
            _categoryId = data.CategoryId.ToString();
            MetadataPriority = data.PriorityScore;
            Fingerprint = data.Fingerprint;
            
            _nameOff = data.NameOff; _nameLen = data.NameLen;
            _coverOff = data.IconOff; _coverLen = data.IconLen;
            _imdbOff = data.ImdbIdOff; _imdbLen = data.ImdbIdLen;
            _descOff = data.PlotOff; _descLen = data.PlotLen;
            _yearOff = data.YearOff; _yearLen = data.YearLen;
            _genreOff = data.GenresOff; _genreLen = data.GenresLen;
            _castOff = data.CastOff; _castLen = data.CastLen;
            _dirOff = data.DirectorOff; _dirLen = data.DirectorLen;
            _trailOff = data.TrailerOff; _trailLen = data.TrailerLen;
            _bgOff = data.BackdropOff; _bgLen = data.BackdropLen;
            _catOff = data.SourceTitleOff; _catLen = data.SourceTitleLen;
            _ratOff = data.RatingOff; _ratLen = data.RatingLen;
            _extOff = data.ExtOff; _extLen = data.ExtLen;

            _bitFlags = data.Flags;
            
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Cover));
            OnPropertyChanged(nameof(Rating));
            OnPropertyChanged(nameof(Year));
            OnPropertyChanged(nameof(SourceTitle));
        }

        public void SetCacheSession(Helpers.BinaryCacheSession session, int recordIndex)
        {
            _session = session;
            _recordIndex = recordIndex;
        }

        [JsonIgnore]
        public string? SourceTitle
        {
            get => SerReadString(SerSlotSourceTitle, _catOff, _catLen);
            set => SerWriteString(SerSlotSourceTitle, ref _catOff, ref _catLen, value, nameof(SourceTitle));
        }
    }

    public class SeriesStreamConverter : JsonConverter<SeriesStream>
    {
        public override SeriesStream Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var stream = new SeriesStream { IsLoading = true };
            try
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string propName = reader.GetString();
                    reader.Read();

                    if (reader.TokenType == JsonTokenType.Null) continue;

                    switch (propName)
                    {
                        case "name":
                            stream.Name = reader.GetString();
                            break;
                        case "series_id":
                            stream.SeriesId = reader.GetInt32();
                            break;
                        case "cover":
                            stream.Cover = reader.GetString();
                            break;
                        case "plot":
                            stream.Plot = reader.GetString();
                            break;
                        case "cast":
                            stream.Cast = reader.GetString();
                            break;
                        case "genre":
                            stream.Genre = FastStringPool.Intern(reader.GetString());
                            break;
                        case "category_id":
                            stream.CategoryId = FastStringPool.Intern(reader.GetString());
                            break;
                        case "imdb_id":
                            stream.ImdbId = reader.GetString();
                            break;
                        case "rating":
                            stream.RatingRaw = reader.TokenType == JsonTokenType.Number ? reader.GetDouble().ToString() : reader.GetString();
                            break;
                        case "releaseDate":
                            stream.ReleaseDate = FastStringPool.Intern(reader.GetString());
                            break;
                        case "air_date":
                            stream.AirDate = FastStringPool.Intern(reader.GetString());
                            break;
                    }
                }
            }
            finally
            {
                stream.IsLoading = false;
            }
            return stream;
        }

        public override void Write(Utf8JsonWriter writer, SeriesStream value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);
            writer.WriteNumber("series_id", value.SeriesId);
            writer.WriteString("cover", value.Cover);
            writer.WriteString("genre", value.Genre);
            writer.WriteString("category_id", value.CategoryId);
            writer.WriteString("imdb_id", value.ImdbId);
            writer.WriteEndObject();
        }
    }
}
