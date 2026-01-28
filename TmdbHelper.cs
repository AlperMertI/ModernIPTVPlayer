using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace ModernIPTVPlayer
{
    public class TmdbHelper
    {
        private const string API_KEY = "d0562d9c2a8b502d37d30b91963dd329";
        private const string BASE_URL = "https://api.themoviedb.org/3";
        private static HttpClient _client = new HttpClient();
        
        // Simple Memory Cache
        private static Dictionary<string, TmdbMovieResult> _searchCache = new();
        private static Dictionary<string, TmdbMovieDetails> _detailsCache = new();
        private static Dictionary<string, TmdbCreditsResponse> _creditsCache = new();
        private static Dictionary<string, TmdbSeasonDetails> _seasonCache = new();
        private static Dictionary<int, TmdbMovieResult> _tvByIdCache = new();

        public static async Task<TmdbMovieResult?> SearchMovieAsync(string title, string year = null)
        {
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

                var cacheKey = $"movie_{cleanTitle}_{year}";
                
                if (_searchCache.TryGetValue(cacheKey, out var cached)) return cached;

                var query = Uri.EscapeDataString(cleanTitle);
                var url = $"{BASE_URL}/search/movie?api_key={API_KEY}&query={query}&language=tr-TR";
                
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
                    _searchCache[cacheKey] = match;
                    return match;
                }
                
                // FALLBACK: If year search failed, try without year
                if (!string.IsNullOrEmpty(year))
                {
                     System.Diagnostics.Debug.WriteLine($"[TMDB] Year search failed. Retrying without year...");
                     url = $"{BASE_URL}/search/movie?api_key={API_KEY}&query={query}&language=tr-TR";
                     
                     json = await _client.GetStringAsync(url);
                     result = JsonSerializer.Deserialize<TmdbSearchResponse>(json);
                     
                     if (result?.Results != null && result.Results.Count > 0)
                     {
                        System.Diagnostics.Debug.WriteLine($"[TMDB] Fallback Search Found: {result.Results.Count} results");
                        var match = result.Results[0];
                        // Cache it under the original key too to save future lookups
                        _searchCache[cacheKey] = match;
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

        public static async Task<TmdbMovieResult?> SearchTvAsync(string title, string year = null)
        {
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

                var cacheKey = $"tv_{cleanTitle}_{year}";

                if (_searchCache.TryGetValue(cacheKey, out var cached)) return cached;

                var query = Uri.EscapeDataString(cleanTitle);
                var url = $"{BASE_URL}/search/tv?api_key={API_KEY}&query={query}&language=tr-TR";

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
                    _searchCache[cacheKey] = match;
                    return match;
                }
                
                // FALLBACK: If year search failed, try without year
                if (!string.IsNullOrEmpty(year))
                {
                     System.Diagnostics.Debug.WriteLine($"[TMDB] Year search failed. Retrying without year...");
                     url = $"{BASE_URL}/search/tv?api_key={API_KEY}&query={query}&language=tr-TR";
                     
                     json = await _client.GetStringAsync(url);
                     result = JsonSerializer.Deserialize<TmdbSearchResponse>(json);
                     
                     if (result?.Results != null && result.Results.Count > 0)
                     {
                        System.Diagnostics.Debug.WriteLine($"[TMDB] Fallback Search Found: {result.Results.Count} results");
                        var match = result.Results[0];
                        _searchCache[cacheKey] = match;
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

        private static Dictionary<string, string> _trailerCache = new();
        public static async Task<string?> GetTrailerKeyAsync(int tmdbId, bool isTv = false)
        {
            try
            {
                string type = isTv ? "tv" : "movie";
                var cacheKey = $"{type}_{tmdbId}";
                if (_trailerCache.TryGetValue(cacheKey, out var cached)) return cached;

                // Get all videos without language filter to find English trailers as fallback
                var url = $"{BASE_URL}/{type}/{tmdbId}/videos?api_key={API_KEY}";
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbVideosResponse>(json);

                if (result?.Results != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB] Found {result.Results.Count} videos for {type} ID {tmdbId}");
                    // Find first Youtube trailer
                    var trailer = result.Results.FirstOrDefault(v => v.Site == "YouTube" && v.Type == "Trailer");
                    if (trailer == null) 
                    {
                        // Fallback to Clip or Teaser if no Trailer
                        trailer = result.Results.FirstOrDefault(v => v.Site == "YouTube" && (v.Type == "Clip" || v.Type == "Teaser"));
                    }

                    if (trailer != null) System.Diagnostics.Debug.WriteLine($"[TMDB] Trailer/Video Found: {trailer.Key}");
                    else System.Diagnostics.Debug.WriteLine("[TMDB] No suitable YouTube video found.");
                    
                    if (trailer?.Key != null) _trailerCache[cacheKey] = trailer.Key;
                    return trailer?.Key; 
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        public static async Task<TmdbCreditsResponse?> GetCreditsAsync(int id, bool isTv = false)
        {
            try
            {
                string type = isTv ? "tv" : "movie";
                var cacheKey = $"{type}_{id}";
                if (_creditsCache.TryGetValue(cacheKey, out var cached)) return cached;

                var url = $"{BASE_URL}/{type}/{id}/credits?api_key={API_KEY}&language=tr-TR";
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbCreditsResponse>(json);
                if (result != null) _creditsCache[cacheKey] = result;
                return result;
            }
            catch { return null; }
        }

        public static async Task<TmdbMovieDetails?> GetDetailsAsync(int id, bool isTv = false)
        {
            try
            {
                string type = isTv ? "tv" : "movie";
                var cacheKey = $"{type}_{id}";
                if (_detailsCache.TryGetValue(cacheKey, out var cached)) return cached;

                var url = $"{BASE_URL}/{type}/{id}?api_key={API_KEY}&language=tr-TR";
                System.Diagnostics.Debug.WriteLine($"[TMDB] Details Request: {url}");

                var json = await _client.GetStringAsync(url);
                System.Diagnostics.Debug.WriteLine($"[TMDB] Details Response (Raw): {json.Substring(0, Math.Min(json.Length, 500))}..."); 

                var result = JsonSerializer.Deserialize<TmdbMovieDetails>(json);
                
                if (result != null)
                {
                     System.Diagnostics.Debug.WriteLine($"[TMDB] Details Parsed. Overview Length: {result.Overview?.Length ?? 0}, Overview Content: '{result.Overview}'");
                     _detailsCache[cacheKey] = result;
                }
                else
                {
                     System.Diagnostics.Debug.WriteLine($"[TMDB] Failed to deserialize Details for {id}");
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB] GetDetails Error: {ex.Message}");
                return null; 
            }
        }

        public static async Task<TmdbSeasonDetails?> GetSeasonDetailsAsync(int tvId, int seasonNumber)
        {
            try
            {
                var cacheKey = $"{tvId}_s{seasonNumber}";
                if (_seasonCache.TryGetValue(cacheKey, out var cached)) return cached;

                var url = $"{BASE_URL}/tv/{tvId}/season/{seasonNumber}?api_key={API_KEY}&language=tr-TR";
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbSeasonDetails>(json);
                if (result != null) _seasonCache[cacheKey] = result;
                return result;
            }
            catch 
            {
                 return null; 
            }
        }

        public static async Task<TmdbMovieResult?> GetTvByIdAsync(int tvId)
        {
            try
            {
                if (_tvByIdCache.TryGetValue(tvId, out var cached)) return cached;
                var url = $"{BASE_URL}/tv/{tvId}?api_key={API_KEY}&language=tr-TR";
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbMovieResult>(json);
                if (result != null) _tvByIdCache[tvId] = result;
                return result;
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
            // Pattern: Matches anything up to SxxExx or "Episode X" followed by separators
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

            // 1. Remove quality tags and common IPTV noise first (case insensitive)
            string[] noise = { "4K", "FHD", "HD", "SD", "HEVC", "H265", "X264", "WEB-DL", "BLURAY", "HDTV", "TOP", "VIP", "DUAL", "MULTI", "TR", "EN" };
            foreach (var tag in noise)
            {
                clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(?i)\b" + tag + @"\b", "");
            }

            // 2. Remove Year patterns: (2024), [2024], 2024
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s*[\(\[]?(19|20)\d{2}[\)\]]?", "");

            // 3. Handle specific IPTV prefix patterns like "Something - Title" or "Something | Title"
            // We look for the LAST separator if there are multiple, assuming the title is at the end
            int lastIndex = Math.Max(clean.LastIndexOf(" - "), clean.LastIndexOf(" | "));
            if (lastIndex != -1)
            {
                var potential = clean.Substring(lastIndex + 3).Trim();
                if (potential.Length > 2) clean = potential;
            }

            // 4. Final regex for any remaining brackets/parentheses content
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s*[\(\[].*?[\)\]]", "");

            // 5. Cleanup punctuation and extra spaces
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"[._\-|]+", " ");
            
            string final = clean.Trim();

            // If we've over-cleaned and produced a very short string, fallback to a simpler cleanup of the original
            if (final.Length < 3)
            {
                // Simple fallback: remove brackets and year from original
                final = System.Text.RegularExpressions.Regex.Replace(input, @"\s*[\(\[].*?[\)\]]", "").Trim();
            }

            return final;
        }
    }

    // Models for TMDB
    public class TmdbSearchResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbMovieResult> Results { get; set; }
    }

    public class TmdbMovieResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        
        [JsonPropertyName("title")]
        public string Title { get; set; }
        
        [JsonPropertyName("name")]
        public string Name { get; set; } // For TV Shows

        [JsonIgnore]
        public string DisplayTitle => !string.IsNullOrEmpty(Title) ? Title : Name;
        
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

    public class TmdbVideo
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } // Youtube ID
        
        [JsonPropertyName("site")]
        public string Site { get; set; }
        
        [JsonPropertyName("type")]
        public string Type { get; set; }
    }

    public class TmdbCreditsResponse
    {
        [JsonPropertyName("cast")]
        public List<TmdbCast> Cast { get; set; }
    }

    public class TmdbCast
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
        
        [JsonPropertyName("character")]
        public string Character { get; set; }
        
        [JsonPropertyName("profile_path")]
        public string ProfilePath { get; set; }

        public string FullProfileUrl => !string.IsNullOrEmpty(ProfilePath) ? $"https://image.tmdb.org/t/p/w185{ProfilePath}" : "ms-appx:///Assets/StoreLogo.png";
    }

    public class TmdbMovieDetails
    {
        [JsonPropertyName("runtime")]
        public int Runtime { get; set; } // Minutes

        [JsonPropertyName("overview")]
        public string Overview { get; set; }

        [JsonPropertyName("genres")]
        public List<TmdbGenre> Genres { get; set; }
    }

    public class TmdbGenre
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public class TmdbSeasonDetails
    {
        [JsonPropertyName("episodes")]
        public List<TmdbEpisode> Episodes { get; set; }
    }

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
        
        public string StillUrl => !string.IsNullOrEmpty(StillPath) ? $"https://image.tmdb.org/t/p/w300{StillPath}" : null;
    }
}
