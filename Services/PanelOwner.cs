using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Sole owner of episode/source panel visibility, opacity, and transition state.
    /// Centralizes all panel control paths that were previously scattered across
    /// MediaInfoPage.PanelState.cs, MediaInfoPage.xaml.cs, and SectionController.cs.
    ///
    /// Responsibilities:
    /// - Owns panel mode state (_panelMode, _isSourcesPanelHidden, _contentKind)
    /// - Coordinates layout engine + layout applier + section registry
    /// - Manages opacity lifecycle to prevent stuck-at-0 bugs
    /// - Handles hide/show panel animations with versioning to prevent stale continuations
    /// - Single point of control for SourcesShowHandle visibility
    /// </summary>
    internal sealed class PanelOwner : IDisposable
    {
        #region Fields

        private readonly Compositor _compositor;
        private readonly DispatcherQueue _dispatcher;
        private readonly SectionRegistry _sectionRegistry;
        private readonly LayoutApplier _layoutApplier;
        private readonly Func<LayoutInputs> _buildLayoutInputs;
        private readonly Action _onPanelChanged;

        private readonly Grid _sourcesPanel;
        private readonly Grid _episodesPanel;
        private readonly FrameworkElement _sourcesPanelInnerContent;
        private readonly ItemsRepeater _sourcesRepeater;
        private readonly ItemsRepeater _episodesRepeater;
        private readonly FrameworkElement _sourcesShowHandle;
        private readonly Button _btnHideSources;
        private readonly Button _btnBackToEpisodes;

        private MediaDetailPanelMode _panelMode = MediaDetailPanelMode.None;
        private MediaContentKind _contentKind = MediaContentKind.Unknown;
        private bool _isSourcesPanelHidden;
        private bool _disposed;

        private int _hideAnimationVersion;

        #endregion

        #region Constructor

        public PanelOwner(
            Compositor compositor,
            DispatcherQueue dispatcher,
            SectionRegistry sectionRegistry,
            LayoutApplier layoutApplier,
            Func<LayoutInputs> buildLayoutInputs,
            Action onPanelChanged,
            Grid sourcesPanel,
            Grid episodesPanel,
            FrameworkElement sourcesPanelInnerContent,
            ItemsRepeater sourcesRepeater,
            ItemsRepeater episodesRepeater,
            FrameworkElement sourcesShowHandle,
            Button btnHideSources,
            Button btnBackToEpisodes)
        {
            _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _sectionRegistry = sectionRegistry ?? throw new ArgumentNullException(nameof(sectionRegistry));
            _layoutApplier = layoutApplier ?? throw new ArgumentNullException(nameof(layoutApplier));
            _buildLayoutInputs = buildLayoutInputs ?? throw new ArgumentNullException(nameof(buildLayoutInputs));
            _onPanelChanged = onPanelChanged ?? throw new ArgumentNullException(nameof(onPanelChanged));

            _sourcesPanel = sourcesPanel ?? throw new ArgumentNullException(nameof(sourcesPanel));
            _episodesPanel = episodesPanel ?? throw new ArgumentNullException(nameof(episodesPanel));
            _sourcesPanelInnerContent = sourcesPanelInnerContent;
            _sourcesRepeater = sourcesRepeater;
            _episodesRepeater = episodesRepeater;
            _sourcesShowHandle = sourcesShowHandle;
            _btnHideSources = btnHideSources;
            _btnBackToEpisodes = btnBackToEpisodes;
        }

        #endregion

        #region Public State

        public MediaDetailPanelMode PanelMode => _panelMode;
        public bool IsSourcesPanelHidden => _isSourcesPanelHidden;

        public void SetContentKind(MediaContentKind kind)
        {
            _contentKind = kind;
        }

        #endregion

        #region Panel Open Operations

        public void OpenSourcesPanel(PanelChangeReason reason)
        {
            if (_disposed) return;

            _isSourcesPanelHidden = false;
            SetSourcesShowHandleVisibility(false);
            SetPanelMode(MediaDetailPanelMode.Sources, reason);

            if (_btnBackToEpisodes != null)
                _btnBackToEpisodes.Visibility = (_contentKind == MediaContentKind.Series) ? Visibility.Visible : Visibility.Collapsed;

            ResetPanelVisuals(_sourcesPanel, _sourcesPanelInnerContent, _sourcesRepeater);
            _sourcesPanel.Visibility = Visibility.Visible;
            _sourcesPanel.Opacity = 1;

            ApplyLayoutAndSection("sources", "episodes");
        }

        public void OpenEpisodesPanel(PanelChangeReason reason)
        {
            if (_disposed) return;

            Debug.WriteLine($"[PANEL-OWNER] OpenEpisodesPanel entry. Reason={reason}, PanelMode={_panelMode}");
            Debug.WriteLine($"[PANEL-OWNER] EpisodesPanel before reset: Vis={_episodesPanel?.Visibility}, Opacity={_episodesPanel?.Opacity}");

            SetPanelMode(MediaDetailPanelMode.Episodes, reason);

            if (_btnBackToEpisodes != null)
                _btnBackToEpisodes.Visibility = Visibility.Collapsed;

            ResetPanelVisuals(_episodesPanel, null, _episodesRepeater);
            _episodesPanel.Visibility = Visibility.Visible;
            _episodesPanel.Opacity = 1;

            Debug.WriteLine($"[PANEL-OWNER] EpisodesPanel after reset: Vis={_episodesPanel?.Visibility}, Opacity={_episodesPanel?.Opacity}");

            ApplyLayoutAndSection("episodes", "sources");
        }

        public void CloseDetailPanel(PanelChangeReason reason)
        {
            if (_disposed) return;

            SetPanelMode(MediaDetailPanelMode.None, reason);

            if (_btnBackToEpisodes != null)
                _btnBackToEpisodes.Visibility = Visibility.Collapsed;

            HidePanelAnimated(_sourcesPanel, "sources");
            HidePanelAnimated(_episodesPanel, "episodes");

            ApplyLayoutAndNotify();
        }

        #endregion

        #region Hide/Show Sources Panel Animations

        public async Task HideSourcesPanelAnimatedAsync()
        {
            if (_disposed || _isSourcesPanelHidden || _sourcesPanel == null) return;

            int version = Interlocked.Increment(ref _hideAnimationVersion);

            _isSourcesPanelHidden = true;

            ElementCompositionPreview.SetIsTranslationEnabled(_sourcesPanel, true);
            var visual = ElementCompositionPreview.GetElementVisual(_sourcesPanel);
            var contentVisual = _sourcesPanelInnerContent != null ? ElementCompositionPreview.GetElementVisual(_sourcesPanelInnerContent) : null;
            var listVisual = _sourcesRepeater != null ? ElementCompositionPreview.GetElementVisual(_sourcesRepeater) : null;

            float width = (float)_sourcesPanel.ActualWidth;
            float height = (float)_sourcesPanel.ActualHeight;
            visual.CenterPoint = new Vector3(width / 2f, height / 2f, 0);
            if (contentVisual != null) contentVisual.CenterPoint = new Vector3(width / 2f, height / 2f, 0);

            var invScaleExpr = _compositor.CreateExpressionAnimation("Vector3(1, 1.0 / panel.Scale.Y, 1)");
            invScaleExpr.SetReferenceParameter("panel", visual);
            if (contentVisual != null) contentVisual.StartAnimation("Scale", invScaleExpr);

            float targetScale = 0.35f;
            var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

            var scaleAnim = _compositor.CreateScalarKeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1f, targetScale, easing);
            scaleAnim.Duration = TimeSpan.FromMilliseconds(550);

            var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
            fadeOut.InsertKeyFrame(1f, 0f, easing);
            fadeOut.Duration = TimeSpan.FromMilliseconds(350);

            visual.StartAnimation("Scale.Y", scaleAnim);
            if (listVisual != null) listVisual.StartAnimation("Opacity", fadeOut);

            await Task.Delay(450);
            if (version != _hideAnimationVersion) return;

            var slideOut = _compositor.CreateVector3KeyFrameAnimation();
            slideOut.InsertKeyFrame(1f, new Vector3(1000, 0, 0), easing);
            slideOut.Duration = TimeSpan.FromMilliseconds(650);
            slideOut.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;

            var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            StartTranslation(_sourcesPanel, slideOut, Vector3.Zero);
            batch.End();

            await Task.Delay(400);
            if (version != _hideAnimationVersion) return;

            SetSourcesShowHandleVisibility(true);

            if (_sourcesPanel != null) _sourcesPanel.Visibility = Visibility.Collapsed;

            SetPanelMode(MediaDetailPanelMode.None, PanelChangeReason.SourcesClosed);
            ApplyLayoutAndNotify();
        }

        public async Task ShowSourcesPanelAnimatedAsync()
        {
            if (_disposed || !_isSourcesPanelHidden || _sourcesPanel == null) return;

            int version = Interlocked.Increment(ref _hideAnimationVersion);

            _isSourcesPanelHidden = false;
            if (_sourcesPanel != null) _sourcesPanel.Visibility = Visibility.Visible;

            if (_sourcesShowHandle != null)
            {
                var handleVisual = ElementCompositionPreview.GetElementVisual(_sourcesShowHandle);
                var hFadeOut = handleVisual.Compositor.CreateScalarKeyFrameAnimation();
                hFadeOut.InsertKeyFrame(1f, 0f);
                hFadeOut.Duration = TimeSpan.FromMilliseconds(300);
                handleVisual.StartAnimation("Opacity", hFadeOut);
                await Task.Delay(250);
                _sourcesShowHandle.Visibility = Visibility.Collapsed;
            }

            var visual = ElementCompositionPreview.GetElementVisual(_sourcesPanel);
            var contentVisual = _sourcesPanelInnerContent != null ? ElementCompositionPreview.GetElementVisual(_sourcesPanelInnerContent) : null;
            var listVisual = _sourcesRepeater != null ? ElementCompositionPreview.GetElementVisual(_sourcesRepeater) : null;

            ElementCompositionPreview.SetIsTranslationEnabled(_sourcesPanel, true);

            float width = (float)_sourcesPanel.ActualWidth;
            float height = (float)_sourcesPanel.ActualHeight;
            visual.CenterPoint = new Vector3(width / 2f, height / 2f, 0);
            if (contentVisual != null) contentVisual.CenterPoint = new Vector3(width / 2f, height / 2f, 0);

            var invScaleExpr = _compositor.CreateExpressionAnimation("Vector3(1, 1.0 / panel.Scale.Y, 1)");
            invScaleExpr.SetReferenceParameter("panel", visual);
            if (contentVisual != null) contentVisual.StartAnimation("Scale", invScaleExpr);

            var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

            if (listVisual != null) listVisual.Opacity = 0f;
            visual.Scale = new Vector3(1f, 0.35f, 1f);

            var slideIn = _compositor.CreateVector3KeyFrameAnimation();
            slideIn.InsertKeyFrame(1f, Vector3.Zero, easing);
            slideIn.Duration = TimeSpan.FromMilliseconds(850);
            slideIn.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;

            StartTranslation(_sourcesPanel, slideIn, new Vector3(1000, 0, 0));

            await Task.Delay(750);
            if (version != _hideAnimationVersion) return;

            var restoreScale = _compositor.CreateScalarKeyFrameAnimation();
            restoreScale.InsertKeyFrame(1f, 1.0f, easing);
            restoreScale.Duration = TimeSpan.FromMilliseconds(550);

            var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
            fadeIn.InsertKeyFrame(1f, 1.0f, easing);
            fadeIn.Duration = TimeSpan.FromMilliseconds(450);

            visual.StartAnimation("Scale.Y", restoreScale);
            if (listVisual != null) listVisual.StartAnimation("Opacity", fadeIn);

            SetPanelMode(MediaDetailPanelMode.Sources, PanelChangeReason.SourcesRequested);
            ApplyLayoutAndNotify();
        }

        #endregion

        #region Handle Manipulation

        public void HandleSourcesShowHandlePull(double delta)
        {
            if (_disposed || _sourcesPanel == null) return;

            if (_sourcesPanel.Visibility != Visibility.Visible)
            {
                _sourcesPanel.Visibility = Visibility.Visible;
            }

            var visual = ElementCompositionPreview.GetElementVisual(_sourcesPanel);
            var contentVisual = _sourcesPanelInnerContent != null ? ElementCompositionPreview.GetElementVisual(_sourcesPanelInnerContent) : null;
            var listVisual = _sourcesRepeater != null ? ElementCompositionPreview.GetElementVisual(_sourcesRepeater) : null;

            visual.Properties.TryGetVector3("Translation", out var currentTrans);
            float newTranslationX = Math.Max(0, currentTrans.X - (float)delta);
            visual.Properties.InsertVector3("Translation", new Vector3(newTranslationX, 0, 0));

            float progress = 1f - (newTranslationX / 1000f);
            float newScaleY = 0.35f + (0.65f * progress);
            visual.Scale = new Vector3(1f, newScaleY, 1f);

            if (listVisual != null) listVisual.Opacity = progress;

            if (contentVisual != null)
            {
                var invScaleExpr = _compositor.CreateExpressionAnimation("Vector3(1, 1.0 / panel.Scale.Y, 1)");
                invScaleExpr.SetReferenceParameter("panel", visual);
                contentVisual.StartAnimation("Scale", invScaleExpr);
            }
        }

        public void ReassertUnsquashExpression()
        {
            if (_disposed || _sourcesPanel == null) return;

            var visual = ElementCompositionPreview.GetElementVisual(_sourcesPanel);
            var contentVisual = _sourcesPanelInnerContent != null ? ElementCompositionPreview.GetElementVisual(_sourcesPanelInnerContent) : null;

            if (contentVisual != null)
            {
                var invScaleExpr = _compositor.CreateExpressionAnimation("Vector3(1, 1.0 / panel.Scale.Y, 1)");
                invScaleExpr.SetReferenceParameter("panel", visual);
                contentVisual.StartAnimation("Scale", invScaleExpr);
            }
        }

        #endregion

        #region Internal State Management

        private void SetPanelMode(MediaDetailPanelMode mode, PanelChangeReason reason)
        {
            var normalizedMode = NormalizePanelMode(mode, _contentKind);
            if (_panelMode == normalizedMode) return;

            _panelMode = normalizedMode;
            if (normalizedMode != MediaDetailPanelMode.Sources)
            {
                _isSourcesPanelHidden = false;
                SetSourcesShowHandleVisibility(false);
            }
        }

        private void ApplyLayoutAndNotify()
        {
            var inputs = _buildLayoutInputs();
            var decision = LayoutEngine.Compute(inputs);
            _layoutApplier.Apply(decision);
            _onPanelChanged();
        }

        private static MediaDetailPanelMode NormalizePanelMode(MediaDetailPanelMode requestedMode, MediaContentKind contentKind)
        {
            if (contentKind == MediaContentKind.Live) return MediaDetailPanelMode.None;
            if (requestedMode == MediaDetailPanelMode.Episodes && contentKind != MediaContentKind.Series)
                return MediaDetailPanelMode.None;
            if (requestedMode == MediaDetailPanelMode.None && contentKind == MediaContentKind.Series)
                return MediaDetailPanelMode.Episodes;
            return requestedMode;
        }

        private void ApplyLayoutAndSection(string showSectionName, string hideSectionName)
        {
            Debug.WriteLine($"[PANEL-OWNER] ApplyLayoutAndSection: show={showSectionName}, hide={hideSectionName}");
            var inputs = _buildLayoutInputs();
            var decision = LayoutEngine.Compute(inputs);
            _layoutApplier.Apply(decision);
            Debug.WriteLine($"[PANEL-OWNER] Layout applied.");

            var showSection = _sectionRegistry.Get(showSectionName);
            if (showSection != null)
            {
                Debug.WriteLine($"[PANEL-OWNER] Show section '{showSectionName}' state={showSection.State}");
                if (showSection.State == SectionState.ContentVisible)
                {
                    showSection.ShowContentImmediate();
                }
                else
                {
                    showSection.ShowSkeleton();
                }
            }

            var hideSection = _sectionRegistry.Get(hideSectionName);
            if (hideSection != null)
            {
                Debug.WriteLine($"[PANEL-OWNER] Hide section '{hideSectionName}' state={hideSection.State}");
                hideSection.Hide();
            }
        }

        private static void ResetPanelVisuals(Grid panel, FrameworkElement innerContent, ItemsRepeater repeater)
        {
            if (panel == null) return;
            Debug.WriteLine($"[PANEL-OWNER] ResetPanelVisuals on {panel.Name}. Before: Vis={panel.Visibility}, Opacity={panel.Opacity}");
            
            CompositionService.StopAllAnimationsImmediately(panel);
            CompositionService.ResetVisual(panel);
            panel.Opacity = 1;
            panel.Visibility = Visibility.Visible;
            Debug.WriteLine($"[PANEL-OWNER] ResetPanelVisuals on {panel.Name}. After: Vis={panel.Visibility}, Opacity={panel.Opacity}");

            if (innerContent != null)
            {
                CompositionService.StopAllAnimationsImmediately(innerContent);
                CompositionService.ResetVisual(innerContent);
            }
            if (repeater != null)
            {
                CompositionService.StopAllAnimationsImmediately(repeater);
                CompositionService.ResetVisual(repeater);
            }
        }

        private void HidePanelAnimated(Grid panel, string sectionName)
        {
            var section = _sectionRegistry.Get(sectionName);
            if (section != null)
            {
                section.Hide();
            }
            else if (panel != null)
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Opacity = 0;
            }
        }

        private void SetSourcesShowHandleVisibility(bool visible)
        {
            if (_sourcesShowHandle != null)
                _sourcesShowHandle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void StartTranslation(FrameworkElement element, Vector3KeyFrameAnimation animation, Vector3 fromValue)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                visual.Properties.InsertVector3("Translation", fromValue);
                visual.StartAnimation("Translation", animation);
            }
            catch
            {
                try
                {
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    visual.Offset = fromValue;
                    visual.StartAnimation(CompositionService.OffsetProperty, animation);
                }
                catch { }
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _disposed = true;
        }

        #endregion
    }
}
