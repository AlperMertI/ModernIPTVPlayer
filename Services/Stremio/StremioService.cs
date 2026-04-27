using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Services.Iptv;
using ModernIPTVPlayer.Services.Metadata;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Json;
using ModernIPTVPlayer;
using System.Linq;

namespace ModernIPTVPlayer.Services.Stremio
{
    public class StremioService
    {
        private static readonly System.Threading.Lock _instanceLock = new();
        private static StremioService _instance;
        public static StremioService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new StremioService();
                    }
                }
                return _instance;
            }
        }

        private HttpClient _client;
        private JsonSerializerOptions _jsonOptions;

        // --- LOG BRIDGE ---
        public static Action<string>? OnLog;
        public static void Log(string msg) => OnLog?.Invoke(msg);
        // ------------------

        // In-Memory Cache for Catalogs to speed up switching
        private const int MAX_CATALOG_CACHE_SIZE = 40;
        private System.Collections.Concurrent.ConcurrentDictionary<string, List<StremioMediaStream>> _catalogCache = new();
        private System.Collections.Concurrent.ConcurrentQueue<string> _catalogCacheKeys = new();

        private readonly Dictionary<string, HashSet<StremioMediaStream>> _globalMetaIndex = new();
        private readonly System.Threading.Lock _indexLock = new();

        // **NEW: Search Session Sharing (Handoff)**
        private StremioSearchSession? _activeSession;
        public StremioSearchSession? ActiveSession => _activeSession;
        private const int RANKING_THROTTLE_MS = 600;

        private StremioService()
        {
            _client = HttpHelper.Client;
            _jsonOptions = new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString 
            };
        }

        // ==========================================
        // 1. MANIFEST (Addon Info)
        // ==========================================
        public async Task<StremioManifest> GetManifestAsync(string baseUrl)
        {
            string url = baseUrl.Trim();
            try
            {
                if (url.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url.Substring(10);
                }

                if (!url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    url = $"{url.TrimEnd('/')}/manifest.json";
                }

                string json = await _client.GetStringAsync(url);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.StremioManifest);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Error fetching manifest (URL: {url})", ex);
                return null;
            }
        }

        // ==========================================
        // 2. CATALOGS (Discovery)
        // ==========================================
        public async Task<List<StremioMediaStream>> GetCatalogItemsAsync(string baseUrl, string type, string id, string extra = "", int skip = 0)
        {
            string cacheKey = $"{baseUrl}|{type}|{id}|{extra}|{skip}";
            if (_catalogCache.TryGetValue(cacheKey, out var cachedData)) return cachedData;

            string url = $"{baseUrl.TrimEnd('/')}/catalog/{type}/{id}";
            string pathParams = extra;
            if (skip > 0)
            {
                if (string.IsNullOrEmpty(pathParams)) pathParams = $"skip={skip}";
                else pathParams += $"&skip={skip}";
            }
            if (!string.IsNullOrEmpty(pathParams)) url += $"/{pathParams}";
            url += ".json";

            try
            {
                var root = await GetCatalogAsync(url);
                
                if (root?.Metas != null)
                {
                    var result = new List<StremioMediaStream>();
                    foreach (var meta in root.Metas)
                    {
                        var stream = new StremioMediaStream(meta);
                        stream.SourceAddon = baseUrl;
                        result.Add(stream);
                    }
                    
                    _catalogCache[cacheKey] = result;
                    _catalogCacheKeys.Enqueue(cacheKey);

                    if (_catalogCache.Count > MAX_CATALOG_CACHE_SIZE)
                    {
                        if (_catalogCacheKeys.TryDequeue(out var oldKey))
                        {
                            _catalogCache.TryRemove(oldKey, out _);
                        }
                    }

                    // Index for global search
                    lock (_indexLock)
                    {
                        foreach (var stream in result) IndexStreamInternal(stream);
                    }

                    return result;
                }
            }
            catch (Exception ex)
            {
                Log($"[Catalog] ERROR: Failed to fetch {url}: {ex.Message}");
            }

            return new List<StremioMediaStream>();
        }


        // ==========================================
        // 3. META (Details)
        // ==========================================
        public async Task<StremioMeta> GetMetaAsync(string baseUrl, string type, string id, string? correlationId = null)
        {
            string url = $"{baseUrl.TrimEnd('/')}/meta/{type}/{id}.json";
            string idPrefix = correlationId != null ? $"[{correlationId}] " : "";
            System.Diagnostics.Debug.WriteLine($"{idPrefix}[StremioService] Fetching metadata URL: {url}");
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _client.SendAsync(request, cts.Token);
                
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync();

                var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.StremioMetaResponse);
                return result?.Meta;
            }
            catch (TaskCanceledException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioService] TaskCanceledException in GetMetaAsync | URL: {url} | Type: {type} | ID: {id} | Message: {ex.Message} | StackTrace: {ex.StackTrace}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioService] Exception in GetMetaAsync | URL: {url} | Type: {type} | ID: {id} | ExceptionType: {ex.GetType().Name} | Message: {ex.Message} | StackTrace: {ex.StackTrace}");
            }
            return null;
        }

        // ==========================================
        // 4. STREAMS (Playback)
        // ==========================================
        public async Task<List<StremioStream>> GetStreamsAsync(List<string> addonUrls, string type, string id, bool includeIptv = true, System.Threading.CancellationToken cancellationToken = default)
        {
            var tasks = new List<Task<List<StremioStream>>>();
            
            foreach (var baseUrl in addonUrls)
            {
                tasks.Add(Task.Run(async () => 
                {
                    try
                    {
                        string url = $"{baseUrl.TrimEnd('/')}/stream/{type}/{id}.json";
                        string json = await _client.GetStringAsync(url);
                        var response = JsonSerializer.Deserialize(json, AppJsonContext.Default.StremioStreamResponse);
                        if (response?.Streams != null)
                        {
                            foreach (var s in response.Streams)
                            {
                                s.AddonUrl = baseUrl;
                                if (string.IsNullOrEmpty(s.Name)) s.Name = "Addon";
                            }
                            return response.Streams;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Error fetching streams from {baseUrl}: {ex.Message}");
                    }
                    return new List<StremioStream>();
                }));
            }

            // IPTV Injection
            if (includeIptv)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        bool isSeries = type == "series";
                        string searchId = isSeries ? id.Split(':')[0] : id;
                        // [Senior] Effortless ID-based match against smart indices
                        var match = IptvMatchService.Instance.MatchToIptvById(searchId, isSeries ? "series" : "movie");
                        
                        if (match == null)
                        {
                            var metas = GetGlobalMetaCache(searchId);
                            var meta = metas.FirstOrDefault();
                            if (meta != null && !string.IsNullOrEmpty(meta.Title))
                            {
                                // [Senior] High-performance fuzzy match against smart indices
                                match = IptvMatchService.Instance.MatchToIptv(meta.Title, meta.Year, isSeries ? "series" : "movie");
                            }
                        }

                        if (match != null)
                        {
                            return new List<StremioStream> { new StremioStream
                            {
                                Name = (type == "series") ? "IPTV" : "IPTV (VOD)",
                                Title = match.Title,
                                Url = match.StreamUrl,
                                AddonUrl = "iptv://internal"
                            }};
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Error during IPTV stream injection: {ex.Message}");
                    }
                    return new List<StremioStream>();
                }));
            }

            var results = await Task.WhenAll(tasks);
            if (cancellationToken.IsCancellationRequested) return new List<StremioStream>();
            
            var allStreams = new List<StremioStream>();
            foreach (var list in results) allStreams.AddRange(list);
            return allStreams;
        }

        public async Task<List<StremioSubtitle>> GetSubtitlesAsync(string baseUrl, string type, string id, string extra = "")
        {
            string url = $"{baseUrl.TrimEnd('/')}/subtitles/{type}/{id}";
            try
            {
                if (!string.IsNullOrEmpty(extra)) url += $"/{extra}";
                url += ".json";

                AppLogger.Info($"[StremioService] Fetching subtitles from: {url}");

                string json = await _client.GetStringAsync(url);
                var response = JsonSerializer.Deserialize(json, AppJsonContext.Default.StremioSubtitleResponse);
                return response?.Subtitles ?? new List<StremioSubtitle>();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"GetSubtitlesAsync failed for {url}: {ex.Message}");
                return new List<StremioSubtitle>();
            }
        }

        // 5. SEARCH (Multi-Addon - Reactive & Parallel)
        // ==========================================
        public async Task<List<StremioMediaStream>> SearchAsync(string query, string type = "all", string scope = "all", Action<List<StremioMediaStream>>? onResultsFound = null, System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<StremioMediaStream>();

            // [SESSION SHARING] Check for existing running or completed session for the same query + filters
            string sessionKey = $"{query}|{type}|{scope}";
            if (_activeSession != null && _activeSession.Query == sessionKey)
            {
                if (onResultsFound != null) _activeSession.Subscribe(onResultsFound);
                return _activeSession.RankedResults;
            }

            // Create new session
            _activeSession?.Cancel();
            _activeSession = new StremioSearchSession(sessionKey, (q, callback, ct) => SearchInternalAsync(query, type, scope, callback, ct));
            if (onResultsFound != null) _activeSession.Subscribe(onResultsFound);

            return await Task.Run(async () => 
            {
                int timeout = 1000;
                while (timeout > 0 && _activeSession.RankedResults.Count == 0 && !_activeSession.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(100, cancellationToken);
                    timeout -= 100;
                }
                return _activeSession.RankedResults;
            }, cancellationToken);
        }

        private async Task<List<StremioMediaStream>> SearchInternalAsync(string query, string type, string scope, Action<List<StremioMediaStream>>? onResultsFound = null, System.Threading.CancellationToken cancellationToken = default)
        {
            using var lifecycle = LifecycleLog.Begin("Stremio.Search", tags: new Dictionary<string, object?>
            {
                ["query"] = query,
                ["type"] = type,
                ["scope"] = scope
            });
            var enabledAddons = StremioAddonManager.Instance.GetAddonsWithManifests();
            var allResults = new List<StremioMediaStream>();
            var resultsLock = new System.Threading.Lock();
            DateTime lastRankingTime = DateTime.MinValue;

            bool includeIptv = scope == "all" || scope == "iptv";
            bool includeLibrary = scope == "all" || scope == "library";
            bool includeAddons = scope == "all";
            lifecycle.Step("sources_selected", new Dictionary<string, object?>
            {
                ["includeIptv"] = includeIptv,
                ["includeLibrary"] = includeLibrary,
                ["includeAddons"] = includeAddons,
                ["addonCount"] = enabledAddons.Count
            });
            
            // 1. IPTV Task (Proactive or Fast)
            var iptvTask = includeIptv ? Task.Run(async () => {
                cancellationToken.ThrowIfCancellationRequested();
                var iptvResults = new List<StremioMediaStream>();
                
                if (scope == "iptv" || query.Length < 3)
                {
                    // FAST SEARCH: Using the new prefix-indexed Search method
                    // [Senior] High-performance index lookup
                    var matches = IptvMatchService.Instance.Search(query, type == "movie" ? "movie" : "series");
                    foreach (var m in matches)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var meta = new StremioMeta { Id = m.Id.ToString(), Type = m.Type ?? (type == "series" ? "series" : "movie"), Name = m.Title, Poster = m.PosterUrl };
                        iptvResults.Add(new StremioMediaStream(meta) { 
                            IsIptv = true, 
                            Title = m.Title, 
                            Year = m.Year
                        });
                    }
                }
                else
                {
                    // STANDARD SEARCH: Modernized fuzzy search using smart indices
                    var movieMatches = (type == "all" || type == "movie") && !string.IsNullOrEmpty(query) ? IptvMatchService.Instance.Search(query) : new List<IMediaStream>();
                    cancellationToken.ThrowIfCancellationRequested();
                    var seriesMatches = (type == "all" || type == "series") && !string.IsNullOrEmpty(query) ? IptvMatchService.Instance.Search(query) : new List<IMediaStream>();
                    
                    foreach (var m in movieMatches.Concat(seriesMatches))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var mType = m.Type ?? (movieMatches.Contains(m) ? "movie" : "series");
                        var meta = new StremioMeta { Id = m.Id.ToString(), Type = mType, Name = m.Title, Poster = m.PosterUrl };
                        iptvResults.Add(new StremioMediaStream(meta) { 
                            IsIptv = true, 
                            Title = m.Title, 
                            Year = m.Year
                        });
                    }
                }
                return iptvResults;
            }, cancellationToken) : Task.FromResult(new List<StremioMediaStream>());

            var libraryTask = includeLibrary ? Task.Run(() => {
                cancellationToken.ThrowIfCancellationRequested();
                var libraryItems = WatchlistManager.Instance.GetWatchlistAsMediaStreams();
                var q = query.ToLowerInvariant();
                var matches = libraryItems.Where(x => 
                    x.Title.Contains(q, StringComparison.OrdinalIgnoreCase) && 
                    (type == "all" || x.Type == type)).ToList();
                
                return matches.Select(m => {
                    if (m is StremioMediaStream s) return s;
                    // Convert generic IMediaStream to StremioMediaStream if needed, but WatchlistManager already does this
                    return m as StremioMediaStream;
                }).Where(x => x != null).ToList();
            }, cancellationToken) : Task.FromResult(new List<StremioMediaStream>());

            var addonTasks = new List<Task<List<StremioMediaStream>>>();
            lifecycle.Step("addons_starting", new Dictionary<string, object?> { ["addonCount"] = enabledAddons.Count });
            if (includeAddons)
            {
                foreach (var (baseUrl, manifest) in enabledAddons)
                {
                    if (type == "all" || type == "movie") addonTasks.Add(SearchAddonAsync(baseUrl, manifest, query, "movie", cancellationToken));
                    if (type == "all" || type == "series") addonTasks.Add(SearchAddonAsync(baseUrl, manifest, query, "series", cancellationToken));
                }
            }

            var allPendingTasks = new List<Task<List<StremioMediaStream>>>(addonTasks) { iptvTask, libraryTask };

            while (allPendingTasks.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var completedTask = await Task.WhenAny(allPendingTasks);
                    cancellationToken.ThrowIfCancellationRequested(); // Add cancellation check
                    allPendingTasks.Remove(completedTask);

                    var results = await completedTask;
                    cancellationToken.ThrowIfCancellationRequested(); // Add cancellation check
                    if (results != null && results.Count > 0)
                    {
                        lock (resultsLock)
                        {
                            foreach (var item in results)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                string normTitle = TitleHelper.Normalize(item.Title);
                                StremioMediaStream? existing = null;
                                foreach (var x in allResults)
                                {
                                    if (IsMatch(x, item, normTitle.AsSpan(), query))
                                    {
                                        existing = x;
                                        break;
                                    }
                                }
                                
                                if (existing != null)
                                {
                                    if (!string.IsNullOrEmpty(existing.IMDbId) && !string.IsNullOrEmpty(item.IMDbId))
                                    {
                                        var idA = existing.IMDbId;
                                        var idB = item.IMDbId;
                                        if (idA.StartsWith("tt") && !idB.StartsWith("tt")) IdMappingService.Instance.RegisterMapping(idA, idB);
                                        else if (idB.StartsWith("tt") && !idA.StartsWith("tt")) IdMappingService.Instance.RegisterMapping(idB, idA);
                                    }
                                    MergeItems(existing, item);
                                }
                                else
                                {
                                    allResults.Add(item);
                                }
                            }
                        }

                        if (onResultsFound != null)
                        {
                            var elapsed = (DateTime.Now - lastRankingTime).TotalMilliseconds;
                            if (elapsed >= RANKING_THROTTLE_MS || allPendingTasks.Count == 0 || allResults.Count < 10)
                            {
                                lastRankingTime = DateTime.Now;
                                var ranked = DeduplicateAndRank(allResults, query, cancellationToken);
                                onResultsFound(ranked);
                            }
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Error("Error processing search result task", ex); }
            }

            var final = DeduplicateAndRank(allResults, query, cancellationToken);
            lifecycle.Step("DONE", new Dictionary<string, object?>
            {
                ["rawResults"] = allResults.Count,
                ["finalResults"] = final.Count
            });
            return final;
        }

        public async Task<List<StremioMediaStream>> SearchAddonAsync(string addonUrl, StremioManifest manifest, string query, string type, System.Threading.CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested) return new List<StremioMediaStream>();
            var results = new List<StremioMediaStream>();
            if (manifest?.Catalogs == null) return results;

            var encodedQuery = Uri.EscapeDataString(query);
            // [FIX] Strictly respect the requested type (movie/series). 
            // Previously it was searching ALL movie/series catalogs regardless of the 'type' param.
            var searchCatalogs = manifest.Catalogs
                .Where(c => c.Type == type && c.Extra != null && c.Extra.Any(e => e.Name == "search"))
                .Where(c => !IsPeopleSearchCatalogId(c.Id))
                .ToList();

            if (LifecycleLog.ShouldLog($"stremio-catalog-filter-{addonUrl}-{type}", TimeSpan.FromSeconds(30)))
            {
                int totalSearchCatalogs = manifest.Catalogs.Count(c => c.Type == type && c.Extra != null && c.Extra.Any(e => e.Name == "search"));
                AppLogger.Info($"[Lifecycle] op=SearchAddon, phase=catalogs_selected, addon={addonUrl}, type={type}, selected={searchCatalogs.Count}, excludedPeopleSearch={Math.Max(0, totalSearchCatalogs - searchCatalogs.Count)}");
            }

            foreach (var catalog in searchCatalogs)
            {
                string url = $"{addonUrl.TrimEnd('/')}/catalog/{catalog.Type}/{catalog.Id}/search={encodedQuery}.json";
                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                    using var combinedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
                    var root = await GetCatalogAsync(url, combinedCts.Token);
                    if (root?.Metas != null)
                    {
                        results.AddRange(root.Metas.Select((m, index) => new StremioMediaStream(m) { SourceAddon = addonUrl, SourceIndex = index }));
                        AppLogger.Info($"[Lifecycle] ➔ Stremio.AddonFetch [STEP] ({addonUrl}) | catalog={catalog.Id}, results={root.Metas.Count}");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Search addon error ({addonUrl}): {ex.Message}");
                }
            }
            return results;
        }

        private static bool IsPeopleSearchCatalogId(string? catalogId)
        {
            if (string.IsNullOrWhiteSpace(catalogId)) return false;
            return catalogId.StartsWith("people_search", StringComparison.OrdinalIgnoreCase)
                || catalogId.Contains(".people_search_", StringComparison.OrdinalIgnoreCase)
                || catalogId.Contains("people_search.", StringComparison.OrdinalIgnoreCase);
        }

        // ==========================================
        // 6. GENRE DISCOVERY
        // ==========================================
        public async Task<List<StremioMediaStream>> DiscoverByGenreAsync(string type, string genre, int skip = 0)
        {
            if (string.IsNullOrWhiteSpace(genre) || genre == "All") return new List<StremioMediaStream>();
            return await DiscoverAggregatedAsync(type, genre, skip);
        }

        public async Task<List<StremioMediaStream>> DiscoverAsync(GenreSelectionArgs args, int skip = 0)
        {
            if (args == null || string.IsNullOrEmpty(args.AddonId)) return new List<StremioMediaStream>();
            try
            {
                string filterKey = args.FilterKey ?? "genre";
                string pathParams = !string.IsNullOrEmpty(args.GenreValue) ? $"{filterKey}={Uri.EscapeDataString(args.GenreValue)}" : "";
                if (skip > 0) pathParams = string.IsNullOrEmpty(pathParams) ? $"skip={skip}" : $"{pathParams}&skip={skip}";

                string url = $"{args.AddonId.TrimEnd('/')}/catalog/{args.CatalogType}/{args.CatalogId}";
                if (!string.IsNullOrEmpty(pathParams)) url += $"/{pathParams}";
                url += ".json";

                return await Task.Run(async () => 
                {
                    var root = await GetCatalogAsync(url);
                    var items = root?.Metas?.Select(m => new StremioMediaStream(m) { SourceAddon = args.AddonId }).ToList() ?? new List<StremioMediaStream>();
                    lock (_indexLock) { foreach (var item in items) IndexStreamInternal(item); }
                    return items;
                });
            }
            catch { return new List<StremioMediaStream>(); }
        }

        private async Task<List<StremioMediaStream>> DiscoverAggregatedAsync(string type, string genre, int skip = 0)
        {
            var addons = StremioAddonManager.Instance.GetAddonsWithManifests();
            var tasks = new List<Task<List<StremioMediaStream>>>();
            
            foreach (var (baseUrl, manifest) in addons)
            {
                if (manifest?.Catalogs == null) continue;
                var relevantCatalogs = manifest.Catalogs.Where(c => c.Type == type && c.Extra != null && c.Extra.Any(e => e.Name == "genre")).ToList();
                foreach (var catalog in relevantCatalogs)
                {
                    string url = $"{baseUrl.TrimEnd('/')}/catalog/{catalog.Type}/{catalog.Id}/genre={Uri.EscapeDataString(genre)}{(skip > 0 ? $"&skip={skip}" : "")}.json";
                    tasks.Add(Task.Run(async () =>
                    {
                        try 
                        { 
                            var root = await GetCatalogAsync(url); 
                            return root?.Metas?.Select(m => new StremioMediaStream(m) { SourceAddon = baseUrl }).ToList() ?? new List<StremioMediaStream>(); 
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn($"Discovery aggregated error ({baseUrl}): {ex.Message}");
                            return new List<StremioMediaStream>();
                        }
                    }));
                }
            }
            var resultsArray = await Task.WhenAll(tasks);
            return DeduplicateAndRank(resultsArray.SelectMany(x => x).ToList(), ""); 
        }

        public async Task<StremioCatalogRoot> GetCatalogAsync(string baseUrl, string type, string id, System.Threading.CancellationToken cancellationToken = default)
        {
            string url = $"{baseUrl.TrimEnd('/')}/catalog/{type}/{id}.json";
            return await GetCatalogAsync(url, cancellationToken);
        }

        private static readonly TimeSpan CatalogHardTtl = TimeSpan.FromHours(4);

        public async Task<StremioCatalogRoot> GetCatalogAsync(string url, System.Threading.CancellationToken cancellationToken = default, bool bypassConditional = false, string? preloadedEtag = null)
        {
            // Extract Base URL (Source Addon)
            string addonBaseUrl = url;
            int catalogIdx = url.IndexOf("/catalog/", StringComparison.OrdinalIgnoreCase);
            if (catalogIdx > 0) addonBaseUrl = url.Substring(0, catalogIdx);

            string? etag = preloadedEtag;
            IReadOnlyList<StremioMediaStream>? cachedItems = null;

            // 1. Only read disk if ETag wasn't provided by the caller (Phase 0 -> Phase 1 handover)
            if (etag == null && !bypassConditional)
            {
                var (diskEtag, diskItems, cachedTime) = await CatalogCacheManager.LoadCatalogBinaryAsync(url);
                etag = diskEtag;
                cachedItems = diskItems;

                if (cachedTime != DateTime.MinValue && (DateTime.UtcNow - cachedTime) > CatalogHardTtl)
                {
                    Log($"[CatalogTTL] Hard TTL exceeded ({(DateTime.UtcNow - cachedTime).TotalHours:F1}h), revalidate: {url}");
                    etag = null;
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            
            // 2. Add Conditional Header
            if (!bypassConditional && !string.IsNullOrEmpty(etag))
            {
                if (etag.StartsWith("W/"))
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                else
                    request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag));
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _client.SendAsync(request, cancellationToken);
            sw.Stop();

            string etagStatus = !string.IsNullOrEmpty(etag) ? $"[ETag: {etag.Substring(0, Math.Min(8, etag.Length))}...]" : "[No ETag]";
                        // 3. Handle 304 Not Modified
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                // If items are missing (common during ETag-only handoff), read them from disk now.
                if (cachedItems == null)
                {
                    var (_, diskItems, _) = await CatalogCacheManager.LoadCatalogBinaryAsync(url);
                    cachedItems = diskItems;
                }

                if (cachedItems != null)
                {
                    Log($"[NET-304] {etagStatus} Catalog up-to-date (Lazy Loaded) in {sw.ElapsedMilliseconds}ms: {url}");

                    bool needsUpgrade = false;
                    foreach (var item in cachedItems)
                    {
                        if (string.IsNullOrEmpty(item.SourceAddon))
                        {
                            item.SourceAddon = addonBaseUrl;
                            needsUpgrade = true;
                        }
                    }

                    if (needsUpgrade)
                    {
                        Log($"[CatalogCache] UPGRADING: Adding missing SourceAddon ({addonBaseUrl}) to existing cache in background.");
                        _ = Task.Run(() => CatalogCacheManager.SaveCatalogBinaryAsync(url, etag, cachedItems.ToList()));
                    }

                    return new StremioCatalogRoot { Metas = cachedItems.Select(i => i.Meta).ToList() };
                }
                
                if (!bypassConditional)
                {
                    Log($"[NET-304] {etagStatus} Warning: Server sent 304 but local binary missing after lazy load. Retrying without If-None-Match: {url}");
                    return await GetCatalogAsync(url, cancellationToken, bypassConditional: true);
                }
                AppLogger.Warn($"[NET-304] Catalog not modified but no local cache after unconditional retry: {url}");
                return new StremioCatalogRoot { Metas = new List<StremioMeta>() };
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Log($"[NET-404] Catalog NOT FOUND (404): {url}");
                return new StremioCatalogRoot { Metas = new List<StremioMeta>() };
            }

            if (!response.IsSuccessStatusCode)
            {
                Log($"[NET-ERR] Catalog fetch FAILED with {response.StatusCode}: {url}");
                return new StremioCatalogRoot { Metas = new List<StremioMeta>() };
            }

            Log($"[NET-200] {etagStatus} Catalog FULL LOAD in {sw.ElapsedMilliseconds}ms: {url}");

            // 4. Handle 200 OK
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            
            try 
            {
                var root = JsonSerializer.Deserialize(json, AppJsonContext.Default.StremioCatalogRoot);

                // 5. Update Cache asynchronously in background
                if (root?.Metas != null)
                {
                    string? newEtag = response.Headers.ETag?.ToString() ?? (response.Headers.TryGetValues("ETag", out var etags) ? etags.FirstOrDefault() : null);
                    
                    var itemsToCache = root.Metas.Select(m => {
                        var s = new StremioMediaStream(m);
                        s.SourceAddon = addonBaseUrl;
                        return s;
                    }).ToList();
                    _ = Task.Run(() => CatalogCacheManager.SaveCatalogBinaryAsync(url, newEtag, itemsToCache));
                }

                return root;
            }
            catch (JsonException jex)
            {
                Log($"[JSON-ERR] Catalog DESERIALIZATION FAILED for {url}: {jex.Message}");
                return new StremioCatalogRoot { Metas = new List<StremioMeta>() };
            }
        }



        private List<StremioMediaStream> DeduplicateAndRank(List<StremioMediaStream> results, string query, System.Threading.CancellationToken ct = default)
        {
            var uniqueItems = new List<StremioMediaStream>();
            var orderedRaw = results.OrderByDescending(x => !string.IsNullOrEmpty(x.IMDbId) && x.IMDbId.StartsWith("tt")).ThenByDescending(x => !string.IsNullOrEmpty(x.Year)).ThenBy(x => x.SourceIndex).ToList();

            Span<char> queryNormBuf = stackalloc char[query.Length + 16];
            int queryNormLen = TitleHelper.NormalizeToBuffer(query.AsSpan(), queryNormBuf);
            ReadOnlySpan<char> normQuery = queryNormBuf[..queryNormLen];

            // [OPTIMIZATION] IPTV Matching is now "Lazy". 
            // We only match for items that are actually being displayed to the user.
            // This loop now only handles metadata deduplication.
            foreach (var item in orderedRaw)
            {
                ct.ThrowIfCancellationRequested();
                
                Span<char> titleNormBuf = stackalloc char[item.Title.Length + 16];
                int titleNormLen = TitleHelper.NormalizeToBuffer(item.Title.AsSpan(), titleNormBuf);
                ReadOnlySpan<char> normTitle = titleNormBuf[..titleNormLen];

                StremioMediaStream? existing = null;
                foreach (var x in uniqueItems)
                {
                    if (IsMatch(x, item, normTitle, query))
                    {
                        existing = x;
                        break;
                    }
                }

                if (existing != null) MergeItems(existing, item);
                else uniqueItems.Add(item);
            }

            string normQueryStr = normQuery.ToString();
            var final = uniqueItems.OrderByDescending(x => CalculateRankWeight(x, normQueryStr.AsSpan())).ThenByDescending(x => GetYearDigits(x.Year)).ToList();

            // [LOGGING] Rank Breakdown for Top 20
            try
            {
                var top = final.Take(20).ToList();
                // AppLogger.Info($"[Rank] Top 20 results for query: '{query}'");
                foreach (var item in top)
                {
                    double w = CalculateRankWeight(item, query, true); // Log breakdown for top items
                    // AppLogger.Info($"[Rank] Result: {item.Title} ({item.Year}) | ID: {item.IMDbId} | Score: {w,6:F1}");
                }
            }
            catch { }

            return final;
        }

        private double CalculateRankWeight(StremioMediaStream x, ReadOnlySpan<char> normQ, bool log = false)
        {
            double similarity = GetScore(x, normQ); // 0-100
            double score = similarity * 9.0; // DEFINITIVE Weight for similarity (max 900)
            
            Span<char> normTBuf = stackalloc char[x.Title.Length + 16];
            int normTLen = TitleHelper.NormalizeToBuffer(x.Title.AsSpan(), normTBuf);
            ReadOnlySpan<char> normT = normTBuf[..normTLen];
            
            // Length Penalty: Penalize titles that are much longer than the query
            // But be very lenient if similarity is a perfect/near-perfect match (98+)
            double lenPenaltyFactor = similarity >= 98 ? 1.0 : 6.0;
            double lengthPenalty = Math.Max(0, normT.Length - normQ.Length) * lenPenaltyFactor; 

            double posterPenalty = StremioMediaStream.IsPosterValid(x.PosterUrl) ? 0 : 500;
            double indexBoost = Math.Max(0, (25 - x.SourceIndex) * 2.0);
            
            double rawRating = ParseRating(x.Rating);
            if (rawRating > 10) rawRating /= 10.0;
            if (rawRating > 10) rawRating = 0; 

            double ratingBoost = ((rawRating <= 0) ? 6.0 : rawRating) * 8.0; // Max 80
            if (rawRating >= 8.5) ratingBoost += 40; 
            else if (rawRating >= 7.5) ratingBoost += 20; 
            else if (rawRating > 0 && rawRating < 5.0) ratingBoost -= 60; 
            
            double sourceBoost = (x.SourceAddon?.Contains("cinemeta") == true) ? 100 : 0;
            double qualityBoost = (!string.IsNullOrEmpty(x.Description) && !string.IsNullOrEmpty(x.PosterUrl)) ? 10 : 0;
            
            double recencyBoost = 0;
            if (int.TryParse(GetYearDigits(x.Year), out int y))
            {
                int currentYear = DateTime.Now.Year;
                if (y > currentYear + 1) recencyBoost = -500; // Future Placeholder / Junk
                else if (y == currentYear) recencyBoost = 30; // Peak for new releases
                else if (y >= 2010) recencyBoost = Math.Min(25, (y - 2010) * 1.5); 
                else if (y >= 2000) recencyBoost = (y - 2000) * 0.5;
                else recencyBoost = (y - 1980) * 0.1;
            }

            double typeBoost = (x.Type == "movie") ? 40 : 0;
            
            // Standalone penalty for non-verified items without external IDs
            bool hasValidId = !string.IsNullOrEmpty(x.IMDbId) && (x.IMDbId.StartsWith("tt") || x.IMDbId.StartsWith("tmdb"));
            double standalonePenalty = (!hasValidId && (x.SourceAddon == null || !x.SourceAddon.Contains("cinemeta"))) ? 300 : 0;

            double final = score + indexBoost + ratingBoost + sourceBoost + qualityBoost + recencyBoost + typeBoost - lengthPenalty - posterPenalty - standalonePenalty;

            if (log) {
                // AppLogger.Info($"[RankDetail] '{x.Title}': Total={final:F1} | Sim={score:F1} - LenPen={lengthPenalty:F1} - PosterPen={posterPenalty:F1} + IndexB={indexBoost:F1} + RatingB={ratingBoost:F1} + SourceB={sourceBoost:F1} + RecencyB={recencyBoost:F1}");
            }

            return final;
        }

        private void AddToIndex(string? id, StremioMediaStream stream) {
            if (string.IsNullOrWhiteSpace(id)) return;
            string key = id.Trim().ToLowerInvariant();
            if (!_globalMetaIndex.TryGetValue(key, out var set)) _globalMetaIndex[key] = set = new HashSet<StremioMediaStream>();
            set.Add(stream);
        }

        private void IndexStreamInternal(StremioMediaStream stream)
        {
            if (stream?.Meta == null) return;
            AddToIndex(stream.Meta.Id, stream);
            AddToIndex(stream.Meta.ImdbId, stream);
            AddToIndex(stream.IMDbId, stream);
            if (stream.Meta.MoviedbId != null) AddToIndex($"tmdb:{stream.Meta.MoviedbId}", stream);
        }

        public List<StremioMediaStream> GetGlobalMetaCache(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return new List<StremioMediaStream>();
            string key = id.Trim().ToLowerInvariant();
            lock (_indexLock) { return _globalMetaIndex.TryGetValue(key, out var set) ? set.ToList() : new List<StremioMediaStream>(); }
        }

        private bool IsMatch(StremioMediaStream existing, StremioMediaStream current, ReadOnlySpan<char> currentNormTitle, string? query = null)
        {
            if (existing.Type != current.Type) return false;
            
            if (!string.IsNullOrEmpty(existing.IMDbId) && !string.IsNullOrEmpty(current.IMDbId))
            {
                if (IdMappingService.Instance.AreIdentical(existing.IMDbId, current.IMDbId)) return true;

                string existingNormId = NormalizeExternalId(existing.IMDbId);
                string currentNormId = NormalizeExternalId(current.IMDbId);
                if (!string.IsNullOrWhiteSpace(existingNormId) && !string.IsNullOrWhiteSpace(currentNormId) && string.Equals(existingNormId, currentNormId, StringComparison.OrdinalIgnoreCase)) return true;
                if (existing.IMDbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && current.IMDbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return existing.IMDbId == current.IMDbId;
            }

            var titles1 = new[] { existing.Title, existing.Meta?.Originalname }.Where(t => !string.IsNullOrWhiteSpace(t)).Concat(existing.Meta?.Aliases ?? Enumerable.Empty<string>());
            var titles2 = new[] { current.Title, current.Meta?.Originalname }.Where(t => !string.IsNullOrWhiteSpace(t)).Concat(current.Meta?.Aliases ?? Enumerable.Empty<string>());
            return TitleHelper.IsMatch(titles1, current.Title, existing.Year, current.Year) || TitleHelper.IsMatch(titles2, existing.Title, existing.Year, current.Year);
        }

        private string NormalizeExternalId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            if (id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase)) return id.Substring(5).Trim();
            if (id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return id.Trim();
            return id.Trim();
        }

        public async Task MatchVisibleIptvAsync(IEnumerable<StremioMediaStream> items, string query)
        {
            if (items == null || string.IsNullOrEmpty(query)) return;
            
            // Filter out items already checked
            var targets = items.Where(i => !i.IsIptvChecked && !i.IsIptv).ToList();
            if (!targets.Any()) return;

            await Task.Run(() => 
            {
                foreach (var item in targets)
                {
                    item.IsIptvChecked = true;
                    // [Senior] Optimized lazy check against smart indices
                    var iptvMatch = IptvMatchService.Instance.MatchToIptv(item.Title, item.Year, item.Type ?? "movie");
                    if (iptvMatch != null)
                    {
                        string itemYear = GetYearDigits(item.Year);
                        string matchYear = GetYearDigits(iptvMatch.Year);
                        if (string.IsNullOrEmpty(itemYear) || string.IsNullOrEmpty(matchYear) || itemYear == matchYear)
                        {
                            item.IsAvailableOnIptv = true;
                            if (string.IsNullOrEmpty(item.IMDbId) && !string.IsNullOrEmpty(iptvMatch.IMDbId)) item.Meta.Id = iptvMatch.IMDbId;
                            AppLogger.Info($"[StremioService] Lazy Match Success: '{item.Title}' -> IPTV Match found.");
                        }
                    }
                }
            });
        }

        private void MergeItems(StremioMediaStream existing, StremioMediaStream incoming)
        {
            existing.SourceAddon ??= incoming.SourceAddon;
            if (incoming.IsAvailableOnIptv) existing.IsAvailableOnIptv = true;
            if (string.IsNullOrEmpty(existing.IMDbId) && !string.IsNullOrEmpty(incoming.IMDbId)) existing.Meta.Id = incoming.IMDbId;
            if (string.IsNullOrEmpty(existing.PosterUrl) && !string.IsNullOrEmpty(incoming.PosterUrl)) existing.PosterUrl = incoming.PosterUrl;
            if (string.IsNullOrEmpty(existing.Description) && !string.IsNullOrEmpty(incoming.Meta?.Description)) existing.Meta.Description = incoming.Meta.Description;
        }

        public static string GetYearDigits(string? year) => string.IsNullOrEmpty(year) ? "" : new string(year.Where(char.IsDigit).Take(4).ToArray());
        
        private double ParseRating(string? r)
        {
            if (string.IsNullOrWhiteSpace(r)) return 0;
            try
            {
                // Handle "7.4 / 10" or "74%" formats
                string clean = r.Split('/')[0].Replace("%", "").Trim();
                if (double.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val))
                {
                    return val;
                }
            }
            catch { }
            return 0;
        }

        private double GetScore(StremioMediaStream x, ReadOnlySpan<char> normQ)
        {
            Span<char> normTBuf = stackalloc char[x.Title.Length + 16];
            int normTLen = TitleHelper.NormalizeToBuffer(x.Title.AsSpan(), normTBuf);
            ReadOnlySpan<char> normT = normTBuf[..normTLen];
            
            if (normT.SequenceEqual(normQ)) return 100;
            if (normT.StartsWith(normQ, StringComparison.OrdinalIgnoreCase)) return 85; 
            
            double sim = TitleHelper.CalculateSimilarity(x.Title, normQ.ToString());
            // DEBUG LOGGING (Temporarily)
            if (normQ.ToString().Contains("no") || x.Title.Contains("no")) {
                AppLogger.Info($"[RankDebug] Q='{normQ.ToString()}' | T='{x.Title}' | Sim={sim:F2}");
            }
            return sim * 85; 
        }
    }
}
