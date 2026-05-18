using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Umbrella coordinator for all detail panel concerns (Sources and Episodes).
    /// Owns per-panel reveal state, the shared reveal animation engine, placeholder factory,
    /// hover animation helper, and the SourcesManager. Acts as the single entry point
    /// for panel content operations, replacing the scattered logic that previously lived
    /// across <c>MediaInfoPage.Sources.cs</c>, <c>MediaInfoPage.Episodes.cs</c>, and
    /// <c>MediaInfoPage.PanelState.cs</c>.
    /// <para>
    /// This class does NOT own panel visibility or layout — those remain the responsibility
    /// of <see cref="Services.PanelOwner"/>. DetailPanelController handles the content level:
    /// reveal animations, item collections, shimmer placeholders, and hover effects.
    /// </para>
    /// </summary>
    internal sealed class DetailPanelController : IDisposable
    {
        #region Fields

        private readonly MediaInfoPage _page;
        private readonly Compositor _compositor;
        private bool _disposed;

        #endregion

        #region Per-Panel State

        /// <summary>Reveal state for the Sources panel.</summary>
        public PanelRevealState SourcesState { get; }

        /// <summary>Reveal state for the Episodes panel.</summary>
        public PanelRevealState EpisodesState { get; }

        #endregion

        #region Shared Engines

        /// <summary>Composition-based reveal animation engine shared by both panels.</summary>
        public RevealAnimationEngine RevealEngine { get; }

        /// <summary>Sources panel lifecycle manager (addon loading, selection, stream presentation).</summary>
        public SourcesManager SourcesManager { get; }

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new DetailPanelController and initializes all subordinate engines.
        /// </summary>
        /// <param name="page">The MediaInfoPage that owns this controller.</param>
        public DetailPanelController(MediaInfoPage page)
        {
            _page = page ?? throw new ArgumentNullException(nameof(page));
            _compositor = page.CompositorInstance ?? throw new ArgumentNullException(nameof(page.CompositorInstance));

            SourcesState = new PanelRevealState();
            EpisodesState = new PanelRevealState();

            RevealEngine = new RevealAnimationEngine(_compositor);
            SourcesManager = new SourcesManager(page, RevealEngine, SourcesState);

            Debug.WriteLine("[DETAIL-PANEL] Initialized");
        }

        #endregion

        #region Panel Open Orchestration

        /// <summary>
        /// Prepares the Sources panel for opening by resetting its reveal state
        /// and setting the presentation timestamp.
        /// </summary>
        /// <param name="itemCount">Number of items in the incoming collection.</param>
        public void PrepareSourcesOpen(int itemCount)
        {
            if (_disposed) return;
            SourcesState.PrepareOpen(itemCount);
        }

        /// <summary>
        /// Prepares the Episodes panel for opening by resetting its reveal state
        /// and setting the presentation timestamp.
        /// </summary>
        /// <param name="itemCount">Number of items in the incoming collection.</param>
        public void PrepareEpisodesOpen(int itemCount)
        {
            if (_disposed) return;
            EpisodesState.PrepareOpen(itemCount);
        }

        /// <summary>
        /// Resets all panel state in preparation for navigating to a new content item.
        /// </summary>
        public void ResetAll()
        {
            if (_disposed) return;
            SourcesState.Reset();
            EpisodesState.Reset();
            SourcesManager.Clear();
            Debug.WriteLine("[DETAIL-PANEL] All panels reset");
        }

        #endregion

        #region ItemsRepeater Event Routing

        /// <summary>
        /// Routes an ElementPrepared event to the appropriate panel's reveal engine.
        /// </summary>
        /// <param name="panelType">Which panel the event belongs to.</param>
        /// <param name="sender">The ItemsRepeater that raised the event.</param>
        /// <param name="args">The event arguments.</param>
        public void OnElementPrepared(PanelType panelType, ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (_disposed) return;
            var state = GetState(panelType);
            var element = args.Element as FrameworkElement;
            if (element == null) return;

            int generation = state.VisualGeneration;
            int preparedIndex = args.Index;

            // Determine if this is a shimmer placeholder
            bool isShimmer = panelType == PanelType.Sources
                ? (element.DataContext as StremioStreamViewModel)?.IsPlaceholder == true
                : (element.DataContext as EpisodeItem)?.IsPlaceholder == true;

            string stamp = $"{(panelType == PanelType.Sources ? "src" : "ep")}:{generation}:{preparedIndex}:{(isShimmer ? "shm" : "real")}";

            // If transitioning from shimmer to real, allow a second reveal
            if (!isShimmer && element.Tag?.ToString().Contains(":shm") == true)
            {
                state.AnimatedRevealIndexes.Remove(preparedIndex);
            }

            if (Equals(element.Tag, stamp)) return;
            element.Tag = stamp;

            if (preparedIndex < state.RevealItemLimit && !state.AnimatedRevealIndexes.Contains(preparedIndex))
            {
                RevealEngine.PrepareForReveal(element);
                RevealEngine.ApplyStaggeredReveal(element, preparedIndex, state);
                state.AnimatedRevealIndexes.Add(preparedIndex);
            }
            else
            {
                RevealEngine.ResetRevealState(element);
            }
        }

        /// <summary>
        /// Routes an ElementClearing event to the reveal engine for visual reset.
        /// </summary>
        /// <param name="panelType">Which panel the event belongs to.</param>
        /// <param name="sender">The ItemsRepeater that raised the event.</param>
        /// <param name="args">The event arguments.</param>
        public void OnElementClearing(PanelType panelType, ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            if (_disposed || args.Element is not FrameworkElement fe) return;
            fe.DataContext = null;
            fe.Tag = null;
            RevealEngine.ResetRevealState(fe);
        }

        #endregion

        #region Hover Animation Routing

        /// <summary>
        /// Handles pointer-enter on any panel item container (Sources or Episodes).
        /// </summary>
        public void OnItemPointerEntered(object sender) => HoverAnimationHelper.OnPointerEntered(sender);

        /// <summary>
        /// Handles pointer-exit on any panel item container (Sources or Episodes).
        /// </summary>
        public void OnItemPointerExited(object sender) => HoverAnimationHelper.OnPointerExited(sender);

        #endregion

        #region Placeholder Factory

        /// <summary>
        /// Creates shimmer placeholder objects for the Sources panel.
        /// </summary>
        /// <param name="count">Number of placeholders to create.</param>
        /// <returns>A list of StremioStreamViewModel placeholders.</returns>
        public ObservableCollection<StremioStreamViewModel> CreateSourcePlaceholders(int count)
        {
            var list = PlaceholderFactory.CreatePlaceholders(
                count,
                opacity => new StremioStreamViewModel
                {
                    IsPlaceholder = true,
                    ShimmerOpacity = opacity
                });
            return new ObservableCollection<StremioStreamViewModel>(list);
        }

        /// <summary>
        /// Creates shimmer placeholder objects for the Episodes panel.
        /// </summary>
        /// <param name="count">Number of placeholders to create.</param>
        /// <returns>A list of EpisodeItem placeholders.</returns>
        public ObservableCollection<EpisodeItem> CreateEpisodePlaceholders(int count)
        {
            var list = PlaceholderFactory.CreatePlaceholders(
                count,
                opacity => new EpisodeItem
                {
                    IsPlaceholder = true,
                    ShimmerOpacity = opacity
                });
            return new ObservableCollection<EpisodeItem>(list);
        }

        /// <summary>
        /// Calculates how many shimmer rows are needed to fill a container.
        /// </summary>
        /// <param name="containerHeight">Measured height of the panel or scroll viewer.</param>
        /// <param name="itemHeight">Expected height of a single placeholder row.</param>
        /// <param name="minCount">Minimum count to return when height is unavailable.</param>
        /// <returns>Number of placeholders needed.</returns>
        public int CalculateSkeletonCount(double containerHeight, double itemHeight, int minCount = 8)
        {
            return PlaceholderFactory.CalculateSkeletonCount(containerHeight, itemHeight, minCount);
        }

        #endregion

        #region Helpers

        private PanelRevealState GetState(PanelType panelType) =>
            panelType == PanelType.Sources ? SourcesState : EpisodesState;

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SourcesManager.Dispose();
            Debug.WriteLine("[DETAIL-PANEL] Disposed");
        }

        #endregion
    }

    /// <summary>
    /// Identifies which detail panel an operation targets.
    /// </summary>
    internal enum PanelType
    {
        /// <summary>The Sources panel (Stremio addon streams).</summary>
        Sources,
        /// <summary>The Episodes panel (season/episode list).</summary>
        Episodes
    }
}
