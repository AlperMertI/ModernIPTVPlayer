using System;

namespace ModernIPTVPlayer.Models.Common
{
    public enum PlaylistType
    {
        M3u,
        XtreamCodes
    }

    [Microsoft.UI.Xaml.Data.Bindable]
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
        public string ExpiryDate { get; set; } = string.Empty;
        
        public bool IsExpired
        {
            get
            {
                if (string.IsNullOrEmpty(ExpiryDate) || ExpiryDate == "Sonsuz" || ExpiryDate == "Belirsiz") return false;
                if (DateTime.TryParseExact(ExpiryDate, "dd.MM.yyyy", null, System.Globalization.DateTimeStyles.None, out DateTime expDate))
                {
                    return expDate < DateTime.Now;
                }
                return false;
            }
        }

        public string Description => Type == PlaylistType.M3u ? Url : Host;
        public bool IsLastUsed => AppSettings.LastPlaylistId == Id;
        public bool IsActiveBadgeVisible => IsLastUsed && !IsExpired;
    }
}
