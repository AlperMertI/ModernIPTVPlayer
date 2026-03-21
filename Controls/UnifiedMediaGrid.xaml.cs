using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using ModernIPTVPlayer.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class UnifiedMediaGrid : UserControl
    {
        public event EventHandler<MediaNavigationArgs>? ItemClicked;
        public event EventHandler<MediaNavigationArgs>? PlayAction;
        public event EventHandler<MediaNavigationArgs>? DetailsAction;
        public event EventHandler<IMediaStream>? AddListAction;
        public event EventHandler<(Windows.UI.Color Primary, Windows.UI.Color Secondary)>? ColorExtracted;
        public event EventHandler<FrameworkElement>? HoverEnded;

        public static readonly DependencyProperty ItemClickCommandProperty =
            DependencyProperty.Register("ItemClickCommand", typeof(ICommand), typeof(UnifiedMediaGrid), new PropertyMetadata(null));

        public static readonly DependencyProperty ShowTitlesProperty =
            DependencyProperty.Register("ShowTitles", typeof(bool), typeof(UnifiedMediaGrid), new PropertyMetadata(false));

        public bool ShowTitles
        {
            get => (bool)GetValue(ShowTitlesProperty);
            set => SetValue(ShowTitlesProperty, value);
        }

        public static readonly DependencyProperty ShowIptvBadgeProperty =
            DependencyProperty.Register("ShowIptvBadge", typeof(bool), typeof(UnifiedMediaGrid), new PropertyMetadata(true));

        public bool ShowIptvBadge
        {
            get => (bool)GetValue(ShowIptvBadgeProperty);
            set => SetValue(ShowIptvBadgeProperty, value);
        }

        private readonly ExpandedCardOverlayController _expandedCardOverlay;
        private System.Collections.IEnumerable? _items;

        public System.Collections.IEnumerable? ItemsSource
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
                    
                    if (_items == null || !EnumerableAny(_items))
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
            _expandedCardOverlay.PlayRequested += (_, stream) => PlayAction?.Invoke(this, new MediaNavigationArgs(stream, null, true, ActiveExpandedCard.BannerImage));
            _expandedCardOverlay.DetailsRequested += (_, args) => DetailsAction?.Invoke(this, new MediaNavigationArgs(args.Stream, args.Tmdb, false, ActiveExpandedCard.BannerImage));
            _expandedCardOverlay.AddListRequested += (_, stream) => AddListAction?.Invoke(this, stream);

            PointerExited += UnifiedMediaGrid_PointerExited;
            Loaded += UnifiedMediaGrid_Loaded;
        }

        private void UnifiedMediaGrid_Loaded(object sender, RoutedEventArgs e)
        {
            // Find the ScrollViewer within the MediaGridView to detect manipulation (scrolling)
            var scrollViewer = FindScrollViewer(MediaGridView);
            if (scrollViewer != null)
            {
                scrollViewer.DirectManipulationStarted += (s, args) => 
                {
                    _expandedCardOverlay.IsManipulationInProgress = true;
                    _expandedCardOverlay.CancelPendingShow();
                };
                scrollViewer.DirectManipulationCompleted += (s, args) => 
                {
                    _expandedCardOverlay.IsManipulationInProgress = false;
                };
            }
        }

        private ScrollViewer? FindScrollViewer(DependencyObject element)
        {
            if (element is ScrollViewer sv) return sv;
            int childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
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
            if (args.InRecycleQueue || args.ItemContainer == null) return;

            // Only animate the first appearance (not on scroll recycle)
            if (args.ItemContainer.Tag == null)
            {
                try
                {
                    if (args.ItemContainer.XamlRoot == null || !args.ItemContainer.IsLoaded) return;

                    int index = args.ItemIndex;
                    // Stagger: max 300ms total so final items don't wait forever
                    int staggerDelay = Math.Min(index % 20 * 30, 300);

                    // Use safe XAML Storyboard (same pattern as CatalogRow which works correctly)
                    var translateTransform = new Microsoft.UI.Xaml.Media.TranslateTransform { Y = 24 };
                    args.ItemContainer.RenderTransform = translateTransform;
                    args.ItemContainer.Opacity = 0;

                    var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

                    var fadeIn = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        To = 1,
                        Duration = TimeSpan.FromMilliseconds(380),
                        BeginTime = TimeSpan.FromMilliseconds(staggerDelay),
                        EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase 
                        { 
                            EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut 
                        }
                    };
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeIn, args.ItemContainer);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeIn, "Opacity");

                    var slideIn = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(380),
                        BeginTime = TimeSpan.FromMilliseconds(staggerDelay),
                        EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase 
                        { 
                            EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut 
                        }
                    };
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slideIn, translateTransform);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slideIn, "Y");

                    sb.Children.Add(fadeIn);
                    sb.Children.Add(slideIn);
                    sb.Begin();

                    args.ItemContainer.Tag = "Shown";
                }
                catch (Exception ex)
                {
                    // Fail-safe: make item visible if animation fails
                    try 
                    { 
                        args.ItemContainer.Opacity = 1;
                        args.ItemContainer.RenderTransform = null;
                    } 
                    catch { }
                    System.Diagnostics.Debug.WriteLine($"[UnifiedMediaGrid] !!! Animation error for Index {args.ItemIndex}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void MediaGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is IMediaStream stream)
            {
                var container = MediaGridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
                UIElement source = (container?.ContentTemplateRoot as PosterCard)?.ImageElement;
                
                System.Diagnostics.Debug.WriteLine($"[UnifiedMediaGrid] Item Clicked. SourceElement Found: {source != null}");
                ItemClicked?.Invoke(this, new MediaNavigationArgs(stream, null, false, source));
            }
        }

        private void PosterCard_ColorsExtracted(object sender, (Windows.UI.Color Primary, Windows.UI.Color Secondary) colors)
        {
            ColorExtracted?.Invoke(this, colors);
        }

        private async void Card_HoverStarted(object sender, EventArgs e)
        {
            if (sender is not FrameworkElement card) return;

            if (card is PosterCard pc && !string.IsNullOrEmpty(pc.ImageUrl))
            {
                var colors = await ImageHelper.GetOrExtractColorAsync(pc.ImageUrl);
                if (colors.HasValue)
                {
                    ColorExtracted?.Invoke(this, colors.Value);
                }
            }

            _expandedCardOverlay.OnHoverStarted(card);
        }

        private void Card_HoverEnded(object sender, EventArgs e)
        {
            if (sender is FrameworkElement card)
            {
                HoverEnded?.Invoke(this, card);
            }
        }

        private bool EnumerableAny(System.Collections.IEnumerable source)
        {
            if (source == null) return false;
            if (source is System.Collections.ICollection collection) return collection.Count > 0;
            
            var enumerator = source.GetEnumerator();
            return enumerator.MoveNext();
        }

        public Task CloseExpandedCardAsync() => _expandedCardOverlay.CloseExpandedCardAsync();
    }
}
