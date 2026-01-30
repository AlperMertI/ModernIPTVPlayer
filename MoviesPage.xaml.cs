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
using ModernIPTVPlayer.Models;
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

        // Global list of all movies for search/filter
        private List<LiveStream> _allMovies = new();

        private async Task LoadVodCategoriesAsync()
        {
            try
            {
                LoadingRing.IsActive = true;
                CategoryListView.ItemsSource = null;
                MediaGrid.ItemsSource = null;
                _allCategories.Clear();
                _allMovies.Clear();

                // -------------------------------------------------------------
                // 1. DATA FETCHING (Cache First -> Network Fallback)
                // -------------------------------------------------------------
                string username = _loginInfo.Username;
                string password = _loginInfo.Password;
                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string playlistId = AppSettings.LastPlaylistId?.ToString() ?? "default";

                // A. Load Categories
                var cachedCats = await Services.ContentCacheService.Instance.LoadCacheAsync<LiveCategory>(playlistId, "vod_cats");
                if (cachedCats != null)
                {
                     _allCategories = cachedCats;
                     System.Diagnostics.Debug.WriteLine("[MoviesPage] Loaded Categories from Cache");
                }
                else
                {
                    // Network Fetch Categories
                    string api = $"{baseUrl}/player_api.php?username={username}&password={password}&action=get_vod_categories";
                    string json = await _httpClient.GetStringAsync(api);
                    
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
                    _allCategories = JsonSerializer.Deserialize<List<LiveCategory>>(json, options) ?? new List<LiveCategory>();
                    
                    // Save
                    _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "vod_cats", _allCategories);
                }

                CategoryListView.ItemsSource = _allCategories;

                // B. Load All Streams (Global Movie List)
                // This enables instant category switching and global search
                var cachedStreams = await Services.ContentCacheService.Instance.LoadCacheAsync<LiveStream>(playlistId, "vod_streams");
                if (cachedStreams != null && cachedStreams.Count > 0)
                {
                    _allMovies = cachedStreams;

                    // Wait for Probe Cache to be ready (Race Condition fix)
                    await Services.ProbeCacheService.Instance.EnsureLoadedAsync();

                    // Hydrate Metadata from ProbeCache
                    foreach (var m in _allMovies)
                    {
                        if (Services.ProbeCacheService.Instance.Get(m.StreamUrl) is Services.ProbeData pd)
                        {
                            m.Resolution = pd.Resolution;
                            m.Codec = pd.Codec;
                            m.Bitrate = pd.Bitrate;
                            m.Fps = pd.Fps;
                            m.IsHdr = pd.IsHdr;
                            m.IsOnline = true; 
                        }
                    }
                    System.Diagnostics.Debug.WriteLine($"[MoviesPage] Loaded {_allMovies.Count} Movies from Cache");
                }
                else
                {
                    // Fetch ALL Streams
                    // Note: 'get_vod_streams' without category_id usually returns ALL
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
                          _allMovies = streams;
                          
                          // Wait for Probe Cache (Race Condition fix)
                          await Services.ProbeCacheService.Instance.EnsureLoadedAsync();

                          // Hydrate Metadata from ProbeCache (Network Path)
                          foreach (var m in _allMovies)
                          {
                              if (Services.ProbeCacheService.Instance.Get(m.StreamUrl) is Services.ProbeData pd)
                              {
                                  m.Resolution = pd.Resolution;
                                  m.Codec = pd.Codec;
                                  m.Bitrate = pd.Bitrate;
                                  m.Fps = pd.Fps;
                                  m.IsHdr = pd.IsHdr;
                                  m.IsOnline = true; 
                              }
                          }

                          // Save
                          _ = Services.ContentCacheService.Instance.SaveCacheAsync(playlistId, "vod_streams", _allMovies);
                     }
                }

                // -------------------------------------------------------------
                // 2. UI INITIALIZATION
                // -------------------------------------------------------------
                
                // Pre-populate category relationships (Optional, but good for local filtering if we want to assign 'Channels' prop)
                // For now, we'll just filter on demand in LoadVodStreamsAsync logic
                
                // Select First
                if (_allCategories.Count > 0)
                {
                    // Restoration Logic
                    var lastId = AppSettings.LastVodCategoryId;
                    var targetCat = _allCategories.FirstOrDefault(c => c.CategoryId == lastId) ?? _allCategories[0];
                    
                    CategoryListView.SelectedItem = targetCat;
                    CategoryListView.ScrollIntoView(targetCat);
                    await LoadVodStreamsAsync(targetCat);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading movies: {ex.Message}");
                await ShowMessageDialog("Hata", $"Film listesi yüklenemedi: {ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private async Task LoadVodStreamsAsync(LiveCategory category)
        {
            SelectedCategoryTitle.Text = category.CategoryName;
            
            // Save selection
            AppSettings.LastVodCategoryId = category?.CategoryId;
            
            // Logic: Filter from _allMovies
            try
            {
                MediaGrid.IsLoading = true;
                
                // Run on thread pool if list is huge
                var filtered = await Task.Run(() => 
                {
                    if (_allMovies == null || _allMovies.Count == 0) return new List<LiveStream>();
                    
                    // Filter matching CategoryId
                    return _allMovies.Where(m => m.CategoryId == category.CategoryId).ToList();
                });

                category.Channels = filtered;
                MediaGrid.ItemsSource = new List<ModernIPTVPlayer.Models.IMediaStream>(filtered);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Filter error: {ex.Message}");
            }
            finally
            {
                MediaGrid.IsLoading = false;
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
            Frame.Navigate(typeof(MediaInfoPage), new MediaNavigationArgs(e), new SuppressNavigationTransitionInfo());
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

        private void MediaGrid_DetailsAction(object sender, ModernIPTVPlayer.Models.MediaNavigationArgs e)
        {
             // Navigate to new MediaInfoPage with animation
             Frame.Navigate(typeof(MediaInfoPage), e, new SuppressNavigationTransitionInfo());
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

        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            // Handled inside UnifiedMediaGrid now, or we can forward if needed.
            // But since MediaGrid handles its own popup closing, we might not need this here 
            // unless we want to force close from the page level.
        }
        

    }
}
