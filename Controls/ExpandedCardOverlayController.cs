using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using ModernIPTVPlayer.Models;
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace ModernIPTVPlayer.Controls
{
    internal sealed class ExpandedCardOverlayController
    {
        private const double CardWidth = 320;
        private const double CardHeight = 420;
        private const double CinemaCoverRatio = 0.70;
        private const double CinemaAspectRatio = 16.0 / 9.0;

        private readonly FrameworkElement _hostElement;
        private readonly Canvas _overlayCanvas;
        private readonly ExpandedCard _expandedCard;
        private readonly Rectangle? _cinemaScrim;
        private readonly ScrollViewer? _scrollLockTarget;

        private DispatcherTimer? _hoverTimer;
        private DispatcherTimer? _flightTimer;
        private CancellationTokenSource? _closeCts;
        private FrameworkElement? _pendingHoverCard;
        private FrameworkElement? _activeSourceCard;
        private Rect _preCinemaBounds;
        private bool _isInCinemaMode;
        private bool _suppressCinemaAnimations;
        private Storyboard? _cardBoundsStoryboard;
        private bool _pointerExitNeedsRearm;
        private DateTimeOffset _suppressPointerExitUntil;
        private bool _isPointerOverCard;
        private bool _isClosing;

        public event EventHandler<IMediaStream>? PlayRequested;
        public event EventHandler<(IMediaStream Stream, TmdbMovieResult Tmdb)>? DetailsRequested;
        public event EventHandler<IMediaStream>? AddListRequested;
        public event EventHandler<bool>? CinemaModeChanged;

        public bool IsInCinemaMode => _isInCinemaMode;
        public bool IsCardVisible => _expandedCard.Visibility == Visibility.Visible;
        public bool IsManipulationInProgress { get; set; }
        public ExpandedCard ActiveExpandedCard => _expandedCard;

        public ExpandedCardOverlayController(
            FrameworkElement hostElement,
            Canvas overlayCanvas,
            ExpandedCard expandedCard,
            Rectangle? cinemaScrim = null,
            ScrollViewer? scrollLockTarget = null)
        {
            _hostElement = hostElement;
            _overlayCanvas = overlayCanvas;
            _overlayCanvas.Visibility = Visibility.Collapsed;
            _overlayCanvas.IsHitTestVisible = false;
            _expandedCard = expandedCard;
            _cinemaScrim = cinemaScrim;
            _scrollLockTarget = scrollLockTarget;

            // [FIX] Enable Translation property early to avoid "Property not found" errors during StopAnimation or early access
            ElementCompositionPreview.SetIsTranslationEnabled(_expandedCard, true);

            _expandedCard.PlayClicked += ExpandedCard_PlayClicked;
            _expandedCard.DetailsClicked += ExpandedCard_DetailsClicked;
            _expandedCard.AddListClicked += ExpandedCard_AddListClicked;
            _expandedCard.PointerEntered += ExpandedCard_PointerEntered;
            _expandedCard.PointerExited += ExpandedCard_PointerExited;
            _expandedCard.CinemaModeToggled += ExpandedCard_CinemaModeToggled;

            _hostElement.PointerExited += (s, e) => _ = CloseExpandedCardAsync();
            _overlayCanvas.Visibility = Visibility.Collapsed;
            
            if (_cinemaScrim != null)
            {
                _cinemaScrim.Opacity = 0;
                _cinemaScrim.Visibility = Visibility.Collapsed;
                _cinemaScrim.IsHitTestVisible = false;
            }

            _expandedCard.Width = CardWidth;
            _expandedCard.Height = CardHeight;
            _hostElement.SizeChanged += HostElement_SizeChanged;

            // CENTRALIZED SCROLL MANIPULATION HANDLING
            if (_scrollLockTarget != null)
            {
                _scrollLockTarget.DirectManipulationStarted += (s, args) => 
                {
                    IsManipulationInProgress = true;
                    CancelPendingShow();
                };
                _scrollLockTarget.DirectManipulationCompleted += (s, args) => 
                {
                    IsManipulationInProgress = false;
                };
            }

            // CENTRALIZED LIFECYCLE MANAGEMENT
            // Auto-clean on unload to prevent card persistence or audio leaks.
            // In WinUI Page, Unloaded fires when navigating away even if cached.
            _hostElement.Unloaded += (s, e) => ForceClose();
        }

        public void PrepareForTrailer() => _expandedCard.PrepareForTrailer();

        public void CancelPendingShow()
        {
            _hoverTimer?.Stop();
            _flightTimer?.Stop();
            _pendingHoverCard = null;
        }

        public void OnHoverStarted(FrameworkElement card)
        {
            if (_isInCinemaMode || IsCardVisible || IsManipulationInProgress) return;

            try 
            {
                _closeCts?.Cancel();

                // [FIX] Robust Hover Management: Reset previous cards immediately when moving to a new one.
                if (_activeSourceCard != null && _activeSourceCard != card)
                {
                    if (_activeSourceCard is PosterCard p) p.ResetHoverState();
                    else if (_activeSourceCard is LandscapeCard l) l.ResetHoverState();
                }
                if (_pendingHoverCard != null && _pendingHoverCard != card)
                {
                    if (_pendingHoverCard is PosterCard p) p.ResetHoverState();
                    else if (_pendingHoverCard is LandscapeCard l) l.ResetHoverState();
                }

                if (_expandedCard.XamlRoot == null) return;
                var visual = ElementCompositionPreview.GetElementVisual(_expandedCard);
                if (visual == null) return;

                TryStopAnimation(visual, "Opacity");
                TryStopAnimation(visual, "Scale");
                TryStopAnimation(visual, "Translation");

                bool isAlreadyOpen = _expandedCard.Visibility == Visibility.Visible;

                if (isAlreadyOpen)
                {
                    visual.Opacity = 1f;
                    _flightTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                    _flightTimer.Tick -= FlightTimer_Tick;
                    _flightTimer.Tick += FlightTimer_Tick;
                    _flightTimer.Stop();
                    _pendingHoverCard = card;
                    _flightTimer.Start();
                }
                else
                {
                    _hoverTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                    _hoverTimer.Tick -= HoverTimer_Tick;
                    _hoverTimer.Tick += HoverTimer_Tick;
                    _hoverTimer.Stop();
                    _pendingHoverCard = card;
                    _hoverTimer.Start();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCardOverlayController] OnHoverStarted error: {ex.Message}");
            }
        }

        public async Task CloseExpandedCardAsync(FrameworkElement? sourceCard = null, bool force = false)
        {
            if (sourceCard != null && _activeSourceCard != sourceCard && !force) return;
            if (_isInCinemaMode && !force) return;
            if (_isPointerOverCard && !force) return;
            if (_isClosing && !force) return;

            if (force)
            {
                ForceClose();
                return;
            }

            _isClosing = true;
            try
            {
                _closeCts?.Cancel();
                _closeCts = new CancellationTokenSource();
                var token = _closeCts.Token;

                if (_expandedCard.Visibility == Visibility.Visible)
                {
                    _expandedCard.StopTrailer(); 
                } 

                await Task.Delay(50, token);
                if (token.IsCancellationRequested) return;

                if (_expandedCard.Visibility != Visibility.Visible || _expandedCard.XamlRoot == null)
                {
                    FinalizeCloseVisualState();
                    return;
                }

                var visual = ElementCompositionPreview.GetElementVisual(_expandedCard);
                if (visual == null)
                {
                    FinalizeCloseVisualState();
                    return;
                }

                var compositor = visual.Compositor;

                var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                fadeOut.Target = "Opacity";
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromMilliseconds(200);

                var scaleDown = compositor.CreateVector3KeyFrameAnimation();
                scaleDown.Target = "Scale";
                scaleDown.InsertKeyFrame(1f, new Vector3(0.95f, 0.95f, 1f));
                scaleDown.Duration = TimeSpan.FromMilliseconds(200);

                visual.StartAnimation("Opacity", fadeOut);
                visual.StartAnimation("Scale", scaleDown);

                await Task.Delay(200, token);
                if (token.IsCancellationRequested) return;

                FinalizeCloseVisualState();
            }
            catch (TaskCanceledException) { }
            finally
            {
                _isClosing = false;
            }
        }

        /// <summary>
        /// Synchronously and immediately clears the expanded card state.
        /// Ideal for navigation and page switches.
        /// </summary>
        public void ForceClose()
        {
            try
            {
                _closeCts?.Cancel();
                _closeCts = null;
                _hoverTimer?.Stop();
                _flightTimer?.Stop();
                
                if (_expandedCard.Visibility == Visibility.Visible)
                {
                    System.Diagnostics.Debug.WriteLine("[ExpandedCardOverlayController] ForceClose: Stopping trailer.");
                    _expandedCard.StopTrailer();
                }
                FinalizeCloseVisualState();
                
                // System.Diagnostics.Debug.WriteLine("[ExpandedCardOverlayController] ForceClose executed.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCardOverlayController] ForceClose Error: {ex.Message}");
            }
        }

        private void FlightTimer_Tick(object sender, object e)
        {
            _flightTimer?.Stop();
            if (_pendingHoverCard != null && IsCardHovered(_pendingHoverCard))
            {
                _expandedCard.PrepareForTrailer();
                ShowExpandedCard(_pendingHoverCard);
            }
        }

        private void HoverTimer_Tick(object sender, object e)
        {
            _hoverTimer?.Stop();
            if (_pendingHoverCard != null && IsCardHovered(_pendingHoverCard))
            {
                _expandedCard.PrepareForTrailer();
                ShowExpandedCard(_pendingHoverCard);
            }
        }

        private bool IsCardHovered(FrameworkElement card)
        {
            if (card is PosterCard p) return p.IsHovered;
            if (card is LandscapeCard l) return l.IsHovered;
            return false;
        }

        private async void ShowExpandedCard(FrameworkElement sourceCard)
        {
            try
            {
                if (_expandedCard.XamlRoot == null || sourceCard.XamlRoot == null) return;

                _closeCts?.Cancel();
                _closeCts = new CancellationTokenSource();
                _activeSourceCard = sourceCard;
                _pointerExitNeedsRearm = false;
                ResetCardFrame();
                _expandedCard.Focus(FocusState.Programmatic);

                // First-hover transform can be wrong while canvas is collapsed.
                _overlayCanvas.Visibility = Visibility.Visible;
                _overlayCanvas.IsHitTestVisible = true;
                _overlayCanvas.UpdateLayout();

                var transform = sourceCard.TransformToVisual(_overlayCanvas);
                var position = transform.TransformPoint(new Point(0, 0));

                double widthDiff = CardWidth - sourceCard.ActualWidth;
                double heightDiff = CardHeight - sourceCard.ActualHeight;

                double targetX = position.X - (widthDiff / 2);
                double targetY = position.Y - (heightDiff / 2);

                if (targetX < 10) targetX = 10;
                if (targetY < 10) targetY = 10;
                if (targetX + CardWidth > _overlayCanvas.ActualWidth) targetX = _overlayCanvas.ActualWidth - CardWidth - 10;
                if (targetY + CardHeight > _overlayCanvas.ActualHeight) targetY = _overlayCanvas.ActualHeight - CardHeight - 10;

                if (targetX < 10) targetX = 10;
                if (targetY < 10) targetY = 10;

                var visual = ElementCompositionPreview.GetElementVisual(_expandedCard);
                if (visual == null) return;

                var compositor = visual.Compositor;
                // [FIX] Already enabled in constructor

                bool isMorph = _expandedCard.Visibility == Visibility.Visible && visual.Opacity > 0.1f && !_isInCinemaMode;

                if (isMorph)
                {
                    _expandedCard.StopTrailer();

                    double oldLeft = Canvas.GetLeft(_expandedCard);
                    double oldTop = Canvas.GetTop(_expandedCard);

                    Canvas.SetLeft(_expandedCard, targetX);
                    Canvas.SetTop(_expandedCard, targetY);
                    _expandedCard.UpdateLayout();

                    float deltaX = (float)(oldLeft - targetX);
                    float deltaY = (float)(oldTop - targetY);

                    try { visual.Properties.InsertVector3("Translation", new Vector3(deltaX, deltaY, 0)); } catch { }

                    var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
                    offsetAnim.Target = "Translation";
                    var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1.0f));
                    offsetAnim.InsertKeyFrame(1.0f, Vector3.Zero, easing);
                    offsetAnim.Duration = TimeSpan.FromMilliseconds(400);

                    // [FIX] Try-Catch to prevent crash if visuals don't support Translation
                    try { visual.StartAnimation("Translation", offsetAnim); } catch { }
                    visual.Opacity = 1f;
                    visual.Scale = Vector3.One;
                }
                else
                {
                    TryStopAnimation(visual, "Translation");
                    try { visual.Properties.InsertVector3("Translation", Vector3.Zero); } catch { }
                    visual.Scale = new Vector3(0.8f, 0.8f, 1f);
                    visual.Opacity = 0;

                    Canvas.SetLeft(_expandedCard, targetX);
                    Canvas.SetTop(_expandedCard, targetY);
                    _expandedCard.Visibility = Visibility.Visible;

                    var springAnim = compositor.CreateSpringVector3Animation();
                    springAnim.Target = "Scale";
                    springAnim.FinalValue = Vector3.One;
                    springAnim.DampingRatio = 0.7f;
                    springAnim.Period = TimeSpan.FromMilliseconds(50);

                    var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
                    fadeAnim.Target = "Opacity";
                    fadeAnim.InsertKeyFrame(1f, 1f);
                    fadeAnim.Duration = TimeSpan.FromMilliseconds(200);

                    visual.StartAnimation("Scale", springAnim);
                    visual.StartAnimation("Opacity", fadeAnim);
                }

                if (sourceCard.DataContext is IMediaStream stream)
                {
                    await _expandedCard.LoadDataAsync(stream, isMorphing: isMorph);
                }
            }
            catch (OperationCanceledException) { /* Expected on hover changes */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCardOverlayController] Show error: {ex.Message}");
            }
        }

        public void ExpandToCinema(IMediaStream item, UIElement sourceElement)
        {
            if (sourceElement is not FrameworkElement fe) return;
            
            _closeCts?.Cancel();
            _closeCts = new CancellationTokenSource();
            
            // Set state to prevent closing
            _activeSourceCard = null; 
            _isInCinemaMode = true;
            
            _overlayCanvas.Visibility = Visibility.Visible;
            if (_cinemaScrim != null)
            {
                _cinemaScrim.Visibility = Visibility.Visible;
                _cinemaScrim.IsHitTestVisible = true;
                _cinemaScrim.Opacity = 0.8f;
            }

            // 1. Initial Position (at source)
            var transform = fe.TransformToVisual(_overlayCanvas);
            var sourcePos = transform.TransformPoint(new Point(0, 0));
            
            _expandedCard.Visibility = Visibility.Visible;
            _expandedCard.Opacity = 1;
            Canvas.SetLeft(_expandedCard, sourcePos.X);
            Canvas.SetTop(_expandedCard, sourcePos.Y);
            _expandedCard.Width = fe.ActualWidth;
            _expandedCard.Height = fe.ActualHeight;
            _expandedCard.UpdateLayout();

            // Store current bounds as "pre-cinema"
            _preCinemaBounds = new Rect(sourcePos.X, sourcePos.Y, fe.ActualWidth, fe.ActualHeight);

            // 2. Load Data & Cinema Mode
            _expandedCard.LoadDataAsync(item);
            _expandedCard.ToggleCinemaMode(true);

            // 3. Animate to Center
            var target = CalculateCinemaTarget();

            if (_cardBoundsStoryboard != null)
            {
                _cardBoundsStoryboard.Stop();
            }

            _cardBoundsStoryboard = new Storyboard();
            var easing = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut };
            
            var animX = new DoubleAnimation { To = target.X, Duration = TimeSpan.FromMilliseconds(800), EasingFunction = easing, EnableDependentAnimation = true };
            var animY = new DoubleAnimation { To = target.Y, Duration = TimeSpan.FromMilliseconds(800), EasingFunction = easing, EnableDependentAnimation = true };
            var animW = new DoubleAnimation { To = target.Width, Duration = TimeSpan.FromMilliseconds(800), EasingFunction = easing, EnableDependentAnimation = true };
            var animH = new DoubleAnimation { To = target.Height, Duration = TimeSpan.FromMilliseconds(800), EasingFunction = easing, EnableDependentAnimation = true };

            Storyboard.SetTarget(animX, _expandedCard);
            Storyboard.SetTargetProperty(animX, "(Canvas.Left)");
            Storyboard.SetTarget(animY, _expandedCard);
            Storyboard.SetTargetProperty(animY, "(Canvas.Top)");
            Storyboard.SetTarget(animW, _expandedCard);
            Storyboard.SetTargetProperty(animW, "Width");
            Storyboard.SetTarget(animH, _expandedCard);
            Storyboard.SetTargetProperty(animH, "Height");

            _cardBoundsStoryboard.Children.Add(animX);
            _cardBoundsStoryboard.Children.Add(animY);
            _cardBoundsStoryboard.Children.Add(animW);
            _cardBoundsStoryboard.Children.Add(animH);
            
            CinemaModeChanged?.Invoke(this, true);
            _cardBoundsStoryboard.Begin();
        }

        private void ExpandedCard_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPointerOverCard = true;
            _pointerExitNeedsRearm = false;
        }

        private async void ExpandedCard_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPointerOverCard = false;
            if (_pointerExitNeedsRearm) return;
            if (DateTimeOffset.UtcNow < _suppressPointerExitUntil) return;
            await CloseExpandedCardAsync();
        }

        private void ExpandedCard_CinemaModeToggled(object sender, bool isCinema)
        {
            _isInCinemaMode = isCinema;
            CinemaModeChanged?.Invoke(this, isCinema);

            if (_suppressCinemaAnimations)
            {
                if (!isCinema)
                {
                    HideScrimImmediately();
                    ResetCardFrame();
                }
                return;
            }

            if (isCinema)
            {
                EnterCinemaMode();
            }
            else
            {
                ExitCinemaMode();
                _pointerExitNeedsRearm = true;
                _suppressPointerExitUntil = DateTimeOffset.UtcNow.AddMilliseconds(900);
            }
        }

        private void EnterCinemaMode()
        {
            CancelPendingShow();

            double currentLeft = Canvas.GetLeft(_expandedCard);
            double currentTop = Canvas.GetTop(_expandedCard);
            if (double.IsNaN(currentLeft)) currentLeft = 0;
            if (double.IsNaN(currentTop)) currentTop = 0;
            _preCinemaBounds = new Rect(currentLeft, currentTop, _expandedCard.Width, _expandedCard.Height);

            var target = CalculateCinemaTarget();
            AnimateCardTo(target.Left, target.Top, target.Width, target.Height);

            ShowScrimAnimated();

            if (_scrollLockTarget != null)
            {
                _scrollLockTarget.VerticalScrollMode = ScrollMode.Disabled;
            }

            if (_cinemaScrim != null)
            {
                Canvas.SetZIndex(_cinemaScrim, 100);
            }
            Canvas.SetZIndex(_expandedCard, 101);
        }

        private void ExitCinemaMode()
        {
            AnimateCardTo(_preCinemaBounds.Left, _preCinemaBounds.Top, _preCinemaBounds.Width, _preCinemaBounds.Height);
            HideScrimAnimated();

            if (_scrollLockTarget != null)
            {
                _scrollLockTarget.VerticalScrollMode = ScrollMode.Enabled;
            }

            if (_cinemaScrim != null)
            {
                Canvas.SetZIndex(_cinemaScrim, 0);
            }
            Canvas.SetZIndex(_expandedCard, 1);
        }

        private Rect CalculateCinemaTarget()
        {
            var hostWidth = Math.Max(_hostElement.ActualWidth, _overlayCanvas.ActualWidth);
            var hostHeight = Math.Max(_hostElement.ActualHeight, _overlayCanvas.ActualHeight);

            var maxWidth = hostWidth * CinemaCoverRatio;
            var maxHeight = hostHeight * CinemaCoverRatio;

            var targetWidth = maxWidth;
            var targetHeight = targetWidth / CinemaAspectRatio;

            if (targetHeight > maxHeight)
            {
                targetHeight = maxHeight;
                targetWidth = targetHeight * CinemaAspectRatio;
            }

            var targetLeft = (hostWidth - targetWidth) / 2;
            var targetTop = (hostHeight - targetHeight) / 2;
            return new Rect(targetLeft, targetTop, targetWidth, targetHeight);
        }

        private void AnimateCardTo(double left, double top, double width, double height)
        {
            _cardBoundsStoryboard?.Stop();
            var storyboard = new Storyboard();
            var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

            var animWidth = new DoubleAnimation
            {
                To = width,
                Duration = TimeSpan.FromMilliseconds(420),
                EnableDependentAnimation = true,
                EasingFunction = easing
            };
            Storyboard.SetTarget(animWidth, _expandedCard);
            Storyboard.SetTargetProperty(animWidth, "Width");

            var animHeight = new DoubleAnimation
            {
                To = height,
                Duration = TimeSpan.FromMilliseconds(420),
                EnableDependentAnimation = true,
                EasingFunction = easing
            };
            Storyboard.SetTarget(animHeight, _expandedCard);
            Storyboard.SetTargetProperty(animHeight, "Height");

            var animL = new DoubleAnimation
            {
                To = left,
                Duration = TimeSpan.FromMilliseconds(420),
                EnableDependentAnimation = true,
                EasingFunction = easing
            };
            Storyboard.SetTarget(animL, _expandedCard);
            Storyboard.SetTargetProperty(animL, "(Canvas.Left)");

            var animT = new DoubleAnimation
            {
                To = top,
                Duration = TimeSpan.FromMilliseconds(420),
                EnableDependentAnimation = true,
                EasingFunction = easing
            };
            Storyboard.SetTarget(animT, _expandedCard);
            Storyboard.SetTargetProperty(animT, "(Canvas.Top)");

            storyboard.Children.Add(animWidth);
            storyboard.Children.Add(animHeight);
            storyboard.Children.Add(animL);
            storyboard.Children.Add(animT);
            _cardBoundsStoryboard = storyboard;
            storyboard.Begin();
        }

        private void ShowScrimAnimated()
        {
            if (_cinemaScrim == null) return;

            SyncScrimSize();
            _cinemaScrim.Visibility = Visibility.Visible;
            _cinemaScrim.IsHitTestVisible = true;
            AnimateScrimOpacity(toOpacity: 0.72, hideAfterAnimation: false);
        }

        private void HideScrimAnimated()
        {
            if (_cinemaScrim == null) return;
            _cinemaScrim.IsHitTestVisible = false;
            AnimateScrimOpacity(toOpacity: 0, hideAfterAnimation: true);
        }

        private void HideScrimImmediately()
        {
            if (_cinemaScrim == null) return;
            _cinemaScrim.Opacity = 0;
            _cinemaScrim.IsHitTestVisible = false;
            _cinemaScrim.Visibility = Visibility.Collapsed;
        }

        private void AnimateScrimOpacity(double toOpacity, bool hideAfterAnimation)
        {
            if (_cinemaScrim == null) return;

            var storyboard = new Storyboard();
            var opacityAnim = new DoubleAnimation
            {
                To = toOpacity,
                Duration = TimeSpan.FromMilliseconds(340),
                EasingFunction = new QuadraticEase { EasingMode = toOpacity > _cinemaScrim.Opacity ? EasingMode.EaseOut : EasingMode.EaseIn }
            };
            Storyboard.SetTarget(opacityAnim, _cinemaScrim);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");

            if (hideAfterAnimation)
            {
                storyboard.Completed += (_, __) =>
                {
                    if (!_isInCinemaMode)
                    {
                        _cinemaScrim.Visibility = Visibility.Collapsed;
                    }
                };
            }

            storyboard.Children.Add(opacityAnim);
            storyboard.Begin();
        }

        private void SyncScrimSize()
        {
            if (_cinemaScrim == null) return;
            var width = Math.Max(_hostElement.ActualWidth, _overlayCanvas.ActualWidth);
            var height = Math.Max(_hostElement.ActualHeight, _overlayCanvas.ActualHeight);
            _cinemaScrim.Width = width;
            _cinemaScrim.Height = height;
        }

        private void HostElement_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isInCinemaMode)
            {
                SyncScrimSize();
                var target = CalculateCinemaTarget();
                Canvas.SetLeft(_expandedCard, target.Left);
                Canvas.SetTop(_expandedCard, target.Top);
                _expandedCard.Width = target.Width;
                _expandedCard.Height = target.Height;
            }
        }

        private void ExpandedCard_PlayClicked(object? sender, EventArgs e)
        {
            var stream = ResolveCurrentStream();
            if (stream != null)
            {
                PlayRequested?.Invoke(this, stream);
            }
        }

        private void ExpandedCard_DetailsClicked(object? sender, TmdbMovieResult tmdb)
        {
            var stream = ResolveCurrentStream();
            if (stream != null)
            {
                DetailsRequested?.Invoke(this, (stream, tmdb));
            }
        }

        private void ExpandedCard_AddListClicked(object? sender, EventArgs e)
        {
            var stream = ResolveCurrentStream();
            if (stream != null)
            {
                AddListRequested?.Invoke(this, stream);
            }
        }

        private IMediaStream? ResolveCurrentStream()
        {
            if (_activeSourceCard?.DataContext is IMediaStream activeStream)
            {
                return activeStream;
            }

            if (_pendingHoverCard?.DataContext is IMediaStream pendingStream)
            {
                return pendingStream;
            }

            return null;
        }

        private void FinalizeCloseVisualState()
        {
            if (_activeSourceCard is PosterCard p) p.ResetHoverState();
            else if (_activeSourceCard is LandscapeCard l) l.ResetHoverState();
            
            _expandedCard.Visibility = Visibility.Collapsed;
            _overlayCanvas.Visibility = Visibility.Collapsed;
            _overlayCanvas.IsHitTestVisible = false;
            _activeSourceCard = null;
            _pendingHoverCard = null;
            _isPointerOverCard = false;

            try 
            {
                if (_expandedCard.XamlRoot != null)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(_expandedCard);
                    if (visual != null)
                    {
                        visual.Opacity = 1f;
                        visual.Scale = Vector3.One;
                        try { visual.Properties.InsertVector3("Translation", Vector3.Zero); } catch { }
                    }
                }
            } catch { }

            HideScrimImmediately();

            if (_scrollLockTarget != null)
            {
                _scrollLockTarget.VerticalScrollMode = ScrollMode.Enabled;
            }

            if (_cinemaScrim != null)
            {
                Canvas.SetZIndex(_cinemaScrim, 0);
            }
            Canvas.SetZIndex(_expandedCard, 1);
            _isInCinemaMode = false;
            _pointerExitNeedsRearm = false;
            ResetCardFrame();
        }

        private void ResetCardFrame()
        {
            _cardBoundsStoryboard?.Stop();
            _expandedCard.Width = CardWidth;
            _expandedCard.Height = CardHeight;
            _expandedCard.RenderTransform = null;
            _expandedCard.RenderTransformOrigin = new Point(0, 0);
        }

        private static void TryStopAnimation(Microsoft.UI.Composition.Visual visual, string propertyName)
        {
            try
            {
                // Property check before stopping to avoid ArgumentException if not enabled yet
                if (propertyName == "Translation")
                {
                    // Even if enabled, sometimes the property isn't "initialized" in the property bag yet
                    visual.StopAnimation(propertyName);
                }
                else
                {
                    visual.StopAnimation(propertyName);
                }
            }
            catch { }
        }
    }
}
