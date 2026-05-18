using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using ModernIPTVPlayer.Controls;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Metadata;
using ModernIPTVPlayer.Services.Stremio;
using ModernIPTVPlayer.Services.Iptv;
using ModernIPTVPlayer.Services.MediaInfo;
using ModernIPTVPlayer.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Input;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using System.Threading;

namespace ModernIPTVPlayer
{
    public partial class MediaInfoPage : Page
    {
        #region Downloads Interaction

        private void ObsidianTray_TrayClosed(object sender, EventArgs e)
        {
             AnimateMainContentRecede(false);
        }

        private async void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            _actionHandlerManager?.OnRestartClicked();
        }

        private void CopyLinkButton_Click(object sender, RoutedEventArgs e)
        {
            string urlToCopy = _streamUrl;
            if (string.IsNullOrEmpty(urlToCopy) && _item != null)
            {
                urlToCopy = _item.StreamUrl;
            }
            _actionService?.HandleCopyLinkClick(urlToCopy, sender);
        }

        private List<System.Threading.CancellationTokenSource> _activeDownloads = new();

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
             if (string.IsNullOrEmpty(_streamUrl))
             {
                  await _actionService.HandleDownloadClickAsync(_item, _streamUrl, sender);
                  return;
             }

             if (_item is SeriesStream)
             {
                  var flyout = new MenuFlyout();
                  var singleItem = new MenuFlyoutItem { Text = "Bu Bölümü İndir", Icon = new FontIcon { Glyph = "\uE896" } };
                  singleItem.Click += async (s, args) => await _actionHandlerManager.DownloadSingleAsync();
                  flyout.Items.Add(singleItem);
                  var seasonItem = new MenuFlyoutItem { Text = "Tüm Sezonu İndir", Icon = new FontIcon { Glyph = "\uE8B7" } };
                  seasonItem.Click += async (s, args) => await _actionHandlerManager.DownloadSeasonAsync();
                  flyout.Items.Add(seasonItem);
                  flyout.ShowAt(sender as FrameworkElement);
             }
             else
             {
                  await _actionService.HandleDownloadClickAsync(_item, _streamUrl, sender);
             }
        }
        
        private async Task DownloadSingle()
        {
            await _actionHandlerManager.DownloadSingleAsync();
        }

        private async Task DownloadSeason()
        {
            await _actionHandlerManager.DownloadSeasonAsync();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) 
        { 
            _actionHandlerManager?.OnBackClicked();
        }

        #endregion

        #region Watchlist & Watched Actions

        private async void MarkWatched_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is EpisodeItem ep)
                _actionHandlerManager?.OnMarkWatchedClicked(ep);
        }
        
        private async void MarkRemainingWatched_Click(object sender, RoutedEventArgs e)
        {
            EpisodeItem startEpisode = null;
            if (sender is MenuFlyoutItem item && item.Tag is EpisodeItem epTag) startEpisode = epTag;
            _actionHandlerManager?.OnMarkRemainingWatchedClicked(startEpisode);
        }

        private async void MarkUnwatched_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is EpisodeItem ep)
                _actionHandlerManager?.OnMarkUnwatchedClicked(ep);
        }

        private async void WatchlistButton_Click(object sender, RoutedEventArgs e)
        {
            await _actionService.HandleWatchlistClickAsync(_item, sender);
            _actionHandlerManager?.UpdateWatchlistState(animate: true);
        }

        private void UpdateWatchlistState(bool animate = false)
        {
            _actionHandlerManager?.UpdateWatchlistState(animate);
        }

        #endregion

        #region Cast & Director Interaction

        private void CastItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
                _castDirectorManager?.OnCastItemPointerEntered(element);
        }

        private void CastItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
                _castDirectorManager?.OnCastItemPointerExited(element, e);
        }

        private void CastItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                bool isCastVisible = CastSection?.Visibility == Visibility.Visible;
                bool isDirectorVisible = DirectorSection?.Visibility == Visibility.Visible;
                _castDirectorManager?.OnCastItemTapped(element, isCastVisible, isDirectorVisible);
            }
        }

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _castDirectorManager?.OnRootGridPointerPressed();
        }

        private void ActivePersonCard_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _castDirectorManager?.OnPersonCardPointerEntered();
        }

        private void ActivePersonCard_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _castDirectorManager?.OnPersonCardPointerExited();
        }

        private void ActivePersonCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _castDirectorManager?.OnActivePersonCardSizeChanged();
        }

        internal T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindParent<T>(parentObject);
        }

        internal void PlacePersonCard(FrameworkElement anchor, bool animateMove) => PlacePersonCardInternal(anchor, animateMove);

        private void PlacePersonCardInternal(FrameworkElement anchor, bool animateMove)
        {
            if (PersonCardOverlay == null || ActivePersonCard == null || anchor == null) return;

            double overlayWidth = PersonCardOverlay.ActualWidth;
            double overlayHeight = PersonCardOverlay.ActualHeight;
            if ((overlayWidth <= 0 || overlayHeight <= 0) && PersonCardOverlay.XamlRoot != null)
            {
                overlayWidth = PersonCardOverlay.XamlRoot.Size.Width;
                overlayHeight = PersonCardOverlay.XamlRoot.Size.Height;
            }

            try
            {
                var transform = anchor.TransformToVisual(PersonCardOverlay);
                var position = transform.TransformPoint(new Point(0, 0));

                double cardWidth = ActivePersonCard.ActualWidth > 0 ? ActivePersonCard.ActualWidth : (ActivePersonCard.Width > 0 ? ActivePersonCard.Width : 420);
                double cardHeight = ActivePersonCard.ActualHeight > 0 ? ActivePersonCard.ActualHeight : (ActivePersonCard.Height > 0 ? ActivePersonCard.Height : 560);
                
                double targetX = position.X + (anchor.ActualWidth / 2) - (cardWidth / 2);
                double targetY = position.Y - cardHeight - 16;

                const double edgeMargin = 24.0;

                if (targetY < edgeMargin + 48) 
                {
                    targetY = position.Y + anchor.ActualHeight + 16;
                }

                if (targetX < edgeMargin) targetX = edgeMargin;
                if (targetX + cardWidth > overlayWidth - edgeMargin) targetX = overlayWidth - cardWidth - edgeMargin;

                if (targetY + cardHeight > overlayHeight - edgeMargin) targetY = overlayHeight - cardHeight - edgeMargin;
                if (targetY < edgeMargin + 20) targetY = edgeMargin + 20;

                double oldLeft = Canvas.GetLeft(ActivePersonCard);
                double oldTop = Canvas.GetTop(ActivePersonCard);
                if (double.IsNaN(oldLeft)) oldLeft = targetX;
                if (double.IsNaN(oldTop)) oldTop = targetY;

                Canvas.SetLeft(ActivePersonCard, targetX);
                Canvas.SetTop(ActivePersonCard, targetY);

                if (!animateMove) return;

                double deltaX = targetX - oldLeft;
                double deltaY = targetY - oldTop;
                if (Math.Abs(deltaX) <= 0.1 && Math.Abs(deltaY) <= 0.1) return;

                var visual = ElementCompositionPreview.GetElementVisual(ActivePersonCard);
                var compositor = visual.Compositor;
                visual.StopAnimation("Offset");

                var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
                offsetAnim.Target = "Translation";
                var cubic = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.33f, 1f), new System.Numerics.Vector2(0.67f, 1f));
                offsetAnim.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, cubic);
                offsetAnim.Duration = TimeSpan.FromMilliseconds(280);
                try { CompositionService.StartTranslationAnimation(ActivePersonCard, offsetAnim, new System.Numerics.Vector3((float)-deltaX, (float)-deltaY, 0)); } catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PersonCard] Placement failed: {ex.Message}");
            }
        }

        #endregion

        #region Panel Reveal & Drawer Interaction

        private async Task HideSourcesPanelAsync()
        {
            if (_panelOwner == null) return;
            await _panelOwner.HideSourcesPanelAnimatedAsync();
        }

        internal async Task ShowSourcesPanelAsync()
        {
            if (_panelOwner == null) return;
            await _panelOwner.ShowSourcesPanelAnimatedAsync();
        }

        private async void SourcesShowHandle_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await ShowSourcesPanelAsync();
        }

        private void SourcesShowHandle_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            _panelOwner?.ReassertUnsquashExpression();
        }

        private void SourcesShowHandle_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            _panelOwner?.HandleSourcesShowHandlePull(e.Delta.Translation.X);
        }

        private async void BtnHideSources_Click(object sender, RoutedEventArgs e)
        {
            if (ActualWidth < LayoutAdaptiveThreshold)
            {
                if (ResolveCurrentContentKind() == MediaContentKind.Series)
                {
                    OpenEpisodesPanel(PanelChangeReason.BackToEpisodes);
                }
                else
                {
                    CloseDetailPanel(PanelChangeReason.SourcesClosed);
                }
                return;
            }

            if (_panelOwner != null && !_panelOwner.IsSourcesPanelHidden)
            {
                await HideSourcesPanelAsync();
            }
        }

        private void BtnCloseSources_Click(object sender, RoutedEventArgs e)
        {
            DeselectEpisode();
        }

        private void BtnBackToEpisodes_Click(object sender, RoutedEventArgs e)
        {
            DeselectEpisode();
        }

        private async void SourcesShowHandle_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            if (_panelOwner == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(SourcesPanel);
            visual.Properties.TryGetVector3("Translation", out var currentTrans);

            if (currentTrans.X < 850)
            {
                await ShowSourcesPanelAsync();
            }
            else
            {
                await HideSourcesPanelAsync();
            }
        }

        #endregion
    }
}
