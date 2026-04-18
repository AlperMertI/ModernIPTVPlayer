using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Metadata; // [NEW] For IdMappingService
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using System.Collections.Concurrent;

namespace ModernIPTVPlayer
{
    public class TmdbHelper
    {
        private static string API_KEY => AppSettings.TmdbApiKey;
        private const string BASE_URL = "https://api.themoviedb.org/3";
        private static HttpClient _client => HttpHelper.Client;

        // In-memory cache for Stremio people search (1h TTL)
        private static readonly ConcurrentDictionary<string, (List<StremioMediaStream> Results, DateTime Expiry)> _stremioPeopleCache = new();
        private static readonly TimeSpan _stremioPeopleCacheTtl = TimeSpan.FromHours(1);

        // Caching is now handled by TmdbCacheService (Persistent)

        public static async Task<List<string>> GetMovieImagesAsync(string tmdbId)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY) || string.IsNullOrEmpty(tmdbId)) return new List<string>();
            try
            {
                var cacheKey = $"movie_images_{tmdbId}";
                if (TmdbCacheService.Instance.Get<List<string>>(cacheKey) is List<string> cached) return cached;

                var language = AppSettings.TmdbLanguage;
                var shortLang = language.Split('-')[0];
                var url = $"{BASE_URL}/movie/{tmdbId}/images?api_key={API_KEY}&include_image_language={shortLang},en,null";
                var json = await _client.GetStringAsync(url);
                System.Diagnostics.Debug.WriteLine($"[TMDB] Movie Images Request ({shortLang}): {url}");
                
                using var doc = JsonDocument.Parse(json);
                var backdrops = new List<string>();
                if (doc.RootElement.TryGetProperty("backdrops", out var backdropsEl))
                {
                    foreach (var item in backdropsEl.EnumerateArray())
                    {
                        if (item.TryGetProperty("file_path", out var pathEl))
                        {
                            backdrops.Add($"https://image.tmdb.org/t/p/original{pathEl.GetString()}");
                        }
                    }
                }
                
                TmdbCacheService.Instance.Set(cacheKey, backdrops);
                return backdrops;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB] Images Error: {ex.Message}");
                return new List<string>();
            }
        }

        public static async Task<List<string>> GetTvImagesAsync(string tmdbId)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY) || string.IsNullOrEmpty(tmdbId)) return new List<string>();
            try
            {
                var cacheKey = $"tv_images_{tmdbId}";
                if (TmdbCacheService.Instance.Get<List<string>>(cacheKey) is List<string> cached) return cached;

                var language = AppSettings.TmdbLanguage;
                var shortLang = language.Split('-')[0];
                var url = $"{BASE_URL}/tv/{tmdbId}/images?api_key={API_KEY}&include_image_language={shortLang},en,null";
                var json = await _client.GetStringAsync(url);
                System.Diagnostics.Debug.WriteLine($"[TMDB] TV Images Request ({shortLang}): {url}");
                
                using var doc = JsonDocument.Parse(json);
                var backdrops = new List<string>();
                if (doc.RootElement.TryGetProperty("backdrops", out var backdropsEl))
                {
                    foreach (var item in backdropsEl.EnumerateArray())
                    {
                        if (item.TryGetProperty("file_path", out var pathEl))
                        {
                            backdrops.Add($"https://image.tmdb.org/t/p/original{pathEl.GetString()}");
                        }
                    }
                }
                
                TmdbCacheService.Instance.Set(cacheKey, backdrops);
                return backdrops;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB] TV Images Error: {ex.Message}");
                return new List<string>();
            }
        }

        public static async Task<TmdbMovieResult?> SearchMovieAsync(string title, string year = null, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB] SearchMovie Input: '{title}'");
                var cleanTitle = CleanTitle(title);
                System.Diagnostics.Debug.WriteLine($"[TMDB] SearchMovie Cleaned: '{cleanTitle}'");
                
                // Fallback: If cleaning killed the title, use original
                if (string.IsNullOrWhiteSpace(cleanTitle) && !string.IsNullOrWhiteSpace(title))
                {
                    cleanTitle = title;
                    System.Diagnostics.Debug.WriteLine($"[TMDB] Cleaning resulted in empty string. Reverted to: '{cleanTitle}'");
                }

                language ??= AppSettings.TmdbLanguage;
                var cacheKey = $"search_movie_{cleanTitle}_{year}_{language}";
                
                if (TmdbCacheService.Instance.Get<TmdbMovieResult>(cacheKey) is TmdbMovieResult cached) return cached;

                var query = Uri.EscapeDataString(cleanTitle);
                var url = $"{BASE_URL}/search/movie?api_key={API_KEY}&query={query}&language={language}";
                
                if (!string.IsNullOrEmpty(year))
                {
                    url += $"&year={year}";
                }

                var json = await _client.GetStringAsync(url);
                System.Diagnostics.Debug.WriteLine($"[TMDB] Movie Search Request: {url}");
                var result = JsonSerializer.Deserialize<TmdbSearchResponse>(json);
                System.Diagnostics.Debug.WriteLine($"[TMDB] Movie Search Found: {result?.Results?.Count ?? 0} results");

                if (result?.Results != null && result.Results.Count > 0)
                {
                    var match = result.Results[0];
                    TmdbCacheService.Instance.Set(cacheKey, match);
                    return match;
                }
                
                // FALLBACK: If year search failed, try without year
                if (!string.IsNullOrEmpty(year))
                {
                     System.Diagnostics.Debug.WriteLine($"[TMDB] Year search failed. Retrying without year...");
                     url = $"{BASE_URL}/search/movie?api_key={API_KEY}&query={query}&language={language}";
                     
                     json = await _client.GetStringAsync(url);
                     result = JsonSerializer.Deserialize<TmdbSearchResponse>(json);
                     
                     if (result?.Results != null && result.Results.Count > 0)
                     {
                        System.Diagnostics.Debug.WriteLine($"[TMDB] Fallback Search Found: {result.Results.Count} results");
                        var match = result.Results[0];
                        // Cache it under the original key too to save future lookups
                        TmdbCacheService.Instance.Set(cacheKey, match);
                        return match;
                     }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB] Search Error: {ex.Message}");
                return null;
            }
        }

        public static async Task<TmdbMovieResult?> SearchTvAsync(string title, string year = null, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB] SearchTV Input: '{title}'");
                var cleanTitle = CleanTitle(title);
                System.Diagnostics.Debug.WriteLine($"[TMDB] SearchTV Cleaned: '{cleanTitle}'");

                if (string.IsNullOrWhiteSpace(cleanTitle) && !string.IsNullOrWhiteSpace(title))
                {
                    cleanTitle = title;
                    System.Diagnostics.Debug.WriteLine($"[TMDB] Cleaning resulted in empty string. Reverted to: '{cleanTitle}'");
                }

                language ??= AppSettings.TmdbLanguage;
                var cacheKey = $"search_tv_{cleanTitle}_{year}_{language}";

                if (TmdbCacheService.Instance.Get<TmdbMovieResult>(cacheKey) is TmdbMovieResult cached) return cached;

                var query = Uri.EscapeDataString(cleanTitle);
                var url = $"{BASE_URL}/search/tv?api_key={API_KEY}&query={query}&language={language}";

                if (!string.IsNullOrEmpty(year))
                {
                    url += $"&first_air_date_year={year}";
                }
                
                var json = await _client.GetStringAsync(url);
                System.Diagnostics.Debug.WriteLine($"[TMDB] TV Search Request: {url}");
                var result = JsonSerializer.Deserialize<TmdbSearchResponse>(json);
                System.Diagnostics.Debug.WriteLine($"[TMDB] TV Search Found: {result?.Results?.Count ?? 0} results");

                if (result?.Results != null && result.Results.Count > 0)
                {
                    var match = result.Results[0];
                    TmdbCacheService.Instance.Set(cacheKey, match);
                    return match;
                }
                
                // FALLBACK: If year search failed, try without year
                if (!string.IsNullOrEmpty(year))
                {
                     System.Diagnostics.Debug.WriteLine($"[TMDB] Year search failed. Retrying without year...");
                     url = $"{BASE_URL}/search/tv?api_key={API_KEY}&query={query}&language={language}";
                     
                     json = await _client.GetStringAsync(url);
                     result = JsonSerializer.Deserialize<TmdbSearchResponse>(json);
                     
                     if (result?.Results != null && result.Results.Count > 0)
                     {
                        System.Diagnostics.Debug.WriteLine($"[TMDB] Fallback Search Found: {result.Results.Count} results");
                        var match = result.Results[0];
                        TmdbCacheService.Instance.Set(cacheKey, match);
                        return match;
                     }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB] TV Search Error: {ex.Message}");
                return null;
            }
        }

        public static async Task<string?> GetTrailerKeyAsync(int tmdbId, bool isTv = false, string? language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                language ??= AppSettings.TmdbLanguage;
                string type = isTv ? "tv" : "movie";
                
                System.Diagnostics.Debug.WriteLine($"[TMDB-Trailer] Start lookup for ID:{tmdbId} ({type}) | Language:{language}");

                // 1. Try with localized language FIRST
                var cacheKey = $"trailer_{type}_{tmdbId}_{language}";
                if (TmdbCacheService.Instance.Get<string>(cacheKey) is string cached) 
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Trailer] Cache HIT for {cacheKey}: {cached}");
                    return cached;
                }

                var url = $"{BASE_URL}/{type}/{tmdbId}/videos?api_key={API_KEY}&language={language}";
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbVideosResponse>(json);

                string? trailerKey = null;
                if (result?.Results != null && result.Results.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Trailer] Localized search ({language}) returned {result.Results.Count} videos.");
                    
                    // [PRIORITY] Prefer 'Trailer' over other types for the localized language
                    var bestMatch = result.Results
                        .Where(v => v.Site == "YouTube")
                        .OrderBy(v => v.Type == "Trailer" ? 0 : 1) // Trailers first
                        .ThenBy(v => v.Type == "Teaser" ? 0 : 1)   // Then Teasers
                        .FirstOrDefault();
                    
                    if (bestMatch != null) 
                    {
                        trailerKey = bestMatch.Key;
                        System.Diagnostics.Debug.WriteLine($"[TMDB-Trailer] Selected Localized ({language}) {bestMatch.Type}: {trailerKey} (Name: {bestMatch.Name})");
                    }
                }

                // 2. Fallback to English/Global if no localized trailer found
                if (string.IsNullOrEmpty(trailerKey))
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Trailer] No localized trailer found for {language}. Retrying with English (en-US) fallback...");
                    var fallbackUrl = $"{BASE_URL}/{type}/{tmdbId}/videos?api_key={API_KEY}&language=en-US";
                    var fallbackJson = await _client.GetStringAsync(fallbackUrl);
                    var fallbackResult = JsonSerializer.Deserialize<TmdbVideosResponse>(fallbackJson);

                    if (fallbackResult?.Results != null && fallbackResult.Results.Count > 0)
                    {
                        var bestFallback = fallbackResult.Results
                            .Where(v => v.Site == "YouTube")
                            .OrderBy(v => v.Type == "Trailer" ? 0 : 1)
                            .ThenBy(v => v.Type == "Teaser" ? 0 : 1)
                            .FirstOrDefault();
                        
                        if (bestFallback != null) 
                        {
                            trailerKey = bestFallback.Key;
                            System.Diagnostics.Debug.WriteLine($"[TMDB-Trailer] Selected Fallback (en-US) {bestFallback.Type}: {trailerKey} (Name: {bestFallback.Name})");
                        }
                    }
                }

                // 3. Last resort: Try without ANY language parameter to see if anything exists
                if (string.IsNullOrEmpty(trailerKey))
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Trailer] No English trailer found. Retrying with NO language parameter (Global)...");
                    var lastUrl = $"{BASE_URL}/{type}/{tmdbId}/videos?api_key={API_KEY}";
                    var lastJson = await _client.GetStringAsync(lastUrl);
                    var lastResult = JsonSerializer.Deserialize<TmdbVideosResponse>(lastJson);
                    
                    if (lastResult?.Results != null && lastResult.Results.Count > 0)
                    {
                        var lastMatch = lastResult.Results
                            .Where(v => v.Site == "YouTube")
                            .OrderBy(v => v.Type == "Trailer" ? 0 : 1)
                            .FirstOrDefault();
                        
                        if (lastMatch != null) 
                        {
                            trailerKey = lastMatch.Key;
                            System.Diagnostics.Debug.WriteLine($"[TMDB-Trailer] Selected Global {lastMatch.Type}: {trailerKey} (Name: {lastMatch.Name})");
                        }
                    }
                }

                if (!string.IsNullOrEmpty(trailerKey))
                {
                    TmdbCacheService.Instance.Set(cacheKey, trailerKey);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB-Trailer] FAILED to find any YouTube trailer/teaser/clip for ID:{tmdbId}");
                }
                
                return trailerKey;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB-Trailer] EXCEPTION: {ex.Message}");
                return null;
            }
        }

        public static async Task<TmdbCreditsResponse?> GetCreditsAsync(int id, bool isTv = false, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                language ??= AppSettings.TmdbLanguage;
                string type = isTv ? "tv" : "movie";
                var cacheKey = $"credits_{type}_{id}_{language}";
                if (TmdbCacheService.Instance.Get<TmdbCreditsResponse>(cacheKey) is TmdbCreditsResponse cached) return cached;

                var url = $"{BASE_URL}/{type}/{id}/credits?api_key={API_KEY}&language={language}";
                System.Diagnostics.Debug.WriteLine($"[TMDB] Fetching Credits ({language}): {url}");
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbCreditsResponse>(json);
                if (result != null) TmdbCacheService.Instance.Set(cacheKey, result);
                return result;
            }
            catch { return null; }
        }

        // [NEW] Person Search
        public static async Task<TmdbPersonSearchResult?> SearchPersonAsync(string name, CancellationToken ct = default)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                var lang = AppSettings.TmdbLanguage;
                var cacheKey = $"person_search_{name}";
                if (TmdbCacheService.Instance.Get<TmdbPersonSearchResponse>(cacheKey) is TmdbPersonSearchResponse cached)
                    return cached.Results?.FirstOrDefault();

                var encoded = Uri.EscapeDataString(name);
                var url = $"{BASE_URL}/search/person?api_key={API_KEY}&query={encoded}&language={lang}";
                System.Diagnostics.Debug.WriteLine($"[TMDB] Person Search: {url}");
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbPersonSearchResponse>(json);
                if (result?.Results?.Count > 0)
                {
                    TmdbCacheService.Instance.Set(cacheKey, result);
                    return result.Results[0];
                }
            }
            catch { }
            return null;
        }

        // [NEW] Person Details
        public static async Task<TmdbPersonDetails?> GetPersonDetailsAsync(int personId, CancellationToken ct = default)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                var lang = AppSettings.TmdbLanguage;
                var cacheKey = $"person_details_{personId}_{lang}";
                if (TmdbCacheService.Instance.Get<TmdbPersonDetails>(cacheKey) is TmdbPersonDetails cached) return cached;

                var url = $"{BASE_URL}/person/{personId}?api_key={API_KEY}&language={lang}";
                System.Diagnostics.Debug.WriteLine($"[TMDB] Person Details: {url}");
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbPersonDetails>(json);
                if (result != null) TmdbCacheService.Instance.Set(cacheKey, result);
                return result;
            }
            catch { return null; }
        }

        // [NEW] Person Combined Credits
        public static async Task<TmdbPersonCreditsResponse?> GetPersonCombinedCreditsAsync(int personId, CancellationToken ct = default)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                var lang = AppSettings.TmdbLanguage;
                var cacheKey = $"person_credits_{personId}_{lang}";
                if (TmdbCacheService.Instance.Get<TmdbPersonCreditsResponse>(cacheKey) is TmdbPersonCreditsResponse cached) return cached;

                var url = $"{BASE_URL}/person/{personId}/combined_credits?api_key={API_KEY}&language={lang}";
                System.Diagnostics.Debug.WriteLine($"[TMDB] Person Credits: {url}");
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbPersonCreditsResponse>(json);
                if (result != null) TmdbCacheService.Instance.Set(cacheKey, result);
                return result;
            }
            catch { return null; }
        }

        // [NEW] AIOMetadata People Search (with 1h in-memory cache)
        public static async Task<List<StremioMediaStream>> SearchPeopleViaStremioAsync(string baseUrl, string personName, string type = "all", CancellationToken ct = default, Action<List<StremioMediaStream>> onProgress = null)
        {
            var cacheKey = $"{baseUrl}_{personName}";
            if (_stremioPeopleCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expiry)
            {
                System.Diagnostics.Debug.WriteLine($"[Stremio] People Search CACHE HIT: {personName}");
                onProgress?.Invoke(cached.Results);
                return cached.Results;
            }

            try
            {
                var encoded = Uri.EscapeDataString(personName);
                var catalogs = new[]
                {
                    $"{baseUrl.TrimEnd('/').Replace("/manifest.json", "")}/catalog/movie/people_search.people_search_movie/search={encoded}.json",
                    $"{baseUrl.TrimEnd('/').Replace("/manifest.json", "")}/catalog/series/people_search.people_search_series/search={encoded}.json"
                };

                var allResults = new List<StremioMediaStream>();
                var resultsLock = new System.Threading.Lock();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var tasks = catalogs.Select(async url =>
                {
                    System.Diagnostics.Debug.WriteLine($"[Stremio] People Search Start: {url}");
                    try
                    {
                        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                        using var response = await _client.SendAsync(request, cts.Token);
                        string json = await response.Content.ReadAsStringAsync();
                        
                        if (!response.IsSuccessStatusCode) return;

                        var result = JsonSerializer.Deserialize<StremioMetaResponse>(json, options);
                        if (result?.Metas != null)
                        {
                            var batch = result.Metas.Select(m => new StremioMediaStream(m)).ToList();
                            lock (resultsLock)
                            {
                                allResults.AddRange(batch);
                            }
                            onProgress?.Invoke(batch);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Stremio] Catalog Error ({url}): {ex.Message}");
                    }
                });

                await Task.WhenAll(tasks);

                var deduped = allResults.DistinctBy(m => m.IMDbId ?? m.Id.ToString()).ToList();
                _stremioPeopleCache[cacheKey] = (deduped, DateTime.UtcNow.Add(_stremioPeopleCacheTtl));
                return deduped;
            }
            catch { return new List<StremioMediaStream>(); }
        }

        public static async Task<TmdbMovieDetails?> GetDetailsAsync(int id, bool isTv = false, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                language ??= AppSettings.TmdbLanguage;
                string type = isTv ? "tv" : "movie";
                var cacheKey = $"details_{type}_{id}_{language}";
                if (TmdbCacheService.Instance.Get<TmdbMovieDetails>(cacheKey) is TmdbMovieDetails cached) return cached;

                var url = $"{BASE_URL}/{type}/{id}?api_key={API_KEY}&language={language}";
                System.Diagnostics.Debug.WriteLine($"[TMDB] Details Request ({language}): {url}");

                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbMovieDetails>(json);
                
                if (result != null)
                {
                     System.Diagnostics.Debug.WriteLine($"[TMDB] Details Parsed ({language}). Overview Length: {result.Overview?.Length ?? 0}");
                     TmdbCacheService.Instance.Set(cacheKey, result);
                }
                else
                {
                     System.Diagnostics.Debug.WriteLine($"[TMDB] Failed to deserialize Details for {id} ({language})");
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB] GetDetails Error ({id}, {isTv}, {language}): {ex.Message}");
                return null; 
            }
        }

        public static async Task<TmdbSeasonDetails?> GetSeasonDetailsAsync(int tvId, int seasonNumber, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                language ??= AppSettings.TmdbLanguage;
                var shortLang = language.Split('-')[0];
                var cacheKey = $"season_{tvId}_s{seasonNumber}_{language}";
                if (TmdbCacheService.Instance.Get<TmdbSeasonDetails>(cacheKey) is TmdbSeasonDetails cached) return cached;

                var url = $"{BASE_URL}/tv/{tvId}/season/{seasonNumber}?api_key={API_KEY}&language={language}&include_image_language={shortLang},en,null";
                System.Diagnostics.Debug.WriteLine($"[TMDB] Season Details Request ({language}): {url}");
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbSeasonDetails>(json);
                if (result?.Episodes != null)
                {
                     int withImg = result.Episodes.Count(e => !string.IsNullOrEmpty(e.StillPath));
                     System.Diagnostics.Debug.WriteLine($"[TMDB] Season {seasonNumber} ({language}) loaded. Episodes: {result.Episodes.Count}, With Images: {withImg}");
                }
                if (result != null) TmdbCacheService.Instance.Set(cacheKey, result);
                return result;
            }
            catch 
            {
                 return null; 
            }
        }

        public static async Task<TmdbMovieResult?> GetMovieByIdAsync(int movieId, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                language ??= AppSettings.TmdbLanguage;
                var shortLang = language.Split('-')[0];
                var cacheKey = $"movie_id_{movieId}_{language}";
                if (TmdbCacheService.Instance.Get<TmdbMovieResult>(cacheKey) is TmdbMovieResult cached) 
                {
                    // [NEW] Persist mapping even on cache hits
                    if (!string.IsNullOrEmpty(cached.ResolvedImdbId))
                        IdMappingService.Instance.RegisterMapping(cached.ResolvedImdbId, cached.Id.ToString());
                    return cached;
                }
                var url = $"{BASE_URL}/movie/{movieId}?api_key={API_KEY}&language={language}&append_to_response=images,external_ids&include_image_language={shortLang},en,null";
                System.Diagnostics.Debug.WriteLine($"[TMDB] GetMovieById Request: {url}");
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbMovieResult>(json);
                if (result != null) 
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB] GetMovieById Parsed: {result.Title}. Resolved IMDb: {result.ResolvedImdbId}");
                    TmdbCacheService.Instance.Set(cacheKey, result);

                    // [NEW] Persist the ID mapping globally for subtitle resolution
                    if (!string.IsNullOrEmpty(result.ResolvedImdbId))
                        IdMappingService.Instance.RegisterMapping(result.ResolvedImdbId, result.Id.ToString());
                }
                return result;
            }
            catch { return null; }
        }

        public static async Task<TmdbMovieResult?> GetTvByIdAsync(int tvId, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                language ??= AppSettings.TmdbLanguage;
                var shortLang = language.Split('-')[0];
                var cacheKey = $"tv_id_{tvId}_{language}";
                if (TmdbCacheService.Instance.Get<TmdbMovieResult>(cacheKey) is TmdbMovieResult cached) 
                {
                    // [NEW] Persist mapping even on cache hits
                    if (!string.IsNullOrEmpty(cached.ResolvedImdbId))
                        IdMappingService.Instance.RegisterMapping(cached.ResolvedImdbId, cached.Id.ToString());
                    return cached;
                }
                var url = $"{BASE_URL}/tv/{tvId}?api_key={API_KEY}&language={language}&append_to_response=images,external_ids&include_image_language={shortLang},en,null";
                System.Diagnostics.Debug.WriteLine($"[TMDB] GetTvById Request: {url}");
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbMovieResult>(json);
                if (result != null) 
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB] GetTvById Parsed: {result.Name}. Resolved IMDb: {result.ResolvedImdbId}");
                    TmdbCacheService.Instance.Set(cacheKey, result);

                    // [NEW] Persist the ID mapping globally for subtitle resolution
                    if (!string.IsNullOrEmpty(result.ResolvedImdbId))
                        IdMappingService.Instance.RegisterMapping(result.ResolvedImdbId, result.Id.ToString());
                }
                return result;
            }
            catch { return null; }
        }

        public static async Task<List<TmdbMovieResult>> GetRecommendationsAsync(int id, bool isTv = false, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return new List<TmdbMovieResult>();
            try
            {
                language ??= AppSettings.TmdbLanguage;
                string type = isTv ? "tv" : "movie";
                var cacheKey = $"recommendations_{type}_{id}_{language}";
                if (TmdbCacheService.Instance.Get<List<TmdbMovieResult>>(cacheKey) is List<TmdbMovieResult> cached) return cached;

                var url = $"{BASE_URL}/{type}/{id}/recommendations?api_key={API_KEY}&language={language}";
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbSearchResponse>(json);

                var recommendations = result?.Results ?? new List<TmdbMovieResult>();
                TmdbCacheService.Instance.Set(cacheKey, recommendations);
                return recommendations;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB] Recommendations Error: {ex.Message}");
                return new List<TmdbMovieResult>();
            }
        }

        public static async Task<TmdbMovieResult?> GetTvByExternalIdAsync(string externalId, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                language ??= AppSettings.TmdbLanguage;
                var cacheKey = $"find_tv_{externalId}_{language}";
                if (TmdbCacheService.Instance.Get<TmdbMovieResult>(cacheKey) is TmdbMovieResult cached) return cached;

                // Use /find/ endpoint
                var url = $"{BASE_URL}/find/{externalId}?api_key={API_KEY}&external_source=imdb_id&language={language}";
                System.Diagnostics.Debug.WriteLine($"[TMDB] Find ID: {url}");
                var json = await _client.GetStringAsync(url);
                
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                 if (root.TryGetProperty("tv_results", out var tvs) && tvs.GetArrayLength() > 0)
                {
                    var first = tvs[0];
                    var result = JsonSerializer.Deserialize<TmdbMovieResult>(first.GetRawText());
                    if (result != null)
                    {
                        TmdbCacheService.Instance.Set(cacheKey, result);
                        // [NEW] Persist the ID mapping globally
                        if (!string.IsNullOrEmpty(result.ImdbId))
                            IdMappingService.Instance.RegisterMapping(result.ImdbId, result.Id.ToString());
                    }
                    return result;
                }
                 // Fallback to movie if TV not found (rare but possible for cross-listings)
                if (root.TryGetProperty("movie_results", out var movies) && movies.GetArrayLength() > 0)
                {
                    var first = movies[0];
                    var result = JsonSerializer.Deserialize<TmdbMovieResult>(first.GetRawText());
                    if (result != null)
                    {
                        TmdbCacheService.Instance.Set(cacheKey, result);
                        // [NEW] Persist the ID mapping globally
                        if (!string.IsNullOrEmpty(result.ImdbId))
                            IdMappingService.Instance.RegisterMapping(result.ImdbId, result.Id.ToString());
                    }
                    return result;
                }

                return null;
            }
            catch { return null; }
        }

        public static async Task<TmdbMovieResult?> GetMovieByExternalIdAsync(string externalId, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY)) return null;
            try
            {
                language ??= AppSettings.TmdbLanguage;
                var cacheKey = $"find_movie_{externalId}_{language}";
                if (TmdbCacheService.Instance.Get<TmdbMovieResult>(cacheKey) is TmdbMovieResult cached) return cached;

                // Use /find/ endpoint
                var url = $"{BASE_URL}/find/{externalId}?api_key={API_KEY}&external_source=imdb_id&language={language}";
                System.Diagnostics.Debug.WriteLine($"[TMDB] Find ID: {url}");
                var json = await _client.GetStringAsync(url);
                
                // We need a specific response model for Find, or use dynamic/JsonElement
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("movie_results", out var movies) && movies.GetArrayLength() > 0)
                {
                    var first = movies[0];
                    var result = JsonSerializer.Deserialize<TmdbMovieResult>(first.GetRawText());
                    if (result != null)
                    {
                        TmdbCacheService.Instance.Set(cacheKey, result);
                        // [NEW] Persist the ID mapping globally
                        if (!string.IsNullOrEmpty(result.ImdbId))
                            IdMappingService.Instance.RegisterMapping(result.ImdbId, result.Id.ToString());
                    }
                    return result;
                }
                 if (root.TryGetProperty("tv_results", out var tvs) && tvs.GetArrayLength() > 0)
                {
                    var first = tvs[0];
                    var result = JsonSerializer.Deserialize<TmdbMovieResult>(first.GetRawText());
                    if (result != null)
                    {
                        TmdbCacheService.Instance.Set(cacheKey, result);
                        // [NEW] Persist the ID mapping globally
                        if (!string.IsNullOrEmpty(result.ImdbId))
                            IdMappingService.Instance.RegisterMapping(result.ImdbId, result.Id.ToString());
                    }
                    return result;
                }

                return null;
            }
            catch { return null; }
        }

        public static string ExtractYear(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(input, @"\((19|20)\d{2}\)|\[(19|20)\d{2}\]");
            if (match.Success)
            {
                return match.Value.Substring(1, 4);
            }
            return null;
        }

        public static string CleanEpisodeTitle(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            // 1. Remove common IPTV prefixes and Season/Episode markers
            var clean = System.Text.RegularExpressions.Regex.Replace(input, @"(?i).*?(s\d{1,2}e\d{1,2}|episode\s*\d{1,2}|sezon\s*\d{1,2})\s*[-:]?\s*", "");
            
            // 2. Extra cleaning for any remaining common tags at the start
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"^\s*[-:]\s*", "");
            
            // 3. If the cleaning resulted in a very short string or nothing, it might be a generic title
            if (clean.Length < 2) return input; 

            return clean.Trim();
        }

        private static string CleanTitle(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            
            string clean = input;

            // 1. Remove common IPTV prefixes/brackets like "[TR]", "[DE]", "[Dual]", "VIP |", "(4K)"
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(?i)\[.*?\]|\(.*?\)", "");
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(?i)\b(tr|ger|eng|en|de|fr|it|es|dual|vip|top|fhd|hd|sd|uhd|4k|hevc|x265|x264|web-dl|bluray)\b", "");

            // 2. Remove common IPTV separators and noise
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s*[:|/-]\s*", " ");

            // 3. Remove Year patterns: 2024
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\b(19|20)\d{2}\b", "");

            // 4. Handle specific IPTV prefix patterns like "Something - Title"
            int lastIndex = Math.Max(clean.LastIndexOf("  "), clean.LastIndexOf(" - "));
            if (lastIndex != -1)
            {
                var potential = clean.Substring(lastIndex + 1).Trim();
                if (potential.Length > 2) clean = potential;
            }

            // 5. Cleanup punctuation and extra spaces
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"[._\-|]+", " ");
            
            string final = clean.Trim();

            // Fallback: If cleaning was too aggressive, use a lighter version
            if (final.Length < 3)
            {
                final = System.Text.RegularExpressions.Regex.Replace(input, @"\s*[\(\[].*?[\)\]]", "").Trim();
            }

            return final;
        }

        public static string GetImageUrl(string path, string size = "original")
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (!path.StartsWith("/")) path = "/" + path;
            return $"https://image.tmdb.org/t/p/{size}{path}";
        }

        public static async Task<List<TmdbMovieResult>> GetMovieRecommendationsAsync(string tmdbId, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY) || string.IsNullOrEmpty(tmdbId)) return new List<TmdbMovieResult>();
            try
            {
                language ??= AppSettings.TmdbLanguage;
                var cacheKey = $"movie_recs_{tmdbId}_{language}";
                if (TmdbCacheService.Instance.Get<List<TmdbMovieResult>>(cacheKey) is List<TmdbMovieResult> cached) return cached;

                var url = $"{BASE_URL}/movie/{tmdbId}/recommendations?api_key={API_KEY}&language={language}";
                var json = await _client.GetStringAsync(url);
                var response = JsonSerializer.Deserialize<TmdbSearchResponse>(json);
                var results = response?.Results ?? new List<TmdbMovieResult>();

                TmdbCacheService.Instance.Set(cacheKey, results);
                return results;
            }
            catch { return new List<TmdbMovieResult>(); }
        }

        public static async Task<List<TmdbMovieResult>> GetTvRecommendationsAsync(string tmdbId, string language = null)
        {
            if (!AppSettings.IsTmdbEnabled || string.IsNullOrEmpty(API_KEY) || string.IsNullOrEmpty(tmdbId)) return new List<TmdbMovieResult>();
            try
            {
                language ??= AppSettings.TmdbLanguage;
                var cacheKey = $"tv_recs_{tmdbId}_{language}";
                if (TmdbCacheService.Instance.Get<List<TmdbMovieResult>>(cacheKey) is List<TmdbMovieResult> cached) return cached;

                var url = $"{BASE_URL}/tv/{tmdbId}/recommendations?api_key={API_KEY}&language={language}";
                var json = await _client.GetStringAsync(url);
                var response = JsonSerializer.Deserialize<TmdbSearchResponse>(json);
                var results = response?.Results ?? new List<TmdbMovieResult>();

                TmdbCacheService.Instance.Set(cacheKey, results);
                return results;
            }
            catch { return new List<TmdbMovieResult>(); }
        }

        public static async Task<List<PersonFilmographyItem>> GetPersonFilmographyAsync(int tmdbId, CancellationToken token)
        {
            try
            {
                var credits = await GetPersonCombinedCreditsAsync(tmdbId, token);
                var list = new List<PersonFilmographyItem>();
                if (credits == null) return list;

                void ProcessList(List<TmdbPersonCredit> source, bool isCast)
                {
                    if (source == null) return;
                    foreach (var c in source)
                    {
                        var release = c.ReleaseDate ?? c.FirstAirDate;
                        DateTime? releaseDate = null;
                        if (DateTime.TryParse(release, out var d)) releaseDate = d;

                        list.Add(new PersonFilmographyItem
                        {
                            Id = c.Id,
                            Title = c.Title ?? c.Name,
                            Character = c.Character ?? c.Job,
                            PosterPath = c.PosterPath,
                            VoteAverage = c.VoteAverage,
                            ReleaseDate = releaseDate,
                            MediaType = c.MediaType,
                            IsCast = isCast
                        });
                    }
                }

                ProcessList(credits.Cast, true);
                ProcessList(credits.Crew, false);

                return list.OrderByDescending(f => f.ReleaseDate ?? DateTime.MinValue).ToList();
            }
            catch { return new List<PersonFilmographyItem>(); }
        }

        public static async Task<IMediaStream> ResolveFilmographyToStreamAsync(PersonFilmographyItem film, string parentImdbId)
        {
            if (film == null) return null;
            
            // If it's a TMDB item, we need to resolve it to an IMDb ID or similar for playback
            if (film.MediaType == "movie")
            {
                var details = await GetMovieByIdAsync(film.Id);
                if (details != null && !string.IsNullOrEmpty(details.ResolvedImdbId))
                    return new StremioMediaStream { Meta = new StremioMeta { Id = details.ResolvedImdbId, Type = "movie", Name = details.Title } };
            }
            else if (film.MediaType == "tv")
            {
                var details = await GetTvByIdAsync(film.Id);
                if (details != null && !string.IsNullOrEmpty(details.ResolvedImdbId))
                    return new StremioMediaStream { Meta = new StremioMeta { Id = details.ResolvedImdbId, Type = "series", Name = details.Name } };
            }
            
            return null;
        }
    }

    // Models for TMDB
    public class TmdbSearchResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbMovieResult> Results { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbMovieResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("original_title")]
        public string OriginalTitle { get; set; }
        
        [JsonPropertyName("name")]
        public string Name { get; set; } 

        [JsonPropertyName("original_name")]
        public string OriginalName { get; set; }

        [JsonIgnore]
        public string DisplayTitle => !string.IsNullOrEmpty(Title) ? Title : Name;

        [JsonIgnore]
        public string DisplayOriginalTitle => !string.IsNullOrEmpty(OriginalTitle) ? OriginalTitle : OriginalName;
        
        [JsonPropertyName("overview")]
        public string Overview { get; set; }
        
        [JsonPropertyName("backdrop_path")]
        public string BackdropPath { get; set; }
        
        [JsonPropertyName("poster_path")]
        public string PosterPath { get; set; }
        
        [JsonPropertyName("vote_average")]
        public double VoteAverage { get; set; }

        [JsonPropertyName("release_date")]
        public string ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string FirstAirDate { get; set; }

        [JsonIgnore]
        public string DisplayDate => !string.IsNullOrEmpty(ReleaseDate) ? ReleaseDate : FirstAirDate;

        [JsonPropertyName("genre_ids")]
        public List<int> GenreIds { get; set; }

        [JsonPropertyName("images")]
        public TmdbImages Images { get; set; }

        [JsonPropertyName("seasons")]
        public List<TmdbSeason> Seasons { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("external_ids")]
        public TmdbExternalIds? ExternalIds { get; set; }

        public string? ResolvedImdbId => ImdbId ?? ExternalIds?.ImdbId;

        public string GetGenreNames()
        {
            if (GenreIds == null || GenreIds.Count == 0) return "Genel";
            
            var names = new List<string>();
            foreach (var id in GenreIds.Take(3))
            {
                if (_genreMap.TryGetValue(id, out string name))
                    names.Add(name);
            }
            
            return names.Count > 0 ? string.Join(" • ", names) : "Genel";
        }

        private static readonly Dictionary<int, string> _genreMap = new Dictionary<int, string>
        {
            {28, "Aksiyon"}, {12, "Macera"}, {16, "Animasyon"}, {35, "Komedi"}, {80, "Suç"},
            {99, "Belgesel"}, {18, "Dram"}, {10751, "Aile"}, {14, "Fantastik"}, {36, "Tarih"},
            {27, "Korku"}, {10402, "Müzik"}, {9648, "Gizem"}, {10749, "Romantik"}, {878, "Bilim Kurgu"},
            {10770, "TV Film"}, {53, "Gerilim"}, {10752, "Savaş"}, {37, "Vahşi Batı"},
            {10759, "Aksiyon & Macera"}, {10762, "Çocuk"}, {10763, "Haber"}, {10764, "Reality"},
            {10765, "Bilim Kurgu & Fantazi"}, {10766, "Pembe Dizi"}, {10767, "Talk Show"}, {10768, "Savaş & Politik"}
        };

        public string FullBackdropUrl => !string.IsNullOrEmpty(BackdropPath) ? $"https://image.tmdb.org/t/p/w1280{BackdropPath}" : null;
    }

    public class TmdbVideosResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbVideo> Results { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbVideo
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } 
        
        [JsonPropertyName("site")]
        public string Site { get; set; }
        
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("iso_639_1")]
        public string Iso639_1 { get; set; }

        [JsonPropertyName("iso_3166_1")]
        public string Iso3166_1 { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public class TmdbCreditsResponse
    {
        [JsonPropertyName("cast")]
        public List<TmdbCast> Cast { get; set; }

        [JsonPropertyName("crew")]
        public List<TmdbCrew> Crew { get; set; }
    }

    public class TmdbImages
    {
        [JsonPropertyName("backdrops")]
        public List<TmdbImage> Backdrops { get; set; }

        [JsonPropertyName("logos")]
        public List<TmdbImage> Logos { get; set; }

        [JsonPropertyName("posters")]
        public List<TmdbImage> Posters { get; set; }
    }

    public class TmdbImage
    {
        [JsonPropertyName("file_path")]
        public string FilePath { get; set; }

        [JsonPropertyName("iso_639_1")]
        public string Iso639_1 { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbCast
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
        
        [JsonPropertyName("character")]
        public string Character { get; set; }
        
        [JsonPropertyName("profile_path")]
        public string ProfilePath { get; set; }

        public string FullProfileUrl => !string.IsNullOrEmpty(ProfilePath) ? $"https://image.tmdb.org/t/p/w185{ProfilePath}" : null;
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbCrew
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("job")]
        public string Job { get; set; }

        [JsonPropertyName("department")]
        public string Department { get; set; }

        [JsonPropertyName("profile_path")]
        public string ProfilePath { get; set; }

        public string FullProfileUrl => !string.IsNullOrEmpty(ProfilePath) ? $"https://image.tmdb.org/t/p/w185{ProfilePath}" : null;
    }

    public class TmdbMovieDetails
    {
        [JsonPropertyName("runtime")]
        public int? Runtime { get; set; } 

        [JsonPropertyName("overview")]
        public string Overview { get; set; }

        [JsonPropertyName("genres")]
        public List<TmdbGenre> Genres { get; set; }

        [JsonPropertyName("imdb_id")]
        public string ImdbId { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbGenre
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbSeason
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("season_number")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("episode_count")]
        public int EpisodeCount { get; set; }
    }

    public class TmdbSeasonDetails
    {
        [JsonPropertyName("poster_path")]
        public string PosterPath { get; set; }

        [JsonPropertyName("episodes")]
        public List<TmdbEpisode> Episodes { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbEpisode
    {
        [JsonPropertyName("episode_number")]
        public int EpisodeNumber { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("overview")]
        public string Overview { get; set; }
        
        [JsonPropertyName("still_path")]
        public string StillPath { get; set; }

        [JsonPropertyName("runtime")]
        public int? Runtime { get; set; }

        [JsonPropertyName("air_date")]
        public string AirDate { get; set; }

        public DateTime? AirDateDateTime => !string.IsNullOrEmpty(AirDate) && DateTime.TryParse(AirDate, out var d) ? d : null;
        
        public string StillUrl => !string.IsNullOrEmpty(StillPath) ? $"https://image.tmdb.org/t/p/w300{StillPath}" : null;
    }

    public class TmdbExternalIds
    {
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("tv_db_id")]
        public object? TvdbId { get; set; }
    }

    // [NEW] Person Search Result
    public class TmdbPersonSearchResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("profile_path")]
        public string ProfilePath { get; set; }

        [JsonPropertyName("known_for_department")]
        public string KnownForDepartment { get; set; }

        [JsonPropertyName("known_for")]
        public List<TmdbPersonKnownFor> KnownFor { get; set; }
    }

    public class TmdbPersonKnownFor
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("poster_path")]
        public string PosterPath { get; set; }

        [JsonPropertyName("media_type")]
        public string MediaType { get; set; }
    }

    // [NEW] Person Details
    public class TmdbPersonDetails
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("biography")]
        public string Biography { get; set; }

        [JsonPropertyName("birthday")]
        public DateTime? Birthday { get; set; }

        [JsonPropertyName("place_of_birth")]
        public string PlaceOfBirth { get; set; }

        [JsonPropertyName("profile_path")]
        public string ProfilePath { get; set; }

        [JsonPropertyName("known_for_department")]
        public string KnownForDepartment { get; set; }
    }

    // [NEW] Person Credit
    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbPersonCredit
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("poster_path")]
        public string PosterPath { get; set; }

        [JsonPropertyName("character")]
        public string Character { get; set; }

        [JsonPropertyName("media_type")]
        public string MediaType { get; set; }

        [JsonPropertyName("release_date")]
        public string ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string FirstAirDate { get; set; }

        [JsonPropertyName("vote_average")]
        public double VoteAverage { get; set; }

        [JsonPropertyName("popularity")]
        public double Popularity { get; set; }

        [JsonPropertyName("genre_ids")]
        public List<int> GenreIds { get; set; }

        [JsonPropertyName("job")]
        public string Job { get; set; }

        [JsonPropertyName("department")]
        public string Department { get; set; }
    }

    public class TmdbPersonCreditsResponse
    {
        [JsonPropertyName("cast")]
        public List<TmdbPersonCredit> Cast { get; set; }

        [JsonPropertyName("crew")]
        public List<TmdbPersonCredit> Crew { get; set; }
    }

    public class TmdbPersonSearchResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbPersonSearchResult> Results { get; set; }
    }
}
