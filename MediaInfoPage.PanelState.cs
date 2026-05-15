using System;
using System.Threading;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage
    {


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
                bool isSourcesPanelHidden,
                double viewportHeight)
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
                ViewportHeight = viewportHeight;
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
            public double ViewportHeight { get; }
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
                _isSourcesPanelHidden,
                GetViewportHeight());
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

        public void OpenSourcesPanel(PanelChangeReason reason)
        {
            _isSourcesPanelHidden = false;
            if (SourcesShowHandle != null) SourcesShowHandle.Visibility = Visibility.Collapsed;
            SetPanelMode(MediaDetailPanelMode.Sources, reason);
            if (BtnBackToEpisodes != null) BtnBackToEpisodes.Visibility = IsSeriesItem() ? Visibility.Visible : Visibility.Collapsed;
            UpdateInfoPanelVisibility(IsSeriesItem());

            // [UX] If we are opening the panel with existing/cached content, force a re-reveal
            // so the user gets the staggered animation experience even if data was already in memory.
            if (reason == PanelChangeReason.SourceCache || reason == PanelChangeReason.NavigationDefault)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _sourcesVisualGeneration++;
                    _animatedSourceRevealIndexes.Clear();
                    _sourcesRevealItemLimit = _visibleSourceStreams.Count;

                    // Force full recycle: ElementClearing resets state, ElementPrepared hides
                    // and animates each item. This is reliable for ALL items, not just realized ones.
                    if (SourcesRepeater != null)
                    {
                        var src = _visibleSourceStreams;
                        SourcesRepeater.ItemsSource = null;
                        SourcesRepeater.ItemsSource = src;
                    }
                });
            }
        }

        public void OpenEpisodesPanel(PanelChangeReason reason)
        {
            SetPanelMode(MediaDetailPanelMode.Episodes, reason);
            if (BtnBackToEpisodes != null) BtnBackToEpisodes.Visibility = Visibility.Collapsed;
            UpdateInfoPanelVisibility(false);

            // [UX] Staggered reveal for episodes when returning to the list
            if (reason == PanelChangeReason.BackToEpisodes || reason == PanelChangeReason.SeriesDefaultEpisodes)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _sourcesVisualGeneration++;
                    _animatedSourceRevealIndexes.Clear();

                    if (EpisodesRepeater != null)
                    {
                        var src = CurrentEpisodes;
                        EpisodesRepeater.ItemsSource = null;
                        EpisodesRepeater.ItemsSource = src;
                    }
                });
            }
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
            if (!this.IsLoaded || _pageCts?.IsCancellationRequested == true) return;
            if (LayoutRoot == null || ContentGrid == null) return;

            var panelState = BuildPanelLayoutSnapshot();
            LogPanelSnapshotIfChanged(panelState);
            ApplyResponsiveContentGrid(panelState);
            ApplyVisualProperties(panelState);
            ApplyPanelLayoutState(panelState);
            ApplyInfoPriorityLayout(panelState.IsWide);
            ApplyMetadataVisibility(panelState);
            ApplyAtomicVisibility(panelState);
            ApplyDetailPanels(panelState);
        }

        private void LogPanelSnapshotIfChanged(MediaPanelLayoutSnapshot panelState)
        {
            string panelSyncSignature = $"{panelState.ContentKind}|{panelState.PanelMode}|{panelState.IsWide}|{panelState.ShowEpisodesPanel}|{panelState.ShowSourcesPanel}|{panelState.HasSelectedEpisode}|{panelState.IsSourcesFetchInProgress}|{panelState.IsSourcesPanelHidden}";
            if (_lastPanelSyncLogSignature == panelSyncSignature) return;

            _lastPanelSyncLogSignature = panelSyncSignature;
            System.Diagnostics.Debug.WriteLine(
                $"[INFO-SYNC] Layout Synced | Mode: {panelState.PanelMode}, Kind: {panelState.ContentKind}, Wide: {panelState.IsWide}");
        }

        private void ApplyVisualProperties(MediaPanelLayoutSnapshot panelState)
        {
            // 1. Handle Adaptive Layout States (Wide vs Narrow)
            int layoutIndex = panelState.IsWide ? 1 : 0;
            if (_isWideModeIndex != layoutIndex)
            {
                _isWideModeIndex = layoutIndex;
                
                if (panelState.IsWide)
                {
                    // WIDE MODE SETTERS
                    if (OverviewText != null) OverviewText.TextAlignment = TextAlignment.Left;
                    if (GenresText != null) GenresText.TextAlignment = TextAlignment.Left;

                    if (InfoColumn != null)
                    {
                        InfoColumn.MaxWidth = 800;
                        InfoColumn.HorizontalAlignment = HorizontalAlignment.Left;
                        InfoColumn.Spacing = 12;
                    }

                    if (IdentityControl != null) IdentityControl.HorizontalAlignment = HorizontalAlignment.Left;
                    
                    if (MetadataRibbon != null)
                        MetadataRibbon.Margin = new Thickness(2, 0, 0, 0);
                    if (ActionBarGroup != null) ActionBarGroup.HorizontalAlignment = HorizontalAlignment.Left;
                    if (ActionBarPanel != null) ActionBarPanel.HorizontalAlignment = HorizontalAlignment.Left;

                    if (InfoContainerInner != null) InfoContainerInner.Margin = new Thickness(0, 0, 0, 60);

                    if (NarrowSectionsContainer != null) NarrowSectionsContainer.Visibility = Visibility.Collapsed;
                    
                    if (EpisodesPanel != null)
                    {
                        EpisodesPanel.VerticalAlignment = VerticalAlignment.Center;
                        EpisodesPanel.HorizontalAlignment = HorizontalAlignment.Right;
                        EpisodesPanel.Width = 440;
                        EpisodesPanel.Margin = new Thickness(0);
                    }

                    if (SourcesPanel != null)
                    {
                        SourcesPanel.VerticalAlignment = VerticalAlignment.Stretch;
                        SourcesPanel.HorizontalAlignment = HorizontalAlignment.Right;
                        SourcesPanel.Width = 440;
                        SourcesPanel.MaxWidth = 440;
                        SourcesPanel.Margin = new Thickness(0);
                    }
                }
                else
                {
                    if (OverviewText != null) OverviewText.TextAlignment = TextAlignment.Left;
                    if (GenresText != null) GenresText.TextAlignment = TextAlignment.Center;

                    if (InfoColumn != null)
                    {
                        InfoColumn.MaxWidth = 800;
                        InfoColumn.HorizontalAlignment = HorizontalAlignment.Center;
                        InfoColumn.VerticalAlignment = VerticalAlignment.Top;
                        InfoColumn.Margin = new Thickness(0);
                        InfoColumn.Spacing = 12;
                    }

                    if (IdentityControl != null) IdentityControl.HorizontalAlignment = HorizontalAlignment.Center;

                    if (MetadataRibbon != null)
                    {
                        MetadataRibbon.HorizontalAlignment = HorizontalAlignment.Center;
                        MetadataRibbon.Margin = new Thickness(0);
                    }
                    if (ActionBarGroup != null) ActionBarGroup.HorizontalAlignment = HorizontalAlignment.Center;
                    if (ActionBarPanel != null) ActionBarPanel.HorizontalAlignment = HorizontalAlignment.Center;

                    if (InfoContainerInner != null) InfoContainerInner.Margin = new Thickness(0);
                    if (NarrowSectionsContainer != null)
                    {
                        NarrowSectionsContainer.Visibility = Visibility.Visible;
                        NarrowSectionsContainer.Spacing = 0;
                    }

                    if (NarrowEpisodesSection != null)
                    {
                        NarrowEpisodesSection.HorizontalAlignment = HorizontalAlignment.Stretch;
                        NarrowEpisodesSection.Margin = new Thickness(0, 12, 0, 0);
                        NarrowEpisodesSection.Padding = new Thickness(20, 0, 20, 0);
                    }
                    if (NarrowEpisodesHeader != null) NarrowEpisodesHeader.HorizontalAlignment = HorizontalAlignment.Center;

                    if (NarrowSourcesSection != null)
                    {
                        NarrowSourcesSection.HorizontalAlignment = HorizontalAlignment.Stretch;
                        NarrowSourcesHeader.HorizontalAlignment = HorizontalAlignment.Center;
                    }
                    if (NarrowAddonSelector != null) NarrowAddonSelector.HorizontalAlignment = HorizontalAlignment.Center;

                    if (NarrowDirectorSection != null)
                    {
                        NarrowDirectorSection.HorizontalAlignment = HorizontalAlignment.Stretch;
                        NarrowDirectorSection.Padding = new Thickness(20, 0, 20, 0);
                        NarrowDirectorHeader.HorizontalAlignment = HorizontalAlignment.Center;
                    }
                    if (NarrowCastSection != null)
                    {
                        NarrowCastSection.HorizontalAlignment = HorizontalAlignment.Stretch;
                        NarrowCastSection.Padding = new Thickness(20, 0, 20, 0);
                        NarrowCastHeader.HorizontalAlignment = HorizontalAlignment.Center;
                    }

                    if (EpisodesPanel != null)
                    {
                        EpisodesPanel.VerticalAlignment = VerticalAlignment.Top;
                        EpisodesPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                        EpisodesPanel.Width = double.NaN;
                        EpisodesPanel.MaxWidth = double.PositiveInfinity;
                        EpisodesPanel.Margin = new Thickness(0, 20, 0, 0);
                    }

                    if (SourcesPanel != null)
                    {
                        SourcesPanel.VerticalAlignment = VerticalAlignment.Top;
                        SourcesPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                        SourcesPanel.Width = double.NaN;
                        SourcesPanel.MaxWidth = double.PositiveInfinity;
                        SourcesPanel.Margin = new Thickness(0, 20, 0, 0);
                    }

                    if (SourcesRepeater != null) SourcesRepeater.MaxHeight = 800;
                    
                    if (CastSection != null) CastSection.Visibility = Visibility.Collapsed;
                    if (DirectorSection != null) DirectorSection.Visibility = Visibility.Collapsed;
                    if (NarrowCastSection != null) NarrowCastSection.Visibility = Visibility.Collapsed;
                    if (CastShimmer != null) CastShimmer.Visibility = Visibility.Collapsed;
                    if (DirectorShimmer != null) DirectorShimmer.Visibility = Visibility.Collapsed;
                }
            }

            // 2. Handle Content Lifecycle States (Loading vs Ready)
            string contentState = (panelState.IsReady || panelState.IsRevealing) ? "ReadyState" : "LoadingState";
            if (_currentContentStateName != contentState)
            {
                _currentContentStateName = contentState;
            }
        }

        private void ApplyResponsiveContentGrid(MediaPanelLayoutSnapshot panelState)
        {
            if (panelState.IsWide)
            {
                bool showSidebar = panelState.ShowSourcesPanel || panelState.ShowEpisodesPanel;
                ContentGrid.Padding = new Thickness(60, 40, 20, 40);
                if (Col0 != null) Col0.Width = new GridLength(1, GridUnitType.Star);
                if (Col1 != null)
                {
                    Col1.MinWidth = showSidebar ? (panelState.ShowSourcesPanel ? WideSourcesColumnMinWidth : WideEpisodesColumnWidth) : 0;
                    Col1.MaxWidth = showSidebar ? (panelState.ShowSourcesPanel ? WideSourcesColumnMaxWidth : WideEpisodesColumnWidth) : double.PositiveInfinity;
                    Col1.Width = showSidebar
                        ? (panelState.ShowSourcesPanel ? new GridLength(0.42, GridUnitType.Star) : GridLength.Auto)
                        : new GridLength(0);
                }

                if (Row0 != null) Row0.Height = new GridLength(1, GridUnitType.Star);
                if (Row1 != null) Row1.Height = new GridLength(0);
                if (Row2 != null) Row2.Height = new GridLength(0);
                if (Row3 != null) Row3.Height = new GridLength(0);

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
                ContentGrid.Padding = new Thickness(20, 60, 20, 40);
                if (AdaptiveInfoHost != null)
                {
                    AdaptiveInfoHost.Width = double.NaN;
                    AdaptiveInfoHost.VerticalAlignment = VerticalAlignment.Top;
                    AdaptiveInfoHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                }
                if (Col0 != null) Col0.Width = new GridLength(1, GridUnitType.Star);
                if (Col1 != null)
                {
                    Col1.MinWidth = 0;
                    Col1.MaxWidth = double.PositiveInfinity;
                    Col1.Width = new GridLength(0);
                }

                if (Row0 != null) Row0.Height = new GridLength(1, GridUnitType.Auto);
                if (Row1 != null) Row1.Height = new GridLength(1, GridUnitType.Auto);
                if (Row2 != null) Row2.Height = new GridLength(1, GridUnitType.Auto);
                if (Row3 != null) Row3.Height = new GridLength(1, GridUnitType.Auto);

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

            // MediaIdentityControl handles its own internal visibility based on Logo/Title
            bool isIdentityVisible = shouldShowMetadata && (panelState.IsReady || panelState.IsRevealing || panelState.IsLoading);
            if (IdentityControl != null) IdentityControl.Visibility = isIdentityVisible ? Visibility.Visible : Visibility.Collapsed;

            if (MetadataPanel != null) MetadataPanel.Visibility = shouldShowMetadata ? Visibility.Visible : Visibility.Collapsed;
            if (OverviewPanel != null) OverviewPanel.Visibility = shouldShowMetadata ? Visibility.Visible : Visibility.Collapsed;
            if (ActionBarPanel != null) ActionBarPanel.Visibility = shouldShowMetadata ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyAtomicVisibility(MediaPanelLayoutSnapshot panelState)
        {
            if (ActualWidth <= 0) return;

            // 1. Identity (InfoContainer)
            if (DisableMediaInfoRevealAnimationsForCrashIsolation)
            {
                if (InfoContainer != null) InfoContainer.Visibility = (panelState.IsLoading || panelState.IsRevealing || panelState.IsReady) ? Visibility.Visible : Visibility.Collapsed;
                if (InfoContainer != null) InfoContainer.Opacity = 1.0;
            }
            else
            {
                _infoPanelAnimator?.ApplyVisible(panelState.IsLoading || panelState.IsRevealing || panelState.IsReady, isHorizontalReveal: false);
            }

            // 2. People Sections (Cast/Director)
            // [NATIVE AOT SAFE] These are now managed by ParallelRevealCoordinator.
            // We only handle basic structural visibility here if needed, but let the coordinator drive animations.
            // [HEIGHT GUARD] We only show people if the window is tall enough to avoid overcrowding.
            bool canShowPeople = panelState.IsWide && 
                                 (panelState.IsLoading || panelState.IsRevealing || panelState.IsReady) &&
                                 panelState.ViewportHeight >= WidePeopleComfortHeight;
            bool hasCast = CastList?.Count > 0;
            bool hasDirector = DirectorList?.Count > 0;
            bool isStillTransitioning = panelState.IsLoading;

            // If we are NOT wide, or have no data, ensure they are collapsed.
            // [RACE CONDITION FIX] We only collapse if we are truly READY and still have no data.
            // If we are Loading or Revealing, we must keep them available for the coordinator.
            
            if (!canShowPeople || (!hasCast && !isStillTransitioning))
            {
                if (CastSection != null) CastSection.Visibility = Visibility.Collapsed;
                if (CastShimmer != null) CastShimmer.Visibility = Visibility.Collapsed;
            }
            else
            {
                // [NATIVE AOT SAFE] If we have data or are loading/revealing, we allow them to be visible.
                // The actual opacity is still managed by the reveal coordinator/animator for a smooth entry.
                if (CastSection != null && CastSection.Visibility != Visibility.Visible) CastSection.Visibility = Visibility.Visible;
                
                // Keep shimmer visible ONLY if we don't have real data yet
                if (CastShimmer != null) CastShimmer.Visibility = !hasCast ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!canShowPeople || (!hasDirector && !isStillTransitioning))
            {
                if (DirectorSection != null) DirectorSection.Visibility = Visibility.Collapsed;
                if (DirectorShimmer != null) DirectorShimmer.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (DirectorSection != null && DirectorSection.Visibility != Visibility.Visible) DirectorSection.Visibility = Visibility.Visible;
                if (DirectorShimmer != null) DirectorShimmer.Visibility = !hasDirector ? Visibility.Visible : Visibility.Collapsed;
            }

            // 3. Episodes
            bool hasEpisodes = (Seasons?.Count > 0 && CurrentEpisodes?.Count > 0) || 
                               (panelState.IsLoading && panelState.ContentKind == MediaContentKind.Series);
            if (DisableAllPanelAnimatorsForCrashIsolation)
            {
                if (EpisodesPanel != null) EpisodesPanel.Visibility = (panelState.ShowEpisodesPanel && hasEpisodes) ? Visibility.Visible : Visibility.Collapsed;
                if (EpisodesPanel != null) EpisodesPanel.Opacity = 1.0;
            }
            else
            {
                _episodesPanelAnimator?.ApplyVisible(panelState.ShowEpisodesPanel && hasEpisodes, isHorizontalReveal: true);
            }

            // 4. Sources
            if (DisableMediaInfoRevealAnimationsForCrashIsolation)
            {
                if (SourcesPanel != null) SourcesPanel.Visibility = panelState.ShowSourcesPanel ? Visibility.Visible : Visibility.Collapsed;
                if (SourcesPanel != null) SourcesPanel.Opacity = 1.0;
            }
            else
            {
                _sourcesPanelAnimator?.ApplyVisible(panelState.ShowSourcesPanel, isHorizontalReveal: true);
            }

            // 5. Shared UI Controls
            if (BtnHideSources != null) BtnHideSources.Visibility = (panelState.IsWide && panelState.ShowSourcesPanel) ? Visibility.Visible : Visibility.Collapsed;
            if (BtnBackToEpisodes != null)
            {
                BtnBackToEpisodes.Visibility = (panelState.ShowSourcesPanel && panelState.ContentKind == MediaContentKind.Series) ? Visibility.Visible : Visibility.Collapsed;
            }
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

            if (InfoContainer != null)
            {
                Grid.SetRow(InfoContainer, 0);
                Grid.SetColumn(InfoContainer, 0);
                Grid.SetColumnSpan(InfoContainer, panelState.IsWide ? 1 : 2);
            }

            if (SourcesPanel != null)
            {
                Grid.SetRow(SourcesPanel, panelState.IsWide ? 0 : 1);
                Grid.SetColumn(SourcesPanel, panelState.IsWide ? 1 : 0);
                Grid.SetColumnSpan(SourcesPanel, panelState.IsWide ? 1 : 2);
                SourcesPanel.VerticalAlignment = panelState.IsWide ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            }

            if (EpisodesPanel != null)
            {
                Grid.SetRow(EpisodesPanel, panelState.IsWide ? 0 : 2);
                Grid.SetColumn(EpisodesPanel, panelState.IsWide ? 1 : 0);
                Grid.SetColumnSpan(EpisodesPanel, panelState.IsWide ? 1 : 2);
                EpisodesPanel.VerticalAlignment = panelState.IsWide ? VerticalAlignment.Center : VerticalAlignment.Top;
            }

            if (NarrowSectionsContainer != null)
            {
                Grid.SetRow(NarrowSectionsContainer, 3);
                NarrowSectionsContainer.Visibility = !panelState.IsWide && (panelState.IsReady || panelState.IsRevealing) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ApplyDetailPanels(MediaPanelLayoutSnapshot panelState)
        {
            RefreshAllShimmers();
            if (EpisodesRepeater != null) EpisodesRepeater.Visibility = panelState.ShowEpisodesPanel ? Visibility.Visible : Visibility.Collapsed;
        }

        private double GetInfoPanelWidth()
        {
            double viewportWidth = RootGrid?.ActualWidth ?? 0;
            if (viewportWidth <= 0) viewportWidth = _lastReportedWidth;
            if (viewportWidth <= 0) return 800;

            if (viewportWidth >= LayoutAdaptiveThreshold)
            {
                double sideWidth = Col1 != null ? Col1.ActualWidth : 0;
                if (sideWidth <= 0) sideWidth = WideEpisodesColumnWidth;
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
