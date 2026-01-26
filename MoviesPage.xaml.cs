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
using ModernIPTVPlayer.Controls;
using System.Numerics;

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
            
            // Wire up MediaGrid events
            MediaGrid.ItemClicked += MediaGrid_ItemClicked;
            MediaGrid.PlayAction += MediaGrid_PlayAction;
            MediaGrid.DetailsAction += MediaGrid_DetailsAction;
            MediaGrid.AddListAction += MediaGrid_AddListAction;
            MediaGrid.ColorExtracted += MediaGrid_ColorExtracted;
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
                if (CategoryListView.ItemsSource == null)
                {
                    await LoadVodCategoriesAsync();
                }
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
                MediaGrid.ItemsSource = null;
                _allCategories.Clear();

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string api = $"{baseUrl}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_vod_categories";

                string json = await _httpClient.GetStringAsync(api);
                
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
                // ShowMessageDialog("Kritik Hata", $"Veri çekilirken hata oluştu:\n{ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private async Task LoadVodStreamsAsync(LiveCategory category)
        {
            SelectedCategoryTitle.Text = category.CategoryName;

            if (category.Channels != null && category.Channels.Count > 0)
            {
                // Note: LiveStream implements IMediaStream due to our changes :)
                MediaGrid.ItemsSource = new List<ModernIPTVPlayer.Models.IMediaStream>(category.Channels);
                return;
            }

            try
            {
                // SHOW SKELETON
                MediaGrid.IsLoading = true;
                
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
                    MediaGrid.ItemsSource = new List<ModernIPTVPlayer.Models.IMediaStream>(category.Channels);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading movies: {ex.Message}");
            }
            finally
            {
                // HIDE SKELETON (Handled by setter if ItemsSource is not null, but explicit is fine)
                // If error, maybe empty list?
                if (MediaGrid.ItemsSource == null) MediaGrid.IsLoading = false; 
            }
        }
        
        private async void CategoryListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveCategory category)
            {
                await LoadVodStreamsAsync(category);
            }
        }

        // ==========================================
        // UNIFIED GRID EVENTS
        // ==========================================

        private void MediaGrid_ItemClicked(object sender, ModernIPTVPlayer.Models.IMediaStream e)
        {
            Frame.Navigate(typeof(MediaInfoPage), e, new DrillInNavigationTransitionInfo());
        }

        private void MediaGrid_PlayAction(object sender, ModernIPTVPlayer.Models.IMediaStream e)
        {
             if (e is LiveStream stream)
             {
                 Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name));
             }
             // If implicit cast needed for SeriesStream, handle it (SeriesStream currently doesn't inherit LiveStream but implements IMediaStream)
             // Series playback should ideally play first episode or resume.
             // For now just handle LiveStream or Log
        }

        private void MediaGrid_DetailsAction(object sender, ModernIPTVPlayer.Models.IMediaStream e)
        {
             // Navigate to new MediaInfoPage with animation
             Frame.Navigate(typeof(MediaInfoPage), e, new DrillInNavigationTransitionInfo());
        }

        private void MediaGrid_AddListAction(object sender, ModernIPTVPlayer.Models.IMediaStream e)
        {
             ShowMessageDialog("Listem", "Listenize eklendi.");
        }

        private void MediaGrid_ColorExtracted(object sender, (Windows.UI.Color Primary, Windows.UI.Color Secondary) colors)
        {
            BackdropControl.TransitionTo(colors.Primary, colors.Secondary);
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog searchDialog = new ContentDialog
            {
                Title = "Global Arama",
                Content = "Bu özellik (Spotlight Search) yakında eklenecek.",
                CloseButtonText = "Kapat",
                XamlRoot = this.XamlRoot
            };
            await searchDialog.ShowAsync();
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

        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            // Handled inside UnifiedMediaGrid now, or we can forward if needed.
            // But since MediaGrid handles its own popup closing, we might not need this here 
            // unless we want to force close from the page level.
        }
        

    }
}
