using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Models.Stremio
{
    // ==========================================
    // 1. MANIFEST
    // ==========================================
    public class StremioManifest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("resources")]
        public List<StremioResource> Resources { get; set; }

        [JsonPropertyName("types")]
        public List<string> Types { get; set; }

        [JsonPropertyName("catalogs")]
        public List<StremioCatalog> Catalogs { get; set; } = new();

        [JsonPropertyName("logo")]
        public string Logo { get; set; }

        [JsonPropertyName("background")]
        public string Background { get; set; }
    }

    [JsonConverter(typeof(StremioResourceConverter))]
    public class StremioResource
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("types")]
        public List<string> Types { get; set; }

        [JsonPropertyName("idPrefixes")]
        public List<string> IdPrefixes { get; set; }

        public override string ToString() => Name ?? "Unknown Resource";

        // Logic to handle if resource is just a string during deserialization
        public static implicit operator StremioResource(string name) => new StremioResource { Name = name };
    }

    public class StremioResourceConverter : JsonConverter<StremioResource>
    {
        public override StremioResource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return new StremioResource { Name = reader.GetString() };
            }
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using (var doc = JsonDocument.ParseValue(ref reader))
                {
                    var root = doc.RootElement;
                    var res = new StremioResource();
                    if (root.TryGetProperty("name", out var n)) res.Name = n.GetString();
                    if (root.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Array)
                    {
                        res.Types = new List<string>();
                        foreach (var item in t.EnumerateArray()) res.Types.Add(item.GetString());
                    }
                    if (root.TryGetProperty("idPrefixes", out var idp) && idp.ValueKind == JsonValueKind.Array)
                    {
                        res.IdPrefixes = new List<string>();
                        foreach (var item in idp.EnumerateArray()) res.IdPrefixes.Add(item.GetString());
                    }
                    return res;
                }
            }
            return null;
        }

        public override void Write(Utf8JsonWriter writer, StremioResource value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);
            if (value.Types != null)
            {
                writer.WriteStartArray("types");
                foreach (var t in value.Types) writer.WriteStringValue(t);
                writer.WriteEndArray();
            }
            if (value.IdPrefixes != null)
            {
                writer.WriteStartArray("idPrefixes");
                foreach (var idp in value.IdPrefixes) writer.WriteStringValue(idp);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
    }

    public class StremioCatalog
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } // "movie", "series"

        [JsonPropertyName("id")]
        public string Id { get; set; } // "top", "year"

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("extra")]
        public List<StremioCatalogExtra> Extra { get; set; }
    }

    public class StremioCatalogExtra
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } // "search", "genre"

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }

        [JsonPropertyName("options")]
        public List<string> Options { get; set; }
    }

    public class StremioCatalogRoot
    {
        [JsonPropertyName("metas")]
        public List<StremioMeta> Metas { get; set; }
    }

    // ==========================================
    // 2. META (Catalog Items / Details)
    // ==========================================
    public class StremioMetaResponse
    {
        [JsonPropertyName("meta")]
        public StremioMeta Meta { get; set; }
        
        [JsonPropertyName("metas")]
        public List<StremioMeta> Metas { get; set; }
    }

    public class StremioMeta
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } // IMDB ID usually (tt1234567)

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("originalName")]
        public string OriginalName { get; set; }

        [JsonPropertyName("aliases")]
        [JsonConverter(typeof(Helpers.UniversalStringListConverter))]
        public List<string> Aliases { get; set; }

        private int _posterOff, _posterLen;
        [JsonPropertyName("poster")]
        public string Poster 
        { 
            get => MetadataBuffer.GetString(_posterOff, _posterLen); 
            set { if (MetadataBuffer.IsEqual(_posterOff, _posterLen, value)) return; var r = MetadataBuffer.Store(value); _posterOff = r.Offset; _posterLen = r.Length; } 
        }

        private int _bgOff, _bgLen;
        [JsonPropertyName("background")]
        public string Background 
        { 
            get => MetadataBuffer.GetString(_bgOff, _bgLen); 
            set { if (MetadataBuffer.IsEqual(_bgOff, _bgLen, value)) return; var r = MetadataBuffer.Store(value); _bgOff = r.Offset; _bgLen = r.Length; } 
        }

        private int _logoOff, _logoLen;
        [JsonPropertyName("logo")]
        public string Logo 
        { 
            get => MetadataBuffer.GetString(_logoOff, _logoLen); 
            set { if (MetadataBuffer.IsEqual(_logoOff, _logoLen, value)) return; var r = MetadataBuffer.Store(value); _logoOff = r.Offset; _logoLen = r.Length; } 
        }

        private int _descOff, _descLen;
        [JsonPropertyName("description")]
        public string Description 
        { 
            get => MetadataBuffer.GetString(_descOff, _descLen); 
            set { if (MetadataBuffer.IsEqual(_descOff, _descLen, value)) return; var r = MetadataBuffer.Store(value); _descOff = r.Offset; _descLen = r.Length; } 
        }

        private int _relOff, _relLen;
        [JsonPropertyName("releaseInfo")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? ReleaseInfo 
        { 
            get => MetadataBuffer.GetString(_relOff, _relLen); 
            set { if (MetadataBuffer.IsEqual(_relOff, _relLen, value)) return; var r = MetadataBuffer.Store(value); _relOff = r.Offset; _relLen = r.Length; } 
        }

        private int _yearOff, _yearLen;
        [JsonPropertyName("year")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Year 
        { 
            get => MetadataBuffer.GetString(_yearOff, _yearLen); 
            set { if (MetadataBuffer.IsEqual(_yearOff, _yearLen, value)) return; var r = MetadataBuffer.Store(value); _yearOff = r.Offset; _yearLen = r.Length; } 
        }

        private int _resOff, _resLen;
        [JsonPropertyName("released")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Released 
        { 
            get => MetadataBuffer.GetString(_resOff, _resLen); 
            set { if (MetadataBuffer.IsEqual(_resOff, _resLen, value)) return; var r = MetadataBuffer.Store(value); _resOff = r.Offset; _resLen = r.Length; } 
        }

        private int _ratOff, _ratLen;
        [JsonPropertyName("imdbRating")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? ImdbRating 
        { 
            get => MetadataBuffer.GetString(_ratOff, _ratLen); 
            set { if (MetadataBuffer.IsEqual(_ratOff, _ratLen, value)) return; var r = MetadataBuffer.Store(value); _ratOff = r.Offset; _ratLen = r.Length; } 
        }

        [JsonPropertyName("genres")]
        [JsonConverter(typeof(Helpers.UniversalStringListConverter))]
        public List<string> Genres { get; set; }

        private int _runOff, _runLen;
        [JsonPropertyName("runtime")]
        public string Runtime 
        { 
            get => MetadataBuffer.GetString(_runOff, _runLen); 
            set { if (MetadataBuffer.IsEqual(_runOff, _runLen, value)) return; var r = MetadataBuffer.Store(value); _runOff = r.Offset; _runLen = r.Length; } 
        }
        
        [JsonPropertyName("cast")]
        [JsonConverter(typeof(Helpers.UniversalStringListConverter))]
        public List<string> Cast { get; set; }
        
        [JsonPropertyName("director")]
        [JsonConverter(typeof(Helpers.UniversalStringListConverter))]
        public List<string> Director { get; set; }

        [JsonPropertyName("videos")]
        public List<StremioVideo> Videos { get; set; }

        [JsonPropertyName("trailers")]
        public List<StremioMetaTrailer> Trailers { get; set; }

        [JsonPropertyName("moviedb_id")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? MovieDbId { get; set; }

        [JsonPropertyName("imdb_id")]
        public string ImdbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? TvDbId { get; set; }

        private int _webOff, _webLen;
        [JsonPropertyName("website")]
        public string Website 
        { 
            get => MetadataBuffer.GetString(_webOff, _webLen); 
            set { if (MetadataBuffer.IsEqual(_webOff, _webLen, value)) return; var r = MetadataBuffer.Store(value); _webOff = r.Offset; _webLen = r.Length; } 
        }

        [JsonPropertyName("links")]
        public List<StremioLink> Links { get; set; }

        [JsonPropertyName("trailerStreams")]
        public List<StremioTrailerStream> TrailerStreams { get; set; }

        [JsonPropertyName("credits_cast")]
        public List<StremioCreditCast> CreditsCast { get; set; }

        [JsonPropertyName("credits_crew")]
        public List<StremioCreditCrew> CreditsCrew { get; set; }

        [JsonPropertyName("country")]
        public string Country { get; set; }

        [JsonPropertyName("writer")]
        [JsonConverter(typeof(Helpers.UniversalStringListConverter))]
        public List<string> Writer { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("app_extras")]
        public StremioAppExtras AppExtras { get; set; }
    }

    public class StremioAppExtras
    {
        [JsonPropertyName("cast")]
        public List<StremioAppCast> Cast { get; set; }

        [JsonPropertyName("directors")]
        public List<StremioAppCast> Directors { get; set; }

        [JsonPropertyName("writers")]
        public List<StremioAppCast> Writers { get; set; }

        [JsonPropertyName("logo")]
        public string Logo { get; set; }

        [JsonPropertyName("trailer")]
        public string Trailer { get; set; }

        [JsonPropertyName("backdrops")]
        public List<StremioAppBackdrop> Backdrops { get; set; }

        [JsonPropertyName("seasonPosters")]
        public List<string> SeasonPosters { get; set; }

        [JsonPropertyName("certification")]
        public string Certification { get; set; }
    }

    public class StremioAppBackdrop
    {
        [JsonPropertyName("url")]
        public string Url { get; set; }
    }

    public class StremioAppCast
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("character")]
        public string Character { get; set; }

        [JsonPropertyName("photo")]
        public string Photo { get; set; }
    }

    public class StremioCreditCast
    {
        [JsonPropertyName("id")]
        public object Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("character")]
        public string Character { get; set; }

        [JsonPropertyName("profile_path")]
        public string ProfilePath { get; set; }
    }

    public class StremioCreditCrew
    {
        [JsonPropertyName("id")]
        public object Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("department")]
        public string Department { get; set; }

        [JsonPropertyName("job")]
        public string Job { get; set; }

        [JsonPropertyName("profile_path")]
        public string ProfilePath { get; set; }
    }

    public class StremioTrailerStream
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("ytId")]
        public string YtId { get; set; }
    }

    public class StremioLink
    {
        private int _nameOff, _nameLen;
        [JsonPropertyName("name")]
        public string Name 
        { 
            get => MetadataBuffer.GetString(_nameOff, _nameLen); 
            set { if (MetadataBuffer.IsEqual(_nameOff, _nameLen, value)) return; var r = MetadataBuffer.Store(value); _nameOff = r.Offset; _nameLen = r.Length; } 
        }

        private int _catOff, _catLen;
        [JsonPropertyName("category")]
        public string Category 
        { 
            get => MetadataBuffer.GetString(_catOff, _catLen); 
            set { if (MetadataBuffer.IsEqual(_catOff, _catLen, value)) return; var r = MetadataBuffer.Store(value); _catOff = r.Offset; _catLen = r.Length; } 
        }

        private int _urlOff, _urlLen;
        [JsonPropertyName("url")]
        public string Url 
        { 
            get => MetadataBuffer.GetString(_urlOff, _urlLen); 
            set { if (MetadataBuffer.IsEqual(_urlOff, _urlLen, value)) return; var r = MetadataBuffer.Store(value); _urlOff = r.Offset; _urlLen = r.Length; } 
        }
    }

    public class StremioMetaTrailer
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } // YouTube ID

        [JsonPropertyName("type")]
        public string Type { get; set; }
    }

    public class StremioVideo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } // "tt1234:1:1"

        private int _nameOff, _nameLen;
        [JsonPropertyName("name")]
        public string Name 
        { 
            get => MetadataBuffer.GetString(_nameOff, _nameLen); 
            set { if (MetadataBuffer.IsEqual(_nameOff, _nameLen, value)) return; var r = MetadataBuffer.Store(value); _nameOff = r.Offset; _nameLen = r.Length; } 
        }

        private int _titleOff, _titleLen;
        [JsonPropertyName("title")]
        public string Title 
        { 
            get => MetadataBuffer.GetString(_titleOff, _titleLen); 
            set { if (MetadataBuffer.IsEqual(_titleOff, _titleLen, value)) return; var r = MetadataBuffer.Store(value); _titleOff = r.Offset; _titleLen = r.Length; } 
        }

        private int _relOff, _relLen;
        [JsonPropertyName("released")]
        public string Released 
        { 
            get => MetadataBuffer.GetString(_relOff, _relLen); 
            set { if (MetadataBuffer.IsEqual(_relOff, _relLen, value)) return; var r = MetadataBuffer.Store(value); _relOff = r.Offset; _relLen = r.Length; } 
        }

        private int _thumbOff, _thumbLen;
        [JsonPropertyName("thumbnail")]
        public string Thumbnail 
        { 
            get => MetadataBuffer.GetString(_thumbOff, _thumbLen); 
            set { if (MetadataBuffer.IsEqual(_thumbOff, _thumbLen, value)) return; var r = MetadataBuffer.Store(value); _thumbOff = r.Offset; _thumbLen = r.Length; } 
        }

        [JsonPropertyName("streams")]
        public List<StremioStream> Streams { get; set; }

        [JsonPropertyName("available")]
        public bool Available { get; set; }

        private int _runtimeOff, _runtimeLen;
        [JsonPropertyName("runtime")]
        public string Runtime
        {
            get => MetadataBuffer.GetString(_runtimeOff, _runtimeLen);
            set { if (MetadataBuffer.IsEqual(_runtimeOff, _runtimeLen, value)) return; var r = MetadataBuffer.Store(value); _runtimeOff = r.Offset; _runtimeLen = r.Length; }
        }

        [JsonPropertyName("episode")]
        public int Episode { get; set; }

        [JsonPropertyName("season")]
        public int Season { get; set; }

        private int _ovOff, _ovLen;
        [JsonPropertyName("overview")]
        public string Overview 
        { 
            get => MetadataBuffer.GetString(_ovOff, _ovLen); 
            set { if (MetadataBuffer.IsEqual(_ovOff, _ovLen, value)) return; var r = MetadataBuffer.Store(value); _ovOff = r.Offset; _ovLen = r.Length; } 
        }

        private int _descOff, _descLen;
        [JsonPropertyName("description")]
        public string Description 
        { 
            get => MetadataBuffer.GetString(_descOff, _descLen); 
            set { if (MetadataBuffer.IsEqual(_descOff, _descLen, value)) return; var r = MetadataBuffer.Store(value); _descOff = r.Offset; _descLen = r.Length; } 
        }
    }

    // ==========================================
    // 3. STREAMS
    // ==========================================
    public class StremioStreamResponse
    {
        [JsonPropertyName("streams")]
        public List<StremioStream> Streams { get; set; }
    }

    public class StremioStream
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } // e.g., "Torrentio\n4K" or just "4K"

        [JsonPropertyName("title")]
        public string Title { get; set; } // Details: "4K HDR 2.5GB"

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; } // DIRECT HTTP URL

        [JsonPropertyName("externalUrl")]
        public string ExternalUrl { get; set; }

        [JsonPropertyName("infoHash")]
        public string InfoHash { get; set; } // For Torrents (Magnet) - Not supported yet

        [JsonPropertyName("fileIdx")]
        public int? FileIdx { get; set; } // For multi-file torrents
        
        [JsonPropertyName("behaviorHints")]
        public BehaviorHints BehaviorHints { get; set; }

        [JsonIgnore]
        public string AddonUrl { get; set; } // Internal tracking
    }
    
    public class BehaviorHints
    {
        [JsonPropertyName("notWebReady")]
        public bool NotWebReady { get; set; }
        
        [JsonPropertyName("bingeGroup")]
        public string BingeGroup { get; set; }
    }
    // ==========================================
    // 3. SUBTITLES
    // ==========================================
    public class StremioSubtitleResponse
    {
        [JsonPropertyName("subtitles")]
        public List<StremioSubtitle> Subtitles { get; set; }
    }

    public class StremioSubtitle
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("lang")]
        public string Lang { get; set; }
    }

    // ==========================================
    // 4. NAVIGATION ARGS
    // ==========================================
    public class GenreSelectionArgs
    {
        public string AddonId { get; set; }
        public string CatalogId { get; set; }
        public string CatalogType { get; set; } // "movie" or "series"
        public string GenreValue { get; set; } // e.g. "Action", "Animasyon", "2024"
        public string FilterKey { get; set; } // e.g. "genre", "year"
        public string DisplayName { get; set; } // For title display
    }
}
