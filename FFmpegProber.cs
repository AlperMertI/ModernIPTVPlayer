using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Threading;

namespace ModernIPTVPlayer
{
    public class FFmpegProber
    {
        // Path to the ffprobe executable found on the system
        private const string FfprobePath = @"C:\Users\mertg\Documents\Stremio\stremio-community-v5\dist\win-x64\ffprobe.exe";

        public async Task<(string Res, string Fps, string Codec, long Bitrate, bool Success, bool IsHdr)> ProbeAsync(string url, CancellationToken ct = default)
        {
            if (!File.Exists(FfprobePath))
            {
                return ("No ffprobe", "-", "-", 0, false, false);
            }

            try
            {
                var sw = Stopwatch.StartNew();
                
                // ffprobe command
                string args = $"-v error -probesize 256000 -analyzeduration 200000 -select_streams v:0 -show_entries stream=width,height,avg_frame_rate,r_frame_rate,codec_name,bit_rate,color_primaries,color_transfer -show_entries format=bit_rate -of json \"{url}\"";

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


                    // Wait for exit with cancellation support
                    var readTask = process.StandardOutput.ReadToEndAsync();
                    var waitForExit = process.WaitForExitAsync(ct);

                    // Combine tasks: Read output + Wait for Exit + Timeout + External Cancellation
                    var timeoutTask = Task.Delay(5000, ct); 
                    
                    var completedTask = await Task.WhenAny(readTask, timeoutTask);

                    // Handle Cancellation or Timeout by forcefully killing
                    if (completedTask == timeoutTask || ct.IsCancellationRequested)
                    {
                        try 
                        { 
                            process.Kill(); 
                            Debug.WriteLine($"[FFmpegProber] KILLED process for {url} (Reason: {(ct.IsCancellationRequested ? "Cancel" : "Timeout")})");
                        } 
                        catch (Exception kEx)
                        {
                            Debug.WriteLine($"[FFmpegProber] Failed to KILL process: {kEx.Message}");
                        }
                        
                        return ("Aborted", "-", "-", 0L, false, false);
                    }

                    string output = await readTask;
                    long probeEndTime = sw.ElapsedMilliseconds;
                long totalTime = sw.ElapsedMilliseconds;

                Debug.WriteLine($"[FFmpegProber] Performance for {url}:");
                Debug.WriteLine($"  - Setup: {initTime}ms");
                Debug.WriteLine($"  - Process Start: {startProcessTime - initTime}ms");
                Debug.WriteLine($"  - Data Read (Network/Probe): {probeEndTime - startProcessTime}ms");
                Debug.WriteLine($"  - Total: {totalTime}ms");

                if (string.IsNullOrWhiteSpace(output))
                {
                    return ("No Data", "-", "-", 0L, false, false);
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

                    // Parse FPS
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

                    // HDR Detection
                    bool isHdr = false;
                    if (!string.IsNullOrEmpty(stream.ColorPrimaries) && stream.ColorPrimaries.Contains("bt2020")) isHdr = true;
                    if (!string.IsNullOrEmpty(stream.ColorTransfer) && (stream.ColorTransfer == "smpte2084" || stream.ColorTransfer == "arib-std-b67")) isHdr = true;

                    return (res, fps, codec, br, true, isHdr);
                }

                return ("Unknown", "-", "-", 0L, false, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FFmpegProber Error: {ex.Message}");
                return ("Error", "-", "-", 0L, false, false);
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

            [JsonPropertyName("color_primaries")]
            public string ColorPrimaries { get; set; }

            [JsonPropertyName("color_transfer")]
            public string ColorTransfer { get; set; }
        }
    }
}
