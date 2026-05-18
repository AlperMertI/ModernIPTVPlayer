using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Services.MediaInfo;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage
    {
        #region Season & Data Logic

        private void SeasonComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SeasonComboBox.SelectedItem is SeasonItem season)
            {
                _isEpisodesLoading = season.Episodes.Count == 0;
                _episodesManager?.PopulateEpisodesFromSeason(season);
                if (EpisodesRepeater != null)
                    EpisodesRepeater.ItemsSource = _episodesManager?.CurrentEpisodes;
            }
        }

        #endregion

        #region Repeater & Item Handlers

        private void EpisodesRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (args.Element is FrameworkElement fe && args.Index >= 0)
            {
                var episodes = _episodesManager?.CurrentEpisodes;
                if (episodes != null && args.Index < episodes.Count)
                {
                    fe.DataContext = episodes[args.Index];
                }
            }
            _detailPanelController?.OnElementPrepared(PanelType.Episodes, sender, args);
        }

        private void EpisodesRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            _detailPanelController?.OnElementClearing(PanelType.Episodes, sender, args);
        }

        private void EpisodeItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is EpisodeItem ep)
            {
                SelectEpisode(ep);
                e.Handled = true;
            }
        }

        private void EpisodeItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _detailPanelController?.OnItemPointerEntered(sender);
        }

        private void EpisodeItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _detailPanelController?.OnItemPointerExited(sender);
        }

        #endregion

        #region Selection Logic

        internal void SelectEpisode(EpisodeItem ep)
        {
            _episodesManager?.SelectEpisode(ep);
        }

        private void DeselectEpisode()
        {
            _episodesManager?.DeselectEpisode();
        }

        #endregion
    }
}
