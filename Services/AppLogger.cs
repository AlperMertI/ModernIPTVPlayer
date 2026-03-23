using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

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
}
