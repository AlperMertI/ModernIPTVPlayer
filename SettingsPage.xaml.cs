using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;

namespace ModernIPTVPlayer
{
    public sealed partial class SettingsPage : Page
    {
        // Simple ViewModel wrapper for x:Bind
        public class SettingsViewModel
        {
            public bool IsAutoCacheEnabled
            {
                get => AppSettings.IsAutoCacheEnabled;
                set => AppSettings.IsAutoCacheEnabled = value;
            }
        }

        public SettingsViewModel ViewModel { get; } = new();

        public SettingsPage()
        {
            this.InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            // Set ComboBox Selection
            var currentInterval = AppSettings.CacheIntervalMinutes;
            foreach (ComboBoxItem item in IntervalCombo.Items)
            {
                if (int.Parse(item.Tag.ToString()) == currentInterval)
                {
                    IntervalCombo.SelectedItem = item;
                    break;
                }
            }
        }

        private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IntervalCombo.SelectedItem is ComboBoxItem item)
            {
                if (int.TryParse(item.Tag.ToString(), out int minutes))
                {
                    AppSettings.CacheIntervalMinutes = minutes;
                    ShowStatus("Güncelleme sıklığı değiştirildi.");
                }
            }
        }

        private void AutoCacheToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // Binding handles value update, we just show status
            ShowStatus(AutoCacheToggle.IsOn ? "Otomatik güncelleme açıldı." : "Otomatik güncelleme kapatıldı.");
        }

        private async void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            BtnClearCache.IsEnabled = false;
            await Services.ContentCacheService.Instance.ClearCacheAsync();
            ShowStatus("İçerik önbelleği temizlendi.");
            BtnClearCache.IsEnabled = true;
        }

        private async void BtnRefreshNow_Click(object sender, RoutedEventArgs e)
        {
            // Fire event from ContentCacheService? Or just signal via App?
            // Since we haven't implemented a global 'ForceUpdate' pipeline yet, we'll placeholder this.
            // Ideally, we should expose a method in App or MainWindow.
            
            // For now, let's assume we can trigger it via a static event in App or just show a message
            ShowStatus("Güncelleme isteği gönderildi (Henüz Aktif Değil).");
            
            // TODO: Hook this to XtreamService.ForceUpdate() when refactoring pages.
        }

        private async void BtnClearProbe_Click(object sender, RoutedEventArgs e)
        {
            await Services.ProbeCacheService.Instance.ClearCacheAsync();
            ShowStatus("Analiz (Probe) verileri temizlendi.");
        }

        private void ShowStatus(string msg)
        {
            TxtStatus.Text = msg;
            StatusInfoBar.Message = msg;
            StatusInfoBar.IsOpen = true;
            
            // Auto hide after 3s
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, args) => 
            {
                StatusInfoBar.IsOpen = false;
                timer.Stop();
            };
            timer.Start();
        }
    }
}
