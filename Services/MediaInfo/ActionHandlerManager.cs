using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    internal sealed class ActionHandlerManager : IDisposable
    {
        private readonly MediaInfoPage _page;
        private bool _disposed;

        public ActionHandlerManager(MediaInfoPage page)
        {
            _page = page ?? throw new ArgumentNullException(nameof(page));
            Debug.WriteLine("[ACTION-HANDLER] Initialized");
        }

        public void OnEpisodePlayButtonClick(EpisodeItem ep)
        {
            if (_disposed || ep == null) return;
            _page.SelectEpisode(ep);
            _ = _page.PlayStremioContent(ep.Id, showGlobalLoading: false, autoPlay: true);
        }

        public void OnEpisodeArrowButtonTapped(EpisodeItem ep)
        {
            if (_disposed || ep == null) return;
            _page.SelectEpisode(ep);
            _ = _page.PlayStremioContent(ep.Id, showGlobalLoading: false, autoPlay: false);
        }

        public void OnMarkWatchedClicked(EpisodeItem ep)
        {
            if (_disposed || ep == null) return;
            string seriesId = "";
            string seriesName = "";
            var item = _page.Item;
            if (item is SeriesStream iptv)
            {
                seriesId = iptv.SeriesId.ToString();
                seriesName = iptv.Name;
            }
            else if (item is StremioMediaStream st)
            {
                seriesId = st.IMDbId ?? st.Id.ToString();
                seriesName = st.Title;
            }

            HistoryManager.Instance.UpdateProgress(ep.Id, ep.Title, ep.StreamUrl ?? "", 1000, 1000, seriesId, seriesName, ep.SeasonNumber, ep.EpisodeNumber, null, null, null, item?.PosterUrl, "series", item?.BackdropUrl);
            ep.IsWatched = true;
            ep.ProgressPercent = 0;
            ep.ProgressText = "";
            ep.HasProgress = false;
            _ = HistoryManager.Instance.SaveAsync();
        }

        public void OnMarkRemainingWatchedClicked(EpisodeItem startEpisode)
        {
            if (_disposed) return;
            var episodes = _page.CurrentEpisodes;
            if (episodes == null || episodes.Count == 0) return;

            string seriesId = "";
            string seriesName = "";
            var item = _page.Item;
            if (item is SeriesStream iptv)
            {
                seriesId = iptv.SeriesId.ToString();
                seriesName = iptv.Name;
            }
            else if (item is StremioMediaStream st)
            {
                seriesId = st.IMDbId ?? st.Id.ToString();
                seriesName = st.Title;
            }

            bool shouldMark = (startEpisode == null);
            foreach (var ep in episodes)
            {
                if (ep == startEpisode) shouldMark = true;
                if (shouldMark && !ep.IsWatched)
                {
                    bool isAired = ep.ReleaseDate.HasValue ? (ep.ReleaseDate.Value <= DateTime.Now.AddDays(1)) : ep.IsReleased;
                    bool hasStream = !string.IsNullOrEmpty(ep.StreamUrl);
                    if (isAired || hasStream)
                    {
                        HistoryManager.Instance.UpdateProgress(ep.Id, ep.Title, ep.StreamUrl ?? "", 1000, 1000, seriesId, seriesName, ep.SeasonNumber, ep.EpisodeNumber, null, null, null, item?.PosterUrl, "series", item?.BackdropUrl);
                        ep.IsWatched = true;
                        ep.ProgressPercent = 0;
                        ep.ProgressText = "";
                        ep.HasProgress = false;
                    }
                }
            }
            _ = HistoryManager.Instance.SaveAsync();
        }

        public void OnMarkUnwatchedClicked(EpisodeItem ep)
        {
            if (_disposed || ep == null) return;
            string seriesId = "";
            string seriesName = "";
            var item = _page.Item;
            if (item is SeriesStream iptv)
            {
                seriesId = iptv.SeriesId.ToString();
                seriesName = iptv.Name;
            }
            else if (item is StremioMediaStream st)
            {
                seriesId = st.IMDbId ?? st.Id.ToString();
                seriesName = st.Title;
            }

            HistoryManager.Instance.UpdateProgress(ep.Id, ep.Title, ep.StreamUrl ?? "", 0, 1000, seriesId, seriesName, ep.SeasonNumber, ep.EpisodeNumber, null, null, null, item?.PosterUrl, "series", item?.BackdropUrl);
            ep.IsWatched = false;
            ep.ProgressPercent = 0;
            ep.ProgressText = "";
            ep.HasProgress = false;
            _ = HistoryManager.Instance.SaveAsync();
        }

        public void UpdateWatchlistState(bool animate = false)
        {
            if (_disposed) return;
            var btn = _page.WatchlistButtonControl;
            if (btn == null) return;

            var item = _page.Item;
            bool isInList = item != null && WatchlistManager.Instance.IsOnWatchlist(item);
            var icon = btn.Content as Microsoft.UI.Xaml.Controls.FontIcon;
            if (icon == null) return;

            string newGlyph = isInList ? "\uE73E" : "\uE710";
            icon.Glyph = newGlyph;
            icon.Foreground = isInList
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 33, 150, 243))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);

            if (isInList)
            {
                var targetBg = Windows.UI.Color.FromArgb(50, 33, 150, 243);
                if (animate) _page.AnimateButtonBrushColor(btn, targetBg, 1.0);
                else btn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(targetBg);
            }
            else
            {
                var themeBrush = _page.ThemeTintBrush;
                btn.Background = themeBrush ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(37, 255, 255, 255));
            }

            ToolTipService.SetToolTip(btn, isInList ? "İzleme Listesinden Çıkar" : "İzleme Listesine Ekle");
        }

        public void RefreshHistoryVisibility()
        {
            if (_disposed) return;
            var item = _page.Item;
            if (item == null) return;

            if (item is StremioMediaStream stremioItem)
            {
                if (stremioItem.Meta?.Type == "series" || stremioItem.Meta?.Type == "tv")
                    _ = RefreshStremioSeriesProgressAsync(stremioItem);
                else
                    UpdateMovieHistoryUi(HistoryManager.Instance.GetProgress(stremioItem.Meta.Id));
            }
            else if (item is SeriesStream series)
                _ = RefreshIptvSeriesProgressAsync(series);
            else if (item is LiveStream live)
                UpdateLiveHistoryUi(HistoryManager.Instance.GetProgress(live.StreamId.ToString()));
        }

        public void UpdateMovieHistoryUi(HistoryItem history)
        {
            if (_disposed) return;
            bool hasProgress = history != null && history.Position > 0;
            _page.UpdateMovieHistoryVisibility(hasProgress);
            if (hasProgress)
            {
                double percent = history.Duration > 0 ? (history.Position / history.Duration) * 100 : 0;
                string progressText = history.IsFinished ? "Tamamlandı" : $"{(int)(history.Position / 60)}dk kaldı";
                _page.SetHistoryProgressText(progressText);
                _page.SetHistoryProgressBarValue(percent);
            }
        }

        private void UpdateLiveHistoryUi(HistoryItem history)
        {
            if (_disposed) return;
            _page.UpdateMovieHistoryVisibility(history != null && history.Position > 0);
        }

        public async Task RefreshStremioSeriesProgressAsync(StremioMediaStream series)
        {
            if (_disposed || series == null) return;
            foreach (var season in _page.Seasons)
                foreach (var ep in season.Episodes) ep.RefreshHistoryState();
        }

        public async Task RefreshIptvSeriesProgressAsync(SeriesStream series)
        {
            if (_disposed || series == null) return;
            foreach (var season in _page.Seasons)
            {
                foreach (var ep in season.Episodes)
                {
                    string resolvedId = _page.ResolveBestContentId(ep.Id);
                    var history = HistoryManager.Instance.GetProgress(resolvedId);
                    if (history != null && history.Duration > 0)
                    {
                        ep.IsWatched = history.IsFinished;
                        double pct = (history.Position / history.Duration) * 100;
                        ep.ProgressPercent = pct;
                        ep.ProgressText = history.IsFinished ? "Tamamlandı" : $"{(int)(history.Position / 60)}dk kaldı";
                        ep.HasProgress = pct > 0 && !history.IsFinished;
                    }
                    else
                    {
                        ep.IsWatched = false;
                        ep.ProgressPercent = 0;
                        ep.ProgressText = "";
                        ep.HasProgress = false;
                    }
                }
            }
        }

        public async Task DownloadSingleAsync()
        {
            if (_disposed) return;
            var streamUrl = _page.StreamUrl;
            if (string.IsNullOrEmpty(streamUrl)) return;

            if (streamUrl.Contains(".m3u8") || streamUrl.Contains(".ts"))
            {
                var dialog = new ContentDialog
                {
                    Title = "Canlı Yayın / Akış İndirme",
                    Content = "Bu içerik bir akış (HLS) formatındadır. Doğrudan dosya olarak indirilemez. Linki kopyalayıp IDM veya JDownloader gibi araçlar kullanmanızı öneririz.",
                    PrimaryButtonText = "Linki Kopyala",
                    CloseButtonText = "Kapat",
                    XamlRoot = _page.XamlRoot
                };
                try
                {
                    var result = await DialogService.ShowAsync(dialog);
                    if (result == ContentDialogResult.Primary)
                    {
                        var pkg = new DataPackage();
                        pkg.SetText(streamUrl);
                        Clipboard.SetContent(pkg);
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[Download] Dialog Error: {ex.Message}"); }
            }
            else
            {
                var savePicker = new FileSavePicker();
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);
                savePicker.SuggestedStartLocation = PickerLocationId.Downloads;
                savePicker.FileTypeChoices.Add("Video File", new List<string>() { ".mp4", ".mkv", ".avi" });

                string fileName = _page.TitleText ?? "Media";
                try
                {
                    var uri = new Uri(streamUrl);
                    string lastSegment = uri.Segments.Last();
                    if (lastSegment.Contains(".") && lastSegment.Length > 4)
                        fileName = System.Net.WebUtility.UrlDecode(lastSegment);
                }
                catch { }

                foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
                savePicker.SuggestedFileName = fileName;

                var file = await savePicker.PickSaveFileAsync();
                if (file != null)
                    DownloadManager.Instance.StartDownload(file, streamUrl, _page.TitleText ?? "Media");
            }
        }

        public async Task DownloadSeasonAsync()
        {
            if (_disposed) return;
            var episodes = _page.CurrentEpisodes;
            if (episodes == null || episodes.Count == 0) return;

            var folderPicker = new FolderPicker();
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hWnd);
            folderPicker.SuggestedStartLocation = PickerLocationId.Downloads;
            folderPicker.FileTypeFilter.Add("*");

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder == null) return;

            string seriesName = _page.Item?.Title ?? "Series";
            int enqueuedCount = 0;
            foreach (var ep in episodes)
            {
                if (string.IsNullOrEmpty(ep.StreamUrl)) continue;
                if (ep.StreamUrl.Contains(".m3u8") || ep.StreamUrl.Contains(".ts")) continue;

                string ext = ".mp4";
                try
                {
                    var uri = new Uri(ep.StreamUrl);
                    string last = uri.Segments.Last();
                    if (last.Contains(".")) ext = Path.GetExtension(last);
                }
                catch { }

                string sNum = ep.SeasonNumber.ToString().PadLeft(2, '0');
                int epNum = episodes.IndexOf(ep) + 1;
                string eNum = epNum.ToString().PadLeft(2, '0');
                string fileName = $"{seriesName} - S{sNum}E{eNum} - {ep.Title}{ext}";
                foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');

                try
                {
                    var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
                    DownloadManager.Instance.StartDownload(file, ep.StreamUrl, fileName.Replace(ext, ""));
                    enqueuedCount++;
                }
                catch { }
            }
        }

        public void OnCopyLinkClicked()
        {
            if (_disposed) return;
            var url = _page.StreamUrl;
            if (!string.IsNullOrEmpty(url))
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(url);
                Clipboard.SetContent(dataPackage);
            }
        }

        public void OnBackClicked()
        {
            if (_disposed) return;
            if (_page.Frame?.CanGoBack == true) _page.Frame.GoBack();
        }

        public async void OnRestartClicked()
        {
            if (_disposed) return;
            var player = _page.MediaInfoPlayerInstance;
            if (player != null)
            {
                try
                {
                    await player.ExecuteCommandAsync("seek", "0", "absolute");
                }
                catch { }
            }
        }

        public async Task PerformHandoverAndNavigateAsync(string url, string title, string id = null, string parentId = null, string seriesName = null, int season = 0, int episode = 0, double startSeconds = -1, string posterUrl = null, string type = null, string backdropUrl = null)
        {
            if (_disposed || string.IsNullOrEmpty(url)) return;

            var player = _page.MediaInfoPlayerInstance;
            if (player != null)
            {
                try
                {
                    var position = player.Position.TotalSeconds;
                    if (startSeconds < 0) startSeconds = position;
                }
                catch { }

                try { player.Pause(); } catch { }

                _page.MediaInfoPlayerHandoff = player;
                _page.MediaInfoPlayerInstance = null;
            }

            _page.Frame?.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(
                url, title, id, parentId, seriesName, season, episode,
                startSeconds, posterUrl, type, backdropUrl,
                _page.LogoUrlForAction, _page.PrimaryColorHex, null,
                _page.YearTextValue, _page.RatingTextValue, _page.DurationTextValue, _page.OverviewTextValue));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Debug.WriteLine("[ACTION-HANDLER] Disposed");
        }
    }
}
