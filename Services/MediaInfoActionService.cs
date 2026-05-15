using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer;
using System.Diagnostics;

namespace ModernIPTVPlayer.Services
{
    public class MediaInfoActionService
    {
        private readonly IMediaInfoUIProxy _ui;

        public MediaInfoActionService(IMediaInfoUIProxy ui)
        {
            _ui = ui;
        }

        public void SyncActionButtons(IMediaStream item, EpisodeItem selectedEpisode, HistoryItem history)
        {
            bool isSeries = item is SeriesStream || (item is Models.Stremio.StremioMediaStream sms && sms.Meta.Type == "series");
            bool hasProgress = history != null && history.Position > 0;
            double progressPercent = (history?.Duration > 0) ? (history.Position / history.Duration) * 100 : 0;
            bool canContinue = hasProgress && !history.IsFinished && progressPercent < 98;

            string mainText = "Oynat";
            string subtext = "";
            bool showRestart = false;

            if (canContinue)
            {
                mainText = "Devam Et";
                showRestart = true;
                
                if (isSeries && selectedEpisode == null)
                {
                    int displayEp = history.EpisodeNumber == 0 ? 1 : history.EpisodeNumber;
                    subtext = $"S{history.SeasonNumber:D2}E{displayEp:D2}";
                }
                else
                {
                    subtext = BuildRemainingText(history);
                }
            }
            else if (hasProgress && (history.IsFinished || progressPercent >= 98))
            {
                mainText = "Tekrar İzle";
                showRestart = true;
            }
            else if (isSeries && selectedEpisode == null)
            {
                mainText = "Bölüm Seçin";
            }
            else if (item is Models.Stremio.StremioMediaStream)
            {
                mainText = "Kaynak Bul";
            }

            // Apply to UI via Proxy
            if (_ui.PlayButtonText != null) _ui.PlayButtonText.Text = mainText;
            if (_ui.StickyPlayButtonText != null) _ui.StickyPlayButtonText.Text = mainText;

            if (_ui.PlayButtonSubtext != null)
            {
                _ui.PlayButtonSubtext.Text = subtext;
                _ui.PlayButtonSubtext.Visibility = string.IsNullOrWhiteSpace(subtext) ? Visibility.Collapsed : Visibility.Visible;
            }
            if (_ui.StickyPlayButtonSubtext != null)
            {
                _ui.StickyPlayButtonSubtext.Text = subtext;
                _ui.StickyPlayButtonSubtext.Visibility = string.IsNullOrWhiteSpace(subtext) ? Visibility.Collapsed : Visibility.Visible;
            }

            if (_ui.RestartButton != null) _ui.RestartButton.Visibility = showRestart ? Visibility.Visible : Visibility.Collapsed;
        }

        public async Task HandlePlayClickAsync(IMediaStream item, EpisodeItem selectedEpisode, string streamUrl, object sender = null)
        {
            // STREMIO LOGIC
            if (item is Models.Stremio.StremioMediaStream stremioItem)
            {
                string currentUrl = streamUrl;

                // [FIX] If streamUrl is missing but we're in an auto-resume flow, try a last-ditch history lookup
                if (string.IsNullOrEmpty(currentUrl))
                {
                    string historyId = stremioItem.Meta.Id;
                    if (stremioItem.Meta.Type == "series" && selectedEpisode != null) historyId = selectedEpisode.Id;
                    
                    var h = HistoryManager.Instance.GetProgress(historyId);
                    if (h != null && !string.IsNullOrEmpty(h.StreamUrl))
                    {
                        currentUrl = h.StreamUrl;
                        Debug.WriteLine($"[ActionService] Recovered streamUrl from history: {currentUrl}");
                    }
                }

                // If we have a cached stream URL (Resume) and user clicked "Continue", prioritize Resume
                if (!string.IsNullOrEmpty(currentUrl))
                {
                     string videoId = stremioItem.Meta.Id;
                     string title = stremioItem.Title;
                     
                     if (stremioItem.Meta.Type == "series" && selectedEpisode != null)
                     {
                         videoId = selectedEpisode.Id;
                         title = $"{selectedEpisode.SeasonNumber}x{selectedEpisode.EpisodeNumber} - {selectedEpisode.Title}";
                     }
                     
                     double resumeSeconds = -1;
                     var history = HistoryManager.Instance.GetProgress(videoId);
                     if (history != null && !history.IsFinished && history.Position > 0)
                     {
                         resumeSeconds = history.Position;
                     }

                     string parentIdStr = (stremioItem.Meta.Type == "series" || stremioItem.Meta.Type == "tv") ? stremioItem.Meta.Id : null;
                     int seasonToPass = selectedEpisode?.SeasonNumber ?? 0;
                     int episodeToPass = selectedEpisode?.EpisodeNumber ?? 0;

                     string handoverType = (stremioItem.Meta.Type == "series" || stremioItem.Meta.Type == "tv") ? "series" : "movie";
                     _ui.StreamUrl = currentUrl; // [SYNC] Update UI state with recovered URL
                     await _ui.PerformHandoverAndNavigate(currentUrl, title, videoId, parentIdStr, null, seasonToPass, episodeToPass, resumeSeconds, item.PosterUrl, handoverType, _ui.GetCurrentBackdrop());
                     return;
                }
                
                // Otherwise show sources or auto-play
                if (stremioItem.Meta.Type == "movie")
                {
                    var history = HistoryManager.Instance.GetProgress(stremioItem.Meta.Id);
                    double startSeconds = -1;
                    if (history != null && !history.IsFinished && history.Position > 0) startSeconds = history.Position;
                    
                    await _ui.PlayStremioContent(stremioItem.Meta.Id, showGlobalLoading: false, autoPlay: true, startSeconds: startSeconds);
                }
                else if (selectedEpisode != null)
                {
                    double resumeSeconds = -1;
                    var h = HistoryManager.Instance.GetProgress(selectedEpisode.Id);
                    if (h != null && !h.IsFinished && h.Position > 0) resumeSeconds = h.Position;
                    
                    await _ui.PlayStremioContent(selectedEpisode.Id, showGlobalLoading: false, autoPlay: true, startSeconds: resumeSeconds);
                }
                else
                {
                    // No episode selected fallback
                     _ui.OpenEpisodesPanel(PanelChangeReason.EpisodeRequired);
                }
                return;
            }

            // [FIX] For IPTV library items, if we have an IMDb ID, we should also offer addon source selection
            if (item != null && !string.IsNullOrEmpty(item.IMDbId))
            {
                string videoId = item.IMDbId;
                if (item is SeriesStream ss && selectedEpisode != null)
                {
                    videoId = $"{ss.IMDbId}:{selectedEpisode.SeasonNumber}:{selectedEpisode.EpisodeNumber}";
                }
                
                if (string.IsNullOrEmpty(streamUrl))
                {
                    await _ui.PlayStremioContent(videoId, showGlobalLoading: false, autoPlay: true);
                    return;
                }
            }

            if (!string.IsNullOrEmpty(streamUrl))
            {
                string idToPass = _ui.ResolveBestContentId(selectedEpisode?.Id ?? (item?.IMDbId ?? item?.Id.ToString()));
                
                double startSecs = -1;
                var h = HistoryManager.Instance.GetProgress(idToPass);
                if (h == null && selectedEpisode == null) h = HistoryManager.Instance.GetProgress(item?.Id.ToString() ?? "");
                if (h != null && !h.IsFinished && h.Position > 0) startSecs = h.Position;

                AppLogger.Info($"[ActionService:Play] Base ID: {idToPass} | URL: {(streamUrl?.Length > 30 ? streamUrl.Substring(0, 30) + "..." : streamUrl)} | Resume: {startSecs}s");

                if (selectedEpisode != null)
                {
                     string parentId = item is SeriesStream ss ? ss.SeriesId.ToString() : null;
                     await _ui.PerformHandoverAndNavigate(streamUrl, selectedEpisode.Title, idToPass, parentId, item.Title, selectedEpisode.SeasonNumber, selectedEpisode.EpisodeNumber, startSecs, item.PosterUrl, "series", _ui.GetCurrentBackdrop());
                }
                else if (item is LiveStream live)
                {
                    await _ui.PerformHandoverAndNavigate(streamUrl, live.Title, idToPass, null, null, 0, 0, startSecs, live.PosterUrl, "iptv", _ui.GetCurrentBackdrop());
                }
                else
                {
                    await _ui.PerformHandoverAndNavigate(streamUrl, _ui.IdentityControl?.TitleTextBlock?.Text ?? "", idToPass, startSeconds: startSecs, backdropUrl: _ui.GetCurrentBackdrop());
                }
            }
            else
            {
                // [FEEDBACK] User feedback for missing sources
                bool isSeries = item is SeriesStream || (item is Models.Stremio.StremioMediaStream sms && sms.Meta.Type == "series");
                
                if (isSeries && selectedEpisode == null)
                {
                    _ui.OpenEpisodesPanel(PanelChangeReason.EpisodeRequired);
                }
                else
                {
                    _ui.ShowActionFeedback("Kaynak Bulunamadı", "Bu içerik için şu anda kullanılabilir bir yayın adresi yok.", sender);
                }
            }
        }

        public async Task HandleTrailerClickAsync(string trailerKey, IMediaStream item, Models.Metadata.UnifiedMetadata unifiedMetadata, object sender = null)
        {
            string key = trailerKey;

            // Fallback 1: If key is null, check item's own TrailerUrl (might be populated in StremioMeta)
            if (string.IsNullOrEmpty(key) && item != null)
            {
                key = item.TrailerUrl;
            }

            // Fallback 2: TMDB Lookup if still null
            if (string.IsNullOrEmpty(key))
            {
                bool isTv = item is SeriesStream || (item is Models.Stremio.StremioMediaStream sms && (sms.Meta.Type == "series" || sms.Meta.Type == "tv"));
                
                int tmdbId = 0;
                if (unifiedMetadata?.TmdbInfo != null) tmdbId = unifiedMetadata.TmdbInfo.Id;
                else if (item?.TmdbInfo != null) tmdbId = item.TmdbInfo.Id;
                else if (unifiedMetadata != null && int.TryParse(unifiedMetadata.MetadataId, out int parsed)) tmdbId = parsed;
                else if (item != null && item.IMDbId != null && item.IMDbId.StartsWith("tt"))
                {
                    // If we have an IMDb ID but no TMDB ID yet, we could try a lookup, 
                    // but for now let's see if TmdbHelper can handle the IMDb ID or if we can parse it.
                }

                if (tmdbId > 0)
                {
                    key = await TmdbHelper.GetTrailerKeyAsync(tmdbId, isTv);
                }
            }

            if (!string.IsNullOrEmpty(key))
            {
                await _ui.PlayTrailer(key);
            }
            else
            {
                _ui.ShowActionFeedback("Fragman Bulunamadı", "Bu içerik için şu anda izlenebilir bir fragman mevcut değil.", sender);
            }
        }

        public async Task HandleDownloadClickAsync(IMediaStream item, string streamUrl, object sender)
        {
            if (string.IsNullOrEmpty(streamUrl))
            {
                _ui.ShowActionFeedback("Kaynak Seçilmedi", "İndirme yapabilmek için önce bir yayın kaynağı seçmelisiniz.", sender);
                return;
            }

            if (item is SeriesStream)
            {
                // This has specific UI (MenuFlyout), so we might need a Proxy for showing it
                // Or just keep it simple and call DownloadSingle from the page for now
                // Actually, let's just let the Page handle the Flyout for now, or implement it here.
                // To keep it clean, I'll assume the Page will call DownloadSingle/Season based on user choice.
            }
            else
            {
                await _ui.DownloadSingle();
                // Show feedback only if it's a direct file (m3u8 check is inside DownloadSingle)
                if (!streamUrl.Contains(".m3u8") && !streamUrl.Contains(".ts"))
                {
                    _ui.ShowActionFeedback("İndirme Başlatıldı", "Dosya indirme işlemi arka planda başlatıldı.", sender);
                }
            }
        }

        public void HandleCopyLinkClick(string streamUrl, object sender = null)
        {
            if (!string.IsNullOrEmpty(streamUrl))
            {
                _ui.CopyToClipboard(streamUrl);
                _ui.ShowActionFeedback("Bağlantı Kopyalandı", "Yayın adresi başarıyla panoya kopyalandı.", sender);
            }
            else
            {
                _ui.ShowActionFeedback("Bağlantı Yok", "Kopyalanacak bir yayın adresi bulunamadı.", sender);
            }
        }

        public async Task HandleWatchlistClickAsync(IMediaStream item, object sender = null)
        {
            if (item == null) return;

            bool alreadyIn = WatchlistManager.Instance.IsOnWatchlist(item);
            if (alreadyIn)
            {
                await WatchlistManager.Instance.RemoveFromWatchlist(item);
                _ui.ShowActionFeedback("Listeden Çıkarıldı", "İçerik izleme listenizden kaldırıldı.", sender);
            }
            else
            {
                await WatchlistManager.Instance.AddToWatchlist(item);
                _ui.ShowActionFeedback("Listeye Eklendi", "İçerik başarıyla izleme listenize eklendi.", sender);
            }

            _ui.SetWatchlistIcon(!alreadyIn, true);
        }

        private static string BuildRemainingText(HistoryItem history)
        {
            if (history == null || history.Duration <= 0) return "";

            double remainingSeconds = history.Duration - history.Position;
            if (remainingSeconds < 60) return "";

            var remaining = TimeSpan.FromSeconds(remainingSeconds);
            return remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}sa {(int)remaining.Minutes}dk Kaldı"
                : $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}dk Kaldı";
        }
    }
}
