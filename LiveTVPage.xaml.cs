using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Media.Imaging;
using MpvWinUI;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer
{
    public enum LiveSortMode
    {
        Default,
        Name,
        Quality,
        OnlineFirst,
        Recent
    }

    public sealed partial class LiveTVPage : Page
    {
        private LoginParams? _loginInfo;
        private HttpClient _httpClient;

        // Data Storage
        private LiveSortMode _currentSortMode = LiveSortMode.Default;
        private bool _isSortDescending = false;
        private List<LiveCategory> _allCategories = new();
        private List<LiveStream> _allChannels = new();
        
        // State
        private LiveCategory? _selectedCategory;
        private string _searchQuery = "";
        private bool _isSortedAscending = false;
        
        // UI References
        private ItemsWrapGrid? _itemsWrapGrid;
        
        // Clock & Recents
        private DispatcherTimer _clockTimer;
        private System.Collections.ObjectModel.ObservableCollection<LiveStream> _recentChannels = new();

        // Auto-Probe Queue
        private ConcurrentQueue<LiveStream> _probingQueue = new();
        private HashSet<string> _queuedUrls = new();
        private CancellationTokenSource _workerCts = new();
        private bool _isWorkerRunning = false;
        private bool _canAutoProbe = false;

        // SCROLL-BASED PROBING
        private ScrollViewer? _channelScrollViewer;

        public LiveTVPage()
        {
            this.InitializeComponent();
            _httpClient = HttpHelper.Client;
            
            // Start Clock
            StartClock();
            
            // Start Worker
            _ = StartProbingWorker();


            // Subscribe to Cache clearing
            Services.ProbeCacheService.Instance.CacheCleared += OnCacheCleared;
        }

        // ==========================================
        // SCROLL-BASED VISIBILITY DETECTION
        // ==========================================

        private T? FindChildOfType<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var nested = FindChildOfType<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void ChannelGridView_Loaded(object sender, RoutedEventArgs e)
        {
            _channelScrollViewer = FindChildOfType<ScrollViewer>(ChannelGridView);
            if (_channelScrollViewer != null)
            {
                _channelScrollViewer.ViewChanged += ChannelScrollViewer_ViewChanged;
            }
            // Initial probe for items visible on first load
            QueueVisibleItems();
        }

        private void ChannelScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            // Only act when scrolling STOPS (natural debounce)
            if (!e.IsIntermediate)
            {
                QueueVisibleItems();
            }
        }

        private void QueueVisibleItems()
        {
            if (!_canAutoProbe || _channelScrollViewer == null) return;
            if (ChannelGridView.ItemsSource is not List<LiveStream> list || list.Count == 0) return;

            double offset = _channelScrollViewer.VerticalOffset;
            double viewportHeight = _channelScrollViewer.ViewportHeight;

            // Get dimensions
            double itemHeight = 80; // Template Height (68) + margins
            double itemWidth = _itemsWrapGrid?.ItemWidth ?? 300;
            double gridWidth = Math.Max(1, ChannelGridView.ActualWidth - 48); // minus padding

            int itemsPerRow = Math.Max(1, (int)(gridWidth / itemWidth));

            // Calculate visible row range
            int firstRow = (int)(offset / itemHeight);
            int lastRow = (int)((offset + viewportHeight) / itemHeight) + 1; // +1 for partial

            // Calculate item index range
            int firstIndex = Math.Max(0, firstRow * itemsPerRow);
            int lastIndex = Math.Min(list.Count, (lastRow + 1) * itemsPerRow);

            // Queue items in visible range
            lock (_probingQueue)
            {
                for (int i = firstIndex; i < lastIndex; i++)
                {
                    var stream = list[i];
                    if (stream.IsOnline == null && !stream.IsProbing && !_queuedUrls.Contains(stream.StreamUrl))
                    {
                        _probingQueue.Enqueue(stream);
                        _queuedUrls.Add(stream.StreamUrl);
                    }
                }
            }
        }


        private void OnCacheCleared(object? sender, EventArgs e)
        {
            // Reset metadata for all loaded channels so UI updates immediately
            DispatcherQueue.TryEnqueue(() => 
            {
                _probingQueue.Clear();
                _queuedUrls.Clear();

                foreach (var s in _allChannels)
                {
                    s.Resolution = "";
                    s.Fps = "";
                    s.Codec = "";
                    s.Bitrate = 0;
                    s.IsHdr = false;
                    s.IsOnline = null; 
                }
                
                // Re-trigger probing for the current view
                QueueVisibleItems();
            });
        }

        private void StartClock()
        {
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) => 
            {
                if (ClockTextBlock != null)
                    ClockTextBlock.Text = DateTime.Now.ToString("HH:mm");
            };
            _clockTimer.Start();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            
            // 1. Initialize History FIRST
            await HistoryManager.Instance.InitializeAsync();
            LoadRecentChannels();

            // 2. Handle Login Parameters
            if (e.Parameter is LoginParams loginParams)
            {
                if (_loginInfo != null && _loginInfo.PlaylistUrl != loginParams.PlaylistUrl)
                {
                    _allCategories.Clear();
                    _allChannels.Clear();
                    CategoryListView.ItemsSource = null;
                    ChannelGridView.ItemsSource = null;
                }
                _loginInfo = loginParams;
            }
            else
            {
                _loginInfo = App.CurrentLogin;
            }

            if (_loginInfo == null)
            {
                LoginRequiredPanel.Visibility = Visibility.Visible;
                HeroSection.Visibility = Visibility.Collapsed;
                return;
            }
            else
            {
                LoginRequiredPanel.Visibility = Visibility.Collapsed;
            }

            // 3. Worker Lifecycle
            if (_workerCts.IsCancellationRequested)
            {
                _workerCts.Dispose();
                _workerCts = new CancellationTokenSource();
            }
            _ = StartProbingWorker();

            // 4. Data Loading
            if (_allCategories.Count > 0) return;

            if (!string.IsNullOrEmpty(_loginInfo.Host) && !string.IsNullOrEmpty(_loginInfo.Username))
            {
                await LoadXtreamCategoriesAsync();
            }
            else if (!string.IsNullOrEmpty(_loginInfo.PlaylistUrl))
            {
                await LoadM3uAsync(_loginInfo.PlaylistUrl);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // Cancel Worker
            _workerCts.Cancel();
        }

        // ==========================================
        // DATA LOADING
        // ==========================================
        private async Task LoadXtreamCategoriesAsync(bool ignoreCache = false)
        {
            try
            {
                SidebarLoadingRing.IsActive = true;
                MainLoadingRing.IsActive = true;
                EmptyStatePanel.Visibility = Visibility.Collapsed;

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string username = _loginInfo.Username;
                string password = _loginInfo.Password;
                string playlistId = AppSettings.LastPlaylistId?.ToString() ?? "default";

                // -------------------------------------------------------------
                // 1. CACHE STRATEGY: Project Zero Binary Bundle (Insanely Fast)
                // -------------------------------------------------------------
                List<LiveCategory> cachedCats = null;
                List<LiveStream> cachedStreams = null;
                bool cacheLoaded = false;
                var swLoad = System.Diagnostics.Stopwatch.StartNew();

                if (!ignoreCache)
                {
                    cachedCats = await Services.ContentCacheService.Instance.LoadCacheAsync<LiveCategory>(playlistId, "live_cats");
                    cachedStreams = await Services.ContentCacheService.Instance.LoadLiveStreamsBinaryAsync(playlistId);

                    if (cachedStreams == null)
                    {
                        cachedStreams = await Services.ContentCacheService.Instance.MigrateLiveStreamsJsonToBinaryAsync(playlistId);
                    }

                    cacheLoaded = (cachedCats != null && cachedStreams != null && cachedCats.Count > 0);
                }

                if (cacheLoaded)
                {
                    swLoad.Stop();
                    System.Diagnostics.Debug.WriteLine($"[LiveTVPage] PROJECT ZERO BINARY LOAD OK! Total: {swLoad.ElapsedMilliseconds}ms");
                    
                    // NEW v2.4: Initialize the ID-based Binary Probe Cache for this playlist
                    await Services.ProbeCacheService.Instance.InitializeForPlaylistAsync(playlistId);

                    _allCategories = cachedCats;
                    _allChannels = cachedStreams; // Raw list
                    
                    // 1. FAST RE-HYDRATION: Build URLs for all channels (Binary doesn't store them)
                    // This is essential for ProbeCache and playback!
                    Parallel.ForEach(_allChannels, s => 
                    {
                        string ext = string.IsNullOrEmpty(s.ContainerExtension) ? "ts" : s.ContainerExtension;
                        s.StreamUrl = $"{baseUrl}/live/{username}/{password}/{s.StreamId}.{ext}";
                    });

                    // 2. Hydrate Metadata from ID-Based ProbeCache (Fastest - No URLs needed!)
                    foreach (var s in _allChannels)
                    {
                        if (Services.ProbeCacheService.Instance.Get(s.StreamId) is Services.ProbeData pd)
                        {
                            s.Resolution = pd.Resolution;
                            s.Codec = pd.Codec;
                            s.Bitrate = pd.Bitrate;
                            s.Fps = pd.Fps;
                            s.IsHdr = pd.IsHdr;
                            s.IsOnline = true; 
                        }
                    }

                    // Re-link logic (same as fetch)
                     var allCat = _allCategories.FirstOrDefault(c => c.CategoryId == "-1");
                     if (allCat == null)
                     {
                         allCat = new LiveCategory { CategoryName = "Tüm Kanallar", CategoryId = "-1" };
                         _allCategories.Insert(0, allCat);
                     }
                     allCat.Channels = _allChannels;

                     foreach (var cat in _allCategories)
                     {
                         if (cat.CategoryId != "-1")
                            cat.Channels = _allChannels.Where(s => s.CategoryId == cat.CategoryId).ToList();
                     }

                    CategoryListView.ItemsSource = _allCategories;
                    CategoryListView.ItemsSource = _allCategories;
                    
                    // Restoration Logic
                    var lastId = AppSettings.LastLiveCategoryId;
                    var targetCat = _allCategories.FirstOrDefault(c => c.CategoryId == lastId) ?? allCat;

                    CategoryListView.SelectedItem = targetCat;
                    CategoryListView.ScrollIntoView(targetCat);
                    SelectCategory(targetCat);
                    
                    LoadRecentChannels();

                    SidebarLoadingRing.IsActive = false;
                    MainLoadingRing.IsActive = false;
                    
                    // Optional: Background Refresh check could happen here
                }

                // 2. NETWORK STRATEGY: If Cache Missing (or forced refresh needed)
                if (!cacheLoaded)
                {
                    string api = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_live_categories";
                    string json = await _httpClient.GetStringAsync(api);
                    var categories = HttpHelper.TryDeserializeList<LiveCategory>(json);

                    // Channel Fetch
                    string streamApi = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_live_streams";
                    string streamJson = await _httpClient.GetStringAsync(streamApi);
                    var streams = HttpHelper.TryDeserializeList<LiveStream>(streamJson);

                    if (streams != null)
                    {
                        // URL Construction
                        foreach (var s in streams)
                        {
                            s.StreamUrl = $"{baseUrl}/live/{username}/{password}/{s.StreamId}.ts";
                        }
                        
                        // 3. CACHE SAVE (Background)
                        // We save RAW lists. Re-linking happens on load.
                        _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "live_cats", categories);
                        _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "live_streams", streams);

                        // 4. UI Update
                        var allCat = new LiveCategory { CategoryName = "Tüm Kanallar", CategoryId = "-1" };
                        _allCategories = new List<LiveCategory> { allCat };
                        _allCategories.AddRange(categories);
                        _allChannels = streams;
                    
                        // Wait for Probe Cache to be ready (Race Condition fix)
                        await Services.ProbeCacheService.Instance.EnsureLoadedAsync();
                        
                        // Hydrate Metadata from ID-Based ProbeCache (Network Path v2.4)
                        foreach (var s in _allChannels)
                        {
                            if (Services.ProbeCacheService.Instance.Get(s.StreamId) is Services.ProbeData pd)
                            {
                                s.Resolution = pd.Resolution;
                                s.Codec = pd.Codec;
                                s.Bitrate = pd.Bitrate;
                                s.Fps = pd.Fps;
                                s.IsHdr = pd.IsHdr;
                                s.IsOnline = true; 
                            }
                        }
                        
                        allCat.Channels = _allChannels;
                        foreach (var cat in categories)
                        {
                            cat.Channels = _allChannels.Where(s => s.CategoryId == cat.CategoryId).ToList();
                        }

                        CategoryListView.ItemsSource = _allCategories;
                        CategoryListView.ItemsSource = _allCategories;

                        // Restoration Logic
                        var lastId = AppSettings.LastLiveCategoryId;
                        var targetCat = _allCategories.FirstOrDefault(c => c.CategoryId == lastId) ?? allCat;
                        
                        CategoryListView.SelectedItem = targetCat;
                        CategoryListView.ScrollIntoView(targetCat);
                        SelectCategory(targetCat);

                        LoadRecentChannels();
                    }
                }
            }
            catch (Exception ex)
            {
                await ShowMessageDialog("Hata", $"Kanal listesi yüklenemedi: {ex.Message}");
            }
            finally
            {
                SidebarLoadingRing.IsActive = false;
                MainLoadingRing.IsActive = false;
            }
        }

        private async Task LoadM3uAsync(string? url, bool ignoreCache = false)
        {
            // Similar logic for M3U...
             if (string.IsNullOrEmpty(url)) return;

            try
            {
                MainLoadingRing.IsActive = true;
                string m3uContent = await _httpClient.GetStringAsync(url);
                
                // Parse on background thread
                var categories = await Task.Run(() => ParseM3u(m3uContent));

                var allCat = new LiveCategory { CategoryName = "Tüm Kanallar", CategoryId = "-1" };
                
                 // Populate All Channels
                _allChannels = categories.SelectMany(c => c.Channels).ToList();
                allCat.Channels = _allChannels;

                _allCategories = new List<LiveCategory> { allCat };
                _allCategories.AddRange(categories);

                CategoryListView.ItemsSource = _allCategories;
                CategoryListView.SelectedIndex = 0;
                SelectCategory(allCat);
            }
            catch (Exception ex)
            {
                await ShowMessageDialog("Hata", ex.Message);
            }
            finally
            {
                MainLoadingRing.IsActive = false;
            }
        }
        
        private List<LiveCategory> ParseM3u(string content)
        {
            var result = new Dictionary<string, LiveCategory>();
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            string? currentName = null;
            string? currentLogo = null;
            string? currentGroup = null;

            foreach (var line in lines)
            {
                var trimLine = line.Trim();
                if (trimLine.StartsWith("#EXTINF:"))
                {
                    // Metadata Parse
                    var logoIndex = trimLine.IndexOf("tvg-logo=\"");
                    if (logoIndex != -1)
                    {
                        var start = logoIndex + 10;
                        var end = trimLine.IndexOf("\"", start);
                        if (end != -1) currentLogo = trimLine.Substring(start, end - start);
                    }

                    var groupIndex = trimLine.IndexOf("group-title=\"");
                    if (groupIndex != -1)
                    {
                        var start = groupIndex + 13;
                        var end = trimLine.IndexOf("\"", start);
                        if (end != -1) currentGroup = trimLine.Substring(start, end - start);
                    }
                    else currentGroup = "Genel";

                    var commaIndex = trimLine.LastIndexOf(',');
                    if (commaIndex != -1) currentName = trimLine.Substring(commaIndex + 1).Trim();
                }
                else if (!trimLine.StartsWith("#") && currentName != null && !string.IsNullOrEmpty(trimLine))
                {
                    // Generate a stable ID from the URL hash for M3U
                    int m3uId = 0;
                    if (!string.IsNullOrEmpty(trimLine))
                    {
                        // Use a basic stable hash instead of .GetHashCode() which is not stable in .NET Core
                        uint hash = 0;
                        foreach (char c in trimLine) hash = hash * 31 + c;
                        m3uId = (int)(hash & 0x7FFFFFFF);
                    }

                    result[currentGroup!].Channels.Add(new LiveStream
                    {
                        StreamId = m3uId,
                        Name = currentName,
                        StreamUrl = trimLine,
                        IconUrl = currentLogo
                    });

                    currentName = null; currentLogo = null; currentGroup = null;
                }
            }

            return result.Values.OrderBy(c => c.CategoryName).ToList();
        }

        // ==========================================
        // UI LOGIC & FILTERING
        // ==========================================
        
        // Search Debounce
        private DispatcherTimer _searchDebounceTimer;

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_searchDebounceTimer == null)
            {
                _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _searchDebounceTimer.Tick += (s, args) => 
                {
                    _searchDebounceTimer.Stop();
                    PerformSearch();
                };
            }

            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private void PerformSearch()
        {
            if (SearchBox == null) return;
            _searchQuery = SearchBox.Text.ToLowerInvariant();
            UpdateSidebar();
            UpdateChannelList();
        }

        private void UpdateSidebar()
        {
            if (string.IsNullOrWhiteSpace(_searchQuery))
            {
                CategoryListView.ItemsSource = _allCategories;
            }
            else
            {
                // Filter categories matching query
                var filteredCats = _allCategories.Where(c => c.CategoryName.ToLowerInvariant().Contains(_searchQuery)).ToList();
                CategoryListView.ItemsSource = filteredCats;
            }
        }

        private void FilterItem_Click(object sender, RoutedEventArgs e) => UpdateChannelList();

        private void ResetFilters_Click(object sender, RoutedEventArgs e)
        {
            Filter4K.IsChecked = false;
            FilterFHD.IsChecked = false;
            FilterHD.IsChecked = false;
            FilterSD.IsChecked = false;
            FilterOnline.IsChecked = false;
            FilterNoFakes.IsChecked = false;
            FilterHEVC.IsChecked = false;
            FilterHDR.IsChecked = false;
            FilterHighFPS.IsChecked = false;
            UpdateChannelList();
        }

        private void UpdateChannelList()
        {
            // Update HeroSection visibility based on search
            if (HeroSection != null)
            {
                bool isSearching = !string.IsNullOrWhiteSpace(_searchQuery);
                HeroSection.Visibility = (_recentChannels.Count > 0 && !isSearching) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_selectedCategory == null) return;
            
            // Clear Queue on View Change to prioritize new items
            _probingQueue.Clear(); 
            _queuedUrls.Clear();

            IEnumerable<LiveStream> source = null;

            // SMART CONTEXT STRATEGY
            // 1. If Category is "All Channels" (-1): Search Globally in _allChannels
            // 2. If Category is specific: Search LOCALLY in that category's channels
            
            bool isGlobal = _selectedCategory.CategoryId == "-1";
            
            if (isGlobal)
            {
                source = _allChannels;
            }
            else
            {
                source = _selectedCategory.Channels ?? new List<LiveStream>();
            }

            if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                source = source.Where(c => c.Name != null && c.Name.ToLowerInvariant().Contains(_searchQuery));
            }

            // ==========================================
            // APPLY ADVANCED FILTERS
            // ==========================================
            
            // 1. Quality Filters
            bool q4k = Filter4K.IsChecked ?? false;
            bool qfhd = FilterFHD.IsChecked ?? false;
            bool qhd = FilterHD.IsChecked ?? false;
            bool qsd = FilterSD.IsChecked ?? false;

            if (q4k || qfhd || qhd || qsd)
            {
                source = source.Where(c => 
                    (q4k && (c.Resolution.Contains("4K") || c.Resolution.Contains("2160") || c.Resolution.Contains("3840"))) ||
                    (qfhd && c.Resolution.Contains("1080")) ||
                    (qhd && c.Resolution.Contains("720")) ||
                    (qsd && (c.Resolution.Contains("576") || c.Resolution.Contains("480") || c.Resolution.Contains("SD")))
                );
            }

            // 2. Health Filters
            if (FilterOnline.IsChecked ?? false)
            {
                source = source.Where(c => c.IsOnline == true);
            }
            if (FilterNoFakes.IsChecked ?? false)
            {
                source = source.Where(c => c.IsOnline == true && !c.IsUnstable);
            }

            // 3. Technical Filters
            if (FilterHEVC.IsChecked ?? false)
            {
                source = source.Where(c => c.Codec.ToLower().Contains("hevc") || c.Codec.ToLower().Contains("h265"));
            }
            if (FilterHDR.IsChecked ?? false)
            {
                source = source.Where(c => c.IsHdr);
            }
            if (FilterHighFPS.IsChecked ?? false)
            {
                source = source.Where(c => {
                    if (string.IsNullOrEmpty(c.Fps)) return false;
                    string cleanFps = c.Fps.Replace(" fps", "", StringComparison.OrdinalIgnoreCase).Trim();
                    if (double.TryParse(cleanFps, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fpsValue)) 
                        return fpsValue >= 49.0;
                    return false;
                });
            }

            // ==========================================
            // APPLY SORTING
            // ==========================================
            var finalResult = ApplySorting(source).ToList();

            // Setup Header
            HeaderTitle.Text = !string.IsNullOrWhiteSpace(_searchQuery) 
                ? (isGlobal ? $"'{SearchBox.Text}' için Sonuçlar" : $"{_selectedCategory.CategoryName} içinde '{SearchBox.Text}'")
                : _selectedCategory.CategoryName;
                
            HeaderCount.Text = $"({finalResult.Count})";

            ChannelGridView.ItemsSource = finalResult;
            
            // Auto-Probe is now always enabled (Viewport based)
            _canAutoProbe = AppSettings.IsAutoProbeEnabled;

            EmptyStatePanel.Visibility = finalResult.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Trigger initial probe for visible items once data is loaded
            if (finalResult.Count > 0)
            {
                DispatcherQueue.TryEnqueue(() => QueueVisibleItems());
            }
        }

        private IEnumerable<LiveStream> ApplySorting(IEnumerable<LiveStream> source)
        {
            if (_currentSortMode == LiveSortMode.Default) return source;

            switch (_currentSortMode)
            {
                case LiveSortMode.Name:
                    return _isSortDescending ? source.OrderByDescending(c => c.Name) : source.OrderBy(c => c.Name);
                
                case LiveSortMode.Quality:
                    // Ascending (Up Arrow) = Lowest First | Descending (Down Arrow) = Highest First
                    if (_isSortDescending) // Highest to Lowest
                    {
                        return source.OrderByDescending(GetResolutionValue)
                                     .ThenByDescending(GetBitrateValue) // Added bitrate as secondary
                                     .ThenByDescending(GetFpsValue)
                                     .ThenByDescending(GetCodecWeight)
                                     .ThenByDescending(c => c.IsHdr)
                                     .ThenBy(c => c.Name);
                    }
                    else // Lowest to Highest (BUT we still want unprobed/0 at the bottom for better UX)
                    {
                        return source.OrderBy(c => GetResolutionValue(c) == 0 ? int.MaxValue : GetResolutionValue(c))
                                     .ThenBy(GetResolutionValue)
                                     .ThenBy(GetBitrateValue)
                                     .ThenBy(GetFpsValue)
                                     .ThenBy(GetCodecWeight)
                                     .ThenBy(c => c.IsHdr)
                                     .ThenBy(c => c.Name);
                    }

                case LiveSortMode.OnlineFirst:
                    // Descending = Healthy Online First | Ascending = Offline First
                    if (_isSortDescending)
                    {
                        return source.OrderByDescending(GetStatusWeight)
                                     .ThenBy(c => c.Name);
                    }
                    else
                    {
                        return source.OrderBy(GetStatusWeight)
                                     .ThenBy(c => c.Name);
                    }

                case LiveSortMode.Recent:
                    var recentUrls = _recentChannels.Select(r => r.StreamUrl).ToList();
                    // Descending = Most Recent First
                    if (_isSortDescending)
                    {
                        return source.OrderByDescending(c => recentUrls.Contains(c.StreamUrl))
                                     .ThenBy(c => {
                                         int idx = recentUrls.IndexOf(c.StreamUrl);
                                         return idx == -1 ? int.MaxValue : idx;
                                     })
                                     .ThenBy(c => c.Name);
                    }
                    else
                    {
                        return source.OrderBy(c => recentUrls.Contains(c.StreamUrl))
                                     .ThenByDescending(c => {
                                         int idx = recentUrls.IndexOf(c.StreamUrl);
                                         return idx == -1 ? int.MinValue : idx;
                                     })
                                     .ThenBy(c => c.Name);
                    }

                default:
                    return source;
            }
        }

        private int GetResolutionValue(LiveStream c)
        {
            if (string.IsNullOrEmpty(c.Resolution)) return 0;
            
            // Priority: Probe results (contains 'x') or explicit keywords
            string res = c.Resolution.ToUpperInvariant();
            
            // If it's a raw resolution string like "3840x2160", try to get the height
            if (res.Contains("X"))
            {
                var parts = res.Split('X');
                if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int h))
                {
                    // Return height as the value for precise sorting (e.g. 1080, 720)
                    return h;
                }
            }

            // Fallback to keyword matching
            if (res.Contains("2160") || res.Contains("4K") || res.Contains("UHD")) return 2160;
            if (res.Contains("1440") || res.Contains("QHD") || res.Contains("2K")) return 1440;
            if (res.Contains("1080") || res.Contains("FHD")) return 1080;
            if (res.Contains("720") || res.Contains("HD")) return 720;
            if (res.Contains("576")) return 576;
            if (res.Contains("480") || res.Contains("SD")) return 480;
            
            return 1;
        }

        private long GetBitrateValue(LiveStream c) => c.Bitrate;

        private double GetFpsValue(LiveStream c)
        {
            if (string.IsNullOrEmpty(c.Fps)) return 0.0;
            string cleanFps = c.Fps.Replace(" fps", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (double.TryParse(cleanFps, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val)) return val;
            return 0.0;
        }

        private int GetCodecWeight(LiveStream c)
        {
            if (string.IsNullOrEmpty(c.Codec)) return 0;
            string lower = c.Codec.ToLowerInvariant();
            if (lower.Contains("hevc") || lower.Contains("h265") || lower.Contains("av1") || lower.Contains("vp9")) return 2;
            if (lower.Contains("h264") || lower.Contains("avc")) return 1;
            return 0;
        }

        private int GetStatusWeight(LiveStream c)
        {
            if (c.IsOnline == true)
            {
                return c.IsUnstable ? 2 : 3; // Healthy = 3, Unstable (Slo/Fake) = 2
            }
            if (c.IsProbing) return 1;
            return 0; // Offline
        }

        private void SortOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                if (Enum.TryParse<LiveSortMode>(btn.Tag.ToString(), out var mode))
                {
                    if (mode == LiveSortMode.Default)
                    {
                        _currentSortMode = LiveSortMode.Default;
                        _isSortDescending = false;
                    }
                    else if (_currentSortMode == mode)
                    {
                        // Toggle direction
                        _isSortDescending = !_isSortDescending;
                    }
                    else
                    {
                        // Change mode, default to Descending (Highest First/Most Recent)
                        _currentSortMode = mode;
                        _isSortDescending = true;
                    }

                    UpdateSortIcons();
                    UpdateChannelList();
                }
            }
        }

        private void UpdateSortIcons()
        {
            // Reset all
            SortIcon_Name.Visibility = Visibility.Collapsed;
            SortIcon_Quality.Visibility = Visibility.Collapsed;
            SortIcon_OnlineFirst.Visibility = Visibility.Collapsed;
            SortIcon_Recent.Visibility = Visibility.Collapsed;

            if (_currentSortMode == LiveSortMode.Default) return;

            // Pick icon to show
            FontIcon target = _currentSortMode switch
            {
                LiveSortMode.Name => SortIcon_Name,
                LiveSortMode.Quality => SortIcon_Quality,
                LiveSortMode.OnlineFirst => SortIcon_OnlineFirst,
                LiveSortMode.Recent => SortIcon_Recent,
                _ => null
            };

            if (target != null)
            {
                target.Visibility = Visibility.Visible;
                target.Glyph = _isSortDescending ? "\uE70D" : "\uE70E"; // Down / Up arrow
            }
        }

        private void SelectCategory(LiveCategory category)
        {
            _selectedCategory = category;
            AppSettings.LastLiveCategoryId = category?.CategoryId; // Save selection
            UpdateChannelList();
        }

        private void CategoryListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveCategory cat)
            {
                // If search is active, clear it so we can see the full category content
                if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    SearchBox.Text = "";
                    // Note: Changing text triggers TextChanged -> UpdateSidebar -> Reset ItemsSource
                    // This might clear selection, so we need to re-select below.
                }

                SelectCategory(cat);
                
                // Re-apply visual selection in case it was lost during search clearing
                CategoryListView.SelectedItem = cat;
                CategoryListView.ScrollIntoView(cat);
            }
        }

        // Handle ENTER key in list to select
        private void CategoryListView_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // Optional: Keyboard support
        }

        private void ChannelGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveStream stream)
            {
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name, LogoUrl: stream.IconUrl, Type: "live"));
            }
        }

        private void ItemsWrapGrid_Loaded(object sender, RoutedEventArgs e)
        {
            _itemsWrapGrid = sender as ItemsWrapGrid;
            RecalculateItemSize();
        }

        private void ChannelGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RecalculateItemSize();
        }

        private void RecalculateItemSize()
        {
            if (_itemsWrapGrid == null || ChannelGridView == null) return;

            // Total available width
            double gridWidth = ChannelGridView.ActualWidth;
            if (gridWidth <= 0) return;

            // Subtract Padding (Left+Right = 24+24 = 48) + Scrollbar buffer (approx 16)
            double availableWidth = gridWidth - 48 - 4; 

            // Desired Minimum Width per Tile
            double minItemWidth = 320.0;

            // Calculate Columns
            int columns = (int)Math.Floor(availableWidth / minItemWidth);
            if (columns < 1) columns = 1;

            // Precise Width per Item
            double newItemWidth = availableWidth / columns;

            // Apply
            _itemsWrapGrid.ItemWidth = newItemWidth;
            
            // ItemHeight is fixed at 80 in XAML.
            
            // Re-queue visible items after resize
            QueueVisibleItems();
        }

        private void LoadRecentChannels()
        {
            var historyItems = HistoryManager.Instance.GetRecentLiveChannels(10);
            
            _recentChannels.Clear();
            foreach (var item in historyItems)
            {
                // CRITICAL FIX: Find the matching channel in the main list to use the CORRECT MetadataBuffer offsets
                var match = _allChannels?.FirstOrDefault(c => c.StreamId.ToString() == item.Id || c.Name == item.Title);
                
                if (match != null)
                {
                    _recentChannels.Add(match);
                }
                else
                {
                    // Fallback to orphaned object if not found (names might be broken)
                    _recentChannels.Add(new LiveStream
                    {
                        StreamId = int.TryParse(item.Id, out var id) ? id : 0,
                        Name = item.Title,
                        StreamUrl = item.StreamUrl,
                        IconUrl = item.PosterUrl
                    });
                }
            }

            RecentListView.ItemsSource = _recentChannels;
            
            bool hasSearchResults = !string.IsNullOrWhiteSpace(_searchQuery);
            HeroSection.Visibility = (_recentChannels.Count > 0 && !hasSearchResults) ? Visibility.Visible : Visibility.Collapsed;
            
            System.Diagnostics.Debug.WriteLine($"[LiveTV] Loaded {_recentChannels.Count} recent channels. Search active: {hasSearchResults}");
        }

        private void LoadDummyRecents() => LoadRecentChannels(); // Keeping the name for compatibility if needed elsewhere

        private void RecentListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveStream stream)
            {
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name, LogoUrl: stream.IconUrl, Type: "live"));
            }
        }

        // ==========================================
        // AUTO-PROBE WORKER
        // ==========================================
        
        private void ChannelGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            // Now handled by ScrollViewer.ViewChanged + QueueVisibleItems
            // This event is no longer used for probing but kept for potential future use
        }
    

        private void ScanCategory_Click(object sender, RoutedEventArgs e)
        {
            if (ChannelGridView.ItemsSource is IEnumerable<LiveStream> currentList)
            {
                // Clear existing metadata and physical cache for these specific URLs to force a fresh scan
                foreach (var stream in currentList)
                {
                    // Remove from ID-Based Physical Cache (v2.4)
                    Services.ProbeCacheService.Instance.Remove(stream.StreamId);

                    // Reset UI State
                    stream.Resolution = "";
                    stream.Fps = "";
                    stream.Codec = "";
                    stream.Bitrate = 0;
                    stream.IsHdr = false;
                    stream.IsOnline = null; 

                    // Queue for analysis (Worker will now pick them up)
                    if (!_queuedUrls.Contains(stream.StreamUrl))
                    {
                        _probingQueue.Enqueue(stream);
                        _queuedUrls.Add(stream.StreamUrl);
                    }
                }

                // Ensure worker is running
                _ = StartProbingWorker();
            }
        }

        private async Task StartProbingWorker()
        {
            if (_isWorkerRunning) return;
            _isWorkerRunning = true;

            // Reset CTS if it was cancelled by previous navigation
            if (_workerCts.IsCancellationRequested)
            {
                _workerCts.Dispose();
                _workerCts = new CancellationTokenSource();
            }

            // Launch 3 parallel workers
            // Launch parallel workers based on user setting
            int workerCount = AppSettings.ProbingWorkerCount;
            var workers = new List<Task>();
            for (int i = 0; i < workerCount; i++)
            {
                workers.Add(ProbingWorkerLoop(_workerCts.Token));
            }

            await Task.WhenAll(workers);
            _isWorkerRunning = false;
        }

        private async Task ProbingWorkerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                LiveStream? item = null;
                bool hasItem = false;

                lock (_probingQueue)
                {
                    hasItem = _probingQueue.TryDequeue(out item);
                }

                if (hasItem && item != null)
                {
                    _queuedUrls.Remove(item.StreamUrl); 

                    // Check if auto-probing is globally disabled 
                    // (BUT we allow it if the queue was manually filled via ScanCategory)
                    // Actually, if it's in the queue, we generally process it.
                    // Let's just check the setting if we are NOT in a ScanCategory context? 
                    // No, usually if user toggles it OFF, they want it to stop.
                    if (!AppSettings.IsAutoProbeEnabled) 
                    {
                        // Minor hack: if the item was just reset (IsOnline == null), 
                        // it might be a manual ScanCategory request, so we let it through.
                        if (item.IsOnline != null) 
                        {
                            await Task.Delay(500, ct); 
                            continue;
                        }
                    }

                    // Double check before processing:
                    // 2. CHECK: Basic validation
                    // If metadata arrived while queued, skip.
                    if (item.HasMetadata || item.IsProbing) continue;

                    var currentList = ChannelGridView.ItemsSource as List<LiveStream>;
                    if (currentList == null || !currentList.Contains(item)) continue;

                    try
                    {
                        DispatcherQueue.TryEnqueue(() => item.IsProbing = true);

                        // OPTIMIZATION: Check ID-Based Cache explicitly to skip throttle (v2.4)
                        if (Services.ProbeCacheService.Instance.Get(item.StreamId) is Services.ProbeData cached)
                        {
                            DispatcherQueue.TryEnqueue(() => 
                            {
                                item.Resolution = cached.Resolution;
                                item.Fps = cached.Fps;
                                item.Codec = cached.Codec;
                                item.Bitrate = cached.Bitrate;
                                item.IsOnline = true; // Cached implies success
                                item.IsHdr = cached.IsHdr;
                                item.IsProbing = false;
                            });
                            continue; // Valid cache hit: Process next item IMMEDIATELY (No Delay)
                        }

                        // Process (This takes ~0.5s - 1.5s)
                        var result = await Services.StreamProberService.Instance.ProbeAsync(item.StreamId, item.StreamUrl, ct);

                        DispatcherQueue.TryEnqueue(() => 
                        {
                            item.Resolution = result.Resolution;
                            item.Fps = result.Fps;
                            item.Codec = result.Codec;
                            item.Bitrate = result.Bitrate;
                            item.IsOnline = result.Success;
                            item.IsHdr = result.IsHdr;
                            item.IsProbing = false;
                        });

                        // Small delay only for REAL probes to prevent CPU choking
                        await Task.Delay(250, ct); 
                    }
                    catch
                    {
                        DispatcherQueue.TryEnqueue(() => item.IsProbing = false);
                    }
                }
                else
                {
                    // Queue empty, sleep for a bit
                    try { await Task.Delay(1000, ct); } catch { break; }
                }
            }
        }

        private async Task ShowMessageDialog(string title, string content)
        {
             if (this.XamlRoot == null) return;
            ContentDialog dialog = new ContentDialog 
            { 
                Title = title, 
                Content = content, 
                CloseButtonText = "Tamam", 
                XamlRoot = this.XamlRoot 
            };
            await Services.DialogService.ShowAsync(dialog);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // Reload Data - Forcing network fetch
            _allCategories.Clear();
            _allChannels.Clear();
            
            if (_loginInfo != null)
            {
                if (!string.IsNullOrEmpty(_loginInfo.Host) && !string.IsNullOrEmpty(_loginInfo.Username))
                {
                    await LoadXtreamCategoriesAsync(ignoreCache: true);
                }
                else
                {
                     await LoadM3uAsync(_loginInfo.PlaylistUrl, ignoreCache: true);
                }
            }
        }



        private async void CheckQuality_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is LiveStream stream)
            {
                if (stream.IsProbing) return;

                try
                {
                    stream.IsProbing = true;
                    var result = await Services.StreamProberService.Instance.ProbeAsync(stream.StreamId, stream.StreamUrl, ct: default);
                    
                    stream.Resolution = result.Resolution;
                    stream.Fps = result.Fps;
                    stream.Codec = result.Codec;
                    stream.Bitrate = result.Bitrate;
                    stream.IsOnline = result.Success;
                    stream.IsHdr = result.IsHdr;
                }
                finally
                {
                    stream.IsProbing = false;
                }
            }
        }


        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is LiveStream stream)
            {
                if (string.IsNullOrEmpty(stream.StreamUrl)) return;
                
                var dataPackage = new DataPackage();
                dataPackage.RequestedOperation = DataPackageOperation.Copy;
                dataPackage.SetText(stream.StreamUrl);
                Clipboard.SetContent(dataPackage);

                // Show notification
                NotificationInfoBar.IsOpen = true;
                
                // Auto-close after 3 seconds
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (s, args) =>
                {
                    NotificationInfoBar.IsOpen = false;
                    timer.Stop();
                };
                timer.Start();
            }
        }

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;
        }

        private void AddPlaylist_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(LoginPage));
        }
    }
}
