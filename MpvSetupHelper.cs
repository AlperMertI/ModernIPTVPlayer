using MpvWinUI;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections;
using System.Diagnostics;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer
{
    public static class MpvSetupHelper
    {
        // Subtitle switch latency is mainly affected by MKV subtitle preroll window.
        private const string SubtitlePrerollSecs = "3";

        /// <summary>
        /// Phase 1: Applied immediately to start the network handshake and probe.
        /// Objective: Fast-start with minimal GPU/CPU overhead.
        /// </summary>
        public static async Task ApplyEssentialSettingsAsync(MpvPlayer player, string streamUrl, bool isSecondary = false)
        {
            if (player == null) return;

            try {
            var baseDir = @"C:\Users\ASUS\Documents\ModernIPTVPlayer\";
            System.IO.File.AppendAllText(System.IO.Path.Combine(baseDir, "init_debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [INIT] ApplyEssentialSettingsAsync (Session Reset)\n");
            System.IO.File.AppendAllText(System.IO.Path.Combine(baseDir, "control_debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [CONTROL] Startup Reset\n");
            System.IO.File.AppendAllText(System.IO.Path.Combine(baseDir, "cs_debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [CS] Startup Reset\n");
        } catch { }

            var pSettings = AppSettings.PlayerSettings;

            // Select the modern (d3d11/gpu-next) or legacy (dxgi/vo_gpu) backend
            player.RenderApi = pSettings.VideoOutput == Models.VideoOutput.GpuNext ? "d3d11" : "dxgi";

            // Phase 1 Optimization: Skip UI scripts (stats/osc) for background players
            await player.InitializePlayerAsync(skipScripts: isSecondary);

            // 1. Globalization & Meta Probing Skip
            await player.SetPropertyAsync("ytdl", "no");

            // 2. Network & Headers
            if (!string.IsNullOrEmpty(streamUrl) && !streamUrl.Contains("127.0.0.1"))
            {
                string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                string headers = "Accept: */*\nConnection: keep-alive\nAccept-Language: en-US,en;q=0.9\n";

                await player.SetPropertyAsync("user-agent", userAgent);
                await player.SetPropertyAsync("http-header-fields", headers);

                if (streamUrl.Contains("youtube.com") || streamUrl.Contains("youtu.be"))
                    await player.SetPropertyAsync("ytdl-raw-options", $"user-agent=\"{userAgent}\",no-check-certificate=");
            }

            // 3. Essential Playback & Decoder (Phase 1)
            
            // Preferred Languages
            if (!string.IsNullOrEmpty(pSettings.PreferredAudioLanguage))
                await SetPropertySafeAsync(player, "alang", pSettings.PreferredAudioLanguage);
            if (!string.IsNullOrEmpty(pSettings.PreferredSubtitleLanguage))
                await SetPropertySafeAsync(player, "slang", pSettings.PreferredSubtitleLanguage);

            // Hardware Decoding Fallback Logic
            string hwdecValue = GetHwdecValue(pSettings.HardwareDecoding, out bool zeroCopy);
            await player.SetPropertyAsync("hwdec", hwdecValue);
            
            // For gpu-next, ensure the underlying API is pinned to d3d11
            if (pSettings.VideoOutput == Models.VideoOutput.GpuNext)
            {
                await player.SetPropertyAsync("gpu-api", "d3d11");
                await player.SetPropertyAsync("gpu-context", "d3d11");
            }

            await player.SetPropertyAsync("d3d11va-zero-copy", zeroCopy ? "yes" : "no");
            await player.SetPropertyAsync("vd-lavc-dr", zeroCopy ? "yes" : "no");
            
            // [LATENCY-FIX] Stable Canvas & Viewport Rendering Config
            if (pSettings.VideoOutput == Models.VideoOutput.GpuNext)
            {
                // 1. Persistent Shader Cache (Eliminates 250ms stalls on ratio change)
                string cacheDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                    "ModernIPTVPlayer", "mpv-shader-cache");
                
                if (!System.IO.Directory.Exists(cacheDir))
                    System.IO.Directory.CreateDirectory(cacheDir);

                await player.SetPropertyAsync("gpu-shader-cache", "yes");
                await player.SetPropertyAsync("gpu-shader-cache-dir", cacheDir);

                // 2. High-Performance 10-bit Output (Saves 50% VRAM Bandwidth vs rgba16f)
                await player.SetPropertyAsync("d3d11-output-format", "rgb10_a2");
                await player.SetPropertyAsync("fbo-format", "rgb10_a2");

                // 3. [FIT-TO-WINDOW] Force aspect ratio and zoom to ensure video
                // scales to the FBO viewport instead of rendering at native size.
                await player.SetPropertyAsync("keepaspect", "yes");
                await player.SetPropertyAsync("video-zoom", "0");
                await player.SetPropertyAsync("panscan", "0");
                await player.SetPropertyAsync("autofit", "");
                await player.SetPropertyAsync("autofit-larger", "");
                await player.SetPropertyAsync("autofit-smaller", "");
            }
            
            
            // Monitor for decoder failure and fallback to safe d3d11va
            _ = MonitorHwdecFallbackAsync(player, hwdecValue);

            // 4. Buffering & Cache (Optimized for 4K)
            await player.SetPropertyAsync("cache", "yes");
            await player.SetPropertyAsync("cache-pause", "yes");
            await player.SetPropertyAsync("cache-pause-wait", "1");
            await player.SetPropertyAsync("cache-pause-initial", "yes");

            // Memory caps optimized for high-bitrate 4K streaming.
            // Phase 1 (Pre-buffer) uses 128MiB for stability.
            await player.SetPropertyAsync("demuxer-max-bytes", isSecondary ? "128MiB" : "512MiB");
            await player.SetPropertyAsync("demuxer-max-back-bytes", "16MiB");
            await player.SetPropertyAsync("demuxer-readahead-secs", isSecondary ? "20" : AppSettings.BufferSeconds.ToString());

            // Stability Overlays
            await SetPropertySafeAsync(player, "demuxer-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_delay_max=30,reconnect_on_network_error=1,reconnect_on_http_error=4xx,5xx,reconnect_at_eof=1");
            await player.SetPropertyAsync("ao", "wasapi");

            // 5. Visual Optimization & Pre-buffer Strategy (Phase 1)
            // [CRITICAL] 
            // We MUST use vid=1 even in background (MediaInfoPage) to allow the demuxer
            // to pull video packets into the cache. vid=no causes video to be ignored completely.
            await player.SetPropertyAsync("vid", "1");
            
            if (isSecondary)
            {
                await player.SetPropertyAsync("mute", "yes");
                await player.SetPropertyAsync("pause", "yes"); 
            }
            else
            {
                await player.SetPropertyAsync("mute", "no");
                // pause status is handled by Page orchestration
            }

            AppLogger.Info($"[MpvSetup] Essential Configuration (Phase 1) Complete. Secondary: {isSecondary}");
        }

        /// <summary>
        /// Phase 2: Applied once the user is actually watching (Handoff or direct start).
        /// Objective: Maximize visual fidelity and apply polishing effects.
        /// </summary>
        public static async Task ApplyVisualSettingsAsync(MpvPlayer player)
        {
            if (player == null) return;
            var pSettings = AppSettings.PlayerSettings;

            // 1. Scalers & Shaders
            string scalerValue = pSettings.Scaler switch
            {
                Models.Scaler.Bilinear => "bilinear",
                Models.Scaler.EwaLanczos => "ewa_lanczossharp",
                _ => "spline36"
            };
            
            await player.SetPropertyAsync("scale", scalerValue);
            if (scalerValue != "bilinear")
            {
                await SetPropertySafeAsync(player, "cscale", scalerValue);
                await SetPropertySafeAsync(player, "dscale", "mitchell");
            }

            // 2. Deband (Restored user-controlled property)
            await player.SetPropertyAsync("deband", pSettings.Deband == Models.DebandMode.Yes ? "yes" : "no");
            
            // 3. Tone Mapping (Phase 2 initial application)
            string tmValue = pSettings.ToneMapping switch
            {
                Models.ToneMapping.Clip => "clip",
                Models.ToneMapping.Spline => "spline", 
                Models.ToneMapping.Bt2446a => "bt.2446a",
                Models.ToneMapping.St2094_40 => "st2094-40",
                _ => "auto"
            };
            await SetPropertySafeAsync(player, "tone-mapping", tmValue);

            // 4. Audio Polish
            await SetPropertySafeAsync(player, "audio-exclusive", pSettings.ExclusiveAudio == Models.ExclusiveMode.Yes ? "yes" : "no");
            await SetPropertySafeAsync(player, "video-sync", "audio");

            // 5. Custom Config Overlay (Final Priority)
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
                            await SetPropertySafeAsync(player, key, val);
                    }
                }
            }

            AppLogger.Info($"[MpvSetup] Visual Enhancements (Phase 2) Complete.");
        }

        /// <summary>
        /// Legacy support / Unified start (Old behavior)
        /// </summary>
        public static async Task ConfigurePlayerAsync(MpvPlayer player, string streamUrl, bool isSecondary = false)
        {
            await ApplyEssentialSettingsAsync(player, streamUrl, isSecondary);
            if (!isSecondary)
            {
                await ApplyVisualSettingsAsync(player);
            }
        }

        private static async Task MonitorHwdecFallbackAsync(MpvPlayer player, string targetHwdec)
        {
            if (targetHwdec == "no" || player == null) return;

            try
            {
                // Wait for Demux/Probe to finish using core-idle
                // If core-idle is 'no', it means mpv has started the playback pipeline.
                int maxChecks = 30; // 3 seconds max
                for (int i = 0; i < maxChecks; i++)
                {
                    await Task.Delay(100);
                    var idle = await player.GetPropertyAsync("core-idle");
                    if (idle == "no") break; 
                }

                // Final check: Is hardware decoding actually active?
                var currentHwdec = await player.GetPropertyAsync("hwdec-current");
                if (currentHwdec == "no")
                {
                    AppLogger.Warn($"[MpvSetup] Preferred hwdec '{targetHwdec}' failed to initialize. Falling back to d3d11va (Safe).");
                    await player.SetPropertyAsync("hwdec", "d3d11va");
                }
            }
            catch { /* Silent fail for background task */ }
        }

        private static string GetHwdecValue(Models.HardwareDecoding setting, out bool zeroCopy)
        {
            switch (setting)
            {
                case Models.HardwareDecoding.AutoSafe:
                    zeroCopy = true; return "d3d11va";
                case Models.HardwareDecoding.AutoCopy:
                    zeroCopy = false; return "d3d11va-copy";
                case Models.HardwareDecoding.No:
                    zeroCopy = false; return "no";
                default:
                    zeroCopy = true; return "d3d11va";
            }
        }

        private static string ExtractCookiesForUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;

            try
            {
                var targetUri = new Uri(url);
                var cookies = HttpHelper.CookieContainer.GetCookies(targetUri);
                
                if (cookies.Count == 0) return string.Empty;

                var cookieStrings = new System.Collections.Generic.List<string>();
                foreach (System.Net.Cookie cookie in cookies)
                {
                    cookieStrings.Add($"{cookie.Name}={cookie.Value}");
                }

                return string.Join("; ", cookieStrings);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Cookie Extraction Error", ex);
                return string.Empty;
            }
        }

        private static async Task SetPropertySafeAsync(MpvPlayer player, string name, string value)
        {
            try
            {
                await player.SetPropertyAsync(name, value);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Optional property '{name}' could not be set: {ex.Message}");
            }
        }
    }
}
