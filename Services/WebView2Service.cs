using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Service to manage shared WebView2 resources, reducing memory usage by sharing the Chromium environment.
    /// </summary>
    public static class WebView2Service
    {
        private static CoreWebView2Environment _sharedEnvironment;
        private static readonly SemaphoreSlim _envLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Gets a shared CoreWebView2Environment. 
        /// Multiple WebView2 controls sharing the same environment will share the same browser process(es),
        /// significantly reducing RAM usage.
        /// </summary>
        public static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
        {
            if (_sharedEnvironment == null)
            {
                await _envLock.WaitAsync();
                try
                {
                    if (_sharedEnvironment == null)
                    {
                        // Use a dedicated cache folder for the shared environment
                        string userDataFolder = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "WebView2_Shared_Cache");
                        _sharedEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, null);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebView2Service] Failed to create shared environment: {ex.Message}");
                    throw;
                }
                finally
                {
                    _envLock.Release();
                }
            }
            return _sharedEnvironment;
        }
    }
}
