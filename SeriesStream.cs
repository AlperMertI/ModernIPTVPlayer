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

        // Bit-packed flags (1: IsFavorite, 2: IsHdr, 4: IsProbing, 8: IsOnline (null as false), 16: IsAvailableOnIptv)
        private byte _bitFlags = 16;
        [JsonIgnore]
        public bool IsLoading { get; set; } = false;
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

        private readonly object _metaLock = new();
        public int MetadataPriority { get; set; } = 0;
        
        // IMediaStream Implementation
        public int Id => SeriesId;

        private int _imdbOff, _imdbLen;
        private int _tmdbRawOff, _tmdbRawLen;
        private int _tmdbAltOff, _tmdbAltLen;
        private int _descOff, _descLen;
        private int _bgOff, _bgLen;
        private int _trailOff, _trailLen;

        [JsonPropertyName("imdb_id")]
        public string? ImdbId 
        { 
            get => MetadataBuffer.GetString(_imdbOff, _imdbLen); 
            set { if (MetadataBuffer.IsEqual(_imdbOff, _imdbLen, value)) return; var r = MetadataBuffer.Store(value); _imdbOff = r.Offset; _imdbLen = r.Length; OnPropertyChanged(); } 
        }

        [JsonPropertyName("tmdb")]
        public string? TmdbIdRaw 
        { 
            get => MetadataBuffer.GetString(_tmdbRawOff, _tmdbRawLen); 
            set { if (MetadataBuffer.IsEqual(_tmdbRawOff, _tmdbRawLen, value)) return; var r = MetadataBuffer.Store(value); _tmdbRawOff = r.Offset; _tmdbRawLen = r.Length; OnPropertyChanged(); } 
        }

        [JsonPropertyName("tmdb_id")]
        public string? TmdbIdAlt 
        { 
            get => MetadataBuffer.GetString(_tmdbAltOff, _tmdbAltLen); 
            set { if (MetadataBuffer.IsEqual(_tmdbAltOff, _tmdbAltLen, value)) return; var r = MetadataBuffer.Store(value); _tmdbAltOff = r.Offset; _tmdbAltLen = r.Length; OnPropertyChanged(); } 
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
            get => MetadataBuffer.GetString(_descOff, _descLen); 
            set { if (MetadataBuffer.IsEqual(_descOff, _descLen, value)) return; var r = MetadataBuffer.Store(value); _descOff = r.Offset; _descLen = r.Length; OnPropertyChanged(); } 
        }

        [JsonIgnore]
        public string PosterUrl 
        { 
            get => Cover ?? ""; 
            set => Cover = value; 
        }
        
        public string? BackdropUrl 
        { 
            get => MetadataBuffer.GetString(_bgOff, _bgLen); 
            set { if (MetadataBuffer.IsEqual(_bgOff, _bgLen, value)) return; var r = MetadataBuffer.Store(value); _bgOff = r.Offset; _bgLen = r.Length; OnPropertyChanged(); } 
        }
        public string? Type => "series";

        [JsonIgnore]
        public string? Genres 
        { 
            get => MetadataBuffer.GetString(_genreOff, _genreLen); 
            set { if (MetadataBuffer.IsEqual(_genreOff, _genreLen, value)) return; var r = MetadataBuffer.Store(value); _genreOff = r.Offset; _genreLen = r.Length; OnPropertyChanged(); } 
        }
        
        [JsonIgnore]
        public string? TrailerUrl 
        { 
            get => MetadataBuffer.GetString(_trailOff, _trailLen); 
            set { if (MetadataBuffer.IsEqual(_trailOff, _trailLen, value)) return; var r = MetadataBuffer.Store(value); _trailOff = r.Offset; _trailLen = r.Length; OnPropertyChanged(); } 
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
                if (!string.IsNullOrEmpty(_year)) return _year;
                string? dateYear = Helpers.TitleHelper.ExtractYear(ReleaseDate ?? AirDate);
                if (!string.IsNullOrEmpty(dateYear)) return dateYear;
                // Fallback to title extraction for bulk list items
                return Helpers.TitleHelper.ExtractYear(Name) ?? "";
            }
            set => _year = value; 
        }
        private string? _year;
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
        public TmdbMovieResult TmdbInfo { get; set; }

        [JsonPropertyName("num")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Num { get; set; } // Sometimes int, sometimes string

        [JsonPropertyName("name")]
        public string Name 
        { 
            get => MetadataBuffer.GetString(_nameOff, _nameLen);
            set 
            { 
                if (MetadataBuffer.IsEqual(_nameOff, _nameLen, value)) return;
                var res = MetadataBuffer.Store(value); 
                _nameOff = res.Offset; _nameLen = res.Length; 
                OnPropertyChanged(); OnPropertyChanged(nameof(Title)); 
            }
        }

        [JsonPropertyName("series_id")]
        public int SeriesId { get; set; }

        [JsonPropertyName("cover")]
        public string? Cover 
        { 
            get => MetadataBuffer.GetString(_coverOff, _coverLen);
            set 
            { 
                if (MetadataBuffer.IsEqual(_coverOff, _coverLen, value)) return;
                var res = MetadataBuffer.Store(value); 
                _coverOff = res.Offset; _coverLen = res.Length; 
                OnPropertyChanged(); OnPropertyChanged(nameof(PosterUrl)); 
            }
        }

        [JsonPropertyName("plot")]
        public string? Plot { get; set; }

        [JsonPropertyName("cast")]
        public string? Cast 
        { 
            get => MetadataBuffer.GetString(_castOff, _castLen);
            set 
            { 
                if (MetadataBuffer.IsEqual(_castOff, _castLen, value)) return;
                var res = MetadataBuffer.Store(value); 
                _castOff = res.Offset; _castLen = res.Length; 
                OnPropertyChanged(); 
            }
        }

        [JsonPropertyName("director")]
        public string? Director 
        { 
            get => MetadataBuffer.GetString(_dirOff, _dirLen);
            set 
            { 
                if (MetadataBuffer.IsEqual(_dirOff, _dirLen, value)) return;
                var res = MetadataBuffer.Store(value); 
                _dirOff = res.Offset; _dirLen = res.Length; 
                OnPropertyChanged(); 
            }
        }

        [JsonPropertyName("genre")]
        public string? Genre 
        { 
            get => MetadataBuffer.GetString(_genreOff, _genreLen);
            set 
            { 
                if (MetadataBuffer.IsEqual(_genreOff, _genreLen, value)) return;
                var res = MetadataBuffer.Store(value); 
                _genreOff = res.Offset; _genreLen = res.Length; 
                OnPropertyChanged(); OnPropertyChanged(nameof(Genres)); 
            }
        }

        [JsonPropertyName("releaseDate")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("air_date")]
        public string? AirDate { get; set; }

        [JsonPropertyName("last_modified")]
        public string? LastModified { get; set; }

        [JsonPropertyName("rating")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? RatingRaw { get; set; }

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
            get => MetadataBuffer.GetString(_catOff, _catLen);
            set 
            { 
                if (MetadataBuffer.IsEqual(_catOff, _catLen, value)) return;
                var res = MetadataBuffer.Store(value); 
                _catOff = res.Offset; _catLen = res.Length; 
                OnPropertyChanged();
            }
        }

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
