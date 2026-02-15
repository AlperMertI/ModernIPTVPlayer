using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services.Stremio;
using ModernIPTVPlayer;

namespace ModernIPTVPlayer.Services.Metadata
{
    public class MetadataProvider
    {
        private static MetadataProvider _instance;
        public static MetadataProvider Instance => _instance ??= new MetadataProvider();

        public async Task<UnifiedMetadata> GetMetadataAsync(Models.IMediaStream stream)
        {
            if (stream == null) return null;
            
            string id = stream.IMDbId; // Use IMDb if available
            string type = "movie";

            // Determine type
            if (stream is ModernIPTVPlayer.SeriesStream || (stream is Models.Stremio.StremioMediaStream sms && (sms.Meta.Type == "series" || sms.Meta.Type == "tv")))
            {
                type = "series";
            }
            
            // Fallback to title if no ID
            return await GetMetadataAsync(id ?? stream.Title, type, stream);
        }

        private readonly StremioService _stremioService = StremioService.Instance;

        public async Task<UnifiedMetadata> GetMetadataAsync(string id, string type, Models.IMediaStream sourceStream = null, string stremioBaseUrl = "https://v3-cinemeta.strem.io")
        {
            var metadata = new UnifiedMetadata { ImdbId = id, IsSeries = type == "series" || type == "tv", MetadataId = id }; // Set MetadataId
            
            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Fetching: {id} | Type: {type} | Source: {sourceStream?.GetType().Name ?? "Null"}");

            
            try
            {
                // 1. Fetch Stremio Meta (Cinemeta is the primary source for IDs)
                var stremioMeta = await _stremioService.GetMetaAsync(stremioBaseUrl, type, id);
                
                if (stremioMeta != null)
                {
                    MapStremioToUnified(stremioMeta, metadata);
                    metadata.DataSource = "Stremio";
                }

                // 2. Enrichment via TMDB (Optional)
                TmdbMovieResult tmdb = null;
                if (AppSettings.IsTmdbEnabled && !string.IsNullOrEmpty(metadata.ImdbId))
                {
                    tmdb = await EnrichWithTmdbAsync(metadata);
                    metadata.TmdbInfo = tmdb;
                }
                
                // 3. Post-processing (Virtual Episode Reconcilation)
                if (metadata.IsSeries)
                {
                    ReconcileEpisodes(stremioMeta, metadata, tmdb);
                }

                // 4. IPTV Fallback (If Overview is missing or it's a direct IPTV stream)
                bool needsEnrichment = string.IsNullOrEmpty(metadata.Overview) || !IsImdbId(metadata.ImdbId);
                
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Needs Enrichment? {needsEnrichment} (Overview: {!string.IsNullOrEmpty(metadata.Overview)}, IsImdb: {IsImdbId(metadata.ImdbId)})");

                if (metadata.IsSeries && sourceStream is SeriesStream seriesStream && needsEnrichment)
                {
                    await EnrichWithIptvAsync(metadata, seriesStream);
                }
                else if (!metadata.IsSeries && sourceStream != null && needsEnrichment)
                {
                    // Allow ANY stream type for movie enrichment if it's not a series
                    await EnrichWithIptvMovieAsync(metadata, sourceStream);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Error: {ex.Message}");
            }

            return metadata;
        }

        private async Task EnrichWithIptvMovieAsync(UnifiedMetadata metadata, Models.IMediaStream vod)
        {
            if (App.CurrentLogin == null) return;
            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Enriching IPTV Movie: {vod.Title} (ID: {vod.Id}) type: {vod.GetType().Name}");

            try
            {
                var result = await ContentCacheService.Instance.GetMovieInfoAsync(vod.Id, App.CurrentLogin);
                if (result?.Info != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataProvider] IPTV Movie Info Found: {result.Info.Name}");
                    
                    // Prefer IPTV data if Stremio/TMDB is missing or incomplete
                    if (string.IsNullOrEmpty(metadata.Overview) || metadata.Overview == "Açıklama mevcut değil.") 
                        metadata.Overview = result.Info.Plot;
                    
                    if (string.IsNullOrEmpty(metadata.Genres)) 
                        metadata.Genres = result.Info.Genre;
                    
                    if (string.IsNullOrEmpty(metadata.Title) || metadata.Title == "Loading...") 
                        metadata.Title = result.Info.Name;
                    
                    if (string.IsNullOrEmpty(metadata.BackdropUrl)) 
                        metadata.BackdropUrl = result.Info.MovieImage;

                    if (string.IsNullOrEmpty(metadata.PosterUrl))
                        metadata.PosterUrl = result.Info.MovieImage;

                    if (metadata.Rating == 0)
                    {
                        if (double.TryParse(result.Info.Rating, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double ratingValue))
                        {
                            metadata.Rating = ratingValue;
                        }
                    }

                    if (string.IsNullOrEmpty(metadata.Year))
                        metadata.Year = result.Info.ReleaseDate;

                    metadata.DataSource += (string.IsNullOrEmpty(metadata.DataSource) ? "" : " + ") + "IPTV";
                    if (result.Info.Director != null && (metadata.Directors == null || metadata.Directors.Count == 0))
                    {
                        metadata.Directors = result.Info.Director.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                          .Select(s => s.Trim()).ToList();
                    }

                    if (result.Info.Cast != null && (metadata.Cast == null || metadata.Cast.Count == 0))
                    {
                        metadata.Cast = result.Info.Cast.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                      .Select(s => s.Trim()).ToList();
                    }

                    // Update Stream URL if container extension is available
                    if (result.MovieData != null)
                    {
                        string ext = result.MovieData.ContainerExtension;
                        if (string.IsNullOrEmpty(ext)) ext = "mkv";
                        if (!ext.StartsWith(".")) ext = "." + ext;
                        
                        string streamUrl = $"{App.CurrentLogin.Host}/movie/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{result.MovieData.StreamId}{ext}";
                        vod.StreamUrl = streamUrl;

                        // However, for consistency with episodes:
                        metadata.MetadataId = result.MovieData.StreamId.ToString();
                    }

                    metadata.DataSource += " + IPTV";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] IPTV Movie Enrich Error: {ex.Message}");
            }
        }

        private async Task EnrichWithIptvAsync(UnifiedMetadata metadata, SeriesStream series)
        {
            if (App.CurrentLogin == null) return;
            
            try 
            {
                var info = await ContentCacheService.Instance.GetSeriesInfoAsync(series.SeriesId, App.CurrentLogin);
                if (info?.Info != null)
                {
                    // Map Basic Info
                    if (string.IsNullOrEmpty(metadata.Overview)) metadata.Overview = info.Info.Plot;
                    if (string.IsNullOrEmpty(metadata.Genres)) metadata.Genres = info.Info.Genre;
                    if (string.IsNullOrEmpty(metadata.Title)) metadata.Title = info.Info.Name;
                    if (string.IsNullOrEmpty(metadata.BackdropUrl)) metadata.BackdropUrl = info.Info.Cover;
                    if (string.IsNullOrEmpty(metadata.PosterUrl)) metadata.PosterUrl = info.Info.Cover;
                    
                    // Map Rating & Year
                    string ratingStr = !string.IsNullOrEmpty(info.Info.Rating) ? info.Info.Rating : series.Rating;
                    if (double.TryParse(ratingStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
                    {
                        metadata.Rating = r;
                    }
                    if (string.IsNullOrEmpty(metadata.Year)) metadata.Year = info.Info.ReleaseDate;
                    
                    if (info.Info.Cast != null && (metadata.Cast == null || metadata.Cast.Count == 0))
                    {
                        metadata.Cast = info.Info.Cast.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                      .Select(s => s.Trim()).ToList();
                    }

                    if (info.Info.Director != null && (metadata.Directors == null || metadata.Directors.Count == 0))
                    {
                        metadata.Directors = info.Info.Director.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                        .Select(s => s.Trim()).ToList();
                    }
                    
                    // Map Seasons/Episodes if missing
                    if (metadata.Seasons.Count == 0 && info.Episodes != null)
                    {
                        foreach (var kvp in info.Episodes)
                        {
                            if (int.TryParse(kvp.Key, out int seasonNum))
                            {
                                var season = new UnifiedSeason
                                {
                                    SeasonNumber = seasonNum,
                                    Name = seasonNum == 0 ? "Özel Bölümler" : $"{seasonNum}. Sezon"
                                };
                                
                                foreach (var ep in kvp.Value)
                                {
                                    int.TryParse(ep.EpisodeNum?.ToString(), out int epNum);
                                    string extension = ep.ContainerExtension;
                                    if (string.IsNullOrEmpty(extension)) extension = "mkv"; // Default
                                    if (!extension.StartsWith(".")) extension = "." + extension;

                                    string streamUrl = $"{App.CurrentLogin.Host}/series/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{ep.Id}{extension}";

                                    season.Episodes.Add(new UnifiedEpisode
                                    {
                                        Id = ep.Id,
                                        Title = ep.Title,
                                        Overview = ep.Info?.Plot, // Episode specific plot (rare but possible)
                                        ThumbnailUrl = ep.Info?.MovieImage,
                                        SeasonNumber = seasonNum,
                                        EpisodeNumber = epNum,
                                        StreamUrl = streamUrl, 
                                        RuntimeFormatted = ep.Info?.Duration
                                    });
                                }
                                metadata.Seasons.Add(season);
                            }
                        }
                         metadata.Seasons = metadata.Seasons.OrderBy(s => s.SeasonNumber).ToList();
                    }
                    
                    metadata.DataSource += " + IPTV";
                }
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[MetadataProvider] IPTV Enrich Error: {ex.Message}");
            }
        }

        public async Task EnrichSeasonAsync(UnifiedMetadata metadata, int seasonNumber)
        {
            if (metadata?.TmdbInfo == null) return;
            
            var season = metadata.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
            if (season == null) return;

            // Fetch detailed season info
            var tmdbSeason = await TmdbHelper.GetSeasonDetailsAsync(metadata.TmdbInfo.Id, seasonNumber);
            if (tmdbSeason?.Episodes == null) return;

            // Merge TMDB data into existing episodes, or create new ones
            foreach (var tmdbEp in tmdbSeason.Episodes)
            {
                var existing = season.Episodes.FirstOrDefault(e => e.EpisodeNumber == tmdbEp.EpisodeNumber);
                if (existing != null)
                {
                    // Enhance existing
                    if (!string.IsNullOrEmpty(tmdbEp.Name)) existing.Title = tmdbEp.Name;
                    if (!string.IsNullOrEmpty(tmdbEp.Overview)) existing.Overview = tmdbEp.Overview;
                    if (!string.IsNullOrEmpty(tmdbEp.StillUrl)) existing.ThumbnailUrl = tmdbEp.StillUrl;
                    if (!existing.AirDate.HasValue && tmdbEp.AirDateDateTime.HasValue) existing.AirDate = tmdbEp.AirDateDateTime;
                }
                else
                {
                    // Virtual Episode (TMDB knows it, Stremio doesn't)
                    season.Episodes.Add(new UnifiedEpisode
                    {
                        Id = $"tmdb:{metadata.TmdbInfo.Id}:{seasonNumber}:{tmdbEp.EpisodeNumber}", // Virtual ID
                        SeasonNumber = seasonNumber,
                        EpisodeNumber = tmdbEp.EpisodeNumber,
                        Title = tmdbEp.Name,
                        Overview = tmdbEp.Overview,
                        ThumbnailUrl = tmdbEp.StillUrl,
                        AirDate = tmdbEp.AirDateDateTime,
                        StreamUrl = null // No stream yet
                    });
                }
            }

            // Ensure correct order
            season.Episodes = season.Episodes.OrderBy(e => e.EpisodeNumber).ToList();
        }

        private void MapStremioToUnified(StremioMeta stremio, UnifiedMetadata unified)
        {
            unified.Title = stremio.Name;
            unified.Overview = stremio.Description;
            unified.PosterUrl = stremio.Poster;
            unified.BackdropUrl = stremio.Background;
            unified.LogoUrl = stremio.Logo;
            unified.Year = stremio.ReleaseInfo;
            unified.Genres = stremio.Genres != null ? string.Join(", ", stremio.Genres) : "";
            unified.Cast = stremio.Cast;
            unified.Runtime = stremio.Runtime;
            unified.MetadataId = stremio.Id; // Critical for History
            
            if (stremio.ImdbRating != null)
                unified.Rating = double.TryParse(stremio.ImdbRating.ToString(), out var r) ? r : 0;

             if (stremio.Trailers != null && stremio.Trailers.Any())
            {
                 var trailer = stremio.Trailers.FirstOrDefault();
                 // Stremio trailers usually provide a "source" field which is the YouTube ID
                 if (!string.IsNullOrEmpty(trailer.Source))
                 {
                     unified.TrailerUrl = trailer.Source;
                 }
            }
            
            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Mapped Stremio Title: '{unified.Title}' from '{stremio.Name}'");
        }

        private async Task<TmdbMovieResult> EnrichWithTmdbAsync(UnifiedMetadata metadata)
        {
            TmdbMovieResult tmdb = null;
            
            // Search TMDB
            if (metadata.IsSeries)
            {
                tmdb = await TmdbHelper.GetTvByExternalIdAsync(metadata.ImdbId);
            }
            else
            {
                tmdb = await TmdbHelper.SearchMovieAsync(metadata.Title, metadata.Year?.Split('–')[0]);
                // If ID-based search failed or wasn't possible, try searching by title? 
                // Using SearchMovieAsync covers title-based. 
                // But GetMovieByExternalIdAsync is safer if we have ID.
                if (tmdb == null && metadata.ImdbId != null && metadata.ImdbId.StartsWith("tt"))
                {
                     tmdb = await TmdbHelper.GetMovieByExternalIdAsync(metadata.ImdbId);
                }
            }

            if (tmdb != null)
            {
                metadata.DataSource = "Stremio + TMDB";
                
                // Prioritize localized TMDB data
                if (!string.IsNullOrEmpty(tmdb.Overview)) metadata.Overview = tmdb.Overview;
                if (!string.IsNullOrEmpty(tmdb.DisplayTitle) && tmdb.DisplayTitle != metadata.Title) 
                    metadata.Title = tmdb.DisplayTitle;

                // Higher quality images
                if (!string.IsNullOrEmpty(tmdb.FullBackdropUrl)) metadata.BackdropUrl = tmdb.FullBackdropUrl;
                
                // Update genres if needed
                var tmdbGenres = tmdb.GetGenreNames();
                if (tmdbGenres != "Genel") metadata.Genres = tmdbGenres;
                
                metadata.TmdbInfo = tmdb;
            }
            
            return tmdb;
        }

        private void ReconcileEpisodes(StremioMeta stremio, UnifiedMetadata metadata, TmdbMovieResult tmdb)
        {
            // 1. Map existing Stremio episodes
            if (stremio?.Videos != null)
            {
                var grouped = stremio.Videos
                    .Where(v => v.Season >= 0)
                    .GroupBy(v => v.Season)
                    .OrderBy(g => g.Key);

                foreach (var group in grouped)
                {
                    var season = new UnifiedSeason
                    {
                        SeasonNumber = group.Key,
                        Name = group.Key == 0 ? "Özel Bölümler" : $"{group.Key}. Sezon"
                    };

                    foreach (var vid in group.OrderBy(v => v.Episode))
                    {
                        season.Episodes.Add(new UnifiedEpisode
                        {
                            Id = vid.Id,
                            SeasonNumber = vid.Season,
                            EpisodeNumber = vid.Episode,
                            Title = !string.IsNullOrEmpty(vid.Name) ? vid.Name : vid.Title,
                            Overview = vid.Overview,
                            ThumbnailUrl = vid.Thumbnail,
                            AirDate = !string.IsNullOrEmpty(vid.Released) && DateTime.TryParse(vid.Released, out var d) ? d : null
                        });
                    }
                    metadata.Seasons.Add(season);
                }
            }

            // 2. Reconcile with TMDB Seasons (if available)
            if (tmdb?.Seasons != null)
            {
                foreach (var tmdbSeason in tmdb.Seasons)
                {
                    if (tmdbSeason.SeasonNumber == 0) continue; // Skip specials for now unless needed

                    var existingSeason = metadata.Seasons.FirstOrDefault(s => s.SeasonNumber == tmdbSeason.SeasonNumber);
                    if (existingSeason == null)
                    {
                        // Stremio missed this entire season! Add a placeholder.
                        // We do NOT populate episodes yet (too slow).
                        // They will be populated on-demand via EnrichSeasonAsync.
                        metadata.Seasons.Add(new UnifiedSeason
                        {
                            SeasonNumber = tmdbSeason.SeasonNumber,
                            Name = $"{tmdbSeason.SeasonNumber}. Sezon",
                            Episodes = new List<UnifiedEpisode>() // Empty for now
                        });
                    }
                }
                
                // Ensure Season Order
                metadata.Seasons = metadata.Seasons.OrderBy(s => s.SeasonNumber).ToList();
            }
        }

        private bool IsImdbId(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            // IMDb IDs start with 'tt' followed by numbers
            return id.StartsWith("tt") && id.Length >= 4;
        }
    }
}
