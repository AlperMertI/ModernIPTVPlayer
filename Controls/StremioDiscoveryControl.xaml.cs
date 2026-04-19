using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using Windows.Foundation;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Json;
using ModernIPTVPlayer.Services.Stremio;
using ModernIPTVPlayer.Services.Metadata;
using ModernIPTVPlayer.Models.Metadata;
using Microsoft.UI.Xaml.Data;

namespace ModernIPTVPlayer.Controls
{
    public class RowStyleToTemplateConverter : IValueConverter
    {
        public DataTemplate StandardTemplate { get; set; }
        public DataTemplate LandscapeTemplate { get; set; }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string style)
            {
                if (style == "Landscape") return LandscapeTemplate;
            }
            return StandardTemplate;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class DiscoveryRowTemplateSelector : DataTemplateSelector
    {
        public DataTemplate StandardRowTemplate { get; set; }
        public DataTemplate SpotlightRowTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            if (item is CatalogRowViewModel vm)
            {
                if (vm.RowStyle == "Spotlight")
                {
                    return SpotlightRowTemplate;
                }
            }
            return StandardRowTemplate;
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            return SelectTemplateCore(item);
        }
    }

    public sealed partial class StremioDiscoveryControl : UserControl
    {
        // Public Events
        public event EventHandler<(IMediaStream Stream, UIElement SourceElement, Microsoft.UI.Xaml.Media.ImageSource PreloadedLogo)> ItemClicked;
        public event EventHandler<(IMediaStream Stream, UIElement SourceElement)> TrailerExpandRequested;
        public event EventHandler<IMediaStream> PlayAction;
        public event EventHandler<(Windows.UI.Color Primary, Windows.UI.Color Secondary)> BackdropColorChanged;
        public event EventHandler<ScrollViewerViewChangedEventArgs> ViewChanged;
        public event EventHandler<CatalogRowViewModel> HeaderClicked;

        public static readonly DependencyProperty ShowIptvBadgeProperty =
            DependencyProperty.Register("ShowIptvBadge", typeof(bool), typeof(StremioDiscoveryControl), new PropertyMetadata(true));

        public bool ShowIptvBadge
        {
            get => (bool)GetValue(ShowIptvBadgeProperty);
            set => SetValue(ShowIptvBadgeProperty, value);
        }
        
        // Expanded Card Event Bridges
        public event EventHandler<FrameworkElement> CardHoverStarted;
        public event EventHandler<FrameworkElement> CardHoverEnded;
        public event EventHandler RowScrollStarted;
        public event EventHandler RowScrollEnded;

        // Exposed properties for Controller linkage
        public ScrollViewer MainScrollViewer => VisualTreeHelper.GetChild(DiscoveryRows, 0) as ScrollViewer;
        public HeroSectionControl HeroSection => HeroControl;

        private ObservableCollection<CatalogRowViewModel> _discoveryRows = new();
        private int _rowCount = 0;
        private bool _isDiscoveryRunning = false;
        private bool _isDraggingRow = false;
        private System.Threading.CancellationTokenSource? _loadCts;
        private string _currentContentType;
        private int _discoveryVersion = 0; // Monotonic counter to invalidate stale runs
        private (Windows.UI.Color Primary, Windows.UI.Color Secondary)? _lastHeroColors;
        private DispatcherTimer? _heroDebounceTimer;
        private bool _isSourceActive = true;

        // Hero Priority Logic
        internal enum RowState { Pending, Success, Failed }
        private Dictionary<string, RowState> _rowStates = new();
        private Dictionary<string, ObservableCollection<StremioMediaStream>> _rowItemsBuffer = new();
        private List<string> _heroPriorityOrder = new();

        // Track used Spotlight IDs and last style to prevent duplicates/clutter
        private readonly HashSet<string> _usedSpotlightIds = new();
        private string _lastUsedStyle = "Standard";

        // [HERO FIX] Prevent concurrent SetItems races
        private volatile bool _heroItemsSet = false;
        // [HERO FIX] Prevent prewarm spam — only fire once per session for the best-priority item
        private volatile bool _heroPrewarmDone = false;

        // [REGRESSION-FIX] Track current hero state to skip redundant updates (prevents flicker)
        private List<string> _currentHeroIds = new();
        private bool _isFirstHeroLoad = true;
        
        // [PERF FIX] Global throttling for catalog loading to prevent "thundering herd" CPU/Disk saturation
        private static readonly System.Threading.SemaphoreSlim _catalogSemaphore = new System.Threading.SemaphoreSlim(2);

        // --- OPTIMIZATION FIELDS ---
        private static Dictionary<string, List<CachedSlot>> _slotMapCache = new();
        private static Task _historyInitTask;
        private const string LAYOUT_CACHE_FILE = "discovery_layout.bin.zst";

        internal class CachedSlot
        {
            public string BaseUrl { get; set; }
            public StremioCatalog Catalog { get; set; }
            public int SortIndex { get; set; }
            public string RowId { get; set; }
        }
        // ---------------------------

        // Memory Cache for instant switching
        internal class DiscoveryState
        {
            public List<CatalogRowViewModel> Rows { get; set; } = new();
            public Dictionary<string, RowState> RowStates { get; set; } = new();
            public Dictionary<string, ObservableCollection<StremioMediaStream>> RowItemsBuffer { get; set; } = new();
            public List<string> HeroPriorityOrder { get; set; } = new();
            public (Windows.UI.Color Primary, Windows.UI.Color Secondary)? HeroColors { get; set; }
        }
        private Dictionary<string, DiscoveryState> _contentCache = new();

        public StremioDiscoveryControl()
        {
            this.InitializeComponent();
            
            // Hero Events
            HeroControl.PlayAction += (s, e) => PlayAction?.Invoke(this, e);
            HeroControl.ColorExtracted += (s, c) => 
            {
                _lastHeroColors = c;
                BackdropColorChanged?.Invoke(this, c);
            };

            DiscoveryRows.ItemsSource = _discoveryRows;

            StremioAddonManager.Instance.AddonsChanged += OnStremioAddonsChanged;

            // History Events
            HistoryManager.Instance.HistoryChanged += OnHistoryChanged;

            // --- LOG BRIDGE ---
            StremioService.OnLog = msg => HeroPerfLog(msg);
            // ------------------

            // Pre-start history init without blocking
            if (_historyInitTask == null || _historyInitTask.IsFaulted)
                _historyInitTask = HistoryManager.Instance.InitializeAsync();
            
            // Pre-load layout from disk
            _ = LoadLayoutCacheFromDiskAsync();

            this.Unloaded += StremioDiscoveryControl_Unloaded;

            // [FIX] Sync hero rotation with control visibility (handles switching to IPTV/Kütüphanem)
            this.RegisterPropertyChangedCallback(VisibilityProperty, (s, dp) => UpdateHeroLifecycle());
        }

        public void SetSourceActive(bool isActive)
        {
            _isSourceActive = isActive;
            UpdateHeroLifecycle();
        }

        private void UpdateHeroLifecycle()
        {
            bool shouldRun = _isSourceActive && this.Visibility == Visibility.Visible;

            HeroControl.Visibility = shouldRun ? Visibility.Visible : Visibility.Collapsed;
            if (shouldRun)
            {
                HeroControl.ResumeHeroAutoRotationIfRevealed();
            }
            else
            {
                HeroControl.StopAutoRotation();
            }
        }

        private static async Task DeleteLayoutCacheAsync()
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await folder.TryGetItemAsync(LAYOUT_CACHE_FILE);
                if (file != null) await file.DeleteAsync();
            }
            catch { }
        }

        private async Task LoadLayoutCacheFromDiskAsync()
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(LAYOUT_CACHE_FILE);
                
                if (item == null) return;

                var zstFile = await folder.GetFileAsync(LAYOUT_CACHE_FILE);
                using (var stream = await zstFile.OpenStreamForReadAsync())
                using (var decompressor = new ZstdSharp.DecompressionStream(stream))
                {
                    var diskCache = await JsonSerializer.DeserializeAsync(decompressor, AppJsonContext.Default.DictionaryStringListCachedSlot);
                    if (diskCache != null)
                    {
                        lock(_slotMapCache)
                        {
                            foreach(var kv in diskCache)
                            {
                                if (kv.Value == null || kv.Value.Count == 0) continue;
                                _slotMapCache[kv.Key] = kv.Value;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private async Task SaveLayoutCacheToDiskAsync()
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(LAYOUT_CACHE_FILE, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                
                using (var stream = await file.OpenStreamForWriteAsync())
                using (var compressor = new ZstdSharp.CompressionStream(stream, 3))
                {
                    Dictionary<string, List<CachedSlot>> copy;
                    lock (_slotMapCache) copy = new Dictionary<string, List<CachedSlot>>(_slotMapCache);
                    await JsonSerializer.SerializeAsync(compressor, copy, AppJsonContext.Default.DictionaryStringListCachedSlot);
                }
            }
            catch { }
        }

        private void OnHistoryChanged(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                RefreshContinueWatching();
            });
        }

        private void DiscoveryRows_Loaded(object sender, RoutedEventArgs e)
        {
            var sv = MainScrollViewer;
            if (sv != null)
            {
                sv.ViewChanged -= ScrollViewer_ViewChanged;
                sv.ViewChanged += ScrollViewer_ViewChanged;
            }
        }

        private void StremioDiscoveryControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // [CRITICAL] Stop hero rotation to prevent background processing and leaks
            HeroControl.StopAutoRotation();

            // [CRITICAL] Unsubscribe from static event to prevent leak
            StremioAddonManager.Instance.AddonsChanged -= OnStremioAddonsChanged;
            HistoryManager.Instance.HistoryChanged -= OnHistoryChanged;

            var sv = MainScrollViewer;
            if (sv != null)
            {
                sv.ViewChanged -= ScrollViewer_ViewChanged;
            }
        }

        private DispatcherTimer? _addonsChangeDebounce;
        private DateTime _lastAddonsReloadAt = DateTime.MinValue;

        private void OnStremioAddonsChanged(object sender, EventArgs e)
        {
            lock (_slotMapCache) _slotMapCache.Clear();
            _ = DeleteLayoutCacheAsync();
            OnAddonsChanged(sender, e);
        }

        private void OnAddonsChanged(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Debounce AddonsChanged storms — during startup the manifests fan in one at a time and we
                // previously fired a full LoadDiscoveryAsync per event, each of which would reset the hero.
                if (_addonsChangeDebounce == null)
                {
                    _addonsChangeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                    _addonsChangeDebounce.Tick += async (s2, e2) =>
                    {
                        _addonsChangeDebounce!.Stop();
                        // Cooldown so we don't reload discovery more than once per ~1.5s for the same type.
                        if ((DateTime.UtcNow - _lastAddonsReloadAt).TotalMilliseconds < 1500) return;
                        _lastAddonsReloadAt = DateTime.UtcNow;
                        if (!string.IsNullOrEmpty(_currentContentType) && !_isDiscoveryRunning)
                        {
                            System.Diagnostics.Debug.WriteLine("[StremioControl] Addons changed/loaded (debounced). Reloading discovery incrementally...");
                            await LoadDiscoveryAsync(_currentContentType);
                        }
                    };
                }
                _addonsChangeDebounce.Stop();
                _addonsChangeDebounce.Start();
            });
        }

        public void CancelLoading()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[StremioControl] CancelLoading invoked.");
                _loadCts?.Cancel();
                HeroControl.StopAutoRotation();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioControl] CancelLoading Error: {ex.Message}");
            }
        }

        public bool HasContent => _discoveryRows.Count > 0 && !_discoveryRows.Any(r => r.IsLoading && r.CatalogName == "Yükleniyor...");

        /// <summary>
        /// Refreshes only the "Continue Watching" row without reloading all catalogs.
        /// Call this when returning from the player.
        /// </summary>
        public void RefreshContinueWatching()
        {
            if (string.IsNullOrEmpty(_currentContentType)) return;

            // Remove any existing CW row
            var existing = _discoveryRows.FirstOrDefault(r => r.SortIndex == -1);
            if (existing != null)
                _discoveryRows.Remove(existing);

            lock (_rowItemsBuffer)
            {
                _rowStates.Remove("CW");
                _rowItemsBuffer.Remove("CW");
            }

            // Re-inject with fresh history
            var continueWatching = HistoryManager.Instance.GetContinueWatching(_currentContentType);
            if (continueWatching.Count == 0) return;

            var cwItems = new ObservableCollection<StremioMediaStream>();
            foreach (var hist in continueWatching)
            {
                string backdrop = hist.BackdropUrl;
                if (string.IsNullOrEmpty(backdrop) && !string.IsNullOrEmpty(hist.ParentSeriesId))
                {
                    var parentHist = HistoryManager.Instance.GetProgress(hist.ParentSeriesId);
                    if (parentHist != null && !string.IsNullOrEmpty(parentHist.BackdropUrl))
                        backdrop = parentHist.BackdropUrl;
                }

                // [TITLE FIX] Always show Episode Title (hist.Title) on CW poster
                // Series Name is used for enrichment or subtext if needed.
                var stream = new StremioMediaStream(new StremioMeta
                {
                    Id = hist.ParentSeriesId ?? hist.Id,
                    Name = hist.Title, // Protected Episode Title
                    Poster = hist.PosterUrl,
                    Background = backdrop,
                    Type = hist.ParentSeriesId != null ? "series" : _currentContentType,
                    Description = hist.ParentSeriesId != null && hist.SeasonNumber > 0 && hist.EpisodeNumber > 0 
                        ? $"S{hist.SeasonNumber:D2}E{hist.EpisodeNumber:D2}" 
                        : null
                });
                stream.SeriesName = hist.SeriesName;
                stream.IsContinueWatching = true;
                stream.EpisodeSubtext = hist.ParentSeriesId != null && hist.SeasonNumber > 0 && hist.EpisodeNumber > 0 
                    ? $"S{hist.SeasonNumber:D2}E{hist.EpisodeNumber:D2}" 
                    : null;
                stream.ProgressValue = hist.Duration > 0 ? ((double)hist.Position / hist.Duration) * 100 : 0;
                cwItems.Add(stream);
            }

            var cwRow = new CatalogRowViewModel
            {
                CatalogName = "İzlemeye Devam Et",
                Items = cwItems,
                IsLoading = false,
                RowId = "CW",
                SortIndex = -1,
                RowStyle = "Landscape" // explicitly set to Landscape
            };

            // Insert at position 0
            _discoveryRows.Insert(0, cwRow);

            lock (_rowItemsBuffer)
            {
                _rowStates["CW"] = RowState.Success;
                _rowItemsBuffer["CW"] = cwRow.Items;
            }

            // Asynchronously enrich items that have no poster
            _ = EnrichItemsBatchAsync(cwItems);
        }
        private void ApplyMetadataToStream(StremioMediaStream stream, UnifiedMetadata meta)
        {
            if (stream == null || meta == null) return;
            stream.UpdateFromUnified(meta);
        }


        // Delegates to Helpers.HeroTracer — a single shared writer prevents the first-chance IOException
        // storm that occurred when this class and HeroSectionControl both tried to own the log file.
        private static void HeroPerfLog(string msg) => Helpers.HeroTracer.Log(msg);

        private async Task EnrichItemsBatchAsync(IEnumerable<StremioMediaStream> items, MetadataContext context = MetadataContext.Discovery)
        {
            if (items == null || !items.Any()) return;
            
            var itemList = items.ToList();
            var tasks = itemList.Select(async item => 
            {
                try
                {
                    var meta = await MetadataProvider.Instance.GetMetadataAsync(item, context);
                    if (meta != null)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        {
                            try
                            {
                                if (this.IsLoaded && (_loadCts == null || !_loadCts.Token.IsCancellationRequested))
                                {
                                    ApplyMetadataToStream(item, meta);
                                }
                                tcs.SetResult(true);
                            }
                            catch { tcs.SetResult(false); }
                        });
                        await tcs.Task;
                    }
                }
                catch { }
            });

            await Task.WhenAll(tasks);
        }

        private async Task EnrichRowMetadataAsync(CatalogRowViewModel row, MetadataContext context = MetadataContext.Discovery)
        {
            if (row.RowStyle != "Landscape" && row.RowStyle != "Spotlight") return;
            if (row.Items == null || row.Items.Count == 0) return;

            // [OPTIMIZATION] Only enrich first 5 items for Spotlight (Carousel items)
            var streams = (row.RowStyle == "Spotlight") ? row.Items.Take(5) : row.Items;

            // Only enrich items that need it
            var toEnrich = streams.Where(stream => 
                context == MetadataContext.Spotlight || // ALWAYS enrich if it's Spotlight row to get Logos/Localizations
                string.IsNullOrEmpty(stream.Banner) || 
                string.IsNullOrEmpty(stream.Year) || 
                string.IsNullOrEmpty(stream.Genres) ||
                MetadataProvider.Instance.IsPlaceholderOverview(stream.Description) ||
                string.IsNullOrEmpty(stream.Rating) ||
                stream.Rating == "0.0" || stream.Rating == "0").ToList();

            if (toEnrich.Count > 0)
            {
                await EnrichItemsBatchAsync(toEnrich, context);
            }
        }


        public async Task LoadDiscoveryAsync(string contentType)
        {
            var overallSw = System.Diagnostics.Stopwatch.StartNew();
            // (Logger is a long-lived StreamWriter; deleting the file here would orphan the handle,
            // so we just emit a separator line instead.)
            HeroPerfLog("=== =================================================== ===");
            HeroPerfLog($"=== LoadDiscoveryAsync started for {contentType} ===");
            HeroPerfLog($"[StremioControl] LoadDiscoveryAsync START: {contentType}");

            // Increment version to invalidate any in-flight stale calls
            int myVersion = ++_discoveryVersion;

            if (_isDiscoveryRunning && contentType == _currentContentType) 
            {
                HeroPerfLog($"[StremioControl] Already running for {contentType}, skipping.");
                return;
            }
            
            try
            {
                _isDiscoveryRunning = true;
                _rowCount = 0; // Reset counter for consistent row styles (Spotlight, etc.)
                _isFirstHeroLoad = true;
                _currentHeroIds.Clear();

                // Cancel previous loading
                _loadCts?.Cancel();
                _loadCts = new System.Threading.CancellationTokenSource();
                var token = _loadCts.Token;

                // [OPTIMIZATION] Don't await history immediately. Just ensure it started.
                // It's needed for the CW row and individual items, but we can start Manifest/Catalog tasks now.
                HeroPerfLog("Step 1: History Initialization CHECK");
                if (_historyInitTask == null || _historyInitTask.IsFaulted) 
                    _historyInitTask = HistoryManager.Instance.InitializeAsync();
                
                // We'll await it later only when generating the CW row items.

                // Bail if a newer LoadDiscoveryAsync call has been triggered while we awaited
                if (myVersion != _discoveryVersion) return;

                // Sync UI rows by removing stale ones, except CW
                lock(_usedSpotlightIds)
                {
                    _usedSpotlightIds.Clear();
                    _lastUsedStyle = "Standard";
                }

                // Fetch Manifests from Cache
                _currentContentType = contentType;

                // --- 0. CACHE RESTORATION PHASE (Instant Swap) ---
                _heroItemsSet = false;
                _heroPrewarmDone = false;
                // [POISON GUARD] Reject empty cached states (0 catalog rows). These can arise when a prior
                // LoadDiscoveryAsync exited early because manifests were still loading from disk; the
                // finally-block UpdateDiscoveryCache fires regardless and pollutes the cache. Treating the
                // entry as a miss forces a proper reload once manifests are available.
                bool cacheHit = _contentCache.TryGetValue(contentType, out var cachedState);
                if (cacheHit && (cachedState.Rows == null || cachedState.Rows.Count(r => r.RowId != "CW") == 0))
                {
                    HeroPerfLog($"[StremioControl] CACHE HIT for {contentType} but empty ({cachedState.Rows?.Count ?? 0} rows). Treating as miss.");
                    _contentCache.Remove(contentType);
                    // Also drop the sibling slot map cache so we don't reuse the empty slot list.
                    lock (_slotMapCache) { _slotMapCache.Remove(contentType); }
                    cacheHit = false;
                }
                if (cacheHit)
                {
                    HeroPerfLog($"[StremioControl] CACHE HIT for {contentType}");
                    System.Diagnostics.Debug.WriteLine($"[StremioControl] Restoring cached state for {contentType}");
                    
                    _heroPriorityOrder = new List<string>(cachedState.HeroPriorityOrder);
                    lock (_rowItemsBuffer)
                    {
                        _rowStates = new Dictionary<string, RowState>(cachedState.RowStates);
                        _rowItemsBuffer = new Dictionary<string, ObservableCollection<StremioMediaStream>>(cachedState.RowItemsBuffer);
                    }

                    _discoveryRows.Clear();
                    // Batch addition to avoid excessive layout passes
                    HeroPerfLog($"[CACHE-RESTORE] Found {cachedState.Rows.Count} rows in memory cache");
                    foreach (var row in cachedState.Rows)
                    {
                        var rowItemCount = row.Items?.Count ?? 0;
                        HeroPerfLog($"  - Row: {row.CatalogName} (ID: {row.RowId}) | Items: {rowItemCount} | Style: {row.RowStyle}");
                        _discoveryRows.Add(row);
                    }

                    // [RESTORE COLOR] Instantly update backdrop color from cache
                    _lastHeroColors = cachedState.HeroColors;
                    if (_lastHeroColors.HasValue)
                        BackdropColorChanged?.Invoke(this, _lastHeroColors.Value);

                    // --- [EARLY PREWARM] ---
                    // Don't wait for Step 4! Start downloading high-res assets IMMEDIATELY from cache.
                    var cachedHeroItems = new List<StremioMediaStream>();
                    foreach (var rid in _heroPriorityOrder)
                    {
                        if (_rowItemsBuffer.TryGetValue(rid, out var rowItems))
                        {
                            cachedHeroItems.AddRange(rowItems);
                            if (cachedHeroItems.Count >= 5) break;
                        }
                    }

                    if (cachedHeroItems.Count > 0)
                    {
                        HeroPerfLog($"[CACHE-EAGER] Triggering immediate asset prewarm for {cachedHeroItems.Count} items");
                        HeroControl.SetItems(cachedHeroItems.Take(5).ToList(), animate: true);
                        _heroItemsSet = true; // Prevents the network-load from double-resetting the UI if identical
                    }

                    // Update Hero immediately with cached items to avoid shimmer
                    var restoreStart = overallSw.ElapsedMilliseconds;
                    UpdateHeroState(skipShimmer: true);
                    HeroPerfLog($"[CACHE] Hero state restored from cache in {overallSw.ElapsedMilliseconds - restoreStart}ms");

                    // [FIX] Refresh Continue Watching row after cache restoration to ensure it's current
                    RefreshContinueWatching();
                    HeroPerfLog($"[CACHE-RESTORE] Complete. CW Refreshed.");
                }
                else
                {
                    // No cache: Perform a clean reset and show shimmer
                    HeroPerfLog($"[StremioControl] CACHE MISS for {contentType}. Clearing UI.");
                    HeroPerfLog($"[CACHE-CLEAR] No cache found for {contentType}. Performing clean reset.");
                    _heroPriorityOrder.Clear(); 
                    _discoveryRows.Clear();
                    lock (_rowItemsBuffer)
                    {
                        _rowStates.Clear();
                        _rowItemsBuffer.Clear();
                    }
                    
                    // [LOG] Check if we call SetLoading here
                    HeroPerfLog("[StremioControl] No cache -> Setting Hero to Loading (Shimmer)");
                    HeroControl.SetLoading(true);
                }

                HeroPerfLog("Step 2: Fetch Addons/Manifests START");
                // [STARTUP RACE FIX] Block briefly (up to 1.5s) for manifests to be restored from disk the
                // first time discovery runs. Before this guard, we'd get an addon list with all-null manifests
                // and bail immediately, poisoning the cache. The ManifestsLoadedFromDiskTask resolves in ~50ms
                // on disk cache hit, so in the common case this is a near-zero wait.
                var manifestsReady = StremioAddonManager.Instance.ManifestsLoadedFromDiskTask;
                if (!manifestsReady.IsCompleted)
                {
                    await Task.WhenAny(manifestsReady, Task.Delay(1500, token)).ConfigureAwait(true);
                    HeroPerfLog($"Step 2: Awaited manifest-from-disk load ({overallSw.ElapsedMilliseconds}ms)");
                }
                var addons = StremioAddonManager.Instance.GetAddonsWithManifests();
                HeroPerfLog($"Step 2: Fetch Addons/Manifests DONE ({overallSw.ElapsedMilliseconds}ms). Found {addons.Count} addons, {addons.Count(a => a.Manifest != null)} manifests loaded.");
                System.Diagnostics.Debug.WriteLine($"[StremioControl] Starting Discovery for: '{contentType}' with {addons.Count} addons.");

                if (addons.Count == 0)
                {
                    HeroControl.SetLoading(false);
                    return;
                }

                await LoadContinueWatchingLaneAsync(contentType, myVersion, token);

                // --- 1. PRE-ALLOCATE SLOTS (To maintain visual priority) ---
                HeroPerfLog($"Step 3: Slot Mapping START ({overallSw.ElapsedMilliseconds}ms)");
                var slotMap = new List<(string BaseUrl, StremioCatalog Catalog, int SortIndex, string RowId)>();
                int globalIndex = 0;
                var newPriorityOrder = new List<string>();

                bool useCache = false;
                lock(_slotMapCache)
                {
                    if (_slotMapCache.TryGetValue(contentType, out var cached))
                    {
                        foreach(var slot in cached)
                        {
                            slotMap.Add((slot.BaseUrl, slot.Catalog, slot.SortIndex, slot.RowId));
                            newPriorityOrder.Add(slot.RowId);
                        }
                        useCache = true;
                        HeroPerfLog("Step 3: Slot Mapping CACHED");
                    }
                }

                if (!useCache)
                {
                    var seenLogicalCatalogs = new HashSet<string>();
                    foreach (var (url, manifest) in addons)
                    {
                        if (manifest?.Catalogs == null) continue;
                        
                        var relevantCatalogs = manifest.Catalogs
                            .Where(c => string.Equals(c.Type, contentType, StringComparison.OrdinalIgnoreCase))
                            .Where(c => !(c.Extra != null && c.Extra.Any(e => string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase) && e.Isrequired)))
                            .Where(c => (c.Id != null) && !c.Id.Contains("search", StringComparison.OrdinalIgnoreCase))
                            .Where(c => (c.Id != null) && !c.Id.Contains("search_movie", StringComparison.OrdinalIgnoreCase))
                            .Where(c => (c.Id != null) && !c.Id.Contains("search_series", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var cat in relevantCatalogs)
                        {
                            string idKey = $"{url}|id:{cat.Id}";
                            if (seenLogicalCatalogs.Contains(idKey)) continue;
                            seenLogicalCatalogs.Add(idKey);

                            string rowId = $"{url}|{cat.Type}|{cat.Id}|{cat.Name}";
                            newPriorityOrder.Add(rowId);
                            slotMap.Add((url, cat, globalIndex, rowId));
                            globalIndex++;
                        }
                    }

                    // Save to cache — but only if we actually computed slots. An empty slotMap means the
                    // manifests hadn't finished loading; caching the empty list would pin discovery to a dead
                    // state on the next reload (pre-cached empty slot map → slotMap.Count==0 → SetLoading(false)).
                    if (slotMap.Count > 0)
                    {
                        lock (_slotMapCache)
                        {
                            _slotMapCache[contentType] = slotMap.Select(s => new CachedSlot {
                                BaseUrl = s.BaseUrl, Catalog = s.Catalog, SortIndex = s.SortIndex, RowId = s.RowId
                            }).ToList();
                        }
                        _ = SaveLayoutCacheToDiskAsync();
                    }
                }
                
                _heroPriorityOrder = newPriorityOrder;

                // Sync UI rows: Add missing, remove stale, then sort
                DispatcherQueue.TryEnqueue(() => 
                {
                    // 1. Remove stale
                    for (int i = _discoveryRows.Count - 1; i >= 0; i--)
                    {
                        var row = _discoveryRows[i];
                        if (row.RowId != "CW" && !_heroPriorityOrder.Contains(row.RowId))
                        {
                            _discoveryRows.RemoveAt(i);
                            lock(_rowItemsBuffer) {
                                _rowStates.Remove(row.RowId);
                                _rowItemsBuffer.Remove(row.RowId);
                            }
                        }
                    }

                    // 2. Add missing as skeletons
                    foreach (var slot in slotMap)
                    {
                        if (!_discoveryRows.Any(r => r.RowId == slot.RowId))
                        {
                            var skeleton = new CatalogRowViewModel {
                                RowId = slot.RowId,
                                CatalogName = slot.Catalog.Name,
                                SortIndex = slot.SortIndex,
                                IsLoading = true,
                                Items = new ObservableCollection<StremioMediaStream>(),
                                SourceUrl = slot.BaseUrl,
                                CatalogType = slot.Catalog.Type,
                                CatalogId = slot.Catalog.Id
                            };
                            _discoveryRows.Add(skeleton);
                        }
                        else
                        {
                            // Sync sort index for existing rows
                            var row = _discoveryRows.First(r => r.RowId == slot.RowId);
                            row.SortIndex = slot.SortIndex;
                        }
                    }
                    
                    // 3. Keep ObservableCollection sorted by SortIndex
                    for (int i = 0; i < _discoveryRows.Count - 1; i++)
                    {
                        for (int j = i + 1; j < _discoveryRows.Count; j++)
                        {
                            if (_discoveryRows[i].SortIndex > _discoveryRows[j].SortIndex)
                            {
                                 _discoveryRows.Move(j, i);
                            }
                        }
                    }
                });

                if (slotMap.Count == 0)
                {
                    int pendingManifests = addons.Count(a => a.Manifest == null);
                    if (pendingManifests > 0)
                    {
                         System.Diagnostics.Debug.WriteLine($"[StremioControl] SlotMap empty but waiting for {pendingManifests} manifests.");
                         return; 
                    }

                    HeroControl.SetLoading(false);
                    return;
                }
                HeroPerfLog($"Step 3: Slot Mapping DONE ({overallSw.ElapsedMilliseconds}ms). Total slots: {slotMap.Count}");
                
                HeroPerfLog($"Step 3.5: Phase 0 Disk Load START ({overallSw.ElapsedMilliseconds}ms)");
                var phase0Tasks = slotMap.Select(slot => ProcessCatalogSlotAsync(slot, isPhase0: true, myVersion, token)).ToArray();
                await Task.WhenAll(phase0Tasks);
                if (token.IsCancellationRequested || myVersion != _discoveryVersion) return;

                HeroPerfLog($"Step 4: Network fetches START ({overallSw.ElapsedMilliseconds}ms)");
                var netTasks = slotMap.Select(slot => ProcessCatalogSlotAsync(slot, isPhase0: false, myVersion, token)).ToArray();
                await Task.WhenAll(netTasks);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[StremioControl] LoadDiscoveryAsync: {ex.Message}");
            }
            finally
            {
                if (myVersion == _discoveryVersion)
                {
                    _isDiscoveryRunning = false;
                    DispatcherQueue.TryEnqueue(() => UpdateDiscoveryCache(contentType, myVersion));
                }
            }
        }

        private void UpdateDiscoveryCache(string contentType, int completedVersion)
        {
            if (completedVersion != _discoveryVersion) return;
            if (string.IsNullOrEmpty(contentType) || contentType != _currentContentType) return;

            var state = new DiscoveryState
            {
                Rows = _discoveryRows.ToList(),
                HeroPriorityOrder = new List<string>(_heroPriorityOrder),
                HeroColors = _lastHeroColors
            };

            // [POISON GUARD] Never persist an empty (no catalog rows) state. This happens when LoadDiscoveryAsync
            // bails early because the addon manifests haven't finished loading from disk; the finally block still
            // fires UpdateDiscoveryCache and would otherwise write a dead cache entry that pins the hero in shimmer
            // forever on the next reload.
            int realRowCount = state.Rows.Count(r => r.RowId != "CW");
            if (realRowCount == 0 && _heroPriorityOrder.Count == 0)
            {
                HeroPerfLog($"[CACHE-SAVE] SKIPPED: no catalog rows yet for {contentType} (likely manifests still loading). Cache untouched.");
                return;
            }

            // [FIX] Memory Cache Persistence Guard
            // If the current list is significantly smaller than the cached one, and we are in a non-stable state, 
            // refuse to overwrite the healthy cache.
            if (_contentCache.TryGetValue(contentType, out var existing))
            {
                if (state.Rows.Count < existing.Rows.Count && (_loadCts == null || _loadCts.Token.IsCancellationRequested))
                {
                    HeroPerfLog($"[CACHE-SAVE] REJECTED: Current row count ({state.Rows.Count}) is less than cached ({existing.Rows.Count}) and task was cancelled.");
                    return;
                }
            }

            lock (_rowItemsBuffer)
            {
                state.RowStates = new Dictionary<string, RowState>(_rowStates);
                state.RowItemsBuffer = new Dictionary<string, ObservableCollection<StremioMediaStream>>(_rowItemsBuffer);
            }

            _contentCache[contentType] = state;
            HeroPerfLog($"[CACHE-SAVE] Saved {state.Rows.Count} rows to memory cache for {contentType}");
        }

        private async Task<CatalogRowViewModel> LoadCatalogRowAsync(string baseUrl, string type, StremioCatalog cat, int sortIndex)
        {
            var rowSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var items = await StremioService.Instance.GetCatalogItemsAsync(baseUrl, type, cat.Id);
                if (items == null || items.Count == 0) return null;

                string style = "Standard";
                bool hasBackgrounds = items.Count > 0 && !string.IsNullOrEmpty(items[0].Banner);

                lock(_usedSpotlightIds)
                {
                    int patternIndex = sortIndex % 3;

                    if (patternIndex == 2) // 3. Sıra: Spotlight
                    {
                        var spotlightItems = items
                            .Where(i => !_usedSpotlightIds.Contains(i.IMDbId))
                            .OrderByDescending(i => !string.IsNullOrEmpty(i.Banner))
                            .Take(5)
                            .ToList();
                        
                        if (spotlightItems.Count < 5)
                        {
                            var fillItems = items
                                .Where(i => !spotlightItems.Contains(i))
                                .OrderByDescending(i => !string.IsNullOrEmpty(i.Banner))
                                .Take(5 - spotlightItems.Count);
                            spotlightItems.AddRange(fillItems);
                        }

                        if (spotlightItems.Count > 0)
                        {
                            style = "Spotlight";
                            foreach(var item in spotlightItems) 
                            {
                                if (!string.IsNullOrEmpty(item.IMDbId)) _usedSpotlightIds.Add(item.IMDbId);
                            }

                            for (int i = 0; i < spotlightItems.Count; i++)
                            {
                                if (items.IndexOf(spotlightItems[i]) != i)
                                {
                                    items.Remove(spotlightItems[i]);
                                    items.Insert(i, spotlightItems[i]);
                                }
                            }
                        }
                        else if (hasBackgrounds)
                        {
                            // Spotlight için yeterli içerik yoksa yedek olarak Landscape yap
                            style = "Landscape";
                            System.Diagnostics.Debug.WriteLine($"[StremioStyle] Spotlight failed (no items). Fallback to Landscape.");
                        }
                        else
                        {
                            // Hiçbir şey yoksa Standard yap
                            style = "Standard";
                            System.Diagnostics.Debug.WriteLine($"[StremioStyle] Spotlight failed (no items). Fallback to Standard.");
                        }
                    }
                    else if (patternIndex == 1) // 2. Sıra: Landscape (Yatay Poster)
                    {
                        style = hasBackgrounds ? "Landscape" : "Standard";
                        if (!hasBackgrounds) System.Diagnostics.Debug.WriteLine($"[StremioStyle] Landscape failed (no background). Fallback to Standard.");
                    }
                    else // 1. Sıra: Standard (Dikey Poster)
                    {
                        style = "Standard";
                    }
                    
                    _lastUsedStyle = style; // Diğer bileşenlerin loglama/ihtiyaçları için yine tutulabilir
                }

                string finalName = cat.Name;
                if (finalName == "KEŞFET" || finalName == "Keşfet") finalName = string.Empty;

                return new CatalogRowViewModel
                {
                    CatalogName = finalName,
                    Items = new ObservableCollection<StremioMediaStream>(items),
                    RowStyle = style,
                    SourceUrl = baseUrl,
                    CatalogType = type,
                    CatalogId = cat.Id,
                    Extra = "",
                    Skip = items.Count,
                    HasMore = items.Count >= 20
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioControl] Error loading row {cat.Name} ({baseUrl}): {ex.Message}");
                return null; 
            }
        }

        private void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            ViewChanged?.Invoke(this, e);
        }

        private int _lastRowIndex = -1;
        private int _lastItemIndex = -1;
        private string _lastItemId = "";

        private void CatalogRow_ItemClicked(object sender, (IMediaStream Stream, UIElement SourceElement) e)
        {
            if (sender is CatalogRow row && DiscoveryRows.ItemsSource is ObservableCollection<CatalogRowViewModel> rows)
            {
                if (row.DataContext is CatalogRowViewModel vm)
                {
                    _lastRowIndex = rows.IndexOf(vm);
                    if (vm.Items != null)
                    {
                        _lastItemIndex = vm.Items.IndexOf((StremioMediaStream)e.Stream);
                        _lastItemId = e.Stream.IMDbId ?? e.Stream.Id.ToString();
                    }
                }
            }
            ItemClicked?.Invoke(this, (e.Stream, e.SourceElement, null));
        }

        private void CatalogRow_HoverStarted(object sender, FrameworkElement e) => CardHoverStarted?.Invoke(this, e);
        private void CatalogRow_HoverEnded(object sender, FrameworkElement e) => CardHoverEnded?.Invoke(this, e);
        private void CatalogRow_ScrollStarted(object sender, EventArgs e) => RowScrollStarted?.Invoke(this, e);
        private void CatalogRow_ScrollEnded(object sender, EventArgs e) => RowScrollEnded?.Invoke(this, e);

        private void CatalogRow_HeaderClicked(object sender, EventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CatalogRowViewModel vm)
            {
                HeaderClicked?.Invoke(this, vm);
            }
        }

        private async void CatalogRow_LoadMoreAction(object sender, EventArgs e)
        {
            if (sender is CatalogRow row && row.DataContext is CatalogRowViewModel vm)
            {
                if (vm.IsLoadingMore || !vm.HasMore) return;
                if (vm.RowId == "CW") return; 

                vm.IsLoadingMore = true;
                try
                {
                    var newItems = await StremioService.Instance.GetCatalogItemsAsync(vm.SourceUrl, vm.CatalogType, vm.CatalogId, vm.Extra, vm.Skip);
                    if (newItems != null && newItems.Count > 0)
                    {
                        foreach(var item in newItems) vm.Items.Add(item);
                        vm.Skip += newItems.Count;
                        vm.HasMore = newItems.Count > 0;
                    }
                    else vm.HasMore = false;
                }
                catch
                {
                    vm.HasMore = false;
                }
                finally
                {
                    vm.IsLoadingMore = false;
                }
            }
        }

        private void LandscapeCard_HoverStarted(object sender, EventArgs e) 
        {
            if (sender is LandscapeCard card) CardHoverStarted?.Invoke(this, card);
        }
        private void LandscapeCard_HoverEnded(object sender, EventArgs e) 
        {
            if (sender is LandscapeCard card) CardHoverEnded?.Invoke(this, card);
        }

        private void SpotlightInjectRow_ItemClicked(object sender, (IMediaStream Stream, UIElement SourceElement, Microsoft.UI.Xaml.Media.ImageSource PreloadedLogo) e)
        {
            ItemClicked?.Invoke(this, e);
        }

        private void SpotlightInjectRow_TrailerExpandRequested(object sender, (IMediaStream Stream, UIElement SourceElement) e)
        {
            TrailerExpandRequested?.Invoke(this, e);
        }

        private void PosterCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is PosterCard card && card.DataContext is IMediaStream stream)
            {
                Microsoft.UI.Xaml.ElementSoundPlayer.Play(Microsoft.UI.Xaml.ElementSoundKind.Invoke);
                // For PosterCard, we handle PreloadedLogo as null here (it will be handled via preloadedImage in the receiver if it's a poster)
                ItemClicked?.Invoke(this, (stream, card.ImageElement, null));
            }
            else if (sender is LandscapeCard lCard && lCard.DataContext is IMediaStream lStream)
            {
                Microsoft.UI.Xaml.ElementSoundPlayer.Play(Microsoft.UI.Xaml.ElementSoundKind.Invoke);
                ItemClicked?.Invoke(this, (lStream, lCard.ImageElement, null));
            }
        }

        private void PosterCard_HoverStarted(object sender, EventArgs e)
        {
            if (sender is PosterCard card) CardHoverStarted?.Invoke(this, card);
        }

        private void PosterCard_HoverEnded(object sender, EventArgs e)
        {
            if (sender is PosterCard card) CardHoverEnded?.Invoke(this, card);
        }

        public async Task ScrollToLastActiveItemAsync()
        {
            if (_lastRowIndex < 0 || _lastItemIndex < 0) return;
            if (DiscoveryRows.ItemsSource is not ObservableCollection<CatalogRowViewModel> rows) return;
            if (_lastRowIndex >= rows.Count) return;
            var targetRowVM = rows[_lastRowIndex];
            DiscoveryRows.ScrollIntoView(targetRowVM);
        }

        public UIElement? GetPosterElement(IMediaStream item)
        {
            if ((!string.IsNullOrEmpty(item.IMDbId) && item.IMDbId == _lastItemId) || item.Id.ToString() == _lastItemId)
            {
                var fastElement = GetLastClickedPosterElement();
                if (fastElement != null) return fastElement;
            }
            
            try 
            {
                foreach (var rowItem in DiscoveryRows.Items)
                {
                    var rowContainer = DiscoveryRows.ContainerFromItem(rowItem) as DependencyObject;
                    if (rowContainer == null) continue; 
                    CatalogRow catalogRow = null;
                    if (VisualTreeHelper.GetChildrenCount(rowContainer) > 0)
                        catalogRow = VisualTreeHelper.GetChild(rowContainer, 0) as CatalogRow;

                    if (catalogRow == null) continue;
                    if (catalogRow.ItemsSource is IEnumerable<IMediaStream> list)
                    {
                        var actualItem = list.FirstOrDefault(x =>
                            (!string.IsNullOrEmpty(x.IMDbId) && x.IMDbId == item.IMDbId) ||
                            x.Id == item.Id);

                        if (actualItem != null)
                        {
                            if (catalogRow.ListView != null)
                            {
                                var container = catalogRow.ListView.ContainerFromItem(actualItem) as ListViewItem;
                                if (container != null)
                                {
                                    var card = FindPosterCardInContainer(container);
                                    if (card != null) return card;
                                }
                            }
                            return null;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private UIElement? GetLastClickedPosterElement()
        {
             if (_lastRowIndex < 0 || _lastItemIndex < 0) return null;
             try
             {
                 var container = DiscoveryRows.ContainerFromIndex(_lastRowIndex) as DependencyObject;
                 if (container == null) return null;
                 var catalogRow = VisualTreeHelper.GetChildrenCount(container) > 0 ? VisualTreeHelper.GetChild(container, 0) as CatalogRow : null;
                 if (catalogRow == null || catalogRow.ListView == null) return null;
                 var itemContainer = catalogRow.ListView.ContainerFromIndex(_lastItemIndex) as ListViewItem;
                 if (itemContainer != null) return FindPosterCardInContainer(itemContainer);
             }
             catch { }
             return null;
        }

        private PosterCard? FindPosterCardInContainer(DependencyObject root)
        {
            if (root == null) return null;
            if (root is PosterCard card) return card;
            try 
            {
                int count = VisualTreeHelper.GetChildrenCount(root);
                for(int i=0; i<count; i++)
                {
                    var result = FindPosterCardInContainer(VisualTreeHelper.GetChild(root, i));
                    if (result != null) return result;
                }
            }
            catch { }
            return null;
        }

        public async Task ScrollToItemAsync(IMediaStream item)
        {
            if (DiscoveryRows.ItemsSource is not IEnumerable<CatalogRowViewModel> rows) return;
            CatalogRowViewModel targetRow = null;
            foreach (var row in rows)
            {
                if (row.Items.Any(x => (!string.IsNullOrEmpty(x.IMDbId) && x.IMDbId == item.IMDbId) || x.Id == item.Id))
                {
                    targetRow = row;
                    break;
                }
            }
            if (targetRow == null) return;
            DiscoveryRows.ScrollIntoView(targetRow);
            CatalogRow catalogRow = null;
            for (int i = 0; i < 10; i++)
            {
                var container = DiscoveryRows.ContainerFromItem(targetRow) as DependencyObject;
                if (container != null && VisualTreeHelper.GetChildrenCount(container) > 0)
                {
                    catalogRow = VisualTreeHelper.GetChild(container, 0) as CatalogRow;
                    if (catalogRow != null) break;
                }
                await Task.Delay(50);
            }
            if (catalogRow != null && catalogRow.ListView != null && catalogRow.ItemsSource is IEnumerable<IMediaStream> list)
            {
                var actualItem = list.FirstOrDefault(x => (!string.IsNullOrEmpty(x.IMDbId) && x.IMDbId == item.IMDbId) || x.Id == item.Id);
                if (actualItem != null)
                {
                    catalogRow.ListView.ScrollIntoView(actualItem);
                    for(int j=0; j<20; j++)
                    {
                        if (catalogRow.ListView.ContainerFromItem(actualItem) != null) break;
                        await Task.Delay(50);
                    }
                }
            }
        }

        public void Clear()
        {
            _discoveryRows.Clear();
            HeroControl.SetLoading(false);
            HeroControl.SetItems(null);
            _currentContentType = string.Empty;
        }
        private void UpdateHeroState(bool skipShimmer = false)
        {
            // [STABILITY] Debounce hero updates to 500ms to prevent "Update Storms" during background enrichment
            if (_heroDebounceTimer == null)
            {
                _heroDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _heroDebounceTimer.Tick += (s, e) => {
                    _heroDebounceTimer.Stop();
                    ExecuteHeroUpdate(true); // Always skip shimmer for debounced updates
                };
            }

            if (skipShimmer)
            {
                _heroDebounceTimer.Stop();
                _heroDebounceTimer.Start();
                return;
            }

            // Immediate update for "New Content" (with shimmer)
            ExecuteHeroUpdate(false);
        }

        private void ExecuteHeroUpdate(bool skipShimmer)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var firstTargetRowId = _heroPriorityOrder.FirstOrDefault();
                if (string.IsNullOrEmpty(firstTargetRowId)) return;

                if (_rowStates.TryGetValue(firstTargetRowId, out var state) && state == RowState.Pending && !skipShimmer)
                {
                    if (!_rowItemsBuffer.ContainsKey(firstTargetRowId)) {
                        if (this.Visibility == Visibility.Visible)
                            HeroControl.SetLoading(true, silent: false, resetHeroPipelineState: false);
                        return;
                    }
                }

                if (!_rowItemsBuffer.TryGetValue(firstTargetRowId, out var items) || items.Count == 0) return;

                // [FIX] Don't trigger hero transitions if we are hidden
                if (this.Visibility != Visibility.Visible) return;

                var heroItems = items.Take(5).ToList();
                var newIds = heroItems.Select(i => i.IMDbId ?? i.Id.ToString()).ToList();
                
                bool isMatch = _currentHeroIds.SequenceEqual(newIds);
                
                // [DEEP GUARD] Compare metadata if IDs match
                if (isMatch)
                {
                    // HeroControl.SetItems already has a deep guard, but we can avoid the call entirely here
                    // if we want to be extra sure. For now, we let SetItems handle the metadata comparison.
                }

                HeroPerfLog($"[HERO-DIAG] UpdateHeroState: Matches Current IDs? {isMatch}. New IDs: {string.Join(", ", newIds.Take(3))}");

                // Same IDs: nothing to do. HeroSectionControl's PropertyChanged subscriptions already react to
                // late metadata/logo/backdrop enrichment — another SetItems call would just reset state.
                if (isMatch) return;

                _currentHeroIds = newIds;
                _isFirstHeroLoad = false;
                
                // [HERO LOG] Detailed timing
                HeroPerfLog($"[HERO-READY] Row 0 processing took {sw.ElapsedMilliseconds}ms. Setting {heroItems.Count} items.");
                
                HeroControl.SetItems(heroItems, animate: !skipShimmer);
                // Do not SetLoading(false) here — races ApplyTransitionInternalAsync / shimmer; hero clears loading in ShowRealContent.
            });
        }

        private async Task LoadContinueWatchingLaneAsync(string contentType, int discoveryVersion, System.Threading.CancellationToken token)
        {
            try
            {
                await (_historyInitTask ?? Task.CompletedTask);
                if (token.IsCancellationRequested || discoveryVersion != _discoveryVersion || contentType != _currentContentType)
                {
                    HeroPerfLog($"[CW] Skipped stale lane. V={discoveryVersion}, CurrentV={_discoveryVersion}, Content={contentType}/{_currentContentType}");
                    return;
                }

                var continueWatching = HistoryManager.Instance.GetContinueWatching(contentType);
                if (continueWatching.Count == 0) return;

                var cwItems = new ObservableCollection<StremioMediaStream>();
                foreach (var hist in continueWatching)
                {
                    var stream = new StremioMediaStream(new StremioMeta
                    {
                        Id = hist.ParentSeriesId ?? hist.Id,
                        Name = hist.ParentSeriesId != null ? (hist.SeriesName ?? hist.Title) : hist.Title,
                        Poster = hist.PosterUrl,
                        Background = hist.BackdropUrl,
                        Type = hist.ParentSeriesId != null ? "series" : contentType
                    });

                    stream.IsContinueWatching = true;
                    stream.EpisodeSubtext = hist.ParentSeriesId != null && hist.SeasonNumber > 0 && hist.EpisodeNumber > 0
                        ? $"S{hist.SeasonNumber:D2}E{hist.EpisodeNumber:D2}"
                        : null;
                    stream.ProgressValue = hist.Duration > 0 ? (hist.Position / hist.Duration) * 100 : 0;
                    cwItems.Add(stream);
                }

                if (cwItems.Count == 0) return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || discoveryVersion != _discoveryVersion || contentType != _currentContentType) return;

                    var existing = _discoveryRows.FirstOrDefault(r => r.RowId == "CW");
                    if (existing == null)
                    {
                        _discoveryRows.Add(new CatalogRowViewModel
                        {
                            CatalogName = "İzlemeye Devam Et",
                            Items = cwItems,
                            IsLoading = false,
                            RowId = "CW",
                            SortIndex = -1,
                            RowStyle = "Landscape"
                        });
                    }
                    else
                    {
                        existing.Items = cwItems;
                        existing.IsLoading = false;
                    }

                    lock (_rowItemsBuffer)
                    {
                        _rowStates["CW"] = RowState.Success;
                        _rowItemsBuffer["CW"] = cwItems;
                    }
                });

                _ = EnrichItemsBatchAsync(cwItems);
                HeroPerfLog($"[CW] Injected {cwItems.Count} items (V={discoveryVersion}).");
            }
            catch (Exception ex)
            {
                HeroPerfLog($"[CW] Lane error: {ex.Message}");
            }
        }

        private async Task ProcessCatalogSlotAsync((string BaseUrl, StremioCatalog Catalog, int SortIndex, string RowId) slot, bool isPhase0, int discoveryVersion, System.Threading.CancellationToken token)
        {
            var (u, cat, sIdx, rid) = slot;
            bool isHeroRow = _heroPriorityOrder.Count > 0 && rid == _heroPriorityOrder[0];

            bool acquired = false;
            if (!isHeroRow)
            {
                await _catalogSemaphore.WaitAsync();
                acquired = true;
            }

            try
            {
                if (token.IsCancellationRequested || discoveryVersion != _discoveryVersion) return;
                string catalogUrl = $"{u.TrimEnd('/')}/catalog/{cat.Type}/{cat.Id}.json";
                
                if (isPhase0)
                {
                    var (_, items, _) = await CatalogCacheManager.LoadCatalogBinaryAsync(catalogUrl);
                    if (token.IsCancellationRequested || discoveryVersion != _discoveryVersion) return;
                    if (items != null && items.Count > 0)
                    {
                        foreach (var item in items) if (string.IsNullOrEmpty(item.SourceAddon)) item.SourceAddon = u;

                        lock (_rowItemsBuffer)
                        {
                            if (!_rowItemsBuffer.ContainsKey(rid))
                            {
                                _rowItemsBuffer[rid] = new ObservableCollection<StremioMediaStream>(items);
                                _rowStates[rid] = RowState.Success;
                                
                                // Only update Hero if this is the priority row
                                if (isHeroRow) UpdateHeroState(skipShimmer: true);
                            }
                        }

                        DispatcherQueue.TryEnqueue(() => {
                            if (token.IsCancellationRequested || discoveryVersion != _discoveryVersion) return;
                            var row = _discoveryRows.FirstOrDefault(r => r.RowId == rid);
                            if (row != null && (row.Items == null || row.Items.Count == 0))
                            {
                                row.Items = new ObservableCollection<StremioMediaStream>(items);
                                row.IsLoading = false;
                            }
                        });
                    }
                }
                else
                {
                    var root = await StremioService.Instance.GetCatalogAsync(u, cat.Type, cat.Id);
                    if (token.IsCancellationRequested || discoveryVersion != _discoveryVersion) return;
                    if (root?.Metas != null)
                    {
                        var itemsList = root.Metas.Select(m => new StremioMediaStream(m) { SourceAddon = u }).ToList();
                        var collection = new ObservableCollection<StremioMediaStream>(itemsList);

                        lock (_rowItemsBuffer)
                        {
                            _rowItemsBuffer[rid] = collection;
                            _rowStates[rid] = RowState.Success;
                        }
                        
                        // Only update Hero if this is the priority row
                        if (isHeroRow) UpdateHeroState();

                        DispatcherQueue.TryEnqueue(() => {
                            if (token.IsCancellationRequested || discoveryVersion != _discoveryVersion) return;
                            var row = _discoveryRows.FirstOrDefault(r => r.RowId == rid);
                            if (row != null)
                            {
                                row.Items = collection;
                                row.IsLoading = false;
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                HeroPerfLog($"[ROW] Slot {rid} failed (Phase0={isPhase0}, V={discoveryVersion}): {ex.Message}");
            }
            finally
            {
                if (acquired) _catalogSemaphore.Release();
            }
        }
    }
}
