using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer
{
    [JsonConverter(typeof(VodStreamConverter))]
    public class VodStream : INotifyPropertyChanged, IMediaStream
    {
        private readonly object _metaLock = new();
        public int MetadataPriority { get; set; } = 0;
        [JsonIgnore]
        public bool IsLoading { get; set; } = false;

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

        // Bit-packed flags (1: IsFavorite, 2: IsHdr, 4: IsProbing, 8: IsOnline (null as false), 16: IsAvailableOnIptv)
        private byte _bitFlags = 16; 
        
        // IMediaStream Implementation
        public int Id => StreamId;
        
        [JsonPropertyName("imdb_id")]
        public string? ImdbId 
        { 
            get => MetadataBuffer.GetString(_imdbOffset, _imdbLen);
            set 
            { 
                if (MetadataBuffer.IsEqual(_imdbOffset, _imdbLen, value)) return;
                var res = MetadataBuffer.Store(value); 
                _imdbOffset = res.Offset; _imdbLen = res.Length; 
                OnPropertyChanged();
            }
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
        public string? Type => "movie";
        
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

        public string StreamUrl { get; set; } = "";

        [JsonPropertyName("rating")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? RatingRaw 
        { 
            get => MetadataBuffer.GetString(_ratOff, _ratLen); 
            set { if (MetadataBuffer.IsEqual(_ratOff, _ratLen, value)) return; var r = MetadataBuffer.Store(value); _ratOff = r.Offset; _ratLen = r.Length; OnPropertyChanged(); OnPropertyChanged(nameof(Rating)); } 
        }

        [JsonIgnore]
        public string Rating { get => RatingRaw ?? ""; set => RatingRaw = value; }

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
        private static long _globalPropertyChangedCount = 0;

        public void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (IsLoading) return; // Suppress during bulk load

            _globalPropertyChangedCount++;
            if (_globalPropertyChangedCount % 5000 == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[VodStream] Property Change Count: {_globalPropertyChangedCount}, Property: {name}");
            }

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
            get => MetadataBuffer.GetString(_nameOffset, _nameLen);
            set 
            { 
                if (MetadataBuffer.IsEqual(_nameOffset, _nameLen, value)) return;
                var res = MetadataBuffer.Store(value); 
                _nameOffset = res.Offset; _nameLen = res.Length; 
                OnPropertyChanged(); OnPropertyChanged(nameof(Title)); 
            }
        }
        
        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }
        
        public string? IconUrl 
        { 
            get => MetadataBuffer.GetString(_iconOffset, _iconLen);
            set 
            { 
                if (MetadataBuffer.IsEqual(_iconOffset, _iconLen, value)) return;
                var res = MetadataBuffer.Store(value); 
                _iconOffset = res.Offset; _iconLen = res.Length; 
                OnPropertyChanged(); OnPropertyChanged(nameof(PosterUrl)); 
            }
        }

        public string? ContainerExtension 
        { 
            get => MetadataBuffer.GetString(_extOffset, _extLen);
            set 
            { 
                if (MetadataBuffer.IsEqual(_extOffset, _extLen, value)) return;
                var res = MetadataBuffer.Store(value); 
                _extOffset = res.Offset; _extLen = res.Length; 
                OnPropertyChanged();
            }
        }

        public string? CategoryId 
        { 
            get => MetadataBuffer.GetString(_catOffset, _catLen);
            set 
            { 
                if (MetadataBuffer.IsEqual(_catOffset, _catLen, value)) return;
                var res = MetadataBuffer.Store(value); 
                _catOffset = res.Offset; _catLen = res.Length; 
                OnPropertyChanged();
            }
        }
        
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
                    string propName = reader.GetString();
                    reader.Read();

                    if (reader.TokenType == JsonTokenType.Null) continue;

                    switch (propName)
                    {
                        case "name":
                            stream.Name = reader.GetString(); // MetadataBuffer internally handles this through property setter
                            break;
                        case "stream_id":
                            stream.StreamId = reader.GetInt32();
                            break;
                        case "stream_icon":
                            stream.IconUrl = reader.GetString();
                            break;
                        case "container_extension":
                            stream.ContainerExtension = FastStringPool.Intern(reader.GetString());
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
                        case "added":
                            stream.DateAdded = FastStringPool.Intern(reader.GetString());
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

        public override void Write(Utf8JsonWriter writer, VodStream value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);
            writer.WriteNumber("stream_id", value.StreamId);
            writer.WriteString("stream_icon", value.IconUrl);
            writer.WriteString("container_extension", value.ContainerExtension);
            writer.WriteString("category_id", value.CategoryId);
            writer.WriteString("imdb_id", value.ImdbId);
            writer.WriteString("rating", value.RatingRaw);
            writer.WriteEndObject();
        }
    }
}
