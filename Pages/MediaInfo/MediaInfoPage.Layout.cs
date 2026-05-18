using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer
{
    /// <summary>
    /// Partial class managing the responsive layout math, coordinate calculations, and composition accordion animations.
    /// </summary>
    public sealed partial class MediaInfoPage : Page
    {
        #region Info Layout Helpers

        /// <summary>
        /// Controls visual visibility and opacity for action button sub-text blocks.
        /// </summary>
        private void SetActionTextVisible(FrameworkElement textHost, bool visible)
        {
            if (textHost == null) return;

            textHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            textHost.Opacity = visible ? 1 : 0;
            
            var visual = ElementCompositionPreview.GetElementVisual(textHost);
            if (visual != null)
            {
                visual.Opacity = visible ? 1f : 0f;
            }
        }

        /// <summary>
        /// Estimates the combined text width of action titles and subtexts.
        /// </summary>
        private double EstimateActionTextWidth(TextBlock primaryText, TextBlock secondaryText, bool showSecondary)
        {
            double primaryWidth = EstimateTextWidth(primaryText?.Text, primaryText?.FontSize ?? 14, 0.58);
            if (!showSecondary)
            {
                return primaryWidth;
            }

            double secondaryWidth = EstimateTextWidth(secondaryText?.Text, secondaryText?.FontSize ?? 11, 0.54);
            return Math.Max(primaryWidth, secondaryWidth);
        }

        /// <summary>
        /// Simple character-width measurement fallback to prevent direct XAML layout passes.
        /// </summary>
        private static double EstimateTextWidth(string text, double fontSize, double averageGlyphFactor)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            return Math.Ceiling(text.Length * fontSize * averageGlyphFactor);
        }

        /// <summary>
        /// Computes expanded bounds for text action items.
        /// </summary>
        private double GetExpandedActionWidth(double actionSize, double textWidth, double padding, double textGap, double minWidth)
        {
            double rawWidth = PrimaryActionIconWidth + textGap + textWidth + (padding * 2);
            return Math.Ceiling(Math.Max(minWidth, rawWidth));
        }

        /// <summary>
        /// Decides whether actions should collapse to their icon-only state due to viewport space constraints.
        /// </summary>
        private bool ShouldUseIconOnlyActions(
            bool isWide,
            double availableWidth,
            double actionSize,
            double spacing,
            double playExpandedWidth,
            double restartExpandedWidth)
        {
            if (!isWide)
            {
                return true;
            }

            int secondaryButtonCount = CountVisibleActionButtons(TrailerButton, DownloadButton, CopyLinkButton, WatchlistButton);
            bool restartVisible = RestartButton?.Visibility == Visibility.Visible;
            int visibleButtonCount = 1 + secondaryButtonCount + (restartVisible ? 1 : 0);
            double totalSpacing = Math.Max(0, visibleButtonCount - 1) * spacing;
            double secondaryWidth = secondaryButtonCount * actionSize;
            double desiredWidth = playExpandedWidth + secondaryWidth + totalSpacing;

            if (restartVisible)
            {
                desiredWidth += restartExpandedWidth;
            }

            return desiredWidth > availableWidth - ActionBarOverflowGuard;
        }

        /// <summary>
        /// Helper count of visible action buttons in the current state.
        /// </summary>
        private static int CountVisibleActionButtons(params Button[] buttons)
        {
            int count = 0;
            foreach (var button in buttons)
            {
                if (button?.Visibility == Visibility.Visible)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// composition translation slide-in animation for expanded text headers.
        /// </summary>
        private void AnimateActionTextIn(FrameworkElement textHost)
        {
            if (textHost == null || _compositor == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(textHost);
            visual.StopAnimation(nameof(visual.Opacity));
            
            visual.Opacity = 0f;

            var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 0.9f), new Vector2(0.24f, 1f));

            var opacity = _compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(1f, 1f, easing);
            opacity.Duration = TimeSpan.FromMilliseconds(180);
            visual.StartAnimation(nameof(visual.Opacity), opacity);

            var translation = _compositor.CreateVector3KeyFrameAnimation();
            translation.InsertKeyFrame(1f, Vector3.Zero, easing);
            translation.Duration = TimeSpan.FromMilliseconds(220);
            CompositionService.StartTranslationAnimation(textHost, translation, new Vector3(12f, 0f, 0f));
        }

        /// <summary>
        /// composition slide-out translation animation when button text is collapsed.
        /// </summary>
        private void AnimateActionTextOut(FrameworkElement textHost)
        {
            if (textHost == null || _compositor == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(textHost);
            visual.StopAnimation(nameof(visual.Opacity));

            var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 0.4f));

            var opacity = _compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(1f, 0f, easing);
            opacity.Duration = TimeSpan.FromMilliseconds(90);
            visual.StartAnimation(nameof(visual.Opacity), opacity);

            var translation = _compositor.CreateVector3KeyFrameAnimation();
            translation.InsertKeyFrame(1f, new Vector3(8f, 0f, 0f), easing);
            translation.Duration = TimeSpan.FromMilliseconds(110);
            CompositionService.StartTranslationAnimation(textHost, translation, Vector3.Zero);
        }

        /// <summary>
        /// Stretches scale animations when action clicks finish.
        /// </summary>
        private void AnimateActionButtonSettle(Button button)
        {
            if (button == null || _compositor == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(button);
            visual.StopAnimation(nameof(visual.Scale));
            visual.CenterPoint = new Vector3((float)button.ActualWidth / 2f, (float)button.ActualHeight / 2f, 0f);
            visual.Scale = new Vector3(0.96f, 0.96f, 1f);

            var spring = _compositor.CreateSpringVector3Animation();
            spring.FinalValue = new Vector3(1f, 1f, 1f);
            spring.DampingRatio = 0.78f;
            spring.Period = TimeSpan.FromMilliseconds(55);
            visual.StartAnimation(nameof(visual.Scale), spring);
        }

        /// <summary>
        /// Simple helper to animate opacity of UIElement using Composition.
        /// </summary>
        private void AnimateOpacity(UIElement element, float opacity, int milliseconds)
        {
            if (element == null)
            {
                return;
            }

            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation(nameof(visual.Opacity));
            element.Opacity = 1;

            if (_compositor == null || milliseconds <= 0)
            {
                element.Opacity = opacity;
                visual.Opacity = opacity;
                return;
            }

            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1.0f, opacity);
            animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
            visual.StartAnimation(nameof(visual.Opacity), animation);
        }

        /// <summary>
        /// Coordinates physical layout transitions for action buttons between collapsed and expanded states.
        /// </summary>
        private void ApplyPrimaryActionButton(
            Button button,
            FrameworkElement textHost,
            double actionSize,
            double expandedWidth,
            bool iconOnly,
            double expandedPadding,
            ref bool lastIconOnlyState,
            ref int transitionVersion)
        {
            if (button == null) return;

            bool modeChanged = lastIconOnlyState != iconOnly;
            int version = ++transitionVersion;

            if (modeChanged)
            {
                if (iconOnly)
                {
                    // Transitioning to Icon-Only: Start with expanded width and fade out text
                    AnimateActionTextOut(textHost);
                    AnimateButtonWidth(button, actionSize, 300);
                    
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        // Hide text early in the collapse to prevent overlap as width shrinks
                        await Task.Delay(80);
                        
                        int currentVersion = ReferenceEquals(button, PlayButton)
                            ? _playActionTransitionVersion
                            : _restartActionTransitionVersion;

                        if (currentVersion != version) return;

                        SetActionTextVisible(textHost, false);
                        button.Padding = new Thickness(0);
                        AnimateActionButtonSettle(button);
                    });
                }
                else
                {
                    // Transitioning to Expanded: Smoothly expand
                    AnimateButtonWidth(button, expandedWidth, 300);
                    button.Padding = new Thickness(expandedPadding, 0, expandedPadding, 0);
                    
                    // Show textHost immediately and start fade-in animation
                    SetActionTextVisible(textHost, true);
                    AnimateActionTextIn(textHost);
                }
                
                lastIconOnlyState = iconOnly;
            }
            else
            {
                // Stable state update (resizing within the same mode)
                bool isTransitioning = Math.Abs(button.Width - (iconOnly ? actionSize : expandedWidth)) > 5.0 && !double.IsNaN(button.Width);
                
                if (!isTransitioning)
                {
                    double targetWidth = iconOnly ? actionSize : expandedWidth;
                    if (Math.Abs(button.Width - targetWidth) > 0.5 && !double.IsNaN(button.Width))
                    {
                        button.Width = targetWidth;
                    }
                    
                    button.Padding = iconOnly ? new Thickness(0) : new Thickness(expandedPadding, 0, expandedPadding, 0);
                    
                    if (textHost != null)
                    {
                        if (!iconOnly && textHost.Visibility == Visibility.Collapsed)
                            SetActionTextVisible(textHost, true);
                        else if (iconOnly && textHost.Visibility == Visibility.Visible)
                            SetActionTextVisible(textHost, false);
                    }
                }
            }

            button.MinWidth = actionSize;
            button.Height = actionSize;
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.CornerRadius = new CornerRadius(actionSize / 2);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
        }

        /// <summary>
        /// Retrieves the current physical width of the details container viewport.
        /// </summary>
        private double GetInfoPanelWidth()
        {
            if (InfoContainer != null && InfoContainer.ActualWidth > 0)
            {
                return InfoContainer.ActualWidth;
            }
            return _lastReportedWidth > 0 ? _lastReportedWidth : 400;
        }

        /// <summary>
        /// Evaluates all viewport boundaries and executes priority responsive alignments on UI controls.
        /// </summary>
        private void ApplyInfoPriorityLayout(bool isWide)
        {
            double infoWidth = GetInfoPanelWidth();
            double viewportHeight = GetViewportHeight();
            const double comfortableInfoWidth = 760.0;
            double layoutWidth = isWide ? infoWidth : Math.Min(infoWidth, 430.0);

            double widthFactor = 1.0; 
            double visualFactor = widthFactor;

            bool compactActions = !isWide || layoutWidth < WideInfoCompactThreshold;
            double actionSize = 52; 
            double actionSpacing = isWide ? 12 : 8; 
            double playPadding = isWide ? 18 : 14;  
            double restartPadding = isWide ? 16 : 14;
            bool showPlaySubtext = !string.IsNullOrWhiteSpace(PlayButtonSubtext?.Text);
            
            double playExpandedWidth = GetExpandedActionWidth(
                actionSize,
                EstimateActionTextWidth(PlayButtonText, PlayButtonSubtext, showPlaySubtext),
                playPadding,
                12,
                PrimaryActionMinExpandedWidth);
            
            double restartExpandedWidth = GetExpandedActionWidth(
                actionSize,
                EstimateActionTextWidth(RestartButtonText, null, false),
                restartPadding,
                10,
                RestartActionMinExpandedWidth);
            
            bool iconOnlyPlay = ShouldUseIconOnlyActions(
                isWide,
                Math.Clamp(layoutWidth, 320, 800),
                actionSize,
                actionSpacing,
                playExpandedWidth,
                restartExpandedWidth);
            
            bool hasLogoIdentity = !string.IsNullOrWhiteSpace(_currentLogoUrl) && !_isLogoFallbackActive;
            bool hasEpisodeTitleUnderLogo = hasLogoIdentity && _selectedEpisode != null;
            double logoWidth = isWide ? Math.Round(372 * widthFactor) : 320;
            double logoHeight = hasLogoIdentity
                ? (isWide ? Math.Round(86 * widthFactor) : 78)
                : (isWide ? Math.Round(104 * widthFactor) : 94);
            double peopleHeight = 145;
            bool showPeopleList = isWide && viewportHeight >= WidePeopleComfortHeight;
            double visiblePeopleHeight = showPeopleList ? peopleHeight : 0;
            double peopleSectionWidth = Math.Clamp(layoutWidth, 360, 800);
            double titleFontSize = isWide ? Math.Round(42 * visualFactor) : 28;
            int overviewMaxLines = isWide ? (viewportHeight < 660 ? 5 : 7) : 0;
            
            int layoutSignature = HashCode.Combine(
                HashCode.Combine(
                    HashCode.Combine(
                        HashCode.Combine(
                            HashCode.Combine(
                                isWide,
                                compactActions,
                                iconOnlyPlay,
                                (int)Math.Round(actionSize),
                                (int)Math.Round(playExpandedWidth),
                                (int)Math.Round(restartExpandedWidth),
                                (int)Math.Round(logoWidth),
                                (int)Math.Round(logoHeight)),
                            hasLogoIdentity,
                            hasEpisodeTitleUnderLogo),
                        (int)Math.Round(layoutWidth / 48.0),
                        (int)Math.Round(visiblePeopleHeight)),
                    showPeopleList),
                (int)Math.Round(titleFontSize),
                overviewMaxLines);

            if (_lastInfoLayoutSignature == layoutSignature)
            {
                return;
            }

            _lastInfoLayoutSignature = layoutSignature;

            if (AdaptiveInfoHost != null)
            {
                AdaptiveInfoHost.Width = double.NaN;
                AdaptiveInfoHost.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
                AdaptiveInfoHost.VerticalAlignment = isWide ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            }

            if (InfoContainerInner != null)
            {
                InfoContainerInner.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            }

            if (InfoColumn != null)
            {
                InfoColumn.Width = double.NaN;
                InfoColumn.MaxWidth = isWide ? Math.Clamp(layoutWidth, 360, 800) : 800;
                InfoColumn.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                InfoColumn.Spacing = isWide
                    ? Math.Round((hasLogoIdentity
                        ? (hasEpisodeTitleUnderLogo ? 6 : 10)
                        : (compactActions ? 12 : 16)) * visualFactor)
                    : (hasLogoIdentity ? (hasEpisodeTitleUnderLogo ? 6 : 8) : 12);
            }

            if (IdentityControl != null)
            {
                if (IdentityControl.LogoHost != null)
                {
                    IdentityControl.LogoHost.Width = logoWidth;
                    IdentityControl.LogoHost.Height = logoHeight;
                    IdentityControl.LogoHost.MaxHeight = logoHeight;
                    IdentityControl.LogoHost.MaxWidth = IdentityControl.LogoHost.Width;
                    IdentityControl.LogoHost.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                }

                if (IdentityControl.LogoImage != null)
                {
                    IdentityControl.LogoImage.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                }

                if (IdentityControl.TitlePanelElement != null)
                {
                    IdentityControl.TitlePanelElement.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                    IdentityControl.TitlePanelElement.Spacing = hasEpisodeTitleUnderLogo ? 4 : 0;
                }

                if (IdentityControl.IdentityPanel != null)
                {
                    IdentityControl.IdentityPanel.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                }
            }

            if (_logoBrush != null)
            {
                _logoBrush.HorizontalAlignmentRatio = isWide ? 0.0f : 0.5f;
            }

            if (MetadataRibbon != null)
            {
                MetadataRibbon.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            }

            if (ActionBarGroup != null)
            {
                ActionBarGroup.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                ActionBarGroup.MaxWidth = Math.Clamp(layoutWidth, 320, 800);
            }
            if (ActionBarPanel != null)
            {
                ActionBarPanel.HorizontalAlignment = isWide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                ActionBarPanel.Spacing = actionSpacing;
                ActionBarPanel.MaxWidth = Math.Clamp(layoutWidth, 320, 800);
            }

            ApplyPrimaryActionButton(
                PlayButton,
                PlayButtonTextStack,
                actionSize,
                playExpandedWidth,
                iconOnlyPlay,
                playPadding,
                ref _lastPlayIconOnlyState,
                ref _playActionTransitionVersion);

            if (PlayButtonSubtext != null)
            {
                PlayButtonSubtext.Visibility = !showPlaySubtext || iconOnlyPlay
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            ApplyPrimaryActionButton(
                RestartButton,
                RestartButtonTextStack,
                actionSize,
                restartExpandedWidth,
                iconOnlyPlay,
                restartPadding,
                ref _lastRestartIconOnlyState,
                ref _restartActionTransitionVersion);

            foreach (var btn in new[] { TrailerButton, DownloadButton, CopyLinkButton, WatchlistButton })
            {
                if (btn == null) continue;
                btn.Width = actionSize;
                btn.Height = actionSize;
                btn.HorizontalContentAlignment = HorizontalAlignment.Center;
                btn.VerticalContentAlignment = VerticalAlignment.Center;
                btn.CornerRadius = new CornerRadius(actionSize / 2);
            }

            if (OverviewText != null)
            {
                ApplyOverviewTextLayout(isWide, visualFactor, overviewMaxLines);
            }

            if (OverviewPanel != null)
            {
                OverviewPanel.Width = double.NaN;
            }

            if (CastSection != null)
            {
                CastSection.Width = peopleSectionWidth;
                CastSection.MaxWidth = peopleSectionWidth;
                CastSection.MinHeight = 0;
                CastSection.Height = double.NaN;
                CastSection.Visibility = (showPeopleList && (CastList?.Count > 0 || CastShimmer?.Visibility == Visibility.Visible)) ? Visibility.Visible : Visibility.Collapsed;
                CastSection.IsHitTestVisible = isWide;
            }

            if (DirectorSection != null)
            {
                DirectorSection.Width = peopleSectionWidth;
                DirectorSection.MaxWidth = peopleSectionWidth;
                DirectorSection.MinHeight = 0;
                DirectorSection.Height = double.NaN;
                bool showDirectorSkeleton = _pageLoadState != PageLoadState.Ready && DirectorShimmer?.Visibility == Visibility.Visible;
                DirectorSection.Visibility = (showPeopleList && (DirectorList?.Count > 0 || showDirectorSkeleton)) ? Visibility.Visible : Visibility.Collapsed;
                DirectorSection.IsHitTestVisible = isWide;
            }

            ApplyPeopleListState(CastListView, peopleSectionWidth, peopleHeight, showPeopleList);
            ApplyPeopleListState(DirectorListView, peopleSectionWidth, peopleHeight, showPeopleList);

            if (GenresText != null)
            {
                GenresText.TextAlignment = isWide ? TextAlignment.Left : TextAlignment.Center;
            }

            if (IdentityControl != null && IdentityControl.TitleTextBlock != null)
            {
                var titleText = IdentityControl.TitleTextBlock;
                titleText.FontSize = titleFontSize;
                titleText.LineHeight = Math.Round(titleFontSize * 1.04);
                titleText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                titleText.Margin = hasEpisodeTitleUnderLogo ? new Thickness(0, -2, 0, -6) : new Thickness(0);
                titleText.TextAlignment = isWide ? TextAlignment.Left : TextAlignment.Center;
            }

            if (MetadataRibbon != null)
            {
                double topPull = hasLogoIdentity
                    ? (hasEpisodeTitleUnderLogo ? 0 : (isWide ? -4 * visualFactor : -2))
                    : 0;
                MetadataRibbon.Margin = isWide
                    ? new Thickness(2, topPull, 0, Math.Round(8 * visualFactor))
                    : new Thickness(0, topPull, 0, 12);
            }
        }

        /// <summary>
        /// Queues layout calculation for the next rendering pass to avoid redundant thread synchronization.
        /// </summary>
        private void QueueInfoPriorityLayout(bool isWide)
        {
            if (_isResponsiveLayoutQueued)
            {
                return;
            }

            _isResponsiveLayoutQueued = true;
            CompositionTarget.Rendering += ApplyQueuedInfoPriorityLayout;
        }

        /// <summary>
        /// Triggers queued layout computations immediately on frame render ticks.
        /// </summary>
        private void ApplyQueuedInfoPriorityLayout(object sender, object e)
        {
            CompositionTarget.Rendering -= ApplyQueuedInfoPriorityLayout;
            _isResponsiveLayoutQueued = false;
            ApplyInfoPriorityLayout(ActualWidth >= LayoutAdaptiveThreshold);
        }

        /// <summary>
        /// Aligns Overview synopsis content wrapping based on wide adaptive triggers.
        /// </summary>
        private void ApplyOverviewTextLayout(bool isWide)
        {
            double infoWidth = GetInfoPanelWidth();
            double layoutWidth = isWide ? infoWidth : Math.Min(infoWidth, 430.0);
            double visualFactor = isWide ? Math.Clamp(layoutWidth / 760.0, 0.86, 1.0) : 1.0;
            double viewportHeight = GetViewportHeight();
            int overviewMaxLines = isWide ? (viewportHeight < 660 ? 5 : 7) : 0;
            ApplyOverviewTextLayout(isWide, visualFactor, overviewMaxLines);
        }

        /// <summary>
        /// Applies structural and sizing alignments on the primary overview text block.
        /// </summary>
        private void ApplyOverviewTextLayout(bool isWide, double visualFactor, int overviewMaxLines)
        {
            if (OverviewText == null) return;

            OverviewText.FontSize = isWide ? Math.Round(15 * visualFactor) : 15;
            OverviewText.LineHeight = isWide ? Math.Round(24 * visualFactor) : 24;
            OverviewText.TextAlignment = TextAlignment.Left;
            OverviewText.MaxLines = overviewMaxLines;
            OverviewText.TextWrapping = TextWrapping.Wrap;
            OverviewText.TextTrimming = isWide ? TextTrimming.CharacterEllipsis : TextTrimming.None;
            OverviewText.Width = double.NaN;
        }

        /// <summary>
        /// Slide-reveals or accordion-collapses cast/crew lists dynamically via composition visual clips.
        /// </summary>
        private void ApplyPeopleListState(ListView listView, double width, double expandedHeight, bool showList)
        {
            if (listView == null) return;
            
            double targetHeight = showList ? expandedHeight : 0;
            
            listView.Width = width;
            listView.MaxWidth = width;

            var visual = ElementCompositionPreview.GetElementVisual(listView);
            if (visual == null) return;

            var compositor = visual.Compositor;
            ElementCompositionPreview.SetIsTranslationEnabled(listView, true);

            if (showList)
            {
                listView.Height = targetHeight;
                listView.Visibility = Visibility.Visible;

                if (visual.Opacity > 0.9f) return;

                visual.Opacity = 0f;

                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(1f, 1f);
                fadeIn.Duration = TimeSpan.FromMilliseconds(450);

                var slideUp = compositor.CreateVector3KeyFrameAnimation();
                slideUp.InsertKeyFrame(1f, Vector3.Zero);
                slideUp.Duration = TimeSpan.FromMilliseconds(450);

                visual.StartAnimation("Opacity", fadeIn);
                CompositionService.StartTranslationAnimation(StickyHeader, slideUp, new Vector3(0, 20, 0));
            }
            else
            {
                if (visual.Opacity < 0.1f) 
                {
                    listView.Height = 0;
                    listView.Visibility = Visibility.Collapsed;
                    return;
                }

                var duration = TimeSpan.FromMilliseconds(350);
                var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.2f, 1f));

                var clip = compositor.CreateInsetClip();
                visual.Clip = clip;

                var wipeAnim = compositor.CreateScalarKeyFrameAnimation();
                wipeAnim.InsertKeyFrame(0f, 0f);
                wipeAnim.InsertKeyFrame(1f, (float)listView.ActualHeight, easing);
                wipeAnim.Duration = duration;

                var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(1f, 0f, easing);
                fadeOut.Duration = duration;

                var slideDown = compositor.CreateVector3KeyFrameAnimation();
                slideDown.InsertKeyFrame(1f, new Vector3(0, 15, 0), easing);
                slideDown.Duration = duration;

                var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                
                clip.StartAnimation(nameof(InsetClip.BottomInset), wipeAnim);
                visual.StartAnimation("Opacity", fadeOut);
                CompositionService.StartTranslationAnimation(StickyHeader, slideDown);
                
                batch.Completed += (s, e) => {
                    if (!showList) 
                    {
                        listView.Height = 0;
                        listView.Visibility = Visibility.Collapsed;
                        visual.Clip = null; 
                    }
                };
                batch.End();
            }
        }

        #endregion
    }
}
