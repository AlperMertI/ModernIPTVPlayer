using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Media.Imaging;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Iptv;
using Microsoft.UI.Xaml.Media.Animation;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Models.Common;
using ModernIPTVPlayer.Helpers;

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
        private IReadOnlyList<LiveCategory> _allCategories = new List<LiveCategory>();
        private IReadOnlyList<LiveStream> _allChannels = new List<LiveStream>();

        // Phase A/B: Pre-computed search structures
        private ushort[] _channelFlags = Array.Empty<ushort>();  // Bit field for quality/health/tech filters
        private int[] _allChannelIndices = Array.Empty<int>();
        private readonly Dictionary<string, int[]> _categoryChannelIndices = new(StringComparer.Ordinal);
        
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
        private readonly ConcurrentQueue<LiveStream> _probingQueue = new();
        private readonly HashSet<string> _queuedUrls = new();
        private readonly System.Threading.Lock _probingLock = new();
        private CancellationTokenSource _workerCts = new();
        private bool _isWorkerRunning = false;
        private bool _canAutoProbe = false;

        // SCROLL-BASED PROBING
        private ScrollViewer? _channelScrollViewer;

        // HTTP Cancellation (Phase 4.1): Cancel in-flight requests on navigation away
        private CancellationTokenSource? _loadCts;

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
            lock (_probingLock)
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
                    _allCategories = Array.Empty<LiveCategory>();
                    _allChannels = Array.Empty<LiveStream>();
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

            // Phase 4.1: Create fresh CTS for this navigation's HTTP requests
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            _loadCts.CancelAfter(TimeSpan.FromSeconds(60)); // Hard timeout

            // 4. Data Loading
            if (_allCategories.Count > 0) return;

            if (!string.IsNullOrEmpty(_loginInfo.Host) && !string.IsNullOrEmpty(_loginInfo.Username))
            {
                await LoadXtreamCategoriesAsync(cancellationToken: _loadCts.Token);
            }
            else if (!string.IsNullOrEmpty(_loginInfo.PlaylistUrl))
            {
                await LoadM3uAsync(_loginInfo.PlaylistUrl, cancellationToken: _loadCts.Token);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // Cancel Worker
            _workerCts.Cancel();
            // Phase 4.1: Cancel in-flight HTTP requests
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
        }

        // ==========================================
        // DATA LOADING
        // ==========================================

        // Phase 4.4: Skeleton Loading Helpers
        private const int SKELETON_ITEM_COUNT = 24; // Show 24 shimmer placeholders (fills ~3-4 rows)

        private void ShowLoadingSkeleton()
        {
            // Show skeleton grid with shimmer items
            if (SkeletonGrid != null)
            {
                SkeletonGrid.ItemsSource = new int[SKELETON_ITEM_COUNT]; // dummy data
                SkeletonGrid.Visibility = Visibility.Visible;
            }
            // Hide the real grid until data is ready
            if (ChannelGridView != null)
                ChannelGridView.Visibility = Visibility.Collapsed;
        }

        private void HideLoadingSkeleton()
        {
            if (SkeletonGrid != null)
                SkeletonGrid.Visibility = Visibility.Collapsed;
            if (ChannelGridView != null)
                ChannelGridView.Visibility = Visibility.Visible;
            SidebarLoadingRing.IsActive = false;
            MainLoadingRing.IsActive = false;
        }

        private async Task LoadXtreamCategoriesAsync(bool ignoreCache = false, CancellationToken cancellationToken = default)
        {
            try
            {
                ShowLoadingSkeleton();
                EmptyStatePanel.Visibility = Visibility.Collapsed;

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string username = _loginInfo.Username;
                string password = _loginInfo.Password;
                string playlistId = AppSettings.LastPlaylistId?.ToString() ?? "default";

                // -------------------------------------------------------------
                // 1. CACHE STRATEGY: Project Zero Binary Bundle (Insanely Fast)
                // -------------------------------------------------------------
                IReadOnlyList<LiveCategory> cachedCats = null;
                IReadOnlyList<LiveStream> cachedStreams = null;
                bool cacheLoaded = false;
                var swLoad = System.Diagnostics.Stopwatch.StartNew();

                if (!ignoreCache)
                {
                    cachedCats = await Services.ContentCacheService.Instance.LoadCacheAsync<LiveCategory>(playlistId, "live_cats");
                    cachedStreams = await Services.ContentCacheService.Instance.LoadLiveStreamsBinaryAsync(playlistId);


                    cacheLoaded = (cachedCats != null && cachedStreams != null && cachedCats.Count > 0);
                }

                if (cacheLoaded)
                {
                    swLoad.Stop();
                    _allCategories = cachedCats;
                    _allChannels = cachedStreams;

                    // Process metadata and indices in background
                    await ProcessLoadedChannelsInternalAsync(playlistId, baseUrl, username, password, cancellationToken);
                    return;
                }

                // 2. NETWORK STRATEGY: Zero-Object Streamed Persistence
                try
                {
                    string api = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_live_categories";
                    string streamApi = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_live_streams";

                    var catResponse = await _httpClient.GetAsync(api, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    var streamResponse = await _httpClient.GetAsync(streamApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    
                    using var streamStream = await streamResponse.Content.ReadAsStreamAsync(cancellationToken);

                    // 1. Efficient Categorization (Source-Gen STJ)
                    var categories = HttpHelper.TryDeserializeList(await catResponse.Content.ReadAsStringAsync(cancellationToken), Services.Json.AppJsonContext.Default.ListLiveCategory);

                    // 2. Stream Direct to Binary (The Project Zero Way)
                    await Services.ContentCacheService.Instance.SaveLiveStreamsBinaryFromStreamAsync(playlistId, streamStream);

                    // 3. Load Virtual List (Zero-Allocation MMF)
                    _allChannels = await Services.ContentCacheService.Instance.LoadLiveStreamsBinaryAsync(playlistId);
                    
                    // Categories Prep
                    categories ??= new List<LiveCategory>();
                    var allCat = new LiveCategory { CategoryName = "Tüm Kanallar", CategoryId = "-1" };
                    var tempList = new List<LiveCategory>(categories.Count + 1) { allCat };
                    tempList.AddRange(categories);
                    _allCategories = tempList;

                    // Save Categories to cache (Async)
                    _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "live_cats", _allCategories.ToList());

                    // 4. Background Processing (Indexing, Probing, Hydration)
                    await ProcessLoadedChannelsInternalAsync(playlistId, baseUrl, username, password, cancellationToken);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("[LiveLoad] Network sync failed", ex);
                    await ShowMessageDialog("Hata", $"Kanal listesi yüklenemedi: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                await ShowMessageDialog("Hata", $"Kanal listesi yüklenemedi: {ex.Message}");
            }
            finally
            {
                HideLoadingSkeleton();
            }
        }


        private async Task LoadM3uAsync(string? url, bool ignoreCache = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(url)) return;

            string playlistId = AppSettings.LastPlaylistId?.ToString() ?? "default";

            // -------------------------------------------------------------
            // 1. CACHE STRATEGY: Check Binary Cache (same as Xtream)
            // -------------------------------------------------------------
            IReadOnlyList<LiveStream> cachedStreams = null;
            IReadOnlyList<LiveCategory> cachedCats = null;
            bool cacheLoaded = false;

            if (!ignoreCache)
            {
                cachedStreams = await Services.ContentCacheService.Instance.LoadLiveStreamsBinaryAsync(playlistId);
                cachedCats = await Services.ContentCacheService.Instance.LoadCacheAsync<LiveCategory>(playlistId, "live_cats_m3u");
                cacheLoaded = (cachedStreams != null && cachedCats != null && cachedStreams.Count > 0);
            }

            if (cacheLoaded)
            {
                _allChannels = cachedStreams;
                _allCategories = cachedCats;

                await ProcessLoadedChannelsInternalAsync(playlistId, null, null, null, cancellationToken);
                return;
            }

            // 2. NETWORK PATH: Download, Stream to Binary, Display
            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

                // Pipe Direct to Binary Cache (The Project Zero Way)
                await Services.ContentCacheService.Instance.SaveLiveStreamsBinaryFromM3uStreamAsync(playlistId, stream);

                // Load Virtual Streams immediately (MMF - Zero Memory)
                _allChannels = await Services.ContentCacheService.Instance.LoadLiveStreamsBinaryAsync(playlistId);

                // Categories (M3U relies on channel metadata)
                _allCategories = BuildInitialCategoriesFromChannels(_allChannels);

                // Save Categories to cache
                _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "live_cats_m3u", _allCategories.ToList());

                // Process in Background thread
                await ProcessLoadedChannelsInternalAsync(playlistId, null, null, null, cancellationToken);
            }
            catch (Exception ex)
            {
                AppLogger.Error("[M3ULoad] Network sync failed", ex);
                await ShowMessageDialog("Hata", $"M3U listesi yüklenemedi: {ex.Message}");
            }
            finally
            {
                HideLoadingSkeleton();
            }
        }

        /// <summary>
        /// Centralized background processor for loaded channels. 
        /// Offloads heavy indexing and metadata hydration to worker threads (Master Plan Item 69).
        /// </summary>
        private async Task ProcessLoadedChannelsInternalAsync(string playlistId, string? baseUrl, string? username, string? password, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // 1. Context & Security (Xtream only)
                if (_allChannels is VirtualLiveList vList && baseUrl != null)
                {
                    vList.SetXtreamContext(baseUrl.TrimEnd('/'), username ?? "", password ?? "");
                }

                // 2. Indexing (Zero-Allocation Binary Scan)
                BuildChannelRuntimeIndexes();

                // 3. Metadata Awareness (Lazy initialization only)
                if (playlistId != null)
                {
                    _ = Services.ProbeCacheService.Instance.InitializeForPlaylistAsync(playlistId);
                }
            }, cancellationToken).ConfigureAwait(false);

            // Final UI Binding
            DispatcherQueue.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;

                CategoryListView.ItemsSource = _allCategories;
                
                var allCat = _allCategories.FirstOrDefault(c => c.CategoryId == "-1");
                var lastId = AppSettings.LastLiveCategoryId;
                var targetCat = _allCategories.FirstOrDefault(c => c.CategoryId == lastId) ?? allCat;

                CategoryListView.SelectedItem = targetCat;
                CategoryListView.ScrollIntoView(targetCat);
                SelectCategory(targetCat);

                LoadRecentChannels();
                HideLoadingSkeleton();
            });
        }

        private List<LiveCategory> BuildInitialCategoriesFromChannels(IReadOnlyList<LiveStream> channels)
        {
            if (channels == null) return new List<LiveCategory>();

            // PROJECT ZERO: Optimized grouping without hydrating 50k+ managed objects.
            IEnumerable<LiveCategory> categories;

            if (channels is IVirtualStreamList vList)
            {
                var indexMap = new ConcurrentDictionary<string, List<int>>();
                vList.ParallelScanInto(indexMap);

                categories = indexMap.Keys
                    .Select(catId => new LiveCategory { CategoryName = catId, CategoryId = catId })
                    .OrderBy(c => c.CategoryName);
            }
            else
            {
                // Fallback for non-virtualized lists
                categories = channels
                    .GroupBy(s => s.CategoryId ?? "Genel")
                    .Select(g => new LiveCategory { CategoryName = g.Key, CategoryId = g.Key })
                    .OrderBy(c => c.CategoryName);
            }

            var categoryList = categories.ToList();

            var allCat = new LiveCategory { CategoryName = "Tüm Kanallar", CategoryId = "-1" };
            var result = new List<LiveCategory>(categoryList.Count + 1) { allCat };
            result.AddRange(categoryList);
            return result;
        }        
        
        private List<LiveCategory> ParseM3u(string content)
        {
            var categoryMap = new Dictionary<string, (LiveCategory Category, List<LiveStream> Channels)>();
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
                    // Generate a stable ID from the URL hash for M3U (SHA256-based)
                    int m3uId = 0;
                    if (!string.IsNullOrEmpty(trimLine))
                    {
                        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trimLine));
                        m3uId = BitConverter.ToInt32(hashBytes, 0) & 0x7FFFFFFF;
                    }

                    if (!categoryMap.TryGetValue(currentGroup!, out var entry))
                    {
                        entry = (new LiveCategory { CategoryName = currentGroup!, CategoryId = currentGroup! }, new List<LiveStream>());
                        categoryMap[currentGroup!] = entry;
                    }

                    entry.Channels.Add(new LiveStream
                    {
                        StreamId = m3uId,
                        Name = currentName,
                        StreamUrl = trimLine,
                        StreamIcon = currentLogo
                    });

                    currentName = null; currentLogo = null; currentGroup = null;
                }
            }

            foreach (var entry in categoryMap.Values)
            {
                entry.Category.Channels = entry.Channels;
            }

            return categoryMap.Values.Select(v => v.Category).OrderBy(c => c.CategoryName).ToList();
        }

        /// <summary>
        /// Phase 4.2: Streaming M3U parser — processes line-by-line without loading entire file into memory.
        /// Uses StreamReader.ReadLineAsync() for memory-efficient parsing of large M3U files.
        /// </summary>
        private static List<LiveCategory> ParseM3uStreaming(Stream stream)
        {
            var categoryMap = new Dictionary<string, (LiveCategory Category, List<LiveStream> Channels)>();

            using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
            string? line;
            string? currentName = null;
            string? currentLogo = null;
            string? currentGroup = null;

            while ((line = reader.ReadLine()) != null)
            {
                var trimLine = line.Trim();
                if (trimLine.StartsWith("#EXTINF:"))
                {
                    // Metadata Parse
                    var logoIndex = trimLine.IndexOf("tvg-logo=\"");
                    if (logoIndex != -1)
                    {
                        var start = logoIndex + 10;
                        var end = trimLine.IndexOf('"', start);
                        if (end != -1) currentLogo = trimLine.Substring(start, end - start);
                    }

                    var groupIndex = trimLine.IndexOf("group-title=\"");
                    if (groupIndex != -1)
                    {
                        var start = groupIndex + 13;
                        var end = trimLine.IndexOf('"', start);
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
                        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trimLine));
                        m3uId = BitConverter.ToInt32(hashBytes, 0) & 0x7FFFFFFF;
                    }

                    if (!categoryMap.TryGetValue(currentGroup!, out var entry))
                    {
                        entry = (new LiveCategory { CategoryName = currentGroup!, CategoryId = currentGroup! }, new List<LiveStream>());
                        categoryMap[currentGroup!] = entry;
                    }

                    entry.Channels.Add(new LiveStream
                    {
                        StreamId = m3uId,
                        Name = currentName,
                        StreamUrl = trimLine,
                        StreamIcon = currentLogo
                    });

                    currentName = null; currentLogo = null; currentGroup = null;
                }
            }

            foreach (var entry in categoryMap.Values)
            {
                entry.Category.Channels = entry.Channels;
            }

            return categoryMap.Values.Select(v => v.Category).OrderBy(c => c.CategoryName).ToList();
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
                _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
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

        // ==========================================
        // Phase A/B: Search Index + Flags Builder
        // ==========================================

        private void BuildChannelRuntimeIndexes()
        {
            BuildCategoryChannelIndexMap();
            BuildChannelFlags();
        }

        /// <summary>
        /// Keeps one canonical channel list in memory and maps categories to channel indices.
        /// This avoids duplicating LiveStream references inside every LiveCategory.
        /// </summary>
        private void BuildCategoryChannelIndexMap()
        {
            try
            {
                _categoryChannelIndices.Clear();
                _allChannelIndices = new int[_allChannels.Count];
                for (int i = 0; i < _allChannels.Count; i++) _allChannelIndices[i] = i;

                var grouped = new ConcurrentDictionary<string, List<int>>(StringComparer.Ordinal);

                // PROJECT ZERO: Specialized Binary Scan for Virtual Lists
                if (_allChannels is IVirtualStreamList virtualList)
                {
                    virtualList.ParallelScanInto(grouped);
                }
                else
                {
                    // Fallback for non-virtualized lists (rare)
                    Parallel.ForEach(Partitioner.Create(0, _allChannels.Count), range =>
                    {
                        for (int i = range.Item1; i < range.Item2; i++)
                        {
                            string catId = _allChannels[i].CategoryId ?? "Genel";
                            var list = grouped.GetOrAdd(catId, _ => new List<int>());
                            lock (list) { list.Add(i); }
                        }
                    });
                }

                foreach (var pair in grouped)
                {
                    _categoryChannelIndices[pair.Key] = pair.Value.OrderBy(x => x).ToArray();
                }

                AppLogger.Info($"[LiveTV] Category index built (Zero-Allocation) | channels={_allChannels.Count} | categories={_categoryChannelIndices.Count}");
            }
            catch (Exception ex)
            {
                AppLogger.Error("[LiveTV] Category index build failed", ex);
                _categoryChannelIndices.Clear();
                _allChannelIndices = Array.Empty<int>();
                throw;
            }
        }

        private int[] GetSelectedCategoryIndices()
        {
            string? categoryId = _selectedCategory?.CategoryId;
            if (string.IsNullOrEmpty(categoryId))
            {
                return Array.Empty<int>();
            }

            return _categoryChannelIndices.TryGetValue(categoryId, out var indices)
                ? indices
                : Array.Empty<int>();
        }

        private static int[] IntersectSortedIndices(int[] left, int[] right)
        {
            if (left == null || right == null || left.Length == 0 || right.Length == 0) return Array.Empty<int>();

            var result = new List<int>(Math.Min(left.Length, right.Length));
            int i = 0, j = 0;

            while (i < left.Length && j < right.Length)
            {
                if (left[i] == right[j]) { result.Add(left[i]); i++; j++; }
                else if (left[i] < right[j]) i++;
                else j++;
            }
            return result.ToArray();
        }


        /// <summary>
        /// PROJECT ZERO: Build channel flags using zero-allocation parallel binary scanning.
        /// Bypasses managed object hydration for 50k+ items.
        /// </summary>
        private void BuildChannelFlags()
        {
            if (_allChannels is VirtualLiveList vList)
            {
                _channelFlags = new ushort[vList.Count];
                vList.ParallelScanFlagsInto(_channelFlags, Services.ProbeCacheService.Instance);
            }
            else
            {
                // Fallback for non-virtualized lists (legacy)
                _channelFlags = new ushort[_allChannels.Count];
                for (int i = 0; i < _allChannels.Count; i++)
                {
                    _channelFlags[i] = Services.ProbeCacheService.Instance.GetFlags((int)_allChannels[i].Id);
                }
            }
        }


        // Bit flag constants (must fit in ushort)
        private const ushort CF_RES_4K = 1 << 0;
        private const ushort CF_RES_1080 = 1 << 1;
        private const ushort CF_RES_720 = 1 << 2;
        private const ushort CF_RES_SD = 1 << 3;
        private const ushort CF_ONLINE = 1 << 4;
        private const ushort CF_UNSTABLE = 1 << 5;
        private const ushort CF_HEVC = 1 << 6;
        private const ushort CF_AVC = 1 << 7;
        private const ushort CF_HDR = 1 << 8;
        private const ushort CF_HAS_BITRATE = 1 << 9;
        private const ushort CF_HIGH_FPS = 1 << 10;

        // ==========================================
        // Phase C: Optimized Single-Pass Filter Pipeline
        // ==========================================
        private void UpdateChannelList()
        {
            // Update HeroSection visibility based on search
            if (HeroSection != null)
            {
                bool isSearching = !string.IsNullOrWhiteSpace(_searchQuery);
                HeroSection.Visibility = (_recentChannels.Count > 0 && !isSearching) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_selectedCategory == null || _allChannels.Count == 0) return;

            // Clear Queue on View Change to prioritize new items
            _probingQueue.Clear();
            _queuedUrls.Clear();

            bool isGlobal = _selectedCategory.CategoryId == "-1";

            // Phase C: Single-pass filter pipeline
            var filteredList = FilterChannelsSinglePass(isGlobal);

            // Setup Header
            HeaderTitle.Text = !string.IsNullOrWhiteSpace(_searchQuery)
                ? (isGlobal ? $"'{SearchBox.Text}' için Sonuçlar" : $"{_selectedCategory.CategoryName} içinde '{SearchBox.Text}'")
                : _selectedCategory.CategoryName;

            HeaderCount.Text = $"({filteredList.Count})";

            ChannelGridView.ItemsSource = filteredList;

            // Auto-Probe is now always enabled (Viewport based)
            _canAutoProbe = AppSettings.IsAutoProbeEnabled;

            EmptyStatePanel.Visibility = filteredList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (filteredList.Count > 0)
            {
                DispatcherQueue.TryEnqueue(() => QueueVisibleItems());
            }
        }

        /// <summary>
        /// Phase C: Single-pass filter pipeline using pre-computed search index + flags.
        /// PROJECT ZERO: Returns a VirtualizedView to prevent 50k+ object hydration.
        /// </summary>
        private IList<LiveStream> FilterChannelsSinglePass(bool isGlobal)
        {
            if (_allChannels == null || _channelFlags == null) return Array.Empty<LiveStream>();

            // 1. Determine candidate indices via Unified Indexing
            int[] candidateIndices;
            
            if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                int[] searchResults = Services.Iptv.IptvMatchService.Instance.SearchIndices(_searchQuery, "live");
                if (searchResults.Length == 0) return Array.Empty<LiveStream>();

                if (!isGlobal)
                {
                    candidateIndices = IntersectSortedIndices(searchResults, GetSelectedCategoryIndices());
                }
                else
                {
                    candidateIndices = searchResults;
                }
            }
            else
            {
                candidateIndices = isGlobal ? _allChannelIndices : GetSelectedCategoryIndices();
            }

            if (candidateIndices.Length == 0) return Array.Empty<LiveStream>();

            // 2. Compute filter masks
            ushort requiredFlags = 0;
            ushort excludeFlags = 0;
            bool q4k = Filter4K.IsChecked ?? false;
            bool qfhd = FilterFHD.IsChecked ?? false;
            bool qhd = FilterHD.IsChecked ?? false;
            bool qsd = FilterSD.IsChecked ?? false;
            bool hasQualityFilter = q4k || qfhd || qhd || qsd;

            if (FilterOnline.IsChecked ?? false) requiredFlags |= CF_ONLINE;
            if (FilterNoFakes.IsChecked ?? false) { requiredFlags |= CF_ONLINE; excludeFlags |= CF_UNSTABLE; }
            if (FilterHEVC.IsChecked ?? false) requiredFlags |= CF_HEVC;
            if (FilterHDR.IsChecked ?? false) requiredFlags |= CF_HDR;
            if (FilterHighFPS.IsChecked ?? false) requiredFlags |= CF_HIGH_FPS;

            // Single-pass filter over indices (Zero-Allocation)
            var resultIndices = new List<int>(Math.Min(candidateIndices.Length, 5000));

            foreach (int idx in candidateIndices)
            {
                if (idx < 0 || idx >= _channelFlags.Length) continue;
                ushort flags = _channelFlags[idx];

                // Bitwise flag filters
                if ((flags & requiredFlags) != requiredFlags) continue;
                if ((flags & excludeFlags) != 0) continue;

                // Quality filters (Zero-Allocation via pre-computed flags)
                if (hasQualityFilter)
                {
                    bool matchQuality = false;
                    if (q4k && (flags & CF_RES_4K) != 0) matchQuality = true;
                    if (qfhd && (flags & CF_RES_1080) != 0) matchQuality = true;
                    if (qhd && (flags & CF_RES_720) != 0) matchQuality = true;
                    if (qsd && (flags & CF_RES_SD) != 0) matchQuality = true;
                    if (!matchQuality) continue;
                }

                resultIndices.Add(idx);
            }

            // 3. Apply sorting (On indices)
            int[] finalIndices = resultIndices.ToArray();
            if (_currentSortMode != LiveSortMode.Default && finalIndices.Length > 0)
            {
                finalIndices = ApplySortingFast(finalIndices);
            }

            // Return VirtualizedView to stop hydration leak
            return new VirtualizedView<LiveStream>(_allChannels, finalIndices);
        }

        private int[] ApplySortingFast(int[] indices)
        {
            if (_currentSortMode == LiveSortMode.Default) return indices;

            IOrderedEnumerable<int> sorted;
            bool desc = _isSortDescending;

            switch (_currentSortMode)
            {
                case LiveSortMode.Name:
                    sorted = desc 
                        ? indices.OrderByDescending(GetSortName) 
                        : indices.OrderBy(GetSortName);
                    break;

                case LiveSortMode.Quality:
                    if (desc)
                    {
                        sorted = indices.OrderByDescending(GetResolutionWeight)
                                     .ThenByDescending(GetBitrateWeight)
                                     .ThenByDescending(GetFpsWeight)
                                     .ThenByDescending(GetCodecWeight)
                                     .ThenByDescending(GetHdrWeight)
                                     .ThenBy(GetSortName);
                    }
                    else
                    {
                        sorted = indices.OrderBy(idx => GetResolutionWeight(idx) == 0 ? 9999 : GetResolutionWeight(idx))
                                     .ThenBy(GetResolutionWeight)
                                     .ThenBy(GetBitrateWeight)
                                     .ThenBy(GetFpsWeight)
                                     .ThenBy(GetCodecWeight)
                                     .ThenBy(GetHdrWeight)
                                     .ThenBy(GetSortName);
                    }
                    break;

                case LiveSortMode.OnlineFirst:
                    sorted = desc 
                        ? indices.OrderByDescending(GetStatusWeight).ThenBy(GetSortName)
                        : indices.OrderBy(GetStatusWeight).ThenBy(GetSortName);
                    break;
                default:
                    return indices;
            }

            return sorted.ToArray();
        }

        private string GetSortName(int idx)
        {
            Span<char> buffer = stackalloc char[256];
            if (_allChannels is IVirtualStreamList vList)
            {
                return vList.GetTitleSpan(idx, buffer).ToString();
            }
            return "";
        }

        private int GetResolutionWeight(int idx)
        {
            ushort f = _channelFlags[idx];
            if ((f & CF_RES_4K) != 0) return 3840;
            if ((f & CF_RES_1080) != 0) return 1080;
            if ((f & CF_RES_720) != 0) return 720;
            if ((f & CF_RES_SD) != 0) return 480;
            return 0;
        }

        private long GetBitrateWeight(int idx)
        {
            if (_allChannels is IVirtualStreamList vList)
            {
                return Services.ProbeCacheService.Instance.Get(vList.GetStreamId(idx))?.Bitrate ?? 0;
            }
            return 0;
        }

        private double GetFpsWeight(int idx)
        {
            if (_allChannels is IVirtualStreamList vList)
            {
                var fpsStr = Services.ProbeCacheService.Instance.Get(vList.GetStreamId(idx))?.Fps;
                if (fpsStr != null && double.TryParse(fpsStr, out double res)) return res;
            }
            return 0;
        }

        private int GetCodecWeight(int idx)
        {
            ushort f = _channelFlags[idx];
            if ((f & CF_HEVC) != 0) return 2;
            if ((f & CF_AVC) != 0) return 1;
            return 0;
        }

        private int GetHdrWeight(int idx)
        {
            return (_channelFlags[idx] & CF_HDR) != 0 ? 1 : 0;
        }

        private int GetStatusWeight(int idx)
        {
            ushort f = _channelFlags[idx];
            if ((f & CF_ONLINE) != 0) return 2;
            if ((f & CF_UNSTABLE) != 0) return 1;
            return 0;
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
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name, LogoUrl: stream.StreamIcon, Type: "live"));
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
                        StreamIcon = item.PosterUrl
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
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name, LogoUrl: stream.StreamIcon, Type: "live"));
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
                            // BATCHED: All property updates in single DispatcherQueue call + IsLoading suppress
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                item.IsLoading = true;
                                item.Resolution = cached.Resolution;
                                item.Fps = cached.Fps;
                                item.Codec = cached.Codec;
                                item.Bitrate = cached.Bitrate;
                                item.IsOnline = true; // Cached implies success
                                item.IsHdr = cached.IsHdr;
                                item.IsProbing = false;
                                item.IsLoading = false;
                            });
                            continue; // Valid cache hit: Process next item IMMEDIATELY (No Delay)
                        }

                        // Process (This takes ~0.5s - 1.5s)
                        var result = await Services.StreamProberService.Instance.ProbeAsync(item.StreamId, item.StreamUrl, ct);

                        // BATCHED: All property updates in single DispatcherQueue call + IsLoading suppress
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            item.IsLoading = true;
                            item.Resolution = result.Resolution;
                            item.Fps = result.Fps;
                            item.Codec = result.Codec;
                            item.Bitrate = result.Bitrate;
                            item.IsOnline = result.Success;
                            item.IsHdr = result.IsHdr;
                            item.IsProbing = false;
                            item.IsLoading = false;
                        });

                        // Small delay only for REAL probes to prevent CPU choking
                        await Task.Delay(250, ct);
                    }
                    catch
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            item.IsProbing = false;
                            item.IsLoading = false;
                        });
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
            _allCategories = Array.Empty<LiveCategory>();
            _allChannels = Array.Empty<LiveStream>();
            
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

    // ==========================================
    // Phase 2.2: ChannelIconConverter
    // Converts channel icon URL to BitmapImage with DecodePixelWidth=48.
    // Prevents full-resolution image loading for 48x48 display area.
    // ==========================================
    public class ChannelIconConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> _cache = new();
        private const int MAX_CACHE_SIZE = 500;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var url = value as string;
            if (string.IsNullOrEmpty(url)) return null;

            if (_cache.TryGetValue(url, out var cached)) return cached;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.DecodePixelWidth = 48; // Matches the 48x48 Border in the template
                bitmap.DecodePixelType = DecodePixelType.Logical;
                bitmap.UriSource = new Uri(url);

                if (_cache.Count > MAX_CACHE_SIZE) _cache.Clear();
                _cache.TryAdd(url, bitmap);
                return bitmap;
            }
            catch { return null; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
