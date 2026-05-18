using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Services;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage : Page
    {
        #region Prebuffering & Native MPV Player Control

        internal void StartPrebuffering(string url, double startTime = 0)
        {
            StartPrebufferingV2(url, startTime);
        }

        private async void StartPrebufferingV2(string url, double startTime = 0)
        {
            if (string.IsNullOrEmpty(url)) return;
            
            // [OPTIMIZATION] Skip starting MPV pre-buffer player if player engine is set to Native (Media Foundation).
            if (AppSettings.PlayerSettings.Engine == Models.PlayerEngine.Native)
            {
                return;
            }

            if (!AppSettings.IsPrebufferEnabled) return;
            if (_prebufferUrl == url && MediaInfoPlayer != null) return; // Already prebuffering this url

            // Cancel any previous prebuffering safely
            try { _prebufferCts?.Cancel(); _prebufferCts?.Dispose(); } catch {}
            _prebufferCts = new CancellationTokenSource();
            var ct = _prebufferCts.Token;
            _prebufferUrl = url;

            var swTotal = Stopwatch.StartNew();
            Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "START Prebuffering", $"{url} | Resume: {startTime}s");

            // 1. Ensure Player Instance Exists & is Attached
            bool isNew = false;
            if (MediaInfoPlayer == null)
            {
                try { 
                    ModernIPTVPlayer.Services.AppLogger.Info($"CREATING NEW MpvPlayer in StartPrebuffering (IsNavigatingAway: {_isNavigatingAway})");
                    MediaInfoPlayer = new MpvWinUI.MpvPlayer(); 
                }
                catch (Exception ex) { ModernIPTVPlayer.Services.AppLogger.Error("Mpv Fatal creation error", ex); }
                isNew = true;
            }

            try 
            {
               var pSettings = AppSettings.PlayerSettings;
               MediaInfoPlayer.RenderApi = pSettings.VideoOutput == ModernIPTVPlayer.Models.VideoOutput.GpuNext ? "gpu-next" : "dxgi";
               
               // Phase 1: Essential configuration
               await MpvSetupHelper.ApplyEssentialSettingsAsync(MediaInfoPlayer, url, isSecondary: true);
            }
            catch (Exception ex)
            {
                ModernIPTVPlayer.Services.AppLogger.Error("Failed to init pre-buffer player", ex);
            }

            if (isNew)
            {
                MediaInfoPlayer.Width = 100;
                MediaInfoPlayer.Height = 100;
                if (PlayerHost != null) {
                    ModernIPTVPlayer.Services.AppLogger.Info($"ATTACHING Player to Host in StartPrebuffering (IsNavigatingAway: {_isNavigatingAway})");
                    PlayerHost.Content = MediaInfoPlayer;
                }
            }

            // 2. Wait for RenderControl
            if (isNew)
            {
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Waiting for Player.Loaded event...");
                var tcs = new TaskCompletionSource<bool>();
                RoutedEventHandler handler = null;
                handler = (s, e) =>
                {
                    MediaInfoPlayer.Loaded -= handler;
                    tcs.TrySetResult(true);
                };
                MediaInfoPlayer.Loaded += handler;

                var timeoutTask = Task.Delay(2000);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Player.Loaded event {(completed == timeoutTask ? "TIMED OUT" : "RECEIVED")}.");
            }

            try
            {
                ct.ThrowIfCancellationRequested();

                // 3. Configure Player settings
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Starting Phase 2 (Configuration)...");
                await MpvSetupHelper.ConfigurePlayerAsync(MediaInfoPlayer, url, isSecondary: true);
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Phase 2 Complete.");

                // 4. Seek to Resume time
                if (startTime > 0)
                {
                    await MediaInfoPlayer.SetPropertyAsync("start", startTime.ToString(CultureInfo.InvariantCulture));
                }

                // 5. Buffer settings
                bool isExplicitVod = _item is Models.Stremio.StremioMediaStream sms_pre && (sms_pre.Meta.Type == "movie" || sms_pre.Meta.Type == "series" || sms_pre.Meta.Type == "tv");
                if (_item is SeriesStream) isExplicitVod = true;
                bool isExplicitLive = (_item is LiveStream) || (_item is Models.Stremio.StremioMediaStream sms_l && sms_l.Meta.Type == "live");

                bool isLive = isExplicitLive || (_streamUrl != null && (_streamUrl.Contains("/live/") || _streamUrl.Contains(".m3u8") || _streamUrl.Contains(":8080") || _streamUrl.Contains("/ts")) && !isExplicitVod);
                await MpvSetupHelper.ApplyBufferSettingsAsync(MediaInfoPlayer, isSecondary: true, isLive: isLive);
                
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Buffer/Seek properties set.");

                // 6. Final UI Prep
                if (PlayerOverlayContainer != null)
                {
                    PlayerOverlayContainer.Visibility = Visibility.Visible;
                    PlayerOverlayContainer.Opacity = 0;
                }

                // 7. OpenAsync
                ct.ThrowIfCancellationRequested();
                await MediaInfoPlayer.SetPropertyAsync("pause", "yes"); 
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Calling OpenAsync (loadfile)...");
                await MediaInfoPlayer.OpenAsync(url);
                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - OpenAsync CALL returned.");
                
                await MediaInfoPlayer.SetPropertyAsync("mute", "yes");
                await MediaInfoPlayer.SetPropertyAsync("pause", "yes");

                Debug.WriteLine($"[Timer:MediaInfo] {swTotal.ElapsedMilliseconds}ms - Pre-buffering STARTED. Monitoring handshake...");
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[FastStart] Prebuffering CANCELLED (user navigated away).");
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    Debug.WriteLine("[FastStart] Prebuffering CANCELLED (user navigated away).");
                else
                    Debug.WriteLine($"[FastStart] Error: {ex.Message}");
            }
        }

        #endregion

        #region Player Handover & Page Transition Orchestration

        public async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            await _actionService.HandlePlayClickAsync(_item, _selectedEpisode, _streamUrl, sender);
        }

        private async Task PerformHandoverAndNavigate(string url, string title, string id = null, string parentId = null, string seriesName = null, int season = 0, int episode = 0, double startSeconds = -1, string posterUrl = null, string type = null, string backdropUrl = null)
        {
            _isHandoffInProgress = false; // Reset state
            _isNavigatingAway = true;
            
            // Skip handoff for Native (Media Foundation) Mode
            if (AppSettings.PlayerSettings.Engine != Models.PlayerEngine.Native)
            {
                try
                {
                    bool isPlayerActive = false;
                    if (MediaInfoPlayer != null)
                    {
                        try
                        {
                            if (_shouldAutoResume)
                            {
                                 isPlayerActive = false;
                                 Debug.WriteLine("[MediaInfoPage:Handoff] AutoResume active -> Forcing FRESH START (Skipping Handoff).");
                            }
                            else
                            {
                                string path = null;
                                try { path = await MediaInfoPlayer.GetPropertyAsync("path"); } catch { }

                                if (!string.IsNullOrEmpty(path) && path != "N/A")
                                {
                                    isPlayerActive = true;
                                    _isHandoffInProgress = true; // Confirmed Handoff
                                    App.HandoffPlayer = MediaInfoPlayer; 
                                    Debug.WriteLine($"[MediaInfoPage:Handoff] Player matched path: {path}");
                                    
                                    // PRE-WARM VISUALS
                                    _ = MpvSetupHelper.ApplyVisualSettingsAsync(MediaInfoPlayer);
                                    
                                    try 
                                    {
                                        MediaInfoPlayer.EnableHandoffMode();
                                        MediaInfoPlayer.EnsureSwapChainLinked();
                                    } catch { }

                                    try 
                                    {
                                        bool isExplicitVod = _item is Models.Stremio.StremioMediaStream sms_h && (sms_h.Meta.Type == "movie" || sms_h.Meta.Type == "series" || sms_h.Meta.Type == "tv");
                                        if (_item is SeriesStream) isExplicitVod = true;
                                        bool isExplicitLive = (_item is LiveStream) || (_item is Models.Stremio.StremioMediaStream sms_lh && sms_lh.Meta.Type == "live");

                                        bool isLive = isExplicitLive || (_streamUrl != null && (_streamUrl.Contains("/live/") || _streamUrl.Contains(".m3u8") || _streamUrl.Contains(":8080") || _streamUrl.Contains("/ts")) && !isExplicitVod);
                                        _ = MpvSetupHelper.ApplyBufferSettingsAsync(MediaInfoPlayer, isSecondary: false, isLive: isLive);
                                        _ = MediaInfoPlayer.SetPropertyAsync("pause", "no");
                                    } catch { }
                                    
                                    MediaInfoPlayer.EnsureSwapChainLinked();
                                    MediaInfoPlayer.EnableHandoffMode();
                                    
                                    var parent = MediaInfoPlayer.Parent;
                                    if (parent is Panel p) p.Children.Remove(MediaInfoPlayer);
                                    else if (parent is ContentControl cc) cc.Content = null;
                                }
                            }
                        }
                        catch (Exception ex) 
                        {
                            Debug.WriteLine($"[MediaInfoPage:Handoff] Player Check Failed: {ex.Message}");
                        }
                    }

                    if (!isPlayerActive)
                    {
                        Debug.WriteLine("[MediaInfoPage:Handoff] Player is not active or empty. Forcing FRESH START.");
                        
                        var pToDispose = MediaInfoPlayer;
                        App.HandoffPlayer = null;
                        MediaInfoPlayer = null;
                        _prebufferUrl = null;
                        
                        if (pToDispose != null)
                        {
                            DetachMediaInfoPlayerFromVisualTree(pToDispose);
                            CleanupMpvPlayerInBackground(pToDispose, "MediaInfo.RejectedHandoff");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaInfoPage:Handoff] ERROR: {ex}");
                }
            }
            else
            {
                Debug.WriteLine("[MediaInfoPage:Handoff] Native Mode detected: Skipping Handoff logic.");
                App.HandoffPlayer = null;
                _isHandoffInProgress = false;
            }

            // Always navigate to PlayerPage here
            Debug.WriteLine($"[MediaInfoPage:Handoff] Navigating to PlayerPage for {url} | StartSeconds: {startSeconds} | HasHandoff: {App.HandoffPlayer != null}");
            
            string yearStr = _unifiedMetadata?.Year;
            string ratingStr = _unifiedMetadata?.Rating.ToString("F1", CultureInfo.InvariantCulture);
            string durationStr = _selectedEpisode?.Duration ?? _unifiedMetadata?.Runtime;
            string overviewStr = _unifiedMetadata?.Overview;

            Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(
                url, title, id, parentId, seriesName, season, episode, startSeconds, posterUrl, type, backdropUrl, 
                GetLogoUrl(), _backgroundManager?.PrimaryColorHex ?? "#FF00BFA5", _sourceAddonUrl, yearStr, ratingStr, durationStr, overviewStr));
        }

        private string GetLogoUrl()
        {
            if (_unifiedMetadata != null && !string.IsNullOrEmpty(_unifiedMetadata.LogoUrl)) return _unifiedMetadata.LogoUrl;
            return null;
        }

        #endregion
    }
}
