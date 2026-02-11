using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using ModernIPTVPlayer.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;

namespace ModernIPTVPlayer.Controls
{

    public sealed partial class CatalogRow : UserControl
    {
        public static readonly DependencyProperty CatalogNameProperty =
            DependencyProperty.Register("CatalogName", typeof(string), typeof(CatalogRow), new PropertyMetadata(string.Empty));

        public string CatalogName
        {
            get => (string)GetValue(CatalogNameProperty);
            set => SetValue(CatalogNameProperty, value);
        }

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register("ItemsSource", typeof(object), typeof(CatalogRow), new PropertyMetadata(null));

        public object ItemsSource
        {
            get => GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
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
            if (ShimmerPanel == null || ItemsListView == null || RowTitle == null) return;

            if (IsLoading)
            {
                ShimmerPanel.Visibility = Visibility.Visible;
                ItemsListView.Visibility = Visibility.Collapsed;
                RowTitle.Opacity = 0.5;
            }
            else
            {
                ShimmerPanel.Visibility = Visibility.Collapsed;
                ItemsListView.Visibility = Visibility.Visible;
                RowTitle.Opacity = 1.0;
            }
        }



        public event EventHandler<IMediaStream> ItemClicked;
        public event EventHandler<PosterCard> HoverStarted;
        public event EventHandler<PosterCard> HoverEnded;
        public event EventHandler ScrollStarted;
        public event EventHandler ScrollEnded;

        public CatalogRow()
        {
            this.InitializeComponent();
            this.Loaded += CatalogRow_Loaded;
        }

        private void CatalogRow_Loaded(object sender, RoutedEventArgs e)
        {
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

        private void ItemsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is IMediaStream stream)
            {
                ItemClicked?.Invoke(this, stream);
            }
        }

        private void PosterCard_HoverStarted(object sender, EventArgs e)
        {
            if (sender is PosterCard card)
            {
                HoverStarted?.Invoke(this, card);
            }
        }

        private void PosterCard_HoverEnded(object sender, EventArgs e)
        {
            if (sender is PosterCard card)
            {
                HoverEnded?.Invoke(this, card);
            }
        }

        // ==========================================
        // SCROLL & DRAG LOGIC
        // ==========================================
        private ScrollViewer _scrollViewer;
        private DispatcherTimer _scrollEndTimer;
        private bool _isScrolling = false;

        private void ScrollLeft_Click(object sender, RoutedEventArgs e)
        {
            EnsureScrollViewer();
            if (_scrollViewer != null)
            {
                double target = Math.Max(0, _scrollViewer.HorizontalOffset - 500);
                _scrollViewer.ChangeView(target, null, null);
            }
        }

        private void ScrollRight_Click(object sender, RoutedEventArgs e)
        {
            EnsureScrollViewer();
            if (_scrollViewer != null)
            {
                double target = Math.Min(_scrollViewer.ScrollableWidth, _scrollViewer.HorizontalOffset + 500);
                _scrollViewer.ChangeView(target, null, null);
            }
        }

        private void EnsureScrollViewer()
        {
            if (_scrollViewer == null)
            {
                _scrollViewer = FindScrollViewer(ItemsListView);
                if (_scrollViewer != null)
                {
                    _scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
                }
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
                };
            }
            _scrollEndTimer.Stop();
            _scrollEndTimer.Start();
        }

        private ScrollViewer FindScrollViewer(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer sv) return sv;
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        private void ItemsListView_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            // Handled by ScrollViewer_ViewChanged
        }

        private void ItemsListView_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            EnsureScrollViewer();
            if (_scrollViewer != null)
            {
                // Smooth drag for mouse and touch (Using ChangeView for offset)
                _scrollViewer.ChangeView(_scrollViewer.HorizontalOffset - e.Delta.Translation.X, null, null, true);
            }
        }

        private void ItemsListView_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            ScrollEnded?.Invoke(this, EventArgs.Empty);
        }

        private void RootPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            VisualStateManager.GoToState(this, "PointerOver", true);
        }

        private void RootPanel_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            VisualStateManager.GoToState(this, "Normal", true);
        }
    }
}
