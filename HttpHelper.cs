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
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        private static readonly SocketsHttpHandler _handler;
        private static readonly HttpClient _client;

        static HttpHelper()
        {
            _handler = new SocketsHttpHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                EnableMultipleHttp2Connections = true
            };

            _client = new HttpClient(_handler);
            _client.Timeout = TimeSpan.FromSeconds(30);
            
            // Standard Browser Headers
            _client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            _client.DefaultRequestHeaders.Add("Accept", "*/*");
            _client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            _client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            var language = System.Globalization.CultureInfo.CurrentUICulture.Name;
            _client.DefaultRequestHeaders.Add("Accept-Language", $"{language},{language.Split('-')[0]};q=0.9,en-US;q=0.8,en;q=0.7");
        }

        public static HttpClient Client => _client;
        public static CookieContainer CookieContainer => _handler.CookieContainer;

        /// <summary>
        /// [NATIVE AOT] Safely deserializes a JSON string into a List of T using source-generated metadata.
        /// </summary>
        public static List<T> TryDeserializeList<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> typeInfo)
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
                catch (Exception ex) when (ex.Source != "System.Text.Json")
                {
                    throw; 
                }
                catch
                {
                    AppLogger.Warn($"[HttpHelper] Expected JSON array for {typeof(T).Name} but received an object.");
                }
                return new List<T>();
            }

            try
            {
                return JsonSerializer.Deserialize(json, typeInfo) ?? new List<T>();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[HttpHelper] Deserialization failed for {typeof(T).Name}", ex);
                return new List<T>();
            }
        }

        /// <summary>
        /// Centralized helper to apply standard browser headers to WinRT HttpClients (used by Media Foundation)
        /// </summary>
        public static void ApplyDefaultHeaders(Windows.Web.Http.HttpClient client)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Add("Accept", "*/*");
            client.DefaultRequestHeaders.Connection.ParseAdd("keep-alive");
        }
    }
}
