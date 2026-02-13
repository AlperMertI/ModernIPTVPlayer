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

        public StremioDiscoveryControl()
        {
            this.InitializeComponent();
            
            // Hero Events
            HeroControl.PlayAction += (s, e) => PlayAction?.Invoke(this, e);
            HeroControl.DetailsAction += (s, e) => DetailsAction?.Invoke(this, e);
            HeroControl.ColorExtracted += (s, c) => BackdropColorChanged?.Invoke(this, c);

            DiscoveryRows.ItemsSource = _discoveryRows;
        }

        public async Task LoadDiscoveryAsync(string contentType)
        {
            try
            {
                // Reset state
                _discoveryRows.Clear();
                
                // Add skeletons
                for (int i = 0; i < 6; i++)
                {
                    _discoveryRows.Add(new CatalogRowViewModel { CatalogName = "Yükleniyor...", IsLoading = true });
                }

                // Fetch Manifests
                var addonUrls = StremioAddonManager.Instance.GetAddons();
                bool firstHeroSet = false;
                int activeSkeletonIndex = 0;

                foreach (var url in addonUrls)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var manifest = await StremioService.Instance.GetManifestAsync(url);
                            if (manifest?.Catalogs == null) return;

                            foreach (var cat in manifest.Catalogs.Where(c => c.Type == contentType))
                            {
                                var row = await LoadCatalogRowAsync(url, contentType, cat);
                                if (row != null && row.Items.Count > 0)
                                {
                                    DispatcherQueue.TryEnqueue(() =>
                                    {
                                        // Replace skeleton
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
                                            HeroControl.SetItems(row.Items.Take(5));
                                        }
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
