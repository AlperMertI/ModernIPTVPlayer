using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Stremio;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ModernIPTVPlayer.Pages
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class SearchArgs
    {
        public string Query { get; set; }
        public string PreferredSource { get; set; } // "Stremio" or "IPTV"
        public string Genre { get; set; } // Legacy / Display string
        public string Type { get; set; } // "movie" or "series"
        public GenreSelectionArgs GenreArgs { get; set; } // New detailed selection
        public string ParentContext { get; set; } // e.g. "Stremio", "Diziler"
    }

    [Microsoft.UI.Xaml.Data.Bindable]
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
        private string _filterSource = "all";
        private string _filterYear = "Tüm Yıllar";
        private string _sortOrder = "relevance";
        
        private ScrollViewer? _gridScrollViewer;
        private readonly ExpandedCardOverlayController _expandedCardController;
        private System.Threading.CancellationTokenSource _pageSearchCts;
        private DateTime _lastUpdate = DateTime.MinValue;
        private const int UPDATE_THROTTLE_MS = 400; // Optimal for human perception stability
        
        public ObservableCollection<int> ShimmerItems { get; } = 
            new ObservableCollection<int>(Enumerable.Range(0, 15));


        public SearchResultsPage()
        {
            _stremioCollection = new ObservableCollection<IMediaStream>();
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
            InitializeGenreOverlay();

            _expandedCardController = new ExpandedCardOverlayController(this, OverlayCanvas, ActiveExpandedCard, CinemaScrim, null); // We will set ScrollViewer later
            _expandedCardController.PlayRequested += (s, stream) => NavigationService.NavigateToDetailsDirect(Frame, stream);
            _expandedCardController.DetailsRequested += (s, item) => NavigationService.NavigateToDetails(Frame, item.Stream);

            this.Loaded += SearchResultsPage_Loaded;
        }

        private void SearchResultsPage_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureScrollDetection();
        }

        private async void EnsureScrollDetection()
        {
            if (_gridScrollViewer != null) return;

            // Give it one frame to materialize the template if it just became visible
            await Task.Delay(100);

            _gridScrollViewer = FindChild<ScrollViewer>(StremioGrid);
            if (_gridScrollViewer != null)
            {
                _gridScrollViewer.ViewChanged += MainScrollViewer_ViewChanged;
                _expandedCardController.SetScrollViewer(_gridScrollViewer);
                System.Diagnostics.Debug.WriteLine("[SearchResults] ScrollViewer found and hooked.");
            }
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
                // [FIX] If we are navigating BACK, and we already have results for this query, DO NOT re-trigger search.
                if (e.NavigationMode == NavigationMode.Back && _args != null && _args.Query == query && _stremioCollection.Count > 0)
                {
                    return;
                }

                // Fallback for string query
                _args = new SearchArgs { Query = query, PreferredSource = "Stremio" };
                PageTitle.Text = $"'{_args.Query}'";
                GenreFilterButton.Visibility = Visibility.Collapsed;

                await PerformSearchAsync();
            }
        }

        private async Task PerformSearchAsync()
        {
            _pageSearchCts?.Cancel();
            _pageSearchCts = new System.Threading.CancellationTokenSource();
            var token = _pageSearchCts.Token;

            try
            {
                EmptyPanel.Visibility = Visibility.Collapsed;
                GlobalShimmer.Visibility = Visibility.Visible;
                StremioGrid.Visibility = Visibility.Collapsed;
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
                        GlobalShimmer.Visibility = Visibility.Collapsed;
                        _allRawResults = results.OfType<IMediaStream>().ToList();
                        UpdateYearList();
                        await ApplyFiltersAndSortAsync();

                        TxtStremioCount.Text = _stremioCollection.Count.ToString();
                        StremioCountBadge.Visibility = Visibility.Visible;
                        StremioGrid.Visibility = Visibility.Visible;
                        EnsureScrollDetection();
                        StremioSectionTitle.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        GlobalShimmer.Visibility = Visibility.Collapsed;
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
                        GlobalShimmer.Visibility = Visibility.Collapsed;
                        _allRawResults = results.OfType<IMediaStream>().ToList();
                        UpdateYearList();
                        await ApplyFiltersAndSortAsync();

                        TxtStremioCount.Text = _stremioCollection.Count.ToString();
                        StremioCountBadge.Visibility = Visibility.Visible;
                        StremioGrid.Visibility = Visibility.Visible;
                        EnsureScrollDetection();
                        StremioSectionTitle.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        GlobalShimmer.Visibility = Visibility.Collapsed;
                        EmptyPanel.Visibility = Visibility.Visible;
                    }

                    // Always update breadcrumbs for legacy genre mode as well
                    UpdateBreadcrumbs(_args.Type, _args.Genre);
                    return;
                }

                // REGULAR SEARCH MODE
                if (!string.IsNullOrEmpty(_args.Query))
                {
                    string searchType = _args.Type ?? "all";
                    string searchScope = "all";
                    if (_args.PreferredSource?.Equals("IPTV", StringComparison.OrdinalIgnoreCase) == true) searchScope = "iptv";
                    else if (_args.PreferredSource?.Equals("Library", StringComparison.OrdinalIgnoreCase) == true) searchScope = "library";

                    // [SESSION HANDOFF] StremioService now manages sessions.
                    // SearchAsync will return instant results if session exists, and we subscribe for more.
                    await StremioService.Instance.SearchAsync(_args.Query, searchType, searchScope, (partialResults) =>
                    {
                        if (token.IsCancellationRequested) return;

                        this.DispatcherQueue.TryEnqueue(() =>
                        {
                            _allRawResults = partialResults.OfType<IMediaStream>().ToList();
                            
                            // [THROTTLE] Only update UI smoothly
                            var now = DateTime.Now;
                            if ((now - _lastUpdate).TotalMilliseconds > UPDATE_THROTTLE_MS || partialResults.Count < 10 || _stremioCollection.Count == 0)
                            {
                                _lastUpdate = now;
                                _ = ApplyFiltersAndSortAsync().ContinueWith(t => 
                                {
                                    this.DispatcherQueue.TryEnqueue(() => 
                                    {
                                        GlobalShimmer.Visibility = Visibility.Collapsed;
                                        TxtStremioCount.Text = _stremioCollection.Count.ToString();
                                        StremioCountBadge.Visibility = Visibility.Visible;
                                        StremioGrid.Visibility = Visibility.Visible;
                                        EnsureScrollDetection();
                                        StremioSectionTitle.Text = searchScope == "library" ? "Kütüphane" : (searchScope == "iptv" ? "IPTV Sonuçları" : "Arama Sonuçları");
                                        StremioSectionTitle.Visibility = Visibility.Visible;
                                        EmptyPanel.Visibility = Visibility.Collapsed;
                                        _ = StremioService.Instance.MatchVisibleIptvAsync(_stremioCollection.Take(15).OfType<StremioMediaStream>(), _args.Query, token);
                                    });
                                });
                            }
                        });
                    }, token);
                }

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

        private bool IsEquivalent(IMediaStream a, IMediaStream b)
        {
            if (a == null || b == null) return false;
            
            bool aIsSeries = a is SeriesStream || (a is StremioMediaStream sa && sa.IsSeries) || a.Type == "series" || a.Type == "tv";
            bool bIsSeries = b is SeriesStream || (b is StremioMediaStream sb && sb.IsSeries) || b.Type == "series" || b.Type == "tv";
            
            if (aIsSeries != bIsSeries) return false;

            bool aHasId = !string.IsNullOrEmpty(a.IMDbId) && a.IMDbId.StartsWith("tt");
            bool bHasId = !string.IsNullOrEmpty(b.IMDbId) && b.IMDbId.StartsWith("tt");

            if (aHasId && bHasId && a.IMDbId == b.IMDbId)
            {
                return true;
            }

            return TitleHelper.IsMatch(a.Title, b.Title, a.Year, b.Year);
        }

        private void ShowEmptyStateWithSuggestions()
        {
            GlobalShimmer.Visibility = Visibility.Collapsed;
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
                    // O(1) duplicate checking using combined HashSet for speed
                    var existingImdbIds = new HashSet<string>(_allRawResults.Where(x => !string.IsNullOrEmpty(x.IMDbId)).Select(x => x.IMDbId));
                    var existingTitleYearKeys = new HashSet<string>(_allRawResults.Where(x => string.IsNullOrEmpty(x.IMDbId)).Select(x => $"{x.Title.ToLowerInvariant()}|{x.Year}"));
                    
                    foreach (var item in newResults)
                    {
                        bool isDuplicate = false;
                        if (!string.IsNullOrEmpty(item.IMDbId) && existingImdbIds.Contains(item.IMDbId))
                        {
                            isDuplicate = true;
                        }
                        else 
                        {
                            string key = $"{item.Title.ToLowerInvariant()}|{item.Year}";
                            if (existingTitleYearKeys.Contains(key))
                            {
                                isDuplicate = true;
                            }
                        }

                        if (!isDuplicate)
                        {
                            _allRawResults.Add(item);
                            if (!string.IsNullOrEmpty(item.IMDbId)) existingImdbIds.Add(item.IMDbId);
                        }
                    }
                    UpdateYearList();
                    await ApplyFiltersAndSortAsync();
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
                _ = ApplyFiltersAndSortAsync();
            }
        }

        private void SourceFilter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb)
            {
                _filterSource = rb.Tag?.ToString() ?? "all";
                _ = ApplyFiltersAndSortAsync();
            }
        }

        private void YearListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is string year)
            {
                _filterYear = year;
                YearFilterText.Text = year;
                _ = ApplyFiltersAndSortAsync();
                YearFilterButton.Flyout?.Hide();
            }
        }

        private void SortItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item)
            {
                _sortOrder = item.Tag?.ToString() ?? "relevance";
                SortButtonText.Text = item.Text;
                _ = ApplyFiltersAndSortAsync();
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

        private async Task ApplyFiltersAndSortAsync()
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

            // 2.5 Filter by Source
            if (_filterSource != "all")
            {
                if (_filterSource == "iptv") items = items.Where(x => x is StremioMediaStream s && (s.IsIptv || s.IsAvailableOnIptv));
                else if (_filterSource == "library") items = items.Where(x => 
                {
                    if (x is StremioMediaStream s) return WatchlistManager.Instance.IsOnWatchlist(s.IMDbId) || WatchlistManager.Instance.IsOnWatchlist(s.Id.ToString());
                    return WatchlistManager.Instance.IsOnWatchlist(x.IMDbId);
                });
                else if (_filterSource == "stremio") items = items.Where(x => x is StremioMediaStream s && !s.IsIptv);
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

            // Synchronize _stremioCollection with processedList securely (FAST-PATH O(N) ALGORITHM)
            var processedSet = new HashSet<IMediaStream>(processedList);

            // Pass 1: Remove items that were filtered out
            for (int i = _stremioCollection.Count - 1; i >= 0; i--)
            {
                var existingItem = _stremioCollection[i];
                if (!processedSet.Contains(existingItem))
                {
                    _stremioCollection.RemoveAt(i);
                }
            }

            // Pass 2: Ensure correct order and insert new items
            for (int i = 0; i < processedList.Count; i++)
            {
                var target = processedList[i];
                
                if (i < _stremioCollection.Count)
                {
                    if (_stremioCollection[i] != target)
                    {
                        // Use a cheaper index check if the item might be further down
                        int currentIndex = -1;
                        for (int j = i + 1; j < Math.Min(i + 20, _stremioCollection.Count); j++)
                        {
                            if (_stremioCollection[j] == target) { currentIndex = j; break; }
                        }

                        if (currentIndex != -1)
                        {
                            _stremioCollection.Move(currentIndex, i);
                        }
                        else
                        {
                            _stremioCollection.Insert(i, target);
                        }
                    }
                }
                else
                {
                    _stremioCollection.Add(target);
                }

                if (i % 30 == 0) await Task.Delay(1);
            }

            // [NEW] Trigger Lazy IPTV Matching for the currently filtered/sorted top items
            if (_args != null && !string.IsNullOrEmpty(_args.Query))
            {
                _ = StremioService.Instance.MatchVisibleIptvAsync(_stremioCollection.Take(25).OfType<StremioMediaStream>(), _args.Query, _pageSearchCts?.Token ?? default);
            }

            TxtStremioCount.Text = _stremioCollection.Count.ToString();
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

            if (_args.PreferredSource?.Equals("Library", StringComparison.OrdinalIgnoreCase) == true)
                RootBreadcrumb.Content = "Kütüphane";
            else if (_args.PreferredSource?.Equals("IPTV", StringComparison.OrdinalIgnoreCase) == true)
                RootBreadcrumb.Content = "IPTV";
            else
                RootBreadcrumb.Content = "Arama";

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

        private void PosterCard_ColorsExtracted(object sender, ColorExtractedEventArgs e)
        {
            // Only update backdrop from the first few items to keep it stable
            if (sender is PosterCard card && _stremioCollection.Take(5).Any(x => x.Title == card.Title))
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
            if (_args != null && _args.GenreArgs == null && !string.IsNullOrEmpty(_args.Query))
            {
                // It's a regular search keyword breadcrumb being cleared
                _args.Query = "";
                _args.PreferredSource = "Stremio"; // Reset to All/Stremio
                _ = PerformSearchAsync();
            }
            else
            {
                CategoryBreadcrumb_Click(null, null);
            }
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

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild) return typedChild;
                var found = FindChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}
