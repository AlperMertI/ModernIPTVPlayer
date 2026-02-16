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
using Microsoft.UI.Xaml.Media.Imaging;

namespace ModernIPTVPlayer
{
    public sealed partial class LiveTVPage : Page
    {
        private LoginParams? _loginInfo;
        private HttpClient _httpClient;

        // Data Storage
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
        private List<LiveStream> _recentChannels = new();

        private FFmpegProber? _prober;
        
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

            if (App.CurrentLogin == null || string.IsNullOrEmpty(App.CurrentLogin.Host))
            {
                // NO IPTV LOGIN
                LoginRequiredPanel.Visibility = Visibility.Visible;
                HeroSection.Visibility = Visibility.Collapsed;
                MainLoadingRing.Visibility = Visibility.Collapsed;
                SidebarLoadingRing.Visibility = Visibility.Collapsed;
                EmptyStatePanel.Visibility = Visibility.Collapsed; // Hide search empty state too
                return;
            }
            else
            {
                LoginRequiredPanel.Visibility = Visibility.Collapsed;
                HeroSection.Visibility = Visibility.Visible;
            }

            if (e.Parameter is LoginParams loginParams)
            {
                // Detect if the playlist has changed
                if (_loginInfo != null && _loginInfo.PlaylistUrl != loginParams.PlaylistUrl)
                {
                    System.Diagnostics.Debug.WriteLine("New playlist detected, clearing cache...");
                    _allCategories.Clear();
                    _allChannels.Clear();
                    CategoryListView.ItemsSource = null;
                    ChannelGridView.ItemsSource = null;
                }
                
                _loginInfo = loginParams;
            }

            if (_loginInfo != null)
            {
                // Reset CTS if it was cancelled by previous navigation
                if (_workerCts.IsCancellationRequested)
                {
                    _workerCts.Dispose();
                    _workerCts = new CancellationTokenSource();
                }

                // Ensure worker is running
                _ = StartProbingWorker();

                if (_allCategories.Count > 0) 
                {
                    // Re-trigger probing for visible items when coming back
                    // TriggerVisibleProbe(); // REMOVED: Managed by view updates
                    return; 
                }

                if (!string.IsNullOrEmpty(_loginInfo.Host) && 
                    !string.IsNullOrEmpty(_loginInfo.Username) && 
                    !string.IsNullOrEmpty(_loginInfo.Password))
                {
                    await LoadXtreamCategoriesAsync();
                }
                else
                {
                    await LoadM3uAsync(_loginInfo.PlaylistUrl);
                }
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
        private async Task LoadXtreamCategoriesAsync()
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
                // 1. CACHE STRATEGY: Try Load from Disk First
                // -------------------------------------------------------------
                var cachedCats = await Services.ContentCacheService.Instance.LoadCacheAsync<LiveCategory>(playlistId, "live_cats");
                var cachedStreams = await Services.ContentCacheService.Instance.LoadCacheAsync<LiveStream>(playlistId, "live_streams");

                bool cacheLoaded = (cachedCats != null && cachedStreams != null && cachedCats.Count > 0);

                if (cacheLoaded)
                {
                    System.Diagnostics.Debug.WriteLine("[LiveTVPage] Loaded from Cache!");
                    _allCategories = cachedCats;
                    _allChannels = cachedStreams; // Raw list, but we need to re-link

                    // Hydrate Metadata from ProbeCache
                    foreach (var s in _allChannels)
                    {
                        if (Services.ProbeCacheService.Instance.Get(s.StreamUrl) is Services.ProbeData pd)
                        {
                            s.Resolution = pd.Resolution;
                            s.Codec = pd.Codec;
                            s.Bitrate = pd.Bitrate;
                            s.Fps = pd.Fps;
                            s.IsHdr = pd.IsHdr;
                            s.IsOnline = true; // Cached implies it was online recently
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
                    
                    LoadDummyRecents();

                    SidebarLoadingRing.IsActive = false;
                    MainLoadingRing.IsActive = false;
                    
                    // Optional: Background Refresh check could happen here
                }

                // 2. NETWORK STRATEGY: If Cache Missing (or forced refresh needed)
                if (!cacheLoaded)
                {
                    string api = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_live_categories";
                    string json = await _httpClient.GetStringAsync(api);
                    var categories = JsonSerializer.Deserialize<List<LiveCategory>>(json) ?? new List<LiveCategory>();

                    // Channel Fetch
                    string streamApi = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_live_streams";
                    string streamJson = await _httpClient.GetStringAsync(streamApi);
                    var streams = JsonSerializer.Deserialize<List<LiveStream>>(streamJson);

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
                        
                        // Hydrate Metadata from ProbeCache (Network Path)
                        foreach (var s in _allChannels)
                        {
                            if (Services.ProbeCacheService.Instance.Get(s.StreamUrl) is Services.ProbeData pd)
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

                        LoadDummyRecents();
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

        private async Task LoadM3uAsync(string? url)
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
                    if (!result.ContainsKey(currentGroup!))
                        result[currentGroup!] = new LiveCategory { CategoryName = currentGroup! };

                    result[currentGroup!].Channels.Add(new LiveStream
                    {
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
                    // Remove " fps" suffix and parse safely
                    string cleanFps = c.Fps.Replace(" fps", "", StringComparison.OrdinalIgnoreCase).Trim();
                    if (double.TryParse(cleanFps, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fpsValue)) 
                        return fpsValue >= 49.0;
                    return false;
                });
            }

            var resultList = source.ToList();

            // Apply Sorting
            if (_isSortedAscending)
            {
                resultList = resultList.OrderBy(c => c.Name).ToList();
            }

            // Setup Header
            HeaderTitle.Text = !string.IsNullOrWhiteSpace(_searchQuery) 
                ? (isGlobal ? $"'{SearchBox.Text}' için Sonuçlar" : $"{_selectedCategory.CategoryName} içinde '{SearchBox.Text}'")
                : _selectedCategory.CategoryName;
                
            HeaderCount.Text = $"({resultList.Count})";

            ChannelGridView.ItemsSource = resultList;
            
            // Auto-Probe is now always enabled (Viewport based)
            _canAutoProbe = AppSettings.IsAutoProbeEnabled;

            // Empty State
            EmptyStatePanel.Visibility = resultList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            
            // Empty State
            EmptyStatePanel.Visibility = resultList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name));
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

        private void LoadDummyRecents()
        {
            // In a real app, this would load from AppSettings
            // For now, we take random 5 channels from _allChannels to populate the "Hero"
            if (_allChannels.Count > 0)
            {
                _recentChannels = _allChannels.Take(5).ToList();
                RecentListView.ItemsSource = _recentChannels;
                
                // Background is now handled via static XAML Gradient
            }
        }

        private void RecentListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveStream stream)
            {
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name));
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
                    // Remove from Physical Cache
                    Services.ProbeCacheService.Instance.Remove(stream.StreamUrl);

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

            if (_prober == null) _prober = new FFmpegProber();

            // Launch 3 parallel workers
            int workerCount = 3;
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

                        // OPTIMIZATION: Check Cache explicitly to skip throttle
                        if (Services.ProbeCacheService.Instance.Get(item.StreamUrl) is Services.ProbeData cached)
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
                        var result = await _prober.ProbeAsync(item.StreamUrl, ct);

                        DispatcherQueue.TryEnqueue(() => 
                        {
                            item.Resolution = result.Res;
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
            await dialog.ShowAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // Reload Data
            _allCategories.Clear();
            _allChannels.Clear();
            
            if (_loginInfo != null)
            {
                if (!string.IsNullOrEmpty(_loginInfo.Host) && !string.IsNullOrEmpty(_loginInfo.Username))
                {
                    await LoadXtreamCategoriesAsync();
                }
                else
                {
                     await LoadM3uAsync(_loginInfo.PlaylistUrl);
                }
            }
        }



        private async void CheckQuality_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is LiveStream stream)
            {
                if (stream.IsProbing) return;

                // Init Prober if needed
                if (_prober == null)
                {
                     _prober = new FFmpegProber();
                }

                try
                {
                    stream.IsProbing = true;
                    var result = await _prober.ProbeAsync(stream.StreamUrl, force: true);
                    
                    stream.Resolution = result.Res;
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

        private void SortButton_Click(object sender, RoutedEventArgs e)
        {
            _isSortedAscending = !_isSortedAscending;
            UpdateChannelList();
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
