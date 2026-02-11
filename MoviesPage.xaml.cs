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
    public class CatalogRowViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string _catalogName;
        private bool _isLoading;
        private ObservableCollection<StremioMediaStream> _items = new();

        public string CatalogName { get => _catalogName; set { _catalogName = value; OnPropertyChanged(); } }
        public ObservableCollection<StremioMediaStream> Items { get => _items; set { _items = value; OnPropertyChanged(); } }
        public bool IsLoading { get => _isLoading; set { _isLoading = value; OnPropertyChanged(); } }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
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
        private readonly ExpandedCardOverlayController _stremioExpandedCardOverlay;

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
            HeroImageHost.Loaded += (s, e) => 
            {
                if (_heroVisual == null) 
                {
                    SetupHeroCompositionMask();
                }
                else
                {
                    // Re-attach existing visual if page was cached
                    ElementCompositionPreview.SetElementChildVisual(HeroImageHost, _heroVisual);
                }
            };

            _stremioExpandedCardOverlay = new ExpandedCardOverlayController(this, OverlayCanvas, ActiveExpandedCard, CinemaScrim, StremioHomeView);
            _stremioExpandedCardOverlay.PlayRequested += StremioExpandedCardOverlay_PlayRequested;
            _stremioExpandedCardOverlay.DetailsRequested += StremioExpandedCardOverlay_DetailsRequested;
            _stremioExpandedCardOverlay.AddListRequested += StremioExpandedCardOverlay_AddListRequested;
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

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // Stop any playing trailer in ExpandedCard when leaving the page
            _stremioExpandedCardOverlay?.CloseExpandedCardAsync(force: true);
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
                _ = _stremioExpandedCardOverlay.CloseExpandedCardAsync(force: true);

                // Sidebar Mode
                MainSplitView.IsPaneOpen = true; 
                MainSplitView.DisplayMode = SplitViewDisplayMode.Inline;
                SidebarToggle.Visibility = Visibility.Visible;
                MediaGrid.Visibility = Visibility.Visible;
                StremioHomeView.Visibility = Visibility.Collapsed;
                OverlayCanvas.Visibility = Visibility.Visible;
            }
            else
            {
                // Stremio Mode (Full Screen Premium)
                MainSplitView.IsPaneOpen = false;
                MainSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
                SidebarToggle.Visibility = Visibility.Collapsed;
                // StremioTitle removed per request
                
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
                 Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(item), new SuppressNavigationTransitionInfo());
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
                // 1. Initial Shimmer State
                HeroShimmer.Visibility = Visibility.Visible;
                HeroTextShimmer.Visibility = Visibility.Visible;
                HeroRealContent.Opacity = 0;
                
                DiscoveryRows.ItemsSource = _discoveryRows;
                _discoveryRows.Clear();
                
                // Add 6 skeleton rows
                for (int i = 0; i < 6; i++)
                {
                    _discoveryRows.Add(new CatalogRowViewModel { CatalogName = "Yükleniyor...", IsLoading = true });
                }

                // 2. Fetch Manifests
                var addonUrls = StremioAddonManager.Instance.GetAddons();
                var catalogTasks = new List<Task<CatalogRowViewModel>>();

                bool firstHeroSet = false;
                int activeSkeletonIndex = 0;

                foreach (var url in addonUrls)
                {
                    // Run each addon's catalog fetching in parallel
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var manifest = await StremioService.Instance.GetManifestAsync(url);
                            if (manifest?.Catalogs == null) return;

                            foreach (var cat in manifest.Catalogs.Where(c => c.Type == contentType))
                            {
                                var row = await LoadCatalogRowAsync(url, contentType, cat);
                                if (row != null && row.Items.Count > 0)
                                {
                                    DispatcherQueue.TryEnqueue(() =>
                                    {
                                        // Replace skeleton if available, otherwise just add
                                        if (activeSkeletonIndex < _discoveryRows.Count && _discoveryRows[activeSkeletonIndex].IsLoading)
                                        {
                                            _discoveryRows[activeSkeletonIndex].CatalogName = row.CatalogName;
                                            _discoveryRows[activeSkeletonIndex].Items = row.Items;
                                            _discoveryRows[activeSkeletonIndex].IsLoading = false;
                                            activeSkeletonIndex++;
                                        }
                                        else
                                        {
                                            _discoveryRows.Add(row);
                                        }

                                        // Update Hero if this is the very first row
                                        if (!firstHeroSet && row.Items.Count > 0)
                                        {
                                            firstHeroSet = true;
                                            _heroItems.Clear();
                                            _heroItems.AddRange(row.Items.Take(5));
                                            _currentHeroIndex = 0;
                                            UpdateHeroSection(_heroItems[0]);
                                            StartHeroAutoRotation();
                                        }
                                    });
                                }
                            }
                        }
                        catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Stremio] Discovery Error: {ex.Message}");
            }
            finally
            {
                // Remove remaining skeletons after a timeout or when all tasks are done
                // For now we keep them until replaced
                LoadingRing.IsActive = false;
            }
        }

        private async Task<CatalogRowViewModel> LoadCatalogRowAsync(string baseUrl, string type, StremioCatalog cat)
        {
            try
            {
                var items = await StremioService.Instance.GetCatalogItemsAsync(baseUrl, type, cat.Id);
                if (items == null || items.Count == 0) return null;

                string finalName = cat.Name;
                if (finalName == "KEŞFET" || finalName == "Keşfet") finalName = string.Empty;

                return new CatalogRowViewModel
                {
                    CatalogName = finalName,
                    Items = new ObservableCollection<StremioMediaStream>(items)
                };
            }
            catch { return null; }
        }

        private async void UpdateHeroSection(StremioMediaStream item, bool animate = false)
        {
            // Transition from Shimmer to Real Content
            if (HeroShimmer.Visibility == Visibility.Visible)
            {
                HeroShimmer.Visibility = Visibility.Collapsed;
                HeroTextShimmer.Visibility = Visibility.Collapsed;
                HeroRealContent.Opacity = 1; // Or animate it
                AnimateTextIn();
            }

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
                HeroYear.Text = item.Meta?.ReleaseInfo ?? "";
                HeroGenres.Text = (item.Meta?.Genres != null && item.Meta.Genres.Count > 0) ? string.Join(", ", item.Meta.Genres.Take(2)) : "";
                HeroRating.Text = item.Meta?.ImdbRating != null ? $"{item.Meta.ImdbRating} ★" : "";

                // Dots Visibility Logic
                HeroYearDot.Visibility = (!string.IsNullOrEmpty(HeroYear.Text) && !string.IsNullOrEmpty(HeroGenres.Text)) ? Visibility.Visible : Visibility.Collapsed;
                HeroRatingDot.Visibility = (!string.IsNullOrEmpty(HeroGenres.Text) && !string.IsNullOrEmpty(HeroRating.Text)) ? Visibility.Visible : Visibility.Collapsed;

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
                HeroYear.Text = item.Meta?.ReleaseInfo ?? "";
                HeroGenres.Text = (item.Meta?.Genres != null && item.Meta.Genres.Count > 0) ? string.Join(", ", item.Meta.Genres.Take(2)) : "";
                HeroRating.Text = item.Meta?.ImdbRating != null ? $"{item.Meta.ImdbRating} ★" : "";

                HeroYearDot.Visibility = (!string.IsNullOrEmpty(HeroYear.Text) && !string.IsNullOrEmpty(HeroGenres.Text)) ? Visibility.Visible : Visibility.Collapsed;
                HeroRatingDot.Visibility = (!string.IsNullOrEmpty(HeroGenres.Text) && !string.IsNullOrEmpty(HeroRating.Text)) ? Visibility.Visible : Visibility.Collapsed;

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

        private void StremioHomeView_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                BackdropControl.SetVerticalShift(sv.VerticalOffset);
                
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
        // STREMIO EVENT HANDLERS
        // ==========================================
        private void CatalogRow_HoverStarted(object sender, PosterCard card)
        {
            if (_isDraggingRow) return;

            if (card.DataContext is IMediaStream stream && !string.IsNullOrEmpty(stream.PosterUrl))
            {
                _ = UpdateBackgroundFromPoster(stream.PosterUrl);
            }

            _stremioExpandedCardOverlay.OnHoverStarted(card);
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
            _stremioExpandedCardOverlay.CancelPendingShow();
            await _stremioExpandedCardOverlay.CloseExpandedCardAsync();
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
        public Task CloseExpandedCardAsync() => _stremioExpandedCardOverlay.CloseExpandedCardAsync();

        private void StremioExpandedCardOverlay_PlayRequested(object? sender, IMediaStream stream)
        {
            if (stream is StremioMediaStream)
            {
                Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(stream, null, true), new SuppressNavigationTransitionInfo());
            }
        }

        private void StremioExpandedCardOverlay_DetailsRequested(object? sender, (IMediaStream Stream, TmdbMovieResult Tmdb) args)
        {
            Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(args.Stream, args.Tmdb), new SuppressNavigationTransitionInfo());
        }

        private void StremioExpandedCardOverlay_AddListRequested(object? sender, IMediaStream stream)
        {
            // TODO: Favorites logic for Stremio expanded card action.
        }
    }
}
