using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
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
    public sealed partial class SeriesPage : Page
    {
        private LoginParams? _loginInfo;
        private HttpClient _httpClient;
        public SeriesPage()
        {
            this.InitializeComponent();
            _httpClient = HttpHelper.Client;
            
            MediaGrid.ItemClicked += MediaGrid_ItemClicked;
            MediaGrid.PlayAction += MediaGrid_PlayAction;
            MediaGrid.DetailsAction += MediaGrid_DetailsAction;
            MediaGrid.AddListAction += MediaGrid_AddListAction;
            MediaGrid.ColorExtracted += MediaGrid_ColorExtracted;
        }

        private void MediaGrid_ItemClicked(object sender, ModernIPTVPlayer.Models.IMediaStream e)
        {
             Frame.Navigate(typeof(MediaInfoPage), e, new DrillInNavigationTransitionInfo());
        }

        private void MediaGrid_PlayAction(object sender, ModernIPTVPlayer.Models.IMediaStream e)
        {
            // For Series, play intent usually means resume or play first.
            // Navigating to details allows user to pick.
            Frame.Navigate(typeof(MediaInfoPage), e, new DrillInNavigationTransitionInfo());
        }
        
        private void MediaGrid_DetailsAction(object sender, ModernIPTVPlayer.Models.IMediaStream e)
        {
             // Navigate to new MediaInfoPage with animation
             Frame.Navigate(typeof(MediaInfoPage), e, new DrillInNavigationTransitionInfo());
        }
        
        private void MediaGrid_AddListAction(object sender, ModernIPTVPlayer.Models.IMediaStream e)
        {
            ShowMessageDialog("Listem", "Listeye Eklendi");
        }
        
        private void MediaGrid_ColorExtracted(object sender, (Windows.UI.Color Primary, Windows.UI.Color Secondary) colors)
        {
            BackdropControl.TransitionTo(colors.Primary, colors.Secondary);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is LoginParams p)
            {
                if (_loginInfo != null && _loginInfo.PlaylistUrl != p.PlaylistUrl)
                {
                    CategoryListView.ItemsSource = null;
                    MediaGrid.ItemsSource = null;
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
            string api = "";
            try
            {
                LoadingRing.IsActive = true;
                CategoryListView.ItemsSource = null;
                MediaGrid.ItemsSource = null;
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

                    // Auto-select first category
                    if (_allCategories.Count > 0)
                    {
                        CategoryListView.SelectedIndex = 0;
                        await LoadSeriesAsync(_allCategories[0]);
                    }
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

        private async Task LoadSeriesAsync(SeriesCategory category)
        {
            SelectedCategoryTitle.Text = category.CategoryName;

            if (category.Series != null && category.Series.Count > 0)
            {
                MediaGrid.ItemsSource = new List<ModernIPTVPlayer.Models.IMediaStream>(category.Series);
                return;
            }

            try
            {
                // SHOW SKELETON
                MediaGrid.IsLoading = true;
                
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
                    MediaGrid.ItemsSource = new List<ModernIPTVPlayer.Models.IMediaStream>(category.Series);
                }
            }
            catch (Exception ex)
            {
                // Log
            }
            finally
            {
                if (MediaGrid.ItemsSource == null) MediaGrid.IsLoading = false;
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
