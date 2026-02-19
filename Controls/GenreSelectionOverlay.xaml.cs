using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services.Stremio;

namespace ModernIPTVPlayer.Controls
{
    public class AddonDisplayItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Logo { get; set; }
        public StremioManifest Manifest { get; set; }
    }

    public class CatalogDisplayItem
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string FilterKey { get; set; }
        public List<string> Options { get; set; }
    }

    public sealed partial class GenreSelectionOverlay : UserControl
    {
        public event EventHandler<GenreSelectionArgs> SelectionMade;
        public event EventHandler CloseRequested;

        private ObservableCollection<AddonDisplayItem> _addons = new();
        private string _contentType = "movie"; // Default

        public GenreSelectionOverlay()
        {
            this.InitializeComponent();
            AddonListView.ItemsSource = _addons;
        }

        public void Show(string type = "movie")
        {
            _contentType = type;
            _addons.Clear();

            var addons = StremioAddonManager.Instance.GetAddonsWithManifests();
            foreach (var addon in addons)
            {
                if (addon.Manifest == null) continue;

                // Only show addons that have catalogs for this type
                if (!addon.Manifest.Catalogs.Any(c => c.Type == type)) continue;

                _addons.Add(new AddonDisplayItem
                {
                    Id = addon.BaseUrl,
                    Name = addon.Manifest.Name,
                    Logo = addon.Manifest.Logo ?? "ms-appx:///Assets/StoreLogo.png",
                    Manifest = addon.Manifest
                });
            }

            // Select first addon by default
            if (_addons.Count > 0)
            {
                AddonListView.SelectedIndex = 0;
            }

            // Prepare for animation
            RootGrid.Opacity = 0;
            RootGrid.Visibility = Visibility.Visible;
            this.Visibility = Visibility.Visible;
            
            ShowStoryboard.Begin();
        }

        private void AddonListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AddonListView.SelectedItem is AddonDisplayItem selected)
            {
                SelectedAddonTitle.Text = selected.Name;
                UpdateFilters(selected.Manifest);
            }
        }

        private void UpdateFilters(StremioManifest manifest)
        {
            var displayCatalogs = new List<CatalogDisplayItem>();

            var relevantCatalogs = manifest.Catalogs.Where(c => c.Type == _contentType);
            foreach (var cat in relevantCatalogs)
            {
                if (cat.Extra == null) continue;
                
                // Find the primary filter (usually named "genre")
                var filterExtra = cat.Extra.FirstOrDefault(e => e.Options != null && e.Options.Any());
                if (filterExtra == null) continue;

                displayCatalogs.Add(new CatalogDisplayItem
                {
                    Name = cat.Name ?? "Kategori",
                    Id = cat.Id,
                    FilterKey = filterExtra.Name,
                    Options = filterExtra.Options
                });
            }

            FilterPivot.ItemsSource = displayCatalogs;
        }

        private void FilterGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is string value && FilterPivot.SelectedItem is CatalogDisplayItem catalog && AddonListView.SelectedItem is AddonDisplayItem selected)
            {
                var args = new GenreSelectionArgs
                {
                    AddonId = selected.Id,
                    CatalogId = catalog.Id,
                    CatalogType = _contentType,
                    GenreValue = value,
                    FilterKey = catalog.FilterKey,
                    DisplayName = $"{selected.Name} ({catalog.Name}): {value}"
                };

                SelectionMade?.Invoke(this, args);
                Hide();
            }
        }

        public void Hide()
        {
            HideStoryboard.Completed += (s, e) => 
            {
                this.Visibility = Visibility.Collapsed;
                RootGrid.Visibility = Visibility.Collapsed;
            };
            HideStoryboard.Begin();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            Hide();
        }

        private void Background_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            Hide();
        }
    }
}
