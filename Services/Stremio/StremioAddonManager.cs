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
        private static readonly System.Threading.Lock _instanceLock = new();
        private static StremioAddonManager _instance;
        public static StremioAddonManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new StremioAddonManager();
                    }
                }
                return _instance;
            }
        }

        private const string ADDONS_KEY = "StremioInstalledAddons";
        private const string MANIFEST_CACHE_FILE = "stremio_manifests.json";
        
        private readonly System.Threading.Lock _addonLock = new();
        private List<string> _addonUrls = new();
        private Dictionary<string, StremioManifest> _manifestCache = new Dictionary<string, StremioManifest>();
        
        private bool _isInitializing = false;
        private readonly System.Threading.Lock _initLock = new();

        // Manifests-from-disk readiness signal — consumers (discovery) can await this to avoid the startup
        // race where GetAddonsWithManifests() returns entries with null manifests before disk load finishes.
        private readonly TaskCompletionSource<bool> _manifestsLoadedFromDisk = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ManifestsLoadedFromDiskTask => _manifestsLoadedFromDisk.Task;

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
            lock (_initLock)
            {
                if (_isInitializing) return;
                _isInitializing = true;
            }

            try
            {
                await LoadManifestsFromDiskAsync();
                _manifestsLoadedFromDisk.TrySetResult(true);
                if (_manifestCache.Count > 0)
                {
                    // Notify UI immediately that we have cached manifests
                    AddonsChanged?.Invoke(this, EventArgs.Empty);
                }
                await RefreshManifestsAsync();
            }
            finally
            {
                lock (_initLock) { _isInitializing = false; }
                // Even on failure, unblock waiters so discovery doesn't deadlock.
                _manifestsLoadedFromDisk.TrySetResult(false);
            }
        }

        private void LoadAddons()
        {
            lock (_addonLock)
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                if (localSettings.Values.ContainsKey(ADDONS_KEY))
                {
                    string json = localSettings.Values[ADDONS_KEY] as string;
                    _addonUrls = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                }
                else
                {
                    // Default: AIOMetadata (rich metadata, cast photos, streaming catalogs) + Cinemeta + OpenSubtitles v3
                    _addonUrls = new List<string>
                    {
                        AppSettings.AioMetadataUrl.TrimEnd('/'),
                        "https://v3-cinemeta.strem.io",
                        "https://opensubtitles-v3.strem.io"
                    };
                    SaveAddons();
                }
            }
        }

        private void SaveAddons()
        {
            lock (_addonLock)
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                string json = JsonSerializer.Serialize(_addonUrls);
                localSettings.Values[ADDONS_KEY] = json;
            }
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
                        lock (_addonLock)
                        {
                            _manifestCache = cache;
                        }
                        AppLogger.Info($"[StremioAddonManager] Loaded {cache.Count} manifests from disk.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[StremioAddonManager] Error loading manifest cache: {ex.Message}");
            }
        }

        private async Task SaveManifestsToDiskAsync()
        {
            try
            {
                string json;
                lock (_addonLock)
                {
                    json = JsonSerializer.Serialize(_manifestCache);
                }

                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(MANIFEST_CACHE_FILE, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[StremioAddonManager] Error saving manifest cache: {ex.Message}");
            }
        }
        
        private async Task RefreshManifestsAsync()
        {
            var client = HttpHelper.Client;
            
            List<string> urls;
            lock (_addonLock) { urls = _addonUrls.ToList(); }

            var tasks = urls.Select(async url =>
            {
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
                            lock (_addonLock)
                            {
                                _manifestCache[url] = manifest;
                            }
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[StremioAddonManager] Failed to fetch manifest for {url}: {ex.Message}");
                }
                return false;
            });

            var results = await Task.WhenAll(tasks);
            bool changed = results.Any(r => r);

            if (changed)
            {
                await SaveManifestsToDiskAsync();
                AddonsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public List<string> GetAddons()
        {
            lock (_addonLock)
            {
                var urls = _addonUrls.ToList();
                // Ensure AIOMetadata is always first (highest priority)
                string aioUrl = AppSettings.AioMetadataUrl.TrimEnd('/');
                urls.Remove(aioUrl);
                urls.Insert(0, aioUrl);
                return urls;
            }
        }
        
        public List<string> GetAddonsByResource(string resourceName)
        {
            var result = new List<string>();
            lock (_addonLock)
            {
                foreach (var url in _addonUrls)
                {
                    if (SupportsResourceInternal(url, resourceName))
                    {
                        result.Add(url);
                    }
                }
            }
            return result;
        }

        public bool SupportsResource(string url, string resourceName)
        {
            lock (_addonLock)
            {
                return SupportsResourceInternal(url, resourceName);
            }
        }

        private bool SupportsResourceInternal(string url, string resourceName)
        {
            if (!_manifestCache.TryGetValue(url, out var manifest) || manifest == null)
            {
                // Fallback: If no manifest cached yet, assume official-looking ones support catalog/meta
                bool looksOfficial = url.Contains("cinemeta") || url.Contains("strem.io");
                return looksOfficial;
            }

            if (manifest.Resources == null) return false;

            foreach (var res in manifest.Resources)
            {
                if (string.Equals(res?.Name, resourceName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        
        public List<(string BaseUrl, StremioManifest Manifest)> GetAddonsWithManifests()
        {
            var list = new List<(string, StremioManifest)>();
            lock (_addonLock)
            {
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
            }
            return list;
        }

        public StremioManifest? GetManifest(string url)
        {
            lock (_addonLock)
            {
                if (_manifestCache.TryGetValue(url, out var manifest))
                    return manifest;
                return null;
            }
        }

        public async void AddAddon(string url)
        {
            bool added = false;
            lock (_addonLock)
            {
                if (!_addonUrls.Contains(url))
                {
                    _addonUrls.Add(url);
                    added = true;
                }
            }

            if (added)
            {
                SaveAddons();
                await RefreshManifestsAsync(); // Will fetch and save
                AddonsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void RemoveAddon(string url)
        {
            bool removed = false;
            lock (_addonLock)
            {
                if (_addonUrls.Contains(url))
                {
                    _addonUrls.Remove(url);
                    if (_manifestCache.ContainsKey(url)) _manifestCache.Remove(url);
                    removed = true;
                }
            }

            if (removed)
            {
                SaveAddons();
                _ = SaveManifestsToDiskAsync();
                AddonsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void UpdateAddonOrder(List<string> orderedUrls)
        {
            lock (_addonLock)
            {
                _addonUrls = orderedUrls;
            }
            SaveAddons();
            AddonsChanged?.Invoke(this, EventArgs.Empty);
        }

        public string GetAddonName(string url)
        {
            lock (_addonLock)
            {
                if (_manifestCache.TryGetValue(url, out var manifest) && manifest != null)
                {
                    return manifest.Name;
                }
            }
            return null;
        }

        public string GetAddonIcon(string url)
        {
            lock (_addonLock)
            {
                if (_manifestCache.TryGetValue(url, out var manifest) && manifest != null && !string.IsNullOrEmpty(manifest.Logo))
                {
                    return manifest.Logo;
                }
            }
            return null;
        }
    }
}
