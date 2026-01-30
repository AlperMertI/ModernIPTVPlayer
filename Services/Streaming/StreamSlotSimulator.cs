using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;
using System.IO;

namespace ModernIPTVPlayer.Services.Streaming
{
    public class StreamSlotSimulator
    {
        private static readonly Lazy<StreamSlotSimulator> _instance = new Lazy<StreamSlotSimulator>(() => new StreamSlotSimulator());
        public static StreamSlotSimulator Instance => _instance.Value;

        private const int READ_TIMEOUT_MS = 8000; // 8 seconds timeout
        private int _maxConnections = 1;
        private readonly ConcurrentDictionary<string, MultiStreamBuffer> _activeStreams = new ConcurrentDictionary<string, MultiStreamBuffer>();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _connectionTasks = new ConcurrentDictionary<string, CancellationTokenSource>();
        private readonly ConcurrentDictionary<string, DateTime> _waitingRequests = new ConcurrentDictionary<string, DateTime>();
        private readonly SemaphoreSlim _slotSemaphore;
        
        // Local Bridge
        private HttpListener _listener;
        private int _localPort;
        private bool _isListenerRunning;

        private StreamSlotSimulator()
        {
            _slotSemaphore = new SemaphoreSlim(_maxConnections);
            StartLocalBridge();
        }

        private void StartLocalBridge()
        {
            try
            {
                _listener = new HttpListener();
                // Find an available port or use a fixed one
                _localPort = 50050; 
                _listener.Prefixes.Add($"http://127.0.0.1:{_localPort}/stream/");
                _listener.Start();
                _isListenerRunning = true;
                _ = AcceptConnectionsAsync();
                Debug.WriteLine($"[SlotSimulator] Local Bridge started at http://127.0.0.1:{_localPort}/stream/");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SlotSimulator] Bridge Error: {ex.Message}");
            }
        }

        private async Task AcceptConnectionsAsync()
        {
            while (_isListenerRunning)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = HandleLocalRequestAsync(context);
                }
                catch { }
            }
        }

        private async Task HandleLocalRequestAsync(HttpListenerContext context)
        {
            DateTime startTime = DateTime.Now;
            string streamId = "unknown";
            try
            {
                string path = context.Request.Url.AbsolutePath;
                streamId = path.Split('/').Last();

                _waitingRequests.TryAdd(streamId, DateTime.Now);
                Debug.WriteLine($"[SlotSimulator:Bridge] GET /stream/{streamId} | UA: {context.Request.UserAgent}");

                if (_activeStreams.TryGetValue(streamId, out var buffer))
                {
                    Debug.WriteLine($"[SlotSimulator:Bridge] Starting stream transmission for {streamId}...");
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.ContentType = "video/mp2t";
                    
                    // CRITICAL: Must be chunked for live streams without a known length
                    context.Response.SendChunked = true;
                    
                    // Prevent any buffering or caching on the local bridge
                    context.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                    context.Response.Headers.Add("Pragma", "no-cache");
                    context.Response.Headers.Add("Expires", "0");

                    using (var output = context.Response.OutputStream)
                    {
                        byte[] chunk = ArrayPool<byte>.Shared.Rent(65536);
                        try
                        {
                            bool isFirstByte = true;
                            while (_isListenerRunning)
                            {
                                int read = 0;
                                try
                                {
                                    // Use a 5s timeout for heartbeat (less noisy than 2s)
                                    using (var heartbeatCts = new CancellationTokenSource(5000))
                                    {
                                    read = await buffer.ReadAsync(chunk, 0, chunk.Length, heartbeatCts.Token);
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    // if it's our heartbeat timeout (and not a global listener shutdown)
                                    if (_isListenerRunning)
                                    {
                                        // Reduced logging - heartbeat timeouts are expected
                                        // Debug.WriteLine($"[SlotSimulator:Bridge] Heartbeat Timeout (5s) for {streamId}. Sending Null Packet.");
                                        // Heartbeat Timeout: Send a null packet to keep the connection alive
                                        await output.WriteAsync(TsPacketParser.NullPacket, 0, TsPacketParser.NullPacket.Length);
                                        await output.FlushAsync();
                                        continue;
                                    }
                                    else
                                    {
                                        // Reduced logging
                                        // Debug.WriteLine($"[SlotSimulator:Bridge] Listener shutdown requested for {streamId}.");
                                    }
                                }

                                if (read == 0) break;
                                
                                if (isFirstByte)
                                {
                                    _waitingRequests.TryRemove(streamId, out _);
                                    isFirstByte = false;
                                }
                                
                                await output.WriteAsync(chunk, 0, read);
                                // await output.FlushAsync(); // REMOVED: Performance Killer! Let OS buffer.
                                
                                StreamDiagnostics.Instance.UpdateStat(streamId, h => h.TotalBytesSent += read);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SlotSimulator:Bridge] Stream {streamId} transmit interrupted: {ex.Message}");
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(chunk);
                        }
                    }
                }
                else
                {
                    Debug.WriteLine($"[SlotSimulator:Bridge] 404 - {streamId} not found.");
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SlotSimulator:Bridge] Internal Error: {ex.Message}");
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            finally
            {
                _waitingRequests.TryRemove(streamId, out _);
                try { context.Response.Close(); } catch { }
            }
        }

        public string GetVirtualUrl(string streamId) => $"http://127.0.0.1:{_localPort}/stream/{streamId}";

        public void Initialize(int maxConnections)
        {
            _maxConnections = maxConnections;
            // Adjust semaphore if needed
            // (In a real app, handle re-initialization carefully)
        }

        public MultiStreamBuffer RegisterStream(string streamId, string url)
        {
            var buffer = _activeStreams.GetOrAdd(streamId, id => new MultiStreamBuffer(id));
            _ = ManageStreamingLoop(streamId, url, buffer);
            return buffer;
        }

        private async Task ManageStreamingLoop(string streamId, string url, MultiStreamBuffer buffer)
        {
            while (true)
            {
                // Priority Check: Should we grab a slot now?
                double priority = CalculatePriority(buffer);
                
                // Adaptive: Request slot if priority is high OR (buffer is empty AND MPV is also starving)
                if (priority > 0.7 || (buffer.BufferLength == 0 && GetCombinedBuffer(streamId, buffer) < TARGET_BUFFER_SECONDS))
                {
                    // Protection: If stream just started, give it at least 3s to stabilize
                    var age = (DateTime.Now - buffer.CreatedAt).TotalSeconds;
                    if (age < 3 && buffer.BufferSeconds > TARGET_BUFFER_SECONDS) 
                    {
                         await Task.Delay(500);
                         continue;
                    }

                    await RequestSlotAndFillBuffer(streamId, url, buffer);
                }

                await Task.Delay(100); // Check 10 times per second for more responsive rotation
            }
        }

        private async Task RequestSlotAndFillBuffer(string streamId, string url, MultiStreamBuffer buffer)
        {
            if (_connectionTasks.ContainsKey(streamId)) return;

            var cts = new CancellationTokenSource();
            if (!_connectionTasks.TryAdd(streamId, cts)) return;

            try
            {
                await _slotSemaphore.WaitAsync(cts.Token);
                Debug.WriteLine($"[SlotSimulator] Slot ACQUIRED for {streamId} - Connecting...");

                // RETRY LOOP: Keep the slot if we just timed out. Only break if actually yielding.
                while (!cts.Token.IsCancellationRequested)
                {
                    bool shouldYield = false; // Flag to exit the Retry Loop

                    // Trigger clean resume logic (wait for next keyframe)
                    buffer.NotifyDiscontinuity();
                    
                    try
                    {
                        using (var response = await HttpHelper.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                Debug.WriteLine($"[SlotSimulator] Connected to {streamId} (200 OK)");
                                using (var stream = await response.Content.ReadAsStreamAsync(cts.Token))
                                {
                                byte[] chunk = ArrayPool<byte>.Shared.Rent(64 * 1024); // 64KB chunks
                                try
                                {
                                    long bytesFetchedInSession = 0;
                                    var startTime = DateTime.Now;
                                    double peakBuffer = 0;
                                    int zeroReadStrikes = 0; // STRIKE SYSTEM

                                    while (!cts.Token.IsCancellationRequested)
                                    {
                                        // TIMEOUT PROTECTION:
                                        // Adaptive: If starving (<5s buffer), timeout quickly (4s) to retry.
                                        // EXCEPTION: If we are DEDUPLICATING, we expect 0 buffer while catching up.
                                        // In that case, give full timeout (8s) to avoid killing the stream during recovery.
                                        int currentTimeout = (buffer.BufferSeconds < 5.0 && !buffer.IsDeduplicating) ? 4000 : READ_TIMEOUT_MS;
                                        int read = 0;
                                        using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                                        {
                                            readCts.CancelAfter(currentTimeout);
                                            try
                                            {
                                                read = await stream.ReadAsync(chunk, 0, chunk.Length, readCts.Token);
                                            }
                                            catch (OperationCanceledException ex)
                                            {
                                                if (cts.Token.IsCancellationRequested) throw; // Manual Cancel
                                                
                                                Debug.WriteLine($"[SlotSimulator] Read Timeout on {streamId} - Forcing Reconnect inside Slot. Source: {ex.Source}");
                                                break; // Break inner loop -> Retry Loop will reconnect
                                            }
                                        }

                                        if (read == 0) 
                                        {
                                            zeroReadStrikes++;
                                            Debug.WriteLine($"[SlotSimulator] Server closed connection for {streamId} (Read 0) - Strike {zeroReadStrikes}/3");
                                            
                                            if (zeroReadStrikes >= 3)
                                            {
                                                Debug.WriteLine($"[SlotSimulator] {streamId} failed 3 times. Yielding slot to reset.");
                                                shouldYield = true;
                                            }
                                            break; // Retry
                                        }
                                        zeroReadStrikes = 0; // Reset on success

                                        // if (read < 188) Debug.WriteLine($"[SlotSimulator:{streamId}] Fragmented Read: {read} bytes");

                                        buffer.AppendData(new ReadOnlySpan<byte>(chunk, 0, read));
                                        bytesFetchedInSession += read;

                                        // Track Peak for "Server Window Size" reporting
                                        if (buffer.BufferSeconds > peakBuffer)
                                        {
                                            peakBuffer = buffer.BufferSeconds;
                                            StreamDiagnostics.Instance.UpdateStat(streamId, h => h.ServerWindowSize = peakBuffer);
                                        }

                                        // Update Speed in Diagnostics
                                        var elapsed = (DateTime.Now - startTime).TotalSeconds;
                                        if (elapsed > 0.5)
                                        {
                                            double mbps = (bytesFetchedInSession * 8.0) / (elapsed * 1_000_000.0);
                                            StreamDiagnostics.Instance.UpdateStat(streamId, h => h.DownloadSpeedMbps = mbps);
                                        }

                                        // Check if we should release slot for a higher priority stream
                                        if (ShouldYieldSlot(buffer, bytesFetchedInSession, startTime))
                                        {
                                            // Reduced logging
                                            Debug.WriteLine($"[SlotSimulator] Yielding slot for {streamId} (Peak: {peakBuffer:F1}s).");
                                            shouldYield = true;
                                            break;
                                        }
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(chunk);
                                }
                            }
                        }
                        else
                        {
                         Debug.WriteLine($"[SlotSimulator] Connection Failed for {streamId}: {response.StatusCode}");
                             await Task.Delay(50, cts.Token); // Immediate retry
                         }
                    }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        Debug.WriteLine($"[SlotSimulator] Connection Error on {streamId} (Retrying inside slot): {ex.Message}");
                        await Task.Delay(50, cts.Token);
                        continue; 
                    }

                    if (shouldYield) break; // Exit the Retry Loop -> Release Semaphore
                    if (cts.Token.IsCancellationRequested) break;

                    Debug.WriteLine($"[SlotSimulator] Reconnecting {streamId} (Sticky Slot)...");
                    await Task.Delay(50, cts.Token); // Immediate reconnect
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    Debug.WriteLine($"[SlotSimulator] Task Cancelled for {streamId} (Expected)");
                }
                else
                    Debug.WriteLine($"[SlotSimulator] Error on {streamId}: {ex.Message} \nStack: {ex.StackTrace}");
            }
            finally
            {
                _slotSemaphore.Release();
                _connectionTasks.TryRemove(streamId, out _);
                Debug.WriteLine($"[SlotSimulator] Slot released for {streamId}.");
            }
        }

        private double GetCombinedBuffer(string streamId, MultiStreamBuffer buffer)
        {
            var health = StreamDiagnostics.Instance.GetHealth(streamId);
            double mpvCache = health?.MpvBufferSeconds ?? 0;
            return buffer.BufferSeconds + mpvCache;
        }

        private double CalculatePriority(MultiStreamBuffer buffer)
        {
            double combinedBuffer = GetCombinedBuffer(buffer.StreamId, buffer);

            // 0.0 (Low) to 1.0 (Critical)
            // Adaptive: Starving is < 5s for fast starts
            if (combinedBuffer < STARVATION_THRESHOLD_SECONDS) return 1.0; 
            if (combinedBuffer > MAX_BUFFER_SECONDS) return 0.05; // Sufficiently filled
            
            // Linear interpolation between starvation and max buffer
            double score = 1.0 - ((combinedBuffer - STARVATION_THRESHOLD_SECONDS) / (MAX_BUFFER_SECONDS - STARVATION_THRESHOLD_SECONDS));
            
            // Waiter Bonus: Massive priority boost for streams actively waiting for headers (to prevent MPV timeout)
            if (_waitingRequests.ContainsKey(buffer.StreamId))
            {
                score += 0.5;
            }

            // Hysteresis: If this stream currently has the slot, give it a 20% bonus 
            // to prevent rapid switching (stickiness)
            if (_connectionTasks.ContainsKey(buffer.StreamId))
            {
                score += 0.2;
            }

            return Math.Min(1.0, score);
        }

        // Adaptive Buffer Rotation Constants
        private const double SURVIVAL_THRESHOLD_SECONDS = 8.0;  // Minimum buffer before yielding (Increased from 5.0)
        private const double STARVATION_THRESHOLD_SECONDS = 5.0; // Another stream is starving below this
        private const double TARGET_BUFFER_SECONDS = 15.0;       // Target buffer for fast starts (Was 3.0)
        private const double MAX_BUFFER_SECONDS = 40.0;          // Maximum buffer before yielding (Was 5.0)
        private const double SURVIVAL_DWELL_MAX = 60.0;         // Max dwell time in survival mode (Was 45)
        private const double EMERGENCY_YIELD_DWELL = 25.0;      // Emergency yield after this dwell time (Was 8.0)
        private const double REGULAR_YIELD_DWELL = 30.0;        // Regular yield after this dwell time (Was 10.0)
        private const double REGULAR_YIELD_BUFFER = 25.0;       // Regular yield if buffer exceeds this
        private const double HARD_MAX_DWELL = 60.0;             // Hard max dwell time (Was 15.0)

        private bool ShouldYieldSlot(MultiStreamBuffer buffer, long bytesFetched, DateTime startTime)
        {
            var dwellTime = (DateTime.Now - startTime).TotalSeconds;
            
            // FIX: Only yield if a DIFFERENT stream is waiting at the bridge.
            // Previously, a stream would detect its own request and yield infinitely.
            bool anyoneElseWaiting = _waitingRequests.Keys.Any(id => id != buffer.StreamId); 
            double combinedBuffer = GetCombinedBuffer(buffer.StreamId, buffer);

            // SOCIALISTIC ROTATION: Heartbeats (v11) mean people are never strictly "waiting" at the bridge.
            // We must proactively check if someone else is starving in the background.
            bool anyoneStarving = false;
            string starvingId = null;
            double maxOtherBuffer = 0;
            foreach (var other in _activeStreams.Values)
            {
                if (other.StreamId == buffer.StreamId) continue;
                double otherBuffer = GetCombinedBuffer(other.StreamId, other);
                maxOtherBuffer = Math.Max(maxOtherBuffer, otherBuffer);
                // Adaptive: If another stream has < 5s buffer, it's starving
                if (otherBuffer < STARVATION_THRESHOLD_SECONDS)
                {
                    anyoneStarving = true;
                    starvingId = other.StreamId;
                }
            }

            // ADAPTIVE BUFFER ROTATION: "Survival Check"
            // If the current stream is barely surviving, YIELDING NOW = DEATH.
            // We must hold the slot until we have at least TARGET_BUFFER_SECONDS cushion.
            // Exception: If we've been hogging for > SURVIVAL_DWELL_MAX, yield anyway.
            if (combinedBuffer < SURVIVAL_THRESHOLD_SECONDS && dwellTime < SURVIVAL_DWELL_MAX)
            {
                 // Minimal logging - only log state changes, not every check
                 // This reduces console spam while still showing important transitions
                 return false;
            }

            // FAST START OPTIMIZATION: If another stream has 0 buffer and we're healthy, yield immediately
            // FIXED: Increased dwell requirement to 5.0s (was 2.0s) to allow meaningful buffer build-up
            if (anyoneStarving && combinedBuffer > TARGET_BUFFER_SECONDS && dwellTime > 5.0)
            {
                Debug.WriteLine($"[SlotSimulator:{buffer.StreamId}] Fast-Yielding for starved peer (Buffer: {combinedBuffer:F1}s > Target).");
                return true;
            }

            // EMERGENCY YIELD: If another stream is NEW or STARVING, yield after EMERGENCY_YIELD_DWELL.
            // FIXED: Only yield if we have SIGNIFICANT buffer (at least 5s) to survive the gap
            if ((anyoneElseWaiting || anyoneStarving) && dwellTime > EMERGENCY_YIELD_DWELL && combinedBuffer > 5.0)
            {
               Debug.WriteLine($"[SlotSimulator:{buffer.StreamId}] Emergency Yield (Dwell: {dwellTime:F1}s).");
               return true;
            }

            /* REVERTED FOR DEBUGGING: User wants to test yielding mechanism
            // FIX: If NO ONE else is waiting or starving, DO NOT YIELD.
            // This prevents single-stream cycling.
            if (!anyoneElseWaiting && !anyoneStarving)
            {
                return false;
            }
            */

            // REGULAR YIELD: If we have plenty of buffer, give others a chance (Socialism)
            // Only if someone else actually exists/needs it.
            if (combinedBuffer > REGULAR_YIELD_BUFFER && (anyoneElseWaiting || anyoneStarving))
            {
                return true;
            }

            // HARD LIMIT: If we've held the slot too long AND someone else is waiting
            if (dwellTime > HARD_MAX_DWELL && (anyoneElseWaiting || anyoneStarving))
            {
                return true;
            }

            return false;
        }


    }
}
