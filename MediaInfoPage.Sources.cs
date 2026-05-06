using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using ModernIPTVPlayer.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Helpers;
using System.Diagnostics;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage
    {
        private SourcesPanelController _sourcesPanelController;
        private bool _isApplyingSourceSelection;
        private bool _suppressSourceSelectionUi;
        private bool _isAddonUnderlineUpdateQueued;
        private bool _queuedAddonUnderlineAnimate;
        private int _addonUnderlineRetryCount;
        private int _addonUnderlineSettleVersion;
        private int _sourcesShimmerRefreshVersion;
        private int _lastSourcesShimmerCount = -1;
        private int _sourceScrollVersion;
        private int _sourcesVisualGeneration;
        private int _sourcesRevealItemLimit;
        private CancellationTokenSource? _sourcesRevealCts;
        private readonly HashSet<int> _animatedSourceRevealIndexes = new();
        private readonly ObservableCollection<StremioStreamViewModel> _visibleSourceStreams = new();
        public ObservableCollection<StremioStreamViewModel> VisibleSourceStreams => _visibleSourceStreams;
        private const int SourceRevealStaggerMs = 34;

        private enum SourceSelectionReason
        {
            Auto,
            User
        }

        private void ClearSourcesPanelState()
        {
            _sourcesPanelController?.Clear();
        }

        private void ClearSourcesPresentationState()
        {
            CancelSourcesReveal();
            _sourcesVisualGeneration++;
            _animatedSourceRevealIndexes.Clear();
            _sourcesRevealItemLimit = 0;
            _lastSourcesShimmerCount = -1;
            _visibleSourceStreams.Clear();
            if (SourcesRepeater != null && SourcesRepeater.ItemsSource != _visibleSourceStreams)
            {
                SourcesRepeater.ItemsSource = _visibleSourceStreams;
            }
        }

        private bool ShouldOpenMovieSourcesEarly(IMediaStream item)
        {
            if (item == null || IsProbablySeriesItem(item)) return false;
            if (item is LiveStream) return false;
            if (item is Models.Stremio.StremioMediaStream sms)
            {
                return !string.Equals(sms.Meta?.Type, "series", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(sms.Meta?.Type, "tv", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private bool IsProbablySeriesItem(IMediaStream item)
        {
            if (item is SeriesStream) return true;
            if (item is Models.Stremio.StremioMediaStream sms)
            {
                return string.Equals(sms.Meta?.Type, "series", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(sms.Meta?.Type, "tv", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(item.Type, "series", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(item.Type, "tv", StringComparison.OrdinalIgnoreCase);
        }

        private void PrepareEarlyMovieSourcesPanel(IMediaStream item)
        {
            if (!ShouldOpenMovieSourcesEarly(item)) return;

            var addons = Services.Stremio.StremioAddonManager.Instance.GetAddons();
            if (addons == null || addons.Count == 0) return;

            _isSourcesFetchInProgress = true;
            OpenSourcesPanel(PanelChangeReason.MovieAutoSources);
            
            _sourcesPanelController.InitializeLoading(addons, GetDynamicShimmerCount());
            
            SyncLayout();
        }

        private void SelectSourceAddon(StremioAddonViewModel addon, SourceSelectionReason reason)
        {
            _sourcesPanelController.SelectAddon(addon, reason);
        }

        private void RebindSelectedSourceAddon(bool animate)
        {
            _sourcesPanelController.RebindSelectedAddon(animate);
        }

        #region Unified Reveal Animation Engine

        /// <summary>
        /// Applies a professional staggered reveal animation to list items.
        /// Consolidates logic for both episodes and sources.
        /// </summary>
        private void ApplyStaggeredReveal(FrameworkElement fe, int index, bool useIndexDelay = true)
        {
            if (fe == null) return;

            // 1. Enable Translation Facade FIRST to prevent "Property not found" errors
            ElementCompositionPreview.SetIsTranslationEnabled(fe, true);
            
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            var compositor = visual.Compositor;
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 0.86f), new Vector2(0.16f, 1.0f));

            int delayMs = useIndexDelay ? Math.Min(Math.Max(index, 0) * SourceRevealStaggerMs, 360) : 0;

            // 2. Stop existing animations safely
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation(nameof(Visual.Offset));
            visual.StopAnimation("Translation");

            if (visual.Clip is InsetClip clip)
            {
                clip.StopAnimation(nameof(InsetClip.RightInset));
                clip.StopAnimation(nameof(InsetClip.LeftInset));
            }
            visual.Clip = null;

            // 3. Set Initial State
            visual.Opacity = 0f;
            visual.Properties.InsertVector3("Translation", new Vector3(0, 18, 0));

            // 4. Smooth Opacity Animation
            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0f, 0f);
            opacity.InsertKeyFrame(1f, 1f, easing);
            opacity.Duration = TimeSpan.FromMilliseconds(320);
            opacity.DelayTime = TimeSpan.FromMilliseconds(delayMs);

            // 2. Precise Glide Animation (Explicit Start)
            var glide = compositor.CreateVector3KeyFrameAnimation();
            glide.InsertKeyFrame(0f, new Vector3(0, 18, 0));
            glide.InsertKeyFrame(1f, Vector3.Zero, easing);
            glide.Duration = TimeSpan.FromMilliseconds(450);
            glide.DelayTime = TimeSpan.FromMilliseconds(delayMs);

            visual.StartAnimation(nameof(Visual.Opacity), opacity);
            visual.StartAnimation("Translation", glide);
        }

        private void CancelSourcesReveal()
        {
            _sourcesRevealCts?.Cancel();
            _sourcesRevealCts?.Dispose();
            _sourcesRevealCts = null;
        }

        private void PresentSourceStreams(IReadOnlyList<StremioStreamViewModel> streams, bool animate)
        {
            CancelSourcesReveal();

            _sourcesVisualGeneration++;
            _animatedSourceRevealIndexes.Clear();
            _sourceScrollVersion++;

            var snapshot = streams?.Where(s => s != null).ToList() ?? new List<StremioStreamViewModel>();
            bool containsOnlyPlaceholders = snapshot.Count > 0 && snapshot.All(s => s.IsPlaceholder);
            _sourcesRevealItemLimit = animate && !containsOnlyPlaceholders ? snapshot.Count : 0;

            if (SourcesRepeater != null && SourcesRepeater.ItemsSource != _visibleSourceStreams)
            {
                SourcesRepeater.ItemsSource = _visibleSourceStreams;
            }

            _visibleSourceStreams.Clear();

            if (snapshot.Count == 0)
            {
                return;
            }

            if (!animate)
            {
                foreach (var stream in snapshot)
                {
                    _visibleSourceStreams.Add(stream);
                }
                return;
            }

            foreach (var stream in snapshot)
            {
                _visibleSourceStreams.Add(stream);
            }
        }

        internal void ResetRevealState(FrameworkElement fe)
        {
            if (fe == null) return;
            fe.Opacity = 1.0;
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation(nameof(Visual.Offset));
            if (visual.Clip is InsetClip clip)
            {
                clip.StopAnimation(nameof(InsetClip.RightInset));
                clip.StopAnimation(nameof(InsetClip.LeftInset));
            }
            visual.Opacity = 1f;
            visual.Offset = Vector3.Zero;
            visual.Clip = null;
        }

        #endregion

        #region Addon Underline Engine (Implicit Composition)

        private bool _isUnderlineImplicitInitialized = false;

        private void InitializeUnderlineImplicitSystem()
        {
            if (_isUnderlineImplicitInitialized || AddonSelectionUnderline == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(AddonSelectionUnderline);
            var compositor = visual.Compositor;

            var implicitAnimations = compositor.CreateImplicitAnimationCollection();

            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

            // 1. Offset Animation (Movement)
            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.Target = "Offset";
            offsetAnim.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
            offsetAnim.Duration = TimeSpan.FromMilliseconds(450);

            // 2. Size Animation (Width)
            var sizeAnim = compositor.CreateVector2KeyFrameAnimation();
            sizeAnim.Target = "Size";
            sizeAnim.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
            sizeAnim.Duration = TimeSpan.FromMilliseconds(450);

            implicitAnimations["Offset"] = offsetAnim;
            implicitAnimations["Size"] = sizeAnim;

            visual.ImplicitAnimations = implicitAnimations;
            _isUnderlineImplicitInitialized = true;
        }

        private void UpdateAddonSelectionUnderline(bool animate)
        {
            if (AddonSelectionUnderline == null || AddonSelectorList == null) return;

            InitializeUnderlineImplicitSystem();

            var lv = AddonSelectorList;
            if (lv.SelectedItem is not StremioAddonViewModel addon || addon.IsLoading)
            {
                AddonSelectionUnderline.Visibility = Visibility.Collapsed;
                return;
            }

            var container = lv.ContainerFromItem(addon) as FrameworkElement;
            if (container == null || !container.IsLoaded)
            {
                if (_addonUnderlineRetryCount++ < 5) QueueAddonSelectionUnderlineUpdate(animate);
                return;
            }

            var parent = AddonSelectionUnderline.Parent as UIElement;
            if (parent == null) return;

            var textBlock = FindFirstVisibleDescendant<TextBlock>(container);
            if (textBlock == null || textBlock.ActualWidth <= 0)
            {
                if (_addonUnderlineRetryCount++ < 5) QueueAddonSelectionUnderlineUpdate(animate);
                return;
            }

            _addonUnderlineRetryCount = 0;

            // Calculate exact position relative to common parent
            var ttv = textBlock.TransformToVisual(parent);
            var pos = ttv.TransformPoint(new Windows.Foundation.Point(0, 0));

            float targetWidth = (float)textBlock.ActualWidth;
            float targetX = (float)pos.X;
            float targetY = (float)(pos.Y + textBlock.ActualHeight + 4); // Positioned 4px below text for better spacing

            var visual = ElementCompositionPreview.GetElementVisual(AddonSelectionUnderline);
            bool wasCollapsed = AddonSelectionUnderline.Visibility != Visibility.Visible;
            
            if (wasCollapsed)
            {
                var savedAnimations = visual.ImplicitAnimations;
                visual.ImplicitAnimations = null;

                AddonSelectionUnderline.Width = targetWidth;
                visual.Offset = new Vector3(targetX, targetY, 0);
                AddonSelectionUnderline.Visibility = Visibility.Visible;

                visual.ImplicitAnimations = savedAnimations;
            }
            else
            {
                AddonSelectionUnderline.Width = targetWidth;
                visual.Offset = new Vector3(targetX, targetY, 0);
            }
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
            int version = ++_addonUnderlineSettleVersion;
            _addonUnderlineRetryCount = 0;

            DispatcherQueue.TryEnqueue(() =>
            {
                QueueAddonSelectionUnderlineUpdate(animate);
                DispatcherQueue.TryEnqueue(() => QueueAddonSelectionUnderlineUpdate(animate));
            });

            await Task.Delay(50);
            if (version != _addonUnderlineSettleVersion) return;
            DispatcherQueue.TryEnqueue(() => QueueAddonSelectionUnderlineUpdate(animate));

            await Task.Delay(140);
            if (version != _addonUnderlineSettleVersion) return;
            DispatcherQueue.TryEnqueue(() => QueueAddonSelectionUnderlineUpdate(animate));
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

        private sealed class SourcesPanelController
        {
            private readonly MediaInfoPage _owner;
            private bool _userHasManuallySelectedAddon;

            public SourcesPanelController(MediaInfoPage owner)
            {
                _owner = owner;
            }

            public void Clear()
            {
                _userHasManuallySelectedAddon = false;
                _owner._lastSourcesShimmerCount = -1;
                _owner.CancelSourcesReveal();
                _owner._sourcesVisualGeneration++;
                _owner._visibleSourceStreams.Clear();
                _owner._addonResults?.Clear();

                if (_owner.SourcesRepeater != null && _owner.SourcesRepeater.ItemsSource != _owner._visibleSourceStreams)
                {
                    _owner.SourcesRepeater.ItemsSource = _owner._visibleSourceStreams;
                }
                _owner.ResetSourcesListTransitionState();
                if (_owner.AddonSelectorList != null)
                {
                    _owner._isApplyingSourceSelection = true;
                    try
                    {
                        _owner.AddonSelectorList.SelectedItem = null;
                        _owner.AddonSelectorList.ItemsSource = null;
                    }
                    finally
                    {
                        _owner._isApplyingSourceSelection = false;
                    }
                }
                _owner.UpdateAddonSelectionUnderline(false);
            }

            public ObservableCollection<StremioAddonViewModel> EnsureCollection(bool resetUserSelection = false)
            {
                if (_owner._addonResults == null)
                {
                    _owner._addonResults = new ObservableCollection<StremioAddonViewModel>();
                }

                if (_owner.AddonSelectorList != null && _owner.AddonSelectorList.ItemsSource != _owner._addonResults)
                {
                    _owner.AddonSelectorList.ItemsSource = _owner._addonResults;
                }

                if (_owner.SourcesRepeater != null && _owner.SourcesRepeater.ItemsSource != _owner._visibleSourceStreams)
                {
                    _owner.SourcesRepeater.ItemsSource = _owner._visibleSourceStreams;
                }

                if (resetUserSelection) _userHasManuallySelectedAddon = false;
                return _owner._addonResults;
            }

            public void BindExisting(IEnumerable<StremioAddonViewModel> addons)
            {
                _userHasManuallySelectedAddon = false;
                _owner._addonResults = new ObservableCollection<StremioAddonViewModel>(addons);
                if (_owner.AddonSelectorList != null) _owner.AddonSelectorList.ItemsSource = _owner._addonResults;
                if (_owner.SourcesRepeater != null && _owner.SourcesRepeater.ItemsSource != _owner._visibleSourceStreams)
                {
                    _owner.SourcesRepeater.ItemsSource = _owner._visibleSourceStreams;
                }
                _owner.QueueAddonSelectionUnderlineUpdate(false);
            }

            public void InitializeLoading(IReadOnlyList<string> addonUrls, int shimmerCount)
            {
                var collection = EnsureCollection(resetUserSelection: true);
                _owner._lastSourcesShimmerCount = shimmerCount;

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
                        Name = "...", // Cleaner placeholder
                        AddonUrl = addonUrl,
                        IsLoading = true,
                        SortIndex = i,
                        Streams = CreatePlaceholders(shimmerCount)
                    });
                }

                if (_owner.AddonSelectorList?.SelectedItem == null)
                {
                    SelectAddon(collection.OrderBy(a => a.SortIndex).FirstOrDefault(), SourceSelectionReason.Auto);
                }
                else
                {
                    RebindSelectedAddon(animate: false);
                }
            }

            public void AddOrUpdatePriorityAddon(StremioAddonViewModel addon)
            {
                if (addon == null) return;

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
                _owner.QueueAddonSelectionUnderlineUpdateAfterLayout(true);
            }

            public StremioAddonViewModel ApplyAddonResult(string addonUrl, string displayName, List<StremioStreamViewModel> streams, int sortIndex)
            {
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

                if (_owner.AddonSelectorList?.SelectedItem == addon)
                {
                    RebindSelectedAddon(animate: true);
                }

                AutoSelectBestLoadedAddon();
                _owner.QueueAddonSelectionUnderlineUpdateAfterLayout(true);
                return addon;
            }

            public void RemoveFailedAddon(string addonUrl)
            {
                if (_owner._addonResults == null) return;

                var addon = _owner._addonResults.FirstOrDefault(a => a.AddonUrl == addonUrl);
                if (addon == null) return;

                bool wasSelected = _owner.AddonSelectorList?.SelectedItem == addon;
                _owner._addonResults.Remove(addon);

                if (wasSelected)
                {
                    if (_userHasManuallySelectedAddon)
                    {
                        SelectAddon(_owner._addonResults.OrderBy(a => a.SortIndex).FirstOrDefault(), SourceSelectionReason.Auto);
                    }
                    else
                    {
                        AutoSelectBestLoadedAddon();
                    }
                }
                ClearOrphanedPlaceholderPresentation();
                _owner.QueueAddonSelectionUnderlineUpdateAfterLayout(true);
            }

            public void CompleteLoading()
            {
                if (_owner._addonResults == null) return;

                foreach (var addon in _owner._addonResults.Where(a => a.IsLoading).ToList())
                {
                    _owner._addonResults.Remove(addon);
                }

                if (!_userHasManuallySelectedAddon)
                {
                    AutoSelectBestLoadedAddon();
                }
                ClearOrphanedPlaceholderPresentation();
                _owner.QueueAddonSelectionUnderlineUpdateAfterLayout(true);
            }

            public void RefreshSelectedShimmerCount(int shimmerCount)
            {
                if (_owner._lastSourcesShimmerCount == shimmerCount) return;
                _owner._lastSourcesShimmerCount = shimmerCount;
                if (_owner._addonResults == null) return;

                foreach (var addon in _owner._addonResults.Where(a => a.IsLoading).ToList())
                {
                    addon.Streams = CreatePlaceholders(shimmerCount);
                }

                if (_owner.AddonSelectorList?.SelectedItem is StremioAddonViewModel selectedAddon &&
                    selectedAddon.IsLoading)
                {
                    RebindSelectedAddon(animate: false);
                }
            }

            public void SelectAddon(StremioAddonViewModel addon, SourceSelectionReason reason, bool forceReveal = false)
            {
                if (addon == null || _owner.AddonSelectorList == null) return;
                if (reason == SourceSelectionReason.User) _userHasManuallySelectedAddon = true;

                _owner._isApplyingSourceSelection = true;
                try
                {
                    if (_owner.AddonSelectorList.SelectedItem != addon)
                    {
                        _owner.AddonSelectorList.SelectedItem = addon;
                    }
                }
                finally
                {
                    _owner._isApplyingSourceSelection = false;
                }

                bool shouldAnimateSelection = forceReveal || reason == SourceSelectionReason.User || addon.IsLoading;

                if (!_owner._suppressSourceSelectionUi)
                {
                    RebindSelectedAddon(animate: shouldAnimateSelection);
                }
                else
                {
                    ApplyVisibleStreams(addon.Streams, animate: shouldAnimateSelection);
                }
            }

            public void HandleUserSelection(StremioAddonViewModel addon)
            {
                SelectAddon(addon, SourceSelectionReason.User);
            }

            public void RebindSelectedAddon(bool animate)
            {
                if (_owner.AddonSelectorList?.SelectedItem is not StremioAddonViewModel addon)
                {
                    _owner.UpdateAddonSelectionUnderline(false);
                    return;
                }

                ApplyVisibleStreams(addon.Streams, animate);

                if (_owner.SourcesPanel?.Visibility == Visibility.Visible &&
                    _owner.SourcesPanel.ActualWidth > 0 &&
                    _owner.AddonSelectorList.ActualWidth > 0)
                {
                    _owner.QueueAddonSelectionUnderlineUpdate(animate);
                    if (addon.Streams?.Any(s => s.IsActive) == true)
                    {
                        _owner.ScrollToActiveSource();
                    }
                }
            }

            private void ApplyVisibleStreams(IReadOnlyList<StremioStreamViewModel> streams, bool animate)
            {
                _owner.PresentSourceStreams(streams, animate);
            }

            public void AutoSelectBestLoadedAddon()
            {
                if (_userHasManuallySelectedAddon || _owner._addonResults == null) return;

                var bestLoaded = _owner._addonResults
                    .Where(a => !a.IsLoading && a.Streams != null && a.Streams.Count > 0)
                    .OrderBy(a => a.SortIndex)
                    .FirstOrDefault();

                if (bestLoaded != null)
                {
                    if (_owner.AddonSelectorList?.SelectedItem == bestLoaded)
                    {
                        return;
                    }

                    SelectAddon(bestLoaded, SourceSelectionReason.Auto, forceReveal: true);
                }
                else if (_owner.AddonSelectorList?.SelectedItem == null)
                {
                    SelectAddon(_owner._addonResults.OrderBy(a => a.SortIndex).FirstOrDefault(), SourceSelectionReason.Auto);
                }
            }

            private void ClearOrphanedPlaceholderPresentation()
            {
                bool selectedIsLoading = _owner.AddonSelectorList?.SelectedItem is StremioAddonViewModel selected && selected.IsLoading;
                bool onlyPlaceholdersVisible = _owner._visibleSourceStreams.Count > 0 &&
                                               _owner._visibleSourceStreams.All(s => s.IsPlaceholder);

                if (!selectedIsLoading && onlyPlaceholdersVisible)
                {
                    _owner.ClearSourcesPresentationState();
                }
            }

            public void SetActiveStream(StremioStreamViewModel activeVm)
            {
                if (_owner._addonResults == null) return;

                foreach (var addon in _owner._addonResults)
                {
                    if (addon.Streams == null) continue;
                    foreach (var stream in addon.Streams)
                    {
                        stream.IsActive = stream == activeVm;
                    }
                }
            }

            private List<StremioStreamViewModel> CreatePlaceholders(int shimmerCount)
            {
                var list = new List<StremioStreamViewModel>();
                foreach (var opacity in _owner.GenerateShimmerOpacitySequence(shimmerCount))
                {
                    list.Add(new StremioStreamViewModel
                    {
                        IsPlaceholder = true,
                        ShimmerOpacity = opacity
                    });
                }
                return list;
            }
        }
        #region Event Handlers & Lifecycle

        private void SourcesRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (args.Element is FrameworkElement fe)
            {
                StremioStreamViewModel? rowViewModel = null;
                if (sender.ItemsSource is System.Collections.IList list && args.Index >= 0 && args.Index < list.Count)
                {
                    rowViewModel = list[args.Index] as StremioStreamViewModel;
                    fe.DataContext = rowViewModel;
                }

                int generation = _sourcesVisualGeneration;
                int preparedIndex = args.Index;
                string stamp = $"src:{generation}:{preparedIndex}";
                bool isPlaceholder = rowViewModel?.IsPlaceholder == true;

                // Only reveal if it's within the limit and hasn't been animated in this generation
                bool shouldReveal = preparedIndex >= 0 &&
                                    preparedIndex < _sourcesRevealItemLimit &&
                                    !isPlaceholder &&
                                    !_animatedSourceRevealIndexes.Contains(preparedIndex);

                if (Equals(fe.Tag, stamp)) return;
                
                if (!shouldReveal)
                {
                    ResetRevealState(fe);
                    fe.Tag = stamp;
                    return;
                }

                fe.Tag = stamp;
                _animatedSourceRevealIndexes.Add(preparedIndex);
                ApplyStaggeredReveal(fe, args.Index, useIndexDelay: true);
            }
        }

        private void SourcesRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            if (args.Element is FrameworkElement fe)
            {
                fe.DataContext = null;
                fe.Tag = null;
                ResetRevealState(fe);
            }
        }

        private void SourceItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var hoverBorder = fe.FindName("HoverBorder") as Border;
                if (hoverBorder != null) AnimateOpacity(hoverBorder, 0.1, TimeSpan.FromMilliseconds(200));
            }
        }

        private void SourceItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var hoverBorder = fe.FindName("HoverBorder") as Border;
                if (hoverBorder != null) AnimateOpacity(hoverBorder, 0.0, TimeSpan.FromMilliseconds(200));
            }
        }

        private async void SourceItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is StremioStreamViewModel vm)
            {
                if (vm.IsPlaceholder) return;

                _sourcesPanelController.SetActiveStream(vm);
                string playUrl = await UrlResolver.ResolveUrlAsync(vm.Url);
                _streamUrl = playUrl;
                _sourceAddonUrl = vm.AddonUrl;
                if (!string.IsNullOrEmpty(_streamUrl)) _ = UpdateTechnicalBadgesAsync(_streamUrl);
                
                string title = _selectedEpisode?.Title ?? _item.Title;
                string videoId = ResolveBestContentId(_selectedEpisode?.Id ?? (_item as Models.Stremio.StremioMediaStream)?.Meta?.Id);

                if (!string.IsNullOrEmpty(vm.Url))
                {
                    var history = HistoryManager.Instance.GetProgress(videoId);
                    HistoryManager.Instance.UpdateProgress(videoId, title, _streamUrl, history?.Position ?? 0, history?.Duration ?? 0);
                    
                    // Logic for handover/play
                    await HandleSourcePlaybackHandoff(playUrl, title, videoId);
                }
                else if (!string.IsNullOrEmpty(vm.Externalurl))
                {
                    _ = Windows.System.Launcher.LaunchUriAsync(new Uri(vm.Externalurl));
                }
                else if (vm.OriginalStream != null && !string.IsNullOrEmpty(vm.OriginalStream.Infohash))
                {
                    var tip = new TeachingTip { Title = "Torrent Bilgisi", Subtitle = "Torrent akışları yakında desteklenecek. Lütfen HTTP kaynaklarını kullanın.", IsLightDismissEnabled = true };
                    tip.XamlRoot = this.XamlRoot;
                    tip.IsOpen = true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Stremio] Clicked item with no URL or Infohash: {vm.Title}");
                }
            }
        }

        private void AddonSelectorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListView lv && lv.SelectedItem is StremioAddonViewModel addon)
            {
                if (_isApplyingSourceSelection) return;
                _sourcesPanelController.HandleUserSelection(addon);
                SyncLayout();
                ScrollToActiveSource();
            }
        }

        private void ScrollToActiveSource()
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await Task.Delay(100);
                    if (SourcesRepeater == null) return;

                    string activeUrl = _streamUrl;
                    if (string.IsNullOrEmpty(activeUrl) && !string.IsNullOrEmpty(_currentStremioVideoId))
                    {
                        activeUrl = HistoryManager.Instance.GetProgress(_currentStremioVideoId)?.StreamUrl;
                    }

                    if (_visibleSourceStreams == null) return;
                    var activeItem = _visibleSourceStreams.FirstOrDefault(s => s.Url == activeUrl);
                    if (activeItem != null)
                    {
                        // ItemsRepeater scrolling is specialized
                        SourcesScrollViewer?.ChangeView(null, 0, null); // Simplified for now
                    }
                }
                catch { }
            });
        }

        private void ToggleSourcesLoading(bool isLoading)
        {
            // Shimmer handled via placeholders
        }

        internal int GetDynamicShimmerCount()
        {
            try
            {
                double height = SourcesScrollViewer?.ActualHeight ?? 0;
                if (height <= 0 && SourcesPanel != null)
                {
                    height = SourcesPanel.ActualHeight - 64 - 54 - 36;
                }

                return CalculateSkeletonCount(height, 92.0);
            }
            catch { return 8; }
        }

        private void RefreshAllShimmers()
        {
            if (_requestedPanelMode == MediaDetailPanelMode.None &&
                (SourcesPanel == null || SourcesPanel.Visibility != Visibility.Visible) &&
                (EpisodesPanel == null || EpisodesPanel.Visibility != Visibility.Visible)) return;

            int version = ++_sourcesShimmerRefreshVersion;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (version != _sourcesShimmerRefreshVersion) return;
                int count = GetDynamicShimmerCount();
                
                // 1. Sources Panel Refresh
                _sourcesPanelController?.RefreshSelectedShimmerCount(count);
                
                // 2. Episodes Panel Refresh
                if (_isEpisodesLoading)
                {
                    var placeholders = CreateEpisodePlaceholders(count);
                    // Only update if count changed or currently empty to avoid flicker
                    if (CurrentEpisodes.Count != count || (count > 0 && CurrentEpisodes.Any(e => !e.IsPlaceholder)))
                    {
                        CurrentEpisodes.Clear();
                        foreach (var p in placeholders) CurrentEpisodes.Add(p);
                    }
                }
                
                QueueAddonSelectionUnderlineUpdateAfterLayout(false);
            });
        }

        internal void ResetSourcesListTransitionState()
        {
            CancelSourcesReveal();
            _sourcesVisualGeneration++;
            _animatedSourceRevealIndexes.Clear();
        }

        #endregion

        private async Task HandleSourcePlaybackHandoff(string playUrl, string title, string videoId)
        {
            if (string.IsNullOrEmpty(playUrl)) return;

            // 1. Technical Badges Update
            _ = UpdateTechnicalBadgesAsync(playUrl);

            // 2. Progress & History Update
            var history = HistoryManager.Instance.GetProgress(videoId);
            double resumeSeconds = (history != null && !history.IsFinished && history.Position > 0) ? history.Position : -1;

            // 3. Determine Stream Type for metadata
            string streamType = "movie";
            string parentIdStr = null;
            if (_item is SeriesStream ss)
            {
                streamType = "series";
                parentIdStr = ss.SeriesId.ToString();
            }
            else if (_item is Models.Stremio.StremioMediaStream stType && (stType.Meta.Type == "series" || stType.Meta.Type == "tv"))
            {
                streamType = "series";
                parentIdStr = stType.Meta.Id;
            }

            // 4. PLAYER REUSE & HANDOVER LOGIC
            bool hasExistingPlayer = MediaInfoPlayer != null;
            string currentPlayerPath = null;

            if (hasExistingPlayer)
            {
                try { currentPlayerPath = await MediaInfoPlayer.GetPropertyAsync("path"); } catch { }
            }

            bool isSameSource = hasExistingPlayer 
                && !string.IsNullOrEmpty(currentPlayerPath) 
                && currentPlayerPath != "N/A"
                && (currentPlayerPath == playUrl || currentPlayerPath.Contains(playUrl));

            if (isSameSource)
            {
                // CASE 1: Same source — use prebuffered player via handoff for instant start
                await PerformHandoverAndNavigate(playUrl, title, videoId, parentIdStr, null, 
                    _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, 
                    resumeSeconds, _item.PosterUrl, streamType, GetCurrentBackdrop());
            }
            else if (hasExistingPlayer && !string.IsNullOrEmpty(currentPlayerPath) && currentPlayerPath != "N/A")
            {
                // CASE 2: Different source — reuse existing player, switch URL
                try
                {
                    // Cancel prebuffering
                    _prebufferCts?.Cancel(); _prebufferCts?.Dispose(); _prebufferCts = null;
                    
                    // Force visual sync before load
                    await MediaInfoPlayer.SetPropertyAsync("keep-open", "yes");
                    await MediaInfoPlayer.SetPropertyAsync("force-window", "yes");
                    
                    // Configure for new URL (cookies/headers may differ)
                    await MpvSetupHelper.ConfigurePlayerAsync(MediaInfoPlayer, playUrl, isSecondary: true);
                    
                    // Switch to new source
                    await MediaInfoPlayer.SetPropertyAsync("pause", "yes");
                    
                    if (resumeSeconds > 0)
                        await MediaInfoPlayer.SetPropertyAsync("start", resumeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    else
                        await MediaInfoPlayer.SetPropertyAsync("start", "0");

                    await MediaInfoPlayer.OpenAsync(playUrl);
                    await MediaInfoPlayer.SetPropertyAsync("mute", "yes");

                    await PerformHandoverAndNavigate(playUrl, title, videoId, parentIdStr, null, 
                        _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, 
                        resumeSeconds, _item.PosterUrl, streamType, GetCurrentBackdrop());
                }
                catch { await PerformHandoverAndNavigate(playUrl, title, videoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, resumeSeconds, _item.PosterUrl, streamType, GetCurrentBackdrop()); }
            }
            else
            {
                // CASE 3: No existing player — fresh navigation
                await PerformHandoverAndNavigate(playUrl, title, videoId, parentIdStr, null, _selectedEpisode?.SeasonNumber ?? 0, _selectedEpisode?.EpisodeNumber ?? 0, resumeSeconds, _item.PosterUrl, streamType, GetCurrentBackdrop());
            }
        }
    }
}
