using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
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

        private readonly ConcurrentDictionary<string, Task<UnifiedMetadata>> _activeTasks = new();
        private readonly ConcurrentDictionary<string, (UnifiedMetadata Data, DateTime Expiry)> _resultCache = new();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(2); // Short term cache

        public async Task<UnifiedMetadata> GetMetadataAsync(Models.IMediaStream stream)
        {
            if (stream == null) return null;
            
            string id = stream.IMDbId;
            string type = (stream is ModernIPTVPlayer.SeriesStream || (stream is Models.Stremio.StremioMediaStream sms && (sms.Meta.Type == "series" || sms.Meta.Type == "tv"))) ? "series" : "movie";
            
            string cacheKey = $"{id ?? stream.Title}_{type}";

            // 1. Check Result Cache
            if (_resultCache.TryGetValue(cacheKey, out var cached) && DateTime.Now < cached.Expiry)
            {
                return cached.Data;
            }

            // 2. Check Active Tasks (Deduplication)
            return await _activeTasks.GetOrAdd(cacheKey, _ => GetMetadataInternalAsync(id ?? stream.Title, type, stream, cacheKey));
        }

        private async Task<UnifiedMetadata> GetMetadataInternalAsync(string id, string type, Models.IMediaStream sourceStream, string cacheKey)
        {
            try
            {
                var result = await GetMetadataAsync(id, type, sourceStream);
                
                if (result != null)
                {
                    _resultCache[cacheKey] = (result, DateTime.Now.Add(_cacheDuration));
                }
                
                return result;
            }
            finally
            {
                _activeTasks.TryRemove(cacheKey, out _);
            }
        }

        private readonly StremioService _stremioService = StremioService.Instance;

        public async Task<UnifiedMetadata> GetMetadataAsync(string id, string type, Models.IMediaStream sourceStream = null)
        {
            var metadata = new UnifiedMetadata 
            { 
                ImdbId = id, 
                IsSeries = type == "series" || type == "tv", 
                MetadataId = id,
                Title = sourceStream?.Title ?? id
            };

            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] START Fetching: {id} | Type: {type} | Source: {sourceStream?.GetType().Name ?? "Null"}");

            try
            {
                TmdbMovieResult tmdb = null;
                StremioMeta stremioMeta = null;

                // --- 1. TMDB PRIORITY (IF ENABLED & HAS ID/TITLE) ---
                if (AppSettings.IsTmdbEnabled)
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataProvider] [Priority 1] Trying TMDB...");
                    tmdb = await EnrichWithTmdbAsync(metadata);
                    if (tmdb != null)
                    {
                        metadata.TmdbInfo = tmdb;
                        metadata.MetadataSourceInfo = "TMDB (Primary)";
                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] SUCCESS: Enriched with TMDB: {tmdb.DisplayTitle}");
                        
                        // Fetch additional backdrops for slideshow
                        var additionalBackdrops = metadata.IsSeries ? 
                            await TmdbHelper.GetTvImagesAsync(tmdb.Id.ToString()) : 
                            await TmdbHelper.GetMovieImagesAsync(tmdb.Id.ToString());
                            
                        foreach(var bg in additionalBackdrops)
                        {
                            AddUniqueBackdrop(metadata, bg);
                        }
                        
                        if (metadata.BackdropUrls.Count > 0)
                            metadata.DataSource = "Stremio + TMDB Slideshow";
                    }
                }

                // --- 2. STREMIO ADDON CHAIN (FOR STREMIO CONTENT OR AS FALLBACK) ---
                bool isStremio = sourceStream is StremioMediaStream;
                if (isStremio || (!isStremio && string.IsNullOrEmpty(metadata.Overview)))
                {
                    var addonUrls = StremioAddonManager.Instance.GetAddons();
                    System.Diagnostics.Debug.WriteLine($"[MetadataProvider] [Priority 2] Trying Stremio Addons ({addonUrls.Count})...");

                    bool isPrimaryMetadataSet = (tmdb != null);
                    bool hasCheckedImagesForThisId = (tmdb != null);

                    string currentSearchId = id;

                    foreach (var url in addonUrls)
                    {
                        if (metadata.BackdropUrls.Count > 20) break; // Increased limit

                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Probing Addon: {url} with ID: {currentSearchId}");
                        var currentStremioMeta = await _stremioService.GetMetaAsync(url, type, currentSearchId);
                        
                        if (currentStremioMeta != null)
                        {
                            // If we found a proper IMDb ID, use it for subsequent addon calls (better compatibility)
                            if (currentSearchId.StartsWith("tmdb:") && !string.IsNullOrEmpty(currentStremioMeta.ImdbId))
                            {
                                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Switching search ID from {currentSearchId} to {currentStremioMeta.ImdbId}");
                                currentSearchId = currentStremioMeta.ImdbId;
                                metadata.ImdbId = currentSearchId; // Update core metadata too
                            }

                            if (IsValidMetadata(currentStremioMeta) && !isPrimaryMetadataSet)
                            {
                                MapStremioToUnified(currentStremioMeta, metadata);
                                stremioMeta = currentStremioMeta; // Keep for episode reconciliation
                                
                                // Source Info
                                string addonHost = new Uri(url).Host;
                                metadata.MetadataSourceInfo = $"{addonHost}";
                                if (!string.IsNullOrEmpty(currentStremioMeta.Background) && currentStremioMeta.Background.Contains("tmdb.org"))
                                    metadata.MetadataSourceInfo += " (TMDB Metadata)";
                                
                                metadata.DataSource = string.IsNullOrEmpty(metadata.DataSource) ? "Stremio" : metadata.DataSource + " + Stremio";
                                isPrimaryMetadataSet = true;
                                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Primary metadata set from: {url}");
                            }
                            else if (!IsValidMetadata(currentStremioMeta))
                            {
                                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] REJECTED invalid metadata from {url} (Name: {currentStremioMeta.Name}, ID: {currentStremioMeta.Id})");
                                continue; // Skip to next addon
                            }
                            
                            if (currentStremioMeta.Background != null)
                            {
                                AddUniqueBackdrop(metadata, currentStremioMeta.Background);
                            }

                            // Deep Image Enrichment: If this addon provides a TMDB ID, use it for images
                            string discoveredTmdbId = currentStremioMeta.MovieDbId?.ToString();
                            if (string.IsNullOrEmpty(discoveredTmdbId) && currentStremioMeta.Id?.StartsWith("tmdb:") == true)
                                discoveredTmdbId = currentStremioMeta.Id.Replace("tmdb:", "");

                            // [FIX] Allow image fetching if we have an API key, even if general metadata is disabled
                            bool hasTmdbKey = !string.IsNullOrEmpty(AppSettings.TmdbApiKey);
                            if (!string.IsNullOrEmpty(discoveredTmdbId) && !hasCheckedImagesForThisId && hasTmdbKey)
                            {
                                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Discovered TMDB ID {discoveredTmdbId} from {url}. Fetching images...");
                                var additionalBackdrops = metadata.IsSeries ? 
                                    await TmdbHelper.GetTvImagesAsync(discoveredTmdbId) : 
                                    await TmdbHelper.GetMovieImagesAsync(discoveredTmdbId);
                                    
                                foreach(var bg in additionalBackdrops)
                                {
                                    AddUniqueBackdrop(metadata, bg);
                                }
                                hasCheckedImagesForThisId = true;
                                if (additionalBackdrops.Count > 0)
                                    metadata.DataSource = (metadata.DataSource ?? "") + " + TMDB Slideshow";
                            }
                        }
                    }
                }

                // --- 3. EPISODE RECONCILIATION ---
                if (metadata.IsSeries)
                {
                    ReconcileEpisodes(stremioMeta, metadata, tmdb);
                }

                // --- 4. IPTV FALLBACK ---
                bool needsIptvEnrichment = string.IsNullOrEmpty(metadata.Overview) || metadata.Overview == "Açıklama mevcut değil." || !IsImdbId(metadata.ImdbId);
                
                if (needsIptvEnrichment)
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataProvider] [Priority 3] Trying IPTV Enrichment...");
                    if (metadata.IsSeries && sourceStream is SeriesStream seriesStream)
                    {
                        await EnrichWithIptvAsync(metadata, seriesStream);
                    }
                    else if (!metadata.IsSeries && sourceStream != null)
                    {
                        await EnrichWithIptvMovieAsync(metadata, sourceStream);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] CRITICAL ERROR: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] FINISHED: {metadata.Title} via {metadata.DataSource}");
            return metadata;
        }

        private bool IsValidMetadata(Models.Stremio.StremioMeta meta)
        {
            if (meta == null) return false;
            
            // 1. Check for common error patterns in name/description
            if (meta.Name != null && (meta.Name.Contains("[❌]") || meta.Name.Contains("fetch failed", StringComparison.OrdinalIgnoreCase))) 
                return false;

            if (meta.Description != null && (meta.Description.Equals("fetch failed", StringComparison.OrdinalIgnoreCase) || meta.Description.Contains("Error", StringComparison.OrdinalIgnoreCase)))
                return false;

            // 2. Check ID for error prefixes
            if (meta.Id != null && (meta.Id.StartsWith("error", StringComparison.OrdinalIgnoreCase) || meta.Id.Contains("aiostreamserror")))
                return false;

            return true;
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
            // Only overwrite if currently empty, as TMDB has higher priority for descriptive text
            if (string.IsNullOrEmpty(unified.Title) || unified.Title == unified.MetadataId)
                unified.Title = stremio.Name;

            if (string.IsNullOrEmpty(unified.Overview) || unified.Overview == "Açıklama mevcut değil.")
                unified.Overview = stremio.Description;

            if (string.IsNullOrEmpty(unified.PosterUrl))
                unified.PosterUrl = stremio.Poster;

            if (string.IsNullOrEmpty(unified.BackdropUrl))
                unified.BackdropUrl = stremio.Background;

            if (string.IsNullOrEmpty(unified.LogoUrl))
                unified.LogoUrl = stremio.Logo;

            if (string.IsNullOrEmpty(unified.Year))
                unified.Year = stremio.ReleaseInfo;

            if (string.IsNullOrEmpty(unified.Genres))
                unified.Genres = (stremio.Genres != null && stremio.Genres.Count > 0) ? string.Join(", ", stremio.Genres) : "";

            if (unified.Cast == null || unified.Cast.Count == 0)
                unified.Cast = stremio.Cast;

            if (string.IsNullOrEmpty(unified.Runtime))
                unified.Runtime = stremio.Runtime;

            // MetadataId is essential for history and addon communication
            // If the current ID is generic/fallback, we use the Stremio provided ID
            if (string.IsNullOrEmpty(unified.MetadataId) || !IsImdbId(unified.MetadataId))
                unified.MetadataId = stremio.Id;
            
            if (stremio.ImdbRating != null && unified.Rating == 0)
                unified.Rating = double.TryParse(stremio.ImdbRating.ToString(), out var r) ? r : 0;

             if (stremio.Trailers != null && stremio.Trailers.Any() && string.IsNullOrEmpty(unified.TrailerUrl))
            {
                 var trailer = stremio.Trailers.FirstOrDefault();
                 if (!string.IsNullOrEmpty(trailer.Source))
                 {
                     unified.TrailerUrl = trailer.Source;
                 }
            }
            
            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Mapped data from addon for: '{unified.Title}'");
        }

        private async Task<TmdbMovieResult?> EnrichWithTmdbAsync(UnifiedMetadata metadata)
        {
            TmdbMovieResult? tmdb = null;
            
            // Search TMDB
            if (metadata.IsSeries)
            {
                if (metadata.ImdbId != null && metadata.ImdbId.StartsWith("tmdb:"))
                {
                    int.TryParse(metadata.ImdbId.Replace("tmdb:", ""), out int tvId);
                    if (tvId > 0) tmdb = await TmdbHelper.GetTvByIdAsync(tvId);
                }
                
                if (tmdb == null)
                    tmdb = await TmdbHelper.GetTvByExternalIdAsync(metadata.ImdbId);
            }
            else
            {
                if (metadata.ImdbId != null && metadata.ImdbId.StartsWith("tmdb:"))
                {
                    int.TryParse(metadata.ImdbId.Replace("tmdb:", ""), out int movieId);
                    if (movieId > 0) tmdb = await TmdbHelper.GetMovieByIdAsync(movieId);
                }

                if (tmdb == null)
                {
                    tmdb = await TmdbHelper.SearchMovieAsync(metadata.Title, metadata.Year?.Split('–')[0]);
                    
                    if (tmdb == null && metadata.ImdbId != null && metadata.ImdbId.StartsWith("tt"))
                    {
                         tmdb = await TmdbHelper.GetMovieByExternalIdAsync(metadata.ImdbId);
                    }
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

                // Propagate IMDb ID if found to help addon lookup
                if (!string.IsNullOrEmpty(tmdb.ImdbId) && (string.IsNullOrEmpty(metadata.ImdbId) || metadata.ImdbId.StartsWith("tmdb:")))
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Resolved IMDb ID from TMDB: {tmdb.ImdbId}");
                    metadata.ImdbId = tmdb.ImdbId;
                }
            }
            
            return tmdb;
        }

        private void ReconcileEpisodes(StremioMeta stremio, UnifiedMetadata metadata, TmdbMovieResult tmdb)
        {
            // 1. Map existing Stremio episodes (if any)
            if (stremio?.Videos != null)
            {
                var grouped = stremio.Videos
                    .Where(v => v.Season >= 0)
                    .GroupBy(v => v.Season)
                    .OrderBy(g => g.Key);

                foreach (var group in grouped)
                {
                    var season = metadata.Seasons.FirstOrDefault(s => s.SeasonNumber == group.Key);
                    if (season == null)
                    {
                        season = new UnifiedSeason
                        {
                            SeasonNumber = group.Key,
                            Name = group.Key == 0 ? "Özel Bölümler" : $"{group.Key}. Sezon"
                        };
                        metadata.Seasons.Add(season);
                    }

                    foreach (var vid in group.OrderBy(v => v.Episode))
                    {
                        if (!season.Episodes.Any(e => e.EpisodeNumber == vid.Episode))
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
                    }
                }
            }

            // 2. Reconcile with TMDB Seasons (if available) - Adds seasons/episodes Stremio might be missing
            if (tmdb?.Seasons != null)
            {
                foreach (var tmdbSeason in tmdb.Seasons)
                {
                    // We typically prioritize TMDB season structure
                    var existingSeason = metadata.Seasons.FirstOrDefault(s => s.SeasonNumber == tmdbSeason.SeasonNumber);
                    if (existingSeason == null)
                    {
                        metadata.Seasons.Add(new UnifiedSeason
                        {
                            SeasonNumber = tmdbSeason.SeasonNumber,
                            Name = tmdbSeason.SeasonNumber == 0 ? "Özel Bölümler" : $"{tmdbSeason.SeasonNumber}. Sezon",
                            Episodes = new List<UnifiedEpisode>()
                        });
                    }
                    else if (existingSeason.Episodes.Count < tmdbSeason.EpisodeCount)
                    {
                        // Stremio has the season but looks incomplete compared to TMDB count
                        // We'll let EnrichSeasonAsync handle the detail later if needed,
                        // but we could pre-fill virtual IDs here if we want instant placeholders.
                    }
                }
            }

            // 3. Fallback: If absolutely no seasons found but it's a series, create at least Season 1 if we have an ID
            if (metadata.Seasons.Count == 0 && !string.IsNullOrEmpty(metadata.ImdbId))
            {
                 metadata.Seasons.Add(new UnifiedSeason { SeasonNumber = 1, Name = "1. Sezon" });
            }
            
            // Ensure correct order
            metadata.Seasons = metadata.Seasons.OrderBy(s => s.SeasonNumber).ToList();
        }

        private void AddUniqueBackdrop(UnifiedMetadata metadata, string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            
            string id = ExtractImageId(url);
            if (string.IsNullOrEmpty(id))
            {
                if (!metadata.BackdropUrls.Contains(url)) metadata.BackdropUrls.Add(url);
                return;
            }

            int existingIndex = -1;
            for (int i = 0; i < metadata.BackdropUrls.Count; i++)
            {
                if (ExtractImageId(metadata.BackdropUrls[i]) == id)
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex == -1)
            {
                metadata.BackdropUrls.Add(url);
            }
            else
            {
                string existingUrl = metadata.BackdropUrls[existingIndex];
                if (GetQualityScore(url) > GetQualityScore(existingUrl))
                {
                    metadata.BackdropUrls[existingIndex] = url;
                }
            }
        }

        private string ExtractImageId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                if (url.Contains("metahub"))
                {
                    var parts = url.Split('/');
                    var ttId = parts.FirstOrDefault(p => p.StartsWith("tt"));
                    return ttId ?? url;
                }

                int lastSlash = url.LastIndexOf('/');
                if (lastSlash >= 0 && lastSlash < url.Length - 1)
                {
                    string file = url.Substring(lastSlash + 1);
                    if (file.Contains(".")) return file;
                }
            }
            catch { }
            return url;
        }

        private int GetQualityScore(string url)
        {
            if (url.Contains("/original/")) return 10;
            if (url.Contains("/w1280/")) return 8;
            if (url.Contains("/w780/")) return 6;
            if (url.Contains("/w500/")) return 4;
            if (url.Contains("/medium/")) return 3;
            return 1;
        }

        private bool IsImdbId(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            // IMDb IDs start with 'tt' followed by numbers
            return id.StartsWith("tt") && id.Length >= 4;
        }
    }
}
