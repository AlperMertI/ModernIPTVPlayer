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
                var url = $"{BASE_URL}/search/movie?api_key={API_KEY}&query={query}";
                
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

        public static async Task<string?> GetTrailerKeyAsync(int tmdbId)
        {
            try
            {
                var url = $"{BASE_URL}/movie/{tmdbId}/videos?api_key={API_KEY}";
                var json = await _client.GetStringAsync(url);
                var result = JsonSerializer.Deserialize<TmdbVideosResponse>(json);

                if (result?.Results != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[TMDB] Found {result.Results.Count} videos for ID {tmdbId}");
                    // Find first Youtube trailer
                    var trailer = result.Results.FirstOrDefault(v => v.Site == "YouTube" && v.Type == "Trailer");
                    if (trailer != null) System.Diagnostics.Debug.WriteLine($"[TMDB] Trailer Found: {trailer.Key}");
                    else System.Diagnostics.Debug.WriteLine("[TMDB] No YouTube Trailer found.");
                    
                    return trailer?.Key; 
                }
                return null;
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
        
        [JsonPropertyName("overview")]
        public string Overview { get; set; }
        
        [JsonPropertyName("backdrop_path")]
        public string BackdropPath { get; set; }
        
        [JsonPropertyName("poster_path")]
        public string PosterPath { get; set; }
        
        [JsonPropertyName("vote_average")]
        public double VoteAverage { get; set; }

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
}
