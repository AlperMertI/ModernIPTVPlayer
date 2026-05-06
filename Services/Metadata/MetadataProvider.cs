using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services.Stremio;
using ModernIPTVPlayer.Services.Iptv;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models.Stremio;
using System.IO;

namespace ModernIPTVPlayer.Services.Metadata
{

    
    public class MetadataProvider
    {
        private sealed class AddonMetaCacheEntry
        {
            public bool HasValue { get; init; }
            public StremioMeta? Meta { get; init; }
        }

        private static readonly System.Threading.Lock _instanceLock = new();
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
            _ = LoadMappingCacheAsync();
        }

        private async Task LoadMappingCacheAsync()
        {
            try
            {
                await LoadMappingCacheBinaryAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Error loading mapping cache: {ex.Message}");
            }
        }

        private async Task LoadMappingCacheBinaryAsync()
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var fileName = Path.Combine(folder.Path, MAPPING_CACHE_FILE_BINARY);
                if (!File.Exists(fileName)) return;

                using var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);

                // Header Check
                int magic = br.ReadInt32(); // 'MZMP' (Metadata Zero Mapping)
                if (magic != 0x504D5A4D) return; 

                int version = br.ReadInt32();
                int count = br.ReadInt32();

                for (int i = 0; i < count; i++)
                {
                    string key = br.ReadString();
                    string val = br.ReadString();
                    _rawToCanonicalIdCache[key] = val;
                }

                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Restored {count} mappings from Binary Index.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Binary Load Error: {ex.Message}");
            }
        }

        private CancellationTokenSource _saveCts;
        private async Task SaveMappingCacheAsync()
        {
            // Debounce save (500ms) to prevent file system thrashing during rapid hover
            _saveCts?.Cancel();
            _saveCts = new CancellationTokenSource();
            var token = _saveCts.Token;

            try
            {
                await Task.Delay(500, token);
                if (token.IsCancellationRequested) return;

                await Task.Run(() => 
                {
                    try
                    {
                        var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                        var fileName = Path.Combine(folder.Path, MAPPING_CACHE_FILE_BINARY);
                        var tempFile = fileName + ".tmp";

                        var mappings = _rawToCanonicalIdCache.ToArray();

                        using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
                        using (var bw = new BinaryWriter(fs))
                        {
                            bw.Write(0x504D5A4D); // 'MZMP'
                            bw.Write(1); // Version
                            bw.Write(mappings.Length);

                            foreach (var kv in mappings)
                            {
                                bw.Write(kv.Key);
                                bw.Write(kv.Value);
                            }
                        }

                        if (File.Exists(fileName)) File.Delete(fileName);
                        File.Move(tempFile, fileName);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Binary Save Error: {ex.Message}");
                    }
                }, token);
            }
            catch (OperationCanceledException) { }
        }

        private readonly ConcurrentDictionary<string, Lazy<Task<UnifiedMetadata>>> _activeTasks = new();
        private readonly ConcurrentDictionary<string, (UnifiedMetadata Data, DateTime Expiry)> _resultCache = new();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(2); // Short term cache
        private readonly ConcurrentDictionary<string, Task<AddonMetaCacheEntry>> _activeAddonMetaTasks = new();
        private readonly ConcurrentDictionary<string, (AddonMetaCacheEntry Data, DateTime Expiry)> _addonMetaCache = new();
        private readonly ConcurrentDictionary<string, string> _rawToCanonicalIdCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _addonMetaPositiveCacheDuration = TimeSpan.FromMinutes(20);
        private readonly TimeSpan _addonMetaNegativeCacheDuration = TimeSpan.FromMinutes(3);

        private const string MAPPING_CACHE_FILE_BINARY = "metadata_mappings.bin";
        private readonly SemaphoreSlim _enrichmentSemaphore = new SemaphoreSlim(16);

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
                if (string.IsNullOrWhiteSpace(rawId) && stream.Id != 0)
                {
                    rawId = $"iptv:{stream.Id}";
                }

                string? id = ResolveBestInitialId(stream) ?? NormalizeId(rawId) ?? (string.IsNullOrWhiteSpace(rawId) ? null : rawId);
                
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
                string normalizedType = (stream is SeriesStream || string.Equals(streamType, "series", StringComparison.OrdinalIgnoreCase) || string.Equals(streamType, "tv", StringComparison.OrdinalIgnoreCase)) ? "series" : "movie";
                
                string addonHash = GetAddonOrderHash();
                string tmdbLang = AppSettings.TmdbLanguage;
                
                // [FIX] Use Title as extreme fallback only if no ID (Raw or Canonical) exists
                string idPart = !string.IsNullOrWhiteSpace(normalizedId) ? normalizedId : (string.IsNullOrWhiteSpace(rawId) ? stream.Title : rawId);
                string cacheKey = $"{idPart}_{normalizedType}_{addonHash}_{tmdbLang}";
                if (context == MetadataContext.Discovery) cacheKey += "_discovery";

                // 1. Check Primary Cache
                if (_resultCache.TryGetValue(cacheKey, out var cached) && DateTime.Now < cached.Expiry)
                {
                    if (cached.Data != null)
                    {
                        // sw.Stop();
                        // var msg = $"[MetadataProvider] ⚡ TryPeek HIT (Primary) for '{stream.Title}' in {sw.Elapsed.TotalMilliseconds:F1}ms";
                        // AppLogger.Info(msg);
                        // System.Diagnostics.Debug.WriteLine(msg);
                    }
                    return cached.Data;
                }

                // [FIX] ALIASED & CANONICAL LOOKUP (LEVEL 0.5)
                // If we have a canonical ID, check the global (suffix-less) cache first
                if (isCanonical)
                {
                    string cleanKey = $"{normalizedId}_{normalizedType}";
                    if (context == MetadataContext.Discovery) cleanKey += "_discovery";
                    
                    if (_resultCache.TryGetValue(cleanKey, out var cleanCached) && DateTime.Now < cleanCached.Expiry)
                    {
                        return cleanCached.Data;
                    }
                }

                // [FIX] ALIASED LOOKUP for TMDB -> IMDB
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
                    SeedFromCatalogMetadata(tempMetadata, sms, null, context, quiet: true);
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
                    return tempMetadata;
                }
                
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
        /// <summary>
        /// High-performance batch enrichment using .NET 11 Parallel.ForEachAsync.
        /// Orchestration is moved off the UI thread to prevent freezing.
        /// </summary>
        public async Task<Dictionary<StremioMediaStream, UnifiedMetadata>> EnrichItemsAsync(IEnumerable<StremioMediaStream> items, MetadataContext context = MetadataContext.Discovery, int maxConcurrency = 8, CancellationToken ct = default)
        {
            var results = new Dictionary<StremioMediaStream, UnifiedMetadata>();
            if (items == null) return results;
            var itemList = items.ToList();
            if (itemList.Count == 0) return results;

            // Hoist addon hash calculation outside the loop for O(1) performance
            string addonHash = GetAddonOrderHash();

            return await Task.Run(async () => 
            {
                var options = new ParallelOptions 
                { 
                    MaxDegreeOfParallelism = maxConcurrency,
                    TaskScheduler = TaskScheduler.Default 
                };

                await Parallel.ForEachAsync(itemList, options, async (item, token) => 
                {
                    try
                    {
                        // [PROGRESSIVE HYDRATION] Update item individually as soon as it's ready
                        item.BeginUpdate();
                        var meta = await GetMetadataAsync(item, context, ct: token); // Uses internal cache for hash
                        if (meta != null)
                        {
                            lock (results) results[item] = meta;
                        }
                        item.EndUpdate();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MetadataProvider] Parallel Enrichment Error for '{item.Title}': {ex.Message}");
                    }
                });

                return results;
            });
        }

        public async Task<UnifiedMetadata> GetMetadataAsync(Models.IMediaStream stream, MetadataContext context = MetadataContext.Detail, Action<string> onBackdropFound = null, Action<UnifiedMetadata> onUpdate = null, CancellationToken ct = default)
        {
            try
            {
                if (stream == null) return null;
                
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string rawId = stream.IMDbId;
                string id = ResolveBestInitialId(stream) ?? NormalizeId(rawId) ?? rawId;
                var trace = new MetadataTrace(context.ToString(), id, stream?.Title);

                if (!string.IsNullOrWhiteSpace(id) && id != rawId)
                {
                    trace?.Log("ID", $"Canonical ID resolved at entry: {rawId} -> {id}");
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
                if (isCanonical && !string.IsNullOrWhiteSpace(rawId) && id != rawId && !_rawToCanonicalIdCache.ContainsKey(rawId))
                {
                    _rawToCanonicalIdCache[rawId] = id;
                    _ = SaveMappingCacheAsync();
                }

                if (!isCanonical && rawId != null && (rawId.Contains(",") || rawId.Contains(" ") || rawId.Length > 100))
                {
                    AppLogger.Warn($"Non-standard ID detected, using title fallback. RawId: {rawId}");
                    id = null;
                }

                string streamType = (stream as Models.Stremio.StremioMediaStream)?.Meta?.Type;
                string normalizedType = (stream is SeriesStream || string.Equals(streamType, "series", StringComparison.OrdinalIgnoreCase) || string.Equals(streamType, "tv", StringComparison.OrdinalIgnoreCase)) ? "series" : "movie";
                
                string fetchType = normalizedType;
                string normalizedId = NormalizeId(id) ?? id;
                
                string fetchId = id ?? rawId ?? stream.Title;
                
                string addonHash = GetAddonOrderHash();
                string tmdbLang = AppSettings.TmdbLanguage;
                
                // [FIX] Use Title as extreme fallback only if no ID (Raw or Canonical) exists
                string idPart = !string.IsNullOrWhiteSpace(id) ? id : (string.IsNullOrWhiteSpace(rawId) ? stream.Title : rawId);
                string baseCacheKey = $"{idPart}_{normalizedType}_{addonHash}_{tmdbLang}";
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
                    bool isCw = (stream as StremioMediaStream)?.IsContinueWatching ?? false;
                    if (IsSatisfied(cached.Data, context, isCw))
                    {
                        sw.Stop();
                        string provenance = cached.Data.DataSource ?? cached.Data.MetadataSourceInfo ?? "Unknown";
                        trace?.Log("Phase1", $"⚡ CACHE HIT (RAM) | Status: SATISFIED | Source: {provenance}"); 

                        // [FIX] CROSS-CATALOG TITLE SEEDING:
                        // Even on cache hit, if we entered from a different catalog, we should seed its title!
                        if (stream is StremioMediaStream stremioStream)
                        {
                            lock (cached.Data.SyncRoot)
                            {
                                SeedFromCatalogMetadata(cached.Data, stremioStream, trace, context: MetadataContext.Detail);
                                
                                // [ARCHITECTURAL FIX] Sync enriched data BACK to the proxy object!
                                // This ensures that even on cache hit, the local stream gets its TrailerUrl, etc.
                                stremioStream.UpdateFromUnified(cached.Data);
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
                        var missingFromCache = GetMissingFields(cached.Data, GetRequiredFields(context, isCw));
                        trace?.Log("Phase1", $"⚡ CACHE HIT (RAM) | Status: INCOMPLETE (Missing: {missingFromCache}) | Transitioning to Phase 2 (Disk/Network)...");
                        
                        // We have cached data but it's not enough for the current context.
                        // We continue to GetMetadataInternalAsync, passing the cached data as 'seed' to avoid redundant addon probes.
                        return await _activeTasks.GetOrAdd(cacheKey, _ => new Lazy<Task<UnifiedMetadata>>(() => 
                        {
                            trace?.Log("Upgrade", $"START Fetching (Context Upgrade {context}): {id ?? stream.Title} | Missing: {missingFromCache}");
                            return GetMetadataInternalAsync(fetchId, fetchType, stream, cacheKey, context, onBackdropFound, cached.Data, onUpdate, ct, trace);
                        })).Value;
                    }
                }
                // 2. Check High-Speed Persistent Disk Cache (BinaryEnrichmentCache)
                // If RAM cache misses, check disk before going to network.
                if (isCanonical && !string.IsNullOrWhiteSpace(id))
                {
                    var diskEnriched = new UnifiedMetadata();
                    // [NATIVE AOT SAFE] Seed from catalog first so patch has a base
                    if (stream is StremioMediaStream stremioStreamDisk) SeedFromCatalogMetadata(diskEnriched, stremioStreamDisk, trace, context, quiet: true);
                    
                    if (await BinaryEnrichmentCache.Instance.TryPatchAsync(id, diskEnriched))
                    {
                        string provenance = diskEnriched.DataSource ?? diskEnriched.MetadataSourceInfo ?? "Unknown";
                        string fields = GetContentSummary(diskEnriched);
                        trace?.Log("Phase2", $"⚡ CACHE HIT (DISK) | Found in binary store [{provenance}] | Content: {fields}");
                        
                        // Satisfaction check - if disk data is enough, promote to RAM cache and return
                        bool isCw = (stream as StremioMediaStream)?.IsContinueWatching ?? false;
                        if (IsSatisfied(diskEnriched, context, isCw))
                        {
                            var expiry = DateTime.Now.AddDays(context == MetadataContext.Discovery ? 1 : 7);
                            _resultCache[cacheKey] = (diskEnriched, expiry);
                            
                            if (stream is StremioMediaStream s) s.UpdateFromUnified(diskEnriched);
                            return diskEnriched;
                        }
                        
                        // If not satisfied (e.g. need seasons), use disk as a higher-quality seed for internal fetch
                        cached = (diskEnriched, DateTime.Now.AddMinutes(1));
                    }
                }
                
                // Promotion check for Discovery cache
                if (context == MetadataContext.Detail)
                {
                    string discoveryKey = cacheKey + "_discovery";
                    if (_resultCache.TryGetValue(discoveryKey, out var disc) && DateTime.Now < disc.Expiry)
                    {
                        bool isCw = (stream as StremioMediaStream)?.IsContinueWatching ?? false;
                        if (IsSatisfied(disc.Data, MetadataContext.Detail, isCw))
                        {
                            sw.Stop();
                            trace?.Log("Promotion", $"⚡ Cache HIT (Promoted) for '{stream.Title}' [{id ?? "NoId"}] in {sw.Elapsed.TotalMilliseconds:F1}ms");
                            return disc.Data;
                        }
                        else
                        {
                            // [PROMOTION SEEDING] Use discovery data as seed to avoid redundant probes
                            var seedMissing = GetMissingFields(disc.Data, GetRequiredFields(MetadataContext.Detail, isCw));
                            string seedReason = seedMissing == MetadataField.None ? "None" : seedMissing.ToString();
                            
                            trace?.Log("Phase1", $"PROMOTING Discovery cache to Detail ({id ?? stream?.Title}) | Reason: {seedReason}");
                            
                            return await _activeTasks.GetOrAdd(cacheKey, _ => new Lazy<Task<UnifiedMetadata>>(() => Task.Run(async () => 
                            {
                                await _enrichmentSemaphore.WaitAsync(ct);
                                try 
                                {
                                    return await GetMetadataInternalAsync(fetchId, fetchType, stream, cacheKey, context, onBackdropFound, disc.Data, onUpdate, ct, trace);
                                }
                                finally { _enrichmentSemaphore.Release(); }
                            }, ct))).Value;
                        }
                    }
                }

                // 2. Check Active Tasks (Deduplication) - Use Lazy to ensure atomic task creation
                
                // Refine fetch reason by seeing what we already have from the stream/catalog data
                var tempMetadata = new UnifiedMetadata();
                if (stream is ModernIPTVPlayer.Models.Stremio.StremioMediaStream sms)
                    SeedFromCatalogMetadata(tempMetadata, sms, null, context, quiet: true);
                else if (stream != null)
                    SeedFromIptvStream(tempMetadata, stream, trace);

                bool isContinueWatching = (stream as StremioMediaStream)?.IsContinueWatching ?? false;
                var missing = GetMissingFields(tempMetadata, GetRequiredFields(context, isContinueWatching));
                
                // [NEW] Early exit if catalog/discovery/IPTV data already satisfies current context requirements
                // [FIX] NEVER early exit for Spotlight or Detail if we want TMDB enrichment, as catalog-seed is only a starting point.
                // [PRIORITY FIX] If the stream already has a high priority score (5000+), it means it was previously enriched
                // and we should trust the disk-based metadata as an "Instant HIT".
                bool isHighPriority = tempMetadata.PriorityScore > 4000;
                
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
                else if (isHighPriority && missing == MetadataField.None)
                {
                    sw.Stop();
                    var msg = $"[MetadataProvider] ⚡ Instant HIT (Satisfied by Priority: {tempMetadata.PriorityScore}) for '{stream.Title}' in {sw.Elapsed.TotalMilliseconds:F1}ms";
                    AppLogger.Info(msg);
                    System.Diagnostics.Debug.WriteLine(msg);
                    
                    var expiry = DateTime.Now.Add(_cacheDuration);
                    _resultCache[cacheKey] = (tempMetadata, expiry);
                    return tempMetadata;
                }

                var fetchReason = missing.ToString();
                trace?.Log("Reason", $"Missing fields for {context}: {fetchReason}");

                // [DISK SEEDING] If we had a disk hit but it wasn't satisfied, 'cached.Data' contains the disk data.
                // We pass it to GetMetadataInternalAsync to avoid redundant network probes for fields we already have.
                var seedData = (cached.Data != null && cached.Data.PriorityScore >= 0) ? cached.Data : null;

                return await _activeTasks.GetOrAdd(cacheKey, _ => new Lazy<Task<UnifiedMetadata>>(() => Task.Run(async () => 
                {
                    trace?.Log("Phase3", $"NETWORK ENRICHMENT START | Reason: {fetchReason} | Context: {context} | Seeded: {seedData != null}");
                    
                    await _enrichmentSemaphore.WaitAsync(ct);
                    try 
                    {
                        return await GetMetadataInternalAsync(fetchId, fetchType, stream, cacheKey, context, onBackdropFound, seedData, onUpdate, ct, trace);
                    }
                    finally { _enrichmentSemaphore.Release(); }
                }, ct))).Value;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
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

            if (meta.MoviedbId != null && int.TryParse(meta.MoviedbId.ToString(), out int tmdbId) && tmdbId > 0)
                return $"tmdb:{tmdbId}";

            return baseId;
        }

        private async Task<UnifiedMetadata> GetMetadataInternalAsync(string id, string type, Models.IMediaStream sourceStream, string cacheKey, MetadataContext context, Action<string> onBackdropFound = null, UnifiedMetadata seed = null, Action<UnifiedMetadata> onUpdate = null, CancellationToken ct = default, MetadataTrace? trace = null)
        {
            // [FIX] Normalize ID to canonical IMDB ID immediately to avoid search failures on priority addons
            string normalizedId = NormalizeId(id) ?? id;
            try
            {
                var result = await GetMetadataAsync(normalizedId, type, sourceStream, context, onBackdropFound, seed, onUpdate, ct, trace);
                
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
                        
                        // [NEW] Also cache the discovery key specifically to prevent redundant searching
                        string cleanKey = $"{normalizedId}_{type}";
                        if (context == MetadataContext.Discovery) cleanKey += "_discovery";
                        if (!_resultCache.ContainsKey(cleanKey)) _resultCache[cleanKey] = (result, expiry);

                        AppLogger.Info($"[MetadataProvider] Multi-cached: {cacheKey} AND {newKey}");
                    }
                    else if ((IsImdbId(normalizedId) || (normalizedId != null && normalizedId.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))) && !cacheKey.Equals($"{normalizedId}_{type}", StringComparison.OrdinalIgnoreCase))
                    {
                        // Even if ID didn't change, if we used a complex cacheKey (with hash), 
                        // also cache under the CLEAN ID to ensure instant peeks later.
                        string cleanKey = $"{normalizedId}_{type}";
                        if (context == MetadataContext.Discovery) cleanKey += "_discovery";
                        if (!_resultCache.ContainsKey(cleanKey)) _resultCache[cleanKey] = (result, expiry);
                    AppLogger.Info($"[MetadataProvider] Promotion-cached: {cacheKey} -> {cleanKey}");
                    }

                    // [NEW] Persist to binary cache for future sessions
                    if (!string.IsNullOrEmpty(result.ImdbId) && IsCanonicalId(result.ImdbId))
                    {
                        _ = BinaryEnrichmentCache.Instance.SaveAsync(result.ImdbId, result);
                    }
                }
                
                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                _activeTasks.TryRemove(cacheKey, out _);
            }
        }

        private readonly StremioService _stremioService = StremioService.Instance;

        private async Task<UnifiedMetadata> GetMetadataAsync(string id, string type, Models.IMediaStream sourceStream = null, MetadataContext context = MetadataContext.Detail, Action<string> onBackdropFound = null, UnifiedMetadata seed = null, Action<UnifiedMetadata> onUpdate = null, CancellationToken ct = default, MetadataTrace? trace = null)
        {
            // [STABILITY] Never re-initialize trace if one was passed from the top-level orchestration.
            trace ??= new MetadataTrace(context.ToString(), id, sourceStream?.Title);
            bool isSeriesType = type == "series" || type == "tv";
            bool isContinueWatching = (sourceStream as StremioMediaStream)?.IsContinueWatching ?? false;
            var metadata = seed ?? new UnifiedMetadata
            {
                ImdbId = id,
                IsSeries = isSeriesType,
                MetadataId = id,
                Title = sourceStream?.Title ?? (id ?? "Unknown"),
                Overview = sourceStream?.Description,
                PosterUrl = sourceStream?.PosterUrl,
                BackdropUrl = sourceStream?.BackdropUrl ?? sourceStream?.PosterUrl,
                Genres = sourceStream?.Genres,
                Rating = double.TryParse(sourceStream?.Rating, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0,
                IsFromIptv = sourceStream is VodStream || sourceStream is SeriesStream,
                DataSource = (sourceStream is StremioMediaStream strm) ? strm.SourceAddon : "Library"
            };

            // [FIX] Ensure IsSeries is correctly synced if we are using a seed (cached result)
            if (seed != null) metadata.IsSeries = isSeriesType;

            // [NEW] Canonical ID Restoration (The Fix)
            // Even if a 'seed' was provided, we must ensure it uses the canonical ID 
            // restored from our persistent disk cache if possible.
            if (!IsCanonicalId(metadata.ImdbId) && !string.IsNullOrEmpty(id) && IsCanonicalId(id))
            {
                trace?.Log("ID", $"Pinning canonical ID to metadata: {id}");
                metadata.ImdbId = id;
            }

            // [NEW] Early ID Resolution: If this is an IPTV ID, check if we have a persistent match.
            if (!string.IsNullOrEmpty(metadata.ImdbId) && !IsCanonicalId(metadata.ImdbId))
            {
                // A. Check high-speed binary cache first (MetadataProvider's own memory)
                string rawId = NormalizeId(metadata.ImdbId) ?? metadata.ImdbId;
                if (_rawToCanonicalIdCache.TryGetValue(rawId, out var cachedCanonical))
                {
                    AppLogger.Info($"[Metadata] Restored mapping from disk cache: {rawId} -> {cachedCanonical}");
                    trace?.Log("ID", $"Restored from disk cache: {rawId} -> {cachedCanonical}");
                    metadata.ImdbId = cachedCanonical;
                }
                // B. Fallback to IptvMatchService (User/Internal persistent mappings)
                else if (char.IsDigit(metadata.ImdbId[0]))
                {
                    var match = IptvMatchService.Instance.MatchToIptvById(metadata.ImdbId, "movie");
                    if (match != null && !string.IsNullOrEmpty(match.IMDbId) && IsImdbId(match.IMDbId))
                    {
                        trace?.Log("ID", $"Early Resolution (Service): IPTV {metadata.ImdbId} -> IMDb {match.IMDbId}");
                        metadata.ImdbId = match.IMDbId;
                        
                        // Sync back to our high-speed cache
                        _rawToCanonicalIdCache[rawId] = match.IMDbId;
                    }
                }
            }

            bool isCw = (sourceStream as StremioMediaStream)?.IsContinueWatching ?? false;
            MetadataField required = GetRequiredFields(context, isCw);
            MetadataField missing = GetMissingFields(metadata, required);

            try
            {
                // [NEW] Parallel Progressive Hydration: Start IPTV enrichment and Discovery Search simultaneously.
                // This ensures the plot and local poster appear within <50ms even if the network is slow.
                Task? iptvTask = null;

                // A. Local IPTV Enrichment (Ultra-fast Disk access)
                if (App.CurrentLogin != null && context != MetadataContext.Discovery && context != MetadataContext.Spotlight)
                {
                    if (metadata.IsSeries)
                    {
                        var seriesStream = sourceStream as SeriesStream;
                        iptvTask = EnrichWithIptvAsync(metadata, seriesStream ?? new SeriesStream { Name = metadata.Title }, trace, ct);
                    }
                    else
                    {
                        var movieStream = sourceStream as VodStream;
                        iptvTask = EnrichWithIptvMovieAsync(metadata, movieStream ?? new VodStream { Name = metadata.Title, StreamId = (sourceStream?.Id ?? 0) }, trace, ct);
                    }
                }

                // Wait for local data and push to UI immediately (The "Progressive" win)
                if (iptvTask != null)
                {
                    await iptvTask;
                    onUpdate?.Invoke(metadata);
                    trace?.Log("Progressive", $"Dispatched early IPTV seed: {metadata.Title}");
                }

                if (sourceStream is StremioMediaStream stremioStream)
                {
                    SeedFromCatalogMetadata(metadata, stremioStream, trace, context: context);
                    stremioStream.UpdateFromUnified(metadata);
                    onUpdate?.Invoke(metadata);
                }

                // Global Catalog Seeding
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
                    onUpdate?.Invoke(metadata);
                }

                missing = GetMissingFields(metadata, required);
                trace?.Log("Phase2", $"NETWORK ENRICHMENT START | Target: {required} | Current Missing: {missing}");

                string currentSearchId = NormalizeId(metadata.ImdbId) ?? NormalizeId(id) ?? id;
                
                // [NEW] Discovery Search: Resolve non-canonical IDs by title (Runs AFTER IPTV seed is safe)
                if (!IsCanonicalId(currentSearchId) && !string.IsNullOrWhiteSpace(metadata.Title))
                {
                    string? year = (sourceStream as Models.IMediaStream)?.Year;
                    if (string.IsNullOrEmpty(year)) year = metadata.Year;

                    Span<char> searchBuf = stackalloc char[metadata.Title.Length + 16];
                    int searchLen = TitleHelper.NormalizeForSearch(metadata.Title.AsSpan(), searchBuf);
                    string cleanSearchTitle = searchBuf[..searchLen].ToString();
                    if (string.IsNullOrWhiteSpace(cleanSearchTitle)) cleanSearchTitle = metadata.Title;

                    var discoveredId = await ResolveIdByTitleDiscoveryAsync(cleanSearchTitle, type, year, trace, ct);
                    if (IsImdbId(discoveredId))
                    {
                        currentSearchId = discoveredId!;
                        metadata.ImdbId = discoveredId;
                        
                        string rawId = NormalizeId(id) ?? id;
                        if (string.IsNullOrEmpty(rawId) || rawId == "imdb" || rawId == "tmdb")
                        {
                            if (sourceStream != null) rawId = $"iptv:{sourceStream.Id}";
                        }

                        if (!IsCanonicalId(rawId) && !string.IsNullOrEmpty(rawId))
                        {
                            _rawToCanonicalIdCache[rawId] = discoveredId;
                            AppLogger.Info($"[Metadata] Discovery LEARNED: {rawId} -> {discoveredId}. Syncing to library...");
                            trace?.Log("Discovery", $"LEARNED: {rawId} -> {discoveredId}");
                            _ = SaveMappingCacheAsync(); 

                            string playlistId = App.CurrentLogin?.PlaylistId ?? AppSettings.LastPlaylistId?.ToString() ?? "default";
                            _ = ContentCacheService.Instance.HydrateInPlaceAsync(playlistId, discoveredId, metadata, metadata.IsSeries);
                        }
                    }
                }

                bool isTmdbConfigured = !string.IsNullOrWhiteSpace(AppSettings.TmdbApiKey) && AppSettings.IsTmdbEnabled;
                bool tmdbAllowed = context == MetadataContext.Detail || context == MetadataContext.ExpandedCard || context == MetadataContext.Spotlight || (context == MetadataContext.Hero && isTmdbConfigured);
                bool tmdbEnabled = tmdbAllowed && AppSettings.IsTmdbEnabled && !string.IsNullOrWhiteSpace(AppSettings.TmdbApiKey);
                
                if (tmdbEnabled)
                {
                    trace?.Log("TMDB", "TMDB enrichment starting...");
                    var tmdb = await EnrichWithTmdbAsync(metadata, context, isContinueWatching, ct);
                    if (tmdb != null)
                    {
                        metadata.TmdbInfo = tmdb;
                        metadata.MetadataSourceInfo = "TMDB";
                        metadata.DataSource = "TMDB"; // TMDB is the authority

                        metadata.PriorityScore = MetadataPriority.CalculateScore(MetadataPriority.AUTHORITY_TMDB, 
                            context == MetadataContext.Detail ? MetadataPriority.DEPTH_DETAIL : 
                            (context == MetadataContext.Spotlight ? MetadataPriority.DEPTH_SPOTLIGHT : MetadataPriority.DEPTH_CATALOG));

                        var additionalBackdrops = metadata.IsSeries
                            ? await TmdbHelper.GetTvImagesAsync(tmdb.Id.ToString(), ct: ct)
                            : await TmdbHelper.GetMovieImagesAsync(tmdb.Id.ToString(), ct: ct);

                        foreach (var bg in additionalBackdrops) AddUniqueBackdrop(metadata, bg, onBackdropFound);

                        if (sourceStream is StremioMediaStream sms) sms.UpdateFromUnified(metadata);
                        onUpdate?.Invoke(metadata);
                        missing = GetMissingFields(metadata, required);
                    }
                    else
                    {
                        trace?.Log("TMDB", "TMDB enrichment failed. Falling back to addon chain.");
                    }
                }
                else
                {
                    if (!tmdbAllowed && AppSettings.IsTmdbEnabled)
                        trace?.Log("TMDB", "TMDB skipped for Discovery context (Throttling Protection).");
                    else
                        trace?.Log("TMDB", "TMDB disabled or API key missing.");
                }

                if (tmdbEnabled)
                {
                    bool satisfiesRequirements = GetMissingFields(metadata, required) == MetadataField.None;
                    if (metadata.TmdbInfo != null && satisfiesRequirements)
                    {
                        trace?.Log("Decision", "TMDB present and satisfies requirements. Skipping addon detail probes.");
                    }
                    else if (metadata.TmdbInfo != null)
                    {
                        trace?.Log("Decision", "TMDB present but requirements not met (e.g. Logo/Rating missing). Probing addons for gaps.");
                        // Proceed to addon probes (line 805 onwards)
                    }
                    else
                    {
                        trace?.Log("Decision", "TMDB enabled but result unavailable. Proceeding to addon detail probes as fallback.");
                    }
                }

                // [ADDON PROBE CHAIN] - RESTORED FULL COMPLEXITY
                if (!tmdbEnabled || (metadata.TmdbInfo == null && GetMissingFields(metadata, required) != MetadataField.None) || (metadata.TmdbInfo != null && GetMissingFields(metadata, required) != MetadataField.None))
                {
                    var addonUrls = StremioAddonManager.Instance.GetAddonsByResource("meta");
                    if (addonUrls.Count > 0)
                    {
                        int sourcePriority = GetAddonPriorityIndex(addonUrls, metadata.CatalogSourceAddonUrl);
                        int existingPrimaryPriority = GetAddonPriorityIndex(addonUrls, metadata.PrimaryMetadataAddonUrl);
                        int primaryPriority = Math.Min(sourcePriority >= 0 ? sourcePriority : int.MaxValue, existingPrimaryPriority >= 0 ? existingPrimaryPriority : int.MaxValue);

                        if (metadata.TmdbInfo != null) primaryPriority = -1;

                        string rawCatalogId = (sourceStream as StremioMediaStream)?.Meta?.Id ?? sourceStream?.IMDbId ?? id;
                        if (!IsCanonicalId(currentSearchId) && !string.IsNullOrWhiteSpace(rawCatalogId) && !string.Equals(currentSearchId, rawCatalogId, StringComparison.Ordinal))
                        {
                            currentSearchId = rawCatalogId;
                            trace?.Log("ID", $"Canonical ID yok. Raw catalog ID ile probe denenecek: {currentSearchId}");
                        }

                        // Prepare Probe Order (Restored full Torbox/TBM nuance)
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
                                trace?.Log("Decision", $"Non-canonical source-first probe uygulanacak: {GetHostSafe(src.Url)}");
                            }
                        }

                        foreach (var pair in probeOrder)
                        {
                            if (ct.IsCancellationRequested) return metadata;
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
                                    trace?.Log("Addon", $"Satisfied but {GetHostSafe(url)} (Prio:{index}) has higher priority than current (Prio:{primaryPriority}). Probing for better data.");
                                }
                                else if (hasGenericEpTitles)
                                {
                                    trace?.Log("Addon", $"Fields satisfied but episode titles are generic. Continuing probe to {GetHostSafe(url)} for better episode data.");
                                }
                                else
                                {
                                    trace?.Log("Addon", "Missing field kalmadi ve tum oncelikli eklentiler tarandi. Probe zinciri tamamlandi.");
                                    break;
                                }
                            }

                            if (metadata.ProbedAddons.Contains(url))
                            {
                                trace?.Log("Addon", $"Skip already probed addon: {GetHostSafe(url)}");
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
                                    trace?.Log("Addon", $"Skip {GetHostSafe(url)}: Non-canonical ID ({currentSearchId}) icin Cinemeta probe edilmez.");
                                    continue;
                                }

                                if (sourcePriority >= 0)
                                {
                                    bool isCatalogSource = index == sourcePriority;
                                    if (!isCatalogSource && !(isTbmNamespace && isTorboxResolver))
                                    {
                                        trace?.Log("Addon", $"Skip {GetHostSafe(url)}: Non-canonical ID ({currentSearchId}) icin sadece catalog source (ve tbm ise torbox resolver) probe edilir.");
                                        continue;
                                    }
                                }
                                else if (index > 0)
                                {
                                    if (!(isTbmNamespace && isTorboxResolver))
                                    {
                                        trace?.Log("Addon", $"Skip {GetHostSafe(url)}: Non-canonical ID ({currentSearchId}) ve source belirsiz. Sadece en yuksek oncelik (tbm ise torbox resolver) probe edilir.");
                                        continue;
                                    }
                                }
                            }

                            if (currentSearchId != null && (currentSearchId.StartsWith("error", StringComparison.OrdinalIgnoreCase) || currentSearchId.Contains("aiostreamserror")))
                            {
                                trace?.Log("Addon", $"Invalid ID for probing: {currentSearchId}");
                                break;
                            }

                            trace?.Log("Addon", $"Probe {GetHostSafe(url)} Priority={index} ID={currentSearchId}");
                            var entry = await GetAddonMetaCachedAsync(url, type, currentSearchId, trace, ct);
                            metadata.ProbedAddons.Add(url);

                            if (!entry.HasValue || entry.Meta == null || !IsValidMetadata(entry.Meta))
                            {
                                trace?.Log("Addon", $"No usable meta from {GetHostSafe(url)} (HasValue={entry.HasValue}, IsNull={entry.Meta == null})");
                                continue;
                            }

                            string? discoveredImdbId = ExtractImdbId(entry.Meta);
                            if (!IsImdbId(currentSearchId) && IsImdbId(discoveredImdbId))
                            {
                                currentSearchId = discoveredImdbId!;
                                metadata.ImdbId = discoveredImdbId;
                                trace?.Log("ID", $"Upgraded search id to canonical IMDb: {discoveredImdbId}. Allowing full addon loop.");
                            }

                             int addonPriority = MetadataPriority.CalculateScore(MetadataPriority.AUTHORITY_STREMIO_ADDON, MetadataPriority.DEPTH_DETAIL, index);
                             bool overwritePrimary = addonPriority >= metadata.PriorityScore;
                             var missingBefore = GetMissingFields(metadata, required);
                             
                             MapStremioToUnified(entry.Meta, metadata, overwritePrimary, trace);

                             if (overwritePrimary)
                             {
                                 primaryPriority = index;
                                 metadata.PriorityScore = addonPriority;
                                 metadata.PrimaryMetadataAddonUrl = url;
                                 metadata.MetadataSourceInfo = $"{GetHostSafe(url)} (Primary)";
                                 trace?.Log("Priority", $"Primary source switched to {GetHostSafe(url)} (Score: {addonPriority})");
                                 if (sourceStream is StremioMediaStream sms) sms.UpdateFromUnified(metadata);
                                 onUpdate?.Invoke(metadata);
                             }
                             else
                             {
                                 trace?.Log("Priority", $"{GetHostSafe(url)} used as gap filler (AddonScore: {addonPriority} < CurrentScore: {metadata.PriorityScore}).");
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
                                     trace?.Log("Merge", $"Addon {GetHostSafe(url)} contributed {epChanges} episode updates.");
                                     string addonName = GetHostSafe(url);
                                     string priorityTag = overwritePrimary ? "Primary" : "Gaps";
                                     string epTag = $"(Episodes: {epChanges}) [{priorityTag}]";
                                     
                                     if (string.IsNullOrEmpty(metadata.DataSource) || metadata.DataSource == addonName || metadata.DataSource == "Library")
                                         metadata.DataSource = $"{addonName} {epTag}";
                                     else if (!metadata.DataSource.Contains(addonName))
                                         metadata.DataSource += $" + {addonName} {epTag}";
                                     else if (!metadata.DataSource.Contains("Episodes:"))
                                         metadata.DataSource = metadata.DataSource.Replace(addonName, $"{addonName} {epTag}");
                                      onUpdate?.Invoke(metadata);
                                 }
                             }

                             var missingAfter = GetMissingFields(metadata, required);

                             // Track field-level contributions (Restored Nuance)
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
                                  onUpdate?.Invoke(metadata);
                             }

                             // [BREAK LOGIC] If everything satisfied, stop early
                             if (missingAfter == MetadataField.None)
                             {
                                 trace?.Log("Addon", "All required fields satisfied. Breaking probe chain.");
                                 break;
                             }

                             // [OPTIMIZATION] Best-effort break for series (One Piece fix)
                             if (metadata.IsSeries && missingAfter == MetadataField.Seasons && index >= 1)
                             {
                                 trace?.Log("Addon", "Found best-effort episode titles. Breaking probe chain to save time.");
                                 break;
                             }
                        }
                    }
                }

                if (metadata.IsSeries && metadata.TmdbInfo != null) ReconcileTmdbSeasons(metadata, metadata.TmdbInfo);
                trace?.Log("Phase3", $"ENRICHMENT COMPLETE | Final Source: {metadata.DataSource ?? metadata.MetadataSourceInfo}");
            }
            catch (Exception ex)
            {
                trace?.Log("Error", ex.Message);
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

            // Mark all requested fields as "Checked" even if they weren't found.
            // This ensures that subsequent requests in the same or lower context won't trigger new network probes.
            metadata.CheckedFields |= required;

            var finalMissing = GetMissingFields(metadata, GetRequiredFields(context, isCw));
            trace?.Log("Finish", $"FinalSource={metadata.MetadataSourceInfo} DataSource={metadata.DataSource} Missing={finalMissing} Checked={metadata.CheckedFields}");
            
            // [FIX] Update the highest enrichment level attained.
            // This prevents re-fetching items that are "unresolvably missing" certain fields (e.g. 2026 movies missing Rating).
            if (metadata.MaxEnrichmentContext < context)
            {
                metadata.MaxEnrichmentContext = context;
            }
            
            // [PERSISTENCE] Sync enriched metadata back to binary cache for cold-start performance
            if (context >= MetadataContext.ExpandedCard)
            {
                string syncId = !string.IsNullOrEmpty(metadata.ImdbId) ? metadata.ImdbId : id;
                
                // 1. Save to high-speed binary persistent cache
                _ = BinaryEnrichmentCache.Instance.SaveAsync(syncId, metadata);

                // 2. Save to standard SQL/Json persistence
                string pid = App.CurrentLogin?.PlaylistId ?? AppSettings.LastPlaylistId?.ToString() ?? "default";
                _ = ContentCacheService.Instance.HydrateInPlaceAsync(pid, syncId, metadata, metadata.IsSeries);
            }
            
            return metadata;
        }

        private MetadataField GetRequiredFields(MetadataContext context, bool isContinueWatching = false)
        {
            switch (context)
            {
                case MetadataContext.Landscape:
                    // Lite enrichment for standard discovery rows (no trailers, no logos, no overview)
                    return MetadataField.Title | MetadataField.Year | MetadataField.Rating | MetadataField.Backdrop;

                case MetadataContext.Spotlight:
                case MetadataContext.Hero:
                    // Full enrichment for top-level showcase items
                    return MetadataField.Title | MetadataField.Overview | MetadataField.Year | 
                           MetadataField.Rating | MetadataField.Backdrop | MetadataField.Logo | 
                           MetadataField.Trailer | MetadataField.Genres;

                case MetadataContext.ExpandedCard:
                    // Deep enrichment for the quick-info view
                    var expReq = MetadataField.Title | MetadataField.Overview | MetadataField.Year | 
                                 MetadataField.Rating | MetadataField.Trailer | MetadataField.Genres | 
                                 MetadataField.Backdrop | MetadataField.Cast | MetadataField.Runtime;
                    if (isContinueWatching) expReq |= MetadataField.Seasons;
                    return expReq;

                case MetadataContext.Detail:
                    // Absolute maximum enrichment for the details page
                    return MetadataField.Title | MetadataField.Overview | MetadataField.Year | 
                           MetadataField.Rating | MetadataField.Backdrop | MetadataField.Trailer | 
                           MetadataField.Seasons | MetadataField.Logo | MetadataField.Gallery | 
                           MetadataField.CastPortraits | MetadataField.Runtime | MetadataField.Cast | 
                           MetadataField.OriginalTitle;

                case MetadataContext.Discovery:
                default:
                    // Minimal requirements for fallback or generic discovery items
                    return MetadataField.Title | MetadataField.Year;
            }
        }

        private MetadataField GetMissingFields(UnifiedMetadata metadata, MetadataField required)
        {
            MetadataField missing = MetadataField.None;
            if (required == MetadataField.None) return MetadataField.None;

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

            if (required.HasFlag(MetadataField.Cast))
            {
                bool hasCast = metadata.Cast != null && metadata.Cast.Count > 0;
                bool hasDirectors = metadata.Directors != null && metadata.Directors.Count > 0;
                bool hasWriters = !string.IsNullOrEmpty(metadata.Writers);

                bool castSatisfied = hasCast && (hasDirectors || (metadata.IsSeries && hasWriters));
                if (!castSatisfied) missing |= MetadataField.Cast;
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

        private void SeedFromIptvStream(UnifiedMetadata metadata, Models.IMediaStream stream, MetadataTrace? trace)
        {
            if (stream == null) return;
            
            // [PHASE 2] High-Performance Disk Data Trust
            // If the stream was already patched in a previous session, it carries a high PriorityScore.
            metadata.PriorityScore = stream.PriorityScore;
            metadata.Title = stream.Title;
            metadata.Year = stream.Year;
            metadata.Overview = stream.Description;
            metadata.PosterUrl = stream.PosterUrl;
            metadata.BackdropUrl = stream.BackdropUrl;
            metadata.Genres = stream.Genres;
            metadata.Cast = stream.Cast?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(s => new UnifiedCast { Name = s.Trim() }).ToList();
            metadata.Directors = stream.Director?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                .Select(s => new UnifiedCast { Name = s.Trim(), Character = "Yönetmen" }).ToList();
            metadata.TrailerUrl = stream.TrailerUrl;

            if (double.TryParse(stream.Rating, NumberStyles.Any, CultureInfo.InvariantCulture, out double rating))
                metadata.Rating = rating;

            // Mark source as "IPTV-Disk" if it was already enriched
            if (metadata.PriorityScore > 4000)
            {
                metadata.CatalogSourceInfo = "IPTV-Enriched";
                trace?.Log("Seed", $"Recovered enriched metadata from disk: Score={metadata.PriorityScore}");
            }
        }

        private void SeedFromCatalogMetadata(UnifiedMetadata metadata, StremioMediaStream stream, MetadataTrace? trace, MetadataContext context, bool quiet = false)
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
                    if (!quiet) trace?.Log("History", $"Source identified as Watch History for {bestId}");
                }
            }
            
            // [FALLBACK] If SourceAddon is missing (common for CW items), check Discovery Cache
            if (string.IsNullOrEmpty(addonUrl))
            {
                // [PRIORITY 1] Check if metadata already has a source we can leverage
                if (!string.IsNullOrEmpty(metadata.CatalogSourceAddonUrl))
                {
                    addonUrl = metadata.CatalogSourceAddonUrl;
                }
                else
                {
                    // [PRIORITY 2] Improved fallback: Search all discovery keys for a match to this ID to recover original source info
                    bestId = ResolveBestInitialId(stream) ?? stream.IMDbId ?? stream.Id.ToString();
                    if (!string.IsNullOrEmpty(bestId))
                    {
                        var discoveryEntry = _resultCache.FirstOrDefault(kvp => kvp.Key.Contains(bestId) && kvp.Key.EndsWith("_discovery")).Value;
                        if (discoveryEntry.Data != null && !string.IsNullOrEmpty(discoveryEntry.Data.CatalogSourceAddonUrl))
                        {
                            addonUrl = discoveryEntry.Data.CatalogSourceAddonUrl;
                            if (!quiet) trace?.Log("Fallback", $"Source addon recovered from discovery cache: {GetHostSafe(addonUrl)}");
                        }
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
                        trace?.Log("Seed", $"Title swap: {metadata.Title} (low pri) -> SubTitle, {newTitle} (high pri) -> Title");
                        metadata.SubTitle = metadata.Title;
                        metadata.Title = newTitle;
                        metadata.CatalogSourceAddonUrl = stream.SourceAddon;
                        metadata.CatalogSourceInfo = GetHostSafe(stream.SourceAddon);
                    }
                    else if (string.IsNullOrWhiteSpace(metadata.SubTitle))
                    {
                        if (!quiet) trace?.Log("Seed", $"Alternative title from lower priority catalog ({GetHostSafe(stream.SourceAddon)}): {newTitle} -> SubTitle");
                        metadata.SubTitle = newTitle;
                    }
                }
            }

            // Originalname handling (as a secondary SubTitle source)
            if (!string.IsNullOrWhiteSpace(stream.Meta.Originalname) &&
                !string.Equals(stream.Meta.Originalname, metadata.Title, StringComparison.OrdinalIgnoreCase) &&
                !IsGenericEpisodeTitle(stream.Meta.Originalname, metadata.Title))
            {
                metadata.SubTitle = stream.Meta.Originalname;
            }

            bool isCurrentSeed = string.IsNullOrWhiteSpace(metadata.MetadataSourceInfo) || metadata.MetadataSourceInfo.Contains("Catalog Seed", StringComparison.OrdinalIgnoreCase);
            bool isHigherOrEqualPriority = currentCatalogPriority <= primaryCatalogPriority;
            bool canOverwriteSeedFields = string.IsNullOrWhiteSpace(metadata.MetadataSourceInfo) || (isCurrentSeed && isHigherOrEqualPriority);

            // Mapping core fields (using thorough shared logic)
            MapStremioToUnified(stream.Meta, metadata, canOverwriteSeedFields, trace, quiet);
            
            if (string.IsNullOrEmpty(metadata.MetadataSourceInfo))
            {
                metadata.MetadataSourceInfo = $"Catalog({GetHostSafe(stream.SourceAddon)})";
            }

            // Merge episodes from catalog metadata (SKIP during grid discovery for performance)
            if (metadata.IsSeries && stream.Meta?.Videos != null && context != MetadataContext.Discovery && context != MetadataContext.ExpandedCard)
            {
                  MergeStremioEpisodes(stream.Meta, metadata, GetHostSafe(addonUrl), canOverwriteSeedFields);
            }

            if (stream.Meta.Imdbrating != null)
            {
                string ratingStr = stream.Meta.Imdbrating.ToString().Replace(",", ".");
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
            else if (stream.Meta.MoviedbId != null && int.TryParse(stream.Meta.MoviedbId.ToString(), out int movieDbId) && movieDbId > 0)
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
                trace?.Log("Seed", $"Catalog IDs: MetaId={stream.Meta.Id ?? "null"} Imdb={stream.Meta.ImdbId ?? "null"} Website={websitePreview} => Resolved={metadata.ImdbId ?? "null"}");

            if ((stream.Meta.Trailers?.Count > 0) || (stream.Meta.TrailerStreams?.Count > 0))
            {
                if (stream.Meta.Trailers != null)
                {
                    foreach (var trailer in stream.Meta.Trailers
                        .Where(t => !string.IsNullOrWhiteSpace(t.Source))
                        .OrderByDescending(t => string.Equals(t.Type, "trailer", StringComparison.OrdinalIgnoreCase)))
                    {
                        AddTrailerCandidate(metadata, trailer.Source, preferPrimary: canOverwriteSeedFields || string.IsNullOrWhiteSpace(metadata.TrailerUrl));
                    }
                }

                if (stream.Meta.TrailerStreams != null)
                {
                    foreach (var trailer in stream.Meta.TrailerStreams.Where(t => !string.IsNullOrWhiteSpace(t.YtId)))
                    {
                        AddTrailerCandidate(metadata, trailer.YtId, preferPrimary: canOverwriteSeedFields || string.IsNullOrWhiteSpace(metadata.TrailerUrl));
                    }
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

                if (!string.IsNullOrEmpty(stream.Meta.AppExtras.Trailer))
                    AddTrailerCandidate(metadata, stream.Meta.AppExtras.Trailer, preferPrimary: canOverwriteSeedFields || string.IsNullOrWhiteSpace(metadata.TrailerUrl));

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

            if (!quiet) trace?.Log("Seed", $"Catalog seed applied from {GetHostSafe(addonUrl)}");
        }

        private async Task<string?> ResolveIdByTitleDiscoveryAsync(string title, string type, string? year, MetadataTrace trace, CancellationToken ct = default)
        {
            try
            {
                // [UNIONIZED] Use the existing TitleHelper logic for cleaning/matching
                string searchQuery = title;
                trace?.Log("Discovery", $"Searching Cinemeta for: \"{searchQuery}\" (Year: {year ?? "Any"})");

                const string cinemetaUrl = "https://v3-cinemeta.strem.io";
                var cinemetaManifest = StremioAddonManager.Instance.GetManifest(cinemetaUrl);
                if (cinemetaManifest == null)
                {
                    trace?.Log("Discovery", "Cinemeta manifest not found in manager. Skipping search.");
                    return null;
                }

                // Call the search endpoint on Cinemeta
                var results = await _stremioService.SearchAddonAsync(cinemetaUrl, cinemetaManifest, searchQuery, type);
                if (results == null || results.Count == 0)
                {
                    trace?.Log("Discovery", "No results found on Cinemeta.");
                    return null;
                }

                foreach (var result in results)
                {
                    // [UNIONIZED] Use existing TitleHelper.IsMatch logic to ensure consistent matching behavior
                    if (TitleHelper.IsMatch(result.Title, title, result.Year, year))
                    {
                        string? discoveredId = NormalizeId(result.IMDbId) ?? result.IMDbId;
                        if (IsImdbId(discoveredId))
                        {
                            trace?.Log("Discovery", $"SUCCESS: Matched \"{title}\" to {discoveredId} ({result.Title})");
                            return discoveredId;
                        }
                    }
                }
                
                trace?.Log("Discovery", $"Found {results.Count} results, but none matched using TitleHelper.IsMatch.");
            }
            catch (Exception ex)
            {
                trace?.Log("Discovery-Error", ex.Message);
            }
            return null;
        }

        private async Task<AddonMetaCacheEntry> GetAddonMetaCachedAsync(string addonUrl, string type, string id, MetadataTrace trace, CancellationToken ct = default)
        {
            string key = $"{addonUrl}|{type}|{id}";
            if (_addonMetaCache.TryGetValue(key, out var cached) && DateTime.Now < cached.Expiry)
            {
                trace?.Log("AddonCache", $"HIT {GetHostSafe(addonUrl)}");
                return cached.Data;
            }

            trace?.Log("AddonCache", $"MISS {GetHostSafe(addonUrl)}");
            return await _activeAddonMetaTasks.GetOrAdd(key, _ => FetchAddonMetaInternalAsync(addonUrl, type, id, key, trace, ct));
        }

        private async Task<AddonMetaCacheEntry> FetchAddonMetaInternalAsync(string addonUrl, string type, string id, string cacheKey, MetadataTrace trace, CancellationToken ct = default)
        {
            try
            {
                trace?.Log("Addon", $"Fetching metadata from addon: {GetHostSafe(addonUrl)}");
                var meta = await _stremioService.GetMetaAsync(addonUrl, type, id, trace.OperationId, ct);
                var entry = new AddonMetaCacheEntry { HasValue = meta != null, Meta = meta };
                var ttl = entry.HasValue ? _addonMetaPositiveCacheDuration : _addonMetaNegativeCacheDuration;
                _addonMetaCache[cacheKey] = (entry, DateTime.Now.Add(ttl));
                return entry;
            }
            catch (TaskCanceledException ex)
            {
                trace?.Log("AddonCache", $"TaskCanceledException on {GetHostSafe(addonUrl)}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] TaskCanceledException in FetchAddonMetaInternalAsync | URL: {addonUrl} | Type: {type} | ID: {id} | Message: {ex.Message} | StackTrace: {ex.StackTrace}");
                var entry = new AddonMetaCacheEntry { HasValue = false, Meta = null };
                _addonMetaCache[cacheKey] = (entry, DateTime.Now.Add(_addonMetaNegativeCacheDuration));
                return entry;
            }
            catch (OperationCanceledException ex)
            {
                trace?.Log("AddonCache", $"OperationCanceledException on {GetHostSafe(addonUrl)}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MetadataProvider] OperationCanceledException in FetchAddonMetaInternalAsync | URL: {addonUrl} | Type: {type} | ID: {id} | Message: {ex.Message} | StackTrace: {ex.StackTrace}");
                var entry = new AddonMetaCacheEntry { HasValue = false, Meta = null };
                _addonMetaCache[cacheKey] = (entry, DateTime.Now.Add(_addonMetaNegativeCacheDuration));
                return entry;
            }
            catch (Exception ex)
            {
                trace?.Log("AddonCache", $"Fetch error on {GetHostSafe(addonUrl)}: {ex.Message}");
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

        private async Task EnrichWithIptvMovieAsync(UnifiedMetadata metadata, Models.IMediaStream vod, MetadataTrace? trace = null, CancellationToken ct = default)
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
                    
                    // [Senior] Effortless matchmaking against internal IPTV indices
                    var match = IptvMatchService.Instance.MatchToIptvById(metadata.ImdbId, "movie") as VodStream;
                    
                    if (match == null && !string.IsNullOrEmpty(metadata.Title) && metadata.Title != "Unknown" && metadata.Title != "Loading...")
                    {
                        match = IptvMatchService.Instance.MatchToIptv(metadata.Title, metadata.Year, "movie") as VodStream;
                    }

                    if (match != null)
                    {
                        trace?.Log("IPTV", $"Match Success: {match.Name}");
                        
                        // [Senior] Register confirmed match in high-perf registry
                        if (string.IsNullOrEmpty(match.ImdbId) || match.ImdbId != metadata.ImdbId)
                        {
                            IptvMatchService.Instance.RegisterManualMatch(match, metadata.ImdbId);
                        }

                        System.Diagnostics.Debug.WriteLine($"[IPTV_MATCH] Match Details: Title='{match.Name}', ProviderImdb='{match.IMDbId}', InternalId={match.StreamId}");
                        streamId = match.StreamId;
                        metadata.IsAvailableOnIptv = true;
                        
                        // Seed basic info from the match early
                        if (string.IsNullOrEmpty(metadata.Title) || metadata.Title == "Loading...") metadata.Title = match.Name;
                        if (string.IsNullOrEmpty(metadata.PosterUrl)) metadata.PosterUrl = match.StreamIcon;
                        if (string.IsNullOrEmpty(metadata.Year)) metadata.Year = match.Year;
                        if (metadata.Rating == 0 && double.TryParse(match.Rating, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r)) metadata.Rating = r;
                    }
                    else
                    {
                        trace?.Log("IPTV", $"Match Failed for: {metadata.Title}");
                    }
                }
                else if (vod is VodStream vs)
                {
                    // If we already have a VodStream from the library, set availability immediately
                    metadata.IsAvailableOnIptv = true;
                    if (string.IsNullOrEmpty(metadata.Title) || metadata.Title == "Loading...") metadata.Title = vs.Name;
                    if (string.IsNullOrEmpty(metadata.PosterUrl)) metadata.PosterUrl = vs.StreamIcon;
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

                        // 3. Classification
                        if (string.IsNullOrEmpty(metadata.AgeRating))
                            metadata.AgeRating = !string.IsNullOrEmpty(result.Info.MpaaRating) ? result.Info.MpaaRating : result.Info.Age;
                        
                        if (string.IsNullOrEmpty(metadata.Country))
                            metadata.Country = result.Info.Country;

                        // 4. Trailer
                        if (string.IsNullOrEmpty(metadata.TrailerUrl) && !string.IsNullOrEmpty(result.Info.YoutubeTrailer))
                        {
                            AddTrailerCandidate(metadata, result.Info.YoutubeTrailer, preferPrimary: true);
                        }

                        // 5. Technical Info
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
                            if (double.TryParse(result.Info.Rating?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double ratingValue))
                            {
                                metadata.Rating = ratingValue;
                            }
                        }

                        // 7. Year
                        if (string.IsNullOrEmpty(metadata.Year))
                            metadata.Year = result.Info.Releasedate;

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

        private async Task EnrichWithIptvAsync(UnifiedMetadata metadata, SeriesStream series, MetadataTrace? trace = null, CancellationToken ct = default)
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
                        
                    // [Senior] Modernized Series Matchmaking against internal indices
                    var match = IptvMatchService.Instance.MatchToIptvById(metadata.ImdbId, "series") as SeriesStream;
                    
                    if (match == null && !string.IsNullOrEmpty(metadata.Title) && metadata.Title != "Unknown" && metadata.Title != "Loading...")
                    {
                        match = IptvMatchService.Instance.MatchToIptv(metadata.Title, metadata.Year, "series") as SeriesStream;
                    }

                    if (match == null && !string.IsNullOrEmpty(metadata.ImdbId))
                    {
                        string? mappedId = IdMappingService.Instance.GetTmdbForImdb(metadata.ImdbId);
                        if (!string.IsNullOrEmpty(mappedId))
                        {
                            trace?.Log("IPTV", $"ID conversion (Service): {metadata.ImdbId} -> {mappedId}");
                            match = IptvMatchService.Instance.MatchToIptvById(mappedId, "series") as SeriesStream;
                        }
                        
                        if (match == null)
                        {
                            AppLogger.Info($"[Enrich-Iptv] No direct IMDb match for '{metadata.Title}'. Trying TMDB network conversion...");
                            var tmdbSearch = await TmdbHelper.GetTvByExternalIdAsync(metadata.ImdbId, ct: ct);
                            if (tmdbSearch != null)
                            {
                                string tmdbIdStr = tmdbSearch.Id.ToString();
                                IdMappingService.Instance.RegisterMapping(metadata.ImdbId, tmdbIdStr);
                                AppLogger.Info($"[Enrich-Iptv] Converted {metadata.ImdbId} -> TMDB ID {tmdbIdStr}. Searching IPTV again...");
                                match = IptvMatchService.Instance.MatchToIptvById(tmdbIdStr, "series") as SeriesStream;
                            }
                        }
                    }

                    if (match != null)
                    {
                        // [Senior] Persist manual match linkage
                        if (string.IsNullOrEmpty(match.ImdbId) || match.ImdbId != metadata.ImdbId)
                        {
                            IptvMatchService.Instance.RegisterManualMatch(match, metadata.ImdbId);
                        }

                        AppLogger.Info($"[Enrich-Iptv] Match SUCCESS: '{match.Name}' (ID: {match.SeriesId}, IMDb: {match.IMDbId})");
                        seriesId = match.SeriesId;
                        metadata.IsAvailableOnIptv = true;

                        // Seed basic info from match
                        bool titleIsPlaceholder = string.IsNullOrEmpty(metadata.Title) || metadata.Title == "Loading...";
                        bool titleLooksLikeEpisode = !titleIsPlaceholder && IsGenericEpisodeTitle(metadata.Title, match.Name);
                        
                        bool shouldPromoteMatchName = titleIsPlaceholder || titleLooksLikeEpisode;
                        if (!shouldPromoteMatchName && !string.IsNullOrEmpty(metadata.SubTitle) && 
                            metadata.SubTitle.Contains(match.Name, StringComparison.OrdinalIgnoreCase) &&
                            !metadata.Title.Contains(match.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            shouldPromoteMatchName = true;
                            trace?.Log("IPTV", $"Title Promotion: '{metadata.Title}' -> '{match.Name}' (Verified via SubTitle)");
                        }

                        if (shouldPromoteMatchName) metadata.Title = match.Name;
                        if (string.IsNullOrEmpty(metadata.PosterUrl)) metadata.PosterUrl = match.Cover;
                        if (string.IsNullOrEmpty(metadata.Year)) metadata.Year = match.Year;
                        if (string.IsNullOrEmpty(metadata.Overview)) metadata.Overview = match.Plot;
                    }
                    else
                    {
                        AppLogger.Warn($"[Enrich-Iptv] Match FAILED for: '{metadata.Title}' (IMDb: {metadata.ImdbId})");
                    }
                }
                else
                {
                    AppLogger.Info($"[Enrich-Iptv] Using existing series stream: {series.Name} (ID: {series.SeriesId})");
                    metadata.IsAvailableOnIptv = true;
                    if (string.IsNullOrEmpty(metadata.Title) || metadata.Title == "Loading...") metadata.Title = series.Name;
                    if (string.IsNullOrEmpty(metadata.PosterUrl)) metadata.PosterUrl = series.Cover;
                    if (string.IsNullOrEmpty(metadata.Year)) metadata.Year = series.Year;
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
                        AppLogger.Info($"[Enrich-Iptv] Received series info (Name: {info.Info.Name}, Rating: {info.Info.Rating}, Release: {info.Info.Releasedate})");
                        
                        // Map Basic Info
                        if (string.IsNullOrEmpty(metadata.Overview)) metadata.Overview = info.Info.Plot;
                        if (string.IsNullOrEmpty(metadata.Genres)) metadata.Genres = info.Info.Genre;
                        if (string.IsNullOrEmpty(metadata.Title)) metadata.Title = info.Info.Name;
                        if (string.IsNullOrEmpty(metadata.BackdropUrl)) metadata.BackdropUrl = info.Info.Cover;
                        if (string.IsNullOrEmpty(metadata.PosterUrl)) metadata.PosterUrl = info.Info.Cover;

                        // Map Rating & Year
                        string ratingStr = info.Info.Rating?.ToString();
                        if (string.IsNullOrEmpty(ratingStr)) ratingStr = series.Rating;

                        if (double.TryParse(ratingStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
                        {
                            metadata.Rating = r;
                        }
                        if (string.IsNullOrEmpty(metadata.Year)) metadata.Year = info.Info.Releasedate?.ToString();

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
                                    if (string.IsNullOrEmpty(extension)) extension = "mkv"; 
                                    if (!extension.StartsWith(".")) extension = "." + extension;

                                    string streamUrl = $"{App.CurrentLogin.Host}/series/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{ep.Id}{extension}";

                                    seasonDef.Episodes.Add(new UnifiedEpisode
                                    {
                                        Id = ep.Id,
                                        Title = ep.Title,
                                        IptvSourceTitle = ep.Title, 
                                        IptvSeriesId = seriesId, 
                                        Overview = ep.Info?.Plot, 
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
                                             existingEp.StreamUrl = streamUrl;
                                             existingEp.IptvSourceTitle = ep.Title; 
                                             existingEp.IptvSeriesId = seriesId; 
                                             if (string.IsNullOrEmpty(existingEp.Id)) existingEp.Id = ep.Id;
                                             if (string.IsNullOrEmpty(existingEp.Overview)) existingEp.Overview = ep.Info?.Plot;
                                             if (string.IsNullOrEmpty(existingEp.RuntimeFormatted)) existingEp.RuntimeFormatted = ep.Info?.Duration;
                                             mergeCount++;
                                         }
                                         else
                                         {
                                             existingSeason.Episodes.Add(new UnifiedEpisode
                                             {
                                                 Id = ep.Id,
                                                 Title = ep.Title,
                                                 IptvSourceTitle = ep.Title, 
                                                 IptvSeriesId = seriesId, 
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

        public async Task EnrichSeasonAsync(UnifiedMetadata metadata, int seasonNumber, MetadataTrace? trace = null, CancellationToken ct = default)
        {
            if (metadata?.TmdbInfo == null) return;
            
            var season = metadata.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
            if (season == null) return;

            // Fetch detailed season info
            if (trace != null) trace?.Log("TMDB", $"Enriching Season {seasonNumber}...");
            var tmdbSeason = await TmdbHelper.GetSeasonDetailsAsync(metadata.TmdbInfo.Id, seasonNumber, ct: ct);
            if (tmdbSeason?.Episodes == null) 
            {
                if (trace != null) trace?.Log("TMDB", $"Season {seasonNumber} NOT found or empty on TMDB.");
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
                enSeason = await TmdbHelper.GetSeasonDetailsAsync(metadata.TmdbInfo.Id, seasonNumber, "en-US", ct);
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
                trace?.Log("TMDB", $"Season {seasonNumber} Enrichment Completed: {initialCount} -> {season.Episodes.Count} (Updated {updated}, Added {added})");

            // Ensure correct order
            season.Episodes = season.Episodes.OrderBy(e => e.EpisodeNumber).ToList();
            season.IsEnrichedByTmdb = true;
        }

        private void MapStremioToUnified(StremioMeta stremio, UnifiedMetadata unified, bool overwritePrimary, MetadataTrace? trace = null, bool quiet = false)
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
                !string.IsNullOrWhiteSpace(stremio.Originalname) &&
                !string.Equals(stremio.Originalname, unified.Title, StringComparison.OrdinalIgnoreCase) && 
                !IsGenericEpisodeTitle(stremio.Originalname, unified.Title))
            {
                unified.SubTitle = stremio.Originalname;
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
            
            // Map high-quality landscape poster as a backdrop if provided
            if (!string.IsNullOrEmpty(stremio.LandscapePoster))
            {
                AddUniqueBackdrop(unified, UpgradeImageUrl(stremio.LandscapePoster));
            }
            
            // Map Backdrop Gallery from AppExtras
            if (stremio.AppExtras?.Backdrops?.Count > 0)
            {
                int mappedCount = 0;
                foreach (var bg in stremio.AppExtras.Backdrops.Take(15))
                {
                    if (!string.IsNullOrEmpty(bg.Url))
                    {
                        if (AddUniqueBackdrop(unified, UpgradeImageUrl(bg.Url))) mappedCount++;
                    }
                }
                if (mappedCount > 0) trace?.Log("Mapping", $"Enriched gallery with {mappedCount} backdrops from AppExtras.");
            }


            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Logo)) || string.IsNullOrEmpty(unified.LogoUrl))
            {
                string logoUrl = stremio.Logo;
                
                // Fallback to AppExtras.Logo if root logo is missing
                if (string.IsNullOrEmpty(logoUrl) && !string.IsNullOrEmpty(stremio.AppExtras?.Logo))
                {
                    logoUrl = stremio.AppExtras.Logo;
                }

                if (!string.IsNullOrEmpty(logoUrl))
                {
                    // Quality Protection for Logo: Preserve Fanart/TMDB logos over Metahub ones
                    if (!string.IsNullOrEmpty(unified.LogoUrl) && overwritePrimary)
                    {
                        if (GetLogoScore(logoUrl) >= GetLogoScore(unified.LogoUrl))
                        {
                            unified.LogoUrl = logoUrl;
                        }
                        else
                        {
                            trace?.Log("Mapping", $"Quality Protection: Preserving existing high-res logo over new {logoUrl}");
                        }
                    }
                    else
                    {
                        unified.LogoUrl = logoUrl;
                    }
                }
            }

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Releaseinfo)) || string.IsNullOrEmpty(unified.Year))
                unified.Year = stremio.Releaseinfo;

            if ((overwritePrimary && !string.IsNullOrEmpty(stremio.Genres)) || string.IsNullOrEmpty(unified.Genres))
                unified.Genres = stremio.Genres;

            // [NEW] Map Country, Status, Writer, Certification from AIOMetadata
            if (!string.IsNullOrEmpty(stremio.Country) && (overwritePrimary || string.IsNullOrEmpty(unified.Country)))
                unified.Country = stremio.Country;

            if (!string.IsNullOrEmpty(stremio.Status) && (overwritePrimary || string.IsNullOrEmpty(unified.Status)))
                unified.Status = stremio.Status;

            if (!string.IsNullOrEmpty(stremio.AppExtras?.Certification) && (overwritePrimary || string.IsNullOrEmpty(unified.Certification)))
                unified.Certification = stremio.AppExtras.Certification;

            if (stremio.Writer != null && stremio.Writer.Count > 0 && (overwritePrimary || string.IsNullOrEmpty(unified.Writers)))
                unified.Writers = string.Join(", ", stremio.Writer);
            else if (stremio.AppExtras?.Writers?.Count > 0 && (overwritePrimary || string.IsNullOrEmpty(unified.Writers)))
                unified.Writers = string.Join(", ", stremio.AppExtras.Writers.Select(w => w.Name));
            else if (stremio.Links != null && (overwritePrimary || string.IsNullOrEmpty(unified.Writers)))
            {
                var writers = stremio.Links.Where(l => string.Equals(l.Category, "Writers", StringComparison.OrdinalIgnoreCase) || string.Equals(l.Category, "Writer", StringComparison.OrdinalIgnoreCase)).Select(l => l.Name).ToList();
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
                trace?.Log("Mapping", $"Mapped {incomingDirectors.Count} Directors from AppExtras for {unified.Title}");
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
                        trace?.Log("Mapping", $"Found {stremio.AppExtras.Cast.Count} Cast in AppExtras for {unified.Title}");
                        incomingCast = stremio.AppExtras.Cast.Take(25).Select(c => new UnifiedCast 
                        { 
                            Name = c.Name, 
                            Character = c.Character,
                            ProfileUrl = c.Photo // Support for addons that return 'photo'
                        }).ToList();
                    }
                    else if (stremio.CreditsCast?.Count > 0)
                    {
                        trace?.Log("Mapping", $"Found {stremio.CreditsCast.Count} Cast in CreditsCast for {unified.Title}");
                        incomingCast = stremio.CreditsCast.Take(25).Select(c => new UnifiedCast
                        {
                            Name = c.Name,
                            Character = c.Character,
                            ProfileUrl = !string.IsNullOrEmpty(c.ProfilePath) ? $"https://image.tmdb.org/t/p/w185{c.ProfilePath}" : null,
                            TmdbId = (c.Id.ValueKind == System.Text.Json.JsonValueKind.Number && c.Id.TryGetInt32(out var idVal)) ? idVal : (int.TryParse(c.Id.ToString(), out var parsed) ? parsed : null)
                        }).ToList();
                    }
                    else if (stremio.Cast?.Count > 0)
                    {
                        trace?.Log("Mapping", $"Found {stremio.Cast.Count} Cast in basic Cast list for {unified.Title}");
                        incomingCast = stremio.Cast.Take(25).Select(name => new UnifiedCast { Name = name }).ToList();
                    }
                    else if (stremio.Links?.Count > 0)
                    {
                        // Some providers store cast members in the 'links' list
                        var castLinks = stremio.Links.Where(l => string.Equals(l.Category, "Cast", StringComparison.OrdinalIgnoreCase)).ToList();
                        if (castLinks.Count > 0)
                        {
                            trace?.Log("Mapping", $"Harvested {castLinks.Count} Cast from links for {unified.Title}");
                            incomingCast = castLinks.Take(25).Select(l => new UnifiedCast { Name = l.Name }).ToList();
                        }
                    }

                    if (incomingCast != null)
                    {
                        trace?.Log("Mapping", $"Mapped {incomingCast.Count} Cast for {unified.Title}");
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
                        trace?.Log("Mapping", $"Final Mapped Cast Count: {unified.Cast.Count}");
                    }
                }
            }

            if (overwritePrimary || unified.Directors == null || unified.Directors.Count == 0)
            {
                List<UnifiedCast> incomingDirectors = null;

                // 1. Priority: AppExtras.Directors (often contains photos)
                if (stremio.AppExtras?.Directors?.Count > 0)
                {
                    incomingDirectors = stremio.AppExtras.Directors.Take(10).Select(d => new UnifiedCast
                    {
                        Name = d.Name,
                        Character = "Yönetmen",
                        ProfileUrl = d.Photo
                    }).ToList();
                }
                // 2. Fallback: CreditsCrew (TMDB-style)
                else if (stremio.CreditsCrew?.Count > 0 && stremio.CreditsCrew.Any(c => c.Job == "Director"))
                {
                    incomingDirectors = stremio.CreditsCrew.Where(c => c.Job == "Director").Take(10).Select(c => new UnifiedCast
                    {
                        Name = c.Name,
                        Character = "Yönetmen",
                        ProfileUrl = !string.IsNullOrEmpty(c.ProfilePath) ? $"https://image.tmdb.org/t/p/w185{c.ProfilePath}" : null
                    }).ToList();
                }
                // 3. Fallback: Search in AppExtras.Cast for "Director" character
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
                // 4. Fallback: Basic Director list
                else if (stremio.Director?.Count > 0)
                {
                    incomingDirectors = stremio.Director.Take(10).Select(name => new UnifiedCast { Name = name, Character = "Yönetmen" }).ToList();
                }
                // 5. Fallback: Links category
                else if (stremio.Links?.Count > 0)
                {
                    var dirLinks = stremio.Links.Where(l => string.Equals(l.Category, "Directors", StringComparison.OrdinalIgnoreCase) || string.Equals(l.Category, "Director", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (dirLinks.Count > 0)
                    {
                        incomingDirectors = dirLinks.Take(5).Select(l => new UnifiedCast { Name = l.Name, Character = "Yönetmen" }).ToList();
                    }
                }

                if (incomingDirectors != null)
                {
                    trace?.Log("Mapping", $"Mapped {incomingDirectors.Count} Directors for {unified.Title}");
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
            
            if (stremio.Imdbrating != null)
            {
                // [FIX] Culture-proof rating parsing. AioStreams returns strings with ".", while system culture might use ",".
                string ratingStr = stremio.Imdbrating.ToString().Replace(",", ".");
                if (double.TryParse(ratingStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0)
                {
                    if (overwritePrimary || unified.Rating == 0)
                        unified.Rating = r;
                }
            }

            if ((stremio.Trailers != null && stremio.Trailers.Any()) || (stremio.TrailerStreams != null && stremio.TrailerStreams.Any()) || !string.IsNullOrEmpty(stremio.AppExtras?.Trailer))
            {
                if (stremio.Trailers != null)
                {
                    foreach (var trailer in stremio.Trailers
                        .Where(x => !string.IsNullOrWhiteSpace(x.Source))
                        .OrderByDescending(x => x.Type?.Equals("trailer", StringComparison.OrdinalIgnoreCase) == true || string.IsNullOrWhiteSpace(x.Type)))
                    {
                        AddTrailerCandidate(unified, trailer.Source, preferPrimary: overwritePrimary || string.IsNullOrEmpty(unified.TrailerUrl));
                    }
                }
                
                if (stremio.TrailerStreams != null)
                {
                    foreach (var trailer in stremio.TrailerStreams.Where(x => !string.IsNullOrWhiteSpace(x.YtId)))
                    {
                        AddTrailerCandidate(unified, trailer.YtId, preferPrimary: overwritePrimary || string.IsNullOrEmpty(unified.TrailerUrl));
                    }
                }

                // Fallback to AppExtras.Trailer (often contains a single YouTube ID or URL)
                if (!string.IsNullOrEmpty(stremio.AppExtras?.Trailer))
                {
                    AddTrailerCandidate(unified, stremio.AppExtras.Trailer, preferPrimary: overwritePrimary || string.IsNullOrWhiteSpace(unified.TrailerUrl));
                }
            }
            
            if (!quiet)
            {
                string msg = $"Mapped data [Primary={overwritePrimary}] for: '{unified.Title}' (Source: {stremio.Name} [{stremio.Id}])";
                if (trace != null) trace?.Log("Seed", msg);
                else AppLogger.Info($"[MetadataProvider] {msg}");
            }
        }

        private async Task<TmdbMovieResult?> EnrichWithTmdbAsync(UnifiedMetadata metadata, MetadataContext context, bool isContinueWatching = false, CancellationToken ct = default)
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
                    var searchResult = await TmdbHelper.GetTvByExternalIdAsync(metadata.ImdbId, ct: ct);
                    if (searchResult != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: IMDb lookup found ID = {searchResult.Id}, fetching full details...");
                        tmdb = await TmdbHelper.GetTvByIdAsync(searchResult.Id, ct: ct);
                    }
                }

                // Step 2: Try tmdb: prefix ID
                if (tmdb == null && metadata.ImdbId != null && metadata.ImdbId.StartsWith("tmdb:"))
                {
                    int.TryParse(metadata.ImdbId.Replace("tmdb:", ""), out int tvId);
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Trying tmdb: ID = {tvId}");
                    if (tvId > 0) tmdb = await TmdbHelper.GetTvByIdAsync(tvId, ct: ct);
                }
                
                // Step 3: Try Title Search with Year
                if (tmdb == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Trying title search = \"{metadata.Title}\", year = \"{metadata.Year}\"");
                    var titleSearch = await TmdbHelper.SearchTvAsync(metadata.Title, TitleHelper.ExtractYear(metadata.Year.AsSpan()).ToString(), ct: ct);
                    if (titleSearch != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Title search found ID = {titleSearch.Id}, fetching full details...");
                        tmdb = await TmdbHelper.GetTvByIdAsync(titleSearch.Id, ct: ct);
                    }
                    else if (!string.IsNullOrEmpty(metadata.SubTitle))
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Primary title failed. Trying SubTitle fallback = \"{metadata.SubTitle}\"");
                        var subTitleSearch = await TmdbHelper.SearchTvAsync(metadata.SubTitle, TitleHelper.ExtractYear(metadata.Year.AsSpan()).ToString(), ct: ct);
                        if (subTitleSearch != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: SubTitle search found ID = {subTitleSearch.Id}");
                            tmdb = await TmdbHelper.GetTvByIdAsync(subTitleSearch.Id, ct: ct);
                        }
                    }
                }

                // Step 4: Try Title Search WITHOUT Year
                if (tmdb == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Trying title search WITHOUT YEAR = \"{metadata.Title}\"");
                    var titleSearch = await TmdbHelper.SearchTvAsync(metadata.Title, null, ct: ct);
                    if (titleSearch != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] SERIES: Title search (no year) found ID = {titleSearch.Id}, fetching full details...");
                        tmdb = await TmdbHelper.GetTvByIdAsync(titleSearch.Id, ct: ct);
                    }
                }
            }
            else // MOVIE
            {
                // Try IMDb ID first
                if (tmdb == null && metadata.ImdbId != null && metadata.ImdbId.StartsWith("tt"))
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: Trying IMDb ID = {metadata.ImdbId}");
                    var extResult = await TmdbHelper.GetMovieByExternalIdAsync(metadata.ImdbId, ct: ct);
                    if (extResult != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: IMDb lookup found ID = {extResult.Id}, fetching full details...");
                        tmdb = await TmdbHelper.GetMovieByIdAsync(extResult.Id, ct: ct);
                    }
                }

                if (tmdb == null && metadata.ImdbId != null && metadata.ImdbId.StartsWith("tmdb:"))
                {
                    int.TryParse(metadata.ImdbId.Replace("tmdb:", ""), out int movieId);
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: Trying tmdb: ID = {movieId}");
                    if (movieId > 0) tmdb = await TmdbHelper.GetMovieByIdAsync(movieId, ct: ct);
                }

                if (tmdb == null)
                {
                    var searchResult = await TmdbHelper.SearchMovieAsync(metadata.Title, TitleHelper.ExtractYear(metadata.Year.AsSpan()).ToString(), ct: ct);
                    if (searchResult != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: Search found ID = {searchResult.Id}, fetching full details...");
                        tmdb = await TmdbHelper.GetMovieByIdAsync(searchResult.Id, ct: ct);
                    }
                    else if (!string.IsNullOrEmpty(metadata.SubTitle))
                    {
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: Primary title failed. Trying SubTitle fallback = \"{metadata.SubTitle}\"");
                        var subSearchResult = await TmdbHelper.SearchMovieAsync(metadata.SubTitle, TitleHelper.ExtractYear(metadata.Year.AsSpan()).ToString(), ct: ct);
                        if (subSearchResult != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] MOVIE: SubTitle Search found ID = {subSearchResult.Id}");
                            tmdb = await TmdbHelper.GetMovieByIdAsync(subSearchResult.Id, ct: ct);
                        }
                    }
                }
            }

            if (tmdb != null)
            {
                AppLogger.Info($"[TMDB-Enrich] SUCCESS: Found TMDB ID = {tmdb.Id}, Title = {tmdb.DisplayTitle}");
                metadata.MetadataSourceInfo = "TMDB";
                metadata.DataSource = "TMDB";

                // [FIX] Store the resolved canonical ID if we don't have one yet. Favor IMDb, fallback to tmdb:ID.
                // This allows subsequent addon probing to use a real ID even for IPTV items.
                string? resolvedId = tmdb.ResolvedImdbId;
                if (!IsImdbId(resolvedId)) resolvedId = $"tmdb:{tmdb.Id}";

                if (IsCanonicalId(resolvedId) && !IsCanonicalId(metadata.ImdbId))
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] Propagating Resolved ID: {resolvedId}");
                    
                    // [FIX] Persist the mapping if the original ID was raw
                    if (!string.IsNullOrEmpty(metadata.MetadataId) && !IsCanonicalId(metadata.MetadataId))
                    {
                        _rawToCanonicalIdCache[metadata.MetadataId] = resolvedId;
                        _ = SaveMappingCacheAsync();
                    }
                    
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
                    var localizedTrailerKey = await TmdbHelper.GetTrailerKeyAsync(tmdb.Id, metadata.IsSeries, ct: ct);
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
                             AddTrailerCandidate(metadata, fullTrailerUrl, preferPrimary: true);
                         }
                         else
                         {
                             AddTrailerCandidate(metadata, fullTrailerUrl, preferPrimary: false);
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
                            var seasonDetails = await TmdbHelper.GetSeasonDetailsAsync(tmdb.Id, tmdbSeason.SeasonNumber, ct: ct);
                            if (seasonDetails?.Episodes != null && seasonDetails.Episodes.Count > 0)
                            {
                                bool areTitlesGeneric = seasonDetails.Episodes.Any(e => IsGenericEpisodeTitle(e.Name, metadata.Title));
                                bool areOverviewsMissing = seasonDetails.Episodes.Any(e => string.IsNullOrEmpty(e.Overview));
                                
                                TmdbSeasonDetails? enSeasonDetails = null;
                                if (areTitlesGeneric || areOverviewsMissing)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[TMDB-Enrich] Season {tmdbSeason.SeasonNumber}: Missing details (TitlesGeneric={areTitlesGeneric}, OverviewsMissing={areOverviewsMissing}). Fetching English fallback...");
                                    enSeasonDetails = await TmdbHelper.GetSeasonDetailsAsync(tmdb.Id, tmdbSeason.SeasonNumber, "en-US", ct);
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
                            if (overwrite || existingEpisode.Releasedate == null)
                                existingEpisode.Releasedate = existingEpisode.AirDate;

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
                                Releasedate = !string.IsNullOrEmpty(vid.Released) && DateTime.TryParse(vid.Released, out var d4) ? d4 : null,
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

        public static bool IsPlaceholderOverview(string? overview)
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

        private bool AddUniqueBackdrop(UnifiedMetadata metadata, string url, Action<string> onBackdropFound = null)
        {
            if (string.IsNullOrEmpty(url) || ImageHelper.IsPlaceholder(url)) return false;
            
            lock (metadata.SyncRoot)
            {
                if (metadata.BackdropUrls == null) metadata.BackdropUrls = new List<string>();

                string id = ExtractImageId(url);
                if (string.IsNullOrEmpty(id))
                {
                    if (!metadata.BackdropUrls.Contains(url))
                    {
                        metadata.BackdropUrls.Add(url);
                        onBackdropFound?.Invoke(url);
                        return true;
                    }
                    return false;
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
                    return true;
                }
                else
                {
                    string existingUrl = metadata.BackdropUrls[existingIndex];
                    if (GetQualityScore(url) > GetQualityScore(existingUrl))
                    {
                        metadata.BackdropUrls[existingIndex] = url;
                        return true;
                    }
                }
            }
            return false;
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

        private static string? NormalizeTrailerCandidate(string? source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            string value = source.Trim();

            if (!value.Contains("/") && !value.Contains("."))
            {
                return $"https://www.youtube.com/watch?v={value}";
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                string host = uri.Host.ToLowerInvariant();
                if (host.Contains("youtu.be"))
                {
                    string id = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(id))
                        return $"https://www.youtube.com/watch?v={id}";
                }

                if (host.Contains("youtube.com"))
                {
                    string? v = ExtractQueryParam(uri.Query, "v");
                    if (!string.IsNullOrWhiteSpace(v))
                        return $"https://www.youtube.com/watch?v={v}";

                    var segments = uri.AbsolutePath.Trim('/').Split('/');
                    int embedIndex = Array.FindIndex(segments, s => s.Equals("embed", StringComparison.OrdinalIgnoreCase));
                    if (embedIndex >= 0 && embedIndex + 1 < segments.Length)
                        return $"https://www.youtube.com/watch?v={segments[embedIndex + 1]}";
                }
            }

            return value;
        }

        private static string GetTrailerDedupKey(string normalized)
        {
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            {
                string host = uri.Host.ToLowerInvariant();
                if (host.Contains("youtube.com"))
                {
                    string? v = ExtractQueryParam(uri.Query, "v");
                    if (!string.IsNullOrWhiteSpace(v))
                        return $"yt:{v.Trim().ToLowerInvariant()}";
                }
            }

            return normalized.Trim().ToLowerInvariant();
        }

        private static int GetTrailerPriority(string normalized)
        {
            string value = normalized.ToLowerInvariant();
            if (value.EndsWith(".mp4") || value.Contains(".mp4?") || value.Contains(".m3u8"))
                return 0;
            if (value.Contains("youtube.com/watch?v="))
                return 1;
            if (value.Contains("youtube.com") || value.Contains("youtu.be"))
                return 2;
            return 3;
        }

        private void AddTrailerCandidate(UnifiedMetadata metadata, string? rawSource, bool preferPrimary = false)
        {
            string? normalized = NormalizeTrailerCandidate(rawSource);
            if (string.IsNullOrWhiteSpace(normalized)) return;

            metadata.TrailerCandidates ??= new List<string>();

            string key = GetTrailerDedupKey(normalized);
            bool alreadyExists = metadata.TrailerCandidates.Any(x => GetTrailerDedupKey(x) == key);
            if (!alreadyExists)
            {
                metadata.TrailerCandidates.Add(normalized);
            }

            metadata.TrailerCandidates = metadata.TrailerCandidates
                .OrderBy(GetTrailerPriority)
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (preferPrimary || string.IsNullOrWhiteSpace(metadata.TrailerUrl))
            {
                metadata.TrailerUrl = normalized;
            }
            else if (!string.IsNullOrWhiteSpace(metadata.TrailerUrl))
            {
                string? normalizedPrimary = NormalizeTrailerCandidate(metadata.TrailerUrl);
                if (!string.IsNullOrWhiteSpace(normalizedPrimary))
                {
                    string primaryKey = GetTrailerDedupKey(normalizedPrimary);
                    int existingIndex = metadata.TrailerCandidates.FindIndex(x => GetTrailerDedupKey(x) == primaryKey);
                    if (existingIndex > 0)
                    {
                        string item = metadata.TrailerCandidates[existingIndex];
                        metadata.TrailerCandidates.RemoveAt(existingIndex);
                        metadata.TrailerCandidates.Insert(0, item);
                    }
                }
            }
        }

        private static string? ExtractQueryParam(string query, string key)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            string trimmed = query.TrimStart('?');
            foreach (string part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string name = part.Substring(0, eq);
                if (!name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                string value = part.Substring(eq + 1);
                return Uri.UnescapeDataString(value);
            }

            return null;
        }

        private string? _addonHashCache;
        private DateTime _lastAddonHashTime;
        private readonly System.Threading.Lock _hashLock = new();

        /// <summary>
        /// Optimized addon order hash with caching to prevent redundant calculations in batch loops.
        /// </summary>
        private string GetAddonOrderHash()
        {
            lock (_hashLock)
            {
                // [PERF] Cache hash for 30 seconds to prevent O(N^2) overhead during catalog loads
                if (_addonHashCache != null && (DateTime.Now - _lastAddonHashTime).TotalSeconds < 30)
                    return _addonHashCache;

                var addonUrls = StremioAddonManager.Instance.GetAddons();
                if (addonUrls == null || addonUrls.Count == 0) return "none";

                // [STABILITY FIX] Use stable FNV-1a hash instead of string.GetHashCode()
                string raw = string.Join("|", addonUrls);
                uint hash = 2166136261;
                foreach (char c in raw)
                {
                    hash = (hash ^ (uint)c) * 16777619;
                }
                _addonHashCache = hash.ToString("X8");
                _lastAddonHashTime = DateTime.Now;
                return _addonHashCache;
            }
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
        private string GetContentSummary(UnifiedMetadata meta)
        {
            if (meta == null) return "None";
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(meta.Overview)) sb.Append("Desc, ");
            if (meta.Rating > 0) sb.Append("Rating, ");
            if (meta.Genres != null) sb.Append("Genres, ");
            if (meta.BackdropUrls != null && meta.BackdropUrls.Count > 0) sb.Append($"{meta.BackdropUrls.Count} BGs, ");
            if (!string.IsNullOrEmpty(meta.TrailerUrl)) sb.Append("Trailer, ");
            if (!string.IsNullOrEmpty(meta.LogoUrl)) sb.Append("Logo, ");
            if (meta.IsSeries && meta.Seasons != null && meta.Seasons.Count > 0) sb.Append($"{meta.Seasons.Count} Seasons, ");
            
            string result = sb.ToString().TrimEnd(',', ' ');
            return string.IsNullOrEmpty(result) ? "Minimal" : result;
        }
    }
}

