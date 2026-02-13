using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Services.Stremio
{
    public class StremioAddonManager
    {
        private static StremioAddonManager _instance;
        public static StremioAddonManager Instance => _instance ??= new StremioAddonManager();

        private const string ADDONS_KEY = "StremioInstalledAddons";
        private List<string> _addonUrls;
        private Dictionary<string, StremioManifest> _manifestCache = new Dictionary<string, StremioManifest>();

        private StremioAddonManager()
        {
            LoadAddons();
            _ = RefreshManifestsAsync();
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
        
        private async Task RefreshManifestsAsync()
        {
            var client = new System.Net.Http.HttpClient();
            foreach (var url in _addonUrls)
            {
                if (_manifestCache.ContainsKey(url)) continue;

                try
                {
                    string manifestUrl = $"{url.TrimEnd('/')}/manifest.json";
                    string json = await client.GetStringAsync(manifestUrl);
                    var manifest = JsonSerializer.Deserialize<StremioManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (manifest != null)
                    {
                        _manifestCache[url] = manifest;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[StremioAddonManager] Failed to fetch manifest for {url}: {ex.Message}");
                }
            }
        }

        public List<string> GetAddons() => _addonUrls;
        
        public List<(string BaseUrl, StremioManifest Manifest)> GetAddonsWithManifests()
        {
            var list = new List<(string, StremioManifest)>();
            foreach(var url in _addonUrls)
            {
                if (_manifestCache.TryGetValue(url, out var manifest))
                {
                    list.Add((url, manifest));
                }
                else
                {
                    // If manifest isn't loaded yet, return null for it or try to fetch? 
                    // For now, return null to avoid blocking, but StremioService should handle nulls.
                    list.Add((url, null));
                }
            }
            return list;
        }

        public async void AddAddon(string url)
        {
            if (!_addonUrls.Contains(url))
            {
                _addonUrls.Add(url);
                SaveAddons();
                await RefreshManifestsAsync();
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
