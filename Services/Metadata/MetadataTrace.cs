using System;
using System.Diagnostics;

namespace ModernIPTVPlayer.Services.Metadata
{
    internal sealed class MetadataTrace
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
            Debug.WriteLine($"[Metadata][{OperationId}][{ContextName}][{stage}] Item='{Title}' Key='{ContentKey}' {message}");
        }
    }
}
