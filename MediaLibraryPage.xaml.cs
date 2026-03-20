using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using ModernIPTVPlayer.Controls;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Stremio;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaLibraryPage : Page
    {
        private enum ContentSource { IPTV, Stremio }
        private ContentSource _currentSource = ContentSource.Stremio;
        private MediaType _mediaType = MediaType.Movie;
        public MediaType MediaType => _mediaType; // Expose for MainWindow logic

        private LoginParams? _loginInfo;
        private HttpClient _httpClient;
        
        // Data Store
        private List<LiveCategory> _iptvCategories = new();
        private List<IMediaStream> _allIptvItems = new();

        private (Windows.UI.Color Primary, Windows.UI.Color Secondary)? _heroColors;
        private ExpandedCardOverlayController? _stremioExpandedCardOverlay;
        
        // Composition
        private Compositor? _compositor;

        public MediaLibraryPage()
        {
            try
            {
                this.InitializeComponent();
                this.NavigationCacheMode = NavigationCacheMode.Enabled;
                _httpClient = HttpHelper.Client;
                _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

                WireEvents();
                AppLogger.Info($"[MediaLibraryPage] Initialized");
            }
            catch (Exception ex)
            {
                AppLogger.Critical($"[MediaLibraryPage] Constructor Error", ex);
            }
        }

        private void WireEvents()
        {
            // IPTV Grid Events
            MediaGrid.ItemClicked += (s, e) => NavigationService.NavigateToDetails(Frame, e, e.SourceElement);
            MediaGrid.PlayAction += (s, e) => NavigationService.NavigateToDetailsDirect(Frame, e);
            MediaGrid.DetailsAction += (s, e) => NavigationService.NavigateToDetails(Frame, e, e.SourceElement);
            MediaGrid.ColorExtracted += (s, colors) => BackdropControl.TransitionTo(colors.Primary, colors.Secondary);
            MediaGrid.HoverEnded += (s, card) => 
            {
                 _ = CloseExpandedCardInternalAsync();
                 if (_heroColors.HasValue) BackdropControl.TransitionTo(_heroColors.Value.Primary, _heroColors.Value.Secondary);
            };

            // Stremio Control Events
            StremioControl.ItemClicked += (s, e) => NavigationService.NavigateToDetails(Frame, new MediaNavigationArgs(e.Stream, preloadedImage: (e.SourceElement is PosterCard pc) ? pc.ImageElement.Source : null, preloadedLogo: e.PreloadedLogo), e.SourceElement);
            StremioControl.BackdropColorChanged += (s, colors) => 
            {
                _heroColors = colors;
                BackdropControl.TransitionTo(colors.Primary, colors.Secondary);
            };
            StremioControl.ViewChanged += (s, e) => 
            {
                BackdropControl.SetVerticalShift(StremioControl.MainScrollViewer.VerticalOffset);
                if (_stremioExpandedCardOverlay?.IsCardVisible == true) _ = _stremioExpandedCardOverlay.CloseExpandedCardAsync();
            };

            // Overlay Controller
            _stremioExpandedCardOverlay = new ExpandedCardOverlayController(this, OverlayCanvas, ActiveExpandedCard, CinemaScrim, StremioControl.MainScrollViewer);
            StremioControl.CardHoverStarted += (s, card) => _stremioExpandedCardOverlay.OnHoverStarted(card);
            StremioControl.CardHoverEnded += async (s, card) => await _stremioExpandedCardOverlay.CloseExpandedCardAsync(card);

            // Spotlight Search
            SpotlightSearch.ItemClicked += (s, item) => NavigationService.NavigateToDetailsDirect(Frame, item);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            
            try
            {
                if (e.Parameter is MediaLibraryArgs args)
                {
                    if (_mediaType != args.Type)
                    {
                        _mediaType = args.Type;
                        ClearData();
                        SidebarTitle.Text = _mediaType == MediaType.Movie ? "FİLMLER" : "DİZİLER";
                    }
                }

                _loginInfo = App.CurrentLogin;
                UpdateLayoutForMode();

                if (_currentSource == ContentSource.IPTV && _iptvCategories.Count == 0)
                {
                    await LoadIptvDataAsync();
                }
                else if (_currentSource == ContentSource.Stremio && !StremioControl.HasContent)
                {
                    await StremioControl.LoadDiscoveryAsync(_mediaType == MediaType.Movie ? "movie" : "series");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[MediaLibraryPage] OnNavigatedTo Error", ex);
            }
        }

        public async void SwitchMediaType(MediaType newType)
        {
            if (_mediaType == newType) return;
            
            AppLogger.Info($"[MediaLibraryPage] Switching MediaType to {newType}");
            _mediaType = newType;
            
            ClearData();
            SidebarTitle.Text = _mediaType == MediaType.Movie ? "FİLMLER" : "DİZİLER";
            
            UpdateLayoutForMode();

            if (_currentSource == ContentSource.IPTV)
            {
                await LoadIptvDataAsync();
            }
            else
            {
                await StremioControl.LoadDiscoveryAsync(_mediaType == MediaType.Movie ? "movie" : "series");
            }
        }

        private void ClearData()
        {
            _iptvCategories.Clear();
            _allIptvItems.Clear();
            CategoryList.ItemsSource = null;
            MediaGrid.ItemsSource = null;
            StremioControl.Clear();
            AppLogger.Info($"[MediaLibraryPage] Data Cleared for {_mediaType}");
        }

        // ==========================================
        // LAYOUT & SOURCE SWITCHING
        // ==========================================
        private void UpdateLayoutForMode()
        {
            bool hasIptv = _loginInfo != null && !string.IsNullOrEmpty(_loginInfo.Host);
            SourceSwitcherPanel.Visibility = hasIptv ? Visibility.Visible : Visibility.Collapsed;
            SidebarToggle.Visibility = (_currentSource == ContentSource.IPTV && hasIptv) ? Visibility.Visible : Visibility.Collapsed;

            if (_currentSource == ContentSource.IPTV && hasIptv)
            {
                MainSplitView.DisplayMode = SplitViewDisplayMode.Inline;
                MediaGrid.Visibility = Visibility.Visible;
                StremioControl.Visibility = Visibility.Collapsed;
            }
            else
            {
                MainSplitView.IsPaneOpen = false;
                MainSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
                MediaGrid.Visibility = Visibility.Collapsed;
                StremioControl.Visibility = Visibility.Visible;
            }
        }

        private async void Source_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag)
            {
                var newSource = tag == "Stremio" ? ContentSource.Stremio : ContentSource.IPTV;
                if (_currentSource == newSource) return;

                _currentSource = newSource;
                AppLogger.Info($"[MediaLibraryPage] Source Switching to {_currentSource}");

                UpdateLayoutForMode();
                
                if (_currentSource == ContentSource.IPTV)
                {
                    await LoadIptvDataAsync();
                }
                else
                {
                    if (!StremioControl.HasContent)
                        await StremioControl.LoadDiscoveryAsync(_mediaType == MediaType.Movie ? "movie" : "series");
                }
            }
        }

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;

        // ==========================================
        // IPTV LOADING LOGIC (Optimized)
        // ==========================================
        private async Task LoadIptvDataAsync()
        {
            AppLogger.Info($"[MediaLibraryPage] Loading IPTV Data ({_mediaType})");
            MediaGrid.IsLoading = true;
            try
            {
                if (_iptvCategories.Count > 0)
                {
                    DisplayCategories(_iptvCategories);
                    return;
                }

                string playlistId = AppSettings.LastPlaylistId?.ToString() ?? "default";
                string typeKey = _mediaType == MediaType.Movie ? "vod" : "series";

                // 1. Categories
                _iptvCategories = await ContentCacheService.Instance.LoadCacheAsync<LiveCategory>(playlistId, $"{typeKey}_cats") ?? new();
                if (_iptvCategories.Count == 0)
                {
                    string api = $"{_loginInfo.Host}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_{typeKey}_categories";
                    string json = await _httpClient.GetStringAsync(api);
                    _iptvCategories = JsonSerializer.Deserialize<List<LiveCategory>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                    _ = ContentCacheService.Instance.SaveCacheAsync(playlistId, $"{typeKey}_cats", _iptvCategories);
                }

                // 2. Streams
                var cachedStreams = await ContentCacheService.Instance.LoadCacheAsync<JsonElement>(playlistId, $"{typeKey}_streams");
                if (cachedStreams != null && cachedStreams.Count > 0)
                {
                    _allIptvItems = ParseIptvStreams(cachedStreams[0]);
                }
                else
                {
                    string action = _mediaType == MediaType.Movie ? "get_vod_streams" : "get_series";
                    string api = $"{_loginInfo.Host}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action={action}";
                    string json = await _httpClient.GetStringAsync(api);
                    var doc = JsonDocument.Parse(json);
                    _allIptvItems = ParseIptvStreams(doc.RootElement);
                    _ = ContentCacheService.Instance.SaveCacheAsync<JsonElement>(playlistId, $"{typeKey}_streams", new List<JsonElement> { doc.RootElement });
                }

                AppLogger.Info($"[MediaLibraryPage] IPTV Load Done. Cats: {_iptvCategories.Count}, Items: {_allIptvItems.Count}");
                DisplayCategories(_iptvCategories);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[MediaLibraryPage] LoadIptvDataAsync Error", ex);
            }
            finally
            {
                MediaGrid.IsLoading = false;
            }
        }

        private List<IMediaStream> ParseIptvStreams(JsonElement root)
        {
            var list = new List<IMediaStream>();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
            
            if (_mediaType == MediaType.Movie)
            {
                var movies = JsonSerializer.Deserialize<List<LiveStream>>(root.GetRawText(), options);
                if (movies != null)
                {
                    foreach(var m in movies) {
                         m.StreamUrl = $"{_loginInfo.Host}/movie/{_loginInfo.Username}/{_loginInfo.Password}/{m.StreamId}.{(string.IsNullOrEmpty(m.ContainerExtension) ? "mp4" : m.ContainerExtension)}";
                         list.Add(m);
                    }
                }
            }
            else
            {
                var series = JsonSerializer.Deserialize<List<SeriesStream>>(root.GetRawText(), options);
                if (series != null) list.AddRange(series);
            }
            return list;
        }

        private void DisplayCategories(List<LiveCategory> categories)
        {
            CategoryList.ItemsSource = categories;
            
            string? lastId = _mediaType == MediaType.Movie ? PageStateProvider.LastMovieCategoryId : PageStateProvider.LastSeriesCategoryId;
            var toSelect = categories.FirstOrDefault(c => c.CategoryId == lastId) ?? categories.FirstOrDefault();

            if (toSelect != null)
            {
                CategoryList.SelectedItem = toSelect;
                _ = LoadCategoryItemsAsync(toSelect);
            }
        }

        private async Task LoadCategoryItemsAsync(LiveCategory category)
        {
            MediaGrid.IsLoading = true;
            try
            {
                var filtered = await Task.Run(() => _allIptvItems.Where(i => i is LiveStream ls ? ls.CategoryId == category.CategoryId : (i as SeriesStream)?.CategoryId == category.CategoryId).ToList());
                
                // STABILITY FIX: Single assignment to avoid GridView virtualization storms
                MediaGrid.ItemsSource = filtered;
                AppLogger.Info($"[MediaLibraryPage] Displaying {filtered.Count} items for Category {category.CategoryName}");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[MediaLibraryPage] LoadCategoryItemsAsync Error", ex);
            }
            finally
            {
                MediaGrid.IsLoading = false;
            }
        }

        // ==========================================
        // SIDEBAR & SEARCH
        // ==========================================
        private void CategorySearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = CategorySearchBox.Text.Trim().ToLower();
            CategoryList.ItemsSource = string.IsNullOrEmpty(query) ? _iptvCategories : _iptvCategories.Where(c => c.CategoryName.ToLower().Contains(query)).ToList();
        }

        private async void CategoryListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveCategory cat)
            {
                if (_mediaType == MediaType.Movie) PageStateProvider.LastMovieCategoryId = cat.CategoryId;
                else PageStateProvider.LastSeriesCategoryId = cat.CategoryId;

                await LoadCategoryItemsAsync(cat);
                UpdateSelectionPill();
            }
        }

        private void UpdateSelectionPill()
        {
            var container = CategoryList.ContainerFromItem(CategoryList.SelectedItem) as ListViewItem;
            if (container == null || _compositor == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(CategorySelectionPill);
            var containerVisual = ElementCompositionPreview.GetElementVisual(container);
            
            // Transform container position to list coordinates
            var transform = container.TransformToVisual(CategoryList);
            var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

            if (CategorySelectionPill.Opacity == 0)
            {
                CategorySelectionPill.Opacity = 1;
                visual.Offset = new Vector3(0, (float)point.Y + 10, 0); // Offset for centering
            }
            else
            {
                var anim = _compositor.CreateScalarKeyFrameAnimation();
                anim.InsertKeyFrame(1.0f, (float)point.Y + 10);
                anim.Duration = TimeSpan.FromMilliseconds(300);
                visual.StartAnimation("Offset.Y", anim);
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e) => SpotlightSearch.Show();

        public bool HandleBackRequest()
        {
            if (SpotlightSearch.Visibility == Visibility.Visible) { SpotlightSearch.Hide(); return true; }
            if (GenreOverlay.Visibility == Visibility.Visible) { GenreOverlay.Hide(); return true; }
            return false;
        }

        private async Task CloseExpandedCardInternalAsync()
        {
            if (_stremioExpandedCardOverlay != null) await _stremioExpandedCardOverlay.CloseExpandedCardAsync();
        }

        private void GenreFilterButton_Click(object sender, RoutedEventArgs e) => GenreOverlay.Show(_mediaType == MediaType.Movie ? "movie" : "series");
    }
}
