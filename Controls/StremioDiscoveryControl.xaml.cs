using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using Windows.Foundation;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
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
        public event EventHandler<IMediaStream> DetailsAction;
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

        // Hero Priority Logic
        private enum RowState { Pending, Success, Failed }
        private Dictionary<string, RowState> _rowStates = new();
        private Dictionary<string, ObservableCollection<StremioMediaStream>> _rowItemsBuffer = new();
        private List<string> _heroPriorityOrder = new();

        // Track used Spotlight IDs and last style to prevent duplicates/clutter
        private readonly HashSet<string> _usedSpotlightIds = new();
        private string _lastUsedStyle = "Standard";

        public StremioDiscoveryControl()
        {
            this.InitializeComponent();
            
            // Hero Events
            HeroControl.PlayAction += (s, e) => PlayAction?.Invoke(this, e);
            HeroControl.DetailsAction += (s, e) => DetailsAction?.Invoke(this, e);
            HeroControl.ColorExtracted += (s, c) => BackdropColorChanged?.Invoke(this, c);

            DiscoveryRows.ItemsSource = _discoveryRows;

            // Listen for addon changes to invalidate local state
            StremioAddonManager.Instance.AddonsChanged += OnAddonsChanged;

            this.Unloaded += StremioDiscoveryControl_Unloaded;
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
            // [CRITICAL] Unsubscribe from static event to prevent leak
            StremioAddonManager.Instance.AddonsChanged -= OnAddonsChanged;

            var sv = MainScrollViewer;
            if (sv != null)
            {
                sv.ViewChanged -= ScrollViewer_ViewChanged;
            }
        }

        private void OnAddonsChanged(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () => 
            {
                if (!string.IsNullOrEmpty(_currentContentType) && !_isDiscoveryRunning)
                {
                    System.Diagnostics.Debug.WriteLine("[StremioControl] Addons changed/loaded. Reloading discovery incrementally...");
                    await LoadDiscoveryAsync(_currentContentType);
                }
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

                var stream = new StremioMediaStream(new StremioMeta
                {
                    Id = hist.ParentSeriesId ?? hist.Id,
                    Name = hist.ParentSeriesId != null ? (hist.SeriesName ?? hist.Title) : hist.Title,
                    Poster = hist.PosterUrl,
                    Background = backdrop,
                    Type = hist.ParentSeriesId != null ? "series" : _currentContentType,
                    Description = hist.ParentSeriesId != null && hist.SeasonNumber > 0 && hist.EpisodeNumber > 0 
                        ? $"S{hist.SeasonNumber:D2}E{hist.EpisodeNumber:D2}" 
                        : null
                });
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

            // Only update if the new metadata is actually richer 
            if (!string.IsNullOrEmpty(meta.PosterUrl)) stream.PosterUrl = meta.PosterUrl;
            if (!string.IsNullOrEmpty(meta.BackdropUrl)) stream.UpdateBackground(meta.BackdropUrl);
            if (!string.IsNullOrEmpty(meta.LogoUrl)) stream.LogoUrl = meta.LogoUrl;

            // Update StremioMeta fields directly for UI bindings to pick up
            if (stream.Meta != null)
            {
                if (!string.IsNullOrEmpty(meta.Title)) stream.Meta.Name = meta.Title;
                if (!string.IsNullOrEmpty(meta.Year)) stream.Meta.ReleaseInfo = meta.Year;
                if (!string.IsNullOrEmpty(meta.Genres))
                {
                    stream.Meta.Genres = meta.Genres.Split(", ").ToList();
                }
                if (!string.IsNullOrEmpty(meta.Overview)) stream.Meta.Description = meta.Overview;
                if (meta.Rating > 0) stream.Meta.ImdbRating = meta.Rating.ToString("F1");
            }

            // Trigger property changes for the stream wrapper
            stream.OnPropertyChanged(nameof(stream.Year));
            stream.OnPropertyChanged(nameof(stream.Genres));
            stream.OnPropertyChanged(nameof(stream.Title));
            stream.OnPropertyChanged(nameof(stream.Description));
            stream.OnPropertyChanged(nameof(stream.Rating));
            stream.OnPropertyChanged(nameof(stream.LogoUrl));
        }

        private async Task EnrichItemsBatchAsync(IEnumerable<StremioMediaStream> items, MetadataContext context = MetadataContext.Discovery)
        {
            if (items == null || !items.Any()) return;
            
            var results = await MetadataProvider.Instance.EnrichItemsAsync(items, context);
            if (results.Count > 0)
            {
                var tcs = new TaskCompletionSource<bool>();
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        // [FIX] Guard against late UI updates after source switch
                        if (!this.IsLoaded || (_loadCts != null && _loadCts.Token.IsCancellationRequested))
                        {
                            tcs.SetResult(false);
                            return;
                        }

                        foreach (var pair in results)
                        {
                            ApplyMetadataToStream(pair.Key, pair.Value);
                        }
                        tcs.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
                await tcs.Task;
            }
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
                string.IsNullOrEmpty(stream.Description) ||
                string.IsNullOrEmpty(stream.Rating) ||
                stream.Rating == "0.0" || stream.Rating == "0").ToList();

            if (toEnrich.Count > 0)
            {
                await EnrichItemsBatchAsync(toEnrich, context);
            }
        }


        public async Task LoadDiscoveryAsync(string contentType)
        {
            // Increment version to invalidate any in-flight stale calls
            int myVersion = ++_discoveryVersion;

            if (_isDiscoveryRunning && contentType == _currentContentType) return;
            
            try
            {
                _isDiscoveryRunning = true;
                _rowCount = 0; // Reset counter for consistent row styles (Spotlight, etc.)

                // Cancel previous loading
                _loadCts?.Cancel();
                _loadCts = new System.Threading.CancellationTokenSource();
                var token = _loadCts.Token;

                // Ensure watch history is loaded before building the CW row
                await HistoryManager.Instance.InitializeAsync();

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
                var addons = StremioAddonManager.Instance.GetAddonsWithManifests();
                System.Diagnostics.Debug.WriteLine($"[StremioControl] Starting Discovery for: '{contentType}' with {addons.Count} addons.");

                // Log invalid manifests
                int validManifests = addons.Count(a => a.Manifest != null);
                if (validManifests < addons.Count)
                {
                    System.Diagnostics.Debug.WriteLine($"[StremioControl] WARNING: Only {validManifests}/{addons.Count} manifests are loaded. Waiting for background refresh...");
                }

                if (addons.Count == 0)
                {
                    HeroControl.SetLoading(false);
                    _discoveryRows.Clear();
                    return;
                }

                // --- 0. INJECT CONTINUE WATCHING ROW ---
                var continueWatching = HistoryManager.Instance.GetContinueWatching(contentType);
                if (continueWatching.Count > 0)
                {
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
                    
                    if (cwItems.Count > 0)
                    {
                        var cwRow = new CatalogRowViewModel 
                        {
                            CatalogName = "İzlemeye Devam Et",
                            Items = cwItems,
                            IsLoading = false,
                            RowId = "CW",
                            SortIndex = -1,
                            RowStyle = "Landscape" // explicitly set Landscape
                        };
                        
                        if (!_discoveryRows.Any(r => r.RowId == "CW"))
                        {
                            _discoveryRows.Add(cwRow);
                        }
                        
                        lock(_rowItemsBuffer) {
                            _rowStates["CW"] = RowState.Success;
                            _rowItemsBuffer["CW"] = cwItems;
                        }

                        // Asynchronously enrich items that have no poster
                        _ = EnrichItemsBatchAsync(cwItems);
                    }
                }

                // --- 1. PRE-ALLOCATE SLOTS (To maintain visual priority) ---
                var slotMap = new List<(string BaseUrl, StremioCatalog Catalog, int SortIndex, string RowId)>();
                int globalIndex = 0;
                var newPriorityOrder = new List<string>();
                var seenLogicalCatalogs = new HashSet<string>();

                foreach (var (url, manifest) in addons)
                {
                    if (manifest?.Catalogs == null) continue;
                    
                    var relevantCatalogs = manifest.Catalogs
                        .Where(c => string.Equals(c.Type, contentType, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var cat in relevantCatalogs)
                    {
                        System.Diagnostics.Debug.WriteLine($"[StremioControl] Found catalog in manifest: '{cat.Name}' (ID: {cat.Id}) Type: {cat.Type} from {url}");
                        
                        string idKey = $"{url}|id:{cat.Id}";
                        string nameKey = $"{url}|name:{cat.Name?.Trim().ToLowerInvariant()}";
                        if (seenLogicalCatalogs.Contains(idKey) || seenLogicalCatalogs.Contains(nameKey)) 
                        {
                            System.Diagnostics.Debug.WriteLine($"[StremioControl] Skipping duplicate catalog '{cat.Name}' (ID: {cat.Id}) from same addon {url}");
                            continue;
                        }
                        seenLogicalCatalogs.Add(idKey);
                        seenLogicalCatalogs.Add(nameKey);

                        string rowId = $"{url}|{cat.Id}|{cat.Name}";
                        newPriorityOrder.Add(rowId);
                        
                        var existingRow = _discoveryRows.FirstOrDefault(r => r.RowId == rowId);
                        if (existingRow != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[StremioControl] Row already exists for {rowId}. Updating sort index to {globalIndex}.");
                            existingRow.SortIndex = globalIndex;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[StremioControl] Adding new slot to slotMap: {rowId} at index {globalIndex}");
                            slotMap.Add((url, cat, globalIndex, rowId));
                        }
                        globalIndex++;
                    }
                }
                
                _heroPriorityOrder = newPriorityOrder;

                // Sync UI rows by removing stale ones, except CW
                DispatcherQueue.TryEnqueue(() => 
                {
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
                    
                    // Keep ObservableCollection sorted by SortIndex
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

                // --- 2. LAUNCH PARALLEL FETCHES ---
                foreach (var (_, _, _, rowId) in slotMap)
                {
                    _rowStates[rowId] = RowState.Pending;
                }

                void UpdateHeroState()
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var orderToCheck = new List<string>();
                        orderToCheck.AddRange(_heroPriorityOrder);
                        
                        foreach (var rowId in orderToCheck)
                        {
                            if (!_rowStates.ContainsKey(rowId)) continue;
                            var state = _rowStates[rowId];
                            if (state == RowState.Pending)
                            {
                                HeroControl.SetLoading(true);
                                return; 
                            }
                            
                            if (state == RowState.Success)
                            {
                                var aggregatedHeroItems = new List<StremioMediaStream>();
                                foreach (var rid in orderToCheck)
                                {
                                    if (_rowStates.TryGetValue(rid, out var rowState) && rowState == RowState.Success)
                                    {
                                        if (_rowItemsBuffer.TryGetValue(rid, out var rowItems))
                                        {
                                            aggregatedHeroItems.AddRange(rowItems);
                                            if (aggregatedHeroItems.Count >= 5) break;
                                        }
                                    }
                                }
                                
                                var heroItemsFinal = aggregatedHeroItems.Take(5).ToList();
                                _ = Task.Run(async () => 
                                {
                                     // Await enrichment for Hero items before display to prevent flicker
                                     await EnrichItemsBatchAsync(heroItemsFinal, MetadataContext.Spotlight);
                                    
                                    DispatcherQueue.TryEnqueue(() => 
                                    {
                                        HeroControl.SetLoading(false);
                                        HeroControl.SetItems(heroItemsFinal);
                                    });
                                });
                                return;
                            }
                        }
                        HeroControl.SetLoading(false);
                    });
                }

                var tasks = new List<Task>();
                foreach (var (url, cat, sortIndex, rowId) in slotMap)
                {
                    if (token.IsCancellationRequested) break;

                    tasks.Add(Task.Run(async () => 
                    {
                        try
                        {
                            var rowResult = await LoadCatalogRowAsync(url, contentType, cat, sortIndex);
                            
                            if (token.IsCancellationRequested) return;

                            // [NEW] Better enrichment flow for Landscape/Spotlight rows
                            // Enrich BEFORE Enqueue to UI thread
                            if (rowResult != null && rowResult.Items.Count > 0)
                            {
                                if (rowResult.RowStyle == "Spotlight")
                                {
                                    // AWAIT enrichment for Spotlight rows so they appear fully populated
                                    await EnrichRowMetadataAsync(rowResult, MetadataContext.Spotlight);
                                }
                                else if (rowResult.RowStyle == "Landscape")
                                {
                                    // Background enrichment for Landscape to maintain scroll performance
                                    _ = EnrichRowMetadataAsync(rowResult, MetadataContext.Discovery);
                                }
                            }

                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (token.IsCancellationRequested || myVersion != _discoveryVersion || !this.IsLoaded) return;

                                if (rowResult != null && rowResult.Items.Count > 0)
                                {
                                    rowResult.SortIndex = sortIndex;
                                    rowResult.RowId = rowId;
                                    rowResult.IsLoading = false;

                                    lock(_rowItemsBuffer) {
                                        _rowStates[rowId] = RowState.Success;
                                        _rowItemsBuffer[rowId] = rowResult.Items;
                                    }

                                    if (_discoveryRows.Any(r => r.RowId == rowId)) return;

                                    int insertAt = _discoveryRows.Count;
                                    for(int i=0; i<_discoveryRows.Count; i++)
                                    {
                                        if (_discoveryRows[i].SortIndex > sortIndex)
                                        {
                                            insertAt = i;
                                            break;
                                        }
                                    }
                                    _discoveryRows.Insert(insertAt, rowResult);
                                    
                                    UpdateHeroState();
                                }
                                else
                                {
                                    lock(_rowItemsBuffer) {
                                        _rowStates[rowId] = RowState.Failed;
                                    }
                                    UpdateHeroState();
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Stremio] Error loading row {cat.Name}: {ex.Message}");
                             DispatcherQueue.TryEnqueue(() => 
                            {
                                if (token.IsCancellationRequested || myVersion != _discoveryVersion || !this.IsLoaded) return;
                                lock(_rowItemsBuffer) {
                                    _rowStates[rowId] = RowState.Failed;
                                }
                                UpdateHeroState();
                            });
                        }
                    }));
                }
 
                await Task.WhenAll(tasks);
                
                DispatcherQueue.TryEnqueue(() => 
                {
                    if (_discoveryRows.Count == 0)
                    {
                        HeroControl.SetLoading(false);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StremioControl] Error: {ex.Message}");
            }
            finally
            {
                _isDiscoveryRunning = false;
            }
        }

        private async Task<CatalogRowViewModel> LoadCatalogRowAsync(string baseUrl, string type, StremioCatalog cat, int sortIndex)
        {
            try
            {
                var items = await StremioService.Instance.GetCatalogItemsAsync(baseUrl, type, cat.Id);
                if (items == null || items.Count == 0) return null;

                string style = "Standard";
                bool hasBackgrounds = items.Count > 0 && !string.IsNullOrEmpty(items[0].Banner);

                lock(_usedSpotlightIds)
                {
                    bool isSpotlightCandidate = (sortIndex + 1) % 3 == 0;

                    if (isSpotlightCandidate) 
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
                            _lastUsedStyle = "Spotlight";

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
                            style = "Landscape";
                            _lastUsedStyle = "Landscape";
                        }
                        else
                        {
                            style = "Standard";
                            _lastUsedStyle = "Standard";
                        }
                    }
                    else if (sortIndex % 2 == 0 && hasBackgrounds && _lastUsedStyle == "Standard")
                    {
                        style = "Landscape";
                        _lastUsedStyle = "Landscape";
                    }
                    else
                    {
                        style = "Standard";
                        _lastUsedStyle = "Standard";
                    }
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
    }
}
