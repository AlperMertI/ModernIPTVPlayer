using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Windows.Storage;

namespace ModernIPTVPlayer.Services.Stremio
{
    public class StremioAddonManager
    {
        private static StremioAddonManager _instance;
        public static StremioAddonManager Instance => _instance ??= new StremioAddonManager();

        private const string ADDONS_KEY = "StremioInstalledAddons";
        private List<string> _addonUrls;

        private StremioAddonManager()
        {
            LoadAddons();
        }

        private void LoadAddons()
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            if (localSettings.Values.ContainsKey(ADDONS_KEY))
            {
                string json = localSettings.Values[ADDONS_KEY] as string;
                _addonUrls = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            else
            {
                // Default: Cinemeta (Official Catalog)
                _addonUrls = new List<string>
                {
                    "https://v3-cinemeta.strem.io"
                };
                SaveAddons();
            }
        }

        private void SaveAddons()
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            string json = JsonSerializer.Serialize(_addonUrls);
            localSettings.Values[ADDONS_KEY] = json;
        }

        public List<string> GetAddons() => _addonUrls;

        public void AddAddon(string url)
        {
            if (!_addonUrls.Contains(url))
            {
                _addonUrls.Add(url);
                SaveAddons();
            }
        }

        public void RemoveAddon(string url)
        {
            if (_addonUrls.Contains(url))
            {
                _addonUrls.Remove(url);
                SaveAddons();
            }
        }
    }
}
