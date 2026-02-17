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

        // Cache Settings
        private const string CacheIntervalKey = "CacheIntervalMinutes";
        private const string AutoCacheKey = "IsAutoCacheEnabled";
        private const string LastLiveKey = "LastLiveCacheTime";
        private const string LastVodKey = "LastVodCacheTime";
        private const string LastSeriesKey = "LastSeriesCacheTime";

        public static int CacheIntervalMinutes
        {
            get => (Settings.Values[CacheIntervalKey] as int?) ?? 1440; // Default 24 Hours
            set => Settings.Values[CacheIntervalKey] = value;
        }

        public static bool IsAutoCacheEnabled
        {
            get => (Settings.Values[AutoCacheKey] as bool?) ?? true;
            set => Settings.Values[AutoCacheKey] = value;
        }

        public static DateTime LastLiveCacheTime
        {
            get => DateTime.FromBinary((Settings.Values[LastLiveKey] as long?) ?? 0);
            set => Settings.Values[LastLiveKey] = value.ToBinary();
        }

        public static DateTime LastVodCacheTime
        {
            get => DateTime.FromBinary((Settings.Values[LastVodKey] as long?) ?? 0);
            set => Settings.Values[LastVodKey] = value.ToBinary();
        }

        public static DateTime LastSeriesCacheTime
        {
            get => DateTime.FromBinary((Settings.Values[LastSeriesKey] as long?) ?? 0);
            set => Settings.Values[LastSeriesKey] = value.ToBinary();
        }

        private const string LastLiveCategoryKey = "LastLiveCategoryId";
        public static string LastLiveCategoryId
        {
            get => (Settings.Values[LastLiveCategoryKey] as string);
            set => Settings.Values[LastLiveCategoryKey] = value;
        }

        private const string LastVodCategoryKey = "LastVodCategoryId";
        public static string LastVodCategoryId
        {
            get => (Settings.Values[LastVodCategoryKey] as string);
            set => Settings.Values[LastVodCategoryKey] = value;
        }

        private const string LastSeriesCategoryKey = "LastSeriesCategoryId";
        public static string LastSeriesCategoryId
        {
            get => (Settings.Values[LastSeriesCategoryKey] as string);
            set => Settings.Values[LastSeriesCategoryKey] = value;
        }

        private const string IsAutoProbeEnabledKey = "IsAutoProbeEnabled";
        public static bool IsAutoProbeEnabled
        {
            get => (Settings.Values[IsAutoProbeEnabledKey] as bool?) ?? true;
            set => Settings.Values[IsAutoProbeEnabledKey] = value;
        }

        // Buffer Settings
        private const string PrebufferEnabledKey = "IsPrebufferEnabled";
        private const string PrebufferSecondsKey = "PrebufferSeconds";
        private const string BufferSecondsKey = "BufferSeconds";

        public static bool IsPrebufferEnabled
        {
            get => (Settings.Values[PrebufferEnabledKey] as bool?) ?? true;
            set => Settings.Values[PrebufferEnabledKey] = value;
        }

        public static int PrebufferSeconds
        {
            get => (Settings.Values[PrebufferSecondsKey] as int?) ?? 15;
            set => Settings.Values[PrebufferSecondsKey] = value;
        }

        public static int BufferSeconds
        {
            get => (Settings.Values[BufferSecondsKey] as int?) ?? 60;
            set => Settings.Values[BufferSecondsKey] = value;
        }

        // TMDB Settings
        private const string TmdbApiKeyKey = "TmdbApiKey";
        private const string IsTmdbEnabledKey = "IsTmdbEnabled";

        public static string TmdbApiKey
        {
            get => (Settings.Values[TmdbApiKeyKey] as string) ?? "";
            set => Settings.Values[TmdbApiKeyKey] = value;
        }

        public static bool IsTmdbEnabled
        {
            get => (Settings.Values[IsTmdbEnabledKey] as bool?) ?? false;
            set => Settings.Values[IsTmdbEnabledKey] = value;
        }

        private const string DefaultStartupPageKey = "DefaultStartupPage";
        public static string DefaultStartupPage
        {
            get => (Settings.Values[DefaultStartupPageKey] as string) ?? "MoviesPage";
            set => Settings.Values[DefaultStartupPageKey] = value;
        }

        // Player Settings
        private const string PlayerSettingsKey = "PlayerSettingsJson";
        
        public static ModernIPTVPlayer.Models.PlayerSettings PlayerSettings
        {
            get
            {
                var json = Settings.Values[PlayerSettingsKey] as string;
                if (string.IsNullOrEmpty(json))
                {
                    // Return default Balanced profile if nothing saved
                    return ModernIPTVPlayer.Models.PlayerSettings.GetDefault(ModernIPTVPlayer.Models.PlayerProfile.Balanced);
                }
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<ModernIPTVPlayer.Models.PlayerSettings>(json);
                }
                catch
                {
                    return ModernIPTVPlayer.Models.PlayerSettings.GetDefault(ModernIPTVPlayer.Models.PlayerProfile.Balanced);
                }
            }
            set
            {
                var json = System.Text.Json.JsonSerializer.Serialize(value);
                Settings.Values[PlayerSettingsKey] = json;
            }
        }
    }
}
