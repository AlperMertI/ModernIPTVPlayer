using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using ModernIPTVPlayer.Controls;
using System.Diagnostics;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Stremio;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using Windows.Foundation;
using System.Numerics;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Input;

namespace ModernIPTVPlayer
{
    public sealed partial class SeriesPage : Page
    {
        private enum ContentSource { IPTV, Stremio }
        private ContentSource _currentSource = ContentSource.Stremio;

        private LoginParams? _loginInfo;
        private IMediaStream? _lastClickedItem;
        private bool _isLoaded = false;
        private HttpClient _httpClient;
        
        // Data Store
        private List<LiveCategory> _iptvCategories = new();
        private List<SeriesStream> _allIptvSeries = new();

        private (Windows.UI.Color Primary, Windows.UI.Color Secondary)? _heroColors;
        private readonly ExpandedCardOverlayController _stremioExpandedCardOverlay;


        public SeriesPage()
        {
            this.InitializeComponent();
            _httpClient = HttpHelper.Client;
            
            // Wire up MediaGrid events
            MediaGrid.ItemClicked += MediaGrid_ItemClicked;
            MediaGrid.PlayAction += MediaGrid_PlayAction;
            MediaGrid.DetailsAction += MediaGrid_DetailsAction;
            MediaGrid.AddListAction += MediaGrid_AddListAction;
            MediaGrid.ColorExtracted += MediaGrid_ColorExtracted;
            MediaGrid.HoverEnded += MediaGrid_HoverEnded;

            // Wire up StremioControl events
            StremioControl.PlayAction += (s, item) => NavigationService.NavigateToDetailsDirect(Frame, item);
            StremioControl.DetailsAction += (s, item) => NavigationService.NavigateToDetailsDirect(Frame, item);
            StremioControl.ItemClicked += (s, e) => NavigationService.NavigateToDetails(Frame, new MediaNavigationArgs(e.Stream), e.SourceElement);
            StremioControl.BackdropColorChanged += (s, colors) => 
            {
                 _heroColors = colors;
                 BackdropControl.TransitionTo(colors.Primary, colors.Secondary);
            };
            StremioControl.ViewChanged += StremioControl_ViewChanged;

            // Connect Overlay to StremioControl's ScrollViewer
            _stremioExpandedCardOverlay = new ExpandedCardOverlayController(this, OverlayCanvas, ActiveExpandedCard, CinemaScrim, StremioControl.MainScrollViewer);
            _stremioExpandedCardOverlay.PlayRequested += StremioExpandedCardOverlay_PlayRequested;
            _stremioExpandedCardOverlay.DetailsRequested += StremioExpandedCardOverlay_DetailsRequested;
            _stremioExpandedCardOverlay.AddListRequested += StremioExpandedCardOverlay_AddListRequested;
            
            // Wire Hover Events
            StremioControl.CardHoverStarted += (s, card) => _stremioExpandedCardOverlay.OnHoverStarted(card);
            StremioControl.CardHoverEnded += async (s, card) => await _stremioExpandedCardOverlay.CloseExpandedCardAsync();
            StremioControl.RowScrollStarted += (s, e) => 
            {
                _stremioExpandedCardOverlay.CancelPendingShow();
                if (!_stremioExpandedCardOverlay.IsInCinemaMode)
                {
                    _ = _stremioExpandedCardOverlay.CloseExpandedCardAsync();
                }
            };

            // Wire Spotlight Search Events
            SpotlightSearch.ItemClicked += (s, item) => NavigationService.NavigateToDetailsDirect(Frame, item);
            SpotlightSearch.SeeAllClicked += (s, query) => 
            {
                var args = new Pages.SearchArgs { Query = query, PreferredSource = _currentSource.ToString() };
                Frame.Navigate(typeof(Pages.SearchResultsPage), args, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
            };

        }


        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.NavigationMode == NavigationMode.Back)
            {
                System.Diagnostics.Debug.WriteLine("[SeriesPage] Back navigation with custom slide animation.");
                return;
            }
            // Controller handles cleanup via NavigatedFrom/Unloaded subscriptions

            if (e.Parameter is LoginParams p)
            {
                if (_loginInfo != null && _loginInfo.PlaylistUrl != p.PlaylistUrl)
                {
                    // Reset on playlist change
                    IptvCategoryList.ItemsSource = null;
                    MediaGrid.ItemsSource = null;
                    _iptvCategories.Clear();
                    _allIptvSeries.Clear();
                }
                _loginInfo = p;
            }
            
            // Ensure layout matches mode (Vital for collapsing OverlayCanvas)
            UpdateLayoutForMode();

            // Initial Load
            if (_currentSource == ContentSource.IPTV && _loginInfo != null && !string.IsNullOrEmpty(_loginInfo.Host))
            {
                if (_iptvCategories.Count == 0)
                {
                    await LoadIptvDataAsync();
                }
            }
            else if (_currentSource == ContentSource.Stremio)
            {
                if (!StremioControl.HasContent)
                {
                    await StremioControl.LoadDiscoveryAsync("series");
                }
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
        }

        // ==========================================
        // SOURCE SWITCHING LOGIC
        // ==========================================
        private async void Source_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag)
            {
                var newSource = tag == "Stremio" ? ContentSource.Stremio : ContentSource.IPTV;
                
                // Avoid reloading if clicking already selected
                // if (_currentSource == newSource && MediaGrid.ItemsSource != null) return;
                
                _currentSource = newSource;
                
                // UI Toggle Logic
                UpdateLayoutForMode();

                // Clear Grid
                MediaGrid.ItemsSource = null;
                
                if (_currentSource == ContentSource.IPTV)
                {
                    await LoadIptvDataAsync();
                }
                else
                {
                    // Trigger load on control -> SERIES
                    if (!StremioControl.HasContent)
                    {
                         await StremioControl.LoadDiscoveryAsync("series");
                    }
                }
            }
        }

        private void UpdateLayoutForMode()
        {
            // Landing/Startup logic: If no IPTV login, force Stremio and hide toggle
            if (_loginInfo == null || string.IsNullOrEmpty(_loginInfo.Host))
            {
                _currentSource = ContentSource.Stremio;
                SourceStremio.IsChecked = true;
                SourceSwitcherPanel.Visibility = Visibility.Collapsed;
                SidebarToggle.Visibility = Visibility.Collapsed;
            }
            else
            {
                SourceSwitcherPanel.Visibility = Visibility.Visible;
                // SidebarToggle visibility is handled inside the source branches below
            }

            if (_currentSource == ContentSource.IPTV)
            {
                _ = _stremioExpandedCardOverlay.CloseExpandedCardAsync(force: true);

                // Sidebar Mode
                MainSplitView.IsPaneOpen = false; 
                MainSplitView.DisplayMode = SplitViewDisplayMode.Inline;
                SidebarToggle.Visibility = Visibility.Visible;
                MediaGrid.Visibility = Visibility.Visible;
                StremioControl.Visibility = Visibility.Collapsed;
                OverlayCanvas.Visibility = Visibility.Visible;
            }
            else
            {
                // Stremio Mode (Full Screen Premium)
                MainSplitView.IsPaneOpen = false;
                MainSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
                SidebarToggle.Visibility = Visibility.Collapsed;
                
                MediaGrid.Visibility = Visibility.Collapsed;
                StremioControl.Visibility = Visibility.Visible;
                OverlayCanvas.Visibility = Visibility.Visible;
            }
        }

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;
        }

        // ==========================================
        // IPTV LOGIC
        // ==========================================
        private async Task LoadIptvDataAsync()
        {
            try
            {
                LoadingRing.IsActive = false;
                MediaGrid.IsLoading = true;
                
                // Return cached if available
                if (_iptvCategories.Count > 0 && _allIptvSeries.Count > 0)
                {
                     DisplayCategories(_iptvCategories);
                     return;
                }

                string username = _loginInfo.Username;
                string password = _loginInfo.Password;
                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string playlistId = AppSettings.LastPlaylistId?.ToString() ?? "default";

                // 1. Categories
                var cachedCats = await Services.ContentCacheService.Instance.LoadCacheAsync<LiveCategory>(playlistId, "series_cats");
                if (cachedCats != null)
                {
                     _iptvCategories = cachedCats;
                }
                else
                {
                    string api = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_series_categories";
                    string json = await _httpClient.GetStringAsync(api);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
                    _iptvCategories = JsonSerializer.Deserialize<List<LiveCategory>>(json, options) ?? new List<LiveCategory>();
                    
                    _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "series_cats", _iptvCategories);
                }

                // 2. Streams (Global List)
                var cachedStreams = await Services.ContentCacheService.Instance.LoadCacheAsync<SeriesStream>(playlistId, "series_streams");
                if (cachedStreams != null && cachedStreams.Count > 0)
                {
                    _allIptvSeries = cachedStreams;
                }
                else
                {
                     string streamApi = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_series";
                     string streamJson = await _httpClient.GetStringAsync(streamApi);
                     var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
                     var streams = JsonSerializer.Deserialize<List<SeriesStream>>(streamJson, options);
                     
                     if (streams != null)
                     {
                         // Fix URLs if needed (Series usually have seasons/episodes structure, but here we list the shows)
                         // The series object from get_series acts as the 'Stream' here
                         // Series ID is likely needed for fetching details later
                         _allIptvSeries = streams;
                         _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "series_streams", _allIptvSeries);
                     }
                }

                DisplayCategories(_iptvCategories);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SeriesPage] IPTV Error: {ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
                // MediaGrid.IsLoading will be set to false in DisplayCategories -> LoadIptvStreams
                // But if we returned early or failed, ensure it's off?
                // Actually LoadIptvStreams handles it.
                // If we errored out, we should probably turn it off.
                // But DisplayCategories is called at end of try block.
                // Let's safe-guard in DisplayCategories or just let it be.
                // If DisplayCategories isn't called, we might be stuck in Loading.
                // Better to just rely on LoadIptvStreams for the grid part.
                if (_iptvCategories.Count == 0) MediaGrid.IsLoading = false; 
            }
        }

        private async Task DisplayCategories(List<LiveCategory> categories)
        {
            IptvCategoryList.ItemsSource = categories; // Binds to Sidebar List
            
            // Restore last selection if available
            LiveCategory? toSelect = null;
            if (!string.IsNullOrEmpty(Services.PageStateProvider.LastSeriesCategoryId))
            {
                toSelect = categories.FirstOrDefault(c => c.CategoryId == Services.PageStateProvider.LastSeriesCategoryId);
            }

            if (toSelect == null && categories.Count > 0)
            {
                toSelect = categories[0];
            }

            if (toSelect != null)
            {
                bool selectionChanged = IptvCategoryList.SelectedItem != toSelect;
                IptvCategoryList.SelectedItem = toSelect;

                if (selectionChanged || MediaGrid.ItemsSource == null)
                {
                    await LoadIptvStreams(toSelect);
                }
            }
        }

        private void CategorySearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_currentSource != ContentSource.IPTV) return;

            string query = CategorySearchBox.Text.Trim().ToLower();
            
            // 1. Filter Category Names
            var filteredCats = string.IsNullOrEmpty(query) 
                ? _iptvCategories 
                : _iptvCategories.Where(c => c.CategoryName.ToLower().Contains(query)).ToList();
            
            IptvCategoryList.ItemsSource = filteredCats;

            // 2. Filter Content within the currently selected category
            if (IptvCategoryList.SelectedItem is LiveCategory selectedCat)
            {
                var baseItems = _allIptvSeries.Where(m => m.CategoryId == selectedCat.CategoryId);
                var filteredItems = string.IsNullOrEmpty(query)
                    ? baseItems
                    : baseItems.Where(m => m.Name.ToLower().Contains(query));
                    
                MediaGrid.ItemsSource = filteredItems.Cast<IMediaStream>().ToList();
            }
        }

        private async Task LoadIptvStreams(LiveCategory category)
        {
            MediaGrid.IsLoading = true;
            try
            {
                 var filtered = await Task.Run(() => 
                 {
                     return _allIptvSeries.Where(m => m.CategoryId == category.CategoryId).ToList();
                 });
                 MediaGrid.ItemsSource = filtered.Cast<IMediaStream>().ToList();
            }
            finally
            {
                MediaGrid.IsLoading = false;
            }
        }

        private async Task LoadStremioStreams(LiveCategory category)
        {
            if (category.CategoryId == "empty") return;

            MediaGrid.IsLoading = true;
            try
            {
                // Deconstruct ID: "URL|Type|Id"
                var parts = category.CategoryId.Split('|');
                if (parts.Length >= 3)
                {
                    string baseUrl = parts[0];
                    string type = parts[1];
                    string id = parts[2];

                    var streams = await StremioService.Instance.GetCatalogItemsAsync(baseUrl, type, id);
                    
                    // Convert Cast
                    // StremioMediaStream implements IMediaStream
                    MediaGrid.ItemsSource = new List<IMediaStream>(streams); 
                }
            }
            finally
            {
                MediaGrid.IsLoading = false;
            }
        }


        // ==========================================
        // EVENTS
        // ==========================================
        private async void CategoryListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveCategory category)
            {
                // Save selection
                Services.PageStateProvider.LastSeriesCategoryId = category.CategoryId;

                if (_currentSource == ContentSource.IPTV)
                    await LoadIptvStreams(category);
                else
                    await LoadStremioStreams(category);
            }
        }


        private void MediaGrid_ItemClicked(object sender, MediaNavigationArgs e)
        {
            _lastClickedItem = e.Stream;
            NavigationService.NavigateToDetails(Frame, e);
        }

        private void MediaGrid_PlayAction(object sender, MediaNavigationArgs e)
        {
             NavigationService.NavigateToDetails(Frame, e, e.SourceElement);
        }

        private void MediaGrid_DetailsAction(object sender, MediaNavigationArgs e)
        {
             NavigationService.NavigateToDetails(Frame, e, e.SourceElement);
        }

        private void StremioControl_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (sender is Controls.StremioDiscoveryControl control) 
            {
                 BackdropControl.SetVerticalShift(control.MainScrollViewer.VerticalOffset);

                // Close expanded card on scroll to prevent detachment
                if (_stremioExpandedCardOverlay.IsInCinemaMode) return;
            
                if (_stremioExpandedCardOverlay.IsCardVisible)
                {
                    _ = _stremioExpandedCardOverlay.CloseExpandedCardAsync();
                }
            }
        }

        private void MediaGrid_AddListAction(object sender, IMediaStream e)
        {
             // TODO: Favorites Logic
        }

        private void MediaGrid_ColorExtracted(object sender, (Windows.UI.Color Primary, Windows.UI.Color Secondary) colors)
        {
            BackdropControl.TransitionTo(colors.Primary, colors.Secondary);
        }

        private void MediaGrid_HoverEnded(object sender, EventArgs e)
        {
            if (_heroColors.HasValue)
            {
                BackdropControl.TransitionTo(_heroColors.Value.Primary, _heroColors.Value.Secondary);
            }
            else
            {
                BackdropControl.TransitionTo(Windows.UI.Color.FromArgb(255, 13, 13, 13), Windows.UI.Color.FromArgb(255, 13, 13, 13));
            }
        }

        // ==========================================
        // EXPANDED CARD PROXY EVENTS
        // ==========================================
        private async void StremioExpandedCardOverlay_PlayRequested(object sender, IMediaStream e)
        {
             NavigationService.NavigateToDetails(Frame, new MediaNavigationArgs(e, autoResume: true, sourceElement: _stremioExpandedCardOverlay.ActiveExpandedCard.BannerImage));
        }

        private async void StremioExpandedCardOverlay_DetailsRequested(object sender, (IMediaStream Stream, TmdbMovieResult Tmdb) e)
        {
            if (e.Stream is IMediaStream stream)
            {
                NavigationService.NavigateToDetails(Frame, new MediaNavigationArgs(stream, e.Tmdb, false, _stremioExpandedCardOverlay.ActiveExpandedCard.BannerImage));
            }
        }
private void StremioExpandedCardOverlay_AddListRequested(object sender, IMediaStream e)
        {
             // To be implemented
        }

        // ==========================================
        // SEARCH LOGIC
        // ==========================================
        private bool _isSearchActive = false;
        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (SpotlightSearch.Visibility == Visibility.Visible)
                SpotlightSearch.Hide();
            else
                SpotlightSearch.Show();
        }

        private void SpotlightSearch_ItemClicked(object sender, StremioMediaStream e)
        {
            NavigationService.NavigateToDetailsDirect(Frame, e);
        }

        private async void SpotlightSearch_SeeAllClicked(object sender, string query)
        {
             // Enter Full Search Mode
            _isSearchActive = true;
            
            // Hide Discovery
            StremioControl.Visibility = Visibility.Collapsed;
            OverlayCanvas.Visibility = Visibility.Collapsed; // Hide expanded cards if any

            // Show Grid
            MediaGrid.Visibility = Visibility.Visible;
            MediaGrid.IsLoading = true;
            
            try
            {
                var results = await StremioService.Instance.SearchAsync(query);
                MediaGrid.ItemsSource = new List<IMediaStream>(results);
            }
            finally
            {
                MediaGrid.IsLoading = false;
            }
        }
        
        // Keyboard Shortcuts
        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            base.OnKeyDown(e);
            
            if (e.Key == Windows.System.VirtualKey.F && InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                if (_currentSource == ContentSource.Stremio)
                {
                    SpotlightSearch.Show();
                    e.Handled = true;
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (SpotlightSearch.Visibility == Visibility.Visible)
                {
                    SpotlightSearch.Hide();
                    e.Handled = true;
                }
                else if (_isSearchActive)
                {
                    HandleBackRequest();
                    e.Handled = true;
                }
            }
        }

        public bool HandleBackRequest()
        {
            if (SpotlightSearch.Visibility == Visibility.Visible)
            {
                SpotlightSearch.Hide();
                return true;
            }

            if (_isSearchActive)
            {
                 // Exit Search Mode
                _isSearchActive = false;
                MediaGrid.ItemsSource = null; // Clear results
                MediaGrid.Visibility = Visibility.Collapsed;
                
                StremioControl.Visibility = Visibility.Visible;
                OverlayCanvas.Visibility = Visibility.Visible;
                return true;
            }
            
            return false;
        }
    }
}
