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
        // Subtitle switch latency is mainly affected by MKV subtitle preroll window.
        private const string SubtitlePrerollSecs = "3";

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
                if (player == null) return;
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


                // 3. Apply Player Settings from AppSettings
                var pSettings = AppSettings.PlayerSettings;

                // Force D3D11 usage for Windows
                // We rely on the internal D3D11RenderControl to handle the SwapChain and Context.
                // Do NOT manually set gpu-context or gpu-api as it conflicts with the wrapper's initialization.
                
                // Force Performance Settings for Secondary Player (PiP/Preview) to save resources
                if (isSecondary)
                {
                    pSettings = Models.PlayerSettings.GetDefault(Models.PlayerProfile.Performance);
                }

                // Hardware Decoding
                string hwdecValue = pSettings.HardwareDecoding switch
                {
                    Models.HardwareDecoding.AutoSafe => "auto-safe",
                    Models.HardwareDecoding.AutoCopy => "auto-copy",
                    Models.HardwareDecoding.No => "no",
                    _ => "auto-copy"
                };
                


                await player.SetPropertyAsync("hwdec", hwdecValue);
                await player.SetPropertyAsync("hwdec-codecs", "all");

                // 4. Performance Profile & Video Settings
                await player.SetPropertyAsync("profile", "fast"); // Always start with fast base for low latency
                
                // Video Output
                // CRITICAL: Do NOT set 'vo' manually for this custom C# backend.
                // The 'MpvPlayer' wrapper initializes the context with 'libmpv' (or equivalent).
                // Changing 'vo' to 'gpu' after initialization causes the window to detach.

                // Scaler
                // 'ewa_lanczos' is very heavy on 'gpu' backend, prefer 'spline36' for high quality
                string scalerValue = pSettings.Scaler switch
                {
                    Models.Scaler.Bilinear => "bilinear",
                    Models.Scaler.EwaLanczos => "ewa_lanczossharp", // Supported by gpu-next!
                    _ => "spline36"
                };
                await player.SetPropertyAsync("scale", scalerValue);
                if (scalerValue != "bilinear")
                {
                     await SetPropertySafeAsync(player, "cscale", scalerValue);
                     await SetPropertySafeAsync(player, "dscale", "mitchell");
                }

                // Deband
                await player.SetPropertyAsync("deband", pSettings.Deband == Models.DebandMode.Yes ? "yes" : "no");

                // Tone Mapping
                string tmValue = pSettings.ToneMapping switch
                {
                    Models.ToneMapping.Clip => "clip",
                    Models.ToneMapping.Spline => "spline", // Native spline supported!
                    Models.ToneMapping.Bt2446a => "bt.2446a", // Native bt.2446a supported!
                    Models.ToneMapping.St2094_40 => "st2094-40", // Native st2094-40 supported!
                    _ => "auto"
                };
                await SetPropertySafeAsync(player, "tone-mapping", tmValue);

                // Target Peak
                if (pSettings.TargetPeak != Models.TargetPeak.Auto)
                {
                    string tpValue = pSettings.TargetPeak switch
                    {
                        Models.TargetPeak.Sdr100 => "100",
                        Models.TargetPeak.Sdr203 => "203",
                        Models.TargetPeak.Hdr400 => "400",
                        Models.TargetPeak.Hdr1000 => "1000",
                        _ => "auto"
                    };
                    await SetPropertySafeAsync(player, "target-peak", tpValue);
                }
                else
                {
                    await SetPropertySafeAsync(player, "target-peak", "auto");
                }

                // Target Display Mode (HDR/SDR Control)
                switch (pSettings.TargetDisplayMode)
                {
                    case Models.TargetDisplayMode.SdrForce:
                        await SetPropertySafeAsync(player, "target-colorspace-hint", "no");
                        await SetPropertySafeAsync(player, "target-trc", "srgb");
                        break;
                    case Models.TargetDisplayMode.HdrPassthrough:
                        await SetPropertySafeAsync(player, "target-colorspace-hint", "yes");
                        await SetPropertySafeAsync(player, "target-trc", "pq");
                        await SetPropertySafeAsync(player, "target-prim", "bt.2020");
                        break;
                    default: // Auto
                        await SetPropertySafeAsync(player, "target-colorspace-hint", "auto");
                        break;
                }

                // 5. Buffering & Cache
                await player.SetPropertyAsync("cache", "yes");
                // IMPORTANT: For network streams, pause if cache runs out. 'no' causes skipping/issues.
                await player.SetPropertyAsync("cache-pause", "yes");
                await player.SetPropertyAsync("cache-pause-wait", "1"); // Wait 1s buffer before resume
                await player.SetPropertyAsync("cache-pause-initial", "yes"); // Wait for initial buffer

                if (isSecondary)
                {
                    await player.SetPropertyAsync("demuxer-max-bytes", "64MiB");
                    await player.SetPropertyAsync("demuxer-max-back-bytes", "16MiB");
                    await player.SetPropertyAsync("demuxer-readahead-secs", "20");
                }
                else
                {
                    int userBuffer = AppSettings.BufferSeconds;
                    await player.SetPropertyAsync("demuxer-max-bytes", "2000MiB");
                    await player.SetPropertyAsync("demuxer-max-back-bytes", "100MiB");
                    await player.SetPropertyAsync("demuxer-readahead-secs", userBuffer.ToString());
                }

                // 6. Fixes & Tweaks
                // CRITICAL: Enable seekable cache - allows seeking within cached data without re-downloading.
                // Without this, track switches (subtitle/audio) flush the entire demuxer cache.
                await SetPropertySafeAsync(player, "demuxer-seekable-cache", "yes");
                
                // Use MKV index for subtitle preroll: finds subtitle packets via index WITHOUT
                // seeking the demuxer, which would otherwise flush the video/audio cache.
                // "index" mode is superior to "yes" because it doesn't trigger a demuxer seek.
                await player.SetPropertyAsync("demuxer-mkv-subtitle-preroll", "index");
                // How far back (in seconds) to search for subtitle packets on track switch
                // Keep this tight to avoid 10-20s subtitle switch latency on large 4K MKV streams.
                await SetPropertySafeAsync(player, "demuxer-mkv-subtitle-preroll-secs", SubtitlePrerollSecs);
                await SetPropertySafeAsync(player, "demuxer-mkv-subtitle-preroll-secs-index", "3");
                // Set general cache time window to match readahead, ensuring subtitle data stays in cache
                await SetPropertySafeAsync(player, "cache-secs", isSecondary ? "20" : AppSettings.BufferSeconds.ToString());
                await player.SetPropertyAsync("sub-scale-with-window", "yes");

                // Network Stability & Performance
                await SetPropertySafeAsync(player, "network-timeout", "20");
                await SetPropertySafeAsync(player, "stream-buffer-size", "4MiB"); // Increased buffering for 4K streams

                // HTTP Reconnect Logic (Crucial for unstable IPTV servers)
                // The correct property name is 'demuxer-lavf-o'
                // Added reconnect_on_network_error and increased robustness
                Debug.WriteLine($"[MpvSetupHelper] Applied Headers: {headers.Replace("\n", " | ")}");
                // reconnect_on_http_error=4xx,5xx: Handle temporary server errors
                // reconnect_at_eof=1: vital for some live streams that close cleanly but aren't done
                await SetPropertySafeAsync(player, "demuxer-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_delay_max=30,reconnect_on_network_error=1,reconnect_on_http_error=4xx,5xx,reconnect_at_eof=1");

                await SetPropertySafeAsync(player, "vd-lavc-fast", "yes"); // Speed up hardware decoding
                await SetPropertySafeAsync(player, "vd-lavc-dr", "yes");   // Direct rendering

                // 7. Audio Settings
                string acValue = pSettings.AudioChannels switch
                {
                    Models.AudioChannels.Stereo => "stereo",
                    Models.AudioChannels.Surround51 => "5.1",
                    Models.AudioChannels.Surround71 => "7.1",
                    _ => "auto-safe"
                };
                await SetPropertySafeAsync(player, "audio-channels", acValue);
                
                // Ensure WASAPI is used
                await player.SetPropertyAsync("ao", "wasapi");
                await SetPropertySafeAsync(player, "audio-exclusive", pSettings.ExclusiveAudio == Models.ExclusiveMode.Yes ? "yes" : "no");
                
                await SetPropertySafeAsync(player, "video-sync", "audio");
                await SetPropertySafeAsync(player, "audio-pitch-correction", "yes");

                // 8. Custom Config Overlay
                if (!string.IsNullOrWhiteSpace(pSettings.CustomConfig))
                {
                    var lines = pSettings.CustomConfig.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            var key = parts[0].Trim();
                            var val = parts[1].Trim();
                            if (!string.IsNullOrEmpty(key))
                            {
                                await SetPropertySafeAsync(player, key, val);
                                Debug.WriteLine($"[MpvSetupHelper] Applied custom config: {key}={val}");
                            }
                        }
                    }
                }

                Debug.WriteLine($"[MpvSetupHelper] Configuration Complete. Profile: {pSettings.Profile}, Secondary: {isSecondary}");
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
