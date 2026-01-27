using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Microsoft.UI.Dispatching;
using System.Linq;

namespace ModernIPTVPlayer.Services
{
    public class DownloadItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; }
        public string Url { get; set; }
        public StorageFile File { get; set; }
        public double Progress { get; set; }
        public string StatusText { get; set; }
        public DownloadStatus Status { get; set; }
        public long BytesDownloaded { get; set; }
        public long? TotalBytes { get; set; }
        public CancellationTokenSource Cts { get; set; }
        public int RetryCount { get; set; } = 0;
        
        // Event to notify UI of changes specific to this item
        public event Action<DownloadItem> Changed;
        
        public void NotifyChanged()
        {
            Changed?.Invoke(this);
        }
    }

    public enum DownloadStatus
    {
        Queued,
        Downloading,
        Paused,
        Completed,
        Failed,
        Cancelled
    }

    public class DownloadManager
    {
        private static DownloadManager _instance;
        public static DownloadManager Instance => _instance ??= new DownloadManager();

        private List<DownloadItem> _downloads = new();
        public IReadOnlyList<DownloadItem> Downloads => _downloads;
        
        // Queue Management
        private List<DownloadItem> _pendingQueue = new(); // Use List instead of Queue to allow re-ordering if needed
        private int _maxConcurrentDownloads = 1;

        public event Action<DownloadItem> DownloadStarted;
        public event Action<DownloadItem> DownloadChanged;

        private HttpClient _client;
        private DispatcherQueue _dispatcher;
        private SemaphoreSlim _queueLock = new SemaphoreSlim(1, 1);

        private DownloadManager()
        {
            _client = new HttpClient();
            _client.Timeout = TimeSpan.FromHours(24);
            _dispatcher = DispatcherQueue.GetForCurrentThread(); 
        }
        
        public void Initialize(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public async void StartDownload(StorageFile file, string url, string title)
        {
            var item = new DownloadItem
            {
                Title = title,
                Url = url,
                File = file,
                Status = DownloadStatus.Queued,
                StatusText = "Sırada bekliyor..."
            };

            _downloads.Add(item);
            
            // UI Update
            DownloadStarted?.Invoke(item);

            // Try to fetch size in background for progress calculation
            _ = Task.Run(() => FetchSizeSilent(item));

            await EnqueueDownload(item);
        }

        private async Task EnqueueDownload(DownloadItem item)
        {
            await _queueLock.WaitAsync();
            try
            {
                if (item.Status == DownloadStatus.Queued)
                {
                    _pendingQueue.Add(item);
                    item.StatusText = $"Sırada ({_pendingQueue.IndexOf(item) + 1})";
                    item.NotifyChanged();
                    DownloadChanged?.Invoke(item);
                }
            }
            finally
            {
                _queueLock.Release();
            }

            ProcessQueue();
        }

        private void ProcessQueue()
        {
            // Run in background to avoid blocking
            Task.Run(async () => 
            {
                await _queueLock.WaitAsync();
                try
                {
                    int activeCount = _downloads.Count(d => d.Status == DownloadStatus.Downloading);
                    
                    while (activeCount < _maxConcurrentDownloads && _pendingQueue.Count > 0)
                    {
                        var nextItem = _pendingQueue[0];
                        _pendingQueue.RemoveAt(0);
                        
                        // Start it
                        _ = DownloadInternal(nextItem); // Fire and forget task
                        activeCount++;
                    }

                    // Update positions for remaining items
                    for(int i=0; i<_pendingQueue.Count; i++)
                    {
                        _pendingQueue[i].StatusText = $"Sırada ({i + 1})";
                        _pendingQueue[i].NotifyChanged();
                         // We might not want to spam Invoke here, UI binds to StatusText
                    }
                }
                finally
                {
                    _queueLock.Release();
                }
            });
        }

        private async Task DownloadInternal(DownloadItem item)
        {
            try
            {
                item.Status = DownloadStatus.Downloading;
                item.StatusText = "İndiriliyor...";
                item.Cts = new CancellationTokenSource();
                item.NotifyChanged();
                DownloadChanged?.Invoke(item);

                // Redo the loop with standard Stream wrapper for robustness
                using (var fileStream = await item.File.OpenStreamForWriteAsync()) 
                {
                    if (item.BytesDownloaded > 0)
                    {
                        fileStream.Seek(item.BytesDownloaded, SeekOrigin.Begin);
                    }
                    else
                    {
                        fileStream.SetLength(0);
                    }
                    
                    var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
                    if (item.BytesDownloaded > 0)
                    {
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(item.BytesDownloaded, null);
                    }
                    
                    using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, item.Cts.Token))
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength.HasValue)
                        {
                            // If range, ContentLength is remaining. Total = Existing + Remaining.
                            item.TotalBytes = item.BytesDownloaded + response.Content.Headers.ContentLength.Value;
                        }

                        using (var netStream = await response.Content.ReadAsStreamAsync(item.Cts.Token))
                        {
                            var buffer = new byte[81920]; // 80KB
                            int read;
                            var lastReport = DateTime.Now;
                            long bytesAtLastReport = item.BytesDownloaded;

                            while ((read = await netStream.ReadAsync(buffer, 0, buffer.Length, item.Cts.Token)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, read, item.Cts.Token);
                                item.BytesDownloaded += read;

                                // Report Progress (throttled ~500ms for stable speed)
                                var now = DateTime.Now;
                                var deltaT = (now - lastReport).TotalSeconds;
                                if (deltaT > 0.5)
                                {
                                    // Calculate Speed
                                    long deltaBytes = item.BytesDownloaded - bytesAtLastReport;
                                    double speed = deltaBytes / deltaT; // bytes per second
                                    
                                    UpdateProgress(item, speed);
                                    
                                    lastReport = now;
                                    bytesAtLastReport = item.BytesDownloaded;
                                }
                            }
                        }
                    }
                }

                item.Status = DownloadStatus.Completed;
                item.StatusText = "Tamamlandı";
                item.Progress = 100;
                item.NotifyChanged();
                DownloadChanged?.Invoke(item);
            }
            catch (OperationCanceledException)
            {
                if (item.Status == DownloadStatus.Paused)
                {
                    item.StatusText = "Duraklatıldı";
                }
                else
                {
                    item.Status = DownloadStatus.Cancelled;
                    item.StatusText = "İptal Edildi";
                }
                item.NotifyChanged();
                DownloadChanged?.Invoke(item);
            }
            catch (HttpRequestException httpEx)
            {
                if (!item.Cts.IsCancellationRequested && item.RetryCount < 3)
                {
                     item.RetryCount++;
                     item.StatusText = $"Hata alındı. Tekrar deneniyor ({item.RetryCount}/3)...";
                     item.NotifyChanged();
                     DownloadChanged?.Invoke(item);
                     await Task.Delay(2000);
                     await DownloadInternal(item);
                     return;
                }

                // Network or HTTP errors - more user-friendly messages
                item.Status = DownloadStatus.Failed;
                if (httpEx.Message.Contains("ended prematurely"))
                {
                    item.StatusText = "Bağlantı koptu. Devam ettirilebilir.";
                }
                else
                {
                    item.StatusText = "Ağ hatası. Tekrar deneyin.";
                }
                item.NotifyChanged();
                DownloadChanged?.Invoke(item);
            }
            catch (IOException ioEx)
            {
                if (!item.Cts.IsCancellationRequested && item.RetryCount < 3)
                {
                     item.RetryCount++;
                     item.StatusText = $"Hata alındı. Tekrar deneniyor ({item.RetryCount}/3)...";
                     item.NotifyChanged();
                     DownloadChanged?.Invoke(item);
                     await Task.Delay(2000);
                     await DownloadInternal(item);
                     return;
                }

                item.Status = DownloadStatus.Failed;
                if (ioEx.Message.Contains("ended prematurely") || ioEx.Message.Contains("additional bytes expected"))
                {
                    item.StatusText = "Bağlantı koptu. Devam ettirilebilir.";
                }
                else
                {
                    item.StatusText = "Dosya hatası: " + TruncateMessage(ioEx.Message, 40);
                }
                item.NotifyChanged();
                DownloadChanged?.Invoke(item);
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Failed;
                // Truncate long error messages
                item.StatusText = "Hata: " + TruncateMessage(ex.Message, 50);
                item.NotifyChanged();
                DownloadChanged?.Invoke(item);
            }
            finally
            {
                ProcessQueue();
            }
        }
        
        private string TruncateMessage(string message, int maxLength)
        {
            if (string.IsNullOrEmpty(message)) return "";
            if (message.Length <= maxLength) return message;
            return message.Substring(0, maxLength) + "...";
        }

        private void UpdateProgress(DownloadItem item, double speedBytesPerSec = 0)
        {
            string speedText = "";
            if (speedBytesPerSec > 1024 * 1024) 
                speedText = $"{speedBytesPerSec / 1024.0 / 1024.0:F1} MB/s";
            else 
                speedText = $"{speedBytesPerSec / 1024.0:F0} KB/s";

            if (item.TotalBytes.HasValue && item.TotalBytes > 0)
            {
                item.Progress = (double)item.BytesDownloaded / item.TotalBytes.Value * 100;
                double mb = item.BytesDownloaded / 1024.0 / 1024.0;
                double totalMb = item.TotalBytes.Value / 1024.0 / 1024.0;
                
                // Format: %45.2 - 120MB / 250MB - 3.5 MB/s
                item.StatusText = $"%{item.Progress:F1} - {mb:F1}MB / {totalMb:F1}MB - {speedText}";
            }
            else
            {
                 double mb = item.BytesDownloaded / 1024.0 / 1024.0;
                 item.StatusText = $"{mb:F1} MB indirildi - {speedText}";
            }
            
            // Dispatch to UI AND trigger DownloadChanged event for MainWindow
            if (_dispatcher != null)
            {
                 _dispatcher.TryEnqueue(() => 
                 {
                     item.NotifyChanged();
                     DownloadChanged?.Invoke(item); // This triggers MainWindow.UpdateDownloadCard
                 });
            }
            else
            {
                item.NotifyChanged();
                DownloadChanged?.Invoke(item);
            }
        }

        public void PauseDownload(DownloadItem item)
        {
            if (item.Status == DownloadStatus.Downloading && item.Cts != null)
            {
                item.Status = DownloadStatus.Paused; 
                item.Cts.Cancel(); 
                // Next item will start when DownloadInternal exits (via finally block or catch)
            }
        }

        public async void ResumeDownload(DownloadItem item)
        {
            if (item.Status == DownloadStatus.Paused || item.Status == DownloadStatus.Failed || item.Status == DownloadStatus.Cancelled)
            {
                item.Status = DownloadStatus.Queued;
                item.StatusText = "Sırada bekliyor...";
                item.NotifyChanged();
                DownloadChanged?.Invoke(item); // UI Update
                
                // Re-enqueue instead of direct start
                await EnqueueDownload(item);
            }
        }

        public void CancelDownload(DownloadItem item)
        {
            if (item.Status == DownloadStatus.Queued)
            {
                lock(_pendingQueue) { _pendingQueue.Remove(item); }
                item.Status = DownloadStatus.Cancelled;
                item.NotifyChanged();
                DownloadChanged?.Invoke(item);
            }
            else if (item.Status == DownloadStatus.Downloading && item.Cts != null)
            {
                item.Status = DownloadStatus.Cancelled;
                item.Cts.Cancel();
            }
            else
            {
                item.Status = DownloadStatus.Cancelled;
                item.NotifyChanged();
                DownloadChanged?.Invoke(item);
            }
            
            // Cleanup: Delete file if exists
            try 
            {
                if (item.File != null)
                {
                    Task.Delay(500).ContinueWith(async _ => await item.File.DeleteAsync());
                }
            } 
            catch { }
            
            // Trigger queue update
            ProcessQueue();
        }

        public void PauseAll()
        {
            // Pause active downloads
            var activeItems = Downloads.Where(d => d.Status == DownloadStatus.Downloading).ToList();
            foreach (var item in activeItems)
            {
                PauseDownload(item);
            }

            // Pause queued items (remove from queue so they don't auto-start)
            lock (_pendingQueue)
            {
                while(_pendingQueue.Count > 0)
                {
                    var item = _pendingQueue[0];
                    _pendingQueue.RemoveAt(0);
                    
                    item.Status = DownloadStatus.Paused;
                    item.StatusText = "Duraklatıldı";
                    item.NotifyChanged();
                    DownloadChanged?.Invoke(item);
                }
            }
        }

        public void ResumeAll()
        {
            var pausedItems = Downloads.Where(d => d.Status == DownloadStatus.Paused).ToList();
            foreach (var item in pausedItems)
            {
                ResumeDownload(item);
            }
        }

        public void CancelAll()
        {
            // Cancel everything that is not finished
            var allItems = Downloads.Where(d => d.Status != DownloadStatus.Cancelled && d.Status != DownloadStatus.Completed).ToList();
            foreach(var item in allItems)
            {
                CancelDownload(item);
            }
        }

        private async Task FetchSizeSilent(DownloadItem item)
        {
            if (item.TotalBytes.HasValue) return;

            try
            {
                // Use a transient client with short timeout for metadata check
                using (var headClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Head, item.Url))
                    {
                         var response = await headClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                         if (response.Content.Headers.ContentLength.HasValue)
                         {
                             item.TotalBytes = response.Content.Headers.ContentLength;
                             
                             // Notify UI on main thread
                             _dispatcher?.TryEnqueue(() => 
                             {
                                 item.NotifyChanged();
                                 DownloadChanged?.Invoke(item);
                             });
                         }
                    }
                }
            }
            catch { /* Ignore errors, size will be known when download starts */ }
        }
    }
}
