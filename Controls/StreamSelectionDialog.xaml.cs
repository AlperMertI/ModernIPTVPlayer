using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class StreamSelectionDialog : ContentDialog
    {
        public StremioStream SelectedStream { get; private set; }

        public StreamSelectionDialog()
        {
            this.InitializeComponent();
        }

        public void LoadStreams(List<StremioStream> streams)
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

            if (streams == null || streams.Count == 0)
            {
                ErrorText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                StreamListView.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
            else
            {
                StreamListView.ItemsSource = streams;
            }
        }

        private void StreamListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StreamListView.SelectedItem is StremioStream stream)
            {
                SelectedStream = stream;
                this.Hide(); // Close dialog on selection (acts as "OK")
            }
        }
    }
}
