using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Microsoft.UI.Composition;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Manages parallax scroll effects on the MediaInfoPage hero background.
    /// Extracted from MediaInfoPage.xaml.cs to isolate visual effect concerns.
    /// 
    /// Responsibilities:
    /// - Applies parallax offset to hero image based on scroll position
    /// - Sets up Composition expression animations for parallax
    /// 
    /// Does NOT:
    /// - Manage hero image loading (that's MediaInfoPage.Background.cs)
    /// - Manage connected animations (that's ConnectedAnimationManager)
    /// </summary>
    internal sealed class ParallaxController : IDisposable
    {
        private readonly MediaInfoPage _page;
        private readonly Compositor _compositor;
        private bool _disposed;
        private bool _isSetup;

        public ParallaxController(MediaInfoPage page, Compositor compositor)
        {
            _page = page ?? throw new ArgumentNullException(nameof(page));
            _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
            Debug.WriteLine("[PARALLAX] Initialized");
        }

        public void SetupParallax()
        {
            if (_disposed || _isSetup) return;

            try
            {
                var scrollViewer = _page.RootScrollViewerControl;
                var heroImage = _page.HeroImageControl;

                if (scrollViewer == null || heroImage == null)
                {
                    Debug.WriteLine("[PARALLAX] Setup skipped: missing scroll viewer or hero image");
                    return;
                }

                var scrollProp = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scrollViewer);
                var visual = ElementCompositionPreview.GetElementVisual(heroImage);

                var parallax = _compositor.CreateExpressionAnimation("Scrolling.Translation.Y * 0.3");
                parallax.SetReferenceParameter("Scrolling", scrollProp);
                visual.StartAnimation("Offset.Y", parallax);

                _isSetup = true;
                Debug.WriteLine("[PARALLAX] Setup complete: hero parallax at 0.3x speed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PARALLAX] SetupParallax error: {ex.Message}");
            }
        }

        public void Reset()
        {
            if (_disposed) return;

            try
            {
                var heroImage = _page.HeroImageControl;
                if (heroImage != null)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(heroImage);
                    visual.StopAnimation("Offset.Y");
                    visual.Offset = new Vector3(0, 0, 0);
                }
                _isSetup = false;
                Debug.WriteLine("[PARALLAX] Reset complete");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PARALLAX] Reset error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Reset();
            Debug.WriteLine("[PARALLAX] Disposed");
        }
    }
}
