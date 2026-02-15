using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Stremio;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ModernIPTVPlayer.Pages
{
    public sealed partial class SearchResultsPage : Page
    {
        private string _query;

        public SearchResultsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is string query)
            {
                // If query changed OR it's a new navigation, fetch.
                // If Back/Forward and query matches, existing results are preserved by CacheMode.
                if (_query != query || e.NavigationMode == NavigationMode.New)
                {
                    _query = query;
                     PageTitle.Text = $"Arama Sonuçları: '{_query}'";
                    await PerformSearchAsync();
                }
            }
        }

        private async System.Threading.Tasks.Task PerformSearchAsync()
        {
            try
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
                ResultsGrid.Visibility = Visibility.Collapsed;
                EmptyPanel.Visibility = Visibility.Collapsed;

                var results = await StremioService.Instance.SearchAsync(_query);

                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;

                if (results != null && results.Any())
                {
                    ResultsGrid.ItemsSource = results.Cast<IMediaStream>().ToList();
                    ResultsGrid.Visibility = Visibility.Visible;
                }
                else
                {
                    EmptyPanel.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Search Error: {ex.Message}");
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                EmptyPanel.Visibility = Visibility.Visible; // Show empty as fallback
            }
        }

        private void ResultsGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is IMediaStream item)
            {
                var args = new MediaNavigationArgs(item);
                NavigationService.NavigateToDetails(Frame, args, sender as UIElement);
            }
        }


        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
