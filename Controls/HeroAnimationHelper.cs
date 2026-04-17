using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace ModernIPTVPlayer.Controls
{
    public static class HeroAnimationHelper
    {
        public static void AnimateTextOut(FrameworkElement element)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                if (visual == null) return;

                var compositor = visual.Compositor;
                var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

                ElementCompositionPreview.SetIsTranslationEnabled(element, true);

                var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, visual.Opacity, easing);
                fadeOut.InsertKeyFrame(1f, 0f, easing);
                fadeOut.Duration = TimeSpan.FromMilliseconds(300);

                var slideOut = compositor.CreateVector3KeyFrameAnimation();
                slideOut.InsertKeyFrame(1f, new Vector3(0, -30, 0), easing);
                slideOut.Duration = TimeSpan.FromMilliseconds(300);

                visual.StartAnimation("Opacity", fadeOut);
                try { visual.StartAnimation("Translation", slideOut); }
                catch { /* Translation may be unavailable on some visuals/states; opacity still runs. */ }
            }
            catch { element.Opacity = 0; }
        }

        public static void FadeVisualOpacity(Visual visual, float target, int durationMs)
        {
            var compositor = visual.Compositor;
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1f, target, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f)));
            anim.Duration = TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation("Opacity", anim);
        }

        public static void FadeElement(FrameworkElement element, double targetOpacity, int durationMs = 400)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                if (visual == null) return;

                var compositor = visual.Compositor;
                var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.2f, 1f));

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(1f, (float)targetOpacity, easing);
                fade.Duration = TimeSpan.FromMilliseconds(durationMs);

                visual.StartAnimation("Opacity", fade);
            }
            catch { element.Opacity = targetOpacity; }
        }

        public static void AnimateShimmerIn(FrameworkElement element)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                if (visual == null) return;

                var compositor = visual.Compositor;
                var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.0f, 0.0f), new Vector2(0.2f, 1.0f));

                ElementCompositionPreview.SetIsTranslationEnabled(element, true);

                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f, easing);
                fadeIn.InsertKeyFrame(1f, 1f, easing);
                fadeIn.Duration = TimeSpan.FromMilliseconds(800);

                var slide = compositor.CreateVector3KeyFrameAnimation();
                slide.InsertKeyFrame(0f, new Vector3(0, 30, 0), easing);
                slide.InsertKeyFrame(1f, Vector3.Zero, easing);
                slide.Duration = TimeSpan.FromMilliseconds(800);

                var scale = compositor.CreateVector3KeyFrameAnimation();
                scale.InsertKeyFrame(0f, new Vector3(1.05f, 1.05f, 1.0f), easing);
                scale.InsertKeyFrame(1f, new Vector3(1.0f, 1.0f, 1.0f), easing);
                scale.Duration = TimeSpan.FromMilliseconds(800);

                visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
                visual.StartAnimation("Opacity", fadeIn);
                try { visual.StartAnimation("Translation", slide); } catch { }
                visual.StartAnimation("Scale", scale);
            }
            catch { element.Opacity = 1; }
        }

        /// <summary>
        /// Slide-in only (no container opacity fade) so composition-hosted logo is not dimmed by a parent
        /// while the hero backdrop fades in on a separate visual.
        /// </summary>
        public static void AnimateTextIn(FrameworkElement element, int slideDurationMs = 1200)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                if (visual == null) return;

                var compositor = visual.Compositor;
                var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0.2f, 1f));

                ElementCompositionPreview.SetIsTranslationEnabled(element, true);

                element.Opacity = 1;

                var slideIn = compositor.CreateVector3KeyFrameAnimation();
                slideIn.InsertKeyFrame(0f, new Vector3(0, 40, 0), easing);
                slideIn.InsertKeyFrame(1f, Vector3.Zero, easing);
                slideIn.Duration = TimeSpan.FromMilliseconds(slideDurationMs);

                // Also fade-in opacity — when the shimmer is suppressed (warm-cache path) the text
                // would otherwise snap visible. A short opacity ramp keeps the reveal smooth in both
                // paths; it piggy-backs on the shimmer's cross-fade when the shimmer IS showing.
                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f, easing);
                fadeIn.InsertKeyFrame(1f, 1f, easing);
                fadeIn.Duration = TimeSpan.FromMilliseconds(Math.Min(slideDurationMs, 500));

                try { visual.StartAnimation("Translation", slideIn); }
                catch { /* Keep opacity fade if Translation channel is unsupported. */ }
                visual.StartAnimation("Opacity", fadeIn);
            }
            catch { element.Opacity = 1; }
        }

        public static void ApplyKenBurns(Visual visual)
        {
            var compositor = visual.Compositor;
            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(0.6f, 1.0f));

            offsetAnim.InsertKeyFrame(0f, Vector3.Zero, easing);
            offsetAnim.InsertKeyFrame(0.5f, new Vector3(-20f, -8f, 0f), easing); // Subtle pan
            offsetAnim.InsertKeyFrame(1f, Vector3.Zero, easing);
            offsetAnim.Duration = TimeSpan.FromSeconds(30);
            offsetAnim.IterationBehavior = AnimationIterationBehavior.Forever;

            // Combined Offset + Scale for a smooth Ken Burns effect without black edges
            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(0f, new Vector3(1.1f, 1.1f, 1.0f), easing);
            scaleAnim.InsertKeyFrame(0.5f, new Vector3(1.15f, 1.15f, 1.0f), easing);
            scaleAnim.InsertKeyFrame(1f, new Vector3(1.1f, 1.1f, 1.0f), easing);
            scaleAnim.Duration = TimeSpan.FromSeconds(30);
            scaleAnim.IterationBehavior = AnimationIterationBehavior.Forever;

            visual.CenterPoint = new Vector3(visual.Size.X / 2, visual.Size.Y / 2, 0);
            visual.StartAnimation("Offset", offsetAnim);
            visual.StartAnimation("Scale", scaleAnim);
        }
    }
}
