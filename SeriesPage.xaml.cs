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
using ModernIPTVPlayer.Models;

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
             Frame.Navigate(typeof(MediaInfoPage), new ModernIPTVPlayer.Models.MediaNavigationArgs(e), new SuppressNavigationTransitionInfo());
        }

        private void MediaGrid_PlayAction(object sender, ModernIPTVPlayer.Models.IMediaStream e)
        {
            // For Series, play intent usually means resume or play first.
            // Navigating to details allows user to pick.
            Frame.Navigate(typeof(MediaInfoPage), new ModernIPTVPlayer.Models.MediaNavigationArgs(e), new SuppressNavigationTransitionInfo());
        }
        
        private void MediaGrid_DetailsAction(object sender, ModernIPTVPlayer.Models.MediaNavigationArgs e)
        {
             // Navigate to new MediaInfoPage with animation
             Frame.Navigate(typeof(MediaInfoPage), e, new SuppressNavigationTransitionInfo());
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

        // Global list of all series
        private List<SeriesStream> _allSeries = new();

        private async Task LoadSeriesCategoriesAsync()
        {
            string api = "";
            try
            {
                LoadingRing.IsActive = true;
                CategoryListView.ItemsSource = null;
                MediaGrid.ItemsSource = null;
                _allCategories.Clear();
                _allSeries.Clear();
                
                string username = _loginInfo.Username;
                string password = _loginInfo.Password;
                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string playlistId = AppSettings.LastPlaylistId?.ToString() ?? "default";

                // -------------------------------------------------------------
                // 1. DATA FETCHING (Cache First -> Network Fallback)
                // -------------------------------------------------------------
                
                // A. Categories
                var cachedCats = await Services.ContentCacheService.Instance.LoadCacheAsync<SeriesCategory>(playlistId, "series_cats");
                if (cachedCats != null)
                {
                    _allCategories = cachedCats;
                     System.Diagnostics.Debug.WriteLine("[SeriesPage] Loaded Categories from Cache");
                }
                else
                {
                    api = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_series_categories";
                    string json = await _httpClient.GetStringAsync(api);
                    
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
                    _allCategories = JsonSerializer.Deserialize<List<SeriesCategory>>(json, options) ?? new List<SeriesCategory>();
                    
                    // Save
                    _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "series_cats", _allCategories);
                }
                
                CategoryListView.ItemsSource = _allCategories;

                // B. Global Series List
                var cachedSeries = await Services.ContentCacheService.Instance.LoadCacheAsync<SeriesStream>(playlistId, "series_list");
                if (cachedSeries != null && cachedSeries.Count > 0)
                {
                    _allSeries = cachedSeries;
                    System.Diagnostics.Debug.WriteLine($"[SeriesPage] Loaded {_allSeries.Count} Series from Cache");
                }
                else
                {
                    // Fetch ALL Series
                    // Usually 'get_series' returns all
                    string seriesApi = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_series";
                    string seriesJson = await _httpClient.GetStringAsync(seriesApi);

                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
                    var seriesList = JsonSerializer.Deserialize<List<SeriesStream>>(seriesJson, options);

                    if (seriesList != null)
                    {
                        // Fix Covers URLs if needed? usually they come with full path or partial.
                        // Assuming they are fine or we fix in binding. 
                        _allSeries = seriesList;
                        
                        // Save
                        _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "series_list", _allSeries);
                    }
                }

                // -------------------------------------------------------------
                // 2. UI INITIALIZATION
                // -------------------------------------------------------------
                
                if (_allCategories.Count > 0)
                {
                    // Restoration Logic
                    var lastId = AppSettings.LastSeriesCategoryId;
                    var targetCat = _allCategories.FirstOrDefault(c => c.CategoryId == lastId) ?? _allCategories[0];

                    CategoryListView.SelectedItem = targetCat;
                    CategoryListView.ScrollIntoView(targetCat);
                    await LoadSeriesAsync(targetCat);
                }
            }
            catch (Exception ex)
            {
                await ShowMessageDialog("Kritik Hata (Dizi Kategori)", $"Hata: {ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private async Task LoadSeriesAsync(SeriesCategory category)
        {
            SelectedCategoryTitle.Text = category.CategoryName;

            // Save selection
            AppSettings.LastSeriesCategoryId = category?.CategoryId;

            try
            {
                MediaGrid.IsLoading = true;
                
                // Filter locally on thread pool
                var filtered = await Task.Run(() => 
                {
                    if (_allSeries == null || _allSeries.Count == 0) return new List<SeriesStream>();
                    return _allSeries.Where(s => s.CategoryId == category.CategoryId).ToList();
                });

                category.Series = filtered;
                MediaGrid.ItemsSource = new List<ModernIPTVPlayer.Models.IMediaStream>(category.Series);
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"Error filtering series: {ex.Message}");
            }
            finally
            {
                MediaGrid.IsLoading = false;
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

        private async Task ShowMessageDialog(string title, string content)
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
