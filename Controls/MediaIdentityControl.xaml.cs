using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class MediaIdentityControl : UserControl
    {
        public event EventHandler<bool> LogoLoadCompleted;

        public MediaIdentityControl()
        {
            this.InitializeComponent();
        }

        public StackPanel TitlePanelElement => TitlePanel;
        public ShimmerControl TitleShimmerElement => TitleShimmer;
        public TextBlock TitleTextBlock => TitleText;
        public TextBlock SuperTitleTextBlock => SuperTitleText;
        public Border LogoHost => ContentLogoHost;
        public Image LogoImage => ContentLogoImage;
        public StackPanel IdentityPanel => IdentityContainer;

        public void SetTitle(string title)
        {
            TitleText.Text = title ?? string.Empty;
        }

        public void SetSuperTitle(string superTitle)
        {
            SuperTitleText.Text = superTitle?.ToUpper() ?? string.Empty;
            IdentityContainer.Visibility = string.IsNullOrEmpty(superTitle) ? Visibility.Collapsed : Visibility.Visible;
        }

        public void SetLogo(Microsoft.UI.Xaml.Media.ImageSource source)
        {
            if (source == null)
            {
                ContentLogoImage.Source = null;
                ContentLogoHost.Visibility = Visibility.Collapsed;
                TitleText.Visibility = Visibility.Collapsed;
                TitleShimmer.Visibility = Visibility.Visible;
                return;
            }

            ContentLogoImage.Source = source;
            ContentLogoHost.Visibility = Visibility.Visible;
            TitleText.Visibility = Visibility.Collapsed;
            TitleShimmer.Visibility = Visibility.Collapsed;
            LogoLoadCompleted?.Invoke(this, true);
        }

        public void SetLogo(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                ContentLogoImage.Source = null;
                ContentLogoHost.Visibility = Visibility.Collapsed;
                TitleText.Visibility = Visibility.Collapsed;
                TitleShimmer.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    var svgSource = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(url));
                    ContentLogoImage.Source = svgSource;
                    svgSource.Opened += (s, e) => OnLogoLoaded();
                    svgSource.OpenFailed += (s, e) => HandleLoadFailure();
                }
                else
                {
                    var bitmap = Helpers.SharedImageManager.GetOptimizedImage(url, targetWidth: 500, xamlRoot: this.XamlRoot);
                    ContentLogoImage.Source = bitmap;
                }

                ContentLogoHost.Visibility = Visibility.Visible;
                TitleText.Visibility = Visibility.Collapsed;
            }
            catch
            {
                HandleLoadFailure();
            }
        }

        private void OnLogoLoaded()
        {
            TitleShimmer.Visibility = Visibility.Collapsed;
            ContentLogoHost.Visibility = Visibility.Visible;
            TitleText.Visibility = Visibility.Collapsed;
            LogoLoadCompleted?.Invoke(this, true);
        }

        private void HandleLoadFailure()
        {
            ContentLogoHost.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            TitleText.FontSize = 48;
            TitleShimmer.Visibility = Visibility.Collapsed;
            LogoLoadCompleted?.Invoke(this, false);
        }

        private void ContentLogoImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            TitleShimmer.Visibility = Visibility.Collapsed;
            LogoLoadCompleted?.Invoke(this, true);
        }

        private void ContentLogoImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            HandleLoadFailure();
        }
    }
}
