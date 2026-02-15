using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using ModernIPTVPlayer.Controls;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ModernIPTVPlayer
{
    public sealed partial class WatchlistPage : Page
    {
        public WatchlistPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            
            if (e.NavigationMode == NavigationMode.Back)
            {
                // Start return animation if available
                var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("BackConnectedAnimation");
                if (anim != null)
                {
                    // For Watchlist, we check both grids
                    if (ContinueWatchingGrid.ActiveExpandedCard != null && ContinueWatchingGrid.ActiveExpandedCard.Visibility == Visibility.Visible)
                    {
                        anim.TryStart(ContinueWatchingGrid.ActiveExpandedCard.BannerImage);
                    }
                    else if (WatchlistGrid.ActiveExpandedCard != null && WatchlistGrid.ActiveExpandedCard.Visibility == Visibility.Visible)
                    {
                        anim.TryStart(WatchlistGrid.ActiveExpandedCard.BannerImage);
                    }
                }
                return; // SKIP reload
            }
            await LoadWatchlistAsync();

            // Subscribe to external changes
            Services.WatchlistManager.Instance.WatchlistChanged += WatchlistManager_WatchlistChanged;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            Services.WatchlistManager.Instance.WatchlistChanged -= WatchlistManager_WatchlistChanged;
        }

        private void WatchlistManager_WatchlistChanged(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () => await LoadWatchlistAsync());
        }

        private List<IMediaStream> _allWatchlistItems = new();

        private async Task LoadWatchlistAsync()
        {
            WatchlistGrid.IsLoading = true;
            ContinueWatchingGrid.IsLoading = true;

            await Services.WatchlistManager.Instance.InitializeAsync();
            await HistoryManager.Instance.InitializeAsync();

            var items = Services.WatchlistManager.Instance.GetWatchlist();
            
            // Sync Progress
            foreach (var item in items)
            {
                var progress = HistoryManager.Instance.GetProgress(item.Id);
                if (progress != null && progress.Duration > 0)
                {
                    item.ProgressValue = (progress.Position / progress.Duration) * 100;
                    
                    // If it's a series, maybe check for new episodes? (Placeholder logic for now)
                    if (item.Type == "series")
                    {
                        // Placeholder: Any series in watchlist gets an EP badge to show it works
                        item.BadgeText = "EP"; 
                    }
                }
            }

            _allWatchlistItems = items.Cast<IMediaStream>().ToList();
            ApplyFiltersAndSorting();
        }

        private string _currentSortTag = "recent";

        private void ApplyFiltersAndSorting()
        {
            if (_allWatchlistItems == null || 
                FilterMovies == null || FilterSeries == null || SortButton == null || 
                WatchlistGrid == null || EmptyStatePanel == null || WatchlistStatsText == null ||
                ContinueWatchingGrid == null || ContinueWatchingSection == null || MainSectionTitle == null) return;

            var filtered = _allWatchlistItems.AsEnumerable();

            // 1. Search
            string searchText = WatchlistSearchBox?.Text?.Trim()?.ToLower();
            if (!string.IsNullOrEmpty(searchText))
            {
                filtered = filtered.Where(x => x.Title.ToLower().Contains(searchText));
            }

            // 1. Filter
            string filterTag = "all";
            if (FilterMovies.IsChecked == true) filterTag = "movie";
            else if (FilterSeries.IsChecked == true) filterTag = "series";

            if (filterTag != "all")
            {
                filtered = filtered.Where(x => (x as WatchlistItem)?.Type == filterTag);
            }

            // 2. Sort
            switch (_currentSortTag)
            {
                case "az":
                    filtered = filtered.OrderBy(x => x.Title);
                    break;
                case "rating":
                    filtered = filtered.OrderByDescending(x => (x as WatchlistItem)?.Rating ?? 0);
                    break;
                case "recent":
                default:
                    filtered = filtered.OrderByDescending(x => (x as WatchlistItem)?.DateAdded ?? DateTime.MinValue);
                    break;
            }

            var finalItems = filtered.ToList();
            
            // 3. Stats
            int movieCount = finalItems.Count(x => (x as WatchlistItem)?.Type == "movie");
            int seriesCount = finalItems.Count(x => (x as WatchlistItem)?.Type == "series");
            WatchlistStatsText.Text = $"{movieCount} Film • {seriesCount} Dizi";

            if (finalItems.Any())
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;
                SectionsPanel.Visibility = Visibility.Visible;

                // 4. Sectioning
                bool isSearching = !string.IsNullOrEmpty(searchText);
                if (isSearching)
                {
                    // Flat list for search results
                    ContinueWatchingSection.Visibility = Visibility.Collapsed;
                    MainSectionTitle.Text = "Arama Sonuçları";
                    WatchlistGrid.ItemsSource = finalItems;
                    WatchlistGrid.Visibility = Visibility.Visible;
                }
                else
                {
                    // Split into Continue Watching and Watch Later
                    var continueWatching = finalItems.Where(x => (x as WatchlistItem)?.ProgressValue > 0.5).ToList();
                    var watchLater = finalItems.Where(x => (x as WatchlistItem)?.ProgressValue <= 0.5).ToList();

                    if (continueWatching.Any())
                    {
                        ContinueWatchingSection.Visibility = Visibility.Visible;
                        ContinueWatchingGrid.ItemsSource = continueWatching;
                        ContinueWatchingGrid.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        ContinueWatchingSection.Visibility = Visibility.Collapsed;
                    }

                    if (watchLater.Any())
                    {
                        MainSectionTitle.Text = "Plandakiler";
                        WatchlistGrid.ItemsSource = watchLater;
                        WatchlistGrid.Visibility = Visibility.Visible;
                        MainWatchlistSection.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        MainWatchlistSection.Visibility = continueWatching.Any() ? Visibility.Collapsed : Visibility.Visible;
                        MainSectionTitle.Text = "İzleme Listesi";
                    }
                }
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                SectionsPanel.Visibility = Visibility.Collapsed;
                // Load Recommendations if list is empty, passing the current filter
                _ = LoadRecommendationsAsync(filterTag);
            }
            
            WatchlistGrid.IsLoading = false;
            ContinueWatchingGrid.IsLoading = false;
        }

        private void WatchlistSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            ApplyFiltersAndSorting();
        }

        private void SearchTriggerButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle animation based on current width state in a more robust way
            if (WatchlistSearchBox.Width == 0 || double.IsNaN(WatchlistSearchBox.Width))
            {
                ExpandSearchAnim.Begin();
                WatchlistSearchBox.Focus(FocusState.Programmatic);
            }
            else
            {
                // Only collapse on button click if there's no text, 
                // allowing it to act as a "toggle back" if user changes their mind
                if (string.IsNullOrEmpty(WatchlistSearchBox.Text))
                {
                    CollapseSearchAnim.Begin();
                }
            }
        }

        private void WatchlistSearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(WatchlistSearchBox.Text))
            {
                CollapseSearchAnim.Begin();
            }
        }

        private async Task LoadRecommendationsAsync(string type = "all")
        {
            try
            {
                // Clear existing if any
                if (RecommendationsGrid != null) RecommendationsGrid.ItemsSource = null;

                // Determine content type for recommendations
                // If "all" is selected but the list is empty, we default to movies or mix.
                // If "series" is selected, we must fetch tv/series.
                string stremioType = type == "series" ? "series" : "movie";
                
                var trending = await Services.Stremio.StremioService.Instance.GetCatalogItemsAsync("https://v3-cinemeta.strem.io", stremioType, "top");
                if (trending.Any() && RecommendationsGrid != null)
                {
                    RecommendationsGrid.ItemsSource = trending.Take(12).Cast<IMediaStream>().ToList();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WatchlistPage] Recommendation Error: {ex.Message}");
            }
        }

        private void Filter_Checked(object sender, RoutedEventArgs e)
        {
            ApplyFiltersAndSorting();
        }

        private void SortItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string tag)
            {
                _currentSortTag = tag;
                SortButtonText.Text = item.Text;
                ApplyFiltersAndSorting();
            }
        }

        private void WatchlistGrid_ColorExtracted(object sender, (Windows.UI.Color Primary, Windows.UI.Color Secondary) e)
        {
            BackdropControl.TransitionTo(e.Primary, e.Secondary);
        }

        private void WatchlistGrid_HoverEnded(object sender, EventArgs e)
        {
            // Reset to dark neutral or keep last color
            // BackdropControl.TransitionTo(Windows.UI.Color.FromArgb(255, 13, 13, 13), Windows.UI.Color.FromArgb(255, 13, 13, 13));
        }

        private void WatchlistGrid_ItemClicked(object sender, MediaNavigationArgs e)
        {
             NavigationService.NavigateToDetails(Frame, e, e.SourceElement);
        }
        
        private void WatchlistGrid_DetailsAction(object sender, MediaNavigationArgs e)
        {
             NavigationService.NavigateToDetails(Frame, e, e.SourceElement);
        }

        private void WatchlistGrid_PlayAction(object sender, MediaNavigationArgs e)
        {
             NavigationService.NavigateToDetails(Frame, e, e.SourceElement);
        }

        private async void WatchlistGrid_AddListAction(object sender, IMediaStream e)
        {
            // In Watchlist page, this action acts as "Remove"
            await Services.WatchlistManager.Instance.RemoveFromWatchlist(e);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            // Navigate to Movies or Main Page
            Frame.Navigate(typeof(MoviesPage), App.CurrentLogin); // Or whatever main discovery page is preferred
        }
    }
}
