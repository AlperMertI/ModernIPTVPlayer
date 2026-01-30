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
            // StartLocalBridge(); // REMOVED: Lazy start in RegisterStream only!
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
                        buffer.AddSubscriber();
                        try
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
                        finally
                        {
                            buffer.RemoveSubscriber();
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
            // [RESTORE BRIDGE] If we stopped the bridge for Single Player, we must restart it now.
            if (!_isListenerRunning)
            {
                Debug.WriteLine("[SlotSimulator] Restarting Local Bridge for Multi-Stream...");
                StartLocalBridge();
            }

            var buffer = _activeStreams.GetOrAdd(streamId, id => new MultiStreamBuffer(id));
            _ = ManageStreamingLoop(streamId, url, buffer);
            return buffer;
        }

        public void StopStream(string streamId)
        {
            if (_activeStreams.ContainsKey(streamId))
            {
                Debug.WriteLine($"[SlotSimulator:{streamId}] Explicit Stop Requested.");
                
                // 1. Remove from Active List (Stops new loops)
                _activeStreams.TryRemove(streamId, out _);

                // 2. CANCEL ACTIVE DOWNLOAD (Stops current loop)
                if (_connectionTasks.TryGetValue(streamId, out var cts))
                {
                    try { cts.Cancel(); } catch { }
                    Debug.WriteLine($"[SlotSimulator:{streamId}] Cancellation Token Triggered.");
                }
            }
        }

        public void StopAll()
        {
            Debug.WriteLine("[SlotSimulator] Stopping ALL streams & Bridge (Single Player Enforcement)...");
            foreach (var key in _activeStreams.Keys.ToList())
            {
                StopStream(key);
            }

            // Also stop the Bridge Listener to clear logs/ports
            try
            {
                if (_listener != null && _listener.IsListening)
                {
                    _isListenerRunning = false;
                    _listener.Stop();
                    // Don't dispose, we might need it later? 
                    // Actually, if we stop it, we need to re-init to start again.
                    // But for Single Player stability, stopping is safer.
                    // We'll let lazy re-init handle it or manual Start if needed.
                    Debug.WriteLine("[SlotSimulator] Local Bridge Stopped.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SlotSimulator] Error stopping bridge: {ex.Message}");
            }
        }

        private async Task ManageStreamingLoop(string streamId, string url, MultiStreamBuffer buffer)
        {
            try
            {
                while (true)
                {
                    // EXPLICIT CHECK: If stream was removed via StopStream(), exit immediately.
                    if (!_activeStreams.ContainsKey(streamId))
                    {
                        Debug.WriteLine($"[SlotSimulator:{streamId}] Stream removed from active list. Stopping Loop.");
                        break;
                    }

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

                    // IDLE CHECK: If no subscribers for 10 seconds, kill the stream loop.
                    if (buffer.SubscriberCount == 0)
                    {
                        double secondsIdle = (DateTime.Now - buffer.LastSubscriberExit).TotalSeconds;
                        // Give newly created streams a grace period of 10s too
                        if (secondsIdle > 10.0 && (DateTime.Now - buffer.CreatedAt).TotalSeconds > 10.0)
                        {
                            Debug.WriteLine($"[SlotSimulator:{streamId}] Idle Timeout (No Subscribers for {secondsIdle:F1}s). Stopping Download Loop.");
                            break; // Exit Loop -> Finally Clean Up
                        }
                    }

                    await Task.Delay(100); // Check 10 times per second for more responsive rotation
                }
            }
            catch (Exception ex)
            {
                 Debug.WriteLine($"[SlotSimulator] Manager Loop Crashed for {streamId}: {ex.Message}");
            }
            finally
            {
                 // CLEANUP: Remove stats when stream is properly dead
                 StreamDiagnostics.Instance.RemoveStat(streamId);
                 _activeStreams.TryRemove(streamId, out _);
                 Debug.WriteLine($"[SlotSimulator] Manager Loop Ended for {streamId}");
            }
        }

        // Returns TRUE if yielded voluntarily, FALSE if error/shutdown
        private async Task<bool> RequestSlotAndFillBuffer(string streamId, string url, MultiStreamBuffer buffer)
        {
            if (_connectionTasks.ContainsKey(streamId)) return false;

            var cts = new CancellationTokenSource();
            if (!_connectionTasks.TryAdd(streamId, cts)) return false;
            
            bool voluntaryYield = false;

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
                                    // NETWORK TUNING: Use 256KB buffer (4x standard) to "trick" throttled servers into sending more data.
                                    byte[] chunk = ArrayPool<byte>.Shared.Rent(256 * 1024); 
                                    try
                                    {
                                        long bytesFetchedInSession = 0;
                                        var startTime = DateTime.Now;
                                        double peakBuffer = 0;
                                        int zeroReadStrikes = 0; // STRIKE SYSTEM

                                        // Diagnostics
                                        long totalReadTimeMs = 0;
                                        int readCount = 0;

                                        while (!cts.Token.IsCancellationRequested)
                                        {
                                            // TIMEOUT PROTECTION:
                                            // Adaptive: If starving (<5s buffer), timeout quickly (4s) to retry.
                                            // EXCEPTION: If we are DEDUPLICATING, we expect 0 buffer while catching up.
                                            // In that case, give full timeout (8s) to avoid killing the stream during recovery.
                                            int currentTimeout = (buffer.BufferSeconds < 5.0 && !buffer.IsDeduplicating) ? 4000 : READ_TIMEOUT_MS;
                                            int read = 0;
                                            
                                            var sw = Stopwatch.StartNew();
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
                                            sw.Stop();
                                            totalReadTimeMs += sw.ElapsedMilliseconds;
                                            readCount++;

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

                                            buffer.AppendData(new ReadOnlySpan<byte>(chunk, 0, read));
                                            bytesFetchedInSession += read;

                                            // Track Peak for "Server Window Size" reporting
                                            if (buffer.BufferSeconds > peakBuffer)
                                            {
                                                peakBuffer = buffer.BufferSeconds;
                                                StreamDiagnostics.Instance.UpdateStat(streamId, h => h.ServerWindowSize = peakBuffer);
                                            }

                                            // Update Speed & Diagnostics
                                            var elapsed = (DateTime.Now - startTime).TotalSeconds;
                                            if (elapsed > 0.5)
                                            {
                                                double mbps = (bytesFetchedInSession * 8.0) / (elapsed * 1_000_000.0);
                                                
                                                // BOTTLENECK HEURISTICS
                                                // Avg Chunk Size
                                                double avgChunk = (double)bytesFetchedInSession / readCount;
                                                double utilization = avgChunk / (double)chunk.Length;
                                                
                                                // If we are reading tiny chunks (Utilization < 20%), we are waiting for data creation (Live Edge).
                                                // If we are reading big chunks (Utilization > 50%), we are limited by transfer speed (Network/Server).
                                                string bottleneck = utilization < 0.2 ? "LIVE-EDGE (Source Latency)" : "BANDWIDTH (Network/Server)";

                                                StreamDiagnostics.Instance.UpdateStat(streamId, h => 
                                                {
                                                    h.DownloadSpeedMbps = mbps;
                                                    h.DebugInfo = $"Limit: {bottleneck} | Fill: {utilization*100:F0}%";
                                                });
                                            }

                                            // Check if we should release slot for a higher priority stream
                                            if (ShouldYieldSlot(buffer, bytesFetchedInSession, startTime))
                                            {
                                                // Reduced logging
                                                Debug.WriteLine($"[SlotSimulator] Yielding slot for {streamId} (Peak: {peakBuffer:F1}s).");
                                                shouldYield = true;
                                                voluntaryYield = true;
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
            return voluntaryYield;
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

        // Greedy Buffer Rotation Constants
        private const double SURVIVAL_THRESHOLD_SECONDS = 12.0;  // Minimum buffer before yielding (Increased for safety)
        private const double STARVATION_THRESHOLD_SECONDS = 8.0; // Another stream is starving below this (Increased from 5s for early rescue)
        private const double TARGET_BUFFER_SECONDS = 30.0;       // Target buffer for "Fortress" (Was 15.0)
        private const double MAX_BUFFER_SECONDS = 300.0;         // Maximum buffer (Effectively Uncapped)
        private const double SURVIVAL_DWELL_MAX = 60.0;         // Max dwell time in survival mode
        private const double EMERGENCY_YIELD_DWELL = 25.0;      // Emergency yield after this dwell time
        private const double REGULAR_YIELD_DWELL = 30.0;        // Regular yield after this dwell time
        private const double REGULAR_YIELD_BUFFER = 20.0;       // Regular yield if buffer exceeds this (Increased to 20s for 3-stream safety)
        private const double HARD_MAX_DWELL = 300.0;            // Hard max (align with max buffer)

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

            // YIELD PROTECTION:
            // 1. NEVER yield if we are catching up (Deduplicating). We need to finish the job.
            if (buffer.IsDeduplicating) return false;

            // DYNAMIC TARGET: If we are Source Limited (Live Edge), we cannot build 30s buffer quickly.
            // If we try to hold for 30s, we will starve the other stream.
            // DETECTED: If fill rate is low (Source Limit), lower the target to 12s to allow rotation.
            // NOTE: We rely on the caller passing 'isSourceLimited' or we infer it from recent stats?
            // Actually, let's look up the stats.
            var health = StreamDiagnostics.Instance.GetHealth(buffer.StreamId);
            bool isSourceLimited = health != null && health.DebugInfo != null && health.DebugInfo.Contains("LIVE-EDGE");

            // Adaptive Target: 30s for Bandwidth Limited (Bursty), 20s for Source Limited (Trickle)
            // Increased Source Limited target from 12s to 20s to survive 3-stream rotation
            double dynamicTarget = isSourceLimited ? 20.0 : TARGET_BUFFER_SECONDS;

            // 2. GREEDY HOLD: Do not yield until we hit our TARGET, 
            //    UNLESS it's a desperate emergency (someone else < 5s).
            if (combinedBuffer < dynamicTarget && !anyoneStarving) return false;

            // FAST START OPTIMIZATION: If another stream has 0 buffer and we're healthy, yield immediately
            // FIXED: Only yield if we are significantly above our dynamic target
            // FAST START OPTIMIZATION: If another stream has 0 buffer and we're healthy, yield immediately
            // FIXED: Only yield if we are significantly above our dynamic target
            if (anyoneStarving && combinedBuffer > dynamicTarget && dwellTime > 5.0)
            {
                Debug.WriteLine($"[SlotSimulator:{buffer.StreamId}] Fast-Yielding for starved peer (Buffer: {combinedBuffer:F1}s > Target).");
                return true;
            }

            // [CRITICAL RESCUE] If someone is dying (<8s) and we are safe (>10s), yield IMMEDIATELY.
            // Ignore Start Dwell or Target dwell. Saving a life is priority.
            if (anyoneStarving && combinedBuffer > 10.0 && dwellTime > 2.0)
            {
                Debug.WriteLine($"[SlotSimulator:{buffer.StreamId}] CRITICAL RESCUE Yield (Buffer: {combinedBuffer:F1}s).");
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
                Debug.WriteLine($"[SlotSimulator:{buffer.StreamId}] Regular Yield Triggered (Buffer: {combinedBuffer:F1}s > {REGULAR_YIELD_BUFFER}).");
                return true;
            }

            // [DEBUG] Log rejection causes for high buffers
            if (combinedBuffer > 10.0 && anyoneStarving)
            {
                 // Trace WHY we are not yielding if we are rich and others are poor
                 // Reasons: Dedupe?
                 if (buffer.IsDeduplicating) Debug.WriteLine($"[SlotSimulator:{buffer.StreamId}] NOT Yielding (Rich but Deduplicating). Gap: {combinedBuffer:F1}s");
                 // else Debug.WriteLine($"[SlotSimulator:{buffer.StreamId}] NOT Yielding (Rich but unknown reason?). Target: {dynamicTarget}");
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
