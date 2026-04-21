using System;
using System.Linq;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Services;

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
