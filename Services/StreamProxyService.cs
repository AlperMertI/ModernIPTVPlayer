using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Services.Streaming;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Local HTTP Proxy that bridges custom headers with the Native Media Foundation engine.
    /// By providing a localhost URL to Windows, we force it into "Network Streaming" mode,
    /// solving the un-seekable Live TS issues.
    /// </summary>
    public sealed class StreamProxyService : IDisposable
    {
        private static StreamProxyService _instance;
        public static StreamProxyService Instance => _instance ??= new StreamProxyService();

        /// <summary>
        /// Latest parsed SPS color information from the stream
        /// Populated when SPS (NAL 33) is detected and parsed
        /// </summary>
        public HevcTsParser.HevcSpsInfo? ColorInfo { get; private set; }

        private readonly HttpListener _listener;
        private readonly HttpClient _httpClient;
        private readonly int _port;
        private bool _isRunning;
        private static int _activeTaskCount = 0;
        public static int ActiveTaskCount => _activeTaskCount;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _activeRequests = new();

        private StreamProxyService()
        {
            _port = GetFreeTcpPort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/stream/");
            
            _httpClient = HttpHelper.Client;
        }

        public void Start()
        {
            if (_isRunning) return;
            try
            {
                _isRunning = true;
                _listener.Start();
                Task.Run(ListenLoop);
                Debug.WriteLine($"[StreamProxy] Service started on http://127.0.0.1:{_port}/stream/");
            }
            catch (Exception ex)
            {
                _isRunning = false;
                Debug.WriteLine($"[StreamProxy] CRITICAL: Failed to start listener: {ex.Message}");
            }
        }

        private int GetFreeTcpPort()
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return port;
            }
            catch
            {
                return new Random().Next(10000, 60000);
            }
        }

        public string GetProxyUrl(string targetUrl)
        {
            if (string.IsNullOrEmpty(targetUrl)) return null;
            
            var sessionId = Guid.NewGuid().ToString();
            var encodedUrl = Uri.EscapeDataString(targetUrl);
            return $"http://127.0.0.1:{_port}/stream/{sessionId}/?url={encodedUrl}";
        }

        public void StopAllRequests()
        {
            var sessions = _activeRequests.Keys.ToList();
            foreach (var session in sessions)
            {
                if (_activeRequests.TryRemove(session, out var cts))
                {
                    try { cts.Cancel(); cts.Dispose(); } catch { }
                }
            }
        }

        public void StopRequest(string proxyUrl)
        {
            if (string.IsNullOrEmpty(proxyUrl)) return;
            
            try
            {
                // Extract session ID from the proxy URL
                // Format: http://127.0.0.1:PORT/stream/{sessionId}/?url=...
                var uri = new Uri(proxyUrl);
                var segments = uri.Segments;
                if (segments.Length >= 3 && segments[1].Equals("stream/", StringComparison.OrdinalIgnoreCase))
                {
                    var sessionId = segments[2].TrimEnd('/');
                    if (_activeRequests.TryRemove(sessionId, out var cts))
                    {
                        cts.Cancel();
                        cts.Dispose();
                        Debug.WriteLine($"[StreamProxy] Session {sessionId} CANCELLED by request.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StreamProxy] Failed to stop request: {ex.Message}");
            }
        }

        private async Task ListenLoop()
        {
            while (_isRunning && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    
                    // Extract session ID from path
                    var segments = context.Request.Url.Segments;
                    string sessionId = null;
                    if (segments.Length >= 3 && segments[1].Equals("stream/", StringComparison.OrdinalIgnoreCase))
                    {
                        sessionId = segments[2].TrimEnd('/');
                    }

                Interlocked.Increment(ref _activeTaskCount);
                _ = Task.Run(async () => 
                {
                    try {
                        await HandleRequest(context, sessionId);
                    }
                    finally {
                        Interlocked.Decrement(ref _activeTaskCount);
                    }
                });
                }
                catch (Exception ex)
                {
                    if (_isRunning) Debug.WriteLine($"[StreamProxy] Listener error: {ex.Message}");
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext context, string sessionId)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string targetUrl = "Unknown";
            CancellationTokenSource? cts = null;

            if (!string.IsNullOrEmpty(sessionId))
            {
                cts = new CancellationTokenSource();
                _activeRequests[sessionId] = cts;
            }

            try
            {
                var ct = cts?.Token ?? default;
                // Robust URL extraction using QueryString collection
                targetUrl = context.Request.QueryString["url"];
                if (string.IsNullOrEmpty(targetUrl))
                {
                    Debug.WriteLine("[StreamProxy] ERROR: No target URL provided in query.");
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                // Debug.WriteLine($"[StreamProxy] New Request: {targetUrl}");
                ColorInfo = null;

                using (var request = new HttpRequestMessage(HttpMethod.Get, targetUrl))
                {
                    if (context.Request.Headers["Range"] != null)
                    {
                        request.Headers.Add("Range", context.Request.Headers["Range"]);
                    }

                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        // Debug.WriteLine($"[StreamProxy] Provider Response: {response.StatusCode} (Fetched in {sw.ElapsedMilliseconds}ms)");

                        context.Response.StatusCode = (int)response.StatusCode;

                        string contentType = response.Content.Headers.ContentType?.MediaType ?? "video/mp2t";
                        if (targetUrl.Contains(".ts") || targetUrl.Contains("/live/")) contentType = "video/mp2t";

                        context.Response.ContentType = contentType;
                        context.Response.SendChunked = true;
                        context.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                        context.Response.AddHeader("Access-Control-Allow-Origin", "*");

                        long totalBytesPiped = 0;
                        bool firstBytesForwarded = false;
                        bool gateOpen = false;
                        long packetsScanned = 0;
                        DateTime gateStartTime = DateTime.Now;

                        HevcTsParser.ParseResult parseResult = new HevcTsParser.ParseResult();

                        byte[] leftover = new byte[188];
                        int leftoverCount = 0;

                        List<byte> pesBuffer = new List<byte>();
                        bool pesReassembling = false;
                        ushort currentPesPID = 0xFFFF;
                        int pesHeaderSize = 0;
                        bool pesHeaderParsed = false;

                        using (var responseStream = await response.Content.ReadAsStreamAsync())
                        using (var outputStream = context.Response.OutputStream)
                        {
                            byte[] buffer = new byte[128 * 1024];
                            int bytesRead;

                            while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                            {
                                int currentOffset = 0;
                                int currentLength = bytesRead;

                                byte[] toProcess;
                                if (leftoverCount > 0)
                                {
                                    toProcess = new byte[leftoverCount + currentLength];
                                    Buffer.BlockCopy(leftover, 0, toProcess, 0, leftoverCount);
                                    Buffer.BlockCopy(buffer, currentOffset, toProcess, leftoverCount, currentLength);
                                }
                                else
                                {
                                    toProcess = new byte[currentLength];
                                    Buffer.BlockCopy(buffer, currentOffset, toProcess, 0, currentLength);
                                }

                                int totalAvailable = toProcess.Length;
                                int packetsAvailable = totalAvailable / 188;
                                leftoverCount = totalAvailable % 188;
                                if (leftoverCount > 0)
                                    Buffer.BlockCopy(toProcess, packetsAvailable * 188, leftover, 0, leftoverCount);

                                HevcTsParser.HevcSpsInfo? colorInfo = null;

                                for (int p = 0; p < packetsAvailable; p++)
                                {
                                    int pStart = p * 188;
                                    if (toProcess[pStart] != 0x47) continue;

                                    bool pusi = (toProcess[pStart + 1] & 0x40) != 0;
                                    int pid = ((toProcess[pStart + 1] & 0x1F) << 8) | toProcess[pStart + 2];
                                    bool hasPayload = (toProcess[pStart + 3] & 0x10) != 0;
                                    bool hasAF = (toProcess[pStart + 3] & 0x20) != 0;

                                    if (!hasPayload || pid == 0x1FFF) continue;

                                    int payloadStart = pStart + 4;
                                    if (hasAF)
                                    {
                                        int afLength = toProcess[payloadStart];
                                        if (afLength > 184) continue;
                                        payloadStart += (afLength + 1);
                                    }

                                    if (payloadStart >= pStart + 188) continue;

                                    int payloadSize = (pStart + 188) - payloadStart;

                                    if (pusi)
                                    {
                                        if (pesReassembling && pesBuffer.Count > 0)
                                        {
                                            if (!colorInfo.HasValue || !colorInfo.Value.HasColourDescription)
                                            {
                                                ParsePesPayloadForMetadata(pesBuffer.ToArray(), packetsScanned,
                                                    ref parseResult, ref colorInfo);
                                            }
                                        }

                                        pesBuffer.Clear();
                                        pesReassembling = true;
                                        currentPesPID = (ushort)pid;
                                        pesHeaderParsed = false;
                                        pesHeaderSize = 0;

                                        if (payloadStart + 6 <= pStart + 188)
                                        {
                                            if (toProcess[payloadStart] == 0x00 && toProcess[payloadStart+1] == 0x00 && toProcess[payloadStart+2] == 0x01)
                                            {
                                                byte streamId = toProcess[payloadStart + 3];
                                                ushort pesPacketLength = (ushort)((toProcess[payloadStart + 4] << 8) | toProcess[payloadStart + 5]);

                                                bool isVideo = (streamId >= 0xE0 && streamId <= 0xEF);

                                                if (isVideo)
                                                {
                                                    if (payloadStart + 8 <= pStart + 188)
                                                    {
                                                        byte ptdtsFlags = toProcess[payloadStart + 7];
                                                        int hasPtsDts = (ptdtsFlags >> 6) & 0x03;

                                                        if (payloadStart + 8 <= pStart + 188)
                                                        {
                                                            byte pesHeaderDataLen = toProcess[payloadStart + 8];
                                                            pesHeaderSize = 9 + pesHeaderDataLen;
                                                            pesHeaderParsed = true;

                                                            int pesPayloadOffset = payloadStart + pesHeaderSize;
                                                            if (pesPayloadOffset < pStart + 188)
                                                            {
                                                                int pesPayloadSize = (pStart + 188) - pesPayloadOffset;
                                                                for (int i = 0; i < pesPayloadSize; i++)
                                                                {
                                                                    pesBuffer.Add(toProcess[pesPayloadOffset + i]);
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else if (pesReassembling && pid == currentPesPID)
                                    {
                                        for (int i = 0; i < payloadSize; i++)
                                        {
                                            pesBuffer.Add(toProcess[payloadStart + i]);
                                        }

                                        if (pesBuffer.Count > 2 * 1024 * 1024)
                                        {
                                            if (pesHeaderParsed && (!colorInfo.HasValue || !colorInfo.Value.HasColourDescription))
                                            {
                                                ParsePesPayloadForMetadata(pesBuffer.ToArray(), packetsScanned,
                                                    ref parseResult, ref colorInfo);
                                            }
                                            pesBuffer.Clear();
                                            pesHeaderParsed = false;
                                        }
                                    }

                                    packetsScanned++;
                                }

                                ColorInfo = colorInfo;

                                if (!gateOpen && (DateTime.Now - gateStartTime).TotalSeconds > 5.0) gateOpen = true;

                                int bytesToOutput = packetsAvailable * 188;
                                if (bytesToOutput > 0)
                                {
                                    await outputStream.WriteAsync(toProcess, 0, bytesToOutput, ct);
                                    if (!firstBytesForwarded)
                                    {
                                        firstBytesForwarded = true;
                                        // Debug.WriteLine($"[StreamProxy] First bytes forwarded in {sw.ElapsedMilliseconds}ms");
                                    }
                                    totalBytesPiped += bytesToOutput;
                                }
                            }
                        }
                    }
                }

                context.Response.Close();
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    Debug.WriteLine($"[StreamProxy] CRITICAL ERROR for {targetUrl}: {ex.Message}");
                try { context.Response.Abort(); } catch { }
            }
            finally
            {
                if (!string.IsNullOrEmpty(sessionId))
                {
                    _activeRequests.TryRemove(sessionId, out _);
                    cts?.Dispose();
                }
            }
        }

        /// <summary>
        /// Parse PES payload using HevcTsParser to extract NAL metadata.
        /// This is called when a complete PES payload is assembled.
        /// The PES bytes are passed through unchanged to the HTTP client.
        /// </summary>
        private static void ParsePesPayloadForMetadata(byte[] pesPayload, long packetsScanned,
            ref HevcTsParser.ParseResult parseResult, ref HevcTsParser.HevcSpsInfo? colorInfo)
        {
            // Use HevcTsParser to analyze the PES payload
            parseResult = HevcTsParser.ParsePesPayload(pesPayload, parseResult);

            // Update color info from parsed result
            if (parseResult.ColorInfo.HasValue && parseResult.ColorInfo.Value.HasColourDescription)
            {
                colorInfo = parseResult.ColorInfo;
                // Color metadata is surfaced through StreamProxyService.ColorInfo; avoid log spam here.
            }
        }

        public void Dispose()
        {
            _isRunning = false;
            foreach (var cts in _activeRequests.Values)
            {
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }
            _activeRequests.Clear();

            try { _listener.Stop(); } catch { }
            _httpClient?.Dispose();
        }
    }
}
