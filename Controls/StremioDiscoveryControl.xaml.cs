using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
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
        public event EventHandler<IMediaStream> ItemClicked;
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

        // Row Interactions
        private void CatalogRow_ItemClicked(object sender, IMediaStream e)
        {
            ItemClicked?.Invoke(this, e);
        }

        private void CatalogRow_HoverStarted(object sender, PosterCard card)
        {
            if (_isDraggingRow) return;
            CardHoverStarted?.Invoke(this, card);
        }

        private void CatalogRow_HoverEnded(object sender, PosterCard card)
        {
            CardHoverEnded?.Invoke(this, card);
        }

        private void CatalogRow_ScrollStarted(object sender, object e)
        {
            _isDraggingRow = true;
            RowScrollStarted?.Invoke(this, EventArgs.Empty);
        }

        private void CatalogRow_ScrollEnded(object sender, object e)
        {
            _isDraggingRow = false;
            RowScrollEnded?.Invoke(this, EventArgs.Empty);
        }
    }
}
