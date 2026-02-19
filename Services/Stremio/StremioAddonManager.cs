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
        private const string MANIFEST_CACHE_FILE = "stremio_manifests.json";
        private List<string> _addonUrls;
        private Dictionary<string, StremioManifest> _manifestCache = new Dictionary<string, StremioManifest>();

        public event EventHandler AddonsChanged;

        private StremioAddonManager()
        {
            LoadAddons();
            // Load cached manifests strictly synchronously if possible or wait?
            // Constructors can't wait. We'll fire off the load.
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            await LoadManifestsFromDiskAsync();
            if (_manifestCache.Count > 0)
            {
                // Notify UI immediately that we have cached manifests
                AddonsChanged?.Invoke(this, EventArgs.Empty);
            }
            await RefreshManifestsAsync();
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

        private async Task LoadManifestsFromDiskAsync()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(MANIFEST_CACHE_FILE);
                if (item != null)
                {
                    var file = await folder.GetFileAsync(MANIFEST_CACHE_FILE);
                    string json = await FileIO.ReadTextAsync(file);
                    var cache = JsonSerializer.Deserialize<Dictionary<string, StremioManifest>>(json);
                    if (cache != null)
                    {
                        _manifestCache = cache;
                        System.Diagnostics.Debug.WriteLine($"[StremioAddonManager] Loaded {_manifestCache.Count} manifests from disk.");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioAddonManager] Error loading manifest cache: {ex.Message}");
            }
        }

        private async Task SaveManifestsToDiskAsync()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(MANIFEST_CACHE_FILE, CreationCollisionOption.ReplaceExisting);
                string json = JsonSerializer.Serialize(_manifestCache);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioAddonManager] Error saving manifest cache: {ex.Message}");
            }
        }
        
        private async Task RefreshManifestsAsync()
        {
            var client = new System.Net.Http.HttpClient();
            bool changed = false;

            foreach (var url in _addonUrls)
            {
                // If we already have it in memory, skip remote fetch for now?
                // Or maybe fetch to update? Let's treat memory cache as "good enough" for quick start,
                // but maybe verify if we shouldn't refresh periodically.
                // For now, only fetch if MISSING.
                if (_manifestCache.ContainsKey(url)) continue;

                try
                {
                    string manifestUrl = $"{url.TrimEnd('/')}/manifest.json";
                    
                    // 5 second timeout for manifest
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var response = await client.GetAsync(manifestUrl, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        var manifest = JsonSerializer.Deserialize<StremioManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (manifest != null)
                        {
                            _manifestCache[url] = manifest;
                            changed = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[StremioAddonManager] Failed to fetch manifest for {url}: {ex.Message}");
                }
            }

            if (changed)
            {
                await SaveManifestsToDiskAsync();
                AddonsChanged?.Invoke(this, EventArgs.Empty);
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
                await RefreshManifestsAsync(); // Will fetch and save
                AddonsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void RemoveAddon(string url)
        {
            if (_addonUrls.Contains(url))
            {
                _addonUrls.Remove(url);
                SaveAddons();
                if (_manifestCache.ContainsKey(url)) _manifestCache.Remove(url);
                _ = SaveManifestsToDiskAsync();
                AddonsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void UpdateAddonOrder(List<string> orderedUrls)
        {
            _addonUrls = orderedUrls;
            SaveAddons();
            AddonsChanged?.Invoke(this, EventArgs.Empty);
        }

        public string GetAddonName(string url)
        {
            if (_manifestCache.TryGetValue(url, out var manifest) && manifest != null)
            {
                return manifest.Name;
            }
            return null;
        }

        public string GetAddonIcon(string url)
        {
             if (_manifestCache.TryGetValue(url, out var manifest) && manifest != null && !string.IsNullOrEmpty(manifest.Logo))
            {
                return manifest.Logo;
            }
            return null;
        }
    }
}
