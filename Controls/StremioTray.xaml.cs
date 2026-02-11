using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class StremioTray : UserControl
    {
        public event EventHandler<StremioStream> SourceSelected;
        public event EventHandler TrayClosed;

        public StremioTray()
        {
            this.InitializeComponent();
        }

        public void Show(string title, List<StremioStream> streams)
        {
            MediaTitleLabel.Text = title;
            this.Visibility = Visibility.Visible;
            SourceCountLabel.Text = $"{streams.Count} Kaynak Bulundu";

            // Grouping: Group by Addon name (parsed or passed)
            // For now, let's group by "Quality" or just raw list as per user's "simple headers" feedback
            // but we need a structure for ItemsControl.
            
            var row = new StreamGroupViewModel { Streams = new ObservableCollection<StremioStream>(streams) };
            StreamGroupsControl.ItemsSource = new List<StreamGroupViewModel> { row };

            // Animation
            var sb = new Storyboard();
            var anim = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(500) };
            anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            Storyboard.SetTarget(anim, TrayRoot);
            Storyboard.SetTargetProperty(anim, "(Grid.Margin).Bottom");
            sb.Children.Add(anim);
            sb.Begin();
        }

        public void Hide()
        {
            var sb = new Storyboard();
            var anim = new DoubleAnimation { To = -600, Duration = TimeSpan.FromMilliseconds(400) };
            anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
            Storyboard.SetTarget(anim, TrayRoot);
            Storyboard.SetTargetProperty(anim, "(Grid.Margin).Bottom");
            sb.Completed += (s, e) => { this.Visibility = Visibility.Collapsed; };
            sb.Children.Add(anim);
            sb.Begin();
            
            TrayClosed?.Invoke(this, EventArgs.Empty);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

        private void GridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is StremioStream stream)
            {
                SourceSelected?.Invoke(this, stream);
                Hide();
            }
        }
    }

    public class StreamGroupViewModel
    {
        public ObservableCollection<StremioStream> Streams { get; set; }
    }
}
