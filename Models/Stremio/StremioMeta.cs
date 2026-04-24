using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ModernIPTVPlayer.Helpers;
using System.Linq;

namespace ModernIPTVPlayer.Models.Stremio
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public partial class StremioMetaResponse
    {
        public StremioMeta Meta { get; set; }
        
        public List<StremioMeta> Metas { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public partial class StremioMeta
    {
        private string _id;
        private int _idOff, _idLen;
        public string Id { get => _id ??= MetadataBuffer.GetString(_idOff, _idLen); set { if (MetadataBuffer.IsEqual(_idOff, _idLen, value)) return; var r = MetadataBuffer.Store(value); _idOff = r.Offset; _idLen = r.Length; _id = value; } }

        private string _type;
        private int _typeOff, _typeLen;
        public string Type { get => _type ??= MetadataBuffer.GetString(_typeOff, _typeLen); set { if (MetadataBuffer.IsEqual(_typeOff, _typeLen, value)) return; var r = MetadataBuffer.Store(value); _typeOff = r.Offset; _typeLen = r.Length; _type = value; } }

        private string _name;
        private int _nameOff, _nameLen;
        public string Name { get => _name ??= MetadataBuffer.GetString(_nameOff, _nameLen); set { if (MetadataBuffer.IsEqual(_nameOff, _nameLen, value)) return; var r = MetadataBuffer.Store(value); _nameOff = r.Offset; _nameLen = r.Length; _name = value; } }

        private string _oname;
        private int _origOff, _origLen;
        public string Originalname { get => _oname ??= MetadataBuffer.GetString(_origOff, _origLen); set { if (MetadataBuffer.IsEqual(_origOff, _origLen, value)) return; var r = MetadataBuffer.Store(value); _origOff = r.Offset; _origLen = r.Length; _oname = value; } }

        [JsonPropertyName("aliases")]
        public System.Text.Json.JsonElement AliasesJson { set => (aliases_off, aliases_len) = MetadataBuffer.StoreJson(value.GetRawText()); }
        private int aliases_off = -1, aliases_len;
        private List<string> _aliases;
        [JsonIgnore] public List<string> Aliases { get => _aliases ??= DeserializeList(aliases_off, aliases_len, Services.Json.AppJsonContext.Default.ListString); set => _aliases = value; }

        private int _posterOff, _posterLen;
        private string _poster;
        public string Poster 
        { 
            get => _poster ??= MetadataBuffer.GetString(_posterOff, _posterLen); 
            set { if (MetadataBuffer.IsEqual(_posterOff, _posterLen, value)) return; var r = MetadataBuffer.Store(value); _posterOff = r.Offset; _posterLen = r.Length; _poster = value; } 
        }

        private int _bgOff, _bgLen;
        private string _bg;
        public string Background 
        { 
            get => _bg ??= MetadataBuffer.GetString(_bgOff, _bgLen); 
            set { if (MetadataBuffer.IsEqual(_bgOff, _bgLen, value)) return; var r = MetadataBuffer.Store(value); _bgOff = r.Offset; _bgLen = r.Length; _bg = value; } 
        }

        private int _logoOff, _logoLen;
        private string _logo;
        public string Logo 
        { 
            get => _logo ??= MetadataBuffer.GetString(_logoOff, _logoLen); 
            set { if (MetadataBuffer.IsEqual(_logoOff, _logoLen, value)) return; var r = MetadataBuffer.Store(value); _logoOff = r.Offset; _logoLen = r.Length; _logo = value; } 
        }

        private int _descOff, _descLen;
        private string _desc;
        public string Description 
        { 
            get => _desc ??= MetadataBuffer.GetString(_descOff, _descLen); 
            set { if (MetadataBuffer.IsEqual(_descOff, _descLen, value)) return; var r = MetadataBuffer.Store(value); _descOff = r.Offset; _descLen = r.Length; _desc = value; } 
        }

        private int _relOff, _relLen;
        private string _rel;
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Releaseinfo 
        { 
            get => _rel ??= MetadataBuffer.GetString(_relOff, _relLen); 
            set { if (MetadataBuffer.IsEqual(_relOff, _relLen, value)) return; var r = MetadataBuffer.Store(value); _relOff = r.Offset; _relLen = r.Length; _rel = value; } 
        }

        private int _yearOff, _yearLen;
        private string _year;
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Year 
        { 
            get => _year ??= MetadataBuffer.GetString(_yearOff, _yearLen); 
            set { if (MetadataBuffer.IsEqual(_yearOff, _yearLen, value)) return; var r = MetadataBuffer.Store(value); _yearOff = r.Offset; _yearLen = r.Length; _year = value; } 
        }

        private int _resOff, _resLen;
        private string _res;
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Released 
        { 
            get => _res ??= MetadataBuffer.GetString(_resOff, _resLen); 
            set { if (MetadataBuffer.IsEqual(_resOff, _resLen, value)) return; var r = MetadataBuffer.Store(value); _resOff = r.Offset; _resLen = r.Length; _res = value; } 
        }

        private int _ratOff, _ratLen;
        [JsonIgnore]
        public double Imdbrating 
        { 
            get => double.TryParse(MetadataBuffer.GetString(_ratOff, _ratLen), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0;
            set => (_ratOff, _ratLen) = MetadataBuffer.Store(value.ToString(System.Globalization.CultureInfo.InvariantCulture)); 
        }

        [JsonPropertyName("imdbRating")]
        public System.Text.Json.JsonElement ImdbratingJson { set => Imdbrating = double.TryParse(value.GetRawText().Trim('"'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0; }

        private int _genOff, _genLen;
        [JsonIgnore]
        public string Genres 
        { 
            get => MetadataBuffer.GetString(_genOff, _genLen); 
            set { if (MetadataBuffer.IsEqual(_genOff, _genLen, value)) return; var r = MetadataBuffer.Store(value); _genOff = r.Offset; _genLen = r.Length; } 
        }

        [JsonPropertyName("genres")]
        public System.Text.Json.JsonElement GenresJson 
        { 
            set 
            { 
                if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var item in value.EnumerateArray()) list.Add(item.GetString());
                    Genres = string.Join(", ", list);
                }
                else if (value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    Genres = value.GetString();
                }
            } 
        }

        private int _runOff, _runLen;
        public string Runtime 
        { 
            get => MetadataBuffer.GetString(_runOff, _runLen); 
            set { if (MetadataBuffer.IsEqual(_runOff, _runLen, value)) return; var r = MetadataBuffer.Store(value); _runOff = r.Offset; _runLen = r.Length; } 
        }
        
        [JsonPropertyName("cast")]
        public System.Text.Json.JsonElement CastJson { set => (cast_off, cast_len) = MetadataBuffer.StoreJson(value.GetRawText()); }
        private int cast_off = -1, cast_len;
        private List<string> _cast;
        [JsonIgnore] public List<string> Cast { get => _cast ??= DeserializeList(cast_off, cast_len, Services.Json.AppJsonContext.Default.ListString); set => _cast = value; }
        
        [JsonPropertyName("director")]
        public System.Text.Json.JsonElement DirectorJson { set => (dir_off, dir_len) = MetadataBuffer.StoreJson(value.GetRawText()); }
        private int dir_off = -1, dir_len;
        private List<string> _director;
        [JsonIgnore] public List<string> Director { get => _director ??= DeserializeList(dir_off, dir_len, Services.Json.AppJsonContext.Default.ListString); set => _director = value; }

        private int _vidOff, _vidLen;
        /// <summary>
        /// PROJECT ZERO: Videos list stored as raw JSON in MetadataBuffer.
        /// Materialized only when requested by MediaInfoPage.
        /// </summary>
        [JsonPropertyName("videos")]
        public System.Text.Json.JsonElement VideosJson 
        { 
            set { var r = MetadataBuffer.StoreJson(value.GetRawText()); _vidOff = r.Offset; _vidLen = r.Length; }
        }

        private List<StremioVideo> _videos;
        [JsonIgnore]
        public List<StremioVideo> Videos => _videos ??= LazyParseVideos();

        private List<StremioVideo> LazyParseVideos()
        {
            if (_vidOff < 0 || _vidLen <= 0) return null;
            var json = MetadataBuffer.GetString(_vidOff, _vidLen);
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return System.Text.Json.JsonSerializer.Deserialize(json, Services.Json.AppJsonContext.Default.ListStremioVideo); }
            catch { return null; }
        }

        [JsonPropertyName("trailers")]
        public System.Text.Json.JsonElement TrailersJson 
        { 
            set 
            { 
                var r = MetadataBuffer.StoreJson(value.GetRawText()); 
                tr_off = r.Offset; 
                tr_len = r.Length; 
                // System.Diagnostics.Debug.WriteLine($"[STREMIO_META_DEBUG] Stored Trailers Metadata: Offset={tr_off}, Len={tr_len}");
            } 
        }
        private int tr_off = -1, tr_len;
        private List<StremioMetaTrailer> _trailers;
        [JsonIgnore] public List<StremioMetaTrailer> Trailers { get => _trailers ??= DeserializeList(tr_off, tr_len, Services.Json.AppJsonContext.Default.ListStremioMetaTrailer); set => _trailers = value; }

        private string _imdbid;
        private int _imdbOff, _imdbLen;
        public string ImdbId { get => _imdbid ?? MetadataBuffer.GetString(_imdbOff, _imdbLen); set { var r = MetadataBuffer.Store(value); _imdbOff = r.Offset; _imdbLen = r.Length; _imdbid = value; } }

        private int _statOff, _statLen;
        private string _status;
        public string Status { get => _status ?? MetadataBuffer.GetString(_statOff, _statLen); set { var r = MetadataBuffer.Store(value); _statOff = r.Offset; _statLen = r.Length; _status = value; } }

        [JsonPropertyName("moviedb_id")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? MoviedbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? TvdbId { get; set; }

        private int _webOff, _webLen;
        public string Website 
        { 
            get => MetadataBuffer.GetString(_webOff, _webLen); 
            set { if (MetadataBuffer.IsEqual(_webOff, _webLen, value)) return; var r = MetadataBuffer.Store(value); _webOff = r.Offset; _webLen = r.Length; } 
        }

        [JsonPropertyName("links")]
        public System.Text.Json.JsonElement LinksJson { set => (links_off, links_len) = MetadataBuffer.StoreJson(value.GetRawText()); }
        private int links_off = -1, links_len;
        private List<StremioLink> _links;
        [JsonIgnore] public List<StremioLink> Links => _links ??= DeserializeList(links_off, links_len, Services.Json.AppJsonContext.Default.ListStremioLink);

        [JsonPropertyName("trailerStreams")]
        public System.Text.Json.JsonElement TrailerStreamsJson { set => (trstr_off, trstr_len) = MetadataBuffer.StoreJson(value.GetRawText()); }
        private int trstr_off = -1, trstr_len;
        private List<StremioTrailerStream> _trailerStreams;
        [JsonIgnore] public List<StremioTrailerStream> TrailerStreams => _trailerStreams ??= DeserializeList(trstr_off, trstr_len, Services.Json.AppJsonContext.Default.ListStremioTrailerStream);

        [JsonPropertyName("creditsCast")]
        public System.Text.Json.JsonElement CreditsCastJson { set => (crcast_off, crcast_len) = MetadataBuffer.StoreJson(value.GetRawText()); }
        private int crcast_off = -1, crcast_len;
        private List<StremioCreditCast> _creditsCast;
        [JsonIgnore] public List<StremioCreditCast> CreditsCast => _creditsCast ??= DeserializeList(crcast_off, crcast_len, Services.Json.AppJsonContext.Default.ListStremioCreditCast);

        [JsonPropertyName("creditsCrew")]
        public System.Text.Json.JsonElement CreditsCrewJson { set => (crcrew_off, crcrew_len) = MetadataBuffer.StoreJson(value.GetRawText()); }
        private int crcrew_off = -1, crcrew_len;
        private List<StremioCreditCrew> _creditsCrew;
        [JsonIgnore] public List<StremioCreditCrew> CreditsCrew => _creditsCrew ??= DeserializeList(crcrew_off, crcrew_len, Services.Json.AppJsonContext.Default.ListStremioCreditCrew);

        private int _countryOff, _countryLen;
        private string _country;
        public string Country { get => _country ??= MetadataBuffer.GetString(_countryOff, _countryLen); set { var r = MetadataBuffer.Store(value); _countryOff = r.Offset; _countryLen = r.Length; _country = value; } }

        [JsonPropertyName("writer")]
        public System.Text.Json.JsonElement WriterJson { set => (writer_off, writer_len) = MetadataBuffer.StoreJson(value.GetRawText()); }
        private int writer_off = -1, writer_len;
        private List<string> _writerList;
        [JsonIgnore] public List<string> Writer => _writerList ??= DeserializeList(writer_off, writer_len, Services.Json.AppJsonContext.Default.ListString);

        private int _extrasOff, _extrasLen;
        /// <summary>
        /// PROJECT ZERO: AppExtras stored as raw JSON to avoid 20,000+ object allocations during discovery.
        /// </summary>
        [JsonPropertyName("appExtras")]
        public System.Text.Json.JsonElement AppExtrasJson 
        { 
            set { var r = MetadataBuffer.StoreJson(value.GetRawText()); _extrasOff = r.Offset; _extrasLen = r.Length; }
        }

        [JsonPropertyName("app_extras")]
        public System.Text.Json.JsonElement AppExtrasSnakeJson 
        { 
            set { var r = MetadataBuffer.StoreJson(value.GetRawText()); _extrasOff = r.Offset; _extrasLen = r.Length; }
        }

        private StremioAppExtras _appExtras;
        [JsonIgnore]
        public StremioAppExtras AppExtras => _appExtras ??= LazyParseAppExtras();

        private StremioAppExtras LazyParseAppExtras()
        {
            if (_extrasOff < 0 || _extrasLen <= 0) return null;
            string json = string.Empty;
            try 
            { 
                json = MetadataBuffer.GetString(_extrasOff, _extrasLen);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return System.Text.Json.JsonSerializer.Deserialize(json, Services.Json.AppJsonContext.Default.StremioAppExtras); 
            }
            catch (System.Text.Json.JsonException ex)
            {
                string snippet = (json.Length > 200) ? json.Substring(0, 200) + "..." : json;
                ModernIPTVPlayer.Services.AppLogger.Error($"[StremioMeta] AppExtras JSON Error: {ex.Message} at {ex.Path} | Snippet: {snippet}", ex);
                return null; 
            }
            catch { return null; }
        }

        // --- PROJECT ZERO: Pinnacle Optimization Helpers ---
        private static List<T> DeserializeList<T>(int offset, int length, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> typeInfo)
        {
            if (offset < 0 || length <= 0) 
            {
                // System.Diagnostics.Debug.WriteLine($"[STREMIO_META_DEBUG] DeserializeList SKIPPED (Empty): Type={typeof(T).Name}");
                return new List<T>();
            }

            string json = string.Empty;
            try
            {
                json = MetadataBuffer.GetString(offset, length);
                if (string.IsNullOrWhiteSpace(json)) 
                {
                    System.Diagnostics.Debug.WriteLine($"[STREMIO_META_DEBUG] DeserializeList ERROR (Buffer Empty): Offset={offset}, Len={length}");
                    return new List<T>();
                }

                // [PROJECT ZERO: Polymorphic List Handling]
                string trimmed = json.TrimStart();
                if (trimmed.Length > 0 && trimmed[0] == '[')
                {
                    var result = System.Text.Json.JsonSerializer.Deserialize(json, typeInfo);
                // System.Diagnostics.Debug.WriteLine($"[STREMIO_META_DEBUG] DeserializeList SUCCESS: Type={typeof(T).Name}, Count={result?.Count ?? 0}");
                    return result ?? new List<T>();
                }
                else if (typeof(T) == typeof(string))
                {
                    // Fallback for single strings stored as lists
                    try
                    {
                        string single = System.Text.Json.JsonSerializer.Deserialize(json, Services.Json.AppJsonContext.Default.String) ?? string.Empty;
                        var list = single.Split(new[] { ',', ';' }, System.StringSplitOptions.RemoveEmptyEntries)
                                         .Select(s => s.Trim())
                                         .ToList();
                    // System.Diagnostics.Debug.WriteLine($"[STREMIO_META_DEBUG] DeserializeList FALLBACK STRING: Count={list.Count}");
                        return (List<T>)(object)list;
                    }
                    catch { return new List<T>(); }
                }

                System.Diagnostics.Debug.WriteLine($"[STREMIO_META_DEBUG] DeserializeList ERROR (Identity Mismatch): Snippet={json.Substring(0, Math.Min(30, json.Length))}");
                return new List<T>();
            }
            catch (System.Text.Json.JsonException ex)
            {
                string snippet = (json.Length > 200) ? json.Substring(0, 200) + "..." : json;
                ModernIPTVPlayer.Services.AppLogger.Error($"[STREMIO_META_DEBUG] JSON Error ({typeof(T).Name}): {ex.Message} | Snippet: {snippet}", ex);
                return new List<T>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[STREMIO_META_DEBUG] FATAL Error: {ex.Message}");
                return new List<T>();
            }
        }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public partial class StremioAppExtras
    {
        [JsonPropertyName("cast")]
        public List<StremioAppCast> Cast { get; set; }

        [JsonPropertyName("directors")]
        public List<StremioAppCast> Directors { get; set; }

        [JsonPropertyName("writers")]
        public List<StremioAppCast> Writers { get; set; }

        private int _logoOff, _logoLen;
        public string Logo { get => MetadataBuffer.GetString(_logoOff, _logoLen); set => (_logoOff, _logoLen) = MetadataBuffer.Store(value); }

        private int _trOff, _trLen;
        public string Trailer { get => MetadataBuffer.GetString(_trOff, _trLen); set => (_trOff, _trLen) = MetadataBuffer.Store(value); }

        public List<StremioAppBackdrop> Backdrops { get; set; }
        public List<string> SeasonPosters { get; set; }

        private int _certOff, _certLen;
        public string Certification { get => MetadataBuffer.GetString(_certOff, _certLen); set => (_certOff, _certLen) = MetadataBuffer.Store(value); }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public partial class StremioAppBackdrop
    {
        private int _urlOff, _urlLen;
        public string Url { get => MetadataBuffer.GetString(_urlOff, _urlLen); set => (_urlOff, _urlLen) = MetadataBuffer.Store(value); }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public partial class StremioAppCast
    {
        private int _nameOff, _nameLen;
        public string Name { get => MetadataBuffer.GetString(_nameOff, _nameLen); set => (_nameOff, _nameLen) = MetadataBuffer.Store(value); }

        private int _charOff, _charLen;
        public string Character { get => MetadataBuffer.GetString(_charOff, _charLen); set => (_charOff, _charLen) = MetadataBuffer.Store(value); }

        private int _photoOff, _photoLen;
        public string Photo { get => MetadataBuffer.GetString(_photoOff, _photoLen); set => (_photoOff, _photoLen) = MetadataBuffer.Store(value); }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public partial class StremioCreditCast
    {
        public System.Text.Json.JsonElement Id { get; set; }

        private int _nameOff, _nameLen;
        public string Name { get => MetadataBuffer.GetString(_nameOff, _nameLen); set => (_nameOff, _nameLen) = MetadataBuffer.Store(value); }

        private int _charOff, _charLen;
        public string Character { get => MetadataBuffer.GetString(_charOff, _charLen); set => (_charOff, _charLen) = MetadataBuffer.Store(value); }

        private int _profOff, _profLen;
        public string ProfilePath { get => MetadataBuffer.GetString(_profOff, _profLen); set => (_profOff, _profLen) = MetadataBuffer.Store(value); }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public partial class StremioCreditCrew
    {
        public System.Text.Json.JsonElement Id { get; set; }

        private int _nameOff, _nameLen;
        public string Name { get => MetadataBuffer.GetString(_nameOff, _nameLen); set => (_nameOff, _nameLen) = MetadataBuffer.Store(value); }

        private int _deptOff, _deptLen;
        public string Department { get => MetadataBuffer.GetString(_deptOff, _deptLen); set => (_deptOff, _deptLen) = MetadataBuffer.Store(value); }

        private int _jobOff, _jobLen;
        public string Job { get => MetadataBuffer.GetString(_jobOff, _jobLen); set => (_jobOff, _jobLen) = MetadataBuffer.Store(value); }

        private int _profOff, _profLen;
        public string ProfilePath { get => MetadataBuffer.GetString(_profOff, _profLen); set => (_profOff, _profLen) = MetadataBuffer.Store(value); }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public partial class StremioTrailerStream
    {
        private int _titleOff, _titleLen;
        public string Title { get => MetadataBuffer.GetString(_titleOff, _titleLen); set => (_titleOff, _titleLen) = MetadataBuffer.Store(value); }

        private int _ytIdOff, _ytIdLen;
        public string YtId { get => MetadataBuffer.GetString(_ytIdOff, _ytIdLen); set => (_ytIdOff, _ytIdLen) = MetadataBuffer.Store(value); }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public partial class StremioLink
    {
        private int _nameOff, _nameLen;
        public string Name 
        { 
            get => MetadataBuffer.GetString(_nameOff, _nameLen); 
            set { if (MetadataBuffer.IsEqual(_nameOff, _nameLen, value)) return; var r = MetadataBuffer.Store(value); _nameOff = r.Offset; _nameLen = r.Length; } 
        }

        private int _catOff, _catLen;
        public string Category 
        { 
            get => MetadataBuffer.GetString(_catOff, _catLen); 
            set { if (MetadataBuffer.IsEqual(_catOff, _catLen, value)) return; var r = MetadataBuffer.Store(value); _catOff = r.Offset; _catLen = r.Length; } 
        }

        private int _urlOff, _urlLen;
        public string Url 
        { 
            get => MetadataBuffer.GetString(_urlOff, _urlLen); 
            set { if (MetadataBuffer.IsEqual(_urlOff, _urlLen, value)) return; var r = MetadataBuffer.Store(value); _urlOff = r.Offset; _urlLen = r.Length; } 
        }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public partial class StremioMetaTrailer
    {
        private int _srcOff, _srcLen;
        public string Source { get => MetadataBuffer.GetString(_srcOff, _srcLen); set => (_srcOff, _srcLen) = MetadataBuffer.Store(value); } // YouTube ID

        private int _typeOff, _typeLen;
        public string Type { get => MetadataBuffer.GetString(_typeOff, _typeLen); set => (_typeOff, _typeLen) = MetadataBuffer.Store(value); }
    }
}
