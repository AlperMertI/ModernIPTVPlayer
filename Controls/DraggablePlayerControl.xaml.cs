using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MpvWinUI;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class DraggablePlayerControl : UserControl
    {
        public MpvPlayer Player { get; private set; }
        public string StreamUrl { get; private set; }
        
        // Events to let the parent Canvas know what to do
        public event EventHandler<Action> RequestClose;
        public event EventHandler RequestFullscreen;
        public event EventHandler<bool> RequestMute; 
        
        // Drag State
        private bool _isDragging = false;
        // private bool _isResizing = false;
        private Windows.Foundation.Point _startPoint;

        public DraggablePlayerControl()
        {
            this.InitializeComponent();
        }

        public async Task InitializeAsync(MpvPlayer existingPlayer = null, string title = "Channel", string url = "")
        {
            try
            {
                TitleText.Text = title;
                StreamUrl = url;

                if (existingPlayer != null)
                {
                    // HANDOFF MODE: Reparent existing player
                    Player = existingPlayer;
                    // Remove from old parent if needed? (Usually handled by caller to remove from old logical tree first)
                    // The caller must ensure 'existingPlayer' is removed from its old visual tree before calling this.
                    
                    PlayerHost.Children.Add(Player);
                    Player.HorizontalAlignment = HorizontalAlignment.Stretch;
                    Player.VerticalAlignment = VerticalAlignment.Stretch;
                }
                else
                {
                    // NEW INSTANCE MODE
                    Player = new MpvPlayer();
                    PlayerHost.Children.Add(Player);
                    Player.HorizontalAlignment = HorizontalAlignment.Stretch;
                    Player.VerticalAlignment = VerticalAlignment.Stretch;
                    
                    // Configure it
                    if (!string.IsNullOrEmpty(url))
                    {
                        await MpvSetupHelper.ConfigurePlayerAsync(Player, url, isSecondary: true);
                        await Player.OpenAsync(url);
                    }
                }
                
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DraggableControl] Init Error: {ex.Message}");
                TitleText.Text = "Error Loading";
            }
        }

        public async Task DisposeAsync()
        {
             // Cleanup MPV
             if (Player != null)
             {
                 try {
                     await Player.CleanupAsync();
                     PlayerHost.Children.Clear();
                 } catch { }
                 Player = null;
             }
        }

        // ==============================================================================
        // DRAG LOGIC (Manipulation)
        // ==============================================================================
        
        public event EventHandler<Windows.Foundation.Point> DragDelta;
        public event EventHandler DragCompleted;
        // public event EventHandler<Windows.Foundation.Point> ResizeDelta;
        public event EventHandler Focused;

        private void Header_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // Just focus
            Focused?.Invoke(this, EventArgs.Empty);
        }

        private void Header_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            // Scale handling?
            // Usually we just care about Translation
            DragDelta?.Invoke(this, new Windows.Foundation.Point(e.Delta.Translation.X, e.Delta.Translation.Y));
        }

        private void Header_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            DragCompleted?.Invoke(this, EventArgs.Empty);
        }



        // ==============================================================================
        // OVERLAY
        // ==============================================================================
        private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ShowOverlayAnim.Begin();
        }

        private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            HideOverlayAnim.Begin();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RequestClose?.Invoke(this, () => 
            {
                // This is a callback the parent calls when it's done removing us? 
                // Or just the event.
            });
        }

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (Player == null) return;
            
            // Get current mute state
            var isMutedStr = await Player.GetPropertyAsync("mute");
            bool isMuted = isMutedStr == "yes";
            
            // Toggle
            await Player.SetPropertyAsync("mute", isMuted ? "no" : "yes");
            
            // Update Icon
            MuteIcon.Glyph = isMuted ? "\uE767" : "\uE74F"; // 767 = Volume, 74F = Mute
        }

        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            RequestFullscreen?.Invoke(this, EventArgs.Empty);
        }
        public void SetControlsPosition(bool onLeft)
        {
            if (onLeft)
            {
                // Move Buttons to Left, Title to Right
                HeaderCol0.Width = new GridLength(1, GridUnitType.Auto);
                HeaderCol1.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(ControlButtons, 0);
                Grid.SetColumn(TitleText, 1);
                ControlButtons.Margin = new Thickness(10, 0, 0, 0);
            }
            else
            {
                // Move Buttons to Right, Title to Left (Default)
                HeaderCol0.Width = new GridLength(1, GridUnitType.Star);
                HeaderCol1.Width = new GridLength(1, GridUnitType.Auto);
                Grid.SetColumn(ControlButtons, 1);
                Grid.SetColumn(TitleText, 0);
                ControlButtons.Margin = new Thickness(0, 0, 10, 0);
            }

            // Always at top as per user's latest request
            ControlHeader.VerticalAlignment = VerticalAlignment.Top;
            HeaderGradient.StartPoint = new Windows.Foundation.Point(0, 0);
            HeaderGradient.EndPoint = new Windows.Foundation.Point(0, 1);
        }
        public void SetMuteVisual(bool isMuted)
        {
            MuteIcon.Glyph = isMuted ? "\uE74F" : "\uE767"; // 74F = Mute, 767 = Volume
        }
    }
}
