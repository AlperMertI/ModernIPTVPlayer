using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer
{
    /// <summary>
    /// Partial class managing the page loading transitions, visual shimmers, lifecycle state resets, and staggered content reveals.
    /// </summary>
    public sealed partial class MediaInfoPage : Page
    {
        #region Page Lifecycle & Resets

        /// <summary>
        /// Resets the page state to prepare the layout view for a new media item.
        /// </summary>
        internal void ResetPageStateInternal(bool resetBackground = true)
        {
            ModernIPTVPlayer.Services.AppLogger.Info("ResetPageState START");

            if (InfoContainer != null)
            {
                _visualStateController.SetState(InfoContainer, Visibility.Visible, 1.0);
                CompositionService.ResetVisual(InfoContainer);
            }

            ResetCollectionsAndBindings();
            ResetActionAndBadgeVisibility();
            ClearMetadataUI();

            if (resetBackground)
            {
                _backgroundManager?.Reset();
            }

            if (HeroShimmer != null)
            {
                HeroShimmer.Opacity = 0.15;
                HeroShimmer.Visibility = Visibility.Visible;
                
                CompositionService.Run(HeroShimmer, visual => {
                    visual.Opacity = 0.15f;
                });
            }
            ModernIPTVPlayer.Services.AppLogger.Info("ResetPageState END");
        }

        /// <summary>
        /// Clears data collections and binds from previous media instances.
        /// </summary>
        private void ResetCollectionsAndBindings()
        {
            Seasons?.Clear();
            CurrentEpisodes?.Clear();
            CastList?.Clear();
            DirectorList?.Clear();
            _addonResults?.Clear();

            _currentStremioVideoId = null;
            _unifiedMetadata = null;
            _streamUrl = null;
            _prebufferUrl = null;

            _sourcesManager?.Clear();
        }

        /// <summary>
        /// Hides all action buttons and technical quality badges, collapsing their XAML containers.
        /// </summary>
        private void ResetActionAndBadgeVisibility()
        {
            PlayButton.Visibility = Visibility.Collapsed;
            TrailerButton.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Collapsed;
            CopyLinkButton.Visibility = Visibility.Collapsed;
            WatchlistButton.Visibility = Visibility.Collapsed;

            PlayButtonSubtext.Visibility = Visibility.Collapsed;
            StickyPlayButton.Visibility = Visibility.Collapsed;
            StickyPlayButtonSubtext.Visibility = Visibility.Collapsed;
            RestartButton.Visibility = Visibility.Collapsed;

            Badge4K.Visibility = Visibility.Collapsed;
            BadgeRes.Visibility = Visibility.Collapsed;
            BadgeHDR.Visibility = Visibility.Collapsed;
            BadgeSDR.Visibility = Visibility.Collapsed;
            BadgeCodecContainer.Visibility = Visibility.Collapsed;
            _visualStateController.Collapse(TechBadgesContent, MetadataSeparator, MetadataShimmer, TechBadgesShimmer);

            if (MetadataRibbon != null) MetadataRibbon.Opacity = 1;
            if (IdentityControl != null)
            {
                if (IdentityControl.TitleShimmerElement != null) IdentityControl.TitleShimmerElement.Visibility = Visibility.Collapsed;
                if (IdentityControl.TitlePanelElement != null) _visualStateController.SetOpacity(IdentityControl.TitlePanelElement, 0.0);
            }
            if (MetadataPanel != null) _visualStateController.SetOpacity(MetadataPanel, 0.0);
            if (OverviewPanel != null) _visualStateController.SetOpacity(OverviewPanel, 0.0);
            if (ActionBarPanel != null) _visualStateController.SetOpacity(ActionBarPanel, 0.0);
            _visualStateController.Collapse(ActionBarShimmer, OverviewShimmer, CastShimmer, DirectorShimmer);
        }

        /// <summary>
        /// Resets all internal query loading flags and TCS states.
        /// </summary>
        private void ResetLoadingFlags()
        {
            _currentStremioVideoId = null;
            _addonResults?.Clear();
            _isSourcesFetchInProgress = false;
            _isEpisodesLoading = false;
            _isCurrentSourcesComplete = false;

            _isLogoReady = false;
            _isLogoPending = false;
            _isLogoFallbackActive = false;
            _logoReadyTcs = null; 
        }

        /// <summary>
        /// Resets text parameters on page metadata fields.
        /// </summary>
        private void ClearMetadataUI()
        {
            if (IdentityControl != null)
            {
                IdentityControl.SetTitle("");
                IdentityControl.SetSuperTitle("");
            }
            if (YearText != null) YearText.Text = "";
            if (OverviewText != null) OverviewText.Text = "";
            
            if (!_isResettingPageState) SyncIdentityVisibility(false); 
            
            if (GenresText != null) { GenresText.Text = ""; }
            _visualStateController.Collapse(GenresText);
            if (RuntimeText != null) RuntimeText.Text = "";
        }

        /// <summary>
        /// Displays visual shimmers safely on the UI dispatcher.
        /// </summary>
        private void ShowShimmer(FrameworkElement shimmer)
        {
            if (shimmer == null) return;
            DispatcherQueue.TryEnqueue(() => {
                CompositionService.Run(shimmer, v => v.Opacity = 1f);
                _visualStateController.SetVisibility(shimmer, Visibility.Visible);
            });
        }

        /// <summary>
        /// Prepares the UI for loading a new media item by showing skeletons and hiding content.
        /// </summary>
        private void SetLoadingState(bool isLoading, IMediaStream? item = null, bool skipSync = false)
        {
            if (isLoading)
            {
                _streamUrl = null;
                _prebufferUrl = null;
                
                if (BadgeBitrate != null) BadgeBitrate.Visibility = Visibility.Collapsed;
                if (Badge4K != null) Badge4K.Visibility = Visibility.Collapsed;
                if (BadgeRes != null) BadgeRes.Visibility = Visibility.Collapsed;
                if (BadgeHDR != null) BadgeHDR.Visibility = Visibility.Collapsed;
                if (BadgeSDR != null) BadgeSDR.Visibility = Visibility.Collapsed;
                if (BadgeCodecContainer != null) BadgeCodecContainer.Visibility = Visibility.Collapsed;
                UpdateTechnicalSectionVisibility(false);

                _currentContentStateName = "LoadingState";

                if (string.IsNullOrEmpty(_streamUrl))
                {
                    _visualStateController.Collapse(TechBadgesShimmer);
                }

                if (YearText != null && !string.IsNullOrEmpty(YearText.Text))
                {
                    _visualStateController.Collapse(MetadataShimmer);
                    _visualStateController.SetOpacity(MetadataPanel, 1.0);
                }
                
                if (!skipSync) OnViewportChanged();
            }
        }

        #endregion

        #region Content State Management & Staggered Reveal

        /// <summary>
        /// Instantly reveals the content without transitions (used for seamless re-entry).
        /// </summary>
        private void ImmediateRevealContent()
        {
            _currentContentStateName = "ReadyState";
            
            if (_pageLoadState != PageLoadState.Ready)
            {
                _pageLoadState = PageLoadState.Ready;
                OnDataCommitted();
            }
            UpdateTechnicalSectionVisibility(HasVisibleBadges());
        }

        /// <summary>
        /// Performs a smooth, staggered reveal sequence from skeletons to content.
        /// </summary>
        internal async void StaggeredRevealContent()
        {
            try
            {
                var revealSw = Stopwatch.StartNew();
                TraceMediaInfo("StaggeredRevealContent enter", new Dictionary<string, object?> { ["state"] = _pageLoadState });
                if (_pageLoadState == PageLoadState.Ready)
                {
                    return;
                }

                if (!DispatcherQueue.HasThreadAccess)
                {
                    DispatcherQueue.TryEnqueue(() => StaggeredRevealContent());
                    return;
                }

                if (_isLogoPending && _logoReadyTcs != null)
                {
                    TraceMediaInfo("StaggeredRevealContent: Logo will load asynchronously (not blocking reveal)");
                    _ = Task.Run(async () =>
                    {
                        using var cts = new CancellationTokenSource(2500);
                        try
                        {
                            await _logoReadyTcs.Task.WaitAsync(cts.Token);
                        }
                        catch { }
                    });
                }

                _pageLoadState = PageLoadState.Revealing;
                _loadPipeline?.TransitionTo(LoadPipeline.State.Revealing);

                _currentContentStateName = "ReadyState";
                
                OnDataCommitted();
                _layoutScheduler?.ForceLayout(LayoutRequestReason.DataCommitted);

                _visualStateController.SetState(InfoContainer, Visibility.Visible, 1.0);
                _visualStateController.SetState(RootScrollViewer, Visibility.Visible, 1.0);

                RevealSectionIfReady(TechBadgesContent, TechBadgesShimmer, HasVisibleBadges());
                RevealSectionIfReady(MetadataPanel, MetadataShimmer, _unifiedMetadata != null);
                RevealSectionIfReady(ActionBarPanel, ActionBarShimmer, PlayButton?.Visibility == Visibility.Visible);
                RevealSectionIfReady(OverviewPanel, OverviewShimmer, !string.IsNullOrEmpty(OverviewText?.Text));

                if (IdentityControl != null)
                {
                    if (IdentityControl.TitlePanelElement != null) _visualStateController.SetOpacity(IdentityControl.TitlePanelElement, 1.0);
                    if (IdentityControl.TitleShimmerElement != null) _visualStateController.Collapse(IdentityControl.TitleShimmerElement);
                }

                if (GenresText != null && !string.IsNullOrEmpty(GenresText.Text))
                    _visualStateController.SetVisibility(GenresText, Visibility.Visible);
                if (OverviewText != null && !string.IsNullOrEmpty(OverviewText.Text))
                    _visualStateController.SetVisibility(OverviewText, Visibility.Visible);

                _pageLoadState = PageLoadState.Ready;
                _loadPipeline?.TransitionTo(LoadPipeline.State.Ready);
                
                CollapseEmptyPeopleSkeletons();
                
                FlushDeferredPanelRequest();
                
                OnDataCommitted();
                RevealReadyPeopleSections();

                _ = Task.Run(async () =>
                {
                    await _animationCoordinator.WaitAllAsync(2000);
                });
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("StaggeredRevealContent error", ex);
                _pageLoadState = PageLoadState.Ready;
                _loadPipeline?.SetError(ex.Message);
                _currentContentStateName = "ReadyState";
                
                try
                {
                    RevealSectionIfReady(TechBadgesContent, TechBadgesShimmer, HasVisibleBadges());
                    RevealSectionIfReady(MetadataPanel, MetadataShimmer, _unifiedMetadata != null);
                    RevealSectionIfReady(ActionBarPanel, ActionBarShimmer, PlayButton?.Visibility == Visibility.Visible);
                    RevealSectionIfReady(OverviewPanel, OverviewShimmer, !string.IsNullOrEmpty(OverviewText?.Text));
                    CollapseEmptyPeopleSkeletons();
                    FlushDeferredPanelRequest();
                    OnDataCommitted();
                    RevealReadyPeopleSections();
                }
                catch (Exception fallbackEx)
                {
                    ModernIPTVPlayer.Services.AppLogger.Error("Fallback error during reveal", fallbackEx);
                }
            }
        }

        /// <summary>
        /// Reveals a section's content and collapses its shimmer if data is ready, or collapses the shimmer if no data is present.
        /// </summary>
        private void RevealSectionIfReady(FrameworkElement content, FrameworkElement shimmer, bool hasData)
        {
            if (hasData)
            {
                if (content != null) _visualStateController.SetState(content, Visibility.Visible, 1.0);
                if (shimmer != null) _visualStateController.Collapse(shimmer);
            }
            else
            {
                if (shimmer != null) _visualStateController.Collapse(shimmer);
            }
        }

        /// <summary>
        /// Collapses all loading shimmer controls on the details panel.
        /// </summary>
        private void CollapseAllShimmers()
        {
            _visualStateController.Collapse(
                TechBadgesShimmer, MetadataShimmer, ActionBarShimmer,
                OverviewShimmer, CastShimmer, DirectorShimmer, HeroShimmer);
        }

        /// <summary>
        /// Reveals all populated XAML text blocks and list containers.
        /// </summary>
        internal void RevealAllContentPanels()
        {
            _visualStateController.Reveal(
                TechBadgesContent, MetadataPanel, ActionBarPanel,
                OverviewPanel, IdentityControl);

            if (IdentityControl != null)
            {
                if (IdentityControl.TitlePanelElement != null) _visualStateController.SetOpacity(IdentityControl.TitlePanelElement, 1.0);
                if (IdentityControl.TitleShimmerElement != null) _visualStateController.Collapse(IdentityControl.TitleShimmerElement);
            }

            if (GenresText != null && !string.IsNullOrEmpty(GenresText.Text))
                _visualStateController.SetVisibility(GenresText, Visibility.Visible);
            if (OverviewText != null && !string.IsNullOrEmpty(OverviewText.Text))
                _visualStateController.SetVisibility(OverviewText, Visibility.Visible);
        }

        /// <summary>
        /// Collapses skeleton regions for Cast and Crew sections if no items were retrieved.
        /// </summary>
        private void CollapseEmptyPeopleSkeletons()
        {
            if (CastList?.Count == 0)
            {
                _visualStateController.Collapse(CastShimmer);
                AdjustCastShimmer(0);
            }

            if (DirectorList?.Count == 0)
            {
                _visualStateController.Collapse(DirectorShimmer);
                AdjustDirectorShimmer(0);
            }
        }

        /// <summary>
        /// Initiates slide-reveals for populated Cast and Crew grids.
        /// </summary>
        private void RevealReadyPeopleSections()
        {
            TraceMediaInfo("RevealReadyPeopleSections enter", new Dictionary<string, object?>
            {
                ["disabled"] = DisableReadyPeopleRevealForCrashIsolation,
                ["castCount"] = CastList?.Count ?? 0,
                ["directorCount"] = DirectorList?.Count ?? 0
            });

            if (DisableReadyPeopleRevealForCrashIsolation)
            {
                TraceMediaInfo("RevealReadyPeopleSections exit isolation");
                return;
            }

            if (CastList?.Count > 0)
            {
                RevealPeopleSectionIfReady(CastSection, CastShimmer, CastList.Count, ref _revealedCastGeneration);
            }

            if (DirectorList?.Count > 0)
            {
                RevealPeopleSectionIfReady(DirectorSection, DirectorShimmer, DirectorList.Count, ref _revealedDirectorGeneration);
            }
        }

        #endregion

        #region Composition & Text Color Interpolation Animations

        /// <summary>
        /// Starts Ken Burns zooming scale animation on the hero backdrop poster.
        /// </summary>
        private void StartKenBurnsEffect(UIElement target = null)
        {
            if (_compositor == null) return;
            var element = target ?? HeroImage;
            if (element == null) return;

            if (element == _lastKenBurnsElement)
            {
                return;
            }
            _lastKenBurnsElement = element;

            var visual = ElementCompositionPreview.GetElementVisual(element);
            
            if (element is FrameworkElement fe)
            {
                var centerExpr = _compositor.CreateExpressionAnimation("Vector3(this.Target.Size.X * 0.5f, this.Target.Size.Y * 0.5f, 0)");
                visual.StartAnimation("CenterPoint", centerExpr);
            }

            var scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(0f, new System.Numerics.Vector3(1.0f, 1.0f, 1.0f));
            scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1.08f, 1.08f, 1.0f));
            scaleAnim.Duration = TimeSpan.FromSeconds(25);
            scaleAnim.IterationBehavior = AnimationIterationBehavior.Forever;
            scaleAnim.Direction = AnimationDirection.Alternate;
            
            visual.StartAnimation("Scale", scaleAnim);
        }

        /// <summary>
        /// Stops the Ken Burns zoom animation, returning element scale to identity.
        /// </summary>
        private void StopKenBurnsEffect(UIElement target = null)
        {
            try
            {
                var element = target ?? HeroImage;
                if (element == null) return;

                var visual = ElementCompositionPreview.GetElementVisual(element);
                visual.StopAnimation("Scale");
                visual.Scale = new System.Numerics.Vector3(1.0f, 1.0f, 1.0f);
            }
            catch { }
        }

        /// <summary>
        /// Smoothly interpolates the color of an icon control.
        /// </summary>
        private void UpdateIconColor(IconElement icon, Color color, double durationSeconds = 2.0)
        {
            if (icon == null) return;
            
            var newBrush = new SolidColorBrush(color);
            icon.Foreground = newBrush;

            if (durationSeconds <= 0.01)
            {
                newBrush.Color = color;
                return;
            }

            AnimateBrushColor(newBrush, color, durationSeconds);
        }

        /// <summary>
        /// Smoothly interpolates the color of a TextBlock control.
        /// </summary>
        private void UpdateTextColor(TextBlock textBlock, Color color, double durationSeconds = 2.0)
        {
            if (textBlock == null) return;
            
            var newBrush = new SolidColorBrush(color);
            textBlock.Foreground = newBrush;

            if (durationSeconds <= 0.01)
            {
                newBrush.Color = color;
                return;
            }

            AnimateBrushColor(newBrush, color, durationSeconds);
        }

        #endregion
    }
}
