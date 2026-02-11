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
                InstalledAddons.Add(new AddonItem { Url = url });
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

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                StremioAddonManager.Instance.RemoveAddon(url);
                LoadAddons();
                ShowStatus("Eklenti kaldırıldı.", InfoBarSeverity.Informational);
            }
        }
        
        private void ShowStatus(string msg, InfoBarSeverity severity)
        {
            StatusInfoBar.Message = msg;
            StatusInfoBar.Severity = severity;
            StatusInfoBar.IsOpen = true;
        }
    }

    public class AddonItem
    {
        public string Url { get; set; }
    }
}
