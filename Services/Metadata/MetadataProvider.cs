using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services.Stremio;
using ModernIPTVPlayer.Services.Iptv;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Services.Metadata
{
    [Flags]
    internal enum MetadataField
    {
        None = 0,
        Title = 1 << 0,
        Overview = 1 << 1,
        Year = 1 << 2,
        Rating = 1 << 3,
        Genres = 1 << 4,
        Poster = 1 << 5,
        Backdrop = 1 << 6,
        Trailer = 1 << 7,
        Runtime = 1 << 8,
        Logo = 1 << 9,
        Cast = 1 << 10,
        Seasons = 1 << 11,
        OriginalTitle = 1 << 12,
        Gallery = 1 << 13,
        CastPortraits = 1 << 14
    }
    
    public class MetadataProvider
    {
        private sealed class AddonMetaCacheEntry
        {
            public bool HasValue { get; init; }
            public StremioMeta? Meta { get; init; }
        }

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

        private MetadataProvider()
        {
        }

        private readonly ConcurrentDictionary<string, Lazy<Task<UnifiedMetadata>>> _activeTasks = new();
        private readonly ConcurrentDictionary<string, (UnifiedMetadata Data, DateTime Expiry)> _resultCache = new();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(2); // Short term cache
        private readonly ConcurrentDictionary<string, Task<AddonMetaCacheEntry>> _activeAddonMetaTasks = new();
        private readonly ConcurrentDictionary<string, (AddonMetaCacheEntry Data, DateTime Expiry)> _addonMetaCache = new();
        private readonly ConcurrentDictionary<string, string> _rawToCanonicalIdCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _addonMetaPositiveCacheDuration = TimeSpan.FromMinutes(20);
        private readonly TimeSpan _addonMetaNegativeCacheDuration = TimeSpan.FromMinutes(3);

        public void ClearCache()
        {
            _resultCache.Clear();
            _activeTasks.Clear();
            _addonMetaCache.Clear();
            _activeAddonMetaTasks.Clear();
            _rawToCanonicalIdCache.Clear();
        }

        /// <summary>
        /// Synchronously checks if metadata is already in cache.
        /// Useful for preventing UI flicker during loading states.
        /// </summary>
        public UnifiedMetadata? TryPeekMetadata(Models.IMediaStream stream, MetadataContext context = MetadataContext.Detail)
        {
            if (stream == null) return null;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string rawId = stream.IMDbId;
                string? id = ResolveBestInitialId(stream) ?? NormalizeId(rawId) ?? rawId;
                
                // [FIX] Canonical ID restoration matching GetMetadataAsync
                bool isCanonical = IsImdbId(id) || (!string.IsNullOrWhiteSpace(id) && id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase));
                if (!isCanonical && !string.IsNullOrWhiteSpace(rawId) && _rawToCanonicalIdCache.TryGetValue(rawId, out var cachedCanonical))
                {
                    id = cachedCanonical;
                    isCanonical = true;
                }

                // [FIX] KEY ALIGNMENT: Normalize ID and Type logic exactly like GetMetadataAsync
                string normalizedId = NormalizeId(id) ?? id;
                string streamType = (stream as Models.Stremio.StremioMediaStream)?.Meta?.Type;
                string normalizedType = (stream is ModernIPTVPlayer.SeriesStream || string.Equals(streamType, "series", StringComparison.OrdinalIgnoreCase) || string.Equals(streamType, "tv", StringComparison.OrdinalIgnoreCase)) ? "series" : "movie";
                
                string addonHash = GetAddonOrderHash();
                string tmdbLang = AppSettings.TmdbLanguage;
                string cacheKey = $"{normalizedId ?? stream.Title}_{normalizedType}_{addonHash}_{tmdbLang}";
                if (context == MetadataContext.Discovery) cacheKey += "_discovery";

                // 1. Check Primary Cache
                if (_resultCache.TryGetValue(cacheKey, out var cached) && DateTime.Now < cached.Expiry)
                {
                    sw.Stop();
                    // We only log if it was actually a hit (Peeks are common)
                    if (cached.Data != null)
                    {
                        var msg = $"[MetadataProvider] ⚡ TryPeek HIT (Primary) for '{stream.Title}' in {sw.Elapsed.TotalMilliseconds:F1}ms";
                        AppLogger.Info(msg);
                        System.Diagnostics.Debug.WriteLine(msg);
                    }
                    return cached.Data;
                }

                // [FIX] ALIASED LOOKUP
                if (normalizedId != null && normalizedId.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
                {
                    string tmdbIdOnly = normalizedId.Replace("tmdb:", "", StringComparison.OrdinalIgnoreCase);
                    string mappedImdb = IdMappingService.Instance.GetImdbForTmdb(tmdbIdOnly);
                    if (!string.IsNullOrEmpty(mappedImdb))
                    {
                        string fallbackKey = $"{mappedImdb}_{normalizedType}_{addonHash}_{tmdbLang}";
                        if (context == MetadataContext.Discovery) fallbackKey += "_discovery";
                        
                        if (_resultCache.TryGetValue(fallbackKey, out var fallbackCached) && DateTime.Now < fallbackCached.Expiry)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Alias HIT for {id} -> {mappedImdb}");
                            return fallbackCached.Data;
                        }
                    }
                }

                // [FIX] CATALOG SATISFACTION (LEVEL 0): 
                // Peek should also check if the stream itself already satisfies the context requirements!
                var tempMetadata = new UnifiedMetadata();
                if (stream is ModernIPTVPlayer.Models.Stremio.StremioMediaStream sms)
                    SeedFromCatalogMetadata(tempMetadata, sms, new MetadataTrace("PeekReasoning", cacheKey, stream.Title), context, quiet: true);
                else if (stream is SeriesStream series_s)
                {
                    tempMetadata.Title = series_s.Title;
                    tempMetadata.PosterUrl = series_s.PosterUrl;
                    tempMetadata.IsSeries = true;
                    tempMetadata.MetadataId = series_s.SeriesId.ToString();
                }
                else if (stream is LiveStream live_s)
                {
                    tempMetadata.Title = live_s.Title;
                    tempMetadata.PosterUrl = live_s.PosterUrl;
                    tempMetadata.MetadataId = live_s.StreamId.ToString();
                }
                
                bool isContinueWatching = (stream as StremioMediaStream)?.IsContinueWatching ?? false;
                var missing = GetMissingFields(tempMetadata, GetRequiredFields(context, isContinueWatching));

                if (missing == MetadataField.None && context != MetadataContext.Spotlight && context != MetadataContext.Detail)
                {
                    sw.Stop();
                    var msg = $"[MetadataProvider] ⚡ TryPeek HIT (Catalog Satisfied) for '{stream.Title}' in {sw.Elapsed.TotalMilliseconds:F1}ms";
                    AppLogger.Info(msg);
                    System.Diagnostics.Debug.WriteLine(msg);
                    return tempMetadata;
                }
                
                // If not found, log it for deep diagnosis (Debug only)
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Cache Peek MISS for key: {cacheKey}");
                return null;


                // 2. Check Detail-from-Discovery promotion
                if (context == MetadataContext.Detail)
                {
                    string discoveryKey = cacheKey + "_discovery";
                    if (_resultCache.TryGetValue(discoveryKey, out var disc) && DateTime.Now < disc.Expiry)
                    {
                        bool isCw = (stream as StremioMediaStream)?.IsContinueWatching ?? false;
                        if (IsSatisfied(disc.Data, MetadataContext.Detail, isCw))
                        {
                            return disc.Data;
                        }
                    }
                }
            }
            catch
            {
                // Peek should be silent and safe
            }

            return null;
        }

        public async Task<Dictionary<StremioMediaStream, UnifiedMetadata>> EnrichItemsAsync(IEnumerable<StremioMediaStream> items, MetadataContext context = MetadataContext.Discovery, int maxConcurrency = 4)
        {
            var results = new Dictionary<StremioMediaStream, UnifiedMetadata>();
            if (items == null) return results;
            var itemList = items.ToList();
            if (itemList.Count == 0) return results;

            using (var semaphore = new System.Threading.SemaphoreSlim(maxConcurrency))
            {
                var tasks = itemList.Select(async item =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var meta = await GetMetadataAsync(item, context);
                        if (meta != null)
                        {
                            lock (results) results[item] = meta;
                        }
                    }
                    catch (TaskCanceledException ex)
                    {
                        // Task-level cancellation
                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] TaskCanceledException in EnrichItemsAsync | Title: {item.Title} | Context: {context} | Message: {ex.Message} | StackTrace: {ex.StackTrace}");
                    }
                    catch (OperationCanceledException ex)
                    {
                        // Expected cancellation - log with details
                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] OperationCanceledException in EnrichItemsAsync | Title: {item.Title} | Context: {context} | Message: {ex.Message} | StackTrace: {ex.StackTrace}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Exception in EnrichItemsAsync | Title: {item.Title} | Context: {context} | ExceptionType: {ex.GetType().Name} | Message: {ex.Message} | StackTrace: {ex.StackTrace}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }
            return results;
        }

        public async Task<UnifiedMetadata> GetMetadataAsync(Models.IMediaStream stream, MetadataContext context = MetadataContext.Detail, Action<string> onBackdropFound = null)
        {
            if (stream == null) return null;
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string rawId = stream.IMDbId;
            string id = ResolveBestInitialId(stream) ?? NormalizeId(rawId) ?? rawId;
            var trace = new MetadataTrace(context.ToString(), id, stream?.Title);

            if (!string.IsNullOrWhiteSpace(id) && id != rawId)
            {
                AppLogger.Info($"Canonical ID resolved at entry: {rawId} -> {id}");
            }

            // 0. Global Check: composite/non-standard IDs should not abort metadata flow.
            // We fall back to title-based keying so catalog-seed + trace logging still works.
            bool isCanonical = IsImdbId(id) || (!string.IsNullOrWhiteSpace(id) && id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase));
            if (!isCanonical && !string.IsNullOrWhiteSpace(rawId) && _rawToCanonicalIdCache.TryGetValue(rawId, out var cachedCanonical))
            {
                id = cachedCanonical;
                isCanonical = true;
                AppLogger.Info($"Canonical ID restored from raw-cache: {rawId} -> {cachedCanonical}");
            }
            if (!isCanonical && rawId != null && (rawId.Contains(",") || rawId.Contains(" ") || rawId.Length > 100))
            {
                AppLogger.Warn($"Non-standard ID detected, using title fallback. RawId: {rawId}");
                id = null;
            }

            string streamType = (stream as Models.Stremio.StremioMediaStream)?.Meta?.Type;
            string normalizedType = (stream is ModernIPTVPlayer.SeriesStream || string.Equals(streamType, "series", StringComparison.OrdinalIgnoreCase) || string.Equals(streamType, "tv", StringComparison.OrdinalIgnoreCase)) ? "series" : "movie";
            
            string fetchType = normalizedType;
            string normalizedId = NormalizeId(id) ?? id;
            
            string fetchId = id ?? rawId ?? stream.Title;
            
            string addonHash = GetAddonOrderHash();
            string tmdbLang = AppSettings.TmdbLanguage;
            string baseCacheKey = $"{id ?? stream.Title}_{normalizedType}_{addonHash}_{tmdbLang}";
            string cacheKey = baseCacheKey;
            
            if (context == MetadataContext.Discovery)
            {
                // Prefer full version if it exists in cache
                if (_resultCache.ContainsKey(baseCacheKey))
                {
                    cacheKey = baseCacheKey;
                }
                else
                {
                    cacheKey += "_discovery";
                }
            }

            // 1. Check Result Cache
            if (_resultCache.TryGetValue(cacheKey, out var cached) && DateTime.Now < cached.Expiry)
            {
                // [FIX] Context-Aware Cache Validation
                // Even if we have a cache hit, we must ensure it satisfies the current context's requirements.
                // For example, a result cached by 'Spotlight' context might not have 'Seasons' data needed by 'Detail' context.
                bool isCw = (stream as StremioMediaStream)?.IsContinueWatching ?? false;
                MetadataField required = GetRequiredFields(context, isCw);
                MetadataField cacheMissing = GetMissingFields(cached.Data, required);
                trace.Log("Cache", $"Required={required} Missing={cacheMissing}");

                if (IsSatisfied(cached.Data, context, isCw))
                {
                    sw.Stop();
                    var msg = $"[MetadataProvider] ⚡ Cache HIT (Primary) for '{stream.Title}' [{id ?? "NoId"}] in {sw.Elapsed.TotalMilliseconds:F1}ms";
                    AppLogger.Info(msg);
                    System.Diagnostics.Debug.WriteLine(msg);
                    trace.Log("Cache", $"HIT (Satisfied): {cacheKey}");

                    // [FIX] CROSS-CATALOG TITLE SEEDING:
                    // Even on cache hit, if we entered from a different catalog, we should seed its title!
                    if (stream is StremioMediaStream stremioStream)
                    {
                        lock (cached.Data.SyncRoot)
                        {
                            SeedFromCatalogMetadata(cached.Data, stremioStream, trace, context: MetadataContext.Detail);
                        }
                    }

                    if (onBackdropFound != null && cached.Data.BackdropUrls != null)
                    {
                        foreach (var bg in cached.Data.BackdropUrls) onBackdropFound(bg);
                    }
                    return cached.Data;
                }
                else
                {
                    isCw = (stream as StremioMediaStream)?.IsContinueWatching ?? false;
                    var missingFromCache = GetMissingFields(cached.Data, GetRequiredFields(context, isCw));
                    trace.Log("Cache", "HIT but NOT SATISFIED. Passing as seed.");
                    
                    // We have cached data but it's not enough for the current context.
                    // We continue to GetMetadataInternalAsync, passing the cached data as 'seed' to avoid redundant addon probes.
                    return await _activeTasks.GetOrAdd(cacheKey, _ => new Lazy<Task<UnifiedMetadata>>(() => 
                    {
                        AppLogger.Info($"START Fetching (Context Upgrade {context}): {id ?? stream.Title} | Missing: {missingFromCache}");
                        return GetMetadataInternalAsync(fetchId, fetchType, stream, cacheKey, context, onBackdropFound, cached.Data);
                    })).Value;
                }
            }

            // [NEW] If asking for Detail, but have an ExpandedCard result in cache, we need to re-fetch!
            // ExpandedCard doesn't merge episodes (for performance), so we need Detail context for full data.
            if (context == MetadataContext.Detail)
            {
                // Check if there's an ExpandedCard cached version - if so, we need to re-fetch
                // The cached version won't have episodes merged
                var cachedData = _resultCache.FirstOrDefault(kvp => 
                    kvp.Key.Contains(id ?? stream?.Title) && 
                    !kvp.Key.EndsWith("_discovery") &&
                    kvp.Value.Expiry > DateTime.Now).Value;
                
                if (cachedData.Data != null && (cachedData.Data.Seasons == null || cachedData.Data.Seasons.All(s => s.Episodes == null || s.Episodes.Count == 0)))
                {
                    // We have cached data but no episodes - likely from ExpandedCard context
                    // Force re-fetch by NOT using cache
                    System.Diagnostics.Debug.WriteLine($"[MetadataProvider] ExpandedCard cache detected without episodes, re-fetching for Detail context");
                }
                else
                {
                    // Also check for Discovery cache promotion (existing logic)
                    string discoveryKey = cacheKey + "_discovery";
                    if (_resultCache.TryGetValue(discoveryKey, out var disc) && DateTime.Now < disc.Expiry)
                    {
                        bool isCw = (stream as StremioMediaStream)?.IsContinueWatching ?? false;
                        if (IsSatisfied(disc.Data, MetadataContext.Detail, isCw))
                        {
                            sw.Stop();
                            var msg = $"[MetadataProvider] ⚡ Cache HIT (Promoted) for '{stream.Title}' [{id ?? "NoId"}] in {sw.Elapsed.TotalMilliseconds:F1}ms";
                            AppLogger.Info(msg);
                            System.Diagnostics.Debug.WriteLine(msg);
                            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] PROMOTED Discovery cache to Detail: {cacheKey}");
                            return disc.Data;
                        }
                        var seedMissing = GetMissingFields(disc.Data, GetRequiredFields(MetadataContext.Detail, false));
                        string seedReason = seedMissing == MetadataField.None ? "None" : seedMissing.ToString();
                        AppLogger.Info($"[MetadataProvider] SEEDING Detail fetch ({id ?? stream?.Title}) | Reason: {seedReason}");
                        return await _activeTasks.GetOrAdd(cacheKey, _ => new Lazy<Task<UnifiedMetadata>>(() => GetMetadataInternalAsync(fetchId, fetchType, stream, cacheKey, context, onBackdropFound, disc.Data))).Value;
                    }
                }
            }

            // 2. Check Active Tasks (Deduplication) - Use Lazy to ensure atomic task creation
            
            // Refine fetch reason by seeing what we already have from the stream/catalog data
            var tempMetadata = new UnifiedMetadata();
            if (stream is ModernIPTVPlayer.Models.Stremio.StremioMediaStream sms)
                SeedFromCatalogMetadata(tempMetadata, sms, new MetadataTrace("Reasoning", cacheKey, stream.Title), context, quiet: true);
            bool isContinueWatching = (stream as StremioMediaStream)?.IsContinueWatching ?? false;
            var missing = GetMissingFields(tempMetadata, GetRequiredFields(context, isContinueWatching));
            
            // [NEW] Early exit if catalog/discovery data already satisfies current context requirements
            // [FIX] NEVER early exit for Spotlight or Detail if we want TMDB enrichment, as catalog-seed is only a starting point.
            if (missing == MetadataField.None && context != MetadataContext.Spotlight && context != MetadataContext.Detail)
            {
                sw.Stop();
                var msg = $"[MetadataProvider] ⚡ Instant HIT (Satisfied by Catalog) for '{stream.Title}' in {sw.Elapsed.TotalMilliseconds:F1}ms";
                AppLogger.Info(msg);
                System.Diagnostics.Debug.WriteLine(msg);
                
                // Result found and satisfied without background task
                var expiry = DateTime.Now.Add(_cacheDuration);
                _resultCache[cacheKey] = (tempMetadata, expiry);
                return tempMetadata;
            }

            var fetchReason = missing.ToString();
            return await _activeTasks.GetOrAdd(cacheKey, _ => new Lazy<Task<UnifiedMetadata>>(() => 
            {
                var msg = $"[MetadataProvider] 🔍 Cache MISS. START Fetching ({context}): {id ?? stream.Title} | Type: {fetchType} | Reason: {fetchReason}";
                AppLogger.Info(msg);
                System.Diagnostics.Debug.WriteLine(msg);
                return GetMetadataInternalAsync(fetchId, fetchType, stream, cacheKey, context, onBackdropFound);
            })).Value;
        }

        private string? ResolveBestInitialId(Models.IMediaStream stream)
        {
            string? baseId = NormalizeId(stream.IMDbId) ?? stream.IMDbId;
            
            // If already IMDb, nothing else to do
            if (IsImdbId(baseId)) return baseId;

            if (stream is not StremioMediaStream sms || sms.Meta == null)
                return baseId;

            var meta = sms.Meta;

            // Prefer IMDb ID from meta even if baseId is tmdb or other
            string? extracted = ExtractImdbId(meta);
            if (IsImdbId(extracted)) return extracted;

            string? normalizedMetaImdb = NormalizeId(meta.ImdbId);
            if (IsImdbId(normalizedMetaImdb)) return normalizedMetaImdb;

            string? normalizedMetaId = NormalizeId(meta.Id);
            if (IsImdbId(normalizedMetaId)) return normalizedMetaId;

            // Fallback to TMDB if no IMDb found
            if (IsImdbId(baseId) || (!string.IsNullOrWhiteSpace(baseId) && baseId.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase)))
                return baseId;

            if (meta.MovieDbId != null && int.TryParse(meta.MovieDbId.ToString(), out int tmdbId) && tmdbId > 0)
                return $"tmdb:{tmdbId}";

            return baseId;
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

                    string? rawSourceId = sourceStream?.IMDbId;
                    if (!string.IsNullOrWhiteSpace(rawSourceId) && !IsCanonicalId(rawSourceId) && IsCanonicalId(result.ImdbId))
                    {
                        _rawToCanonicalIdCache[rawSourceId] = result.ImdbId;
                        
                        // [NEW] Also register in persistent IdMappingService for external lookups (like subtitle fetching)
                        if (rawSourceId.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
                        {
                            var rParts = rawSourceId.Split(':');
                            if (rParts.Length > 1)
                            {
                                string tmdbIdOnly = rParts[1];
                                IdMappingService.Instance.RegisterMapping(result.ImdbId, tmdbIdOnly);
                            }
                        }
                    }
                    
                    // If the ID changed (e.g. tmdb -> imdb), cache under the new ID as well
                    if (!string.IsNullOrEmpty(result.ImdbId) && result.ImdbId != normalizedId)
                    {
                        string newKey = $"{result.ImdbId}_{type}";
                        if (context == MetadataContext.Discovery) newKey += "_discovery";
                        _resultCache[newKey] = (result, expiry);
                        AppLogger.Info($"[MetadataProvider] Multi-cached: {cacheKey} AND {newKey}");
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
            var trace = new MetadataTrace(context.ToString(), id, sourceStream?.Title);
            bool isSeriesType = type == "series" || type == "tv";
            bool isContinueWatching = (sourceStream as StremioMediaStream)?.IsContinueWatching ?? false;
            var metadata = seed ?? new UnifiedMetadata
            {
                ImdbId = id,
                IsSeries = isSeriesType,
                MetadataId = id,
                Title = sourceStream != null && !string.IsNullOrEmpty(sourceStream.Title) && sourceStream.Title != "Loading..." ? sourceStream.Title : (id ?? "Unknown")
            };

            // [FIX] Ensure IsSeries is correctly synced if we are using a seed (cached result)
            if (seed != null) metadata.IsSeries = isSeriesType;

            // [NEW] Early ID Resolution: If this is a numeric IPTV ID, check if we already have an IMDb mapping.
            // This allows the fetch to start with a canonical ID even if the user clicked an IPTV result.
            if (!string.IsNullOrEmpty(metadata.MetadataId) && !IsCanonicalId(metadata.MetadataId) && char.IsDigit(metadata.MetadataId[0]))
            {
                var match = IptvMatchService.Instance.FindAllMatchesById(metadata.MetadataId, true).FirstOrDefault();
                if (match != null && !string.IsNullOrEmpty(match.IMDbId) && IsImdbId(match.IMDbId))
                {
                    trace.Log("ID", $"Early Resolution: IPTV {metadata.MetadataId} -> IMDb {match.IMDbId}");
                    metadata.ImdbId = match.IMDbId;
                }
            }

            try
            {
                if (sourceStream is StremioMediaStream stremioStream)
                {
                    SeedFromCatalogMetadata(metadata, stremioStream, trace, context: context);
                    
                    // [PROGRESSIVE] Push initial catalog data (Logo/Backdrop) to UI immediately
                    stremioStream.UpdateFromUnified(metadata);
                }

                // Global Catalog Seeding (from current cache)
                var initialFragments = _stremioService.GetGlobalMetaCache(id);
                if (initialFragments != null)
                {
                    var seededKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (sourceStream is StremioMediaStream sms) seededKeys.Add($"{sms.SourceAddon}_{sms.Meta.Id}");

                    foreach (var fragment in initialFragments)
                    {
                        string key = $"{fragment.SourceAddon}_{fragment.Meta.Id}";
                        if (seededKeys.Contains(key)) continue;
                        SeedFromCatalogMetadata(metadata, fragment, trace, quiet: true, context: context);
                        if (metadata.IsSeries && fragment.Meta?.Videos != null && context != MetadataContext.Discovery && context != MetadataContext.ExpandedCard)
                        {
                            MergeStremioEpisodes(fragment.Meta, metadata, GetHostSafe(fragment.SourceAddon), false);
                        }
                        seededKeys.Add(key);
                    }
                }

            bool isCw = (sourceStream as StremioMediaStream)?.IsContinueWatching ?? false;
                MetadataField required = GetRequiredFields(context, isCw);
                MetadataField missing = GetMissingFields(metadata, required);
                trace.Log("Seed", $"Required={required} Missing={missing}");

                // [NEW] TMDB THROTTLING PROTECTION
                // Only enrich with TMDB if context is Detail, ExpandedCard, or Spotlight.
                // Discovery (main grid) is excluded to prevent throttling/bans.
                bool tmdbAllowed = context == MetadataContext.Detail || context == MetadataContext.ExpandedCard || context == MetadataContext.Spotlight;
                bool tmdbEnabled = tmdbAllowed && AppSettings.IsTmdbEnabled && !string.IsNullOrWhiteSpace(AppSettings.TmdbApiKey);
                
                if (context == MetadataContext.Spotlight)
                {
                    trace.Log("Spotlight-Check", $"TMDB Allowed={tmdbAllowed}, Enabled={tmdbEnabled}, Missing={missing}");
                }
                
                if (tmdbEnabled)
                {
                    trace.Log("TMDB", $"TMDB is enabled. CW={isContinueWatching}. Trying direct enrichment...");
                    var tmdb = await EnrichWithTmdbAsync(metadata, context, isContinueWatching);
                    if (tmdb != null)
                    {
                        metadata.TmdbInfo = tmdb;
                        metadata.MetadataSourceInfo = "TMDB (Primary)";
                        metadata.DataSource = string.IsNullOrEmpty(metadata.DataSource) ? "TMDB" : $"{metadata.DataSource} + TMDB";

                        // Set High Priority for TMDB data
                        metadata.PriorityScore = MetadataPriority.CalculateScore(
                            MetadataPriority.AUTHORITY_TMDB, 
                            context == MetadataContext.Detail ? MetadataPriority.DEPTH_DETAIL : 
                            (context == MetadataContext.Spotlight ? MetadataPriority.DEPTH_SPOTLIGHT : MetadataPriority.DEPTH_CATALOG)
                        );

                        var additionalBackdrops = metadata.IsSeries
                            ? await TmdbHelper.GetTvImagesAsync(tmdb.Id.ToString())
                            : await TmdbHelper.GetMovieImagesAsync(tmdb.Id.ToString());

                        foreach (var bg in additionalBackdrops)
                        {
                            AddUniqueBackdrop(metadata, bg, onBackdropFound);
                        }

                        missing = GetMissingFields(metadata, required);
                        trace.Log("TMDB", $"TMDB enriched. RemainingMissing={missing}");

                        // [PROGRESSIVE] Push TMDB data to UI immediately
                        if (sourceStream is StremioMediaStream sms) sms.UpdateFromUnified(metadata);
                    }
                    else
                    {
                        trace.Log("TMDB", "TMDB enrichment failed. Falling back to addon chain.");
                    }
                }
                else
                {
                    if (!tmdbAllowed && AppSettings.IsTmdbEnabled)
                        trace.Log("TMDB", "TMDB skipped for Discovery context (Throttling Protection).");
                    else
                        trace.Log("TMDB", "TMDB disabled or API key missing.");
                }

                if (tmdbEnabled)
                {
                    bool satisfiesRequirements = GetMissingFields(metadata, required) == MetadataField.None;
                    if (metadata.TmdbInfo != null && satisfiesRequirements)
                    {
                        trace.Log("Decision", "TMDB present and satisfies requirements. Skipping addon detail probes.");
                    }
                    else if (metadata.TmdbInfo != null)
                    {
                        trace.Log("Decision", "TMDB present but requirements not met (e.g. Logo/Rating missing). Probing addons for gaps.");
                        // Proceed to addon probes (line 482 onwards)
                    }
                    else
                    {
                        trace.Log("Decision", "TMDB enabled but result unavailable. Addon detail probes are disabled by policy for High-Vis scenes.");
                        // [FIX] Actually, if TMDB failed, we SHOULD allow addon probes as fallback
                        // BUT only if we don't have enough data yet.
                    }
                }

                if (!tmdbEnabled || (metadata.TmdbInfo == null && GetMissingFields(metadata, required) != MetadataField.None) || (metadata.TmdbInfo != null && GetMissingFields(metadata, required) != MetadataField.None))
                {
                    var addonUrls = StremioAddonManager.Instance.GetAddonsByResource("meta");
                    if (addonUrls.Count > 0)
                    {
                        int sourcePriority = GetAddonPriorityIndex(addonUrls, metadata.CatalogSourceAddonUrl);
                        int existingPrimaryPriority = GetAddonPriorityIndex(addonUrls, metadata.PrimaryMetadataAddonUrl);

                        // [FIX] Initial priority should be the BEST (lowest index) priority we already have. 
                        // This prevents lower-priority catalog sources (like Cinemeta) from demoting higher-priority data (like AioStreams)
                        // that was already discovered and seeded into this fetch.
                        int primaryPriority = Math.Min(
                            sourcePriority >= 0 ? sourcePriority : int.MaxValue,
                            existingPrimaryPriority >= 0 ? existingPrimaryPriority : int.MaxValue
                        );

                        // [NEW] If TMDB has already provided primary metadata, treat it as the highest priority source (-1)
                        // to prevent addons from overwriting better (often localized) TMDB data.
                        if (metadata.TmdbInfo != null)
                        {
                            primaryPriority = -1;
                        }

                        string rawCatalogId = (sourceStream as StremioMediaStream)?.Meta?.Id ?? sourceStream?.IMDbId ?? id;
                        string currentSearchId = NormalizeId(metadata.ImdbId) ?? NormalizeId(id) ?? id;
                        if (!IsCanonicalId(currentSearchId) && !string.IsNullOrWhiteSpace(rawCatalogId) &&
                            !string.Equals(currentSearchId, rawCatalogId, StringComparison.Ordinal))
                        {
                            currentSearchId = rawCatalogId;
                            trace.Log("ID", $"Canonical ID yok. Raw catalog ID ile probe denenecek: {currentSearchId}");
                        }

                        trace.Log("Addon", $"Order={string.Join(" > ", addonUrls.Select((u, i) => $"{i}:{GetHostSafe(u)}"))} CatalogPriority={sourcePriority}");

                        bool skipCrossAddonProbes = !IsCanonicalId(currentSearchId) && LooksLikeTitleFallback(currentSearchId, metadata.Title);
                        if (skipCrossAddonProbes)
                        {
                            trace.Log("Decision", "Search ID title-fallback durumda. Yanlis eslesme/404 spam riskine karsi addon probe atlandi.");
                        }
                        else
                        {
                            var indexedAddons = addonUrls.Select((u, i) => (Url: u, Index: i)).ToList();
                            IEnumerable<(string Url, int Index)> probeOrder = indexedAddons;
                            bool sourceFirstNeeded = !IsCanonicalId(currentSearchId) && sourcePriority >= 0;
                            if (sourceFirstNeeded)
                            {
                                var src = indexedAddons.FirstOrDefault(x => x.Index == sourcePriority);
                                if (!string.IsNullOrWhiteSpace(src.Url))
                                {
                                    bool isTbmNamespace = currentSearchId.StartsWith("tbm:", StringComparison.OrdinalIgnoreCase);
                                    var remaining = indexedAddons.Where(x => x.Index != sourcePriority).ToList();
                                    if (isTbmNamespace)
                                    {
                                        var torboxResolvers = remaining.Where(x => GetHostSafe(x.Url).Contains("torbox", StringComparison.OrdinalIgnoreCase)).ToList();
                                        var nonResolvers = remaining.Where(x => !GetHostSafe(x.Url).Contains("torbox", StringComparison.OrdinalIgnoreCase)).ToList();
                                        probeOrder = new[] { src }.Concat(torboxResolvers).Concat(nonResolvers);
                                    }
                                    else
                                    {
                                        probeOrder = new[] { src }.Concat(remaining);
                                    }
                                    trace.Log("Decision", $"Non-canonical akista source-first probe uygulanacak: {GetHostSafe(src.Url)}");
                                }
                            }

                            foreach (var pair in probeOrder)
                            {
                                string url = pair.Url;
                                int index = pair.Index;

                                missing = GetMissingFields(metadata, required);
                                if (missing == MetadataField.None)
                                {
                                    // Even if all required fields are present, if episode titles are generic,
                                    // we MUST continue probing lower-priority addons (e.g. Cinemeta) to get real titles.
                                    bool hasGenericEpTitles = metadata.IsSeries && HasGenericEpisodeTitles(metadata);
                                    
                                    // Even if satisfied, we MUST probe higher priority addons
                                    // to ensure that the preferred source (e.g. AioStreams) logic is applied.
                                    if (index < primaryPriority)
                                    {
                                        trace.Log("Addon", $"Satisfied but {GetHostSafe(url)} (Prio:{index}) has higher priority than current (Prio:{primaryPriority}). Probing for better data.");
                                    }
                                    else if (hasGenericEpTitles)
                                    {
                                        trace.Log("Addon", $"Fields satisfied but episode titles are generic. Continuing probe to {GetHostSafe(url)} for better episode data.");
                                    }
                                    else
                                    {
                                        trace.Log("Addon", "Missing field kalmadi ve tum oncelikli eklentiler tarandi. Probe zinciri tamamlandi.");
                                        break;
                                    }
                                }

                                var addonMeta = await _stremioService.GetMetaAsync(url, type, currentSearchId);
                                if (addonMeta != null)
                                {
                                    int addonPriority = MetadataPriority.CalculateScore(MetadataPriority.AUTHORITY_STREMIO_ADDON, MetadataPriority.DEPTH_DETAIL, index);
                                    
                                    // Only update base metadata if this addon has higher or equal priority than current source
                                    bool canOverwrite = addonPriority >= metadata.PriorityScore;
                                    
                                    trace.Log("Addon", $"Fetch success from {GetHostSafe(url)} (Score:{addonPriority}). CanOverwrite={canOverwrite}");

                                    if (canOverwrite)
                                    {
                                        MapStremioToUnified(addonMeta, metadata, true);
                                        metadata.PrimaryMetadataAddonUrl = url;
                                        metadata.MetadataSourceInfo = $"{GetHostSafe(url)} (Full Details)";
                                        metadata.PriorityScore = addonPriority;
                                    }
                                    else
                                    {
                                        // Backfill only
                                        MapStremioToUnified(addonMeta, metadata, false);
                                    }
                                }
                                
                                // [NEW] SMART SEQUENTIAL PROBING (Discovery only)
                                // If the CURRENT context requirements are met (even if not from a higher priority source), 
                                // we can stop probing to save network requests, UNLESS we still want to hunt for better priority data.
                                // For Discovery context, we stop immediately once basics are met.
                                if (context == MetadataContext.Discovery && missing == MetadataField.None)
                                {
                                    trace.Log("Decision", $"Context '{context}' requirements fully satisfied. Stopping probe loop.");
                                    break;
                                }

                                if (metadata.ProbedAddons.Contains(url))
                                {
                                    trace.Log("Addon", $"Skip already probed addon: {GetHostSafe(url)}");
                                    continue;
                                }

                                // Non-canonical ID ile sadece katalog kaynagindan probe yap.
                                if (!IsCanonicalId(currentSearchId))
                                {
                                    bool isTbmNamespace = currentSearchId.StartsWith("tbm:", StringComparison.OrdinalIgnoreCase);
                                    bool isTorboxResolver = GetHostSafe(url).Contains("torbox", StringComparison.OrdinalIgnoreCase);
                                    
                                    // [FIX] Never probe Cinemeta with a non-canonical ID (like IPTV name/id) to avoid 404 spam.
                                    if (GetHostSafe(url).Contains("cinemeta", StringComparison.OrdinalIgnoreCase))
                                    {
                                        trace.Log("Addon", $"Skip {GetHostSafe(url)}: Non-canonical ID ({currentSearchId}) icin Cinemeta probe edilmez.");
                                        continue;
                                    }

                                    if (sourcePriority >= 0)
                                    {
                                        bool isCatalogSource = index == sourcePriority;
                                        if (!isCatalogSource && !(isTbmNamespace && isTorboxResolver))
                                        {
                                            trace.Log("Addon", $"Skip {GetHostSafe(url)}: Non-canonical ID ({currentSearchId}) icin sadece catalog source (ve tbm ise torbox resolver) probe edilir.");
                                            continue;
                                        }
                                    }
                                    else if (index > 0)
                                    {
                                        if (!(isTbmNamespace && isTorboxResolver))
                                        {
                                            trace.Log("Addon", $"Skip {GetHostSafe(url)}: Non-canonical ID ({currentSearchId}) ve source belirsiz. Sadece en yuksek oncelik (tbm ise torbox resolver) probe edilir.");
                                            continue;
                                        }
                                    }
                                }

                                if (currentSearchId != null && (currentSearchId.StartsWith("error", StringComparison.OrdinalIgnoreCase) || currentSearchId.Contains("aiostreamserror")))
                                {
                                    trace.Log("Addon", $"Invalid ID for probing: {currentSearchId}");
                                    break;
                                }

                                trace.Log("Addon", $"Probe {GetHostSafe(url)} Priority={index} ID={currentSearchId} Missing={missing}");
                                var entry = await GetAddonMetaCachedAsync(url, type, currentSearchId, trace);
                                metadata.ProbedAddons.Add(url);

                                if (!entry.HasValue || entry.Meta == null || !IsValidMetadata(entry.Meta))
                                {
                                    trace.Log("Addon", $"No usable meta from {GetHostSafe(url)} (HasValue={entry.HasValue}, IsNull={entry.Meta == null})");
                                    continue;
                                }

                                string? discoveredImdbId = ExtractImdbId(entry.Meta);
                                if (!IsImdbId(currentSearchId) && IsImdbId(discoveredImdbId))
                                {
                                    currentSearchId = discoveredImdbId!;
                                    metadata.ImdbId = discoveredImdbId;
                                    trace.Log("ID", $"Upgraded search id to canonical IMDb: {discoveredImdbId}. Allowing full addon loop.");
                                }

                                 bool overwritePrimary = index <= primaryPriority;

                                 var missingBefore = GetMissingFields(metadata, GetRequiredFields(context, isContinueWatching));
                                 
                                 MapStremioToUnified(entry.Meta, metadata, overwritePrimary);

                                 if (overwritePrimary)
                                 {
                                     primaryPriority = index;
                                     metadata.PrimaryMetadataAddonUrl = url;
                                     metadata.MetadataSourceInfo = $"{GetHostSafe(url)} (Primary)";
                                     trace.Log("Priority", $"Primary source switched to {GetHostSafe(url)}");
                                 }
                                 else
                                 {
                                     trace.Log("Priority", $"{GetHostSafe(url)} used as gap filler.");
                                 }

                                 if (!string.IsNullOrEmpty(entry.Meta.Background) && metadata.BackdropUrls.Count <= 20)
                                 {
                                     AddUniqueBackdrop(metadata, entry.Meta.Background, onBackdropFound);
                                 }

                                 if (metadata.IsSeries && context != MetadataContext.Discovery)
                                 {
                                     int epChanges = MergeStremioEpisodes(entry.Meta, metadata, GetHostSafe(url), overwritePrimary);
                                     if (epChanges > 0)
                                     {
                                         // Credit the addon for episode contributions even if the "Seasons" bit isn't fully cleared
                                         trace.Log("Merge", $"Addon {GetHostSafe(url)} contributed {epChanges} episode updates.");
                                         string addonName = GetHostSafe(url);
                                         string priorityTag = overwritePrimary ? "Primary" : "Gaps";
                                         string epTag = $"(Episodes: {epChanges}) [{priorityTag}]";
                                         
                                         if (string.IsNullOrEmpty(metadata.DataSource))
                                             metadata.DataSource = $"{addonName} {epTag}";
                                         else if (!metadata.DataSource.Contains(addonName))
                                             metadata.DataSource += $" + {addonName} {epTag}";
                                         else if (!metadata.DataSource.Contains("Episodes:"))
                                             metadata.DataSource = metadata.DataSource.Replace(addonName, $"{addonName} {epTag}");
                                     }

                                     // [PROGRESSIVE] Push data to UI as soon as it's from a primary or equivalent source
                                 }

                                 // [PROGRESSIVE] Push data to UI as soon as it's from a primary or equivalent source
                                 if (overwritePrimary && sourceStream is StremioMediaStream sms)
                                 {
                                     sms.UpdateFromUnified(metadata);
                                 }

                                 var missingAfter = GetMissingFields(metadata, GetRequiredFields(context, isContinueWatching));

                                 // Track field-level contributions (Title, Overview, etc.)
                                 var contributedFields = missingBefore & ~missingAfter;
                                 if (contributedFields != MetadataField.None)
                                 {
                                     string addonName = GetHostSafe(url);
                                     string fieldStr = contributedFields.ToString();
                                     string priorityTag = overwritePrimary ? "Primary" : "Gaps";
                                     
                                     if (string.IsNullOrEmpty(metadata.DataSource))
                                         metadata.DataSource = $"{addonName} ({fieldStr}) [{priorityTag}]";
                                     else if (!metadata.DataSource.Contains(addonName))
                                         metadata.DataSource += $" + {addonName} ({fieldStr}) [{priorityTag}]";
                                     else if (!metadata.DataSource.Contains($"({fieldStr})"))
                                         metadata.DataSource = metadata.DataSource.Replace(addonName, $"{addonName} ({fieldStr})");
                                 }

                                 // [BREAK LOGIC] If everything satisfied, stop early
                                 if (missingAfter == MetadataField.None)
                                 {
                                     trace.Log("Addon", "All required fields satisfied. Breaking probe chain.");
                                     break;
                                 }

                                 // [OPTIMIZATION] Best-effort break for series (One Piece fix)
                                 if (metadata.IsSeries && missingAfter == MetadataField.Seasons && index >= 1)
                                 {
                                     trace.Log("Addon", "Found best-effort episode titles. Breaking probe chain to save time.");
                                     break;
                                 }
                            }
                        }
                    }
                }

                if (metadata.IsSeries && metadata.TmdbInfo != null)
                {
                    ReconcileTmdbSeasons(metadata, metadata.TmdbInfo);
                }

                if (App.CurrentLogin != null && context != MetadataContext.Discovery && context != MetadataContext.Spotlight)
                {
                    if (metadata.IsSeries)
                    {
                        var seriesStream = sourceStream as SeriesStream;
                        await EnrichWithIptvAsync(metadata, seriesStream ?? new SeriesStream { Name = metadata.Title }, trace);
                    }
                    else
                    {
                        var movieStream = sourceStream as VodStream;
                        // If it's another IPTV type (e.g. LiveStream), we still want to pass its Name for matching if ID is not known
                        await EnrichWithIptvMovieAsync(metadata, movieStream ?? new VodStream { Name = metadata.Title, StreamId = (sourceStream?.Id ?? 0) }, trace);
                    }
                }
            }
            catch (Exception ex)
            {
                trace.Log("Error", ex.Message);
                AppLogger.Critical("CRITICAL ERROR in GetMetadataAsync", ex);
            }

            // [FIX] Cross-cache by IMDb ID to prevent duplicate fetches for the same content
            if (!string.IsNullOrEmpty(metadata.ImdbId) && IsImdbId(metadata.ImdbId))
            {
                _resultCache[metadata.ImdbId] = (metadata, DateTime.Now.Add(_cacheDuration));
            }

            if (metadata.IsSeries)
            {
                SortSeasons(metadata);
            }

            var finalMissing = GetMissingFields(metadata, GetRequiredFields(context, isContinueWatching));
            trace.Log("Finish", $"DataSource={metadata.DataSource} Source={metadata.MetadataSourceInfo} RemainingMissing={finalMissing}");
            
            // [FIX] Update the highest enrichment level attained.
            // This prevents re-fetching items that are "unresolvably missing" certain fields (e.g. 2026 movies missing Rating).
            if (metadata.MaxEnrichmentContext < context)
            {
                metadata.MaxEnrichmentContext = context;
            }
            
            return metadata;
        }

        private MetadataField GetRequiredFields(MetadataContext context, bool isContinueWatching = false)
        {
            if (context == MetadataContext.Discovery)
            {
                return MetadataField.Title | MetadataField.Overview | MetadataField.Year | MetadataField.Rating | MetadataField.Backdrop;
            }

            if (context == MetadataContext.Spotlight)
            {
                return MetadataField.Title | MetadataField.Overview | MetadataField.Year | MetadataField.Rating | MetadataField.Backdrop | MetadataField.Logo | MetadataField.Trailer | MetadataField.Genres;
            }

            if (context == MetadataContext.ExpandedCard)
            {
                var req = MetadataField.Title | MetadataField.Overview | MetadataField.Year | MetadataField.Rating | MetadataField.Trailer | MetadataField.Genres | MetadataField.CastPortraits | MetadataField.Backdrop;
                if (isContinueWatching) req |= MetadataField.Seasons;
                return req;
            }

            // Detail context requirements.
            return MetadataField.Title | MetadataField.Overview | MetadataField.Year | MetadataField.Rating | MetadataField.Backdrop | 
                   MetadataField.Trailer | MetadataField.Seasons | MetadataField.Logo | MetadataField.Gallery | MetadataField.CastPortraits;
        }

        private MetadataField GetMissingFields(UnifiedMetadata metadata, MetadataField required)
        {
            MetadataField missing = MetadataField.None;
            if (required.HasFlag(MetadataField.Title) && string.IsNullOrWhiteSpace(metadata.Title)) missing |= MetadataField.Title;
            
            bool hasRealOverview = !string.IsNullOrWhiteSpace(metadata.Overview) && !IsPlaceholderOverview(metadata.Overview);
            if (required.HasFlag(MetadataField.Overview) && !hasRealOverview) missing |= MetadataField.Overview;
            
            if (required.HasFlag(MetadataField.Year) && string.IsNullOrWhiteSpace(metadata.Year)) missing |= MetadataField.Year;
            
            if (required.HasFlag(MetadataField.Rating) && metadata.Rating <= 0)
            {
                // [FIX] AioStreams exemption: If we have AioStreams data, we don't strictly require a rating to be satisfied
                bool isAio = metadata.DataSource?.Contains("AioStreams", StringComparison.OrdinalIgnoreCase) == true;
                if (!isAio) missing |= MetadataField.Rating;
            }

            if (required.HasFlag(MetadataField.Genres) && string.IsNullOrWhiteSpace(metadata.Genres)) missing |= MetadataField.Genres;
            if (required.HasFlag(MetadataField.Poster) && string.IsNullOrWhiteSpace(metadata.PosterUrl)) missing |= MetadataField.Poster;
            if (required.HasFlag(MetadataField.Backdrop) && string.IsNullOrWhiteSpace(metadata.BackdropUrl)) missing |= MetadataField.Backdrop;
            if (required.HasFlag(MetadataField.Trailer) && string.IsNullOrWhiteSpace(metadata.TrailerUrl)) missing |= MetadataField.Trailer;
            if (required.HasFlag(MetadataField.Runtime) && string.IsNullOrWhiteSpace(metadata.Runtime)) missing |= MetadataField.Runtime;
            if (required.HasFlag(MetadataField.Logo) && string.IsNullOrWhiteSpace(metadata.LogoUrl)) missing |= MetadataField.Logo;
            
            if (required.HasFlag(MetadataField.Gallery))
            {
                if (metadata.BackdropUrls == null || metadata.BackdropUrls.Count < 2) missing |= MetadataField.Gallery;
            }

            if (required.HasFlag(MetadataField.CastPortraits))
            {
                if (metadata.Cast != null && metadata.Cast.Count > 0 && !metadata.Cast.Any(c => !string.IsNullOrEmpty(c.ProfileUrl)))
                    missing |= MetadataField.CastPortraits;
            }

            // [NEW] Check for generic episode titles in series
            if (metadata.IsSeries && required.HasFlag(MetadataField.Seasons))
            {
                bool hasEpisodes = metadata.Seasons != null && metadata.Seasons.Any(s => s.Episodes != null && s.Episodes.Count > 0);
                if (!hasEpisodes || HasGenericEpisodeTitles(metadata))
                    missing |= MetadataField.Seasons;
            }

            return missing;
        }

        private int GetAddonPriorityIndex(List<string> addonUrls, string? addonUrl)
        {
            if (string.IsNullOrWhiteSpace(addonUrl)) return -1;
            string host = GetHostSafe(addonUrl);
            return addonUrls.FindIndex(a => string.Equals(GetHostSafe(a), host, StringComparison.OrdinalIgnoreCase));
        }

        private void SeedFromCatalogMetadata(UnifiedMetadata metadata, StremioMediaStream stream, MetadataTrace trace, MetadataContext context, bool quiet = false)
        {
            if (stream?.Meta == null) return;

            string? addonUrl = stream.SourceAddon;
            
            // [NEW] Check for History Persistence (AUTHORITY_HISTORY)
            bool isFromHistory = false;
            string bestId = ResolveBestInitialId(stream);
            if (!string.IsNullOrEmpty(bestId) && HistoryManager.Instance.GetProgress(bestId) != null)
            {
                isFromHistory = true;
                if (string.IsNullOrEmpty(addonUrl))
                {
                    addonUrl = "history";
                    if (!quiet) trace.Log("History", $"Source identified as Watch History for {bestId}");
                }
            }
            
            // [FALLBACK] If SourceAddon is missing (common for CW items), check Discovery Cache
            if (string.IsNullOrEmpty(addonUrl))
            {
                // Improved fallback: Search all discovery keys for a match to this ID to recover original source info
                bestId = ResolveBestInitialId(stream) ?? stream.IMDbId ?? stream.Id.ToString();
                if (!string.IsNullOrEmpty(bestId))
                {
                    var discoveryEntry = _resultCache.FirstOrDefault(kvp => kvp.Key.Contains(bestId) && kvp.Key.EndsWith("_discovery")).Value;
                    if (discoveryEntry.Data != null && !string.IsNullOrEmpty(discoveryEntry.Data.CatalogSourceAddonUrl))
                    {
                        addonUrl = discoveryEntry.Data.CatalogSourceAddonUrl;
                        if (!quiet) trace.Log("Fallback", $"Source addon recovered from discovery cache: {GetHostSafe(addonUrl)}");
                    }
                }
            }

            // [FIX] CROSS-CATALOG TITLE SEEDING
            var addonUrls = StremioAddonManager.Instance.GetAddons();
            int currentCatalogPriority = GetAddonPriorityIndex(addonUrls, addonUrl);
            int primaryCatalogPriority = GetAddonPriorityIndex(addonUrls, metadata.CatalogSourceAddonUrl);

            if (currentCatalogPriority < 0) currentCatalogPriority = int.MaxValue;
            if (primaryCatalogPriority < 0) primaryCatalogPriority = int.MaxValue;

            string newTitle = stream.Meta.Name;
            if (!string.IsNullOrWhiteSpace(newTitle))
            {
                if (string.IsNullOrWhiteSpace(metadata.Title))
                {
                    metadata.Title = newTitle;
                    metadata.CatalogSourceAddonUrl = stream.SourceAddon;
                    metadata.CatalogSourceInfo = isFromHistory ? "Watch History" : GetHostSafe(stream.SourceAddon);
                    
                    // Assign initial priority
                    int authority = isFromHistory ? MetadataPriority.AUTHORITY_HISTORY : MetadataPriority.AUTHORITY_STREMIO_ADDON;
                    int depth = isFromHistory ? MetadataPriority.DEPTH_DETAIL : MetadataPriority.DEPTH_CATALOG; // History has likely been enriched before

                    metadata.PriorityScore = MetadataPriority.CalculateScore(
                        authority, 
                        depth, 
                        currentCatalogPriority >= 0 ? currentCatalogPriority : 99
                    );
                }
                else if (!string.Equals(metadata.Title, newTitle, StringComparison.OrdinalIgnoreCase))
                {
                    // Heuristic: If new catalog has HIGHER (lower index) priority, it becomes Title, old becomes SubTitle.
                    // Otherwise, new becomes SubTitle.
                    if (currentCatalogPriority < primaryCatalogPriority)
                    {
                        trace.Log("Seed", $"Title swap: {metadata.Title} (low pri) -> SubTitle, {newTitle} (high pri) -> Title");
                        metadata.SubTitle = metadata.Title;
                        metadata.Title = newTitle;
                        metadata.CatalogSourceAddonUrl = stream.SourceAddon;
                        metadata.CatalogSourceInfo = GetHostSafe(stream.SourceAddon);
                    }
                    else if (string.IsNullOrWhiteSpace(metadata.SubTitle))
                    {
                        if (!quiet) trace.Log("Seed", $"Alternative title from lower priority catalog ({GetHostSafe(stream.SourceAddon)}): {newTitle} -> SubTitle");
                        metadata.SubTitle = newTitle;
                    }
                }
            }

            // OriginalName handling (as a secondary SubTitle source)
            if (!string.IsNullOrWhiteSpace(stream.Meta.OriginalName) &&
                !string.Equals(stream.Meta.OriginalName, metadata.Title, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(metadata.SubTitle))
            {
                metadata.SubTitle = stream.Meta.OriginalName;
            }

            bool isCurrentSeed = string.IsNullOrWhiteSpace(metadata.MetadataSourceInfo) || metadata.MetadataSourceInfo.Contains("Catalog Seed", StringComparison.OrdinalIgnoreCase);
            bool isHigherOrEqualPriority = currentCatalogPriority <= primaryCatalogPriority;
            bool canOverwriteSeedFields = string.IsNullOrWhiteSpace(metadata.MetadataSourceInfo) || (isCurrentSeed && isHigherOrEqualPriority);

            // Mapping core fields (using thorough shared logic)
            MapStremioToUnified(stream.Meta, metadata, canOverwriteSeedFields, quiet);

            // Merge episodes from catalog metadata (SKIP during grid discovery for performance)
            if (metadata.IsSeries && stream.Meta?.Videos != null && context != MetadataContext.Discovery && context != MetadataContext.ExpandedCard)
            {
                  MergeStremioEpisodes(stream.Meta, metadata, GetHostSafe(addonUrl), canOverwriteSeedFields);
            }

            if (stream.Meta.ImdbRating != null)
            {
                string ratingStr = stream.Meta.ImdbRating.ToString().Replace(",", ".");
                if ((canOverwriteSeedFields || metadata.Rating <= 0) && double.TryParse(ratingStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedRating) && parsedRating > 0)
                {
                    metadata.Rating = parsedRating;
                }
            }

            // Always link the Catalog Source Info during seeding if available
            if (!string.IsNullOrEmpty(addonUrl))
            {
                // Only update SourceAddon/Info if we are actually overwriting or if it's currently empty
                if (canOverwriteSeedFields || string.IsNullOrEmpty(metadata.CatalogSourceAddonUrl))
                {
                    metadata.CatalogSourceAddonUrl = addonUrl;
                    metadata.CatalogSourceInfo = GetHostSafe(addonUrl);
                }
            }
            else if (string.IsNullOrEmpty(metadata.CatalogSourceInfo))
            {
                metadata.CatalogSourceInfo = "Unknown";
            }

            if (canOverwriteSeedFields && !string.IsNullOrWhiteSpace(metadata.CatalogSourceInfo))
            {
                metadata.MetadataSourceInfo = isFromHistory ? "Watch History (Catalog Seed)" : $"{metadata.CatalogSourceInfo} (Catalog Seed)";
                metadata.PrimaryMetadataAddonUrl = isFromHistory ? "history" : metadata.CatalogSourceAddonUrl;
            }

            if (string.IsNullOrWhiteSpace(metadata.DataSource))
            {
                metadata.DataSource = !string.IsNullOrEmpty(metadata.CatalogSourceInfo) ? $"{metadata.CatalogSourceInfo} (Catalog)" : "Catalog";
            }
            else if (!metadata.DataSource.Contains("Catalog", StringComparison.OrdinalIgnoreCase))
            {
                metadata.DataSource = $"{metadata.DataSource} + Catalog";
            }

            string? bestImdb = ExtractImdbId(stream.Meta) ?? NormalizeId(stream.IMDbId) ?? NormalizeId(stream.Meta.Id);
            if (IsImdbId(bestImdb))
            {
                if (string.IsNullOrEmpty(metadata.ImdbId)) metadata.ImdbId = bestImdb;
            }
            else if (stream.Meta.MovieDbId != null && int.TryParse(stream.Meta.MovieDbId.ToString(), out int movieDbId) && movieDbId > 0)
            {
                if (string.IsNullOrEmpty(metadata.ImdbId)) metadata.ImdbId = $"tmdb:{movieDbId}";
            }
            else if (!string.IsNullOrWhiteSpace(stream.Meta.Id) && stream.Meta.Id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(metadata.ImdbId)) metadata.ImdbId = NormalizeId(stream.Meta.Id);
            }

            if (string.IsNullOrWhiteSpace(metadata.MetadataId))
            {
                metadata.MetadataId = stream.Meta.Id;
            }

            string websitePreview = string.IsNullOrWhiteSpace(stream.Meta.Website)
                ? "none"
                : (stream.Meta.Website.Length > 96 ? stream.Meta.Website.Substring(0, 96) + "..." : stream.Meta.Website);
            
            if (!quiet)
                trace.Log("Seed", $"Catalog IDs: MetaId={stream.Meta.Id ?? "null"} Imdb={stream.Meta.ImdbId ?? "null"} Website={websitePreview} => Resolved={metadata.ImdbId ?? "null"}");

            if ((stream.Meta.Trailers?.Count > 0) || (stream.Meta.TrailerStreams?.Count > 0))
            {
                var trailer = stream.Meta.Trailers?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Source))?.Source
                    ?? stream.Meta.TrailerStreams?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.YtId))?.YtId;
                if (!string.IsNullOrWhiteSpace(trailer))
                {
                    metadata.TrailerUrl = trailer.Contains("/") || trailer.Contains(".")
                        ? trailer
                        : $"https://www.youtube.com/watch?v={trailer}";
                }
            }

            if ((canOverwriteSeedFields && !string.IsNullOrEmpty(stream.Meta.Logo)) || string.IsNullOrEmpty(metadata.LogoUrl))
            {
                if (!string.IsNullOrEmpty(stream.Meta.Logo)) metadata.LogoUrl = stream.Meta.Logo;
            }

            // Enrich backdrop list for slideshows
            if (!string.IsNullOrEmpty(stream.Meta.Background))
            {
                AddUniqueBackdrop(metadata, UpgradeImageUrl(stream.Meta.Background));
            }

            if (stream.Meta.AppExtras != null)
            {
                if (!string.IsNullOrEmpty(stream.Meta.AppExtras.Logo) && (canOverwriteSeedFields || string.IsNullOrEmpty(metadata.LogoUrl)))
                    metadata.LogoUrl = stream.Meta.AppExtras.Logo;

                if (!string.IsNullOrEmpty(stream.Meta.AppExtras.Trailer) && (canOverwriteSeedFields || string.IsNullOrEmpty(metadata.TrailerUrl)))
                    metadata.TrailerUrl = stream.Meta.AppExtras.Trailer;

                if (stream.Meta.AppExtras.Backdrops?.Count > 0)
                {
                    foreach (var bg in stream.Meta.AppExtras.Backdrops.Take(10))
                    {
                        if (!string.IsNullOrEmpty(bg.Url))
                            AddUniqueBackdrop(metadata, UpgradeImageUrl(bg.Url), null);
                    }
                }
            }

            // [FIX] ENRICH SEEDED CAST PORTRAITS: Map Portraits from Catalog Meta if available
            lock (metadata.SyncRoot)
            {
                if (canOverwriteSeedFields || metadata.Cast == null || metadata.Cast.Count == 0)
                {
                    if (stream.Meta.AppExtras?.Cast?.Count > 0)
                    {
                        var incoming = stream.Meta.AppExtras.Cast.Take(25).Select(c => new UnifiedCast
                        {
                            Name = c.Name,
                            Character = c.Character,
                            ProfileUrl = c.Photo
                        }).ToList();
                        
                        if (metadata.Cast != null && metadata.Cast.Count > 0) 
                        {
                            foreach(var ic in incoming) {
                                if (string.IsNullOrEmpty(ic.ProfileUrl)) {
                                    var ex = metadata.Cast.FirstOrDefault(oc => string.Equals(oc.Name, ic.Name, StringComparison.OrdinalIgnoreCase));
                                    if (ex != null) ic.ProfileUrl = ex.ProfileUrl;
                                }
                            }
                        }
                        metadata.Cast = incoming;
                    }
                }
            }

            // [FIX] LINK DIRECTOR PHOTO FROM CAST: Propagate photo if name matches (Crucial for AioStreams)
            if (metadata.Directors != null && metadata.Cast != null)
            {
                foreach (var d in metadata.Directors)
                {
                    if (string.IsNullOrEmpty(d.ProfileUrl))
                    {
                        var match = metadata.Cast.FirstOrDefault(c => !string.IsNullOrEmpty(c.ProfileUrl) && string.Equals(c.Name, d.Name, StringComparison.OrdinalIgnoreCase));
                        if (match != null) d.ProfileUrl = match.ProfileUrl;
                    }
                }
            }

            if (!quiet) trace.Log("Seed", $"Catalog seed applied from {GetHostSafe(addonUrl)}");
        }

        private async Task<AddonMetaCacheEntry> GetAddonMetaCachedAsync(string addonUrl, string type, string id, MetadataTrace trace)
        {
            string key = $"{addonUrl}|{type}|{id}";
            if (_addonMetaCache.TryGetValue(key, out var cached) && DateTime.Now < cached.Expiry)
            {
                trace.Log("AddonCache", $"HIT {GetHostSafe(addonUrl)}");
                return cached.Data;
            }

            trace.Log("AddonCache", $"MISS {GetHostSafe(addonUrl)}");
            return await _activeAddonMetaTasks.GetOrAdd(key, _ => FetchAddonMetaInternalAsync(addonUrl, type, id, key, trace));
        }

        private async Task<AddonMetaCacheEntry> FetchAddonMetaInternalAsync(string addonUrl, string type, string id, string cacheKey, MetadataTrace trace)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Fetching metadata from addon: {GetHostSafe(addonUrl)}, type: {type}, id: {id}");
                var meta = await _stremioService.GetMetaAsync(addonUrl, type, id);
                var entry = new AddonMetaCacheEntry { HasValue = meta != null, Meta = meta };
                var ttl = entry.HasValue ? _addonMetaPositiveCacheDuration : _addonMetaNegativeCacheDuration;
                _addonMetaCache[cacheKey] = (entry, DateTime.Now.Add(ttl));
                return entry;
            }
            catch (TaskCanceledException ex)
            {
                trace.Log("AddonCache", $"TaskCanceledException on {GetHostSafe(addonUrl)}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] TaskCanceledException in FetchAddonMetaInternalAsync | URL: {addonUrl} | Type: {type} | ID: {id} | Message: {ex.Message} | StackTrace: {ex.StackTrace}");
                var entry = new AddonMetaCacheEntry { HasValue = false, Meta = null };
                _addonMetaCache[cacheKey] = (entry, DateTime.Now.Add(_addonMetaNegativeCacheDuration));
                return entry;
            }
            catch (OperationCanceledException ex)
            {
                trace.Log("AddonCache", $"OperationCanceledException on {GetHostSafe(addonUrl)}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] OperationCanceledException in FetchAddonMetaInternalAsync | URL: {addonUrl} | Type: {type} | ID: {id} | Message: {ex.Message} | StackTrace: {ex.StackTrace}");
                var entry = new AddonMetaCacheEntry { HasValue = false, Meta = null };
                _addonMetaCache[cacheKey] = (entry, DateTime.Now.Add(_addonMetaNegativeCacheDuration));
                return entry;
            }
            catch (Exception ex)
            {
                trace.Log("AddonCache", $"Fetch error on {GetHostSafe(addonUrl)}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Exception in FetchAddonMetaInternalAsync | URL: {addonUrl} | Type: {type} | ID: {id} | ExceptionType: {ex.GetType().Name} | Message: {ex.Message} | StackTrace: {ex.StackTrace}");
                var entry = new AddonMetaCacheEntry { HasValue = false, Meta = null };
                _addonMetaCache[cacheKey] = (entry, DateTime.Now.Add(_addonMetaNegativeCacheDuration));
                return entry;
            }
            finally
            {
                _activeAddonMetaTasks.TryRemove(cacheKey, out _);
            }
        }

        private bool IsSatisfied(UnifiedMetadata metadata, MetadataContext context, bool isCw = false)
        {
            // [FIX] Loading Loop Protection
            // If we've already done the full enrichment for this context or a higher one,
            // we consider it satisfied even if some fields (like Rating or Logo) are still missing.
            // This prevents continuous re-fetching for items where certain data simply doesn't exist.
            if (metadata.MaxEnrichmentContext >= context)
                return true;

            return GetMissingFields(metadata, GetRequiredFields(context, isCw)) == MetadataField.None;
        }

        private string GetHostSafe(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "Unknown";
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return uri.Host;

                // Fallback: manually extract host if possible
                var host = url.Replace("https://", "").Replace("http://", "").Split('/')[0];
                return string.IsNullOrWhiteSpace(host) ? "Unknown" : host;
            }
            catch
            {
                return "Unknown";
            }
        }

        private bool IsDiscoveryComplete(UnifiedMetadata metadata)
        {
            // [REFINEMENT] Loosen rating requirement for Discovery. 
            // Some newer items might not have a rating yet, but we want to show the TR title and posters.
            return !string.IsNullOrEmpty(metadata.Title) && 
                   !string.IsNullOrEmpty(metadata.BackdropUrl) &&
                   !string.IsNullOrEmpty(metadata.Year) &&
                   !IsPlaceholderOverview(metadata.Overview) &&
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

        private async Task EnrichWithIptvMovieAsync(UnifiedMetadata metadata, Models.IMediaStream vod, MetadataTrace? trace = null)
        {
            if (App.CurrentLogin == null) return;
            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Enriching IPTV Movie: {vod.Title} (ID: {vod.Id}, ProviderImdb: {vod.IMDbId}) type: {vod.GetType().Name}");

            try
            {
                int streamId = vod.Id;
                
                // [FIX] If navigating from search (Stremio item), we need to find the matching IPTV item by IMDb ID or Title
                if (streamId <= 0)
                {
                    string playlistId = App.CurrentLogin?.PlaylistId ?? AppSettings.LastPlaylistId?.ToString() ?? "default";
                    trace?.Log("IPTV", $"Enriching IPTV Movie: {metadata.Title} | CacheId: {playlistId}");
                    
                    var allVod = await ContentCacheService.Instance.LoadCacheAsync<VodStream>(playlistId, "vod");
                    if (allVod != null)
                    {
                        // Use Centralized IptvMatchService
                        var match = IptvMatchService.Instance.FindMatchById(metadata.ImdbId, false) as VodStream;
                        if (match == null && !string.IsNullOrEmpty(metadata.Title) && metadata.Title != "Unknown" && metadata.Title != "Loading...")
                        {
                            match = IptvMatchService.Instance.FindMatch(metadata.Title, metadata.OriginalTitle, metadata.SubTitle, null, metadata.Year, null, false) as VodStream;
                        }

                        if (match != null)
                        {
                            trace?.Log("IPTV", $"Match Success: {match.Name}");
                        }
                        else
                        {
                            trace?.Log("IPTV", $"Match Failed for: {metadata.Title}");
                        }

                        if (match != null) 
                        {
                            // [NEW] Learn and Persist this match if it was found via Title
                            if (string.IsNullOrEmpty(match.ImdbId) || match.ImdbId != metadata.ImdbId)
                            {
                                IptvMatchService.Instance.RegisterMatch(match, metadata.ImdbId);
                            }

                            System.Diagnostics.Debug.WriteLine($"[IPTV_MATCH] Match Details: Title='{match.Name}', ProviderImdb='{match.IMDbId}', InternalId={match.StreamId}");
                            streamId = match.StreamId;
                            metadata.IsAvailableOnIptv = true;
                            
                            // Seed basic info from the match early
                            if (string.IsNullOrEmpty(metadata.Title) || metadata.Title == "Loading...") metadata.Title = match.Name;
                            if (string.IsNullOrEmpty(metadata.PosterUrl)) metadata.PosterUrl = match.IconUrl;
                            if (string.IsNullOrEmpty(metadata.Year)) metadata.Year = match.Year;
                            if (metadata.Rating == 0 && double.TryParse(match.Rating, out double r)) metadata.Rating = r;
                        }
                        else
                        {
                            trace?.Log("IPTV", $"Match Failed for: {metadata.Title}");
                        }
                    }
                    else
                    {
                        trace?.Log("IPTV", $"VOD Cache is empty or not found for CacheId: {playlistId}");
                    }
                }
                else if (vod is VodStream vs)
                {
                    // If we already have a VodStream from the library, set availability immediately
                    metadata.IsAvailableOnIptv = true;
                    if (string.IsNullOrEmpty(metadata.Title) || metadata.Title == "Loading...") metadata.Title = vs.Name;
                    if (string.IsNullOrEmpty(metadata.PosterUrl)) metadata.PosterUrl = vs.IconUrl;
                    if (string.IsNullOrEmpty(metadata.Year)) metadata.Year = vs.Year;
                }

                if (streamId <= 0) return;

                // [FIX] Pre-set basic stream URL in case GetMovieInfoAsync returns empty info/movie_data
                if (metadata.IsAvailableOnIptv && string.IsNullOrEmpty(metadata.StreamUrl))
                {
                    var login = App.CurrentLogin;
                    string ext = (vod as VodStream)?.ContainerExtension ?? "mkv";
                    if (!ext.StartsWith(".")) ext = "." + ext;
                    metadata.StreamUrl = $"{login.Host}/movie/{login.Username}/{login.Password}/{streamId}{ext}";
                    metadata.MetadataId = streamId.ToString();
                }

                var result = await ContentCacheService.Instance.GetMovieInfoAsync(streamId, App.CurrentLogin);
                if (result != null)
                {
                    metadata.IsAvailableOnIptv = true;
                    if (string.IsNullOrEmpty(metadata.MetadataId)) metadata.MetadataId = streamId.ToString();

                    if (result.Info != null)
                    {
                        AppLogger.Info($"[Enrich-Iptv] IPTV Movie Details Found: {result.Info.Name}");
                        
                        // 1. Basic Metadata (Priority to Stremio/TMDB, but fill if empty)
                        if (string.IsNullOrEmpty(metadata.Overview) || metadata.Overview == "Açıklama mevcut değil.") 
                            metadata.Overview = result.Info.Plot;
                        
                        if (string.IsNullOrEmpty(metadata.Genres)) 
                            metadata.Genres = result.Info.Genre;
                        
                        if (string.IsNullOrEmpty(metadata.Title) || metadata.Title == "Loading...") 
                            metadata.Title = result.Info.Name;
                        
                        // 2. Visuals
                        if (string.IsNullOrEmpty(metadata.BackdropUrl)) 
                            metadata.BackdropUrl = result.Info.MovieImage;

                        if (result.Info.BackdropPath != null && result.Info.BackdropPath.Length > 0)
                        {
                            foreach (var path in result.Info.BackdropPath)
                            {
                                if (!metadata.BackdropUrls.Contains(path)) metadata.BackdropUrls.Add(path);
                            }
                            if (string.IsNullOrEmpty(metadata.BackdropUrl)) metadata.BackdropUrl = result.Info.BackdropPath[0];
                        }

                        if (string.IsNullOrEmpty(metadata.PosterUrl))
                            metadata.PosterUrl = result.Info.MovieImage ?? result.Info.CoverBig;

                        // MovieImage from IPTV is nearly always a poster, not a logo.
                        // We rely on TMDB to provide a real logo.

                        // 3. Classification
                        if (string.IsNullOrEmpty(metadata.AgeRating))
                            metadata.AgeRating = !string.IsNullOrEmpty(result.Info.MpaaRating) ? result.Info.MpaaRating : result.Info.Age;
                        
                        if (string.IsNullOrEmpty(metadata.Country))
                            metadata.Country = result.Info.Country;

                        // 4. Trailer
                        if (string.IsNullOrEmpty(metadata.TrailerUrl) && !string.IsNullOrEmpty(result.Info.YoutubeTrailer))
                        {
                            metadata.TrailerUrl = result.Info.YoutubeTrailer.StartsWith("http") 
                                ? result.Info.YoutubeTrailer 
                                : $"https://www.youtube.com/watch?v={result.Info.YoutubeTrailer}";
                        }

                        // 5. Technical Info (Used to skip probing)
                        if (string.IsNullOrEmpty(metadata.Runtime))
                            metadata.Runtime = result.Info.Duration;

                        if (result.Info.Video != null)
                        {
                            metadata.Resolution = $"{result.Info.Video.Width}x{result.Info.Video.Height}";
                            metadata.VideoCodec = result.Info.Video.CodecName;
                        }
                        if (result.Info.Audio != null)
                        {
                            metadata.AudioCodec = result.Info.Audio.CodecName;
                        }
                        if (result.Info.Bitrate != null)
                        {
                            if (long.TryParse(result.Info.Bitrate.ToString(), out long br)) metadata.Bitrate = br;
                        }

                        // 6. Rating
                        if (metadata.Rating == 0)
                        {
                            if (double.TryParse(result.Info.Rating, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double ratingValue))
                            {
                                metadata.Rating = ratingValue;
                            }
                        }

                        // 7. Year
                        if (string.IsNullOrEmpty(metadata.Year))
                            metadata.Year = result.Info.ReleaseDate;

                        // 8. Cast & Crew
                        if (result.Info.Director != null && (metadata.Directors == null || metadata.Directors.Count == 0))
                        {
                            metadata.Directors = result.Info.Director.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                              .Select(s => new Models.Metadata.UnifiedCast { Name = s.Trim(), Character = "Yönetmen" }).ToList();
                        }

                        if (result.Info.Cast != null && (metadata.Cast == null || metadata.Cast.Count == 0))
                        {
                            metadata.Cast = result.Info.Cast.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                          .Select(s => new Models.Metadata.UnifiedCast { Name = s.Trim() }).ToList();
                        }
                    }

                    // [CRITICAL] Update Stream URL
                    if (result.MovieData != null)
                    {
                        string ext = result.MovieData.ContainerExtension;
                        if (string.IsNullOrEmpty(ext)) ext = (vod as VodStream)?.ContainerExtension ?? "mkv";
                        if (!ext.StartsWith(".")) ext = "." + ext;
                        
                        string streamUrl = $"{App.CurrentLogin.Host}/movie/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{result.MovieData.StreamId}{ext}";
                        vod.StreamUrl = streamUrl;
                        metadata.StreamUrl = streamUrl;
                        metadata.IsAvailableOnIptv = true;
                        metadata.MetadataId = result.MovieData.StreamId.ToString();
                        
                        if (string.IsNullOrEmpty(metadata.DataSource) || !metadata.DataSource.Contains("IPTV"))
                            metadata.DataSource += (string.IsNullOrEmpty(metadata.DataSource) ? "" : " + ") + "IPTV";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] IPTV Movie Enrich Error: {ex.Message}");
            }
        }

        private async Task EnrichWithIptvAsync(UnifiedMetadata metadata, SeriesStream series, MetadataTrace? trace = null)
        {
            if (App.CurrentLogin == null) return;
            AppLogger.Info($"[Enrich-Iptv] Starting enrichment for: {metadata.Title} (IMDb: {metadata.ImdbId})");

            try
            {
                int seriesId = series.SeriesId;

                // [FIX] If seriesId is 0 (or we are in a Stremio context), try to find by IMDbId or Title
                if (seriesId <= 0)
                {
                    string playlistId = App.CurrentLogin?.PlaylistId ?? AppSettings.LastPlaylistId?.ToString() ?? "default";
                    trace?.Log("IPTV", $"Enriching IPTV Series: {metadata.Title} | CacheId: {playlistId}");
                    var allSeries = await ContentCacheService.Instance.LoadCacheAsync<SeriesStream>(playlistId, "series");
                    if (allSeries != null)
                    {
                        // Use Centralized IptvMatchService
                        var match = IptvMatchService.Instance.FindAllMatchesById(metadata.ImdbId, true).FirstOrDefault() as SeriesStream;
                        if (match == null && !string.IsNullOrEmpty(metadata.Title) && metadata.Title != "Unknown" && metadata.Title != "Loading...")
                        {
                            match = IptvMatchService.Instance.FindAllMatches(metadata.Title, metadata.OriginalTitle, metadata.SubTitle, null, metadata.Year, null, true).FirstOrDefault() as SeriesStream;
                        }

                        if (match == null && !string.IsNullOrEmpty(metadata.ImdbId))
                        {
                            string? mappedId = IdMappingService.Instance.GetTmdbForImdb(metadata.ImdbId);
                            if (!string.IsNullOrEmpty(mappedId))
                            {
                                trace?.Log("IPTV", $"ID conversion (Service): {metadata.ImdbId} -> {mappedId}");
                                match = IptvMatchService.Instance.FindAllMatchesById(mappedId, true).FirstOrDefault() as SeriesStream;
                            }
                            
                            if (match == null)
                            {
                                AppLogger.Info($"[Enrich-Iptv] No direct IMDb match for '{metadata.Title}'. Trying TMDB network conversion...");
                                var tmdbSearch = await TmdbHelper.GetTvByExternalIdAsync(metadata.ImdbId);
                                if (tmdbSearch != null)
                                {
                                    string tmdbIdStr = tmdbSearch.Id.ToString();
                                    IdMappingService.Instance.RegisterMapping(metadata.ImdbId, tmdbIdStr);
                                    AppLogger.Info($"[Enrich-Iptv] Converted {metadata.ImdbId} -> TMDB ID {tmdbIdStr}. Searching IPTV again...");
                                    match = IptvMatchService.Instance.FindAllMatchesById(tmdbIdStr, true).FirstOrDefault() as SeriesStream;
                                }
                            }
                        }

                        if (match != null)
                        {
                            // [NEW] Learn and Persist this match if it was found via Title or ID conversion
                            if (string.IsNullOrEmpty(match.ImdbId) || match.ImdbId != metadata.ImdbId)
                            {
                                IptvMatchService.Instance.RegisterMatch(match, metadata.ImdbId);
                            }

                            AppLogger.Info($"[Enrich-Iptv] Match SUCCESS: '{match.Name}' (ID: {match.SeriesId}, IMDb: {match.IMDbId})");
                            seriesId = match.SeriesId;
                            metadata.IsAvailableOnIptv = true;

                            // Seed basic info from match
                            // [REFINEMENT] If current title looks like an episode name (e.g. from an IPTV provider) 
                            // and the IPTV match name looks more like a series title, promote it.
                            bool titleIsPlaceholder = string.IsNullOrEmpty(metadata.Title) || metadata.Title == "Loading...";
                            bool titleLooksLikeEpisode = !titleIsPlaceholder && IsGenericEpisodeTitle(metadata.Title, match.Name);
                            
                            // If title is placeholder OR it's a very different name than the match (and match name matches subtitle)
                            bool shouldPromoteMatchName = titleIsPlaceholder || titleLooksLikeEpisode;
                            if (!shouldPromoteMatchName && !string.IsNullOrEmpty(metadata.SubTitle) && 
                                metadata.SubTitle.Contains(match.Name, StringComparison.OrdinalIgnoreCase) &&
                                !metadata.Title.Contains(match.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                // Case: Title="Yaz Geldi...", SubTitle="Watchmen", MatchName="Watchmen"
                                // The title is clearly wrong and SubTitle holds the real name.
                                shouldPromoteMatchName = true;
                                trace?.Log("IPTV", $"Title Promotion: '{metadata.Title}' -> '{match.Name}' (Verified via SubTitle)");
                            }

                            if (shouldPromoteMatchName) metadata.Title = match.Name;
                            if (string.IsNullOrEmpty(metadata.PosterUrl)) metadata.PosterUrl = match.Cover;
                            if (string.IsNullOrEmpty(metadata.Year)) metadata.Year = match.ReleaseDate;
                            if (string.IsNullOrEmpty(metadata.Overview)) metadata.Overview = match.Plot;
                        }
                        else
                        {
                            AppLogger.Warn($"[Enrich-Iptv] Match FAILED for: '{metadata.Title}' (IMDb: {metadata.ImdbId})");
                        }
                    }
                    else
                    {
                        AppLogger.Warn($"[Enrich-Iptv] Series Cache is empty for: {playlistId}");
                    }
                }
                else
                {
                    AppLogger.Info($"[Enrich-Iptv] Using existing series stream: {series.Name} (ID: {series.SeriesId})");
                    metadata.IsAvailableOnIptv = true;
                    if (string.IsNullOrEmpty(metadata.Title) || metadata.Title == "Loading...") metadata.Title = series.Name;
                    if (string.IsNullOrEmpty(metadata.PosterUrl)) metadata.PosterUrl = series.Cover;
                    if (string.IsNullOrEmpty(metadata.Year)) metadata.Year = series.ReleaseDate;
                    if (string.IsNullOrEmpty(metadata.Overview)) metadata.Overview = series.Plot;
                }

                if (seriesId <= 0) return;

                AppLogger.Info($"[Enrich-Iptv] Fetching full series info for ID: {seriesId}");
                var info = await ContentCacheService.Instance.GetSeriesInfoAsync(seriesId, App.CurrentLogin);
                if (info != null)
                {
                    metadata.IsAvailableOnIptv = true;
                    if (string.IsNullOrEmpty(metadata.MetadataId)) metadata.MetadataId = seriesId.ToString();

                    // 1. Series Level Metadata
                    if (info.Info != null)
                    {
                        AppLogger.Info($"[Enrich-Iptv] Received series info (Name: {info.Info.Name}, Rating: {info.Info.Rating}, Release: {info.Info.ReleaseDate})");
                        
                        // Map Basic Info
                        if (string.IsNullOrEmpty(metadata.Overview)) metadata.Overview = info.Info.Plot;
                        if (string.IsNullOrEmpty(metadata.Genres)) metadata.Genres = info.Info.Genre;
                        if (string.IsNullOrEmpty(metadata.Title)) metadata.Title = info.Info.Name;
                        if (string.IsNullOrEmpty(metadata.BackdropUrl)) metadata.BackdropUrl = info.Info.Cover;
                        if (string.IsNullOrEmpty(metadata.PosterUrl)) metadata.PosterUrl = info.Info.Cover;

                        // info.Info.Cover is a poster, not a logo. Rely on TMDB for real logos.

                        // Map Rating & Year
                        string ratingStr = !string.IsNullOrEmpty(info.Info.Rating) ? info.Info.Rating : series.Rating;
                        if (double.TryParse(ratingStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
                        {
                            metadata.Rating = r;
                        }
                        if (string.IsNullOrEmpty(metadata.Year)) metadata.Year = info.Info.ReleaseDate;

                        // Classification
                        if (string.IsNullOrEmpty(metadata.AgeRating))
                            metadata.AgeRating = info.Info.Age;

                        if (string.IsNullOrEmpty(metadata.Country))
                            metadata.Country = info.Info.Country;

                        if (info.Info.Cast != null && (metadata.Cast == null || metadata.Cast.Count == 0))
                        {
                            metadata.Cast = info.Info.Cast.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                          .Select(s => new Models.Metadata.UnifiedCast { Name = s.Trim() }).ToList();
                        }

                        if (info.Info.Director != null && (metadata.Directors == null || metadata.Directors.Count == 0))
                        {
                            metadata.Directors = info.Info.Director.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                            .Select(s => new Models.Metadata.UnifiedCast { Name = s.Trim(), Character = "Yönetmen" }).ToList();
                        }

                        if (string.IsNullOrEmpty(metadata.DataSource) || !metadata.DataSource.Contains("IPTV"))
                            metadata.DataSource += (string.IsNullOrEmpty(metadata.DataSource) ? "" : " + ") + "IPTV";
                    }

                    // Map Seasons/Episodes if missing
                    if (metadata.Seasons.Count == 0 && info.Episodes != null)
                    {
                        int totalEpisodes = info.Episodes.Sum(kvp => kvp.Value.Count);
                        AppLogger.Info($"[Enrich-Iptv] Mapping {info.Episodes.Count} seasons ({totalEpisodes} episodes) from IPTV.");
                        
                        foreach (var kvp in info.Episodes)
                        {
                            if (int.TryParse(kvp.Key, out int seasonNum))
                            {
                                var seasonDef = new UnifiedSeason
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

                                    seasonDef.Episodes.Add(new UnifiedEpisode
                                    {
                                        Id = ep.Id,
                                        Title = ep.Title,
                                        IptvSourceTitle = ep.Title, // [NEW] Keep literal IPTV name for source panel
                                        IptvSeriesId = seriesId, // [NEW] Store series ID for deduplication
                                        Overview = ep.Info?.Plot, // Episode specific plot (rare but possible)
                                        ThumbnailUrl = ep.Info?.MovieImage,
                                        SeasonNumber = seasonNum,
                                        EpisodeNumber = epNum,
                                        StreamUrl = streamUrl,
                                        RuntimeFormatted = ep.Info?.Duration
                                    });
                                }
                                metadata.Seasons.Add(seasonDef);
                            }
                        }
                        metadata.Seasons = metadata.Seasons
                             .OrderBy(s => s.SeasonNumber == 0 ? int.MaxValue : s.SeasonNumber)
                             .ToList();
                    }
                    else if (metadata.Seasons.Count > 0 && info.Episodes != null)
                    {
                         AppLogger.Info($"[Enrich-Iptv] Metadata already has {metadata.Seasons.Count} seasons. Merging IPTV stream URLs and technical details.");
                         int mergeCount = 0;
                         foreach (var kvp in info.Episodes)
                         {
                             if (int.TryParse(kvp.Key, out int seasonNum))
                             {
                                 var existingSeason = metadata.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNum);
                                 if (existingSeason != null)
                                 {
                                     foreach (var ep in kvp.Value)
                                     {
                                         int.TryParse(ep.EpisodeNum?.ToString(), out int epNum);
                                         var existingEp = existingSeason.Episodes.FirstOrDefault(e => e.EpisodeNumber == epNum);
                                         
                                         string extension = ep.ContainerExtension;
                                         if (string.IsNullOrEmpty(extension)) extension = "mkv"; 
                                         if (!extension.StartsWith(".")) extension = "." + extension;
                                         string streamUrl = $"{App.CurrentLogin.Host}/series/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{ep.Id}{extension}";

                                         if (existingEp != null)
                                         {
                                             // Update existing episode
                                             existingEp.StreamUrl = streamUrl;
                                             existingEp.IptvSourceTitle = ep.Title; // [NEW] Keep literal IPTV name for source panel
                                             existingEp.IptvSeriesId = seriesId; // [NEW] Store series ID for deduplication
                                             if (string.IsNullOrEmpty(existingEp.Id)) existingEp.Id = ep.Id;
                                             if (string.IsNullOrEmpty(existingEp.Overview)) existingEp.Overview = ep.Info?.Plot;
                                             if (string.IsNullOrEmpty(existingEp.RuntimeFormatted)) existingEp.RuntimeFormatted = ep.Info?.Duration;
                                             mergeCount++;
                                         }
                                         else
                                         {
                                             // Add as new episode if it doesn't exist (e.g. IPTV has more episodes than Stremio)
                                             existingSeason.Episodes.Add(new UnifiedEpisode
                                             {
                                                 Id = ep.Id,
                                                 Title = ep.Title,
                                                 IptvSourceTitle = ep.Title, // [NEW] Keep literal IPTV name for source panel
                                                 IptvSeriesId = seriesId, // [NEW] Store series ID for deduplication
                                                 Overview = ep.Info?.Plot,
                                                 ThumbnailUrl = ep.Info?.MovieImage,
                                                 SeasonNumber = seasonNum,
                                                 EpisodeNumber = epNum,
                                                 StreamUrl = streamUrl,
                                                 RuntimeFormatted = ep.Info?.Duration
                                             });
                                             mergeCount++;
                                         }
                                     }
                                     existingSeason.Episodes = existingSeason.Episodes.OrderBy(e => e.EpisodeNumber).ToList();
                                 }
                             }
                         }
                         AppLogger.Info($"[Enrich-Iptv] Successfully merged {mergeCount} IPTV episode sources.");
                    }
                }
                else
                {
                    AppLogger.Warn($"[Enrich-Iptv] No info returned from GetSeriesInfoAsync for ID: {seriesId}");
                }

                metadata.IsAvailableOnIptv = true;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[Enrich-Iptv] IPTV Enrich Error", ex);
            }
        }

        public async Task EnrichSeasonAsync(UnifiedMetadata metadata, int seasonNumber, MetadataTrace? trace = null)
        {
            if (metadata?.TmdbInfo == null) return;
            
            var season = metadata.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
            if (season == null) return;

            // Fetch detailed season info
            if (trace != null) trace.Log("TMDB", $"Enriching Season {seasonNumber}...");
            var tmdbSeason = await TmdbHelper.GetSeasonDetailsAsync(metadata.TmdbInfo.Id, seasonNumber);
            if (tmdbSeason?.Episodes == null) 
            {
                if (trace != null) trace.Log("TMDB", $"Season {seasonNumber} NOT found or empty on TMDB.");
                return;
            }

            // Merge TMDB data into existing episodes, or create new ones
            int initialCount = season.Episodes.Count;
            int updated = 0;
            int added = 0;

            // [NEW] Check for generic titles or missing overviews in localized response
            // If descriptions are missing, we'll try to fetch English fallback.
            bool areTitlesGeneric = tmdbSeason.Episodes.Any(e => IsGenericEpisodeTitle(e.Name, metadata.Title));
            bool areOverviewsMissing = tmdbSeason.Episodes.Any(e => string.IsNullOrEmpty(e.Overview));
            
            TmdbSeasonDetails? enSeason = null;
            if (areTitlesGeneric || areOverviewsMissing)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] EnrichSeason {seasonNumber}: Missing details (TitlesGeneric={areTitlesGeneric}, OverviewsMissing={areOverviewsMissing}, Lang={AppSettings.TmdbLanguage}). Fetching English fallback...");
                enSeason = await TmdbHelper.GetSeasonDetailsAsync(metadata.TmdbInfo.Id, seasonNumber, "en-US");
            }

            foreach (var tmdbEp in tmdbSeason.Episodes)
            {
                var existing = season.Episodes.FirstOrDefault(e => e.EpisodeNumber == tmdbEp.EpisodeNumber);
                
                // [FIX] Pick best title and overview
                string? bestName = tmdbEp.Name;
                string? bestOverview = tmdbEp.Overview;

                if (IsGenericEpisodeTitle(bestName, metadata.Title) || string.IsNullOrEmpty(bestOverview))
                {
                    var enEp = enSeason?.Episodes?.FirstOrDefault(e => e.EpisodeNumber == tmdbEp.EpisodeNumber);
                    if (enEp != null)
                    {
                        if (IsGenericEpisodeTitle(bestName, metadata.Title) && !IsGenericEpisodeTitle(enEp.Name, metadata.Title))
                            bestName = enEp.Name;
                        if (string.IsNullOrEmpty(bestOverview))
                            bestOverview = enEp.Overview;
                    }
                }

                if (existing != null)
                {
                    // Enhance existing
                    if (!string.IsNullOrEmpty(bestName)) 
                    {
                        bool existingIsGeneric = IsGenericEpisodeTitle(existing.Title, metadata.Title);
                        bool newIsGeneric = IsGenericEpisodeTitle(bestName, metadata.Title);
                        if (existingIsGeneric || !newIsGeneric)
                        {
                            existing.Title = bestName;
                        }
                    }
                    if (!string.IsNullOrEmpty(bestOverview)) existing.Overview = bestOverview;
                    if (!string.IsNullOrEmpty(tmdbEp.StillUrl)) existing.ThumbnailUrl = tmdbEp.StillUrl;
                    if (!existing.AirDate.HasValue && tmdbEp.AirDateDateTime.HasValue) existing.AirDate = tmdbEp.AirDateDateTime;
                    updated++;
                }
                else
                {
                    // Virtual Episode
                    season.Episodes.Add(new UnifiedEpisode
                    {
                        Id = $"tmdb:{metadata.TmdbInfo.Id}:{seasonNumber}:{tmdbEp.EpisodeNumber}",
                        SeasonNumber = seasonNumber,
                        EpisodeNumber = tmdbEp.EpisodeNumber,
                        Title = bestName ?? $"Episode {tmdbEp.EpisodeNumber}",
                        Overview = bestOverview,
                        ThumbnailUrl = tmdbEp.StillUrl,
                        AirDate = tmdbEp.AirDateDateTime,
                        StreamUrl = null
                    });
                    added++;
                }
            }

            if (trace != null) 
                trace.Log("TMDB", $"Season {seasonNumber} Enrichment Completed: {initialCount} -> {season.Episodes.Count} (Updated {updated}, Added {added})");

            // Ensure correct order
            season.Episodes = season.Episodes.OrderBy(e => e.EpisodeNumber).ToList();
            season.IsEnrichedByTmdb = true;
        }

        private void MapStremioToUnified(StremioMeta stremio, UnifiedMetadata unified, bool overwritePrimary, bool quiet = false)
        {
            if (stremio == null || stremio.Name == "#DUPE#") return;

            // [REFINEMENT] If overwritePrimary is true, only overwrite if source has data (don't overwrite with null)
            // [CRITICAL] Don't let non-canonical IDs (like numeric IPTV IDs) overwrite existing valid titles.
            string previousTitle = unified.Title;
            bool isCanonicalSource = !string.IsNullOrEmpty(stremio.Id) && IsCanonicalId(stremio.Id);
            bool currentIsPlaceholder = string.IsNullOrEmpty(unified.Title) || unified.Title == unified.MetadataId || unified.Title == "Loading...";
            
            bool shouldOverwriteTitle = overwritePrimary && !string.IsNullOrEmpty(stremio.Name) && !IsGenericEpisodeTitle(stremio.Name, unified.Title);
            if (shouldOverwriteTitle && !isCanonicalSource && !currentIsPlaceholder)
            {
                // Source is non-canonical (e.g. random IPTV numeric ID) and we already have a real title. 
                // Don't overwrite the primary title, but we can save it as a subtitle if it's different.
                shouldOverwriteTitle = false;
                if (string.IsNullOrEmpty(unified.SubTitle) && stremio.Name != unified.Title)
                    unified.SubTitle = stremio.Name;
            }

            if (shouldOverwriteTitle || currentIsPlaceholder)
                unified.Title = stremio.Name;
            else if (!overwritePrimary && !string.IsNullOrEmpty(stremio.Name) && stremio.Name != unified.Title)
            {
                // Secondary title (e.g. English title from Cinemeta when Turkish is primary)
                if (string.IsNullOrEmpty(unified.SubTitle) && !IsGenericEpisodeTitle(stremio.Name, unified.Title))
                    unified.SubTitle = stremio.Name;
            }

            if (string.IsNullOrWhiteSpace(unified.SubTitle) &&
                !string.IsNullOrWhiteSpace(stremio.OriginalName) &&
                !string.Equals(stremio.OriginalName, unified.Title, StringComparison.OrdinalIgnoreCase) && 
                !IsGenericEpisodeTitle(stremio.OriginalName, unified.Title))
            {
                unified.SubTitle = stremio.OriginalName;
            }

            // If a higher-priority source replaced title, keep previous title as subtitle.
            if (overwritePrimary &&
                !string.IsNullOrWhiteSpace(previousTitle) &&
                !string.IsNullOrWhiteSpace(unified.Title) &&
                !string.Equals(previousTitle, unified.Title, StringComparison.OrdinalIgnoreCase) &&
                previousTitle != unified.MetadataId && 
                !IsGenericEpisodeTitle(previousTitle, unified.Title))
            {
                unified.SubTitle = previousTitle;
            }

            bool currentOverviewIsPlaceholder = IsPlaceholderOverview(unified.Overview);
            bool newOverviewIsPlaceholder = IsPlaceholderOverview(stremio.Description);

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Description) && !newOverviewIsPlaceholder) || 
                string.IsNullOrEmpty(unified.Overview) || 
                (currentOverviewIsPlaceholder && !newOverviewIsPlaceholder))
            {
                unified.Overview = stremio.Description;
            }

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Poster)) || string.IsNullOrEmpty(unified.PosterUrl))
            {
                string newPoster = UpgradeImageUrl(stremio.Poster);
                if (overwritePrimary || string.IsNullOrEmpty(unified.PosterUrl) || GetQualityScore(newPoster) >= GetQualityScore(unified.PosterUrl))
                {
                    unified.PosterUrl = newPoster;
                }
            }


            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Background) && !ImageHelper.IsPlaceholder(stremio.Background)) || string.IsNullOrEmpty(unified.BackdropUrl))
            {
                string newBackdrop = UpgradeImageUrl(stremio.Background);
                if (!ImageHelper.IsPlaceholder(newBackdrop) && (overwritePrimary || string.IsNullOrEmpty(unified.BackdropUrl) || GetQualityScore(newBackdrop) >= GetQualityScore(unified.BackdropUrl)))
                {
                    // Quality Protection: If we already have a high-quality backdrop (e.g. from AioStreams/TMDB),
                    // don't let Priority-0 sources (Cinemeta) overwrite it with lower quality ones (like metahub medium).
                    if (!string.IsNullOrEmpty(unified.BackdropUrl) && overwritePrimary)
                    {
                        if (GetQualityScore(newBackdrop) < GetQualityScore(unified.BackdropUrl) && GetQualityScore(unified.BackdropUrl) >= 8)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Quality Protection: Preserving existing high-res backdrop over new {newBackdrop}");
                        }
                        else
                        {
                            unified.BackdropUrl = newBackdrop;
                        }
                    }
                    else
                    {
                        unified.BackdropUrl = newBackdrop;
                    }
                }
            }

            // Enrich backdrop list for slideshows
            if (!string.IsNullOrEmpty(stremio.Background))
            {
                AddUniqueBackdrop(unified, UpgradeImageUrl(stremio.Background));
            }


            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Logo)) || string.IsNullOrEmpty(unified.LogoUrl))
            {
                // Quality Protection for Logo: Preserve Fanart/TMDB logos over Metahub ones
                if (!string.IsNullOrEmpty(unified.LogoUrl) && overwritePrimary)
                {
                    if (GetLogoScore(stremio.Logo) >= GetLogoScore(unified.LogoUrl))
                    {
                        unified.LogoUrl = stremio.Logo;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Quality Protection: Preserving existing high-res logo over new {stremio.Logo}");
                    }
                }
                else
                {
                    unified.LogoUrl = stremio.Logo;
                }
            }

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.ReleaseInfo)) || string.IsNullOrEmpty(unified.Year))
                unified.Year = stremio.ReleaseInfo;

            if ((overwritePrimary && stremio.Genres?.Count > 0) || string.IsNullOrEmpty(unified.Genres))
                unified.Genres = (stremio.Genres != null && stremio.Genres.Count > 0) ? string.Join(", ", stremio.Genres) : "";

            // [NEW] Map Country, Status, Writer, Certification from AIOMetadata
            if (!string.IsNullOrEmpty(stremio.Country) && (overwritePrimary || string.IsNullOrEmpty(unified.Country)))
                unified.Country = stremio.Country;

            if (!string.IsNullOrEmpty(stremio.Status) && (overwritePrimary || string.IsNullOrEmpty(unified.Status)))
                unified.Status = stremio.Status;

            if (!string.IsNullOrEmpty(stremio.AppExtras?.Certification) && (overwritePrimary || string.IsNullOrEmpty(unified.Certification)))
                unified.Certification = stremio.AppExtras.Certification;

            if (stremio.Writer?.Count > 0 && (overwritePrimary || string.IsNullOrEmpty(unified.Writers)))
                unified.Writers = string.Join(", ", stremio.Writer);
            else if (stremio.AppExtras?.Writers?.Count > 0 && (overwritePrimary || string.IsNullOrEmpty(unified.Writers)))
                unified.Writers = string.Join(", ", stremio.AppExtras.Writers.Select(w => w.Name));
            else if (stremio.Links != null && (overwritePrimary || string.IsNullOrEmpty(unified.Writers)))
            {
                var writers = stremio.Links.Where(l => l.Category == "Writers").Select(l => l.Name).ToList();
                if (writers.Count > 0) unified.Writers = string.Join(", ", writers);
            }

            // [NEW] Map AppExtras.Directors as alternative director source with photos
            if (stremio.AppExtras?.Directors?.Count > 0 && (overwritePrimary || unified.Directors == null || unified.Directors.Count == 0))
            {
                var incomingDirectors = stremio.AppExtras.Directors.Take(10).Select(d => new UnifiedCast
                {
                    Name = d.Name,
                    Character = "Yönetmen",
                    ProfileUrl = d.Photo
                }).ToList();
                if (unified.Directors == null || unified.Directors.Count == 0)
                    unified.Directors = incomingDirectors;
            }

            // [NEW] Map SeasonPosters to UnifiedSeason.PosterUrl
            if (stremio.AppExtras?.SeasonPosters?.Count > 0 && unified.Seasons != null)
            {
                for (int i = 0; i < unified.Seasons.Count && i < stremio.AppExtras.SeasonPosters.Count; i++)
                {
                    var season = unified.Seasons[i];
                    if (season != null && string.IsNullOrEmpty(season.PosterUrl))
                    {
                        season.PosterUrl = UpgradeImageUrl(stremio.AppExtras.SeasonPosters[i]);
                    }
                }
            }

            lock (unified.SyncRoot)
            {
                if (overwritePrimary || unified.Cast == null || unified.Cast.Count == 0 || unified.Cast.All(c => string.IsNullOrEmpty(c.ProfileUrl)))
                {
                    List<UnifiedCast> incomingCast = null;
                    if (stremio.AppExtras?.Cast?.Count > 0)
                    {
                        incomingCast = stremio.AppExtras.Cast.Take(25).Select(c => new UnifiedCast 
                        { 
                            Name = c.Name, 
                            Character = c.Character,
                            ProfileUrl = c.Photo // Support for addons that return 'photo'
                        }).ToList();
                    }
                    else if (stremio.CreditsCast?.Count > 0)
                    {
                        incomingCast = stremio.CreditsCast.Take(25).Select(c => new UnifiedCast
                        {
                            Name = c.Name,
                            Character = c.Character,
                            ProfileUrl = !string.IsNullOrEmpty(c.ProfilePath) ? $"https://image.tmdb.org/t/p/w185{c.ProfilePath}" : null,
                            TmdbId = c.Id is int idVal ? idVal : (int.TryParse(c.Id?.ToString(), out var parsed) ? parsed : null)
                        }).ToList();
                    }
                    else if (stremio.Cast?.Count > 0)
                    {
                        incomingCast = stremio.Cast.Take(25).Select(name => new UnifiedCast { Name = name }).ToList();
                    }

                    if (incomingCast != null)
                    {
                        if (unified.Cast != null && unified.Cast.Count > 0)
                        {
                            // Smart Cast Merging: 
                            // 1. Prioritize order and details from the new (Primary) source.
                            // 2. Preserve portraits from the old list if the new one is missing them.
                            // 3. Append actors from the old list who are NOT in the new list.
                            foreach (var n in incomingCast)
                            {
                                if (string.IsNullOrEmpty(n.ProfileUrl))
                                {
                                    var existing = unified.Cast.FirstOrDefault(c => string.Equals(c.Name, n.Name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.ProfileUrl));
                                    if (existing != null) n.ProfileUrl = existing.ProfileUrl;
                                }
                            }

                            var nonMatches = unified.Cast.Where(oc => !incomingCast.Any(nc => string.Equals(nc.Name, oc.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                            incomingCast.AddRange(nonMatches.Take(15)); // Limit total size but be inclusive
                        }
                        unified.Cast = incomingCast;
                    }
                }
            }

            if (overwritePrimary || unified.Directors == null || unified.Directors.Count == 0)
            {
                List<UnifiedCast> incomingDirectors = null;
                if (stremio.CreditsCrew?.Count > 0 && stremio.CreditsCrew.Any(c => c.Job == "Director"))
                {
                    incomingDirectors = stremio.CreditsCrew.Where(c => c.Job == "Director").Take(10).Select(c => new UnifiedCast
                    {
                        Name = c.Name,
                        Character = "Yönetmen",
                        ProfileUrl = !string.IsNullOrEmpty(c.ProfilePath) ? $"https://image.tmdb.org/t/p/w185{c.ProfilePath}" : null
                    }).ToList();
                }
                else if (stremio.AppExtras?.Cast?.Any(c => c.Character?.Contains("Director", StringComparison.OrdinalIgnoreCase) == true) == true)
                {
                    incomingDirectors = stremio.AppExtras.Cast
                        .Where(c => c.Character?.Contains("Director", StringComparison.OrdinalIgnoreCase) == true)
                        .Take(10)
                        .Select(c => new UnifiedCast
                        {
                            Name = c.Name,
                            Character = "Yönetmen",
                            ProfileUrl = c.Photo
                        }).ToList();
                }
                else if (stremio.Director?.Count > 0)
                {
                    incomingDirectors = stremio.Director.Take(10).Select(name => new UnifiedCast { Name = name, Character = "Yönetmen" }).ToList();
                }

                if (incomingDirectors != null)
                {
                    // [FIX] Deduplicate incoming directors themselves first (AioStreams sometimes repeats)
                    // Prioritize those with profile URLs
                    incomingDirectors = incomingDirectors
                        .OrderByDescending(d => !string.IsNullOrEmpty(d.ProfileUrl))
                        .DistinctBy(d => d.Name?.Trim()?.ToLowerInvariant())
                        .ToList();

                    if (unified.Directors != null && unified.Directors.Count > 0)
                    {
                        // Smart Director Merging: Copy missing URLs from existing to incoming
                        foreach (var d in incomingDirectors)
                        {
                            if (string.IsNullOrEmpty(d.ProfileUrl))
                            {
                                var existing = unified.Directors.FirstOrDefault(ex => 
                                    string.Equals(ex.Name?.Trim(), d.Name?.Trim(), StringComparison.OrdinalIgnoreCase) && 
                                    !string.IsNullOrEmpty(ex.ProfileUrl));
                                if (existing != null) d.ProfileUrl = existing.ProfileUrl;
                            }
                        }

                        // Combine lists, keeping incoming (new source) but adding unique ones from previous
                        var currentNames = incomingDirectors.Select(id => id.Name?.Trim()?.ToLowerInvariant()).ToHashSet();
                        var extras = unified.Directors
                            .Where(ed => !string.IsNullOrEmpty(ed.Name) && !currentNames.Contains(ed.Name.Trim().ToLowerInvariant()))
                            .ToList();
                        
                        incomingDirectors.AddRange(extras);
                    }

                    // Final safety distinct by name
                    unified.Directors = incomingDirectors
                        .OrderByDescending(d => !string.IsNullOrEmpty(d.ProfileUrl))
                        .DistinctBy(d => d.Name?.Trim()?.ToLowerInvariant())
                        .ToList();
                }

                // [FIX] LINK DIRECTOR PHOTO FROM CAST: Some secondary sources (like AioStreams) put photos in app_extras.cast but not in director list.
                // We propagate the photo if the name matches. Search RAW lists to avoid Take(25) truncation issues.
                if (unified.Directors != null)
                {
                    foreach (var d in unified.Directors)
                    {
                        if (string.IsNullOrEmpty(d.ProfileUrl))
                        {
                            // 1. Search in RAW AppExtras.Cast
                            if (stremio.AppExtras?.Cast != null)
                            {
                                var match = stremio.AppExtras.Cast.FirstOrDefault(c => !string.IsNullOrEmpty(c.Photo) && string.Equals(c.Name, d.Name, StringComparison.OrdinalIgnoreCase));
                                if (match != null) d.ProfileUrl = match.Photo;
                            }
                            
                            // 2. Search in RAW CreditsCast
                            if (string.IsNullOrEmpty(d.ProfileUrl) && stremio.CreditsCast != null)
                            {
                                var match = stremio.CreditsCast.FirstOrDefault(c => !string.IsNullOrEmpty(c.ProfilePath) && string.Equals(c.Name, d.Name, StringComparison.OrdinalIgnoreCase));
                                if (match != null) d.ProfileUrl = $"https://image.tmdb.org/t/p/w185{match.ProfilePath}";
                            }
                        }
                    }
                }
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
            
            if (!quiet)
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Mapped data [Primary={overwritePrimary}] for: '{unified.Title}' (Source: {stremio.Name} [{stremio.Id}])");
        }

        private async Task<TmdbMovieResult?> EnrichWithTmdbAsync(UnifiedMetadata metadata, MetadataContext context, bool isContinueWatching = false)
        {
            TmdbMovieResult? tmdb = null;
            
            // [DEBUG] Log the lookup details for series
            System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] {(metadata.IsSeries ? "SERIES" : "MOVIE")}: Title=\"{metadata.Title}\", ImdbId=\"{metadata.ImdbId}\", Year=\"{metadata.Year}\", CW={isContinueWatching}");
            
            // Search TMDB
            // Step 1: Try External ID (IMDb) FIRST - Most reliable and avoids "Untitled..." title mismatches
            if (metadata.IsSeries)
            {
                if (tmdb == null && !string.IsNullOrEmpty(metadata.ImdbId) && metadata.ImdbId.StartsWith("tt"))
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Trying IMDb ID = {metadata.ImdbId}");
                    var searchResult = await TmdbHelper.GetTvByExternalIdAsync(metadata.ImdbId);
                    if (searchResult != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: IMDb lookup found ID = {searchResult.Id}, fetching full details...");
                        tmdb = await TmdbHelper.GetTvByIdAsync(searchResult.Id);
                    }
                }

                // Step 2: Try tmdb: prefix ID
                if (tmdb == null && metadata.ImdbId != null && metadata.ImdbId.StartsWith("tmdb:"))
                {
                    int.TryParse(metadata.ImdbId.Replace("tmdb:", ""), out int tvId);
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Trying tmdb: ID = {tvId}");
                    if (tvId > 0) tmdb = await TmdbHelper.GetTvByIdAsync(tvId);
                }
                
                // Step 3: Try Title Search with Year
                if (tmdb == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Trying title search = \"{metadata.Title}\", year = \"{metadata.Year}\"");
                    var titleSearch = await TmdbHelper.SearchTvAsync(metadata.Title, TitleHelper.ExtractYear(metadata.Year));
                    if (titleSearch != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Title search found ID = {titleSearch.Id}, fetching full details...");
                        tmdb = await TmdbHelper.GetTvByIdAsync(titleSearch.Id);
                    }
                    else if (!string.IsNullOrEmpty(metadata.SubTitle))
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Primary title failed. Trying SubTitle fallback = \"{metadata.SubTitle}\"");
                        var subTitleSearch = await TmdbHelper.SearchTvAsync(metadata.SubTitle, TitleHelper.ExtractYear(metadata.Year));
                        if (subTitleSearch != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: SubTitle search found ID = {subTitleSearch.Id}");
                            tmdb = await TmdbHelper.GetTvByIdAsync(subTitleSearch.Id);
                        }
                    }
                }

                // Step 4: Try Title Search WITHOUT Year
                if (tmdb == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Trying title search WITHOUT YEAR = \"{metadata.Title}\"");
                    var titleSearch = await TmdbHelper.SearchTvAsync(metadata.Title, null);
                    if (titleSearch != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Title search (no year) found ID = {titleSearch.Id}, fetching full details...");
                        tmdb = await TmdbHelper.GetTvByIdAsync(titleSearch.Id);
                    }
                }
            }
            else // MOVIE
            {
                // Try IMDb ID first
                if (tmdb == null && metadata.ImdbId != null && metadata.ImdbId.StartsWith("tt"))
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: Trying IMDb ID = {metadata.ImdbId}");
                    var extResult = await TmdbHelper.GetMovieByExternalIdAsync(metadata.ImdbId);
                    if (extResult != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: IMDb lookup found ID = {extResult.Id}, fetching full details...");
                        tmdb = await TmdbHelper.GetMovieByIdAsync(extResult.Id);
                    }
                }

                if (tmdb == null && metadata.ImdbId != null && metadata.ImdbId.StartsWith("tmdb:"))
                {
                    int.TryParse(metadata.ImdbId.Replace("tmdb:", ""), out int movieId);
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: Trying tmdb: ID = {movieId}");
                    if (movieId > 0) tmdb = await TmdbHelper.GetMovieByIdAsync(movieId);
                }

                if (tmdb == null)
                {
                    var searchResult = await TmdbHelper.SearchMovieAsync(metadata.Title, TitleHelper.ExtractYear(metadata.Year));
                    if (searchResult != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: Search found ID = {searchResult.Id}, fetching full details...");
                        tmdb = await TmdbHelper.GetMovieByIdAsync(searchResult.Id);
                    }
                    else if (!string.IsNullOrEmpty(metadata.SubTitle))
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: Primary title failed. Trying SubTitle fallback = \"{metadata.SubTitle}\"");
                        var subSearchResult = await TmdbHelper.SearchMovieAsync(metadata.SubTitle, TitleHelper.ExtractYear(metadata.Year));
                        if (subSearchResult != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: SubTitle Search found ID = {subSearchResult.Id}");
                            tmdb = await TmdbHelper.GetMovieByIdAsync(subSearchResult.Id);
                        }
                    }
                }
            }

            if (tmdb != null)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SUCCESS: Found TMDB ID = {tmdb.Id}, Title = {tmdb.DisplayTitle}");
                metadata.DataSource = "Stremio + TMDB";

                // [FIX] Store the resolved canonical ID if we don't have one yet. Favor IMDb, fallback to tmdb:ID.
                // This allows subsequent addon probing to use a real ID even for IPTV items.
                string? resolvedId = tmdb.ResolvedImdbId;
                if (!IsImdbId(resolvedId)) resolvedId = $"tmdb:{tmdb.Id}";

                if (IsCanonicalId(resolvedId) && !IsCanonicalId(metadata.ImdbId))
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] Propagating Resolved ID: {resolvedId}");
                    metadata.ImdbId = resolvedId;
                }
                
                // Prioritize localized TMDB data
                if (!string.IsNullOrEmpty(tmdb.Overview)) metadata.Overview = tmdb.Overview;
                if (!string.IsNullOrEmpty(tmdb.DisplayTitle) && tmdb.DisplayTitle != metadata.Title) 
                {
                    // [NEW] Move previous title to SubTitle if it was valid/meaningful
                    if (!string.IsNullOrWhiteSpace(metadata.Title) && 
                        metadata.Title != metadata.MetadataId && 
                        !IsGenericEpisodeTitle(metadata.Title, tmdb.DisplayTitle))
                    {
                        metadata.SubTitle = metadata.Title;
                    }
                    metadata.Title = tmdb.DisplayTitle;
                }

                if (!string.IsNullOrEmpty(tmdb.DisplayOriginalTitle))
                    metadata.OriginalTitle = tmdb.DisplayOriginalTitle;

                // Higher quality images
                if (!string.IsNullOrEmpty(tmdb.FullBackdropUrl)) metadata.BackdropUrl = tmdb.FullBackdropUrl;

                // [TRAILER LOCALIZATION] Try fetching localized trailer key
                if (AppSettings.IsTmdbEnabled && !string.IsNullOrEmpty(AppSettings.TmdbApiKey))
                {
                    // If we have a trailer from Stremio, it might be English. 
                    // We attempt to get a localized one from TMDB.
                    var localizedTrailerKey = await TmdbHelper.GetTrailerKeyAsync(tmdb.Id, metadata.IsSeries);
                    if (!string.IsNullOrEmpty(localizedTrailerKey))
                    {
                         string fullTrailerUrl = localizedTrailerKey;
                         if (!fullTrailerUrl.Contains("/") && !fullTrailerUrl.Contains("."))
                             fullTrailerUrl = $"https://www.youtube.com/watch?v={localizedTrailerKey}";
                         
                         // We overwrite if existing is null OR if it was already a YT link (likely from another source)
                         // to ensure we get the localized version from TMDB.
                         if (string.IsNullOrEmpty(metadata.TrailerUrl) || metadata.TrailerUrl.Contains("youtube.com"))
                         {
                             System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] Overwriting Trailer: {metadata.TrailerUrl} -> {fullTrailerUrl}");
                             metadata.TrailerUrl = fullTrailerUrl;
                         }
                         else
                         {
                             System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SKIPPED Trailer overwrite. Current: {metadata.TrailerUrl}, TMDB: {fullTrailerUrl}");
                         }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] No trailer found for TMDB ID: {tmdb.Id}");
                    }
                }

                // [LOGO LOCALIZATION] Select best logo based on language preference
                string? tmdbLogo = SelectBestLogo(tmdb);
                if (!string.IsNullOrEmpty(tmdbLogo))
                {
                    // Check if existing logo is better quality (e.g. from fanart.tv) or if we should override with TMDB localized logo
                    int existingScore = GetLogoScore(metadata.LogoUrl);
                    int tmdbScore = 8; // TMDB score

                    // If existing logo is just a metahub logo or missing, or if it's not localized, prefer TMDB
                    if (existingScore <= 4 || string.IsNullOrEmpty(metadata.LogoUrl))
                    {
                        metadata.LogoUrl = tmdbLogo;
                    }
                }
                
                // Update genres if needed
                var tmdbGenres = tmdb.GetGenreNames();
                if (tmdbGenres != "Genel") metadata.Genres = tmdbGenres;
                
                metadata.TmdbInfo = tmdb;

                // [NEW] For series, fetch season details from TMDB to get episodes
                // [OPTIMIZATION] Only fetch seasons for Detail view OR for ExpandedCard IF user has already started watching (history exists)
                bool hasHistory = !string.IsNullOrEmpty(metadata.ImdbId) && HistoryManager.Instance.GetLastWatchedEpisode(metadata.ImdbId) != null;
                bool shouldFetchSeasons = context == MetadataContext.Detail || (context == MetadataContext.ExpandedCard && (isContinueWatching || hasHistory));
                if (metadata.IsSeries && shouldFetchSeasons && tmdb != null && tmdb.Seasons != null && tmdb.Seasons.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] Fetching season details for {tmdb.Seasons.Count} TMDB seasons (Context: {context})...");
                    
                    var validSeasons = tmdb.Seasons.Where(s => s.SeasonNumber > 0).ToList();
                    var seasonTasks = validSeasons.Select(async tmdbSeason => 
                    {
                        try {
                            var seasonDetails = await TmdbHelper.GetSeasonDetailsAsync(tmdb.Id, tmdbSeason.SeasonNumber);
                            if (seasonDetails?.Episodes != null && seasonDetails.Episodes.Count > 0)
                            {
                                bool areTitlesGeneric = seasonDetails.Episodes.Any(e => IsGenericEpisodeTitle(e.Name, metadata.Title));
                                bool areOverviewsMissing = seasonDetails.Episodes.Any(e => string.IsNullOrEmpty(e.Overview));
                                
                                TmdbSeasonDetails? enSeasonDetails = null;
                                if (areTitlesGeneric || areOverviewsMissing)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] Season {tmdbSeason.SeasonNumber}: Missing details (TitlesGeneric={areTitlesGeneric}, OverviewsMissing={areOverviewsMissing}). Fetching English fallback...");
                                    enSeasonDetails = await TmdbHelper.GetSeasonDetailsAsync(tmdb.Id, tmdbSeason.SeasonNumber, "en-US");
                                }
                                return new { tmdbSeason, seasonDetails, enSeasonDetails };
                            }
                        } catch (Exception ex) {
                            System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] Error fetching season {tmdbSeason.SeasonNumber}: {ex.Message}");
                        }
                        return null;
                    }).ToList();

                    var seasonResults = (await Task.WhenAll(seasonTasks)).Where(r => r != null).ToList();

                    foreach (var res in seasonResults)
                    {
                        var tmdbSeason = res.tmdbSeason;
                        var seasonDetails = res.seasonDetails;
                        var enSeasonDetails = res.enSeasonDetails;

                        var existingSeason = metadata.Seasons.FirstOrDefault(s => s.SeasonNumber == tmdbSeason.SeasonNumber);
                        if (existingSeason == null)
                        {
                            existingSeason = new UnifiedSeason { SeasonNumber = tmdbSeason.SeasonNumber };
                            metadata.Seasons.Add(existingSeason);
                        }

                        // If addon already has episodes, merge: use TMDB titles/descriptions but keep addon stream URLs
                        bool hasExistingEpisodes = existingSeason.Episodes != null && existingSeason.Episodes.Count > 0;

                        if (hasExistingEpisodes)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] Season {tmdbSeason.SeasonNumber}: Merging {seasonDetails.Episodes.Count} TMDB episodes with {existingSeason.Episodes.Count} addon episodes");
                            // For each TMDB episode, update title/overview but keep addon stream URL if available
                            foreach (var tmdbEp in seasonDetails.Episodes)
                            {
                                var existingEp = existingSeason.Episodes.FirstOrDefault(e => e.EpisodeNumber == tmdbEp.EpisodeNumber);
                                
                                // [FIX] Pick the best title and overview (localized with English fallback)
                                string? bestName = tmdbEp.Name;
                                string? bestOverview = tmdbEp.Overview;

                                if (IsGenericEpisodeTitle(bestName, metadata.Title) || string.IsNullOrEmpty(bestOverview))
                                {
                                    var enEp = enSeasonDetails?.Episodes?.FirstOrDefault(e => e.EpisodeNumber == tmdbEp.EpisodeNumber);
                                    if (enEp != null)
                                    {
                                        if (IsGenericEpisodeTitle(bestName, metadata.Title) && !IsGenericEpisodeTitle(enEp.Name, metadata.Title))
                                            bestName = enEp.Name;
                                        if (string.IsNullOrEmpty(bestOverview))
                                            bestOverview = enEp.Overview;
                                    }
                                }

                                if (existingEp != null)
                                {
                                    // Update existing episode metadata while preserving stream URLs
                                    bool isNewNameGeneric = IsGenericEpisodeTitle(bestName, metadata.Title);
                                    bool isExistingNameGeneric = IsGenericEpisodeTitle(existingEp.Title, metadata.Title);

                                    if (!string.IsNullOrEmpty(bestName) && (isExistingNameGeneric || !isNewNameGeneric))
                                        existingEp.Title = bestName;

                                    if (!string.IsNullOrEmpty(bestOverview))
                                        existingEp.Overview = bestOverview;

                                    if (!string.IsNullOrEmpty(tmdbEp.StillPath))
                                        existingEp.ThumbnailUrl = $"https://image.tmdb.org/t/p/w300{tmdbEp.StillPath}";
                                }
                                else
                                {
                                    // Create and add new episode metadata
                                    string episodeId = !string.IsNullOrEmpty(metadata.ImdbId) 
                                        ? $"{metadata.ImdbId}:{tmdbSeason.SeasonNumber}:{tmdbEp.EpisodeNumber}" 
                                        : $"tmdb:{tmdb.Id}:{tmdbSeason.SeasonNumber}:{tmdbEp.EpisodeNumber}";

                                    existingSeason.Episodes.Add(new UnifiedEpisode // Changed from EpisodeItem to UnifiedEpisode
                                    {
                                        Id = episodeId,
                                        SeasonNumber = tmdbSeason.SeasonNumber,
                                        EpisodeNumber = tmdbEp.EpisodeNumber,
                                        Title = bestName ?? $"Episode {tmdbEp.EpisodeNumber}",
                                        Overview = bestOverview,
                                        ThumbnailUrl = !string.IsNullOrEmpty(tmdbEp.StillPath) ? $"https://image.tmdb.org/t/p/w300{tmdbEp.StillPath}" : null,
                                        AirDate = tmdbEp.AirDateDateTime
                                    });
                                }
                            }
                        }
                        else
                        {
                            // No existing episodes - add all from TMDB
                            existingSeason.Episodes.Clear();
                            foreach (var ep in seasonDetails.Episodes)
                            {
                                string episodeId = !string.IsNullOrEmpty(tmdb.ImdbId) 
                                    ? $"{tmdb.ImdbId}:{tmdbSeason.SeasonNumber}:{ep.EpisodeNumber}" 
                                    : $"tmdb:{tmdb.Id}:{tmdbSeason.SeasonNumber}:{ep.EpisodeNumber}";
                                
                                // [FIX] Best name and overview for new episodes too
                                string? bestName = ep.Name;
                                string? bestOverview = ep.Overview;

                                if (IsGenericEpisodeTitle(bestName, metadata.Title) || string.IsNullOrEmpty(bestOverview))
                                {
                                    var enEp = enSeasonDetails?.Episodes?.FirstOrDefault(e => e.EpisodeNumber == ep.EpisodeNumber);
                                    if (enEp != null)
                                    {
                                        if (IsGenericEpisodeTitle(bestName, metadata.Title) && !IsGenericEpisodeTitle(enEp.Name, metadata.Title))
                                            bestName = enEp.Name;
                                        if (string.IsNullOrEmpty(bestOverview))
                                            bestOverview = enEp.Overview;
                                    }
                                }

                                existingSeason.Episodes.Add(new UnifiedEpisode
                                {
                                    Id = episodeId,
                                    SeasonNumber = tmdbSeason.SeasonNumber,
                                    EpisodeNumber = ep.EpisodeNumber,
                                    Title = bestName ?? (ep.Name ?? $"{ep.EpisodeNumber}. Bölüm"),
                                    Overview = bestOverview,
                                    ThumbnailUrl = !string.IsNullOrEmpty(ep.StillPath) ? $"https://image.tmdb.org/t/p/w300{ep.StillPath}" : null,
                                    AirDate = ep.AirDateDateTime
                                });
                            }
                        }
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] Season {tmdbSeason.SeasonNumber}: Processed {seasonDetails.Episodes.Count} episodes");
                    }
                }

                // Propagate IMDb ID if found to help addon lookup
                if (!string.IsNullOrEmpty(tmdb.ImdbId) && (string.IsNullOrEmpty(metadata.ImdbId) || metadata.ImdbId.StartsWith("tmdb:")))
                {
                    System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Resolved IMDb ID from TMDB: {tmdb.ImdbId}");
                    metadata.ImdbId = tmdb.ImdbId;
                }
            }
            
            if (tmdb == null)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] FAILED: No TMDB match found for Title=\"{metadata.Title}\", ImdbId=\"{metadata.ImdbId}\"");
            }
            
            return tmdb;
        }

        private int MergeStremioEpisodes(ModernIPTVPlayer.Models.Stremio.StremioMeta stremio, UnifiedMetadata metadata, string source, bool overwrite = false)
        {
            if (stremio?.Videos == null) return 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int added = 0;
            int updated = 0;

            lock (metadata.SyncRoot)
            {
                // Pre-create dictionaries for O(1) lookups, but handle duplicates safely
                // Some addons return the same season/episode multiple times
                var seasonMap = new Dictionary<int, UnifiedSeason>();
                foreach (var s in metadata.Seasons)
                {
                    if (s != null) seasonMap[s.SeasonNumber] = s;
                }

                var grouped = stremio.Videos
                    .Where(v => v.Season >= 0)
                    .GroupBy(v => v.Season);

                foreach (var group in grouped)
                {
                    int sNum = group.Key;
                    if (!seasonMap.TryGetValue(sNum, out var season))
                    {
                        season = new UnifiedSeason
                        {
                            SeasonNumber = sNum,
                            Name = sNum == 0 ? "Özel Bölümler" : $"{sNum}. Sezon"
                        };
                        metadata.Seasons.Add(season);
                        seasonMap[sNum] = season;
                    }

                    var episodeMap = new Dictionary<int, UnifiedEpisode>();
                    foreach (var e in season.Episodes)
                    {
                        if (e != null) episodeMap[e.EpisodeNumber] = e;
                    }

                    foreach (var vid in group.OrderBy(v => v.Episode))
                    {
                        if (episodeMap.TryGetValue(vid.Episode, out var existingEpisode))
                        {
                            // Update missing fields OR override if overwrite is true
                            bool isGenericTitle = IsGenericEpisodeTitle(existingEpisode.Title, metadata.Title);
                            bool isGenericOverview = IsPlaceholderOverview(existingEpisode.Overview);

                            if (overwrite || isGenericTitle)
                            {
                                string newTitle = !string.IsNullOrEmpty(vid.Name) ? vid.Name : vid.Title;
                                bool isNewGeneric = IsGenericEpisodeTitle(newTitle, metadata.Title);

                                // NEVER replace a real title with a generic one, even if overwrite is true.
                                if ((overwrite && !isNewGeneric) || (!isNewGeneric && isGenericTitle) || string.IsNullOrEmpty(existingEpisode.Title))
                                {
                                    existingEpisode.Title = newTitle;
                                }
                            }

                            if (overwrite || isGenericOverview || string.IsNullOrEmpty(existingEpisode.Overview))
                            {
                                string newOverview = !string.IsNullOrEmpty(vid.Overview) ? vid.Overview : vid.Description;
                                bool isNewGeneric = IsPlaceholderOverview(newOverview);

                                // NEVER replace a real overview with a placeholder one, even if overwrite is true.
                                if ((overwrite && !isNewGeneric) || (!isNewGeneric && isGenericOverview) || string.IsNullOrEmpty(existingEpisode.Overview))
                                    existingEpisode.Overview = newOverview;
                            }

                            if (overwrite || string.IsNullOrEmpty(existingEpisode.ThumbnailUrl))
                            {
                                if (!string.IsNullOrEmpty(vid.Thumbnail))
                                    existingEpisode.ThumbnailUrl = vid.Thumbnail;
                            }

                            if ((overwrite || existingEpisode.AirDate == null) && !string.IsNullOrEmpty(vid.Released) && DateTime.TryParse(vid.Released, out var d2))
                                existingEpisode.AirDate = d2;

                            // [NEW] Map episode availability and runtime from AIOMetadata
                            if (overwrite || !existingEpisode.IsAvailable)
                                existingEpisode.IsAvailable = vid.Available;
                            if (overwrite || string.IsNullOrEmpty(existingEpisode.Runtime))
                                existingEpisode.Runtime = vid.Runtime;
                            if (overwrite || existingEpisode.ReleaseDate == null)
                                existingEpisode.ReleaseDate = existingEpisode.AirDate;

                            updated++;
                        }
                        else
                        {
                            var newEp = new UnifiedEpisode
                            {
                                Id = vid.Id,
                                SeasonNumber = sNum,
                                EpisodeNumber = vid.Episode,
                                Title = !string.IsNullOrEmpty(vid.Title) ? vid.Title : (!string.IsNullOrEmpty(vid.Name) ? vid.Name : $"{vid.Episode}. Bölüm"),
                                Overview = !string.IsNullOrEmpty(vid.Overview) ? vid.Overview : vid.Description,
                                ThumbnailUrl = vid.Thumbnail,
                                AirDate = !string.IsNullOrEmpty(vid.Released) && DateTime.TryParse(vid.Released, out var d3) ? d3 : null,
                                ReleaseDate = !string.IsNullOrEmpty(vid.Released) && DateTime.TryParse(vid.Released, out var d4) ? d4 : null,
                                IsAvailable = vid.Available,
                                Runtime = vid.Runtime
                            };
                            season.Episodes.Add(newEp);
                            episodeMap[vid.Episode] = newEp;
                            added++;
                        }
                    }
                }
            }

            if (added > 0 || updated > 0)
            {
                sw.Stop();
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] MergeStremioEpisodes: Added {added}, Updated {updated} in {sw.ElapsedMilliseconds}ms");
            }

            return added + updated;
        }

        public static bool IsGenericEpisodeTitle(string title, string seriesTitle = null)
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
            // [FIX] Turkish characters handling for generic title check
            string lettersOnly = new string(t.Replace("ı", "i").Replace("ö", "o").Replace("ü", "u").Replace("ş", "s").Replace("ç", "c").Replace("ğ", "g")
                .Where(c => char.IsLetter(c)).ToArray());
            
            if (string.IsNullOrEmpty(lettersOnly)) return true; // empty like "1.0", "1-2"

            bool isGeneric = lettersOnly == "bolum" || 
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

            return isGeneric;
        }

        public bool IsPlaceholderOverview(string? overview)
        {
            if (string.IsNullOrWhiteSpace(overview)) return true;
            if (overview == "Açıklama mevcut değil.") return true;
            if (overview == "Açıklama mevcut değil") return true;
            if (overview.Contains("açıklama mevcut değil", StringComparison.OrdinalIgnoreCase)) return true;
            if (overview.Contains("description not available", StringComparison.OrdinalIgnoreCase)) return true;
            
            string o = overview.ToLowerInvariant();
            
            // Patterns like "Full Title: ... Size: 3GB" or technical filenames
            if (o.Contains("full title:") || o.Contains("size:") || o.Contains("3gb") || o.Contains("1080p") || o.Contains("x264"))
                return true;
            
            // If it's just a repeat of a generic title pattern
            if (o.Length < 30 && IsGenericEpisodeTitle(o, null))
                return true;

            // Very short overviews that look like filenames or technical tags
            if (overview.Length < 10)
            {
                if (overview.Contains(".") || overview.Contains("-")) return true;
            }

            return false;
        }

        private bool HasGenericEpisodeTitles(UnifiedMetadata metadata)
        {
            if (!metadata.IsSeries || metadata.Seasons == null || metadata.Seasons.Count == 0) return false;
            
            // Check ANY non-specials season. If ANY full season has only generic episode titles,
            // we should consider it as having generic titles and keep probing for better data
            // (e.g., Season 1 might be properly named, but Season 2 might be just "1. Bölüm").
            var normalSeasons = metadata.Seasons.Where(s => s.SeasonNumber > 0).ToList();
            if (normalSeasons.Count == 0) return false;

            return normalSeasons.Any(s => s.Episodes != null && s.Episodes.Count > 0 && s.Episodes.All(e => IsGenericEpisodeTitle(e.Title, metadata.Title)));
        }

        private void ReconcileTmdbSeasons(UnifiedMetadata metadata, TmdbMovieResult tmdb)
        {
            // Reconcile with TMDB Seasons (if available) - Adds seasons/episodes Stremio might be missing
            if (tmdb?.Seasons != null)
            {
                foreach (var tmdbSeason in tmdb.Seasons)
                {
                    // Filter out placeholder seasons with 0 episodes (common for future seasons not yet populated)
                    if (tmdbSeason.EpisodeCount == 0 && tmdbSeason.SeasonNumber > 1) continue;

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
        }

        private void SortSeasons(UnifiedMetadata metadata)
        {
            if (metadata.Seasons == null || metadata.Seasons.Count <= 1) return;

            // Ensure correct order: 1..N, then 0 (Specials)
            metadata.Seasons = metadata.Seasons
                .OrderBy(s => s.SeasonNumber == 0 ? int.MaxValue : s.SeasonNumber)
                .ToList();
        }

        private void AddUniqueBackdrop(UnifiedMetadata metadata, string url, Action<string> onBackdropFound = null)
        {
            if (string.IsNullOrEmpty(url) || ImageHelper.IsPlaceholder(url)) return;
            
            lock (metadata.SyncRoot)
            {
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
                    }
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

        public static string UpgradeImageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;

            if (url.Contains("metahub.space/"))
            {
                if (url.Contains("/medium/") || url.Contains("/small/"))
                {
                    return url.Replace("/medium/", "/large/").Replace("/small/", "/large/");
                }
            }

            return url;
        }

        private int GetQualityScore(string url)
        {
            if (string.IsNullOrEmpty(url)) return 0;
            if (url.Contains("/original/")) return 10;
            if (url.Contains("/large/")) return 9;
            if (url.Contains("/w1280/")) return 8;
            if (url.Contains("/w780/")) return 6;
            if (url.Contains("/w500/")) return 4;
            if (url.Contains("/medium/")) return 3;
            if (url.Contains("/small/")) return 2;
            return 1;
        }

        public static bool IsImdbId(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            // IMDb IDs start with 'tt' followed by numbers
            return Regex.IsMatch(id, @"^tt\d+$", RegexOptions.IgnoreCase);
        }


        public static bool IsCanonicalId(string? id)
        {
            return IsImdbId(id) || (!string.IsNullOrWhiteSpace(id) && id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase));
        }

        private bool LooksLikeTitleFallback(string? id, string? title)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return false;
            return string.Equals(id.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private string? ExtractImdbId(Models.Stremio.StremioMeta meta)
        {
            if (IsImdbId(meta.ImdbId)) return meta.ImdbId;
            if (IsImdbId(meta.Id)) return meta.Id;

            // Normalize wrapped formats (e.g. imdb_id:tt12345)
            string? normalizedImdb = NormalizeId(meta.ImdbId);
            if (IsImdbId(normalizedImdb)) return normalizedImdb;

            string? normalizedId = NormalizeId(meta.Id);
            if (IsImdbId(normalizedId)) return normalizedId;

            // Check website field
            if (!string.IsNullOrEmpty(meta.Website))
            {
                var match = Regex.Match(meta.Website, @"tt\d+", RegexOptions.IgnoreCase);
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
                        var match = Regex.Match(link.Url, @"tt\d+", RegexOptions.IgnoreCase);
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
            var imdbMatch = Regex.Match(id, @"tt\d+", RegexOptions.IgnoreCase);
            if (imdbMatch.Success) return imdbMatch.Value.ToLowerInvariant();

            var tmdbMatch = Regex.Match(id, @"tmdb:\d+", RegexOptions.IgnoreCase);
            if (tmdbMatch.Success) return tmdbMatch.Value.ToLowerInvariant();
            
            return id;
        }

        private string GetAddonOrderHash()
        {
            var addons = StremioAddonManager.Instance.GetAddons();
            if (addons == null || addons.Count == 0) return "none";

            // Simple combined string hash is sufficient for short-term result cache keying
            string combined = string.Join("|", addons);
            return combined.GetHashCode().ToString("X");
        }

        private string? SelectBestLogo(TmdbMovieResult tmdb)
        {
            if (tmdb?.Images?.Logos == null || tmdb.Images.Logos.Count == 0) return null;
            
            var preferred = AppSettings.TmdbLanguage.Split('-')[0];
            
            // 1. Try preferred language
            var logo = tmdb.Images.Logos.FirstOrDefault(l => l.Iso639_1 == preferred);
            
            // 2. Try English
            if (logo == null) logo = tmdb.Images.Logos.FirstOrDefault(l => l.Iso639_1 == "en");
            
            // 3. Try language-neutral (null or empty)
            if (logo == null) logo = tmdb.Images.Logos.FirstOrDefault(l => string.IsNullOrEmpty(l.Iso639_1));
            
            // 4. Fallback to first available
            if (logo == null) logo = tmdb.Images.Logos[0];
            
            return logo != null ? TmdbHelper.GetImageUrl(logo.FilePath, "original") : null;
        }

        private int GetLogoScore(string? url)
        {
            if (string.IsNullOrEmpty(url)) return 0;
            string u = url.ToLowerInvariant();
            if (u.Contains("fanart.tv") || u.Contains("fanart-tv")) return 10;
            if (u.Contains("tmdb.org") || u.Contains("themoviedb.org")) return 8;
            if (u.Contains("metahub.space/logo/")) return 4;
            return 1;
        }
    }
}

