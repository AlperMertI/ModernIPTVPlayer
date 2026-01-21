using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModernIPTVPlayer
{
    public sealed partial class MoviesPage : Page
    {
        private LoginParams? _loginInfo;
        private HttpClient _httpClient;

        public MoviesPage()
        {
            this.InitializeComponent();
            _httpClient = HttpHelper.Client;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Try to recover login params from AppSettings if not passed directly (since NavView navigation might not pass params)
            if (e.Parameter is LoginParams p)
            {
                _loginInfo = p;
            }
            else if (App.CurrentLogin != null)
            {
                _loginInfo = App.CurrentLogin;
            }
            else
            {
                // Reconstruct from AppSettings (Last fallback)
                if (AppSettings.LastLoginType == 1) // Xtream
                {
                    _loginInfo = new LoginParams
                    {
                        Host = AppSettings.SavedHost,
                        Username = AppSettings.SavedUsername,
                        Password = AppSettings.SavedPassword
                    };
                }
            }

            if (_loginInfo != null && !string.IsNullOrEmpty(_loginInfo.Host))
            {
                if (CategoryListView.ItemsSource != null) return;
                await LoadVodCategoriesAsync();
            }
        }

        private async Task LoadVodCategoriesAsync()
        {
            try
            {
                LoadingRing.IsActive = true;
                CategoryListView.ItemsSource = null;
                MovieGridView.ItemsSource = null;

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string api = $"{baseUrl}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_vod_categories";

                string json = await _httpClient.GetStringAsync(api);
                var categories = JsonSerializer.Deserialize<List<LiveCategory>>(json);

                if (categories != null)
                {
                    CategoryListView.ItemsSource = categories;
                }
            }
            catch (Exception ex)
            {
                // Silent fail or log
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private async Task LoadVodStreamsAsync(LiveCategory category)
        {
            if (category.Channels != null && category.Channels.Count > 0)
            {
                MovieGridView.ItemsSource = category.Channels;
                return;
            }

            try
            {
                MovieLoadingRing.IsActive = true;
                MovieGridView.ItemsSource = null;

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string api = $"{baseUrl}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_vod_streams&category_id={category.CategoryId}";

                string json = await _httpClient.GetStringAsync(api);
                
                // Note: VOD streams have slightly different JSON fields usually, but LiveStream model might fit if fields match (name, stream_id, stream_icon).
                // Usually extension is in 'container_extension'
                var streams = JsonSerializer.Deserialize<List<LiveStream>>(json);

                if (streams != null)
                {
                    foreach (var s in streams)
                    {
                        // Use container_extension from API if available, fallback to mp4
                        string extension = !string.IsNullOrEmpty(s.ContainerExtension) ? s.ContainerExtension : "mp4";
                        s.StreamUrl = $"{baseUrl}/movie/{_loginInfo.Username}/{_loginInfo.Password}/{s.StreamId}.{extension}"; 
                    }

                    category.Channels = streams;
                    MovieGridView.ItemsSource = category.Channels;
                }
            }
            catch (Exception ex)
            {
                // Log
            }
            finally
            {
                MovieLoadingRing.IsActive = false;
            }
        }

        private async void CategoryListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveCategory category)
            {
                await LoadVodStreamsAsync(category);
            }
        }

        private void MovieGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveStream stream)
            {
                // Play VOD
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name));
            }
        }
    }
}
