using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Stremio;
using ModernIPTVPlayer.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ModernIPTVPlayer.Pages
{
    public class SearchArgs
    {
        public string Query { get; set; }
        public string PreferredSource { get; set; } // "Stremio" or "IPTV"
        public string Genre { get; set; } // Legacy / Display string
        public string Type { get; set; } // "movie" or "series"
        public GenreSelectionArgs GenreArgs { get; set; } // New detailed selection
        public string ParentContext { get; set; } // e.g. "Stremio", "Diziler"
    }

    public sealed partial class SearchResultsPage : Page
    {
        private SearchArgs _args;
        private ObservableCollection<IMediaStream> _stremioCollection;
        private int _stremioSkip = 0;
        private bool _isLoadingMore = false;
        private bool _hasMoreStremio = true;
        
        private readonly ExpandedCardOverlayController _expandedCardController;
        
        public ObservableCollection<int> ShimmerItems { get; } = 
            new ObservableCollection<int>(Enumerable.Range(0, 15));

        public SearchResultsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
            InitializeGenreOverlay();

            _expandedCardController = new ExpandedCardOverlayController(this, OverlayCanvas, ActiveExpandedCard, CinemaScrim, MainScrollViewer);
            _expandedCardController.PlayRequested += (s, stream) => NavigationService.NavigateToDetailsDirect(Frame, stream);
            _expandedCardController.DetailsRequested += (s, item) => NavigationService.NavigateToDetails(Frame, item.Stream);
        }

        private void InitializeGenreOverlay()
        {
            GenreOverlay.SelectionMade += GenreOverlay_SelectionMade;
            GenreOverlay.CloseRequested += (s, e) => { /* internal hide */ };
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is SearchArgs args)
            {
                // Check if args changed or it's a new navigation
                bool isNewGenre = args.GenreArgs != null && (args.GenreArgs.AddonId != _args?.GenreArgs?.AddonId || args.GenreArgs.GenreValue != _args?.GenreArgs?.GenreValue);
                bool isNewQuery = args.Query != _args?.Query;
                
                if (isNewGenre || isNewQuery || _args == null || e.NavigationMode == NavigationMode.New)
                {
                    _args = args;
                    
                    if (_args.GenreArgs != null)
                    {
                        PageTitle.Text = _args.GenreArgs.DisplayName;
                        GenreFilterButton.Visibility = Visibility.Visible; 
                        
                        UpdateBreadcrumbs(_args.GenreArgs.CatalogType, _args.GenreArgs.DisplayName);
                    }
                    else if (!string.IsNullOrEmpty(_args.Genre))
                    {
                        PageTitle.Text = _args.Genre;
                        GenreFilterButton.Visibility = Visibility.Visible; 
                        
                        UpdateBreadcrumbs(_args.Type, _args.Genre);
                    }
                    else
                    {
                        PageTitle.Text = $"'{_args.Query}'";
                        GenreFilterButton.Visibility = Visibility.Collapsed;
                        
                        UpdateBreadcrumbs(_args.Type, "Arama");
                    }

                    // Reorder sections based on preference
                    if (_args.PreferredSource == "IPTV")
                    {
                        if (ResultsStack.Children.Contains(IptvSection) && ResultsStack.Children.IndexOf(IptvSection) > 0)
                        {
                            ResultsStack.Children.Remove(IptvSection);
                            ResultsStack.Children.Insert(0, IptvSection);
                        }
                    }
                    else
                    {
                        if (ResultsStack.Children.Contains(StremioSection) && ResultsStack.Children.IndexOf(StremioSection) > 0)
                        {
                            ResultsStack.Children.Remove(StremioSection);
                            ResultsStack.Children.Insert(0, StremioSection);
                        }
                    }

                    await PerformSearchAsync();
                }
            }
            else if (e.Parameter is string query)
            {
                // Fallback for string query
                _args = new SearchArgs { Query = query, PreferredSource = "Stremio" };
                PageTitle.Text = $"Arama Sonuçları: '{_args.Query}'";
                GenreFilterButton.Visibility = Visibility.Collapsed;
                await PerformSearchAsync();
            }
        }

        private async Task PerformSearchAsync()
        {
            try
            {
                GlobalShimmer.Visibility = Visibility.Visible;
                StremioSection.Visibility = Visibility.Collapsed;
                // Header Reset
                StremioCountBadge.Visibility = Visibility.Collapsed;
                IptvCountBadge.Visibility = Visibility.Collapsed;

                // Reset Pagination
                _stremioSkip = 0;
                _hasMoreStremio = true;
                _stremioCollection = new ObservableCollection<IMediaStream>();
                StremioGrid.ItemsSource = _stremioCollection;

                // GENRE MODE (New Addon-Centric)
                if (_args.GenreArgs != null)
                {
                    var results = await StremioService.Instance.DiscoverAsync(_args.GenreArgs);
                    
                    if (results != null && results.Any())
                    {
                        foreach(var item in results) _stremioCollection.Add(item);
                        TxtStremioCount.Text = _stremioCollection.Count.ToString();
                        StremioCountBadge.Visibility = Visibility.Visible;
                        StremioSection.Visibility = Visibility.Visible;
                        StremioSectionTitle.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        EmptyPanel.Visibility = Visibility.Visible;
                    }

                    // Always update breadcrumbs at the end of search to ensure UI is in sync
                    if (_args.GenreArgs != null)
                    {
                        UpdateBreadcrumbs(_args.GenreArgs.CatalogType, _args.GenreArgs.DisplayName);
                    }
                    return;
                }

                // LEGACY GENRE MODE
                if (!string.IsNullOrEmpty(_args.Genre))
                {
                    var results = await StremioService.Instance.DiscoverByGenreAsync(_args.Type ?? "movie", _args.Genre);
                    
                    if (results != null && results.Any())
                    {
                        foreach(var item in results) _stremioCollection.Add(item);
                        TxtStremioCount.Text = _stremioCollection.Count.ToString();
                        StremioCountBadge.Visibility = Visibility.Visible;
                        StremioSection.Visibility = Visibility.Visible;
                        StremioSectionTitle.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        EmptyPanel.Visibility = Visibility.Visible;
                    }

                    // Always update breadcrumbs for legacy genre mode as well
                    UpdateBreadcrumbs(_args.Type, _args.Genre);
                    return;
                }

                // SEARCH MODE
                var stremioTask = StremioService.Instance.SearchAsync(_args.Query);
                var iptvTask = SearchIptvAsync(_args.Query);

                await Task.WhenAll(stremioTask, iptvTask);

                var stremioResults = await stremioTask;
                var iptvResults = await iptvTask;

                GlobalShimmer.Visibility = Visibility.Collapsed;

                if (stremioResults != null && stremioResults.Any())
                {
                    foreach(var item in stremioResults) _stremioCollection.Add(item);
                    TxtStremioCount.Text = _stremioCollection.Count.ToString();
                    StremioCountBadge.Visibility = Visibility.Visible;
                    StremioSection.Visibility = Visibility.Visible;
                    StremioSectionTitle.Text = "Stremio Sonuçları";
                }

                if (iptvResults != null && iptvResults.Any())
                {
                    IptvGrid.ItemsSource = iptvResults;
                    TxtIptvCount.Text = iptvResults.Count.ToString();
                    IptvCountBadge.Visibility = Visibility.Visible;
                    IptvSection.Visibility = Visibility.Visible;
                }

                if (_stremioCollection.Count == 0 && (iptvResults == null || !iptvResults.Any()))
                {
                    ShowEmptyStateWithSuggestions();
                }

                // Finalize Breadcrumbs
                UpdateBreadcrumbs(_args.Type, _args.Query);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Search Error: {ex.Message}");
                GlobalShimmer.Visibility = Visibility.Collapsed;
                ShowEmptyStateWithSuggestions();
            }
        }

        private void ShowEmptyStateWithSuggestions()
        {
            EmptyPanel.Visibility = Visibility.Visible;
            
            // Generate some nice suggestions based on current type
            var suggestions = new List<GenreSelectionArgs>();
            string type = _args.Type ?? "movie";
            string addonId = "https://v3-cinemeta.strem.io"; // Default to cinemeta
            
            if (type == "movie")
            {
                suggestions.Add(new GenreSelectionArgs { DisplayName = "🎥 Popüler Filmler", AddonId = addonId, CatalogId = "top", CatalogType = "movie" });
                suggestions.Add(new GenreSelectionArgs { DisplayName = "💥 Aksiyon", AddonId = addonId, CatalogId = "top", CatalogType = "movie", GenreValue = "Action" });
                suggestions.Add(new GenreSelectionArgs { DisplayName = "😂 Komedi", AddonId = addonId, CatalogId = "top", CatalogType = "movie", GenreValue = "Comedy" });
                suggestions.Add(new GenreSelectionArgs { DisplayName = "👻 Korku", AddonId = addonId, CatalogId = "top", CatalogType = "movie", GenreValue = "Horror" });
            }
            else
            {
                suggestions.Add(new GenreSelectionArgs { DisplayName = "📺 Popüler Diziler", AddonId = addonId, CatalogId = "top", CatalogType = "series" });
                suggestions.Add(new GenreSelectionArgs { DisplayName = "🎭 Dram", AddonId = addonId, CatalogId = "top", CatalogType = "series", GenreValue = "Drama" });
                suggestions.Add(new GenreSelectionArgs { DisplayName = "🚀 Bilim Kurgu", AddonId = addonId, CatalogId = "top", CatalogType = "series", GenreValue = "Sci-Fi & Fantasy" });
                suggestions.Add(new GenreSelectionArgs { DisplayName = "🕵️ Suç", AddonId = addonId, CatalogId = "top", CatalogType = "series", GenreValue = "Crime" });
            }
            
            SuggestionGrid.ItemsSource = suggestions;
        }

        private void Suggestion_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is GenreSelectionArgs args)
            {
                GenreOverlay_SelectionMade(this, args);
            }
        }

        private async Task<List<IMediaStream>> SearchIptvAsync(string query)
        {
            var results = new List<IMediaStream>();
            var login = App.CurrentLogin;
            if (login == null) return results;

            string playlistId = login.PlaylistUrl ?? "default";

            // We can search VOD and Series
            var movies = await ContentCacheService.Instance.LoadCacheAsync<LiveStream>(playlistId, "vod_streams");
            if (movies != null)
            {
                results.AddRange(movies.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
            }

            var series = await ContentCacheService.Instance.LoadCacheAsync<SeriesStream>(playlistId, "series_streams");
            if (series != null)
            {
                results.AddRange(series.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
            }

            return results;
        }

        private void ResultsGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is IMediaStream item)
            {
                var args = new MediaNavigationArgs(item);
                NavigationService.NavigateToDetails(Frame, args, sender as UIElement);
            }
        }


        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (GenreOverlay.Visibility == Visibility.Visible)
            {
                GenreOverlay.Hide();
                return;
            }

            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void MainScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            var sv = sender as ScrollViewer;
            if (sv == null) return;

            // Trigger when near bottom (e.g., within 1000px)
            if (sv.VerticalOffset >= sv.ScrollableHeight - 1000)
            {
                if (!_isLoadingMore && _hasMoreStremio && (_args.GenreArgs != null || !string.IsNullOrEmpty(_args.Genre)))
                {
                    _ = LoadMoreStremioResultsAsync();
                }
            }
        }

        private async Task LoadMoreStremioResultsAsync()
        {
            if (_isLoadingMore) return;
            _isLoadingMore = true;
            LoadMoreProgress.Visibility = Visibility.Visible;

            try
            {
                int currentCount = _stremioCollection.Count;
                List<StremioMediaStream> newResults = null;

                if (_args.GenreArgs != null)
                {
                    newResults = await StremioService.Instance.DiscoverAsync(_args.GenreArgs, currentCount);
                }
                else if (!string.IsNullOrEmpty(_args.Genre))
                {
                    newResults = await StremioService.Instance.DiscoverByGenreAsync(_args.Type ?? "movie", _args.Genre, currentCount);
                }

                if (newResults != null && newResults.Any())
                {
                    foreach (var item in newResults)
                    {
                        if (!_stremioCollection.Any(x => x.Id == item.Id))
                        {
                            _stremioCollection.Add(item);
                        }
                    }
                    TxtStremioCount.Text = _stremioCollection.Count.ToString();
                }
                else
                {
                    _hasMoreStremio = false; 
                }
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"Load More Error: {ex.Message}");
            }
            finally
            {
                _isLoadingMore = false;
                LoadMoreProgress.Visibility = Visibility.Collapsed;
            }
        }

        // GENRE FILTER LOGIC
        private void GenreFilterButton_Click(object sender, RoutedEventArgs e)
        {
            GenreOverlay.Show(_args.Type ?? "movie");
        }

        private void GenreOverlay_SelectionMade(object sender, GenreSelectionArgs args)
        {
             // Update existing args to preserve context (AddonId, CatalogId etc)
             _args.GenreArgs = args;
             _args.Genre = args.GenreValue;
             _args.Query = args.GenreValue; // Sync query for search display
             
             if (args.CatalogType != null) _args.Type = args.CatalogType;

             PageTitle.Text = args.DisplayName;
             
             // PerformSearchAsync will handle clearing collections and resetting pagination
             _ = PerformSearchAsync();
        }
        private void UpdateBreadcrumbs(string type, string current)
        {
            System.Diagnostics.Debug.WriteLine($"[SearchResults] UpdateBreadcrumbs: type={type}, parentContext={_args.ParentContext}, genre={_args.Genre}");

            RootBreadcrumb.Content = "Stremio";
            TypeBreadcrumb.Content = type == "movie" ? "Filmler" : (type == "series" ? "Diziler" : "Tümü");
            
            if (_args.GenreArgs != null)
            {
                CategoryBreadcrumbArea.Visibility = Visibility.Visible;
                
                // Refined De-duplication: Only hide if it exactly matches the type label
                var category = _args.ParentContext ?? "Kategori";
                
                // [FIX] If the context contains a ": Genre" suffix from the service, strip it for the breadcrumb
                if (category.Contains(": ")) {
                    category = category.Split(": ")[0];
                }

                var typeLabel = (string)TypeBreadcrumb.Content;
                
                System.Diagnostics.Debug.WriteLine($"[SearchResults] Breadcrumb Logic: category='{category}', typeLabel='{typeLabel}'");

                if (string.Equals(category, typeLabel, StringComparison.OrdinalIgnoreCase) || 
                    string.Equals(category, "Movie", StringComparison.OrdinalIgnoreCase) || 
                    string.Equals(category, "Series", StringComparison.OrdinalIgnoreCase))
                {
                    CategoryBreadcrumbArea.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CategoryBreadcrumb.Content = category;
                }
                
                if (!string.IsNullOrEmpty(_args.Genre))
                {
                    GenreBreadcrumbArea.Visibility = Visibility.Visible;
                    GenreBreadcrumbText.Text = _args.Genre;
                }
                else
                {
                    GenreBreadcrumbArea.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                CategoryBreadcrumbArea.Visibility = Visibility.Collapsed;
                GenreBreadcrumbArea.Visibility = Visibility.Collapsed;
            }
        }

        private void PosterCard_HoverStarted(object sender, EventArgs e)
        {
            if (sender is PosterCard card)
            {
                _expandedCardController.OnHoverStarted(card);
                
                // Also update backdrop immediately on specific item hover for premium feel
                if (card.HeroColors.HasValue)
                {
                    BackdropControl.TransitionTo(card.HeroColors.Value.Primary, card.HeroColors.Value.Secondary);
                }
            }
        }

        private void PosterCard_HoverEnded(object sender, EventArgs e)
        {
            _ = _expandedCardController.CloseExpandedCardAsync();
        }

        private void PosterCard_ColorsExtracted(object sender, (Windows.UI.Color Primary, Windows.UI.Color Secondary) e)
        {
            // Only update backdrop from the first few items to keep it stable
            var card = sender as PosterCard;
            if (_stremioCollection.Take(5).Any(x => x.Title == card.Title))
            {
                 BackdropControl.TransitionTo(e.Primary, e.Secondary);
            }
        }

        private void RootBreadcrumb_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void TypeBreadcrumb_Click(object sender, RoutedEventArgs e)
        {
            // Just refresh with base catalog for that type (preserve Addon/Catalog)
            if (_args.GenreArgs != null)
            {
                var baseArgs = new GenreSelectionArgs
                {
                    AddonId = _args.GenreArgs.AddonId,
                    CatalogId = _args.GenreArgs.CatalogId,
                    CatalogType = _args.GenreArgs.CatalogType,
                    DisplayName = (_args.GenreArgs.CatalogType == "movie" || _args.Type == "movie") ? "Tüm Filmler" : "Tüm Diziler",
                    GenreValue = null // Clear the sub-filter but keep the catalog
                };
                GenreOverlay_SelectionMade(this, baseArgs);
            }
        }

        private void ClearGenre_Click(object sender, RoutedEventArgs e)
        {
            CategoryBreadcrumb_Click(null, null);
        }

        private void CategoryBreadcrumb_Click(object sender, RoutedEventArgs e)
        {
            if (_args.GenreArgs != null)
            {
                var category = _args.ParentContext ?? "Kategori";
                if (category.Contains(": ")) category = category.Split(": ")[0];

                var baseArgs = new GenreSelectionArgs
                {
                    AddonId = _args.GenreArgs.AddonId,
                    CatalogId = _args.GenreArgs.CatalogId,
                    CatalogType = _args.GenreArgs.CatalogType,
                    DisplayName = category,
                    GenreValue = null
                };
                GenreOverlay_SelectionMade(this, baseArgs);
            }
        }

        private void OverlayCanvas_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_expandedCardController.IsCardVisible)
            {
                // Redirect to card and stop bubbling to main page
                _expandedCardController.ActiveExpandedCard.OnRootPointerWheelChanged(e);
                e.Handled = true;
            }
        }
    }
}
