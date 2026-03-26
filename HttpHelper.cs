using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer
{
    public static class HttpHelper
    {
        private static readonly HttpClientHandler _handler;
        private static readonly HttpClient _client;

        static HttpHelper()
        {
            _handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _client = new HttpClient(_handler);
            
            // Standard Browser Headers
            _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _client.DefaultRequestHeaders.Add("Accept", "*/*");
            _client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            var language = System.Globalization.CultureInfo.CurrentUICulture.Name;
            _client.DefaultRequestHeaders.Add("Accept-Language", $"{language},{language.Split('-')[0]};q=0.9,en-US;q=0.8,en;q=0.7");
        }

        public static HttpClient Client => _client;
        public static CookieContainer CookieContainer => _handler.CookieContainer;

        /// <summary>
        /// Safely deserializes a JSON string into a List of T, handling cases where the API returns 
        /// an object (e.g. error message) instead of an array.
        /// </summary>
        public static List<T> TryDeserializeList<T>(string json, JsonSerializerOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<T>();

            string trimmed = json.Trim();
            
            // Check if it's a JSON array
            if (!trimmed.StartsWith("[") && trimmed.StartsWith("{"))
            {
                // It's an object, likely an error, account info, or message
                try 
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    string? serverError = null;
                    
                    if (doc.RootElement.TryGetProperty("error", out var errProp)) serverError = errProp.GetString();
                    else if (doc.RootElement.TryGetProperty("message", out var msgProp)) serverError = msgProp.GetString();

                    if (!string.IsNullOrEmpty(serverError))
                    {
                        AppLogger.Warn($"[HttpHelper] Server returned error: {serverError}");
                        throw new Exception(serverError);
                    }
                }
                catch (Exception ex) when (ex.Source != "System.Text.Json") // Don't catch our own thrown exception
                {
                    throw; 
                }
                catch
                {
                    // Fallback: It's an object but not a recognized error.
                    AppLogger.Warn($"[HttpHelper] Expected JSON array for {typeof(T).Name} but received an object: {trimmed.Substring(0, Math.Min(200, trimmed.Length))}...");
                }
                return new List<T>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<T>>(json, options) ?? new List<T>();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[HttpHelper] Deserialization failed for {typeof(T).Name}", ex);
                AppLogger.Info($"[HttpHelper] Failed JSON snippet: {trimmed.Substring(0, Math.Min(500, trimmed.Length))}");
                return new List<T>();
            }
        }
    }
}
