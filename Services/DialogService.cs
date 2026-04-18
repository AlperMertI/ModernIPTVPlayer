using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Centralized service to manage ContentDialogs and prevent "Only a single ContentDialog can be open at any time" crash.
    /// </summary>
    public static class DialogService
    {
        private static bool _isAnyDialogShowing = false;
        private static readonly System.Threading.Lock _lock = new();

        /// <summary>
        /// Shows a ContentDialog and ensures no other dialog is currently visible.
        /// Returns ContentDialogResult.None if another dialog is already showing.
        /// </summary>
        public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
        {
            lock (_lock)
            {
                if (_isAnyDialogShowing)
                {
                    System.Diagnostics.Debug.WriteLine("[DialogService] BLOCKED: A dialog is already showing.");
                    return ContentDialogResult.None;
                }
                _isAnyDialogShowing = true;
            }

            try
            {
                // Ensure XamlRoot is set if possible, though it's usually set by the caller
                return await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DialogService] ERROR: {ex.Message}");
                return ContentDialogResult.None;
            }
            finally
            {
                lock (_lock)
                {
                    _isAnyDialogShowing = false;
                }
            }
        }
    }
}
