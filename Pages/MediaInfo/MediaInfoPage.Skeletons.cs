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

            bool isLoading = _pageLoadState == PageLoadState.Loading;

            if (isLoading)
            {
                PrepareDefaultTitleSkeleton();
                PrepareDefaultBadgesSkeleton();
                PrepareDefaultActionBarSkeleton();
                RebuildDefaultOverviewSkeleton();
                PrepareDefaultPeopleSkeletons();

                bool isSeries = ModernIPTVPlayer.Helpers.StreamHelper.IsSeriesItem(_item);
                if (isSeries)
                {
                    _isEpisodesLoading = true;
                    var placeholders = _detailPanelController?.CreateEpisodePlaceholders(5);
                    if (placeholders != null)
                    {
                        CurrentEpisodes.Clear();
                        foreach (var p in placeholders) CurrentEpisodes.Add(p);
                    }
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        OpenEpisodesPanel(PanelChangeReason.SeriesDefaultEpisodes);
                    });
                }
            }
            else
            {
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
        }

        #region Title & Logo Skeletons

        /// <summary>
        /// Unifies evaluation and layout matching for the media title or logo shimmer block.
        /// </summary>
        private void SetupTitleSkeleton(bool isDynamicMatch)
        {
            if (IdentityControl == null) return;
            var titleShimmer = IdentityControl.TitleShimmerElement;
            var titlePanel = IdentityControl.TitlePanelElement;
            var logoHost = IdentityControl.LogoHost;

            if (titleShimmer == null) return;

            if (titlePanel != null)
            {
                _visualStateController.SetOpacity(titlePanel, 0.0);
            }

            bool hasLogoSlot = !string.IsNullOrWhiteSpace(_currentLogoUrl) && logoHost != null;
            if (hasLogoSlot)
            {
                double logoWidth = logoHost.ActualWidth > 1 ? logoHost.ActualWidth : logoHost.Width;
                double logoHeight = logoHost.ActualHeight > 1 ? logoHost.ActualHeight : logoHost.Height;
                if (double.IsNaN(logoWidth) || logoWidth <= 1) logoWidth = 220;
                if (double.IsNaN(logoHeight) || logoHeight <= 1) logoHeight = 72;

                titleShimmer.Width = isDynamicMatch ? Math.Max(220, Math.Ceiling(logoWidth)) : logoWidth;
                titleShimmer.Height = isDynamicMatch ? Math.Max(72, Math.Ceiling(logoHeight)) : logoHeight;
                titleShimmer.HorizontalAlignment = logoHost.HorizontalAlignment;
                titleShimmer.VerticalAlignment = logoHost.VerticalAlignment;
                _visualStateController.SetState(titleShimmer, Visibility.Visible, 1.0);
            }
            else
            {
                if (isDynamicMatch)
                {
                    MatchSkeletonToContent(titleShimmer, titlePanel, minWidth: 260, minHeight: 56);
                }
                else
                {
                    titleShimmer.Width = 340;
                    titleShimmer.Height = 44;
                    titleShimmer.HorizontalAlignment = HorizontalAlignment.Left;
                    titleShimmer.VerticalAlignment = VerticalAlignment.Center;
                    _visualStateController.SetState(titleShimmer, Visibility.Visible, 1.0);
                }
            }
        }

        private void PrepareDefaultTitleSkeleton() => SetupTitleSkeleton(isDynamicMatch: false);
        public void MatchTitleSkeletonToContent() => SetupTitleSkeleton(isDynamicMatch: true);

        #endregion

        #region Action Bar Skeletons

        private void PrepareDefaultActionBarSkeleton()
        {
            if (ActionBarShimmer == null || ActionBarPanel == null) return;

            ActionBarShimmer.Children.Clear();
            ActionBarShimmer.Spacing = ActionBarPanel.Spacing > 0 ? ActionBarPanel.Spacing : 12;
            ActionBarShimmer.HorizontalAlignment = ActionBarPanel.HorizontalAlignment;
            ActionBarShimmer.VerticalAlignment = ActionBarPanel.VerticalAlignment;
            ActionBarShimmer.Margin = ActionBarPanel.Margin;

            // 1. Play Button Shimmer (Primary)
            ActionBarShimmer.Children.Add(new ShimmerControl
            {
                Width = 150,
                Height = 52,
                CornerRadius = new CornerRadius(26),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            });

            // 2. Trailer Button Shimmer
            ActionBarShimmer.Children.Add(new ShimmerControl
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(26),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            });

            // 3. Download Button Shimmer
            ActionBarShimmer.Children.Add(new ShimmerControl
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(26),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            });

            // 4. Copy Link Button Shimmer
            ActionBarShimmer.Children.Add(new ShimmerControl
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(26),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            });

            // 5. Watchlist Button Shimmer
            ActionBarShimmer.Children.Add(new ShimmerControl
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(26),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            });

            ActionBarShimmer.Width = double.NaN;
            ActionBarShimmer.Height = 52;
            _visualStateController.SetState(ActionBarShimmer, Visibility.Visible, 1.0);
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
                    width = isPrimary ? 150 : 52;
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

        #endregion

        #region Overview & Metadata Ribbon Skeletons

        private void PrepareDefaultBadgesSkeleton()
        {
            if (MetadataShimmer != null)
            {
                MetadataShimmer.Width = 180;
                MetadataShimmer.Height = 22;
                _visualStateController.SetState(MetadataShimmer, Visibility.Visible, 1.0);
            }

            if (TechBadgesShimmer != null)
            {
                TechBadgesShimmer.Width = 120;
                TechBadgesShimmer.Height = 22;
                _visualStateController.SetState(TechBadgesShimmer, Visibility.Visible, 1.0);
            }
        }

        /// <summary>
        /// Unifies structural creation of the overview text shimmer lines.
        /// </summary>
        private void SetupOverviewSkeleton(double panelWidth, double lineHeight, int lines, bool includeGenre)
        {
            if (OverviewShimmer == null) return;

            OverviewShimmer.Children.Clear();
            OverviewShimmer.HorizontalAlignment = HorizontalAlignment.Left;
            OverviewShimmer.VerticalAlignment = OverviewPanel?.VerticalAlignment ?? VerticalAlignment.Top;
            OverviewShimmer.Margin = new Thickness(0, 4, 0, 0);

            if (includeGenre && GenresText != null && GenresText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(GenresText.Text))
            {
                double genreWidth = GenresText.ActualWidth > 1 ? GenresText.ActualWidth : Math.Min(panelWidth * 0.55, 280);
                OverviewShimmer.Children.Add(new ShimmerControl
                {
                    Width = Math.Max(96, Math.Ceiling(genreWidth)),
                    Height = Math.Max(14, Math.Ceiling(GenresText.ActualHeight > 1 ? GenresText.ActualHeight : GenresText.FontSize + 4)),
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            }

            for (int i = 0; i < lines; i++)
            {
                bool isLast = i == lines - 1;
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
            bool isWide = ActualWidth >= LayoutAdaptiveThreshold;
            double width = InfoColumn?.ActualWidth > 280 ? InfoColumn.ActualWidth : (isWide ? 620 : Math.Max(280, ActualWidth - 40));
            double lineHeight = isWide ? 24 : 22;
            int lines = isWide ? 4 : 5;
            SetupOverviewSkeleton(width, lineHeight, lines, includeGenre: false);
        }

        private void RebuildOverviewSkeletonFromText()
        {
            if (OverviewPanel == null || OverviewText == null) return;

            double panelWidth = OverviewPanel.ActualWidth > 280 ? OverviewPanel.ActualWidth : (InfoColumn?.ActualWidth > 280 ? InfoColumn.ActualWidth : 0);
            if (panelWidth <= 280)
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

            SetupOverviewSkeleton(panelWidth, lineHeight, textLineCount, includeGenre: true);
        }

        #endregion

        #region Cast & Crew / People Skeletons

        /// <summary>
        /// Shared setup helper for managing dynamic cast/director segment skeletons and transitions.
        /// </summary>
        private void SetupPeopleSectionSkeleton(StackPanel shimmer, Action<int> adjustShimmer, int itemCount, bool showList, bool shouldShow)
        {
            if (shimmer == null) return;

            if (shouldShow)
            {
                adjustShimmer(itemCount);
                if (shimmer.Children.Count >= 2 && shimmer.Children[1] is FrameworkElement profilePanel)
                {
                    profilePanel.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
                }
                _visualStateController.SetState(shimmer, Visibility.Visible, 1.0);
            }
            else
            {
                _visualStateController.Collapse(shimmer);
            }
        }

        private void PrepareDefaultPeopleSkeletons()
        {
            bool showList = GetViewportHeight() >= 720;
            SetupPeopleSectionSkeleton(CastShimmer, AdjustCastShimmer, 0, showList, true);
            SetupPeopleSectionSkeleton(DirectorShimmer, AdjustDirectorShimmer, 0, showList, true);
        }

        private void ShowInitialPeopleSkeletons()
        {
            bool isWide = ActualWidth >= LayoutAdaptiveThreshold;
            if (!isWide) return;

            bool showList = GetViewportHeight() >= 720;
            bool isLoading = _pageLoadState == PageLoadState.Loading;

            SetupPeopleSectionSkeleton(CastShimmer, AdjustCastShimmer, CastList?.Count ?? 0, showList, CastList?.Count > 0 || isLoading);
            SetupPeopleSectionSkeleton(DirectorShimmer, AdjustDirectorShimmer, DirectorList?.Count ?? 0, showList, DirectorList?.Count > 0 || isLoading);
        }

        private void AdjustCastShimmer(int count) => AdjustPeopleShimmer(CastShimmer, count, 5, 8, ref _lastAdjustedCastCount);
        private void AdjustDirectorShimmer(int count) => AdjustPeopleShimmer(DirectorShimmer, count, 2, 4, ref _lastAdjustedDirectorCount);

        private void AdjustPeopleShimmer(StackPanel shimmer, int count, int defaultCount, int maxCount, ref int lastAdjusted)
        {
            if (shimmer == null) return;

            int effectiveCount = count <= 0 ? defaultCount : count;
            int displayCount = Math.Min(effectiveCount, maxCount);

            if (lastAdjusted == displayCount) return;
            lastAdjusted = displayCount;

            if (shimmer.Children.Count >= 2 && shimmer.Children[1] is StackPanel horizontalPanel)
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
        }

        #endregion

        #region Common Layout Helpers

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

        #endregion

        #endregion
    }
}
