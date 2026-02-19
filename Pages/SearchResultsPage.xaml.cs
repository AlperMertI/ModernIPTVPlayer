using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Stremio;
using System;
using System.Collections.Generic;
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
    }

    public sealed partial class SearchResultsPage : Page
    {
        private SearchArgs _args;
        private System.Collections.ObjectModel.ObservableCollection<IMediaStream> _stremioCollection;
        private int _stremioSkip = 0;
        private bool _isLoadingMore = false;
        private bool _hasMoreStremio = true;

        public SearchResultsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
            InitializeGenreOverlay();
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
                        GenreButtonText.Text = _args.GenreArgs.GenreValue;
                    }
                    else if (!string.IsNullOrEmpty(_args.Genre))
                    {
                        PageTitle.Text = $"Kategori: {_args.Genre}";
                        GenreFilterButton.Visibility = Visibility.Visible; 
                        GenreButtonText.Text = _args.Genre;
                    }
                    else
                    {
                        PageTitle.Text = $"Arama Sonuçları: '{_args.Query}'";
                         GenreFilterButton.Visibility = Visibility.Collapsed;
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
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
                StremioSection.Visibility = Visibility.Collapsed;
                IptvSection.Visibility = Visibility.Collapsed;
                EmptyPanel.Visibility = Visibility.Collapsed;

                // Reset Pagination
                _stremioSkip = 0;
                _hasMoreStremio = true;
                _stremioCollection = new System.Collections.ObjectModel.ObservableCollection<IMediaStream>();
                StremioGrid.ItemsSource = _stremioCollection;

                // GENRE MODE (New Addon-Centric)
                if (_args.GenreArgs != null)
                {
                    var results = await StremioService.Instance.DiscoverAsync(_args.GenreArgs);
                    
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;

                    if (results != null && results.Any())
                    {
                        foreach(var item in results) _stremioCollection.Add(item);
                        TxtStremioCount.Text = _stremioCollection.Count.ToString();
                        StremioSection.Visibility = Visibility.Visible;
                        StremioSectionTitle.Text = _args.GenreArgs.DisplayName;
                    }
                    else
                    {
                        EmptyPanel.Visibility = Visibility.Visible;
                    }
                    return;
                }

                // LEGACY GENRE MODE
                if (!string.IsNullOrEmpty(_args.Genre))
                {
                    var results = await StremioService.Instance.DiscoverByGenreAsync(_args.Type ?? "movie", _args.Genre);
                    
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;

                    if (results != null && results.Any())
                    {
                        foreach(var item in results) _stremioCollection.Add(item);
                        TxtStremioCount.Text = _stremioCollection.Count.ToString();
                        StremioSection.Visibility = Visibility.Visible;
                        StremioSectionTitle.Text = $"{_args.Genre} ({_args.Type})"; 
                    }
                    else
                    {
                        EmptyPanel.Visibility = Visibility.Visible;
                    }
                    return;
                }

                // SEARCH MODE
                var stremioTask = StremioService.Instance.SearchAsync(_args.Query);
                var iptvTask = SearchIptvAsync(_args.Query);

                await Task.WhenAll(stremioTask, iptvTask);

                var stremioResults = await stremioTask;
                var iptvResults = await iptvTask;

                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;

                // Update UI ... (rest same)
                if (stremioResults != null && stremioResults.Any())
                {
                    foreach(var item in stremioResults) _stremioCollection.Add(item);
                    TxtStremioCount.Text = _stremioCollection.Count.ToString();
                    StremioSection.Visibility = Visibility.Visible;
                    StremioSectionTitle.Text = "Stremio Sonuçları";
                }

                if (iptvResults != null && iptvResults.Any())
                {
                    IptvGrid.ItemsSource = iptvResults;
                    TxtIptvCount.Text = iptvResults.Count.ToString();
                    IptvSection.Visibility = Visibility.Visible;
                }

                if (_stremioCollection.Count == 0 && (iptvResults == null || !iptvResults.Any()))
                {
                    EmptyPanel.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Search Error: {ex.Message}");
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                EmptyPanel.Visibility = Visibility.Visible;
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
             var newArgs = new SearchArgs 
             { 
                 Query = args.GenreValue, 
                 PreferredSource = "Stremio",
                 Genre = args.GenreValue,
                 Type = args.CatalogType,
                 GenreArgs = args
             };
             
             // If we are already on SearchResultsPage, just update the args and refresh
             if (this.Frame.Content is SearchResultsPage current)
             {
                 _args = newArgs;
                 PageTitle.Text = _args.GenreArgs.DisplayName;
                 GenreButtonText.Text = _args.GenreArgs.GenreValue;
                 
                 // PerformSearchAsync will handle clearing collections and resetting pagination
                 _ = PerformSearchAsync();
             }
             else
             {
                 Frame.Navigate(typeof(SearchResultsPage), newArgs, new DrillInNavigationTransitionInfo());
             }
        }
    }
}
