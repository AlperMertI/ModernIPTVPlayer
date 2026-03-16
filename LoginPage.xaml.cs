using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using System.Text.Json;
using ModernIPTVPlayer.Services;

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
            
            // Auto-login is now handled globally at startup, 
            // but we can still trigger it here if coming from another page
            if (App.CurrentLogin == null)
            {
                CheckAutoLogin();
            }
        }

        private async void CheckAutoLogin()
        {
            if (_isLoading) return;
            _isLoading = true;
            SetLoadingState(true);

            bool success = await AuthService.Instance.CheckAutoLoginAsync();
            
            if (success)
            {
                Frame.Navigate(typeof(LiveTVPage), App.CurrentLogin);
            }
            else
            {
                _isLoading = false;
                SetLoadingState(false);
            }
        }

        private void LoadPlaylists()
        {
            _playlists.Clear();
            var list = AuthService.Instance.GetSavedPlaylists();
            foreach (var p in list) _playlists.Add(p);
            
            PlaylistListView.ItemsSource = _playlists;
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            EmptyStatePanel.Visibility = _playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PlaylistListView.Visibility = _playlists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveAllPlaylists()
        {
            AuthService.Instance.SavePlaylists(new System.Collections.Generic.List<Playlist>(_playlists));
            UpdateEmptyState();
        }

        private async void AddPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new PlaylistDialog { XamlRoot = this.XamlRoot };
            var result = await Services.DialogService.ShowAsync(dialog);

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
            var result = await Services.DialogService.ShowAsync(dialog);

            if (result == ContentDialogResult.Primary)
            {
                dialog.PrepareResult();
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

            if (await Services.DialogService.ShowAsync(deleteDialog) == ContentDialogResult.Primary)
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
            if (_isLoading) return;
            _isLoading = true;
            SetLoadingState(true);

            try
            {
                bool success = await AuthService.Instance.LoginWithPlaylistAsync(p);
                if (success)
                {
                    Frame.Navigate(typeof(LiveTVPage), App.CurrentLogin);
                }
                else
                {
                    await ShowHelpDialog("Giriş Başarısız", "Sunucuya bağlanılamadı veya hatalı giriş.");
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
            await Services.DialogService.ShowAsync(dialog);
        }
    }
}
