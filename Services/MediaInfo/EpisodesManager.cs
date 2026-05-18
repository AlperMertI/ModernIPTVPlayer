using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Services.Metadata;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Manages episode and season lifecycle: loading, selection, enrichment, UI updates.
    /// Extracted from MediaInfoPage.Episodes.cs to isolate series concerns.
    /// </summary>
    internal sealed class EpisodesManager : IDisposable
    {
        private readonly MediaInfoPage _page;
        private readonly PanelRevealState _episodesState;
        private bool _disposed;
        private bool _isProgrammaticSelection;
        private EpisodeItem _pendingAutoSelectEpisode;

        public ObservableCollection<SeasonItem> Seasons { get; } = new();
        public ObservableCollection<EpisodeItem> CurrentEpisodes { get; } = new();

        private EpisodeItem _selectedEpisode;
        private SeasonItem _selectedSeason;

        public EpisodeItem SelectedEpisode => _selectedEpisode;
        public SeasonItem SelectedSeason => _selectedSeason;

        public event Action<EpisodeItem>? EpisodeSelected;
        public event Action? EpisodeDeselected;

        public EpisodesManager(MediaInfoPage page, PanelRevealState episodesState)
        {
            _page = page ?? throw new ArgumentNullException(nameof(page));
            _episodesState = episodesState ?? throw new ArgumentNullException(nameof(episodesState));
            Debug.WriteLine("[EPISODES] Initialized");
        }

        public void SetPendingAutoSelectEpisode(EpisodeItem episode)
        {
            _pendingAutoSelectEpisode = episode;
        }

        public void LoadSeasons(IEnumerable<SeasonItem> seasonItems)
        {
            if (_disposed || seasonItems == null) return;

            try
            {
                Seasons.Clear();
                foreach (var season in seasonItems)
                {
                    Seasons.Add(season);
                }
                Debug.WriteLine($"[EPISODES] Loaded {Seasons.Count} seasons");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EPISODES] LoadSeasons error: {ex.Message}");
            }
        }

        public void PopulateEpisodesFromSeason(SeasonItem season)
        {
            if (_disposed || season == null) return;

            try
            {
                _selectedSeason = season;
                _episodesState.PrepareOpen(season.Episodes.Count);

                CurrentEpisodes.Clear();
                foreach (var ep in season.Episodes)
                {
                    CurrentEpisodes.Add(ep);
                }

                Debug.WriteLine($"[EPISODES] Populated {CurrentEpisodes.Count} episodes from season {season.SeasonNumber}");

                // Check if enrichment is needed
                bool hasGenericTitles = season.Episodes.Any(ep =>
                    ModernIPTVPlayer.Services.Metadata.MetadataProvider.IsGenericEpisodeTitle(ep.Title, _page.UnifiedMetadata?.Title));

                var tmdbInfo = _page.UnifiedMetadata?.TmdbInfo;
                bool tmdbEnabled = AppSettings.IsTmdbEnabled && !string.IsNullOrWhiteSpace(AppSettings.TmdbApiKey);

                bool needsEnrichment = season.Episodes.Count == 0 ||
                    hasGenericTitles ||
                    (tmdbInfo != null && !season.IsEnrichedByTmdb &&
                     (season.Episodes.Any(ep => string.IsNullOrEmpty(ep.Overview) || string.IsNullOrEmpty(ep.DurationFormatted)) || tmdbEnabled));

                if (needsEnrichment && tmdbInfo != null)
                {
                    Debug.WriteLine($"[EPISODES] Season {season.SeasonNumber} needs enrichment");
                    _ = LoadTmdbSeasonDataAsync(season.SeasonNumber);
                }

                // Restore selection
                if (_selectedEpisode != null)
                {
                    var matching = CurrentEpisodes.FirstOrDefault(e => e.Id == _selectedEpisode.Id);
                    if (matching != null)
                    {
                        _page.SetSelectedEpisode(matching);
                        SelectEpisode(matching);
                        Debug.WriteLine($"[EPISODES] Restored selection: {matching.Title}");
                    }
                }
                else if (_pendingAutoSelectEpisode != null)
                {
                    var matching = CurrentEpisodes.FirstOrDefault(e => e.Id == _pendingAutoSelectEpisode.Id);
                    if (matching != null)
                    {
                        _page.SetSelectedEpisode(matching);
                        Debug.WriteLine($"[EPISODES] Auto-selecting: {matching.Title}");
                        _isProgrammaticSelection = true;
                        try { SelectEpisode(matching); }
                        finally { _isProgrammaticSelection = false; }
                        _pendingAutoSelectEpisode = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EPISODES] PopulateEpisodesFromSeason error: {ex.Message}");
            }
        }

        private async Task LoadTmdbSeasonDataAsync(int seasonNumber)
        {
            if (_disposed || _page.UnifiedMetadata == null) return;

            try
            {
                var cts = _page.SeasonEnrichCts;
                cts?.Cancel();
                cts?.Dispose();
                cts = new System.Threading.CancellationTokenSource();
                _page.SeasonEnrichCts = cts;

                await ModernIPTVPlayer.Services.Metadata.MetadataProvider.Instance.EnrichSeasonAsync(
                    _page.UnifiedMetadata, seasonNumber, ct: cts.Token);

                _page.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var unifiedSeason = _page.UnifiedMetadata.Seasons
                            .FirstOrDefault(s => s.SeasonNumber == seasonNumber);
                        var uiSeason = Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);

                        if (unifiedSeason != null && uiSeason != null)
                        {
                            var newEpList = new List<EpisodeItem>();
                            foreach (var e in unifiedSeason.Episodes)
                            {
                                newEpList.Add(new EpisodeItem
                                {
                                    Id = e.Id,
                                    SeasonNumber = e.SeasonNumber,
                                    EpisodeNumber = e.EpisodeNumber,
                                    Title = e.Title,
                                    Name = e.Title,
                                    Overview = e.Overview,
                                    ImageUrl = !string.IsNullOrEmpty(e.ThumbnailUrl)
                                        ? e.ThumbnailUrl
                                        : (_page.Item?.PosterUrl ?? ""),
                                    Thumbnail = ImageHelper.GetImage(
                                        !string.IsNullOrEmpty(e.ThumbnailUrl)
                                            ? e.ThumbnailUrl
                                            : (_page.Item?.PosterUrl ?? ""), 150, 80),
                                    StreamUrl = e.StreamUrl,
                                    IsReleased = (e.AirDate ?? DateTime.MinValue) <= DateTime.Now,
                                    DurationFormatted = !string.IsNullOrEmpty(e.RuntimeFormatted)
                                        ? e.RuntimeFormatted : "",
                                    Resolution = e.Resolution,
                                    VideoCodec = e.VideoCodec,
                                    Bitrate = e.Bitrate,
                                    IsHdr = e.IsHdr,
                                    IptvSeriesId = e.IptvSeriesId,
                                    IptvSourceTitle = e.IptvSourceTitle
                                });
                                newEpList.Last().RefreshHistoryState();
                            }

                            uiSeason.Episodes = newEpList;
                            uiSeason.IsEnrichedByTmdb = true;
                            Debug.WriteLine($"[EPISODES] Enriched season {seasonNumber}: {newEpList.Count} episodes");

                            if (_page.SeasonComboBoxControl?.SelectedItem == uiSeason)
                            {
                                var selectedEpNum = _selectedEpisode?.EpisodeNumber;
                                _episodesState.PrepareOpen(newEpList.Count);
                                _page.IsEpisodesLoading = false;

                                CurrentEpisodes.Clear();
                                foreach (var ep in newEpList) CurrentEpisodes.Add(ep);

                                if (selectedEpNum.HasValue)
                                {
                                    var toSelect = CurrentEpisodes
                                        .FirstOrDefault(x => x.EpisodeNumber == selectedEpNum.Value);
                                    if (toSelect != null)
                                    {
                                        _page.SetSelectedEpisode(toSelect);
                                        SelectEpisode(toSelect);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[EPISODES] TMDB enrichment UI update error: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EPISODES] LoadTmdbSeasonDataAsync error: {ex.Message}");
            }
        }

        public void SelectEpisode(EpisodeItem episode)
        {
            if (_disposed) return;

            try
            {
                if (episode != null && _selectedEpisode == episode)
                {
                    DeselectEpisode();
                    return;
                }

                if (episode == null)
                {
                    foreach (var item in CurrentEpisodes) item.IsSelected = false;
                    return;
                }

                _selectedEpisode = episode;
                _page.SetSelectedEpisode(episode);

                _isProgrammaticSelection = true;
                try
                {
                    foreach (var item in CurrentEpisodes)
                        item.IsSelected = (item == episode);

                    _page.StreamUrl = episode.StreamUrl;

                    string resolvedEpId = _page.ResolveBestContentId(episode.Id);
                    var history = HistoryManager.Instance.GetProgress(resolvedEpId);
                    if (history != null && !string.IsNullOrEmpty(history.StreamUrl))
                        _page.StreamUrl = history.StreamUrl;

                    _page.StartPrebuffering(_page.StreamUrl);
                    if (!string.IsNullOrEmpty(_page.StreamUrl))
                        _ = _page.UpdateTechnicalBadgesAsync(_page.StreamUrl);

                    _page.SyncActionButtonsInternal(history);
                    _page.RefreshAllAddonActiveFlags();
                    _page.SyncAddonSelectionToActive();

                    _page.OnIdentityChanged();
                    _page.UpdateInfoPanelVisibility(true);
                    _page.SetOverviewText(!string.IsNullOrWhiteSpace(episode.Overview)
                        ? episode.Overview : "Açıklama mevcut değil.");
                    _page.EnsureEpisodeTitleVisibleUnderLogo();

                    Debug.WriteLine($"[EPISODES] Selected: {episode.Title} (S{episode.SeasonNumber}E{episode.EpisodeNumber})");
                    EpisodeSelected?.Invoke(episode);
                }
                finally
                {
                    _isProgrammaticSelection = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EPISODES] SelectEpisode error: {ex.Message}");
            }
        }

        public void DeselectEpisode()
        {
            if (_disposed) return;

            try
            {
                _isProgrammaticSelection = true;
                try
                {
                    _selectedEpisode = null;
                    _page.SetSelectedEpisode(null);
                    _page.StreamUrl = null;

                    foreach (var item in CurrentEpisodes) item.IsSelected = false;

                    _page.OpenEpisodesPanel(PanelChangeReason.EpisodeDeselected);
                    _page.UpdateInfoPanelVisibility(false);
                    _page.SyncActionButtonsInternal(null);

                    if (!_page.IsResettingPageState) _page.OnDataCommitted();

                    Debug.WriteLine("[EPISODES] Deselected");
                    EpisodeDeselected?.Invoke();
                }
                finally
                {
                    _isProgrammaticSelection = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EPISODES] DeselectEpisode error: {ex.Message}");
            }
        }

        public void Clear()
        {
            if (_disposed) return;
            try
            {
                Seasons.Clear();
                CurrentEpisodes.Clear();
                _selectedEpisode = null;
                _selectedSeason = null;
                _pendingAutoSelectEpisode = null;
                Debug.WriteLine("[EPISODES] Cleared");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EPISODES] Clear error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Debug.WriteLine("[EPISODES] Disposed");
        }
    }
}
