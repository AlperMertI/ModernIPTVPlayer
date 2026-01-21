using Windows.Storage;

namespace ModernIPTVPlayer
{
    public static class AppSettings
    {
        private const string PlaylistUrlKey = "PlaylistUrl";
        private const string HostKey = "XtreamHost";
        private const string UsernameKey = "XtreamUsername";
        private const string PasswordKey = "XtreamPassword";
        private const string LoginTypeKey = "LastLoginType"; // 0 = M3U, 1 = Xtream

        private static ApplicationDataContainer Settings => ApplicationData.Current.LocalSettings;

        // 0 for M3U, 1 for Xtream
        public static int LastLoginType
        {
            get => (Settings.Values[LoginTypeKey] as int?) ?? 0;
            set => Settings.Values[LoginTypeKey] = value;
        }

        public static string? SavedPlaylistUrl
        {
            get => Settings.Values[PlaylistUrlKey] as string;
            set
            {
                if (value == null) Settings.Values.Remove(PlaylistUrlKey);
                else Settings.Values[PlaylistUrlKey] = value;
            }
        }

        public static string? SavedHost
        {
            get => Settings.Values[HostKey] as string;
            set
            {
                if (value == null) Settings.Values.Remove(HostKey);
                else Settings.Values[HostKey] = value;
            }
        }

        public static string? SavedUsername
        {
            get => Settings.Values[UsernameKey] as string;
            set
            {
                if (value == null) Settings.Values.Remove(UsernameKey);
                else Settings.Values[UsernameKey] = value;
            }
        }

        public static string? SavedPassword
        {
            get => Settings.Values[PasswordKey] as string;
            set
            {
                if (value == null) Settings.Values.Remove(PasswordKey);
                else Settings.Values[PasswordKey] = value;
            }
        }
    }
}
