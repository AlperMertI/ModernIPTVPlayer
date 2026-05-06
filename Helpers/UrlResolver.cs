using System;
using System.Linq;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Stremio;

namespace ModernIPTVPlayer.Helpers
{
    public static class UrlResolver
    {
        /// <summary>
        /// Resolves internal protocols like iptv:// to real HTTP URLs asynchronously.
        /// </summary>
        public static async Task<string> ResolveUrlAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            
            // [RESILIENCE] Handle "Wrapped" URLs from community addons that might use internal/unresolvable hostnames.
            // These often return URLs like http://[container-id]/extract?url=[real-url].
            if (url.Contains("/extract/") && url.Contains("url="))
            {
                try
                {
                    var uri = new Uri(url);
                    string host = uri.Host;

                    // 1. REPAIR: Try to fix truncated hostnames by matching against installed addons
                    // (e.g. 87d6a6ef6b58-webstreamrmbg -> 87d6a6ef6b58-webstreamrmbg.baby-beamup.club)
                    var addons = StremioAddonManager.Instance.GetAddons();
                    foreach (var addonBaseUrl in addons)
                    {
                        try
                        {
                            if (Uri.TryCreate(addonBaseUrl, UriKind.Absolute, out var addonUri))
                            {
                                // If the addon host starts with the broken host + a dot, it's a match!
                                if (addonUri.Host.StartsWith(host + ".", StringComparison.OrdinalIgnoreCase))
                                {
                                    var builder = new UriBuilder(uri);
                                    builder.Host = addonUri.Host;
                                    string repairedUrl = builder.ToString();
                                    AppLogger.Info($"[UrlResolver] Repaired truncated hostname: {host} -> {addonUri.Host}");
                                    url = repairedUrl;
                                    // Update for the next step (unwrapping is still a fallback if repair fails)
                                    uri = new Uri(url);
                                    host = uri.Host;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }

                    // 2. UNWRAP: Decide whether to use the nested URL directly or keep the (repaired) extractor.
                    string nestedUrl = null;
                    int urlIdx = url.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
                    if (urlIdx > 0)
                    {
                        nestedUrl = url.Substring(urlIdx + 4);
                        int ampersandIdx = nestedUrl.IndexOf('&');
                        if (ampersandIdx > 0) nestedUrl = nestedUrl.Substring(0, ampersandIdx);
                        nestedUrl = Uri.UnescapeDataString(nestedUrl);
                    }

                    if (!string.IsNullOrEmpty(nestedUrl))
                    {
                        // Proactively unwrap if:
                        // A) The host is STILL internal/unresolvable (no dots).
                        // B) The nested URL looks like a direct media link (ends in .mkv, .mp4, etc.)
                        // C) The nested URL is from a known direct-link API (like aoneroom).
                        bool isUnresolvable = !host.Contains(".") && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
                        
                        bool isDirectMedia = nestedUrl.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || 
                                           nestedUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                                           nestedUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                                           nestedUrl.Contains("/download") || 
                                           nestedUrl.Contains("/stream");

                        if (isDirectMedia)
                        {
                            string decodedUrl = Uri.UnescapeDataString(nestedUrl).TrimEnd('.');
                            if (Uri.TryCreate(decodedUrl, UriKind.Absolute, out _))
                            {
                                AppLogger.Info($"[UrlResolver] Smart Unwrap: Bypassing extractor for direct media: {decodedUrl}");
                                url = decodedUrl;
                            }
                        }
                        else if (isUnresolvable)
                        {
                            AppLogger.Warn($"[UrlResolver] Unresolvable host ({host}) for a potential landing page. Skipping unwrap.");
                        }
                    }
                }
                catch { /* Fallback to original URL */ }
            }

            // Basic cleanup: some servers dislike explicit :80
            url = url.Replace(":80/", "/");

            if (url.StartsWith("iptv://", StringComparison.OrdinalIgnoreCase))
            {
                if (url.StartsWith("iptv://series/", StringComparison.OrdinalIgnoreCase))
                {
                    // Series resolution requires season/episode context, usually handled by the caller
                    // but we can have a fallback here if we want.
                    return url; 
                }

                string streamIdStr = url.Substring(7);
                if (int.TryParse(streamIdStr, out int streamId) && App.CurrentLogin != null)
                {
                    var playlistId = App.CurrentLogin.PlaylistUrl ?? "default";

                    // Try VOD
                    var vods = await ContentCacheService.Instance.LoadCacheAsync<VodStream>(playlistId, "vod_streams");
                    var match = vods?.FirstOrDefault(v => v.StreamId == streamId);
                    if (match != null)
                    {
                        return match.GetStreamUrl(App.CurrentLogin);
                    }

                    // Try Live
                    var lives = await ContentCacheService.Instance.LoadCacheAsync<LiveStream>(playlistId, "live_streams");
                    var liveMatch = lives?.FirstOrDefault(l => l.StreamId == streamId);
                    if (liveMatch != null)
                    {
                        return liveMatch.GetStreamUrl(App.CurrentLogin);
                    }
                }
            }

            return url;
        }

        public static string GetStreamUrl(this VodStream stream, ModernIPTVPlayer.Models.Iptv.LoginParams login)
        {
            if (stream == null || login == null) return string.Empty;
            string host = login.Host?.TrimEnd('/') ?? string.Empty;
            string ext = stream.ContainerExtension ?? "mkv";
            if (!ext.StartsWith(".")) ext = "." + ext;
            return $"{host}/movie/{login.Username}/{login.Password}/{stream.StreamId}{ext}";
        }

        public static string GetStreamUrl(this LiveStream stream, ModernIPTVPlayer.Models.Iptv.LoginParams login)
        {
            if (stream == null || login == null) return string.Empty;
            string host = login.Host?.TrimEnd('/') ?? string.Empty;
            string ext = string.IsNullOrEmpty(stream.ContainerExtension) ? "ts" : stream.ContainerExtension;
            return $"{host}/live/{login.Username}/{login.Password}/{stream.StreamId}.{ext}";
        }

        public static string GetSeriesStreamUrl(int epId, string containerExt, ModernIPTVPlayer.Models.Iptv.LoginParams login)
        {
            if (login == null) return string.Empty;
            string host = login.Host?.TrimEnd('/') ?? string.Empty;
            string ext = containerExt ?? "mp4";
            if (!ext.StartsWith(".")) ext = "." + ext;
            return $"{host}/series/{login.Username}/{login.Password}/{epId}{ext}";
        }
    }
}
