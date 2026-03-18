using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Services
{
    public static class NavigationService
    {
        /// <summary>
        /// Navigate to MediaInfoPage with slide transition from right.
        /// Use when you have a source element (for potential future animation support).
        /// </summary>
        public static void NavigateToDetails(Frame frame, MediaNavigationArgs args, UIElement sourceElement = null, Microsoft.UI.Xaml.Media.ImageSource preloadedImage = null)
        {
            if (frame == null || args == null) return;
            if (preloadedImage != null) args.PreloadedImage = preloadedImage;
            NavigateWithSlideAnimation(frame, args);
        }

        /// <summary>
        /// Overload for direct IMediaStream navigation.
        /// </summary>
        public static void NavigateToDetails(Frame frame, IMediaStream stream, UIElement sourceElement = null, Microsoft.UI.Xaml.Media.ImageSource preloadedImage = null)
        {
            var args = new MediaNavigationArgs(stream, preloadedImage: preloadedImage);
            NavigateToDetails(frame, args, sourceElement);
        }

        /// <summary>
        /// Navigate to MediaInfoPage with slide transition.
        /// Use for Spotlight search results or when no source element is available.
        /// </summary>
        public static void NavigateToDetailsDirect(Frame frame, IMediaStream stream, Microsoft.UI.Xaml.Media.ImageSource preloadedImage = null)
        {
            if (frame == null || stream == null) return;
            var args = new MediaNavigationArgs(stream, preloadedImage: preloadedImage);
            NavigateWithSlideAnimation(frame, args);
        }

        /// <summary>
        /// Navigate to MediaInfoPage with MediaNavigationArgs without slide transition.
        /// </summary>
        public static void NavigateToDetailsDirect(Frame frame, MediaNavigationArgs args, Microsoft.UI.Xaml.Media.ImageSource preloadedImage = null)
        {
            if (frame == null || args == null) return;
            if (preloadedImage != null) args.PreloadedImage = preloadedImage;
            NavigateWithSlideAnimation(frame, args);
        }

        private static void NavigateWithSlideAnimation(Frame frame, MediaNavigationArgs args)
        {
            var transitionInfo = new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromRight
            };

            frame.Navigate(typeof(MediaInfoPage), args, transitionInfo);
        }
    }
}
