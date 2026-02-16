using System.Collections.Generic;
using Microsoft.UI.Xaml;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services.Stremio;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class SpotlightSearchControl : UserControl
    {
        public event EventHandler<IMediaStream> ItemClicked;
        public event EventHandler<string> SeeAllClicked;

        private DispatcherTimer _debounceTimer;
        private string _lastQuery;

        public SpotlightSearchControl()
        {
            this.InitializeComponent();

            _debounceTimer = new DispatcherTimer();
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(400); // 400ms debounce
            _debounceTimer.Tick += DebounceTimer_Tick;
        }

        public void Show()
        {
            this.Visibility = Visibility.Visible;
            OpenAnimation.Begin();
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        }

        public async void Hide()
        {
            CloseAnimation.Begin();
            await Task.Delay(250); // Wait for animation (Open is 250ms, Close should be similar)
            this.Visibility = Visibility.Collapsed;
        }

        private void SearchScrim_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Hide();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _debounceTimer.Stop();
            
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                ResultsList.ItemsSource = null;
                EmptyStatePanel.Visibility = Visibility.Collapsed;
                ShimmerPanel.Visibility = Visibility.Collapsed;
                SeeAllButton.Visibility = Visibility.Collapsed;
                return;
            }

            _debounceTimer.Start();
        }

        private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (ResultsList.Items.Count > 0)
                {
                   var first = ResultsList.Items[0] as IMediaStream;
                   ItemClicked?.Invoke(this, first);
                   Hide();
                }
                else
                {
                      SeeAllButton_Click(null, null);
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Hide();
            }
            else if (e.Key == Windows.System.VirtualKey.Down)
            {
                if (ResultsList.Items.Count > 0)
                {
                    ResultsList.Focus(FocusState.Programmatic);
                    ResultsList.SelectedIndex = 0;
                }
            }
        }

        private async void DebounceTimer_Tick(object sender, object e)
        {
            _debounceTimer.Stop();
            string query = SearchBox.Text.Trim();
            
            if (string.IsNullOrEmpty(query) || query == _lastQuery) return;
            _lastQuery = query;

            // Update UI State -> Loading
            ResultsList.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            ShimmerPanel.Visibility = Visibility.Visible;
            
            SeeAllText.Text = $"Tüm sonuçları gör: '{query}'";
            SeeAllButton.Visibility = Visibility.Visible;

            // Perform Hybrid Search
            var stremioTask = StremioService.Instance.SearchAsync(query);
            var iptvTask = SearchIptvAsync(query);

            await Task.WhenAll(stremioTask, iptvTask);

            var stremioResults = await stremioTask;
            var iptvResults = await iptvTask;

            // Combine Results
            var combined = new List<IMediaStream>();
            combined.AddRange(iptvResults.Take(2)); // Limit IPTV in spotlight for quick look
            combined.AddRange(stremioResults);
            
            // Update UI State -> Result
            ShimmerPanel.Visibility = Visibility.Collapsed;
            
            if (combined.Any())
            {
                ResultsList.ItemsSource = combined.Take(7); // Show top 7 inline
                ResultsList.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
            }
        }

        private async Task<List<IMediaStream>> SearchIptvAsync(string query)
        {
            var results = new List<IMediaStream>();
            var login = App.CurrentLogin;
            if (login == null) return results;

            string playlistId = login.PlaylistUrl ?? "default";
            
            // Search Movies
            var movies = await ContentCacheService.Instance.LoadCacheAsync<LiveStream>(playlistId, "vod");
            if (movies != null)
            {
                var filtered = movies.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(5);
                results.AddRange(filtered);
            }

            // Search Series
            var series = await ContentCacheService.Instance.LoadCacheAsync<SeriesStream>(playlistId, "series");
            if (series != null)
            {
                var filtered = series.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(5);
                results.AddRange(filtered);
            }

            return results;
        }

        private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is IMediaStream item)
            {
                ItemClicked?.Invoke(this, item);
                Hide();
            }
        }

        private void SeeAllButton_Click(object sender, RoutedEventArgs e)
        {
            string query = SearchBox.Text.Trim();
            if(!string.IsNullOrEmpty(query))
            {
                SeeAllClicked?.Invoke(this, query);
                Hide();
            }
        }
    }
}
