using System;
using Windows.Storage;
using ModernIPTVPlayer.Models.Common;
using ModernIPTVPlayer.Services.Json;

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
            get => Settings.Values[PlaylistsKey] as string ?? "[]";
            set => Settings.Values[PlaylistsKey] = value;
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

        public static event Action CacheSettingsChanged;

        public static int CacheIntervalMinutes
        {
            get => (Settings.Values[CacheIntervalKey] as int?) ?? 1440; // Default 24 Hours
            set 
            { 
                Settings.Values[CacheIntervalKey] = value;
                CacheSettingsChanged?.Invoke();
            }
        }

        public static bool IsAutoCacheEnabled
        {
            get => (Settings.Values[AutoCacheKey] as bool?) ?? true;
            set 
            { 
                Settings.Values[AutoCacheKey] = value;
                CacheSettingsChanged?.Invoke();
            }
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
        private const string ProbingWorkerCountKey = "ProbingWorkerCount";

        public static bool IsAutoProbeEnabled
        {
            get => (Settings.Values[IsAutoProbeEnabledKey] as bool?) ?? false;
            set => Settings.Values[IsAutoProbeEnabledKey] = value;
        }

        public static int ProbingWorkerCount
        {
            get => (Settings.Values[ProbingWorkerCountKey] as int?) ?? 3;
            set => Settings.Values[ProbingWorkerCountKey] = value;
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

        private const string MaxBufferMegabytesKey = "MaxBufferMegabytes";
        public static int MaxBufferMegabytes
        {
            get => (Settings.Values[MaxBufferMegabytesKey] as int?) ?? 256;
            set => Settings.Values[MaxBufferMegabytesKey] = value;
        }

        private const string SeekForwardSecondsKey = "SeekForwardSeconds";
        public static int SeekForwardSeconds
        {
            get => (Settings.Values[SeekForwardSecondsKey] as int?) ?? 10;
            set => Settings.Values[SeekForwardSecondsKey] = value;
        }

        private const string SeekBackwardSecondsKey = "SeekBackwardSeconds";
        public static int SeekBackwardSeconds
        {
            get => (Settings.Values[SeekBackwardSecondsKey] as int?) ?? 10;
            set => Settings.Values[SeekBackwardSecondsKey] = value;
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

        private const string TmdbLanguageKey = "TmdbLanguage";
        public static string TmdbLanguage
        {
            get => (Settings.Values[TmdbLanguageKey] as string) ?? "tr-TR";
            set => Settings.Values[TmdbLanguageKey] = value;
        }

        // AIOMetadata
        private const string CustomAioMetadataUrlKey = "CustomAioMetadataUrl";
        public static string? CustomAioMetadataUrl
        {
            get => Settings.Values[CustomAioMetadataUrlKey] as string;
            set => Settings.Values[CustomAioMetadataUrlKey] = value;
        }

        public static string AioMetadataUrl
        {
            get
            {
                var url = CustomAioMetadataUrl;
                if (!string.IsNullOrEmpty(url))
                {
                    // Strip /manifest.json if user pasted the full URL
                    return url.TrimEnd('/').Replace("/manifest.json", "");
                }
                return "https://aiometadatafortheweebs.midnightignite.me/stremio/0925c97f-be68-4b15-a37a-8740a523c713";
            }
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
                    return System.Text.Json.JsonSerializer.Deserialize(json, AppJsonContext.Default.PlayerSettings);
                }
                catch
                {
                    return ModernIPTVPlayer.Models.PlayerSettings.GetDefault(ModernIPTVPlayer.Models.PlayerProfile.Balanced);
                }
            }
            set
            {
                var json = System.Text.Json.JsonSerializer.Serialize(value, AppJsonContext.Default.PlayerSettings);
                Settings.Values[PlayerSettingsKey] = json;
            }
        }
        // Trailer Settings
        private const string TrailerQualityKey = "TrailerQuality";
        public static int TrailerQuality
        {
            get => (Settings.Values[TrailerQualityKey] as int?) ?? 1; // Default to 1 (Balanced/720p)
            set => Settings.Values[TrailerQualityKey] = value;
        }
    }
}
