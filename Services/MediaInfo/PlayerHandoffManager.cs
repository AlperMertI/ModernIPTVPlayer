using System;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using MpvWinUI;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Manages MPV player handoff lifecycle between MediaInfoPage and PlayerPage.
    /// Prevents race conditions during player transfer.
    /// 
    /// Responsibilities:
    /// - Coordinates player handoff to PlayerPage
    /// - Tracks handoff state to prevent double-disposal
    /// - Cancels handoff on navigation away
    /// 
    /// Does NOT:
    /// - Create or destroy players (that's the page's responsibility)
    /// - Configure player settings (that's MpvSetupHelper)
    /// </summary>
    internal sealed class PlayerHandoffManager : IDisposable
    {
        private bool _isHandoffInProgress;
        private bool _disposed;

        public bool IsHandoffInProgress => _isHandoffInProgress;

        public void PrepareHandoff(MpvPlayer player)
        {
            if (_disposed)
            {
                Debug.WriteLine("[HANDOFF] PrepareHandoff skipped (disposed)");
                return;
            }

            try
            {
                if (player == null)
                {
                    Debug.WriteLine("[HANDOFF] PrepareHandoff skipped (null player)");
                    return;
                }

                _isHandoffInProgress = true;
                App.HandoffPlayer = player;
                Debug.WriteLine("[HANDOFF] Player prepared for handoff");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HANDOFF] PrepareHandoff error: {ex.Message}");
                _isHandoffInProgress = false;
            }
        }

        public void CompleteHandoff()
        {
            if (_disposed) return;

            try
            {
                _isHandoffInProgress = false;
                Debug.WriteLine("[HANDOFF] Handoff completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HANDOFF] CompleteHandoff error: {ex.Message}");
            }
        }

        public void CancelHandoff()
        {
            if (_disposed) return;

            try
            {
                _isHandoffInProgress = false;
                App.HandoffPlayer = null;
                Debug.WriteLine("[HANDOFF] Handoff cancelled");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HANDOFF] CancelHandoff error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelHandoff();
            Debug.WriteLine("[HANDOFF] Disposed");
        }
    }
}
