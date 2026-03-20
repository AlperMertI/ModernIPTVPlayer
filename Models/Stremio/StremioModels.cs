using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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
        public List<System.Text.Json.JsonElement> Resources { get; set; } // Can be strings or StremioResource objects

        [JsonPropertyName("types")]
        public List<string> Types { get; set; }

        [JsonPropertyName("catalogs")]
        public List<StremioCatalog> Catalogs { get; set; } = new();

        [JsonPropertyName("logo")]
        public string Logo { get; set; }

        [JsonPropertyName("background")]
        public string Background { get; set; }
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
        public List<string> Aliases { get; set; }

        [JsonPropertyName("poster")]
        public string Poster { get; set; }

        [JsonPropertyName("background")]
        public string Background { get; set; }

        [JsonPropertyName("logo")]
        public string Logo { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("releaseInfo")]
        public object ReleaseInfoRaw { get; set; } // Can be string or int

        [JsonIgnore]
        public string ReleaseInfo 
        { 
            get => ReleaseInfoRaw?.ToString();
            set => ReleaseInfoRaw = value;
        }

        [JsonPropertyName("imdbRating")]
        public object ImdbRatingRaw { get; set; } // Can be string or double

        [JsonIgnore]
        public string ImdbRating 
        { 
            get => ImdbRatingRaw?.ToString();
            set => ImdbRatingRaw = value;
        }

        [JsonPropertyName("genres")]
        public List<string> Genres { get; set; }

        [JsonPropertyName("runtime")]
        public string Runtime { get; set; }
        
        [JsonPropertyName("cast")]
        public List<string> Cast { get; set; }
        
        [JsonPropertyName("director")]
        public List<string> Director { get; set; }

        [JsonPropertyName("videos")]
        public List<StremioVideo> Videos { get; set; }

        [JsonPropertyName("trailers")]
        public List<StremioMetaTrailer> Trailers { get; set; }

        [JsonPropertyName("moviedb_id")]
        public object MovieDbId { get; set; }

        [JsonPropertyName("imdb_id")]
        public string ImdbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        public object TvDbId { get; set; }

        [JsonPropertyName("website")]
        public string Website { get; set; }

        [JsonPropertyName("links")]
        public List<StremioLink> Links { get; set; }

        [JsonPropertyName("trailerStreams")]
        public List<StremioTrailerStream> TrailerStreams { get; set; }

        [JsonPropertyName("credits_cast")]
        public List<StremioCreditCast> CreditsCast { get; set; }

        [JsonPropertyName("credits_crew")]
        public List<StremioCreditCrew> CreditsCrew { get; set; }

        [JsonPropertyName("app_extras")]
        public StremioAppExtras AppExtras { get; set; }
    }

    public class StremioAppExtras
    {
        [JsonPropertyName("cast")]
        public List<StremioAppCast> Cast { get; set; }

        [JsonPropertyName("logo")]
        public string Logo { get; set; }

        [JsonPropertyName("trailer")]
        public string Trailer { get; set; }

        [JsonPropertyName("backdrops")]
        public List<StremioAppBackdrop> Backdrops { get; set; }
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
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }
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

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("released")]
        public string Released { get; set; }

        [JsonPropertyName("thumbnail")]
        public string Thumbnail { get; set; }

        [JsonPropertyName("streams")]
        public List<StremioStream> Streams { get; set; }

        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("episode")]
        public int Episode { get; set; }

        [JsonPropertyName("season")]
        public int Season { get; set; }

        [JsonPropertyName("overview")]
        public string Overview { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }
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
