using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage
    {
        private EpisodeItem _pendingAutoSelectEpisode;

        #region Season & Data Logic

        private void SeasonComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SeasonComboBox.SelectedItem is SeasonItem season)
            {
                if (season != null)
                {
                    // 1. ALWAYS populate CurrentEpisodes with what we have immediately
                    CurrentEpisodes.Clear();
                    foreach (var ep in season.Episodes) CurrentEpisodes.Add(ep);

                    // 2. Identify if enrichment is needed
                    bool hasGenericTitles = season.Episodes.Any(ep => Services.Metadata.MetadataProvider.IsGenericEpisodeTitle(ep.Title, _unifiedMetadata?.Title));

                    var tmdbInfo = _unifiedMetadata?.TmdbInfo;
                    bool tmdbEnabled = AppSettings.IsTmdbEnabled && !string.IsNullOrWhiteSpace(AppSettings.TmdbApiKey);

                    bool needsEnrichment = season.Episodes.Count == 0 || 
                                           hasGenericTitles ||
                                           (tmdbInfo != null && !season.IsEnrichedByTmdb && (season.Episodes.Any(ep => string.IsNullOrEmpty(ep.Overview) || string.IsNullOrEmpty(ep.DurationFormatted)) || tmdbEnabled));

                    if (needsEnrichment && tmdbInfo != null)
                    {
                         System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Season {season.SeasonNumber} needs enrichment. (EnrichedByTmdb={season.IsEnrichedByTmdb}, TmdbEnabled={tmdbEnabled})");
                         _ = LoadTmdbSeasonDataAsync(season.SeasonNumber);
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 1.3: Setting EpisodesRepeater ItemsSource (Count={CurrentEpisodes.Count})");
                EpisodesRepeater.ItemsSource = CurrentEpisodes;

                // Restore selection state - always update IsSelected based on _selectedEpisode
                if (_selectedEpisode != null)
                {
                    var matchingEpisode = CurrentEpisodes.FirstOrDefault(e => e.Id == _selectedEpisode.Id);
                    if (matchingEpisode != null)
                    {
                        SelectEpisode(matchingEpisode);
                        System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Restored selection: {matchingEpisode.Title}");
                    }
                }
                else if (_pendingAutoSelectEpisode != null)
                {
                    var matchingEpisode = CurrentEpisodes.FirstOrDefault(e => e.Id == _pendingAutoSelectEpisode.Id);
                     
                    if (matchingEpisode != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Auto-selecting episode: {matchingEpisode.Title} (ID: {matchingEpisode.Id})");
                        _isProgrammaticSelection = true;
                        try
                        {
                            SelectEpisode(matchingEpisode);
                            _pendingAutoSelectEpisode = null;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Auto-selection failed: {ex.Message}");
                        }
                        finally
                        {
                            _isProgrammaticSelection = false;
                        }
                    }
                }
            }
        }

        private async Task LoadTmdbSeasonDataAsync(int seasonNumber)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 7: LoadTmdbSeasonDataAsync ENTER for Season: {seasonNumber}");
             try
             {
                 if (_unifiedMetadata == null) return;
                 
                 // 1. Enrich the unified model (Fetches and Merges TMDB logic)
                 _seasonEnrichCts?.Cancel();
                 _seasonEnrichCts?.Dispose();
                 _seasonEnrichCts = new CancellationTokenSource();
                 await Services.Metadata.MetadataProvider.Instance.EnrichSeasonAsync(_unifiedMetadata, seasonNumber, ct: _seasonEnrichCts.Token);

                 DispatcherQueue.TryEnqueue(() => 
                 {
                     // 2. Re-Sync UI from Unified Model
                     var unifiedSeason = _unifiedMetadata.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
                     var uiSeason = Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
                     
                     if (unifiedSeason != null && uiSeason != null)
                     {
                         var newEpList = new List<EpisodeItem>();
                         foreach (var e in unifiedSeason.Episodes)
                         {
                             var epItem = new EpisodeItem
                             {
                                 Id = e.Id,
                                 SeasonNumber = e.SeasonNumber,
                                 EpisodeNumber = e.EpisodeNumber,
                                 Title = e.Title,
                                 Name = e.Title,
                                 Overview = e.Overview,
                                 ImageUrl = !string.IsNullOrEmpty(e.ThumbnailUrl) ? e.ThumbnailUrl : (_item?.PosterUrl ?? ""),
                                 Thumbnail = ImageHelper.GetImage(!string.IsNullOrEmpty(e.ThumbnailUrl) ? e.ThumbnailUrl : (_item?.PosterUrl ?? ""), 150, 80),
                                 StreamUrl = e.StreamUrl,
                                 IsReleased = (e.AirDate ?? DateTime.MinValue) <= DateTime.Now,
                                 DurationFormatted = (!string.IsNullOrEmpty(e.RuntimeFormatted)) ? e.RuntimeFormatted : "",
                                 Resolution = e.Resolution,
                                 VideoCodec = e.VideoCodec,
                                 Bitrate = e.Bitrate,
                                 IsHdr = e.IsHdr,
                                 IptvSeriesId = e.IptvSeriesId,
                                 IptvSourceTitle = e.IptvSourceTitle
                             };
                             epItem.RefreshHistoryState();
                             newEpList.Add(epItem);
                         }

                         uiSeason.Episodes = newEpList;
                         uiSeason.IsEnrichedByTmdb = true;
                         System.Diagnostics.Debug.WriteLine($"[MediaInfo-Flow] STEP 7.1: LoadTmdbSeasonDataAsync UI Update: Season {seasonNumber}, Count {newEpList.Count}");
                          
                         if (SeasonComboBox.SelectedItem == uiSeason)
                         {
                              var selectedEpNum = (_selectedEpisode as EpisodeItem)?.EpisodeNumber;
                              
                              _sourcesVisualGeneration++;
                              _animatedSourceRevealIndexes.Clear();
                              CurrentEpisodes.Clear();
                              foreach(var ep in newEpList) CurrentEpisodes.Add(ep);
                              
                              if (selectedEpNum.HasValue)
                              {
                                  var toSelect = CurrentEpisodes.FirstOrDefault(x => x.EpisodeNumber == selectedEpNum.Value);
                                  if (toSelect != null) SelectEpisode(toSelect);
                              }
                         }
                     }
                 });
             }
             catch (Exception ex)
             {
                 System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] LoadTmdbSeasonDataAsync Error: {ex.Message}");
             }
        }

        #endregion

        #region Repeater & Item Handlers

        private void EpisodesRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (args.Element is FrameworkElement fe && args.Index >= 0 && args.Index < CurrentEpisodes.Count)
            {
                fe.DataContext = CurrentEpisodes[args.Index];

                int generation = _sourcesVisualGeneration;
                int preparedIndex = args.Index;
                string stamp = $"ep:{generation}:{preparedIndex}";

                bool shouldReveal = !_animatedSourceRevealIndexes.Contains(preparedIndex);

                if (Equals(fe.Tag, stamp) || !shouldReveal)
                {
                    ResetRevealState(fe);
                    return;
                }

                fe.Tag = stamp;
                _animatedSourceRevealIndexes.Add(preparedIndex);
                ApplyStaggeredReveal(fe, args.Index);
            }
        }

        private void EpisodesRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args) { }

        private void EpisodeItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is EpisodeItem ep)
            {
                SelectEpisode(ep);
                e.Handled = true;
            }
        }

        private void EpisodeItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var hoverBorder = fe.FindName("HoverBorder") as Border;
                if (hoverBorder != null) AnimateOpacity(hoverBorder, 0.1, TimeSpan.FromMilliseconds(200));
            }
        }

        private void EpisodeItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var hoverBorder = fe.FindName("HoverBorder") as Border;
                if (hoverBorder != null) AnimateOpacity(hoverBorder, 0.0, TimeSpan.FromMilliseconds(200));
            }
        }

        #endregion

        #region Selection Logic

        private void SelectEpisode(EpisodeItem ep)
        {
            if (ep != null && _selectedEpisode == ep)
            {
                DeselectEpisode();
                return;
            }

            if (ep == null)
            {
                foreach (var item in CurrentEpisodes) item.IsSelected = false;
                return;
            }

            _selectedEpisode = ep;
            UpdateEpisodeUI(ep);

            UpdateInfoPanelVisibility(true);
            EnsureEpisodeTitleVisibleUnderLogo();
            if (OverviewText != null) OverviewText.Text = !string.IsNullOrWhiteSpace(ep.Overview) ? ep.Overview : "Açıklama mevcut değil.";

            _isProgrammaticSelection = true;
            try
            {
                foreach (var item in CurrentEpisodes) item.IsSelected = (item == ep);
                _streamUrl = ep.StreamUrl;
                
                string resolvedEpId = ResolveBestContentId(ep.Id);
                var history = HistoryManager.Instance.GetProgress(resolvedEpId);
                if (history != null && !string.IsNullOrEmpty(history.StreamUrl)) _streamUrl = history.StreamUrl;
                
                StartPrebuffering(_streamUrl);
                if (!string.IsNullOrEmpty(_streamUrl)) _ = UpdateTechnicalBadgesAsync(_streamUrl);
                
                SyncLayout();
            }
            finally { _isProgrammaticSelection = false; }
        }

        private void UpdateEpisodeUI(EpisodeItem ep)
        {
            if (ep == null) return;
            // UPDATE INFO PANEL (Already handled in SelectEpisode for text, but can add more logic here)
        }

        private void DeselectEpisode()
        {
            _isProgrammaticSelection = true;
            try
            {
                _selectedEpisode = null;
                _streamUrl = null;

                if (EpisodesRepeater != null)
                {
                    foreach (var item in CurrentEpisodes) item.IsSelected = false;
                }

                // Restore UI State
                ShowSourcesPanel(false);
                UpdateInfoPanelVisibility(false);

                if (PlayButtonText != null)
                {
                    PlayButtonText.Text = "Oynat";
                    if (PlayButtonSubtext != null) PlayButtonSubtext.Visibility = Visibility.Collapsed;
                }
                if (RestartButton != null) RestartButton.Visibility = Visibility.Collapsed;

                SyncLayout();
            }
            finally
            {
                _isProgrammaticSelection = false;
            }
        }

        #endregion
    }
}
