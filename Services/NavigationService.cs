using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using ModernIPTVPlayer.Models;
using System;

namespace ModernIPTVPlayer.Services
{
    public static class NavigationService
    {
        private static UIElement _lastSourceElement;
        private static IMediaStream _lastNavigatedStream;
        private static bool _useCustomAnimation = true;

        public static UIElement LastSourceElement => _lastSourceElement;
        public static IMediaStream LastNavigatedStream => _lastNavigatedStream;

        public static bool UseCustomAnimation
        {
            get => _useCustomAnimation;
            set => _useCustomAnimation = value;
        }

        /// <summary>
        /// Centralized navigation to MediaInfoPage with smooth fade transition.
        /// Uses custom fade animation instead of ConnectedAnimation for reliability.
        /// </summary>
        public static void NavigateToDetails(Frame frame, MediaNavigationArgs args, UIElement sourceElement = null)
        {
            if (frame == null || args == null) return;

            _lastSourceElement = sourceElement;
            _lastNavigatedStream = args.Stream;

            if (_useCustomAnimation)
            {
                NavigateWithCustomAnimation(frame, args);
            }
            else
            {
                PrepareForwardAnimation(sourceElement);
                NavigateToMediaInfoPage(frame, args);
            }
        }

        /// <summary>
        /// Overload for direct IMediaStream navigation with source element.
        /// </summary>
        public static void NavigateToDetails(Frame frame, IMediaStream stream, UIElement sourceElement = null)
        {
            var args = new MediaNavigationArgs(stream);
            NavigateToDetails(frame, args, sourceElement);
        }

        /// <summary>
        /// Navigate to MediaInfoPage without connected animation.
        /// Use this for Spotlight search results or when no source element is available.
        /// </summary>
        public static void NavigateToDetailsDirect(Frame frame, IMediaStream stream)
        {
            if (frame == null || stream == null) return;

            _lastNavigatedStream = stream;
            _lastSourceElement = null;

            var args = new MediaNavigationArgs(stream);
            NavigateToMediaInfoPage(frame, args);
        }

        /// <summary>
        /// Navigate to MediaInfoPage with MediaNavigationArgs without connected animation.
        /// </summary>
        public static void NavigateToDetailsDirect(Frame frame, MediaNavigationArgs args)
        {
            if (frame == null || args == null) return;

            _lastNavigatedStream = args.Stream;
            _lastSourceElement = null;

            NavigateToMediaInfoPage(frame, args);
        }

        private static void NavigateWithCustomAnimation(Frame frame, MediaNavigationArgs args)
        {
            var transitionInfo = new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromRight
            };

            frame.Navigate(typeof(MediaInfoPage), args, transitionInfo);
        }

        /// <summary>
        /// Prepare forward connected animation from a UI element.
        /// Called internally by NavigateToDetails variants.
        /// </summary>
        public static void PrepareForwardAnimation(UIElement sourceElement)
        {
            if (sourceElement != null)
            {
                ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate("ForwardConnectedAnimation", sourceElement);

                System.Diagnostics.Debug.WriteLine($"[NavigationService] Prepared 'ForwardConnectedAnimation' from Source: {sourceElement.GetType().Name} | Size: {sourceElement.RenderSize} | Visibility: {sourceElement.Visibility}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[NavigationService] WARNING: No source element found for animation.");
            }
        }

        /// <summary>
        /// Prepare backward connected animation from a UI element.
        /// Call this in OnNavigatedFrom before navigating back.
        /// </summary>
        public static void PrepareBackAnimation(UIElement sourceElement)
        {
            if (sourceElement != null)
            {
                var anim = ConnectedAnimationService.GetForCurrentView()
                    .PrepareToAnimate("BackConnectedAnimation", sourceElement);
                
                if (anim != null)
                {
                    anim.Configuration = new DirectConnectedAnimationConfiguration();
                }

                System.Diagnostics.Debug.WriteLine($"[NavigationService] Prepared 'BackConnectedAnimation' from: {sourceElement.GetType().Name}");
            }
        }

        /// <summary>
        /// Get the prepared backward animation for the current view.
        /// </summary>
        public static ConnectedAnimation GetBackAnimation()
        {
            return ConnectedAnimationService.GetForCurrentView().GetAnimation("BackConnectedAnimation");
        }

        private static void NavigateToMediaInfoPage(Frame frame, MediaNavigationArgs args)
        {
            frame.Navigate(typeof(MediaInfoPage), args, new SuppressNavigationTransitionInfo());
        }
    }
}
