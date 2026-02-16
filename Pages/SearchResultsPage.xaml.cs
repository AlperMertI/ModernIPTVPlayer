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
    public class SearchArgs
    {
        public string Query { get; set; }
        public string PreferredSource { get; set; } // "Stremio" or "IPTV"
    }

    public sealed partial class SearchResultsPage : Page
    {
        private SearchArgs _args;

        public SearchResultsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is SearchArgs args)
            {
                if (_args?.Query != args.Query || _args?.PreferredSource != args.PreferredSource || e.NavigationMode == NavigationMode.New)
                {
                    _args = args;
                    PageTitle.Text = $"Arama Sonuçları: '{_args.Query}'";
                    
                    // Reorder sections based on preference
                    if (_args.PreferredSource == "IPTV")
                    {
                        if (ResultsStack.Children.Contains(IptvSection) && ResultsStack.Children.IndexOf(IptvSection) > 0)
                        {
                            ResultsStack.Children.Remove(IptvSection);
                            ResultsStack.Children.Insert(0, IptvSection);
                        }
                    }
                    else
                    {
                        if (ResultsStack.Children.Contains(StremioSection) && ResultsStack.Children.IndexOf(StremioSection) > 0)
                        {
                            ResultsStack.Children.Remove(StremioSection);
                            ResultsStack.Children.Insert(0, StremioSection);
                        }
                    }

                    await PerformSearchAsync();
                }
            }
            else if (e.Parameter is string query)
            {
                // Fallback for string query
                _args = new SearchArgs { Query = query, PreferredSource = "Stremio" };
                PageTitle.Text = $"Arama Sonuçları: '{_args.Query}'";
                await PerformSearchAsync();
            }
        }

        private async System.Threading.Tasks.Task PerformSearchAsync()
        {
            try
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
                StremioSection.Visibility = Visibility.Collapsed;
                IptvSection.Visibility = Visibility.Collapsed;
                EmptyPanel.Visibility = Visibility.Collapsed;

                // Parallel Search
                var stremioTask = StremioService.Instance.SearchAsync(_args.Query);
                var iptvTask = SearchIptvAsync(_args.Query);

                await System.Threading.Tasks.Task.WhenAll(stremioTask, iptvTask);

                var stremioResults = await stremioTask;
                var iptvResults = await iptvTask;

                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;

                // Update Stremio UI
                if (stremioResults != null && stremioResults.Any())
                {
                    StremioGrid.ItemsSource = stremioResults;
                    TxtStremioCount.Text = stremioResults.Count.ToString();
                    StremioSection.Visibility = Visibility.Visible;
                }

                // Update IPTV UI
                if (iptvResults != null && iptvResults.Any())
                {
                    IptvGrid.ItemsSource = iptvResults;
                    TxtIptvCount.Text = iptvResults.Count.ToString();
                    IptvSection.Visibility = Visibility.Visible;
                }

                if ((stremioResults == null || !stremioResults.Any()) && (iptvResults == null || !iptvResults.Any()))
                {
                    EmptyPanel.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Search Error: {ex.Message}");
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                EmptyPanel.Visibility = Visibility.Visible;
            }
        }

        private async System.Threading.Tasks.Task<List<IMediaStream>> SearchIptvAsync(string query)
        {
            var results = new List<IMediaStream>();
            var login = App.CurrentLogin;
            if (login == null) return results;

            string playlistId = login.PlaylistUrl ?? "default";

            // We can search VOD and Series
            var movies = await ContentCacheService.Instance.LoadCacheAsync<LiveStream>(playlistId, "vod_streams");
            if (movies != null)
            {
                results.AddRange(movies.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
            }

            var series = await ContentCacheService.Instance.LoadCacheAsync<SeriesStream>(playlistId, "series_streams");
            if (series != null)
            {
                results.AddRange(series.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
            }

            return results;
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
