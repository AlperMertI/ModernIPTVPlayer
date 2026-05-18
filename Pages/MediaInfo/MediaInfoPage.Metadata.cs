using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models.Tmdb;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.MediaInfo;

namespace ModernIPTVPlayer
{
    /// <summary>
    /// Partial class managing detail loading metadata enrichment pipelines, IPTV series progress, and Stremio addon catalog orchestrators.
    /// </summary>
    public sealed partial class MediaInfoPage : Page
    {
        #region Details Load Pipeline

        /// <summary>
        /// Orchestrates high-level asynchronous metadata retrieval and layout shimmers for the navigated media item.
        /// </summary>
        private async Task LoadDetailsAsync(IMediaStream item, TmdbMovieResult preFetchedTmdb = null, IMediaStream previousItem = null, int? loadSession = null, UnifiedMetadata prePeekedMetadata = null)
        {
            try
            {
                TraceMediaInfo("LoadDetailsAsync start", new Dictionary<string, object?> { ["title"] = item?.Title });
                
                if (item == null)
                {
                    return;
                }

                if (_pageOrchestrator != null)
                {
                    await _pageOrchestrator.LoadDetailsAsync(item, prePeekedMetadata, previousItem, loadSession);
                }
                else
                {
                    await LoadDetailsInternalAsync(item, prePeekedMetadata, previousItem);
                }

                if (item is Models.Stremio.StremioMediaStream stremioMovie && stremioMovie.Meta?.Type == "movie")
                {
                    _ = PlayStremioContent(stremioMovie.Meta.Id, showGlobalLoading: false, autoPlay: false);
                }
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("LoadDetailsAsync error", ex);
                _loadPipeline?.SetError(ex.Message);
            }
        }

        /// <summary>
        /// Executes deep multi-layered metadata provider queries and updates commit services.
        /// </summary>
        private async Task LoadDetailsInternalAsync(IMediaStream item, UnifiedMetadata prePeekedMetadata, IMediaStream previousItem)
        {
            try
            {
                int session = BeginLoadSession();

                ResetPageState();
                
                _loadPipeline?.Reset();
                _pageLoadState = PageLoadState.Initial;
                _loadPipeline?.TransitionTo(LoadPipeline.State.Preparing);
                SetLoadStateInternal(PageLoadState.Loading);
                _loadPipeline?.TransitionTo(LoadPipeline.State.Fetching);

                await PrepareInfoSkeletonForRevealAsync();

                UnifiedMetadata metadata = null;
                if (prePeekedMetadata != null)
                {
                    metadata = prePeekedMetadata;
                }
                else if (item != null)
                {
                    try
                    {
                        metadata = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(
                            item,
                            Models.Metadata.MetadataContext.Detail,
                            ct: _pageCts?.Token ?? default);
                    }
                    catch (Exception ex)
                    {
                        ModernIPTVPlayer.Services.AppLogger.Error("Metadata fetch failed", ex);
                        _loadPipeline?.SetError($"Metadata fetch: {ex.Message}");
                    }
                }

                if (metadata != null && Volatile.Read(ref _loadingVersion) == session)
                {
                    _unifiedMetadata = metadata;
                    _loadPipeline?.TransitionTo(LoadPipeline.State.Committing);
                    await _commitService.CommitAsync(metadata, item);
                }

                if (Volatile.Read(ref _loadingVersion) == session)
                {
                    StaggeredRevealContent();
                }
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("LoadDetailsInternalAsync error", ex);
                _loadPipeline?.SetError(ex.Message);
            }
        }

        /// <summary>
        /// Standard helper mapping unified metadata text values directly to active XAML TextBlocks.
        /// </summary>
        private void ApplyMetadataToUI(UnifiedMetadata metadata)
        {
            try
            {
                if (metadata == null) return;

                if (YearText != null && !string.IsNullOrEmpty(metadata.Year))
                {
                    YearText.Text = metadata.Year;
                }

                if (OverviewText != null && !string.IsNullOrEmpty(metadata.Overview))
                {
                    OverviewText.Text = metadata.Overview;
                }

                if (GenresText != null && !string.IsNullOrEmpty(metadata.Genres))
                {
                    GenresText.Text = metadata.Genres;
                }
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("ApplyMetadataToUI error", ex);
            }
        }

        /// <summary>
        /// Slide-reveals a completed people section (Cast or Director) cleanly once loading finishes.
        /// </summary>
        private void RevealPeopleSectionIfReady(FrameworkElement? section, FrameworkElement? shimmer, int itemCount, ref int revealedGeneration)
        {
            if (section == null || itemCount <= 0 || _pageLoadState != PageLoadState.Ready)
            {
                return;
            }

            if (revealedGeneration == itemCount && section.Opacity >= 0.99)
            {
                return;
            }

            if (section.Opacity >= 0.99 && shimmer?.Visibility != Visibility.Visible)
            {
                revealedGeneration = itemCount;
                return;
            }

            revealedGeneration = itemCount;
            section.Visibility = Visibility.Visible;
            section.Opacity = 1;
            if (shimmer != null) shimmer.Visibility = Visibility.Collapsed;

            CompositionService.Run(section, visual => 
            {
                var compositor = visual.Compositor;
                CompositionService.StopAll(visual);
                
                visual.Opacity = 0f;
                visual.Offset = new Vector3(0, 10, 0);

                var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 0.86f), new Vector2(0.16f, 1f));
                var opacity = compositor.CreateScalarKeyFrameAnimation();
                opacity.InsertKeyFrame(0f, 0f);
                opacity.InsertKeyFrame(1f, 1f, easing);
                opacity.Duration = TimeSpan.FromMilliseconds(360);

                var offset = compositor.CreateVector3KeyFrameAnimation();
                offset.InsertKeyFrame(0f, new Vector3(0, 10, 0));
                offset.InsertKeyFrame(1f, Vector3.Zero, easing);
                offset.Duration = TimeSpan.FromMilliseconds(460);

                visual.StartAnimation(nameof(Visual.Opacity), opacity);
                visual.StartAnimation(nameof(Visual.Offset), offset);
            });
        }

        #endregion

        #region Series Progress & Season Adapters

        /// <summary>
        /// Transforms unified season/episode catalogs into rich EpisodeItems and binds them to the SeasonComboBox.
        /// </summary>
        internal async Task LoadSeriesDataAsync(UnifiedMetadata unified)
        {
            try
            {
                if (Seasons.Count == 0)
                {
                    _isEpisodesLoading = true;
                    RefreshAllShimmers();
                }
                
                if (unified.Seasons == null || unified.Seasons.Count == 0)
                {
                    _isEpisodesLoading = false;
                    RefreshAllShimmers();
                    return;
                }

                var newSeasons = new List<SeasonItem>();
                foreach (var s in unified.Seasons)
                {
                    var epList = new List<EpisodeItem>();
                    foreach (var e in s.Episodes)
                    {
                        var epItem = new EpisodeItem
                        {
                            Id = e.Id,
                            SeasonNumber = e.SeasonNumber,
                            EpisodeNumber = e.EpisodeNumber,
                            Title = e.Title,
                            Name = e.Title,
                            Overview = e.Overview,
                            ImageUrl = !string.IsNullOrEmpty(e.ThumbnailUrl) ? e.ThumbnailUrl : (unified.PosterUrl ?? ""),
                            Thumbnail = ImageHelper.GetImage(!string.IsNullOrEmpty(e.ThumbnailUrl) ? e.ThumbnailUrl : (unified.PosterUrl ?? ""), 150, 80),
                            ReleaseDate = e.AirDate,
                            IsReleased = e.AirDate.HasValue ? e.AirDate.Value <= DateTime.Now : true,
                            StreamUrl = e.StreamUrl,
                            Resolution = e.Resolution,
                            VideoCodec = e.VideoCodec,
                            Bitrate = e.Bitrate,
                            IsHdr = e.IsHdr,
                            IptvSeriesId = e.IptvSeriesId,
                            IptvSourceTitle = e.IptvSourceTitle
                        };
                        epItem.RefreshHistoryState();
                        epList.Add(epItem);
                    }

                    if (epList.Count > 0)
                    {
                        var seasonItem = new SeasonItem
                        {
                            SeasonNumber = s.SeasonNumber,
                            Name = s.Name ?? (s.SeasonNumber == 0 ? "Özel Bölümler" : $"{s.SeasonNumber}. Sezon"),
                            SeasonName = s.Name ?? (s.SeasonNumber == 0 ? "Özel Bölümler" : $"{s.SeasonNumber}. Sezon"),
                            Episodes = epList,
                            IsEnrichedByTmdb = s.IsEnrichedByTmdb
                        };
                        newSeasons.Add(seasonItem);
                    }
                }
                
                bool contentChanged = _episodesManager.Seasons.Count > 0 && newSeasons.Count > 0 &&
                    _episodesManager.Seasons[0].Episodes.Count > 0 && newSeasons[0].Episodes.Count > 0 &&
                    _episodesManager.Seasons[0].Episodes[0].Title != newSeasons[0].Episodes[0].Title;

                bool seriesChanged = _episodesManager.Seasons.Count != newSeasons.Count || contentChanged ||
                    (_episodesManager.Seasons.Count > 0 && _episodesManager.Seasons[0].Episodes.Count != newSeasons[0].Episodes.Count);

                if (seriesChanged)
                {
                    _episodesManager?.Clear();
                    _episodesManager?.LoadSeasons(newSeasons);

                    Seasons.Clear();
                    foreach (var s in newSeasons) Seasons.Add(s);
                }

                int targetSeasonIndex = 0;
                EpisodeItem episodeToSelect = null;
                var lastWatched = HistoryManager.Instance.GetLastWatchedEpisode(unified.MetadataId);
                
                if (lastWatched != null)
                {
                    if (lastWatched.IsFinished)
                    {
                        var nextEp = unified.Seasons
                            .SelectMany(s => s.Episodes)
                            .OrderBy(e => e.SeasonNumber)
                            .ThenBy(e => e.EpisodeNumber)
                            .FirstOrDefault(e => 
                                (e.SeasonNumber == lastWatched.SeasonNumber && e.EpisodeNumber > lastWatched.EpisodeNumber) ||
                                (e.SeasonNumber > lastWatched.SeasonNumber));
                        
                        if (nextEp != null)
                        {
                            var foundSeason = Seasons.FirstOrDefault(s => s.SeasonNumber == nextEp.SeasonNumber);
                            if (foundSeason != null)
                            {
                                targetSeasonIndex = Seasons.IndexOf(foundSeason);
                                episodeToSelect = foundSeason.Episodes.FirstOrDefault(e => e.Id == nextEp.Id);
                            }
                        }
                        else
                        {
                            var foundSeason = Seasons.FirstOrDefault(s => s.SeasonNumber == lastWatched.SeasonNumber);
                            if (foundSeason != null)
                            {
                                targetSeasonIndex = Seasons.IndexOf(foundSeason);
                                episodeToSelect = foundSeason.Episodes.FirstOrDefault(e => e.Id == lastWatched.Id);
                            }
                        }
                    }
                    else
                    {
                        var foundSeason = Seasons.FirstOrDefault(s => s.SeasonNumber == lastWatched.SeasonNumber);
                        if (foundSeason != null)
                        {
                            targetSeasonIndex = Seasons.IndexOf(foundSeason);
                            episodeToSelect = foundSeason.Episodes.FirstOrDefault(e => e.Id == lastWatched.Id);
                        }
                    }
                }

                if (episodeToSelect != null) _episodesManager?.SetPendingAutoSelectEpisode(episodeToSelect);

                SeasonComboBox.ItemsSource = Seasons;
                if (Seasons.Count > 0) SeasonComboBox.SelectedIndex = targetSeasonIndex;

                if (_item is Models.Stremio.StremioMediaStream stremioItem)
                {
                    await RefreshStremioSeriesProgressAsync(stremioItem);
                }
                else if (_item is SeriesStream iptvSeries)
                {
                    await RefreshIptvSeriesProgressAsync(iptvSeries);
                }

                if (unified.BackdropUrls != null && unified.BackdropUrls.Count > 0)
                {
                    _backgroundManager?.StartSlideshow(unified.BackdropUrls);
                }
                if (SourceAttributionText != null)
                {
                    SourceAttributionText.Text = unified.MetadataSourceInfo;
                }
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("LoadSeriesData Error", ex);
            }
            _isEpisodesLoading = false;
        }

        /// <summary>
        /// Checks if an episode title is generic or needs fallback names.
        /// </summary>
        private static bool IsGenericEpisodeTitle(string title, int episodeNumber)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;

            string t = title.Trim().ToLowerInvariant();
            if (t == episodeNumber.ToString()) return true;
            if (t == $"e{episodeNumber}" || t == $"ep {episodeNumber}" || t == $"ep. {episodeNumber}") return true;
            if (t.Contains("episode") || t.Contains("bölüm") || t.Contains("bolum")) return true;
            return false;
        }

        /// <summary>
        /// Refreshes Stremio series episode progress ticks.
        /// </summary>
        private async Task RefreshStremioSeriesProgressAsync(Models.Stremio.StremioMediaStream series)
        {
            await _actionHandlerManager?.RefreshStremioSeriesProgressAsync(series);
        }

        /// <summary>
        /// Refreshes IPTV series episode progress ticks.
        /// </summary>
        private async Task RefreshIptvSeriesProgressAsync(SeriesStream series)
        {
            await _actionHandlerManager?.RefreshIptvSeriesProgressAsync(series);
        }

        /// <summary>
        /// Refreshes watchlist and history badge visual overlays.
        /// </summary>
        private void RefreshHistoryVisibility()
        {
            _actionHandlerManager?.RefreshHistoryVisibility();
        }

        /// <summary>
        /// Refreshes history completion bars on movie action grids.
        /// </summary>
        private void UpdateMovieHistoryUi(HistoryItem history)
        {
            _actionHandlerManager?.UpdateMovieHistoryUi(history);
        }

        #endregion

        #region Stremio Addon Streams Orchestrator

        /// <summary>
        /// Coordinates Stremio addon scraper requests and updates XAML selector collections.
        /// </summary>
        internal async Task PlayStremioContent(string videoId, bool showGlobalLoading = true, bool autoPlay = false, double startSeconds = -1)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return;
            System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] PlayStremioContent START for {videoId}");
            _isSourcesFetchInProgress = true;

            string type = (_item as Models.Stremio.StremioMediaStream)?.Meta?.Type ?? "movie";

            string resolvedVideoId = videoId;
            if (!videoId.StartsWith("tt") && _unifiedMetadata != null)
            {
                if (type == "movie" && !string.IsNullOrEmpty(_unifiedMetadata.ImdbId) && _unifiedMetadata.ImdbId.StartsWith("tt"))
                {
                    resolvedVideoId = _unifiedMetadata.ImdbId;
                }
                else if (type == "series" && !string.IsNullOrEmpty(_unifiedMetadata.ImdbId) && _unifiedMetadata.ImdbId.StartsWith("tt"))
                {
                    var parts = videoId.Split(':');
                    if (parts.Length >= 3)
                    {
                        string season = parts[parts.Length - 2];
                        string episode = parts[parts.Length - 1];
                        resolvedVideoId = $"{_unifiedMetadata.ImdbId}:{season}:{episode}";
                    }
                }
            }
            
            if (resolvedVideoId != videoId) System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] Resolved ID: {videoId} -> {resolvedVideoId}");

            string currentItemId = _item is Models.Stremio.StremioMediaStream sms ? sms.Meta.Id : null;
            bool isSameItem = currentItemId != null && _currentStremioVideoId == resolvedVideoId;

            if (isSameItem)
            {
                bool hasVisibleSources = _addonResults != null &&
                                         _addonResults.Any(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0);

                if (hasVisibleSources)
                {
                    if (AddonSelectorList != null && AddonSelectorList.ItemsSource == null) AddonSelectorList.ItemsSource = _addonResults;
                    RefreshAllAddonActiveFlags();
                    SyncAddonSelectionToActive();
                    OpenSourcesPanel(PanelChangeReason.SourceCache);
                    _sourcesManager?.ScrollToActiveSource();
                    
                    _isSourcesFetchInProgress = !_isCurrentSourcesComplete; 
                    return;
                }
            }
            else
            {
                _addonResults?.Clear();
            }

            _sourcesCts?.Cancel();
            _sourcesCts?.Dispose();
            _sourcesCts = new CancellationTokenSource();
            var sourcesToken = _sourcesCts.Token;

            int requestVersion = Interlocked.Increment(ref _sourcesRequestVersion);
            try
            {
                if (showGlobalLoading) SetLoadingState(true);

                string cacheKey = $"{type}|{resolvedVideoId}";
                bool hasCachedAddons = false;
                Services.MediaInfo.StremioSourcesService.StremioSourcesCacheEntry cacheEntry = null;

                if (_stremioSourcesCache.TryGetValue(cacheKey, out cacheEntry) &&
                    cacheEntry?.Addons != null &&
                    cacheEntry.Addons.Count > 0)
                {
                    if (requestVersion != Volatile.Read(ref _sourcesRequestVersion)) return;

                    _currentStremioVideoId = resolvedVideoId;
                    _isCurrentSourcesComplete = cacheEntry.IsComplete;
                    _isSourcesFetchInProgress = !cacheEntry.IsComplete;
                    
                    foreach (var addon in cacheEntry.Addons)
                    {
                        _sourcesManager.AddOrUpdatePriorityAddon(CloneAddonViewModel(addon));
                    }
                    
                    var firstAddon = _addonResults.FirstOrDefault(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0);
                    if (firstAddon != null) OpenSourcesPanel(PanelChangeReason.SourceCache);
                    RefreshAllAddonActiveFlags();
                    SyncAddonSelectionToActive();
                    if (AddonSelectorList.SelectedItem == null) AddonSelectorList.SelectedItem = firstAddon;
                    if (AddonSelectorList.SelectedItem != null) OpenSourcesPanel(PanelChangeReason.SourceCache);

                    _sourcesManager?.ScrollToActiveSource();

                    if (autoPlay)
                    {
                        var firstStream = firstAddon?.Streams?.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));
                        if (firstStream != null)
                        {
                            SetLoadingState(false);
                            string parentIdStr = (_item is Models.Stremio.StremioMediaStream sMediaStream && (sMediaStream.Meta.Type == "series" || sMediaStream.Meta.Type == "tv")) ? sMediaStream.Meta.Id : null;
                            string autoStreamType = (_item is Models.Stremio.StremioMediaStream sMediaStream2 && (sMediaStream2.Meta.Type == "series" || sMediaStream2.Meta.Type == "tv")) ? "series" : "movie";
                            _sourceAddonUrl = firstStream.AddonUrl;
                            
                            string yearStr = _unifiedMetadata?.Year;
                            string ratingStr = _unifiedMetadata?.Rating.ToString("F1", CultureInfo.InvariantCulture);
                            string durationStr = _selectedEpisode?.Duration ?? _unifiedMetadata?.Runtime;
                            string overviewStr = _unifiedMetadata?.Overview;

                            Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(
                                firstStream.Url, _item.Title, resolvedVideoId, parentIdStr, null, 
                                _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, 
                                startSeconds, _item.PosterUrl, autoStreamType, _backgroundManager?.GetCurrentBackdrop() ?? string.Empty, 
                                GetLogoUrl(), _backgroundManager?.PrimaryColorHex ?? "#FF00BFA5", _sourceAddonUrl, yearStr, ratingStr, durationStr, overviewStr));
                            return;
                        }
                    }

                    if (cacheEntry.IsComplete)
                    {
                        if (showGlobalLoading) SetLoadingState(false);
                        return;
                    }
                }

                _currentStremioVideoId = resolvedVideoId;
                _isCurrentSourcesComplete = false;

                var addons = Services.Stremio.StremioAddonManager.Instance.GetAddons();

                if (!hasCachedAddons || _addonResults == null)
                {
                    _addonResults = new System.Collections.ObjectModel.ObservableCollection<StremioAddonViewModel>();
                    AddonSelectorList.ItemsSource = _addonResults;
                }
                var dispatcherQueue = this.DispatcherQueue;

                System.Diagnostics.Debug.WriteLine($"[Stremio] Fetching sources for {resolvedVideoId} (Original: {videoId}) ({type}) from {addons.Count} addons.");

                string lastStreamUrl = _streamUrl;
                if (string.IsNullOrEmpty(lastStreamUrl))
                {
                    lastStreamUrl = HistoryManager.Instance.GetProgress(resolvedVideoId)?.StreamUrl;
                }

                StremioAddonViewModel iptvAddonToSelect = null;
                
                var jitIptvAddon = await _stremioSourcesService.BuildIptvAddonAsync(_item, _unifiedMetadata, _selectedEpisode, lastStreamUrl);
                if (jitIptvAddon != null)
                {
                    _addonResults.Add(jitIptvAddon);
                    iptvAddonToSelect = jitIptvAddon;
                }
                _sourcesManager.InitializeLoading(addons, GetDynamicShimmerCount());

                if (iptvAddonToSelect != null) AddonSelectorList.SelectedItem = iptvAddonToSelect;
                else if (_addonResults.Count > 0) AddonSelectorList.SelectedIndex = 0;

                OpenSourcesPanel(PanelChangeReason.SourceFetch);

                await _stremioSourcesService.FetchSourcesAsync(
                    resolvedVideoId,
                    type,
                    lastStreamUrl,
                    onAddonFetched: (vm) =>
                    {
                        dispatcherQueue.TryEnqueue(() =>
                        {
                            _sourcesManager.AddOrUpdatePriorityAddon(vm);
                            RefreshAllAddonActiveFlags();
                            SyncAddonSelectionToActive();
                        });
                    },
                    onAddonFailed: (baseUrl) =>
                    {
                        dispatcherQueue.TryEnqueue(() =>
                        {
                            _sourcesManager.RemoveFailedAddon(baseUrl);
                        });
                    },
                    sourcesToken);

                if (!_addonResults.Any(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0))
                {
                    if (ResolveCurrentContentKind() == MediaContentKind.Series)
                    {
                        OpenEpisodesPanel(PanelChangeReason.NoSources);
                    }
                    else
                    {
                        CloseDetailPanel(PanelChangeReason.NoSources);
                    }
                    try
                    {
                        if (this.XamlRoot != null)
                        {
                            var err = new ContentDialog { 
                                Title = "Kaynak Bulunamadı", 
                                Content = "Eklentilerinizde bu içerik için uygun bir kaynak bulunamadı.", 
                                CloseButtonText = "Tamam", 
                                XamlRoot = this.XamlRoot 
                            };
                            await Services.DialogService.ShowAsync(err);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                if (requestVersion == Volatile.Read(ref _sourcesRequestVersion))
                {
                    if (showGlobalLoading) SetLoadingState(false);
                    _isSourcesFetchInProgress = false;
                    OnDataCommitted();
                }
                System.Diagnostics.Debug.WriteLine($"PlayStremio Error: {ex}");
            }
        }

        /// <summary>
        /// Deep-clones viewmodels for priority caching.
        /// </summary>
        private static StremioAddonViewModel CloneAddonViewModel(StremioAddonViewModel source)
        {
            return new StremioAddonViewModel
            {
                Name = source.Name,
                AddonUrl = source.AddonUrl,
                IsLoading = source.IsLoading,
                SortIndex = source.SortIndex,
                Streams = source.Streams?.Select(CloneStreamViewModel).ToList() ?? new List<StremioStreamViewModel>()
            };
        }

        /// <summary>
        /// Deep-clones viewmodels for priority caching.
        /// </summary>
        private static StremioStreamViewModel CloneStreamViewModel(StremioStreamViewModel source)
        {
            return new StremioStreamViewModel
            {
                Title = source.Title,
                Name = source.Name,
                ProviderText = source.ProviderText,
                AddonName = source.AddonName,
                Url = source.Url,
                Externalurl = source.Externalurl,
                Quality = source.Quality,
                Size = source.Size,
                IsCached = source.IsCached,
                OriginalStream = source.OriginalStream,
                IsActive = source.IsActive
            };
        }

        /// <summary>
        /// Refreshes the IsActive property of all loaded addon streams by comparing URLs and filenames.
        /// </summary>
        internal void RefreshAllAddonActiveFlags()
        {
            try
            {
                if (_addonResults == null) return;

                string activeUrl = _streamUrl;
                if (string.IsNullOrEmpty(activeUrl) && !string.IsNullOrEmpty(_currentStremioVideoId))
                {
                    activeUrl = HistoryManager.Instance.GetProgress(_currentStremioVideoId)?.StreamUrl;
                }

                if (string.IsNullOrEmpty(activeUrl)) return;

                string activeFileName = null;
                try { activeFileName = System.IO.Path.GetFileName(new Uri(activeUrl).LocalPath); } catch { }

                foreach (var addon in _addonResults)
                {
                    if (addon.Streams == null) continue;
                    foreach (var stream in addon.Streams)
                    {
                        string streamUrl = stream.Url ?? "";
                        string normActive = (activeUrl ?? "").Replace("iptv://", "").TrimEnd('/').ToLowerInvariant();
                        string normStream = (streamUrl ?? "").Replace("iptv://", "").TrimEnd('/').ToLowerInvariant();
                        bool isActive = !string.IsNullOrEmpty(normActive) && normStream == normActive;

                        string currentFileName = "";
                        try { currentFileName = System.IO.Path.GetFileName(new Uri(streamUrl).LocalPath); } catch { }
                        
                        if (!isActive && !string.IsNullOrEmpty(currentFileName) && !string.IsNullOrEmpty(activeFileName) && currentFileName == activeFileName) isActive = true;

                        if (!isActive && !string.IsNullOrEmpty(stream.Title) && !string.IsNullOrEmpty(activeFileName))
                        {
                            if (stream.Title.ToLowerInvariant().Contains(activeFileName.ToLowerInvariant()) || activeFileName.ToLowerInvariant().Contains(stream.Title.ToLowerInvariant()))
                            {
                                isActive = true;
                            }
                        }
                        
                        if (!isActive && !string.IsNullOrEmpty(streamUrl) && !string.IsNullOrEmpty(activeFileName) && streamUrl.Contains(activeFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            isActive = true;
                        }
                        stream.IsActive = isActive;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Stremio] RefreshAllAddonActiveFlags Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Synchronizes the selected addon in the UI list to the one containing the active active stream.
        /// </summary>
        internal void SyncAddonSelectionToActive()
        {
            try
            {
                if (_addonResults == null) return;
                var activeAddon = _addonResults.FirstOrDefault(a => !a.IsLoading && a.Streams != null && a.Streams.Any(s => s.IsActive));
                if (activeAddon != null)
                {
                    if (AddonSelectorList != null) 
                    {
                        AddonSelectorList.SelectedItem = activeAddon;
                        _sourcesManager?.ScrollToActiveSource();
                    }
                }
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[Stremio] SyncAddonSelectionToActive Error: {ex.Message}");
            }
        }

        #endregion
    }
}
