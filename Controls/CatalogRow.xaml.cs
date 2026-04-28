using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;

using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class CatalogRow : UserControl
    {
        public static readonly DependencyProperty CatalogNameProperty =
            DependencyProperty.Register("CatalogName", typeof(string), typeof(CatalogRow), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty IsLoadingMoreProperty =
            DependencyProperty.Register("IsLoadingMore", typeof(bool), typeof(CatalogRow), new PropertyMetadata(false));

        public bool IsLoadingMore
        {
            get => (bool)GetValue(IsLoadingMoreProperty);
            set => SetValue(IsLoadingMoreProperty, value);
        }

        public string CatalogName
        {
            get => (string)GetValue(CatalogNameProperty);
            set => SetValue(CatalogNameProperty, value);
        }

        public static readonly DependencyProperty IsHeaderInteractiveProperty =
            DependencyProperty.Register("IsHeaderInteractive", typeof(bool), typeof(CatalogRow), new PropertyMetadata(true));

        public bool IsHeaderInteractive
        {
            get => (bool)GetValue(IsHeaderInteractiveProperty);
            set => SetValue(IsHeaderInteractiveProperty, value);
        }
        
        public static readonly DependencyProperty RowStyleProperty =
            DependencyProperty.Register("RowStyle", typeof(string), typeof(CatalogRow), new PropertyMetadata("Standard", OnRowStyleChanged));

        public string RowStyle
        {
            get => (string)GetValue(RowStyleProperty);
            set => SetValue(RowStyleProperty, value);
        }

        private static void OnRowStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CatalogRow row)
            {
                row.UpdateLoadingState();
            }
        }

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register("ItemsSource", typeof(object), typeof(CatalogRow), new PropertyMetadata(null, OnItemsSourceChanged));

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CatalogRow row)
            {
                // [VIRTUALIZATION FIX] Force the layout to re-measure when new data arrives
                // This prevents items from staying invisible until a scroll forces a layout pass.
                row.Repeater?.InvalidateMeasure();
            }
        }

        public object ItemsSource
        {
            get => GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public static readonly DependencyProperty ItemTemplateProperty =
            DependencyProperty.Register("ItemTemplate", typeof(DataTemplate), typeof(CatalogRow), new PropertyMetadata(null));

        public DataTemplate ItemTemplate
        {
            get => (DataTemplate)GetValue(ItemTemplateProperty);
            set => SetValue(ItemTemplateProperty, value);
        }

        private static void OnItemTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CatalogRow row)
            {
                if (row.Repeater != null)
                    row.Repeater.ItemTemplate = e.NewValue as DataTemplate;
            }
        }

        public static readonly DependencyProperty IsLoadingProperty =
            DependencyProperty.Register("IsLoading", typeof(bool), typeof(CatalogRow), new PropertyMetadata(false, OnIsLoadingChanged));

        public bool IsLoading
        {
            get => (bool)GetValue(IsLoadingProperty);
            set => SetValue(IsLoadingProperty, value);
        }

        private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CatalogRow row)
            {
                row.UpdateLoadingState();
            }
        }

        private bool? _lastLoadingState = null;
        private bool _isUpdatingHeroState = false;

        private void UpdateLoadingState()
        {
            if (ShimmerPanel == null || ItemsScrollViewer == null) return;

            bool useTransition = _lastLoadingState == true && !IsLoading;
            _lastLoadingState = IsLoading;

            // 1. Lifecycle State (Loading vs Content)
            string loadingState = IsLoading ? "Loading" : "ContentReady";
            VisualStateManager.GoToState(this, loadingState, useTransition);

            // 2. Structural State (Standard vs Landscape)
            VisualStateManager.GoToState(this, RowStyle ?? "Standard", true);

            // [VIRTUALIZATION FIX] Force layout pass to ensure items appear immediately
            if (!IsLoading)
            {
                Repeater?.InvalidateMeasure();
            }
        }

        public ItemsRepeater RepeaterControl => Repeater;



        public event EventHandler<CatalogItemClickedEventArgs> ItemClicked;
        public event EventHandler HeaderClicked;
        public event EventHandler<FrameworkElement> HoverStarted;
        public event EventHandler<FrameworkElement> HoverEnded;
        public event EventHandler ScrollStarted;
        public event EventHandler ScrollEnded;
        public event EventHandler LoadMoreAction;

        public CatalogRow()
        {
            // #region agent log
            try { ModernIPTVPlayer.App.DebugNdjson("CatalogRow.xaml.cs:ctor", "enter", null, "H-RENDER"); } catch { }
            // #endregion
            this.InitializeComponent();
            // #region agent log
            try { ModernIPTVPlayer.App.DebugNdjson("CatalogRow.xaml.cs:ctor", "InitializeComponent done", null, "H-RENDER"); } catch { }
            // #endregion
            this.Loaded += CatalogRow_Loaded;
            this.Unloaded += CatalogRow_Unloaded;
        }

        private void CatalogRow_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureScrollViewer();
            
            // [SYNC] Ensure initial loading state is correctly reflected
            UpdateLoadingState();
        }

        private void PosterCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is PosterCard card && card.MediaStream is IMediaStream stream)
            {
                ElementSoundPlayer.Play(ElementSoundKind.Invoke);
                // Pass the internal ImageElement for ConnectedAnimation
                ItemClicked?.Invoke(this, new CatalogItemClickedEventArgs(stream, card.ImageElement));
            }
            else if (sender is LandscapeCard lCard && lCard.MediaStream is IMediaStream lStream)
            {
                ElementSoundPlayer.Play(ElementSoundKind.Invoke);
                ItemClicked?.Invoke(this, new CatalogItemClickedEventArgs(lStream, lCard.ImageElement));
            }
        }

        private void HeaderLink_Click(object sender, RoutedEventArgs e)
        {
            ElementSoundPlayer.Play(ElementSoundKind.Invoke);
            HeaderClicked?.Invoke(this, EventArgs.Empty);
        }

        private void PosterCard_HoverStarted(object sender, EventArgs e)
        {
            if (sender is FrameworkElement card)
            {
                HoverStarted?.Invoke(this, card);
            }
        }

        private void PosterCard_HoverEnded(object sender, EventArgs e)
        {
            if (sender is FrameworkElement card)
            {
                HoverEnded?.Invoke(this, card);
            }
        }
        
        private void LandscapeCard_HoverStarted(object sender, EventArgs e) => PosterCard_HoverStarted(sender, e);
        private void LandscapeCard_HoverEnded(object sender, EventArgs e) => PosterCard_HoverEnded(sender, e);

        // ==========================================
        // SCROLL & DRAG LOGIC
        // ==========================================
        private ScrollViewer _scrollViewer;
        private DispatcherTimer _scrollEndTimer;
        private bool _isScrolling = false;

        private void Repeater_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            ScrollStarted?.Invoke(this, EventArgs.Empty);
        }

        private void Repeater_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            if (ItemsScrollViewer != null)
            {
                // [NATIVE FIX] Using ManipulationDelta ensures proper gesture railing
                ItemsScrollViewer.ChangeView(ItemsScrollViewer.HorizontalOffset - e.Delta.Translation.X, null, null, true);
            }
        }

        private void Repeater_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            ScrollEnded?.Invoke(this, EventArgs.Empty);
        }

        private void ScrollLeft_Click(object sender, RoutedEventArgs e)
        {
            if (ItemsScrollViewer != null)
            {
                double target = Math.Max(0, ItemsScrollViewer.HorizontalOffset - 500);
                ItemsScrollViewer.ChangeView(target, null, null);
            }
        }

        private void ScrollRight_Click(object sender, RoutedEventArgs e)
        {
            if (ItemsScrollViewer != null)
            {
                double target = Math.Min(ItemsScrollViewer.ScrollableWidth, ItemsScrollViewer.HorizontalOffset + 500);
                ItemsScrollViewer.ChangeView(target, null, null);
            }
        }

        private void EnsureScrollViewer()
        {
            if (_scrollViewer == null && ItemsScrollViewer != null)
            {
                _scrollViewer = ItemsScrollViewer;
                _scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
            }
        }

        private void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!_isScrolling)
            {
                _isScrolling = true;
                ScrollStarted?.Invoke(this, EventArgs.Empty);
            }

            // Debounce scroll end
            if (_scrollEndTimer == null)
            {
                _scrollEndTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _scrollEndTimer.Tick += (s, args) =>
                {
                    _scrollEndTimer.Stop();
                    _isScrolling = false;
                    ScrollEnded?.Invoke(this, EventArgs.Empty);

                    if (_scrollViewer != null && _scrollViewer.ScrollableWidth > 0 && !IsLoadingMore)
                    {
                        // Enable early trigger point so user doesn't hit a wall
                        // 1500 pixels is roughly 4-5 cards ahead
                        if (_scrollViewer.HorizontalOffset >= _scrollViewer.ScrollableWidth - 1500)
                        {
                            IsLoadingMore = true;
                            LoadMoreAction?.Invoke(this, EventArgs.Empty);
                            
                            // Reset flag after a delay to allow data to load
                            var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                            resetTimer.Tick += (resetS, resetArgs) =>
                            {
                                resetTimer.Stop();
                                IsLoadingMore = false;
                            };
                            resetTimer.Start();
                        }
                    }
                };
            }
            _scrollEndTimer.Stop();
            _scrollEndTimer.Start();
        }

        private void CatalogRow_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
                _scrollViewer = null;
            }

            if (_scrollEndTimer != null)
            {
                _scrollEndTimer.Stop();
                _scrollEndTimer = null;
            }
        }

        private void ItemsScrollViewer_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureScrollViewer();
            if (ItemsScrollViewer != null)
            {
                // [FIX] Manually bubble vertical wheel events. 
                ItemsScrollViewer.PointerWheelChanged += (s, args) =>
                {
                    var props = args.GetCurrentPoint(ItemsScrollViewer).Properties;
                    if (!props.IsHorizontalMouseWheel)
                    {
                        args.Handled = false;
                    }
                };

                ItemsScrollViewer.ViewChanged += (s, args) => UpdateButtonVisibility();
            }
        }

        private void UpdateButtonVisibility()
        {
            if (ItemsScrollViewer == null) return;
            
            // We use Opacity or Visibility here to complement the VisualStateManager
            // Actually, we'll just set IsEnabled or a separate Visibility flag 
            // but since we have a Storyboard, let's just adjust the target visibility.
            bool canScrollLeft = ItemsScrollViewer.HorizontalOffset > 10;
            bool canScrollRight = ItemsScrollViewer.HorizontalOffset < (ItemsScrollViewer.ScrollableWidth - 10);

            ScrollLeftButton.IsEnabled = canScrollLeft;
            ScrollRightButton.IsEnabled = canScrollRight;
            
            // Optional: Hard hide if they aren't usable
            ScrollLeftButton.Visibility = canScrollLeft ? Visibility.Visible : Visibility.Collapsed;
            ScrollRightButton.Visibility = canScrollRight ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Repeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            // [ROOT FIX] Set MediaStream directly from the managed ItemsSource using the index.
            // The DataTemplate is now "thin" (no x:DataType, no {x:Bind} property bindings),
            // so this is the single point where cards receive their data.
            // This completely bypasses XAML-generated interface casts and WinRT proxy issues.
            IMediaStream stream = null;
            if (sender.ItemsSource is System.Collections.IList list && args.Index >= 0 && args.Index < list.Count)
            {
                stream = list[args.Index] as IMediaStream;
            }

            // Connect events and set data on the created element
            if (args.Element is PosterCard poster)
            {
                if (stream != null) 
                {
                    poster.MediaStream = stream;
                    poster.DataContext = stream; // [DEEPER FIX] ExpandedCard relies on DataContext
                }
                poster.Tapped -= PosterCard_Tapped;
                poster.Tapped += PosterCard_Tapped;
                poster.HoverStarted -= PosterCard_HoverStarted;
                poster.HoverStarted += PosterCard_HoverStarted;
                poster.HoverEnded -= PosterCard_HoverEnded;
                poster.HoverEnded += PosterCard_HoverEnded;
            }
            else if (args.Element is LandscapeCard landscape)
            {
                if (stream != null) 
                {
                    landscape.MediaStream = stream;
                    landscape.DataContext = stream; // [DEEPER FIX]
                }
                landscape.Tapped -= PosterCard_Tapped;
                landscape.Tapped += PosterCard_Tapped;
                landscape.HoverStarted -= LandscapeCard_HoverStarted;
                landscape.HoverStarted += LandscapeCard_HoverStarted;
                landscape.HoverEnded -= LandscapeCard_HoverEnded;
                landscape.HoverEnded += LandscapeCard_HoverEnded;
            }
        }

        private void Repeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            // [VIRTUALIZATION STABILITY]
            // We NO LONGER clear the ImageElement.Source here.
            // ItemsRepeater recycles these controls; by leaving the Source intact,
            // we allow the UI to show the previous image instantly if the same item
            // scrolls back into view, and avoid white flickers during the reuse phase.
            if (args.Element is PosterCard poster)
            {
                poster.Tapped -= PosterCard_Tapped;
                poster.HoverStarted -= PosterCard_HoverStarted;
                poster.HoverEnded -= PosterCard_HoverEnded;
            }
            else if (args.Element is LandscapeCard landscape)
            {
                landscape.Tapped -= PosterCard_Tapped;
                landscape.HoverStarted -= LandscapeCard_HoverStarted;
                landscape.HoverEnded -= LandscapeCard_HoverEnded;
            }
        }



        private void RootPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            VisualStateManager.GoToState(this, "PointerOver", true);
        }

        private void RootPanel_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            VisualStateManager.GoToState(this, "Normal", true);
        }
        private void Button_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            // When mouse enters a navigation button, we want to suppress any pending expanded card triggers 
            // from the posters that might be behind it.
            HoverEnded?.Invoke(this, null);
        }
    }
}
