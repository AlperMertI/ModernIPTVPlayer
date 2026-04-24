using System;
using System.Diagnostics;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer.Services.Metadata
{
    public sealed class MetadataTrace
    {
        public string OperationId { get; }
        public string ContextName { get; }
        public string ContentKey { get; }
        public string Title { get; private set; }

        public MetadataTrace(string contextName, string contentKey, string? title)
        {
            OperationId = Guid.NewGuid().ToString("N")[..8];
            ContextName = contextName;
            ContentKey = string.IsNullOrWhiteSpace(contentKey) ? "unknown" : contentKey;
            Title = string.IsNullOrWhiteSpace(title) ? "unknown" : title.Trim();
            
            // Critical for observability: Log the start of the trace so the ID is searchable
            Log("START", $"[{contextName}] Operation initialized for '{Title}' ({ContentKey})");
        }

        public void UpdateTitle(string? title)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                Title = title.Trim();
            }
        }

        public void Log(string stage, string message)
        {
            // Unify formatting to AppLogger for consistency across the app
            AppLogger.Info($"[MetadataTrace|{OperationId}|{stage}] {message}");
        }
    }
}
