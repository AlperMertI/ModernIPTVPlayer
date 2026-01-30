using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using System.Text.Json;

namespace ModernIPTVPlayer
{
    public sealed partial class LoginPage : Page
    {
        private bool _isLoading = false;
        private System.Collections.ObjectModel.ObservableCollection<Playlist> _playlists = new();

        public LoginPage()
        {
            this.InitializeComponent();
            this.Loaded += LoginPage_Loaded;
        }

        private void LoginPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadPlaylists();
            
            // Check for auto-login only on startup (when App.CurrentLogin is null)
            if (App.CurrentLogin == null)
            {
                CheckAutoLogin();
            }
        }

        private void CheckAutoLogin()
        {
            var lastId = AppSettings.LastPlaylistId;
            if (lastId.HasValue)
            {
                foreach (var p in _playlists)
                {
                    if (p.Id == lastId.Value)
                    {
                        System.Diagnostics.Debug.WriteLine($"Auto-login found for: {p.Name}");
                        _ = LoginWithPlaylist(p);
                        break;
                    }
                }
            }
        }

        private void LoadPlaylists()
        {
            try
            {
                var json = AppSettings.PlaylistsJson;
                var list = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<Playlist>>(json) 
                           ?? new System.Collections.Generic.List<Playlist>();
                
                _playlists.Clear();
                foreach (var p in list) _playlists.Add(p);
                
                PlaylistListView.ItemsSource = _playlists;
                UpdateEmptyState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading playlists: {ex.Message}");
            }
        }

        private void UpdateEmptyState()
        {
            EmptyStatePanel.Visibility = _playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PlaylistListView.Visibility = _playlists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveAllPlaylists()
        {
            var list = new System.Collections.Generic.List<Playlist>(_playlists);
            AppSettings.PlaylistsJson = System.Text.Json.JsonSerializer.Serialize(list);
            UpdateEmptyState();
        }

        private async void AddPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new PlaylistDialog { XamlRoot = this.XamlRoot };
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                dialog.PrepareResult();
                _playlists.Add(dialog.Result);
                SaveAllPlaylists();
            }
        }

        private async void EditPlaylist_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var playlist = btn?.Tag as Playlist;
            if (playlist == null) return;

            var dialog = new PlaylistDialog(playlist) { XamlRoot = this.XamlRoot };
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                dialog.PrepareResult();
                // Find and update in collection
                var index = _playlists.IndexOf(playlist);
                if (index != -1)
                {
                    _playlists[index] = dialog.Result;
                    SaveAllPlaylists();
                }
            }
        }

        private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var playlist = btn?.Tag as Playlist;
            if (playlist == null) return;

            ContentDialog deleteDialog = new ContentDialog
            {
                Title = "Playlisti Sil",
                Content = $"'{playlist.Name}' playlistini silmek istediğinize emin misiniz?",
                PrimaryButtonText = "Sil",
                CloseButtonText = "İptal",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            if (await deleteDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _playlists.Remove(playlist);
                SaveAllPlaylists();
            }
        }

        private void PlaylistListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Playlist playlist)
            {
                _ = LoginWithPlaylist(playlist);
            }
        }

        private async Task LoginWithPlaylist(Playlist p)
        {
            if (p.Type == PlaylistType.M3u)
            {
                await AttemptLogin(p.Url, 0, p);
            }
            else
            {
                string cleanHost = CleanHost(p.Host);
                string authUrl = $"{cleanHost}/player_api.php?username={p.Username}&password={p.Password}";
                string playlistUrl = $"{cleanHost}/get.php?username={p.Username}&password={p.Password}&type=m3u_plus&output=ts";
                await AttemptLogin(authUrl, 1, p, playlistUrl, cleanHost);
            }
        }

        private string CleanHost(string rawHost)
        {
            if (string.IsNullOrEmpty(rawHost)) return "";
            string host = rawHost.Trim();
            if (!host.StartsWith("http")) host = "http://" + host;
            host = host.TrimEnd('/');
            if (host.EndsWith("/get.php")) host = host.Substring(0, host.Length - "/get.php".Length);
            if (host.EndsWith("/player_api.php")) host = host.Substring(0, host.Length - "/player_api.php".Length);
            return host.TrimEnd('/');
        }

        private async Task AttemptLogin(string checkUrl, int loginType, Playlist p, string? finalPlaylistUrl = null, string? manualHost = null)
        {
            if (_isLoading) return;
            _isLoading = true;
            SetLoadingState(true);

            string targetUrl = finalPlaylistUrl ?? checkUrl;

            try
            {
                HttpResponseMessage response = await HttpHelper.Client.GetAsync(checkUrl, HttpCompletionOption.ResponseHeadersRead);
                
                if (response.IsSuccessStatusCode)
                {
                    AppSettings.LastLoginType = loginType;
                    AppSettings.LastPlaylistId = p.Id; // Persist the successful ID

                    string authJson = await response.Content.ReadAsStringAsync();
                    int maxCons = 1;
                    try
                    {
                        var authData = JsonSerializer.Deserialize<XtreamAuthResponse>(authJson);
                        if (authData?.UserInfo != null)
                        {
                            maxCons = authData.UserInfo.MaxConnections;
                            System.Diagnostics.Debug.WriteLine($"[Login] Max Connections: {maxCons}");
                        }
                    }
                    catch { /* Fallback to 1 */ }

                    App.CurrentLogin = new LoginParams 
                    { 
                        PlaylistUrl = targetUrl,
                        Host = (loginType == 1) ? manualHost : null,
                        Username = (loginType == 1) ? p.Username : null,
                        Password = (loginType == 1) ? p.Password : null,
                        MaxConnections = maxCons
                    };

                    Frame.Navigate(typeof(LiveTVPage), App.CurrentLogin);
                }
                else
                {
                    string msg = $"Server Error: {response.StatusCode} ({(int)response.StatusCode})";
                    await ShowHelpDialog("Giriş Başarısız", msg);
                }
            }
            catch (Exception ex)
            {
                await ShowHelpDialog("Hata", $"Bağlantı hatası: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
                SetLoadingState(false);
            }
        }

        private void SetLoadingState(bool isLoading)
        {
            LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            PlaylistListView.IsEnabled = !isLoading;
            AddPlaylistButton.IsEnabled = !isLoading;
        }

        private async Task ShowHelpDialog(string title, string content)
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
