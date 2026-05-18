using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using ModernIPTVPlayer.Controls;
using ModernIPTVPlayer.Services;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage : Page
    {
        #region Page Skeleton Revealer & Shimmer Management

        internal async Task PrepareInfoSkeletonAsync()
        {
            if (!this.IsLoaded || _pageCts?.IsCancellationRequested == true) return;

            OnViewportChanged();
            if (!TryUpdateLayout(this, nameof(PrepareInfoSkeletonAsync))) return;
            
            // If ActualWidth is still 0, WinUI hasn't performed the layout pass yet.
            if (this.ActualWidth <= 0)
            {
                await Task.Yield();
                if (!this.IsLoaded || _pageCts?.IsCancellationRequested == true) return;
                TryUpdateLayout(this, nameof(PrepareInfoSkeletonAsync));
            }

            MatchTitleSkeletonToContent();
            MatchSkeletonToContent(TechBadgesShimmer, TechBadgesContent, minWidth: 0, minHeight: 22, collapseWhenContentHidden: true);
            MatchSkeletonToContent(MetadataShimmer, MetadataPanel, minWidth: 108, minHeight: 22);
            RebuildActionBarSkeletonFromButtons();
            RebuildOverviewSkeletonFromText();
            
            if (_pageCts?.IsCancellationRequested == true) return;

            if (CastSection != null && CastShimmer != null)
            {
                AdjustCastShimmer(CastList.Count);
                MatchSkeletonToContent(CastShimmer, CastSection, minWidth: 180, minHeight: 145);
                _visualStateController.SetState(CastShimmer, Visibility.Visible, 1.0);
            }
            else if (CastShimmer != null)
            {
                _visualStateController.Collapse(CastShimmer);
            }

            if (DirectorSection != null && DirectorShimmer != null)
            {
                AdjustDirectorShimmer(DirectorList.Count);
                MatchSkeletonToContent(DirectorShimmer, DirectorSection, minWidth: 180, minHeight: 145);
                _visualStateController.SetState(DirectorShimmer, Visibility.Visible, 1.0);
            }
            else if (DirectorShimmer != null)
            {
                _visualStateController.Collapse(DirectorShimmer);
            }
        }

        internal static bool TryUpdateLayout(FrameworkElement element, string caller)
        {
            if (element == null || !element.IsLoaded) return false;
            try
            {
                element.UpdateLayout();
                return true;
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80004002)
            {
                return false;
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error($"UpdateLayout error in {caller}", ex);
                return false;
            }
        }

        private void ShowInitialPeopleSkeletons()
        {
            bool isWide = ActualWidth >= LayoutAdaptiveThreshold;
            if (!isWide) return;

            bool isLoading = _pageLoadState == PageLoadState.Loading;
            if (CastList?.Count > 0 || isLoading)
            {
                AdjustCastShimmer(CastList?.Count ?? 0);
                _visualStateController.SetState(CastShimmer, Visibility.Visible, 1.0);
            }
            else if (CastShimmer != null)
            {
                _visualStateController.Collapse(CastShimmer);
            }

            if (DirectorList?.Count > 0 || isLoading)
            {
                AdjustDirectorShimmer(DirectorList?.Count ?? 0);
                _visualStateController.SetState(DirectorShimmer, Visibility.Visible, 1.0);
            }
            else if (DirectorShimmer != null)
            {
                _visualStateController.Collapse(DirectorShimmer);
            }
        }

        private void RebuildActionBarSkeletonFromButtons()
        {
            if (ActionBarShimmer == null || ActionBarPanel == null) return;

            ActionBarShimmer.Children.Clear();
            ActionBarShimmer.Spacing = ActionBarPanel.Spacing;
            ActionBarShimmer.HorizontalAlignment = ActionBarPanel.HorizontalAlignment;
            ActionBarShimmer.VerticalAlignment = ActionBarPanel.VerticalAlignment;
            ActionBarShimmer.Margin = ActionBarPanel.Margin;

            var buttons = new[] { PlayButton, RestartButton, TrailerButton, DownloadButton, CopyLinkButton, WatchlistButton }
                .Where(b => b != null && b.Visibility == Visibility.Visible)
                .ToList();

            if (buttons.Count == 0 && PlayButton != null)
            {
                buttons.Add(PlayButton);
            }

            foreach (var button in buttons)
            {
                double width = button.ActualWidth > 1 ? button.ActualWidth : button.Width;
                double height = button.ActualHeight > 1 ? button.ActualHeight : button.Height;

                if (double.IsNaN(width) || width <= 1)
                {
                    bool isPrimary = button == PlayButton || button == RestartButton;
                    width = isPrimary ? 142 : 52;
                }

                if (double.IsNaN(height) || height <= 1)
                {
                    height = 52;
                }

                var radius = button.CornerRadius.TopLeft > 0
                    ? button.CornerRadius
                    : new CornerRadius(height / 2);

                ActionBarShimmer.Children.Add(new ShimmerControl
                {
                    Width = Math.Ceiling(width),
                    Height = Math.Ceiling(height),
                    CornerRadius = radius,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            ActionBarShimmer.Width = ActionBarPanel.ActualWidth > 1
                ? Math.Ceiling(ActionBarPanel.ActualWidth)
                : double.NaN;
            ActionBarShimmer.Height = ActionBarPanel.ActualHeight > 1
                ? Math.Ceiling(ActionBarPanel.ActualHeight)
                : (PlayButton?.ActualHeight > 1 ? Math.Ceiling(PlayButton.ActualHeight) : 52);
            _visualStateController.SetState(ActionBarShimmer, Visibility.Visible, 1.0);
        }

        private void RebuildOverviewSkeletonFromText()
        {
            if (OverviewShimmer == null || OverviewPanel == null || OverviewText == null) return;

            OverviewShimmer.Children.Clear();
            OverviewShimmer.HorizontalAlignment = OverviewPanel.HorizontalAlignment;
            OverviewShimmer.VerticalAlignment = OverviewPanel.VerticalAlignment;
            OverviewShimmer.Margin = new Thickness(0, 4, 0, 0);

            double panelWidth = OverviewPanel.ActualWidth > 1 ? OverviewPanel.ActualWidth : InfoColumn?.ActualWidth ?? 0;
            if (panelWidth <= 1)
            {
                panelWidth = ActualWidth >= LayoutAdaptiveThreshold ? 620 : Math.Max(280, ActualWidth - 40);
            }

            double lineHeight = OverviewText.LineHeight > 0 ? OverviewText.LineHeight : OverviewText.FontSize * 1.45;
            double textHeight = OverviewText.ActualHeight;
            if (textHeight <= 1)
            {
                OverviewText.Measure(new Windows.Foundation.Size(panelWidth, double.PositiveInfinity));
                textHeight = OverviewText.DesiredSize.Height;
            }

            int textLineCount = Math.Max(1, (int)Math.Ceiling(textHeight / Math.Max(1, lineHeight)));
            if (OverviewText.MaxLines > 0)
            {
                textLineCount = Math.Min(textLineCount, OverviewText.MaxLines);
            }

            double genreWidth = 0;
            if (GenresText != null && GenresText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(GenresText.Text))
            {
                genreWidth = GenresText.ActualWidth > 1 ? GenresText.ActualWidth : Math.Min(panelWidth * 0.55, 280);
                OverviewShimmer.Children.Add(new ShimmerControl
                {
                    Width = Math.Max(96, Math.Ceiling(genreWidth)),
                    Height = Math.Max(14, Math.Ceiling(GenresText.ActualHeight > 1 ? GenresText.ActualHeight : GenresText.FontSize + 4)),
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            }

            for (int i = 0; i < textLineCount; i++)
            {
                bool isLast = i == textLineCount - 1;
                double widthFactor = isLast ? 0.68 : (i % 3 == 1 ? 0.94 : 1.0);
                OverviewShimmer.Children.Add(new ShimmerControl
                {
                    Width = Math.Max(120, Math.Ceiling(panelWidth * widthFactor)),
                    Height = Math.Max(12, Math.Ceiling(lineHeight * 0.58)),
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            }

            OverviewShimmer.Width = Math.Ceiling(panelWidth);
            OverviewShimmer.Height = double.NaN;
            _visualStateController.SetState(OverviewShimmer, Visibility.Visible, 1.0);
        }

        private void RebuildDefaultOverviewSkeleton()
        {
            if (OverviewShimmer == null) return;

            bool isWide = ActualWidth >= LayoutAdaptiveThreshold;
            double width = InfoColumn?.ActualWidth > 1
                ? InfoColumn.ActualWidth
                : (isWide ? 620 : Math.Max(280, ActualWidth - 40));
            double lineHeight = isWide ? 14 : 13;
            int lines = isWide ? 4 : 5;

            OverviewShimmer.Children.Clear();
            for (int i = 0; i < lines; i++)
            {
                bool isLast = i == lines - 1;
                OverviewShimmer.Children.Add(new ShimmerControl
                {
                    Width = Math.Max(120, Math.Ceiling(width * (isLast ? 0.68 : (i % 2 == 0 ? 1.0 : 0.92)))),
                    Height = lineHeight,
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            }

            OverviewShimmer.Width = Math.Ceiling(width);
            OverviewShimmer.Height = double.NaN;
            _visualStateController.SetState(OverviewShimmer, Visibility.Visible, 1.0);
        }

        public void MatchTitleSkeletonToContent()
        {
            if (IdentityControl == null) return;
            var titleShimmer = IdentityControl.TitleShimmerElement;
            var titlePanel = IdentityControl.TitlePanelElement;
            var logoHost = IdentityControl.LogoHost;

            if (titleShimmer == null) return;

            bool hasLogoSlot = !string.IsNullOrWhiteSpace(_currentLogoUrl) && logoHost != null;
            if (hasLogoSlot)
            {
                double logoWidth = logoHost.ActualWidth > 1 ? logoHost.ActualWidth : logoHost.Width;
                double logoHeight = logoHost.ActualHeight > 1 ? logoHost.ActualHeight : logoHost.Height;
                titleShimmer.Width = Math.Max(220, Math.Ceiling(logoWidth));
                titleShimmer.Height = Math.Max(72, Math.Ceiling(logoHeight));
                titleShimmer.HorizontalAlignment = logoHost.HorizontalAlignment;
                titleShimmer.VerticalAlignment = logoHost.VerticalAlignment;
                _visualStateController.SetState(titleShimmer, Visibility.Visible, 1.0);
                return;
            }

            MatchSkeletonToContent(titleShimmer, titlePanel, minWidth: 260, minHeight: 56);
        }

        private static void MatchSkeletonToContent(
            FrameworkElement skeleton,
            FrameworkElement content,
            double minWidth,
            double minHeight,
            bool collapseWhenContentHidden = false)
        {
            if (skeleton == null || content == null) return;
            if (collapseWhenContentHidden && content.Visibility != Visibility.Visible)
            {
                skeleton.Visibility = Visibility.Collapsed;
                return;
            }

            double width = content.ActualWidth;
            double height = content.ActualHeight;

            if (width <= 1 && content is TextBlock tb)
            {
                width = tb.DesiredSize.Width;
                height = tb.DesiredSize.Height;
            }

            if (width > 1)
            {
                skeleton.Width = Math.Max(minWidth, Math.Ceiling(width));
            }

            if (height > 1)
            {
                skeleton.Height = Math.Max(minHeight, Math.Ceiling(height));
            }

            skeleton.HorizontalAlignment = content.HorizontalAlignment;
            skeleton.VerticalAlignment = content.VerticalAlignment;
            skeleton.Opacity = 1;
            skeleton.Visibility = Visibility.Visible;
        }

        private void AdjustCastShimmer(int count)
        {
            if (CastShimmer == null) return;
            
            int effectiveCount = count <= 0 ? 5 : count;
            int displayCount = Math.Min(effectiveCount, 8); 

            if (_lastAdjustedCastCount == displayCount) return;
            _lastAdjustedCastCount = displayCount;

            DispatcherQueue.TryEnqueue(() => 
            {
                if (CastShimmer.Children.Count >= 2 && CastShimmer.Children[1] is StackPanel horizontalPanel)
                {
                    horizontalPanel.Children.Clear();
                    for (int i = 0; i < displayCount; i++)
                    {
                        var itemStack = new StackPanel { Spacing = 6 };
                        itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 100, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left });
                        itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left });
                        horizontalPanel.Children.Add(itemStack);
                    }
                }
            });
        }

        private void AdjustDirectorShimmer(int count)
        {
            if (DirectorShimmer == null) return;
            
            int effectiveCount = count <= 0 ? 2 : count;
            int displayCount = Math.Min(effectiveCount, 4); 

            if (_lastAdjustedDirectorCount == displayCount) return;
            _lastAdjustedDirectorCount = displayCount;

            DispatcherQueue.TryEnqueue(() => 
            {
                if (DirectorShimmer.Children.Count >= 2 && DirectorShimmer.Children[1] is StackPanel horizontalPanel)
                {
                    horizontalPanel.Children.Clear();
                    for (int i = 0; i < displayCount; i++)
                    {
                        var itemStack = new StackPanel { Spacing = 6 };
                        itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 100, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left });
                        itemStack.Children.Add(new ShimmerControl { Width = 80, Height = 12, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left });
                        horizontalPanel.Children.Add(itemStack);
                    }
                }
            });
        }

        #endregion
    }
}
