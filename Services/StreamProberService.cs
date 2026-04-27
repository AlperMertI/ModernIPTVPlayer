using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Mpv.Core;
using Mpv.Core.Interop;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Timers;
using System.Text.Json;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Holds the results of a stream analysis.
    /// Can be reported incrementally via IProgress.
    /// </summary>
    public class ProbeResult
    {
        // Basic Info
        public string Resolution { get; set; }
        public string Fps { get; set; }
        public string Codec { get; set; }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }

        // Advanced Video
        public string AspectRatio { get; set; }
        public string PixelFormat { get; set; }
        public string ColorSpace { get; set; }
        public string ColorRange { get; set; }
        public string ChromaSubsampling { get; set; }
        public string ScanType { get; set; }
        public string Encoder { get; set; }

        // Audio Details
        public string AudioCodec { get; set; }
        public string AudioChannels { get; set; }
        public string AudioSampleRate { get; set; }
        public string AudioLanguages { get; set; }

        // Network & Protocol
        public string Container { get; set; }
        public string Protocol { get; set; }
        public string Server { get; set; }
        public string MimeType { get; set; }
        public int Latency { get; set; }

        // Buffer & Performance
        public long BufferSize { get; set; }
        public double BufferDuration { get; set; }
        public double AvSync { get; set; }

        // Security & Tracks
        public bool IsEncrypted { get; set; }
        public string DrmType { get; set; }
        public string SubtitleTracks { get; set; }

        public bool Success { get; set; }
        public string Error { get; set; }

        public ProbeData ToCacheData() => new ProbeData
        {
            Resolution = Resolution,
            Fps = Fps,
            Codec = Codec,
            Bitrate = Bitrate,
            IsHdr = IsHdr,
            AspectRatio = AspectRatio,
            PixelFormat = PixelFormat,
            ColorSpace = ColorSpace,
            ColorRange = ColorRange,
            ChromaSubsampling = ChromaSubsampling,
            ScanType = ScanType,
            Encoder = Encoder,
            AudioCodec = AudioCodec,
            AudioChannels = AudioChannels,
            AudioSampleRate = AudioSampleRate,
            AudioLanguages = AudioLanguages,
            Container = Container,
            Protocol = Protocol,
            Server = Server,
            MimeType = MimeType,
            Latency = Latency,
            BufferSize = BufferSize,
            BufferDuration = BufferDuration,
            AvSync = AvSync,
            IsEncrypted = IsEncrypted,
            DrmType = DrmType,
            SubtitleTracks = SubtitleTracks,
            LastUpdated = DateTime.Now
        };
    }

    /// <summary>
    /// Service for background stream analysis using pooled mpv instances.
    /// Supports progressive reporting for modern "shimmer" UI effects.
    /// </summary>
    public class StreamProberService : IDisposable
    {
        private static StreamProberService _instance;
        public static StreamProberService Instance => _instance ??= new StreamProberService();

        private readonly SemaphoreSlim _poolLock = new SemaphoreSlim(AppSettings.ProbingWorkerCount, AppSettings.ProbingWorkerCount);
        private readonly System.Collections.Concurrent.ConcurrentStack<(Player Player, DateTime LastUsed)> _playerPool = new System.Collections.Concurrent.ConcurrentStack<(Player, DateTime)>();
        private readonly System.Timers.Timer _idleCleanupTimer;
        private bool _isDisposed = false;
        private const int MAX_WARM_PLAYERS = 1; // Keep 1 ready for instant response

        private StreamProberService() 
        {
            _idleCleanupTimer = new System.Timers.Timer(60000); 
            _idleCleanupTimer.Elapsed += OnIdleCleanupElapsed;
            _idleCleanupTimer.AutoReset = true;
            _idleCleanupTimer.Start();
        }

        private async void OnIdleCleanupElapsed(object sender, ElapsedEventArgs e)
        {
            var now = DateTime.Now;
            var toDispose = new List<Player>();
            var temp = new List<(Player Player, DateTime LastUsed)>();

            while (_playerPool.TryPop(out var entry))
            {
                // Keep only warm players or items used recently (last 60s)
                if (temp.Count >= MAX_WARM_PLAYERS && (now - entry.LastUsed).TotalSeconds > 60) 
                {
                    toDispose.Add(entry.Player);
                }
                else 
                {
                    temp.Add(entry);
                }
            }

            for (int i = temp.Count - 1; i >= 0; i--)
            {
                _playerPool.Push(temp[i]);
            }

            foreach (var p in toDispose) 
            { 
                try { await p.DisposeAsync(); } catch { } 
            }
        }

        /// <summary>
        /// Probes a stream with optional progressive reporting.
        /// </summary>
        public async Task<ProbeResult> ProbeAsync(int streamId, string url, IProgress<ProbeResult> progress = null, CancellationToken ct = default)
        {
            // 1. Check Cache
            var cached = ProbeCacheService.Instance.Get(streamId);
            if (cached != null)
            {
                var cachedResult = new ProbeResult
                {
                    Resolution = cached.Resolution,
                    Fps = cached.Fps,
                    Codec = cached.Codec,
                    Bitrate = cached.Bitrate,
                    IsHdr = cached.IsHdr,
                    AspectRatio = cached.AspectRatio,
                    PixelFormat = cached.PixelFormat,
                    ColorSpace = cached.ColorSpace,
                    ScanType = cached.ScanType,
                    AudioCodec = cached.AudioCodec,
                    AudioChannels = cached.AudioChannels,
                    AudioSampleRate = cached.AudioSampleRate,
                    Container = cached.Container,
                    Server = cached.Server,
                    Success = true
                };
                progress?.Report(cachedResult);
                return cachedResult;
            }

            await _poolLock.WaitAsync(ct);
            Player player = null;
            try
            {
                if (!_playerPool.TryPop(out var entry))
                {
                    player = new Player();
                    
                    // ULTRA-LIGHT BACKGROUND PROBING CONFIGURATION
                    // Minimizes native memory, threads, and network buffers.
                    player.Client.SetOption("vo", "null");
                    player.Client.SetOption("ao", "null");
                    player.Client.SetOption("ytdl", "no");
                    player.Client.SetOption("demuxer-max-bytes", "131072");      // 128 KB limit
                    player.Client.SetOption("demuxer-max-back-bytes", "131072"); // 128 KB back buffer
                    player.Client.SetOption("cache", "no");                      // Disable disk cache
                    player.Client.SetOption("network-timeout", "5");             // Fast fail
                    player.Client.SetOption("vd-lavc-fast", "yes");              // Fast decoding
                    player.Client.SetOption("load-stats-overlay", "no");
                    player.Client.SetOption("input-default-bindings", "no");
                    player.Client.SetOption("input-vo-keyboard", "no");

                    await player.InitializeAsync();
                }
                else player = entry.Player;

                // Load file
                await player.Client.ExecuteAsync($"loadfile \"{url.Replace("\"", "\\\"")}\" replace");
                
                var stopwatch = Stopwatch.StartNew();
                var result = new ProbeResult { Success = false };

                // Progressive Polling Logic
                while (stopwatch.ElapsedMilliseconds < 12000 && !ct.IsCancellationRequested)
                {
                    bool changed = false;

                    // 1. Video Core Info
                    if (string.IsNullOrEmpty(result.Resolution))
                    {
                        string w = await GetPropertySafeAsync(player.Client, "video-params/w");
                        string h = await GetPropertySafeAsync(player.Client, "video-params/h");
                        if (!string.IsNullOrEmpty(w) && !string.IsNullOrEmpty(h))
                        {
                            result.Resolution = $"{w}x{h}";
                            
                            string f = await GetPropertySafeAsync(player.Client, "estimated-fps");
                            if (string.IsNullOrEmpty(f) || f == "0.000000") f = await GetPropertySafeAsync(player.Client, "fps");
                            if (string.IsNullOrEmpty(f) || f == "0.000000") f = await GetPropertySafeAsync(player.Client, "container-fps");
                            if (double.TryParse(f, NumberStyles.Any, CultureInfo.InvariantCulture, out double fv) && fv > 0)
                                result.Fps = fv.ToString("F2");

                            string vCodec = await GetPropertySafeAsync(player.Client, "video-codec");
                            if (string.IsNullOrEmpty(vCodec)) vCodec = await GetPropertySafeAsync(player.Client, "video-params/codec");
                            result.Codec = SimplifyCodec(vCodec);

                            result.AspectRatio = await GetPropertySafeAsync(player.Client, "video-params/aspect");
                            result.PixelFormat = await GetPropertySafeAsync(player.Client, "video-params/pixelformat");
                            
                            string interlaced = await GetPropertySafeAsync(player.Client, "video-params/interlaced");
                            result.ScanType = interlaced == "yes" ? "i" : "p";
                            
                            changed = true;
                        }
                    }

                    // 2. Color & Encryption & Encoder
                    if (result.Resolution != null && string.IsNullOrEmpty(result.ColorSpace))
                    {
                        result.ColorSpace = await GetPropertySafeAsync(player.Client, "video-params/primaries") ?? "Auto";
                        result.ColorRange = await GetPropertySafeAsync(player.Client, "video-params/colorlevels");
                        result.ChromaSubsampling = await GetPropertySafeAsync(player.Client, "video-params/chroma-location");
                        
                        string trc = await GetPropertySafeAsync(player.Client, "video-params/trc");
                        result.IsHdr = result.ColorSpace == "bt.2020" || trc == "pq" || trc == "hlg";
                        
                        result.Encoder = await GetPropertySafeAsync(player.Client, "metadata/by-key/encoder");
                        changed = true;
                    }

                    // 3. Audio & Tracks (Languages / Subtitles)
                    if (string.IsNullOrEmpty(result.AudioCodec))
                    {
                        result.AudioCodec = SimplifyCodec(await GetPropertySafeAsync(player.Client, "audio-codec"));
                        result.AudioChannels = await GetPropertySafeAsync(player.Client, "audio-params/channel-count");
                        result.AudioSampleRate = await GetPropertySafeAsync(player.Client, "audio-params/samplerate");
                        
                        // Extract languages and subtitles from track-list
                        string tracksJson = await GetPropertySafeAsync(player.Client, "track-list");
                        if (!string.IsNullOrEmpty(tracksJson))
                        {
                            try {
                                using var doc = JsonDocument.Parse(tracksJson);
                                var audioLangs = new List<string>();
                                var subTracks = new List<string>();
                                foreach (var track in doc.RootElement.EnumerateArray()) {
                                    string type = track.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                                    string lang = track.TryGetProperty("lang", out var lp) ? lp.GetString() : "Unknown";
                                    if (type == "audio") audioLangs.Add(lang);
                                    else if (type == "sub") subTracks.Add(lang);
                                }
                                result.AudioLanguages = string.Join(", ", audioLangs.Distinct());
                                result.SubtitleTracks = string.Join(", ", subTracks.Distinct());
                            } catch { }
                        }
                        if (result.AudioCodec != null) changed = true;
                    }

                    // 4. Network & Buffer & Stats
                    result.Container = (await GetPropertySafeAsync(player.Client, "file-format"))?.ToUpper();
                    result.Protocol = (await GetPropertySafeAsync(player.Client, "metadata/by-key/protocol")) ?? (url.StartsWith("http") ? "HTTP" : "UDP");
                    result.MimeType = await GetPropertySafeAsync(player.Client, "metadata/by-key/content-type");
                    
                    // Real-time stats
                    string avs = await GetPropertySafeAsync(player.Client, "avsync");
                    if (double.TryParse(avs, NumberStyles.Any, CultureInfo.InvariantCulture, out double avv)) result.AvSync = avv * 1000;

                    // Buffer stats from demuxer-cache-state
                    string cacheJson = await GetPropertySafeAsync(player.Client, "demuxer-cache-state");
                    if (!string.IsNullOrEmpty(cacheJson))
                    {
                        try {
                            using var doc = JsonDocument.Parse(cacheJson);
                            if (doc.RootElement.TryGetProperty("fw-bytes", out var b)) result.BufferSize = b.GetInt64();
                            if (doc.RootElement.TryGetProperty("reader-pts", out var r) && doc.RootElement.TryGetProperty("cache-end", out var c))
                                result.BufferDuration = c.GetDouble() - r.GetDouble();
                        } catch { }
                    }

                    // 5. Bitrate
                    if (result.Bitrate == 0)
                    {
                        string br = await GetPropertySafeAsync(player.Client, "video-bitrate") ?? 
                                    await GetPropertySafeAsync(player.Client, "bitrate");
                        if (long.TryParse(br, out long brVal) && brVal > 0)
                        {
                            result.Bitrate = brVal;
                            changed = true;
                        }
                    }

                    if (changed) progress?.Report(result);
                    await Task.Delay(250, ct);

                    if (changed) progress?.Report(result);

                    // Break if we have the "minimum viable technical info"
                    if (result.Resolution != null && result.AudioCodec != null && (result.Bitrate > 0 || stopwatch.ElapsedMilliseconds > 8000))
                    {
                        result.Success = true;
                        break;
                    }

                    await Task.Delay(400, ct);
                }

                if (result.Success) ProbeCacheService.Instance.Update(streamId, result.ToCacheData());
                return result;
            }
            catch (Exception ex)
            {
                return new ProbeResult { Success = false, Error = ex.Message };
            }
            finally
            {
                if (player != null)
                {
                    try { await player.Client.ExecuteAsync("stop"); _playerPool.Push((player, DateTime.Now)); } 
                    catch { try { await player.DisposeAsync(); } catch { } }
                }
                _poolLock.Release();
            }
        }

        private static async Task<string> GetPropertySafeAsync(MpvClientNative client, string name)
        {
            return await Task.Run(() =>
            {
                try { return client.GetPropertyToString(name); }
                catch { return null; }
            });
        }

        private static string ExtractCookiesForProber(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return "";
                var targetUri = new Uri(url);
                var cookies = HttpHelper.CookieContainer.GetCookies(targetUri);
                var header = "";
                if (cookies != null)
                {
                    foreach (System.Net.Cookie c in cookies) header += $"{c.Name}={c.Value}; ";
                }
                return header.TrimEnd(' ', ';');
            }
            catch { return ""; }
        }

        private static string SimplifyCodec(string codec)
        {
            if (string.IsNullOrEmpty(codec)) return null;
            codec = codec.ToUpper();
            if (codec == "UNKNOWN") return "UNKNOWN";

            if (codec.Contains("H265") || codec.Contains("HEVC")) return "H265";
            if (codec.Contains("H264") || codec.Contains("AVC")) return "H264";
            if (codec.Contains("H263")) return "H.263";
            if (codec.Contains("VP9")) return "VP9";
            if (codec.Contains("VP8")) return "VP8";
            if (codec.Contains("AV1")) return "AV1";
            if (codec.Contains("MJPEG")) return "MJPEG";
            if (codec.Contains("MPEG2")) return "MPEG2";
            if (codec.Contains("MPEG4")) return "MPEG4";
            if (codec.Contains("AAC")) return "AAC";
            if (codec.Contains("AC3")) return "AC3";
            if (codec.Contains("MP3")) return "MP3";
            if (codec.Contains("DTS")) return "DTS";
            if (codec.Contains("OPUS")) return "OPUS";

            // Default fallback: take the first segment and truncate if too long
            string first = codec.Split('/')[0].Split(' ')[0].Trim();
            return first.Length > 8 ? first.Substring(0, 6) + ".." : first;
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
        /// <summary>
        /// LEGACY/HELPER: Extracts probe data from an existing player instance.
        /// Useful when a player is already active and we don't want to start a new prober.
        /// </summary>
        public static async Task<ProbeResult> ExtractProbeDataAsync(MpvWinUI.MpvPlayer player, CancellationToken ct)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.ElapsedMilliseconds < 5000 && !ct.IsCancellationRequested)
                {
                    string wStr = await player.GetPropertyAsync("video-params/w");
                    string hStr = await player.GetPropertyAsync("video-params/h");

                    if (!string.IsNullOrEmpty(wStr) && !string.IsNullOrEmpty(hStr))
                    {
                        var result = new ProbeResult
                        {
                            Resolution = $"{wStr}x{hStr}",
                            Fps = await player.GetPropertyAsync("estimated-fps"),
                            Codec = SimplifyCodec(await player.GetPropertyAsync("video-codec")),
                            Success = true
                        };
                        
                        string brStr = await player.GetPropertyAsync("video-bitrate");
                        if (long.TryParse(brStr, out long br)) result.Bitrate = br;

                        string primaries = await player.GetPropertyAsync("video-params/primaries");
                        result.IsHdr = primaries == "bt.2020";
                        
                        return result;
                    }
                    await Task.Delay(250, ct);
                }
                return new ProbeResult { Success = false };
            }
            catch { return new ProbeResult { Success = false }; }
        }
    }
}
