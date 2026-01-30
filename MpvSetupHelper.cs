using MpvWinUI;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections;
using System.Diagnostics;

namespace ModernIPTVPlayer
{
    public static class MpvSetupHelper
    {
        /// <summary>
        /// Initializes and configures an MpvPlayer instance for high-performance streaming.
        /// </summary>
        /// <param name="player">The player instance to configure.</param>
        /// <param name="streamUrl">The URL of the stream (used for cookie matching).</param>
        /// <param name="isSecondary">If true, applies more aggressive memory saving (smaller buffers).</param>
        public static async Task ConfigurePlayerAsync(MpvPlayer player, string streamUrl, bool isSecondary = false)
        {
            try
            {
                // 1. Initialize Core
                await player.InitializePlayerAsync();

                // 2. Network & Headers
                string headers = ""; // Scope Fix for Logging at line 87

                // Bypass headers for local bridge to avoid 400 Bad Request
                if (streamUrl.Contains("127.0.0.1"))
                {
                    Debug.WriteLine("[MpvSetupHelper] Local Bridge URL detected. Skipping external headers.");
                }
                else
                {
                    string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                    string cookieHeader = ExtractCookiesForUrl(streamUrl);
                    headers = $"Accept: */*\nConnection: keep-alive\nAccept-Language: en-US,en;q=0.9\n";

                    if (!string.IsNullOrEmpty(cookieHeader))
                    {
                        headers += $"Cookie: {cookieHeader}\n";
                        Debug.WriteLine($"[MpvSetupHelper] Cookies applied for {streamUrl}");
                    }

                    await player.SetPropertyAsync("user-agent", userAgent);
                    await player.SetPropertyAsync("http-header-fields", headers);
                }

                // 3. Hardware Acceleration
                // Prioritize 'auto-safe' which usually maps to d3d11va (Zero-Copy) on Windows
                await player.SetPropertyAsync("hwdec", "auto-safe");

                // 4. Performance Profile
                await player.SetPropertyAsync("profile", "fast"); // Low latency profile

                // 5. Buffering & Cache (Optimized for Multi-View vs Single View)
                await player.SetPropertyAsync("cache", "yes");
                await player.SetPropertyAsync("cache-pause", "no"); // Don't pause if cache runs low

                if (isSecondary)
                {
                    // Aggressive RAM saving but still enough for 4K streams
                    // 64MB max buffer (increased from 15MB to prevent demuxer overflow in 4K)
                    await player.SetPropertyAsync("demuxer-max-bytes", "64MiB");
                    await player.SetPropertyAsync("demuxer-max-back-bytes", "16MiB");
                    await player.SetPropertyAsync("demuxer-readahead-secs", "20");
                }
                else
                {
                    // Standard High Performance for Main Player
                    // 250MB buffer
                    await player.SetPropertyAsync("demuxer-max-bytes", "250MiB");
                    await player.SetPropertyAsync("demuxer-max-back-bytes", "100MiB");
                    await player.SetPropertyAsync("demuxer-readahead-secs", "120");
                }

                // 6. Fixes & Tweaks
                await player.SetPropertyAsync("demuxer-mkv-subtitle-preroll", "no");
                await player.SetPropertyAsync("sub-scale-with-window", "yes");
                
                // Network Stability & Performance
                await SetPropertySafeAsync(player, "network-timeout", "20"); 
                await SetPropertySafeAsync(player, "stream-buffer-size", "512KiB");
                
                // HTTP Reconnect Logic (Crucial for unstable IPTV servers)
                // The correct property name is 'demuxer-lavf-o'
                // Added reconnect_on_network_error and increased robustness
                Debug.WriteLine($"[MpvSetupHelper] Applied Headers: {headers.Replace("\n", " | ")}");
                await SetPropertySafeAsync(player, "demuxer-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_delay_max=30,reconnect_on_network_error=1");

                await SetPropertySafeAsync(player, "vd-lavc-fast", "yes"); // Speed up hardware decoding
                await SetPropertySafeAsync(player, "vd-lavc-dr", "yes");   // Direct rendering

                // 7. Audio (If secondary, maybe mute by default? Let caller handle that.)
                // But we can ensure correct audio output driver
                await player.SetPropertyAsync("ao", "wasapi"); 
                // Allow some audio/video desync instead of freezing
                await SetPropertySafeAsync(player, "video-sync", "audio"); 
                await SetPropertySafeAsync(player, "audio-pitch-correction", "yes");
                Debug.WriteLine($"[MpvSetupHelper] Configuration Complete. Secondary Mode: {isSecondary}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MpvSetupHelper] Error configuring player: {ex.Message}");
                throw;
            }
        }

        private static string ExtractCookiesForUrl(string url)
        {
            string cookieHeader = "";
            try
            {
                var targetUri = new Uri(url);
                // 1. Try strict URI matching first
                var cookies = HttpHelper.CookieContainer.GetCookies(targetUri);

                // 2. If valid cookies found, use them. If not, Dump ALL cookies (fallback).
                if (cookies.Count == 0)
                {
                    Debug.WriteLine("[MpvSetupHelper] GetCookies(uri) returned 0. Trying Reflection for ALL cookies...");
                    cookies = GetAllCookies(HttpHelper.CookieContainer);
                }

                foreach (System.Net.Cookie c in cookies)
                {
                    cookieHeader += $"{c.Name}={c.Value}; ";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MpvSetupHelper] Cookie Extraction Error: {ex.Message}");
            }
            return cookieHeader;
        }

        private static async Task SetPropertySafeAsync(MpvPlayer player, string name, string value)
        {
            try
            {
                await player.SetPropertyAsync(name, value);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MpvSetupHelper] Optional property '{name}' could not be set: {ex.Message}");
            }
        }

        // Helper to extract ALL cookies from a CookieContainer (ignoring Domain restrictions)
        private static System.Net.CookieCollection GetAllCookies(System.Net.CookieContainer container)
        {
            var allCookies = new System.Net.CookieCollection();
            var domainTableField = container.GetType().GetRuntimeFields().FirstOrDefault(x => x.Name == "m_domainTable" || x.Name == "_domainTable");
            var domains = domainTableField?.GetValue(container) as IDictionary;

            if (domains != null)
            {
                foreach (var val in domains.Values)
                {
                    var type = val.GetType();
                    var flagsField = type.GetRuntimeFields().FirstOrDefault(x => x.Name == "m_list" || x.Name == "_list");
                    var cookieList = flagsField?.GetValue(val) as IDictionary;

                    if (cookieList != null)
                    {
                        foreach (System.Net.CookieCollection col in cookieList.Values)
                        {
                            allCookies.Add(col);
                        }
                    }
                }
            }
            return allCookies;
        }
    }
}
