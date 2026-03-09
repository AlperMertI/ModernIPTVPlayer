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
    public enum MetadataContext { Discovery, Detail }
    
    public class MetadataProvider
    {
        private static readonly object _instanceLock = new object();
        private static MetadataProvider _instance;
        public static MetadataProvider Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new MetadataProvider();
                    }
                }
                return _instance;
            }
        }

        private readonly ConcurrentDictionary<string, Task<UnifiedMetadata>> _activeTasks = new();
        private readonly ConcurrentDictionary<string, (UnifiedMetadata Data, DateTime Expiry)> _resultCache = new();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(2); // Short term cache

        public async Task<UnifiedMetadata> GetMetadataAsync(Models.IMediaStream stream, MetadataContext context = MetadataContext.Detail, Action<string> onBackdropFound = null)
        {
            if (stream == null) return null;
            
            string id = stream.IMDbId;
            
            // 0. Global Check: Ignore composite / non-standard IDs to avoid hammering addons at startup
            if (id != null && (id.Contains(",") || id.Contains(" ") || id.Length > 100) && !id.StartsWith("tmdb:") && !id.StartsWith("tt"))
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] REJECTED non-standard ID: {id}");
                return null;
            }

            string type = (stream is ModernIPTVPlayer.SeriesStream || (stream is Models.Stremio.StremioMediaStream sms && (sms.Meta.Type == "series" || sms.Meta.Type == "tv"))) ? "series" : "movie";
            
            string cacheKey = $"{id ?? stream.Title}_{type}";
            if (context == MetadataContext.Discovery) cacheKey += "_discovery";

            // 1. Check Result Cache
            if (_resultCache.TryGetValue(cacheKey, out var cached) && DateTime.Now < cached.Expiry)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] CACHE HIT: {cacheKey}");
                // If cached, trigger callback for all existing backdrops immediately
                if (onBackdropFound != null && cached.Data.BackdropUrls != null)
                {
                    foreach (var bg in cached.Data.BackdropUrls) onBackdropFound(bg);
                }
                return cached.Data;
            }

            // [NEW] If asking for Detail, but have a "Satisfied" Discovery result in cache, use it!
            if (context == MetadataContext.Detail)
            {
                string discoveryKey = cacheKey + "_discovery";
                if (_resultCache.TryGetValue(discoveryKey, out var disc) && DateTime.Now < disc.Expiry)
                {
                    if (IsSatisfied(disc.Data, MetadataContext.Detail))
                    {
                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] PROMOTED Discovery cache to Detail: {cacheKey}");
                        return disc.Data;
                    }
                    // [SEED] If not fully satisfied, seed the Detail fetch with Discovery data!
                    // This allows skipping addons that were already probed.
                    System.Diagnostics.Debug.WriteLine($"[MetadataProvider] SEEDING Detail fetch with Discovery data for: {cacheKey}");
                    return await _activeTasks.GetOrAdd(cacheKey, _ => GetMetadataInternalAsync(id ?? stream.Title, type, stream, cacheKey, context, onBackdropFound, disc.Data));
                }
            }

            // 2. Check Active Tasks (Deduplication)
            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] START Fetching ({context}): {id ?? stream.Title} | Type: {type}");
            return await _activeTasks.GetOrAdd(cacheKey, _ => GetMetadataInternalAsync(id ?? stream.Title, type, stream, cacheKey, context, onBackdropFound));
        }

        private async Task<UnifiedMetadata> GetMetadataInternalAsync(string id, string type, Models.IMediaStream sourceStream, string cacheKey, MetadataContext context, Action<string> onBackdropFound = null, UnifiedMetadata seed = null)
        {
            // [FIX] Normalize ID to canonical IMDB ID immediately to avoid search failures on priority addons
            string normalizedId = NormalizeId(id) ?? id;
            try
            {
                var result = await GetMetadataAsync(normalizedId, type, sourceStream, context, onBackdropFound, seed);
                
                if (result != null)
                {
                    var expiry = DateTime.Now.Add(_cacheDuration);
                    _resultCache[cacheKey] = (result, expiry);
                    
                    // If the ID changed (e.g. tmdb -> imdb), cache under the new ID as well
                    if (!string.IsNullOrEmpty(result.ImdbId) && result.ImdbId != normalizedId)
                    {
                        string newKey = $"{result.ImdbId}_{type}";
                        if (context == MetadataContext.Discovery) newKey += "_discovery";
                        _resultCache[newKey] = (result, expiry);
                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Multi-cached: {cacheKey} AND {newKey}");
                    }
                }
                
                return result;
            }
            finally
            {
                _activeTasks.TryRemove(cacheKey, out _);
            }
        }

        private readonly StremioService _stremioService = StremioService.Instance;

        private async Task<UnifiedMetadata> GetMetadataAsync(string id, string type, Models.IMediaStream sourceStream = null, MetadataContext context = MetadataContext.Detail, Action<string> onBackdropFound = null, UnifiedMetadata seed = null)
        {
            var metadata = seed ?? new UnifiedMetadata 
            { 
                ImdbId = id, 
                IsSeries = type == "series" || type == "tv", 
                MetadataId = id,
                Title = sourceStream?.Title ?? id
            };

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
                            AddUniqueBackdrop(metadata, bg, onBackdropFound);
                        }
                        
                        if (metadata.BackdropUrls.Count > 0)
                            metadata.DataSource = "Stremio + TMDB Slideshow";

                        // [OPTIMIZATION] If TMDB succeeded with full info for a movie, skip Stremio probes in Discovery mode
                        if (context == MetadataContext.Discovery && !metadata.IsSeries && !string.IsNullOrEmpty(metadata.Overview))
                        {
                            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Discovery: TMDB success for movie. Skipping Stremio probes.");
                            return metadata;
                        }
                    }
                }

                // --- 2. STREMIO ADDON CHAIN (FOR STREMIO CONTENT OR AS FALLBACK) ---
                bool isStremio = sourceStream is StremioMediaStream;
                if (isStremio || (!isStremio && string.IsNullOrEmpty(metadata.Overview)))
                {
                    var addonUrls = StremioAddonManager.Instance.GetAddons();
                    System.Diagnostics.Debug.WriteLine($"[MetadataProvider] [Priority 2] Trying Stremio Addons ({addonUrls.Count})...");

                    // [FIX] Priority: Respect primary metadata already set by TMDB or Seeding
                    bool isPrimaryMetadataSet = (tmdb != null) || !string.IsNullOrEmpty(metadata.MetadataSourceInfo);
                    string? lastCheckedTmdbId = tmdb?.Id.ToString();
                    string currentSearchId = IsImdbId(metadata.ImdbId) ? metadata.ImdbId : id;
                    
                    bool idUpgraded = false;
                    var probedAddonsWithTtId = new HashSet<string>();

                    // Pass 1: Resolve ID if needed, and/or get initial metadata
                    foreach (var url in addonUrls)
                    {
                        // [NEW] Skip if already probed for this item
                        if (metadata.ProbedAddons.Contains(url)) continue;

                        // [NEW] Safety: Don't probe if ID is an error
                        if (currentSearchId != null && (currentSearchId.StartsWith("error", StringComparison.OrdinalIgnoreCase) || currentSearchId.Contains("aiostreamserror")))
                        {
                            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Skipping addon probe for error ID: {currentSearchId}");
                            continue;
                        }

                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Probing Addon: {url} with ID: {currentSearchId}");
                        metadata.ProbedAddons.Add(url);
                        var currentStremioMeta = await _stremioService.GetMetaAsync(url, type, currentSearchId);
                        
                        if (currentStremioMeta != null)
                        {
                            // 1. ID UPGRADE CHECK
                            string? discoveredImdbId = ExtractImdbId(currentStremioMeta);
                            if (!IsImdbId(currentSearchId) && IsImdbId(discoveredImdbId))
                            {
                                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Switching search ID from {currentSearchId} to {discoveredImdbId}");
                                currentSearchId = discoveredImdbId;
                                metadata.ImdbId = currentSearchId;
                                idUpgraded = true;

                                // Optimization: Only re-query if initial response for series is missing videos
                                bool initialIsComplete = !string.IsNullOrEmpty(currentStremioMeta.Description) && currentStremioMeta.Genres?.Count > 0;
                                if (type == "series" && (currentStremioMeta.Videos == null || currentStremioMeta.Videos.Count == 0))
                                    initialIsComplete = false;

                                if (!initialIsComplete)
                                {
                                    var fullMeta = await _stremioService.GetMetaAsync(url, type, currentSearchId);
                                    if (fullMeta != null && IsValidMetadata(fullMeta))
                                        currentStremioMeta = fullMeta;
                                }
                                
                                // Mapping this "resolver" data
                                MapStremioToUnified(currentStremioMeta, metadata, !isPrimaryMetadataSet);
                                if (!isPrimaryMetadataSet) 
                                {
                                    isPrimaryMetadataSet = true;
                                    stremioMeta = currentStremioMeta;
                                    metadata.MetadataSourceInfo = GetHostSafe(url);
                                    metadata.DataSource = (string.IsNullOrEmpty(metadata.DataSource)) ? "Stremio" : metadata.DataSource + " + Stremio";
                                }

                                if (metadata.IsSeries) MergeStremioEpisodes(currentStremioMeta, metadata);
                                
                                // [FIX] Add background to list before satisfaction check
                                if (currentStremioMeta.Background != null && metadata.BackdropUrls.Count <= 20)
                                {
                                    AddUniqueBackdrop(metadata, currentStremioMeta.Background, onBackdropFound);
                                }

                                // [NEW] Early exit only if actually satisfied AND not missing a trailer we might find elsewhere
                                if (IsSatisfied(metadata, context))
                                {
                                    // If we are in Discovery mode and missing a trailer, check if we should keep probing 
                                    // to fulfill Spotlight requirements (Cinemeta/Torbox often have them even if AioStreams doesn't)
                                    if (context == MetadataContext.Discovery && string.IsNullOrEmpty(metadata.TrailerUrl))
                                    {
                                         System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Discovery satisfied by {GetHostSafe(url)} but MISSING TRAILER. Continuing search...");
                                    }
                                    else
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Resolved ID and satisfied by {GetHostSafe(url)}. Early exit.");
                                        return metadata;
                                    }
                                }
                                
                                // RESTART LOOP WITH CANONICAL ID (Break and proceed to Pass 2)
                                probedAddonsWithTtId.Add(url);
                                break; 
                            }
                            
                            // 2. NORMAL MAPPING (IF VALID)
                            if (IsValidMetadata(currentStremioMeta))
                            {
                                bool isHighestPrioritySoFar = !isPrimaryMetadataSet;
                                MapStremioToUnified(currentStremioMeta, metadata, isHighestPrioritySoFar);

                                if (isHighestPrioritySoFar)
                                {
                                    stremioMeta = currentStremioMeta;
                                    metadata.MetadataSourceInfo = GetHostSafe(url);
                                    if (!string.IsNullOrEmpty(currentStremioMeta.Background) && currentStremioMeta.Background.Contains("tmdb.org"))
                                        metadata.MetadataSourceInfo += " (TMDB Metadata)";
                                    
                                    metadata.DataSource = string.IsNullOrEmpty(metadata.DataSource) ? "Stremio" : metadata.DataSource + " + Stremio";
                                    isPrimaryMetadataSet = true;
                                }

                                if (metadata.IsSeries) MergeStremioEpisodes(currentStremioMeta, metadata);
                                if (IsImdbId(currentSearchId)) probedAddonsWithTtId.Add(url);

                                // Enrichment: Backdrops (Done here for non-upgrade path)
                                if (currentStremioMeta.Background != null && metadata.BackdropUrls.Count <= 20)
                                {
                                    AddUniqueBackdrop(metadata, currentStremioMeta.Background, onBackdropFound);
                                }

                                 string discoveredTmdbId = currentStremioMeta.MovieDbId?.ToString();
                                if (string.IsNullOrEmpty(discoveredTmdbId) && currentStremioMeta.Id?.StartsWith("tmdb:") == true)
                                    discoveredTmdbId = currentStremioMeta.Id.Replace("tmdb:", "");

                                bool hasTmdbKey = !string.IsNullOrEmpty(AppSettings.TmdbApiKey);
                                if (!string.IsNullOrEmpty(discoveredTmdbId) && lastCheckedTmdbId != discoveredTmdbId && hasTmdbKey)
                                {
                                    var additionalBackdrops = metadata.IsSeries ? 
                                        await TmdbHelper.GetTvImagesAsync(discoveredTmdbId) : 
                                        await TmdbHelper.GetMovieImagesAsync(discoveredTmdbId);
                                        
                                    foreach(var bg in additionalBackdrops) AddUniqueBackdrop(metadata, bg, onBackdropFound);
                                    
                                    lastCheckedTmdbId = discoveredTmdbId;
                                    metadata.MetadataSourceInfo += (metadata.MetadataSourceInfo?.Length > 0 ? " + " : "") + "TMDB Slideshow";
                                }

                                // Early Break if Satisfied
                                if (IsSatisfied(metadata, context))
                                {
                                    if (context == MetadataContext.Discovery && string.IsNullOrEmpty(metadata.TrailerUrl))
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Pass 1 satisfied by {GetHostSafe(url)} but MISSING TRAILER. Continuing search...");
                                    }
                                    else
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Pass 1 early break! Satisfied by {url}");
                                        return metadata;
                                    }
                                }
                            }
                        }
                    }

                    if (idUpgraded)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Restarting prioritized search with {currentSearchId}");
                        // [FIX] Priority: Only treat Pass 2 as "Primary" if Pass 1 (the resolver) didn't already set it.
                        bool pass2FirstMatch = !isPrimaryMetadataSet;

                        foreach (var url in addonUrls)
                        {
                            // [REFINEMENT] If ID was upgraded, we WANT to re-probe high-priority addons 
                            // that might have been skipped or failed in Pass 1 because they didn't support tmdb:ID.
                            if (probedAddonsWithTtId.Contains(url)) continue; 

                            // [NEW] Safety: Don't probe if ID is an error
                            if (currentSearchId != null && (currentSearchId.StartsWith("error", StringComparison.OrdinalIgnoreCase) || currentSearchId.Contains("aiostreamserror")))
                            {
                                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Skipping addon probe (Pass 2) for error ID: {currentSearchId}");
                                continue;
                            }

                            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Probing Addon (Pass 2): {url} with ID: {currentSearchId}");
                            metadata.ProbedAddons.Add(url);
                            var currentStremioMeta = await _stremioService.GetMetaAsync(url, type, currentSearchId);
                            if (currentStremioMeta != null && IsValidMetadata(currentStremioMeta))
                            {
                                // --- ID CHECK ---
                                string? respId = ExtractImdbId(currentStremioMeta);
                                if (IsImdbId(currentSearchId) && !string.IsNullOrEmpty(respId) && respId != currentSearchId)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Mismatched ID in Pass 2 ({url})! Expected {currentSearchId}, got {respId}. Skipping.");
                                    continue;
                                }

                                MapStremioToUnified(currentStremioMeta, metadata, pass2FirstMatch); 
                                
                                if (pass2FirstMatch)
                                {
                                    stremioMeta = currentStremioMeta;
                                    metadata.MetadataSourceInfo = $"{GetHostSafe(url)} (Priority Upgrade)";
                                    isPrimaryMetadataSet = true;
                                }

                                if (metadata.IsSeries) MergeStremioEpisodes(currentStremioMeta, metadata, pass2FirstMatch);

                                // Enrichment in Pass 2
                                if (currentStremioMeta.Background != null)
                                {
                                    if (metadata.BackdropUrls.Count <= 20)
                                        AddUniqueBackdrop(metadata, currentStremioMeta.Background, onBackdropFound);
                                }

                                string discoveredTmdbId = currentStremioMeta.MovieDbId?.ToString();
                                if (string.IsNullOrEmpty(discoveredTmdbId) && currentStremioMeta.Id?.StartsWith("tmdb:") == true)
                                    discoveredTmdbId = currentStremioMeta.Id.Replace("tmdb:", "");

                                if (!string.IsNullOrEmpty(discoveredTmdbId) && lastCheckedTmdbId != discoveredTmdbId && !string.IsNullOrEmpty(AppSettings.TmdbApiKey))
                                {
                                    var additionalBackdrops = metadata.IsSeries ? 
                                        await TmdbHelper.GetTvImagesAsync(discoveredTmdbId) : 
                                        await TmdbHelper.GetMovieImagesAsync(discoveredTmdbId);
                                        
                                    foreach(var bg in additionalBackdrops) AddUniqueBackdrop(metadata, bg, onBackdropFound);
                                    lastCheckedTmdbId = discoveredTmdbId;
                                }

                                pass2FirstMatch = false; // Next ones only fill gaps

                                // Early Break if Satisfied
                                if (IsSatisfied(metadata, context))
                                {
                                    if (context == MetadataContext.Discovery && string.IsNullOrEmpty(metadata.TrailerUrl))
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Pass 2 satisfied by {GetHostSafe(url)} but MISSING TRAILER. Continuing search...");
                                    }
                                    else
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Pass 2 early break! Satisfied by {url}");
                                        return metadata;
                                    }
                                }
                            }
                        }
                    }
                }

                // --- 3. EPISODE RECONCILIATION ---
                if (metadata.IsSeries)
                {
                    ReconcileTmdbSeasons(metadata, tmdb);
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

        private bool IsSatisfied(UnifiedMetadata metadata, MetadataContext context)
        {
            // Always satisfy discovery first
            bool discoveryComplete = IsDiscoveryComplete(metadata);
            if (context == MetadataContext.Discovery) return discoveryComplete;

            // For Detail mode: 
            // We want Title, Overview, Trailer, and Logo.
            // Backdrop count: 2 is usually enough for movies from Stremio addons.
            bool hasImages = metadata.BackdropUrls.Count >= 2;
            bool hasTrailer = !string.IsNullOrEmpty(metadata.TrailerUrl);
            bool hasLogo = !string.IsNullOrEmpty(metadata.LogoUrl) || metadata.IsSeries;

            bool satisfied = discoveryComplete && hasTrailer && hasLogo && hasImages;
            
            if (!satisfied)
            {
                var reasons = new List<string>();
                if (!discoveryComplete) reasons.Add("Discovery incomplete (missing Title/Overview/Rating/Year/Genres)");
                if (!hasTrailer) reasons.Add("Missing Trailer");
                if (!hasLogo) reasons.Add("Missing Logo");
                if (!hasImages) reasons.Add($"Insufficient images ({metadata.BackdropUrls.Count}/2)");
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Not satisfied for Detail: {string.Join(", ", reasons)}");
            }

            return satisfied;
        }

        private string GetHostSafe(string url)
        {
            if (string.IsNullOrEmpty(url)) return "Unknown";
            try
            {
                return new Uri(url).Host;
            }
            catch
            {
                // Fallback: manually extract host if possible
                var host = url.Replace("https://", "").Replace("http://", "").Split('/')[0];
                return string.IsNullOrEmpty(host) ? "Unknown" : host;
            }
        }

        private bool IsDiscoveryComplete(UnifiedMetadata metadata)
        {
            // [REFINEMENT] Loosen rating requirement for Discovery. 
            // Some newer items might not have a rating yet, but we want to show the TR title and posters.
            return !string.IsNullOrEmpty(metadata.Title) && 
                   !string.IsNullOrEmpty(metadata.BackdropUrl) &&
                   !string.IsNullOrEmpty(metadata.Year) &&
                   !string.IsNullOrEmpty(metadata.Overview) &&
                   (metadata.Rating > 0 || metadata.DataSource.Contains("AioStreams")) && // AioStreams is high priority enough to break early
                   !string.IsNullOrEmpty(metadata.Genres);
        }

        private bool IsValidMetadata(Models.Stremio.StremioMeta meta)
        {
            if (meta == null) return false;

            // 0. Safety: Meta must have at least a Name or an ID to be considered valid
            if (string.IsNullOrEmpty(meta.Name) && string.IsNullOrEmpty(meta.Id))
                return false;
            
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
                    if (!string.IsNullOrEmpty(tmdbEp.Name)) 
                    {
                        // [FIX] TMDB often returns generic titles ("Season 2 Episode 1") while Stremio has proper ones.
                        // Only override if the new title is NOT generic, or if the current title IS generic.
                        bool existingIsGeneric = IsGenericEpisodeTitle(existing.Title, metadata.Title);
                        bool newIsGeneric = IsGenericEpisodeTitle(tmdbEp.Name, metadata.Title);
                        if (existingIsGeneric || !newIsGeneric)
                        {
                            existing.Title = tmdbEp.Name;
                        }
                    }
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

        private void MapStremioToUnified(StremioMeta stremio, UnifiedMetadata unified, bool overwritePrimary)
        {
            // Only overwrite if currently empty or explicitly requested by priority, as TMDB has higher priority for descriptive text
            // [REFINEMENT] If overwritePrimary is true, only overwrite if source has data (don't overwrite with null)
            
            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Name)) || string.IsNullOrEmpty(unified.Title) || unified.Title == unified.MetadataId)
                unified.Title = stremio.Name;
            else if (!overwritePrimary && !string.IsNullOrEmpty(stremio.Name) && stremio.Name != unified.Title)
            {
                // Secondary title (e.g. English title from Cinemeta when Turkish is primary)
                if (string.IsNullOrEmpty(unified.SubTitle))
                    unified.SubTitle = stremio.Name;
            }

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Description)) || string.IsNullOrEmpty(unified.Overview) || unified.Overview == "Açıklama mevcut değil.")
                unified.Overview = stremio.Description;

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Poster)) || string.IsNullOrEmpty(unified.PosterUrl))
                unified.PosterUrl = stremio.Poster;

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Background)) || string.IsNullOrEmpty(unified.BackdropUrl))
                unified.BackdropUrl = stremio.Background;

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Logo)) || string.IsNullOrEmpty(unified.LogoUrl))
                unified.LogoUrl = stremio.Logo;

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.ReleaseInfo)) || string.IsNullOrEmpty(unified.Year))
                unified.Year = stremio.ReleaseInfo;

            if ((overwritePrimary && stremio.Genres?.Count > 0) || string.IsNullOrEmpty(unified.Genres))
                unified.Genres = (stremio.Genres != null && stremio.Genres.Count > 0) ? string.Join(", ", stremio.Genres) : "";

            if (overwritePrimary || unified.Cast == null || unified.Cast.Count == 0)
            {
                if (stremio.Cast?.Count > 0)
                    unified.Cast = stremio.Cast?.Take(25).ToList();
            }

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Runtime)) || string.IsNullOrEmpty(unified.Runtime))
                unified.Runtime = stremio.Runtime;

            // MetadataId is essential for history and addon communication
            // If the current ID is generic/fallback, we use the Stremio provided ID
            if (string.IsNullOrEmpty(unified.MetadataId) || !IsImdbId(unified.MetadataId))
                unified.MetadataId = stremio.Id;
            
            if (stremio.ImdbRating != null)
            {
                // [FIX] Culture-proof rating parsing. AioStreams returns strings with ".", while system culture might use ",".
                string ratingStr = stremio.ImdbRating.ToString().Replace(",", ".");
                if (double.TryParse(ratingStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0)
                {
                    if (overwritePrimary || unified.Rating == 0)
                        unified.Rating = r;
                }
            }

            if ((stremio.Trailers != null && stremio.Trailers.Any()) || (stremio.TrailerStreams != null && stremio.TrailerStreams.Any()))
            {
                 string? trailerId = null;
                 
                 // 1. Try standard trailers list
                 if (stremio.Trailers != null)
                 {
                     var t = stremio.Trailers.FirstOrDefault(x => (x.Type?.ToLower() == "trailer" || string.IsNullOrEmpty(x.Type)) && !string.IsNullOrWhiteSpace(x.Source));
                     if (t != null) trailerId = t.Source;
                 }

                 // 2. Try AioStreams specific trailerStreams
                 if (string.IsNullOrEmpty(trailerId) && stremio.TrailerStreams != null)
                 {
                     var ts = stremio.TrailerStreams.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.YtId));
                     if (ts != null) trailerId = ts.YtId;
                 }

                 if (!string.IsNullOrEmpty(trailerId))
                 {
                     string source = trailerId;
                     // Normalize YouTube IDs to full URLs
                     if (!source.Contains("/") && !source.Contains("."))
                         source = $"https://www.youtube.com/watch?v={source}";

                     if (overwritePrimary || string.IsNullOrEmpty(unified.TrailerUrl))
                     {
                         unified.TrailerUrl = source;
                     }
                 }
            }
            
            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Mapped data [Primary={overwritePrimary}] for: '{unified.Title}' (Source: {stremio.Name} [{stremio.Id}])");
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

                if (!string.IsNullOrEmpty(tmdb.DisplayOriginalTitle))
                    metadata.OriginalTitle = tmdb.DisplayOriginalTitle;

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

        private void MergeStremioEpisodes(StremioMeta stremio, UnifiedMetadata metadata, bool overwrite = false)
        {
            if (stremio?.Videos == null) return;

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
                    var existingEpisode = season.Episodes.FirstOrDefault(e => e.EpisodeNumber == vid.Episode);
                    if (existingEpisode == null)
                    {
                        season.Episodes.Add(new UnifiedEpisode
                        {
                            Id = vid.Id,
                            SeasonNumber = vid.Season,
                            EpisodeNumber = vid.Episode,
                            Title = !string.IsNullOrEmpty(vid.Name) ? vid.Name : vid.Title,
                            Overview = !string.IsNullOrEmpty(vid.Overview) ? vid.Overview : vid.Description,
                            ThumbnailUrl = vid.Thumbnail,
                            AirDate = !string.IsNullOrEmpty(vid.Released) && DateTime.TryParse(vid.Released, out var d) ? d : null
                        });
                    }
                    else
                    {
                        // Update missing fields OR override if overwrite is true
                        bool isGenericTitle = IsGenericEpisodeTitle(existingEpisode.Title, metadata.Title);
                        bool isGenericOverview = IsGenericEpisodeOverview(existingEpisode.Overview);

                        if (overwrite || isGenericTitle)
                        {
                            string newTitle = !string.IsNullOrEmpty(vid.Name) ? vid.Name : vid.Title;
                            bool isNewGeneric = IsGenericEpisodeTitle(newTitle, metadata.Title);

                            // Prefer real titles over generic ones, or accept whatever if overwrite is true
                            if (overwrite || !isNewGeneric || string.IsNullOrEmpty(existingEpisode.Title))
                                existingEpisode.Title = newTitle;
                        }

                        if (overwrite || isGenericOverview || string.IsNullOrEmpty(existingEpisode.Overview))
                        {
                            string newOverview = !string.IsNullOrEmpty(vid.Overview) ? vid.Overview : vid.Description;
                            if (!string.IsNullOrEmpty(newOverview) && !IsGenericEpisodeOverview(newOverview))
                                existingEpisode.Overview = newOverview;
                            else if (string.IsNullOrEmpty(existingEpisode.Overview))
                                existingEpisode.Overview = newOverview; // accept placeholder if we have nothing better
                        }

                        if (overwrite || string.IsNullOrEmpty(existingEpisode.ThumbnailUrl))
                        {
                            if (!string.IsNullOrEmpty(vid.Thumbnail))
                                existingEpisode.ThumbnailUrl = vid.Thumbnail;
                        }
                            
                        if ((overwrite || existingEpisode.AirDate == null) && !string.IsNullOrEmpty(vid.Released) && DateTime.TryParse(vid.Released, out var d2))
                            existingEpisode.AirDate = d2;
                    }
                }
            }
        }

        private bool IsGenericEpisodeTitle(string title, string seriesTitle = null)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;
            
            string t = title.ToLowerInvariant().Trim();

            if (!string.IsNullOrEmpty(seriesTitle))
            {
                string sTitle = seriesTitle.ToLowerInvariant().Trim();
                if (t.StartsWith(sTitle))
                {
                    t = t.Substring(sTitle.Length).TrimStart('-', ':', ' ', '.');
                }
                else 
                {
                    // Fallback for "ThePitt S02E01" if there are spacing differences
                    string sTitleStripped = new string(sTitle.Where(c => char.IsLetterOrDigit(c)).ToArray());
                    string tStripped = new string(t.Where(c => char.IsLetterOrDigit(c)).ToArray());
                    if (tStripped.StartsWith(sTitleStripped))
                    {
                        t = t.Replace(sTitle, "").Replace(sTitleStripped, "").Trim('-', ':', ' ', '.'); 
                    }
                }
            }
            
            t = t.ToLowerInvariant();
            
            // Just a number check
            if (int.TryParse(t, out _)) return true; // e.g., "1", "02", "12"
            
            // Extract only the letters from the title (e.g., "1. Bölüm" -> "bölüm")
            string lettersOnly = new string(t.Where(c => char.IsLetter(c)).ToArray());
            
            if (string.IsNullOrEmpty(lettersOnly)) return true; // empty like "1.0", "1-2"

            // Check for generic episode/season naming patterns
            return lettersOnly == "bölüm" || 
                   lettersOnly == "episode" || 
                   lettersOnly == "ep" || 
                   lettersOnly == "sezon" || 
                   lettersOnly == "s" || 
                   lettersOnly == "e" ||
                   lettersOnly == "se" ||
                   lettersOnly == "seasonepisode" ||
                   lettersOnly == "season" ||
                   lettersOnly == "sno" ||
                   lettersOnly == "episodeid" ||
                   lettersOnly == "epno";
        }

        private bool IsGenericEpisodeOverview(string? overview)
        {
            if (string.IsNullOrWhiteSpace(overview)) return true;
            string o = overview.ToLowerInvariant();
            
            // Patterns like "Full Title: ... Size: 3GB" or technical filenames
            if (o.Contains("full title:") || o.Contains("size:") || o.Contains("3gb") || o.Contains("1080p") || o.Contains("x264"))
                return true;
            
            // If it's just a repeat of a generic title pattern
            if (o.Length < 30 && IsGenericEpisodeTitle(o, null))
                return true;

            return false;
        }

        private void ReconcileTmdbSeasons(UnifiedMetadata metadata, TmdbMovieResult tmdb)
        {
            // Reconcile with TMDB Seasons (if available) - Adds seasons/episodes Stremio might be missing
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
                }
            }

            // Fallback: If absolutely no seasons found but it's a series, create at least Season 1 if we have an ID
            if (metadata.Seasons.Count == 0 && !string.IsNullOrEmpty(metadata.ImdbId))
            {
                 metadata.Seasons.Add(new UnifiedSeason { SeasonNumber = 1, Name = "1. Sezon" });
            }
            
            // Ensure correct order
            metadata.Seasons = metadata.Seasons.OrderBy(s => s.SeasonNumber).ToList();
        }

        private void AddUniqueBackdrop(UnifiedMetadata metadata, string url, Action<string> onBackdropFound = null)
        {
            if (string.IsNullOrEmpty(url)) return;
            
            string id = ExtractImageId(url);
            if (string.IsNullOrEmpty(id))
            {
                if (!metadata.BackdropUrls.Contains(url)) 
                {
                    metadata.BackdropUrls.Add(url);
                    onBackdropFound?.Invoke(url);
                }
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
                onBackdropFound?.Invoke(url);
            }
            else
            {
                string existingUrl = metadata.BackdropUrls[existingIndex];
                if (GetQualityScore(url) > GetQualityScore(existingUrl))
                {
                    metadata.BackdropUrls[existingIndex] = url;
                    // Note: Quality improvement might not need a re-trigger to the slideshow 
                    // unless we want to replace the current display image. 
                    // For now, only new additions trigger.
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

        private string? ExtractImdbId(Models.Stremio.StremioMeta meta)
        {
            if (IsImdbId(meta.ImdbId)) return meta.ImdbId;
            if (IsImdbId(meta.Id)) return meta.Id;

            // Check website field
            if (!string.IsNullOrEmpty(meta.Website))
            {
                var match = System.Text.RegularExpressions.Regex.Match(meta.Website, @"tt\d+");
                if (match.Success) return match.Value;
            }

            // Check links collection
            if (meta.Links != null)
            {
                foreach (var link in meta.Links)
                {
                    // Some addons put the ID in 'name' when category is 'imdb'
                    if (link.Category == "imdb" && IsImdbId(link.Name)) return link.Name;
                    
                    // Or extract from URL
                    if (!string.IsNullOrEmpty(link.Url))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(link.Url, @"tt\d+");
                        if (match.Success) return match.Value;
                    }
                }
            }

            return null;
        }

        private string? NormalizeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            
            // Extract tt\d+ or tmdb:\d+ from prefixes (like "imdb_id:tt12345" or "torbox:tt12345")
            var imdbMatch = System.Text.RegularExpressions.Regex.Match(id, @"tt\d+");
            if (imdbMatch.Success) return imdbMatch.Value;

            var tmdbMatch = System.Text.RegularExpressions.Regex.Match(id, @"tmdb:\d+");
            if (tmdbMatch.Success) return tmdbMatch.Value;
            
            return id;
        }
    }
}
