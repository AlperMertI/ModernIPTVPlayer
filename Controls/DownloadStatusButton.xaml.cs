using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Linq;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class DownloadStatusButton : UserControl
    {
        public DownloadStatusButton()
        {
            this.InitializeComponent();
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Services.DownloadManager.Instance != null)
            {
                Services.DownloadManager.Instance.DownloadStarted += OnDownloadChanged;
                Services.DownloadManager.Instance.DownloadChanged += OnDownloadChanged;
                UpdateProgress();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (Services.DownloadManager.Instance != null)
            {
                Services.DownloadManager.Instance.DownloadStarted -= OnDownloadChanged;
                Services.DownloadManager.Instance.DownloadChanged -= OnDownloadChanged;
            }
        }

        private void OnDownloadChanged(Services.DownloadItem item)
        {
            DispatcherQueue.TryEnqueue(UpdateProgress);
        }

        private void UpdateProgress()
        {
            var downloads = Services.DownloadManager.Instance.Downloads;

            var activeCount = downloads.Count(d => 
                d.Status == Services.DownloadStatus.Queued || 
                d.Status == Services.DownloadStatus.Downloading || 
                d.Status == Services.DownloadStatus.Paused);

            // Progress Ring Loop
            var relevantItems = downloads.Where(d => 
                d.Status != Services.DownloadStatus.Cancelled && 
                d.Status != Services.DownloadStatus.Failed).ToList();

            long totalKnownBytes = 0;
            long totalDownloadedBytes = 0;
            
            int knownCount = 0;
            int unknownCount = 0;
            
            foreach(var item in relevantItems)
            {
                totalDownloadedBytes += item.BytesDownloaded;
                
                if (item.TotalBytes.HasValue && item.TotalBytes > 0)
                {
                    totalKnownBytes += item.TotalBytes.Value;
                    knownCount++;
                }
                else if (item.Status == Services.DownloadStatus.Completed)
                {
                    totalKnownBytes += item.BytesDownloaded;
                    knownCount++;
                }
                else
                {
                    unknownCount++;
                }
            }
            
            if (unknownCount > 0)
            {
                long avgSize = (knownCount > 0) ? (totalKnownBytes / knownCount) : (500 * 1024 * 1024);
                totalKnownBytes += (unknownCount * avgSize);
            }

            if (totalKnownBytes > 0)
            {
                double percent = (double)totalDownloadedBytes / totalKnownBytes * 100.0;
                
                // Fine Touch: Minimum 1.5% visibility so user sees it starting immediately
                if (percent > 0 && percent < 1.5) percent = 1.5;
                if (percent > 100) percent = 100;
                
                GlobalProgressRing.Value = percent;
                GlobalProgressRing.IsIndeterminate = false;
                GlobalProgressRing.Visibility = Visibility.Visible;
                ProgressTrack.Visibility = Visibility.Visible;

                // Color logic: Orange if all active items are paused/queued, Blue if at least one is downloading
                bool isAnyActiveDownloading = relevantItems.Any(d => d.Status == Services.DownloadStatus.Downloading);
                if (isAnyActiveDownloading)
                {
                    GlobalProgressRing.Foreground = (Microsoft.UI.Xaml.Media.Brush)this.Resources["ActiveProgressBrush"];
                }
                else
                {
                    GlobalProgressRing.Foreground = (Microsoft.UI.Xaml.Media.Brush)this.Resources["PausedProgressBrush"];
                }
            }
            else if (activeCount > 0)
            {
                GlobalProgressRing.IsIndeterminate = true;
                GlobalProgressRing.Visibility = Visibility.Visible;
                ProgressTrack.Visibility = Visibility.Visible;
                GlobalProgressRing.Foreground = (Microsoft.UI.Xaml.Media.Brush)this.Resources["PausedProgressBrush"];
            }
            else
            {
                GlobalProgressRing.Visibility = Visibility.Collapsed;
                ProgressTrack.Visibility = Visibility.Collapsed;
            }
        }

        private void RootButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle Global Panel via MainWindow
            if (App.MainWindow is MainWindow mw)
            {
                mw.ToggleDownloads();
            }
        }
    }
}
