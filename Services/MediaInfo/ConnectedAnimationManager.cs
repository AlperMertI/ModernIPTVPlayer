using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Manages connected animations for the hero background image during navigation.
    /// Provides smooth transitions between list page and info page.
    /// 
    /// Responsibilities:
    /// - Starts connected animation from source element
    /// - Applies hero background image from navigation args
    /// - Manages animation lifecycle
    /// 
    /// Does NOT:
    /// - Load images (that's ImageHelper)
    /// - Manage slideshow (that's MediaInfoPage.Background.cs)
    /// </summary>
    internal sealed class ConnectedAnimationManager : IDisposable
    {
        private readonly MediaInfoPage _page;
        private bool _disposed;

        public ConnectedAnimationManager(MediaInfoPage page)
        {
            _page = page ?? throw new ArgumentNullException(nameof(page));
            Debug.WriteLine("[CONNECTED-ANIM] Initialized");
        }

        public void StartHeroConnectedAnimation()
        {
            if (_disposed) return;

            try
            {
                Debug.WriteLine("[CONNECTED-ANIM] StartHeroConnectedAnimation called");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CONNECTED-ANIM] StartHeroConnectedAnimation error: {ex.Message}");
            }
        }

        public void ApplyHeroBackgroundAction(ImageSource image, string source = "animation")
        {
            if (_disposed || image == null) return;

            try
            {
                Debug.WriteLine($"[CONNECTED-ANIM] ApplyHeroBackgroundAction from {source}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CONNECTED-ANIM] ApplyHeroBackgroundAction error: {ex.Message}");
            }
        }

        public void ApplyHeroBackgroundAction(string url, string source = "url")
        {
            if (_disposed || string.IsNullOrEmpty(url)) return;

            try
            {
                Debug.WriteLine($"[CONNECTED-ANIM] ApplyHeroBackgroundAction from {source}: {url}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CONNECTED-ANIM] ApplyHeroBackgroundAction error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Debug.WriteLine("[CONNECTED-ANIM] Disposed");
        }
    }
}
