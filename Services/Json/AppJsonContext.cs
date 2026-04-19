using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Common;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Models.Tmdb;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Controls;
using System.Text.Json.Nodes;

namespace ModernIPTVPlayer.Services.Json
{
    [JsonSourceGenerationOptions(
        GenerationMode = JsonSourceGenerationMode.Metadata | JsonSourceGenerationMode.Serialization,
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString)]
    [JsonSerializable(typeof(PlayerSettings))]
    [JsonSerializable(typeof(List<HistoryItem>))]
    [JsonSerializable(typeof(List<Playlist>))]
    [JsonSerializable(typeof(XtreamAuthResponse))]
    [JsonSerializable(typeof(List<LiveStream>))]
    [JsonSerializable(typeof(List<LiveCategory>))]
    [JsonSerializable(typeof(List<VodStream>))]
    [JsonSerializable(typeof(List<SeriesStream>))]
    [JsonSerializable(typeof(Dictionary<string, TmdbCacheEntry>))]
    [JsonSerializable(typeof(Dictionary<string, StremioManifest>))]
    [JsonSerializable(typeof(List<StremioMediaStream>))]
    [JsonSerializable(typeof(UnifiedMetadata))]
    [JsonSerializable(typeof(List<UnifiedCast>))]
    [JsonSerializable(typeof(List<UnifiedSeason>))]
    [JsonSerializable(typeof(List<UnifiedEpisode>))]
    [JsonSerializable(typeof(UnifiedSeason))]
    [JsonSerializable(typeof(UnifiedEpisode))]
    [JsonSerializable(typeof(UnifiedCast))]
    [JsonSerializable(typeof(TmdbSearchResponse))]
    [JsonSerializable(typeof(TmdbVideosResponse))]
    [JsonSerializable(typeof(TmdbCreditsResponse))]
    [JsonSerializable(typeof(TmdbMovieDetails))]
    [JsonSerializable(typeof(TmdbSeasonDetails))]
    [JsonSerializable(typeof(TmdbPersonDetails))]
    [JsonSerializable(typeof(TmdbPersonCreditsResponse))]
    [JsonSerializable(typeof(TmdbPersonSearchResponse))]
    [JsonSerializable(typeof(List<TmdbMovieResult>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(StremioManifest))]
    [JsonSerializable(typeof(StremioMetaResponse))]
    [JsonSerializable(typeof(StremioStreamResponse))]
    [JsonSerializable(typeof(StremioSubtitleResponse))]
    [JsonSerializable(typeof(VodInfoResponse))]
    [JsonSerializable(typeof(VodInfo))]
    [JsonSerializable(typeof(VodStreamInfo))]
    [JsonSerializable(typeof(SeriesInfoResult))]
    [JsonSerializable(typeof(MovieInfoResult))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(Dictionary<string, List<SeriesEpisodeDef>>))]
    [JsonSerializable(typeof(List<SeriesEpisodeDef>))]
    [JsonSerializable(typeof(SeriesInfoDetails))]
    [JsonSerializable(typeof(MovieInfoDetails))]
    [JsonSerializable(typeof(MovieDataDetails))]
    [JsonSerializable(typeof(TechnicalVideoInfo))]
    [JsonSerializable(typeof(TechnicalAudioInfo))]
    [JsonSerializable(typeof(SeriesEpisodeDef))]
    [JsonSerializable(typeof(SeriesEpisodeInfo))]
    [JsonSerializable(typeof(StremioCatalogRoot))]
    [JsonSerializable(typeof(List<WatchlistItem>))]
    [JsonSerializable(typeof(WatchlistItem))]
    [JsonSerializable(typeof(Dictionary<string, List<StremioDiscoveryControl.CachedSlot>>))]
    [JsonSerializable(typeof(List<StremioDiscoveryControl.CachedSlot>))]
    [JsonSerializable(typeof(StremioDiscoveryControl.CachedSlot))]
    [JsonSerializable(typeof(CatalogRowViewModel))]
    [JsonSerializable(typeof(List<CatalogRowViewModel>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(System.Collections.ObjectModel.ObservableCollection<StremioMediaStream>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal partial class AppJsonContext : JsonSerializerContext { }
}
