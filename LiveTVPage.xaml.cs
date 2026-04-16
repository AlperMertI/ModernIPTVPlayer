using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Media.Imaging;
using MpvWinUI;
using ModernIPTVPlayer.Services;
using System.Collections.Concurrent;

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

        // Phase A/B: Pre-computed search structures
        private ushort[] _channelFlags = Array.Empty<ushort>();  // Bit field for quality/health/tech filters
        private Dictionary<LiveStream, int> _channelToIndex = new();  // Fast reverse lookup: channel object → index
        
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

                    _allCategories = cachedCats;
                    _allChannels = cachedStreams; // Raw list

                    // -------------------------------------------------------
                    // 1. Create "All Channels" category
                    // -------------------------------------------------------
                    var allCat = _allCategories.FirstOrDefault(c => c.CategoryId == "-1");
                    if (allCat == null)
                    {
                        allCat = new LiveCategory { CategoryName = "Tüm Kanallar", CategoryId = "-1" };
                        _allCategories.Insert(0, allCat);
                    }
                    allCat.Channels = _allChannels;

                    // 2. Group channels by category (single-pass, ~50-100ms for 50k channels)
                    var grouped = new Dictionary<string, List<LiveStream>>(_allCategories.Count, StringComparer.Ordinal);
                    foreach (var s in _allChannels)
                    {
                        if (!grouped.TryGetValue(s.CategoryId, out var list))
                            grouped[s.CategoryId] = list = new List<LiveStream>();
                        list.Add(s);
                    }
                    foreach (var cat in _allCategories)
                    {
                        if (cat.CategoryId != "-1")
                            cat.Channels = grouped.TryGetValue(cat.CategoryId, out var list) ? list : new List<LiveStream>();
                    }

                    // 3. Build flags immediately (needed for filtering)
                    BuildChannelFlags();

                    // 4. Show UI
                    CategoryListView.ItemsSource = _allCategories;

                    // Restoration Logic
                    var lastId = AppSettings.LastLiveCategoryId;
                    var targetCat = _allCategories.FirstOrDefault(c => c.CategoryId == lastId) ?? allCat;

                    CategoryListView.SelectedItem = targetCat;
                    CategoryListView.ScrollIntoView(targetCat);
                    SelectCategory(targetCat);

                    LoadRecentChannels();

                    HideLoadingSkeleton();

                    // -------------------------------------------------------
                    // BACKGROUND: URL reconstruction + probe cache
                    // -------------------------------------------------------
                    _ = Task.Run(() =>
                    {
                        Parallel.ForEach(_allChannels, s =>
                        {
                            if (string.IsNullOrEmpty(s.StreamUrl))
                            {
                                string ext = string.IsNullOrEmpty(s.ContainerExtension) ? "ts" : s.ContainerExtension;
                                s.StreamUrl = $"{baseUrl}/live/{username}/{password}/{s.StreamId}.{ext}";
                            }
                        });
                    });

                    // Probe cache init — fire-and-forget (probe worker handles lazy loading)
                    _ = Services.ProbeCacheService.Instance.InitializeForPlaylistAsync(playlistId);
                }

                // 2. NETWORK STRATEGY: If Cache Missing (or forced refresh needed)
                if (!cacheLoaded)
                {
                    // ZERO-ALLOCATION PARSING (Phase 1.3): Parse directly from HTTP stream into structs + MetadataBuffer
                    // Falls back to JsonSerializer if zero-alloc parser fails (robustness)
                    string api = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_live_categories";
                    string streamApi = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_live_streams";

                    List<LiveCategory> categories = null;
                    List<LiveStream> streams = null;

                    try
                    {
                        // Parse in parallel with zero-allocation parser
                        var catTask = Services.ZeroAllocJsonParser.ParseLiveCategoriesAsync(_httpClient, api, cancellationToken);
                        var streamTask = Services.ZeroAllocJsonParser.ParseLiveStreamsAsync(_httpClient, streamApi, cancellationToken);
                        await Task.WhenAll(catTask, streamTask);
                        categories = catTask.Result;
                        streams = streamTask.Result;
                    }
                    catch (Exception ex)
                    {
                        // Fallback: Use traditional JsonSerializer if zero-alloc parser fails
                        System.Diagnostics.Debug.WriteLine($"[ZeroAlloc] Parser failed: {ex.Message}. Falling back to JsonSerializer.");

                        var catTask = _httpClient.GetStringAsync(api, cancellationToken);
                        var streamTask = _httpClient.GetStringAsync(streamApi, cancellationToken);
                        await Task.WhenAll(catTask, streamTask);

                        string catJson = catTask.Result;
                        string streamJson = streamTask.Result;

                        categories = HttpHelper.TryDeserializeList<LiveCategory>(catJson);
                        streams = HttpHelper.TryDeserializeList<LiveStream>(streamJson);
                    }

                    if (streams != null && streams.Count > 0)
                    {
                        // URL Construction
                        foreach (var s in streams)
                        {
                            s.StreamUrl = $"{baseUrl}/live/{username}/{password}/{s.StreamId}.ts";
                        }

                        // 3. CACHE SAVE (Background)
                        _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "live_cats", categories ?? new List<LiveCategory>());
                        _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "live_streams", streams);

                        // 4. SINGLE-PASS CATEGORY GROUPING (O(N) instead of O(N×M))
                        categories ??= new List<LiveCategory>();
                        var allCat = new LiveCategory { CategoryName = "Tüm Kanallar", CategoryId = "-1" };
                        _allCategories = new List<LiveCategory>(categories.Count + 1) { allCat };
                        _allCategories.AddRange(categories);
                        _allChannels = streams;

                        // Group channels by category in a single pass
                        var grouped = new Dictionary<string, List<LiveStream>>(categories.Count + 1, StringComparer.Ordinal);
                        foreach (var s in _allChannels)
                        {
                            if (!grouped.TryGetValue(s.CategoryId, out var list))
                                grouped[s.CategoryId] = list = new List<LiveStream>();
                            list.Add(s);
                        }

                        allCat.Channels = _allChannels;
                        foreach (var cat in categories)
                        {
                            cat.Channels = grouped.TryGetValue(cat.CategoryId, out var list) ? list : new List<LiveStream>();
                        }

                        // Phase B: Build flags immediately (needed for filtering)
                        BuildChannelFlags();

                        // LAZY PROBE CACHE (Phase 3.2): Initialize in background, don't block UI
                        // The probe worker checks cache before each probe — if not loaded yet, it proceeds normally
                        _ = Services.ProbeCacheService.Instance.InitializeForPlaylistAsync(playlistId);

                        // Hydrate Metadata from ID-Based ProbeCache (Network Path v2.4)
                        // This will only hit if probe cache is already loaded (non-blocking)
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
            List<LiveStream> cachedStreams = null;
            List<LiveCategory> cachedCats = null;
            bool cacheLoaded = false;

            if (!ignoreCache)
            {
                cachedStreams = await Services.ContentCacheService.Instance.LoadLiveStreamsBinaryAsync(playlistId);
                cachedCats = await Services.ContentCacheService.Instance.LoadCacheAsync<LiveCategory>(playlistId, "live_cats_m3u");
                cacheLoaded = (cachedStreams != null && cachedCats != null && cachedStreams.Count > 0);
            }

            if (cacheLoaded)
            {
                // FAST PATH: Load from binary cache, show UI immediately
                _allChannels = cachedStreams;
                _allCategories = cachedCats;

                // Immediate UI
                var allCat = _allCategories.FirstOrDefault(c => c.CategoryId == "-1");
                if (allCat != null)
                {
                    allCat.Channels = _allChannels;
                }

                CategoryListView.ItemsSource = _allCategories;
                CategoryListView.SelectedIndex = 0;
                SelectCategory(allCat);
                LoadRecentChannels();
                HideLoadingSkeleton();

                // Background: everything else
                _ = Task.Run(() =>
                {
                    // URL Reconstruction (only if needed)
                    Parallel.ForEach(_allChannels, s =>
                    {
                        if (string.IsNullOrEmpty(s.StreamUrl))
                        {
                            // M3U channels already have full URLs in StreamUrl
                            // No action needed unless StreamUrl is empty
                        }
                    });
                });

                // Probe cache init — fire-and-forget
                _ = Services.ProbeCacheService.Instance.InitializeForPlaylistAsync(playlistId);

                return;
            }

            // -------------------------------------------------------------
            // 2. NETWORK PATH: Download, Parse, Cache, Display
            // -------------------------------------------------------------
            try
            {
                ShowLoadingSkeleton();

                // Phase 4.2: Streaming M3U parse — avoid loading entire file into memory
                var categories = await Task.Run(async () =>
                {
                    using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    return ParseM3uStreaming(stream);
                }, cancellationToken);

                if (categories == null || categories.Count == 0)
                {
                    await ShowMessageDialog("Hata", "M3U listesinde kanal bulunamadı.");
                    return;
                }

                var allCat = new LiveCategory { CategoryName = "Tüm Kanallar", CategoryId = "-1" };

                // Populate All Channels (single pass — SelectMany is efficient here)
                _allChannels = categories.SelectMany(c => c.Channels).ToList();
                allCat.Channels = _allChannels;

                _allCategories = new List<LiveCategory> { allCat };
                _allCategories.AddRange(categories);

                // SAVE TO BINARY CACHE (background, fire-and-forget)
                _ = Services.ContentCacheService.Instance.SaveLiveStreamsBinaryAsync(playlistId, _allChannels);
                _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "live_cats_m3u", _allCategories);

                // Phase B: Build flags immediately (needed for filtering)
                BuildChannelFlags();

                // Search index: lazy (built on first search)

                CategoryListView.ItemsSource = _allCategories;
                CategoryListView.SelectedIndex = 0;
                SelectCategory(allCat);
                LoadRecentChannels();
            }
            catch (Exception ex)
            {
                await ShowMessageDialog("Hata", ex.Message);
            }
            finally
            {
                HideLoadingSkeleton();
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
                    // Generate a stable ID from the URL hash for M3U (SHA256-based)
                    int m3uId = 0;
                    if (!string.IsNullOrEmpty(trimLine))
                    {
                        // Use SHA256 for stable, collision-resistant IDs (.NET 8 compatible)
                        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trimLine));
                        m3uId = BitConverter.ToInt32(hashBytes, 0) & 0x7FFFFFFF; // Ensure positive
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

        /// <summary>
        /// Phase 4.2: Streaming M3U parser — processes line-by-line without loading entire file into memory.
        /// Uses StreamReader.ReadLineAsync() for memory-efficient parsing of large M3U files.
        /// </summary>
        private static List<LiveCategory> ParseM3uStreaming(Stream stream)
        {
            var result = new Dictionary<string, LiveCategory>();

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
                        // Same SHA256-based hash as ParseM3u
                        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trimLine));
                        m3uId = BitConverter.ToInt32(hashBytes, 0) & 0x7FFFFFFF;
                    }

                    if (!result.TryGetValue(currentGroup!, out var cat))
                    {
                        cat = new LiveCategory { CategoryName = currentGroup!, CategoryId = currentGroup!, Channels = new List<LiveStream>() };
                        result[currentGroup!] = cat;
                    }

                    cat.Channels.Add(new LiveStream
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

            // Lazy build search index on first search
            if (!Services.ChannelSearchIndex.IsBuilt && _allChannels.Count > 0)
                BuildChannelSearchStructures();

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

        /// <summary>
        /// Build ONLY channel flags + reverse index map (fast ~50ms for 50k channels).
        /// Called immediately after cache load so filters work.
        /// </summary>
        private void BuildChannelFlags()
        {
            if (_channelFlags.Length == _allChannels.Count) return; // Already built

            // Build reverse map (channel object → index) — needed for category filtering
            _channelToIndex.Clear();

            _channelFlags = new ushort[_allChannels.Count];
            for (int i = 0; i < _allChannels.Count; i++)
            {
                var c = _allChannels[i];
                _channelToIndex[c] = i;

                ushort flags = 0;

                // Resolution flags
                var res = c.Resolution?.ToUpperInvariant() ?? "";
                if (res.Contains("4K") || res.Contains("2160") || res.Contains("3840")) flags |= CF_RES_4K;
                if (res.Contains("1080") || res.Contains("FHD")) flags |= CF_RES_1080;
                if (res.Contains("720")) flags |= CF_RES_720;
                if (res.Contains("576") || res.Contains("480") || res.Contains("SD")) flags |= CF_RES_SD;

                // Health flags
                if (c.IsOnline == true) flags |= CF_ONLINE;
                if (c.IsUnstable) flags |= CF_UNSTABLE;

                // Codec flags
                var codec = c.Codec?.ToUpperInvariant() ?? "";
                if (codec.Contains("HEVC") || codec.Contains("H265") || codec.Contains("H.265")) flags |= CF_HEVC;
                if (codec.Contains("AVC") || codec.Contains("H264") || codec.Contains("H.264")) flags |= CF_AVC;

                // Tech flags
                if (c.IsHdr) flags |= CF_HDR;
                if (c.Bitrate > 0) flags |= CF_HAS_BITRATE;

                // FPS flag (high FPS = 50+)
                var fpsStr = c.Fps?.Replace(" fps", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (double.TryParse(fpsStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fpsVal) && fpsVal >= 49.0)
                    flags |= CF_HIGH_FPS;

                _channelFlags[i] = flags;
            }
        }

        /// <summary>
        /// Build search index only (deferred until first search).
        /// Reverse map is already built by BuildChannelFlags().
        /// </summary>
        private void BuildChannelSearchStructures()
        {
            if (Services.ChannelSearchIndex.IsBuilt) return;

            // Phase A: Build token search index from channel names
            var names = new string[_allChannels.Count];
            for (int i = 0; i < _allChannels.Count; i++)
            {
                names[i] = _allChannels[i].Name;
            }
            Services.ChannelSearchIndex.BuildIndex(names);
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

            // Trigger initial probe for visible items once data is loaded
            if (filteredList.Count > 0)
            {
                DispatcherQueue.TryEnqueue(() => QueueVisibleItems());
            }
        }

        /// <summary>
        /// Phase C: Single-pass filter pipeline using pre-computed search index + flags.
        /// Replaces 5 chained .Where() calls with one optimized pass.
        /// </summary>
        private List<LiveStream> FilterChannelsSinglePass(bool isGlobal)
        {
            // Determine candidate indices
            int[] candidateIndices;
            if (!string.IsNullOrWhiteSpace(_searchQuery) && Services.ChannelSearchIndex.IsBuilt)
            {
                // Use token index for search
                int[] searchResults = Services.ChannelSearchIndex.Search(_searchQuery);
                if (searchResults.Length == 0) return new List<LiveStream>();

                // If not global, filter to only indices within the selected category
                if (!isGlobal && _selectedCategory?.Channels != null)
                {
                    // Use pre-built reverse index for O(1) lookup
                    var categorySet = new HashSet<int>();
                    foreach (var ch in _selectedCategory.Channels)
                    {
                        if (_channelToIndex.TryGetValue(ch, out int idx))
                            categorySet.Add(idx);
                    }

                    // Intersect search results with category indices
                    var filtered = new List<int>(Math.Min(searchResults.Length, categorySet.Count));
                    foreach (var idx in searchResults)
                    {
                        if (categorySet.Contains(idx)) filtered.Add(idx);
                    }
                    candidateIndices = filtered.ToArray();
                }
                else
                {
                    candidateIndices = searchResults;
                }
            }
            else
            {
                // No search query — use all channels or category channels
                if (isGlobal)
                {
                    candidateIndices = new int[_allChannels.Count];
                    for (int i = 0; i < _allChannels.Count; i++) candidateIndices[i] = i;
                }
                else if (_selectedCategory?.Channels != null)
                {
                    var indices = new List<int>(_selectedCategory.Channels.Count);
                    foreach (var ch in _selectedCategory.Channels)
                    {
                        if (_channelToIndex.TryGetValue(ch, out int idx))
                            indices.Add(idx);
                    }
                    candidateIndices = indices.ToArray();
                }
                else
                {
                    return new List<LiveStream>();
                }
            }

            if (candidateIndices.Length == 0) return new List<LiveStream>();

            // Compute filter masks from UI state
            ushort requiredFlags = 0;
            ushort excludeFlags = 0;
            bool hasQualityFilter = false;

            // 1. Quality Filters
            bool q4k = Filter4K.IsChecked ?? false;
            bool qfhd = FilterFHD.IsChecked ?? false;
            bool qhd = FilterHD.IsChecked ?? false;
            bool qsd = FilterSD.IsChecked ?? false;

            if (q4k || qfhd || qhd || qsd)
            {
                hasQualityFilter = true;
            }

            // 2. Health Filters
            if (FilterOnline.IsChecked ?? false)
            {
                requiredFlags |= CF_ONLINE;
            }
            if (FilterNoFakes.IsChecked ?? false)
            {
                requiredFlags |= CF_ONLINE;
                excludeFlags |= CF_UNSTABLE;
            }

            // 3. Technical Filters
            bool needHevc = FilterHEVC.IsChecked ?? false;
            bool needHdr = FilterHDR.IsChecked ?? false;
            bool needHighFps = FilterHighFPS.IsChecked ?? false;

            if (needHevc) requiredFlags |= CF_HEVC;
            if (needHdr) requiredFlags |= CF_HDR;
            if (needHighFps) requiredFlags |= CF_HIGH_FPS;

            bool needsFlagFilter = (requiredFlags != 0) || (excludeFlags != 0) || hasQualityFilter;
            _ = needsFlagFilter; // Used implicitly by flag checks below

            // Single-pass filter
            var result = new List<LiveStream>(Math.Min(candidateIndices.Length, 5000));

            foreach (int idx in candidateIndices)
            {
                if (idx < 0 || idx >= _channelFlags.Length) continue;
                ushort flags = _channelFlags[idx];

                // Apply bitwise flag filters
                if ((flags & requiredFlags) != requiredFlags) continue;
                if ((flags & excludeFlags) != 0) continue;

                // Quality filters (still need string checks for some edge cases)
                if (hasQualityFilter)
                {
                    var res = _allChannels[idx].Resolution?.ToUpperInvariant() ?? "";
                    bool matchQuality = false;
                    if (q4k && (res.Contains("4K") || res.Contains("2160") || res.Contains("3840"))) matchQuality = true;
                    if (qfhd && res.Contains("1080")) matchQuality = true;
                    if (qhd && res.Contains("720")) matchQuality = true;
                    if (qsd && (res.Contains("576") || res.Contains("480") || res.Contains("SD"))) matchQuality = true;
                    if (!matchQuality) continue;
                }

                result.Add(_allChannels[idx]);
            }

            // Apply sorting
            if (_currentSortMode != LiveSortMode.Default && result.Count > 0)
            {
                result = ApplySortingFast(result);
            }

            return result;
        }

        private List<LiveStream> ApplySortingFast(List<LiveStream> source)
        {
            if (_currentSortMode == LiveSortMode.Default) return source;

            IEnumerable<LiveStream> result;
            switch (_currentSortMode)
            {
                case LiveSortMode.Name:
                    result = _isSortDescending ? source.OrderByDescending(c => c.Name) : source.OrderBy(c => c.Name);
                    break;

                case LiveSortMode.Quality:
                    if (_isSortDescending)
                    {
                        result = source.OrderByDescending(GetResolutionValue)
                                     .ThenByDescending(GetBitrateValue)
                                     .ThenByDescending(GetFpsValue)
                                     .ThenByDescending(GetCodecWeight)
                                     .ThenByDescending(c => c.IsHdr)
                                     .ThenBy(c => c.Name);
                    }
                    else
                    {
                        result = source.OrderBy(c => GetResolutionValue(c) == 0 ? int.MaxValue : GetResolutionValue(c))
                                     .ThenBy(GetResolutionValue)
                                     .ThenBy(GetBitrateValue)
                                     .ThenBy(GetFpsValue)
                                     .ThenBy(GetCodecWeight)
                                     .ThenBy(c => c.IsHdr)
                                     .ThenBy(c => c.Name);
                    }
                    break;

                case LiveSortMode.OnlineFirst:
                    if (_isSortDescending)
                    {
                        result = source.OrderByDescending(GetStatusWeight)
                                     .ThenBy(c => c.Name);
                    }
                    else
                    {
                        result = source.OrderBy(GetStatusWeight)
                                     .ThenBy(c => c.Name);
                    }
                    break;

                case LiveSortMode.Recent:
                    var recentUrls = _recentChannels.Select(r => r.StreamUrl).ToList();
                    if (_isSortDescending)
                    {
                        result = source.OrderByDescending(c => recentUrls.Contains(c.StreamUrl))
                                     .ThenBy(c => {
                                         int idx = recentUrls.IndexOf(c.StreamUrl);
                                         return idx == -1 ? int.MaxValue : idx;
                                     })
                                     .ThenBy(c => c.Name);
                    }
                    else
                    {
                        result = source.OrderBy(c => recentUrls.Contains(c.StreamUrl))
                                     .ThenByDescending(c => {
                                         int idx = recentUrls.IndexOf(c.StreamUrl);
                                         return idx == -1 ? int.MinValue : idx;
                                     })
                                     .ThenBy(c => c.Name);
                    }
                    break;

                default:
                    return source;
            }

            return result.ToList();
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
