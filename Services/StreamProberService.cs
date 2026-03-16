using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Mpv.Core;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Net;
using System.Timers;

namespace ModernIPTVPlayer.Services
{
    public class ProbeResult
    {
        public string Resolution { get; set; }
        public string Fps { get; set; }
        public string Codec { get; set; }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    public class StreamProberService : IDisposable
    {
        private static StreamProberService _instance;
        public static StreamProberService Instance => _instance ??= new StreamProberService();

        private readonly SemaphoreSlim _poolLock = new SemaphoreSlim(10, 10); // Scalable lock
        private readonly System.Collections.Concurrent.ConcurrentStack<(Player Player, DateTime LastUsed)> _playerPool = new System.Collections.Concurrent.ConcurrentStack<(Player, DateTime)>();
        private readonly System.Timers.Timer _idleCleanupTimer;
        private bool _isDisposed = false;

        private StreamProberService() 
        {
            _idleCleanupTimer = new System.Timers.Timer(60000); // 1 minute
            _idleCleanupTimer.Elapsed += OnIdleCleanupElapsed;
            _idleCleanupTimer.AutoReset = true;
            _idleCleanupTimer.Start();
        }

        private async void OnIdleCleanupElapsed(object sender, ElapsedEventArgs e)
        {
            var now = DateTime.Now;
            var toDispose = new List<Player>();

            // Temporarily drain pool to check ages
            var temp = new List<(Player Player, DateTime LastUsed)>();
            while (_playerPool.TryPop(out var entry))
            {
                if ((now - entry.LastUsed).TotalMinutes > 5)
                {
                    toDispose.Add(entry.Player);
                }
                else
                {
                    temp.Add(entry);
                }
            }

            // Put back fresh ones
            foreach (var entry in temp) _playerPool.Push(entry);

            // Dispose old ones
            foreach (var p in toDispose)
            {
                Debug.WriteLine("[StreamProber] Disposing idle player instance due to inactivity.");
                try { await p.DisposeAsync(); } catch { }
            }
        }

        public async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct = default)
        {
            // 1. Check Cache First
            var cached = ProbeCacheService.Instance.Get(url);
            if (cached != null)
            {
                return new ProbeResult
                {
                    Resolution = cached.Resolution,
                    Fps = cached.Fps,
                    Codec = cached.Codec,
                    Bitrate = cached.Bitrate,
                    IsHdr = cached.IsHdr,
                    Success = true
                };
            }

            // 2. Acquire Slot (Wait for a slot if more than 10 concurrent requests)
            await _poolLock.WaitAsync(ct);
            Player player = null;
            try
            {
                Debug.WriteLine($"[StreamProber] Starting native probe for: {url}");
                
                // 3. Get or Create Player
                if (!_playerPool.TryPop(out var entry))
                {
                    player = new Player();
                    // Configure CORE options BEFORE initialization
                    try { player.Client.SetOption("vo", "null"); } catch { }
                    try { player.Client.SetOption("ao", "null"); } catch { }
                    try { player.Client.SetOption("ytdl", "no"); } catch { }
                    try { player.Client.SetOption("load-stats-overlay", "no"); } catch { }
                    try { player.Client.SetOption("load-osd-console", "no"); } catch { }
                    
                    await player.InitializeAsync();
                    
                    // Safer settings after init
                    try { player.Client.SetProperty("aid", "no"); } catch { }
                    try { player.Client.SetProperty("sid", "no"); } catch { }
                    try { player.Client.SetProperty("input-default-bindings", "no"); } catch { }
                    try { player.Client.SetProperty("input-vo-keyboard", "no"); } catch { }
                }
                else
                {
                    player = entry.Player;
                }

                // Per-Probe Configuration
                string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                string headers = "Accept: */*\nConnection: keep-alive\nAccept-Language: en-US,en;q=0.9\n";
                string cookies = ExtractCookiesForProber(url);
                if (!string.IsNullOrEmpty(cookies)) headers += $"Cookie: {cookies}\n";

                try { player.Client.SetProperty("user-agent", ua); } catch { }
                try { player.Client.SetProperty("http-header-fields", headers); } catch { }
                
                // OPTIMIZATION: Faster connectivity detection
                try { player.Client.SetProperty("demuxer-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_delay_max=2"); } catch { }
                try { player.Client.SetProperty("demuxer-readahead-secs", "1"); } catch { }
                try { player.Client.SetProperty("network-timeout", "5"); } catch { }
                
                // 4. Load file
                await player.Client.ExecuteAsync($"loadfile \"{url.Replace("\"", "\\\"")}\" replace");
                
                // 5. Poll for Metadata (Max 10 seconds for difficult streams)
                var stopwatch = Stopwatch.StartNew();
                bool metadataFound = false;
                ProbeResult result = new ProbeResult { Success = false };

                while (stopwatch.ElapsedMilliseconds < 10000 && !ct.IsCancellationRequested)
                {
                    try 
                    {
                        // Use string-based access + manual parsing (Avoids many native bridge exceptions)
                        string wStr = player.Client.GetPropertyToString("video-params/w");
                        string hStr = player.Client.GetPropertyToString("video-params/h");
                        
                        if (!string.IsNullOrEmpty(wStr) && !string.IsNullOrEmpty(hStr) && 
                            long.TryParse(wStr, out long width) && long.TryParse(hStr, out long height) && 
                            width > 0 && height > 0)
                        {
                            metadataFound = true;
                            result.Resolution = $"{width}x{height}";
                            
                            // Better FPS Detection (Check multiple sources)
                            string fpsStr = player.Client.GetPropertyToString("container-fps") ?? 
                                           player.Client.GetPropertyToString("video-params/fps") ??
                                           player.Client.GetPropertyToString("estimated-fps");
                            
                            if (double.TryParse(fpsStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double fpsVal) && fpsVal > 0)
                                result.Fps = $"{fpsVal:0.##} FPS";
                            else
                                result.Fps = "0 FPS";

                            // Simplified Codec Mapping
                            string rawCodec = player.Client.GetPropertyToString("video-codec")?.ToUpper() ?? "UNKNOWN";
                            result.Codec = rawCodec switch
                            {
                                string c when c.Contains("H264") || c.Contains("AVC") => "H.264",
                                string c when c.Contains("HEVC") || c.Contains("H265") => "HEVC",
                                string c when c.Contains("VP9") => "VP9",
                                string c when c.Contains("AV1") => "AV1",
                                string c when c.Contains("MPEG2") => "MPEG2",
                                _ => rawCodec.Split('/').FirstOrDefault()?.Trim() ?? rawCodec
                            };
                            
                            string br1 = player.Client.GetPropertyToString("video-bitrate");
                            string br2 = player.Client.GetPropertyToString("bitrate");
                            string br3 = player.Client.GetPropertyToString("estimated-bitrate");
                            string br4 = player.Client.GetPropertyToString("demuxer-bitrate");
                            string br5 = player.Client.GetPropertyToString("packet-video-bitrate");
                            string br6 = player.Client.GetPropertyToString("packet-bitrate");

                            Debug.WriteLine($"[StreamProber] Bitrate Trace ({stopwatch.ElapsedMilliseconds}ms): v-br:{br1} | br:{br2} | est:{br3} | demux:{br4} | p-v-br:{br5} | p-br:{br6}");

                            // HDR Detection
                            string primaries = player.Client.GetPropertyToString("video-params/primaries");
                            string trc = player.Client.GetPropertyToString("video-params/trc");
                            result.IsHdr = (primaries == "bt.2020") || (trc == "pq" || trc == "hlg");
                            
                            string brStr = br1 ?? br2 ?? br3 ?? br4 ?? br5 ?? br6;

                            if (long.TryParse(brStr, out long bitrate) && bitrate > 0)
                            {
                                result.Bitrate = bitrate;
                                result.Success = true;
                                Debug.WriteLine($"[StreamProber] ✅ Metadata Found (Found Bitrate: {bitrate}) at {stopwatch.ElapsedMilliseconds}ms | HDR: {result.IsHdr}");
                                break; // FINALLY GOT BITRATE
                            }
                            else
                            {
                                // If we have resolution but NO bitrate, we KEEP POLLING specifically for bitrate
                                if (stopwatch.ElapsedMilliseconds < 9500)
                                {
                                    await Task.Delay(250, ct); 
                                    continue; 
                                }
                                else if (stopwatch.ElapsedMilliseconds >= 9500)
                                {
                                    // Timeout reached, we give up on bitrate but return success if we have resolution
                                    Debug.WriteLine($"[StreamProber] ⚠️ Bitrate Timeout (Returning resolution only) | HDR: {result.IsHdr}");
                                    result.Success = true;
                                    break; 
                                }
                            }
                            
                            result.Success = true;
                            break;
                        }
                        
                        // Check for error/idle
                        string idleStr = player.Client.GetPropertyToString("idle-active");
                        if (idleStr == "yes" && stopwatch.ElapsedMilliseconds > 3000)
                        {
                            result.Error = "Idle state reached without metadata";
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Some properties throw a generic exception if the player state is "unstable"
                        // We skip these until the next poll.
                        if (ex is TaskCanceledException || ex is OperationCanceledException) throw;
                    }
                    
                    try { await Task.Delay(250, ct); } catch { break; }
                }

                if (result.Success)
                {
                    // Update Cache
                    ProbeCacheService.Instance.Update(url, result.Resolution, result.Fps, result.Codec, result.Bitrate, result.IsHdr);
                }

                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StreamProber] Error: {ex.Message}");
                return new ProbeResult { Success = false, Error = ex.Message };
            }
            finally
            {
                if (player != null)
                {
                    try 
                    { 
                        // Instead of disposing, we stop and return to pool
                        await player.Client.ExecuteAsync("stop");
                        _playerPool.Push((player, DateTime.Now)); 
                    } 
                    catch 
                    { 
                        // If something went wrong, dispose it instead of returning to pool
                        try { await player.DisposeAsync(); } catch { }
                    }
                }
                _poolLock.Release();
            }
        }

        public static async Task<(string Res, string Fps, string Codec, long Bitrate, bool Success, bool IsHdr)> ExtractProbeDataAsync(MpvWinUI.MpvPlayer player, CancellationToken ct)
        {
            try
            {
                // Wait for video parameters to be available
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.ElapsedMilliseconds < 5000 && !ct.IsCancellationRequested)
                {
                    var width = await player.GetPropertyLongAsync("video-params/w");
                    var height = await player.GetPropertyLongAsync("video-params/h");

                    if (width > 0 && height > 0)
                    {
                        string res = $"{width}x{height}";
                        string fpsStr = await player.GetPropertyAsync("estimated-fps");
                        double.TryParse(fpsStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double fpsVal);
                        string fps = $"{fpsVal:F2}";
                        
                        string codec = (await player.GetPropertyAsync("video-codec"))?.ToUpper() ?? "Unknown";
                        long bitrate = await player.GetPropertyLongAsync("video-bitrate");
                        if (bitrate <= 0) bitrate = await player.GetPropertyLongAsync("bitrate");
 
                        // HDR Detection
                        string primaries = await player.GetPropertyAsync("video-params/primaries");
                        string trc = await player.GetPropertyAsync("video-params/trc");
                        bool isHdr = (primaries == "bt.2020") || (trc == "pq" || trc == "hlg");
 
                        return (res, fps, codec, bitrate, true, isHdr);
                    }

                    await Task.Delay(250, ct);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StreamProberService] ExtractProbeData error: {ex.Message}");
            }

            return (null, null, null, 0, false, false);
        }

        private static string ExtractCookiesForProber(string url)
        {
            string header = "";
            try
            {
                var targetUri = new Uri(url);
                var cookies = HttpHelper.CookieContainer.GetCookies(targetUri);
                
                // Fallback to all cookies if strict domain match fails
                if (cookies.Count == 0)
                {
                    var domainTableField = HttpHelper.CookieContainer.GetType().GetRuntimeFields().FirstOrDefault(x => x.Name == "m_domainTable" || x.Name == "_domainTable");
                    var domains = domainTableField?.GetValue(HttpHelper.CookieContainer) as IDictionary;

                    if (domains != null)
                    {
                        foreach (var val in domains.Values)
                        {
                            var listField = val.GetType().GetRuntimeFields().FirstOrDefault(x => x.Name == "m_list" || x.Name == "_list");
                            var cookieList = listField?.GetValue(val) as IDictionary;

                            if (cookieList != null)
                            {
                                foreach (System.Net.CookieCollection col in cookieList.Values)
                                {
                                    foreach (System.Net.Cookie c in col)
                                    {
                                        if (url.Contains(c.Domain)) header += $"{c.Name}={c.Value}; ";
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    foreach (System.Net.Cookie c in cookies) header += $"{c.Name}={c.Value}; ";
                }

                return header.TrimEnd(' ', ';');
            }
            catch { return ""; }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _poolLock.Dispose();
                while (_playerPool.TryPop(out var entry))
                {
                    try { Task.Run(async () => await entry.Player.DisposeAsync()); } catch { }
                }
                _isDisposed = true;
            }
        }
    }
}
