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
                    _isEpisodesLoading = season.Episodes.Count == 0;
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
                         System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] Season {season.SeasonNumber} needs enrichment. (EnrichedByTmdb={season.IsEnrichedByTmdb}, TmdbEnabled={tmdbEnabled})");
                         _ = LoadTmdbSeasonDataAsync(season.SeasonNumber);
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] STEP 1.3: Setting EpisodesRepeater ItemsSource (Count={CurrentEpisodes.Count})");
                EpisodesRepeater.ItemsSource = CurrentEpisodes;

                // Restore selection state - always update IsSelected based on _selectedEpisode
                if (_selectedEpisode != null)
                {
                    var matchingEpisode = CurrentEpisodes.FirstOrDefault(e => e.Id == _selectedEpisode.Id);
                    if (matchingEpisode != null)
                    {
                        SelectEpisode(matchingEpisode);
                        System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] Restored selection: {matchingEpisode.Title}");
                    }
                }
                else if (_pendingAutoSelectEpisode != null)
                {
                    var matchingEpisode = CurrentEpisodes.FirstOrDefault(e => e.Id == _pendingAutoSelectEpisode.Id);
                     
                    if (matchingEpisode != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] Auto-selecting episode: {matchingEpisode.Title} (ID: {matchingEpisode.Id})");
                        _isProgrammaticSelection = true;
                        try
                        {
                            SelectEpisode(matchingEpisode);
                            _pendingAutoSelectEpisode = null;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] Auto-selection failed: {ex.Message}");
                        }
                        finally
                        {
                            _isProgrammaticSelection = false;
                        }
                    }
                }
                
                // [REVEAL SYNC] Re-evaluate layout now that episodes are in the binding collection.
                // Using Low priority ensures ItemsRepeater has a chance to see the new collection.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
                    SyncLayout();
                });
            }
        }

        private async Task LoadTmdbSeasonDataAsync(int seasonNumber)
        {
            System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] STEP 7: LoadTmdbSeasonDataAsync ENTER for Season: {seasonNumber}");
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
                         System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] STEP 7.1: LoadTmdbSeasonDataAsync UI Update: Season {seasonNumber}, Count {newEpList.Count}");
                          
                         if (SeasonComboBox.SelectedItem == uiSeason)
                         {
                              var selectedEpNum = (_selectedEpisode as EpisodeItem)?.EpisodeNumber;
                              
                              _sourcesVisualGeneration++;
                              _animatedSourceRevealIndexes.Clear();
                              _isEpisodesLoading = false;
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
                 System.Diagnostics.Debug.WriteLine($"[INFO-PAGE] LoadTmdbSeasonDataAsync Error: {ex.Message}");
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
                ApplySelectedEpisodeHistoryActions(history);
                
                RefreshAllAddonActiveFlags();
                SyncAddonSelectionToActive();

                // Selecting an episode is intentionally side-effect light: it updates the hero
                // metadata and selected row, but it does not request sources. The arrow/source
                // commands own that panel transition so the episodes list stays visible.
                SyncLayout();
            }
            finally { _isProgrammaticSelection = false; }
        }

        private void UpdateEpisodeUI(EpisodeItem ep)
        {
            if (ep == null) return;
            // UPDATE INFO PANEL (Already handled in SelectEpisode for text, but can add more logic here)
        }

        private void ApplySelectedEpisodeHistoryActions(HistoryItem history)
        {
            if (PlayButtonText == null) return;

            bool hasProgress = history != null && history.Position > 0;
            double progressPercent = history?.Duration > 0
                ? (history.Position / history.Duration) * 100
                : 0;
            bool canContinue = hasProgress && !history.IsFinished && progressPercent < 98;

            if (canContinue)
            {
                PlayButtonText.Text = "Devam Et";
                if (StickyPlayButtonText != null) StickyPlayButtonText.Text = "Devam Et";

                string subtext = "";
                if (history.Duration > 0)
                {
                    subtext = BuildRemainingText(history);
                }

                if (PlayButtonSubtext != null)
                {
                    PlayButtonSubtext.Text = subtext;
                    PlayButtonSubtext.Visibility = string.IsNullOrWhiteSpace(subtext) ? Visibility.Collapsed : Visibility.Visible;
                }

                if (StickyPlayButtonSubtext != null)
                {
                    StickyPlayButtonSubtext.Text = subtext;
                    StickyPlayButtonSubtext.Visibility = string.IsNullOrWhiteSpace(subtext) ? Visibility.Collapsed : Visibility.Visible;
                }

                if (RestartButton != null) RestartButton.Visibility = Visibility.Visible;
                return;
            }

            if (hasProgress && (history.IsFinished || progressPercent >= 98))
            {
                PlayButtonText.Text = "Tekrar İzle";
                if (StickyPlayButtonText != null) StickyPlayButtonText.Text = "Tekrar İzle";
                if (PlayButtonSubtext != null) PlayButtonSubtext.Visibility = Visibility.Collapsed;
                if (StickyPlayButtonSubtext != null) StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
                if (RestartButton != null) RestartButton.Visibility = Visibility.Visible;
                return;
            }

            PlayButtonText.Text = "Oynat";
            if (StickyPlayButtonText != null) StickyPlayButtonText.Text = "Oynat";
            if (PlayButtonSubtext != null) PlayButtonSubtext.Visibility = Visibility.Collapsed;
            if (StickyPlayButtonSubtext != null) StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
            if (RestartButton != null) RestartButton.Visibility = Visibility.Collapsed;
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
                OpenEpisodesPanel(PanelChangeReason.EpisodeDeselected);
                UpdateInfoPanelVisibility(false);

                if (PlayButtonText != null)
                {
                    PlayButtonText.Text = "Oynat";
                    if (PlayButtonSubtext != null) PlayButtonSubtext.Visibility = Visibility.Collapsed;
                }
                if (RestartButton != null) RestartButton.Visibility = Visibility.Collapsed;

                if (!_isResettingPageState) SyncLayout();
            }
            finally
            {
                _isProgrammaticSelection = false;
            }
        }

        #endregion
        
        private List<EpisodeItem> CreateEpisodePlaceholders(int count)
        {
            var list = new List<EpisodeItem>();
            foreach (var opacity in GenerateShimmerOpacitySequence(count))
            {
                list.Add(new EpisodeItem
                {
                    IsPlaceholder = true,
                    ShimmerOpacity = opacity
                });
            }
            return list;
        }
    }
}
