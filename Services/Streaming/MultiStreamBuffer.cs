using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ModernIPTVPlayer.Services.Streaming
{
    public enum FilterState
    {
        Normal,
        WaitForKey,
        CleanRasl,
        CollectingHeaders  // NEW: Buffering parameter sets before resume
    }
    /// <summary>
    /// Highly optimized block-based buffer for multi-stream rotation.
    /// Eliminates memory copies and provides O(1) performance for 4K streams.
    /// </summary>
    public class MultiStreamBuffer : Stream
    {
        private readonly string _streamId;
        private readonly Channel<byte[]> _channel;
        private readonly object _lock = new object();

        // Stats tracking
        private long _totalBytesReceived;
        private long _totalBytesRead;
        private long _currentBufferBytes;
        
        public DateTime CreatedAt { get; } = DateTime.Now;
        public long TotalBytesReceived => _totalBytesReceived;
        public long TotalBytesRead => _totalBytesRead;
        public long BufferLength => _currentBufferBytes;
        public string StreamId => _streamId;

        public double BufferSeconds { get; private set; }
        public long CurrentBitrate { get; set; }
        public bool IsDeduplicating => _isDeduplicating;

        private bool _discontinuityRequested = true;
        private FilterState _filterState = FilterState.WaitForKey;
        private bool _isInsideUndecodableFrame = false;
        private HashSet<int> _pidsSeenSinceResume = new HashSet<int>();
        private long _maxVideoPts = 0; // Tracks the highest Video PTS (Time Base) for Audio Gating
        private bool _isDeduplicating = false; // Smart Stream Trimming State
        private long _dedupeDropBytes = 0;
        private byte[] _alignBuffer = new byte[TsPacketParser.PacketSize];
        private int _alignFill = 0;
        private int _debugLogLimit = 0; // Deep Trace Logger Limit

        // PCR tracking
        private ushort? _referencePid;
        private long? _lastAppendedPcr;
        private long? _lastReadPcr;

        private readonly Dictionary<ushort, byte> _lastCcPerPid = new Dictionary<ushort, byte>();
        private DateTime _lastMetricUpdate = DateTime.MinValue;

        // Parameter set tracking for HEVC initialization
        private bool _hasVps = false;
        private bool _hasSps = false;
        private bool _hasPps = false;
        private bool _hasKeyframe = false;
        private List<byte[]> _parameterSetBuffer = new List<byte[]>();
        private DateTime _parameterSetWaitStart = DateTime.MinValue;

        // Audio grace period tracking
        private int _packetsAfterResume = 0;
        private const int AUDIO_GRACE_PERIOD = 100; // Allow 100 packets before filtering
        
        // Adaptive buffer management constants
        private const double TARGET_BUFFER_SECONDS = 3.0; // Optimal buffer for live TV
        private const double MAX_BUFFER_SECONDS = 5.0; // Max buffer before stopping fetch
        private const int MAX_PARAMETER_SET_BUFFER = 20; // Reduced from 50 for faster start
        private const int PARAMETER_SET_TIMEOUT_MS = 500; // 500ms timeout for fast start

        public MultiStreamBuffer(string streamId)
        {
            _streamId = streamId;
            _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

        public void AppendData(ReadOnlySpan<byte> data)
        {
            // Process buffer alignment
            ReadOnlySpan<byte> workData = data;
            int i = 0;

            if (_alignFill > 0)
            {
                int needed = TsPacketParser.PacketSize - _alignFill;
                if (data.Length >= needed)
                {
                    data.Slice(0, needed).CopyTo(_alignBuffer.AsSpan(_alignFill));
                    AppendPacket(_alignBuffer);
                    _alignFill = 0;
                    i = needed; // Advance i by 'needed'
                    workData = data; // workData is reset implicitly by indexing below
                }
                else
                {
                    data.CopyTo(_alignBuffer.AsSpan(_alignFill));
                    _alignFill += data.Length;
                    return;
                }
            }

            // Correct loop: data is 'workData', but we need to start from 'i'
            if (i < workData.Length && workData[i] != TsPacketParser.SyncByte)
            {
                 Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [AlignWarn] Lost sync after buffer fill! Expected 0x47 at {i}, got {workData[i]:X2}");
            }

            while (i <= workData.Length - TsPacketParser.PacketSize)
            {
                if (workData[i] == TsPacketParser.SyncByte)
                {
                    ReadOnlySpan<byte> packet = workData.Slice(i, TsPacketParser.PacketSize);
                    AppendPacket(packet);
                    i += TsPacketParser.PacketSize;
                }
                else
                {
                    i++;
                }
            }

            // Store remainder
            _alignFill = workData.Length - i;
            if (_alignFill > 0)
            {
                workData.Slice(i).CopyTo(_alignBuffer);
            }
        }

        private void InjectDiscontinuityPacket(int pid, byte continuityCounter)
        {
            // FIX: Skip non-PES PIDs to avoid corrupting SI tables
            // PID 0: PAT, PID 1: CAT, PID 2: TSDT, PID 16: NIT, PID 17: DVB-SI (SDT/EIT)
            // PID 18-31: Reserved, PID 0x1FFF: Null, PID 4096: Reserved
            // Only inject discontinuities for actual media PIDs (video/audio)
            if (pid <= 0x20 || pid == 0x1FFF || pid == 4096)
            {
                // Reduced logging - system PID skips are expected
                // Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [DetailedCC] SKIPPING discontinuity for system PID: {pid}");
                return;
            }

            byte[] buffer = new byte[TsPacketParser.PacketSize];

            // TS Header
            buffer[0] = 0x47; // Sync Byte
            // FIX: PUSI must be 0 for Adaptation Field Only packets!
            buffer[1] = (byte)((pid >> 8) & 0x1F); // PayloadStart=0, Priority=0, PID-Hi
            buffer[2] = (byte)(pid & 0xFF); // PID-Lo
            // Adaptation=1 (field only), Continuity should be (Target - 1) so that Next(Target) is a valid increment
            byte injectedCc = (byte)((continuityCounter - 1) & 0x0F);
            buffer[3] = (byte)(0x20 | injectedCc); 
            
            // Reduced logging - only log for non-system PIDs
            // Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [DetailedCC] Injecting Discontinuity for PID: {pid}, TargetCC: {continuityCounter}, InjectCC: {injectedCc}");

            // Adaptation Field
            buffer[4] = 183; // Length: 188 - 4 (Header) - 1 (Length Byte) = 183 bytes of filler
            buffer[5] = 0x80; // Discontinuity Indicator = 1

            // Stuffing (0xFF)
            for (int k = 6; k < 188; k++)
            {
                buffer[k] = 0xFF;
            }

            _channel.Writer.TryWrite(buffer);
            Interlocked.Add(ref _totalBytesReceived, TsPacketParser.PacketSize);
            Interlocked.Add(ref _currentBufferBytes, TsPacketParser.PacketSize);
        }

        /// <summary>
        /// Returns the current buffer state for diagnostics
        /// </summary>
        public (double BufferSeconds, double MpvBuffer, double CombinedBuffer, string Status) GetBufferState()
        {
            var health = StreamDiagnostics.Instance.GetHealth(_streamId);
            double mpvBuffer = health?.MpvBufferSeconds ?? 0;
            double combined = BufferSeconds + mpvBuffer;
            
            string status;
            if (combined > 15) status = "Healthy";
            else if (combined > 5) status = "Buffering";
            else status = "Starving";
            
            return (BufferSeconds, mpvBuffer, combined, status);
        }


        private void AppendPacket(ReadOnlySpan<byte> packet)
        {
            if (TsPacketParser.TryParseHeader(packet, out var header))
            {
                // DISCONTINUITY FILTER: 
                // We MUST wait for an I-Frame for media data, 
                // BUT we should ALWAYS allow System PIDs (PAT, PMT, SDT) through!
                bool isSystemPid = header.Pid < 0x20 || header.Pid == 0x1FFF;
                bool isVideo = _referencePid.HasValue ? header.Pid == _referencePid.Value : !isSystemPid;
                
                // DEEP PACKET INSPECTION & SMART DEDUPLICATION (TRIMMING)
                
                // 1. Deduplication (Only for Video/Audio payloads)
                // We filter based on PUSI (Payload Unit Start Indicator) which generally aligns with PES headers and PTS.
                // We rely on getting a reliable PTS to make the decision.
                if ((isVideo || !isSystemPid) && header.HasPayload && header.PayloadUnitStartIndicator)
                {
                    if (TsPacketParser.TryGetPts(packet, header, out long pts))
                    {
                        // Check for Rollover (33-bit wrap ~26 hours)
                        long diff = pts - _maxVideoPts;
                        const long ROLLOVER_THRESHOLD = 0x1FFFFFFFF / 2; // ~13 hours
                        
                        bool isNewer = diff > 0;
                        if (diff < -ROLLOVER_THRESHOLD) isNewer = true; // Wrapped around forward
                        else if (diff > ROLLOVER_THRESHOLD) isNewer = false; // Wrapped around backward (weird but possible)

                        // ADAPTIVE DEDUPLICATION:
                        // If we are starving (< 1.0s) and deduplication is blocking us, BYPASS IT.
                        // It is better to have a small visual glitch (repeat frames) than to freeze.
                        if (_isDeduplicating && BufferSeconds < 1.0)
                        {
                            Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [DEDUPE-EMERGENCY] Low Buffer ({BufferSeconds:F2}s). Bypassing Dedupe to prevent starvation.");
                            _isDeduplicating = false;
                            _dedupeDropBytes = 0;
                            _maxVideoPts = pts; // Reset anchor to current
                        }

                        // Strict Deduplication Mode
                        if (_isDeduplicating)
                        {
                            // SAFETY HATCH: If we drop too much (>32MB), assume stream reset and force resume
                            if (_dedupeDropBytes > 32 * 1024 * 1024)
                            {
                                Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [DEDUPE-WARN] Drop limit exceeded (32MB). Forcing resume at PTS:{pts}.");
                                _isDeduplicating = false;
                                _dedupeDropBytes = 0;
                                _maxVideoPts = pts; // Reset anchor
                                return; // Allow this packet
                            }

                            bool shouldResume = false;

                             if (isNewer)
                            {
                                // CRITICAL FIX: Only resume on a KEYFRAME (or Param Set)
                                // Resuming on a P-Frame after Discontinuity causes decoder freeze/corruption.
                                bool isLikelyKeyframe = false;
                                int offset = TsPacketParser.GetPayloadOffset(packet, header);
                                if (offset != -1)
                                {
                                    // Scan for Start Code 00 00 01
                                    for (int k = offset; k < packet.Length - 4; k++)
                                    {
                                        if (packet[k] == 0 && packet[k+1] == 0 && packet[k+2] == 1)
                                        {
                                            byte nalByte = packet[k+3];
                                            // HEVC Check (Types 16-23 IRAP, 32-34 VPS/SPS/PPS)
                                            int hevcType = (nalByte >> 1) & 0x3F;
                                            if ((hevcType >= 16 && hevcType <= 23) || (hevcType >= 32 && hevcType <= 34)) 
                                            {
                                                isLikelyKeyframe = true;
                                            }
                                            
                                            // H.264 Check (Type 5 IDR, 7 SPS, 8 PPS) - only if not already identified
                                            int h264Type = nalByte & 0x1F;
                                            if (h264Type == 5 || h264Type == 7 || h264Type == 8) 
                                            {
                                                isLikelyKeyframe = true;
                                            }

                                            if (isLikelyKeyframe) break;
                                        }
                                    }
                                }

                                if (isLikelyKeyframe)
                                {
                                    shouldResume = true;
                                    Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [DEDUPE-END] Caught up at KEYFRAME PTS:{pts} (Gap: {diff/90000.0:F2}s). Resuming.");
                                }
                                else
                                {
                                    // ENABLED LOGGING for Skipped P-Frames (Diagnostic)
                                    // Log every ~0.5MB
                                    long prevBytes = _dedupeDropBytes;
                                    _dedupeDropBytes += TsPacketParser.PacketSize; // Count skipped P-frames in dropped stats
                                    
                                    if ((_dedupeDropBytes / 524288) > (prevBytes / 524288)) 
                                    {
                                        Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [DEDUPE-SKIP] Newer P-Frame PTS:{pts}. Waiting for Keyframe... (Dropped: {_dedupeDropBytes/1024}KB)");
                                    }
                                    if ((_dedupeDropBytes / 524288) > (prevBytes / 524288)) 
                                    {
                                        // Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [DEDUPE-SKIP] Newer P-Frame PTS:{pts}. Waiting for Keyframe... (Dropped: {_dedupeDropBytes/1024}KB)");
                                    }
                                    return; // SKIP Packet
                                }
                            }

                            if (shouldResume)
                            {
                                _isDeduplicating = false;
                                _dedupeDropBytes = 0;
                            }
                            else
                            {
                                // Old Data DROP
                                long prevBytes = _dedupeDropBytes;
                                _dedupeDropBytes += TsPacketParser.PacketSize;
                                
                                // Log every ~1MB
                                if ((_dedupeDropBytes / 1048576) > (prevBytes / 1048576))
                                {
                                    Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [DEDUPE-DROP] Dropping... Total: {_dedupeDropBytes/1024}KB (PTS:{pts})");
                                }
                                return; // SILENTLY DROP PACKET
                            }
                        }
                        
                        // Track Max PTS for *Next* Reconnect
                        // Only update if it's actually newer (and we are not dropping)
                        if (isNewer || _maxVideoPts == 0)
                        {
                            _maxVideoPts = pts;
                        }
                    }
                }
                else if (_isDeduplicating && !isSystemPid)
                {
                    // Drop all non-PUSI media packets while deduplicating
                    // Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [DEDUPE-DROP-NON-PUSI] PID:{header.Pid} CC:{header.ContinuityCounter}");
                    return; 
                }

                if (_discontinuityRequested && !isSystemPid)
                {
                    // NEW: Collect parameter sets before resuming
                    if (_filterState != FilterState.CollectingHeaders)
                    {
                        _filterState = FilterState.CollectingHeaders;
                        _parameterSetWaitStart = DateTime.Now;
                        _parameterSetBuffer.Clear();
                        _hasVps = false;
                        _hasSps = false;
                        _hasPps = false;
                        _hasKeyframe = false;
                    }

                    // Buffer packets while collecting parameter sets
                    if (_filterState == FilterState.CollectingHeaders)
                    {
                        // Check for timeout (500ms max wait for fast start)
                        // Most IPTV streams don't have in-band parameter sets, so we timeout quickly
                        if ((DateTime.Now - _parameterSetWaitStart).TotalMilliseconds > PARAMETER_SET_TIMEOUT_MS)
                        {
                            _discontinuityRequested = false;
                            _filterState = FilterState.WaitForKey;
                            // Flush buffered packets
                            foreach (var bufferedPacket in _parameterSetBuffer)
                            {
                                ProcessBufferedPacket(bufferedPacket);
                            }
                            _parameterSetBuffer.Clear();
                        }
                        else
                        {
                                // Detect parameter sets and keyframes
                                // Buffer ALL packets with PUSI during collection, not just I-Frames
                                if (header.PayloadUnitStartIndicator)
                                {
                                    int offset = TsPacketParser.GetPayloadOffset(packet, header);
                                    if (offset != -1)
                                    {
                                        for (int k = offset; k < packet.Length - 4; k++)
                                        {
                                            if (packet[k] == 0 && packet[k+1] == 0 && packet[k+2] == 1)
                                            {
                                                byte nalByte = packet[k+3];
                                                
                                                // HEVC
                                                int hevcType = (nalByte >> 1) & 0x3F;
                                                if (hevcType == 32) _hasVps = true;
                                                if (hevcType == 33) _hasSps = true;
                                                if (hevcType == 34) _hasPps = true;
                                                if ((hevcType >= 16 && hevcType <= 23)) _hasKeyframe = true; 
                                                
                                                // H.264
                                                int h264Type = nalByte & 0x1F;
                                                if (h264Type == 7) _hasSps = true;
                                                if (h264Type == 8) _hasPps = true;
                                                if (h264Type == 5) _hasKeyframe = true;

                                                if (_hasKeyframe || _hasSps || _hasPps) break;
                                            }
                                        }
                                    }
                                    
                                    // Buffer this packet regardless of type
                                    byte[] buffered = new byte[TsPacketParser.PacketSize];
                                    packet.CopyTo(buffered);
                                    _parameterSetBuffer.Add(buffered);

                                    // Check if we have complete set (SPS + PPS + Keyframe required, VPS optional)
                                    // OPTIMIZATION: If we are RESUMING (MaxPts > 0), we assume decoder has headers,
                                    // so just a Keyframe is enough to flush and start playing!
                                    bool isResuming = _maxVideoPts > 0;
                                    bool hasRequired = (_hasSps && _hasPps && _hasKeyframe);
                                    if (isResuming && _hasKeyframe) hasRequired = true;

                                    if (hasRequired || _parameterSetBuffer.Count > MAX_PARAMETER_SET_BUFFER)
                                    {
                                        _discontinuityRequested = false;
                                        _filterState = FilterState.Normal; // FIX: Switch to Normal immediately! Do not wait for Keyframe again (we just found it).
                                        _referencePid = header.Pid;
                                        // _maxVideoPts = 0; // DO NOT RESET THIS ANYMORE - Used for Deduplication
                                        _pidsSeenSinceResume.Clear();
                                        _debugLogLimit = 500;
                                        _packetsAfterResume = 0; // Reset grace period counter

                                        // Flush all buffered packets
                                        foreach (var bufferedPacket in _parameterSetBuffer)
                                        {
                                            ProcessBufferedPacket(bufferedPacket);
                                        }
                                        _parameterSetBuffer.Clear();
                                        return; // Current packet already processed
                                    }
                                }
                                else
                                {
                                    // Buffer non-PUSI packets too to ensure we don't lose data
                                    byte[] buffered = new byte[TsPacketParser.PacketSize];
                                    packet.CopyTo(buffered);
                                    _parameterSetBuffer.Add(buffered);
                                }
                                return; // Continue buffering
                        }
                    }
                }


                // DYNAMIC AUDIO FILTER (The Fix):
                // We rely on _referencePid being latching on the first PUSI video packet.
                // 1. If we haven't found Video yet (_referencePid == null), Drop Audio.
                // 2. If we are still "Waiting for Keyframe", Drop Audio (avoid audio playing while video is black/adjusting).
                // This ensures Audio only enters the pipeline AFTER Video is stable and established.
                if (!isSystemPid)
                {
                    // CASE 1: VIDEO PACKET
                    if (_referencePid.HasValue && header.Pid == _referencePid.Value)
                    {
                         // Video Max PTS tracked above in Deduplication block
                    }
                    // CASE 2: AUDIO / OTHER
                    else if (_referencePid.HasValue && header.Pid != _referencePid.Value)
                    {
                        // SMART AUDIO GATE: Filter Toxic Audio (with Grace Period)
                        if (header.PayloadUnitStartIndicator && TsPacketParser.TryGetPts(packet, header, out long audioPts))
                        {
                            // Grace Period: Allow audio to flow freely for first N packets after resume
                            if (_packetsAfterResume < AUDIO_GRACE_PERIOD)
                            {
                                // Allow through during grace period
                            }
                            else
                            {
                                // 1. Warm-up: Drop Audio if Video hasn't established a baseline yet.
                                if (_maxVideoPts == 0) return;

                                // 2. Toxic Filter: Drop Audio that is OLDER than Video (Backward Jump).
                                // INCREASED Tolerance: 180,000 ticks = 2.0 seconds (was 0.5s)
                                if (audioPts < _maxVideoPts - 180000)
                                {
                                    return; 
                                }
                            }
                        }
                    }
                    // CASE 3: UNKNOWN STREAM (Pre-Reference) -> Drop until Video is found
                    else if (!_referencePid.HasValue)
                    {
                        // Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [DROP-PRE-REF] PID:{header.Pid} (No Ref Yet)");
                        return;
                    }
                }

                // PER-PID DISCONTINUITY INJECTION
                // For every PID we see after a resume, we must inject a "Discontinuity" packet first.
                // This ensures that Video, Audio, and Metadata decoders are ALL flushed.
                if (!_discontinuityRequested && !_pidsSeenSinceResume.Contains(header.Pid))
                {
                    // FIX: Only allow Start of PES (PUSI) to trigger "First Packet" logic for media tracks.
                    if (header.PayloadUnitStartIndicator || isSystemPid) 
                    {
                        _pidsSeenSinceResume.Add(header.Pid);
                        InjectDiscontinuityPacket(header.Pid, header.ContinuityCounter);

                        // CRITICAL: Force reset of CC tracking for this PID. 
                        _lastCcPerPid.Remove(header.Pid);
                    }
                    else
                    {
                        return;
                    }
                }

                int payloadStart = 4;
                if (header.HasAdaptationField)
                {
                    payloadStart += (1 + header.AdaptationFieldLength);
                }

                // Copy to block immediately so we can modify it in place
                byte[] block = new byte[TsPacketParser.PacketSize];
                packet.CopyTo(block);

                // 2. CC Check (Network Health)
                if (_lastCcPerPid.TryGetValue(header.Pid, out byte lastInCc))
                {
                    byte nextCc = lastInCc;
                    if (header.HasPayload) nextCc = (byte)((lastInCc + 1) & 0x0F);
                    
                    if ((header.ContinuityCounter & 0x0F) != nextCc)
                    {
                         if (header.HasPayload)
                         {
                             // Debug.WriteLine($"[CC_ERROR:{_streamId}] PID:{header.Pid} Expected:{nextCc} Got:{header.ContinuityCounter}");
                         }
                    }
                    _lastCcPerPid[header.Pid] = header.ContinuityCounter;
                }
                else
                {
                    _lastCcPerPid[header.Pid] = header.ContinuityCounter;
                }

                /* NAL SCRUBBING DISABLED */

                _channel.Writer.TryWrite(block);
                Interlocked.Add(ref _currentBufferBytes, TsPacketParser.PacketSize);
                Interlocked.Add(ref _totalBytesReceived, TsPacketParser.PacketSize);
                
                // Increment grace period counter
                if (_packetsAfterResume < AUDIO_GRACE_PERIOD)
                {
                    _packetsAfterResume++;
                }
                
                UpdateMetrics(header);
            }
        }

        public void NotifyDiscontinuity()
        {
            _discontinuityRequested = true;
            _filterState = FilterState.WaitForKey;
            _isInsideUndecodableFrame = false;
            
            // Enable Smart Deduplication
            // We do NOT reset _maxVideoPts here. We use it to filter the new incoming stream.
            if (_maxVideoPts > 0)
            {
                 _isDeduplicating = true;
                 _dedupeDropBytes = 0;
                 Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] [DEDUPE-START] Resume detected. Filtering overlap <= PTS:{_maxVideoPts}...");
            }
            
            _pidsSeenSinceResume.Clear();
            _lastCcPerPid.Clear(); // Allow new stream to establish its own CC sequence
            _referencePid = null;
            _lastAppendedPcr = null;
            _lastReadPcr = null;
            _alignFill = 0; // CRITICAL: Reset alignment buffer to avoid stitching old stream tail with new stream head
            _debugLogLimit = 0; // Stop tracing until next resume
            
            // Reset parameter set tracking
            _hasVps = false;
            _hasSps = false;
            _hasPps = false;
            _hasKeyframe = false;
            _parameterSetBuffer.Clear();
            _parameterSetWaitStart = DateTime.MinValue;
            
            // Reset audio grace period
            _packetsAfterResume = 0;
            
            // Minimal logging - discontinuity is expected during slot rotation
             Debug.WriteLine($"[MultiStreamBuffer:{_streamId}] Discontinuity notified. Mode:{(_isDeduplicating ? "DEDUPE" : "NORMAL")}");
        }

        private void UpdateMetrics(TsPacketParser.TsHeader header)
        {
            if (header.Pcr.HasValue)
            {
                if (!_referencePid.HasValue) _referencePid = header.Pid;
                if (header.Pid == _referencePid) _lastAppendedPcr = header.Pcr;
            }

            if (_lastAppendedPcr.HasValue && _lastReadPcr.HasValue)
            {
                double diff = (_lastAppendedPcr.Value - _lastReadPcr.Value) / 90000.0;
                BufferSeconds = Math.Max(0, diff);
            }
            else 
            {
                long effectiveBitrate = CurrentBitrate > 0 ? CurrentBitrate : 15_000_000;
                BufferSeconds = (_currentBufferBytes * 8.0) / effectiveBitrate;
            }

            if ((DateTime.Now - _lastMetricUpdate).TotalSeconds > 1)
            {
                _lastMetricUpdate = DateTime.Now;
                double mpvPlusNetwork = BufferSeconds + StreamDiagnostics.Instance.GetMpvBuffer(_streamId);

                StreamDiagnostics.Instance.UpdateStat(_streamId, h => 
                {
                    if (mpvPlusNetwork > 15) h.Status = "Healthy";
                    else if (mpvPlusNetwork > 5) h.Status = "Buffering";
                    else h.Status = "Starving";
                    
                    h.BufferSeconds = BufferSeconds;
                    h.TotalBytesDownloaded = _totalBytesReceived;
                });
            }
        }

        #region Stream Implementation
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _currentBufferBytes;
        public override long Position { get => _totalBytesRead; set => throw new NotSupportedException(); }

        private byte[] _activeReadBlock;
        private int _activeReadOffset;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int totalRead = 0;

            // 1. Flush any active leftover block first
            if (_activeReadBlock != null)
            {
                int available = _activeReadBlock.Length - _activeReadOffset;
                int toCopy = Math.Min(count, available);
                Buffer.BlockCopy(_activeReadBlock, _activeReadOffset, buffer, offset, toCopy);

                _activeReadOffset += toCopy;
                offset += toCopy;
                totalRead += toCopy;
                count -= toCopy; // Remaining needed

                if (_activeReadOffset >= _activeReadBlock.Length)
                {
                    _activeReadBlock = null;
                    _activeReadOffset = 0;
                }

                Interlocked.Add(ref _totalBytesRead, toCopy);
                Interlocked.Add(ref _currentBufferBytes, -toCopy);

                // If output buffer is full, return immediately
                if (count == 0) return totalRead;
            }

            // 2. Loop to fill buffer with AVAILABLE data
            while (count > 0)
            {
                // Try synchronous read first (Low Latency)
                if (_channel.Reader.TryRead(out var nextBlock))
                {
                    UpdateReadPcr(nextBlock);
                    
                    int available = nextBlock.Length;
                    int toCopy = Math.Min(count, available);
                    Buffer.BlockCopy(nextBlock, 0, buffer, offset, toCopy);

                    if (toCopy < available)
                    {
                        // We couldn't consume the whole block, save the rest
                        _activeReadBlock = nextBlock;
                        _activeReadOffset = toCopy;
                    }

                    offset += toCopy;
                    totalRead += toCopy;
                    count -= toCopy;

                    Interlocked.Add(ref _totalBytesRead, toCopy);
                    Interlocked.Add(ref _currentBufferBytes, -toCopy);
                    continue; 
                }

                // 3. If we have read SOMETHING, return immediately! (Smart Batching)
                // Do not wait for more if we already have data. This ensures low latency.
                if (totalRead > 0) return totalRead;

                // 4. If we read NOTHING, we must wait (Async Path)
                try
                {
                    // Wait until at least 1 item is available
                    bool hasData = await _channel.Reader.WaitToReadAsync(cancellationToken);
                    if (!hasData) return 0; // Channel completed
                    // Loop back to TryRead
                }
                catch (OperationCanceledException)
                {
                    // FIX: Propagate cancellation so SlotSimulator knows it's a timeout (heartbeat)
                    // and not a closed stream (EOF).
                    // Debug.WriteLine($"[MultiStreamBuffer] Read Timeout (Heartbeat) - Propagating Cancellation");
                    throw;
                }
            }

            return totalRead;
        }

        private void UpdateReadPcr(byte[] packet)
        {
            if (TsPacketParser.TryParseHeader(packet, out var header))
            {
                if (header.Pcr.HasValue && header.Pid == _referencePid)
                {
                    _lastReadPcr = header.Pcr;
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        #endregion

        #region Helper Methods
        
        private int GetFirstNalType(ReadOnlySpan<byte> packet, TsPacketParser.TsHeader header)
        {
            int offset = TsPacketParser.GetPayloadOffset(packet, header);
            if (offset == -1 || offset >= packet.Length) return -1;

            for (int i = offset; i < packet.Length - 4; i++)
            {
                if (packet[i] == 0x00 && packet[i+1] == 0x00 && packet[i+2] == 0x01)
                {
                    if (i + 3 < packet.Length)
                    {
                        return (packet[i+3] >> 1) & 0x3F; // HEVC NAL type
                    }
                }
            }
            return -1;
        }

        private void ProcessBufferedPacket(byte[] packet)
        {
            // Process a buffered packet as if it just arrived
            if (TsPacketParser.TryParseHeader(packet, out var header))
            {
                // Inject discontinuity for this PID if first time seeing it
                if (!_pidsSeenSinceResume.Contains(header.Pid))
                {
                    if (header.PayloadUnitStartIndicator || header.Pid < 0x20)
                    {
                        _pidsSeenSinceResume.Add(header.Pid);
                        InjectDiscontinuityPacket(header.Pid, header.ContinuityCounter);
                        _lastCcPerPid.Remove(header.Pid);
                    }
                }

                // Copy packet to block
                byte[] block = new byte[TsPacketParser.PacketSize];
                packet.AsSpan().CopyTo(block);

                // CC PATCHING: Remux PID continuity
                if (_lastCcPerPid.TryGetValue(header.Pid, out byte lastInCc))
                {
                    byte nextCc = lastInCc;
                    if (header.HasPayload)
                    {
                        nextCc = (byte)((lastInCc + 1) & 0x0F);
                    }
                    
                    if ((block[3] & 0x0F) != nextCc)
                    {
                        block[3] = (byte)((block[3] & 0xF0) | nextCc);
                    }
                    _lastCcPerPid[header.Pid] = nextCc;
                }
                else
                {
                    _lastCcPerPid[header.Pid] = header.ContinuityCounter;
                }

                _channel.Writer.TryWrite(block);
                Interlocked.Add(ref _currentBufferBytes, TsPacketParser.PacketSize);
                Interlocked.Add(ref _totalBytesReceived, TsPacketParser.PacketSize);
                
                UpdateMetrics(header);
            }
        }
        
        #endregion
    }
}
