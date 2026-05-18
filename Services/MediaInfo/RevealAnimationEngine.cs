using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Shared Composition-based reveal animation engine for detail panel ItemsRepeater items.
    /// Replaces the duplicated reveal animation methods that previously lived in
    /// <c>MediaInfoPage.Sources.cs</c> but were used by both Sources and Episodes panels.
    /// </summary>
    internal sealed class RevealAnimationEngine
    {
        #region Constants

        /// <summary>Per-item stagger delay in milliseconds for the initial reveal.</summary>
        private const int SourceRevealStaggerMs = 48;

        /// <summary>Duration of the shimmer placeholder fade-in animation.</summary>
        private const int ShimmerRevealDurationMs = 300;

        /// <summary>Duration of the real-item opacity reveal animation.</summary>
        private const int RealRevealOpacityDurationMs = 500;

        /// <summary>Duration of the real-item slide reveal animation.</summary>
        private const int RealRevealSlideDurationMs = 600;

        /// <summary>Maximum total stagger delay before capping.</summary>
        private const int MaxStaggerDelayMs = 400;

        /// <summary>Time window (ms) within which reveals are considered "initial".</summary>
        private const int InitialRevealWindowMs = 3000;

        #endregion

        #region Fields

        private readonly Compositor _compositor;
        private CubicBezierEasingFunction _staggerEasing;
        private CubicBezierEasingFunction _shimmerEasing;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new reveal animation engine bound to the given compositor.
        /// </summary>
        /// <param name="compositor">The UI compositor used to create animations.</param>
        public RevealAnimationEngine(Compositor compositor)
        {
            _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
        }

        #endregion

        #region Reveal Animations

        /// <summary>
        /// Applies the staggered diagonal reveal animation (opacity + slide from below)
        /// to a real data item container.
        /// </summary>
        /// <param name="element">The FrameworkElement to animate.</param>
        /// <param name="index">The item's index within the collection.</param>
        /// <param name="state">The panel's reveal state for timing and delay calculations.</param>
        public void ApplyStaggeredReveal(FrameworkElement element, int index, PanelRevealState state)
        {
            if (element == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(element);
            if (visual == null) return;

            ElementCompositionPreview.SetIsTranslationEnabled(element, true);

            int delayMs = state.IsInitialReveal
                ? Math.Min(index * SourceRevealStaggerMs, MaxStaggerDelayMs)
                : 0;

            if (_staggerEasing == null)
            {
                _staggerEasing = _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.16f, 0.86f),
                    new Vector2(0.16f, 1.0f));
            }

            CompositionService.StopAll(visual);

            visual.Opacity = 0f;
            try
            {
                visual.Properties.InsertVector3(
                    CompositionService.TranslationProperty,
                    new Vector3(0, 20, 0));
            }
            catch { }

            var opacity = _compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0f, 0f);
            opacity.InsertKeyFrame(1f, 1f, _staggerEasing);
            opacity.Duration = TimeSpan.FromMilliseconds(RealRevealOpacityDurationMs);
            opacity.DelayTime = TimeSpan.FromMilliseconds(delayMs);

            var slide = _compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, new Vector3(0, 20, 0));
            slide.InsertKeyFrame(1f, Vector3.Zero, _staggerEasing);
            slide.Duration = TimeSpan.FromMilliseconds(RealRevealSlideDurationMs);
            slide.DelayTime = TimeSpan.FromMilliseconds(delayMs);

            try
            {
                visual.StartAnimation(CompositionService.OpacityProperty, opacity);
                visual.StartAnimation(CompositionService.TranslationProperty, slide);
            }
            catch { }
        }

        /// <summary>
        /// Applies a fast opacity-only fade for skeleton/placeholder items.
        /// No translation — placeholders fade in top-to-bottom proportional to their index.
        /// </summary>
        /// <param name="element">The FrameworkElement to animate.</param>
        /// <param name="index">The item's index within the collection.</param>
        public void ApplyShimmerReveal(FrameworkElement element, int index)
        {
            if (element == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(element);
            if (visual == null) return;

            int delayMs = index * 30;

            if (_shimmerEasing == null)
            {
                _shimmerEasing = _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.0f, 0.0f),
                    new Vector2(0.4f, 1.0f));
            }

            CompositionService.StopAll(visual);
            visual.Opacity = 0f;

            var opacity = _compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0f, 0f);
            opacity.InsertKeyFrame(1f, 1f, _shimmerEasing);
            opacity.Duration = TimeSpan.FromMilliseconds(ShimmerRevealDurationMs);
            opacity.DelayTime = TimeSpan.FromMilliseconds(delayMs);

            try
            {
                visual.StartAnimation(CompositionService.OpacityProperty, opacity);
            }
            catch { }
        }

        #endregion

        #region State Management

        /// <summary>
        /// Prepares an element for reveal by setting it to the initial hidden state
        /// (opacity 0, translated 20px down). Called before starting the reveal animation.
        /// </summary>
        /// <param name="element">The FrameworkElement to prepare.</param>
        public void PrepareForReveal(FrameworkElement element)
        {
            if (element == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(element);
            if (visual == null) return;

            ElementCompositionPreview.SetIsTranslationEnabled(element, true);

            visual.StopAnimation(CompositionService.OpacityProperty);
            visual.StopAnimation(CompositionService.TranslationProperty);
            visual.Opacity = 0f;
            try
            {
                visual.Properties.InsertVector3(
                    CompositionService.TranslationProperty,
                    new Vector3(0, 20, 0));
            }
            catch { }
        }

        /// <summary>
        /// Resets an element to its fully visible, non-animated state.
        /// Called when an item is being cleared from the ItemsRepeater or when
        /// it falls outside the reveal limit.
        /// </summary>
        /// <param name="element">The FrameworkElement to reset.</param>
        public void LeanReset(FrameworkElement element)
        {
            if (element == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(element);
            if (visual == null) return;

            ElementCompositionPreview.SetIsTranslationEnabled(element, true);

            visual.StopAnimation(CompositionService.OpacityProperty);
            visual.StopAnimation(CompositionService.TranslationProperty);

            visual.Opacity = 1f;
            try
            {
                visual.Properties.InsertVector3(
                    CompositionService.TranslationProperty,
                    Vector3.Zero);
            }
            catch { }
        }

        /// <summary>
        /// Alias for <see cref="LeanReset"/> for semantic clarity at call sites
        /// that are resetting reveal state specifically.
        /// </summary>
        /// <param name="element">The FrameworkElement to reset.</param>
        public void ResetRevealState(FrameworkElement element) => LeanReset(element);

        #endregion
    }
}
