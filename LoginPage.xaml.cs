using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace ModernIPTVPlayer
{
    public sealed partial class LoginPage : Page
    {
        private bool _isLoading = false;

        public LoginPage()
        {
            this.InitializeComponent();
            this.Loaded += LoginPage_Loaded;
        }

        private void LoginPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Son kullanılan giriş yöntemini kontrol et
            int lastType = AppSettings.LastLoginType;
            LoginPivot.SelectedIndex = lastType;

            if (lastType == 0) // M3U
            {
                var savedUrl = AppSettings.SavedPlaylistUrl;
                if (!string.IsNullOrEmpty(savedUrl))
                {
                    UrlTextBox.Text = savedUrl;
                    // Auto-login (M3U)
                     _ = AttemptLogin(savedUrl, 0);
                }
            }
            else // Xtream
            {
                var host = AppSettings.SavedHost;
                var user = AppSettings.SavedUsername;
                var pass = AppSettings.SavedPassword;

                if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                {
                    HostTextBox.Text = host;
                    UsernameTextBox.Text = user;
                    PasswordBox.Password = pass;
                    
                    // Xtream: Create CLEAN host and URL
                    string cleanHost = CleanHost(host);
                    string authUrl = $"{cleanHost}/player_api.php?username={user}&password={pass}";
                    string playlistUrl = $"{cleanHost}/get.php?username={user}&password={pass}&type=m3u_plus&output=ts";

                    // Auto-login (Xtream)
                    _ = AttemptLogin(authUrl, 1, playlistUrl, cleanHost);
                }
            }
        }

        private async void M3uLoginButton_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlTextBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                await ShowHelpDialog("Error", "Please enter a URL.");
                return;
            }
            
            // 0 = M3U
            await AttemptLogin(url, 0);
        }

        private async void XtreamLoginButton_Click(object sender, RoutedEventArgs e)
        {
            string host = HostTextBox.Text.Trim();
            string user = UsernameTextBox.Text.Trim();
            string pass = PasswordBox.Password.Trim();

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                await ShowHelpDialog("Error", "Please fill all fields.");
                return;
            }

            // CLEAN THE HOST
            string cleanHost = CleanHost(host);

            // 1. URL'leri Hazırla
            string authUrl = $"{cleanHost}/player_api.php?username={user}&password={pass}";
            string playlistUrl = $"{cleanHost}/get.php?username={user}&password={pass}&type=m3u_plus&output=ts";
            
            // 2. Xtream login (pass manual cleanHost)
            await AttemptLogin(authUrl, 1, playlistUrl, cleanHost);
        }

        private string CleanHost(string rawHost)
        {
            if (string.IsNullOrEmpty(rawHost)) return "";

            // 1. Http
            string host = rawHost.Trim();
            if (!host.StartsWith("http")) host = "http://" + host;
            
            // 2. Trailing Slash
            host = host.TrimEnd('/');

            // 3. Known Suffixes
            // Remove common suffixes if user copy-pasted them
            if (host.EndsWith("/get.php")) host = host.Substring(0, host.Length - "/get.php".Length);
            if (host.EndsWith("/player_api.php")) host = host.Substring(0, host.Length - "/player_api.php".Length);
            
            // 4. Repeated Trailing Slash (just in case)
            host = host.TrimEnd('/');

            return host;
        }

        // Expanded signature to accept cleanHost
        private async Task AttemptLogin(string checkUrl, int loginType, string? finalPlaylistUrl = null, string? manualHost = null)
        {
            if (_isLoading) return;
            _isLoading = true;
            SetLoadingState(true);

            // Eğer final URL verilmediyse (M3U modu), checkUrl kullanılır
            string targetUrl = finalPlaylistUrl ?? checkUrl;

            try
            {
                // Use Shared HttpHelper to maintain cookies/session
                HttpResponseMessage response = await HttpHelper.Client.GetAsync(checkUrl, HttpCompletionOption.ResponseHeadersRead);
                
                if (response.IsSuccessStatusCode)
                {
                   // SUCCESS
                   if (loginType == 1 && manualHost != null)
                   {
                        // Xtream: Save Clean Host
                        AppSettings.SavedHost = manualHost;
                        AppSettings.SavedUsername = UsernameTextBox.Text.Trim();
                        AppSettings.SavedPassword = PasswordBox.Password.Trim();
                        AppSettings.LastLoginType = 1;
                   }
                   else
                   {
                        // M3U
                        SaveCredentials(targetUrl, 0);
                   }

                    // Set Global Login Info for sidebar navigation
                    App.CurrentLogin = new LoginParams 
                    { 
                        PlaylistUrl = targetUrl,
                        Host = (loginType == 1) ? manualHost : null, // Uses Sanitzed Host!
                        Username = (loginType == 1) ? UsernameTextBox.Text.Trim() : null,
                        Password = (loginType == 1) ? PasswordBox.Password.Trim() : null
                    };

                    // Navigate
                    Frame.Navigate(typeof(LiveTVPage), App.CurrentLogin);
                }
                else
                {
                    // Masked URL for debug
                    string maskedUrl = checkUrl.Replace(PasswordBox.Password, "****");
                    await ShowHelpDialog("Connection Failed", 
                        $"Server Error: {response.StatusCode} ({(int)response.StatusCode})\n" +
                        $"Message: {response.ReasonPhrase}\n" +
                        $"Check URL: {maskedUrl}");
                }
            }
            catch (Exception ex)
            {
                await ShowHelpDialog("Error", $"Connection error: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
                SetLoadingState(false);
            }
        }

        private void SaveCredentials(string url, int type)
        {
            AppSettings.LastLoginType = type;
            if (type == 0)
            {
                AppSettings.SavedPlaylistUrl = url;
            }
            // Xtream logic is handled inside AttemptLogin success block now for better cleanliness
        }

        private void SetLoadingState(bool isLoading)
        {
            LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            LoginPivot.IsEnabled = !isLoading;
        }

        private void LoginPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // UI Cleanup if needed
        }

        private async Task ShowHelpDialog(string title, string content)
        {
            if (this.XamlRoot == null) return;
            ContentDialog dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}
