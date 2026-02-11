using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ModernIPTVPlayer.Models.Stremio;
using System;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class StreamCard : UserControl
    {
        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register("ViewModel", typeof(StremioStream), typeof(StreamCard), new PropertyMetadata(null, OnViewModelChanged));

        public StremioStream ViewModel
        {
            get => (StremioStream)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        public StreamCard()
        {
            this.InitializeComponent();
        }

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StreamCard card && e.NewValue is StremioStream stream)
            {
                card.UpdateUI(stream);
            }
        }

        private void UpdateUI(StremioStream stream)
        {
            TitleText.Text = stream.Title ?? stream.Name;
            
            // Try to parse addon name or just use stream name
            AddonText.Text = stream.Name; 

            // Simple parsing for Quality/Size from title if possible
            // Most Stremio titles look like "4K | HEVC | 24GB"
            string title = stream.Title ?? "";
            
            if (title.Contains("4K", StringComparison.OrdinalIgnoreCase))
            {
                QualityBadge.Visibility = Visibility.Visible;
                QualityBadge.BorderBrush = Application.Current.Resources["GoldGradient"] as Microsoft.UI.Xaml.Media.Brush;
                QualityText.Text = "4K";
                QualityText.Foreground = Application.Current.Resources["GoldGradient"] as Microsoft.UI.Xaml.Media.Brush;
            }
            else if (title.Contains("1080p", StringComparison.OrdinalIgnoreCase))
            {
                QualityBadge.Visibility = Visibility.Visible;
                QualityBadge.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightSlateGray);
                QualityText.Text = "1080P";
                QualityText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            }

            // Extract GB if found
            var matches = System.Text.RegularExpressions.Regex.Match(title, @"(\d+\.?\d*)\s*(GB|MB)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matches.Success)
            {
                SizeText.Text = matches.Value.ToUpper();
            }
            else
            {
                SizeText.Text = "";
            }
        }

        private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            VisualStateManager.GoToState(this, "PointerOver", true);
        }

        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            VisualStateManager.GoToState(this, "Normal", true);
        }
    }
}
