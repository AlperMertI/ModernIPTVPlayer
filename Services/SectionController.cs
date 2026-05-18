using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Represents the lifecycle state of a UI section.
    /// </summary>
    internal enum SectionState
    {
        NotReady,
        SkeletonVisible,
        ContentRevealing,
        ContentVisible
    }

    /// <summary>
    /// Manages the lifecycle and animations of a single UI section.
    /// Each section operates independently — no coordination with other sections.
    /// </summary>
    internal sealed class SectionController : IDisposable
    {
        #region Static Batching

        private static readonly List<SectionController> s_pendingReveals = new();
        private static DispatcherQueue s_dispatcher;
        private static bool s_batchPending;
        private static readonly object s_batchLock = new();
        private static AnimationCoordinator s_animationCoordinator;

        internal static void SetDispatcher(DispatcherQueue dispatcher)
        {
            s_dispatcher = dispatcher;
        }

        internal static void SetAnimationCoordinator(AnimationCoordinator coordinator)
        {
            s_animationCoordinator = coordinator;
        }

        internal static void FlushPendingReveals()
        {
            lock (s_batchLock)
            {
                if (s_pendingReveals.Count == 0) return;

                var batch = new List<SectionController>(s_pendingReveals);
                s_pendingReveals.Clear();
                s_batchPending = false;

                if (s_dispatcher == null) return;

                var sorted = batch.FindAll(s => s._panel != null);
                sorted.Sort((a, b) => a.DisplayOrder.CompareTo(b.DisplayOrder));

                for (int i = 0; i < sorted.Count; i++)
                {
                    var section = sorted[i];
                    int delay = i * 40;

                    _ = Task.Run(async () =>
                    {
                        if (delay > 0) await Task.Delay(delay);

                        s_dispatcher.TryEnqueue(() =>
                        {
                            try
                            {
                                section.ExecuteCrossfade();
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[SECTION] {section.Name} crossfade failed: {ex.Message}");
                                s_animationCoordinator?.Fail($"crossfade:{section.Name}", ex.Message);
                            }
                        });
                    });
                }
            }
        }

        #endregion

        #region Fields

        private readonly FrameworkElement _panel;
        private readonly FrameworkElement _content;
        private readonly FrameworkElement _skeleton;
        private readonly Compositor _compositor;
        private SectionState _state;
        private bool _disposed;
        private CompositionScopedBatch _activeAnimationBatch;
        private bool _hideCancelled;

        #endregion

        #region Properties

        public string Name { get; }
        public int DisplayOrder { get; set; }
        public SectionState State => _state;
        public bool IsContentLoaded => _state == SectionState.ContentVisible;
        public bool IsShowingSkeleton => _state == SectionState.SkeletonVisible;

        #endregion

        #region Constructor

        public SectionController(string name, FrameworkElement panel, FrameworkElement content,
            FrameworkElement skeleton, Compositor compositor, int displayOrder)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _panel = panel ?? throw new ArgumentNullException(nameof(panel));
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _skeleton = skeleton;
            _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
            DisplayOrder = displayOrder;
            _state = SectionState.NotReady;

            CompositionService.EnableTranslation(_panel);
        }

        #endregion

        #region Panel Slide-In (Phase 1)

        /// <summary>
        /// Animates the panel sliding in from off-screen.
        /// Skeletons are expected to be visible inside the panel already.
        /// </summary>
        public void ShowPanelAnimated()
        {
            ThrowIfDisposed();

            try
            {
                Debug.WriteLine($"[SECTION] {Name} ShowPanelAnimated entry. Visibility={_panel.Visibility}, Opacity={_panel.Opacity}");
                if (_panel.Visibility == Visibility.Visible && _panel.Opacity >= 0.99)
                    return;

                CancelActiveAnimation();

                _panel.Visibility = Visibility.Visible;
                CompositionService.EnableTranslation(_panel);

                CompositionService.Run(_panel, visual =>
                {
                    CompositionService.StopAll(visual);

                    visual.Opacity = 0f;
                    try
                    {
                        visual.Properties.InsertVector3("Translation", new Vector3(-800, 0, 0));
                    }
                    catch
                    {
                        visual.Offset = new Vector3(-800, 0, 0);
                    }

                    var compositor = visual.Compositor;
                    var spring = compositor.CreateSpringVector3Animation();
                    spring.FinalValue = Vector3.Zero;
                    spring.DampingRatio = 0.75f;
                    spring.Period = TimeSpan.FromMilliseconds(120);

                    try
                    {
                        visual.StartAnimation("Translation", spring);
                    }
                    catch
                    {
                        try
                        {
                            visual.StartAnimation(CompositionService.OffsetProperty, spring);
                        }
                        catch { }
                    }

                    var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(0f, 0f);
                    fadeIn.InsertKeyFrame(1f, 1f);
                    fadeIn.Duration = TimeSpan.FromMilliseconds(400);

                    try
                    {
                        visual.StartAnimation(CompositionService.OpacityProperty, fadeIn);
                    }
                    catch { }
                });

                _state = SectionState.SkeletonVisible;
                Debug.WriteLine($"[SECTION] {Name} panel slide-in started");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SECTION] {Name} ShowPanelAnimated failed: {ex.Message}");
            }
        }

        #endregion

        #region Content Reveal (Phase 3)

        private void CancelActiveAnimation()
        {
            _hideCancelled = true;
            _activeAnimationBatch?.Dispose();
            _activeAnimationBatch = null;
            CompositionService.StopAllAnimationsImmediately(_panel);
        }

        /// <summary>
        /// Schedules this section for content crossfade via micro-batching.
        /// Sections arriving within 50ms are grouped and staggered at 40ms intervals.
        /// </summary>
        public void RevealContent()
        {
            ThrowIfDisposed();

            if (_state == SectionState.ContentVisible)
                return;

            if (_skeleton == null)
            {
                ShowContentImmediate();
                return;
            }

            lock (s_batchLock)
            {
                _state = SectionState.ContentRevealing;
                s_pendingReveals.Add(this);

                if (!s_batchPending)
                {
                    s_batchPending = true;

                    s_dispatcher?.TryEnqueue(async () =>
                    {
                        await Task.Delay(50);
                        FlushPendingReveals();
                    });
                }
            }

            Debug.WriteLine($"[SECTION] {Name} reveal content scheduled");
        }

        /// <summary>
        /// Reveals content immediately without animation.
        /// Used for sections without a skeleton element.
        /// </summary>
        public void ShowContentImmediate()
        {
            ThrowIfDisposed();

            try
            {
                Debug.WriteLine($"[SECTION] {Name} ShowContentImmediate entry. Vis={_panel.Visibility}, Opacity={_panel.Opacity}, State={_state}");

                CancelActiveAnimation();

                CompositionService.Run(_panel, visual =>
                {
                    CompositionService.StopAll(visual);
                    try { visual.StopAnimation("Translation"); } catch { }
                    visual.Opacity = 1f;
                    Debug.WriteLine($"[SECTION] {Name} ShowContentImmediate composition reset. Visual Opacity set to 1f");
                });
                CompositionService.ResetVisual(_panel);
                _panel.Visibility = Visibility.Visible;
                _panel.Opacity = 1;

                if (_content != null)
                {
                    CompositionService.Run(_content, visual =>
                    {
                        CompositionService.StopAll(visual);
                        visual.Opacity = 1f;
                    });
                    CompositionService.ResetVisual(_content);
                    _content.Opacity = 1;
                    _content.Visibility = Visibility.Visible;
                }

                if (_skeleton != null)
                {
                    _skeleton.Visibility = Visibility.Collapsed;
                    _skeleton.Opacity = 0;
                }

                _state = SectionState.ContentVisible;
                Debug.WriteLine($"[SECTION] {Name} content revealed immediately. PanelOpacity={_panel.Opacity}, PanelVis={_panel.Visibility}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SECTION] {Name} ShowContentImmediate failed: {ex.Message}");
            }
        }

        private void ExecuteCrossfade()
        {
            if (_content == null || _skeleton == null)
            {
                ShowContentImmediate();
                return;
            }

            var animKey = $"crossfade:{Name}";
            var tcs = s_animationCoordinator?.Register(animKey, $"{Name} crossfade");

            try
            {
                CompositionService.ResetVisual(_panel);
                _panel.Visibility = Visibility.Visible;
                _panel.Opacity = 1;

                if (_content != null)
                {
                    _content.Visibility = Visibility.Visible;
                }

                var easing = _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.16f, 0.86f), new Vector2(0.16f, 1.0f));

                CompositionService.Run(_content, visual =>
                {
                    CompositionService.StopAll(visual);

                    var fade = _compositor.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(0f, 0f);
                    fade.InsertKeyFrame(1f, 1f, easing);
                    fade.Duration = TimeSpan.FromMilliseconds(300);

                    try
                    {
                        visual.StartAnimation(CompositionService.OpacityProperty, fade);
                    }
                    catch { }
                });

                CompositionService.Run(_skeleton, visual =>
                {
                    CompositionService.StopAll(visual);

                    var fade = _compositor.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(0f, 1f);
                    fade.InsertKeyFrame(1f, 0f, easing);
                    fade.Duration = TimeSpan.FromMilliseconds(300);

                    try
                    {
                        visual.StartAnimation(CompositionService.OpacityProperty, fade);
                    }
                    catch { }
                });

                _ = Task.Run(async () =>
                {
                    await Task.Delay(300);

                    s_dispatcher?.TryEnqueue(() =>
                    {
                        try
                        {
                            _skeleton.Visibility = Visibility.Collapsed;
                            _content.Opacity = 1;
                            _state = SectionState.ContentVisible;
                            Debug.WriteLine($"[SECTION] {Name} crossfade complete");
                            s_animationCoordinator?.Complete(animKey);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SECTION] {Name} crossfade finalize failed: {ex.Message}");
                            s_animationCoordinator?.Fail(animKey, ex.Message);
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SECTION] {Name} ExecuteCrossfade failed: {ex.Message}");
                s_animationCoordinator?.Fail(animKey, ex.Message);
            }
        }

        #endregion

        #region Dismiss

        /// <summary>
        /// Dismisses the panel with a slide-out animation.
        /// </summary>
        public void Hide()
        {
            ThrowIfDisposed();

            if (_state == SectionState.NotReady)
            {
                Debug.WriteLine($"[SECTION] {Name} Hide skipped (already NotReady)");
                return;
            }

            Debug.WriteLine($"[SECTION] {Name} Hide entry. State={_state}, Vis={_panel.Visibility}, Opacity={_panel.Opacity}");

            try
            {
                _hideCancelled = false;
                _state = SectionState.NotReady;

                CompositionService.Run(_panel, visual =>
                {
                    CompositionService.StopAll(visual);
                    try { visual.StopAnimation("Translation"); } catch { }

                    visual.Opacity = 1f;
                    try
                    {
                        visual.Properties.InsertVector3("Translation", Vector3.Zero);
                    }
                    catch { }

                    var compositor = visual.Compositor;
                    var slideOut = compositor.CreateVector3KeyFrameAnimation();
                    slideOut.InsertKeyFrame(1f, new Vector3(800, 0, 0),
                        compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(0.2f, 1f)));
                    slideOut.Duration = TimeSpan.FromMilliseconds(350);

                    try
                    {
                        visual.StartAnimation("Translation", slideOut);
                    }
                    catch
                    {
                        try
                        {
                            visual.StartAnimation(CompositionService.OffsetProperty, slideOut);
                        }
                        catch { }
                    }

                    var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                    fadeOut.InsertKeyFrame(1f, 0f);
                    fadeOut.Duration = TimeSpan.FromMilliseconds(300);

                    try
                    {
                        visual.StartAnimation(CompositionService.OpacityProperty, fadeOut);
                    }
                    catch { }
                });

                var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                CompositionService.Run(_panel, visual => { });
                batch.End();

                _activeAnimationBatch = batch;
                batch.Completed += (s, e) =>
                {
                    _activeAnimationBatch = null;

                    if (_hideCancelled)
                    {
                        Debug.WriteLine($"[SECTION] {Name} hide cancelled (animation interrupted)");
                        return;
                    }

                    s_dispatcher?.TryEnqueue(() =>
                    {
                        try
                        {
                            if (_state != SectionState.NotReady)
                            {
                                Debug.WriteLine($"[SECTION] {Name} hide cancelled (state changed to {_state})");
                                return;
                            }

                             Debug.WriteLine($"[SECTION] {Name} hide finalize. Vis={_panel.Visibility}, Opacity={_panel.Opacity}");
                             CompositionService.StopAllAnimationsImmediately(_panel);
                             _panel.Visibility = Visibility.Collapsed;
                             _panel.Opacity = 1;
                            Debug.WriteLine($"[SECTION] {Name} panel dismissed. Vis={_panel.Visibility}, Opacity={_panel.Opacity}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SECTION] {Name} dismiss finalize failed: {ex.Message}");
                        }
                    });
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SECTION] {Name} Hide failed: {ex.Message}");
            }
        }

        #endregion

        #region Skeleton Management

        /// <summary>
        /// Ensures the skeleton is visible and content is hidden.
        /// </summary>
        public void ShowSkeleton()
        {
            ThrowIfDisposed();

            if (_skeleton == null)
            {
                ShowContentImmediate();
                return;
            }

            try
            {
                CancelActiveAnimation();

                CompositionService.Run(_panel, visual =>
                {
                    CompositionService.StopAll(visual);
                    try { visual.StopAnimation("Translation"); } catch { }
                    visual.Opacity = 1f;
                });
                CompositionService.ResetVisual(_panel);
                _panel.Visibility = Visibility.Visible;
                _panel.Opacity = 1;

                if (_content != null)
                {
                    CompositionService.Run(_content, visual =>
                    {
                        CompositionService.StopAll(visual);
                        visual.Opacity = 0f;
                    });
                    _content.Visibility = Visibility.Visible;
                    _content.Opacity = 0;
                }
                if (_skeleton != null)
                {
                    _skeleton.Opacity = 1;
                    _skeleton.Visibility = Visibility.Visible;
                }

                _state = SectionState.SkeletonVisible;
                Debug.WriteLine($"[SECTION] {Name} ShowSkeleton. PanelOpacity={_panel.Opacity}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SECTION] {Name} ShowSkeleton failed: {ex.Message}");
            }
        }

        #endregion

        #region IDisposable

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SectionController));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }

        #endregion
    }
}
