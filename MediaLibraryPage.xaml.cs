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
using ModernIPTVPlayer.Pages;

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
        private DateTime _indexingStartTime;
        
        // Data Store
        private List<LiveCategory> _iptvCategories = new();
        private List<IMediaStream> _allIptvItems = new();
        private System.Threading.CancellationTokenSource? _navigationCts;
        private Dictionary<MediaType, List<LiveCategory>> _categoryCache = new();
        private Dictionary<MediaType, List<IMediaStream>> _itemsCache = new();
        private Dictionary<MediaType, (Windows.UI.Color Primary, Windows.UI.Color Secondary)?> _heroColorsCache = new();

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
            MediaGrid.ColorExtracted += (s, colors) => 
            {
                _heroColorsCache[_mediaType] = colors;
                BackdropControl.TransitionTo(colors.Primary, colors.Secondary);
            };
            MediaGrid.HoverEnded += (s, card) => 
            {
                 _ = CloseExpandedCardInternalAsync();
                 if (_heroColors.HasValue) BackdropControl.TransitionTo(_heroColors.Value.Primary, _heroColors.Value.Secondary);
            };

            // Stremio Control Events
            StremioControl.ItemClicked += (s, e) => NavigationService.NavigateToDetails(Frame, new MediaNavigationArgs(e.Stream, preloadedImage: (e.SourceElement is PosterCard pc) ? pc.ImageElement.Source : null, preloadedLogo: e.PreloadedLogo), e.SourceElement);
            StremioControl.PlayAction += (s, stream) => NavigationService.NavigateToDetailsDirect(Frame, stream);
            StremioControl.BackdropColorChanged += (s, colors) => 
            {
                _heroColors = colors;
                _heroColorsCache[_mediaType] = colors;
                BackdropControl.TransitionTo(colors.Primary, colors.Secondary);
            };
            StremioControl.ViewChanged += (s, e) => 
            {
                BackdropControl.SetVerticalShift(StremioControl.MainScrollViewer.VerticalOffset);
                if (_stremioExpandedCardOverlay?.IsCardVisible == true) _ = _stremioExpandedCardOverlay.CloseExpandedCardAsync();
            };
            StremioControl.RowScrollStarted += (s, e) => 
            { 
                if (_stremioExpandedCardOverlay != null) 
                {
                    _stremioExpandedCardOverlay.IsManipulationInProgress = true; 
                    _stremioExpandedCardOverlay.UpdatePositions();
                }
            };

            StremioControl.HeaderClicked += (s, vm) => 
            {
                if (string.IsNullOrEmpty(vm.SourceUrl)) return;
                var args = new SearchArgs 
                { 
                    GenreArgs = new ModernIPTVPlayer.Models.Stremio.GenreSelectionArgs 
                    {
                        AddonId = vm.SourceUrl,
                        CatalogId = vm.CatalogId,
                        CatalogType = vm.CatalogType,
                        DisplayName = vm.CatalogName
                    },
                    Type = vm.CatalogType,
                    ParentContext = vm.CatalogName
                };
                Frame.Navigate(typeof(SearchResultsPage), args);
            };
            StremioControl.RowScrollEnded += (s, e) => 
            { 
                if (_stremioExpandedCardOverlay != null) _stremioExpandedCardOverlay.IsManipulationInProgress = false; 
            };

            // Overlay Controller
            _stremioExpandedCardOverlay = new ExpandedCardOverlayController(this, OverlayCanvas, ActiveExpandedCard, CinemaScrim, StremioControl.MainScrollViewer);
            _stremioExpandedCardOverlay.PlayRequested += (s, stream) => 
            {
                var args = new MediaNavigationArgs(stream) { AutoResume = true };
                NavigationService.NavigateToDetailsDirect(Frame, args);
            };

            _stremioExpandedCardOverlay.DetailsRequested += (s, e) => NavigationService.NavigateToDetails(Frame, new MediaNavigationArgs(e.Stream, preloadedImage: ActiveExpandedCard.BannerImage.Source, tmdbInfo: e.Tmdb), null);
            
            StremioControl.CardHoverStarted += (s, card) => _stremioExpandedCardOverlay.OnHoverStarted(card);
            StremioControl.CardHoverEnded += async (s, card) => await _stremioExpandedCardOverlay.CloseExpandedCardAsync(card);

            // Spotlight Search
            SpotlightSearch.ItemClicked += (s, item) => NavigationService.NavigateToDetailsDirect(Frame, item);
            SpotlightSearch.SeeAllClicked += (s, args) => Frame.Navigate(typeof(SearchResultsPage), args);

            // Indexing Status
            ContentCacheService.Instance.IndexingStatusChanged += (s, isIndexing) => 
            {
                DispatcherQueue.TryEnqueue(() => UpdateIndexingProgressUI(isIndexing));
            };
            
            // Initial check
            if (ContentCacheService.Instance.IsIndexing) UpdateIndexingProgressUI(true);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            
            try
            {
                MediaType targetType = PageStateProvider.LastMediaType;

                if (e.Parameter is MediaLibraryArgs args)
                {
                    targetType = args.Type;
                }
                
                if (_mediaType != targetType)
                {
                    _mediaType = targetType;
                    ClearData();
                    SidebarTitle.Text = _mediaType == MediaType.Movie ? "FİLMLER" : "DİZİLER";
                }
                
                // Sync back to state provider
                PageStateProvider.LastMediaType = _mediaType;

                _loginInfo = App.CurrentLogin;
                UpdateLayoutForMode();

                _navigationCts?.Cancel();
                _navigationCts = new System.Threading.CancellationTokenSource();
                var token = _navigationCts.Token;

                if (_currentSource == ContentSource.IPTV && _iptvCategories.Count == 0)
                {
                    await LoadIptvDataAsync(token);
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
            PageStateProvider.LastMediaType = newType;
            
            ClearData();
            SidebarTitle.Text = _mediaType == MediaType.Movie ? "FİLMLER" : "DİZİLER";
            
            UpdateLayoutForMode();

            _navigationCts?.Cancel();
            _navigationCts = new System.Threading.CancellationTokenSource();
            var token = _navigationCts.Token;

            if (_currentSource == ContentSource.IPTV)
            {
                _ = LoadIptvDataAsync(token);
            }
            else
            {
                _ = StremioControl.LoadDiscoveryAsync(_mediaType == MediaType.Movie ? "movie" : "series");
            }
        }

        private void ClearData()
        {
            // [FIX] Re-assign to new lists instead of calling .Clear()
            // to avoid clearing the same list instance that might be stored in the memory cache.
            _iptvCategories = new List<LiveCategory>();
            _allIptvItems = new List<IMediaStream>();
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
                
                _navigationCts?.Cancel();
                _navigationCts = new System.Threading.CancellationTokenSource();
                var token = _navigationCts.Token;

                if (_currentSource == ContentSource.IPTV)
                {
                    await LoadIptvDataAsync(token);
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
        private async Task LoadIptvDataAsync(System.Threading.CancellationToken token = default)
        {
            var contextType = _mediaType;
            var contextSource = _currentSource;

            AppLogger.Info($"[MediaLibraryPage] Loading IPTV Data ({contextType})");

            // 0. MEMORY CACHE CHECK (Instant Switch)
            if (_categoryCache.TryGetValue(contextType, out var memCats) && 
                _itemsCache.TryGetValue(contextType, out var memItems) && 
                memCats.Count > 0)
            {
                // Yield to ensure the UI thread can finish the navigation transition (sidebar anim, etc.)
                // before we potentially block it with heavy ItemsSource updates.
                await Task.Yield();
                if (token.IsCancellationRequested || _mediaType != contextType) return;

                AppLogger.Info($"[MediaLibraryPage] Restoring from Memory Cache ({contextType})");
                _iptvCategories = memCats;
                _allIptvItems = memItems;

                // [RESTORE COLOR]
                if (_heroColorsCache.TryGetValue(contextType, out var cachedColors) && cachedColors.HasValue)
                {
                    _heroColors = cachedColors;
                    BackdropControl.TransitionTo(cachedColors.Value.Primary, cachedColors.Value.Secondary);
                }

                DisplayCategories(_iptvCategories, contextType, skipLoadingRing: true, token: token);
                return;
            }

            if (token.IsCancellationRequested || _mediaType != contextType) return;

            MediaGrid.IsLoading = true;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
            try
            {
                if (_iptvCategories.Count > 0)
                {
                    DisplayCategories(_iptvCategories, contextType);
                    return;
                }

                string playlistId = AppSettings.LastPlaylistId?.ToString() ?? "default";
                string typeKey = _mediaType == MediaType.Movie ? "vod" : "series";

                // 1. Categories
                _iptvCategories = await ContentCacheService.Instance.LoadCacheAsync<LiveCategory>(playlistId, $"{typeKey}_categories") ?? new();
                if (token.IsCancellationRequested || _mediaType != contextType) return;
                if (_iptvCategories.Count == 0)
                {
                    string api = $"{_loginInfo.Host}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_{typeKey}_categories";
                    string json = await _httpClient.GetStringAsync(api, token);
                    if (token.IsCancellationRequested || _mediaType != contextType) return;
                    _iptvCategories = HttpHelper.TryDeserializeList<LiveCategory>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    _ = ContentCacheService.Instance.SaveCacheAsync(playlistId, $"{typeKey}_categories", _iptvCategories);
                }

                // 2. Streams
                if (_mediaType == MediaType.Movie)
                {
                    var cached = await ContentCacheService.Instance.LoadCacheAsync<VodStream>(playlistId, "vod");
                    if (token.IsCancellationRequested || _mediaType != contextType) return;
                    if (cached != null && cached.Count > 0)
                    {
                        foreach(var m in cached)
                        {
                            if (string.IsNullOrEmpty(m.StreamUrl))
                                m.StreamUrl = $"{_loginInfo.Host}/movie/{_loginInfo.Username}/{_loginInfo.Password}/{m.StreamId}.{(string.IsNullOrEmpty(m.ContainerExtension) ? "mp4" : m.ContainerExtension)}";
                        }
                        _allIptvItems = cached.Cast<IMediaStream>().ToList();
                    }
                    else
                    {
                        string api = $"{_loginInfo.Host}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_vod_streams";
                        string json = await _httpClient.GetStringAsync(api, token);
                        if (token.IsCancellationRequested || _mediaType != contextType) return;
                        var movies = HttpHelper.TryDeserializeList<VodStream>(json, options);
                        foreach(var m in movies) 
                            m.StreamUrl = $"{_loginInfo.Host}/movie/{_loginInfo.Username}/{_loginInfo.Password}/{m.StreamId}.{(string.IsNullOrEmpty(m.ContainerExtension) ? "mp4" : m.ContainerExtension)}";
                        
                        _allIptvItems = movies.Cast<IMediaStream>().ToList();
                        _ = ContentCacheService.Instance.SaveCacheAsync(playlistId, "vod", movies);
                    }
                }
                else
                {
                    var cached = await ContentCacheService.Instance.LoadCacheAsync<SeriesStream>(playlistId, "series");
                    if (token.IsCancellationRequested || _mediaType != contextType) return;
                    if (cached != null && cached.Count > 0)
                    {
                        _allIptvItems = cached.Cast<IMediaStream>().ToList();
                    }
                    else
                    {
                        string api = $"{_loginInfo.Host}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_series";
                        string json = await _httpClient.GetStringAsync(api, token);
                        if (token.IsCancellationRequested || _mediaType != contextType) return;
                        var series = HttpHelper.TryDeserializeList<SeriesStream>(json, options);
                        _allIptvItems = series.Cast<IMediaStream>().ToList();
                        await ContentCacheService.Instance.SaveCacheAsync(playlistId, "series", series);
                        _ = ContentCacheService.Instance.RefreshIptvMatchIndexAsync(playlistId);
                    }
                }

                AppLogger.Info($"[MediaLibraryPage] IPTV Load Done. Cats: {_iptvCategories.Count}, Items: {_allIptvItems.Count}");
                
                // Save to Memory Cache
                _categoryCache[_mediaType] = _iptvCategories;
                _itemsCache[_mediaType] = _allIptvItems;

                DisplayCategories(_iptvCategories, contextType, token: token);
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    AppLogger.Info($"[MediaLibraryPage] LoadIptvDataAsync cancelled for {contextType}.");
                }
                else
                {
                    AppLogger.Error($"[MediaLibraryPage] LoadIptvDataAsync Error", ex);
                }
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    MediaGrid.IsLoading = false;
                }
            }
        }



        private void DisplayCategories(List<LiveCategory> categories, MediaType contextType, bool skipLoadingRing = false, System.Threading.CancellationToken token = default)
        {
            if (token.IsCancellationRequested || _mediaType != contextType) return;

            CategoryList.ItemsSource = categories;
            
            string? lastId = contextType == MediaType.Movie ? PageStateProvider.LastMovieCategoryId : PageStateProvider.LastSeriesCategoryId;
            var toSelect = categories.FirstOrDefault(c => c.CategoryId == lastId) ?? categories.FirstOrDefault();

            if (toSelect != null)
            {
                CategoryList.SelectedItem = toSelect;
                _ = LoadCategoryItemsAsync(toSelect, contextType, skipLoadingRing, token);
            }
        }

        private async Task LoadCategoryItemsAsync(LiveCategory category, MediaType contextType, bool skipLoadingRing = false, System.Threading.CancellationToken token = default)
        {
            if (token.IsCancellationRequested || _mediaType != contextType) return;
            if (!skipLoadingRing) MediaGrid.IsLoading = true;
            
            try
            {
                var filtered = await Task.Run(() => _allIptvItems.Where(i => 
                {
                    if (i is LiveStream ls) return ls.CategoryId == category.CategoryId;
                    if (i is SeriesStream ss) return ss.CategoryId == category.CategoryId;
                    if (i is VodStream vs) return vs.CategoryId == category.CategoryId;
                    return false;
                }).ToList());
                
                if (token.IsCancellationRequested || _mediaType != contextType) return;

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

                _navigationCts?.Cancel();
                _navigationCts = new System.Threading.CancellationTokenSource();

                await LoadCategoryItemsAsync(cat, _mediaType, false, _navigationCts.Token);
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

        private void UpdateIndexingProgressUI(bool isIndexing)
        {
            try
            {
                if (IndexingProgressPill == null) return;

                // [FIX] Enable Translation property early for Composition animations
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(IndexingProgressPill, true);

                if (isIndexing)
                {
                    _indexingStartTime = DateTime.Now;
                    IndexingProgressPill.Visibility = Visibility.Visible;
                }
                else
                {
                    var elapsed = DateTime.Now - _indexingStartTime;
                    if (elapsed.TotalSeconds < 2)
                    {
                        // Ensure at least 2 seconds visibility to avoid flicker
                        _ = Task.Delay(2000 - (int)elapsed.TotalMilliseconds).ContinueWith(_ =>
                        {
                            DispatcherQueue?.TryEnqueue(() => UpdateIndexingProgressUI(false));
                        });
                        return;
                    }
                }

                // 120FPS Composition Animation (Offloads from UI Thread)
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(IndexingProgressPill);
                var compositor = visual.Compositor;

                // Opacity Animation
                var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
                opacityAnim.InsertKeyFrame(1f, isIndexing ? 1f : 0f);
                opacityAnim.Duration = TimeSpan.FromMilliseconds(400);

                // Slide Animation (Translation)
                var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
                // [FIX] Slide in from relative right (20px -> 0px)
                offsetAnim.InsertKeyFrame(0f, isIndexing ? new System.Numerics.Vector3(20, 0, 0) : new System.Numerics.Vector3(0, 0, 0));
                offsetAnim.InsertKeyFrame(1f, isIndexing ? new System.Numerics.Vector3(0, 0, 0) : new System.Numerics.Vector3(20, 0, 0));
                offsetAnim.Duration = TimeSpan.FromMilliseconds(500);

                var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);

                visual.StartAnimation("Opacity", opacityAnim);
                // [FIX] Use Translation instead of Offset for better layout-independent animation
                visual.StartAnimation("Translation", offsetAnim);

                batch.Completed += (s, e) => {
                    if (!isIndexing) IndexingProgressPill.Visibility = Visibility.Collapsed;
                };
                batch.End();
            }
            catch (Exception ex)
            {
                AppLogger.Error("[MediaLibrary] UI Update Failed", ex);
            }
        }
    }
}
