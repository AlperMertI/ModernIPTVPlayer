using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModernIPTVPlayer
{
    public sealed partial class SeriesPage : Page
    {
        private LoginParams? _loginInfo;
        private HttpClient _httpClient;

        public SeriesPage()
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
                    SeriesGridView.ItemsSource = null;
                }
                _loginInfo = p;
            }

            if (_loginInfo != null && !string.IsNullOrEmpty(_loginInfo.Host))
            {
                if (CategoryListView.ItemsSource != null) return;
                await LoadSeriesCategoriesAsync();
            }
        }

        private List<SeriesCategory> _allCategories = new();

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
                var filtered = new List<SeriesCategory>();
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

        private async Task LoadSeriesCategoriesAsync()
        {
            ShowMessageDialog("Debug (Dizi)", $"Yükleme başlıyor... Host: {_loginInfo?.Host}");
            
            string api = "";
            try
            {
                LoadingRing.IsActive = true;
                CategoryListView.ItemsSource = null;
                SeriesGridView.ItemsSource = null;
                _allCategories.Clear();

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                api = $"{baseUrl}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_series_categories";

                string json = await _httpClient.GetStringAsync(api);
                
                if (string.IsNullOrEmpty(json))
                {
                    ShowMessageDialog("Hata", "Dizi kategorileri API'den boş cevap döndü.");
                    return;
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };

                var categories = JsonSerializer.Deserialize<List<SeriesCategory>>(json, options);

                if (categories != null)
                {
                    _allCategories = categories;
                    CategoryListView.ItemsSource = _allCategories;
                }
                else
                {
                    ShowMessageDialog("Hata", "Dizi kategorileri JSON parse edilemedi.");
                }
            }
            catch (Exception ex)
            {
                ShowMessageDialog("Kritik Hata (Dizi Kategori)", $"Hata: {ex.Message}\nURL: {api}");
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private List<int> _skeletonList = new List<int>(new int[20]);
        
        private async Task LoadSeriesAsync(SeriesCategory category)
        {
            SelectedCategoryTitle.Text = category.CategoryName;

            if (category.Series != null && category.Series.Count > 0)
            {
                SeriesGridView.ItemsSource = category.Series;
                SeriesGridView.Visibility = Visibility.Visible;
                SkeletonGrid.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                // SHOW SKELETON
                SeriesGridView.Visibility = Visibility.Collapsed;
                SkeletonGrid.Visibility = Visibility.Visible;
                SkeletonGrid.ItemsSource = _skeletonList;
                
                SeriesGridView.ItemsSource = null;

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string api = $"{baseUrl}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_series&category_id={category.CategoryId}";

                string json = await _httpClient.GetStringAsync(api);
                
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };

                var seriesList = JsonSerializer.Deserialize<List<SeriesStream>>(json, options);

                if (seriesList != null)
                {
                    category.Series = seriesList;
                    SeriesGridView.ItemsSource = category.Series;
                }
            }
            catch (Exception ex)
            {
                // Log
            }
            finally
            {
                SkeletonGrid.Visibility = Visibility.Collapsed;
                SeriesGridView.Visibility = Visibility.Visible;
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
            if (e.ClickedItem is SeriesCategory category)
            {
                await LoadSeriesAsync(category);
            }
        }

        private void SeriesGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SeriesStream series)
            {
                // Series Click - Usually opens episodes.
                // For now, we don't have an EpisodesPage.
                // Just show Dialog saying "Coming Soon" or similar?
                ShowMessageDialog("Dizi Bilgisi", $"'{series.Name}' dizisi seçildi. Bölümler özelliği hazırlanıyor.");
            }
        }

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
        // 3D TILT EFFECT & CONTAINER LOGIC
        // ==========================================
        
        private void SeriesGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                var container = args.ItemContainer;
                container.PointerMoved -= SeriesItem_PointerMoved;
                container.PointerExited -= SeriesItem_PointerExited;
            }
            else
            {
                var container = args.ItemContainer;
                 if (container.Background == null) container.Background = new SolidColorBrush(Colors.Transparent);

                container.PointerMoved -= SeriesItem_PointerMoved;
                container.PointerMoved += SeriesItem_PointerMoved;
                
                container.PointerExited -= SeriesItem_PointerExited;
                container.PointerExited += SeriesItem_PointerExited;
            }
        }

        private void SeriesItem_PointerMoved(object sender, PointerRoutedEventArgs e)
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

        private void SeriesItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is GridViewItem item)
            {
                var rootGrid = GetTemplateChild<Grid>(item, "RootGrid");
                if (rootGrid != null && rootGrid.Projection is PlaneProjection projection)
                {
                     projection.RotationX = 0;
                     projection.RotationY = 0;
                }
            }
        }

        private T GetTemplateChild<T>(DependencyObject parent, string name) where T : DependencyObject
        {
             int count = VisualTreeHelper.GetChildrenCount(parent);
             for (int i = 0; i < count; i++)
             {
                 var child = VisualTreeHelper.GetChild(parent, i);
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
    }
}
