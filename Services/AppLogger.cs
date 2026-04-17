using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ModernIPTVPlayer.Services
{
    public static class AppLogger
    {
        public enum LogLevel
        {
            Info = 0,
            Warn = 1,
            Error = 2,
            Critical = 3
        }

        public static LogLevel MinLevel { get; set; } = LogLevel.Info;
        public static bool EnableConsoleLogging { get; set; } = true;

        public static void Info(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
        {
            if (MinLevel > LogLevel.Info) return;
            LogRaw(LogLevel.Info, message, memberName, filePath);
        }

        public static void Warn(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
        {
            if (MinLevel > LogLevel.Warn) return;
            LogRaw(LogLevel.Warn, message, memberName, filePath);
        }

        public static void Error(string message, Exception? ex = null, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
        {
            if (MinLevel > LogLevel.Error) return;
            string detail = ex != null ? $"{message} | Exception: {ex.Message}\nStack: {ex.StackTrace}" : message;
            LogRaw(LogLevel.Error, detail, memberName, filePath);
        }

        public static void Critical(string message, Exception? ex = null, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
        {
            // Critical should always be logged usually
            string detail = ex != null ? $"{message} | Exception: {ex.Message}\nStack: {ex.StackTrace}" : message;
            LogRaw(LogLevel.Critical, detail, memberName, filePath);
        }

        private static void LogRaw(LogLevel level, string message, string memberName, string filePath)
        {
            if (level < MinLevel) return;

            // Performance: Extremely aggressive truncation for Info logs to save console/memory
            int limit = level >= LogLevel.Warn ? 262144 : 4096;
            if (message.Length > limit)
            {
                message = string.Concat(message.AsSpan(0, limit), "... [TRUNCATED]");
            }

            string fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string threadId = Environment.CurrentManagedThreadId.ToString().PadLeft(3);
            
            string prefix = level switch
            {
                LogLevel.Info => "[INFO]",
                LogLevel.Warn => "[WARN]",
                LogLevel.Error => "[ERR ]",
                LogLevel.Critical => "[CRIT]",
                _ => "[LOG ]"
            };

            string logLine = $"{timestamp} | {prefix} | TID:{threadId} | {fileName}.{memberName} | {message}";

            // Optimization: Only write to Trace (VS Console) if enabled or if it's a warning/error.
            // FileLoggerListener will still receive it via Trace.Listeners.
            // BUT: If we skip Trace.WriteLine, listeners won't get it.
            // Better approach: FileLoggerListener is what we really want for persistent logs.
            // We can add a custom event for FileLogger to avoid Trace bottleneck entirely, 
            // but let's stick to Trace and just be careful with what goes to console.
            
            Trace.WriteLine(logLine);
        }
    }

    public static class LifecycleLog
    {
        private static readonly ConcurrentDictionary<string, long> ThrottleMap = new(StringComparer.OrdinalIgnoreCase);

        public readonly struct Scope : IDisposable
        {
            private readonly Stopwatch _sw;
            private readonly string _operation;
            private readonly string _correlationId;
            private readonly Dictionary<string, object?> _baseTags;

            public Scope(string operation, string correlationId, Dictionary<string, object?> baseTags)
            {
                _operation = operation;
                _correlationId = correlationId;
                _baseTags = baseTags;
                _sw = Stopwatch.StartNew();
                AppLogger.Info(Format("start", operation, correlationId, baseTags));
            }

            public void Step(string phase, Dictionary<string, object?>? tags = null)
            {
                AppLogger.Info(Format(phase, _operation, _correlationId, Merge(tags)));
            }

            public void Dispose()
            {
                var tags = Merge(new Dictionary<string, object?> { ["durationMs"] = _sw.ElapsedMilliseconds });
                AppLogger.Info(Format("done", _operation, _correlationId, tags));
            }

            private Dictionary<string, object?> Merge(Dictionary<string, object?>? extra)
            {
                if (extra == null || extra.Count == 0) return _baseTags;
                var merged = new Dictionary<string, object?>(_baseTags, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in extra) merged[kv.Key] = kv.Value;
                return merged;
            }
        }

        public static Scope Begin(string operation, string? correlationId = null, Dictionary<string, object?>? tags = null)
        {
            correlationId ??= NewId(operation);
            return new Scope(operation, correlationId, tags ?? new Dictionary<string, object?>());
        }

        public static string NewId(string prefix)
        {
            string token = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"{prefix}-{token}";
        }

        public static bool ShouldLog(string key, TimeSpan interval)
        {
            long now = DateTime.UtcNow.Ticks;
            long minDelta = interval.Ticks;
            while (true)
            {
                long previous = ThrottleMap.GetOrAdd(key, 0);
                if (now - previous < minDelta) return false;
                if (ThrottleMap.TryUpdate(key, now, previous)) return true;
            }
        }

        private static string Format(string phase, string operation, string correlationId, Dictionary<string, object?> tags)
        {
            string p = phase.ToUpperInvariant();
            string icon = p switch {
                "START" => "◎",
                "DONE"  => "✓",
                "FAIL"  => "✗",
                "ERROR" => "✗",
                _ => "➔"
            };

            string tagString = tags.Count == 0
                ? ""
                : " | " + string.Join(", ", tags.Where(kv => kv.Value != null).Select(kv => $"{kv.Key}={kv.Value}"));
            
            return $"[Lifecycle] {icon} {operation} [{p}] ({correlationId}){tagString}";
        }
    }
}
