using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections;
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
        private IReadOnlyList<LiveCategory> _iptvCategories = new List<LiveCategory>();
        private IReadOnlyList<IMediaStream> _allIptvItems = new List<IMediaStream>();
        private System.Threading.CancellationTokenSource? _navigationCts;
        private Dictionary<MediaType, IReadOnlyList<LiveCategory>> _categoryCache = new();
        private Dictionary<MediaType, IReadOnlyList<IMediaStream>> _itemsCache = new();
        private Dictionary<string, IReadOnlyList<IMediaStream>> _itemsByNormalizedCategoryId = new(StringComparer.Ordinal);
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
                
                // [ENGINEERED] Initialize Composition Visuals
                _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
                SetupCompositionTransitions();

                this.SizeChanged += (s, e) => 
                {
                    ViewportClip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
                    SyncHeaderSpacerHeight();
                };

                PageHeader.SizeChanged += (s, e) => SyncHeaderSpacerHeight();
                this.Loaded += (s, e) => SyncHeaderSpacerHeight();

                WireEvents();
                AppLogger.Info($"[MediaLibraryPage] Initialized");
            }
            catch (Exception ex)
            {
                AppLogger.Critical($"[MediaLibraryPage] Constructor Error", ex);
            }
        }

        private Visual _iptvVisual;
        private Visual _stremioVisual;

        private void SyncHeaderSpacerHeight()
        {
            if (HeaderSpacer is null || PageHeader is null)
                return;

            // Keep content start aligned below the floating header/pill region.
            HeaderSpacer.Height = PageHeader.ActualHeight;
        }

        private void SetupCompositionTransitions()
        {
            _iptvVisual = ElementCompositionPreview.GetElementVisual(IptvViewportHost);
            _stremioVisual = ElementCompositionPreview.GetElementVisual(StremioViewportHost);

            // Create an Implicit Animation Collection for the 'Offset' property
            // This means whenever we change Visual.Offset, it handles the slide automatically
            var implicitAnimations = _compositor.CreateImplicitAnimationCollection();
            
            // Define the Slide Animation
            var slideAnimation = _compositor.CreateVector3KeyFrameAnimation();
            slideAnimation.Target = "Offset";
            slideAnimation.Duration = TimeSpan.FromMilliseconds(500);
            
            // [ENGINEERED] Premium Quintic-Out Bezier (Snap + Glide)
            var quinticBezier = _compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f), 
                new Vector2(0.2f, 1.0f));

            // Apply easing directly to the final keyframe
            slideAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", quinticBezier);
            
            slideAnimation.IterationBehavior = AnimationIterationBehavior.Count;
            slideAnimation.IterationCount = 1;

            // Set the slide as the trigger for Offset changes
            implicitAnimations["Offset"] = slideAnimation;
            
            // Assign to both visuals
            _iptvVisual.ImplicitAnimations = implicitAnimations;
            _stremioVisual.ImplicitAnimations = implicitAnimations;

            // Initial Position: Stremio is active (index 1), so IPTV starts at left offset
            this.Loaded += (s, e) => 
            {
                float width = (float)ViewportGrid.ActualWidth;
                if (width == 0) width = 1920; // Fallback

                if (_currentSource == ContentSource.Stremio)
                {
                    _iptvVisual.Offset = new Vector3(-width, 0, 0);
                    _stremioVisual.Offset = new Vector3(0, 0, 0);
                    IptvViewportHost.Visibility = Visibility.Visible;
                    StremioViewportHost.Visibility = Visibility.Visible;
                    StremioControl.SetSourceActive(true);
                }
                else
                {
                    _iptvVisual.Offset = new Vector3(0, 0, 0);
                    _stremioVisual.Offset = new Vector3(width, 0, 0);
                    IptvViewportHost.Visibility = Visibility.Visible;
                    StremioViewportHost.Visibility = Visibility.Visible;
                    StremioControl.SetSourceActive(false);
                }
            };
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

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            
            // [PHASE 2.4] Resource Disposal: Kill the hydration anchors
            _navigationCts?.Cancel();
            _navigationCts = null;

            // Clear the local references to help GC
            _allIptvItems = new List<IMediaStream>();
            _itemsByNormalizedCategoryId = new Dictionary<string, IReadOnlyList<IMediaStream>>(StringComparer.Ordinal);
            
            // Explicitly clear memory caches for this page instance
            _categoryCache.Clear();
            _itemsCache.Clear();

            // Notify MediaLibraryStateService to purge if needed
            MediaLibraryStateService.Instance.UpdateScope(null);

            // [PHASE 2.4] Manual GC Hint: Flush transient UI and Buffer allocations
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();

            AppLogger.Info("[MediaLibraryPage] Navigated From - Resources Purged.");
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
            _itemsByNormalizedCategoryId = new Dictionary<string, IReadOnlyList<IMediaStream>>(StringComparer.Ordinal);
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

            float width = (float)ViewportGrid.ActualWidth;
            if (width == 0) width = 1920; // Fallback

            if (_currentSource == ContentSource.IPTV && hasIptv)
            {
                MainSplitView.DisplayMode = SplitViewDisplayMode.Inline;
                
                // [ENGINEERED] Horizontal Viewport Slide
                _iptvVisual.Offset = new Vector3(0, 0, 0);
                _stremioVisual.Offset = new Vector3(width, 0, 0);
                
                // Keep Sidebar Toggle only for IPTV
                SidebarToggle.Visibility = Visibility.Visible;

                IptvViewportHost.Visibility = Visibility.Visible;
                StremioViewportHost.Visibility = Visibility.Visible;
                MediaGrid.IsHitTestVisible = true;
                StremioControl.IsHitTestVisible = false;
                StremioControl.SetSourceActive(false);
            }
            else
            {
                MainSplitView.IsPaneOpen = false;
                MainSplitView.DisplayMode = SplitViewDisplayMode.Overlay;

                // [ENGINEERED] Horizontal Viewport Slide
                _iptvVisual.Offset = new Vector3(-width, 0, 0);
                _stremioVisual.Offset = new Vector3(0, 0, 0);
                
                SidebarToggle.Visibility = Visibility.Collapsed;

                IptvViewportHost.Visibility = Visibility.Visible;
                StremioViewportHost.Visibility = Visibility.Visible;
                MediaGrid.IsHitTestVisible = false;
                StremioControl.IsHitTestVisible = true;
                StremioControl.SetSourceActive(true);
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
            string playlistId = AppSettings.LastPlaylistId?.ToString() ?? "default";

            AppLogger.Info($"[MediaLibraryPage] Loading IPTV Data ({contextType})");

            // 0. MEMORY CACHE CHECK
            if (_categoryCache.TryGetValue(contextType, out var memCats) && 
                _itemsCache.TryGetValue(contextType, out var memItems) && 
                memCats.Count > 0)
            {
                if (token.IsCancellationRequested || _mediaType != contextType) return;

                AppLogger.Info($"[MediaLibraryPage] Restoring from Memory Cache ({contextType})");
                _iptvCategories = memCats;
                _allIptvItems = memItems;
                await UpdateIptvCollectionScopeAsync(playlistId, contextType);

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
            try
            {
                string typeKey = _mediaType == MediaType.Movie ? "vod" : "series";

                // EXTREME PERFORMANCE: Move all CPU-bound work to background thread
                await Task.Run(async () =>
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };

                    // 1. Categories
                    var cats = await ContentCacheService.Instance.LoadCacheAsync<LiveCategory>(playlistId, $"{typeKey}_categories") ?? new List<LiveCategory>();
                    
                    if (cats.Count == 0 && !token.IsCancellationRequested)
                    {
                        AppLogger.Warn($"[MediaLibraryPage] Cache Empty. Fetching {typeKey} categories from network...");
                        string api = $"{_loginInfo.Host}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_{typeKey}_categories";
                        string json = await _httpClient.GetStringAsync(api, token);
                        cats = HttpHelper.TryDeserializeList(json, Services.Json.AppJsonContext.Default.ListLiveCategory);
                        await ContentCacheService.Instance.SaveCacheAsync(playlistId, $"{typeKey}_categories", cats);
                    }

                    if (token.IsCancellationRequested) return;

                    // 2. Streams
                    IReadOnlyList<IMediaStream> items = new List<IMediaStream>();
                    if (contextType == MediaType.Movie)
                    {
                        var movies = await ContentCacheService.Instance.LoadCacheAsync<VodStream>(playlistId, "vod");
                        
                        if ((movies == null || movies.Count == 0) && !token.IsCancellationRequested)
                        {
                            AppLogger.Warn("[MediaLibraryPage] Cache Empty. Fetching VOD streams from network...");
                            string api = $"{_loginInfo.Host}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_vod_streams";
                            MemoryTelemetryService.LogCheckpoint("MediaLibrary.VOD.fetch.start", $"playlist={playlistId}");
                            using var response = await _httpClient.GetAsync(api, HttpCompletionOption.ResponseHeadersRead, token);
                            response.EnsureSuccessStatusCode();
                            await using var stream = await response.Content.ReadAsStreamAsync(token);
                            await ContentCacheService.Instance.SaveVodStreamsBinaryFromJsonStreamAsync(playlistId, stream, token);
                            movies = await ContentCacheService.Instance.LoadCacheAsync<VodStream>(playlistId, "vod");
                            MemoryTelemetryService.LogCheckpoint("MediaLibrary.VOD.binary.ready", "streamed=true");
                        }

                        if (movies != null)
                        {
                            // [REMOVED] foreach (var m in movies) { ... } 
                            // This loop was causing mass hydration of 200k items.
                            // VodStream now calculates StreamUrl lazily.
                            items = movies;
                        }
                    }
                    else
                    {
                        var series = await ContentCacheService.Instance.LoadCacheAsync<SeriesStream>(playlistId, "series");
                        
                        if ((series == null || series.Count == 0) && !token.IsCancellationRequested)
                        {
                            AppLogger.Warn("[MediaLibraryPage] Cache Empty. Fetching Series from network...");
                            string api = $"{_loginInfo.Host}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_series";
                            MemoryTelemetryService.LogCheckpoint("MediaLibrary.Series.fetch.start", $"playlist={playlistId}");
                            using var response = await _httpClient.GetAsync(api, HttpCompletionOption.ResponseHeadersRead, token);
                            response.EnsureSuccessStatusCode();
                            await using var stream = await response.Content.ReadAsStreamAsync(token);
                            await ContentCacheService.Instance.SaveSeriesStreamsBinaryFromJsonStreamAsync(playlistId, stream, token);
                            series = await ContentCacheService.Instance.LoadCacheAsync<SeriesStream>(playlistId, "series");
                            MemoryTelemetryService.LogCheckpoint("MediaLibrary.Series.binary.ready", "streamed=true");
                        }

                        if (series != null) items = series;
                    }

                    if (token.IsCancellationRequested) return;

                    // Update local state
                    _iptvCategories = cats;
                    _allIptvItems = items;

                    // Update memory cache
                    _categoryCache[contextType] = cats;
                    _itemsCache[contextType] = items;

                    AppLogger.Info($"[MediaLibraryPage] Background Load Done. Cats: {cats.Count}, Items: {items.Count}");
                });

                if (token.IsCancellationRequested || _mediaType != contextType) return;

                // Return to UI thread for binding
                await UpdateIptvCollectionScopeAsync(playlistId, contextType);
                DisplayCategories(_iptvCategories, contextType, token: token);
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) AppLogger.Error($"[MediaLibraryPage] LoadIptvDataAsync Error", ex);
            }
            finally
            {
                if (!token.IsCancellationRequested) MediaGrid.IsLoading = false;
            }
        }



        private void DisplayCategories(IReadOnlyList<LiveCategory> categories, MediaType contextType, bool skipLoadingRing = false, System.Threading.CancellationToken token = default)
        {
            if (token.IsCancellationRequested || _mediaType != contextType) return;

            // [ENGINEERED] Skip if already set to the same reference
            if (CategoryList.ItemsSource != categories)
            {
                CategoryList.ItemsSource = categories;
            }
            
            string? lastId = contextType == MediaType.Movie ? PageStateProvider.LastMovieCategoryId : PageStateProvider.LastSeriesCategoryId;
            string? lastNorm = string.IsNullOrEmpty(lastId) ? null : NormalizeCategoryId(lastId);
            var toSelect = lastNorm != null
                ? categories.FirstOrDefault(c => NormalizeCategoryId(c.CategoryId) == lastNorm)
                : null;
            toSelect ??= categories.FirstOrDefault(c => c.CategoryId == lastId) ?? categories.FirstOrDefault();

            if (toSelect != null)
            {
                CategoryList.SelectedItem = toSelect;
                _ = LoadCategoryItemsAsync(toSelect, contextType, skipLoadingRing, token);
            }
        }

        private async Task LoadCategoryItemsAsync(LiveCategory category, MediaType contextType, bool skipLoadingRing = false, System.Threading.CancellationToken token = default)
        {
            if (token.IsCancellationRequested || _mediaType != contextType) return;
            bool cacheHit = MediaLibraryStateService.Instance.TryGetCollection(contextType, category.CategoryId, out _);
            string selectedRaw = category.CategoryId ?? string.Empty;
            string selectedNormalized = NormalizeCategoryId(selectedRaw);

            LogCategoryDiagnostics(category, selectedRaw, selectedNormalized);

            if (_itemsByNormalizedCategoryId.Count == 0 && _allIptvItems.Count > 0)
                RebuildCategoryIndexMap();

            var collection = MediaLibraryStateService.Instance.GetOrCreateCollection(contextType, category.CategoryId, () =>
            {
                if (_itemsByNormalizedCategoryId.TryGetValue(selectedNormalized, out var bucket))
                    return bucket;
                return new List<IMediaStream>();
            });

            // Only set IsLoading if we don't already have the items bound
            if (MediaGrid.ItemsSource != collection && !skipLoadingRing) MediaGrid.IsLoading = true;
            
            try
            {
                if (token.IsCancellationRequested || _mediaType != contextType) return;

                // If identical reference, GridView will skip layout destruction.
                MediaGrid.ItemsSource = collection;
                AppLogger.Info($"[MediaLibraryPage] Displaying {((ICollection)collection).Count} items for Category {category.CategoryName} (RawCatId='{selectedRaw}', NormalizedCatId='{selectedNormalized}', Cache {(cacheHit ? "Hit" : "Miss")})");
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

        private async Task UpdateIptvCollectionScopeAsync(string playlistId, MediaType contextType)
        {
            // 1. O(1) Fingerprint from Virtual List Header
            long datasetFp = 0;
            if (_allIptvItems is Helpers.VirtualVodList vvl) datasetFp = vvl.Fingerprint;
            else if (_allIptvItems is Helpers.VirtualSeriesList vsl) datasetFp = vsl.Fingerprint;

            // 2. High Performance Indexing (Already offloaded if called correctly, but we ensure it remains Task-safe)
            await Task.Run(() => RebuildCategoryIndexMap());
            
            string scopeKey = MediaLibraryStateService.BuildScopeKey(playlistId, contextType, _currentSource.ToString(), (ulong)datasetFp);
            MediaLibraryStateService.Instance.UpdateScope(scopeKey);
        }

        private void RebuildCategoryIndexMap()
        {
            var map = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<int>>(StringComparer.Ordinal);
            
            // ARCHITECTURAL FIX: Use Zero-Lock Parallel Scan (Index-only) to avoid object hydration
            if (_allIptvItems is Helpers.VirtualVodList vvl)
            {
                vvl.ParallelScanInto(map);
                _itemsByNormalizedCategoryId = map.ToDictionary(
                    k => k.Key, 
                    v => (IReadOnlyList<IMediaStream>)new Helpers.VirtualStreamSubList(vvl, v.Value)
                );
            }
            else if (_allIptvItems is Helpers.VirtualSeriesList vsl)
            {
                vsl.ParallelScanInto(map);
                _itemsByNormalizedCategoryId = map.ToDictionary(
                    k => k.Key, 
                    v => (IReadOnlyList<IMediaStream>)new Helpers.VirtualStreamSubList(vsl, v.Value)
                );
            }
            else
            {
                // Fallback for non-virtual lists
                var fallbackMap = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<IMediaStream>>(StringComparer.Ordinal);
                Parallel.ForEach(_allIptvItems, (stream) => 
                {
                    string norm = NormalizeCategoryId(GetStreamCategoryId(stream));
                    var bag = fallbackMap.GetOrAdd(norm, _ => new System.Collections.Concurrent.ConcurrentBag<IMediaStream>());
                    bag.Add(stream);
                });
                _itemsByNormalizedCategoryId = fallbackMap.ToDictionary(k => k.Key, v => v.Value.ToList() as IReadOnlyList<IMediaStream>);
            }
        }

        private static ulong ComputeDatasetFingerprint(IReadOnlyList<IMediaStream> items)
        {
            if (items == null || items.Count == 0) return 0;
            ulong h = 14695981039346656037UL;
            foreach (var s in items)
            {
                h ^= (ulong)(uint)s.Id;
                h *= 1099511628211UL;
                string? cid = GetStreamCategoryId(s);
                if (!string.IsNullOrEmpty(cid))
                {
                    h ^= (ulong)(uint)cid.GetHashCode(StringComparison.Ordinal);
                    h *= 1099511628211UL;
                }
            }
            h ^= (ulong)(uint)items.Count;
            return h;
        }

        private static string NormalizeCategoryId(string? categoryId)
        {
            if (string.IsNullOrWhiteSpace(categoryId)) return "0";
            string trimmed = categoryId.Trim();
            if (int.TryParse(trimmed, out int numeric)) return numeric.ToString();
            return trimmed;
        }

        private void LogCategoryDiagnostics(LiveCategory category, string selectedRaw, string selectedNormalized)
        {
            // Lightweight diagnostic: avoid full scan hydration
            int itemCount = (_itemsByNormalizedCategoryId != null && _itemsByNormalizedCategoryId.TryGetValue(selectedNormalized, out var list)) ? list.Count : 0;
            AppLogger.Info($"[Diag][MediaLibraryPage] Category: name='{category.CategoryName}' raw='{selectedRaw}' norm='{selectedNormalized}' items={itemCount}");
        }

        private static string? GetStreamCategoryId(IMediaStream stream)
        {
            if (stream is LiveStream ls) return ls.CategoryId;
            if (stream is SeriesStream ss) return ss.CategoryId;
            if (stream is VodStream vs) return vs.CategoryId;
            return null;
        }

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
