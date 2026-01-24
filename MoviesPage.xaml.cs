using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using Windows.UI;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModernIPTVPlayer
{
    public sealed partial class MoviesPage : Page
    {
        private LoginParams? _loginInfo;
        private HttpClient _httpClient;
        private Windows.UI.Color _currentBackdropColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        private ConcurrentDictionary<string, Windows.UI.Color> _colorCache = new();
        private string? _lastProcessedImageUrl;
        private DispatcherTimer? _backdropAnimationTimer;
        private Windows.UI.Color _animatingFromColor;
        private DispatcherTimer? _breathingTimer;
        private double _breathPhase = 0;

        public MoviesPage()
        {
            this.InitializeComponent();
            _httpClient = HttpHelper.Client;
            StartBreathingAnimation();
        }
        
        private void StartBreathingAnimation()
        {
            _breathingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _breathingTimer.Tick += (s, e) =>
            {
                _breathPhase += 0.025; // Slow, subtle breathing
                if (_breathPhase > Math.PI * 2) _breathPhase = 0;
                
                // Subtle opacity pulsing for layers
                // Primary: 0.35 to 0.55 (main glow breathes)
                PrimaryGlowLayer.Opacity = 0.45 + 0.10 * Math.Sin(_breathPhase);
                
                // Ambient: inverse pulse (creates depth perception)
                AmbientLayer.Opacity = 0.25 + 0.05 * Math.Sin(_breathPhase + Math.PI);
                
                // Bloom: faster, subtle pulse
                BloomLayer.Opacity = 0.15 + 0.05 * Math.Sin(_breathPhase * 1.5);
                
                // Secondary (top-right): different phase for variety
                SecondaryGlowLayer.Opacity = 0.12 + 0.04 * Math.Sin(_breathPhase * 0.7 + Math.PI/2);
            };
            _breathingTimer.Start();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is LoginParams p)
            {
                if (_loginInfo != null && _loginInfo.PlaylistUrl != p.PlaylistUrl)
                {
                    CategoryListView.ItemsSource = null;
                    MovieGridView.ItemsSource = null;
                }
                _loginInfo = p;
            }

            if (_loginInfo != null && !string.IsNullOrEmpty(_loginInfo.Host))
            {
                if (CategoryListView.ItemsSource != null) return;
                await LoadVodCategoriesAsync();
            }
        }

        private List<LiveCategory> _allCategories = new();

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;
        }

        private void CategorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_allCategories == null) return;
            
            var query = CategorySearchBox.Text.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(query))
            {
                CategoryListView.ItemsSource = _allCategories;
            }
            else
            {
                var filtered = new List<LiveCategory>();
                foreach (var cat in _allCategories)
                {
                    if (cat.CategoryName != null && cat.CategoryName.ToLowerInvariant().Contains(query))
                    {
                        filtered.Add(cat);
                    }
                }
                CategoryListView.ItemsSource = filtered;
            }
        }

        // ==========================================
        // Premium Search Box Interactions
        // ==========================================
        private void SearchPill_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (CategorySearchBox.FocusState == FocusState.Unfocused)
            {
                SearchPillBorder.Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255));
            }
        }

        private void SearchPill_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (CategorySearchBox.FocusState == FocusState.Unfocused)
            {
                SearchPillBorder.Background = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));
            }
        }

        private void CategorySearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            SearchPillBorder.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            SearchPillBorder.BorderBrush = (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"];
        }

        private void CategorySearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            SearchPillBorder.Background = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));
            SearchPillBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
        }

        private async Task LoadVodCategoriesAsync()
        {
            // PROMPT: Start diag
            ShowMessageDialog("Debug", $"Yükleme başlıyor... Host: {_loginInfo?.Host}");

            try
            {
                LoadingRing.IsActive = true;
                CategoryListView.ItemsSource = null;
                MovieGridView.ItemsSource = null;
                _allCategories.Clear();

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string api = $"{baseUrl}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_vod_categories";

                // DEBUG: Show URL
                System.Diagnostics.Debug.WriteLine($"DEBUG LOAD: {api}");

                string json = await _httpClient.GetStringAsync(api);
                
                // DEBUG: Show JSON length
                System.Diagnostics.Debug.WriteLine($"DEBUG JSON LEN: {json?.Length ?? 0}");

                if (string.IsNullOrEmpty(json))
                {
                    ShowMessageDialog("Hata", "API'den boş cevap döndü.");
                    return;
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };

                var categories = JsonSerializer.Deserialize<List<LiveCategory>>(json, options);

                if (categories != null)
                {
                    System.Diagnostics.Debug.WriteLine($"DEBUG CAT COUNT: {categories.Count}");
                    _allCategories = categories;
                    CategoryListView.ItemsSource = _allCategories;
                    
                    // Auto-select first category
                    if (_allCategories.Count > 0)
                    {
                        CategoryListView.SelectedIndex = 0;
                        await LoadVodStreamsAsync(_allCategories[0]);
                    }
                }
                else
                {
                    ShowMessageDialog("Hata", "JSON parse edildi ama kategori listesi boş (null).");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading movie categories: {ex.Message}");
                ShowMessageDialog("Kritik Hata", $"Veri çekilirken hata oluştu:\n{ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private List<int> _skeletonList = new List<int>(new int[20]); // 20 placeholders

        private async Task LoadVodStreamsAsync(LiveCategory category)
        {
            // Update Title when category selected
            SelectedCategoryTitle.Text = category.CategoryName;

            if (category.Channels != null && category.Channels.Count > 0)
            {
                MovieGridView.ItemsSource = category.Channels;
                 // Ensure Grid is visible and Skeleton hidden
                MovieGridView.Visibility = Visibility.Visible;
                SkeletonGrid.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                // SHOW SKELETON
                MovieGridView.Visibility = Visibility.Collapsed;
                SkeletonGrid.Visibility = Visibility.Visible;
                SkeletonGrid.ItemsSource = _skeletonList;
                
                MovieGridView.ItemsSource = null;

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string api = $"{baseUrl}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_vod_streams&category_id={category.CategoryId}";

                string json = await _httpClient.GetStringAsync(api);
                
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };

                var streams = JsonSerializer.Deserialize<List<LiveStream>>(json, options);

                if (streams != null)
                {
                    foreach (var s in streams)
                    {
                        string extension = !string.IsNullOrEmpty(s.ContainerExtension) ? s.ContainerExtension : "mp4";
                        s.StreamUrl = $"{baseUrl}/movie/{_loginInfo.Username}/{_loginInfo.Password}/{s.StreamId}.{extension}"; 
                    }

                    category.Channels = streams;
                    MovieGridView.ItemsSource = category.Channels;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading movies: {ex.Message}");
            }
            finally
            {
                // HIDE SKELETON
                SkeletonGrid.Visibility = Visibility.Collapsed;
                MovieGridView.Visibility = Visibility.Visible;
            }
        }
        
        private void Image_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is Image img)
            {
                img.Opacity = 0;
                var anim = new DoubleAnimation
                {
                    To = 1,
                    Duration = TimeSpan.FromSeconds(0.6),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                
                var sb = new Storyboard();
                sb.Children.Add(anim);
                Storyboard.SetTarget(anim, img);
                Storyboard.SetTargetProperty(anim, "Opacity");
                sb.Begin();
            }
        }

        private async void CategoryListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            // Reset Scroll to Top
            // Note: GridView doesn't have a direct ScrollToTop, but setting ItemsSource usually resets it.
            // If needed we can find the ScrollViewer.
            
            if (e.ClickedItem is LiveCategory category)
            {
                await LoadVodStreamsAsync(category);
            }
        }

        private void MovieGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveStream stream)
            {
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name));
            }
        }

        // ==========================================
        // 3D TILT EFFECT & CONTAINER LOGIC
        // ==========================================
        
        private void MovieGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                var container = args.ItemContainer;
                container.PointerMoved -= MovieItem_PointerMoved;
                container.PointerExited -= MovieItem_PointerExited;
                container.PointerEntered -= MovieItem_PointerEntered;
            }
            else
            {
                var container = args.ItemContainer;
                if (container.Background == null) container.Background = new SolidColorBrush(Colors.Transparent);
                
                container.PointerMoved -= MovieItem_PointerMoved;
                container.PointerMoved += MovieItem_PointerMoved;
                
                container.PointerExited -= MovieItem_PointerExited;
                container.PointerExited += MovieItem_PointerExited;
                
                container.PointerEntered -= MovieItem_PointerEntered;
                container.PointerEntered += MovieItem_PointerEntered;
            }
        }
        
        private async void MovieItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is GridViewItem item && item.Content is LiveStream stream && !string.IsNullOrEmpty(stream.IconUrl))
            {
                await UpdateBackdropColorAsync(stream.IconUrl);
            }
        }
        
        private void MovieItem_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (sender is GridViewItem item)
            {
                var rootGrid = GetTemplateChild<Grid>(item, "RootGrid");
                
                if (rootGrid != null && rootGrid.Projection is PlaneProjection projection)
                {
                    var pointerPosition = e.GetCurrentPoint(rootGrid).Position;
                    var center = new Windows.Foundation.Point(rootGrid.ActualWidth / 2, rootGrid.ActualHeight / 2);
                    
                    var xDiff = pointerPosition.X - center.X;
                    var yDiff = pointerPosition.Y - center.Y;

                    projection.RotationY = -xDiff / 15.0;
                    projection.RotationX = yDiff / 15.0;
                }
            }
        }

        private void MovieItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is GridViewItem item)
            {
                var rootGrid = GetTemplateChild<Grid>(item, "RootGrid");
                if (rootGrid != null && rootGrid.Projection is PlaneProjection projection)
                {
                     // Reset smoothly
                     projection.RotationX = 0;
                     projection.RotationY = 0;
                }
            }
        }

        // Helper to find named elements in the template
        private T GetTemplateChild<T>(DependencyObject parent, string name) where T : DependencyObject
        {
             int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
             for (int i = 0; i < count; i++)
             {
                 var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                 if (child is FrameworkElement fe && fe.Name == name && child is T typed)
                 {
                     return typed;
                 }
                 var result = GetTemplateChild<T>(child, name);
                 if (result != null) return result;
             }
             return null;
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            // Placeholder for Global Spotlight Search
            ContentDialog searchDialog = new ContentDialog
            {
                Title = "Global Arama",
                Content = "Bu özellik (Spotlight Search) yakında eklenecek.",
                CloseButtonText = "Kapat",
                XamlRoot = this.XamlRoot
            };
            await searchDialog.ShowAsync();
        }

        // TODO: Implement UpdateDynamicBackdrop(string imageUrl) when ready
        private async void ShowMessageDialog(string title, string content)
        {
            if (this.XamlRoot == null) return;
            ContentDialog dialog = new ContentDialog 
            { 
                Title = title, 
                Content = content, 
                CloseButtonText = "Tamam", 
                XamlRoot = this.XamlRoot 
            };
            await dialog.ShowAsync();
        }
        
        // ==========================================
        // DYNAMIC BACKDROP COLOR SYSTEM
        // ==========================================
        
        private async Task UpdateBackdropColorAsync(string imageUrl)
        {
            if (imageUrl == _lastProcessedImageUrl) return;
            _lastProcessedImageUrl = imageUrl;
            
            try
            {
                Windows.UI.Color dominantColor;
                
                if (_colorCache.TryGetValue(imageUrl, out var cached))
                {
                    dominantColor = cached;
                }
                else
                {
                    dominantColor = await ExtractDominantColorAsync(imageUrl);
                    _colorCache.TryAdd(imageUrl, dominantColor);
                }
                
                System.Diagnostics.Debug.WriteLine($"BACKDROP: Extracted color R={dominantColor.R}, G={dominantColor.G}, B={dominantColor.B}");
                AnimateBackdropColor(dominantColor);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BACKDROP ERROR: {ex.Message}");
            }
        }
        
        private async Task<Windows.UI.Color> ExtractDominantColorAsync(string imageUrl)
        {
            using var response = await _httpClient.GetAsync(imageUrl);
            response.EnsureSuccessStatusCode();
            
            using var stream = await response.Content.ReadAsStreamAsync();
            using var memStream = new InMemoryRandomAccessStream();
            await stream.CopyToAsync(memStream.AsStreamForWrite());
            memStream.Seek(0);
            
            var decoder = await BitmapDecoder.CreateAsync(memStream);
            
            // Scale entire image down to 10x10 for simple averaging
            uint targetSize = 10;
            var transform = new BitmapTransform 
            { 
                ScaledWidth = targetSize, 
                ScaledHeight = targetSize,
                InterpolationMode = BitmapInterpolationMode.Linear
            };
            
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            
            var pixels = pixelData.DetachPixelData();
            
            long totalR = 0, totalG = 0, totalB = 0;
            int pixelCount = pixels.Length / 4;
            
            if (pixelCount == 0) return Windows.UI.Color.FromArgb(0, 0, 0, 0);
            
            for (int i = 0; i < pixels.Length; i += 4)
            {
                totalB += pixels[i];
                totalG += pixels[i + 1];
                totalR += pixels[i + 2];
            }
            
            byte avgR = (byte)(totalR / pixelCount);
            byte avgG = (byte)(totalG / pixelCount);
            byte avgB = (byte)(totalB / pixelCount);
            
            // Boost saturation slightly for more vibrant backdrop
            float saturationBoost = 1.4f;
            avgR = (byte)Math.Min(255, avgR * saturationBoost);
            avgG = (byte)Math.Min(255, avgG * saturationBoost);
            avgB = (byte)Math.Min(255, avgB * saturationBoost);
            
            return Windows.UI.Color.FromArgb(255, avgR, avgG, avgB);
        }
        
        private void AnimateBackdropColor(Windows.UI.Color targetColor)
        {
            // Stop any existing animation
            if (_backdropAnimationTimer != null)
            {
                _backdropAnimationTimer.Stop();
                _backdropAnimationTimer = null;
            }
            
            var startColor = _currentBackdropColor;
            var steps = 18;
            var stepDuration = TimeSpan.FromMilliseconds(22);
            int currentStep = 0;
            
            _backdropAnimationTimer = new DispatcherTimer { Interval = stepDuration };
            _backdropAnimationTimer.Tick += (s, e) =>
            {
                currentStep++;
                float t = (float)currentStep / steps;
                t = 1 - (1 - t) * (1 - t); // EaseOut
                
                byte r = (byte)(startColor.R + (targetColor.R - startColor.R) * t);
                byte g = (byte)(startColor.G + (targetColor.G - startColor.G) * t);
                byte b = (byte)(startColor.B + (targetColor.B - startColor.B) * t);
                
                _currentBackdropColor = Windows.UI.Color.FromArgb(255, r, g, b);
                
                // Bloom uses +40 brightness for "light source" effect
                byte br = (byte)Math.Min(255, r + 40);
                byte bg = (byte)Math.Min(255, g + 40);
                byte bb = (byte)Math.Min(255, b + 40);
                
                // Apply brushes to Grid.Background (original working approach)
                // Layer 1: Ambient - wide soft wash
                AmbientLayer.Background = CreateRadialBrush(r, g, b, 255, 100, 0.0, 0.0, 1.8, 1.4);
                // Layer 2: Primary - focused vibrant glow
                PrimaryGlowLayer.Background = CreateRadialBrush(r, g, b, 255, 150, 0.1, 0.15, 0.9, 0.7);
                // Layer 3: Bloom - bright corner highlight
                BloomLayer.Background = CreateRadialBrush(br, bg, bb, 255, 180, 0.0, 0.0, 0.4, 0.3);
                // Layer 4: Secondary top-right light (slightly desaturated)
                byte sr = (byte)((r + 255) / 2); // Blend with white for subtle effect
                byte sg = (byte)((g + 255) / 2);
                byte sb = (byte)((b + 255) / 2);
                SecondaryGlowLayer.Background = CreateRadialBrush(sr, sg, sb, 200, 80, 1.0, 0.0, 0.6, 0.5);
                
                if (currentStep >= steps)
                {
                    _backdropAnimationTimer?.Stop();
                    _backdropAnimationTimer = null;
                }
            };
            _backdropAnimationTimer.Start();
        }
        
        private RadialGradientBrush CreateRadialBrush(byte r, byte g, byte b, byte alpha1, byte alpha2, 
            double centerX, double centerY, double radiusX, double radiusY)
        {
            var brush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(centerX, centerY),
                RadiusX = radiusX,
                RadiusY = radiusY,
                GradientOrigin = new Windows.Foundation.Point(centerX, centerY)
            };
            // 6 gradient stops for smooth anti-banding
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(alpha1, r, g, b), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb((byte)(alpha1 * 0.85), r, g, b), Offset = 0.15 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb((byte)(alpha1 * 0.60), r, g, b), Offset = 0.35 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(alpha2, r, g, b), Offset = 0.55 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb((byte)(alpha2 * 0.35), r, g, b), Offset = 0.8 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, r, g, b), Offset = 1 });
            return brush;
        }
    }
}
