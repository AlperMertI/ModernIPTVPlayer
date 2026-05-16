using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Xaml;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage
    {
        #region Content Resolution

        private MediaContentKind ResolveCurrentContentKind()
        {
            if (_unifiedMetadata?.IsSeries == true) return MediaContentKind.Series;
            if (_item is LiveStream) return MediaContentKind.Live;
            if (_item != null && IsProbablySeriesItem(_item)) return MediaContentKind.Series;
            if (_item is Models.Stremio.StremioMediaStream sms)
            {
                string type = sms.Meta?.Type ?? _item.Type;
                if (string.Equals(type, "series", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase))
                {
                    return MediaContentKind.Series;
                }

                if (string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase))
                {
                    return MediaContentKind.Movie;
                }
            }

            if (_item == null) return MediaContentKind.Unknown;
            return MediaContentKind.Movie;
        }

        #endregion

        #region Panel Owner

        private PanelOwner _panelOwner;

        private void InitializePanelOwner()
        {
            if (_compositor == null || _sectionRegistry == null || _layoutApplier == null) return;

            _panelOwner?.Dispose();
            _panelOwner = new PanelOwner(
                _compositor,
                DispatcherQueue,
                _sectionRegistry,
                _layoutApplier,
                BuildLayoutInputs,
                OnPanelChanged,
                SourcesPanel,
                EpisodesPanel,
                SourcesPanelInnerContent,
                SourcesRepeater,
                EpisodesRepeater,
                SourcesShowHandle,
                BtnHideSources,
                BtnBackToEpisodes);
        }

        #endregion

        #region Panel Mode Management (delegated to PanelOwner)

        private MediaDetailPanelMode GetDefaultPanelModeForItem(IMediaStream item)
        {
            if (item == null) return MediaDetailPanelMode.None;
            if (item is LiveStream) return MediaDetailPanelMode.None;
            if (IsProbablySeriesItem(item)) return MediaDetailPanelMode.Episodes;
            return MediaDetailPanelMode.Sources;
        }

        private void ApplyDefaultPanelModeForItem(IMediaStream item, PanelChangeReason reason)
        {
            var defaultMode = GetDefaultPanelModeForItem(item);
            if (defaultMode == MediaDetailPanelMode.Episodes)
            {
                OpenEpisodesPanel(reason);
            }
            else if (defaultMode == MediaDetailPanelMode.Sources)
            {
                OpenSourcesPanel(reason);
            }
        }

        private void SetPanelMode(MediaDetailPanelMode mode, PanelChangeReason reason)
        {
            _panelOwner?.SetContentKind(ResolveCurrentContentKind());
        }

        public void OpenSourcesPanel(PanelChangeReason reason)
        {
            _sourcesPanelOpenedTime = DateTime.Now;
            _sourcesPresentationTime = DateTime.Now;
            _sourcesRevealItemLimit = Math.Min(30, _visibleSourceStreams.Count);

            _panelOwner?.SetContentKind(ResolveCurrentContentKind());
            _panelOwner?.OpenSourcesPanel(reason);

            UpdateInfoPanelVisibility(IsSeriesItem());

            if (reason == PanelChangeReason.SourceCache || reason == PanelChangeReason.NavigationDefault)
            {
                if (_visibleSourceStreams.Count > 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _sourcesVisualGeneration++;
                        _animatedSourceRevealIndexes.Clear();
                        _sourcesRevealItemLimit = _visibleSourceStreams.Count;

                        if (SourcesRepeater != null)
                        {
                            SourcesRepeater.ItemsSource = null;
                            SourcesRepeater.ItemsSource = _visibleSourceStreams;
                        }
                    });
                }
            }
        }

        public void OpenEpisodesPanel(PanelChangeReason reason)
        {
            Debug.WriteLine($"[INFO-STATE] OpenEpisodesPanel called. Reason={reason}");

            _panelOwner?.SetContentKind(ResolveCurrentContentKind());
            _panelOwner?.OpenEpisodesPanel(reason);

            UpdateInfoPanelVisibility(false);

            if (reason == PanelChangeReason.BackToEpisodes || reason == PanelChangeReason.SeriesDefaultEpisodes || reason == PanelChangeReason.EpisodeRequired)
            {
                _sourcesPresentationTime = DateTime.Now;
                _sourcesRevealItemLimit = Math.Min(30, CurrentEpisodes.Count);

                DispatcherQueue.TryEnqueue(() =>
                {
                    _sourcesVisualGeneration++;
                    _animatedSourceRevealIndexes.Clear();

                    if (EpisodesRepeater != null)
                    {
                        EpisodesRepeater.ItemsSource = null;
                        EpisodesRepeater.ItemsSource = CurrentEpisodes;
                    }
                });
            }
        }

        private void CloseDetailPanel(PanelChangeReason reason)
        {
            Interlocked.Increment(ref _sourcesRequestVersion);
            _isSourcesFetchInProgress = false;
            _panelOwner?.SetContentKind(ResolveCurrentContentKind());
            _panelOwner?.CloseDetailPanel(reason);
        }

        private void LogPanelState(PanelChangeReason reason, MediaDetailPanelMode panelMode, MediaContentKind contentKind, string action)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[INFO-STATE] {action} | Reason: {reason}, Mode: {panelMode}, Kind: {contentKind}, State: {_pageLoadState}");
        }

        #endregion

        #region Section Architecture

        private SectionRegistry _sectionRegistry;
        private LayoutApplier _layoutApplier;
        private bool _layoutUpdatePending;
        private MediaDetailPanelMode? _pendingPanelRequest;
        private PanelChangeReason? _pendingPanelReason;

        private void InitializeSectionArchitecture()
        {
            try
            {
                if (_compositor == null)
                {
                    System.Diagnostics.Debug.WriteLine("[SECTION-ARCH] Compositor not available, deferring initialization");
                    return;
                }

                var elements = new LayoutElements
                {
                    Col0 = Col0,
                    Col1 = Col1,
                    Row0 = Row0,
                    Row1 = Row1,
                    Row2 = Row2,
                    Row3 = Row3,
                    Row4 = Row4,
                    ContentGrid = ContentGrid,
                    RootScrollViewer = RootScrollViewer,
                    InfoContainer = InfoContainer,
                    SourcesPanel = SourcesPanel,
                    EpisodesPanel = EpisodesPanel,
                    NarrowSectionsContainer = NarrowSectionsContainer,
                    OverviewText = OverviewText,
                    GenresText = GenresText,
                    InfoColumn = InfoColumn,
                    IdentityControl = IdentityControl,
                    MetadataRibbon = MetadataRibbon,
                    ActionBarGroup = ActionBarGroup,
                    ActionBarPanel = ActionBarPanel,
                    InfoContainerInner = InfoContainerInner,
                    AdaptiveInfoHost = AdaptiveInfoHost,
                    CastSection = CastSection,
                    CastShimmer = CastShimmer,
                    DirectorSection = DirectorSection,
                    DirectorShimmer = DirectorShimmer,
                    BtnHideSources = BtnHideSources,
                    BtnBackToEpisodes = BtnBackToEpisodes,
                    SourcesShowHandle = SourcesShowHandle,
                    MetadataPanel = MetadataPanel,
                    OverviewPanel = OverviewPanel,
                };

                _sectionRegistry = new SectionRegistry();
                _sectionRegistry.SetDispatcher(DispatcherQueue);

                _sectionRegistry.Register("identity", InfoContainer, IdentityControl, null, _compositor, 0);
                _sectionRegistry.Register("metadata", InfoContainer, MetadataPanel, MetadataShimmer, _compositor, 1);
                _sectionRegistry.Register("actionbar", InfoContainer, ActionBarPanel, ActionBarShimmer, _compositor, 2);
                _sectionRegistry.Register("overview", InfoContainer, OverviewPanel, OverviewShimmer, _compositor, 3);
                _sectionRegistry.Register("director", InfoContainer, DirectorSection, DirectorShimmer, _compositor, 4);
                _sectionRegistry.Register("cast", InfoContainer, CastSection, CastShimmer, _compositor, 5);
                _sectionRegistry.Register("sources", SourcesPanel, SourcesRepeater, null, _compositor, 6);
                _sectionRegistry.Register("episodes", EpisodesPanel, EpisodesRepeater, null, _compositor, 7);

                _layoutApplier = new LayoutApplier(elements);

                InitializePanelOwner();

                System.Diagnostics.Debug.WriteLine("[SECTION-ARCH] Section architecture initialized");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SECTION-ARCH] Initialization failed: {ex.Message}");
            }
        }

        #endregion

        #region Layout Entry Points

        private void OnViewportChanged()
        {
            if (_pageCts?.IsCancellationRequested == true) return;
            if (LayoutRoot == null || ContentGrid == null) return;

            try
            {
                var inputs = BuildLayoutInputs();
                var decision = LayoutEngine.Compute(inputs);
                _layoutApplier?.Apply(decision);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LAYOUT] OnViewportChanged failed: {ex.Message}");
            }
        }

        private void OnPanelChanged()
        {
            if (_pageCts?.IsCancellationRequested == true) return;
            if (LayoutRoot == null || ContentGrid == null) return;

            try
            {
                var inputs = BuildLayoutInputs();
                var decision = LayoutEngine.Compute(inputs);
                ApplyLayoutDecision(decision);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LAYOUT] OnPanelChanged failed: {ex.Message}");
            }
        }

        private void ApplyLayoutDecision(LayoutDecision decision)
        {
            _layoutApplier?.Apply(decision);
        }

        private void OnDataCommitted()
        {
            if (_pageCts?.IsCancellationRequested == true) return;
            if (LayoutRoot == null || ContentGrid == null) return;

            try
            {
                var inputs = BuildLayoutInputs();
                var decision = LayoutEngine.Compute(inputs);
                ApplyLayoutDecision(decision);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LAYOUT] OnDataCommitted failed: {ex.Message}");
            }
        }

        private void OnIdentityChanged()
        {
            if (_pageCts?.IsCancellationRequested == true) return;
            if (LayoutRoot == null || ContentGrid == null) return;

            try
            {
                var inputs = BuildLayoutInputs();
                var decision = LayoutEngine.Compute(inputs);
                ApplyLayoutDecision(decision);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LAYOUT] OnIdentityChanged failed: {ex.Message}");
            }
        }

        private LayoutInputs BuildLayoutInputs()
        {
            var contentKind = ResolveCurrentContentKind();
            var panelMode = _panelOwner != null ? _panelOwner.PanelMode : MediaDetailPanelMode.None;

            return new LayoutInputs(
                ViewportWidth: ActualWidth > 0 ? ActualWidth : _lastReportedWidth,
                ViewportHeight: GetViewportHeight(),
                LoadState: _pageLoadState,
                PanelMode: panelMode,
                ContentKind: contentKind,
                IsSourcesPanelHidden: _panelOwner != null ? _panelOwner.IsSourcesPanelHidden : false,
                IsSourcesFetchInProgress: _isSourcesFetchInProgress,
                CastCount: CastList?.Count ?? 0,
                DirectorCount: DirectorList?.Count ?? 0,
                HasEpisodes: (Seasons?.Count > 0 && CurrentEpisodes?.Count > 0),
                HasMetadata: _unifiedMetadata != null,
                HasSelectedEpisode: _selectedEpisode != null);
        }

        private double GetViewportHeight()
        {
            double viewportHeight = ActualHeight > 0 ? ActualHeight : _lastReportedHeight;
            return viewportHeight > 0 ? viewportHeight : 720;
        }

        #endregion

        #region Section Notifications

        void IMediaInfoUIProxy.NotifySectionDataReady(string sectionName)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var section = _sectionRegistry?.Get(sectionName);
                    if (section != null)
                    {
                        section.RevealContent();
                        System.Diagnostics.Debug.WriteLine($"[SECTION] NotifySectionDataReady: {sectionName}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SECTION] NotifySectionDataReady failed for '{sectionName}': {ex.Message}");
                }
            });
        }

        void IMediaInfoUIProxy.RequestPanelOpen(MediaDetailPanelMode mode, PanelChangeReason reason)
        {
            if (_pageLoadState == PageLoadState.Ready || _pageLoadState == PageLoadState.LayoutReady)
            {
                if (mode == MediaDetailPanelMode.Sources)
                {
                    OpenSourcesPanel(reason);
                }
                else if (mode == MediaDetailPanelMode.Episodes)
                {
                    OpenEpisodesPanel(reason);
                }
            }
            else
            {
                _pendingPanelRequest = mode;
                _pendingPanelReason = reason;
                System.Diagnostics.Debug.WriteLine($"[SECTION] Panel request deferred: {mode} (state: {_pageLoadState})");
            }
        }

        private void FlushDeferredPanelRequest()
        {
            if (_pendingPanelRequest.HasValue)
            {
                var mode = _pendingPanelRequest.Value;
                var reason = _pendingPanelReason ?? PanelChangeReason.NavigationDefault;
                _pendingPanelRequest = null;
                _pendingPanelReason = null;

                System.Diagnostics.Debug.WriteLine($"[SECTION] Flushing deferred panel request: {mode}");

                if (mode == MediaDetailPanelMode.Sources)
                {
                    OpenSourcesPanel(reason);
                }
                else if (mode == MediaDetailPanelMode.Episodes)
                {
                    OpenEpisodesPanel(reason);
                }
            }
            else
            {
                ApplyDefaultPanelModeForItem(_item, PanelChangeReason.NavigationDefault);
            }
        }

        #endregion
    }
}
