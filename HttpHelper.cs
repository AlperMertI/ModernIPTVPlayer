using System;
using System.Net;
using System.Net.Http;

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
            _client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        }

        public static HttpClient Client => _client;
        public static CookieContainer CookieContainer => _handler.CookieContainer;
    }
}
