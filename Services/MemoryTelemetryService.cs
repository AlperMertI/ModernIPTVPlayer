using System;
using System.Diagnostics;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Lightweight process/GC memory checkpoints for comparing UI and cache changes.
    /// This intentionally does not force a collection; it records the runtime as-is.
    /// </summary>
    public static class MemoryTelemetryService
    {
        public readonly record struct Snapshot(long ManagedBytes, long HeapBytes, long CommittedBytes, long PrivateBytes, long WorkingSetBytes, int Handles, int Threads)
        {
            public static Snapshot Empty => new(0, 0, 0, 0, 0, 0, 0);
        }

        public static Snapshot Capture()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var gc = GC.GetGCMemoryInfo();
                return new Snapshot(
                    GC.GetTotalMemory(forceFullCollection: false),
                    gc.HeapSizeBytes,
                    gc.TotalCommittedBytes,
                    process.PrivateMemorySize64,
                    process.WorkingSet64,
                    process.HandleCount,
                    process.Threads.Count);
            }
            catch
            {
                return Snapshot.Empty;
            }
        }

        public static Snapshot LogCheckpoint(string name, string? detail = null, Snapshot? baseline = null)
        {
            try
            {
                var snapshot = Capture();

                AppLogger.Info(
                    $"[Memory] {name}"
                    + (string.IsNullOrWhiteSpace(detail) ? "" : $" | {detail}")
                    + FormatSnapshot(snapshot, baseline));
                
                return snapshot;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[Memory] Checkpoint failed for {name}: {ex.Message}");
                return Snapshot.Empty;
            }
        }

        public static Snapshot ForceFullCollectionAndLog(string name, string? detail = null, Snapshot? baseline = null)
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                return LogCheckpoint(name, detail, baseline);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[Memory] Forced GC checkpoint failed for {name}: {ex.Message}");
                return LogCheckpoint($"{name}.forced-cleanup-failed");
            }
        }

        private static string FormatSnapshot(Snapshot snapshot, Snapshot? baseline)
        {
            var text =
                $" | managed={ToMb(snapshot.ManagedBytes)}MB"
                + $" | heapSize={ToMb(snapshot.HeapBytes)}MB"
                + $" | committed={ToMb(snapshot.CommittedBytes)}MB"
                + $" | private={ToMb(snapshot.PrivateBytes)}MB"
                + $" | workingSet={ToMb(snapshot.WorkingSetBytes)}MB"
                + $" | handles={snapshot.Handles}"
                + $" | threads={snapshot.Threads}";

            if (baseline is { } b && b.PrivateBytes > 0)
            {
                text +=
                    $" | privateDelta={FormatDelta(snapshot.PrivateBytes - b.PrivateBytes)}MB"
                    + $" | workingSetDelta={FormatDelta(snapshot.WorkingSetBytes - b.WorkingSetBytes)}MB"
                    + $" | handleDelta={snapshot.Handles - b.Handles:+#;-#;0}"
                    + $" | threadDelta={snapshot.Threads - b.Threads:+#;-#;0}";
            }

            return text;
        }

        private static string FormatDelta(long bytes)
        {
            long mb = ToMb(bytes);
            return mb > 0 ? $"+{mb}" : mb.ToString();
        }

        private static long ToMb(long bytes) => bytes / 1024 / 1024;
    }
}
