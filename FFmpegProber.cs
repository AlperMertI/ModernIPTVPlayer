using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ModernIPTVPlayer
{
    public class FFmpegProber
    {
        // Path to the ffprobe executable found on the system
        private const string FfprobePath = @"C:\Users\mertg\Documents\Stremio\stremio-community-v5\dist\win-x64\ffprobe.exe";

        public async Task<(string Res, string Fps, string Codec, long Bitrate, bool Success)> ProbeAsync(string url)
        {
            if (!File.Exists(FfprobePath))
            {
                return ("No ffprobe", "-", "-", 0, false);
            }

            try
            {
                var sw = Stopwatch.StartNew();
                
                // ffprobe command - Extracting Stream AND Format info for Bitrate
                string args = $"-v error -probesize 256000 -analyzeduration 200000 -select_streams v:0 -show_entries stream=width,height,avg_frame_rate,r_frame_rate,codec_name,bit_rate -show_entries format=bit_rate -of json \"{url}\"";

                var startInfo = new ProcessStartInfo
                {
                    FileName = FfprobePath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = startInfo };
                
                long initTime = sw.ElapsedMilliseconds;
                process.Start();
                long startProcessTime = sw.ElapsedMilliseconds;

                var readTask = process.StandardOutput.ReadToEndAsync();
                
                // 5 second timeout for probing
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(readTask, timeoutTask);

                long probeEndTime = sw.ElapsedMilliseconds;

                if (completedTask == timeoutTask)
                {
                    try { process.Kill(); } catch { }
                    Debug.WriteLine($"[FFmpegProber] TIMEOUT for {url} after {probeEndTime}ms");
                    return ("Timeout", "-", "-", 0, false);
                }

                string output = await readTask;
                long totalTime = sw.ElapsedMilliseconds;

                Debug.WriteLine($"[FFmpegProber] Performance for {url}:");
                Debug.WriteLine($"  - Setup: {initTime}ms");
                Debug.WriteLine($"  - Process Start: {startProcessTime - initTime}ms");
                Debug.WriteLine($"  - Data Read (Network/Probe): {probeEndTime - startProcessTime}ms");
                Debug.WriteLine($"  - Total: {totalTime}ms");
                // The Bitrate will be logged after parsing.

                if (string.IsNullOrWhiteSpace(output))
                {
                    return ("No Data", "-", "-", 0, false);
                }

                // Parse JSON
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = JsonSerializer.Deserialize<FfprobeResult>(output, options);

                if (result?.Streams != null && result.Streams.Length > 0)
                {
                    var stream = result.Streams[0];
                    string res = (stream.Width > 0) ? $"{stream.Width}x{stream.Height}" : "Unknown";
                    
                    // Parse Bitrate (Try stream first, then format)
                    long br = 0;
                    if (!string.IsNullOrEmpty(stream.BitRate) && long.TryParse(stream.BitRate, out long sbr)) br = sbr;
                    else if (result.Format != null && !string.IsNullOrEmpty(result.Format.BitRate) && long.TryParse(result.Format.BitRate, out long fbr)) br = fbr;

                    // Parse FPS... (Keep existing logic)
                    string fps = "- fps";
                    string rawFps = !string.IsNullOrEmpty(stream.AvgFrameRate) && stream.AvgFrameRate != "0/0" 
                        ? stream.AvgFrameRate 
                        : stream.RFrameRate;

                    if (!string.IsNullOrEmpty(rawFps) && rawFps != "0/0")
                    {
                        if (rawFps.Contains("/"))
                        {
                            var parts = rawFps.Split('/');
                            if (parts.Length == 2 && 
                                double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double num) &&
                                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double den) &&
                                den != 0)
                            {
                                fps = $"{Math.Round(num / den)} fps";
                            }
                        }
                        else if (double.TryParse(rawFps, NumberStyles.Any, CultureInfo.InvariantCulture, out double fv))
                        {
                            fps = $"{Math.Round(fv)} fps";
                        }
                    }

                    // Normalize Codec
                    string codec = stream.CodecName?.ToUpper() ?? "-";
                    if (codec.Contains("H264")) codec = "H.264";
                    else if (codec.Contains("HEVC") || codec.Contains("H265")) codec = "HEVC";

                    Debug.WriteLine($"  - Bitrate: {br} bps");
                    return (res, fps, codec, br, true);
                }

                return ("Unknown", "-", "-", 0, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FFmpegProber Error: {ex.Message}");
                return ("Error", "-", "-", 0, false);
            }
        }

        private class FfprobeResult
        {
            [JsonPropertyName("streams")]
            public FfprobeStream[] Streams { get; set; }

            [JsonPropertyName("format")]
            public FfprobeFormat Format { get; set; }
        }

        private class FfprobeFormat
        {
            [JsonPropertyName("bit_rate")]
            public string BitRate { get; set; }
        }

        private class FfprobeStream
        {
            [JsonPropertyName("width")]
            public int Width { get; set; }

            [JsonPropertyName("height")]
            public int Height { get; set; }

            [JsonPropertyName("codec_name")]
            public string CodecName { get; set; }

            [JsonPropertyName("avg_frame_rate")]
            public string AvgFrameRate { get; set; }

            [JsonPropertyName("r_frame_rate")]
            public string RFrameRate { get; set; }

            [JsonPropertyName("bit_rate")]
            public string BitRate { get; set; }
        }
    }
}
