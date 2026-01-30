using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Timers;

namespace ModernIPTVPlayer.Services.Streaming
{
    public class StreamDiagnostics
    {
        private static readonly Lazy<StreamDiagnostics> _instance = new Lazy<StreamDiagnostics>(() => new StreamDiagnostics());
        public static StreamDiagnostics Instance => _instance.Value;

        private readonly Timer _logTimer;
        private readonly ConcurrentDictionary<string, StreamHealth> _stats = new ConcurrentDictionary<string, StreamHealth>();

        private StreamDiagnostics()
        {
            _logTimer = new Timer(5000); // Log every 5 seconds
            _logTimer.Elapsed += (s, e) => LogAllStats();
            _logTimer.Start();
        }

        public void UpdateStat(string streamId, Action<StreamHealth> updateAction)
        {
            var health = _stats.GetOrAdd(streamId, id => new StreamHealth { StreamId = id });
            lock (health)
            {
                updateAction(health);
            }
        }

        public void RemoveStat(string streamId)
        {
            _stats.TryRemove(streamId, out _);
        }

        public StreamHealth GetHealth(string streamId)
        {
            _stats.TryGetValue(streamId, out var health);
            return health;
        }

        public double GetMpvBuffer(string streamId)
        {
            if (_stats.TryGetValue(streamId, out var health))
            {
                lock (health) return health.MpvBufferSeconds;
            }
            return 0;
        }

        private void LogAllStats()
        {
            if (_stats.IsEmpty) return;

            Debug.WriteLine("\n--- [STREAM DIAGNOSTICS] ---");
            foreach (var health in _stats.Values.OrderBy(h => h.StreamId))
            {
                lock (health)
                {
                    Debug.WriteLine($"Stream: {health.StreamId} | Status: {health.Status} " +
                                    $"| Buffer: {health.BufferSeconds:F1}s | Mpv: {health.MpvBufferSeconds:F1}s " +
                                    $"| Window: {health.ServerWindowSize:F1}s " +
                                    $"| In: {health.TotalBytesDownloaded / 1024 / 1024}MB | Out: {health.TotalBytesSent / 1024 / 1024}MB " +
                                    $"| Speed: {health.DownloadSpeedMbps:F2} Mbps | Errors: {health.SyncLossCount} " +
                                    $"| {health.DebugInfo}");
                }
            }
            Debug.WriteLine("---------------------------\n");
        }
    }

    public class StreamHealth
    {
        public string StreamId { get; set; }
        public string Status { get; set; } = "Idle";
        public double BufferSeconds { get; set; }
        public double MpvBufferSeconds { get; set; }
        public double ServerWindowSize { get; set; }
        public double DownloadSpeedMbps { get; set; }
        public int SyncLossCount { get; set; }
        public long TotalBytesDownloaded { get; set; }
        public long TotalBytesSent { get; set; }
        public string DebugInfo { get; set; } // Added for bottleneck diagnostics
    }
}
