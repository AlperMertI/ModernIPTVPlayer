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
            await _player.SetPropertyAsync("ytdl", "no");
            await _player.SetPropertyAsync("demuxer-mkv-subtitle-preroll", "no");
            


            _isInitialized = true;
        }

        public async Task<(string Res, string Fps, string Codec)> ProbeAsync(string url)
        {
            if (!_isInitialized) await InitializeAsync();

            try
            {
                // Force stop previous
                await _player.ExecuteCommandAsync("stop");

                // Open
                await _player.OpenAsync(url);

                // Wait for loading (Polling state)
                int retries = 0;
                while (retries < 20) // Max 10 seconds (20 * 500ms)
                {
                    await Task.Delay(500);
                    
                    var duration = await _player.GetPropertyAsync("duration");
                    var width = await _player.GetPropertyAsync("video-params/w");
                    
                    // If we have basic video params, we are good
                    if (!string.IsNullOrEmpty(width) && width != "N/A") 
                    {
                        break;
                    }
                    retries++;
                }

                // Extract Data
                var w = await _player.GetPropertyAsync("video-params/w");
                var h = await _player.GetPropertyAsync("video-params/h");
                var fps = await _player.GetPropertyAsync("estimated-fps");
                if (string.IsNullOrEmpty(fps) || fps == "N/A") fps = await _player.GetPropertyAsync("container-fps");
                var codec = await _player.GetPropertyAsync("video-codec");

                // Format
                string resStr = (!string.IsNullOrEmpty(w) && w != "N/A") ? $"{w}x{h}" : "Unknown";
                
                string fpsStr = "- fps";
                if (double.TryParse(fps, NumberStyles.Any, CultureInfo.InvariantCulture, out double fv))
                {
                    fpsStr = $"{fv:F0} fps";
                }

                string codecStr = codec ?? "-";
                // Simplify Codec Name (e.g. "H.264 / ...")
                if (!string.IsNullOrEmpty(codecStr))
                {
                    if (codecStr.Contains("/")) codecStr = codecStr.Split('/')[0].Trim();
                    
                    // Normalize common names
                    var lower = codecStr.ToLowerInvariant();
                    if (lower.Contains("h.264") || lower.Contains("avc")) codecStr = "H.264";
                    else if (lower.Contains("hevc") || lower.Contains("h.265")) codecStr = "HEVC";
                    else if (lower.Contains("mpeg2")) codecStr = "MPEG2";
                }
                // Start cleanup
                await _player.ExecuteCommandAsync("stop");

                return (resStr, fpsStr, codecStr);
            }
            catch (Exception)
            {
                await _player.ExecuteCommandAsync("stop");
                return ("Error", "-", "-");
            }
        }
    }
}
