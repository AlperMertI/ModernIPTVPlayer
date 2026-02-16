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
    public sealed partial class MoviesPage : Page
    {
        private enum ContentSource { IPTV, Stremio }
        private ContentSource _currentSource = ContentSource.Stremio;

        private LoginParams? _loginInfo;
        private IMediaStream? _lastClickedItem;
        private bool _isLoaded = false;
        private HttpClient _httpClient;
        
        // Data Store
        private List<LiveCategory> _iptvCategories = new();
        private List<LiveStream> _allIptvMovies = new();

        private (Windows.UI.Color Primary, Windows.UI.Color Secondary)? _heroColors;
        private readonly ExpandedCardOverlayController _stremioExpandedCardOverlay;


        public MoviesPage()
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
            StremioControl.ItemClicked += (s, e) => 
            {
                _lastClickedItem = e.Stream;
                NavigationService.NavigateToDetails(Frame, new MediaNavigationArgs(e.Stream), e.SourceElement);
            };
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
                return; // SKIP full reload on back navigation
            }

            if (e.Parameter is LoginParams p)
            {
                if (_loginInfo != null && _loginInfo.PlaylistUrl != p.PlaylistUrl)
                {
                    // Reset on playlist change
                    IptvCategoryList.ItemsSource = null;
                    MediaGrid.ItemsSource = null;
                    _iptvCategories.Clear();
                    _allIptvMovies.Clear();
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
                    await StremioControl.LoadDiscoveryAsync("movie");
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
                    // Trigger load on control
                    if (!StremioControl.HasContent)
                    {
                         await StremioControl.LoadDiscoveryAsync("movie");
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
                SidebarToggle.Visibility = Visibility.Visible;
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
                SearchButton.Visibility = Visibility.Visible;
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
                if (_iptvCategories.Count > 0 && _allIptvMovies.Count > 0)
                {
                     DisplayCategories(_iptvCategories);
                     return;
                }

                string username = _loginInfo.Username;
                string password = _loginInfo.Password;
                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string playlistId = AppSettings.LastPlaylistId?.ToString() ?? "default";

                // 1. Categories
                var cachedCats = await Services.ContentCacheService.Instance.LoadCacheAsync<LiveCategory>(playlistId, "vod_cats");
                if (cachedCats != null)
                {
                     _iptvCategories = cachedCats;
                }
                else
                {
                    string api = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_vod_categories";
                    string json = await _httpClient.GetStringAsync(api);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
                    _iptvCategories = JsonSerializer.Deserialize<List<LiveCategory>>(json, options) ?? new List<LiveCategory>();
                    
                    _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "vod_cats", _iptvCategories);
                }

                // 2. Streams (Global List)
                var cachedStreams = await Services.ContentCacheService.Instance.LoadCacheAsync<LiveStream>(playlistId, "vod_streams");
                if (cachedStreams != null && cachedStreams.Count > 0)
                {
                    _allIptvMovies = cachedStreams;
                }
                else
                {
                     string streamApi = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_vod_streams";
                     string streamJson = await _httpClient.GetStringAsync(streamApi);
                     var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
                     var streams = JsonSerializer.Deserialize<List<LiveStream>>(streamJson, options);
                     
                     if (streams != null)
                     {
                         // Fix URLs
                         foreach (var s in streams)
                         {
                             string extension = !string.IsNullOrEmpty(s.ContainerExtension) ? s.ContainerExtension : "mp4";
                             s.StreamUrl = $"{baseUrl}/movie/{username}/{password}/{s.StreamId}.{extension}"; 
                         }
                         _allIptvMovies = streams;
                         _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "vod_streams", _allIptvMovies);
                     }
                }

                DisplayCategories(_iptvCategories);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MoviesPage] IPTV Error: {ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
                if (_iptvCategories.Count == 0) MediaGrid.IsLoading = false;
            }
        }

        private async Task DisplayCategories(List<LiveCategory> categories)
        {
            IptvCategoryList.ItemsSource = categories; // Binds to Sidebar List
            
            // Restore last selection if available
            LiveCategory? toSelect = null;
            if (!string.IsNullOrEmpty(Services.PageStateProvider.LastMovieCategoryId))
            {
                toSelect = categories.FirstOrDefault(c => c.CategoryId == Services.PageStateProvider.LastMovieCategoryId);
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
                var baseItems = _allIptvMovies.Where(m => m.CategoryId == selectedCat.CategoryId);
                var filteredItems = string.IsNullOrEmpty(query)
                    ? baseItems
                    : baseItems.Where(m => m.Name.ToLower().Contains(query));
                    
                MediaGrid.ItemsSource = new List<IMediaStream>(filteredItems);
            }
        }

        private async Task LoadIptvStreams(LiveCategory category)
        {
            MediaGrid.IsLoading = true;
            try
            {
                 var filtered = await Task.Run(() => 
                 {
                     return _allIptvMovies.Where(m => m.CategoryId == category.CategoryId).ToList();
                 });
                 MediaGrid.ItemsSource = new List<IMediaStream>(filtered);
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
                Services.PageStateProvider.LastMovieCategoryId = category.CategoryId;

                if (_currentSource == ContentSource.IPTV)
                    await LoadIptvStreams(category);
                else
                    await LoadStremioStreams(category);
            }
        }


        private void MediaGrid_ItemClicked(object sender, MediaNavigationArgs e)
        {
            _lastClickedItem = e.Stream;
            NavigationService.NavigateToDetails(Frame, e, e.SourceElement);
        }

        private void MediaGrid_PlayAction(object sender, MediaNavigationArgs e)
        {
            // Direct Play
             if (e.Stream is LiveStream stream)
             {
                 Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name));
             }
             else
             {
                 NavigationService.NavigateToDetails(Frame, e, e.SourceElement);
             }
        }

        private void MediaGrid_DetailsAction(object sender, MediaNavigationArgs e)
        {
             NavigationService.NavigateToDetails(Frame, e, e.SourceElement);
        }

        private void StremioControl_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (sender is Controls.StremioDiscoveryControl control) // Or sender is ScrollViewer
            {
                 // We need offset
                 // The event sender is actually the ScrollViewer if we proxied it directly, or the control.
                 // In our StremioControl implementation: `ViewChanged?.Invoke(this, e);` sent `this` as sender.
                 
                 // Access the offset via the public property we added? 
                 // Actually we exposed MainScrollViewer.
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
             NavigationService.NavigateToDetails(Frame, new MediaNavigationArgs(e.Stream, e.Tmdb, false, _stremioExpandedCardOverlay.ActiveExpandedCard.BannerImage));
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

        private void SpotlightSearch_ItemClicked(object sender, IMediaStream e)
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
            
            // Update UI (Maybe header title?)
            // We don't have a dedicated Title TextBlock exposed in XAML easily, 
            // but we can assume the user knows they are searching.
            
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

        // Back Navigation Handling (Call this from MainWindow or verify generic back works)
        // Since we don't have a global back handler here, we rely on the internal state.
        // If the user presses "Back" on the mouse or NavView back, we should exit search mode.
        // But Page doesn't have a "BackRequested" virtual method easily accessible without partial hacks or NavView hooks.
        // For now, let's assume if they click "SidebarToggle" (which turns into Back in some designs) or just use the Grid actions.
        
        // To make "Back" work, we'll implement a public method called by MainWindow's BackRequested if Frame.CanGoBack is false?
        // OR simply: If search is active, we provide a "Close Search" button in the header (which we haven't added yet).
        // Let's add specific logic to "SidebarToggle" to act as Back when in search?
        // Or better: Re-enable SidebarToggle as "Close Search" when _isSearchActive.
        
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
