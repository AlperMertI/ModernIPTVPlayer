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
        private static StremioService _instance;
        public static StremioService Instance => _instance ??= new StremioService();

        private HttpClient _client;
        private JsonSerializerOptions _jsonOptions;

        // In-Memory Cache for Catalogs to speed up switching
        private Dictionary<string, List<StremioMediaStream>> _catalogCache = new();

        private StremioService()
        {
            _client = new HttpClient();
            _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _client.Timeout = TimeSpan.FromSeconds(30); // Robust timeout for slow addons
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
            try
            {
                string url = baseUrl.Trim();
                
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
                System.Diagnostics.Debug.WriteLine($"[StremioService] Error fetching manifest: {ex.Message}");
                return null;
            }
        }

        // ==========================================
        // 2. CATALOGS (Discovery)
        // ==========================================
        public async Task<List<StremioMediaStream>> GetCatalogItemsAsync(string baseUrl, string type, string id, string extra = "")
        {
            string cacheKey = $"{baseUrl}|{type}|{id}|{extra}";
            if (_catalogCache.ContainsKey(cacheKey)) return _catalogCache[cacheKey];

            try
            {
                // Format: /catalog/{type}/{id}.json  OR /catalog/{type}/{id}/{extra}.json
                string url = $"{baseUrl.TrimEnd('/')}/catalog/{type}/{id}.json";
                if (!string.IsNullOrEmpty(extra))
                {
                    // If extra params exist (like genre), append. 
                    // Note: Stremio URL structure for extra args is intricate (key=value), skipping complex filters for now.
                    // Simple "skip" logic: /catalog/movie/top/skip=20.json
                }

                string json = await _client.GetStringAsync(url);
                var response = JsonSerializer.Deserialize<StremioMetaResponse>(json, _jsonOptions);

                if (response?.Metas != null)
                {
                    var result = new List<StremioMediaStream>();
                    foreach (var meta in response.Metas)
                    {
                        var stream = new StremioMediaStream(meta);
                        stream.SourceAddon = baseUrl;
                        result.Add(stream);
                    }
                    
                    _catalogCache[cacheKey] = result; // Cache it
                    return result;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioService] Error fetching catalog: {ex.Message}");
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
                string json = await _client.GetStringAsync(url);
                System.Diagnostics.Debug.WriteLine($"[StremioService] RAW META JSON from {baseUrl}:\n{json}");
                var response = JsonSerializer.Deserialize<StremioMetaResponse>(json, _jsonOptions);
                return response?.Meta;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioService] Error fetching meta: {ex.Message}");
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
                        string json = await _client.GetStringAsync(url);
                        // System.Diagnostics.Debug.WriteLine($"[StremioService] FULL RAW JSON from {baseUrl}: {json}");
                        var response = JsonSerializer.Deserialize<StremioStreamResponse>(json, _jsonOptions);
                        if (response?.Streams != null)
                        {
                            foreach(var s in response.Streams)
                            {
                                // Tag the stream with source logic if needed, 
                                // or just ensure name is set properly
                                if (string.IsNullOrEmpty(s.Name)) s.Name = "Addon";
                            }
                            return response.Streams;
                        }
                    }
                    catch (Exception ex)
                    {
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
        public async Task<List<StremioMediaStream>> SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<StremioMediaStream>();

            // Get all active addons with their manifests
            var addons = StremioAddonManager.Instance.GetAddonsWithManifests();
            var tasks = new List<Task<List<StremioMediaStream>>>();
            string encodedQuery = Uri.EscapeDataString(query);

            foreach (var (baseUrl, manifest) in addons)
            {
                if (manifest?.Catalogs == null) continue;

                // Find catalogs that support search (extra: "search" or "name": "search" in extra items)
                // If an addon doesn't explicitly advertise search in extra, we generally skip it or try 'top' if desperately needed, 
                // but probing 'top' for search usually causes 404s.
                // Standard: "extra": [ { "name": "search", "isRequired": false } ]
                
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
                            var root = await GetCatalogAsync(url);
                            var items = root?.Metas?.Select(m => new StremioMediaStream(m) { SourceAddon = manifest.Name ?? "Unknown Addon" }).ToList() ?? new List<StremioMediaStream>();
                            return items;
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            return new List<StremioMediaStream>();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[StremioService] Search error on {url}: {ex.Message}");
                            return new List<StremioMediaStream>();
                        }
                    }));
                }
            }

            var resultsArray = await Task.WhenAll(tasks);

            // Flatten
            var allResults = resultsArray.SelectMany(x => x).ToList();

            // DEBUG: Log Raw Results
            System.Diagnostics.Debug.WriteLine($"[StremioService] Total Raw Results: {allResults.Count}");
            foreach (var item in allResults)
            {
                System.Diagnostics.Debug.WriteLine($"[RawResult] Source: '{item.SourceAddon}' | Title: '{item.Title}' | Year: '{item.Year}' | ID: '{item.IMDbId}' | Type: '{item.Type}'");
            }

            // Deduplicate & Rank
            return DeduplicateAndRank(allResults, query);
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

            // Smart Ranking
            return uniqueItems
                .Select(x => new { Item = x, Score = GetScore(x, query) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Item.Year)
                .Select(x => x.Item)
                .ToList();
        }

        private string NormalizeTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            return new string(title.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLower();
        }

        private bool IsMatch(StremioMediaStream existing, StremioMediaStream current, string currentNormTitle)
        {
            // 1. ID Match (Strongest)
            // If both have IMDb IDs (tt...), and they match -> Match
            if (!string.IsNullOrEmpty(existing.IMDbId) && !string.IsNullOrEmpty(current.IMDbId))
            {
                if (existing.IMDbId.StartsWith("tt") && current.IMDbId.StartsWith("tt"))
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

            if (!existingIsCinemeta && currentIsCinemeta)
            {
                // Swap core identities to use the "better" source
                existing.Meta.Id = current.Meta.Id; // Use the tt ID
                existing.Meta.Type = current.Meta.Type; 
                // existing.SourceAddon = current.SourceAddon; // Maybe?
            }

            // 1. Poster
            if (string.IsNullOrEmpty(existing.PosterUrl) && !string.IsNullOrEmpty(current.PosterUrl))
            {
                existing.Meta.Poster = current.PosterUrl;
            }
            // 2. Rating
            if (string.IsNullOrEmpty(existing.Rating) && !string.IsNullOrEmpty(current.Rating))
            {
                existing.Meta.ImdbRatingRaw = current.Rating;
            }
            // 3. Year (if existing was empty)
            if (string.IsNullOrEmpty(existing.Year) && !string.IsNullOrEmpty(current.Year))
            {
                existing.Meta.ReleaseInfoRaw = current.Year;
            }
            // 4. Background
            if (string.IsNullOrEmpty(existing.Banner) && !string.IsNullOrEmpty(current.Banner))
            {
                existing.Meta.Background = current.Banner;
            }
        }

        private int GetScore(StremioMediaStream item, string query)
        {
            if (string.IsNullOrEmpty(item.Title)) return 0;
            
            string title = item.Title.Trim();
            string q = query.Trim();

            if (title.Equals(q, StringComparison.OrdinalIgnoreCase)) return 100; // Exact
            if (title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 50; // Starts With
            if (title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return 10; // Contains (Relaxed)
            
            // fuzzy match or split words?
            var queryWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (queryWords.Any(word => title.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return 5; // Partial word match
            }
            
            return 0; // Other
        }
    }
}
