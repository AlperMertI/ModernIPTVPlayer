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
                ContentLogoHost.Visibility = Visibility.Collapsed;
                TitleText.Visibility = Visibility.Visible;
                TitleText.FontSize = 48;
                return;
            }

            ContentLogoImage.Source = source;
            ContentLogoHost.Visibility = Visibility.Visible;
            
            // By default, hide title when logo is present. 
            // The caller can override this (e.g. for episodes)
            TitleText.Visibility = Visibility.Collapsed;
            TitleText.FontSize = 20;
            TitleText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            TitleText.Opacity = 0.8;
            LogoLoadCompleted?.Invoke(this, true);
        }

        public void SetLogo(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                ContentLogoHost.Visibility = Visibility.Collapsed;
                TitleText.Visibility = Visibility.Visible;
                TitleText.FontSize = 48;
                return;
            }

            try
            {
                if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    var svgSource = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(url));
                    ContentLogoImage.Source = svgSource;
                    svgSource.Opened += (s, e) => LogoLoadCompleted?.Invoke(this, true);
                    svgSource.OpenFailed += (s, e) => HandleLoadFailure();
                }
                else
                {
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url));
                    ContentLogoImage.Source = bitmap;
                    // ImageOpened/Failed events handled in XAML for BitmapImage
                }

                ContentLogoHost.Visibility = Visibility.Visible;
                
                // By default, hide title when logo is present.
                TitleText.Visibility = Visibility.Collapsed;
                TitleText.FontSize = 20;
                TitleText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                TitleText.Opacity = 0.8;
            }
            catch
            {
                HandleLoadFailure();
            }
        }

        private void HandleLoadFailure()
        {
            ContentLogoHost.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            TitleText.FontSize = 48;
            LogoLoadCompleted?.Invoke(this, false);
        }

        private void ContentLogoImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            LogoLoadCompleted?.Invoke(this, true);
        }

        private void ContentLogoImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            HandleLoadFailure();
        }
    }
}
