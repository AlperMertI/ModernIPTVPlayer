using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Manages the Sources panel lifecycle: addon loading, stream presentation,
    /// addon selection, underline positioning, and ItemsRepeater element animations.
    /// Extracted from <c>MediaInfoPage.Sources.cs</c> to isolate sources-specific concerns
    /// and eliminate the nested <c>SourcesPanelController</c> class.
    /// </summary>
    internal sealed class SourcesManager : IDisposable
    {
        #region Fields

        private readonly MediaInfoPage _page;
        private readonly RevealAnimationEngine _revealEngine;
        private readonly PanelRevealState _revealState;
        private readonly DispatcherQueue _dispatcher;
        private bool _disposed;
        private bool _userHasManuallySelectedAddon;
        private bool _isUnderlineImplicitInitialized;
        private int _addonUnderlineRetryCount;
        private int _addonUnderlineSettleVersion;
        private bool _isAddonUnderlineUpdateQueued;
        private bool _queuedAddonUnderlineAnimate;
        private int _lastSourcesShimmerCount = -1;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new SourcesManager bound to the given page and reveal engine.
        /// </summary>
        /// <param name="page">The MediaInfoPage that owns this manager.</param>
        /// <param name="revealEngine">Shared reveal animation engine for ItemsRepeater items.</param>
        /// <param name="revealState">Per-panel reveal state tracker for the Sources panel.</param>
        public SourcesManager(MediaInfoPage page, RevealAnimationEngine revealEngine, PanelRevealState revealState)
        {
            _page = page ?? throw new ArgumentNullException(nameof(page));
            _revealEngine = revealEngine ?? throw new ArgumentNullException(nameof(revealEngine));
            _revealState = revealState ?? throw new ArgumentNullException(nameof(revealState));
            _dispatcher = page.DispatcherQueue ?? throw new ArgumentNullException(nameof(page.DispatcherQueue));
            Debug.WriteLine("[SOURCES] Initialized");
        }

        #endregion

        #region Addon Collection Management

        /// <summary>
        /// Clears all addon and stream state in preparation for a new content item.
        /// </summary>
        public void Clear()
        {
            if (_disposed) return;
            try
            {
                _userHasManuallySelectedAddon = false;
                _lastSourcesShimmerCount = -1;
                _revealState.Reset();
                _page.VisibleSourceStreamsCollection.Clear();
                _page.AddonResults?.Clear();

                if (_page.SourcesRepeaterField != null && _page.SourcesRepeaterField.ItemsSource != _page.VisibleSourceStreamsCollection)
                {
                    _page.SourcesRepeaterField.ItemsSource = _page.VisibleSourceStreamsCollection;
                }
                _page.ResetSourcesListTransitionState();
                if (_page.AddonSelectorListField != null)
                {
                    _page.IsApplyingSourceSelection = true;
                    try
                    {
                        _page.AddonSelectorListField.SelectedItem = null;
                        _page.AddonSelectorListField.ItemsSource = null;
                    }
                    finally
                    {
                        _page.IsApplyingSourceSelection = false;
                    }
                }
                UpdateAddonSelectionUnderline(false);
                Debug.WriteLine("[SOURCES] Cleared");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SOURCES] Clear error: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures the addon results collection exists and is bound to the UI.
        /// </summary>
        /// <param name="resetUserSelection">Whether to reset the manual selection flag.</param>
        /// <returns>The active addon results collection.</returns>
        public ObservableCollection<StremioAddonViewModel> EnsureCollection(bool resetUserSelection = false)
        {
            if (_page.AddonResults == null)
            {
                _page.AddonResults = new ObservableCollection<StremioAddonViewModel>();
            }

            if (_page.AddonSelectorListField != null && _page.AddonSelectorListField.ItemsSource != _page.AddonResults)
            {
                _page.AddonSelectorListField.ItemsSource = _page.AddonResults;
            }

            if (_page.SourcesRepeaterField != null && _page.SourcesRepeaterField.ItemsSource != _page.VisibleSourceStreamsCollection)
            {
                _page.SourcesRepeaterField.ItemsSource = _page.VisibleSourceStreamsCollection;
            }

            if (resetUserSelection) _userHasManuallySelectedAddon = false;
            return _page.AddonResults;
        }

        /// <summary>
        /// Binds a pre-existing collection of addons (e.g., from cache).
        /// </summary>
        public void BindExisting(IEnumerable<StremioAddonViewModel> addons)
        {
            if (_disposed) return;
            _userHasManuallySelectedAddon = false;
            _page.AddonResults = new ObservableCollection<StremioAddonViewModel>(addons);
            if (_page.AddonSelectorListField != null) _page.AddonSelectorListField.ItemsSource = _page.AddonResults;
            if (_page.SourcesRepeaterField != null && _page.SourcesRepeaterField.ItemsSource != _page.VisibleSourceStreamsCollection)
            {
                _page.SourcesRepeaterField.ItemsSource = _page.VisibleSourceStreamsCollection;
            }
            QueueAddonSelectionUnderlineUpdate(false);
        }

        #endregion

        #region Loading Lifecycle

        /// <summary>
        /// Initializes the addon list with loading placeholders for each addon URL.
        /// Called before async addon fetches begin.
        /// </summary>
        /// <param name="addonUrls">List of addon base URLs to fetch from.</param>
        /// <param name="shimmerCount">Number of shimmer placeholder rows per addon.</param>
        public void InitializeLoading(IReadOnlyList<string> addonUrls, int shimmerCount)
        {
            if (_disposed) return;
            var collection = EnsureCollection(resetUserSelection: true);
            _lastSourcesShimmerCount = shimmerCount;

            for (int i = 0; i < addonUrls.Count; i++)
            {
                string addonUrl = addonUrls[i];
                var existing = collection.FirstOrDefault(a => a.AddonUrl == addonUrl);
                if (existing != null)
                {
                    existing.SortIndex = i;
                    if (existing.IsLoading && (existing.Streams == null || existing.Streams.Count == 0))
                    {
                        existing.Streams = CreatePlaceholders(shimmerCount);
                    }
                    continue;
                }

                collection.Add(new StremioAddonViewModel
                {
                    Name = "...",
                    AddonUrl = addonUrl,
                    IsLoading = true,
                    SortIndex = i,
                    Streams = CreatePlaceholders(shimmerCount)
                });
            }

            if (_page.AddonSelectorListField?.SelectedItem == null)
            {
                SelectAddon(collection.OrderBy(a => a.SortIndex).FirstOrDefault(), SourceSelectionReason.Auto);
            }
            else
            {
                RebindSelectedAddon(animate: false);
            }
        }

        /// <summary>
        /// Inserts or updates a priority addon (e.g., IPTV) at the top of the list.
        /// </summary>
        public void AddOrUpdatePriorityAddon(StremioAddonViewModel addon)
        {
            if (_disposed || addon == null) return;

            var collection = EnsureCollection();
            var existing = collection.FirstOrDefault(a => a.AddonUrl == addon.AddonUrl);
            if (existing != null)
            {
                existing.Name = addon.Name;
                existing.Streams = addon.Streams;
                existing.IsLoading = false;
                existing.SortIndex = addon.SortIndex;
            }
            else
            {
                collection.Insert(0, addon);
            }

            SelectAddon(existing ?? addon, SourceSelectionReason.Auto);
            QueueAddonSelectionUnderlineUpdateAfterLayout(true);
        }

        /// <summary>
        /// Applies the result of an addon stream fetch, replacing placeholders with real streams.
        /// </summary>
        /// <returns>The updated addon view model.</returns>
        public StremioAddonViewModel ApplyAddonResult(string addonUrl, string displayName, List<StremioStreamViewModel> streams, int sortIndex)
        {
            if (_disposed) return null;
            var collection = EnsureCollection();
            var addon = collection.FirstOrDefault(a => a.AddonUrl == addonUrl);
            if (addon == null)
            {
                addon = new StremioAddonViewModel { AddonUrl = addonUrl };
                int insertAt = 0;
                while (insertAt < collection.Count && collection[insertAt].SortIndex < sortIndex) insertAt++;
                collection.Insert(insertAt, addon);
            }

            addon.Name = displayName;
            addon.Streams = streams;
            addon.IsLoading = false;
            addon.SortIndex = sortIndex;

            if (_page.AddonSelectorListField?.SelectedItem == addon)
            {
                RebindSelectedAddon(animate: true);
            }

            AutoSelectBestLoadedAddon();
            QueueAddonSelectionUnderlineUpdateAfterLayout(true);
            return addon;
        }

        /// <summary>
        /// Removes an addon that failed to respond from the list.
        /// </summary>
        public void RemoveFailedAddon(string addonUrl)
        {
            if (_disposed || _page.AddonResults == null) return;

            var addon = _page.AddonResults.FirstOrDefault(a => a.AddonUrl == addonUrl);
            if (addon == null) return;

            bool wasSelected = _page.AddonSelectorListField?.SelectedItem == addon;
            _page.AddonResults.Remove(addon);

            if (wasSelected)
            {
                if (_userHasManuallySelectedAddon)
                {
                    SelectAddon(_page.AddonResults.OrderBy(a => a.SortIndex).FirstOrDefault(), SourceSelectionReason.Auto);
                }
                else
                {
                    AutoSelectBestLoadedAddon();
                }
            }
            ClearOrphanedPlaceholderPresentation();
            QueueAddonSelectionUnderlineUpdateAfterLayout(true);
        }

        /// <summary>
        /// Marks loading as complete by removing any remaining loading placeholders
        /// and auto-selecting the best loaded addon.
        /// </summary>
        public void CompleteLoading()
        {
            if (_disposed || _page.AddonResults == null) return;

            foreach (var addon in _page.AddonResults.Where(a => a.IsLoading).ToList())
            {
                _page.AddonResults.Remove(addon);
            }

            if (!_userHasManuallySelectedAddon)
            {
                AutoSelectBestLoadedAddon();
            }
            ClearOrphanedPlaceholderPresentation();
            QueueAddonSelectionUnderlineUpdateAfterLayout(true);
        }

        /// <summary>
        /// Updates the shimmer placeholder count for all loading addons.
        /// Called when the panel size changes and the number of visible rows needs adjustment.
        /// </summary>
        public void RefreshSelectedShimmerCount(int shimmerCount)
        {
            if (_disposed || _lastSourcesShimmerCount == shimmerCount) return;
            _lastSourcesShimmerCount = shimmerCount;
            if (_page.AddonResults == null) return;

            foreach (var addon in _page.AddonResults.Where(a => a.IsLoading).ToList())
            {
                addon.Streams = CreatePlaceholders(shimmerCount);
            }

            if (_page.AddonSelectorListField?.SelectedItem is StremioAddonViewModel selectedAddon &&
                selectedAddon.IsLoading)
            {
                RebindSelectedAddon(animate: false);
            }
        }

        #endregion

        #region Addon Selection

        /// <summary>
        /// Selects an addon and presents its streams in the SourcesRepeater.
        /// </summary>
        /// <param name="addon">The addon to select.</param>
        /// <param name="reason">Whether the selection was automatic or user-driven.</param>
        /// <param name="forceReveal">Whether to force the reveal animation even for auto selections.</param>
        public void SelectAddon(StremioAddonViewModel addon, SourceSelectionReason reason, bool forceReveal = false)
        {
            if (_disposed || addon == null || _page.AddonSelectorListField == null) return;
            if (reason == SourceSelectionReason.User) _userHasManuallySelectedAddon = true;

            _page.IsApplyingSourceSelection = true;
            try
            {
                if (_page.AddonSelectorListField.SelectedItem != addon)
                {
                    _page.AddonSelectorListField.SelectedItem = addon;
                }
            }
            finally
            {
                _page.IsApplyingSourceSelection = false;
            }

            bool shouldAnimateSelection = forceReveal || reason == SourceSelectionReason.User || addon.IsLoading;

            if (!_page.SuppressSourceSelectionUi)
            {
                RebindSelectedAddon(animate: shouldAnimateSelection);
            }
            else
            {
                ApplyVisibleStreams(addon.Streams, animate: shouldAnimateSelection);
            }
        }

        /// <summary>
        /// Handles a user-driven addon selection from the ListView.
        /// </summary>
        public void HandleUserSelection(StremioAddonViewModel addon)
        {
            if (_disposed) return;
            SelectAddon(addon, SourceSelectionReason.User);
        }

        /// <summary>
        /// Re-binds the currently selected addon's streams to the SourcesRepeater.
        /// </summary>
        public void RebindSelectedAddon(bool animate)
        {
            if (_disposed) return;
            if (_page.AddonSelectorListField?.SelectedItem is not StremioAddonViewModel addon)
            {
                UpdateAddonSelectionUnderline(false);
                return;
            }

            ApplyVisibleStreams(addon.Streams, animate);

            if (_page.SourcesPanelField?.Visibility == Visibility.Visible &&
                _page.SourcesPanelField.ActualWidth > 0 &&
                _page.AddonSelectorListField.ActualWidth > 0)
            {
                QueueAddonSelectionUnderlineUpdate(animate);
                if (addon.Streams?.Any(s => s.IsActive) == true)
                {
                    ScrollToActiveSource();
                }
            }
        }

        /// <summary>
        /// Auto-selects the best loaded addon based on sort index and stream availability.
        /// Only acts when the user has not manually selected an addon.
        /// </summary>
        public void AutoSelectBestLoadedAddon()
        {
            if (_disposed || _userHasManuallySelectedAddon || _page.AddonResults == null) return;

            var bestLoaded = _page.AddonResults
                .Where(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0)
                .OrderBy(a => a.SortIndex)
                .FirstOrDefault();

            if (bestLoaded != null)
            {
                if (_page.AddonSelectorListField?.SelectedItem == bestLoaded) return;
                SelectAddon(bestLoaded, SourceSelectionReason.Auto, forceReveal: true);
            }
            else if (_page.AddonSelectorListField?.SelectedItem == null)
            {
                SelectAddon(_page.AddonResults.OrderBy(a => a.SortIndex).FirstOrDefault(), SourceSelectionReason.Auto);
            }
        }

        /// <summary>
        /// Marks the given stream as active across all addons and clears the active flag on all others.
        /// </summary>
        public void SetActiveStream(StremioStreamViewModel activeVm)
        {
            if (_disposed || _page.AddonResults == null) return;

            foreach (var addon in _page.AddonResults)
            {
                if (addon.Streams == null) continue;
                foreach (var stream in addon.Streams)
                {
                    stream.IsActive = stream == activeVm;
                }
            }
        }

        private void ApplyVisibleStreams(IReadOnlyList<StremioStreamViewModel> streams, bool animate)
        {
            if (_disposed) return;
            PresentSourceStreams(streams, animate);
        }

        private void PresentSourceStreams(IReadOnlyList<StremioStreamViewModel> streams, bool animate)
        {
            if (_disposed) return;
            _revealState.Reset();

            var snapshot = streams?.Where(s => s != null).ToList() ?? new List<StremioStreamViewModel>();
            _revealState.PrepareOpen(snapshot.Count);

            _page.VisibleSourceStreamsCollection.Clear();
            foreach (var stream in snapshot)
            {
                _page.VisibleSourceStreamsCollection.Add(stream);
            }
        }

        private void ClearOrphanedPlaceholderPresentation()
        {
            bool selectedIsLoading = _page.AddonSelectorListField?.SelectedItem is StremioAddonViewModel selected && selected.IsLoading;
            bool onlyPlaceholdersVisible = _page.VisibleSourceStreamsCollection.Count > 0 &&
                                           _page.VisibleSourceStreamsCollection.All(s => s.IsPlaceholder);

            if (!selectedIsLoading && onlyPlaceholdersVisible)
            {
                _revealState.Reset();
                _page.VisibleSourceStreamsCollection.Clear();
            }
        }

        #endregion

        #region ItemsRepeater Event Handlers

        /// <summary>
        /// Called when the ItemsRepeater prepares a container element for display.
        /// Applies the reveal animation to items within the reveal limit.
        /// </summary>
        public void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (_disposed || args.Element is not FrameworkElement fe) return;

            StremioStreamViewModel rowViewModel = null;
            if (sender.ItemsSource is System.Collections.IList list && args.Index >= 0 && args.Index < list.Count)
            {
                rowViewModel = list[args.Index] as StremioStreamViewModel;
                fe.DataContext = rowViewModel;
            }

            int generation = _revealState.VisualGeneration;
            int preparedIndex = args.Index;
            bool isShimmer = rowViewModel?.IsPlaceholder == true;
            string stamp = $"src:{generation}:{preparedIndex}:{(isShimmer ? "shm" : "real")}";

            if (!isShimmer && fe.Tag?.ToString().Contains(":shm") == true)
            {
                _revealState.AnimatedRevealIndexes.Remove(preparedIndex);
            }

            if (Equals(fe.Tag, stamp)) return;

            fe.Tag = stamp;

            if (preparedIndex < _revealState.RevealItemLimit && !_revealState.AnimatedRevealIndexes.Contains(preparedIndex))
            {
                _revealEngine.PrepareForReveal(fe);
                _revealEngine.ApplyStaggeredReveal(fe, preparedIndex, _revealState);
                _revealState.AnimatedRevealIndexes.Add(preparedIndex);
            }
            else
            {
                _revealEngine.ResetRevealState(fe);
            }
        }

        /// <summary>
        /// Called when the ItemsRepeater is clearing a container element.
        /// Resets the element's visual state to prevent stale animations.
        /// </summary>
        public void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            if (_disposed || args.Element is not FrameworkElement fe) return;
            fe.DataContext = null;
            fe.Tag = null;
            _revealEngine.ResetRevealState(fe);
        }

        /// <summary>
        /// Handles pointer-enter on a source item (hover effect).
        /// </summary>
        public void OnSourceItemPointerEntered(object sender) => HoverAnimationHelper.OnPointerEntered(sender);

        /// <summary>
        /// Handles pointer-exit on a source item (hover effect).
        /// </summary>
        public void OnSourceItemPointerExited(object sender) => HoverAnimationHelper.OnPointerExited(sender);

        #endregion

        #region Addon Selection Underline

        /// <summary>
        /// Positions the selection underline beneath the selected addon's text.
        /// Uses implicit Composition animations for smooth movement and width changes.
        /// </summary>
        public void UpdateAddonSelectionUnderline(bool animate)
        {
            if (_disposed || _page.AddonSelectionUnderlineField == null || _page.AddonSelectorListField == null) return;

            InitializeUnderlineImplicitSystem();

            var lv = _page.AddonSelectorListField;
            if (lv.SelectedItem is not StremioAddonViewModel addon || addon.IsLoading)
            {
                _page.AddonSelectionUnderlineField.Visibility = Visibility.Collapsed;
                return;
            }

            var container = lv.ContainerFromItem(addon) as FrameworkElement;
            if (container == null || !container.IsLoaded)
            {
                if (_addonUnderlineRetryCount++ < 5) QueueAddonSelectionUnderlineUpdate(animate);
                return;
            }

            var parent = _page.AddonSelectionUnderlineField.Parent as UIElement;
            if (parent == null) return;

            var textBlock = FindFirstVisibleDescendant<TextBlock>(container);
            if (textBlock == null || textBlock.ActualWidth <= 0)
            {
                if (_addonUnderlineRetryCount++ < 5) QueueAddonSelectionUnderlineUpdate(animate);
                return;
            }

            _addonUnderlineRetryCount = 0;

            var ttv = textBlock.TransformToVisual(parent);
            var pos = ttv.TransformPoint(new Point(0, 0));

            float targetWidth = (float)textBlock.ActualWidth;
            float targetX = (float)pos.X;
            float targetY = (float)(pos.Y + textBlock.ActualHeight + 4);

            var visual = ElementCompositionPreview.GetElementVisual(_page.AddonSelectionUnderlineField);
            bool wasCollapsed = _page.AddonSelectionUnderlineField.Visibility != Visibility.Visible;

            if (wasCollapsed)
            {
                var savedAnimations = visual.ImplicitAnimations;
                visual.ImplicitAnimations = null;

                _page.AddonSelectionUnderlineField.Width = targetWidth;
                visual.Offset = new Vector3(targetX, targetY, 0);
                _page.AddonSelectionUnderlineField.Visibility = Visibility.Visible;

                visual.ImplicitAnimations = savedAnimations;
            }
            else
            {
                _page.AddonSelectionUnderlineField.Width = targetWidth;
                visual.Offset = new Vector3(targetX, targetY, 0);
            }
        }

        private void InitializeUnderlineImplicitSystem()
        {
            if (_isUnderlineImplicitInitialized || _page.AddonSelectionUnderlineField == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(_page.AddonSelectionUnderlineField);
            var compositor = visual.Compositor;

            var implicitAnimations = compositor.CreateImplicitAnimationCollection();
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.Target = "Offset";
            offsetAnim.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
            offsetAnim.Duration = TimeSpan.FromMilliseconds(450);

            var sizeAnim = compositor.CreateVector2KeyFrameAnimation();
            sizeAnim.Target = "Size";
            sizeAnim.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
            sizeAnim.Duration = TimeSpan.FromMilliseconds(450);

            implicitAnimations["Offset"] = offsetAnim;
            implicitAnimations["Size"] = sizeAnim;

            visual.ImplicitAnimations = implicitAnimations;
            _isUnderlineImplicitInitialized = true;
        }

        private void QueueAddonSelectionUnderlineUpdate(bool animate)
        {
            if (_isAddonUnderlineUpdateQueued)
            {
                _queuedAddonUnderlineAnimate |= animate;
                return;
            }

            _isAddonUnderlineUpdateQueued = true;
            _queuedAddonUnderlineAnimate = animate;
            CompositionTarget.Rendering += AddonUnderline_Rendering;
        }

        private async void QueueAddonSelectionUnderlineUpdateAfterLayout(bool animate)
        {
            if (_disposed) return;
            int version = ++_addonUnderlineSettleVersion;
            _addonUnderlineRetryCount = 0;

            _dispatcher.TryEnqueue(() =>
            {
                QueueAddonSelectionUnderlineUpdate(animate);
                _dispatcher.TryEnqueue(() => QueueAddonSelectionUnderlineUpdate(animate));
            });

            await Task.Delay(50);
            if (_disposed || version != _addonUnderlineSettleVersion) return;
            _dispatcher.TryEnqueue(() =>
            {
                if (_page.IsNavigatingAway) return;
                QueueAddonSelectionUnderlineUpdate(animate);
            });

            await Task.Delay(140);
            if (_disposed || version != _addonUnderlineSettleVersion) return;
            _dispatcher.TryEnqueue(() =>
            {
                if (_page.IsNavigatingAway) return;
                QueueAddonSelectionUnderlineUpdate(animate);
            });
        }

        private void AddonUnderline_Rendering(object sender, object e)
        {
            CompositionTarget.Rendering -= AddonUnderline_Rendering;
            bool animate = _queuedAddonUnderlineAnimate;
            _queuedAddonUnderlineAnimate = false;
            _isAddonUnderlineUpdateQueued = false;
            UpdateAddonSelectionUnderline(animate);
        }

        private static T FindFirstVisibleDescendant<T>(DependencyObject root) where T : FrameworkElement
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match && match.Visibility == Visibility.Visible && match.ActualWidth > 0) return match;
                var nested = FindFirstVisibleDescendant<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        #endregion

        #region Scroll Management

        /// <summary>
        /// Scrolls the SourcesRepeater to the active stream item.
        /// </summary>
        public void ScrollToActiveSource()
        {
            if (_disposed) return;
            _dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await Task.Delay(100);
                    if (_page.SourcesRepeaterField == null) return;

                    string activeUrl = _page.StreamUrl;
                    if (string.IsNullOrEmpty(activeUrl) && !string.IsNullOrEmpty(_page.CurrentStremioVideoId))
                    {
                        activeUrl = HistoryManager.Instance.GetProgress(_page.CurrentStremioVideoId)?.StreamUrl;
                    }

                    var activeItem = _page.VisibleSourceStreamsCollection.FirstOrDefault(s => s.Url == activeUrl);
                    if (activeItem != null)
                    {
                        _page.SourcesScrollViewerField?.ChangeView(null, 0, null);
                    }
                }
                catch { }
            });
        }

        #endregion

        #region Placeholder Creation

        private List<StremioStreamViewModel> CreatePlaceholders(int shimmerCount)
        {
            return PlaceholderFactory.CreatePlaceholders(
                shimmerCount,
                opacity => new StremioStreamViewModel
                {
                    IsPlaceholder = true,
                    ShimmerOpacity = opacity
                });
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Debug.WriteLine("[SOURCES] Disposed");
        }

        #endregion
    }

    /// <summary>
    /// Indicates why an addon was selected.
    /// </summary>
    internal enum SourceSelectionReason
    {
        /// <summary>Selected automatically by the system (e.g., first loaded, best match).</summary>
        Auto,
        /// <summary>Selected explicitly by the user tapping an addon tab.</summary>
        User
    }
}
