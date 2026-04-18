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
        public static void LogCheckpoint(string name, string? detail = null)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var gc = GC.GetGCMemoryInfo();
                long managed = GC.GetTotalMemory(forceFullCollection: false);

                AppLogger.Info(
                    $"[Memory] {name}"
                    + (string.IsNullOrWhiteSpace(detail) ? "" : $" | {detail}")
                    + $" | managed={ToMb(managed)}MB"
                    + $" | heapSize={ToMb(gc.HeapSizeBytes)}MB"
                    + $" | committed={ToMb(gc.TotalCommittedBytes)}MB"
                    + $" | private={ToMb(process.PrivateMemorySize64)}MB"
                    + $" | workingSet={ToMb(process.WorkingSet64)}MB");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[Memory] Checkpoint failed for {name}: {ex.Message}");
            }
        }

        private static long ToMb(long bytes) => bytes / 1024 / 1024;
    }
}
