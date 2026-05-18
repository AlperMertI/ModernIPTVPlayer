using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ModernIPTVPlayer.Helpers;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage : Page
    {
        #region Page Control & Resizing Animations

        private void SetupProfessionalAnimations()
        {
            // 1. Back Button Vortex + Morph
            SetupVortexEffect(BackButton, BackIconVisual);

            // 2. Play Button Anticipation
            SetupAnticipationPulse(PlayButton, PlayButtonIcon);
            SetupAnticipationPulse(StickyPlayButton, StickyPlayButtonIcon);
            
            // 3. Action Bar Buttons
            var actionButtons = new Button[] { DownloadButton, TrailerButton, CopyLinkButton, RestartButton, WatchlistButton };
            foreach (var btn in actionButtons)
            {
                if (btn != null) SetupAnticipationPulse(btn, (FrameworkElement)btn.Content);
            }

            // 4. Alive System: Organic Breathing
            ApplyOrganicBreathing(PlayButtonIcon);

            // 5. Modern Layout: Implicit Animations (Force smooth resizing)
            SetupImplicitAnimations();

            // 6. Action Bar Implicit Animations
            SetupActionBarImplicitAnimations();
        }

        private void SetupImplicitAnimations()
        {
            try
            {
                if (_compositor == null) _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

                // 1. Offset/Translation Animation
                var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
                offsetAnimation.Target = "Offset";
                offsetAnimation.Duration = TimeSpan.FromMilliseconds(400); 
                var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));
                offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);

                // 2. Scale Animation
                var scaleAnimation = _compositor.CreateVector3KeyFrameAnimation();
                scaleAnimation.Target = "Scale";
                scaleAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
                scaleAnimation.Duration = TimeSpan.FromMilliseconds(350);

                // 3. Opacity Animation
                var opacityAnimation = _compositor.CreateScalarKeyFrameAnimation();
                opacityAnimation.Target = "Opacity";
                opacityAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
                opacityAnimation.Duration = TimeSpan.FromMilliseconds(400);

                var implicitAnimationCollection = _compositor.CreateImplicitAnimationCollection();
                implicitAnimationCollection["Offset"] = offsetAnimation;
                implicitAnimationCollection["Scale"] = scaleAnimation;

                var scaleCollection = _compositor.CreateImplicitAnimationCollection();
                scaleCollection["Scale"] = scaleAnimation;

                // Elements that should "glide" and "morph" during resize/layout changes
                UIElement[] glideElements = { 
                    InfoContainer, InfoContainerInner, AdaptiveInfoHost, InfoColumn, MetadataRibbon, ActionBarPanel, 
                    CastPanel, DirectorPanel, 
                    IdentityControl?.TitlePanelElement, IdentityControl?.LogoHost, IdentityControl?.TitleTextBlock,
                    IdentityControl?.IdentityPanel, OverviewPanel, GenresText, MetadataPanel,
                    IdentityControl?.TitleShimmerElement, MetadataShimmer, ActionBarShimmer, OverviewShimmer,
                    EpisodesPanel, SourcesPanel 
                };
                
                // Define which elements get the full Offset animation (gliding/morphing)
                HashSet<UIElement> offsetGlideElements = new() { 
                    EpisodesPanel, SourcesPanel, 
                    InfoContainer, InfoContainerInner, AdaptiveInfoHost, InfoColumn,
                    ActionBarPanel, MetadataPanel, OverviewPanel, GenresText, 
                    IdentityControl?.IdentityPanel, IdentityControl?.TitlePanelElement,
                    MetadataRibbon
                };

                foreach (var element in glideElements)
                {
                    if (element == null) continue;
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    
                    if (offsetGlideElements.Contains(element))
                    {
                        visual.ImplicitAnimations = implicitAnimationCollection;
                    }
                    else
                    {
                        visual.ImplicitAnimations = scaleCollection;
                    }

                    // Enable translation facade
                    ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LayoutDebug] SetupImplicitAnimations Error: {ex.Message}");
            }
        }

        private void SetupAnticipationPulse(Button btn, FrameworkElement content)
        {
            if (btn == null || content == null) return;
            var compositor = _compositor;
            if (compositor == null) return;
            
            // 1. Content Visual (Scale Pulse)
            var contentVisual = ElementCompositionPreview.GetElementVisual(content);
            
            // 2. Button Visual (Magnetic Positional Tracking)
            var btnVisual = ElementCompositionPreview.GetElementVisual(btn);
            try
            {
                ElementCompositionPreview.SetIsTranslationEnabled(btn, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AnticipationPulse] Translation disabled for {btn.Name ?? "<button>"}: {ex.Message}");
                return;
            }

            void UpdateCenter()
            {
                contentVisual.CenterPoint = new Vector3((float)content.ActualWidth / 2f, (float)content.ActualHeight / 2f, 0);
            }

            content.SizeChanged += (s, e) => UpdateCenter();
            if (content.ActualWidth > 0) UpdateCenter();

            btn.PointerMoved += (s, e) => 
            {
                try
                {
                    // Calculate Magnetic Offset
                    var ptr = e.GetCurrentPoint(btn);
                    var center = new Windows.Foundation.Point(btn.ActualWidth / 2, btn.ActualHeight / 2);
                    var deltaX = (float)(ptr.Position.X - center.X);
                    var deltaY = (float)(ptr.Position.Y - center.Y);
                    
                    // Limit movement (Magnetic strength)
                    float limit = 12f;
                    float moveX = Math.Clamp(deltaX * 0.35f, -limit, limit);
                    float moveY = Math.Clamp(deltaY * 0.35f, -limit, limit);

                    // Stop any reset animation and apply direct offset from Pointer
                    CompositionService.StopTranslationAnimation(btn);
                    CompositionService.SetTranslation(btn, new Vector3(moveX, moveY, 0));
                }
                catch {}
            };

            btn.PointerEntered += (s, e) => {
                try {
                    // Pulse Scale on Content
                    contentVisual.StopAnimation("Scale");
                    var pulse = compositor.CreateVector3KeyFrameAnimation();
                    pulse.InsertKeyFrame(0.2f, new Vector3(0.85f, 0.85f, 1f));
                    pulse.InsertKeyFrame(0.6f, new Vector3(1.25f, 1.25f, 1f));
                    pulse.InsertKeyFrame(1f, new Vector3(1.15f, 1.15f, 1f));
                    pulse.Duration = TimeSpan.FromMilliseconds(500);
                    contentVisual.StartAnimation("Scale", pulse);
                } catch {}
            };

            btn.PointerExited += (s, e) => {
                try {
                    // Reset Scale
                    contentVisual.StopAnimation("Scale");
                    var resetScale = compositor.CreateVector3KeyFrameAnimation();
                    resetScale.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
                    resetScale.Duration = TimeSpan.FromMilliseconds(300);
                    contentVisual.StartAnimation("Scale", resetScale);
                    
                    // Reset Position (Spring back)
                    btnVisual.StopAnimation("Translation");
                    var resetPos = compositor.CreateVector3KeyFrameAnimation();
                    resetPos.InsertKeyFrame(1f, Vector3.Zero);
                    resetPos.Duration = TimeSpan.FromMilliseconds(400);
                    resetPos.InsertKeyFrame(0.5f, Vector3.Zero, compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0f, 1f))); 
                    CompositionService.StartTranslationAnimation(btn, resetPos);
                } catch {}
            };
        }

        private void ApplyOrganicBreathing(FrameworkElement element)
        {
            if (element == null) return;
            var visual = ElementCompositionPreview.GetElementVisual(element);
            
            element.SizeChanged += (s, e) => {
                visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
            };

            var breath = _compositor.CreateVector3KeyFrameAnimation();
            breath.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
            breath.InsertKeyFrame(0.5f, new Vector3(1.04f, 1.04f, 1f));
            breath.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
            breath.Duration = TimeSpan.FromSeconds(4);
            breath.IterationBehavior = AnimationIterationBehavior.Forever;
            
            visual.StartAnimation("Scale", breath);
        }

        private void SetupActionBarImplicitAnimations()
        {
            try
            {
                if (_compositor == null) _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

                // Create shared implicit animation collection
                var implicitAnimations = _compositor.CreateImplicitAnimationCollection();

                var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
                offsetAnimation.Target = "Offset";
                var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
                offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
                offsetAnimation.Duration = TimeSpan.FromMilliseconds(260);

                implicitAnimations["Offset"] = offsetAnimation;

                var buttonAnimations = _compositor.CreateImplicitAnimationCollection();
                buttonAnimations["Offset"] = offsetAnimation;

                var sizeAnimation = _compositor.CreateVector2KeyFrameAnimation();
                sizeAnimation.Target = "Size";
                sizeAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
                sizeAnimation.Duration = TimeSpan.FromMilliseconds(220);
                buttonAnimations["Size"] = sizeAnimation;

                if (ActionBarPanel != null)
                {
                    var panelVisual = ElementCompositionPreview.GetElementVisual(ActionBarPanel);
                    panelVisual.ImplicitAnimations = null;
                    panelVisual.Offset = Vector3.Zero;
                }

                var actionButtons = new Button[] { PlayButton, RestartButton, TrailerButton, DownloadButton, CopyLinkButton, WatchlistButton };
                var centerPointExpression = _compositor.CreateExpressionAnimation("Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0)");

                foreach (var btn in actionButtons)
                {
                    if (btn == null) continue;
                    var visual = ElementCompositionPreview.GetElementVisual(btn);
                    visual.StartAnimation("CenterPoint", centerPointExpression);
                    visual.ImplicitAnimations = buttonAnimations;
                }
            }
            catch { }
        }

        private void SetupButtonInteractions(params Button[] buttons)
        {
            foreach (var btn in buttons)
            {
                if (btn == null) continue;
                if (!_initializedButtonInteractions.Add(btn)) continue;
                
                var visual = ElementCompositionPreview.GetElementVisual(btn);
                btn.SizeChanged += (s, e) => 
                {
                    visual.CenterPoint = new Vector3((float)btn.ActualWidth / 2f, (float)btn.ActualHeight / 2f, 0);
                };

                btn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
                {
                    var scale = _compositor.CreateVector3KeyFrameAnimation();
                    scale.InsertKeyFrame(1f, new Vector3(0.92f, 0.92f, 1f));
                    scale.Duration = TimeSpan.FromMilliseconds(100);
                    visual.StartAnimation("Scale", scale);
                }), true);

                btn.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, e) =>
                {
                    var spring = _compositor.CreateSpringVector3Animation();
                    spring.FinalValue = new Vector3(1f, 1f, 1f);
                    spring.DampingRatio = 0.5f;
                    spring.Period = TimeSpan.FromMilliseconds(40);
                    visual.StartAnimation("Scale", spring);
                }), true);
                
                btn.PointerExited += (s, e) =>
                {
                    var spring = _compositor.CreateSpringVector3Animation();
                    spring.FinalValue = new Vector3(1f, 1f, 1f);
                    spring.DampingRatio = 0.7f;
                    visual.StartAnimation("Scale", spring);
                };
            }
        }

        private void SetupMagneticEffect(Button btn, float intensity)
        {
            if (btn == null) return;
            if (!_initializedMagneticButtons.Add(btn)) return;
            var visual = ElementCompositionPreview.GetElementVisual(btn);
            try
            {
                ElementCompositionPreview.SetIsTranslationEnabled(btn, true);
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error($"MagneticEffect Translation disabled for {btn.Name ?? "<button>"}", ex);
                return;
            }

            var props = visual.Properties;
            props.InsertVector2("TouchPoint", new Vector2(0, 0));

            var leanExpr = _compositor.CreateExpressionAnimation("Vector3(props.TouchPoint.X * intensity, props.TouchPoint.Y * intensity, 0)");
            leanExpr.SetReferenceParameter("props", props);
            leanExpr.SetScalarParameter("intensity", intensity);
            try
            {
                CompositionService.StartTranslationAnimation(HeroContainer, leanExpr);
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error($"MagneticEffect StartAnimation failed for {btn.Name ?? "<button>"}", ex);
                return;
            }

            btn.PointerMoved += (s, e) =>
            {
                var ptr = e.GetCurrentPoint(btn).Position;
                var cx = btn.ActualWidth / 2;
                var cy = btn.ActualHeight / 2;
                props.InsertVector2("TouchPoint", new Vector2((float)(ptr.X - cx), (float)(ptr.Y - cy)));
            };

            btn.PointerExited += (s, e) =>
            {
                try
                {
                    var reset = _compositor.CreateVector3KeyFrameAnimation();
                    reset.InsertKeyFrame(1f, new System.Numerics.Vector3(0, 0, 0));
                    reset.Duration = TimeSpan.FromMilliseconds(400);
                    CompositionService.StartTranslationAnimation(HeroContainer, reset);
                }
                catch { }
            };
        }

        private void SetupVortexEffect(Button btn, FrameworkElement target)
        {
            if (btn == null || target == null) return;
            var visual = ElementCompositionPreview.GetElementVisual(target);
            
            target.SizeChanged += (s, e) => {
                visual.CenterPoint = new Vector3((float)target.ActualWidth / 2f, (float)target.ActualHeight / 2f, 0);
            };

            btn.PointerEntered += (s, e) =>
            {
                // 1. Vortex Rotation with Overshoot
                var spin = _compositor.CreateScalarKeyFrameAnimation();
                spin.InsertKeyFrame(0.7f, 380f, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0f, 1f)));
                spin.InsertKeyFrame(1f, 360f);
                spin.Duration = TimeSpan.FromMilliseconds(700);
                visual.StartAnimation("RotationAngleInDegrees", spin);

                // 2. Anticipation Scale Pulse
                var pulse = _compositor.CreateVector3KeyFrameAnimation();
                pulse.InsertKeyFrame(0.3f, new Vector3(0.85f, 0.85f, 1f));
                pulse.InsertKeyFrame(1f, new Vector3(1.1f, 1.1f, 1f));
                pulse.Duration = TimeSpan.FromMilliseconds(300);
                visual.StartAnimation("Scale", pulse);

                // 3. AnimatedIcon State
                AnimatedIcon.SetState(BackIconVisual, "PointerOver");
            };

            btn.PointerExited += (s, e) =>
            {
                var reset = _compositor.CreateScalarKeyFrameAnimation();
                reset.InsertKeyFrame(1f, 0f);
                reset.Duration = TimeSpan.FromMilliseconds(500);
                visual.StartAnimation("RotationAngleInDegrees", reset);

                var scaleReset = _compositor.CreateSpringVector3Animation();
                scaleReset.FinalValue = new Vector3(1f, 1f, 1f);
                scaleReset.DampingRatio = 0.6f;
                visual.StartAnimation("Scale", scaleReset);

                AnimatedIcon.SetState(BackIconVisual, "Normal");
            };

            btn.PointerPressed += (s, e) =>
            {
                AnimatedIcon.SetState(BackIconVisual, "Pressed");
            };
            btn.PointerReleased += (s, e) =>
            {
                AnimatedIcon.SetState(BackIconVisual, "PointerOver");
            };
        }

        #endregion

        #region Composition & Storyboard Transition Helper Animators

        private void AnimateMainContentRecede(bool recede)
        {
            var visual = ElementCompositionPreview.GetElementVisual(MainContentWrapper);
            var compositor = visual.Compositor;

            // 1. Scale Animation (0.98 for recede)
            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1.0f, recede ? new Vector3(0.98f, 0.98f, 1f) : Vector3.One);
            scaleAnim.Duration = TimeSpan.FromMilliseconds(500);
            visual.StartAnimation("Scale", scaleAnim);

            // 2. Blur / Dim Overlay
            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(1.0f, recede ? 0.6f : 1.0f);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(500);
            visual.StartAnimation("Opacity", opacityAnim);
        }

        private void AnimateBrushColor(FrameworkElement target, Color targetColor, double durationSeconds = 2.0, string propertyPath = "Background.Color")
        {
            if (target == null) return;
            
            try
            {
                Color fromColor = Colors.Transparent;
                SolidColorBrush activeBrush = null;

                if (target is Control animControl)
                {
                    if (animControl.Background is SolidColorBrush controlBrush)
                    {
                        activeBrush = controlBrush;
                        fromColor = controlBrush.Color;
                    }
                    else
                    {
                        activeBrush = new SolidColorBrush(Colors.Transparent);
                        animControl.Background = activeBrush;
                    }
                }
                else if (target is Panel animPanel)
                {
                    if (animPanel.Background is SolidColorBrush panelBrush)
                    {
                        activeBrush = panelBrush;
                        fromColor = panelBrush.Color;
                    }
                    else
                    {
                        activeBrush = new SolidColorBrush(Colors.Transparent);
                        animPanel.Background = activeBrush;
                    }
                }
                else if (target is Border animBorder)
                {
                    if (animBorder.Background is SolidColorBrush borderBrush)
                    {
                        activeBrush = borderBrush;
                        fromColor = borderBrush.Color;
                    }
                    else
                    {
                        activeBrush = new SolidColorBrush(Colors.Transparent);
                        animBorder.Background = activeBrush;
                    }
                }
                
                if (fromColor == targetColor) return;

                if (activeBrush != null && propertyPath == "Background.Color")
                {
                    AnimateBrushColor(activeBrush, targetColor, durationSeconds);
                    return;
                }

                var animation = new ColorAnimation
                {
                    From = fromColor,
                    To = targetColor,
                    Duration = TimeSpan.FromSeconds(durationSeconds),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                Storyboard.SetTarget(animation, target);
                Storyboard.SetTargetProperty(animation, propertyPath);

                var sb = new Storyboard();
                sb.Children.Add(animation);
                sb.Begin();
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("AnimateBrushColor failed", ex);
                if (target is Control c) c.Background = new SolidColorBrush(targetColor);
                else if (target is Panel p) p.Background = new SolidColorBrush(targetColor);
                else if (target is Border b) b.Background = new SolidColorBrush(targetColor);
            }
        }

        private void AnimateBrushColor(SolidColorBrush brush, Color targetColor, double durationSeconds = 2.0)
        {
            if (brush == null || brush.Color == targetColor) return;

            if (durationSeconds <= 0.01)
            {
                brush.Color = targetColor;
                return;
            }
            
            try
            {
                var animation = new ColorAnimation
                {
                    From = brush.Color,
                    To = targetColor,
                    Duration = TimeSpan.FromSeconds(durationSeconds),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                Storyboard.SetTarget(animation, brush);
                Storyboard.SetTargetProperty(animation, "Color");

                var sb = new Storyboard();
                sb.Children.Add(animation);
                sb.Begin();
            }
            catch { brush.Color = targetColor; }
        }

        private void AnimateOpacity(UIElement element, double toOpacity, TimeSpan duration)
        {
            if (element == null) return;
            var storyboard = new Storyboard();
            var anim = new DoubleAnimation
            {
                To = toOpacity,
                Duration = duration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(anim, element);
            Storyboard.SetTargetProperty(anim, "Opacity");
            storyboard.Children.Add(anim);
            storyboard.Begin();
        }

        private void AnimateOpacity(UIElement element, float targetOpacity, TimeSpan duration)
        {
            if (element == null) return;

            if (targetOpacity <= 0.01f)
            {
                element.Opacity = 0;
                var elementVisual = ElementCompositionPreview.GetElementVisual(element);
                elementVisual?.StopAnimation("Opacity");
                if (elementVisual != null) elementVisual.Opacity = 0f;
                return;
            }
            
            var visual = ElementCompositionPreview.GetElementVisual(element);
            
            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1f, targetOpacity);
            animation.Duration = duration;
            
            visual.StartAnimation("Opacity", animation);
            element.Opacity = 1; // Ensure XAML visibility but use Composition for the actual fade
        }

        private void SetupStickyScroller()
        {
            RootScrollViewer.ViewChanged += (s, e) =>
            {
                var offset = RootScrollViewer.VerticalOffset;
                if (offset > 150 && _isWideModeIndex != 1) // Only show sticky header in Narrow Mode
                {
                    double progress = Math.Clamp((offset - 150) / 100.0, 0, 1);
                    StickyHeader.Opacity = progress;
                    // Slide down from -80 to 0
                    StickyHeaderTranslate.Y = -80 * (1.0 - progress);
                    StickyHeader.IsHitTestVisible = progress > 0.5;
                    
                    StickyPlayButtonText.Text = PlayButtonText.Text;
                }
                else
                {
                    StickyHeader.Opacity = 0;
                    StickyHeaderTranslate.Y = -80;
                    StickyHeader.IsHitTestVisible = false;
                }
            };
        }

        private void AnimateButtonWidth(Button? button, FrameworkElement? textHost, double targetWidth, double durationMs = 250)
        {
            if (button == null) return;

            // Use a smooth DoubleAnimation for the Width property
            var animation = new DoubleAnimation
            {
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop // Crucial: Don't lock the property
            };

            var storyboard = new Storyboard();
            Storyboard.SetTarget(animation, button);
            Storyboard.SetTargetProperty(animation, "Width");
            
            // Set the final value explicitly when the animation ends to ensure stability
            storyboard.Completed += (s, e) => 
            { 
                button.Width = targetWidth; 
                button.InvalidateMeasure();
                textHost?.InvalidateMeasure();
                button.UpdateLayout();

                if (ReferenceEquals(button, PlayButton)) _isPlayActionAnimating = false;
                else _isRestartActionAnimating = false;
            };
            storyboard.Begin();
        }

        #endregion
    }
}
