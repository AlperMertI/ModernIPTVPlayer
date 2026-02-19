using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;
using System.Collections.ObjectModel;
using ModernIPTVPlayer.Services.Stremio;

namespace ModernIPTVPlayer.Pages
{
    public sealed partial class AddonsPage : Page
    {
        public ObservableCollection<AddonItem> InstalledAddons { get; } = new();

        public AddonsPage()
        {
            this.InitializeComponent();
            LoadAddons();
        }

        private void LoadAddons()
        {
            InstalledAddons.Clear();
            var urls = StremioAddonManager.Instance.GetAddons();
            foreach (var url in urls)
            {
                var name = StremioAddonManager.Instance.GetAddonName(url);
                var icon = StremioAddonManager.Instance.GetAddonIcon(url);

                InstalledAddons.Add(new AddonItem 
                { 
                    Url = url, 
                    Name = name,
                    IconUrl = icon
                });
            }
            AddonListView.ItemsSource = InstalledAddons;
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            string url = AddonUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;
            
            // Validate URL roughly
            if (!url.StartsWith("http") && !url.StartsWith("https"))
            {
                 url = "https://" + url;
            }

            // Verify Manifest
            BtnInstall.IsEnabled = false;
            try
            {
                 var manifest = await StremioService.Instance.GetManifestAsync(url);
                 if (manifest != null)
                 {
                     // Normalize saved URL: remove manifest.json if present, ensure https
                     string savedUrl = url;
                     if (savedUrl.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
                         savedUrl = "https://" + savedUrl.Substring(10);
                     
                     if (savedUrl.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                         savedUrl = savedUrl.Substring(0, savedUrl.Length - 14);
                     else if (savedUrl.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                         savedUrl = savedUrl.Substring(0, savedUrl.Length - 13);

                     StremioAddonManager.Instance.AddAddon(savedUrl.TrimEnd('/'));
                     LoadAddons();
                     AddonUrlBox.Text = "";
                     ShowStatus("Eklenti başarıyla yüklendi!", InfoBarSeverity.Success);
                 }
                 else
                 {
                     ShowStatus("Geçersiz eklenti veya manifest bulunamadı.", InfoBarSeverity.Error);
                 }
            }
            catch (Exception ex)
            {
                ShowStatus($"Hata: {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                BtnInstall.IsEnabled = true;
            }
        }

        private void AddonListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            // Persist the new order
            var urls = InstalledAddons.Select(a => a.Url).ToList();
            StremioAddonManager.Instance.UpdateAddonOrder(urls);
            ShowStatus("Sıralama güncellendi.", InfoBarSeverity.Informational);
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                StremioAddonManager.Instance.RemoveAddon(url);
                LoadAddons();
                ShowStatus("Eklenti kaldırıldı.", InfoBarSeverity.Informational);
            }
        }

        private void BtnCopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(url);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                ShowStatus("Bağlantı kopyalandı.", InfoBarSeverity.Informational);
            }
        }

        private async void BtnConfigure_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                // Most Stremio addons have /configure instead of /manifest.json
                string configUrl = url;
                if (configUrl.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                    configUrl = configUrl.Replace("/manifest.json", "/configure", StringComparison.OrdinalIgnoreCase);
                else
                    configUrl = configUrl.TrimEnd('/') + "/configure";

                try
                {
                    await Windows.System.Launcher.LaunchUriAsync(new Uri(configUrl));
                }
                catch (Exception ex)
                {
                    ShowStatus($"Konfigürasyon sayfası açılamadı: {ex.Message}", InfoBarSeverity.Error);
                }
            }
        }
        
        private void ShowStatus(string msg, InfoBarSeverity severity)
        {
            StatusInfoBar.Message = msg;
            StatusInfoBar.Severity = severity;
            StatusInfoBar.IsOpen = true;

            // Auto hide
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, args) => { StatusInfoBar.IsOpen = false; timer.Stop(); };
            timer.Start();
        }
    }

    public class AddonItem
    {
        public string Url { get; set; }
        public string Name { get; set; }
        public string IconUrl { get; set; }

        public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : Url;
        public bool HasIcon => !string.IsNullOrEmpty(IconUrl);
    }
}
