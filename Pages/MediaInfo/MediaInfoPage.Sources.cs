using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage
    {
        #region Content Resolution & Early Sources

        private bool ShouldOpenMovieSourcesEarly(IMediaStream item)
        {
            if (item == null || IsProbablySeriesItem(item)) return false;
            if (item is LiveStream) return false;
            if (item is Models.Stremio.StremioMediaStream sms)
            {
                return !string.Equals(sms.Meta?.Type, "series", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(sms.Meta?.Type, "tv", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        private bool IsProbablySeriesItem(IMediaStream item)
        {
            if (item is SeriesStream) return true;
            if (item is Models.Stremio.StremioMediaStream sms)
            {
                return string.Equals(sms.Meta?.Type, "series", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(sms.Meta?.Type, "tv", StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(item.Type, "series", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(item.Type, "tv", StringComparison.OrdinalIgnoreCase);
        }

        private void PrepareEarlyMovieSourcesPanel(IMediaStream item)
        {
            if (!ShouldOpenMovieSourcesEarly(item)) return;

            var addons = Services.Stremio.StremioAddonManager.Instance.GetAddons();
            if (addons == null || addons.Count == 0) return;

            _isSourcesFetchInProgress = true;
            OpenSourcesPanel(PanelChangeReason.MovieAutoSources);

            _sourcesManager?.InitializeLoading(addons, GetDynamicShimmerCount());

            OnDataCommitted();
        }

        #endregion

        #region Event Handlers (delegated to SourcesManager)

        private void SourcesRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            _sourcesManager?.OnElementPrepared(sender, args);
        }

        private void SourcesRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            _sourcesManager?.OnElementClearing(sender, args);
        }

        private void SourceItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _sourcesManager?.OnSourceItemPointerEntered(sender);
        }

        private void SourceItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _sourcesManager?.OnSourceItemPointerExited(sender);
        }

        private async void SourceItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is StremioStreamViewModel vm)
            {
                if (vm.IsPlaceholder) return;

                _sourcesManager?.SetActiveStream(vm);
                string playUrl = await UrlResolver.ResolveUrlAsync(vm.Url);
                _streamUrl = playUrl;
                _sourceAddonUrl = vm.AddonUrl;
                if (!string.IsNullOrEmpty(_streamUrl)) _ = UpdateTechnicalBadgesAsync(_streamUrl);

                string title = _selectedEpisode?.Title ?? _item.Title;
                string videoId = ResolveBestContentId(_selectedEpisode?.Id ?? (_item as Models.Stremio.StremioMediaStream)?.Meta?.Id);

                if (!string.IsNullOrEmpty(vm.Url))
                {
                    var history = HistoryManager.Instance.GetProgress(videoId);
                    HistoryManager.Instance.UpdateProgress(videoId, title, _streamUrl, history?.Position ?? 0, history?.Duration ?? 0);
                    await HandleSourcePlaybackHandoff(playUrl, title, videoId);
                }
                else if (!string.IsNullOrEmpty(vm.Externalurl))
                {
                    _ = Windows.System.Launcher.LaunchUriAsync(new Uri(vm.Externalurl));
                }
                else if (vm.OriginalStream != null && !string.IsNullOrEmpty(vm.OriginalStream.Infohash))
                {
                    var tip = new TeachingTip { Title = "Torrent Bilgisi", Subtitle = "Torrent akışları yakında desteklenecek. Lütfen HTTP kaynaklarını kullanın.", IsLightDismissEnabled = true };
                    tip.XamlRoot = this.XamlRoot;
                    tip.IsOpen = true;
                }
                else
                {
                    Debug.WriteLine($"[Stremio] Clicked item with no URL or Infohash: {vm.Title}");
                }
            }
        }

        private void AddonSelectorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListView lv && lv.SelectedItem is StremioAddonViewModel addon)
            {
                if (_isApplyingSourceSelection) return;
                _sourcesManager?.HandleUserSelection(addon);
                _sourcesManager?.ScrollToActiveSource();
            }
        }

        #endregion

        #region Shimmer & Utility

        internal int GetDynamicShimmerCount()
        {
            try
            {
                double height = SourcesScrollViewer?.ActualHeight ?? 0;
                if (height <= 0) return 8;
                return _detailPanelController?.CalculateSkeletonCount(height, 92.0) ?? 8;
            }
            catch { return 8; }
        }

        private void RefreshAllShimmers()
        {
            if (_panelOwner != null && _panelOwner.PanelMode == MediaDetailPanelMode.None &&
                (SourcesPanel == null || SourcesPanel.Visibility != Visibility.Visible) &&
                (EpisodesPanel == null || EpisodesPanel.Visibility != Visibility.Visible)) return;

            // Root optimization: If nothing is currently in a loading state (neither episodes nor sources),
            // there are no shimmers on screen. Do not waste CPU cycles.
            bool isSourcesLoading = AddonResults != null && AddonResults.Any(a => a.IsLoading);
            if (!_isEpisodesLoading && !isSourcesLoading) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                int count = GetDynamicShimmerCount();
                _sourcesManager?.RefreshSelectedShimmerCount(count);

                if (_isEpisodesLoading)
                {
                    var placeholders = _detailPanelController?.CreateEpisodePlaceholders(count);
                    if (placeholders != null && (CurrentEpisodes.Count != count || (count > 0 && CurrentEpisodes.Any(e => !e.IsPlaceholder))))
                    {
                        CurrentEpisodes.Clear();
                        foreach (var p in placeholders) CurrentEpisodes.Add(p);
                    }
                }
            });
        }

        internal void ResetSourcesListTransitionState()
        {
            _detailPanelController?.SourcesState.Reset();
        }

        #endregion

        #region Playback Handoff

        private async Task HandleSourcePlaybackHandoff(string playUrl, string title, string videoId)
        {
            if (string.IsNullOrEmpty(playUrl)) return;

            _ = UpdateTechnicalBadgesAsync(playUrl);

            var history = HistoryManager.Instance.GetProgress(videoId);
            double resumeSeconds = (history != null && !history.IsFinished && history.Position > 0) ? history.Position : -1;

            string streamType = "movie";
            string parentIdStr = null;
            if (_item is SeriesStream ss)
            {
                streamType = "series";
                parentIdStr = ss.SeriesId.ToString();
            }
            else if (_item is Models.Stremio.StremioMediaStream stType && (stType.Meta.Type == "series" || stType.Meta.Type == "tv"))
            {
                streamType = "series";
                parentIdStr = stType.Meta.Id;
            }

            bool hasExistingPlayer = MediaInfoPlayer != null;
            string currentPlayerPath = null;

            if (hasExistingPlayer)
            {
                try { currentPlayerPath = await MediaInfoPlayer.GetPropertyAsync("path"); } catch { }
            }

            bool isSameSource = hasExistingPlayer
                && !string.IsNullOrEmpty(currentPlayerPath)
                && currentPlayerPath != "N/A"
                && (currentPlayerPath == playUrl || currentPlayerPath.Contains(playUrl));

            if (isSameSource)
            {
                await PerformHandoverAndNavigate(playUrl, title, videoId, parentIdStr, null,
                    _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0,
                    resumeSeconds, _item.PosterUrl, streamType, _backgroundManager?.GetCurrentBackdrop() ?? string.Empty);
            }
            else if (hasExistingPlayer && !string.IsNullOrEmpty(currentPlayerPath) && currentPlayerPath != "N/A")
            {
                try
                {
                    _prebufferCts?.Cancel(); _prebufferCts?.Dispose(); _prebufferCts = null;

                    await MediaInfoPlayer.SetPropertyAsync("keep-open", "yes");
                    await MediaInfoPlayer.SetPropertyAsync("force-window", "yes");
                    await MpvSetupHelper.ConfigurePlayerAsync(MediaInfoPlayer, playUrl, isSecondary: true);
                    await MediaInfoPlayer.SetPropertyAsync("pause", "yes");

                    if (resumeSeconds > 0)
                        await MediaInfoPlayer.SetPropertyAsync("start", resumeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    else
                        await MediaInfoPlayer.SetPropertyAsync("start", "0");

                    await MediaInfoPlayer.OpenAsync(playUrl);
                    await MediaInfoPlayer.SetPropertyAsync("mute", "yes");

                    await PerformHandoverAndNavigate(playUrl, title, videoId, parentIdStr, null,
                        _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0,
                        resumeSeconds, _item.PosterUrl, streamType, _backgroundManager?.GetCurrentBackdrop() ?? string.Empty);
                }
                catch { await PerformHandoverAndNavigate(playUrl, title, videoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, resumeSeconds, _item.PosterUrl, streamType, _backgroundManager?.GetCurrentBackdrop() ?? string.Empty); }
            }
            else
            {
                await PerformHandoverAndNavigate(playUrl, title, videoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, resumeSeconds, _item.PosterUrl, streamType, _backgroundManager?.GetCurrentBackdrop() ?? string.Empty);
            }
        }

        #endregion
    }
}
