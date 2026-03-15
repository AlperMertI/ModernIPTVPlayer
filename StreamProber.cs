using MpvWinUI;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace ModernIPTVPlayer
{
    public class StreamProber
    {
        private MpvPlayer _player;
        private bool _isInitialized = false;

        public StreamProber(MpvPlayer player)
        {
            _player = player;
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await _player.InitializePlayerAsync();
            await _player.SetPropertyAsync("vo", "null"); // No video output
            await _player.SetPropertyAsync("ao", "null"); // No audio output
            // await _player.SetPropertyAsync("ytdl", "no"); // Removed: ytdl not available in this MPV build
            await _player.SetPropertyAsync("demuxer-mkv-subtitle-preroll", "yes");



            _isInitialized = true;
        }

        public async Task<(string Res, string Fps, string Codec, long Bitrate, bool Success, bool IsHdr)> ProbeAsync(string url, System.Threading.CancellationToken ct = default, bool force = false)
        {
            if (!_isInitialized) await InitializeAsync();
            Services.CacheLogger.Info(Services.CacheLogger.Category.Probe, "START probing", url);

            try
            {
                // Force stop previous
                await _player.ExecuteCommandAsync("stop");

                // Open
                await _player.OpenAsync(url);

                var result = await ExtractProbeDataAsync(_player, ct);
                
                if (result.Success)
                    Services.CacheLogger.Success(Services.CacheLogger.Category.Probe, "Probing Success", $"{url} | {result.Res}");
                else
                    Services.CacheLogger.Warning(Services.CacheLogger.Category.Probe, "Probing Failed/Timeout", url);

                // Start cleanup
                await _player.ExecuteCommandAsync("stop");

                return result;
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.Probe, "Probing Exception", $"{url} | {ex.Message}");
                await _player.ExecuteCommandAsync("stop");
                return ("Error", "-", "-", 0, false, false);
            }
        }

        public static async Task<(string Res, string Fps, string Codec, long Bitrate, bool Success, bool IsHdr)> ExtractProbeDataAsync(MpvPlayer player, System.Threading.CancellationToken ct = default)
        {
            try
            {
                // Wait for loading (Polling state)
                int retries = 0;
                bool loaded = false;
                while (retries < 20) // Max 10 seconds (20 * 500ms)
                {
                    await Task.Delay(500);
                    if (ct.IsCancellationRequested) 
                    {
                        return ("Cancelled", "-", "-", 0, false, false);
                    }

                    var width = await player.GetPropertyAsync("video-params/w");
                    
                    // If we have basic video params, we are good
                    if (!string.IsNullOrEmpty(width) && width != "N/A" && width != "0")
                    {
                        loaded = true;
                        break;
                    }
                    retries++;
                }

                if (!loaded)
                {
                    return ("Timeout", "-", "-", 0, false, false);
                }

                // Extract Data
                var w = await player.GetPropertyAsync("video-params/w");
                var h = await player.GetPropertyAsync("video-params/h");
                var fps = await player.GetPropertyAsync("estimated-fps");
                if (string.IsNullOrEmpty(fps) || fps == "N/A") fps = await player.GetPropertyAsync("container-fps");
                var codec = await player.GetPropertyAsync("video-codec");
                var brStr = await player.GetPropertyAsync("video-bitrate");
                if (string.IsNullOrEmpty(brStr) || brStr == "N/A") brStr = await player.GetPropertyAsync("bitrate");
                
                // HDR Detection via mpv properties
                var pCol = await player.GetPropertyAsync("video-params/primaries");
                var pTr = await player.GetPropertyAsync("video-params/trc");
                bool isHdr = (pCol == "bt.2020") || (pTr == "pq" || pTr == "hlg");

                // Format
                string resStr = (!string.IsNullOrEmpty(w) && w != "N/A") ? $"{w}x{h}" : "Unknown";

                string fpsStr = "- fps";
                if (double.TryParse(fps, NumberStyles.Any, CultureInfo.InvariantCulture, out double fv))
                {
                    fpsStr = $"{fv:F0} fps";
                }

                long bitrate = 0;
                if (long.TryParse(brStr, out long brVal)) bitrate = brVal;

                string codecStr = codec ?? "-";
                if (!string.IsNullOrEmpty(codecStr))
                {
                    if (codecStr.Contains("/")) codecStr = codecStr.Split('/')[0].Trim();
                    var lower = codecStr.ToLowerInvariant();
                    if (lower.Contains("h.264") || lower.Contains("avc")) codecStr = "H.264";
                    else if (lower.Contains("hevc") || lower.Contains("h.265")) codecStr = "HEVC";
                    else if (lower.Contains("mpeg2")) codecStr = "MPEG2";
                }

                return (resStr, fpsStr, codecStr, bitrate, true, isHdr);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StreamProber] Shared extraction error: {ex.Message}");
                return ("Error", "-", "-", 0, false, false);
            }
        }
    }
}
