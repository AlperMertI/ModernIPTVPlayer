using System;
using System.Collections.Generic;
using System.Linq;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Centralized telemetry for MediaInfoPage lifecycle events, state transitions,
    /// and performance checkpoints. All telemetry flows through AppLogger for
    /// consistent formatting and file/console output.
    /// </summary>
    public static class PageTelemetry
    {
        #region Lifecycle

        /// <summary>Begins a timed scope for the page load operation.</summary>
        public static OperationTimer BeginLoad(string itemId) =>
            new OperationTimer("MediaInfoPage.Load", itemId);

        /// <summary>Begins a timed scope for the navigation-away operation.</summary>
        public static OperationTimer BeginNavigateAway(string targetPage) =>
            new OperationTimer("MediaInfoPage.NavigateAway", targetPage);

        /// <summary>Begins a timed scope for the reveal animation sequence.</summary>
        public static OperationTimer BeginReveal() =>
            new OperationTimer("MediaInfoPage.Reveal");

        /// <summary>Begins a timed scope for the cleanup/dispose operation.</summary>
        public static OperationTimer BeginCleanup() =>
            new OperationTimer("MediaInfoPage.Cleanup");

        #endregion

        #region State Transitions

        /// <summary>Logs a page load state transition.</summary>
        public static void LogStateTransition(string from, string to) =>
            AppLogger.Info($"[State] {from} \u2192 {to}");

        /// <summary>Logs a panel mode change.</summary>
        public static void LogPanelTransition(string from, string to, string reason) =>
            AppLogger.Info($"[Panel] {from} \u2192 {to} (reason: {reason})");

        /// <summary>Logs a content kind resolution.</summary>
        public static void LogContentKind(string kind, string itemTitle) =>
            AppLogger.Info($"[Content] kind={kind}, title={itemTitle}");

        #endregion

        #region Data Operations

        /// <summary>Logs metadata fetch result.</summary>
        public static void LogMetadataResult(bool success, string title, long elapsedMs, string source = "unknown")
        {
            string status = success ? "OK" : "FAIL";
            AppLogger.Info($"[Metadata] {status} | title={title}, source={source}, took={elapsedMs}ms");
        }

        /// <summary>Logs cast/director population result.</summary>
        public static void LogCastResult(int castCount, int directorCount, long elapsedMs) =>
            AppLogger.Info($"[Cast] cast={castCount}, directors={directorCount}, took={elapsedMs}ms");

        /// <summary>Logs episode/season load result.</summary>
        public static void LogEpisodesResult(int seasonCount, int episodeCount, long elapsedMs) =>
            AppLogger.Info($"[Episodes] seasons={seasonCount}, episodes={episodeCount}, took={elapsedMs}ms");

        /// <summary>Logs Stremio source fetch result.</summary>
        public static void LogSourcesResult(int addonCount, int streamCount, long elapsedMs, bool fromCache) =>
            AppLogger.Info($"[Sources] addons={addonCount}, streams={streamCount}, cache={fromCache}, took={elapsedMs}ms");

        #endregion

        #region Player Operations

        /// <summary>Logs prebuffer operation start.</summary>
        public static void LogPrebufferStart(string url, double position) =>
            AppLogger.Info($"[Prebuffer] start | url={TruncateUrl(url)}, position={position}s");

        /// <summary>Logs prebuffer operation completion.</summary>
        public static void LogPrebufferComplete(long elapsedMs) =>
            AppLogger.Info($"[Prebuffer] complete | took={elapsedMs}ms");

        /// <summary>Logs player handoff operation.</summary>
        public static void LogHandoff(bool success, string reason) =>
            AppLogger.Info($"[Handoff] {(success ? "OK" : "FAIL")} | {reason}");

        #endregion

        #region UI Operations

        /// <summary>Logs hero background image change.</summary>
        public static void LogHeroChange(string url, string reason) =>
            AppLogger.Info($"[Hero] change | url={TruncateUrl(url)}, reason={reason}");

        /// <summary>Logs ambience color extraction.</summary>
        public static void LogAmbienceExtract(string url, byte r, byte g, byte b, long elapsedMs) =>
            AppLogger.Info($"[Ambience] extracted | url={TruncateUrl(url)}, color=({r},{g},{b}), took={elapsedMs}ms");

        /// <summary>Logs layout decision.</summary>
        public static void LogLayoutDecision(bool isWide, double width, double height) =>
            AppLogger.Info($"[Layout] mode={(isWide ? "Wide" : "Narrow")}, size={width}x{height}");

        /// <summary>Logs skeleton rebuild.</summary>
        public static void LogSkeletonRebuild(string section, int count, long elapsedMs) =>
            AppLogger.Info($"[Skeleton] section={section}, items={count}, took={elapsedMs}ms");

        #endregion

        #region Errors

        /// <summary>Logs an error with operation context.</summary>
        public static void LogError(string operation, Exception ex, Dictionary<string, object?>? context = null)
        {
            string ctxStr = context != null && context.Count > 0
                ? " | " + string.Join(", ", context.Where(kv => kv.Value != null).Select(kv => $"{kv.Key}={kv.Value}"))
                : string.Empty;

            AppLogger.Error($"[Error] {operation}{ctxStr}", ex);
        }

        /// <summary>Logs a warning with context.</summary>
        public static void LogWarn(string operation, string message, Dictionary<string, object?>? context = null)
        {
            string ctxStr = context != null && context.Count > 0
                ? " | " + string.Join(", ", context.Where(kv => kv.Value != null).Select(kv => $"{kv.Key}={kv.Value}"))
                : string.Empty;

            AppLogger.Warn($"[Warn] {operation}{ctxStr} | {message}");
        }

        #endregion

        #region Memory

        /// <summary>Logs a memory checkpoint with baseline delta.</summary>
        public static void LogMemory(string phase, MemoryTelemetryService.Snapshot? baseline = null) =>
            MemoryTelemetryService.LogCheckpoint($"MediaInfoPage.{phase}", baseline: baseline);

        #endregion

        #region Helpers

        private static string TruncateUrl(string url, int maxLength = 80)
        {
            if (string.IsNullOrEmpty(url)) return "<null>";
            return url.Length > maxLength ? url.Substring(0, maxLength) + "..." : url;
        }

        #endregion
    }
}
