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
                IsLoading = false; // This triggers the visibility check
            }
        }

        public bool IsLoading
        {
            set
            {
                if (value)
                {
                    MediaGridView.Visibility = Visibility.Collapsed;
                    EmptyStatePanel.Visibility = Visibility.Collapsed;
                    SkeletonGrid.Visibility = Visibility.Visible;
                    SkeletonGrid.ItemsSource = new List<int>(new int[20]);
                }
                else
                {
                    SkeletonGrid.Visibility = Visibility.Collapsed;
                    
                    if (_items == null || _items.Count == 0)
                    {
                        MediaGridView.Visibility = Visibility.Collapsed;
                        EmptyStatePanel.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        MediaGridView.Visibility = Visibility.Visible;
                        EmptyStatePanel.Visibility = Visibility.Collapsed;
                    }
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

        private void MediaGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue) return;

            // Staggered Entrance Animation
            // Only animate if it hasn't been shown before to avoid flickering on scroll up
            if (args.ItemContainer != null && args.ItemContainer.Tag == null)
            {
                // Calculate delay based on index relative to the first visible index or just modulo
                // A simple index-based delay works well for initial load
                int index = args.ItemIndex;
                int staggerDelay = (index % 40) * 25; // Increase batch to 40 items for 4K screens, slightly faster 25ms delay

                // Ensure XAML Opacity is 1 (Visible) so hit-testing works, and we strictly animate Visual Layer
                args.ItemContainer.Opacity = 1; 

                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(args.ItemContainer);
                var compositor = visual.Compositor;

                // Initial State: Invisible via Visual Layer
                visual.Opacity = 0f;

                // Opacity Animation
                var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
                opacityAnim.Target = "Opacity";
                opacityAnim.InsertKeyFrame(0, 0);
                opacityAnim.InsertKeyFrame(1, 1);
                opacityAnim.Duration = TimeSpan.FromMilliseconds(400);
                opacityAnim.DelayTime = TimeSpan.FromMilliseconds(staggerDelay);
                opacityAnim.DelayTime = TimeSpan.FromMilliseconds(staggerDelay);

                // Offset Animation (Slide Up)
                var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
                offsetAnim.Target = "Offset";
                offsetAnim.InsertKeyFrame(0, new System.Numerics.Vector3(0, 20, 0)); // Start 20px down
                offsetAnim.InsertKeyFrame(1, new System.Numerics.Vector3(0, 0, 0));
                offsetAnim.Duration = TimeSpan.FromMilliseconds(400);
                offsetAnim.DelayTime = TimeSpan.FromMilliseconds(staggerDelay);

                visual.StartAnimation("Opacity", opacityAnim);
                visual.StartAnimation("Offset", offsetAnim);

                args.ItemContainer.Tag = "Shown"; // Mark as shown
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
