using ModernIPTVPlayer.Helpers;
using System.Text.Json.Serialization;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Models.Tmdb;
using ModernIPTVPlayer.Models.Stremio;
using System;
using System.Collections.Generic;
using ModernIPTVPlayer.Services;
using System.Linq;

namespace ModernIPTVPlayer.Models.Metadata
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class UnifiedMetadata
    {
        private readonly System.Threading.Lock _lock = new();
        [JsonIgnore] public object SyncRoot => _lock;

        // --- Core String Properties (Buffer Backed) ---
        private string _title; private int _titleOff, _titleLen;
        public string Title { get => _title ??= MetadataBuffer.GetString(_titleOff, _titleLen); set { var r = MetadataBuffer.Store(value); _titleOff = r.Offset; _titleLen = r.Length; _title = value; } }

        private string _sourceTitle; private int _sTitleOff, _sTitleLen;
        public string SourceTitle { get => _sourceTitle ??= MetadataBuffer.GetString(_sTitleOff, _sTitleLen); set { var r = MetadataBuffer.Store(value); _sTitleOff = r.Offset; _sTitleLen = r.Length; _sourceTitle = value; } }

        private string _origTitle; private int _oTitleOff, _oTitleLen;
        public string OriginalTitle { get => _origTitle ??= MetadataBuffer.GetString(_oTitleOff, _oTitleLen); set { var r = MetadataBuffer.Store(value); _oTitleOff = r.Offset; _oTitleLen = r.Length; _origTitle = value; } }

        private string _subTitle; private int _subOff, _subLen;
        public string SubTitle { get => _subTitle ??= MetadataBuffer.GetString(_subOff, _subLen); set { var r = MetadataBuffer.Store(value); _subOff = r.Offset; _subLen = r.Length; _subTitle = value; } }

        private string _overview; private int _ovOff, _ovLen;
        public string Overview { get => _overview ??= MetadataBuffer.GetString(_ovOff, _ovLen); set { var r = MetadataBuffer.Store(value); _ovOff = r.Offset; _ovLen = r.Length; _overview = value; } }

        private string _posterUrl; private int _pOff, _pLen;
        public string PosterUrl { get => _posterUrl ??= MetadataBuffer.GetString(_pOff, _pLen); set { var r = MetadataBuffer.Store(value); _pOff = r.Offset; _pLen = r.Length; _posterUrl = value; } }

        private string _backdropUrl; private int _bOff, _bLen;
        public string BackdropUrl { get => _backdropUrl ??= MetadataBuffer.GetString(_bOff, _bLen); set { var r = MetadataBuffer.Store(value); _bOff = r.Offset; _bLen = r.Length; _backdropUrl = value; } }

        public List<string> BackdropUrls { get; set; } = new List<string>();

        private string _logoUrl; private int _lOff, _lLen;
        public string LogoUrl { get => _logoUrl ??= MetadataBuffer.GetString(_lOff, _lLen); set { var r = MetadataBuffer.Store(value); _lOff = r.Offset; _lLen = r.Length; _logoUrl = value; } }

        // --- Stats & Numeric ---
        private int _ratOff, _ratLen;
        public double Rating 
        { 
            get => double.TryParse(MetadataBuffer.GetString(_ratOff, _ratLen), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0;
            set => (_ratOff, _ratLen) = MetadataBuffer.Store(value.ToString(System.Globalization.CultureInfo.InvariantCulture)); 
        }

        private string _year; private int _yOff, _yLen;
        public string Year { get => _year ??= MetadataBuffer.GetString(_yOff, _yLen); set { var r = MetadataBuffer.Store(value); _yOff = r.Offset; _yLen = r.Length; _year = value; } }

        private string _runtime; private int _rOff, _rLen;
        public string Runtime { get => _runtime ??= MetadataBuffer.GetString(_rOff, _rLen); set { var r = MetadataBuffer.Store(value); _rOff = r.Offset; _rLen = r.Length; _runtime = value; } }

        private string _ageRating; private int _arOff, _arLen;
        public string AgeRating { get => _ageRating ??= MetadataBuffer.GetString(_arOff, _arLen); set { var r = MetadataBuffer.Store(value); _arOff = r.Offset; _arLen = r.Length; _ageRating = value; } }

        private string _status; private int _stOff, _stLen;
        public string Status { get => _status ??= MetadataBuffer.GetString(_stOff, _stLen); set { var r = MetadataBuffer.Store(value); _stOff = r.Offset; _stLen = r.Length; _status = value; } }

        private string _genres; private int _gOff, _gLen;
        public string Genres { get => _genres ??= MetadataBuffer.GetString(_gOff, _gLen); set { var r = MetadataBuffer.Store(value); _gOff = r.Offset; _gLen = r.Length; _genres = value; } }

        private string _country; private int _cOff, _cLen;
        public string Country { get => _country ??= MetadataBuffer.GetString(_cOff, _cLen); set { var r = MetadataBuffer.Store(value); _cOff = r.Offset; _cLen = r.Length; _country = value; } }

        private string _writers; private int _wOff, _wLen;
        public string Writers { get => _writers ??= MetadataBuffer.GetString(_wOff, _wLen); set { var r = MetadataBuffer.Store(value); _wOff = r.Offset; _wLen = r.Length; _writers = value; } }

        private string _cert; private int _ceOff, _ceLen;
        public string Certification { get => _cert ??= MetadataBuffer.GetString(_ceOff, _ceLen); set { var r = MetadataBuffer.Store(value); _ceOff = r.Offset; _ceLen = r.Length; _cert = value; } }

        // --- Tracking & Enrichment ---
        public MetadataContext MaxEnrichmentContext { get; set; } = MetadataContext.Discovery;
        public int PriorityScore { get; set; }

        // --- IPTV & Source Tracking ---
        private string _streamUrl; private int _strmOff, _strmLen;
        public string StreamUrl { get => _streamUrl ??= MetadataBuffer.GetString(_strmOff, _strmLen); set { var r = MetadataBuffer.Store(value); _strmOff = r.Offset; _strmLen = r.Length; _streamUrl = value; } }
        public bool IsAvailableOnIptv { get; set; }
        public bool IsFromIptv { get; set; }

        [JsonPropertyName("iptvVods")] public System.Text.Json.JsonElement IptvVodsJson { set => (_ivodsOff, _ivodsLen) = MetadataBuffer.StoreJson(value.GetRawText()); }
        private int _ivodsOff = -1, _ivodsLen;
        private List<VodStream> _iptvVods;
        [JsonIgnore] public List<VodStream> IptvVods { get => _iptvVods ??= DeserializeList(_ivodsOff, _ivodsLen, Services.Json.AppJsonContext.Default.ListVodStream); set { _iptvVods = value; var r = SerializeList(value, Services.Json.AppJsonContext.Default.ListVodStream); _ivodsOff = r.Offset; _ivodsLen = r.Length; } }

        [JsonPropertyName("iptvSeries")] public System.Text.Json.JsonElement IptvSeriesJson { set => (_iseriesOff, _iseriesLen) = MetadataBuffer.StoreJson(value.GetRawText()); }
        private int _iseriesOff = -1, _iseriesLen;
        private List<SeriesStream> _iptvSeries;
        [JsonIgnore] public List<SeriesStream> IptvSeries { get => _iptvSeries ??= DeserializeList(_iseriesOff, _iseriesLen, Services.Json.AppJsonContext.Default.ListSeriesStream); set { _iptvSeries = value; var r = SerializeList(value, Services.Json.AppJsonContext.Default.ListSeriesStream); _iseriesOff = r.Offset; _iseriesLen = r.Length; } }

        private string _dataSource; private int _dsOff, _dsLen;
        public string DataSource { get => _dataSource ??= MetadataBuffer.GetString(_dsOff, _dsLen); set { var r = MetadataBuffer.Store(value); _dsOff = r.Offset; _dsLen = r.Length; _dataSource = value; } }

        private string _metadataInfo; private int _msiOff, _msiLen;
        public string MetadataSourceInfo { get => _metadataInfo ?? MetadataBuffer.GetString(_msiOff, _msiLen); set { var r = MetadataBuffer.Store(value); _msiOff = r.Offset; _msiLen = r.Length; _metadataInfo = value; } }

        private string _catalogInfo; private int _csiOff, _csiLen;
        public string CatalogSourceInfo { get => _catalogInfo ?? MetadataBuffer.GetString(_csiOff, _csiLen); set { var r = MetadataBuffer.Store(value); _csiOff = r.Offset; _csiLen = r.Length; _catalogInfo = value; } }

        private string _catalogAddonUrl; private int _csaOff, _csaLen;
        public string CatalogSourceAddonUrl { get => _catalogAddonUrl ?? MetadataBuffer.GetString(_csaOff, _csaLen); set { var r = MetadataBuffer.Store(value); _csaOff = r.Offset; _csaLen = r.Length; _catalogAddonUrl = value; } }

        private string _primaryAddonUrl; private int _pmaOff, _pmaLen;
        public string PrimaryMetadataAddonUrl { get => _primaryAddonUrl ?? MetadataBuffer.GetString(_pmaOff, _pmaLen); set { var r = MetadataBuffer.Store(value); _pmaOff = r.Offset; _pmaLen = r.Length; _primaryAddonUrl = value; } }
        
        [JsonIgnore] public MetadataField CheckedFields { get; set; } = MetadataField.None;

        [JsonIgnore] public HashSet<string> ProbedAddons { get; set; } = new HashSet<string>();
        [JsonIgnore] public string DurationFormatted => Runtime;

        // --- Technical & Collections ---
        private string _res; private int _resOff, _resLen;
        public string Resolution { get => _res ?? MetadataBuffer.GetString(_resOff, _resLen); set { var r = MetadataBuffer.Store(value); _resOff = r.Offset; _resLen = r.Length; _res = value; } }

        private string _vc; private int _vcOff, _vcLen;
        public string VideoCodec { get => _vc ?? MetadataBuffer.GetString(_vcOff, _vcLen); set { var r = MetadataBuffer.Store(value); _vcOff = r.Offset; _vcLen = r.Length; _vc = value; } }

        private string _ac; private int _acOff, _acLen;
        public string AudioCodec { get => _ac ?? MetadataBuffer.GetString(_acOff, _acLen); set { var r = MetadataBuffer.Store(value); _acOff = r.Offset; _acLen = r.Length; _ac = value; } }

        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }

        private int _castOff = -1, _castLen;
        private List<UnifiedCast> _cast;
        [JsonPropertyName("cast")] public System.Text.Json.JsonElement CastJson { set => (_castOff, _castLen) = MetadataBuffer.StoreJson(value.GetRawText()); }
        [JsonIgnore] public List<UnifiedCast> Cast { get => _cast ??= DeserializeList(_castOff, _castLen, Services.Json.AppJsonContext.Default.ListUnifiedCast); set { _cast = value; var r = SerializeList(value, Services.Json.AppJsonContext.Default.ListUnifiedCast); _castOff = r.Offset; _castLen = r.Length; } }

        private int _dirOff = -1, _dirLen;
        private List<UnifiedCast> _directors;
        [JsonPropertyName("directors")] public System.Text.Json.JsonElement DirectorsJson { set => (_dirOff, _dirLen) = MetadataBuffer.StoreJson(value.GetRawText()); }
        [JsonIgnore] public List<UnifiedCast> Directors { get => _directors ??= DeserializeList(_dirOff, _dirLen, Services.Json.AppJsonContext.Default.ListUnifiedCast); set { _directors = value; var r = SerializeList(value, Services.Json.AppJsonContext.Default.ListUnifiedCast); _dirOff = r.Offset; _dirLen = r.Length; } }

        private string _trUrl; private int _trOff, _trLen;
        public string TrailerUrl { get => _trUrl ?? MetadataBuffer.GetString(_trOff, _trLen); set { var r = MetadataBuffer.Store(value); _trOff = r.Offset; _trLen = r.Length; _trUrl = value; } }

        private int _trcOff = -1, _trcLen;
        private List<string> _trailerCandidates;
        [JsonPropertyName("trailerCandidates")] public System.Text.Json.JsonElement TrailerCandidatesJson { set => (_trcOff, _trcLen) = MetadataBuffer.StoreJson(value.GetRawText()); }
        [JsonIgnore] public List<string> TrailerCandidates { get => _trailerCandidates ??= DeserializeList(_trcOff, _trcLen, Services.Json.AppJsonContext.Default.ListString); set { _trailerCandidates = value; var r = SerializeList(value, Services.Json.AppJsonContext.Default.ListString); _trcOff = r.Offset; _trcLen = r.Length; } }

        private string _imdb; private int _imdbOff, _imdbLen;
        public string ImdbId { get => _imdb ?? MetadataBuffer.GetString(_imdbOff, _imdbLen); set { var r = MetadataBuffer.Store(value); _imdbOff = r.Offset; _imdbLen = r.Length; _imdb = value; } }

        private string _metaId; private int _metaIdOff, _metaIdLen;
        public string MetadataId { get => _metaId ?? MetadataBuffer.GetString(_metaIdOff, _metaIdLen); set { var r = MetadataBuffer.Store(value); _metaIdOff = r.Offset; _metaIdLen = r.Length; _metaId = value; } }

        public bool IsSeries { get; set; }
        public TmdbMovieResult TmdbInfo { get; set; }
        
        private int _seaOff = -1, _seaLen;
        private List<UnifiedSeason> _seasons;
        [JsonPropertyName("seasons")] public System.Text.Json.JsonElement SeasonsJson { set => (_seaOff, _seaLen) = MetadataBuffer.StoreJson(value.GetRawText()); }
        [JsonIgnore] public List<UnifiedSeason> Seasons { get => _seasons ??= DeserializeList(_seaOff, _seaLen, Services.Json.AppJsonContext.Default.ListUnifiedSeason); set { _seasons = value; var r = SerializeList(value, Services.Json.AppJsonContext.Default.ListUnifiedSeason); _seaOff = r.Offset; _seaLen = r.Length; } }

        // --- PROJECT ZERO: Specialized Typed Serialization ---
        private static (int Offset, int Length) SerializeList<T>(IEnumerable<T>? list, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> typeInfo)
        {
            if (list == null || !list.Any()) return (-1, 0);
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(list.ToList(), typeInfo);
                return MetadataBuffer.StoreJson(json);
            }
            catch { return (-1, 0); }
        }

        private static List<T> DeserializeList<T>(int offset, int length, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> typeInfo)
        {
            if (offset < 0 || length <= 0) return new List<T>();
            string json = string.Empty;
            try
            {
                json = MetadataBuffer.GetString(offset, length);
                if (string.IsNullOrWhiteSpace(json)) return new List<T>();

                string trimmed = json.TrimStart();
                if (trimmed.Length == 0) return new List<T>();

                // [PROJECT ZERO: Polymorphic List Handling]
                // Detect if the JSON is an array [ ... ] or a single string " ... "
                if (trimmed[0] == '[')
                {
                    return System.Text.Json.JsonSerializer.Deserialize(json, typeInfo) ?? new List<T>();
                }
                else if (typeof(T) == typeof(string))
                {
                    // If we expect a List<string> but got a single string (possibly comma-separated),
                    // parse the string and split it. This avoids expensive JsonExceptions.
                    try
                    {
                        string single = System.Text.Json.JsonSerializer.Deserialize(json, Services.Json.AppJsonContext.Default.String) ?? string.Empty;
                        if (string.IsNullOrEmpty(single)) return new List<T>();

                        var list = single.Split(new[] { ',', ';' }, System.StringSplitOptions.RemoveEmptyEntries)
                                         .Select(s => s.Trim())
                                         .ToList();
                        return (List<T>)(object)list;
                    }
                    catch { return new List<T>(); }
                }

                return new List<T>();
            }
            catch (System.Text.Json.JsonException ex)
            {
                // [DEEP DIAGNOSTICS] Capture real structural errors
                string snippet = (json.Length > 200) ? json.Substring(0, 200) + "..." : json;
                AppLogger.Error($"[UnifiedMetadata] JSON Parse Error: {ex.Message} at {ex.Path} | Snippet: {snippet}", ex);
                return new List<T>();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[UnifiedMetadata] Unexpected Error: {ex.Message}", ex);
                return new List<T>();
            }
        }

        public static UnifiedMetadata FromStream(IMediaStream stream)
        {
            if (stream == null) return null;
            var meta = new UnifiedMetadata {
                Title = stream.Title, Overview = stream.Description, PosterUrl = stream.PosterUrl,
                BackdropUrl = stream.BackdropUrl ?? stream.PosterUrl, Year = stream.Year, Genres = stream.Genres,
                MetadataId = stream.Id.ToString(), ImdbId = stream.IMDbId, IsSeries = stream is SeriesStream,
                SourceTitle = stream.SourceTitle, Resolution = stream.Resolution, DataSource = "Seed",
                LogoUrl = (stream is StremioMediaStream s) ? s.LogoUrl : null
            };
            if (double.TryParse(stream.Rating, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
                meta.Rating = r;
            return meta;
        }
    }

    public class UnifiedSeason
    {
        public int SeasonNumber { get; set; }
        private string _name; private int _nameOff, _nameLen;
        public string Name { get => _name ?? MetadataBuffer.GetString(_nameOff, _nameLen); set => (_nameOff, _nameLen) = MetadataBuffer.Store(value); }
        private string _post; private int _postOff, _postLen;
        public string PosterUrl { get => _post ?? MetadataBuffer.GetString(_postOff, _postLen); set => (_postOff, _postLen) = MetadataBuffer.Store(value); }
        public List<UnifiedEpisode> Episodes { get; set; } = new List<UnifiedEpisode>();
        public bool IsEnrichedByTmdb { get; set; }
    }

    public class UnifiedEpisode
    {
        private string _id; private int _idOff, _idLen;
        public string Id { get => _id ?? MetadataBuffer.GetString(_idOff, _idLen); set => (_idOff, _idLen) = MetadataBuffer.Store(value); }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        private string _tit; private int _titOff, _titLen;
        public string Title { get => _tit ?? MetadataBuffer.GetString(_titOff, _titLen); set => (_titOff, _titLen) = MetadataBuffer.Store(value); }
        private string _ov; private int _ovOff, _ovLen;
        public string Overview { get => _ov ?? MetadataBuffer.GetString(_ovOff, _ovLen); set => (_ovOff, _ovLen) = MetadataBuffer.Store(value); }
        private string _thumb; private int _thumbOff, _thumbLen;
        public string ThumbnailUrl { get => _thumb ?? MetadataBuffer.GetString(_thumbOff, _thumbLen); set => (_thumbOff, _thumbLen) = MetadataBuffer.Store(value); }
        public DateTime? AirDate { get; set; }
        public DateTime? Releasedate { get; set; }
        public bool IsAvailable { get; set; } = true;
        private string _run; private int _runOff, _runLen;
        public string Runtime { get => _run ?? MetadataBuffer.GetString(_runOff, _runLen); set => (_runOff, _runLen) = MetadataBuffer.Store(value); }
        private string _strm; private int _strmOff, _strmLen;
        public string StreamUrl { get => _strm ?? MetadataBuffer.GetString(_strmOff, _strmLen); set => (_strmOff, _strmLen) = MetadataBuffer.Store(value); }
        private string _rf; private int _rfOff, _rfLen;
        public string RuntimeFormatted { get => _rf ?? MetadataBuffer.GetString(_rfOff, _rfLen); set => (_rfOff, _rfLen) = MetadataBuffer.Store(value); }
        private string _res; private int _resOff, _resLen;
        public string Resolution { get => _res ?? MetadataBuffer.GetString(_resOff, _resLen); set => (_resOff, _resLen) = MetadataBuffer.Store(value); }
        private string _vc; private int _vcOff, _vcLen;
        public string VideoCodec { get => _vc ?? MetadataBuffer.GetString(_vcOff, _vcLen); set => (_vcOff, _vcLen) = MetadataBuffer.Store(value); }
        private string _ac; private int _acOff, _acLen;
        public string AudioCodec { get => _ac ?? MetadataBuffer.GetString(_acOff, _acLen); set => (_acOff, _acLen) = MetadataBuffer.Store(value); }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }
        private string _ist; private int _istOff, _istLen;
        public string IptvSourceTitle { get => _ist ?? MetadataBuffer.GetString(_istOff, _istLen); set => (_istOff, _istLen) = MetadataBuffer.Store(value); }
        public int IptvSeriesId { get; set; }
    }

    public class UnifiedCast
    {
        private string _name; private int _nameOff, _nameLen;
        public string Name { get => _name ?? MetadataBuffer.GetString(_nameOff, _nameLen); set => (_nameOff, _nameLen) = MetadataBuffer.Store(value); }
        private string _char; private int _charOff, _charLen;
        public string Character { get => _char ?? MetadataBuffer.GetString(_charOff, _charLen); set => (_charOff, _charLen) = MetadataBuffer.Store(value); }
        private string _prof; private int _profOff, _profLen;
        public string ProfileUrl { get => _prof ?? MetadataBuffer.GetString(_profOff, _profLen); set => (_profOff, _profLen) = MetadataBuffer.Store(value); }
        public int? TmdbId { get; set; }
    }
}
