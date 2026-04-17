using System;
using System.IO;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Single-writer tracer for all Hero-related performance logging.
    /// Before this existed, <c>HeroSectionControl</c> and <c>StremioDiscoveryControl</c> each owned their
    /// own <see cref="StreamWriter"/> pointed at the same temp file. The second opener always lost the
    /// write lock (FileShare.Read denies concurrent writers) and spammed first-chance IOExceptions on every
    /// log line. Consolidating here makes the logger a single, well-defined component and kills the storm
    /// at the source rather than masking it via FileShare.ReadWrite.
    /// </summary>
    internal static class HeroTracer
    {
        private static readonly object _lock = new();
        private static StreamWriter? _writer;
        private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "hero_perf_log.txt");

        public static void Log(string msg)
        {
            var line = $"[HERO PERF] {DateTime.Now:HH:mm:ss.fff} | {msg}";
            System.Diagnostics.Debug.WriteLine(line);
            try
            {
                lock (_lock)
                {
                    if (_writer == null)
                    {
                        // FileShare.ReadWrite so that external tail viewers can observe in real time,
                        // not because we expect another writer (there is only one -- this class).
                        _writer = new StreamWriter(
                            new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        { AutoFlush = true };
                    }
                    _writer.WriteLine(line);
                }
            }
            catch
            {
                // Tracing is best-effort; never let a logger issue cascade into the app.
            }
        }
    }
}