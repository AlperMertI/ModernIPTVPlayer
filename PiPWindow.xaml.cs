using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MpvWinUI;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;
using System.Diagnostics;

namespace ModernIPTVPlayer
{
    public sealed partial class PiPWindow : Window
    {
        private AppWindow _appWindow;
        private OverlappedPresenter _presenter;
        private MpvPlayer _mpvPlayer;
        private bool _isDragging = false;
        private PointInt32 _lastMousePosition;

        public event EventHandler<MpvPlayer> ExitPiPRequested;

        public PiPWindow(MpvPlayer player)
        {
            this.InitializeComponent();
            _mpvPlayer = player;
            Debug.WriteLine("[PiP] Window Constructor");
            
            this.Closed += PiPWindow_Closed;
            PlayerContainer.SizeChanged += PlayerContainer_SizeChanged;
            
            // Move initialization to Activated to ensure Window Handle and Visual Tree are ready
            this.Activated += PiPWindow_Activated;
        }

        public void AttachPlayer()
        {
            if (!PlayerContainer.Children.Contains(_mpvPlayer))
            {
                PlayerContainer.Children.Add(_mpvPlayer);
                _mpvPlayer.Visibility = Visibility.Visible;
                _mpvPlayer.IsHitTestVisible = false;

                // Explicitly clear size to allow Stretch to work
                _mpvPlayer.Width = double.NaN;
                _mpvPlayer.Height = double.NaN;
                _mpvPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                _mpvPlayer.VerticalAlignment = VerticalAlignment.Stretch;
                
                // Restore Black Background (now that content is here)
                RootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
                
                Debug.WriteLine("[PiP] Player MANUALLY attached");
            }
        }

        private void PiPWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            this.Activated -= PiPWindow_Activated; // Run once
            Debug.WriteLine("[PiP] Window Activated");

            // 1. Wait for manual attachment
            // We do NOT attach here automatically anymore to avoid black flash.
            // PlayerPage will call AttachPlayer() when ready.
            Debug.WriteLine("[PiP] Window Activated (Waiting for AttachPlayer)");

            // 2. Configure Window (Win32 Interop)
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            _presenter = OverlappedPresenter.CreateForContextMenu();
            _presenter.IsAlwaysOnTop = true;
            _presenter.IsResizable = true;
            _presenter.SetBorderAndTitleBar(false, false);
            _appWindow.SetPresenter(_presenter);

            // [FIX] Restore ExtendsContentIntoTitleBar to handle non-client area properly
            if (_appWindow.TitleBar != null)
            {
                _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }

            // [FIX] Truly Borderless Style using Win32
            // We force WS_POPUP style and strip all standard decorations
            int style = GetWindowLong(hWnd, GWL_STYLE);
            style &= ~WS_OVERLAPPEDWINDOW;
            style |= WS_POPUP | WS_VISIBLE; 
            SetWindowLong(hWnd, GWL_STYLE, style);

            // [FIX] Strip Extended Styles (Shadows/Borders)
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            exStyle &= ~WS_EX_DLGMODALFRAME;
            exStyle &= ~WS_EX_CLIENTEDGE;
            exStyle &= ~WS_EX_STATICEDGE;
            SetWindowLong(hWnd, GWL_EXSTYLE, exStyle);

            // [FIX] Force Windows to re-evaluate the frame
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, 
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

            ExtendsContentIntoTitleBar = true; // WinUI property

            // 3. Wakeup Video
            this.DispatcherQueue.TryEnqueue(async () => {
                await System.Threading.Tasks.Task.Delay(100);
                Debug.WriteLine("[PiP] Starting WakeupVideo sequence");
                WakeupVideo();
                UpdateControlsState();
            });
        }

        private async void WakeupVideo()
        {
            if (_mpvPlayer == null) return;
            Debug.WriteLine("[PiP] WakeupVideo: Start");
            
            try
            {
                // Force a layout update
                _mpvPlayer.UpdateLayout();
                
                // Toggle visibility to force swapchain recreation/reattachment
                _mpvPlayer.Opacity = 0.99;
                await System.Threading.Tasks.Task.Delay(50);
                _mpvPlayer.Opacity = 1.0;
                Debug.WriteLine("[PiP] WakeupVideo: Opacity toggled");
                
                // Toggle pause state to force a frame render
                bool isPaused = await _mpvPlayer.GetPropertyBoolAsync("pause");
                if (!isPaused)
                {
                    await _mpvPlayer.SetPropertyAsync("pause", "yes");
                    await System.Threading.Tasks.Task.Delay(100);
                    await _mpvPlayer.SetPropertyAsync("pause", "no");
                    Debug.WriteLine("[PiP] WakeupVideo: Pause toggled");
                }
                else
                {
                     // If already paused, just unpause briefly to render a frame? No, better to seek 0 relative.
                     // Or just leave it. The opacity toggle usually does the trick.
                }
            }
            catch { }
        }

        private void PlayerContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_mpvPlayer != null)
            {
                Debug.WriteLine($"[PiP] SizeChanged: {e.NewSize.Width}x{e.NewSize.Height}");
                // Rely on HorizontalAlignment="Stretch" and VerticalAlignment="Stretch"
                // Do NOT manually set Width/Height, as it breaks the layout engine's auto-sizing
                _mpvPlayer.Width = double.NaN;
                _mpvPlayer.Height = double.NaN;
                _mpvPlayer.UpdateLayout();
            }
        }

        private async void UpdateControlsState()
        {
            if (_mpvPlayer == null) return;
            try
            {
                bool isPaused = await _mpvPlayer.GetPropertyBoolAsync("pause");
                PlayPauseIcon.Glyph = isPaused ? "\uE768" : "\uE769";

                bool isMuted = await _mpvPlayer.GetPropertyBoolAsync("mute");
                MuteIcon.Glyph = isMuted ? "\uE74F" : "\uE767";
            }
            catch { }
        }

        private void OverlayGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            OverlayBackground.Opacity = 0.6;
            ControlsPanel.Opacity = 1;
        }

        private void OverlayGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging)
            {
                OverlayBackground.Opacity = 0;
                ControlsPanel.Opacity = 0;
            }
        }

        private void OverlayGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint(OverlayGrid).Properties;
            if (properties.IsLeftButtonPressed)
            {
                _isDragging = true;
                OverlayGrid.CapturePointer(e.Pointer);
                _lastMousePosition = GetCursorPosition();
            }
        }

        private void OverlayGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging)
            {
                PointInt32 currentMousePosition = GetCursorPosition();
                int deltaX = currentMousePosition.X - _lastMousePosition.X;
                int deltaY = currentMousePosition.Y - _lastMousePosition.Y;

                PointInt32 currentWindowPosition = _appWindow.Position;
                _appWindow.Move(new PointInt32(currentWindowPosition.X + deltaX, currentWindowPosition.Y + deltaY));

                _lastMousePosition = currentMousePosition;
            }
        }

        private void OverlayGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            OverlayGrid.ReleasePointerCapture(e.Pointer);
            
            // If mouse is no longer over the grid after drag, hide controls
            // (PointerExited might not fire during drag capture)
            // But usually we want to see them if we just stopped dragging.
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mpvPlayer == null) return;
            try
            {
                bool isPaused = await _mpvPlayer.GetPropertyBoolAsync("pause");
                if (isPaused) _mpvPlayer.Play();
                else _mpvPlayer.Pause();
                UpdateControlsState();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PiP] PlayPause Error: {ex.Message}");
            }
        }

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mpvPlayer == null) return;
            try
            {
                bool isMuted = await _mpvPlayer.GetPropertyBoolAsync("mute");
                await _mpvPlayer.SetPropertyAsync("mute", !isMuted ? "yes" : "no");
                UpdateControlsState();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PiP] Mute Error: {ex.Message}");
            }
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            ExitPiP();
        }

        private void ExitPiP()
        {
            Debug.WriteLine("[PiP] ExitPiP called");
            _mpvPlayer.EnableHandoffMode(); // [FIX] Prevent D3D context destruction on close
            PlayerContainer.Children.Remove(_mpvPlayer);
            ExitPiPRequested?.Invoke(this, _mpvPlayer);
            this.Close();
        }

        private void PiPWindow_Closed(object sender, WindowEventArgs args)
        {
            // Ensure player is removed if closed via taskbar or other means
            if (PlayerContainer.Children.Contains(_mpvPlayer))
            {
                PlayerContainer.Children.Remove(_mpvPlayer);
                ExitPiPRequested?.Invoke(this, _mpvPlayer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_VISIBLE = 0x10000000;

        const int WS_EX_DLGMODALFRAME = 0x00000001;
        const int WS_EX_CLIENTEDGE = 0x00000200;
        const int WS_EX_STATICEDGE = 0x00020000;

        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_SHOWWINDOW = 0x0040;

        private PointInt32 GetCursorPosition()
        {
            GetCursorPos(out POINT p);
            return new PointInt32(p.X, p.Y);
        }
    }
}
