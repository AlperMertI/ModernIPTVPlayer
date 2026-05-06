using System;
using System.Threading;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Iptv;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage
    {
        /// <summary>
        /// The logical side panel requested by page state. Rendering and animation should derive from this,
        /// so movie sources, series episodes, and source drill-in behavior do not fight through loose booleans.
        /// </summary>
        private enum MediaDetailPanelMode
        {
            None,
            Episodes,
            Sources
        }

        private enum MediaContentKind
        {
            Unknown,
            Movie,
            Series,
            Live
        }

        private enum PanelChangeReason
        {
            Reset,
            NavigationDefault,
            MovieAutoSources,
            SeriesDefaultEpisodes,
            EpisodeSelected,
            EpisodeDeselected,
            SourcesRequested,
            SourcesClosed,
            BackToEpisodes,
            SourceFetch,
            SourceCache,
            NoSources
        }

        /// <summary>
        /// Immutable input for layout application. It captures what the UI should show before any controls
        /// are mutated, keeping panel decisions auditable and easier to debug.
        /// </summary>
        private readonly struct MediaPanelLayoutSnapshot
        {
            public MediaPanelLayoutSnapshot(
                bool isWide,
                MediaContentKind contentKind,
                MediaDetailPanelMode panelMode,
                bool hasMetadata,
                bool isLoading,
                bool isRevealing,
                bool isReady,
                bool hasSelectedEpisode,
                bool isSourcesFetchInProgress,
                bool isSourcesPanelHidden)
            {
                IsWide = isWide;
                ContentKind = contentKind;
                PanelMode = panelMode;
                HasMetadata = hasMetadata;
                IsLoading = isLoading;
                IsRevealing = isRevealing;
                IsReady = isReady;
                HasSelectedEpisode = hasSelectedEpisode;
                IsSourcesFetchInProgress = isSourcesFetchInProgress;
                IsSourcesPanelHidden = isSourcesPanelHidden;
            }

            public bool IsWide { get; }
            public MediaContentKind ContentKind { get; }
            public MediaDetailPanelMode PanelMode { get; }
            public bool HasMetadata { get; }
            public bool IsLoading { get; }
            public bool IsRevealing { get; }
            public bool IsReady { get; }
            public bool HasSelectedEpisode { get; }
            public bool IsSourcesFetchInProgress { get; }
            public bool IsSourcesPanelHidden { get; }
            public bool ShowSourcesPanel => PanelMode == MediaDetailPanelMode.Sources;
            public bool ShowEpisodesPanel => PanelMode == MediaDetailPanelMode.Episodes;
        }

        private MediaPanelLayoutSnapshot BuildPanelLayoutSnapshot()
        {
            var contentKind = ResolveCurrentContentKind();
            var panelMode = NormalizePanelMode(_requestedPanelMode, contentKind);

            return new MediaPanelLayoutSnapshot(
                ActualWidth >= LayoutAdaptiveThreshold,
                contentKind,
                panelMode,
                _unifiedMetadata != null,
                _pageLoadState == PageLoadState.Loading,
                _pageLoadState == PageLoadState.Revealing,
                _pageLoadState == PageLoadState.Ready,
                _selectedEpisode != null,
                _isSourcesFetchInProgress,
                _isSourcesPanelHidden);
        }

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

        private static MediaDetailPanelMode NormalizePanelMode(MediaDetailPanelMode requestedMode, MediaContentKind contentKind)
        {
            if (contentKind == MediaContentKind.Live) return MediaDetailPanelMode.None;
            if (requestedMode == MediaDetailPanelMode.Episodes && contentKind != MediaContentKind.Series)
            {
                return MediaDetailPanelMode.None;
            }

            if (requestedMode == MediaDetailPanelMode.None && contentKind == MediaContentKind.Series)
            {
                return MediaDetailPanelMode.Episodes;
            }

            return requestedMode;
        }

        private MediaDetailPanelMode GetDefaultPanelModeForItem(IMediaStream item)
        {
            if (item == null) return MediaDetailPanelMode.None;
            if (item is LiveStream) return MediaDetailPanelMode.None;
            if (IsProbablySeriesItem(item)) return MediaDetailPanelMode.Episodes;
            return MediaDetailPanelMode.Sources;
        }

        private void ApplyDefaultPanelModeForItem(IMediaStream item, PanelChangeReason reason)
        {
            SetPanelMode(GetDefaultPanelModeForItem(item), reason);
        }

        private void SetPanelMode(MediaDetailPanelMode mode, PanelChangeReason reason)
        {
            var contentKind = ResolveCurrentContentKind();
            var normalizedMode = NormalizePanelMode(mode, contentKind);
            if (_requestedPanelMode == normalizedMode)
            {
                LogPanelState(reason, normalizedMode, contentKind, "unchanged");
                return;
            }

            _requestedPanelMode = normalizedMode;
            if (normalizedMode != MediaDetailPanelMode.Sources)
            {
                _isSourcesPanelHidden = false;
                if (SourcesShowHandle != null) SourcesShowHandle.Visibility = Visibility.Collapsed;
            }

            LogPanelState(reason, normalizedMode, contentKind, "changed");
            SyncLayout();
        }

        private void OpenSourcesPanel(PanelChangeReason reason)
        {
            _isSourcesPanelHidden = false;
            if (SourcesShowHandle != null) SourcesShowHandle.Visibility = Visibility.Collapsed;
            SetPanelMode(MediaDetailPanelMode.Sources, reason);
            if (BtnBackToEpisodes != null) BtnBackToEpisodes.Visibility = IsSeriesItem() ? Visibility.Visible : Visibility.Collapsed;
            UpdateInfoPanelVisibility(IsSeriesItem());
        }

        private void OpenEpisodesPanel(PanelChangeReason reason)
        {
            SetPanelMode(MediaDetailPanelMode.Episodes, reason);
            if (BtnBackToEpisodes != null) BtnBackToEpisodes.Visibility = Visibility.Collapsed;
            UpdateInfoPanelVisibility(false);
        }

        private void CloseDetailPanel(PanelChangeReason reason)
        {
            Interlocked.Increment(ref _sourcesRequestVersion);
            _isSourcesFetchInProgress = false;
            SetPanelMode(MediaDetailPanelMode.None, reason);
            if (BtnBackToEpisodes != null) BtnBackToEpisodes.Visibility = Visibility.Collapsed;
            UpdateInfoPanelVisibility(false);
        }

        private void LogPanelState(PanelChangeReason reason, MediaDetailPanelMode panelMode, MediaContentKind contentKind, string action)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[INFO-STATE] {action} | Reason: {reason}, Mode: {panelMode}, Kind: {contentKind}, State: {_pageLoadState}");
        }

        private void SyncLayout()
        {
            if (LayoutRoot == null || ContentGrid == null) return;

            var panelState = BuildPanelLayoutSnapshot();
            LogPanelSnapshotIfChanged(panelState);
            ApplyResponsiveContentGrid(panelState);
            ApplyContentVisualState(panelState);
            ApplyPanelLayoutState(panelState);
            ApplyInfoPriorityLayout(panelState.IsWide);
            ApplyMetadataVisibility(panelState);
            ApplyAtomicVisibility(panelState);
            ApplyDetailPanels(panelState);
        }

        private void LogPanelSnapshotIfChanged(MediaPanelLayoutSnapshot panelState)
        {
            // [SUPPRESSION] Only log if the actual visual structure (Mode, Wide, Panels) changes. 
            // We ignore _pageLoadState here to prevent "sync spam" during the 100ms loading sequence.
            string panelSyncSignature = $"{panelState.ContentKind}|{panelState.PanelMode}|{panelState.IsWide}|{panelState.ShowEpisodesPanel}|{panelState.ShowSourcesPanel}|{panelState.HasSelectedEpisode}|{panelState.IsSourcesFetchInProgress}|{panelState.IsSourcesPanelHidden}";
            if (_lastPanelSyncLogSignature == panelSyncSignature) return;

            _lastPanelSyncLogSignature = panelSyncSignature;
            System.Diagnostics.Debug.WriteLine(
                $"[INFO-SYNC] Layout Synced | Mode: {panelState.PanelMode}, Kind: {panelState.ContentKind}, Wide: {panelState.IsWide}");
        }

        private void ApplyContentVisualState(MediaPanelLayoutSnapshot panelState)
        {
            int layoutIndex = panelState.IsWide ? 1 : 0;
            if (_isWideModeIndex != layoutIndex)
            {
                _isWideModeIndex = layoutIndex;
                VisualStateManager.GoToState(this, panelState.IsWide ? "WideState" : "NarrowState", true);
            }

            string contentState = (panelState.IsReady || panelState.IsRevealing) ? "ReadyState" : "LoadingState";
            if (_currentContentStateName != contentState)
            {
                _currentContentStateName = contentState;
                VisualStateManager.GoToState(this, contentState, true);
            }
        }

        private void ApplyResponsiveContentGrid(MediaPanelLayoutSnapshot panelState)
        {
            if (panelState.IsWide)
            {
                bool showSidebar = panelState.ShowSourcesPanel || panelState.ShowEpisodesPanel;
                Grid.SetRow(InfoContainer, 0);
                Grid.SetColumn(InfoContainer, 0);
                Grid.SetColumnSpan(InfoContainer, 1);
                ContentGrid.Padding = new Thickness(60, 40, 20, 40);
                ContentGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                ContentGrid.ColumnDefinitions[1].MinWidth = showSidebar ? (panelState.ShowSourcesPanel ? WideSourcesColumnMinWidth : WideEpisodesColumnWidth) : 0;
                ContentGrid.ColumnDefinitions[1].MaxWidth = showSidebar ? (panelState.ShowSourcesPanel ? WideSourcesColumnMaxWidth : WideEpisodesColumnWidth) : double.PositiveInfinity;
                ContentGrid.ColumnDefinitions[1].Width = showSidebar
                    ? (panelState.ShowSourcesPanel ? new GridLength(0.42, GridUnitType.Star) : new GridLength(WideEpisodesColumnWidth))
                    : new GridLength(0);

                if (Row0 != null) Row0.Height = new GridLength(1, GridUnitType.Star);
                if (Row1 != null) Row1.Height = new GridLength(0);
                if (Row2 != null) Row2.Height = new GridLength(0);
                if (ContentGrid.RowDefinitions.Count > 3) ContentGrid.RowDefinitions[3].Height = new GridLength(0);

                if (RootScrollViewer != null)
                {
                    RootScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    RootScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
                }

                if (InfoContainerInner != null) InfoContainerInner.VerticalAlignment = VerticalAlignment.Stretch;
                if (InfoContainerInner != null) InfoContainerInner.HorizontalAlignment = HorizontalAlignment.Left;
                if (AdaptiveInfoHost != null)
                {
                    AdaptiveInfoHost.Width = double.NaN;
                    AdaptiveInfoHost.VerticalAlignment = VerticalAlignment.Bottom;
                    AdaptiveInfoHost.HorizontalAlignment = HorizontalAlignment.Left;
                }
            }
            else
            {
                Grid.SetRow(InfoContainer, 0);
                Grid.SetColumn(InfoContainer, 0);
                Grid.SetColumnSpan(InfoContainer, 2);
                ContentGrid.Padding = new Thickness(20, 60, 20, 40);
                if (AdaptiveInfoHost != null)
                {
                    AdaptiveInfoHost.Width = double.NaN;
                    AdaptiveInfoHost.VerticalAlignment = VerticalAlignment.Top;
                    AdaptiveInfoHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                }
                ContentGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                ContentGrid.ColumnDefinitions[1].MinWidth = 0;
                ContentGrid.ColumnDefinitions[1].MaxWidth = double.PositiveInfinity;
                ContentGrid.ColumnDefinitions[1].Width = new GridLength(0);

                if (Row0 != null) Row0.Height = new GridLength(1, GridUnitType.Auto);
                if (Row1 != null) Row1.Height = new GridLength(1, GridUnitType.Auto);
                if (Row2 != null) Row2.Height = new GridLength(1, GridUnitType.Auto);
                if (ContentGrid.RowDefinitions.Count > 3) ContentGrid.RowDefinitions[3].Height = new GridLength(0);

                if (RootScrollViewer != null)
                {
                    RootScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    RootScrollViewer.VerticalScrollMode = ScrollMode.Auto;
                }

                if (InfoContainerInner != null)
                {
                    InfoContainerInner.VerticalAlignment = VerticalAlignment.Top;
                    InfoContainerInner.HorizontalAlignment = HorizontalAlignment.Stretch;
                }
            }
        }

        private void ApplyMetadataVisibility(MediaPanelLayoutSnapshot panelState)
        {
            bool shouldShowMetadata = panelState.HasMetadata || panelState.IsReady || panelState.IsRevealing;

            if (ContentLogoHost != null)
            {
                bool hasLogo = !string.IsNullOrWhiteSpace(_currentLogoUrl);
                bool isTimedOut = (DateTime.Now - _navigationStartTime).TotalMilliseconds > IdentityGateTimeoutMs;
                bool isWaitingForLogoDecision = _isLogoPending && !_isLogoFallbackActive && !isTimedOut;
                bool showLogo = shouldShowMetadata && hasLogo && _isLogoReady && !_isLogoFallbackActive;
                bool showEpisodeTitleUnderLogo = _selectedEpisode != null;

                ContentLogoHost.Visibility = showLogo ? Visibility.Visible : Visibility.Collapsed;
                ContentLogoHost.Margin = new Thickness(0);
                ContentLogoHost.Opacity = 1;
                var logoHostVisual = ElementCompositionPreview.GetElementVisual(ContentLogoHost);
                logoHostVisual.StopAnimation(nameof(Visual.Opacity));
                logoHostVisual.Opacity = 1f;
                if (TitleText != null)
                {
                    bool showTitleFallback = !hasLogo || _isLogoFallbackActive || (hasLogo && !_isLogoReady && isTimedOut);
                    bool showTitle = shouldShowMetadata && !isWaitingForLogoDecision && (showTitleFallback || showEpisodeTitleUnderLogo);
                    TitleText.Visibility = showTitle ? Visibility.Visible : Visibility.Collapsed;
                    if (!showTitle)
                    {
                        TitleText.Opacity = 0;
                        var titleVisual = ElementCompositionPreview.GetElementVisual(TitleText);
                        titleVisual.StopAnimation(nameof(Visual.Opacity));
                        titleVisual.Opacity = 0f;
                    }
                    else
                    {
                        TitleText.Opacity = 1;
                        ElementCompositionPreview.GetElementVisual(TitleText).Opacity = 1f;
                    }
                }
            }

            if (MetadataPanel != null) MetadataPanel.Visibility = shouldShowMetadata ? Visibility.Visible : Visibility.Collapsed;
            if (OverviewPanel != null) OverviewPanel.Visibility = shouldShowMetadata ? Visibility.Visible : Visibility.Collapsed;
            if (ActionBarPanel != null) ActionBarPanel.Visibility = shouldShowMetadata ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyAtomicVisibility(MediaPanelLayoutSnapshot panelState)
        {
            if (ActualWidth <= 0) return;

            // 1. Identity (InfoContainer)
            _infoPanelAnimator?.ApplyVisible(panelState.IsLoading || panelState.IsRevealing || panelState.IsReady, isHorizontalReveal: false);

            // 2. People Sections (Cast/Director)
            // Rule: Reveal as soon as we have data. Reveal Sync (1-frame delay) in PopulateCastAndDirectors ensures we don't reveal 'blank'.
            bool canShowPeople = panelState.IsWide && (panelState.IsLoading || panelState.IsRevealing || panelState.IsReady);
            bool hasCast = CastList?.Count > 0 || panelState.IsLoading;
            bool hasDirector = DirectorList?.Count > 0 || panelState.IsLoading;

            _castPanelAnimator?.ApplyVisible(canShowPeople && hasCast, isHorizontalReveal: false);
            _directorPanelAnimator?.ApplyVisible(canShowPeople && hasDirector, isHorizontalReveal: false);

            // 3. Episodes
            // Rule: Only reveal if we have at least one season and episodes are ready. 
            // Prevents race conditions where the panel slides in empty before selection-changed fires.
            bool hasEpisodes = (Seasons?.Count > 0 && CurrentEpisodes?.Count > 0) || 
                               (panelState.IsLoading && panelState.ContentKind == MediaContentKind.Series);
            _episodesPanelAnimator?.ApplyVisible(panelState.ShowEpisodesPanel && hasEpisodes, isHorizontalReveal: true);

            // 4. Sources
            _sourcesPanelAnimator?.ApplyVisible(panelState.ShowSourcesPanel, isHorizontalReveal: false);
        }

        private void ApplyPeopleVisibility(MediaPanelLayoutSnapshot panelState)
        {
            // Legacy method kept for signature but logic moved to ApplyAtomicVisibility
        }

        private void ApplyDetailPanels(MediaPanelLayoutSnapshot panelState)
        {
            // Animation logic moved to ApplyAtomicVisibility
            RefreshAllShimmers();

            if (EpisodesRepeater != null) EpisodesRepeater.Visibility = panelState.ShowEpisodesPanel ? Visibility.Visible : Visibility.Collapsed;

            if (NarrowSectionsContainer != null)
                NarrowSectionsContainer.Visibility = Visibility.Collapsed;
        }

        private void ApplyPanelLayoutState(MediaPanelLayoutSnapshot panelState)
        {
            bool modeChanged = _lastIsWideForPanels != panelState.IsWide;
            _lastIsWideForPanels = panelState.IsWide;

            if (modeChanged)
            {
                _infoPanelAnimator?.MorphIfNeeded(LayoutRoot);
                _sourcesPanelAnimator?.MorphIfNeeded(LayoutRoot);
                _episodesPanelAnimator?.MorphIfNeeded(LayoutRoot);
            }
            if (SourcesPanel != null)
            {
                Grid.SetRow(SourcesPanel, panelState.IsWide ? 0 : 1);
                Grid.SetColumn(SourcesPanel, panelState.IsWide ? 1 : 0);
                Grid.SetColumnSpan(SourcesPanel, panelState.IsWide ? 1 : 2);
                SourcesPanel.VerticalAlignment = panelState.IsWide ? VerticalAlignment.Stretch : VerticalAlignment.Top;
                SourcesPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                SourcesPanel.Width = double.NaN;
                SourcesPanel.MaxWidth = panelState.IsWide ? 520 : double.PositiveInfinity;
                SourcesPanel.Margin = panelState.IsWide ? new Thickness(24, 40, 0, 40) : new Thickness(0, 20, 0, 0);
            }

            if (EpisodesPanel != null)
            {
                Grid.SetRow(EpisodesPanel, panelState.IsWide ? 0 : 2);
                Grid.SetColumn(EpisodesPanel, panelState.IsWide ? 1 : 0);
                Grid.SetColumnSpan(EpisodesPanel, panelState.IsWide ? 1 : 2);
                EpisodesPanel.VerticalAlignment = panelState.IsWide ? VerticalAlignment.Center : VerticalAlignment.Top;
                EpisodesPanel.HorizontalAlignment = panelState.IsWide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
                EpisodesPanel.Width = panelState.IsWide ? 400 : double.NaN;
                EpisodesPanel.MaxWidth = panelState.IsWide ? 400 : double.PositiveInfinity;
                EpisodesPanel.Margin = panelState.IsWide ? new Thickness(24, 40, 0, 40) : new Thickness(0, 20, 0, 0);
            }

            if (BtnHideSources != null)
            {
                BtnHideSources.Visibility = (panelState.IsWide && panelState.ShowSourcesPanel) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (BtnBackToEpisodes != null)
            {
                BtnBackToEpisodes.Visibility = (panelState.ShowSourcesPanel && panelState.ContentKind == MediaContentKind.Series)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (panelState.IsWide)
            {
                _sourcesPanelAnimator?.MorphIfNeeded(LayoutRoot);
                _episodesPanelAnimator?.MorphIfNeeded(LayoutRoot);
            }
        }

        private double GetInfoPanelWidth()
        {
            double viewportWidth = RootGrid?.ActualWidth ?? 0;
            if (viewportWidth <= 0) viewportWidth = _lastReportedWidth;
            if (viewportWidth <= 0) return 800;

            if (viewportWidth >= LayoutAdaptiveThreshold)
            {
                double sideWidth = ContentGrid?.ColumnDefinitions.Count > 1
                    ? ContentGrid.ColumnDefinitions[1].ActualWidth
                    : 0;

                if (sideWidth <= 0)
                {
                    sideWidth = WideEpisodesColumnWidth;
                }

                return Math.Max(360, viewportWidth - sideWidth - 96);
            }

            return Math.Max(320, viewportWidth - 40);
        }

        private double GetViewportHeight()
        {
            double viewportHeight = ActualHeight > 0 ? ActualHeight : _lastReportedHeight;
            return viewportHeight > 0 ? viewportHeight : 720;
        }
    }
}
