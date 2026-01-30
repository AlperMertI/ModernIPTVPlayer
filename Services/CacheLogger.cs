using System;
using System.Diagnostics;

namespace ModernIPTVPlayer.Services
{
    public static class CacheLogger
    {
        public enum Category
        {
            Probe,
            Content,
            TMDB,
            MediaInfo
        }

        public enum Level
        {
            Info,
            Success,
            Warning,
            Error
        }

        public static void Log(Category category, string action, string details = "", Level level = Level.Info)
        {
            string icon = level switch
            {
                Level.Info => "ℹ️",
                Level.Success => "✅",
                Level.Warning => "⚠️",
                Level.Error => "❌",
                _ => "📝"
            };

            string categoryStr = category.ToString().ToUpper();
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

            // Format: [TIME] [CATEGORY] [ACTION] Message
            string message = $"[{timestamp}] [{categoryStr}] {icon} {action}";
            if (!string.IsNullOrEmpty(details))
            {
                message += $" | {details}";
            }

            Debug.WriteLine(message);
        }

        // Convenience methods
        public static void Info(Category category, string action, string details = "") => Log(category, action, details, Level.Info);
        public static void Success(Category category, string action, string details = "") => Log(category, action, details, Level.Success);
        public static void Warning(Category category, string action, string details = "") => Log(category, action, details, Level.Warning);
        public static void Error(Category category, string action, string details = "") => Log(category, action, details, Level.Error);
    }
}
