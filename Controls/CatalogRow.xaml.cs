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

        private void UpdateLoadingState()
        {
            if (ShimmerPanel == null || ItemsScrollViewer == null) return;

            // 1. Lifecycle State (Loading vs Content)
            string loadingState = IsLoading ? "Loading" : "ContentReady";
            VisualStateManager.GoToState(this, loadingState, true);

            // 2. Structural State (Standard vs Landscape)
            VisualStateManager.GoToState(this, RowStyle ?? "Standard", true);

            // [VIRTUALIZATION FIX] Force layout pass to ensure items appear immediately
            if (!IsLoading)
            {
                Repeater?.InvalidateMeasure();
            }
        }

        public ItemsRepeater RepeaterControl => Repeater;



        public event EventHandler<(IMediaStream Stream, UIElement SourceElement)> ItemClicked;
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
            
            // [SYNC] Ensure initial loading state is correctly reflected (Essential for Pre-loaded Phase 0 rows)
            UpdateLoadingState();

            // Simple entrance animation
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var opacityAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(600) };
            var translateAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(600) };
            translateAnim.EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };

            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(opacityAnim, RootPanel);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(translateAnim, EntranceTranslation);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(translateAnim, "Y");

            sb.Children.Add(opacityAnim);
            sb.Children.Add(translateAnim);
            sb.Begin();
        }

        private void PosterCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is PosterCard card && card.DataContext is IMediaStream stream)
            {
                ElementSoundPlayer.Play(ElementSoundKind.Invoke);
                // Pass the internal ImageElement for ConnectedAnimation
                ItemClicked?.Invoke(this, (stream, card.ImageElement));
            }
            else if (sender is LandscapeCard lCard && lCard.DataContext is IMediaStream lStream)
            {
                ElementSoundPlayer.Play(ElementSoundKind.Invoke);
                ItemClicked?.Invoke(this, (lStream, lCard.ImageElement));
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
        }

        private void Repeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            // Connect events to the created element if it's a known card type
            if (args.Element is PosterCard poster)
            {
                poster.Tapped -= PosterCard_Tapped;
                poster.Tapped += PosterCard_Tapped;
                poster.HoverStarted -= PosterCard_HoverStarted;
                poster.HoverStarted += PosterCard_HoverStarted;
                poster.HoverEnded -= PosterCard_HoverEnded;
                poster.HoverEnded += PosterCard_HoverEnded;
            }
            else if (args.Element is LandscapeCard landscape)
            {
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

        private void Repeater_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            if (ItemsScrollViewer != null)
            {
                ItemsScrollViewer.ChangeView(ItemsScrollViewer.HorizontalOffset - e.Delta.Translation.X, null, null, true);
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
