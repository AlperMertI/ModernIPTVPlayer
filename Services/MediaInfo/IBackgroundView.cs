using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// View contract for hero background operations. Implemented by MediaInfoPage.
    /// Decouples BackgroundManager from the page's internal UI elements.
    /// </summary>
    internal interface IBackgroundView
    {
        #region Hero Image References

        Microsoft.UI.Xaml.Controls.Image HeroImage { get; }
        Microsoft.UI.Xaml.Controls.Image HeroImage2 { get; }

        #endregion

        #region Item Context

        string? ItemTitle { get; }
        string? ItemImdbId { get; }

        #endregion

        #region Hero Image Operations

        void SetHeroImageSource(ImageSource? source);
        void SetHeroImage2Source(ImageSource? source);
        void SetHeroImageOpacity(double opacity);
        void SetHeroImage2Opacity(double opacity);
        void SetActiveHeroOpacity(double opacity);
        void SetInactiveHeroOpacity(double opacity);
        void SetOutgoingHeroSource(ImageSource? source);
        double GetActiveHeroOpacity();
        double GetInactiveHeroOpacity();
        string? GetActiveHeroUrl();
        string? GetInactiveHeroUrl();

        #endregion

        #region Shimmer

        void SetHeroShimmerVisibility(Visibility visibility);
        void SetHeroShimmerOpacity(float opacity);

        #endregion

        #region Gradients

        void SetGradientOpacity(string gradientName, float opacity, int durationMs);

        #endregion

        #region Text Colors

        void SetTitleColor(Color color);
        void SetOverviewColor(Color color);

        #endregion

        #region Ambient Color

        string PrimaryColorHex { get; }

        #endregion
    }
}
