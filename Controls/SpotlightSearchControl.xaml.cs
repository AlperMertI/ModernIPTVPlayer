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

            // Perform Unified Search (Service now handles IPTV + Ranking)
            await StremioService.Instance.SearchAsync(query, (partialResults) =>
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    // partialResults already includes ranked IPTV + Addon results
                    ResultsList.ItemsSource = partialResults.Take(8).ToList();
                    ResultsList.Visibility = ResultsList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    ShimmerPanel.Visibility = Visibility.Collapsed;
                    
                    if (ResultsList.Items.Count == 0 && !string.IsNullOrEmpty(query)) 
                        EmptyStatePanel.Visibility = Visibility.Visible;
                    else
                        EmptyStatePanel.Visibility = Visibility.Collapsed;
                });
            });
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
