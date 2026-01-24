using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ModernIPTVPlayer
{
    public sealed partial class PlaylistDialog : ContentDialog
    {
        public Playlist Result { get; private set; }

        public PlaylistDialog(Playlist? existing = null)
        {
            this.InitializeComponent();
            if (existing != null)
            {
                NameTextBox.Text = existing.Name;
                TypeComboBox.SelectedIndex = existing.Type == PlaylistType.M3u ? 0 : 1;
                UrlTextBox.Text = existing.Url;
                HostTextBox.Text = existing.Host;
                UsernameTextBox.Text = existing.Username;
                PasswordBox.Password = existing.Password;
                Result = existing;
            }
            else
            {
                TypeComboBox.SelectedIndex = 0;
                Result = new Playlist();
            }
        }

        private void TypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (M3uPanel == null || XtreamPanel == null) return;

            if (TypeComboBox.SelectedIndex == 0)
            {
                M3uPanel.Visibility = Visibility.Visible;
                XtreamPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                M3uPanel.Visibility = Visibility.Collapsed;
                XtreamPanel.Visibility = Visibility.Visible;
            }
        }

        public void PrepareResult()
        {
            Result.Name = NameTextBox.Text;
            Result.Type = TypeComboBox.SelectedIndex == 0 ? PlaylistType.M3u : PlaylistType.XtreamCodes;
            Result.Url = UrlTextBox.Text;
            Result.Host = HostTextBox.Text;
            Result.Username = UsernameTextBox.Text;
            Result.Password = PasswordBox.Password;
        }
    }
}
