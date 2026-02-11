using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ModernIPTVPlayer.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class UnifiedMediaGrid : UserControl
    {
        public event EventHandler<IMediaStream>? ItemClicked;
        public event EventHandler<IMediaStream>? PlayAction;
        public event EventHandler<MediaNavigationArgs>? DetailsAction;
        public event EventHandler<IMediaStream>? AddListAction;
        public event EventHandler<(Windows.UI.Color Primary, Windows.UI.Color Secondary)>? ColorExtracted;
        public event EventHandler? HoverEnded;

        private readonly ExpandedCardOverlayController _expandedCardOverlay;
        private List<IMediaStream>? _items;

        public List<IMediaStream>? ItemsSource
        {
            get => _items;
            set
            {
                _items = value;
                MediaGridView.ItemsSource = _items;
                IsLoading = false;
            }
        }

        public bool IsLoading
        {
            set
            {
                if (value)
                {
                    MediaGridView.Visibility = Visibility.Collapsed;
                    SkeletonGrid.Visibility = Visibility.Visible;
                    SkeletonGrid.ItemsSource = new List<int>(new int[20]);
                }
                else
                {
                    SkeletonGrid.Visibility = Visibility.Collapsed;
                    MediaGridView.Visibility = Visibility.Visible;
                }
            }
        }

        public UnifiedMediaGrid()
        {
            InitializeComponent();

            _expandedCardOverlay = new ExpandedCardOverlayController(this, OverlayCanvas, ActiveExpandedCard, CinemaScrim);
            _expandedCardOverlay.PlayRequested += (_, stream) => PlayAction?.Invoke(this, stream);
            _expandedCardOverlay.DetailsRequested += (_, args) => DetailsAction?.Invoke(this, new MediaNavigationArgs(args.Stream, args.Tmdb));
            _expandedCardOverlay.AddListRequested += (_, stream) => AddListAction?.Invoke(this, stream);

            PointerExited += UnifiedMediaGrid_PointerExited;
        }

        private async void UnifiedMediaGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_expandedCardOverlay.IsCardVisible)
            {
                await CloseExpandedCardAsync();
            }
        }

        private void MediaGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is IMediaStream stream)
            {
                var container = MediaGridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
                if (container?.ContentTemplateRoot is PosterCard poster)
                {
                    poster.PrepareConnectedAnimation();
                }

                ItemClicked?.Invoke(this, stream);
            }
        }

        private void PosterCard_ColorsExtracted(object sender, (Windows.UI.Color Primary, Windows.UI.Color Secondary) colors)
        {
            ColorExtracted?.Invoke(this, colors);
        }

        private async void Card_HoverStarted(object sender, EventArgs e)
        {
            if (sender is not PosterCard card) return;

            if (!string.IsNullOrEmpty(card.ImageUrl))
            {
                var colors = await ImageHelper.GetOrExtractColorAsync(card.ImageUrl);
                if (colors.HasValue)
                {
                    ColorExtracted?.Invoke(this, colors.Value);
                }
            }

            _expandedCardOverlay.OnHoverStarted(card);
        }

        private void Card_HoverEnded(object sender, EventArgs e)
        {
            HoverEnded?.Invoke(this, EventArgs.Empty);
        }

        public Task CloseExpandedCardAsync() => _expandedCardOverlay.CloseExpandedCardAsync();
    }
}
