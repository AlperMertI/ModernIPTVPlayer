using Microsoft.UI.Xaml;
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
        public event EventHandler<StremioMediaStream> ItemClicked;
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
            await Task.Delay(150); // Wait for animation
            this.Visibility = Visibility.Collapsed;
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
                // If results exist and first one is selected/highlighted, verify.
                // For now, if "See All" is visible, trigger that? Or pick first result?
                // Let's implement: Pick first result if list has items.
                if (ResultsList.Items.Count > 0)
                {
                   var first = ResultsList.Items[0] as StremioMediaStream;
                   ItemClicked?.Invoke(this, first);
                   Hide();
                }
                else
                {
                     // Trigger See All
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

            // Perform Search
            var results = await StremioService.Instance.SearchAsync(query);

            // Update UI State -> Result
            ShimmerPanel.Visibility = Visibility.Collapsed;
            
            if (results != null && results.Any())
            {
                ResultsList.ItemsSource = results.Take(6); // Show top 6 inline
                ResultsList.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
            }
        }

        private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is StremioMediaStream item)
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
