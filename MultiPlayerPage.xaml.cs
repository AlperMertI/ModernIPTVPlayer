using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ModernIPTVPlayer.Controls;
using MpvWinUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ModernIPTVPlayer.Services.Streaming;

namespace ModernIPTVPlayer
{
    public sealed partial class MultiPlayerPage : Page
    {
        private List<DraggablePlayerControl> _activePlayers = new();
        private double _defaultWidth = 480;
        private double _defaultHeight = 270;
        private double _splitRatio = 0.50; // Vertical Ratio
        private double _hSplitRatio = 0.50; // Horizontal Ratio
        private UIElement _activeSplitter = null; 
        private Orientation _mainOrientation = Orientation.Vertical;

        public MultiPlayerPage()
        {
            this.InitializeComponent();
            PlayerCanvas.SizeChanged += (s, e) => ReflowLayout();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Initialize Slot Simulator with server limits
            if (App.CurrentLogin != null)
            {
                StreamSlotSimulator.Instance.Initialize(App.CurrentLogin.MaxConnections);
                Debug.WriteLine($"[MultiView] Initialized StreamSlotSimulator with {App.CurrentLogin.MaxConnections} connections.");
            }

            // HANDOFF CHECK
            if (App.HandoffPlayer != null)
            {
                Debug.WriteLine("[MultiView] Received Handoff Player!");
                
                // Retrieve Handoff Data
                var player = App.HandoffPlayer;
                string title = "Active Channel";
                string url = ""; 
                
                // Try to get existing metadata if passed? 
                // e.Parameter could be PlayerNavigationArgs or similar
                if (e.Parameter is PlayerNavigationArgs args)
                {
                    title = args.Title;
                    url = args.Url;
                }
                
                // Add as first window
                await AddPlayerToCanvas(player, title, url);
                
                // Clear global reference so we own it now
                App.HandoffPlayer = null;
            }
            else
            {
                 EmptyMessage.Visibility = Visibility.Visible;
            }
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
             base.OnNavigatedFrom(e);
             
             // CLEANUP: Dispose ALL players when leaving this page (Back to menu)
             // Unless... we want to handoff BACK? For now, destroy.
             foreach (var ctrl in _activePlayers)
             {
                 if (!string.IsNullOrEmpty(ctrl.StreamId))
                 {
                     StreamSlotSimulator.Instance.StopStream(ctrl.StreamId);
                 }
                 await ctrl.DisposeAsync();
             }
             _activePlayers.Clear();
             PlayerCanvas.Children.Clear();
        }

        private async void AddChannelBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ChannelSelectionDialog();
            dialog.XamlRoot = this.XamlRoot;
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary && dialog.SelectedStream != null)
            {
                await AddPlayerToCanvas(null, dialog.SelectedStream.Name, dialog.SelectedStream.StreamUrl);
            }
        }

        private async Task AddPlayerToCanvas(MpvPlayer existingPlayer, string title, string url)
        {
            EmptyMessage.Visibility = Visibility.Collapsed;

            var control = new DraggablePlayerControl();
            
            // Z-Index: Base level
            Canvas.SetZIndex(control, 10);

            // DRAG & SWAP LOGIC
            var transform = new Microsoft.UI.Xaml.Media.TranslateTransform();
            control.RenderTransform = transform;
            
            control.DragDelta += (s, delta) =>
            {
                transform.X += delta.X;
                transform.Y += delta.Y;
                control.Opacity = 0.8;
                Canvas.SetZIndex(control, 999);

                // PREVIEW LOGIC - Premium Bi-directional Intent
                double dropX = Canvas.GetLeft(control) + transform.X + (control.Width / 2);
                double dropY = Canvas.GetTop(control) + transform.Y + (control.Height / 2);
                double W = PlayerCanvas.ActualWidth;
                double H = PlayerCanvas.ActualHeight;
                double relX = dropX / W;
                double relY = dropY / H;

                LayoutPreview.Visibility = Visibility.Visible;
                
                // Priority 1: Orientation Switch Detection (Only for 2 players)
                if (_activePlayers.Count == 2)
                {
                    if (_mainOrientation == Orientation.Vertical && (relY < 0.15 || relY > 0.85))
                    {
                        ShowLayoutLine(0, H / 2 - 4, W, 8, Microsoft.UI.Colors.Orange);
                        return;
                    }
                    else if (_mainOrientation == Orientation.Horizontal && (relX < 0.15 || relX > 0.85))
                    {
                        ShowLayoutLine(W / 2 - 4, 0, 8, H, Microsoft.UI.Colors.Orange);
                        return;
                    }
                }

                // Priority 2: Swap Detection (Bi-directional - Works for ANY number of players)
                DraggablePlayerControl target = FindDropTarget(dropX, dropY, control);
                if (target != null)
                {
                    SetPreviewPos(Canvas.GetLeft(target), Canvas.GetTop(target), target.Width, target.Height, false);
                }
                else
                {
                    LayoutPreview.Visibility = Visibility.Collapsed;
                }
            };
            
            control.DragCompleted += (s, args) =>
            {
                LayoutPreview.Visibility = Visibility.Collapsed;
                control.Opacity = 1.0;
                Canvas.SetZIndex(control, 10); // RESET Z-INDEX: Crucial so it doesn't block LayoutPreview next time
                
                double dropX = Canvas.GetLeft(control) + transform.X + (control.Width / 2);
                double dropY = Canvas.GetTop(control) + transform.Y + (control.Height / 2);
                
                DraggablePlayerControl target = FindDropTarget(dropX, dropY, control);

                if (_activePlayers.Count == 2 && target == null)
                {
                    double relativeY = dropY / PlayerCanvas.ActualHeight;
                    double relativeX = dropX / PlayerCanvas.ActualWidth;

                    if (_mainOrientation == Orientation.Vertical && (relativeY < 0.2 || relativeY > 0.8))
                    {
                         _mainOrientation = Orientation.Horizontal;
                         _splitRatio = 0.5;
                    }
                    else if (_mainOrientation == Orientation.Horizontal && (relativeX < 0.2 || relativeX > 0.8))
                    {
                         _mainOrientation = Orientation.Vertical;
                         _splitRatio = 0.5;
                    }
                }

                if (target != null)
                {
                    int idx1 = _activePlayers.IndexOf(control);
                    int idx2 = _activePlayers.IndexOf(target);
                    if (idx1 != -1 && idx2 != -1)
                    {
                        _activePlayers[idx1] = target;
                        _activePlayers[idx2] = control;
                    }
                }
                
                transform.X = 0;
                transform.Y = 0;
                ReflowLayout();
            };

            control.RequestClose += async (s, action) => 
            {
               await control.DisposeAsync();
               PlayerCanvas.Children.Remove(control);
               _activePlayers.Remove(control);
               
               // EXPLICIT STOP: Kill the stream immediately (skip 10s idle wait)
               if (!string.IsNullOrEmpty(control.StreamId))
               {
                   StreamSlotSimulator.Instance.StopStream(control.StreamId);
               }

               if (_activePlayers.Count == 0) EmptyMessage.Visibility = Visibility.Visible;
               ReflowLayout();
            };
            
            control.Focused += (s, args) =>
            {
                int maxZ = _activePlayers.Count > 0 ? _activePlayers.Max(p => Canvas.GetZIndex(p)) : 0;
                Canvas.SetZIndex(control, maxZ + 1);
                UpdateAudioFocus(control);
            };

            control.RequestFullscreen += (s, args) =>
            {
                // UI Check: Are we already in fullscreen?
                bool isFullScreen = (control.Visibility == Visibility.Visible && control.Width == PlayerCanvas.ActualWidth && control.Height == PlayerCanvas.ActualHeight);

                if (isFullScreen)
                {
                    ReflowLayout();
                }
                else
                {
                    // Maximize specifically this control
                    foreach(var p in _activePlayers) p.Visibility = Visibility.Collapsed;
                    control.Visibility = Visibility.Visible;
                    
                    // Direct positioning to bypass Reflow logic
                    Canvas.SetLeft(control, 0);
                    Canvas.SetTop(control, 0);
                    control.Width = PlayerCanvas.ActualWidth;
                    control.Height = PlayerCanvas.ActualHeight;
                    Canvas.SetZIndex(control, 5000);
                    
                    // Clean splitters during single-player fullscreen
                    ClearSplitters();
                }
            };

            PlayerCanvas.Children.Add(control);
            _activePlayers.Add(control);

            string effectiveUrl = url;
            if (!string.IsNullOrEmpty(url))
            {
                // Register with Slot Simulator
                string streamId = Guid.NewGuid().ToString().Substring(0, 8);
                control.StreamId = streamId; // SET THIS BEFORE INIT
                StreamSlotSimulator.Instance.RegisterStream(streamId, url);
                effectiveUrl = StreamSlotSimulator.Instance.GetVirtualUrl(streamId);
                Debug.WriteLine($"[MultiView] Routing {title} via SlotSimulator Virtual URL: {effectiveUrl}");
            }

            await control.InitializeAsync(existingPlayer, title, effectiveUrl);
            UpdateAudioFocus(control);
            ReflowLayout();
        }

        private void UpdateAudioFocus(DraggablePlayerControl focusedControl)
        {
            foreach (var p in _activePlayers)
            {
                try
                {
                    if (p == focusedControl)
                    {
                        p.Player.SetPropertyAsync("mute", "no");
                        p.SetMuteVisual(false);
                        // p.Player.SetPropertyAsync("ao", "wasapi"); // Ensure audio driver is active
                        
                        // Highlight Visuals?
                        p.Opacity = 1.0;
                    }
                    else
                    {
                        // Background Player: Mute
                        p.Player.SetPropertyAsync("mute", "yes");
                        p.SetMuteVisual(true);
                    }
                }
                catch { }
            }
        }

        private void ResetLayoutBtn_Click(object sender, RoutedEventArgs e)
        {
             ReflowLayout();
        }

        private void CloseMultiViewBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }
        
        private void LayoutResetBtn_Click(object sender, RoutedEventArgs e)
        {
            _splitRatio = 0.5;
            ReflowLayout();
        }

        private void FullscreenViewBtn_Click(object sender, RoutedEventArgs e)
        {
            if (MainWindow.Current == null) return;

            var appWindow = MainWindow.Current.AppWindow;
            if (appWindow != null)
            {
                bool isFullScreen = appWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
                
                // Use the global MainWindow method to handle both AppWindow and Sidebar/TitleBar visibility
                MainWindow.Current.SetFullScreen(!isFullScreen);

                // Update icon based on NEW state
                FullscreenViewIcon.Glyph = isFullScreen ? "\uE740" : "\uE73F";
            }
        }

        private void ReflowLayout()
        {
            if (_activePlayers.Count == 0 || PlayerCanvas.ActualWidth == 0 || PlayerCanvas.ActualHeight == 0) return;
            
            // Ensure all players are visible (important after exiting single-player fullscreen)
            foreach(var p in _activePlayers) p.Visibility = Visibility.Visible;

            ClearSplitters(); // Clear once at start of reflow

            int count = _activePlayers.Count;
            double W = PlayerCanvas.ActualWidth;
            double H = PlayerCanvas.ActualHeight;

            // ZERO WASTE ALGORITHM
            if (count == 1)
            {
                var p = _activePlayers[0];
                SetPos(p, 0, 0, W, H);
            }
            else if (count == 2)
            {
                if (_mainOrientation == Orientation.Vertical)
                {
                    double splitX = W * _splitRatio;
                    SetPos(_activePlayers[0], 0, 0, splitX, H);
                    SetPos(_activePlayers[1], splitX, 0, W - splitX, H);
                    AddLayoutSplitter(splitX, 0, 10, H, Orientation.Vertical);
                }
                else
                {
                    double splitY = H * _splitRatio;
                    SetPos(_activePlayers[0], 0, 0, W, splitY);
                    SetPos(_activePlayers[1], 0, splitY, W, H - splitY);
                    AddLayoutSplitter(0, splitY, W, 10, Orientation.Horizontal);
                }
            }
            else if (count == 3)
            {
                // 1 Big Left, 2 Small Right based on Ratio
                double bigW = W * _splitRatio;
                double smallW = W - bigW;
                double splitY = H * _hSplitRatio;
                
                SetPos(_activePlayers[0], 0, 0, bigW, H);           // Left Big
                SetPos(_activePlayers[1], bigW, 0, smallW, splitY);  // Right Top
                SetPos(_activePlayers[2], bigW, splitY, smallW, H - splitY); // Right Bottom
                
                AddLayoutSplitter(bigW, 0, 10, H, Orientation.Vertical);
                AddLayoutSplitter(bigW, splitY, smallW, 10, Orientation.Horizontal);
            }
            else // 4 or more -> Grid (2x2 Fixed for now)
            {
                int cols = (int)Math.Ceiling(Math.Sqrt(count));
                int rows = (int)Math.Ceiling((double)count / cols);
                
                double cellW = W / cols;
                double cellH = H / rows;

                for (int i = 0; i < count; i++)
                {
                    int col = i % cols;
                    int row = i / cols;
                    SetPos(_activePlayers[i], col * cellW, row * cellH, cellW, cellH);
                }
            }
        }

        private void ClearSplitters()
        {
            var oldSplitters = PlayerCanvas.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>()
                .Where(r => r.Tag as string == "Splitter").ToList();
            foreach (var s in oldSplitters) PlayerCanvas.Children.Remove(s);
        }

        private void AddLayoutSplitter(double x, double y, double w, double h, Orientation orient)
        {
            // Re-use logic: Find a splitter in children or add new one?
            // Simple approach: Clear splitters first? 
            // Better: Just check if we have a "SplitterRectangle" tag.
            
            // CLEANUP OLD SPLITTERS FIRST (Naive but safe)
            // This block is removed as ClearSplitters() is called once at the start of ReflowLayout()
            // var oldSplitters = PlayerCanvas.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>().ToList();
            // foreach(var s in oldSplitters) PlayerCanvas.Children.Remove(s);

            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = w,
                Height = h,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), // Fully invisible by default
                Tag = "Splitter",
                IsHitTestVisible = true
            };
            
            rect.PointerEntered += (s,e) => {
                 // Premium look: Cyan-Blue Glow
                 rect.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(180, 0, 183, 255)); 
                 var shape = orient == Orientation.Vertical ? Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast : Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth;
                 ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(shape);
            };
            rect.PointerExited += (s,e) => {
                 if (_activeSplitter == rect) return; // Keep color while dragging
                 rect.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                 ProtectedCursor = null; 
            };

            rect.PointerPressed += (s, e) => {
                 _activeSplitter = rect;
                 rect.CapturePointer(e.Pointer);
                 rect.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DodgerBlue); // Ensure solid color during drag
            };
            rect.PointerReleased += (s, e) => {
                 _activeSplitter = null;
                 rect.ReleasePointerCapture(e.Pointer);
                 // Reset color based on current position
                 rect.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            };
            rect.PointerMoved += (s, e) => {
                if (_activeSplitter == rect)
                {
                    var pt = e.GetCurrentPoint(PlayerCanvas).Position;
                    
                    if (orient == Orientation.Vertical)
                    {
                        double newRatio = Math.Clamp(pt.X / PlayerCanvas.ActualWidth, 0.1, 0.9);
                        if (Math.Abs(newRatio - 0.5) < 0.03) newRatio = 0.5;
                        _splitRatio = newRatio;
                        Canvas.SetLeft(rect, (PlayerCanvas.ActualWidth * newRatio) - (w/2));
                    }
                    else
                    {
                        double newRatio = Math.Clamp(pt.Y / PlayerCanvas.ActualHeight, 0.1, 0.9);
                        if (Math.Abs(newRatio - 0.5) < 0.03) newRatio = 0.5;
                        _hSplitRatio = newRatio;
                        Canvas.SetTop(rect, (PlayerCanvas.ActualHeight * newRatio) - (h/2));
                    }
                    
                    UpdatePlayerPositionsOnly();
                }
            };

            if (orient == Orientation.Vertical)
                Canvas.SetLeft(rect, x - (w/2));
            else
                Canvas.SetLeft(rect, x);

            if (orient == Orientation.Horizontal)
                Canvas.SetTop(rect, y - (h/2));
            else
                Canvas.SetTop(rect, y);

            Canvas.SetZIndex(rect, 100); // Between players and floating header
            
            PlayerCanvas.Children.Add(rect);
        }

        private void UpdatePlayerPositionsOnly()
        {
             if (_activePlayers.Count < 2) return;
             
             double W = PlayerCanvas.ActualWidth;
             double H = PlayerCanvas.ActualHeight;
             
             foreach(var p in _activePlayers) p.Visibility = Visibility.Visible;

             if (_activePlayers.Count == 2)
             {
                if (_mainOrientation == Orientation.Vertical)
                {
                    double splitX = W * _splitRatio;
                    SetPos(_activePlayers[0], 0, 0, splitX, H);
                    SetPos(_activePlayers[1], splitX, 0, W - splitX, H);
                }
                else
                {
                    double splitY = H * _splitRatio;
                    SetPos(_activePlayers[0], 0, 0, W, splitY);
                    SetPos(_activePlayers[1], 0, splitY, W, H - splitY);
                }
             }
             else if (_activePlayers.Count == 3)
             {
                double bigW = W * _splitRatio;
                double smallW = W - bigW;
                double splitY = H * _hSplitRatio;
                SetPos(_activePlayers[0], 0, 0, bigW, H);
                SetPos(_activePlayers[1], bigW, 0, smallW, splitY);
                SetPos(_activePlayers[2], bigW, splitY, smallW, H - splitY);
             }
        }

        private DraggablePlayerControl FindDropTarget(double x, double y, DraggablePlayerControl source)
        {
            foreach (var p in _activePlayers)
            {
                if (p == source) continue;
                double pX = Canvas.GetLeft(p);
                double pY = Canvas.GetTop(p);
                if (x >= pX && x <= pX + p.Width && y >= pY && y <= pY + p.Height)
                    return p;
            }
            return null;
        }

        private void ShowLayoutLine(double x, double y, double w, double h, Windows.UI.Color color)
        {
            Canvas.SetLeft(LayoutPreview, x);
            Canvas.SetTop(LayoutPreview, y);
            LayoutPreview.Width = w;
            LayoutPreview.Height = h;
            LayoutPreview.Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            
            // Custom fill for orientation lines
            var orange = Windows.UI.Color.FromArgb(100, 255, 165, 0);
            LayoutPreview.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(orange);
            
            LayoutPreview.Visibility = Visibility.Visible;
        }

        private void SetPreviewPos(double x, double y, double w, double h, bool isFullLayoutChange)
        {
            Canvas.SetLeft(LayoutPreview, x);
            Canvas.SetTop(LayoutPreview, y);
            LayoutPreview.Width = w;
            LayoutPreview.Height = h;
            
            // Glassy Blue or Orange based on intent
            var blueFill = Windows.UI.Color.FromArgb(100, 0, 183, 255);
            var orangeFill = Windows.UI.Color.FromArgb(100, 255, 165, 0);
            
            LayoutPreview.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(isFullLayoutChange ? orangeFill : blueFill);
            LayoutPreview.Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(isFullLayoutChange ? Microsoft.UI.Colors.Orange : Microsoft.UI.Colors.DodgerBlue);
            
            LayoutPreview.Visibility = Visibility.Visible;
        }

        private void SetPos(FrameworkElement el, double x, double y, double w, double h)
        {
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
            el.Width = Math.Max(0, w);
            el.Height = Math.Max(0, h);

            // ADJUST OVERLAP WITH FLOATING HEADER
            if (el is DraggablePlayerControl player)
            {
                // Header is center 500px, top 60px
                double centerMin = (PlayerCanvas.ActualWidth - 500) / 2;
                double centerMax = (PlayerCanvas.ActualWidth + 500) / 2;

                bool isTopTier = y < 100; // Increased range for safety
                
                // We only care if the RIGHT side of the player (where buttons are) 
                // is currently intersecting with the floating header's horizontal space.
                double playerRight = x + w;
                bool isRightEdgeOverlapping = (playerRight > centerMin && playerRight < centerMax);

                player.SetControlsPosition(isTopTier && isRightEdgeOverlapping);
            }
        }

        private void Header_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            FloatingHeader.Opacity = 1;
            FloatingHeader.IsHitTestVisible = true;
        }

        private void Header_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Only hide if we are NOT over the header itself (simplified check)
            // ideally we check bounds, but auto-hide usually relies on timers or complex hit testing.
            // For now, let's keep it simple: Sticky header? Or separate region?
            // Let's use a timer to auto-hide if mouse leaves top area.
             FloatingHeader.Opacity = 0;
             FloatingHeader.IsHitTestVisible = false;
        }
    }
}
