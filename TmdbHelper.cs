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

        public static async Task<TmdbMovieResult?> SearchMovieAsync(string title, string year = null)
        {
            try
            {
                // Clean title: Remove brackets, quality tags etc.
                var cleanTitle = CleanTitle(title);
                var query = Uri.EscapeDataString(cleanTitle);
                var url = $"{BASE_URL}/search/movie?api_key={API_KEY}&query={query}&language=tr-TR";
                
                if (!string.IsNullOrEmpty(year))
                {
                    url += $"&year={year}";
                }

                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbSearchResponse>(json);

                if (result?.Results != null && result.Results.Count > 0)
                {
                    return result.Results[0]; // Return best match
                }
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB] Search Error: {ex.Message}");
                return null;
            }
        }

        public static async Task<TmdbMovieResult?> SearchTvAsync(string title)
        {
            try
            {
                var cleanTitle = CleanTitle(title);
                var query = Uri.EscapeDataString(cleanTitle);
                var url = $"{BASE_URL}/search/tv?api_key={API_KEY}&query={query}&language=tr-TR";
                
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbSearchResponse>(json);

                if (result?.Results != null && result.Results.Count > 0)
                {
                    return result.Results[0];
                }
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TMDB] TV Search Error: {ex.Message}");
                return null;
            }
        }

        public static async Task<string?> GetTrailerKeyAsync(int tmdbId, bool isTv = false)
        {
            try
            {
                string type = isTv ? "tv" : "movie";
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
                var url = $"{BASE_URL}/{type}/{id}/credits?api_key={API_KEY}&language=tr-TR";
                var json = await _client.GetStringAsync(url);
                return JsonSerializer.Deserialize<TmdbCreditsResponse>(json);
            }
            catch { return null; }
        }

        public static async Task<TmdbMovieDetails?> GetDetailsAsync(int id, bool isTv = false)
        {
            try
            {
                string type = isTv ? "tv" : "movie";
                var url = $"{BASE_URL}/{type}/{id}?api_key={API_KEY}&language=tr-TR";
                var json = await _client.GetStringAsync(url);
                return JsonSerializer.Deserialize<TmdbMovieDetails>(json);
            }
            catch { return null; }
        }

        public static async Task<TmdbSeasonDetails?> GetSeasonDetailsAsync(int tvId, int seasonNumber)
        {
            try
            {
                var url = $"{BASE_URL}/tv/{tvId}/season/{seasonNumber}?api_key={API_KEY}&language=tr-TR";
                var json = await _client.GetStringAsync(url);
                return JsonSerializer.Deserialize<TmdbSeasonDetails>(json);
            }
            catch 
            {
                 return null; 
            }
        }

        private static string CleanTitle(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            // 1. Remove (...) and [...] content
            // We use Regex to remove things like (2024), [4K], [TR] etc.
            var clean = System.Text.RegularExpressions.Regex.Replace(input, @"\s*[\(\[].*?[\)\]]", "");
            
            // 2. Handle prefixes ending with " - " or similar
            // Example: "4K-TOP - Movie Name" -> "Movie Name"
            // Strategy: Split by " - " and take the last part if it looks substantial, or the longest part.
            // Often identifying the title is about finding the cleanest segment.
            
            if (clean.Contains(" - "))
            {
                var parts = clean.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
                // Usually the last part is the title in formats like "Category - Title"
                // But sometimes "Title - Subtitle".
                // Heuristic: If first part is short (<10 chars) and mostly upper/digits, it's a prefix.
                
                if (parts.Length > 1)
                {
                   // If first part looks like "TR", "EN", "4K-HEVC", skip it.
                   if (parts[0].Length < 12) 
                   {
                       return parts[1].Trim();
                   }
                }
            }
            
            // 3. Remove known standalone quality tags if they remain
            string[] tags = { "4K", "FHD", "HD", "SD", "HEVC", "H265", "TR", "EN", "X264" };
            foreach (var tag in tags)
            {
               // Remove " 4K" at end of string or similar
               // Simple replace for now
            }

            return clean.Trim();
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
