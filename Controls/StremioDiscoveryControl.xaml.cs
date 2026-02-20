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

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class StremioDiscoveryControl : UserControl
    {
        // Public Events
        public event EventHandler<(IMediaStream Stream, UIElement SourceElement)> ItemClicked;
        public event EventHandler<IMediaStream> PlayAction;
        public event EventHandler<IMediaStream> DetailsAction;
        public event EventHandler<(Windows.UI.Color Primary, Windows.UI.Color Secondary)> BackdropColorChanged;
        public event EventHandler<ScrollViewerViewChangedEventArgs> ViewChanged;
        
        // Expanded Card Event Bridges
        public event EventHandler<PosterCard> CardHoverStarted;
        public event EventHandler<PosterCard> CardHoverEnded;
        public event EventHandler RowScrollStarted;
        public event EventHandler RowScrollEnded;

        // Exposed properties for Controller linkage
        public ScrollViewer MainScrollViewer => DiscoveryScrollViewer;

        private ObservableCollection<CatalogRowViewModel> _discoveryRows = new();
        private bool _isDraggingRow = false;
        private System.Threading.CancellationTokenSource? _loadCts;
        private string _currentContentType;

        // Hero Priority Logic
        private enum RowState { Pending, Success, Failed }
        private Dictionary<int, RowState> _rowStates = new();
        private Dictionary<int, ObservableCollection<StremioMediaStream>> _rowItemsBuffer = new();

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
        }

        private void OnAddonsChanged(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () => 
            {
                if (!string.IsNullOrEmpty(_currentContentType))
                {
                    System.Diagnostics.Debug.WriteLine("[StremioControl] Addons changed/loaded. Reloading discovery...");
                    _discoveryRows.Clear();
                    await LoadDiscoveryAsync(_currentContentType);
                }
            });
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
                _rowStates.Remove(-1);
                _rowItemsBuffer.Remove(-1);
            }

            // Re-inject with fresh history
            var continueWatching = HistoryManager.Instance.GetContinueWatching(_currentContentType);
            if (continueWatching.Count == 0) return;

            var cwItems = new ObservableCollection<StremioMediaStream>();
            foreach (var hist in continueWatching)
            {
                var stream = new StremioMediaStream(new StremioMeta
                {
                    Id = hist.ParentSeriesId ?? hist.Id,
                    Name = hist.ParentSeriesId != null ? (hist.SeriesName ?? hist.Title) : hist.Title,
                    Poster = hist.PosterUrl,
                    Type = hist.ParentSeriesId != null ? "series" : _currentContentType
                });
                stream.ProgressValue = hist.Duration > 0 ? (hist.Position / hist.Duration) * 100 : 0;
                cwItems.Add(stream);
            }

            var cwRow = new CatalogRowViewModel
            {
                CatalogName = "İzlemeye Devam Et",
                Items = cwItems,
                IsLoading = false,
                SortIndex = -1
            };

            // Insert at position 0
            _discoveryRows.Insert(0, cwRow);

            lock (_rowItemsBuffer)
            {
                _rowStates[-1] = RowState.Success;
                _rowItemsBuffer[-1] = cwRow.Items;
            }

            // Asynchronously enrich items that have no poster
            _ = EnrichContinueWatchingMetadataAsync(cwItems);
        }

        private async Task EnrichContinueWatchingMetadataAsync(ObservableCollection<StremioMediaStream> items)
        {
            var provider = new MetadataProvider();
            foreach (var stream in items)
            {
                if (!string.IsNullOrEmpty(stream.PosterUrl)) continue; // Already has poster

                try
                {
                    var meta = await provider.GetMetadataAsync(stream);
                    if (meta?.PosterUrl is string url && !string.IsNullOrEmpty(url))
                    {
                        // Update on UI thread so PropertyChanged triggers binding
                        DispatcherQueue.TryEnqueue(() => stream.PosterUrl = url);

                        // Also persist to history so next time it's available
                        var histId = stream.IMDbId;
                        if (!string.IsNullOrEmpty(histId))
                            HistoryManager.Instance.UpdateProgress(histId, stream.Title, "", 0, 0, posterUrl: url);
                    }
                }
                catch { /* Best effort */ }
            }
        }

        public async Task LoadDiscoveryAsync(string contentType)
        {
            try
            {
                // Cancel previous loading
                _loadCts?.Cancel();
                _loadCts = new System.Threading.CancellationTokenSource();
                var token = _loadCts.Token;

                // Ensure watch history is loaded before building the CW row
                await HistoryManager.Instance.InitializeAsync();

                // Optimization: Keep existing content visible if available
                bool hasExistingContent = _discoveryRows.Count > 0;
                
                if (!hasExistingContent)
                {
                     HeroControl.SetLoading(true);
                     // Add skeletons only if empty (REMOVED for Dynamic Sorting)
                     // for (int i = 0; i < 6; i++)
                     // {
                     //     _discoveryRows.Add(new CatalogRowViewModel { CatalogName = "Yükleniyor...", IsLoading = true });
                     // }
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
                    if (validManifests == 0)
                    {
                         // If we have 0 valid manifests, we might be in a "First Run" race condition.
                         // We will rely on OnAddonsChanged to trigger a reload when they arrive.
                         // But we should probably show a "Loading Addons..." state?
                         // Skeletons are already added above. We just return.
                         // If we return here, Skeletons stay visible?
                         // "if (addons.Count == 0)" check below handles empty LIST. 
                         // But if list has 3 items (all null manifests), we proceed to SlotMap loop.
                    }
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
                            Type = hist.ParentSeriesId != null ? "series" : contentType
                        });
                        
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
                            SortIndex = -1
                        };
                        _discoveryRows.Add(cwRow);
                        
                        lock(_rowItemsBuffer) {
                            _rowStates[-1] = RowState.Success;
                            _rowItemsBuffer[-1] = cwRow.Items;
                        }

                        // Asynchronously enrich items that have no poster
                        _ = EnrichContinueWatchingMetadataAsync(cwItems);
                    }
                }

                // --- 1. PRE-ALLOCATE SLOTS (To maintain visual priority) ---
                var slotMap = new List<(string BaseUrl, StremioCatalog Catalog, int SortIndex)>();
                int globalIndex = 0;
                
                foreach (var (url, manifest) in addons)
                {
                    if (manifest?.Catalogs == null) continue;
                    
                    var relevantCatalogs = manifest.Catalogs
                        .Where(c => string.Equals(c.Type, contentType, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var cat in relevantCatalogs)
                    {
                        // DYNAMIC SORTING: We no longer add skeletons here.
                        // We simply track the "intended" order (globalIndex).
                        slotMap.Add((url, cat, globalIndex));
                        globalIndex++;
                    }
                }

                if (slotMap.Count == 0)
                {
                    // Check if we are potentially waiting for manifests
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
                // --- 2. LAUNCH PARALLEL FETCHES ---
                // Reset Hero Logic on Load
                _rowStates.Clear();
                _rowItemsBuffer.Clear();
                
                // Initialize states
                foreach (var (_, _, sortIndex) in slotMap)
                {
                    _rowStates[sortIndex] = RowState.Pending;
                }

                // Helper to Update Hero based on Priority Chain
                void UpdateHeroState()
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        // Iterate strictly by priority index (0, 1, 2...)
                        var sortedKeys = _rowStates.Keys.OrderBy(k => k).ToList();
                        
                        foreach (var idx in sortedKeys)
                        {
                            var state = _rowStates[idx];
                            if (state == RowState.Pending)
                            {
                                // Strict Wait: Highest priority is still loading -> Show Shimmer
                                HeroControl.SetLoading(true);
                                return; 
                            }
                            
                            if (state == RowState.Success)
                            {
                                // Success: This is the winner. Set items and STOP.
                                if (_rowItemsBuffer.ContainsKey(idx))
                                {
                                    HeroControl.SetLoading(false);
                                    HeroControl.SetItems(_rowItemsBuffer[idx].Take(5));
                                }
                                return;
                            }
                            
                            // If Failed, continue loop to next priority
                        }
                        
                        // If we fall through here, everything failed?
                        HeroControl.SetLoading(false);
                    });
                }

                var tasks = new List<Task>();

                foreach (var (url, cat, sortIndex) in slotMap)
                {
                    if (token.IsCancellationRequested) break;

                    tasks.Add(Task.Run(async () => 
                    {
                        try
                        {
                            var rowResult = await LoadCatalogRowAsync(url, contentType, cat);
                            
                            if (token.IsCancellationRequested) return;

                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (token.IsCancellationRequested) return;

                                if (rowResult != null && rowResult.Items.Count > 0)
                                {
                                    rowResult.SortIndex = sortIndex;
                                    rowResult.IsLoading = false;

                                    // Store for Hero Logic
                                    lock(_rowItemsBuffer) {
                                        _rowStates[sortIndex] = RowState.Success;
                                        _rowItemsBuffer[sortIndex] = rowResult.Items;
                                    }

                                    // DYNAMIC INSERTION: Find correct index to insert
                                    // We want to insert before the first item that has a HIGHER SortIndex than ours.
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
                                    
                                    // Trigger Hero Check
                                    UpdateHeroState();
                                }
                                else
                                {
                                    // No results -> Mark as failed for Hero purposes so we skip to next
                                    lock(_rowItemsBuffer) {
                                        _rowStates[sortIndex] = RowState.Failed;
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
                                lock(_rowItemsBuffer) {
                                    _rowStates[sortIndex] = RowState.Failed;
                                }
                                UpdateHeroState();
                            });
                        }
                    }));
                }

                // --- 3. CLEANUP AFTER ALL TASKS ---
                await Task.WhenAll(tasks);
                
                DispatcherQueue.TryEnqueue(() => 
                {
                    // If absolutely nothing loaded, ensure loading state is off
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
        }

        private async Task<CatalogRowViewModel> LoadCatalogRowAsync(string baseUrl, string type, StremioCatalog cat)
        {
            try
            {
                var items = await StremioService.Instance.GetCatalogItemsAsync(baseUrl, type, cat.Id);
                if (items == null || items.Count == 0) 
                {
                    System.Diagnostics.Debug.WriteLine($"[StremioControl] No items returned for {cat.Name} ({baseUrl}). Removing row.");
                    return null;
                }

                string finalName = cat.Name;
                if (finalName == "KEŞFET" || finalName == "Keşfet") finalName = string.Empty;

                return new CatalogRowViewModel
                {
                    CatalogName = finalName,
                    Items = new ObservableCollection<StremioMediaStream>(items)
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



        // State Tracking for Backward Animation
        private int _lastRowIndex = -1;
        private int _lastItemIndex = -1;
        private string _lastItemId = "";

        // Row Interactions
        private void CatalogRow_ItemClicked(object sender, (IMediaStream Stream, UIElement SourceElement) e)
        {
            // Capture state on click
            if (sender is CatalogRow row && DiscoveryRows.ItemsSource is ObservableCollection<CatalogRowViewModel> rows)
            {
                if (row.DataContext is CatalogRowViewModel vm)
                {
                    _lastRowIndex = rows.IndexOf(vm);
                    
                    if (vm.Items != null)
                    {
                        // Use IndexOf to find position. 
                        // Note: e.Stream might be a different object instance if list refreshed, 
                        // so we might need ID check if simple IndexOf fails, but usually on click it's the exact object.
                        _lastItemIndex = vm.Items.IndexOf((StremioMediaStream)e.Stream);
                        _lastItemId = e.Stream.IMDbId ?? e.Stream.Id.ToString();
                    }
                }
            }
            
            System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] Item Clicked. Saved State -> Row: {_lastRowIndex}, Item: {_lastItemIndex}, ID: {_lastItemId}");
            ItemClicked?.Invoke(this, e);
        }

        private void CatalogRow_HoverStarted(object sender, PosterCard e) => CardHoverStarted?.Invoke(this, e);
        private void CatalogRow_HoverEnded(object sender, PosterCard e) => CardHoverEnded?.Invoke(this, e);
        private void CatalogRow_ScrollStarted(object sender, EventArgs e) => RowScrollStarted?.Invoke(this, e);
        private void CatalogRow_ScrollEnded(object sender, EventArgs e) => RowScrollEnded?.Invoke(this, e);

        public async Task ScrollToLastActiveItemAsync()
        {
            // This is now effectively replaced by ResolveBackAnimationElementAsync but we might want it for simple scrolling
            if (_lastRowIndex < 0 || _lastItemIndex < 0) return;
            if (DiscoveryRows.ItemsSource is not ObservableCollection<CatalogRowViewModel> rows) return;
            if (_lastRowIndex >= rows.Count) return;

            var targetRowVM = rows[_lastRowIndex];
            DiscoveryRows.ScrollIntoView(targetRowVM);
        }

        public UIElement? GetPosterElement(IMediaStream item)
        {
            // Try Fast Path first
            if ((!string.IsNullOrEmpty(item.IMDbId) && item.IMDbId == _lastItemId) || item.Id.ToString() == _lastItemId)
            {
                 var fastElement = GetLastClickedPosterElement();
                 if (fastElement != null) 
                 {
                     System.Diagnostics.Debug.WriteLine("[StremioDiscoveryControl] Fast Path: Found element via cached indices.");
                     return fastElement;
                 }
            }
            
            try 
            {
                System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] Looking for Poster for item: {item.Title} (IMDb: {item.IMDbId})");

                // Iterate loaded rows
                foreach (var rowItem in DiscoveryRows.Items)
                {
                   var rowContainer = DiscoveryRows.ContainerFromItem(rowItem) as DependencyObject; // Could be SelectorItem/ListViewItem
                   
                   if (rowContainer == null) continue; 

                   // Traverse to find CatalogRow (Template Root)
                   CatalogRow catalogRow = null;
                   int childCount = VisualTreeHelper.GetChildrenCount(rowContainer);
                   if (childCount > 0)
                   {
                        catalogRow = VisualTreeHelper.GetChild(rowContainer, 0) as CatalogRow;
                   }

                   if (catalogRow == null) continue;

                   // Check if this row contains the item
                   if (catalogRow.ItemsSource is IEnumerable<IMediaStream> list)
                   {
                        // Use IMDbId for robust comparison if available
                        var actualItem = list.FirstOrDefault(x =>
                            (!string.IsNullOrEmpty(x.IMDbId) && x.IMDbId == item.IMDbId) ||
                            x.Id == item.Id);

                        if (actualItem != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] Found Item in Row: {catalogRow.CatalogName}");

                            // Found the row, now find the card
                            if (catalogRow.ListView != null)
                            {
                                var container = catalogRow.ListView.ContainerFromItem(actualItem) as ListViewItem;
                                if (container != null)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] Found Container for item. Searching visual tree...");
                                    var card = FindPosterCardInContainer(container);
                                    if (card != null)
                                    {
                                         System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] Found PosterCard! Size: {card.ActualWidth}x{card.ActualHeight}");
                                         return card;
                                    }
                                    else
                                    {
                                         System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] Container found but PosterCard NOT found in visual tree.");
                                    }
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] Item is in data source but ContainerFromItem returned null (Virtualized?)");
                                }
                            }
                            return null; // Item found in this row, don't check other rows
                        }
                   }
                }
                System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] Item {item.Title} (IMDb: {item.IMDbId}) not found in any visible rows.");
            }
            catch (Exception ex) 
            {
               System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] GetPosterElement Error: {ex.Message}");
            }
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
                 if (itemContainer != null)
                 {
                      return FindPosterCardInContainer(itemContainer);
                 }
             }
             catch (Exception ex)
             {
                 System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] GetLastClickedPosterElement Error: {ex.Message}");
             }
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
                    var child = VisualTreeHelper.GetChild(root, i);
                    var result = FindPosterCardInContainer(child);
                    if (result != null) return result;
                }
            }
            catch { /* Ignore visual tree errors */ }
            
            return null;
        }

        public async Task ScrollToItemAsync(IMediaStream item)
        {
            if (DiscoveryRows.ItemsSource is not IEnumerable<CatalogRowViewModel> rows) return;

            // 1. Find the row containing the item
            CatalogRowViewModel targetRow = null;
            foreach (var row in rows)
            {
                // Use IMDbId for robust comparison
                if (row.Items.Any(x => (!string.IsNullOrEmpty(x.IMDbId) && x.IMDbId == item.IMDbId) || x.Id == item.Id))
                {
                    targetRow = row;
                    break;
                }
            }

            if (targetRow == null) 
            {
                System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] ScrollToItemAsync: Row NOT found for {item.Title}");
                return;
            }

            // 2. Scroll Outer List to the Row
            DiscoveryRows.ScrollIntoView(targetRow);
            
            // 3. Wait for Row to Realize (Virtualized)
            CatalogRow catalogRow = null;
            for (int i = 0; i < 10; i++) // Retry up to 500ms
            {
                var container = DiscoveryRows.ContainerFromItem(targetRow) as DependencyObject;
                if (container != null)
                {
                    if (VisualTreeHelper.GetChildrenCount(container) > 0)
                    {
                        catalogRow = VisualTreeHelper.GetChild(container, 0) as CatalogRow;
                        if (catalogRow != null) break;
                    }
                }
                await Task.Delay(50);
            }

            // 4. Scroll Inner List to the Item
            if (catalogRow != null && catalogRow.ListView != null && catalogRow.ItemsSource is IEnumerable<IMediaStream> list)
            {
                var actualItem = list.FirstOrDefault(x => (!string.IsNullOrEmpty(x.IMDbId) && x.IMDbId == item.IMDbId) || x.Id == item.Id);
                
                if (actualItem != null)
                {
                    catalogRow.ListView.ScrollIntoView(actualItem);
                    
                     // Wait for Inner Item to Realize
                    for(int j=0; j<20; j++) // Wait up to 1 second
                    {
                        if (catalogRow.ListView.ContainerFromItem(actualItem) != null) break;
                        await Task.Delay(50);
                    }
                }
            }
        }
    }
}
