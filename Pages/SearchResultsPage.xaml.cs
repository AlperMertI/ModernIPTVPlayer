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
        
        // Filter & Sort State
        private List<IMediaStream> _allRawResults = new();
        private List<string> _availableYears = new() { "Tüm Yıllar" };
        private string _filterType = "all";
        private string _filterYear = "Tüm Yıllar";
        private string _sortOrder = "relevance";
        
        private readonly ExpandedCardOverlayController _expandedCardController;
        
        public ObservableCollection<int> ShimmerItems { get; } = 
            new ObservableCollection<int>(Enumerable.Range(0, 15));

        public SearchResultsPage()
        {
            _stremioCollection = new ObservableCollection<IMediaStream>();
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


                    await PerformSearchAsync();
                }
            }
            else if (e.Parameter is string query)
            {
                // Fallback for string query
                _args = new SearchArgs { Query = query, PreferredSource = "Stremio" };
                PageTitle.Text = $"'{_args.Query}'";
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

                // Reset Pagination
                _stremioSkip = 0;
                _hasMoreStremio = true;
                _allRawResults.Clear();
                _stremioCollection = new ObservableCollection<IMediaStream>();
                StremioGrid.ItemsSource = _stremioCollection;

                // GENRE MODE (New Addon-Centric)
                if (_args.GenreArgs != null)
                {
                    var results = await StremioService.Instance.DiscoverAsync(_args.GenreArgs);
                    
                    if (results != null && results.Any())
                    {
                        _allRawResults = results.Cast<IMediaStream>().ToList();
                        UpdateYearList();
                        ApplyFiltersAndSort();

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
                        _allRawResults = results.Cast<IMediaStream>().ToList();
                        UpdateYearList();
                        ApplyFiltersAndSort();

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
                await StremioService.Instance.SearchAsync(_args.Query, (partialResults) => 
                {
                    this.DispatcherQueue.TryEnqueue(() => 
                    {
                        // Update raw results and year list
                        _allRawResults = partialResults.Cast<IMediaStream>().ToList();
                        UpdateYearList();

                        // Use filtered/sorted results for the UI sync
                        ApplyFiltersAndSort();

                        TxtStremioCount.Text = _stremioCollection.Count.ToString();
                        StremioCountBadge.Visibility = Visibility.Visible;
                        StremioSection.Visibility = Visibility.Visible;
                        StremioSectionTitle.Text = "Arama Sonuçları";
                        GlobalShimmer.Visibility = Visibility.Collapsed;
                    });
                });

                if (_stremioCollection.Count == 0)
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
                        if (!_allRawResults.Any(x => x.Id == item.Id))
                        {
                            _allRawResults.Add(item);
                        }
                    }
                    UpdateYearList();
                    ApplyFiltersAndSort();
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
        // FILTER & SORT LOGIC
        private void TypeFilter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb)
            {
                _filterType = rb.Tag?.ToString() ?? "all";
                ApplyFiltersAndSort();
            }
        }

        private void YearListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (YearListView.SelectedItem is string year)
            {
                _filterYear = year;
                YearFilterText.Text = year;
                ApplyFiltersAndSort();
                YearFilterButton.Flyout?.Hide();
            }
        }

        private void SortItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item)
            {
                _sortOrder = item.Tag?.ToString() ?? "relevance";
                SortButtonText.Text = item.Text;
                ApplyFiltersAndSort();
            }
        }

        private void UpdateYearList()
        {
            var years = _allRawResults
                .Select(x => GetYearDigits(x.Year))
                .Where(y => !string.IsNullOrEmpty(y))
                .Distinct()
                .OrderByDescending(y => y)
                .ToList();

            if (years.Count > 0)
            {
                var currentSelection = _filterYear;
                var newList = new List<string> { "Tüm Yıllar" };
                newList.AddRange(years);
                
                // Check if list actually changed to avoid UI flicker
                if (YearListView.ItemsSource is List<string> existingList && existingList.SequenceEqual(newList))
                    return;

                YearListView.ItemsSource = newList;
                
                // Re-select if still valid
                if (newList.Contains(currentSelection))
                    YearListView.SelectedItem = currentSelection;
            }
        }

        private void ApplyFiltersAndSort()
        {
            if (_stremioCollection == null) return;
            var items = _allRawResults.AsEnumerable();

            // 1. Filter by Type
            if (_filterType != "all")
            {
                items = items.Where(x => x.Type == _filterType);
            }

            // 2. Filter by Year
            if (_filterYear != "Tüm Yıllar")
            {
                items = items.Where(x => GetYearDigits(x.Year) == _filterYear);
            }

            // 3. Sort
            items = _sortOrder switch
            {
                "rating" => items.OrderByDescending(x => GetRatingNumeric(x.Rating)),
                "year_desc" => items.OrderByDescending(x => GetYearDigits(x.Year)),
                "year_asc" => items.OrderBy(x => GetYearDigits(x.Year)),
                _ => items // "relevance" or default (Already ranked by service)
            };

            var processedList = items.ToList();

            // Synchronize _stremioCollection with processedList
            for (int i = 0; i < processedList.Count; i++)
            {
                var target = processedList[i];
                var existingIndex = -1;
                for (int j = i; j < _stremioCollection.Count; j++)
                {
                    if (_stremioCollection[j].Id == target.Id) { existingIndex = j; break; }
                }

                if (existingIndex == -1)
                {
                    var anyIndex = -1;
                    for (int k = 0; k < _stremioCollection.Count; k++) if (_stremioCollection[k].Id == target.Id) { anyIndex = k; break; }
                    if (anyIndex != -1) _stremioCollection.Move(anyIndex, i);
                    else _stremioCollection.Insert(i, target);
                }
                else if (existingIndex != i)
                {
                    _stremioCollection.Move(existingIndex, i);
                }
            }

            while (_stremioCollection.Count > processedList.Count)
            {
                _stremioCollection.RemoveAt(_stremioCollection.Count - 1);
            }
        }

        private string GetYearDigits(string year)
        {
            if (string.IsNullOrEmpty(year)) return "";
            var match = System.Text.RegularExpressions.Regex.Match(year, @"\d{4}");
            return match.Success ? match.Value : "";
        }

        private double GetRatingNumeric(string rating)
        {
            if (string.IsNullOrEmpty(rating)) return 0;
            // Handle formats like "8.5 / 10" or just "8.5"
            var parts = rating.Split(' ');
            if (double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
                return r;
            return 0;
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
                
                // [NEW] If it's a search, show it as a breadcrumb level
                if (!string.IsNullOrEmpty(_args.Query) && _args.GenreArgs == null && string.IsNullOrEmpty(_args.Genre))
                {
                    GenreBreadcrumbArea.Visibility = Visibility.Visible;
                    GenreBreadcrumbText.Text = $"'{_args.Query}'";
                }
                else
                {
                    GenreBreadcrumbArea.Visibility = Visibility.Collapsed;
                }
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
