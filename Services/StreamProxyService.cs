using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Services.Streaming;

namespace ModernIPTVPlayer.Services
{
    public enum StreamProxyMode
    {
        ProgressiveVod,
        LiveTs
    }

    /// <summary>
    /// Local HTTP proxy used only when Media Foundation needs help with headers or raw TS.
    /// VOD mode is intentionally stateless and byte-exact because MF performs precise range reads.
    /// Live TS mode can scan early HEVC PES payloads for diagnostics while forwarding bytes unchanged.
    /// </summary>
    public sealed class StreamProxyService : IDisposable
    {
        private static StreamProxyService _instance;
        public static StreamProxyService Instance => _instance ??= new StreamProxyService();

        private const int CopyBufferSize = 128 * 1024;
        private const long MetadataScanWindowBytes = 10L * 1024 * 1024;

        public HevcTsParser.HevcSpsInfo? ColorInfo { get; private set; }

        private static int _activeTaskCount;
        public static int ActiveTaskCount => _activeTaskCount;

        private readonly HttpListener _listener;
        private readonly HttpClient _httpClient;
        private readonly int _port;
        private volatile bool _isRunning;

        private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessionCts = new();
        private readonly ConcurrentDictionary<string, ProxySessionInfo> _sessions = new();

        private sealed class ProxySessionInfo
        {
            public string TargetUrl { get; init; } = "";
            public StreamProxyMode Mode { get; init; }
            public string? LastContentType { get; set; }
            public int RequestCount;
        }

        private sealed class ProxyRequest
        {
            public string SessionId { get; init; } = "";
            public string TargetUrl { get; init; } = "";
            public StreamProxyMode Mode { get; init; }
        }

        private StreamProxyService()
        {
            _port = GetFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/stream/");

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                EnableMultipleHttp2Connections = true,
                MaxConnectionsPerServer = 32,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _isRunning = true;
                _listener.Start();
                _ = Task.Run(ListenLoopAsync);
                Log(null, $"Listening on http://127.0.0.1:{_port}/stream/");
            }
            catch (Exception ex)
            {
                _isRunning = false;
                LogError(null, "Failed to start listener", ex);
            }
        }

        public string GetProxyUrl(string targetUrl, StreamProxyMode mode = StreamProxyMode.LiveTs)
        {
            if (string.IsNullOrWhiteSpace(targetUrl)) return null;

            var sid = Guid.NewGuid().ToString("N");
            _sessions[sid] = new ProxySessionInfo
            {
                TargetUrl = targetUrl,
                Mode = mode
            };

            ColorInfo = null;

            return $"http://127.0.0.1:{_port}/stream/{sid}/?mode={mode}&url={Uri.EscapeDataString(targetUrl)}";
        }

        public void StopRequest(string proxyUrl)
        {
            if (string.IsNullOrWhiteSpace(proxyUrl)) return;

            try
            {
                var sid = ExtractSessionId(proxyUrl);
                if (sid == null) return;

                CancelSession(sid);
                _sessions.TryRemove(sid, out _);
                Log(sid, "Session stopped by caller.");
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        public void StopAllRequests()
        {
            foreach (var sid in _sessionCts.Keys.ToList())
            {
                CancelSession(sid);
                _sessions.TryRemove(sid, out _);
            }
        }

        private async Task ListenLoopAsync()
        {
            while (_isRunning && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (IsBenignListenerException(ex))
                {
                    continue;
                }
                catch (Exception ex)
                {
                    if (_isRunning) LogError(null, "Listener accept error", ex);
                    continue;
                }

                Interlocked.Increment(ref _activeTaskCount);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleRequestAsync(context).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeTaskCount);
                    }
                });
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            ProxyRequest request;
            try
            {
                request = ParseProxyRequest(context.Request);
                if (request == null)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                if (!_sessions.TryGetValue(request.SessionId, out var info))
                {
                    info = new ProxySessionInfo
                    {
                        TargetUrl = request.TargetUrl,
                        Mode = request.Mode
                    };
                    _sessions[request.SessionId] = info;
                }

                Interlocked.Increment(ref info.RequestCount);
                LogRequest(request.SessionId, context.Request, request.TargetUrl, request.Mode);

                var sessionCts = _sessionCts.GetOrAdd(request.SessionId, _ => new CancellationTokenSource());
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);

                await ProxyExactRequestAsync(context, request, info, linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsBenignStreamException(ex))
            {
                try { context.Response.Abort(); } catch { }
            }
            catch (Exception ex)
            {
                LogError(null, "Unhandled request error", ex);
                try { context.Response.Abort(); } catch { }
            }
        }

        private async Task ProxyExactRequestAsync(
            HttpListenerContext context,
            ProxyRequest proxyRequest,
            ProxySessionInfo sessionInfo,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage upstream = null;
            Stream upstreamStream = null;

            try
            {
                using var upstreamRequest = BuildUpstreamRequest(context.Request, proxyRequest.TargetUrl);
                upstream = await _httpClient
                    .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                sessionInfo.LastContentType = upstream.Content.Headers.ContentType?.ToString();
                WriteResponseHeaders(context.Response, upstream, proxyRequest);

                Log(proxyRequest.SessionId,
                    $"Upstream {(int)upstream.StatusCode} {upstream.StatusCode} mode={proxyRequest.Mode} ct={sessionInfo.LastContentType ?? "-"} len={upstream.Content.Headers.ContentLength?.ToString("N0", CultureInfo.InvariantCulture) ?? "-"}");

                if (context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Close();
                    return;
                }

                upstreamStream = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await CopyBodyAsync(
                    proxyRequest.SessionId,
                    context.Response.OutputStream,
                    upstreamStream,
                    proxyRequest.Mode,
                    IsTsLike(proxyRequest.TargetUrl, sessionInfo.LastContentType),
                    cancellationToken).ConfigureAwait(false);

                try { context.Response.Close(); } catch { }
            }
            finally
            {
                try { upstreamStream?.Dispose(); } catch { }
                try { upstream?.Dispose(); } catch { }
            }
        }

        private static HttpRequestMessage BuildUpstreamRequest(HttpListenerRequest clientRequest, string targetUrl)
        {
            var method = clientRequest.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                ? HttpMethod.Head
                : HttpMethod.Get;

            var request = new HttpRequestMessage(method, targetUrl)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            request.Headers.TryAddWithoutValidation("User-Agent", HttpHelper.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", BuildAcceptLanguageHeader());

            CopyHeader(clientRequest, request, "Range");
            CopyHeader(clientRequest, request, "If-Range");
            CopyHeader(clientRequest, request, "If-None-Match");
            CopyHeader(clientRequest, request, "If-Modified-Since");

            return request;
        }

        private static void CopyHeader(HttpListenerRequest source, HttpRequestMessage target, string name)
        {
            var value = source.Headers[name];
            if (!string.IsNullOrWhiteSpace(value))
            {
                target.Headers.TryAddWithoutValidation(name, value);
            }
        }

        private static string BuildAcceptLanguageHeader()
        {
            var language = CultureInfo.CurrentUICulture.Name;
            var neutral = language.Split('-')[0];
            return $"{language},{neutral};q=0.9,en-US;q=0.8,en;q=0.7";
        }

        private static void WriteResponseHeaders(
            HttpListenerResponse response,
            HttpResponseMessage upstream,
            ProxyRequest proxyRequest)
        {
            response.StatusCode = (int)upstream.StatusCode;
            response.KeepAlive = true;
            response.SendChunked = false;

            var contentType = upstream.Content.Headers.ContentType?.ToString();
            if (string.IsNullOrWhiteSpace(contentType) && proxyRequest.Mode == StreamProxyMode.LiveTs)
            {
                contentType = "video/mp2t";
            }

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                response.ContentType = contentType;
            }

            if (upstream.Content.Headers.ContentLength.HasValue)
            {
                response.ContentLength64 = upstream.Content.Headers.ContentLength.Value;
            }
            else
            {
                response.SendChunked = true;
            }

            AddHeader(response, "Accept-Ranges", "bytes");
            CopyResponseHeader(upstream, response, "Content-Range");
            CopyResponseHeader(upstream, response, "Content-Encoding");
            CopyResponseHeader(upstream, response, "Last-Modified");
            CopyResponseHeader(upstream, response, "ETag");
            CopyResponseHeader(upstream, response, "Cache-Control");
            CopyResponseHeader(upstream, response, "Expires");
            CopyResponseHeader(upstream, response, "Vary");
            AddHeader(response, "Access-Control-Allow-Origin", "*");
        }

        private static void CopyResponseHeader(HttpResponseMessage source, HttpListenerResponse target, string name)
        {
            if (source.Content.Headers.TryGetValues(name, out var contentValues))
            {
                AddHeader(target, name, string.Join(", ", contentValues));
                return;
            }

            if (source.Headers.TryGetValues(name, out var responseValues))
            {
                AddHeader(target, name, string.Join(", ", responseValues));
            }
        }

        private static void AddHeader(HttpListenerResponse response, string name, string value)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    response.AddHeader(name, value);
                }
            }
            catch
            {
                // HttpListener rejects a few restricted/invalid headers. Skip them.
            }
        }

        private async Task CopyBodyAsync(
            string sid,
            Stream output,
            Stream input,
            StreamProxyMode mode,
            bool isTsLike,
            CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            var scanner = mode == StreamProxyMode.LiveTs && isTsLike ? new LiveTsHevcScanner() : null;
            long copied = 0;

            try
            {
                while (true)
                {
                    int read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;

                    if (scanner != null && copied < MetadataScanWindowBytes)
                    {
                        scanner.Feed(buffer, read);
                        if (scanner.ColorInfo.HasValue)
                        {
                            ColorInfo = scanner.ColorInfo;
                        }
                    }

                    await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    copied += read;
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                Log(sid, $"Pipe complete mode={mode} bytes={copied:N0} tsScan={(scanner != null ? "yes" : "no")}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private sealed class LiveTsHevcScanner
        {
            private readonly byte[] _leftover = new byte[187];
            private readonly System.Collections.Generic.List<byte> _pesBuffer = new(capacity: 256 * 1024);
            private int _leftoverCount;
            private bool _reassembling;
            private ushort _currentPid = 0xFFFF;
            private HevcTsParser.ParseResult _parseResult = new();

            public HevcTsParser.HevcSpsInfo? ColorInfo { get; private set; }

            public void Feed(byte[] buffer, int count)
            {
                if (count <= 0 || ColorInfo.HasValue && ColorInfo.Value.HasColourDescription) return;

                byte[] rented = null;
                ReadOnlySpan<byte> data;

                if (_leftoverCount > 0)
                {
                    rented = ArrayPool<byte>.Shared.Rent(_leftoverCount + count);
                    Buffer.BlockCopy(_leftover, 0, rented, 0, _leftoverCount);
                    Buffer.BlockCopy(buffer, 0, rented, _leftoverCount, count);
                    data = rented.AsSpan(0, _leftoverCount + count);
                }
                else
                {
                    data = buffer.AsSpan(0, count);
                }

                try
                {
                    int packetCount = data.Length / 188;
                    _leftoverCount = data.Length % 188;
                    if (_leftoverCount > 0)
                    {
                        data.Slice(packetCount * 188, _leftoverCount).CopyTo(_leftover);
                    }

                    for (int p = 0; p < packetCount; p++)
                    {
                        ProcessPacket(data.Slice(p * 188, 188));
                        if (ColorInfo.HasValue && ColorInfo.Value.HasColourDescription) break;
                    }
                }
                finally
                {
                    if (rented != null)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }

            private void ProcessPacket(ReadOnlySpan<byte> packet)
            {
                if (packet.Length != 188 || packet[0] != 0x47) return;

                bool pusi = (packet[1] & 0x40) != 0;
                ushort pid = (ushort)(((packet[1] & 0x1F) << 8) | packet[2]);
                bool hasPayload = (packet[3] & 0x10) != 0;
                bool hasAdaptation = (packet[3] & 0x20) != 0;

                if (!hasPayload || pid == 0x1FFF) return;

                int payloadStart = 4;
                if (hasAdaptation)
                {
                    if (payloadStart >= packet.Length) return;
                    int adaptationLength = packet[payloadStart];
                    payloadStart += adaptationLength + 1;
                }

                if (payloadStart >= packet.Length) return;
                var payload = packet.Slice(payloadStart);

                if (pusi)
                {
                    FlushPes();
                    _pesBuffer.Clear();
                    _reassembling = false;
                    _currentPid = pid;

                    if (payload.Length < 9 ||
                        payload[0] != 0x00 ||
                        payload[1] != 0x00 ||
                        payload[2] != 0x01)
                    {
                        return;
                    }

                    byte streamId = payload[3];
                    bool isVideo = streamId >= 0xE0 && streamId <= 0xEF;
                    if (!isVideo) return;

                    int pesHeaderLength = 9 + payload[8];
                    if (pesHeaderLength >= payload.Length) return;

                    _reassembling = true;
                    AppendPes(payload.Slice(pesHeaderLength));
                    return;
                }

                if (_reassembling && pid == _currentPid)
                {
                    AppendPes(payload);
                    if (_pesBuffer.Count > 2 * 1024 * 1024)
                    {
                        FlushPes();
                        _pesBuffer.Clear();
                    }
                }
            }

            private void AppendPes(ReadOnlySpan<byte> payload)
            {
                for (int i = 0; i < payload.Length; i++)
                {
                    _pesBuffer.Add(payload[i]);
                }
            }

            private void FlushPes()
            {
                if (_pesBuffer.Count == 0) return;

                try
                {
                    _parseResult = HevcTsParser.ParsePesPayload(_pesBuffer.ToArray(), _parseResult);
                    if (_parseResult.ColorInfo.HasValue)
                    {
                        ColorInfo = _parseResult.ColorInfo;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[StreamProxy] Live TS HEVC parse error: {ex.Message}");
                }
            }
        }

        private static bool IsTsLike(string targetUrl, string contentType)
        {
            return (!string.IsNullOrWhiteSpace(contentType) &&
                    contentType.Contains("mp2t", StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrWhiteSpace(targetUrl) &&
                    (targetUrl.Contains(".ts", StringComparison.OrdinalIgnoreCase) ||
                     targetUrl.Contains("/live/", StringComparison.OrdinalIgnoreCase)));
        }

        private static ProxyRequest ParseProxyRequest(HttpListenerRequest request)
        {
            var targetUrl = request.QueryString["url"];
            if (string.IsNullOrWhiteSpace(targetUrl)) return null;

            var sid = ExtractSessionId(request.Url.AbsoluteUri) ?? "global";
            var modeText = request.QueryString["mode"];
            var mode = Enum.TryParse(modeText, ignoreCase: true, out StreamProxyMode parsed)
                ? parsed
                : StreamProxyMode.LiveTs;

            return new ProxyRequest
            {
                SessionId = sid,
                TargetUrl = targetUrl,
                Mode = mode
            };
        }

        private static string ExtractSessionId(string url)
        {
            try
            {
                var uri = new Uri(url);
                var segments = uri.Segments;
                if (segments.Length >= 3 &&
                    segments[1].Equals("stream/", StringComparison.OrdinalIgnoreCase))
                {
                    return segments[2].TrimEnd('/');
                }
            }
            catch
            {
                // Invalid URL means no session id.
            }

            return null;
        }

        private void CancelSession(string sid)
        {
            if (_sessionCts.TryRemove(sid, out var cts))
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

        private static int GetFreePort()
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
            catch
            {
                return new Random().Next(20000, 60000);
            }
        }

        private static bool IsBenignListenerException(Exception ex)
        {
            return ex is ObjectDisposedException ||
                   ex is OperationCanceledException ||
                   ex is HttpListenerException hle && (hle.ErrorCode == 995 || hle.ErrorCode == 64);
        }

        private static bool IsBenignStreamException(Exception ex)
        {
            return ex is OperationCanceledException ||
                   ex is IOException ||
                   ex is HttpListenerException ||
                   ex is ObjectDisposedException;
        }

        private static void LogRequest(string sid, HttpListenerRequest request, string targetUrl, StreamProxyMode mode)
        {
            Debug.WriteLine("");
            Log(sid, $"Request mode={mode} method={request.HttpMethod}");
            Log(sid, $"Target={targetUrl}");

            var range = request.Headers["Range"];
            if (!string.IsNullOrWhiteSpace(range))
            {
                Log(sid, $"Range={range}");
            }

            var userAgent = request.Headers["User-Agent"];
            if (!string.IsNullOrWhiteSpace(userAgent))
            {
                Log(sid, $"ClientUA={userAgent}");
            }
        }

        private static void Log(string sid, string message)
        {
            Debug.WriteLine(sid != null
                ? $"[StreamProxy][{sid}] {message}"
                : $"[StreamProxy] {message}");
        }

        private static void LogError(string sid, string message, Exception ex)
        {
            Debug.WriteLine(sid != null
                ? $"[StreamProxy][{sid}] ERROR {message}: {ex.GetType().Name} - {ex.Message}"
                : $"[StreamProxy] ERROR {message}: {ex.GetType().Name} - {ex.Message}");
        }

        public void Dispose()
        {
            _isRunning = false;
            StopAllRequests();
            _sessions.Clear();
            try { _listener.Stop(); } catch { }
            try { _httpClient.Dispose(); } catch { }
        }
    }
}
