using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Pure layout computation engine. Transforms viewport state and panel configuration
    /// into a complete layout decision with zero side effects.
    /// </summary>
    internal static class LayoutEngine
    {
        #region Constants

        private const double LayoutAdaptiveThreshold = 950.0;
        private const double WideSourcesColumnMinWidth = 400.0;
        private const double WideSourcesColumnMaxWidth = 600.0;
        private const double WideEpisodesColumnWidth = 420.0;
        private const double WidePeopleComfortHeight = 720.0;

        #endregion

        #region Public API

        /// <summary>
        /// Computes a complete layout decision from the given inputs.
        /// This is a pure function — no side effects, no UI mutations.
        /// </summary>
        public static LayoutDecision Compute(LayoutInputs inputs)
        {
            Debug.WriteLine($"[LAYOUT] Compute started. Width={inputs.ViewportWidth}, PanelMode={inputs.PanelMode}, HasEpisodes={inputs.HasEpisodes}");
            try
            {
                bool isWide = inputs.ViewportWidth >= LayoutAdaptiveThreshold;
                bool showSidebar = inputs.PanelMode != MediaDetailPanelMode.None;

                var grid = ComputeGridConfig(isWide, showSidebar, inputs.PanelMode);
                var placement = ComputePanelPlacement(isWide);
                var visual = ComputeVisualProperties(isWide);
                var visibility = ComputeVisibility(inputs, isWide);

                var decision = new LayoutDecision(
                    grid, placement, visual, visibility, isWide,
                    inputs.ViewportWidth, inputs.ViewportHeight);

                Debug.WriteLine($"[LAYOUT] Compute finished. Width={decision.ViewportWidth}, Wide={decision.IsWide}");
                return decision;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LAYOUT-ENGINE] Compute failed: {ex.Message}");
                return LayoutDecision.Default;
            }
        }

        #endregion

        #region Grid Configuration

        private static GridConfig ComputeGridConfig(bool isWide, bool showSidebar, MediaDetailPanelMode panelMode)
        {
            if (isWide)
            {
                bool showSources = panelMode == MediaDetailPanelMode.Sources;
                double col1Min = showSidebar ? (showSources ? WideSourcesColumnMinWidth : WideEpisodesColumnWidth) : 0;
                double col1Max = showSidebar ? (showSources ? WideSourcesColumnMaxWidth : WideEpisodesColumnWidth) : double.PositiveInfinity;
                GridLength col1Width = showSidebar
                    ? (showSources ? new GridLength(0.42, GridUnitType.Star) : GridLength.Auto)
                    : new GridLength(0);

                return new GridConfig(
                    col0Width: new GridLength(1, GridUnitType.Star),
                    col1Width: col1Width,
                    col1MinWidth: col1Min,
                    col1MaxWidth: col1Max,
                    rowHeights: new[]
                    {
                        new GridLength(1, GridUnitType.Star),  // Row0: Info
                        new GridLength(0),                      // Row1: Sources (wide)
                        new GridLength(0),                      // Row2: Episodes (narrow)
                        new GridLength(0),                      // Row3: Narrow sections
                        new GridLength(0),                      // Row4: Content Buffer
                    },
                    contentGridPadding: new Thickness(60, 0, 20, 30),
                    scrollBarVisibility: ScrollBarVisibility.Disabled,
                    scrollMode: ScrollMode.Disabled);
            }
            else
            {
                return new GridConfig(
                    col0Width: new GridLength(1, GridUnitType.Star),
                    col1Width: new GridLength(0),
                    col1MinWidth: 0,
                    col1MaxWidth: double.PositiveInfinity,
                    rowHeights: new[]
                    {
                        new GridLength(1, GridUnitType.Star),   // Row0: Info
                        new GridLength(1, GridUnitType.Auto),   // Row1: Sources
                        new GridLength(1, GridUnitType.Auto),   // Row2: Episodes
                        new GridLength(1, GridUnitType.Auto),   // Row3: Narrow sections
                        new GridLength(1, GridUnitType.Auto),   // Row4: Content Buffer
                    },
                    contentGridPadding: new Thickness(20, 0, 20, 30),
                    scrollBarVisibility: ScrollBarVisibility.Auto,
                    scrollMode: ScrollMode.Auto);
            }
        }

        #endregion

        #region Panel Placement

        private static PanelPlacement ComputePanelPlacement(bool isWide)
        {
            if (isWide)
            {
                return new PanelPlacement(
                    infoRow: 0, infoColumn: 0, infoColumnSpan: 1,
                    sourcesRow: 0, sourcesColumn: 1, sourcesColumnSpan: 1,
                    episodesRow: 0, episodesColumn: 1, episodesColumnSpan: 1,
                    narrowSectionsVisible: false);
            }
            else
            {
                return new PanelPlacement(
                    infoRow: 0, infoColumn: 0, infoColumnSpan: 2,
                    sourcesRow: 1, sourcesColumn: 0, sourcesColumnSpan: 2,
                    episodesRow: 2, episodesColumn: 0, episodesColumnSpan: 2,
                    narrowSectionsVisible: true);
            }
        }

        #endregion

        #region Visual Properties

        private static VisualProperties ComputeVisualProperties(bool isWide)
        {
            return new VisualProperties(
                isWide: isWide,
                overviewTextAlignment: TextAlignment.Left,
                genresTextAlignment: isWide ? TextAlignment.Left : TextAlignment.Center,
                infoColumnHAlign: isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                infoColumnMaxWidth: 1200,
                infoColumnSpacing: 12,
                infoContainerInnerVAlign: isWide ? VerticalAlignment.Stretch : VerticalAlignment.Top,
                infoContainerInnerHAlign: isWide ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
                adaptiveInfoHostHAlign: isWide ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
                adaptiveInfoHostVAlign: isWide ? VerticalAlignment.Bottom : VerticalAlignment.Top,
                adaptiveInfoHostWidth: double.NaN,
                identityControlHAlign: isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                metadataRibbonHAlign: isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                metadataRibbonMargin: new Thickness(2, 0, 0, 0),
                actionBarHAlign: isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                episodesPanelVAlign: isWide ? VerticalAlignment.Stretch : VerticalAlignment.Top,
                episodesPanelHAlign: isWide ? HorizontalAlignment.Stretch : HorizontalAlignment.Stretch,
                episodesPanelWidth: isWide ? 440 : double.NaN,
                episodesPanelMaxWidth: isWide ? 600 : 600,
                episodesPanelMargin: isWide ? new Thickness(0, 16, 0, 0) : new Thickness(0),
                sourcesPanelVAlign: isWide ? VerticalAlignment.Stretch : VerticalAlignment.Stretch,
                sourcesPanelHAlign: isWide ? HorizontalAlignment.Stretch : HorizontalAlignment.Stretch,
                sourcesPanelWidth: isWide ? double.NaN : double.NaN,
                sourcesPanelMaxWidth: isWide ? 600 : 600,
                sourcesPanelMargin: isWide ? new Thickness(40, 16, 0, 0) : new Thickness(0),
                infoContainerInnerMargin: new Thickness(0));
        }

        #endregion

        #region Visibility Computation

        private static VisibilityMap ComputeVisibility(LayoutInputs inputs, bool isWide)
        {
            bool isLoading = inputs.LoadState == PageLoadState.Loading;
            bool isRevealing = inputs.LoadState == PageLoadState.Revealing;
            bool isReady = inputs.LoadState == PageLoadState.Ready;
            bool isTransitioning = isLoading || isRevealing || isReady;

            bool showSourcesPanel = inputs.PanelMode == MediaDetailPanelMode.Sources && !inputs.IsSourcesPanelHidden;
            bool showEpisodesPanel = inputs.PanelMode == MediaDetailPanelMode.Episodes;

            bool canShowPeople = isWide && isTransitioning && inputs.ViewportHeight >= WidePeopleComfortHeight;
            bool hasCast = inputs.CastCount > 0;
            bool hasDirector = inputs.DirectorCount > 0;
            bool isStillLoading = isLoading;

            return new VisibilityMap(
                infoContainer: isTransitioning ? Visibility.Visible : Visibility.Collapsed,
                castSection: (canShowPeople && (hasCast || isStillLoading)) ? Visibility.Visible : Visibility.Collapsed,
                castShimmer: (canShowPeople && !hasCast && isStillLoading) ? Visibility.Visible : Visibility.Collapsed,
                directorSection: (canShowPeople && (hasDirector || isStillLoading)) ? Visibility.Visible : Visibility.Collapsed,
                directorShimmer: (canShowPeople && !hasDirector && isStillLoading) ? Visibility.Visible : Visibility.Collapsed,
                narrowSectionsContainer: (!isWide && isTransitioning) ? Visibility.Visible : Visibility.Collapsed,
                btnHideSources: (isWide && showSourcesPanel) ? Visibility.Visible : Visibility.Collapsed,
                btnBackToEpisodes: (showSourcesPanel && inputs.ContentKind == MediaContentKind.Series) ? Visibility.Visible : Visibility.Collapsed,
                sourcesShowHandle: (isWide && inputs.PanelMode == MediaDetailPanelMode.None) ? Visibility.Visible : Visibility.Collapsed,
                identityControl: isTransitioning ? Visibility.Visible : Visibility.Collapsed,
                metadataPanel: (inputs.HasMetadata || isTransitioning) ? Visibility.Visible : Visibility.Collapsed,
                overviewPanel: (inputs.HasMetadata || isTransitioning) ? Visibility.Visible : Visibility.Collapsed,
                actionBarPanel: (inputs.HasMetadata || isTransitioning) ? Visibility.Visible : Visibility.Collapsed);
        }

        #endregion
    }

    #region Input/Output Types

    /// <summary>
    /// Immutable input record for layout computation.
    /// </summary>
    internal sealed record LayoutInputs(
        double ViewportWidth,
        double ViewportHeight,
        PageLoadState LoadState,
        MediaDetailPanelMode PanelMode,
        MediaContentKind ContentKind,
        bool IsSourcesPanelHidden,
        bool IsSourcesFetchInProgress,
        int CastCount,
        int DirectorCount,
        bool HasEpisodes,
        bool HasMetadata,
        bool HasSelectedEpisode);

    /// <summary>
    /// Immutable layout decision produced by <see cref="LayoutEngine"/>.
    /// </summary>
    internal readonly struct LayoutDecision
    {
        public static LayoutDecision Default { get; } = new LayoutDecision(
            GridConfig.Default, PanelPlacement.Default, VisualProperties.Default,
            VisibilityMap.Default, false, 0, 0);

        public LayoutDecision(GridConfig grid, PanelPlacement placement, VisualProperties visual,
            VisibilityMap visibility, bool isWide, double viewportWidth, double viewportHeight)
        {
            Grid = grid;
            Placement = placement;
            Visual = visual;
            Visibility = visibility;
            IsWide = isWide;
            ViewportWidth = viewportWidth;
            ViewportHeight = viewportHeight;
        }

        public GridConfig Grid { get; }
        public PanelPlacement Placement { get; }
        public VisualProperties Visual { get; }
        public VisibilityMap Visibility { get; }
        public bool IsWide { get; }
        public double ViewportWidth { get; }
        public double ViewportHeight { get; }
    }

    internal readonly struct GridConfig
    {
        public static GridConfig Default { get; } = new GridConfig(
            new GridLength(1, GridUnitType.Star), new GridLength(0), 0, double.PositiveInfinity,
            new[] { new GridLength(1, GridUnitType.Auto), new GridLength(0), new GridLength(0), new GridLength(0) },
            new Thickness(20, 60, 20, 40), ScrollBarVisibility.Auto, ScrollMode.Auto);

        public GridConfig(GridLength col0Width, GridLength col1Width, double col1MinWidth, double col1MaxWidth,
            GridLength[] rowHeights, Thickness contentGridPadding,
            ScrollBarVisibility scrollBarVisibility, ScrollMode scrollMode)
        {
            Col0Width = col0Width;
            Col1Width = col1Width;
            Col1MinWidth = col1MinWidth;
            Col1MaxWidth = col1MaxWidth;
            RowHeights = rowHeights;
            ContentGridPadding = contentGridPadding;
            ScrollBarVisibility = scrollBarVisibility;
            ScrollMode = scrollMode;
        }

        public GridLength Col0Width { get; }
        public GridLength Col1Width { get; }
        public double Col1MinWidth { get; }
        public double Col1MaxWidth { get; }
        public GridLength[] RowHeights { get; }
        public Thickness ContentGridPadding { get; }
        public ScrollBarVisibility ScrollBarVisibility { get; }
        public ScrollMode ScrollMode { get; }
    }

    internal readonly struct PanelPlacement
    {
        public static PanelPlacement Default { get; } = new PanelPlacement(0, 0, 2, 1, 0, 1, 2, 0, 2, true);

        public PanelPlacement(int infoRow, int infoColumn, int infoColumnSpan,
            int sourcesRow, int sourcesColumn, int sourcesColumnSpan,
            int episodesRow, int episodesColumn, int episodesColumnSpan,
            bool narrowSectionsVisible)
        {
            InfoRow = infoRow;
            InfoColumn = infoColumn;
            InfoColumnSpan = infoColumnSpan;
            SourcesRow = sourcesRow;
            SourcesColumn = sourcesColumn;
            SourcesColumnSpan = sourcesColumnSpan;
            EpisodesRow = episodesRow;
            EpisodesColumn = episodesColumn;
            EpisodesColumnSpan = episodesColumnSpan;
            NarrowSectionsVisible = narrowSectionsVisible;
        }

        public int InfoRow { get; }
        public int InfoColumn { get; }
        public int InfoColumnSpan { get; }
        public int SourcesRow { get; }
        public int SourcesColumn { get; }
        public int SourcesColumnSpan { get; }
        public int EpisodesRow { get; }
        public int EpisodesColumn { get; }
        public int EpisodesColumnSpan { get; }
        public bool NarrowSectionsVisible { get; }
    }

    internal readonly struct VisualProperties
    {
        public static VisualProperties Default { get; } = new VisualProperties(
            false, TextAlignment.Left, TextAlignment.Center, HorizontalAlignment.Center,
            800, 12, VerticalAlignment.Top, HorizontalAlignment.Stretch,
            HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN,
            HorizontalAlignment.Center, HorizontalAlignment.Center, new Thickness(0),
            HorizontalAlignment.Center, VerticalAlignment.Top, HorizontalAlignment.Stretch,
            double.NaN, double.PositiveInfinity, new Thickness(0, 20, 0, 0),
            VerticalAlignment.Top, HorizontalAlignment.Stretch, double.NaN,
            double.PositiveInfinity, new Thickness(0, 20, 0, 0), new Thickness(0));

        public VisualProperties(bool isWide, TextAlignment overviewTextAlignment, TextAlignment genresTextAlignment,
            HorizontalAlignment infoColumnHAlign, double infoColumnMaxWidth, int infoColumnSpacing,
            VerticalAlignment infoContainerInnerVAlign, HorizontalAlignment infoContainerInnerHAlign,
            HorizontalAlignment adaptiveInfoHostHAlign, VerticalAlignment adaptiveInfoHostVAlign,
            double adaptiveInfoHostWidth, HorizontalAlignment identityControlHAlign,
            HorizontalAlignment metadataRibbonHAlign, Thickness metadataRibbonMargin,
            HorizontalAlignment actionBarHAlign,
            VerticalAlignment episodesPanelVAlign, HorizontalAlignment episodesPanelHAlign,
            double episodesPanelWidth, double episodesPanelMaxWidth, Thickness episodesPanelMargin,
            VerticalAlignment sourcesPanelVAlign, HorizontalAlignment sourcesPanelHAlign,
            double sourcesPanelWidth, double sourcesPanelMaxWidth, Thickness sourcesPanelMargin,
            Thickness infoContainerInnerMargin)
        {
            IsWide = isWide;
            OverviewTextAlignment = overviewTextAlignment;
            GenresTextAlignment = genresTextAlignment;
            InfoColumnHAlign = infoColumnHAlign;
            InfoColumnMaxWidth = infoColumnMaxWidth;
            InfoColumnSpacing = infoColumnSpacing;
            InfoContainerInnerVAlign = infoContainerInnerVAlign;
            InfoContainerInnerHAlign = infoContainerInnerHAlign;
            AdaptiveInfoHostHAlign = adaptiveInfoHostHAlign;
            AdaptiveInfoHostVAlign = adaptiveInfoHostVAlign;
            AdaptiveInfoHostWidth = adaptiveInfoHostWidth;
            IdentityControlHAlign = identityControlHAlign;
            MetadataRibbonHAlign = metadataRibbonHAlign;
            MetadataRibbonMargin = metadataRibbonMargin;
            ActionBarHAlign = actionBarHAlign;
            EpisodesPanelVAlign = episodesPanelVAlign;
            EpisodesPanelHAlign = episodesPanelHAlign;
            EpisodesPanelWidth = episodesPanelWidth;
            EpisodesPanelMaxWidth = episodesPanelMaxWidth;
            EpisodesPanelMargin = episodesPanelMargin;
            SourcesPanelVAlign = sourcesPanelVAlign;
            SourcesPanelHAlign = sourcesPanelHAlign;
            SourcesPanelWidth = sourcesPanelWidth;
            SourcesPanelMaxWidth = sourcesPanelMaxWidth;
            SourcesPanelMargin = sourcesPanelMargin;
            InfoContainerInnerMargin = infoContainerInnerMargin;
        }

        public bool IsWide { get; }
        public TextAlignment OverviewTextAlignment { get; }
        public TextAlignment GenresTextAlignment { get; }
        public HorizontalAlignment InfoColumnHAlign { get; }
        public double InfoColumnMaxWidth { get; }
        public int InfoColumnSpacing { get; }
        public VerticalAlignment InfoContainerInnerVAlign { get; }
        public HorizontalAlignment InfoContainerInnerHAlign { get; }
        public HorizontalAlignment AdaptiveInfoHostHAlign { get; }
        public VerticalAlignment AdaptiveInfoHostVAlign { get; }
        public double AdaptiveInfoHostWidth { get; }
        public HorizontalAlignment IdentityControlHAlign { get; }
        public HorizontalAlignment MetadataRibbonHAlign { get; }
        public Thickness MetadataRibbonMargin { get; }
        public HorizontalAlignment ActionBarHAlign { get; }
        public VerticalAlignment EpisodesPanelVAlign { get; }
        public HorizontalAlignment EpisodesPanelHAlign { get; }
        public double EpisodesPanelWidth { get; }
        public double EpisodesPanelMaxWidth { get; }
        public Thickness EpisodesPanelMargin { get; }
        public VerticalAlignment SourcesPanelVAlign { get; }
        public HorizontalAlignment SourcesPanelHAlign { get; }
        public double SourcesPanelWidth { get; }
        public double SourcesPanelMaxWidth { get; }
        public Thickness SourcesPanelMargin { get; }
        public Thickness InfoContainerInnerMargin { get; }
    }

    internal readonly struct VisibilityMap
    {
        public static VisibilityMap Default { get; } = new VisibilityMap(
            Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed,
            Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed,
            Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed,
            Visibility.Collapsed, Visibility.Collapsed);

        public VisibilityMap(
            Visibility infoContainer,
            Visibility castSection, Visibility castShimmer,
            Visibility directorSection, Visibility directorShimmer,
            Visibility narrowSectionsContainer, Visibility btnHideSources,
            Visibility btnBackToEpisodes, Visibility sourcesShowHandle,
            Visibility identityControl, Visibility metadataPanel,
            Visibility overviewPanel, Visibility actionBarPanel)
        {
            InfoContainer = infoContainer;
            CastSection = castSection;
            CastShimmer = castShimmer;
            DirectorSection = directorSection;
            DirectorShimmer = directorShimmer;
            NarrowSectionsContainer = narrowSectionsContainer;
            BtnHideSources = btnHideSources;
            BtnBackToEpisodes = btnBackToEpisodes;
            SourcesShowHandle = sourcesShowHandle;
            IdentityControl = identityControl;
            MetadataPanel = metadataPanel;
            OverviewPanel = overviewPanel;
            ActionBarPanel = actionBarPanel;
        }

        public Visibility InfoContainer { get; }
        public Visibility CastSection { get; }
        public Visibility CastShimmer { get; }
        public Visibility DirectorSection { get; }
        public Visibility DirectorShimmer { get; }
        public Visibility NarrowSectionsContainer { get; }
        public Visibility BtnHideSources { get; }
        public Visibility BtnBackToEpisodes { get; }
        public Visibility SourcesShowHandle { get; }
        public Visibility IdentityControl { get; }
        public Visibility MetadataPanel { get; }
        public Visibility OverviewPanel { get; }
        public Visibility ActionBarPanel { get; }
    }

    #endregion
}
