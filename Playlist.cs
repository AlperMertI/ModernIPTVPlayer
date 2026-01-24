using System;

namespace ModernIPTVPlayer
{
    public enum PlaylistType
    {
        M3u,
        XtreamCodes
    }

    public class Playlist
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public PlaylistType Type { get; set; } = PlaylistType.M3u;

        // M3U specific
        public string Url { get; set; } = string.Empty;

        // Xtream Codes specific
        public string Host { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        public string Description => Type == PlaylistType.M3u ? Url : Host;
        public bool IsLastUsed => AppSettings.LastPlaylistId == Id;
    }
}
