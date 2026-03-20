using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using System.Linq;

namespace ModernIPTVPlayer.Services.Stremio
{
    public class StremioService
    {
        private static readonly object _instanceLock = new object();
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

        // In-Memory Cache for Catalogs to speed up switching
        private System.Collections.Concurrent.ConcurrentDictionary<string, List<StremioMediaStream>> _catalogCache = new();

        // Global High-Performance Index for Metadata across all catalogs
        private readonly Dictionary<string, HashSet<StremioMediaStream>> _globalMetaIndex = new();
        private readonly object _indexLock = new();

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
                // Handle stremio:// protocol
                if (url.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url.Substring(10);
                }

                if (!url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    url = $"{url.TrimEnd('/')}/manifest.json";
                }

                string json = await _client.GetStringAsync(url);
                return JsonSerializer.Deserialize<StremioManifest>(json, _jsonOptions);
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
            try
            {
                // Format: /catalog/{type}/{id}.json  OR /catalog/{type}/{id}/{extra}.json
                
                string pathParams = extra;
                if (skip > 0)
                {
                    if (string.IsNullOrEmpty(pathParams)) pathParams = $"skip={skip}";
                    else pathParams += $"&skip={skip}";
                }

                if (!string.IsNullOrEmpty(pathParams))
                {
                    url += $"/{pathParams}";
                }
                url += ".json";

                System.Diagnostics.Debug.WriteLine($"[StremioService] Fetching Catalog URL: {url}");

                // USE SHORTER TIMEOUT FOR CATALOGS (10s)
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _client.SendAsync(request, cts.Token);
                
                System.Diagnostics.Debug.WriteLine($"[StremioService] Response for {url}: {response.StatusCode}");
                response.EnsureSuccessStatusCode();
                
                string json = await response.Content.ReadAsStringAsync();
                var resultResponse = JsonSerializer.Deserialize<StremioMetaResponse>(json, _jsonOptions);

                if (resultResponse?.Metas != null)
                {
                    var result = new List<StremioMediaStream>();
                    foreach (var meta in resultResponse.Metas)
                    {
                        var stream = new StremioMediaStream(meta);
                        stream.SourceAddon = baseUrl;
                        result.Add(stream);
                    }
                    
                    _catalogCache[cacheKey] = result; // Cache it
                    
                    // Update Global Index
                    lock (_indexLock)
                    {
                        foreach (var stream in result) IndexStreamInternal(stream);
                    }

                    return result;
                }
            }
            catch (TaskCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioService] Catalog TIMEOUT for addon: {baseUrl} (Type: {type}, ID: {id})");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"JSON Error for {url} ({baseUrl})", ex);
            }

            return new List<StremioMediaStream>();
        }

        // ==========================================
        // 3. META (Details)
        // ==========================================
        public async Task<StremioMeta> GetMetaAsync(string baseUrl, string type, string id)
        {
            try
            {
                // Format: /meta/{type}/{id}.json
                string url = $"{baseUrl.TrimEnd('/')}/meta/{type}/{id}.json";
                System.Diagnostics.Debug.WriteLine($"[StremioService] Fetching Meta: {url}");
                string json = await _client.GetStringAsync(url);
                var response = JsonSerializer.Deserialize<StremioMetaResponse>(json, _jsonOptions);
                return response?.Meta;
            }
            catch (Exception ex)
            {
                string failedUrl = $"{baseUrl.TrimEnd('/')}/meta/{type}/{id}.json";
                AppLogger.Error($"Error fetching meta ({type}:{id}) from {failedUrl}", ex);
                return null;
            }
        }

        // ==========================================
        // 4. STREAMS (Playback)
        // ==========================================
        public async Task<List<StremioStream>> GetStreamsAsync(List<string> addonUrls, string type, string id)
        {
            var tasks = new List<Task<List<StremioStream>>>();
            
            foreach (var baseUrl in addonUrls)
            {
                tasks.Add(Task.Run(async () => 
                {
                    try
                    {
                        // Format: /stream/{type}/{id}.json
                        string url = $"{baseUrl.TrimEnd('/')}/stream/{type}/{id}.json";
                        System.Diagnostics.Debug.WriteLine($"[StremioService] Fetching Streams: {url}");
                        string json = await _client.GetStringAsync(url);
                        // System.Diagnostics.Debug.WriteLine($"[StremioService] FULL RAW JSON from {baseUrl}: {json}");
                        var response = JsonSerializer.Deserialize<StremioStreamResponse>(json, _jsonOptions);
                        if (response?.Streams != null)
                        {
                            foreach (var s in response.Streams)
                            {
                                // Tag the stream with source logic
                                s.AddonUrl = baseUrl;
                                if (string.IsNullOrEmpty(s.Name)) s.Name = "Addon";
                            }
                            return response.Streams;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Barebones penalty: Log the error and return an empty list for this addon
                        System.Diagnostics.Debug.WriteLine($"[StremioService] Error fetching streams from {baseUrl}: {ex.Message}");
                    }
                    return new List<StremioStream>();
                }));
            }

            var results = await Task.WhenAll(tasks);
            
            var allStreams = new List<StremioStream>();
            foreach (var list in results)
            {
                allStreams.AddRange(list);
            }
            
            return allStreams;
        }
        // ==========================================
        // 5. SEARCH (Multi-Addon)
        // ==========================================
        public async Task<List<StremioMediaStream>> SearchAsync(string query, Action<List<StremioMediaStream>>? onResultsFound = null)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<StremioMediaStream>();
            
            // Get all active addons with their manifests
            var addons = StremioAddonManager.Instance.GetAddonsWithManifests();
            var tasks = new List<Task<List<StremioMediaStream>>>();
            string encodedQuery = Uri.EscapeDataString(query);

            // [VOD/SERIES INTEGRATION] Start IPTV VOD/Series search from cache in parallel
            var playlistId = App.CurrentLogin?.PlaylistUrl ?? "default";
            var iptvVodTask = ContentCacheService.Instance.LoadCacheAsync<LiveStream>(playlistId, "vod_streams");
            var iptvSeriesTask = ContentCacheService.Instance.LoadCacheAsync<SeriesStream>(playlistId, "series_streams");

            var allResults = new List<StremioMediaStream>();

            // [NEW] Local function for IPTV matching (moved from end to beginning)
            void ProcessIptvMatch(IEnumerable<IMediaStream>? iptvItems, string query)
            {
                if (iptvItems == null) return;
                var matches = iptvItems.Where(x => x.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var iptv in matches)
                {
                    string iptvNorm = NormalizeTitle(iptv.Title);
                    // Match by IMDb ID (Strongest) or Normalized Title
                    var existing = allResults.FirstOrDefault(x => 
                        (!string.IsNullOrEmpty(x.IMDbId) && !string.IsNullOrEmpty(iptv.IMDbId) && x.IMDbId == iptv.IMDbId) ||
                        (NormalizeTitle(x.Title) == iptvNorm)
                    );

                    if (existing != null)
                    {
                        existing.IsAvailableOnIptv = true;
                    }
                    else
                    {
                        // Standalone IPTV VOD/Series result
                        var iptvStream = new StremioMediaStream
                        {
                            Title = iptv.Title,
                            IsIptv = true,
                            IsAvailableOnIptv = true,
                            PosterUrl = iptv.PosterUrl,
                            Type = iptv.Type ?? "movie",
                            IMDbIdRaw = iptv.IMDbId // Use Raw property to set it
                        };
                        allResults.Add(iptvStream);
                    }
                }
            }

            // [NEW] Await and process IPTV immediately for instant results
            var iptvVods = await iptvVodTask;
            var iptvSeries = await iptvSeriesTask;
            ProcessIptvMatch(iptvVods, query);
            ProcessIptvMatch(iptvSeries, query);

            if (allResults.Count > 0 && onResultsFound != null)
            {
                onResultsFound(DeduplicateAndRank(allResults, query));
            }

            foreach (var (baseUrl, manifest) in addons)
            {
                if (manifest?.Catalogs == null) continue;

                var searchCatalogs = manifest.Catalogs.Where(c => 
                    (c.Type == "movie" || c.Type == "series") && 
                    c.Extra != null && 
                    c.Extra.Any(e => e.Name == "search")
                ).ToList();

                foreach (var catalog in searchCatalogs)
                {
                    string url = $"{baseUrl.TrimEnd('/')}/catalog/{catalog.Type}/{catalog.Id}/search={encodedQuery}.json";

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            // 8s timeout per addon search
                            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                            var root = await GetCatalogAsync(url);
                            var items = root?.Metas?.Select((m, index) => new StremioMediaStream(m) { SourceAddon = baseUrl, SourceIndex = index }).ToList() ?? new List<StremioMediaStream>();
                            return items;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[StremioService] Search error on {url}: {ex.Message}");
                            return new List<StremioMediaStream>();
                        }
                    }));
                }
            }

            var remainingTasks = new HashSet<Task<List<StremioMediaStream>>>(tasks);

            while (remainingTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(remainingTasks);
                remainingTasks.Remove(completedTask);

                try
                {
                    var results = await completedTask;
                    if (results != null && results.Count > 0)
                    {
                        lock (_indexLock)
                        {
                            foreach (var stream in results) IndexStreamInternal(stream);
                        }

                        // Deduplicate against already found results
                        var newUniqueResults = new List<StremioMediaStream>();
                        foreach (var item in results)
                        {
                            string normTitle = NormalizeTitle(item.Title);
                            var existing = allResults.FirstOrDefault(x => IsMatch(x, item, normTitle));
                            if (existing != null)
                            {
                                MergeItems(existing, item);
                            }
                            else
                            {
                                allResults.Add(item);
                                newUniqueResults.Add(item);
                            }
                        }

                        if (onResultsFound != null)
                        {
                            // Report latest ranked results (includes IPTV and all addons found so far)
                            // We call this every time any results arrive, because merging might have improved the rank of existing items
                            onResultsFound(DeduplicateAndRank(allResults, query));
                        }
                    }
                }
                catch { /* Ignore Individual Addon Failures */ }
            }

            var finalResults = DeduplicateAndRank(allResults, query);
            onResultsFound?.Invoke(finalResults); // Final definitive update
            return finalResults;
        }

        // ==========================================
        // 6. GENRE DISCOVERY
        // ==========================================
        public async Task<List<StremioMediaStream>> DiscoverByGenreAsync(string type, string genre, int skip = 0)
        {
            if (string.IsNullOrWhiteSpace(genre) || genre == "All") return new List<StremioMediaStream>();
            
            // Standard aggregated discovery logic (kept for backward compatibility or global search)
            // ... (rest of existing logic) ...
            return await DiscoverAggregatedAsync(type, genre, skip);
        }

        public async Task<List<StremioMediaStream>> DiscoverAsync(GenreSelectionArgs args, int skip = 0)
        {
            if (args == null || string.IsNullOrEmpty(args.AddonId)) return new List<StremioMediaStream>();

            try
            {
                string filterKey = args.FilterKey ?? "genre";
                string pathParams = "";
                
                if (!string.IsNullOrEmpty(args.GenreValue))
                {
                    string encodedGenre = Uri.EscapeDataString(args.GenreValue);
                    pathParams = $"{filterKey}={encodedGenre}";
                }
                
                if (skip > 0) 
                {
                    if (string.IsNullOrEmpty(pathParams)) pathParams = $"skip={skip}";
                    else pathParams += $"&skip={skip}";
                }

                string url = $"{args.AddonId.TrimEnd('/')}/catalog/{args.CatalogType}/{args.CatalogId}";
                if (!string.IsNullOrEmpty(pathParams)) url += $"/{pathParams}";
                url += ".json";
                System.Diagnostics.Debug.WriteLine($"[StremioService] Pinpoint Discover URL: {url}");

                var root = await GetCatalogAsync(url);
                var items = root?.Metas?.Select(m => new StremioMediaStream(m) { SourceAddon = args.AddonId }).ToList() ?? new List<StremioMediaStream>();
                
                // Update Global Index
                lock (_indexLock)
                {
                    foreach (var item in items) IndexStreamInternal(item);
                }

                // No need for validation here since the user manually clicked this option from the manifest!
                return items;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioService] Pinpoint Discover error: {ex.Message}");
                return new List<StremioMediaStream>();
            }
        }

        private async Task<List<StremioMediaStream>> DiscoverAggregatedAsync(string type, string genre, int skip = 0)
        {
            if (string.IsNullOrWhiteSpace(genre) || genre == "All") return new List<StremioMediaStream>();

            var addons = StremioAddonManager.Instance.GetAddonsWithManifests();
            var tasks = new List<Task<List<StremioMediaStream>>>();
            
            foreach (var (baseUrl, manifest) in addons)
            {
                if (manifest?.Catalogs == null) continue;

                string queryGenre = genre; 
                string encodedGenre = Uri.EscapeDataString(queryGenre);

                var relevantCatalogs = manifest.Catalogs.Where(c => 
                    c.Type == type && 
                    c.Extra != null && 
                    c.Extra.Any(e => e.Name == "genre") // Relaxed check: we strive to fetch even if exact match isn't perfect, relying on our resolved queryGenre
                ).ToList();

                foreach (var catalog in relevantCatalogs)
                {
                    // Check if this catalog ACTUALLY supports our resolved queryGenre
                    var extra = catalog.Extra.First(e => e.Name == "genre");
                    // If options are restrictive and don't contain our resolved string, skip it.
                    if (extra.Options != null && !extra.Options.Any(o => o.Equals(queryGenre, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // Construct URL: /catalog/{type}/{id}/genre={genre}&skip={skip}.json
                    string pathParams = $"genre={encodedGenre}";
                    if (skip > 0)
                    {
                        pathParams += $"&skip={skip}";
                    }
                    
                    string url = $"{baseUrl.TrimEnd('/')}/catalog/{catalog.Type}/{catalog.Id}/{pathParams}.json";
                    System.Diagnostics.Debug.WriteLine($"[StremioService] Discover URL: {url} (Query: {queryGenre})");

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var root = await GetCatalogAsync(url);
                            var items = root?.Metas?.Select(m => new StremioMediaStream(m) { SourceAddon = baseUrl }).ToList() ?? new List<StremioMediaStream>();
                            return items;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[StremioService] Genre fetch error on {url}: {ex.Message}");
                            return new List<StremioMediaStream>();
                        }
                    }));
                }
            }

            var resultsArray = await Task.WhenAll(tasks);
            var allResults = resultsArray.SelectMany(x => x).ToList();

            // Deduplicate (Zero Logic ranking)
            return DeduplicateAndRank(allResults, ""); 
        }

        private async Task<StremioCatalogRoot> GetCatalogAsync(string url)
        {
            string json = await _client.GetStringAsync(url);
            return JsonSerializer.Deserialize<StremioCatalogRoot>(json, _jsonOptions);
        }

        private List<StremioMediaStream> DeduplicateAndRank(List<StremioMediaStream> raw, string query)
        {
            var uniqueItems = new List<StremioMediaStream>();

            foreach (var item in raw)
            {
                // Normalize Title
                string normTitle = NormalizeTitle(item.Title);
                
                // Find existing match
                var existing = uniqueItems.FirstOrDefault(x => IsMatch(x, item, normTitle));

                if (existing != null)
                {
                    // MERGE LOGIC
                    MergeItems(existing, item);
                }
                else
                {
                    uniqueItems.Add(item);
                }
            }

            // Deduplicate and Rank based on Weighted Score (Relevance + Popularity)
            var sorted = uniqueItems
                .OrderByDescending(x => CalculateRankWeight(x, query))
                .ThenByDescending(x => GetYearDigits(x.Year)) // Recency tie-breaker
                .ToList();

            // [DEBUG] Log Top 10 Results with Extensive Breakdown
            System.Diagnostics.Debug.WriteLine($"\n[SEARCH_RANK] Query: '{query}' | Total: {sorted.Count}");
            int rank = 1;
            foreach (var x in sorted.Take(10))
            {
                double baseS = GetScore(x, query);
                double s = baseS * 3.0; // [REFINED] Higher multiplier for base relevance
                string normQ = NormalizeTitle(query);
                string normT = NormalizeTitle(x.Title);
                double lp = (normT.Length - normQ.Length) * 3.5; 
                double pp = StremioMediaStream.IsPosterValid(x.PosterUrl) ? 0 : 1000;
                double ib = Math.Max(0, (25 - x.SourceIndex) * 3.0);
                double rawRating = ParseRating(x.Rating);
                double er = (rawRating <= 0) ? 6.0 : rawRating; 
                double rb = er * 10.0;
                if (rawRating > 0 && rawRating < 4.5) rb -= 100;
                else if (rawRating >= 7.5) rb += 25;
                double sb = (x.SourceAddon?.Contains("cinemeta") == true) ? 50 : 0;
                double qb = (!string.IsNullOrEmpty(x.Description) && !string.IsNullOrEmpty(x.PosterUrl)) ? 5 : 0;
                double recb = 0;
                if (int.TryParse(GetYearDigits(x.Year), out int y))
                {
                   int currentYear = DateTime.Now.Year;
                   if (y > currentYear + 1) recb = -100;
                   else if (y >= 2000) recb = (Math.Min(y, currentYear) - 2000) * 0.2; 
                   else if (y >= 1970) recb = (y - 1970) * 0.05;
                }
                double tb = (x.Type == "movie") ? 40 : 0;
                
                // [REFINED] Lower penalties for missing info (10 vs 50)
                double mp = 0;
                if (string.IsNullOrEmpty(x.Description)) mp += 10;
                if (rawRating <= 0) mp += 10;

                double final = CalculateRankWeight(x, query);
                
                System.Diagnostics.Debug.WriteLine($" #{rank++} | Score:{final,5:F1} | {x.Title} ({x.Year}) | MetaID: {x.Meta?.Id} (Hash: {x.Id})");
                System.Diagnostics.Debug.WriteLine($"     > IPTV: {x.IsAvailableOnIptv} | Addon: {x.SourceAddon}");
                System.Diagnostics.Debug.WriteLine($"     > Rel:{s,3:F0} LPen:{lp,3:F1} PPen:{pp,4:F0} IdxB:{ib,3:F1} RatB:{rb,3:F0} SrcB:{sb,2:F0} Qual:{qb,2:F0} YearB:{recb,3:F1} TypeB:{tb,2:F0} MetaP:{mp,3:F0} (Idx:{x.SourceIndex})");
            }
            System.Diagnostics.Debug.WriteLine("------------------------------------------\n");

            return sorted;
        }

        private double CalculateRankWeight(StremioMediaStream x, string query)
        {
            double score = GetScore(x, query); // Base relevance (Exact=100, NormalizedStartsWith=98)
            
            // [CRITICAL] Length-based Similarity Penalty:
            // Favor shorter titles but less aggressively than before (3.5x instead of 8.0x)
            string normQ = NormalizeTitle(query);
            string normT = NormalizeTitle(x.Title);
            double lengthPenalty = (normT.Length - normQ.Length) * 3.5; 
            
            // [CRITICAL] Massive Poster Penalty:
            // Results without posters must stay at the bottom.
            double posterPenalty = StremioMediaStream.IsPosterValid(x.PosterUrl) ? 0 : 1000;

            // Popularity Boost: Addons rank by popularity.
            double indexBoost = Math.Max(0, (25 - x.SourceIndex) * 3.0);
            
            // Rating Boost & Quality Filter (Aggressive)
            double rawRating = ParseRating(x.Rating);
            double effectiveRating = (rawRating <= 0) ? 6.0 : rawRating; 
            double ratingBoost = effectiveRating * 10.0;
            
            if (rawRating > 0 && rawRating < 4.5) ratingBoost -= 100;
            else if (rawRating >= 7.5) ratingBoost += 25;
            
            // Primary Source Boost (Cinemeta): Source of truth for major franchises.
            double sourceBoost = (x.SourceAddon?.Contains("cinemeta") == true) ? 50 : 0;
            
            // Content Quality Boost
            double qualityBoost = (!string.IsNullOrEmpty(x.Description) && !string.IsNullOrEmpty(x.PosterUrl)) ? 5 : 0;

            // Linear Recency Boost (Subtle Tie-Breaker):
            double recencyBoost = 0;
            if (int.TryParse(GetYearDigits(x.Year), out int y))
            {
                int currentYear = DateTime.Now.Year;
                if (y > currentYear + 1) recencyBoost = -100; // Future/Placeholder Penalty
                else if (y >= 2000) recencyBoost = (Math.Min(y, currentYear) - 2000) * 0.2; 
                else if (y >= 1970) recencyBoost = (y - 1970) * 0.05;
            }

            // Movie vs Series Bias: Favor films significantly for general searches
            double typeBoost = (x.Type == "movie") ? 40 : 0;

            // Metadata Completion Penalty: Cumulative penalty for missing info
            double metaPenalty = 0;
            if (string.IsNullOrEmpty(x.Description)) metaPenalty += 10;
            if (rawRating <= 0) metaPenalty += 10;

            return (score * 3.0) + indexBoost + ratingBoost + sourceBoost + qualityBoost + recencyBoost + typeBoost - lengthPenalty - posterPenalty - metaPenalty;
        }

        private void IndexStreamInternal(StremioMediaStream stream)
        {
            if (stream?.Meta == null) return;

            void AddToIndex(string? id)
            {
                if (string.IsNullOrWhiteSpace(id)) return;
                string key = id.Trim().ToLowerInvariant();
                if (!_globalMetaIndex.TryGetValue(key, out var set))
                {
                    set = new HashSet<StremioMediaStream>();
                    _globalMetaIndex[key] = set;
                }
                set.Add(stream);
            }

            AddToIndex(stream.Meta.Id);
            AddToIndex(stream.Meta.ImdbId);
            AddToIndex(stream.IMDbId);
            
            // Index by TMDB if available
            if (stream.Meta.MovieDbId != null) AddToIndex($"tmdb:{stream.Meta.MovieDbId}");
        }

        public List<StremioMediaStream> GetGlobalMetaCache(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return new List<StremioMediaStream>();
            string key = id.Trim().ToLowerInvariant();

            lock (_indexLock)
            {
                if (_globalMetaIndex.TryGetValue(key, out var set))
                {
                    return set.ToList();
                }
            }
            return new List<StremioMediaStream>();
        }

        private string NormalizeTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            return new string(title.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLower();
        }

        private bool IsMatch(StremioMediaStream existing, StremioMediaStream current, string currentNormTitle)
        {
            // 1. ID Match (Strongest)
            // If both have IDs and normalized IDs match exactly -> Match.
            // This is important for non-tt IDs like tbm:..., where title can differ by locale.
            if (!string.IsNullOrEmpty(existing.IMDbId) && !string.IsNullOrEmpty(current.IMDbId))
            {
                string existingNormId = NormalizeExternalId(existing.IMDbId);
                string currentNormId = NormalizeExternalId(current.IMDbId);

                if (!string.IsNullOrWhiteSpace(existingNormId) &&
                    !string.IsNullOrWhiteSpace(currentNormId) &&
                    string.Equals(existingNormId, currentNormId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (existing.IMDbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) &&
                    current.IMDbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                     if (existing.IMDbId == current.IMDbId) return true;
                     // If both are tt but different, definitely NOT a match (e.g. Sequel)
                     return false;
                }
                // If one is tt and other is tmdb, we can't rely on ID equality. Proceed to title match.
            }

            // 2. Title Match
            string existingNorm = NormalizeTitle(existing.Title);
            if (existingNorm != currentNormTitle) return false;

            // 3. Year Match (Soft)
            // If titles match, check years.
            // If either year is missing/empty, assume match.
            // If both have years, they must be equal (or very close? strict for now).
            
            bool existingHasYear = !string.IsNullOrWhiteSpace(existing.Year);
            bool currentHasYear = !string.IsNullOrWhiteSpace(current.Year);

            if (!existingHasYear || !currentHasYear) return true; // One missing year -> Assume match
            
            // Clean years (take first 4 digits)
            string y1 = GetYearDigits(existing.Year);
            string y2 = GetYearDigits(current.Year);
            
            return y1 == y2;
        }

        private string NormalizeExternalId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return string.Empty;
            return id.Trim().ToLowerInvariant();
        }

        private string GetYearDigits(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            return digits.Length >= 4 ? digits.Substring(0, 4) : digits;
        }

        private void MergeItems(StremioMediaStream existing, StremioMediaStream current)
        {
            // Prefer existing if it has a valid IMDb ID (tt...)
            // If current has a better ID (tt...) and existing doesn't, we might want to swap base properties?
            // For simplicity, we just fill in gaps in 'existing'.

            bool existingIsCinemeta = existing.IMDbId?.StartsWith("tt") == true;
            bool currentIsCinemeta = current.IMDbId?.StartsWith("tt") == true;

            // If current is Cinemeta/Better and existing is NOT, we should probably upgrade existing to be current
            // But replacing the object in the list is hard with this reference.
            // Instead, copy property values FROM current TO existing.

            // [NEW] Rank Preference: Keep the best SourceIndex/Addon for the final rank
            if (current.SourceIndex < existing.SourceIndex)
            {
                existing.SourceIndex = current.SourceIndex;
                existing.SourceAddon = current.SourceAddon;
            }

            if (!existingIsCinemeta && currentIsCinemeta)
            {
                // Swap core identities to use the "better" source
                existing.Meta.Id = current.Meta.Id; // Use the tt ID
                existing.Meta.Type = current.Meta.Type; 
            }

            // Keep identifier hints from any source. These are critical for later canonical ID resolution.
            bool existingImdbUsable = !string.IsNullOrWhiteSpace(existing.Meta.ImdbId) &&
                                      System.Text.RegularExpressions.Regex.IsMatch(existing.Meta.ImdbId, @"tt\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool currentImdbUsable = !string.IsNullOrWhiteSpace(current.Meta.ImdbId) &&
                                     System.Text.RegularExpressions.Regex.IsMatch(current.Meta.ImdbId, @"tt\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if ((!existingImdbUsable && currentImdbUsable) || string.IsNullOrWhiteSpace(existing.Meta.ImdbId))
            {
                existing.Meta.ImdbId = current.Meta.ImdbId;
            }

            bool existingWebsiteHasImdb = !string.IsNullOrWhiteSpace(existing.Meta.Website) &&
                                          System.Text.RegularExpressions.Regex.IsMatch(existing.Meta.Website, @"tt\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool currentWebsiteHasImdb = !string.IsNullOrWhiteSpace(current.Meta.Website) &&
                                         System.Text.RegularExpressions.Regex.IsMatch(current.Meta.Website, @"tt\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if ((!existingWebsiteHasImdb && currentWebsiteHasImdb) || string.IsNullOrWhiteSpace(existing.Meta.Website))
            {
                existing.Meta.Website = current.Meta.Website;
            }

            // [NEW] Poster Preference: Always take the poster if existing is missing it
            if (!StremioMediaStream.IsPosterValid(existing.PosterUrl) && StremioMediaStream.IsPosterValid(current.PosterUrl))
            {
                existing.Meta.Poster = current.Meta.Poster;
            }

            // [IPTV Integration] Propagate flag
            existing.IsAvailableOnIptv |= current.IsAvailableOnIptv;

            // [NEW] Description Preference: Take if existing is missing it
            if (string.IsNullOrEmpty(existing.Description) && !string.IsNullOrEmpty(current.Description))
            {
                existing.Meta.Description = current.Description;
            }

            if (existing.Meta.MovieDbId == null && current.Meta.MovieDbId != null)
            {
                existing.Meta.MovieDbId = current.Meta.MovieDbId;
            }

            if ((existing.Meta.Links == null || existing.Meta.Links.Count == 0) && current.Meta.Links?.Count > 0)
            {
                existing.Meta.Links = current.Meta.Links;
            }

            if ((existing.Meta.Trailers == null || existing.Meta.Trailers.Count == 0) && current.Meta.Trailers?.Count > 0)
            {
                existing.Meta.Trailers = current.Meta.Trailers;
            }

            if ((existing.Meta.TrailerStreams == null || existing.Meta.TrailerStreams.Count == 0) && current.Meta.TrailerStreams?.Count > 0)
            {
                existing.Meta.TrailerStreams = current.Meta.TrailerStreams;
            }

            // Prefer richer text metadata
            if (string.IsNullOrWhiteSpace(existing.Meta.Description) && !string.IsNullOrWhiteSpace(current.Meta.Description))
            {
                existing.Meta.Description = current.Meta.Description;
            }

            bool existingGenresGeneric = existing.Meta.Genres == null ||
                                         existing.Meta.Genres.Count == 0 ||
                                         (existing.Meta.Genres.Count == 1 && string.Equals(existing.Meta.Genres[0], "movie", StringComparison.OrdinalIgnoreCase));
            bool currentGenresUseful = current.Meta.Genres != null &&
                                       current.Meta.Genres.Count > 0 &&
                                       !(current.Meta.Genres.Count == 1 && string.Equals(current.Meta.Genres[0], "movie", StringComparison.OrdinalIgnoreCase));
            if (existingGenresGeneric && currentGenresUseful)
            {
                existing.Meta.Genres = current.Meta.Genres;
            }

            // 1. Poster
            if (string.IsNullOrEmpty(existing.PosterUrl) && !string.IsNullOrEmpty(current.PosterUrl))
            {
                existing.Meta.Poster = current.PosterUrl;
            }
            // 2. Rating
            double existingRating = ParseRating(existing.Rating);
            double currentRating = ParseRating(current.Rating);
            if (currentRating > 0 && existingRating <= 0)
            {
                existing.Meta.ImdbRatingRaw = current.Meta.ImdbRatingRaw ?? current.Rating;
            }
            // 3. Year (if existing was empty)
            string existingYear = GetYearDigits(existing.Year);
            string currentYear = GetYearDigits(current.Year);
            bool existingYearWeak = string.IsNullOrEmpty(existingYear) || existingYear == "0";
            bool existingLooksPlaceholder = existingYear == "2026" && (string.IsNullOrWhiteSpace(existing.Meta.Description) || existingRating <= 0);
            if ((existingYearWeak || existingLooksPlaceholder) && !string.IsNullOrEmpty(currentYear))
            {
                existing.Meta.ReleaseInfoRaw = current.Meta.ReleaseInfoRaw ?? current.Year;
            }
            // 4. Background
            if (string.IsNullOrEmpty(existing.Banner) && !string.IsNullOrEmpty(current.Banner))
            {
                existing.Meta.Background = current.Banner;
            }
        }

        private double ParseRating(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            if (double.TryParse(raw.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r))
            {
                return r;
            }
            return 0;
        }

        private int GetScore(StremioMediaStream item, string query)
        {
            if (string.IsNullOrEmpty(item.Title)) return 0;
            
            string title = item.Title.Trim();
            string q = query.Trim().ToLowerInvariant();
            string lowTitle = title.ToLowerInvariant();

            // Normalized variants (no spaces, no punctuation)
            string normQuery = NormalizeTitle(q);
            string normTitle = NormalizeTitle(lowTitle);

            // 1. Normalized Exact Match (e.g. "spider-man" == "spider man")
            if (normTitle == normQuery) return 100;

            // 2. Normalized Starts With (Very high relevance for sequels)
            if (normTitle.StartsWith(normQuery)) return 98;

            // 3. Exact Match (Standard) or stripped article match
            if (lowTitle == q || StripLeadingArticles(lowTitle) == q) return 100;

            // 4. Word-based "Starts With" (on original title words)
            var titleWords = title.Split(new[] { ' ', '-', ':', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (titleWords.Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 90;

            // 5. Normalized Contains
            if (normTitle.Contains(normQuery)) return 50;

            // 6. Partial word match
            var queryWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (queryWords.Any(word => lowTitle.Contains(word)))
            {
                return 10;
            }
            
            return 0;
        }

        private string StripLeadingArticles(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            string[] articles = { "the ", "a ", "an " };
            foreach (var art in articles)
            {
                if (title.StartsWith(art, StringComparison.OrdinalIgnoreCase))
                {
                    return title.Substring(art.Length).Trim();
                }
            }
            return title;
        }

        // ==========================================
        // 3. SUBTITLES
        // ==========================================
        public async Task<List<StremioSubtitle>> GetSubtitlesAsync(string baseUrl, string type, string id, string extra = "")
        {
            try
            {
                // Format: /subtitles/{type}/{id}.json OR /subtitles/{type}/{id}/{extra}.json
                string url = $"{baseUrl.TrimEnd('/')}/subtitles/{type}/{id}.json";
                if (!string.IsNullOrEmpty(extra))
                {
                    // Stremio expects extra as a path segment if provided, e.g. "videoHash=..."
                    // Addons usually expect: /subtitles/movie/tt1234567/videoHash=...json
                    url = $"{baseUrl.TrimEnd('/')}/subtitles/{type}/{id}/{extra}.json";
                }

                System.Diagnostics.Debug.WriteLine($"[StremioService] Fetching subtitles from: {url}");
                string json = await _client.GetStringAsync(url);
                var response = JsonSerializer.Deserialize<StremioSubtitleResponse>(json, _jsonOptions);
                return response?.Subtitles ?? new List<StremioSubtitle>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioService] Error fetching subtitles from {baseUrl}: {ex.Message}");
                return new List<StremioSubtitle>();
            }
        }
    }
}
