using System;
using Windows.Storage;

namespace ModernIPTVPlayer
{
    public static class AppSettings
    {
        private const string PlaylistsKey = "PlaylistsJson";
        private const string LoginTypeKey = "LastLoginType"; // 0 = M3U, 1 = Xtream
        private const string LastPlaylistIdKey = "LastPlaylistId";

        private static ApplicationDataContainer Settings => ApplicationData.Current.LocalSettings;

        public static Guid? LastPlaylistId
        {
            get
            {
                var val = Settings.Values[LastPlaylistIdKey] as string;
                return Guid.TryParse(val, out var guid) ? guid : null;
            }
            set => Settings.Values[LastPlaylistIdKey] = value?.ToString();
        }

        // Migration and Collection Support
        public static string PlaylistsJson
        {
            get
            {
                var json = Settings.Values[PlaylistsKey] as string;
                if (string.IsNullOrEmpty(json))
                {
                    // Try to migrate old data
                    json = TryMigrateOldData();
                }
                return json ?? "[]";
            }
            set => Settings.Values[PlaylistsKey] = value;
        }

        private static string TryMigrateOldData()
        {
            const string PlaylistUrlKey = "PlaylistUrl";
            const string HostKey = "XtreamHost";
            const string UsernameKey = "XtreamUsername";
            const string PasswordKey = "XtreamPassword";

            var oldUrl = Settings.Values[PlaylistUrlKey] as string;
            var oldHost = Settings.Values[HostKey] as string;
            var oldUser = Settings.Values[UsernameKey] as string;
            var oldPass = Settings.Values[PasswordKey] as string;
            var type = (Settings.Values[LoginTypeKey] as int?) ?? 0;

            if (string.IsNullOrEmpty(oldUrl) && string.IsNullOrEmpty(oldHost)) return "[]";

            var playlist = new Playlist
            {
                Name = "Varsayılan Playlist",
                Type = (PlaylistType)type,
                Url = oldUrl ?? "",
                Host = oldHost ?? "",
                Username = oldUser ?? "",
                Password = oldPass ?? ""
            };

            var list = new System.Collections.Generic.List<Playlist> { playlist };
            var json = System.Text.Json.JsonSerializer.Serialize(list);
            Settings.Values[PlaylistsKey] = json;

            // Clear old keys
            Settings.Values.Remove(PlaylistUrlKey);
            Settings.Values.Remove(HostKey);
            Settings.Values.Remove(UsernameKey);
            Settings.Values.Remove(PasswordKey);

            return json;
        }

        public static int LastLoginType
        {
            get => (Settings.Values[LoginTypeKey] as int?) ?? 0;
            set => Settings.Values[LoginTypeKey] = value;
        }
    }
}
