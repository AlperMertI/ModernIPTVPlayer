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
using System.Linq;

namespace ModernIPTVPlayer
{
    public sealed partial class MoviesPage : Page
    {
        private LoginParams? _loginInfo;
        private HttpClient _httpClient;
        // _currentBackdropColor removed
        // _colorCache removed
        // _lastProcessedImageUrl removed
        public MoviesPage()
        {
            this.InitializeComponent();
            _httpClient = HttpHelper.Client;
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

        private void PosterCard_ColorsExtracted(object sender, (Windows.UI.Color Primary, Windows.UI.Color Secondary) colors)
        {
             // Update the global backdrop when a poster decides its colors are ready/hovered
             BackdropControl.TransitionTo(colors.Primary, colors.Secondary);
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
        
        private void MovieItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is GridViewItem item && item.Content is LiveStream stream && !string.IsNullOrEmpty(stream.IconUrl))
            {
                System.Diagnostics.Debug.WriteLine($"\n=== HOVER: {stream.Name} ===");
                
                // 2. Update local GlowEffect with extensive debugging
                var glow = GetTemplateChild<Border>(item, "GlowEffect");
                System.Diagnostics.Debug.WriteLine($"GLOW DEBUG: GetTemplateChild returned {(glow != null ? "Border" : "NULL")}");
                
                if (glow != null)
                {
                    System.Diagnostics.Debug.WriteLine($"  ActualSize: {glow.ActualWidth}x{glow.ActualHeight}");
                    System.Diagnostics.Debug.WriteLine($"  Margin: {glow.Margin}");
                    System.Diagnostics.Debug.WriteLine($"  Visibility: {glow.Visibility}, Opacity: {glow.Opacity}");
                    
                    // Check parent
                    var parent = glow.Parent as FrameworkElement;
                    if (parent != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"  Parent Type: {parent.GetType().Name}");
                        System.Diagnostics.Debug.WriteLine($"  Parent Size: {parent.ActualWidth}x{parent.ActualHeight}");
                    }
                    
                    
                    // Apply color to the radial gradient stop
                    if (glow.Background is RadialGradientBrush brush)
                    {
                        // The actual color will be set by the PosterCard_ColorsExtracted event handler
                        // This part might need to be updated if the glow color is also dynamic based on the extracted colors
                        // For now, we'll assume the glow color is handled by the PosterCard itself or a default.
                        // If the glow color needs to be set here, we'd need the extracted colors passed or retrieved.
                        // For this change, we're removing the direct color extraction from here.
                    }
                }
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
        

    }
}
