using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services.Iptv;
using ModernIPTVPlayer.Services.Stremio;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Handles Stremio addon source fetching, stream processing, IPTV matching, and caching.
    /// Extracted from MediaInfoPage.PlayStremioContent.
    /// </summary>
    internal sealed class StremioSourcesService
    {
        #region Fields

        private readonly StremioAddonManager _addonManager;
        private readonly StremioService _stremioService;
        private readonly IptvMatchService _iptvMatchService;
        private readonly Dictionary<string, StremioSourcesCacheEntry> _cache = new();

        #endregion

        #region Constructor

        public StremioSourcesService()
        {
            _addonManager = StremioAddonManager.Instance;
            _stremioService = StremioService.Instance;
            _iptvMatchService = IptvMatchService.Instance;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Concurrently fetches streams from all configured Stremio addons and feeds them back reactively via callbacks.
        /// </summary>
        public async Task FetchSourcesAsync(
            string resolvedVideoId,
            string type,
            string? lastStreamUrl,
            Action<StremioAddonViewModel> onAddonFetched,
            Action<string> onAddonFailed,
            CancellationToken cancellationToken)
        {
            var addons = _addonManager.GetAddons();
            var tasks = addons.Select(async (baseUrl, i) =>
            {
                var processed = await FetchSingleAddonAsync(baseUrl, i, resolvedVideoId, type, lastStreamUrl, cancellationToken);
                if (processed != null)
                {
                    var vm = new StremioAddonViewModel
                    {
                        Name = processed.Name,
                        AddonUrl = processed.AddonUrl,
                        IsLoading = false,
                        SortIndex = processed.SortIndex,
                        Streams = processed.Streams.Select(s => new StremioStreamViewModel
                        {
                            Title = s.Title,
                            Name = s.Name,
                            ProviderText = s.ProviderText,
                            AddonName = s.AddonName,
                            AddonUrl = s.AddonUrl,
                            Url = s.Url,
                            Externalurl = s.Externalurl,
                            Quality = s.Quality,
                            Size = s.Size,
                            IsCached = s.IsCached,
                            OriginalStream = s.OriginalStream,
                            IsActive = s.IsActive,
                            IptvStreamId = s.IptvStreamId,
                            IptvSeriesId = s.IptvSeriesId
                        }).ToList()
                    };
                    onAddonFetched(vm);
                }
                else
                {
                    onAddonFailed(baseUrl);
                }
            });
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Builds IPTV addon view model from matched IPTV streams, resolving series just-in-time.
        /// </summary>
        public async Task<StremioAddonViewModel?> BuildIptvAddonAsync(
            IMediaStream item,
            UnifiedMetadata? metadata,
            EpisodeItem? selectedEpisode,
            string? lastStreamUrl)
        {
            if (item is not StremioMediaStream stremioStream) return null;

            var iptvMatches = _iptvMatchService.FindPotentialMatchesInIptv(
                metadata?.Title ?? stremioStream.Title,
                stremioStream.Meta?.Type ?? "movie",
                0.3,
                metadata?.Year ?? stremioStream.Meta?.Year);

            if (iptvMatches == null || !iptvMatches.Any()) return null;

            var iptvStreams = new List<StremioStreamViewModel>();
            var seenUrls = new HashSet<string>();

            foreach (var match in iptvMatches)
            {
                string iptvUrl = match.StreamUrl;
                string displayTitle = match.Title;

                // [JUST-IN-TIME RESOLUTION] Asynchronously resolve matched SeriesStream to selected episode's direct playable URL
                if (match is SeriesStream sMatch && selectedEpisode != null)
                {
                    int targetSeason = selectedEpisode.SeasonNumber;
                    int targetEpisode = selectedEpisode.EpisodeNumber;
                    
                    AppLogger.Info($"[StremioSourcesService] Resolving IPTV Series ID={sMatch.SeriesId} just-in-time for S{targetSeason}E{targetEpisode}...");
                    
                    var info = await ContentCacheService.Instance.GetSeriesInfoAsync(sMatch.SeriesId, App.CurrentLogin);
                    if (info?.Episodes != null)
                    {
                        bool found = false;
                        foreach (var kvp in info.Episodes)
                        {
                            if (int.TryParse(kvp.Key, out int seasonNum) && seasonNum == targetSeason)
                            {
                                foreach (var ep in kvp.Value)
                                {
                                    if (int.TryParse(ep.EpisodeNum?.ToString(), out int epNum) && epNum == targetEpisode)
                                    {
                                        string extension = ep.ContainerExtension;
                                        if (string.IsNullOrEmpty(extension)) extension = "mkv"; 
                                        if (!extension.StartsWith(".")) extension = "." + extension;
                                        
                                        iptvUrl = $"{App.CurrentLogin.Host}/series/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{ep.Id}{extension}";
                                        
                                        if (!string.IsNullOrEmpty(ep.Title))
                                            displayTitle = $"{sMatch.Name} - S{targetSeason:D2}E{targetEpisode:D2} - {ep.Title}";
                                        else
                                            displayTitle = $"{sMatch.Name} - S{targetSeason:D2}E{targetEpisode:D2}";
                                        
                                        AppLogger.Info($"[StremioSourcesService] Just-in-time resolved IPTV series S{targetSeason}E{targetEpisode} to direct URL: {iptvUrl}");
                                        found = true;
                                        break;
                                    }
                                }
                            }
                            if (found) break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(iptvUrl) && App.CurrentLogin != null)
                {
                    string host = App.CurrentLogin.Host?.TrimEnd('/') ?? "";
                    string user = App.CurrentLogin.Username ?? "";
                    string pass = App.CurrentLogin.Password ?? "";

                    if (match is VodStream vStream)
                    {
                        string ext = vStream.ContainerExtension ?? "mp4";
                        iptvUrl = $"{host}/movie/{user}/{pass}/{vStream.StreamId}.{ext}";
                    }
                    else if (match is SeriesStream sStream)
                    {
                        iptvUrl = $"iptv://series/{sStream.SeriesId}";
                    }
                    else
                    {
                        iptvUrl = match.Id.ToString();
                    }
                }

                if (!string.IsNullOrEmpty(iptvUrl) && !iptvUrl.Contains("://") && !iptvUrl.StartsWith("/"))
                    iptvUrl = $"iptv://{iptvUrl}";

                if (string.IsNullOrEmpty(iptvUrl) || seenUrls.Contains(iptvUrl)) continue;
                seenUrls.Add(iptvUrl);

                bool isActive = !string.IsNullOrEmpty(lastStreamUrl) && iptvUrl == lastStreamUrl;

                iptvStreams.Add(new StremioStreamViewModel
                {
                    Title = displayTitle,
                    ProviderText = App.CurrentLogin?.PlaylistName?.ToUpperInvariant() ?? "IPTV",
                    AddonName = "IPTV",
                    Url = iptvUrl,
                    IptvStreamId = (match is VodStream vod) ? (int?)vod.StreamId : null,
                    IptvSeriesId = (match is SeriesStream series) ? (int?)series.SeriesId : null,
                    IsCached = true,
                    Quality = !string.IsNullOrEmpty(match.Resolution) ? match.Resolution : "VOD",
                    IsActive = isActive
                });
            }

            if (selectedEpisode != null && !string.IsNullOrEmpty(selectedEpisode.StreamUrl) && selectedEpisode.StreamUrl.Contains("/series/"))
            {
                if (!seenUrls.Contains(selectedEpisode.StreamUrl))
                {
                    iptvStreams.Insert(0, new StremioStreamViewModel
                    {
                        Title = !string.IsNullOrEmpty(selectedEpisode.IptvSourceTitle) ? selectedEpisode.IptvSourceTitle : selectedEpisode.Title,
                        ProviderText = App.CurrentLogin?.PlaylistName?.ToUpperInvariant() ?? "IPTV",
                        AddonName = "IPTV",
                        Url = selectedEpisode.StreamUrl,
                        IptvStreamId = selectedEpisode.IptvStreamId,
                        IptvSeriesId = selectedEpisode.IptvSeriesId,
                        IsCached = true,
                        Quality = selectedEpisode.Resolution,
                        IsActive = !string.IsNullOrEmpty(lastStreamUrl) && selectedEpisode.StreamUrl == lastStreamUrl
                    });
                }
            }

            if (iptvStreams.Count == 0) return null;

            return new StremioAddonViewModel
            {
                Name = "IPTV",
                AddonUrl = "iptv://internal",
                Streams = iptvStreams,
                IsLoading = false,
                SortIndex = -1
            };
        }

        /// <summary>
        /// Checks the internal cache for previously fetched sources.
        /// </summary>
        public StremioSourcesCacheEntry? GetCachedSources(string type, string resolvedVideoId)
        {
            string cacheKey = $"{type}|{resolvedVideoId}";
            return _cache.TryGetValue(cacheKey, out var entry) && entry?.Addons != null && entry.Addons.Count > 0 ? entry : null;
        }

        /// <summary>
        /// Updates the cache with a partial or complete snapshot.
        /// </summary>
        public void UpdateCache(string type, string resolvedVideoId, IReadOnlyList<StremioAddonViewModel> addons, bool isComplete)
        {
            if (addons.Count == 0) return;
            string cacheKey = $"{type}|{resolvedVideoId}";
            var cloned = addons.Select(CloneAddonViewModel).ToList();
            _cache[cacheKey] = new StremioSourcesCacheEntry { Addons = cloned, IsComplete = isComplete };
        }

        /// <summary>
        /// Resolves a TMDB-style ID to an IMDb ID using unified metadata.
        /// </summary>
        public static string ResolveVideoId(string videoId, string type, UnifiedMetadata? metadata)
        {
            if (videoId.StartsWith("tt") || metadata == null || string.IsNullOrEmpty(metadata.ImdbId) || !metadata.ImdbId.StartsWith("tt"))
                return videoId;

            if (type == "movie")
                return metadata.ImdbId;

            if (type == "series")
            {
                var parts = videoId.Split(':');
                if (parts.Length >= 3)
                    return $"{metadata.ImdbId}:{parts[parts.Length - 2]}:{parts[parts.Length - 1]}";
            }

            return videoId;
        }

        #endregion

        #region Private Methods

        private async Task<ProcessedAddon?> FetchSingleAddonAsync(
            string baseUrl,
            int sortIndex,
            string resolvedVideoId,
            string type,
            string? lastStreamUrl,
            CancellationToken cancellationToken)
        {
            try
            {
                var manifest = await _stremioService.GetManifestAsync(baseUrl, cancellationToken);
                if (manifest == null) return null;

                if (!_addonManager.SupportsResource(baseUrl, "stream")) return null;

                string addonDisplayName = NormalizeAddonText(manifest.Name ?? baseUrl.Replace("https://", "").Replace("http://", "").Split('/')[0]);
                var streams = await _stremioService.GetStreamsAsync(new List<string> { baseUrl }, type, resolvedVideoId, includeIptv: false, cancellationToken: cancellationToken);

                if (streams == null || streams.Count == 0) return null;

                var processedStreams = new List<ProcessedStream>();
                foreach (var s in streams)
                {
                    string displayFileName = "";
                    string displayDescription = "";
                    string rawName = NormalizeAddonText(s.Name ?? "");
                    string rawTitle = NormalizeAddonText(s.Title ?? "");
                    string rawDesc = NormalizeAddonText(s.Description ?? "");

                    if (!string.IsNullOrEmpty(rawDesc))
                    {
                        var lines = rawDesc.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        var metaParts = new List<string>();
                        foreach (var line in lines)
                        {
                            string trimmed = line.Trim();
                            if (string.IsNullOrEmpty(trimmed)) continue;
                            if (trimmed.StartsWith("Name:", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("File:", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("\U0001F4C4"))
                                displayFileName = trimmed.Replace("Name:", "").Replace("File:", "").Replace("\U0001F4C4", "").Trim();
                            else
                                metaParts.Add(trimmed);
                        }
                        if (string.IsNullOrEmpty(displayFileName) && lines.Length > 0)
                        {
                            string lastLine = lines.Last().Trim();
                            if (lastLine.Contains(".") && lastLine.Split('.').Last().Length <= 4)
                            {
                                displayFileName = lastLine;
                                metaParts.RemoveAt(metaParts.Count - 1);
                            }
                        }
                        displayDescription = string.Join("  •  ", metaParts);
                    }

                    string finalTitle = displayFileName;
                    if (string.IsNullOrEmpty(finalTitle)) finalTitle = rawTitle;
                    if (string.IsNullOrEmpty(finalTitle) || finalTitle.Length < 3) finalTitle = rawName.Split('\n')[0];
                    if (string.IsNullOrEmpty(finalTitle)) finalTitle = addonDisplayName;

                    bool isCached = IsStreamCached(s) || addonDisplayName.ToLower().Contains("debrid") || rawName.ToLower().Contains("rd+");
                    string sizeInfo = ExtractSize(displayDescription) ?? ExtractSize(rawTitle) ?? ExtractSize(rawName);

                    bool isActive = !string.IsNullOrEmpty(lastStreamUrl) && s.Url == lastStreamUrl;
                    if (!isActive && !string.IsNullOrEmpty(lastStreamUrl))
                    {
                        try
                        {
                            string lastFileName = System.IO.Path.GetFileName(new Uri(lastStreamUrl).LocalPath);
                            string currentFileName = System.IO.Path.GetFileName(new Uri(s.Url).LocalPath);
                            if (!string.IsNullOrEmpty(lastFileName) && lastFileName == currentFileName) isActive = true;
                        }
                        catch { }
                    }

                    processedStreams.Add(new ProcessedStream
                    {
                        Title = finalTitle,
                        Name = displayDescription,
                        ProviderText = rawName.Trim(),
                        AddonName = addonDisplayName,
                        AddonUrl = baseUrl,
                        Url = s.Url,
                        Externalurl = s.Externalurl,
                        Quality = ParseQuality(rawName + " " + rawTitle + " " + rawDesc),
                        Size = sizeInfo,
                        IsCached = isCached,
                        OriginalStream = s,
                        IsActive = isActive
                    });
                }

                if (processedStreams.Count == 0) return null;

                return new ProcessedAddon
                {
                    Name = addonDisplayName.ToUpper(),
                    AddonUrl = baseUrl,
                    Streams = processedStreams,
                    IsLoading = false,
                    SortIndex = sortIndex
                };
            }
            catch
            {
                return null;
            }
        }

        private static bool IsStreamCached(StremioStream s)
        {
            string all = NormalizeAddonText((s.Name ?? "") + (s.Title ?? "") + (s.Description ?? "")).ToLowerInvariant();
            return all.Contains("\u26a1") || all.Contains("[rd+]") || all.Contains("[ad+]") || all.Contains("[pm+]") ||
                   all.Contains("cached") || all.Contains("downloaded") || all.Contains("tb+") ||
                   all.Contains("\U0001F4e5") || all.Contains("instant") || all.Contains("[debrid]") ||
                   all.Contains("real-debrid") || all.Contains("all-debrid") || all.Contains("premiumize");
        }

        private static string? ExtractSize(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(input, @"\d+(\.\d+)?\s*(GB|MB|MiB|GiB|TB)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Value : null;
        }

        private static string ParseQuality(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var qualities = new[] { "2160p", "1080p", "720p", "480p", "360p", "4K", "HDR", "HEVC", "x265", "x264", "BluRay", "WEB-DL", "WEBRip", "HDTV", "CAM", "TS", "SCR" };
            foreach (var q in qualities)
            {
                if (text.Contains(q, StringComparison.OrdinalIgnoreCase)) return q;
            }
            return "";
        }

        private static string NormalizeAddonText(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;

            var text = input
                .Replace("\u00e2\u201a\u00ac", "\u2022")
                .Replace("\u00e2\u201a\u201d", "-")
                .Replace("\u00c2", "");

            return text;
        }

        public static StremioAddonViewModel CloneAddonViewModel(StremioAddonViewModel source)
        {
            return new StremioAddonViewModel
            {
                Name = source.Name,
                AddonUrl = source.AddonUrl,
                IsLoading = source.IsLoading,
                SortIndex = source.SortIndex,
                Streams = source.Streams?.Select(CloneStreamViewModel).ToList() ?? new List<StremioStreamViewModel>()
            };
        }

        public static StremioStreamViewModel CloneStreamViewModel(StremioStreamViewModel source)
        {
            return new StremioStreamViewModel
            {
                Title = source.Title,
                Name = source.Name,
                ProviderText = source.ProviderText,
                AddonName = source.AddonName,
                Url = source.Url,
                Externalurl = source.Externalurl,
                Quality = source.Quality,
                Size = source.Size,
                IsCached = source.IsCached,
                OriginalStream = source.OriginalStream,
                IsActive = source.IsActive
            };
        }

        #endregion

        #region Nested Types

        internal sealed class ProcessedAddon
        {
            public string Name { get; set; } = string.Empty;
            public string AddonUrl { get; set; } = string.Empty;
            public List<ProcessedStream> Streams { get; set; } = new();
            public bool IsLoading { get; set; }
            public int SortIndex { get; set; }
        }

        internal sealed class ProcessedStream
        {
            public string Title { get; set; } = string.Empty;
            public string? Name { get; set; }
            public string? ProviderText { get; set; }
            public string? AddonName { get; set; }
            public string? AddonUrl { get; set; }
            public string? Url { get; set; }
            public string? Externalurl { get; set; }
            public string Quality { get; set; } = string.Empty;
            public string? Size { get; set; }
            public bool IsCached { get; set; }
            public StremioStream? OriginalStream { get; set; }
            public bool IsActive { get; set; }
            public int? IptvStreamId { get; set; }
            public int? IptvSeriesId { get; set; }
        }

        internal sealed class StremioSourcesCacheEntry
        {
            public List<StremioAddonViewModel> Addons { get; set; } = new();
            public bool IsComplete { get; set; }
        }

        /// <summary>
        /// Exposes the internal cache for integration with existing page code.
        /// </summary>
        public Dictionary<string, StremioSourcesCacheEntry> Cache => _cache;

        #endregion
    }
}
