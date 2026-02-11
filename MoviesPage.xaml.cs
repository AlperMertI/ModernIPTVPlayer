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
using ModernIPTVPlayer.Services.Stremio;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using Windows.Foundation;
using System.Numerics;
using Microsoft.UI.Xaml.Hosting;

namespace ModernIPTVPlayer
{
    public class CatalogRowViewModel
    {
        public string CatalogName { get; set; }
        public ObservableCollection<StremioMediaStream> Items { get; set; } = new();
    }
    public sealed partial class MoviesPage : Page
    {
        private enum ContentSource { IPTV, Stremio }
        private ContentSource _currentSource = ContentSource.IPTV;

        private LoginParams? _loginInfo;
        private HttpClient _httpClient;
        
        // Data Store
        private List<LiveCategory> _iptvCategories = new();
        
        private List<LiveStream> _allIptvMovies = new();

        private List<StremioMediaStream> _heroItems = new();
        private int _currentHeroIndex = 0;
        private (Windows.UI.Color Primary, Windows.UI.Color Secondary)? _heroColors;
        private bool _isDraggingRow = false;

        // Composition API: True pixel-level alpha masking
        private Microsoft.UI.Composition.SpriteVisual _heroVisual;
        private Microsoft.UI.Composition.CompositionSurfaceBrush _heroImageBrush;
        private Microsoft.UI.Composition.CompositionLinearGradientBrush _heroAlphaMask;
        private Microsoft.UI.Composition.CompositionMaskBrush _heroMaskBrush;

        // Auto-rotation
        private DispatcherTimer _heroAutoTimer;
        private bool _heroTransitioning = false;

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

            // Setup composition-based hero image with true alpha mask
            HeroImageHost.Loaded += (s, e) => SetupHeroCompositionMask();
        }

        private void SetupHeroCompositionMask()
        {
            var compositor = ElementCompositionPreview.GetElementVisual(HeroImageHost).Compositor;

            // 1. Image source brush
            _heroImageBrush = compositor.CreateSurfaceBrush();
            _heroImageBrush.Stretch = Microsoft.UI.Composition.CompositionStretch.UniformToFill;
            _heroImageBrush.HorizontalAlignmentRatio = 0.5f;
            _heroImageBrush.VerticalAlignmentRatio = 0.0f; // Top-aligned

            // 2. Alpha gradient mask
            _heroAlphaMask = compositor.CreateLinearGradientBrush();
            _heroAlphaMask.StartPoint = new Vector2(0, 0);
            _heroAlphaMask.EndPoint = new Vector2(0, 1);
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Windows.UI.Color.FromArgb(255, 255, 255, 255)));
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.55f, Windows.UI.Color.FromArgb(255, 255, 255, 255)));
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.75f, Windows.UI.Color.FromArgb(140, 255, 255, 255)));
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(0.9f, Windows.UI.Color.FromArgb(30, 255, 255, 255)));
            _heroAlphaMask.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Windows.UI.Color.FromArgb(0, 255, 255, 255)));

            // 3. Mask brush
            _heroMaskBrush = compositor.CreateMaskBrush();
            _heroMaskBrush.Source = _heroImageBrush;
            _heroMaskBrush.Mask = _heroAlphaMask;

            // 4. SpriteVisual
            _heroVisual = compositor.CreateSpriteVisual();
            _heroVisual.Brush = _heroMaskBrush;
            _heroVisual.Size = new Vector2((float)HeroImageHost.ActualWidth, (float)HeroImageHost.ActualHeight);

            // 5. Attach
            ElementCompositionPreview.SetElementChildVisual(HeroImageHost, _heroVisual);

            // 6. Size sync
            HeroImageHost.SizeChanged += (s, e) =>
            {
                if (_heroVisual != null)
                    _heroVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            };

            // 7. Ken Burns: animate the PARENT visual, NOT the SpriteVisual with MaskBrush
            //    Animating _heroVisual forces MaskBrush recomposite every frame (expensive)
            //    Animating the parent just moves the cached composited result (cheap)
            ApplyKenBurnsComposition(compositor);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

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

            // Initial Load (Default to IPTV if first time)
            if (_loginInfo != null && !string.IsNullOrEmpty(_loginInfo.Host))
            {
                if (_currentSource == ContentSource.IPTV && _iptvCategories.Count == 0)
                {
                    await LoadIptvDataAsync();
                }
            }
            
            // Ensure layout matches mode (Vital for collapsing OverlayCanvas)
            UpdateLayoutForMode();
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
                    await LoadStremioDataAsync();
                }
            }
        }

        private void UpdateLayoutForMode()
        {
            if (_currentSource == ContentSource.IPTV)
            {
                // Sidebar Mode
                MainSplitView.IsPaneOpen = true; 
                MainSplitView.DisplayMode = SplitViewDisplayMode.Inline;
                SidebarToggle.Visibility = Visibility.Visible;
                StremioTitle.Visibility = Visibility.Collapsed;
                MediaGrid.Visibility = Visibility.Visible;
                StremioHomeView.Visibility = Visibility.Collapsed;
                OverlayCanvas.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Stremio Mode (Full Screen Premium)
                MainSplitView.IsPaneOpen = false;
                MainSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
                SidebarToggle.Visibility = Visibility.Collapsed;
                StremioTitle.Visibility = Visibility.Visible;
                
                MediaGrid.Visibility = Visibility.Collapsed;
                StremioHomeView.Visibility = Visibility.Visible;
                OverlayCanvas.Visibility = Visibility.Visible;
            }
        }

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;
        }

        private void HeroPlayButton_Click(object sender, RoutedEventArgs e)
        {
             if (_heroItems.Count > _currentHeroIndex)
             {
                 var item = _heroItems[_currentHeroIndex];
                 Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(item), new HyperlinkButton().Margin == new Thickness(0) ? null : new SuppressNavigationTransitionInfo());
             }
        }

        private void HeroDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItems.Count > _currentHeroIndex)
            {
                var item = _heroItems[_currentHeroIndex];
                Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(item), new SuppressNavigationTransitionInfo());
            }
        }

        // ==========================================
        // IPTV LOGIC
        // ==========================================
        private async Task LoadIptvDataAsync()
        {
            try
            {
                LoadingRing.IsActive = true;
                
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
            }
        }

        private async Task DisplayCategories(List<LiveCategory> categories)
        {
            IptvCategoryList.ItemsSource = categories; // Binds to Sidebar List
            if (categories.Count > 0)
            {
                // Restore last selection or pick first
                IptvCategoryList.SelectedItem = categories[0];
                await LoadIptvStreams(categories[0]);
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

        // ==========================================
        // STREMIO LOGIC (Multiverse Discovery)
        // ==========================================
        private async Task LoadStremioDataAsync()
        {
            await LoadStremioDiscoveryAsync("movie");
        }

        private ObservableCollection<CatalogRowViewModel> _discoveryRows = new();

        private async Task LoadStremioDiscoveryAsync(string contentType)
        {
            try
            {
                LoadingRing.IsActive = true;
                DiscoveryRows.ItemsSource = null;
                _discoveryRows.Clear();

                // 1. Fetch Manifests from all addons
                var addonUrls = StremioAddonManager.Instance.GetAddons();
                var tasks = new List<Task<CatalogRowViewModel>>();

                foreach (var url in addonUrls)
                {
                    var manifest = await StremioService.Instance.GetManifestAsync(url);
                    if (manifest?.Catalogs == null) continue;

                    foreach (var cat in manifest.Catalogs.Where(c => c.Type == contentType))
                    {
                        tasks.Add(LoadCatalogRowAsync(url, contentType, cat));
                    }
                }

                var rows = await Task.WhenAll(tasks);
                foreach (var row in rows.Where(r => r != null && r.Items.Count > 0))
                {
                    _discoveryRows.Add(row);
                }

                DiscoveryRows.ItemsSource = _discoveryRows;

                // 2. Prepare Hero Items (Top 5 from first row)
                _heroItems.Clear();
                if (_discoveryRows.Count > 0)
                {
                    _heroItems.AddRange(_discoveryRows[0].Items.Take(5));
                }

                // Update Hero Section
                if (_heroItems.Count > 0)
                {
                    _currentHeroIndex = 0;
                    UpdateHeroSection(_heroItems[0]);
                    StartHeroAutoRotation();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Stremio] Discovery Error: {ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private async Task<CatalogRowViewModel> LoadCatalogRowAsync(string baseUrl, string type, StremioCatalog cat)
        {
            try
            {
                var items = await StremioService.Instance.GetCatalogItemsAsync(baseUrl, type, cat.Id);
                if (items == null || items.Count == 0) return null;

                return new CatalogRowViewModel
                {
                    CatalogName = cat.Name,
                    Items = new ObservableCollection<StremioMediaStream>(items)
                };
            }
            catch { return null; }
        }

        private async void UpdateHeroSection(StremioMediaStream item, bool animate = false)
        {
            string imgUrl = item.Meta?.Background ?? item.PosterUrl;

            if (animate && _heroVisual != null && !_heroTransitioning)
            {
                _heroTransitioning = true;
                var compositor = _heroVisual.Compositor;

                // Phase 1: Fade out image + slide out text
                var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f)));
                fadeOut.Duration = TimeSpan.FromMilliseconds(400);
                _heroVisual.StartAnimation("Opacity", fadeOut);

                AnimateTextOut();
                await Task.Delay(420);

                // Phase 2: Swap content
                HeroTitle.Text = item.Title;
                HeroOverview.Text = item.Meta?.Description ?? "Sinematik bir serüven sizi bekliyor.";

                if (!string.IsNullOrEmpty(imgUrl))
                {
                    var surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(imgUrl));
                    _heroImageBrush.Surface = surface;
                }

                // Phase 3: Fade in image + slide in text
                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.2f, 1f)));
                fadeIn.Duration = TimeSpan.FromMilliseconds(600);
                _heroVisual.StartAnimation("Opacity", fadeIn);

                AnimateTextIn();

                // Phase 4: Bleed colors
                if (!string.IsNullOrEmpty(imgUrl))
                {
                    var colors = await ImageHelper.GetOrExtractColorAsync(imgUrl);
                    if (colors.HasValue)
                    {
                        _heroColors = colors.Value;
                        BackdropControl.TransitionTo(_heroColors.Value.Primary, _heroColors.Value.Secondary);
                    }
                }

                _heroTransitioning = false;
            }
            else
            {
                // No animation (first load)
                HeroTitle.Text = item.Title;
                HeroOverview.Text = item.Meta?.Description ?? "Sinematik bir serüven sizi bekliyor.";

                if (!string.IsNullOrEmpty(imgUrl))
                {
                    var surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(imgUrl));
                    if (_heroImageBrush != null)
                        _heroImageBrush.Surface = surface;

                    var colors = await ImageHelper.GetOrExtractColorAsync(imgUrl);
                    if (colors.HasValue)
                    {
                        _heroColors = colors.Value;
                        BackdropControl.TransitionTo(_heroColors.Value.Primary, _heroColors.Value.Secondary);
                    }
                }
            }
        }

        private void AnimateTextOut()
        {
            var sb = new Storyboard();
            var fadeOut = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            var slideOut = new DoubleAnimation { To = -30, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(fadeOut, HeroContentPanel);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            Storyboard.SetTarget(slideOut, HeroContentPanel);
            Storyboard.SetTargetProperty(slideOut, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
            sb.Children.Add(fadeOut);
            sb.Children.Add(slideOut);
            HeroContentPanel.RenderTransform = new CompositeTransform();
            sb.Begin();
        }

        private void AnimateTextIn()
        {
            // Start from below
            var ct = new CompositeTransform { TranslateY = 30 };
            HeroContentPanel.RenderTransform = ct;
            HeroContentPanel.Opacity = 0;

            var sb = new Storyboard();
            var fadeIn = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var slideIn = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(fadeIn, HeroContentPanel);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            Storyboard.SetTarget(slideIn, HeroContentPanel);
            Storyboard.SetTargetProperty(slideIn, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
            sb.Children.Add(fadeIn);
            sb.Children.Add(slideIn);
            sb.Begin();
        }

        private void StartHeroAutoRotation()
        {
            _heroAutoTimer?.Stop();
            _heroAutoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _heroAutoTimer.Tick += (s, e) =>
            {
                if (_heroItems.Count > 1 && !_heroTransitioning)
                {
                    _currentHeroIndex = (_currentHeroIndex + 1) % _heroItems.Count;
                    UpdateHeroSection(_heroItems[_currentHeroIndex], animate: true);
                }
            };
            _heroAutoTimer.Start();
        }

        private void ResetHeroAutoTimer()
        {
            // Restart timer after manual navigation
            _heroAutoTimer?.Stop();
            _heroAutoTimer?.Start();
        }

        private void HeroNext_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItems.Count == 0 || _heroTransitioning) return;
            _currentHeroIndex = (_currentHeroIndex + 1) % _heroItems.Count;
            UpdateHeroSection(_heroItems[_currentHeroIndex], animate: true);
            ResetHeroAutoTimer();
        }

        private void HeroPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItems.Count == 0 || _heroTransitioning) return;
            _currentHeroIndex--;
            if (_currentHeroIndex < 0) _currentHeroIndex = _heroItems.Count - 1;
            UpdateHeroSection(_heroItems[_currentHeroIndex], animate: true);
            ResetHeroAutoTimer();
        }

        private void ApplyKenBurnsComposition(Microsoft.UI.Composition.Compositor compositor)
        {
            // CRITICAL GPU OPTIMIZATION:
            // Animate HeroImageHost's visual (parent), NOT _heroVisual (MaskBrush visual).
            // MaskBrush SpriteVisual is composited once → cached as a GPU texture.
            // Moving the parent just translates that cached texture = near zero cost.
            var hostVisual = ElementCompositionPreview.GetElementVisual(HeroImageHost);

            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f));
            offsetAnim.InsertKeyFrame(0f, Vector3.Zero, easing);
            offsetAnim.InsertKeyFrame(0.5f, new Vector3(-12f, -4f, 0f), easing);
            offsetAnim.InsertKeyFrame(1f, Vector3.Zero, easing);
            offsetAnim.Duration = TimeSpan.FromSeconds(25);
            offsetAnim.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;

            hostVisual.StartAnimation("Offset", offsetAnim);
        }

        private void CatalogRow_ItemClicked(object sender, IMediaStream e)
        {
            Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(e), new SuppressNavigationTransitionInfo());
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
                if (_currentSource == ContentSource.IPTV)
                    await LoadIptvStreams(category);
                else
                    await LoadStremioStreams(category);
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement Unified Search
        }

        private void MediaGrid_ItemClicked(object sender, IMediaStream e)
        {
             Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(e), new SuppressNavigationTransitionInfo());
        }

        private void MediaGrid_PlayAction(object sender, IMediaStream e)
        {
            // Direct Play
             if (e is LiveStream stream)
             {
                 Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name));
             }
             // Stremio Stream (StremioMediaStream) -> Needs Resolution first (Dialog)
             // We'll let MediaInfoPage handle it mostly, but for Direct Play from Grid:
             else if (e is Models.Stremio.StremioMediaStream stremioStream)
             {
                 // Stremio items usually need "Stream Selection". 
                 // So "Direct Play" from grid might best imply "Go to Details" or "Pick Top Stream"
                 // Let's redirect to Details for now to be safe
                 Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(e), new SuppressNavigationTransitionInfo());
             }
        }

        private void MediaGrid_DetailsAction(object sender, MediaNavigationArgs e)
        {
             Frame.Navigate(typeof(MediaInfoPage), e, new SuppressNavigationTransitionInfo());
        }

        private void MediaGrid_DetailsAction(object sender, LiveStream e)
        {
             Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(e), new SuppressNavigationTransitionInfo());
        }

        private void StremioHomeView_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                BackdropControl.SetVerticalShift(sv.VerticalOffset);
                
                // Close expanded card on scroll to prevent detachment
                // If in Cinema Mode, ignore scroll close request
                if (_isInCinemaMode) return;
            
                if (ActiveExpandedCard.Visibility == Visibility.Visible)
                {
                   _ = CloseExpandedCardAsync();
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
        // STREMIO EVENT HANDLERS
        // ==========================================
        private void CatalogRow_HoverStarted(object sender, PosterCard card)
        {
            // Call the shared logic
            Card_HoverStarted(card, EventArgs.Empty);
        }

        private void CatalogRow_HoverEnded(object sender, PosterCard card)
        {
            // Revert background to hero colors
            if (_heroColors.HasValue)
            {
                BackdropControl.TransitionTo(_heroColors.Value.Primary, _heroColors.Value.Secondary);
            }
            else
            {
                BackdropControl.TransitionTo(Windows.UI.Color.FromArgb(255, 13, 13, 13), Windows.UI.Color.FromArgb(255, 13, 13, 13));
            }

            // Close card (with debounce/check)
            _ = CloseExpandedCardAsync();
        }

        private async void CatalogRow_ScrollStarted(object sender, EventArgs e)
        {
            _isDraggingRow = true;
            _hoverTimer?.Stop();
            _flightTimer?.Stop();
            await CloseExpandedCardAsync();
        }

        private void CatalogRow_ScrollEnded(object sender, EventArgs e)
        {
            _isDraggingRow = false;
        }

        private async Task UpdateBackgroundFromPoster(string url)
        {
            var colors = await ImageHelper.GetOrExtractColorAsync(url);
            if (colors.HasValue)
            {
                BackdropControl.TransitionTo(colors.Value.Primary, colors.Value.Secondary);
            }
        }
        private void MediaGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is IMediaStream stream)
            {
                 Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(stream), new SuppressNavigationTransitionInfo());
            }
        }

        // ==========================================
        // FLYING PANEL LOGIC (Stremio)
        // ==========================================
        
        private DispatcherTimer _hoverTimer;
        private PosterCard _pendingHoverCard;
        private System.Threading.CancellationTokenSource _closeCts;
        private DispatcherTimer _flightTimer;

        private void Card_HoverStarted(object sender, EventArgs e)
        {
            if (_isDraggingRow) return;

            if (sender is PosterCard card)
            {
                // 1. Cinematic Background Update
                if (card.DataContext is IMediaStream stream && !string.IsNullOrEmpty(stream.PosterUrl))
                {
                    _ = UpdateBackgroundFromPoster(stream.PosterUrl);
                }

                // 2. Expanded Card Logic
                // Cancel any pending close
                _closeCts?.Cancel();

                var visual = ElementCompositionPreview.GetElementVisual(ActiveExpandedCard);
                // Safe cancel animations
                try { visual.StopAnimation("Opacity"); } catch { }
                try { visual.StopAnimation("Scale"); } catch { }
                try { visual.StopAnimation("Translation"); } catch { }

                bool isAlreadyOpen = ActiveExpandedCard.Visibility == Visibility.Visible;

                if (isAlreadyOpen)
                {
                    // Flight Mode
                    visual.Opacity = 1f;
                    
                    if (_flightTimer == null) 
                    {
                        _flightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                        _flightTimer.Tick += FlightTimer_Tick;
                    }
                    else
                    {
                        _flightTimer.Stop();
                    }
                    
                    _pendingHoverCard = card;
                    _flightTimer.Start();
                }
                else
                {
                    // Fresh Open (Debounce)
                    _pendingHoverCard = card;
                    if (_hoverTimer == null)
                    {
                        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                        _hoverTimer.Tick += HoverTimer_Tick;
                    }
                    else
                    {
                        _hoverTimer.Stop();
                    }
                    _hoverTimer.Start();
                    ActiveExpandedCard.PrepareForTrailer();
                }
            }
        }

        private void FlightTimer_Tick(object sender, object e)
        {
            _flightTimer.Stop();
            if (_pendingHoverCard != null && _pendingHoverCard.IsHovered)
            {
                 ShowExpandedCard(_pendingHoverCard);
            }
        }

        private void HoverTimer_Tick(object sender, object e)
        {
            _hoverTimer.Stop();
            if (_pendingHoverCard != null && _pendingHoverCard.IsHovered)
            {
                ShowExpandedCard(_pendingHoverCard);
            }
        }

        private async void ShowExpandedCard(PosterCard sourceCard)
        {
            try
            {
                _closeCts?.Cancel();
                _closeCts = new System.Threading.CancellationTokenSource();

                // 1. Coordinates relative to OverlayCanvas
                // OverlayCanvas covers the entire SplitView.Content Grid
                var transform = sourceCard.TransformToVisual(OverlayCanvas);
                var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                
                double widthDiff = 320 - sourceCard.ActualWidth;
                double heightDiff = 420 - sourceCard.ActualHeight;
                
                double targetX = position.X - (widthDiff / 2);
                double targetY = position.Y - (heightDiff / 2);

                // Boundaries
                if (targetX < 10) targetX = 10;
                if (targetX + 320 > OverlayCanvas.ActualWidth) targetX = OverlayCanvas.ActualWidth - 330;
                if (targetY < 10) targetY = 10;
                if (targetY + 420 > OverlayCanvas.ActualHeight) targetY = OverlayCanvas.ActualHeight - 430;

                var visual = ElementCompositionPreview.GetElementVisual(ActiveExpandedCard);
                var compositor = visual.Compositor;
                ElementCompositionPreview.SetIsTranslationEnabled(ActiveExpandedCard, true);

                // 2. Pop vs Morph
                bool isMorph = ActiveExpandedCard.Visibility == Visibility.Visible && visual.Opacity > 0.1f;

                if (isMorph)
                {
                    ActiveExpandedCard.StopTrailer();

                    double oldLeft = Canvas.GetLeft(ActiveExpandedCard);
                    double oldTop = Canvas.GetTop(ActiveExpandedCard);
                    
                    Canvas.SetLeft(ActiveExpandedCard, targetX);
                    Canvas.SetTop(ActiveExpandedCard, targetY);
                    ActiveExpandedCard.UpdateLayout(); 

                    float deltaX = (float)(oldLeft - targetX);
                    float deltaY = (float)(oldTop - targetY);
                    
                    // Translation Hack
                    visual.Properties.InsertVector3("Translation", new Vector3(deltaX, deltaY, 0));
                    
                    var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
                    offsetAnim.Target = "Translation";
                    var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1.0f));
                    offsetAnim.InsertKeyFrame(1.0f, Vector3.Zero, easing);
                    offsetAnim.Duration = TimeSpan.FromMilliseconds(400);
                    
                    visual.StartAnimation("Translation", offsetAnim);
                    visual.Opacity = 1f;
                    visual.Scale = Vector3.One;
                }
                else
                {
                    visual.StopAnimation("Translation");
                    visual.Properties.InsertVector3("Translation", Vector3.Zero);
                    visual.Scale = new Vector3(0.8f, 0.8f, 1f);
                    visual.Opacity = 0;

                    Canvas.SetLeft(ActiveExpandedCard, targetX);
                    Canvas.SetTop(ActiveExpandedCard, targetY);
                    ActiveExpandedCard.Visibility = Visibility.Visible;

                    var springAnim = compositor.CreateSpringVector3Animation();
                    springAnim.Target = "Scale";
                    springAnim.FinalValue = Vector3.One;
                    springAnim.DampingRatio = 0.7f;
                    springAnim.Period = TimeSpan.FromMilliseconds(50);
                    
                    var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
                    fadeAnim.Target = "Opacity";
                    fadeAnim.InsertKeyFrame(1f, 1f);
                    fadeAnim.Duration = TimeSpan.FromMilliseconds(200);

                    visual.StartAnimation("Scale", springAnim);
                    visual.StartAnimation("Opacity", fadeAnim);
                }

                if (sourceCard.DataContext is IMediaStream stream)
                {
                    await ActiveExpandedCard.LoadDataAsync(stream, isMorphing: isMorph);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing card: {ex.Message}");
            }
        }

        // Cinema Mode State
        private bool _isInCinemaMode = false;
        private Rect _preCinemaBounds; // To restore position

        private async void ActiveExpandedCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            // If in Cinema Mode, don't close on hover exit
            if (_isInCinemaMode) return;

            
            // Actually, the existing logic is:
            // _hoverTimer.Tick += (s, args) => CloseExpandedCardAsync();
            // We should cancel that timer if we enter Cinema Mode.
            await CloseExpandedCardAsync();
        }

        private void ActiveExpandedCard_CinemaModeToggled(object sender, bool isCinema)
        {
            _isInCinemaMode = isCinema;
            System.Diagnostics.Debug.WriteLine($"[MoviesPage] Cinema Mode Toggled: {isCinema}");

            if (isCinema)
            {
                // ENTER CINEMA MODE
                
                // 1. Cancel any close timers
                _hoverTimer?.Stop();
                
                // 2. Save current position
                double currentLeft = Canvas.GetLeft(ActiveExpandedCard);
                double currentTop = Canvas.GetTop(ActiveExpandedCard);
                _preCinemaBounds = new Rect(currentLeft, currentTop, ActiveExpandedCard.ActualWidth, ActiveExpandedCard.ActualHeight);

                // 3. Calculate Target (Center of Page)
                // User requested "almost half the screen", let's make it significant but not overwhelming? 
                // Actually 85% is a good "Cinema" standard.
                double targetWidth = this.ActualWidth * 0.85;
                double targetHeight = this.ActualHeight * 0.85;
                
                // Aspect Ratio 16:9 check
                if (targetWidth / targetHeight > 1.77)
                {
                    targetWidth = targetHeight * 1.77;
                }
                else
                {
                    targetHeight = targetWidth / 1.77;
                }

                double targetLeft = (this.ActualWidth - targetWidth) / 2;
                double targetTop = (this.ActualHeight - targetHeight) / 2;

                System.Diagnostics.Debug.WriteLine($"[MoviesPage] Animate To: {targetWidth}x{targetHeight} at {targetLeft},{targetTop}");

                // 4. Animate to Center
                AnimateCardTo(targetLeft, targetTop, targetWidth, targetHeight);
                
                // 5. Show Scrim (AND SET SIZE)
                CinemaScrim.Width = this.ActualWidth;
                CinemaScrim.Height = this.ActualHeight;
                CinemaScrim.Visibility = Visibility.Visible;
                CinemaScrim.IsHitTestVisible = true; 
                
                // 6. Disable Scrolling
                StremioHomeView.VerticalScrollMode = ScrollMode.Disabled;
                
                // 7. Bring to very front
                Canvas.SetZIndex(CinemaScrim, 100);
                Canvas.SetZIndex(ActiveExpandedCard, 101);
            }
            else
            {
                // EXIT CINEMA MODE
                
                // 1. Animate back to original position
                AnimateCardTo(_preCinemaBounds.Left, _preCinemaBounds.Top, 320, 420); // 320x420 is default size
                
                // 2. Hide Scrim
                CinemaScrim.Visibility = Visibility.Collapsed;
                CinemaScrim.IsHitTestVisible = false;
                
                // 3. Re-enable Scrolling
                StremioHomeView.VerticalScrollMode = ScrollMode.Enabled;
                
                // 4. Reset Z-Index
                Canvas.SetZIndex(CinemaScrim, 0);
                Canvas.SetZIndex(ActiveExpandedCard, 1);
            }
        }

        private void AnimateCardTo(double left, double top, double width, double height)
        {
            // SCALE TRANSFORM APPROACH
            // Instead of resizing the control (which seems stubborn), we will Scale it.
            // Default size: 320x420
            
            double targetScaleX = width / 320.0;
            double targetScaleY = height / 420.0;
            
            System.Diagnostics.Debug.WriteLine($"[MoviesPage] AnimateCardTo (Scale): Target {width}x{height} -> Scale {targetScaleX:F2}x{targetScaleY:F2} @ {left},{top}");

            // Ensure RenderTransform is CompositeTransform
            if (!(ActiveExpandedCard.RenderTransform is CompositeTransform))
            {
                ActiveExpandedCard.RenderTransform = new CompositeTransform();
                ActiveExpandedCard.RenderTransformOrigin = new Windows.Foundation.Point(0, 0); // Scale from top-left match Canvas positioning
            }

            var storyboard = new Storyboard();
            
            var animSX = new DoubleAnimation 
            { 
                To = targetScaleX, 
                Duration = TimeSpan.FromMilliseconds(400), 
                EnableDependentAnimation = true, 
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } 
            };
            Storyboard.SetTarget(animSX, ActiveExpandedCard);
            Storyboard.SetTargetProperty(animSX, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)");
            
            var animSY = new DoubleAnimation 
            { 
                To = targetScaleY, 
                Duration = TimeSpan.FromMilliseconds(400), 
                EnableDependentAnimation = true, 
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } 
            };
            Storyboard.SetTarget(animSY, ActiveExpandedCard);
            Storyboard.SetTargetProperty(animSY, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");
            
            var animL = new DoubleAnimation 
            { 
                To = left, 
                Duration = TimeSpan.FromMilliseconds(400), 
                EnableDependentAnimation = true, 
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } 
            };
            Storyboard.SetTarget(animL, ActiveExpandedCard);
            Storyboard.SetTargetProperty(animL, "(Canvas.Left)");
            
            var animT = new DoubleAnimation 
            { 
                To = top, 
                Duration = TimeSpan.FromMilliseconds(400), 
                EnableDependentAnimation = true, 
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } 
            };
            Storyboard.SetTarget(animT, ActiveExpandedCard);
            Storyboard.SetTargetProperty(animT, "(Canvas.Top)");
            
            storyboard.Children.Add(animSX);
            storyboard.Children.Add(animSY);
            storyboard.Children.Add(animL);
            storyboard.Children.Add(animT);
            
            storyboard.Begin();
        }

        public async Task CloseExpandedCardAsync()
        {
             // If in Cinema Mode, ignore close request
             if (_isInCinemaMode) return;

             try 
             {
                 _closeCts?.Cancel();
                 _closeCts = new System.Threading.CancellationTokenSource();
                 var token = _closeCts.Token;

                 ActiveExpandedCard.StopTrailer();
                 await Task.Delay(50, token);
                 
                 if (token.IsCancellationRequested) return;

                 var visual = ElementCompositionPreview.GetElementVisual(ActiveExpandedCard);
                 var compositor = visual.Compositor;
                 
                 var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                 fadeOut.Target = "Opacity";
                 fadeOut.InsertKeyFrame(1f, 0f);
                 fadeOut.Duration = TimeSpan.FromMilliseconds(200);
                 
                 var scaleDown = compositor.CreateVector3KeyFrameAnimation();
                 scaleDown.Target = "Scale";
                 scaleDown.InsertKeyFrame(1f, new Vector3(0.95f, 0.95f, 1f));
                 scaleDown.Duration = TimeSpan.FromMilliseconds(200);
                 
                 visual.StartAnimation("Opacity", fadeOut);
                 visual.StartAnimation("Scale", scaleDown);
                 
                 await Task.Delay(200, token);
                 if (token.IsCancellationRequested) return;

                 ActiveExpandedCard.Visibility = Visibility.Collapsed;
             }
             catch (TaskCanceledException) { }
        }

        private void ExpandedCard_PlayClicked(object sender, EventArgs e) 
        {
             // TODO: Handle Play from Card (for now same as ItemClick)
             if (_pendingHoverCard?.DataContext is IMediaStream item)
             {
                 // Re-use existing play logic?
                 // MediaGridView_ItemClick handles navigation to Details/Player
                 // But we need to call it manually
                 // For now, let's just trigger item click logic
                 // But ItemClickEventArgs is internal.
                 
                 // We can direct navigate
                 if (item is Models.Stremio.StremioMediaStream sms)
                 {
                      // Stremio Play Logic (TODO: Verify specific stream play or details)
                      Frame.Navigate(typeof(MediaInfoPage), item); 
                 }
             }
        }

        private void ExpandedCard_DetailsClicked(object sender, TmdbMovieResult tmdb) 
        {
             if (_pendingHoverCard?.DataContext is IMediaStream item)
             {
                 Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(item, tmdb));
             }
        }

        private void ExpandedCard_AddListClicked(object sender, EventArgs e) 
        {
             // Add to list logic (ignored for now)
        }
    }
}
