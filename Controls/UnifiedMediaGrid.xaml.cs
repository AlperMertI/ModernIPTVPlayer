using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Helpers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class UnifiedMediaGrid : UserControl
    {
        public event EventHandler<MediaNavigationArgs>? ItemClicked;
        public event EventHandler<MediaNavigationArgs>? PlayAction;
        public event EventHandler<MediaNavigationArgs>? DetailsAction;
        public event EventHandler<IMediaStream>? AddListAction;
        public event EventHandler<ColorExtractedEventArgs>? ColorExtracted;
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
                if (_items == value) return; // ENGINEERED: No-op if reference is identical

                _items = value;

                if (_items != null)
                {
                    var wrapped = new List<UnifiedMediaItemContext>();
                    foreach (var item in _items)
                    {
                        var stream = WinRTHelpers.AsMediaStream(item);
                        if (stream != null)
                        {
                            wrapped.Add(new UnifiedMediaItemContext(stream, this));
                        }
                    }
                    MediaGridView.ItemsSource = wrapped;
                }
                else
                {
                    MediaGridView.ItemsSource = null;
                }

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

            // [PROJECT ZERO] Phase-Aware Skeleton Management
            // Phase 0: Skeleton is shown by default (XAML).
            // Phase 1+: Skeleton is hidden to save GPU/Overdraw
            if (args.Phase > 0)
            {
                var skeleton = FindVisualChild<ShimmerCard>(args.ItemContainer, "Skeleton");
                if (skeleton != null) skeleton.Visibility = Visibility.Collapsed;
                // No need to continue if we're just updating phase 1 (PosterCard takes over)
            }
            else
            {
                var skeleton = FindVisualChild<ShimmerCard>(args.ItemContainer, "Skeleton");
                if (skeleton != null) skeleton.Visibility = Visibility.Visible;
            }

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



        private void PosterCard_Clicked(object sender, IMediaStream stream)
        {
            System.Diagnostics.Debug.WriteLine($"[UnifiedMediaGrid] PosterCard_Clicked fired. Stream: {stream?.Title}");
            if (stream != null && sender is PosterCard pc)
            {
                ItemClicked?.Invoke(this, new MediaNavigationArgs(stream, null, false, pc.ImageElement));
            }
        }

        private void PosterCard_ColorsExtracted(object sender, ColorExtractedEventArgs e)
        {
            ColorExtracted?.Invoke(this, e);
        }

        private async void Card_HoverStarted(object sender, EventArgs e)
        {
            if (sender is not FrameworkElement card) return;

            if (card is PosterCard pc && !string.IsNullOrEmpty(pc.ImageUrl))
            {
                var colors = await ImageHelper.GetOrExtractColorAsync(pc.ImageUrl);
                if (colors.HasValue)
                {
                    ColorExtracted?.Invoke(this, new ColorExtractedEventArgs(colors.Value.Primary, colors.Value.Secondary));
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

        private T? FindVisualChild<T>(DependencyObject element, string name) where T : DependencyObject
        {
            if (element == null) return null;
            int childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                if (child is T typedChild && (child is FrameworkElement fe && fe.Name == name))
                {
                    return typedChild;
                }
                var result = FindVisualChild<T>(child, name);
                if (result != null) return result;
            }
            return null;
        }
    }
 
    [Microsoft.UI.Xaml.Data.Bindable]
    public class UnifiedMediaItemContext
    {
        private readonly UnifiedMediaGrid _parent;
        public IMediaStream Data { get; }
        public bool ShowTitles => _parent.ShowTitles;
        public bool ShowIptvBadge => _parent.ShowIptvBadge;
 
        public UnifiedMediaItemContext(IMediaStream data, UnifiedMediaGrid parent)
        {
            Data = data;
            _parent = parent;
        }
    }
}
