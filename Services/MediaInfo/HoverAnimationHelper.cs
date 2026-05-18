using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Provides shared pointer-enter/exit hover animations for ItemsRepeater item containers.
    /// Replaces the byte-for-byte identical <c>SourceItem_PointerEntered/Exited</c> and
    /// <c>EpisodeItem_PointerEntered/Exited</c> handlers that previously lived in both
    /// <c>MediaInfoPage.Sources.cs</c> and <c>MediaInfoPage.Episodes.cs</c>.
    /// </summary>
    internal static class HoverAnimationHelper
    {
        /// <summary>
        /// Fades in the hover overlay when the pointer enters an item container.
        /// Expects a child element named "HoverBorder" within the container.
        /// </summary>
        /// <param name="sender">The FrameworkElement that raised the PointerEntered event.</param>
        /// <param name="targetOpacity">The opacity to animate to (typically 0.1 for hover).</param>
        /// <param name="durationMs">Animation duration in milliseconds (default 200).</param>
        public static void OnPointerEntered(object sender, double targetOpacity = 0.1, int durationMs = 200)
        {
            if (sender is not FrameworkElement fe) return;
            var hoverBorder = fe.FindName("HoverBorder") as Border;
            if (hoverBorder == null) return;
            AnimateBorderOpacity(hoverBorder, targetOpacity, durationMs);
        }

        /// <summary>
        /// Fades out the hover overlay when the pointer exits an item container.
        /// Expects a child element named "HoverBorder" within the container.
        /// </summary>
        /// <param name="sender">The FrameworkElement that raised the PointerExited event.</param>
        /// <param name="durationMs">Animation duration in milliseconds (default 200).</param>
        public static void OnPointerExited(object sender, int durationMs = 200)
        {
            if (sender is not FrameworkElement fe) return;
            var hoverBorder = fe.FindName("HoverBorder") as Border;
            if (hoverBorder == null) return;
            AnimateBorderOpacity(hoverBorder, 0.0, durationMs);
        }

        private static void AnimateBorderOpacity(Border border, double toOpacity, int durationMs)
        {
            if (border == null) return;

            var storyboard = new Storyboard();
            var anim = new DoubleAnimation
            {
                To = toOpacity,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(anim, border);
            Storyboard.SetTargetProperty(anim, "Opacity");
            storyboard.Children.Add(anim);
            storyboard.Begin();
        }
    }
}
