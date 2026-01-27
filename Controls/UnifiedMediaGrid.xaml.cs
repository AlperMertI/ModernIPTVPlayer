using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class UnifiedMediaGrid : UserControl
    {
        // Events exposed to parent pages
        public event EventHandler<IMediaStream> ItemClicked;
        public event EventHandler<IMediaStream> PlayAction;
        public event EventHandler<MediaNavigationArgs> DetailsAction;
        public event EventHandler<IMediaStream> AddListAction;
        public event EventHandler<(Windows.UI.Color Primary, Windows.UI.Color Secondary)> ColorExtracted;

        // Current Data
        private List<IMediaStream> _items;
        public List<IMediaStream> ItemsSource
        {
            get => _items;
            set
            {
                _items = value;
                MediaGridView.ItemsSource = _items;
                IsLoading = false;
            }
        }

        public bool IsLoading
        {
            set
            {
                if (value)
                {
                    MediaGridView.Visibility = Visibility.Collapsed;
                    SkeletonGrid.Visibility = Visibility.Visible;
                    // Populate dummy skeleton items
                    var skeletons = new List<int>(new int[20]);
                    SkeletonGrid.ItemsSource = skeletons;
                }
                else
                {
                    SkeletonGrid.Visibility = Visibility.Collapsed;
                    MediaGridView.Visibility = Visibility.Visible;
                }
            }
        }

        public UnifiedMediaGrid()
        {
            this.InitializeComponent();
            
            // Auto-closing the panel when mouse leaves the entire user control area
            this.PointerExited += UnifiedMediaGrid_PointerExited;
        }

        private async void UnifiedMediaGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
             // Only close if we really left the control (and not moving into the popup canvas)
             // However, since the canvas is inside this control, strict exit checking is complex.
             // We'll trust the ExpandedCard PointerExited and simple bounds check if needed.
             // For now, let's just trigger close if visible.
             if (ActiveExpandedCard.Visibility == Visibility.Visible)
             {
                 // Simple bounds check?
                 // Or just rely on ActiveExpandedCard_PointerExited for the mouse leaving the CARD.
                 // If the mouse leaves the GRID but not the card (impossible if card is overlay?), 
                 // we usually want to close if they drift far away.
                 
                 // Let's implement the Close call explicitly just to be safe if they exit the window
                 await CloseExpandedCardAsync();
             }
        }

        // ==========================================
        // GRID INTERACTION
        // ==========================================
        private void MediaGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is IMediaStream stream)
            {
                // Find the PosterCard to prepare animation from grid
                var container = MediaGridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
                if (container != null && container.ContentTemplateRoot is PosterCard poster)
                {
                    poster.PrepareConnectedAnimation();
                }

                ItemClicked?.Invoke(this, stream);
            }
        }

        private void PosterCard_ColorsExtracted(object sender, (Windows.UI.Color Primary, Windows.UI.Color Secondary) colors)
        {
            ColorExtracted?.Invoke(this, colors);
        }

        // ==========================================
        // FLYING PANEL LOGIC (Shared)
        // ==========================================
        
        private DispatcherTimer _hoverTimer;
        private PosterCard _pendingHoverCard;
        private System.Threading.CancellationTokenSource _closeCts;
        private DispatcherTimer _flightTimer;

        private void Card_HoverStarted(object sender, EventArgs e)
        {
            if (sender is PosterCard card)
            {
                 // Cancel any pending close
                _closeCts?.Cancel();

                var visual = ElementCompositionPreview.GetElementVisual(ActiveExpandedCard);
                // Safe cancel animations
                try { visual.StopAnimation("Opacity"); } catch { }
                try { visual.StopAnimation("Scale"); } catch { }
                try { visual.StopAnimation("Translation"); } catch { }

                bool isAlreadyOpen = ActiveExpandedCard.Visibility == Visibility.Visible;

                if (isAlreadyOpen)
                {
                    // Flight Mode
                    visual.Opacity = 1f;
                    
                    if (_flightTimer == null) 
                    {
                        _flightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                        _flightTimer.Tick += FlightTimer_Tick;
                    }
                    else
                    {
                        _flightTimer.Stop();
                    }
                    
                    _pendingHoverCard = card;
                    _flightTimer.Start();
                }
                else
                {
                    // Fresh Open (Debounce)
                    _pendingHoverCard = card;
                    if (_hoverTimer == null)
                    {
                        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                        _hoverTimer.Tick += HoverTimer_Tick;
                    }
                    else
                    {
                        _hoverTimer.Stop();
                    }
                    _hoverTimer.Start();
                    ActiveExpandedCard.PrepareForTrailer();
                }
            }
        }

        private void FlightTimer_Tick(object sender, object e)
        {
            _flightTimer.Stop();
            if (_pendingHoverCard != null && _pendingHoverCard.IsHovered)
            {
                 ShowExpandedCard(_pendingHoverCard);
            }
        }

        private void HoverTimer_Tick(object sender, object e)
        {
            _hoverTimer.Stop();
            if (_pendingHoverCard != null && _pendingHoverCard.IsHovered)
            {
                ShowExpandedCard(_pendingHoverCard);
            }
        }

        private async void ShowExpandedCard(PosterCard sourceCard)
        {
            try
            {
                _closeCts?.Cancel();
                _closeCts = new System.Threading.CancellationTokenSource();

                // 1. Coordinates
                var transform = sourceCard.TransformToVisual(OverlayCanvas);
                var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                
                double widthDiff = 320 - sourceCard.ActualWidth;
                double heightDiff = 420 - sourceCard.ActualHeight;
                
                double targetX = position.X - (widthDiff / 2);
                double targetY = position.Y - (heightDiff / 2);

                // Boundaries
                if (targetX < 10) targetX = 10;
                if (targetX + 320 > OverlayCanvas.ActualWidth) targetX = OverlayCanvas.ActualWidth - 330;
                if (targetY < 10) targetY = 10;
                if (targetY + 420 > OverlayCanvas.ActualHeight) targetY = OverlayCanvas.ActualHeight - 430;

                var visual = ElementCompositionPreview.GetElementVisual(ActiveExpandedCard);
                var compositor = visual.Compositor;
                ElementCompositionPreview.SetIsTranslationEnabled(ActiveExpandedCard, true);

                // 2. Pop vs Morph
                bool isMorph = ActiveExpandedCard.Visibility == Visibility.Visible && visual.Opacity > 0.1f;

                if (isMorph)
                {
                    ActiveExpandedCard.StopTrailer();

                    double oldLeft = Canvas.GetLeft(ActiveExpandedCard);
                    double oldTop = Canvas.GetTop(ActiveExpandedCard);
                    
                    Canvas.SetLeft(ActiveExpandedCard, targetX);
                    Canvas.SetTop(ActiveExpandedCard, targetY);
                    ActiveExpandedCard.UpdateLayout(); 

                    float deltaX = (float)(oldLeft - targetX);
                    float deltaY = (float)(oldTop - targetY);
                    
                    // Translation Hack: Current Pos is (0,0) relative to new layout. 
                    // Set Translation to (deltaX, deltaY) to visually put it back at old pos.
                    visual.Properties.InsertVector3("Translation", new Vector3(deltaX, deltaY, 0));
                    
                    var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
                    offsetAnim.Target = "Translation";
                    var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1.0f));
                    offsetAnim.InsertKeyFrame(1.0f, Vector3.Zero, easing);
                    offsetAnim.Duration = TimeSpan.FromMilliseconds(400);
                    
                    visual.StartAnimation("Translation", offsetAnim);
                    visual.Opacity = 1f;
                    visual.Scale = Vector3.One;
                }
                else
                {
                    visual.StopAnimation("Translation");
                    visual.Properties.InsertVector3("Translation", Vector3.Zero);
                    visual.Scale = new Vector3(0.8f, 0.8f, 1f);
                    visual.Opacity = 0;

                    Canvas.SetLeft(ActiveExpandedCard, targetX);
                    Canvas.SetTop(ActiveExpandedCard, targetY);
                    ActiveExpandedCard.Visibility = Visibility.Visible;

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

                if (sourceCard.DataContext is LiveStream stream)
                {
                    await ActiveExpandedCard.LoadDataAsync(stream);
                }
                else if (sourceCard.DataContext is SeriesStream series)
                {
                    await ActiveExpandedCard.LoadDataAsync(series);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing card: {ex.Message}");
            }
        }

        private async void ActiveExpandedCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            await CloseExpandedCardAsync();
        }

        public async Task CloseExpandedCardAsync()
        {
             try 
             {
                 _closeCts?.Cancel();
                 _closeCts = new System.Threading.CancellationTokenSource();
                 var token = _closeCts.Token;

                 ActiveExpandedCard.StopTrailer();
                 await Task.Delay(50, token);
                 
                 if (token.IsCancellationRequested) return;

                 var visual = ElementCompositionPreview.GetElementVisual(ActiveExpandedCard);
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

                 ActiveExpandedCard.Visibility = Visibility.Collapsed;
             }
             catch (TaskCanceledException) { }
        }

        // ==========================================
        // CARD EVENTS
        // ==========================================
        private void ExpandedCard_PlayClicked(object sender, EventArgs e) => TriggerEvent(PlayAction);
        private void ExpandedCard_DetailsClicked(object sender, TmdbMovieResult tmdb) => TriggerDetailsEvent(tmdb);
        private void ExpandedCard_AddListClicked(object sender, EventArgs e) => TriggerEvent(AddListAction);

        private void TriggerDetailsEvent(TmdbMovieResult tmdb)
        {
            if (ActiveExpandedCard.Visibility == Visibility.Visible)
            {
                if (_pendingHoverCard?.DataContext is IMediaStream item)
                {
                    DetailsAction?.Invoke(this, new MediaNavigationArgs(item, tmdb));
                }
            }
        }

        private void TriggerEvent(EventHandler<IMediaStream> handler)
        {
            if (ActiveExpandedCard.Visibility == Visibility.Visible)
            {
                // We need to resolve which item is in the card.
                // Currently ExpandedCard doesn't expose the DataContext back easily as IMediaStream.
                // But we can cache it or assume it's the pending one.
                // Let's rely on _pendingHoverCard's context for now as it was the source.
                if (_pendingHoverCard?.DataContext is IMediaStream item)
                {
                    handler?.Invoke(this, item);
                }
                // Also handle the Series adapter case if needed.
                else if (_pendingHoverCard?.DataContext is SeriesStream series)
                {
                     // Convert back wrapper if needed? 
                     // Since SeriesStream implements IMediaStream now, the above check handles it!
                }
            }
        }
    }
}
