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

        public StremioDiscoveryControl()
        {
            this.InitializeComponent();
            
            // Hero Events
            HeroControl.PlayAction += (s, e) => PlayAction?.Invoke(this, e);
            HeroControl.DetailsAction += (s, e) => DetailsAction?.Invoke(this, e);
            HeroControl.ColorExtracted += (s, c) => BackdropColorChanged?.Invoke(this, c);

            DiscoveryRows.ItemsSource = _discoveryRows;
        }

        public bool HasContent => _discoveryRows.Count > 0 && !_discoveryRows.Any(r => r.IsLoading && r.CatalogName == "Yükleniyor...");

        public async Task LoadDiscoveryAsync(string contentType)
        {
            try
            {
                // Cancel previous loading
                _loadCts?.Cancel();
                _loadCts = new System.Threading.CancellationTokenSource();
                var token = _loadCts.Token;

                // Optimization: Keep existing content visible if available
                bool hasExistingContent = _discoveryRows.Count > 0;
                
                if (!hasExistingContent)
                {
                     HeroControl.SetLoading(true);
                     // Add skeletons only if empty
                     for (int i = 0; i < 6; i++)
                     {
                         _discoveryRows.Add(new CatalogRowViewModel { CatalogName = "Yükleniyor...", IsLoading = true });
                     }
                }
                
                // Fetch Manifests
                var addonUrls = StremioAddonManager.Instance.GetAddons();
                bool firstHeroSet = false;
                int activeSkeletonIndex = 0;
                
                // Logic to handle clearing old content on first arrival of new content
                bool isFirstLoadForThisCall = true;

                foreach (var url in addonUrls)
                {
                    _ = Task.Run(async () =>
                    {
                        if (token.IsCancellationRequested) return;

                        try
                        {
                            var manifest = await StremioService.Instance.GetManifestAsync(url);
                            if (manifest?.Catalogs == null || token.IsCancellationRequested) return;

                            foreach (var cat in manifest.Catalogs.Where(c => c.Type == contentType))
                            {
                                if (token.IsCancellationRequested) return;

                                var row = await LoadCatalogRowAsync(url, contentType, cat);
                                if (row != null && row.Items.Count > 0)
                                {
                                    if (token.IsCancellationRequested) return;

                                    DispatcherQueue.TryEnqueue(() =>
                                    {
                                        if (token.IsCancellationRequested) return;

                                        if (isFirstLoadForThisCall && hasExistingContent)
                                        {
                                            _discoveryRows.Clear();
                                            activeSkeletonIndex = 0;
                                            hasExistingContent = false;
                                        }

                                        // Replace skeleton if any
                                        if (activeSkeletonIndex < _discoveryRows.Count && _discoveryRows[activeSkeletonIndex].IsLoading)
                                        {
                                            _discoveryRows[activeSkeletonIndex].CatalogName = row.CatalogName;
                                            _discoveryRows[activeSkeletonIndex].Items = row.Items;
                                            _discoveryRows[activeSkeletonIndex].IsLoading = false;
                                            activeSkeletonIndex++;
                                        }
                                        else
                                        {
                                            _discoveryRows.Add(row);
                                        }
                                        
                                        // Update Hero if first
                                        if (!firstHeroSet && row.Items.Count > 0)
                                        {
                                            firstHeroSet = true;
                                            HeroControl.SetLoading(false); // Ensure shimmer is gone
                                            HeroControl.SetItems(row.Items.Take(5));
                                        }
                                        
                                        isFirstLoadForThisCall = false;
                                    });
                                }
                            }
                        }
                        catch { }
                    });
                }
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
                if (items == null || items.Count == 0) return null;

                string finalName = cat.Name;
                if (finalName == "KEŞFET" || finalName == "Keşfet") finalName = string.Empty;

                return new CatalogRowViewModel
                {
                    CatalogName = finalName,
                    Items = new ObservableCollection<StremioMediaStream>(items)
                };
            }
            catch { return null; }
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

        public async Task<bool> TryStartBackAnimationAsync(ConnectedAnimation anim, IMediaStream item)
        {
            if (anim == null || DiscoveryRows == null || DiscoveryRows.ItemsSource is not ObservableCollection<CatalogRowViewModel> rows) return false;

            // 1. Find Row Index
            int rowIndex = -1;
            string targetId = item.IMDbId ?? item.Id.ToString();
            if (targetId == _lastItemId) rowIndex = _lastRowIndex;
            else
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Items != null && rows[i].Items.Any(x => (x.IMDbId ?? x.Id.ToString()) == targetId))
                    {
                        rowIndex = i;
                        break;
                    }
                }
            }
            if (rowIndex < 0) return false;

            System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] Starting Back Animation for: {item.Title} (Row: {rowIndex})");

            // 2. Scroll Row into view
            var rowVM = rows[rowIndex];
            DiscoveryRows.ScrollIntoView(rowVM);

            // 3. Resolve Row Control (Simple Wait)
            CatalogRow? catalogRow = null;
            for (int i = 0; i < 30; i++)
            {
                if (DiscoveryRows.ContainerFromIndex(rowIndex) is ListViewItem lvi && VisualTreeHelper.GetChildrenCount(lvi) > 0)
                {
                    catalogRow = VisualTreeHelper.GetChild(lvi, 0) as CatalogRow;
                    if (catalogRow != null && catalogRow.ListView != null) break;
                }
                await Task.Delay(16); // ~1 frame delay
            }

            if (catalogRow == null || catalogRow.ListView == null) return false;

            // 4. Find the actual item instance in the row's list
            var items = (catalogRow.ItemsSource as System.Collections.IEnumerable)?.Cast<IMediaStream>();
            var actualItem = items?.FirstOrDefault(x => (x.IMDbId ?? x.Id.ToString()) == targetId);
            if (actualItem == null) return false;

            // 5. Manual Resolution: Find the Container and target the Inner Image
            var container = catalogRow.ListView.ContainerFromItem(actualItem) as ListViewItem;
            if (container == null)
            {
                // Wait a bit more if container is still being realized
                await Task.Delay(50);
                container = catalogRow.ListView.ContainerFromItem(actualItem) as ListViewItem;
            }

            if (container != null)
            {
                // Find the PosterCard in the container
                var posterCard = container.ContentTemplateRoot as PosterCard;
                if (posterCard != null)
                {
                    var target = posterCard.ImageElement;
                    if (target != null)
                    {
                        // Ensure layout and translation are ready
                        target.UpdateLayout();
                        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(target, true);

                        bool success = anim.TryStart(target);
                        System.Diagnostics.Debug.WriteLine($"[StremioDiscoveryControl] Manual TryStart Back Animation: {success}");
                        return success;
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine("[StremioDiscoveryControl] FAILED to resolve target element for back animation.");
            return false;
        }

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
