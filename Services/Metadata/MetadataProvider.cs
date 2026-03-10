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
using ModernIPTVPlayer;

namespace ModernIPTVPlayer.Services.Metadata
{
    public enum MetadataContext { Discovery, Detail }

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
        OriginalTitle = 1 << 12
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

        private readonly ConcurrentDictionary<string, Task<UnifiedMetadata>> _activeTasks = new();
        private readonly ConcurrentDictionary<string, (UnifiedMetadata Data, DateTime Expiry)> _resultCache = new();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(2); // Short term cache
        private readonly ConcurrentDictionary<string, Task<AddonMetaCacheEntry>> _activeAddonMetaTasks = new();
        private readonly ConcurrentDictionary<string, (AddonMetaCacheEntry Data, DateTime Expiry)> _addonMetaCache = new();
        private readonly ConcurrentDictionary<string, string> _rawToCanonicalIdCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _addonMetaPositiveCacheDuration = TimeSpan.FromMinutes(20);
        private readonly TimeSpan _addonMetaNegativeCacheDuration = TimeSpan.FromMinutes(3);

        public async Task<UnifiedMetadata> GetMetadataAsync(Models.IMediaStream stream, MetadataContext context = MetadataContext.Detail, Action<string> onBackdropFound = null)
        {
            if (stream == null) return null;
            
            string rawId = stream.IMDbId;
            string id = ResolveBestInitialId(stream) ?? NormalizeId(rawId) ?? rawId;
            var trace = new MetadataTrace(context.ToString(), id, stream?.Title);

            if (!string.IsNullOrWhiteSpace(id) && id != rawId)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Canonical ID resolved at entry: {rawId} -> {id}");
            }

            // 0. Global Check: composite/non-standard IDs should not abort metadata flow.
            // We fall back to title-based keying so catalog-seed + trace logging still works.
            bool isCanonical = IsImdbId(id) || (!string.IsNullOrWhiteSpace(id) && id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase));
            if (!isCanonical && !string.IsNullOrWhiteSpace(rawId) && _rawToCanonicalIdCache.TryGetValue(rawId, out var cachedCanonical))
            {
                id = cachedCanonical;
                isCanonical = true;
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Canonical ID restored from raw-cache: {rawId} -> {cachedCanonical}");
            }
            if (!isCanonical && rawId != null && (rawId.Contains(",") || rawId.Contains(" ") || rawId.Length > 100))
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Non-standard ID detected, using title fallback. RawId: {rawId}");
                id = null;
            }

            string type = (stream is ModernIPTVPlayer.SeriesStream || (stream is Models.Stremio.StremioMediaStream sms_entry && (sms_entry.Meta.Type == "series" || sms_entry.Meta.Type == "tv"))) ? "series" : "movie";
            string fetchId = id ?? rawId ?? stream.Title;
            
            string cacheKey = $"{id ?? stream.Title}_{type}";
            if (context == MetadataContext.Discovery) cacheKey += "_discovery";

            // 1. Check Result Cache
            if (_resultCache.TryGetValue(cacheKey, out var cached) && DateTime.Now < cached.Expiry)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] CACHE HIT: {cacheKey}");

                // [FIX] CROSS-CATALOG TITLE SEEDING:
                // Even on cache hit, if we entered from a different catalog, we should seed its title!
                if (stream is StremioMediaStream stremioStream)
                {
                    SeedFromCatalogMetadata(cached.Data, stremioStream, trace);
                }

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
                    return await _activeTasks.GetOrAdd(cacheKey, _ => GetMetadataInternalAsync(fetchId, type, stream, cacheKey, context, onBackdropFound, disc.Data));
                }
            }

            // 2. Check Active Tasks (Deduplication)
            System.Diagnostics.Debug.WriteLine($"[MetadataProvider] START Fetching ({context}): {id ?? stream.Title} | Type: {type}");
            return await _activeTasks.GetOrAdd(cacheKey, _ => GetMetadataInternalAsync(fetchId, type, stream, cacheKey, context, onBackdropFound));
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
                    }
                    
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
            var trace = new MetadataTrace(context.ToString(), id, sourceStream?.Title);
            var metadata = seed ?? new UnifiedMetadata
            {
                ImdbId = id,
                IsSeries = type == "series" || type == "tv",
                MetadataId = id,
                Title = sourceStream?.Title ?? id
            };

            try
            {
                if (sourceStream is StremioMediaStream stremioStream)
                {
                    SeedFromCatalogMetadata(metadata, stremioStream, trace);
                }

                trace.UpdateTitle(metadata.Title);
                MetadataField required = GetRequiredFields(context);
                MetadataField missing = GetMissingFields(metadata, required);
                trace.Log("Seed", $"Required={required} Missing={missing}");

                bool tmdbEnabled = AppSettings.IsTmdbEnabled && !string.IsNullOrWhiteSpace(AppSettings.TmdbApiKey);
                if (tmdbEnabled)
                {
                    trace.Log("TMDB", "TMDB is enabled. Trying direct enrichment and skipping addon detail chain on success.");
                    var tmdb = await EnrichWithTmdbAsync(metadata);
                    if (tmdb != null)
                    {
                        metadata.TmdbInfo = tmdb;
                        metadata.MetadataSourceInfo = "TMDB (Primary)";
                        metadata.DataSource = string.IsNullOrEmpty(metadata.DataSource) ? "TMDB" : $"{metadata.DataSource} + TMDB";

                        var additionalBackdrops = metadata.IsSeries
                            ? await TmdbHelper.GetTvImagesAsync(tmdb.Id.ToString())
                            : await TmdbHelper.GetMovieImagesAsync(tmdb.Id.ToString());

                        foreach (var bg in additionalBackdrops)
                        {
                            AddUniqueBackdrop(metadata, bg, onBackdropFound);
                        }

                        missing = GetMissingFields(metadata, required);
                        trace.Log("TMDB", $"TMDB enriched. RemainingMissing={missing}");
                    }
                    else
                    {
                        trace.Log("TMDB", "TMDB enrichment failed. Falling back to addon chain.");
                    }
                }
                else
                {
                    trace.Log("TMDB", "TMDB disabled or API key missing.");
                }

                if (tmdbEnabled)
                {
                    if (metadata.TmdbInfo != null)
                    {
                        trace.Log("Decision", "TMDB present. Skipping addon detail probes.");
                    }
                    else
                    {
                        trace.Log("Decision", "TMDB enabled but result unavailable. Addon detail probes are disabled by policy.");
                    }
                }
                else
                {
                    var addonUrls = StremioAddonManager.Instance.GetAddons();
                    if (addonUrls.Count > 0)
                    {
                        int sourcePriority = GetAddonPriorityIndex(addonUrls, metadata.CatalogSourceAddonUrl);
                        int primaryPriority = sourcePriority >= 0 ? sourcePriority : int.MaxValue;
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
                                    // [OPTIMIZATION] If we are satisfied, but a higher priority addon'S DATA IS ALREADY IN CACHED, 
                                    // apply it for consistency across different catalogs.
                                    var nextCacheKey = $"{url}|{type}|{currentSearchId}";
                                    if (index <= primaryPriority && _addonMetaCache.ContainsKey(nextCacheKey))
                                    {
                                        trace.Log("Addon", $"Satisfied but higher priority addon {GetHostSafe(url)} is in cache. Applying for consistency.");
                                    }
                                    else
                                    {
                                        trace.Log("Addon", "Missing field kalmadi. Probe zinciri tamamlandi.");
                                        break;
                                    }
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
                                    trace.Log("ID", $"Upgraded search id to canonical IMDb: {discoveredImdbId}");
                                }

                                bool overwritePrimary = index <= primaryPriority;
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

                                if (metadata.IsSeries)
                                {
                                    MergeStremioEpisodes(entry.Meta, metadata, overwritePrimary);
                                }
                            }
                        }
                    }
                }

                if (metadata.IsSeries && metadata.TmdbInfo != null)
                {
                    ReconcileTmdbSeasons(metadata, metadata.TmdbInfo);
                }

                bool needsIptvEnrichment = string.IsNullOrEmpty(metadata.Overview) || metadata.Overview == "Açıklama mevcut değil." || !IsImdbId(metadata.ImdbId);
                if (needsIptvEnrichment)
                {
                    trace.Log("IPTV", "Trying IPTV enrichment due to missing overview or non-imdb id.");
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
                trace.Log("Error", ex.Message);
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] CRITICAL ERROR: {ex.Message}");
            }

            var finalMissing = GetMissingFields(metadata, GetRequiredFields(context));
            trace.Log("Finish", $"DataSource={metadata.DataSource} Source={metadata.MetadataSourceInfo} RemainingMissing={finalMissing}");
            return metadata;
        }

        private MetadataField GetRequiredFields(MetadataContext context)
        {
            if (context == MetadataContext.Discovery)
            {
                return MetadataField.Title | MetadataField.Overview | MetadataField.Year | MetadataField.Rating | MetadataField.Backdrop;
            }

            return MetadataField.Title | MetadataField.Overview | MetadataField.Year | MetadataField.Rating | MetadataField.Backdrop | MetadataField.Trailer;
        }

        private MetadataField GetMissingFields(UnifiedMetadata metadata, MetadataField required)
        {
            MetadataField missing = MetadataField.None;
            if (required.HasFlag(MetadataField.Title) && string.IsNullOrWhiteSpace(metadata.Title)) missing |= MetadataField.Title;
            if (required.HasFlag(MetadataField.Overview) && string.IsNullOrWhiteSpace(metadata.Overview)) missing |= MetadataField.Overview;
            if (required.HasFlag(MetadataField.Year) && string.IsNullOrWhiteSpace(metadata.Year)) missing |= MetadataField.Year;
            if (required.HasFlag(MetadataField.Rating) && metadata.Rating <= 0) missing |= MetadataField.Rating;
            if (required.HasFlag(MetadataField.Genres) && string.IsNullOrWhiteSpace(metadata.Genres)) missing |= MetadataField.Genres;
            if (required.HasFlag(MetadataField.Poster) && string.IsNullOrWhiteSpace(metadata.PosterUrl)) missing |= MetadataField.Poster;
            if (required.HasFlag(MetadataField.Backdrop) && string.IsNullOrWhiteSpace(metadata.BackdropUrl)) missing |= MetadataField.Backdrop;
            if (required.HasFlag(MetadataField.Trailer) && string.IsNullOrWhiteSpace(metadata.TrailerUrl)) missing |= MetadataField.Trailer;
            if (required.HasFlag(MetadataField.Runtime) && string.IsNullOrWhiteSpace(metadata.Runtime)) missing |= MetadataField.Runtime;
            if (required.HasFlag(MetadataField.Logo) && string.IsNullOrWhiteSpace(metadata.LogoUrl)) missing |= MetadataField.Logo;
            return missing;
        }

        private int GetAddonPriorityIndex(List<string> addonUrls, string? addonUrl)
        {
            if (string.IsNullOrWhiteSpace(addonUrl)) return -1;
            return addonUrls.FindIndex(a => string.Equals(a?.TrimEnd('/'), addonUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        }

        private void SeedFromCatalogMetadata(UnifiedMetadata metadata, StremioMediaStream stream, MetadataTrace trace)
        {
            if (stream?.Meta == null) return;

            // [FIX] CROSS-CATALOG TITLE SEEDING
            // If we already have a title from another catalog, we want to maintain both if they differ.
            // This ensures consistent SuperTitle display regardless of which catalog entry is used.
            var addonUrls = StremioAddonManager.Instance.GetAddons();
            int currentCatalogPriority = GetAddonPriorityIndex(addonUrls, stream.SourceAddon);
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
                    metadata.CatalogSourceInfo = GetHostSafe(stream.SourceAddon);
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
                        trace.Log("Seed", $"Alternative title from lower priority catalog ({GetHostSafe(stream.SourceAddon)}): {newTitle} -> SubTitle");
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

            bool canOverwriteSeedFields = string.IsNullOrWhiteSpace(metadata.MetadataSourceInfo) || metadata.MetadataSourceInfo.Contains("Catalog Seed", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(stream.Meta.Description) && (canOverwriteSeedFields || string.IsNullOrWhiteSpace(metadata.Overview))) metadata.Overview = stream.Meta.Description;
            if (!string.IsNullOrWhiteSpace(stream.Meta.Poster) && (canOverwriteSeedFields || string.IsNullOrWhiteSpace(metadata.PosterUrl))) metadata.PosterUrl = stream.Meta.Poster;
            if (!string.IsNullOrWhiteSpace(stream.Meta.Background))
            {
                if (canOverwriteSeedFields || string.IsNullOrWhiteSpace(metadata.BackdropUrl))
                    metadata.BackdropUrl = stream.Meta.Background;
                AddUniqueBackdrop(metadata, stream.Meta.Background);
            }

            if (!string.IsNullOrWhiteSpace(stream.Meta.ReleaseInfo) && (canOverwriteSeedFields || string.IsNullOrWhiteSpace(metadata.Year))) metadata.Year = stream.Meta.ReleaseInfo;
            if (stream.Meta.Genres?.Count > 0 && (canOverwriteSeedFields || string.IsNullOrWhiteSpace(metadata.Genres))) metadata.Genres = string.Join(", ", stream.Meta.Genres);
            if (!string.IsNullOrWhiteSpace(stream.Meta.Runtime) && (canOverwriteSeedFields || string.IsNullOrWhiteSpace(metadata.Runtime))) metadata.Runtime = stream.Meta.Runtime;
            if (!string.IsNullOrWhiteSpace(stream.Meta.Logo) && (canOverwriteSeedFields || string.IsNullOrWhiteSpace(metadata.LogoUrl))) metadata.LogoUrl = stream.Meta.Logo;
            if (stream.Meta.Cast?.Count > 0 && (canOverwriteSeedFields || metadata.Cast == null || metadata.Cast.Count == 0)) metadata.Cast = stream.Meta.Cast.Take(25).ToList();

            if (stream.Meta.ImdbRating != null)
            {
                string ratingStr = stream.Meta.ImdbRating.ToString().Replace(",", ".");
                if ((canOverwriteSeedFields || metadata.Rating <= 0) && double.TryParse(ratingStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedRating) && parsedRating > 0)
                {
                    metadata.Rating = parsedRating;
                }
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

            if (string.IsNullOrWhiteSpace(metadata.MetadataSourceInfo) && !string.IsNullOrWhiteSpace(metadata.CatalogSourceInfo))
            {
                metadata.MetadataSourceInfo = $"{metadata.CatalogSourceInfo} (Catalog Seed)";
                metadata.PrimaryMetadataAddonUrl = metadata.CatalogSourceAddonUrl;
            }

            if (string.IsNullOrWhiteSpace(metadata.DataSource))
            {
                metadata.DataSource = "Catalog";
            }
            else if (!metadata.DataSource.Contains("Catalog", StringComparison.OrdinalIgnoreCase))
            {
                metadata.DataSource = $"{metadata.DataSource} + Catalog";
            }
            trace.Log("Seed", $"Catalog seed applied from {metadata.CatalogSourceInfo}");
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
                var meta = await _stremioService.GetMetaAsync(addonUrl, type, id);
                var entry = new AddonMetaCacheEntry { HasValue = meta != null, Meta = meta };
                var ttl = entry.HasValue ? _addonMetaPositiveCacheDuration : _addonMetaNegativeCacheDuration;
                _addonMetaCache[cacheKey] = (entry, DateTime.Now.Add(ttl));
                return entry;
            }
            catch (Exception ex)
            {
                trace.Log("AddonCache", $"Fetch error on {GetHostSafe(addonUrl)}: {ex.Message}");
                var entry = new AddonMetaCacheEntry { HasValue = false, Meta = null };
                _addonMetaCache[cacheKey] = (entry, DateTime.Now.Add(_addonMetaNegativeCacheDuration));
                return entry;
            }
            finally
            {
                _activeAddonMetaTasks.TryRemove(cacheKey, out _);
            }
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
            string previousTitle = unified.Title;
            
            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Name)) || string.IsNullOrEmpty(unified.Title) || unified.Title == unified.MetadataId)
                unified.Title = stremio.Name;
            else if (!overwritePrimary && !string.IsNullOrEmpty(stremio.Name) && stremio.Name != unified.Title)
            {
                // Secondary title (e.g. English title from Cinemeta when Turkish is primary)
                if (string.IsNullOrEmpty(unified.SubTitle))
                    unified.SubTitle = stremio.Name;
            }

            if (string.IsNullOrWhiteSpace(unified.SubTitle) &&
                !string.IsNullOrWhiteSpace(stremio.OriginalName) &&
                !string.Equals(stremio.OriginalName, unified.Title, StringComparison.OrdinalIgnoreCase))
            {
                unified.SubTitle = stremio.OriginalName;
            }

            // If a higher-priority source replaced title, keep previous title as subtitle.
            if (overwritePrimary &&
                !string.IsNullOrWhiteSpace(previousTitle) &&
                !string.IsNullOrWhiteSpace(unified.Title) &&
                !string.Equals(previousTitle, unified.Title, StringComparison.OrdinalIgnoreCase) &&
                previousTitle != unified.MetadataId)
            {
                unified.SubTitle = previousTitle;
            }

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Description)) || string.IsNullOrEmpty(unified.Overview) || unified.Overview == "Açıklama mevcut değil.")
                unified.Overview = stremio.Description;

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Poster)) || string.IsNullOrEmpty(unified.PosterUrl))
            {
                string newPoster = UpgradeImageUrl(stremio.Poster);
                if (overwritePrimary || string.IsNullOrEmpty(unified.PosterUrl) || GetQualityScore(newPoster) >= GetQualityScore(unified.PosterUrl))
                {
                    unified.PosterUrl = newPoster;
                }
            }


            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Background)) || string.IsNullOrEmpty(unified.BackdropUrl))
            {
                string newBackdrop = UpgradeImageUrl(stremio.Background);
                if (overwritePrimary || string.IsNullOrEmpty(unified.BackdropUrl) || GetQualityScore(newBackdrop) >= GetQualityScore(unified.BackdropUrl))
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

        private bool IsImdbId(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            // IMDb IDs start with 'tt' followed by numbers
            return Regex.IsMatch(id, @"^tt\d+$", RegexOptions.IgnoreCase);
        }

        private bool IsCanonicalId(string? id)
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
    }
}

