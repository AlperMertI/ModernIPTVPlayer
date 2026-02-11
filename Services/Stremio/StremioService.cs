using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;

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
            _client.Timeout = TimeSpan.FromSeconds(10); // Fast timeout
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
                string url = $"{baseUrl.TrimEnd('/')}/manifest.json";
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
                        result.Add(new StremioMediaStream(meta));
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
    }
}
