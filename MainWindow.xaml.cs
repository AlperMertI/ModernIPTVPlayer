using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;

namespace ModernIPTVPlayer
{
    public sealed partial class MainWindow : Window
    {
        public new static MainWindow Current { get; private set; }
        public MainWindow()
        {
            Current = this;
            this.InitializeComponent();
            
            // Set title bar
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // Default navigation
            NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First();
            ContentFrame.Navigate(typeof(LoginPage));
        }

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                // Settings Page logic here
                return;
            }

            if (args.InvokedItemContainer?.Tag is string tag)
            {
                Type? pageType = tag switch
                {
                    "LoginPage" => typeof(LoginPage),
                    "LiveTVPage" => typeof(LiveTVPage),
                    "MoviesPage" => typeof(MoviesPage), 
                    "SeriesPage" => typeof(SeriesPage), 
                    _ => null
                };

                if (pageType != null && ContentFrame.CurrentSourcePageType != pageType)
                {
                    ContentFrame.Navigate(pageType);
                }
            }
        }

        private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
            }
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            NavView.IsBackEnabled = ContentFrame.CanGoBack;
            NavView.IsBackEnabled = ContentFrame.CanGoBack;
            NavView.IsBackEnabled = ContentFrame.CanGoBack;
            NavView.IsPaneVisible = true; // Default to visible, special cases below
            NavView.AlwaysShowHeader = true; // Default to show header

            // Reset Default State
            AppTitleBar.Visibility = Visibility.Visible;
            ContentFrame.Margin = new Thickness(0, 4, 0, 0);

            // Make sure the header reflects the current page
            if (ContentFrame.SourcePageType == typeof(LoginPage))
            {
                NavView.Header = "Giriş Yap";
                NavView.IsPaneVisible = true;
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => i.Tag.ToString() == "LoginPage");
            }
            else if (ContentFrame.SourcePageType == typeof(LiveTVPage))
            {
                NavView.Header = null; // Custom Header in Page
                NavView.AlwaysShowHeader = false; 
                NavView.IsPaneVisible = true;
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => i.Tag.ToString() == "LiveTVPage");
            }
            else if (ContentFrame.SourcePageType == typeof(MoviesPage))
            {
                NavView.Header = "Filmler";
                NavView.IsPaneVisible = true;
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => i.Tag.ToString() == "MoviesPage");
            }
            else if (ContentFrame.SourcePageType == typeof(SeriesPage))
            {
                NavView.Header = "Diziler";
                NavView.IsPaneVisible = true;
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => i.Tag.ToString() == "SeriesPage");
            }
            else if (ContentFrame.SourcePageType == typeof(PlayerPage))
            {
                NavView.Header = null; // Hide header for player
                NavView.IsPaneVisible = false;
                NavView.AlwaysShowHeader = false;
                
                // Hide TitleBar and Remove Margins for Full Immersion
                AppTitleBar.Visibility = Visibility.Collapsed;
                ContentFrame.Margin = new Thickness(0);
            }
        }
        public void SetFullScreen(bool isFullScreen)
        {
            var presenter = isFullScreen ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen : Microsoft.UI.Windowing.AppWindowPresenterKind.Default;
            if (this.AppWindow != null)
            {
                this.AppWindow.SetPresenter(presenter);
            }
        }

    }
}
