using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

// NOTE: Series API returns different JSON structure ("series_id", "cover" instead of "stream_id", "stream_icon").
// We might need a separate model, but for now we can try to reuse LiveStream if we map it or if JSON deserializer is flexible.
// Actually, Xtream Series JSON: [ { "num": 1, "name": "...", "series_id": 123, "cover": "..." }, ... ]
// LiveStream has: Name, StreamId (int), IconUrl (stream_icon).
// We need to handle mapping. Or update LiveStream to allow alias properties. Best to use JsonPropertyName or a new model.
// For speed, let's add [JsonPropertyName("series_id")] to StreamId etc in LiveStream.cs FIRST.

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
                _loginInfo = p;
            }
            else if (App.CurrentLogin != null)
            {
                _loginInfo = App.CurrentLogin;
            }
            else
            {
                if (AppSettings.LastLoginType == 1) 
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
                await LoadSeriesCategoriesAsync();
            }
        }

        private async Task LoadSeriesCategoriesAsync()
        {
            try
            {
                LoadingRing.IsActive = true;
                CategoryListView.ItemsSource = null;
                SeriesGridView.ItemsSource = null;

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string api = $"{baseUrl}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_series_categories";

                string json = await _httpClient.GetStringAsync(api);
                var categories = JsonSerializer.Deserialize<List<LiveCategory>>(json);

                if (categories != null)
                {
                    CategoryListView.ItemsSource = categories;
                }
            }
            catch (Exception ex)
            {
                // Silent fail
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private async Task LoadSeriesAsync(LiveCategory category)
        {
            if (category.Channels != null && category.Channels.Count > 0)
            {
                SeriesGridView.ItemsSource = category.Channels;
                return;
            }

            try
            {
                SeriesLoadingRing.IsActive = true;
                SeriesGridView.ItemsSource = null;

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string api = $"{baseUrl}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_series&category_id={category.CategoryId}";

                string json = await _httpClient.GetStringAsync(api);
                var seriesList = JsonSerializer.Deserialize<List<LiveStream>>(json);

                if (seriesList != null)
                {
                    category.Channels = seriesList;
                    SeriesGridView.ItemsSource = category.Channels;
                }
            }
            catch (Exception ex)
            {
                // Log
            }
            finally
            {
                SeriesLoadingRing.IsActive = false;
            }
        }

        private async void CategoryListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveCategory category)
            {
                await LoadSeriesAsync(category);
            }
        }

        private void SeriesGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveStream stream)
            {
                // Series Click - Usually opens episodes.
                // For now, we don't have an EpisodesPage.
                // Just show Dialog saying "Coming Soon" or similar?
                ShowMessageDialog("Bilgi", "Dizi detayları ve bölümler özelliği yakında eklenecek.");
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
    }
}
