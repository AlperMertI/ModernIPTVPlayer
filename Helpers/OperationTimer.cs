using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// High-performance operation timer with step-level tracing and automatic duration logging.
    /// Implements IDisposable for scope-based timing: wrap an operation, call Step() for phases,
    /// duration is logged automatically on dispose.
    /// </summary>
    /// <example>
    /// using var timer = new OperationTimer("LoadMetadata", itemId);
    /// timer.Step("fetch-from-api");
    /// var result = await api.GetAsync();
    /// timer.Step("parse-response", ("count", result.Count));
    /// // On dispose: "[OP-DONE] LoadMetadata [abc12345] took 234ms"
    /// </example>
    public readonly struct OperationTimer : IDisposable
    {
        private readonly Stopwatch _sw;
        private readonly string _operation;
        private readonly string _correlationId;
        private readonly bool _enabled;

        /// <summary>
        /// Starts timing an operation with a unique correlation ID.
        /// </summary>
        /// <param name="operation">Human-readable operation name (e.g., "MediaInfoPage.Load").</param>
        /// <param name="correlationId">Optional correlation ID for tracing across components. Auto-generated if null.</param>
        public OperationTimer(string operation, string? correlationId = null)
        {
            _operation = operation;
            _correlationId = correlationId ?? GenerateId(operation);
            _enabled = AppLogger.MinLevel <= AppLogger.LogLevel.Info;
            _sw = _enabled ? Stopwatch.StartNew() : new Stopwatch();

            if (_enabled)
            {
                AppLogger.Info($"[OP-START] {_operation} [{_correlationId}]");
            }
        }

        /// <summary>
        /// Logs a phase step within the current operation.
        /// </summary>
        /// <param name="phase">Phase name (e.g., "fetch-metadata", "apply-ui").</param>
        /// <param name="tags">Optional key-value pairs for context (e.g., ("count", 42), ("cache", "hit")).</param>
        public void Step(string phase, params (string key, object? value)[] tags)
        {
            if (!_enabled) return;

            string tagStr = tags.Length > 0
                ? " | " + string.Join(", ", tags.Where(t => t.value != null).Select(t => $"{t.key}={t.value}"))
                : string.Empty;

            AppLogger.Info($"[OP-STEP] {_operation}/{phase} [{_correlationId}]{tagStr}");
        }

        /// <summary>
        /// Logs operation completion with elapsed time. Called automatically on Dispose.
        /// </summary>
        public void Complete(params (string key, object? value)[] tags)
        {
            if (!_enabled) return;

            _sw.Stop();
            string tagStr = tags.Length > 0
                ? " | " + string.Join(", ", tags.Where(t => t.value != null).Select(t => $"{t.key}={t.value}"))
                : string.Empty;

            AppLogger.Info($"[OP-DONE] {_operation} [{_correlationId}] took {_sw.ElapsedMilliseconds}ms{tagStr}");
        }

        /// <summary>
        /// Logs operation failure with exception details.
        /// </summary>
        public void Fail(Exception ex, params (string key, object? value)[] tags)
        {
            _sw.Stop();
            string tagStr = tags.Length > 0
                ? " | " + string.Join(", ", tags.Where(t => t.value != null).Select(t => $"{t.key}={t.value}"))
                : string.Empty;

            AppLogger.Error($"[OP-FAIL] {_operation} [{_correlationId}] took {_sw.ElapsedMilliseconds}ms{tagStr}", ex);
        }

        /// <summary>
        /// Returns elapsed milliseconds without stopping the timer.
        /// </summary>
        public long ElapsedMs => _sw.IsRunning ? _sw.ElapsedMilliseconds : _sw.ElapsedMilliseconds;

        void IDisposable.Dispose()
        {
            if (_enabled && _sw.IsRunning)
            {
                Complete();
            }
        }

        private static string GenerateId(string prefix)
        {
            return $"{prefix}-{Guid.NewGuid():N}".Substring(0, 16);
        }
    }

    /// <summary>
    /// Thread-safe throttle for preventing log spam from high-frequency operations.
    /// </summary>
    public static class LogThrottle
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _throttleMap =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns true if enough time has passed since the last log for this key.
        /// </summary>
        /// <param name="key">Unique throttle key (e.g., "ResizeEvent").</param>
        /// <param name="interval">Minimum time between logs.</param>
        public static bool ShouldLog(string key, TimeSpan interval)
        {
            long now = DateTime.UtcNow.Ticks;
            long minDelta = interval.Ticks;
            while (true)
            {
                long previous = _throttleMap.GetOrAdd(key, 0);
                if (now - previous < minDelta) return false;
                if (_throttleMap.TryUpdate(key, now, previous)) return true;
            }
        }
    }
}
