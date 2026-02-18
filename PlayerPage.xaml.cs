using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using WinRT.Interop;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MpvWinUI; 
using System;
using System.Globalization; 
using Windows.Media.Core;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Linq;
using System.Net.Http; // Added for Diagnostic Check
using System.Reflection;
using Windows.Foundation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using ModernIPTVPlayer.Services.Stremio;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer
{
    public record PlayerNavigationArgs(string Url, string Title, string Id = null, string ParentId = null, string SeriesName = null, int Season = 0, int Episode = 0, double StartSeconds = -1);

    public sealed partial class PlayerPage : Page
    {

        private MpvPlayer? _mpvPlayer;
        private Compositor _compositor;
        private bool _useMpvPlayer = true;
        private string _streamUrl = string.Empty;
        private PlayerNavigationArgs _navArgs;
        private bool _isPageLoaded = false;
        private string? _navigationError = null;
        private DispatcherTimer? _statsTimer;
        private Microsoft.UI.Xaml.DispatcherTimer _seekDebounceTimer; // Timer for cumulative seek
        private int _pendingSeekSeconds = 0; // Accumulated seconds to seek
        private bool _isDragging = false;
        private bool _isBehind = false;
        private DateTime _lastSeekTime = DateTime.MinValue;
        private bool _isFullScreen = false;
        private bool _isDraggingSpeed = false;
        private double _dragStartY = 0;
        private double _dragStartOffset = 0;
        private string _lastAppliedSpeed = "1.0";
        private bool _isSnapping = false;
        private bool _blockNextClick = false;
        private double _lastVisualUpdateOffset = -1;
        private double _lastDragOffset = -1;
        private bool _isStaticMetadataFetched = false;
        private string _cachedResolution = "-";
        private string _cachedFps = "-";
        private string _cachedCodec = "-";

        // ---------- SUBTITLE & SYNC STATE ----------
        private bool _isAudioDelayMode = false; // true = Audio, false = Subtitle
        private double _audioDelayMs = 0;
        private double _subDelayMs = 0;
        private List<SubtitleLanguageViewModel> _subtitleLanguages = new();
        private List<SubtitleTrackViewModel> _currentSubtitleTracks = new();
        private List<SubtitleTrackViewModel> _cachedAddonSubtitles = new(); // Cache for fetched addon subs
        private string _lastAddonFetchId = null; // Track which ID we cached for

        public class SubtitleLanguageViewModel
        {
            public string Code { get; set; }
            public string Name { get; set; }
            public int Count { get; set; }
            public bool IsLoadingItem { get; set; } // For Shimmer
        }

        public class SubtitleTrackViewModel
        {
            public int Id { get; set; } // MPV Track ID
            public string Text { get; set; }
            public string Lang { get; set; }
            public bool IsAddon { get; set; }
            public string Url { get; set; } // For external/addon subs
            public bool IsSelected { get; set; }
            public string AddonName { get; set; } = "ADDON";
        }

        // Helper class to deserialize MPV track-list
        private class MpvTrack
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }
            [JsonPropertyName("type")]
            public string Type { get; set; } // "video", "audio", "sub"
            [JsonPropertyName("lang")]
            public string Lang { get; set; }
            [JsonPropertyName("title")]
            public string Title { get; set; }
            [JsonPropertyName("selected")]
            public bool Selected { get; set; }
            [JsonPropertyName("codec")]
            public string Codec { get; set; }
            [JsonPropertyName("audio-channels")]
            public int? AudioChannels { get; set; }
            [JsonPropertyName("demux-w")]
            public int? Width { get; set; }
            [JsonPropertyName("demux-h")]
            public int? Height { get; set; }
        }




        private string _cachedAudio = "-";
        private string _cachedColorspace = "-";
        private string _cachedHdr = "-";
        private int _nativeMonitorFps = 0;
        private bool _isHandoff = false;
        private bool _bufferUnlocked = false;
        private DateTime _lastFullScreenToggle = DateTime.MinValue;
        
        // Auto-Hide Logic
        private DispatcherTimer? _cursorTimer;
        private bool _controlsHidden = false;
        private bool _isPiPMode = false;

        
        private bool _isMpvStatsVisible = false;
        
        // [PiP] Single Window State Preservation
        private Windows.Graphics.RectInt32 _savedWindowBounds;
        private bool _savedIsFullScreen;
        private OverlappedPresenterState _savedPresenterState;

        public PlayerPage()
        {
            this.InitializeComponent();
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

            // UI Audio Feedback Setup
            this.ElementSoundMode = global::Microsoft.UI.Xaml.ElementSoundMode.Off;
            BackButton.ElementSoundMode = global::Microsoft.UI.Xaml.ElementSoundMode.Default;

            this.Loaded += PlayerPage_Loaded;
            // Hide default back button since we have a custom one and pane is closed
            // But navigation service back requests still need handling in MainWindow

            _statsTimer = new DispatcherTimer();
            _statsTimer.Interval = TimeSpan.FromMilliseconds(500);

            _cursorTimer = new DispatcherTimer();
            _cursorTimer.Interval = TimeSpan.FromSeconds(3);
            _cursorTimer.Tick += CursorTimer_Tick;
            _statsTimer.Tick += StatsTimer_Tick;
            
            _seekDebounceTimer = new Microsoft.UI.Xaml.DispatcherTimer();
            _seekDebounceTimer.Interval = TimeSpan.FromMilliseconds(500); // Wait 500ms after last click
            _seekDebounceTimer.Tick += SeekDebounceTimer_Tick;

            // Register speed dragging handlers globally on SpeedOverlay to ensure capture even if buttons swallow events
            SpeedOverlay.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerPressed), true);
            SpeedOverlay.AddHandler(PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerMoved), true);
            SpeedOverlay.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerReleased), true);
            SpeedOverlay.AddHandler(PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerReleased), true);
            SpeedOverlay.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerReleased), true);
            SpeedOverlay.AddHandler(PointerCanceledEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SpeedOverlay_PointerReleased), true);

            // [FIX] SeekSlider Pointer Events - Handle Handled Events too!
            // The Slider control swallows PointerPressed (Handled=true) for its own logic.
            // We must use AddHandler(..., true) to detect the start of a drag reliably.
            SeekSlider.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SeekSlider_PointerPressed), true);
            SeekSlider.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SeekSlider_PointerReleased), true);
            SeekSlider.AddHandler(PointerCanceledEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SeekSlider_PointerReleased), true);
            SeekSlider.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SeekSlider_PointerCaptureLost), true);
        }



        


        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            ElementSoundPlayer.Play(ElementSoundKind.GoBack);
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private async void StatsTimer_Tick(object? sender, object e)
        {
            if (_mpvPlayer == null || !_isPageLoaded) return;

            try 
            {
                // Sync Play/Pause State
                bool isPaused = await _mpvPlayer.GetPropertyBoolAsync("pause");
                PlayPauseIcon.Glyph = isPaused ? "\uE768" : "\uE769";

                // ---------- SEEKBAR & TIME LOGIC ----------
                string durationStr = await _mpvPlayer.GetPropertyAsync("duration");
                string positionStr = await _mpvPlayer.GetPropertyAsync("time-pos");
                
                double.TryParse(durationStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double duration);
                double.TryParse(positionStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double position);

                // ---------- HANDOFF BUFFER UNLOCK LOGIC ----------
                // Ensuring buffer limits are lifted only after playback truly begins prevent startup race conditions.
                if (_isHandoff && !_bufferUnlocked && position > 0.1)
                {
                    System.Diagnostics.Debug.WriteLine($"[HANDOFF_UNLOCK] Playback started at {position}s. Applying main buffer settings...");
                    
                    // Apply main buffer settings from user preferences
                    int bufferSecs = AppSettings.BufferSeconds;
                    await _mpvPlayer.SetPropertyAsync("cache", "yes");
                    await _mpvPlayer.SetPropertyAsync("demuxer-readahead-secs", bufferSecs.ToString());
                    await _mpvPlayer.SetPropertyAsync("demuxer-max-bytes", "2000MiB");
                    await _mpvPlayer.SetPropertyAsync("demuxer-max-back-bytes", "100MiB");
                    
                    _bufferUnlocked = true;
                }

                var seekable = await _mpvPlayer.GetPropertyAsync("seekable");
                // Fix: Default to isSeekable = true unless explicitly "no". 
                // This prevents "Live" UI from appearing during loading/buffering.
                bool isSeekable = (seekable != "no");

                // Disable Seek controls for linear streams (Live) to prevent freezing
                RewindButton.IsEnabled = isSeekable;
                FastForwardButton.IsEnabled = isSeekable;
                SeekSlider.IsEnabled = isSeekable;
                RewindButton.Opacity = isSeekable ? 1.0 : 0.5;
                FastForwardButton.Opacity = isSeekable ? 1.0 : 0.5;

                // Removed redundant "Live Detection Logic" block (lines 87-102) to avoid flickering
                // and rely on the robust "isLikelyLive" check below.
                
                string fileFormat = await _mpvPlayer.GetPropertyAsync("file-format");
                
                bool isMpegTs = (fileFormat == "mpegts");

                // Heuristic: Live if explicitly not seekable, 
                // Heuristic: Live if explicitly not seekable, 
                // OR mpegts (IPTV) with short duration (e.g. < 10 mins treated as rolling buffer).
                bool isLikelyLive = (!isSeekable) 
                                    || (isMpegTs && duration > 0 && duration < 600); 

                // Track Progress (VOD only)
                if (!isLikelyLive && _navArgs != null && position > 1 && duration > 0)
                {
                     string id = !string.IsNullOrEmpty(_navArgs.Id) ? _navArgs.Id : _navArgs.Url;
                     
                     // Capture current Audio and Subtitle Track IDs
                     string aid = await _mpvPlayer.GetPropertyAsync("aid");
                     string sid = await _mpvPlayer.GetPropertyAsync("sid");

                     HistoryManager.Instance.UpdateProgress(id, _navArgs.Title, _navArgs.Url, position, duration, _navArgs.ParentId, _navArgs.SeriesName, _navArgs.Season, _navArgs.Episode, aid, sid);
                }

                if (!isLikelyLive)
                {
                    // VOD MODE
                    LiveButton.Visibility = Visibility.Collapsed;
                    TimeTextBlock.Visibility = Visibility.Visible;
                    SeekSlider.Visibility = Visibility.Visible;
                    SeekSlider.IsEnabled = true;

                    // Only update slider if user is completely hands-off
                    if (!_isDragging && (DateTime.Now - _lastSeekTime).TotalSeconds > 3.0)
                    {
                        SeekSlider.Maximum = duration;
                        SeekSlider.Value = position;
                    }
                    
                    TimeSpan tPos = TimeSpan.FromSeconds(position);
                    TimeSpan tDur = TimeSpan.FromSeconds(duration);
                    TimeTextBlock.Text = tDur.TotalHours >= 1 
                        ? $"{tPos:hh\\:mm\\:ss} / {tDur:hh\\:mm\\:ss}"
                        : $"{tPos:mm\\:ss} / {tDur:mm\\:ss}";
                }
                else
                {
                    // LIVE MODE
                    LiveButton.Visibility = Visibility.Visible;
                    TimeTextBlock.Visibility = Visibility.Collapsed;
                    SeekSlider.Visibility = Visibility.Collapsed;
                    
                    if (isPaused || _isBehind)
                    {
                        LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
                        LiveText.Text = "CANLI (GERİDE)";
                    }
                    else
                    {
                        LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                        LiveText.Text = "CANLI";
                    }
                }

                // ---------- STATS UPDATES (Conditional & Cached) ----------
                // ---------- STATS UPDATES (Conditional & Cached) ----------
                
                // 1. Static Metadata (Fetched once automatically on start, then refreshed occasionally)
                int metadataRefreshTicks = 20; // Every 10 seconds (20 * 500ms)
                bool shouldRefreshMetadata = !_isStaticMetadataFetched || (DateTime.Now.Second % 10 == 0 && _isPageLoaded);

                if (shouldRefreshMetadata)
                {
                    // Resolution consolidation
                    var wSize = await _mpvPlayer.GetPropertyAsync("video-params/w");
                    var hSize = await _mpvPlayer.GetPropertyAsync("video-params/h");
                    
                    if (!string.IsNullOrEmpty(wSize) && wSize != "N/A" && wSize != "0")
                    {
                        string newRes = $"{wSize}x{hSize}";
                        
                        // FPS consolidation
                        var fpsValStr = await _mpvPlayer.GetPropertyAsync("estimated-fps");
                        if (string.IsNullOrEmpty(fpsValStr) || fpsValStr == "N/A") fpsValStr = await _mpvPlayer.GetPropertyAsync("container-fps");
                        
                        string newFps = "- fps";
                        if (double.TryParse(fpsValStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double fv))
                        {
                            newFps = $"{fv:F2} fps";
                        }

                        // If something meaningful changed or it's the first fetch, update UI and CACHE
                        if (newRes != _cachedResolution || newFps != _cachedFps || !_isStaticMetadataFetched)
                        {
                            _cachedResolution = newRes;
                            _cachedFps = newFps;
                            if (_nativeMonitorFps > 0) _cachedFps += $" / {_nativeMonitorFps}Hz";

                            // Codec (Video & Audio)
                            string rawCodec = await _mpvPlayer.GetPropertyAsync("video-codec");
                            _cachedCodec = GetShortCodecName(rawCodec);

                            string audioCodec = await _mpvPlayer.GetPropertyAsync("audio-codec");
                            string audioChannels = await _mpvPlayer.GetPropertyAsync("audio-params/hr-channels");
                            if (string.IsNullOrEmpty(audioChannels) || audioChannels == "N/A")
                                audioChannels = await _mpvPlayer.GetPropertyAsync("audio-out-params/channels");
                            
                            _cachedAudio = $"{GetShortCodecName(audioCodec).ToUpper()} ({audioChannels})";
                            
                            _cachedColorspace = await _mpvPlayer.GetPropertyAsync("video-params/colormatrix");
                            if (string.IsNullOrEmpty(_cachedColorspace) || _cachedColorspace == "N/A") _cachedColorspace = "-";

                            // HDR / Tone Mapping
                            string hdrStatus = await _mpvPlayer.GetPropertyAsync("video-out-params/sig-peak");
                            string gamma = await _mpvPlayer.GetPropertyAsync("video-out-params/gamma");
                            if (!string.IsNullOrEmpty(hdrStatus) && hdrStatus != "N/A" && double.TryParse(hdrStatus, NumberStyles.Any, CultureInfo.InvariantCulture, out double peak) && peak > 1.0)
                                _cachedHdr = $"HDR ({peak:F1} nits)";
                            else
                                _cachedHdr = $"SDR ({gamma})";

                            // Update UI
                            TxtResolution.Text = _cachedResolution;
                            TxtFps.Text = _cachedFps;
                            TxtCodec.Text = _cachedCodec.ToUpper();
                            TxtAudioCodec.Text = _cachedAudio;
                            TxtColorspace.Text = _cachedColorspace;
                            TxtHdr.Text = _cachedHdr;
                            
                            PillResolution.Text = _cachedResolution;
                            PillFps.Text = _cachedFps;
                            PillCodec.Text = _cachedCodec;

                            _isStaticMetadataFetched = true;
                            ShowInfoPills();

                            // [CACHE UPDATE] Update global cache with real playback data
                            try 
                            {
                                bool isHdr = _cachedHdr.Contains("HDR");
                                string simpleFps = _cachedFps.Split(' ')[0] + " fps";
                                
                                // Fetch real-time bitrate from demuxer if possible, or use current video-bitrate
                                string brStr = await _mpvPlayer.GetPropertyAsync("video-bitrate");
                                long bitrate = 0;
                                if (double.TryParse(brStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double brVal)) 
                                    bitrate = (long)brVal;

                                Services.ProbeCacheService.Instance.Update(_streamUrl, _cachedResolution, simpleFps, _cachedCodec, bitrate, isHdr);
                                Debug.WriteLine($"[PlayerPage] Metadata Updated in Global Cache for {_streamUrl} ({_cachedResolution})");
                            }
                            catch (Exception ex) { Debug.WriteLine($"[PlayerPage] Global Cache Update Failed: {ex.Message}"); }
                        }
                    }
                }

                if (StatsOverlay.Visibility == Visibility.Visible)
                {
                    // 2. Dynamic Metadata (Always poll when visible)
                    var bitrate = await _mpvPlayer.GetPropertyAsync("video-bitrate");
                    TxtBitrate.Text = FormatBitrate(bitrate);
                    
                    // Improved speed detection: Real-time network activity
                    // Probe showed 'cache-speed' is the working property for this environment
                    long speedVal = await GetPropertyLongSafe("cache-speed");
                    TxtSpeed.Text = FormatSpeedLong(speedVal);

                    var hwdec = await _mpvPlayer.GetPropertyAsync("hwdec-current");
                    var vo = await _mpvPlayer.GetPropertyAsync("vo-configured");
                    TxtHardware.Text = (hwdec != "no" && !string.IsNullOrEmpty(hwdec)) ? $"{hwdec.ToUpper()} ({vo})" : $"SOFTWARE ({vo})";
                    TxtRenderer.Text = _mpvPlayer.RenderApi == "d3d11" ? "GPU-NEXT (Placebo)" : "GPU (Legacy DXGI)";

                    // Drops & AV Sync
                    try 
                    {
                        // Some MPV versions use frame-drop-count directly for total drops
                        long decDrops = await GetPropertyLongSafe("frame-drop-count");
                        if (decDrops < 0) decDrops = await GetPropertyLongSafe("vo-drop-frame-count");
                        if (decDrops < 0) decDrops = await GetPropertyLongSafe("decoder-frame-drop-count");
                        
                        string avSync = await _mpvPlayer.GetPropertyAsync("avsync");
                        string buffDur = await _mpvPlayer.GetPropertyAsync("demuxer-cache-duration");

                        // Drops
                        TxtDroppedDecoder.Text = decDrops >= 0 ? $"{decDrops}" : "0";

                        // AV Sync
                        if (double.TryParse(avSync, NumberStyles.Any, CultureInfo.InvariantCulture, out double avVal))
                        {
                             TxtAvSync.Text = $"{avVal * 1000:F1} ms"; // Show in ms
                             TxtAvSync.Foreground = (Math.Abs(avVal) > 0.1) 
                                 ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange) 
                                 : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                        }
                        else
                        {
                             TxtAvSync.Text = "-";
                        }

                        // Buffer
                        if (double.TryParse(buffDur, NumberStyles.Any, CultureInfo.InvariantCulture, out double buffVal))
                        {
                             TxtBuffer.Text = $"{buffVal:F1}s";
                        }
                        else
                        {
                             TxtBuffer.Text = "0s";
                        }
                    }
                    catch (Exception)
                    {
                        TxtDroppedDecoder.Text = "-";
                        TxtAvSync.Text = "-";
                        TxtBuffer.Text = "-";
                    }
                }
            }
            catch { /* Ignore errors during polling */ }
        }


        private async Task<long> GetPropertyLongSafe(string name)
        {
            if (_mpvPlayer == null) return -1;
            
            try
            {
                // [FIX] Use String-based retrieval to avoid "MpvException: property unavailable" 
                // which seems to crash GetPropertyToLong even with try-catch blocks in some environments.
                string valStr = await _mpvPlayer.GetPropertyAsync(name);
                
                if (string.IsNullOrEmpty(valStr) || valStr == "N/A") return -1;

                if (long.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out long lVal))
                {
                    return lVal;
                }
                
                // Fallback for floating point values returned as strings (e.g. "123.45")
                if (double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal))
                {
                    return (long)dVal;
                }

                return -1;
            }
            catch
            {
                return -1;
            }
        }

        private string FormatBitrate(string? bitrate)
        {
            if (string.IsNullOrEmpty(bitrate) || bitrate == "N/A") return "Calculating...";
            if (double.TryParse(bitrate, NumberStyles.Any, CultureInfo.InvariantCulture, out double brVal))
            {
                if (brVal > 1000000) return $"{brVal / 1000000:F1} Mbps";
                return $"{brVal / 1000:F0} kbps";
            }
            return "-";
        }

        private string FormatSpeedLong(long sVal)
        {
            if (sVal <= 0) return "0 KB/s";
            double mbps = (sVal * 8.0) / 1000000.0;
            if (sVal > 1024 * 1024) 
                return $"{(double)sVal / (1024 * 1024):F2} MB/s ({mbps:F1} Mbps)";
            return $"{(double)sVal / 1024:F0} KB/s ({mbps:F2} Mbps)";
        }

        private void ShowInfoPills()
        {
            if (InfoPillsStack == null) return;

            PillResolution.Text = _cachedResolution;
            PillFps.Text = _cachedFps;
            PillCodec.Text = _cachedCodec;

            if (InfoPillsStack.Visibility != Visibility.Visible)
            {
                 InfoPillsStack.Visibility = Visibility.Visible;
                 ShowInfoPillsAnim.Begin();
            }
        }

        protected override async void OnKeyDown(KeyRoutedEventArgs e)
        {
            base.OnKeyDown(e);
            if (_mpvPlayer == null) return;

            if (e.Key == Windows.System.VirtualKey.Left)
            {
                // Seek Backward 10s
                ShowOsd("-10 SN");
                await _mpvPlayer.ExecuteCommandAsync("seek", "-10", "relative");
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Right)
            {
                // Seek Forward 30s
                ShowOsd("+30 SN");
                await _mpvPlayer.ExecuteCommandAsync("seek", "30", "relative");
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.I)
            {
                // Toggle MPV stats overlay via script-binding (correct method for stats.lua)
                // stats.lua registers: mp.add_key_binding(nil, "display-stats-toggle", ...)
                // Triggered via: script-binding stats/display-stats-toggle
                _isMpvStatsVisible = !_isMpvStatsVisible;
                await _mpvPlayer.ExecuteCommandAsync("script-binding", "stats/display-stats-toggle");
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number1 || e.Key == Windows.System.VirtualKey.NumberPad1)
            {
                if (_isMpvStatsVisible)
                    await _mpvPlayer.ExecuteCommandAsync("script-binding", "stats/display-page-1");
                else
                    await _mpvPlayer.ExecuteCommandAsync("add", "contrast", "-1"); // Default MPV: 1 decreases contrast
                
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number2 || e.Key == Windows.System.VirtualKey.NumberPad2)
            {
                if (_isMpvStatsVisible)
                    await _mpvPlayer.ExecuteCommandAsync("script-binding", "stats/display-page-2");
                else
                    await _mpvPlayer.ExecuteCommandAsync("add", "contrast", "1"); // Default MPV: 2 increases contrast

                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number3 || e.Key == Windows.System.VirtualKey.NumberPad3)
            {
                if (_isMpvStatsVisible)
                    await _mpvPlayer.ExecuteCommandAsync("script-binding", "stats/display-page-3");
                else
                    await _mpvPlayer.ExecuteCommandAsync("add", "brightness", "-1"); // Default MPV: 3 decreases brightness

                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number4 || e.Key == Windows.System.VirtualKey.NumberPad4)
            {
                if (_isMpvStatsVisible)
                    await _mpvPlayer.ExecuteCommandAsync("script-binding", "stats/display-page-4");
                else
                    await _mpvPlayer.ExecuteCommandAsync("add", "brightness", "1"); // Default MPV: 4 increases brightness

                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number5 || e.Key == Windows.System.VirtualKey.NumberPad5)
            {
                if (_isMpvStatsVisible)
                    await _mpvPlayer.ExecuteCommandAsync("script-binding", "stats/display-page-5");
                else
                    await _mpvPlayer.ExecuteCommandAsync("add", "gamma", "-1"); // Default MPV: 5 decreases gamma

                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number6 || e.Key == Windows.System.VirtualKey.NumberPad6)
            {
                if (!_isMpvStatsVisible)
                    await _mpvPlayer.ExecuteCommandAsync("add", "gamma", "1"); // Default MPV: 6 increases gamma
                
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number7 || e.Key == Windows.System.VirtualKey.NumberPad7)
            {
                if (!_isMpvStatsVisible)
                    await _mpvPlayer.ExecuteCommandAsync("add", "saturation", "-1"); // Default MPV: 7 decreases saturation
                
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number8 || e.Key == Windows.System.VirtualKey.NumberPad8)
            {
                if (!_isMpvStatsVisible)
                    await _mpvPlayer.ExecuteCommandAsync("add", "saturation", "1"); // Default MPV: 8 increases saturation
                
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number9 || e.Key == Windows.System.VirtualKey.NumberPad9)
            {
                if (!_isMpvStatsVisible)
                    await _mpvPlayer.ExecuteCommandAsync("add", "volume", "-2"); // Default MPV: 9 decreases volume
                
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Number0 || e.Key == Windows.System.VirtualKey.NumberPad0)
            {
                if (_isMpvStatsVisible)
                    // Stats page 0 often used for keybindings list or internal stats
                    await _mpvPlayer.ExecuteCommandAsync("script-binding", "stats/display-page-0"); 
                else
                    await _mpvPlayer.ExecuteCommandAsync("add", "volume", "2"); // Default MPV: 0 increases volume

                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.S)
            {
                // Toggle Custom Stats Overlay
                StatsOverlay.Visibility = StatsOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                e.Handled = true;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {

            base.OnNavigatedTo(e);

            if (e.Parameter is string url)
            {
                _streamUrl = url;
                VideoTitleText.Text = ""; // No title provided
                _navArgs = new PlayerNavigationArgs(url, "");
            }
            else if (e.Parameter is PlayerNavigationArgs args)
            {
                _streamUrl = args.Url;
                VideoTitleText.Text = args.Title;
                _navArgs = args;
            }
            else
            {
                _navigationError = "Geçersiz yayın URL'si alındı.";
            }

            _navigationError = null;
        }
        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {

            // 1. Stop timer IMMEDIATELY
            _statsTimer?.Stop();
            StopCursorTimer();
            _seekDebounceTimer?.Stop();
            _isPageLoaded = false;
            
            // Save Progress
            _ = HistoryManager.Instance.SaveAsync();

            if (_isFullScreen)
            {
                MainWindow.Current.SetFullScreen(false);
                _isFullScreen = false;
            }

            if (_isPiPMode)
            {
                MainWindow.Current.SetCompactOverlay(false);
                _isPiPMode = false;
            }

            base.OnNavigatedFrom(e);

            // 2. Cleanup MPV carefully
            if (_mpvPlayer is not null)
            {
                // Detach from visual tree first
                try {
                    if (PlayerContainer != null && PlayerContainer.Children.Contains(_mpvPlayer))
                        PlayerContainer.Children.Remove(_mpvPlayer);
                } catch { }

                if (_isHandoff)
                {
                    // If it was a handoff, we DON'T CleanupAsync because the control belongs to MediaInfoPage.
                    // CleanupAsync destroys the native MpvContext, making the control unusable on the previous page.
                    // Instead, we just PAUSE playback to keep buffer in RAM.
                    _ = _mpvPlayer.ExecuteCommandAsync("set", "pause", "yes");
                    
                    // We DO NOT call DisableHandoffMode(); -> PreserveStateOnUnload keeps RenderControl alive.
                    
                    Debug.WriteLine("[PlayerPage] Returned handed-off player to source page (Paused, Buffer Preserved).");
                }
                else
                {
                    try
                    {
                        // Fresh player created on this page: Full destruction is safe and required.

                        await _mpvPlayer.CleanupAsync();
                        // _mpvPlayer.CleanupAsync() COMPLETED
                    }
                    catch (Exception ex) { Debug.WriteLine($"[LIFECYCLE] Cleanup Error: {ex}"); }
                }
                _mpvPlayer = null;
            }
            else
            {
                MediaFoundationPlayer.MediaPlayer.Pause();
                MediaFoundationPlayer.Source = null;
            }

        }

        private async Task ShowMessageDialog(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new ScrollViewer { Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap } },
                CloseButtonText = "Kapat",
                PrimaryButtonText = "Kopyala",
                XamlRoot = this.XamlRoot
            };

            dialog.PrimaryButtonClick += (s, e) =>
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(content);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                e.Cancel = true; 
                dialog.PrimaryButtonText = "Kopyalandı!";
            };

            await dialog.ShowAsync();
        }

        // Helper to extract ALL cookies from a CookieContainer (ignoring Domain restrictions)
        // MOVED TO MpvSetupHelper


        private async void PlayerPage_Loaded(object sender, RoutedEventArgs e)
        {

            if (_isPageLoaded) return;
            
            _isPageLoaded = true;
            _isStaticMetadataFetched = false;
            _cachedResolution = "-";
            _cachedFps = "-";
            _cachedCodec = "-";
            _cachedColorspace = "-";

            StartCursorTimer();



            if (!string.IsNullOrEmpty(_navigationError))
            {
                await ShowMessageDialog("Hata", _navigationError);
                _navigationError = null;
                return;
            }

            if (_useMpvPlayer)
            {
                MediaFoundationPlayer.Visibility = Visibility.Collapsed;
                MediaFoundationPlayer.Source = null;
                try { MediaFoundationPlayer.MediaPlayer?.Pause(); } catch {}

                if (App.HandoffPlayer != null)
                {
                    // HANDOFF MODE: FAST PATH (No Delay!)
                    Debug.WriteLine("[PlayerPage] Doing Handoff...");
                    _isHandoff = true;
                    _bufferUnlocked = false;
                    _mpvPlayer = App.HandoffPlayer;
                    App.HandoffPlayer = null; 
                    
                    PlayerContainer.Children.Add(_mpvPlayer);
                    _mpvPlayer.Visibility = Visibility.Visible;
                    _mpvPlayer.Opacity = 1;
                    _mpvPlayer.IsHitTestVisible = false;
                    _mpvPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                    _mpvPlayer.VerticalAlignment = VerticalAlignment.Stretch;


                    _mpvPlayer.Redraw();

                    // 1. Initial State Restoration & UI Activation
                    _statsTimer?.Start(); 
                    await _mpvPlayer.SetPropertyAsync("pause", "no");
                    await _mpvPlayer.SetPropertyAsync("mute", "no");

                    // 2. Apply main buffer settings from user preferences
                    int mainBuffer = AppSettings.BufferSeconds;
                    _ = _mpvPlayer.SetPropertyAsync("cache", "yes");
                    await _mpvPlayer.SetPropertyAsync("demuxer-readahead-secs", mainBuffer.ToString());
                    await _mpvPlayer.SetPropertyAsync("demuxer-max-bytes", "2000MiB");
                    await _mpvPlayer.SetPropertyAsync("demuxer-max-back-bytes", "100MiB");

                    // 3. Verify and Fix
                    var pPause = await _mpvPlayer.GetPropertyAsync("pause");
                    var pMute = await _mpvPlayer.GetPropertyAsync("mute");
                    var pIdle = await _mpvPlayer.GetPropertyAsync("core-idle");
                    var pPath = await _mpvPlayer.GetPropertyAsync("path");
                    Debug.WriteLine($"[PlayerPage:Handoff_Verify] State: Pause={pPause}, Mute={pMute}, CoreIdle={pIdle}, Path={pPath}");

                    if (string.IsNullOrEmpty(pPath) || pPath == "N/A")
                    {
                        Debug.WriteLine("[PlayerPage:Handoff] Path is EMPTY or INVALID! Player lost content. Reloading URL...");
                        
                        // [OPTIMIZATION] Set start time BEFORE OpenAsync to avoid seeking delay (Handoff Recovery)
                        if (_navArgs != null && _navArgs.StartSeconds > 0)
                        {
                            Debug.WriteLine($"[PlayerPage:Handoff] Setting start time for recovery: {_navArgs.StartSeconds}");
                            await _mpvPlayer.SetPropertyAsync("start", _navArgs.StartSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }

                        await _mpvPlayer.OpenAsync(_navArgs.Url);
                        await _mpvPlayer.SetPropertyAsync("pause", "no");
                    }
                    else if (pIdle == "yes")
                    {
                        // Some streams need a kick after attachment
                        Debug.WriteLine("[PlayerPage:Handoff] Player is stuck idle. Retrying unpause...");
                        await _mpvPlayer.SetPropertyAsync("pause", "no");
                        await Task.Delay(200);
                        await _mpvPlayer.SetPropertyAsync("pause", "no");
                        _mpvPlayer.Redraw(); // Force redraw after unpause kick
                    }

                    // Fast Start prebuffer already sought to the correct position - no need to seek again
                    Debug.WriteLine("[PlayerPage:Handoff] Prebuffer ready, skipping seek logic");

                    // Auto-fetch addon subtitles in background for auto-restore
                    _ = AutoFetchAndRestoreAddonSubtitleAsync();
                }
                else
                {
                     // FRESH START MODE: SLOW PATH (Delay for socket safety)
                     Debug.WriteLine("[PlayerPage] Starting Fresh Playback...");
                     Services.Streaming.StreamSlotSimulator.Instance.StopAll();
                     // REMOVED: await Task.Delay(1200); -> Unnecessary delay
                     await Task.Delay(50); // Minimal safety check

                     _mpvPlayer = new MpvWinUI.MpvPlayer();

                     // [GPU-NEXT Integration] Select backend based on settings
                     try 
                     {
                         var pSettings = AppSettings.PlayerSettings;
                         if (pSettings.VideoOutput == ModernIPTVPlayer.Models.VideoOutput.GpuNext)
                         {
                             _mpvPlayer.RenderApi = "d3d11";
                         }
                         else
                         {
                             _mpvPlayer.RenderApi = "dxgi";
                         }
                         Debug.WriteLine($"[PlayerPage] Selected Render API: {_mpvPlayer.RenderApi}");
                     }
                     catch (Exception ex)
                     {
                         Debug.WriteLine($"[PlayerPage] Failed to load settings for RenderApi: {ex.Message}");
                         _mpvPlayer.RenderApi = "d3d11"; // Default to gpu-next
                     }

                     PlayerContainer.Children.Add(_mpvPlayer);
                     _mpvPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                     _mpvPlayer.VerticalAlignment = VerticalAlignment.Stretch;
                     _mpvPlayer.IsHitTestVisible = false;

                     try
                    {
                        ShowOsd("Bağlantı Kontrol Ediliyor...");
                        
                        // [OPTIMIZATION] Skip probe for known fast providers to save ~1-2s
                        bool isTrusted = _streamUrl.Contains("torbox") || _streamUrl.Contains("real-debrid") || _streamUrl.Contains("alldebrid") || _streamUrl.Contains("premiumize");
                        
                        if (!isTrusted)
                        {
                            var checkResult = await CheckStreamUrlAsync(_streamUrl);
                            if (!checkResult.Success)
                            {
                                await ShowMessageDialog("Yayın Hatası", checkResult.ErrorMsg);
                                if (Frame.CanGoBack) Frame.GoBack();
                                return;
                            }
                            _streamUrl = checkResult.Url;
                        }
                        else
                        {
                             Debug.WriteLine("[PlayerPage] Trusted Source detected. Skipping pre-check.");
                        }

                        // [Fix] Check if player was disposed during check
                        if (_mpvPlayer == null) return;

                        await MpvSetupHelper.ConfigurePlayerAsync(_mpvPlayer, _streamUrl, isSecondary: false);
                        
                        // [FIX] Apply Buffer Settings (Same as Handoff)
                        int bufferSecs = AppSettings.BufferSeconds;
                        await _mpvPlayer.SetPropertyAsync("cache", "yes");
                        await _mpvPlayer.SetPropertyAsync("demuxer-readahead-secs", bufferSecs.ToString());
                        await _mpvPlayer.SetPropertyAsync("demuxer-max-bytes", "2000MiB");
                        await _mpvPlayer.SetPropertyAsync("demuxer-max-back-bytes", "100MiB");
                        
                        // [Fix] Double check before OpenAsync
                        if (_mpvPlayer == null) return; 

                        // [OPTIMIZATION] Set start time BEFORE OpenAsync to avoid seeking delay
                        if (_navArgs != null && _navArgs.StartSeconds > 0)
                        {
                            Debug.WriteLine($"[PlayerPage] Setting start time: {_navArgs.StartSeconds}");
                            await _mpvPlayer.SetPropertyAsync("start", _navArgs.StartSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }

                        await _mpvPlayer.OpenAsync(_streamUrl);

                        
                        // Detect Physical Refresh Rate
                        try
                        {
                             var ptr = GetForegroundWindow();
                             var monitor = MonitorFromWindow(ptr, MONITOR_DEFAULTTONEAREST);
                             var devMode = new DEVMODE();
                             devMode.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DEVMODE));
                             if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref devMode))
                             {
                                 if (devMode.dmDisplayFrequency > 0)
                                 {
                                     _mpvPlayer?.SetDisplayFps(devMode.dmDisplayFrequency);
                                 }
                             }
                        }
                        catch {}

                        _statsTimer?.Start();
                        SetupProfessionalAnimations();

                        _statsTimer?.Start();
                        SetupProfessionalAnimations();
                        
                        // [RESTORE SAVED AUDIO/SUBTITLE TRACKS]
                         string contentId = !string.IsNullOrEmpty(_navArgs?.Id) ? _navArgs.Id : _streamUrl;
                         var history = HistoryManager.Instance.GetProgress(contentId);
                         if (history != null)
                         {
                             if (!string.IsNullOrEmpty(history.AudioTrackId))
                             {
                                 Debug.WriteLine($"[PlayerPage] Restoring Audio Track: {history.AudioTrackId}");
                                 await _mpvPlayer.SetPropertyAsync("aid", history.AudioTrackId);
                             }
                             if (!string.IsNullOrEmpty(history.SubtitleTrackId))
                             {
                                 Debug.WriteLine($"[PlayerPage] Restoring Subtitle Track: {history.SubtitleTrackId}");
                                 await _mpvPlayer.SetPropertyAsync("sid", history.SubtitleTrackId);
                             }
                        }

                        // [FRESH START SEEK LOGIC]
                        // REMOVED: Replaced with 'start' property before OpenAsync for instant seeking.

                        // Auto-fetch addon subtitles in background for auto-restore
                        _ = AutoFetchAndRestoreAddonSubtitleAsync();

                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[PlayerPage] MPV Error: {ex}");
                        if (_mpvPlayer == null)
                        {
                            await ShowMessageDialog("Oynatıcı Hatası", "Video oynatıcı başlatılamadı.");
                        }
                        else
                        {
                            await ShowMessageDialog("MPV Oynatıcı Hatası", $"MPV başlatılamadı. \n\nHata: {ex.Message}");
                        }
                    }
                }
            }
            else
            {
                MediaFoundationPlayer.Visibility = Visibility.Visible;
                try
                {
                    MediaFoundationPlayer.Source = MediaSource.CreateFromUri(new Uri(_streamUrl));
                }
                catch (Exception ex)
                {
                    await ShowMessageDialog("Oynatıcı Hatası", $"Video yüklenemedi. \n\nHata: {ex.Message}");
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);
        private const int ENUM_CURRENT_SETTINGS = -1;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct DEVMODE
        {
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        private void SeekSlider_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isDragging = true;
        }

        private async void SeekSlider_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            e.Handled = true;
            if (_mpvPlayer == null) return;
            _isDragging = false;
            _lastSeekTime = DateTime.Now;
            var val = SeekSlider.Value;
            await _mpvPlayer.ExecuteCommandAsync("seek", val.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
        }

        private void SeekSlider_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Just reset the flag, don't trigger a seek if capture was lost involuntarily
            _isDragging = false;
        }

        private async void SeekSlider_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
             e.Handled = true;
             if (_mpvPlayer != null && SeekSlider.ActualWidth > 0)
             {
                 var pos = e.GetPosition(SeekSlider);
                 var ratio = pos.X / SeekSlider.ActualWidth;
                 var newVal = ratio * SeekSlider.Maximum;
                 SeekSlider.Value = newVal;

                 _lastSeekTime = DateTime.Now;
                 await _mpvPlayer.ExecuteCommandAsync("seek", newVal.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
             }
        }

        private void InteractiveControl_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            e.Handled = true;
            _isGestureTarget = false;
            ResetCursorTimer();
        }

        private void InteractiveControl_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            e.Handled = true;
            ResetCursorTimer();
        }

        private async void LiveButton_Click(object sender, RoutedEventArgs e)
        {
             if (_mpvPlayer == null) return;

             // Check if seekable
             var seekable = await _mpvPlayer.GetPropertyAsync("seekable");
             
             if (seekable == "no")
             {
                 ShowOsd("YAYIN YENİDEN YÜKLENİYOR (LIVE)...");
                 await _mpvPlayer.OpenAsync(_streamUrl);
                 await _mpvPlayer.SetPropertyAsync("pause", "no"); // Force play
                 
                 // Force UI update
                 LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                 LiveText.Text = "CANLI";
                 return;
             }

             ShowOsd("CANLI YAYINA GİDİLİYOR...");
             
             // Resume just in case
             await _mpvPlayer.SetPropertyAsync("pause", "no");
             _isBehind = false;

             // Force explicit seek to end using Percent (Cleanest for HLS Live)
             ShowOsd("SEEKING TO LIVE...");
             await LogStatus("PRE-LIVE-SEEK");
             await _mpvPlayer.ExecuteCommandAsync("seek", "100", "absolute-percent+exact");
             await LogStatus("POST-LIVE-SEEK");

             // Force UI update
             LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
             LiveText.Text = "CANLI";
        }

        private void ControlBar_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true; // Prevent triggering Page_DoubleTapped (Fullscreen toggle)
        }



        private async Task LogStatus(string context)
        {
             if (_mpvPlayer == null) return;
             var cacheDur = await _mpvPlayer.GetPropertyAsync("demuxer-cache-duration");
             var paused = await _mpvPlayer.GetPropertyAsync("core-idle"); // is core waiting?
             var buffering = await _mpvPlayer.GetPropertyAsync("paused-for-cache");
             var time = await _mpvPlayer.GetPropertyAsync("time-pos");
             
             var log = $"[{context}] Time:{time} Cache:{cacheDur} Idle:{paused} Buffering:{buffering}";
             System.Diagnostics.Debug.WriteLine(log);
        }

        private async void RewindButton_Click(object sender, RoutedEventArgs e)
        {
             if (_mpvPlayer == null || !RewindButton.IsEnabled) return;
             
             _pendingSeekSeconds -= 10;
             ShowOsd($"{(_pendingSeekSeconds > 0 ? "+" : "")}{_pendingSeekSeconds} SANİYE");
             
             _seekDebounceTimer.Stop(); // Reset timer
             _seekDebounceTimer.Start();
        }

        private async void FastForwardButton_Click(object sender, RoutedEventArgs e)
        {
             if (_mpvPlayer == null || !FastForwardButton.IsEnabled) return;
             
             _pendingSeekSeconds += 30;
             ShowOsd($"{(_pendingSeekSeconds > 0 ? "+" : "")}{_pendingSeekSeconds} SANİYE");
             
             _seekDebounceTimer.Stop(); // Reset timer
             _seekDebounceTimer.Start();
        }
        
        private async void SeekDebounceTimer_Tick(object? sender, object e)
        {
             _seekDebounceTimer.Stop();
             if (_pendingSeekSeconds == 0 || _mpvPlayer == null) return;

             try
             {
                 int seekVal = _pendingSeekSeconds;
                 _pendingSeekSeconds = 0; // Reset pending immediately

                 if (seekVal < 0) _isBehind = true;

                 ShowOsd($"GİDİLİYOR: {(seekVal > 0 ? "+" : "")}{seekVal}sn");
                 await LogStatus($"PRE-DEBOUNCE-SEEK({seekVal})");
                 await _mpvPlayer.ExecuteCommandAsync("seek", seekVal.ToString(), "relative");
                 await LogStatus($"POST-DEBOUNCE-SEEK({seekVal})");
             }
             catch { }
        }

        private void SpeedFlyout_Opening(object sender, object e)
        {
        }

        private void UpdateFullScreenUI()
        {
            if (_isFullScreen)
            {
                FullScreenIcon.Glyph = "\uE1D8"; // BackToWindow icon
                ToolTipService.SetToolTip(FullScreenButton, "Tam Ekrandan Çık");
            }
            else
            {
                FullScreenIcon.Glyph = "\uE1D9"; // Fullscreen icon
                ToolTipService.SetToolTip(FullScreenButton, "Tam Ekran");
            }
        }

        private void ToggleFullScreen()
        {
            // Guard against redundant calls or rapid clicks (winui3/appwindow sensitive to rapid state changes)
            if (DateTime.Now - _lastFullScreenToggle < TimeSpan.FromMilliseconds(500)) return;
            _lastFullScreenToggle = DateTime.Now;

            _isFullScreen = !_isFullScreen;
            MainWindow.Current.SetFullScreen(_isFullScreen);
            UpdateFullScreenUI();
        }

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullScreen();
        }





        private async void CloseSpeedOverlay()
        {
            if (SpeedOverlay.Visibility == Visibility.Visible)
            {
                HideSpeedOverlayAnim.Begin();
                await Task.Delay(200); // Wait for anim
                SpeedOverlay.Visibility = Visibility.Collapsed;
                // Resume StatsTimer
                _statsTimer?.Start();
            }
        }

        private async void CloseVolumeOverlay()
        {
            if (VolumeOverlay.Visibility == Visibility.Visible)
            {
                HideVolumeOverlayAnim.Begin();
                await Task.Delay(200);
                VolumeOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void CloseStatsOverlay()
        {
            if (StatsOverlay != null)
            {
                StatsOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async void CloseTracksOverlay()
        {
            if (TracksOverlay.Visibility == Visibility.Visible)
            {
                HideTracksOverlayAnim.Begin();
                await Task.Delay(200);
                TracksOverlay.Visibility = Visibility.Collapsed;
            }
        }

        // ---------- SPEED PICKER LOGIC ----------
        private bool _ignoreSpeedApply = false; // Prevents applying speed during setup




        private async void SpeedMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_blockNextClick)
            {
                _blockNextClick = false;
                return;
            }
            
            // Support both MenuFlyoutItem and Button
            string? tag = null;
            if (sender is MenuFlyoutItem mItem) tag = mItem.Tag?.ToString();
            else if (sender is Button btn) tag = btn.Tag?.ToString();

            if (_mpvPlayer == null || tag == null) return;
            
            try
            {
                string speedVal = tag ?? "1.0";
                
                // Direct Click Selection
                // Scroll to the item and apply
                await _mpvPlayer.SetPropertyAsync("speed", speedVal);
                ShowOsd($"Hız: {speedVal}x");
                
                // Find index to scroll purely for visual sync
                int index = GetSpeedIndex(speedVal);
                if (index >= 0)
                {
                    _ignoreSpeedApply = true;
                    SpeedScrollViewer.ChangeView(null, index * 36.0, null);
                    // Resume StatsTimer because we effectively "chose" 
                    _statsTimer?.Start();
                    // Reset flag after animation
                    await Task.Delay(300);
                    _ignoreSpeedApply = false;
                }
            }
            catch { _statsTimer?.Start(); }
        }

        private Point _dragStartPoint;
        private bool _isDraggingSpeedActive = false;

        private void SpeedOverlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_mpvPlayer == null || SpeedOverlay.Visibility != Visibility.Visible) return;
            
            _isDraggingSpeedActive = false;
            _dragStartPoint = e.GetCurrentPoint(SpeedOverlay).Position;
            _dragStartOffset = SpeedScrollViewer.VerticalOffset;
            ResetCursorTimer();
        }

        private void SpeedOverlay_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (SpeedOverlay.Visibility != Visibility.Visible) return;
            ResetCursorTimer();

            var ptr = e.GetCurrentPoint(SpeedOverlay);
            var currentPoint = ptr.Position;

            if (!_isDraggingSpeedActive)
            {
                // ONLY start dragging if left button is actually pressed down
                if (ptr.Properties.IsLeftButtonPressed && Math.Abs(currentPoint.Y - _dragStartPoint.Y) > 8)
                {
                    _isDraggingSpeedActive = true;
                    SpeedOverlay.CapturePointer(e.Pointer);
                    e.Handled = true;
                }
            }
            else
            {
                // If button is released but Capture was lost or something, stop here
                if (!ptr.Properties.IsLeftButtonPressed)
                {
                    SpeedOverlay_PointerReleased(sender, e);
                    return;
                }

                e.Handled = true;
                double delta = _dragStartPoint.Y - currentPoint.Y;
                double newOffset = _dragStartOffset + delta;
                
                // Throttle: Only update if offset has changed by at least 0.5 pixels
                if (Math.Abs(newOffset - _lastDragOffset) > 0.5)
                {
                    _lastDragOffset = newOffset;
                    SpeedScrollViewer.ChangeView(null, newOffset, null, true);
                }
            }
        }

        private void SpeedOverlay_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDraggingSpeedActive)
            {
                _isDraggingSpeedActive = false;
                _blockNextClick = true; // Prevents the button Click event that follows release
                SpeedOverlay.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
                
                // Trigger a final snap check manually since ViewChanged might not fire 
                // for the very last movement if it was small
                DispatcherQueue.TryEnqueue(() => { 
                    SpeedScrollViewer_ViewChanged(SpeedScrollViewer, new ScrollViewerViewChangedEventArgs());
                });
            }
        }

        private Button? GetClosestSpeedItem(out double targetOffset)
        {
            targetOffset = 0;
            if (SpeedListStack == null || SpeedScrollViewer == null) return null;

            double currentOffset = SpeedScrollViewer.VerticalOffset;
            int index = (int)Math.Round(currentOffset / 36.0);
            
            // Clamp index to valid children
            index = Math.Max(0, Math.Min(index, SpeedListStack.Children.Count - 1));
            
            targetOffset = index * 36.0;
            return SpeedListStack.Children[index] as Button;
        }

        private void SpeedScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            // Note: Visual updates are now handled by Composition API on the GPU.
            // We only need to check for snapping/application here.
            
            // Handle the end of a scroll or snap operation
            bool isEnd = e == null || !e.IsIntermediate;

            if (isEnd && !_isDraggingSpeedActive)
            {
                 var closest = GetClosestSpeedItem(out double snapOffset);
                 double currentOffset = SpeedScrollViewer.VerticalOffset;

                 if (Math.Abs(snapOffset - currentOffset) > 0.5) 
                 {
                     // Not yet aligned, start or continue snapping
                     if (!_isSnapping)
                     {
                         _isSnapping = true;
                         SpeedScrollViewer.ChangeView(null, snapOffset, null, false);
                     }
                 }
                 else
                 {
                     // Aligned at snap point
                     _isSnapping = false;
                     if (!_ignoreSpeedApply)
                     {
                         ApplyCenteredSpeed();
                     }
                 }
            }
        }

        private async void SpeedButton_Click(object sender, RoutedEventArgs e)
        {
            if (SpeedOverlay.Visibility == Visibility.Visible)
            {
                 CloseSpeedOverlay();
            }
            else
            {
                // Ensure other overlays are closed
                CloseVolumeOverlay();
                CloseTracksOverlay();

                // 1. Prepare Overlay but keep Hidden potentially? 
                //    Or just accept 1 frame glitch if Layout not ready.
                //    Better: Set Visibility, Force Layout Update or simply wait for next tick properly.
                
                SpeedOverlay.Visibility = Visibility.Visible; // Must be visible for ScrollViewer to have extent
                SpeedOverlay.Opacity = 0; // Hide visually until scrolled
                
                // Initialize Scroll Position based on current MPV speed
                if (_mpvPlayer != null)
                {
                    try 
                    {
                        var currentSpeed = await _mpvPlayer.GetPropertyAsync("speed");
                        if (string.IsNullOrEmpty(currentSpeed)) currentSpeed = "1.0";
                        
                        // Parse double
                        if (Double.TryParse(currentSpeed, NumberStyles.Any, CultureInfo.InvariantCulture, out double sVal))
                        {
                            // Map to nearest tag
                            // 2.0, 1.5, 1.25, 1.0, 0.75, 0.5
                            string targetTag = "1.0";
                            double minDiff = double.MaxValue;
                            
                            // Iterate children to find closest tag match dynamically if possible, or use hardcoded map
                            // Hardcoded map is faster for now
                            double[] speeds = { 2.0, 1.5, 1.25, 1.0, 0.75, 0.5 };
                            foreach(var s in speeds) {
                                double diff = Math.Abs(sVal - s);
                                if (diff < minDiff) { minDiff = diff; targetTag = s.ToString("0.0#", CultureInfo.InvariantCulture); }
                            }
                            
                            int index = GetSpeedIndex(targetTag);
                            _lastAppliedSpeed = targetTag;
                            _ignoreSpeedApply = true;
                            
                            // Reset optimization flags
                            _lastVisualUpdateOffset = -1;
                            _lastDragOffset = -1;
                            
                            // Suspend background timer to give 100% CPU/UI-thread to interaction
                            _statsTimer?.Stop();

                            SpeedOverlay.UpdateLayout(); // Ensure layout is ready
                            
                            // Use Dispatcher to ensure Visibility change is processed and ScrollViewer has extent
                            DispatcherQueue.TryEnqueue(async () => {
                                 await Task.Delay(50); // Small buffer for layout
                                 SpeedScrollViewer.ChangeView(null, index * 36.0, null, true);
                                 UpdateSpeedPickerVisuals(); 

                                 SpeedOverlay.Opacity = 1; 
                                 ShowSpeedOverlayAnim.Begin();

                                 await Task.Delay(150); 
                                 _ignoreSpeedApply = false;
                            });
                        }
                        else
                        {
                            // Fallback if parse fails
                             SpeedOverlay.Opacity = 1; 
                             ShowSpeedOverlayAnim.Begin();
                        }
                    } 
                    catch 
                    {
                         SpeedOverlay.Opacity = 1; 
                         ShowSpeedOverlayAnim.Begin();
                    }
                }
                else
                {
                     SpeedOverlay.Opacity = 1; 
                     ShowSpeedOverlayAnim.Begin();
                }
            }
        }

        private void SpeedListStack_Loaded(object sender, RoutedEventArgs e)
        {
            if (SpeedListStack == null || SpeedScrollViewer == null) return;

            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            var scrollerPropertySet = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(SpeedScrollViewer);
            
            // Distance formula: abs(buttonCenterY + scrollTranslation.Y - viewportCenter)
            // Scroll translation Y is negative in WinUI 3 manipulation property set
            
            double currentY = 57; // Padding top
            foreach (var child in SpeedListStack.Children)
            {
                if (child is Button btn)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(btn);
                    // Use fixed sizes based on XAML for better performance 
                    visual.Size = new Vector2(80, 36);
                    visual.CenterPoint = new Vector3(40, 18, 0);

                    double btnCenterY = currentY + 18;
                    
                    // Scale Animation
                    var scaleExp = compositor.CreateExpressionAnimation(
                        "1.1 - clamp((abs(btnCenterY + scroller.Translation.Y - 75) - 18) / 36, 0, 1) * 0.25");
                    scaleExp.SetScalarParameter("btnCenterY", (float)btnCenterY);
                    scaleExp.SetReferenceParameter("scroller", scrollerPropertySet);
                    visual.StartAnimation("Scale.X", scaleExp);
                    visual.StartAnimation("Scale.Y", scaleExp);

                    // Opacity Animation
                    var opacityExp = compositor.CreateExpressionAnimation(
                        "1.0 - clamp((abs(btnCenterY + scroller.Translation.Y - 75) - 18) / 36, 0, 1) * 0.75");
                    opacityExp.SetScalarParameter("btnCenterY", (float)btnCenterY);
                    opacityExp.SetReferenceParameter("scroller", scrollerPropertySet);
                    visual.StartAnimation("Opacity", opacityExp);

                    currentY += btn.ActualHeight;
                }
            }
        }

        private void UpdateSpeedPickerVisuals(bool force = false)
        {
            // Composition API now handles Scale and Opacity on the GPU.
            // This method is kept for any UI-thread specific updates if needed in future (e.g. FontWeight)
        }

        private int GetSpeedIndex(string speed)
        {
             // Robust search
             int i = 0;
             foreach(var child in SpeedListStack.Children)
             {
                  if (child is Button btn) 
                  {
                      string? t = btn.Tag?.ToString();
                      if (t == speed) return i;
                     
                     // Try loose match (e.g. "1" matches "1.0")
                     if (double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out double tVal) &&
                         double.TryParse(speed, NumberStyles.Any, CultureInfo.InvariantCulture, out double sVal))
                     {
                         if (Math.Abs(tVal - sVal) < 0.01) return i;
                     }
                 }
                 i++;
             }
             return 3; // Default to 1.0 (Index 3)
        }


        private async void ApplyCenteredSpeed()
        {
             // Apply only if valid
             if (SpeedListStack == null || _mpvPlayer == null || _ignoreSpeedApply) return;
             
             var closestBtn = GetClosestSpeedItem(out _);

             if (closestBtn != null && closestBtn.Tag != null)
             {
                 string speedVal = closestBtn.Tag.ToString() ?? "1.0";
                if (speedVal != _lastAppliedSpeed)
                {
                    _lastAppliedSpeed = speedVal;
                    await _mpvPlayer.SetPropertyAsync("speed", speedVal);
                    ShowOsd($"Hız: {speedVal}x");
                }
             }
        }






        private string[] _aspectRatios = { "-1", "16:9", "4:3", "2.35:1", "fill" };
        private string[] _aspectNames = { "Otomatik", "16:9", "4:3", "Sinema", "Ekranı Kapla" };
        private int _currentAspectIndex = 0;

        private async void AspectRatioButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mpvPlayer == null) return;

            _currentAspectIndex = (_currentAspectIndex + 1) % _aspectRatios.Length;
            string newAspect = _aspectRatios[_currentAspectIndex];
            string aspectName = _aspectNames[_currentAspectIndex];

            if (newAspect == "fill")
            {
                 // Stretch to fill (No crop, no black bars, distorts image)
                 await _mpvPlayer.SetPropertyAsync("keepaspect", "no");
            }
            else
            {
                 // Standard Aspect Ratio override
                 await _mpvPlayer.SetPropertyAsync("keepaspect", "yes");
                 await _mpvPlayer.SetPropertyAsync("panscan", "0.0"); 
                 await _mpvPlayer.SetPropertyAsync("video-aspect-override", newAspect);
            }
            
            ShowOsd($"Görüntü: {aspectName}");
        }

        // ---------- VOLUME CONTROL ----------
        private async void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_mpvPlayer == null) return;

            double val = e.NewValue;


            // Auto-unmute if dragging
            await _mpvPlayer.SetPropertyAsync("mute", "no");
            await _mpvPlayer.SetPropertyAsync("volume", val.ToString(CultureInfo.InvariantCulture));

            UpdateVolumeIcon(val, false);
        }

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mpvPlayer == null) return;

            // Simple toggle logic
            // We assume Current State based on Icon for speed, or fetch property
            // Fetching property is safer.
            string muteState = await _mpvPlayer.GetPropertyAsync("mute");
            bool isMuted = (muteState == "yes");

            if (isMuted)
            {
                // Unmute
                await _mpvPlayer.SetPropertyAsync("mute", "no");
                UpdateVolumeIcon(VolumeSlider.Value, false);
            }
            else
            {
                // Mute
                await _mpvPlayer.SetPropertyAsync("mute", "yes");
                UpdateVolumeIcon(VolumeSlider.Value, true);
            }
        }

        private void UpdateVolumeIcon(double volume, bool isMuted)
        {
            // Main Button: Status Indicator (What is the current state?)
            if (isMuted || volume == 0)
            {
                VolumeIcon.Glyph = "\uE74F"; // Mute Cross
            }
            else if (volume < 33)
            {
                VolumeIcon.Glyph = "\uE993"; // Volume1 (Thin waves)
            }
            else if (volume < 66)
            {
                VolumeIcon.Glyph = "\uE994"; // Volume2 (Semi-waves)
            }
            else
            {
                VolumeIcon.Glyph = "\uE995"; // Volume3 (Full waves)
            }

            // Overlay Mute Button: Action Indicator (What will happen when clicked?)
            // If Muted -> Show Speaker (Unmute)
            // If Sound -> Show Cross (Mute)
            if (isMuted || volume == 0)
            {
                 MuteIcon.Glyph = "\uE767"; // Volume Icon (Speaker) -> Unmute
                 if (PipMuteIcon != null) PipMuteIcon.Glyph = "\uE767";
            }
            else
            {
                 MuteIcon.Glyph = "\uE74F"; // Mute Icon (Cross) -> Mute
                 if (PipMuteIcon != null) PipMuteIcon.Glyph = "\uE74F";
            }
        }



        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (VolumeOverlay.Visibility == Visibility.Visible)
            {
                CloseVolumeOverlay();
            }
            else
            {
                // Ensure other overlays are closed
                CloseTracksOverlay();
                CloseSpeedOverlay();

                VolumeOverlay.Visibility = Visibility.Visible;
                ShowVolumeOverlayAnim.Begin();
            }
        }
        // ------------------------------------

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mpvPlayer == null) return;
            try
            {
                // Toggle Pause
                bool isPaused = await _mpvPlayer.GetPropertyBoolAsync("pause");
                bool newState = !isPaused;
                await _mpvPlayer.SetPropertyAsync("pause", newState ? "yes" : "no");
                
                // Update Icon: If paused(true) -> Show Play Icon, If playing(false) -> Show Pause Icon
                PlayPauseIcon.Glyph = newState ? "\uF5B0" : "\uF8AE"; // PlaySolid / PauseSolid
                if (PipPlayPauseIcon != null) PipPlayPauseIcon.Glyph = newState ? "\uF5B0" : "\uF8AE";
            }
            catch { }
        }

        // ---------- GESTURE & POINTER LOGIC ----------
        private Point _pointerDownPosition;
        private bool _isPointerDragging;
        private double _originalVolume;
        private double _originalBrightness;
        private long _lastDoubleTapTime = 0;
        private const int DoubleTapThreshold = 300; // ms

        // Brightness: simulated via overlay opacity (0.0 to 0.7)
        // 0.0 = 100% brightness (Transparent overlay)
        // 0.7 = 30% brightness (Dark overlay)
        private double _currentBrightness = 1.0; 

        // Flag to tracking if the current pointer sequence belongs to a gesture
        private bool _isGestureTarget = false;

        private void MainGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(MainGrid).Position;
            _pointerDownPosition = pos;
            _isPointerDragging = false;
            _isGestureTarget = true;

            // 1. Exclude Bottom Control Bar Area (Height ~110)
            // This prevents gestures when using buttons/sliders at the bottom
            if (pos.Y > MainGrid.ActualHeight - 110)
            {
                _isGestureTarget = false;
                return;
            }

            // 2. Filter out interactive controls (Robust Name & Type check)
            if (e.OriginalSource is FrameworkElement fe)
            {
                var parent = fe;
                while (parent != null && parent != MainGrid) 
                {
                    // Check by Type
                    if (parent is Button || parent is Slider || parent is Microsoft.UI.Xaml.Controls.Primitives.Thumb || parent is ToggleSwitch)
                    {
                        _isGestureTarget = false;
                        return;
                    }
                    
                    // Check by Name (Extra safety for specific controls)
                    if (parent.Name == "VolumeSlider" || parent.Name == "SeekSlider" || parent.Name == "VolumeOverlay" || 
                        parent.Name == "TracksOverlay" || parent.Name == "SpeedOverlay" || parent.Name == "FullScreenButton" || 
                        parent.Name == "PlayPauseButton" || parent.Name == "StartButton" || parent.Name == "RewindButton" || parent.Name == "FastForwardButton")
                    {
                         _isGestureTarget = false;
                         return;
                    }

                    parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent) as FrameworkElement;
                }
            }

            // 3. Click Outside Logic - Close Overlays if clicking on empty space
            bool overlayWasOpen = false;
            if (VolumeOverlay.Visibility == Visibility.Visible) { CloseVolumeOverlay(); overlayWasOpen = true; }
            if (TracksOverlay.Visibility == Visibility.Visible) { CloseTracksOverlay(); overlayWasOpen = true; }
            if (SpeedOverlay.Visibility == Visibility.Visible) { CloseSpeedOverlay(); overlayWasOpen = true; }
            
            if (overlayWasOpen)
            {
                 _isGestureTarget = false; // Don't start a gesture if we just closed a menu
                 return;
            }
            
            // Capture initial values for drag delta
            if (_mpvPlayer != null && _isGestureTarget)
            {
                 // Volume is 0-100
                 _originalVolume = VolumeSlider.Value;
                 // Brightness is internal
                 _originalBrightness = _currentBrightness;
            }
        }

        private void MainGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // [FIX] Prevent gesture conflict: If we are scrubbing the seekbar, IGNORE any main grid gestures!
            if (_isDragging) return;

            // ALWAYS reset timer on any movement to show controls
            ResetCursorTimer();

            if (!e.Pointer.IsInContact || !_isGestureTarget) return;

            var currentPoint = e.GetCurrentPoint(MainGrid).Position;
            
            var deltaY = _pointerDownPosition.Y - currentPoint.Y; // Up is positive
            
            if (Math.Abs(deltaY) > 20)
            {
                _isPointerDragging = true;
                double scaledDelta = deltaY / 5.0; // Sensitivity factor

                // Determine Zone: Left (Brightness) vs Right (Volume)
                bool isRightSide = _pointerDownPosition.X > MainGrid.ActualWidth / 2;

                if (isRightSide)
                {
                    // Update Volume
                    double newVol = Math.Clamp(_originalVolume + scaledDelta, 0, 100);
                    _mpvPlayer?.SetPropertyAsync("volume", newVol.ToString(CultureInfo.InvariantCulture));
                    VolumeSlider.Value = newVol; // Update UI logic
                    ShowOsd($"SES: {(int)newVol}");
                }
                else
                {
                    // Update Brightness (0.0 to 1.0)
                    // Drag Up (+delta) -> Increase Brightness (Decrease Opacity)
                    // scaledDelta is roughly -50 to +50
                    double change = scaledDelta / 100.0; 
                    double newBright = Math.Clamp(_originalBrightness + change, 0.1, 1.0);
                    
                    SetBrightness(newBright);
                    ShowOsd($"PARLAKLIK: {(int)(newBright * 100)}");
                }
            }
        }

        private void SetBrightness(double brightness)
        {
            _currentBrightness = brightness;
            // brightness 1.0 -> Opacity 0.0
            // brightness 0.1 -> Opacity 0.7 (max darkness)
            double maxDarkness = 0.7;
            double opacity = (1.0 - brightness) * (maxDarkness / 0.9);
            BrightnessOverlay.Opacity = Math.Clamp(opacity, 0, maxDarkness);
        }

        private void MainGrid_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
             _isPointerDragging = false;
             ResetCursorTimer();
        }

        private void MainGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            HideControls();
        }

        // ---------- CURSOR & CONTROLS AUTO-HIDE LOGIC ----------





        private void StartCursorTimer()
        {
            ResetCursorTimer();
        }

        private void StopCursorTimer()
        {
            _cursorTimer?.Stop();
            SetControlsVisibility(Visibility.Visible);
        }

        private void ResetCursorTimer()
        {
            if (_cursorTimer == null) return;
            _cursorTimer.Stop();
            _cursorTimer.Start();
            
            if (_controlsHidden)
            {
                SetControlsVisibility(Visibility.Visible);
            }
        }

        private void CursorTimer_Tick(object? sender, object e)
        {
             // [FIX] Don't hide controls if user is interacting with the seekbar or if a panel is open!
             if (_isDragging) return;
             if (TracksOverlay.Visibility == Visibility.Visible || 
                 VolumeOverlay.Visibility == Visibility.Visible || 
                 SpeedOverlay.Visibility == Visibility.Visible)
             {
                 return;
             }

             HideControls();
        }

        private void HideControls()
        {
            if (!_controlsHidden)
            {
                SetControlsVisibility(Visibility.Collapsed);
            }
        }

        private void SetControlsVisibility(Visibility visibility)
        {
            if (ControlsBorder == null || BackButton == null) return;

            if (ControlsBorder.Visibility != visibility)
            {
                ControlsBorder.Visibility = visibility;
                BackButton.Visibility = visibility;
                VideoTitleText.Visibility = visibility;
                FullScreenButton.Visibility = visibility;

                if (visibility == Visibility.Visible)
                {
                    if (InfoPillsStack.Visibility != Visibility.Visible)
                    {
                        InfoPillsStack.Visibility = Visibility.Visible;
                        ShowInfoPillsAnim.Begin();
                    }
                }

                if (visibility == Visibility.Collapsed)
                {
                    CloseVolumeOverlay();
                    CloseTracksOverlay();
                    CloseSpeedOverlay();
                    if (StatsOverlay.Visibility == Visibility.Visible)
                    {
                         // Keep stats overlay visible
                    }
                    if (InfoPillsStack.Visibility == Visibility.Visible)
                    {
                         InfoPillsStack.Visibility = Visibility.Collapsed;
                    }
                }

                _controlsHidden = (visibility == Visibility.Collapsed);
            }
        }



        private string GetShortCodecName(string codec)
        {
            if (string.IsNullOrEmpty(codec) || codec == "N/A" || codec == "-") return "-";
            codec = codec.ToLower().Trim();

            if (codec.Contains("h264") || codec.Contains("avc")) return "H.264";
            if (codec.Contains("h265") || codec.Contains("hevc")) return "HEVC";
            if (codec.Contains("vp9")) return "VP9";
            if (codec.Contains("av1")) return "AV1";
            if (codec.Contains("mpeg2")) return "MPEG2";
            if (codec.Contains("mpeg4")) return "MPEG4";
            if (codec.Contains("aac")) return "AAC";
            if (codec.Contains("mp3")) return "MP3";
            
            // Return uppercase if no match found but it's short, else truncate
            if (codec.Length > 8) return codec.Substring(0, 8).ToUpper() + "..";
            return codec.ToUpper();
        }

        private void MainGrid_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(MainGrid).Properties;
            int delta = props.MouseWheelDelta;
            var pos = e.GetCurrentPoint(MainGrid).Position;
            
            bool isRightSide = pos.X > MainGrid.ActualWidth / 2;

            if (isRightSide)
            {
                // Volume
                double currentVol = VolumeSlider.Value;
                double change = delta > 0 ? 5 : -5;
                double newVol = Math.Clamp(currentVol + change, 0, 100);
                 VolumeSlider.Value = newVol; // Triggers property update
                 ShowOsd($"SES: {(int)newVol}");
            }
            else
            {
                // Brightness
                 double change = delta > 0 ? 0.05 : -0.05;
                 double newBright = Math.Clamp(_currentBrightness + change, 0.1, 1.0);
                 SetBrightness(newBright);
                 ShowOsd($"PARLAKLIK: {(int)(newBright * 100)}");
            }
        }

        // ---------- DOUBLE TAP LOGIC ----------
        // ---------- DOUBLE TAP LOGIC ----------
        private async void Page_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            if (_isPiPMode)
            {
                TogglePiPAsync();
                return;
            }

            // [FIX] Ignore double taps if they originated from a control/button or if we were dragging
            if (_isPointerDragging) return;

            if (e.OriginalSource is FrameworkElement fe)
            {
               // Walk up to check for interactive parents
               var parent = fe;
               while (parent != null && parent != MainGrid)
               {
                   if (parent is Button || parent is Slider || parent is ToggleSwitch) return;
                   parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent) as FrameworkElement;
               }
            }

            var pos = e.GetPosition(MainGrid);
            double width = MainGrid.ActualWidth;
            
            // Zones:
            // 0 - 30% : Left (Rewind)
            // 30 - 70% : Center (Play/Pause or Fullscreen - User preference, defaulting to Fullscreen toggle here)
            // 70 - 100%: Right (Seek Forward)

            if (pos.X < width * 0.30)
            {
                // Seek Back 10s
                if (!RewindButton.IsEnabled || _mpvPlayer == null) return; // Not seekable
                 ShowOsd("-10 SN");
                 await _mpvPlayer.ExecuteCommandAsync("seek", "-10", "relative");
            }
            else if (pos.X > width * 0.70)
            {
                // Seek Fwd 30s
                if (!FastForwardButton.IsEnabled || _mpvPlayer == null) return;
                 ShowOsd("+30 SN");
                 await _mpvPlayer.ExecuteCommandAsync("seek", "30", "relative");
            }
            else
            {
                // Center -> Toggle Fullscreen
                 ToggleFullScreen();
            }
        }

        // ---------- ANIMATION HELPERS ----------
        private void AnimateButtonPress(Button btn)
        {
            // Simple scale animation could go here, 
            // but VisualStates are improved in XAML default styles usually.
            // For now, relies on standard button feedback.
        }

        // ---------- TRACKS TOGGLE LOGIC ----------


        private System.Threading.CancellationTokenSource? _osdCts;
        private void ShowOsd(string text)
        {
            _osdCts?.Cancel();
            _osdCts = new System.Threading.CancellationTokenSource();
            
            OsdText.Text = text;
            OsdOverlay.Visibility = Visibility.Visible;
            
            var token = _osdCts.Token;
            Task.Delay(2000, token).ContinueWith(t => 
            {
                 if (!t.IsCanceled)
                 {
                     DispatcherQueue.TryEnqueue(() => 
                     {
                         OsdOverlay.Visibility = Visibility.Collapsed;

                     });
                 }
            });
        }

        private void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (StatsOverlay.Visibility == Visibility.Collapsed)
            {
                StatsOverlay.Visibility = Visibility.Visible;
                // Force an immediate fetch of static metadata if not already done
                _isStaticMetadataFetched = false; 
                _isStaticMetadataFetched = false; 
                StatsTimer_Tick(this, new object());
            }
            else
            {
                StatsOverlay.Visibility = Visibility.Collapsed;
            }
        }

        // ---------- UNIFIED TRACKS OVERLAY LOGIC ----------

        private class TrackItem
        {
            public long Id { get; set; }
            public string Text { get; set; } = "";
            public bool IsSelected { get; set; }
            public string Type { get; set; } = "";
            public bool IsNone { get; set; }

            public override string ToString() => Text; // Fallback
        }

        private bool _isPopulatingTracks = false;





        private async Task LoadTracksAsync()
        {
            if (_mpvPlayer == null) return;
            
            _isPopulatingTracks = true;
            System.Diagnostics.Debug.WriteLine("[LoadTracksAsync] Started (Safe Loop Mode).");

            try
            {
                // 1. Get Track Count
                string sCount = await _mpvPlayer.GetPropertyAsync("track-list/count");
                if (!int.TryParse(sCount, out int trackCount) || trackCount <= 0)
                {
                    System.Diagnostics.Debug.WriteLine("[LoadTracksAsync] No tracks found.");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[LoadTracksAsync] Count: {trackCount}");

                var audioTracks = new List<TrackItem>();
                var newSubTracks = new List<SubtitleTrackViewModel>();
                // _currentSubtitleTracks will be updated at the end, not cleared here

                // 2. Iterate with Safety Delay
                for (int i = 0; i < trackCount; i++)
                {
                    // CRITICAL: Throttle calls to prevent Heap Corruption (0xC0000374)
                    await Task.Delay(15); 

                    string type = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/type");
                    
                    if (type == "audio")
                    {
                        string sId = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/id");
                        int.TryParse(sId, out int id);
                        
                        bool selected = await _mpvPlayer.GetPropertyBoolAsync($"track-list/{i}/selected");
                        string title = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/title");
                        string lang = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/lang");
                        string codec = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/codec-name");
                        string channels = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/audio-channels");

                        if (title == "N/A") title = "";
                        if (lang == "N/A") lang = "";

                        string details = "";
                        if (!string.IsNullOrEmpty(codec) && codec != "N/A") details += codec.ToUpper();
                        if (!string.IsNullOrEmpty(channels) && channels != "N/A") details += $" {channels}ch";

                        string langStr = !string.IsNullOrEmpty(lang) ? lang.ToUpper() : $"Track {id}";
                        string titleStr = !string.IsNullOrEmpty(title) ? title : "";

                        string displayText = $"{langStr} {titleStr}".Trim();
                        if (!string.IsNullOrEmpty(details)) displayText += $" ({details.Trim()})";

                        audioTracks.Add(new TrackItem 
                        { 
                            Id = id, 
                            Text = displayText, 
                            IsSelected = selected, 
                            Type = "audio" 
                        });
                    }
                    else if (type == "sub")
                    {
                        string sId = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/id");
                        int.TryParse(sId, out int id);
                        
                        string title = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/title");
                        string lang = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/lang");

                        if (title == "N/A") title = "";
                        if (lang == "N/A") lang = "";

                        newSubTracks.Add(new SubtitleTrackViewModel 
                        { 
                            Id = id, 
                            Text = string.IsNullOrEmpty(title) ? $"Track {id}" : title,
                            Lang = !string.IsNullOrEmpty(lang) ? lang : "und",
                            IsAddon = false,
                            IsSelected = await _mpvPlayer.GetPropertyBoolAsync($"track-list/{i}/selected")
                        });
                    }
                }

                // Update Audio UI
                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync] Updating Audio UI...");
                AudioListView.ItemsSource = audioTracks;
                var selectedAudio = audioTracks.FirstOrDefault(t => t.IsSelected);
                if (selectedAudio != null) AudioListView.SelectedItem = selectedAudio;

                // Update Subtitle UI (Merge new tracks, keep addons)
                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync] Updating Subtitle UI...");

                // Preserve addons
                var existingAddons = _currentSubtitleTracks.Where(t => t.IsAddon).ToList();
                _currentSubtitleTracks = newSubTracks; 
                _currentSubtitleTracks.AddRange(existingAddons);

                UpdateLanguageList(isLoading: true);
                _ = FetchAddonSubtitles();
                
                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync] Completed Successfully.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadTracksAsync] CRITICAL ERROR: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[LoadTracksAsync] StackTrace: {ex.StackTrace}");
            }
            finally
            {
                _isPopulatingTracks = false;
            }
        }

        private async void AudioListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isPopulatingTracks || _mpvPlayer == null || AudioListView.SelectedItem is not TrackItem item) return;
            
            // Update models
            if (AudioListView.ItemsSource is IEnumerable<TrackItem> tracks)
            {
                foreach (var t in tracks) t.IsSelected = (t == item);
            }

            await _mpvPlayer.SetPropertyAsync("aid", item.Id.ToString());
            ShowOsd($"Ses: {item.Text}");
        }

        private void CloseTracksButton_Click(object sender, RoutedEventArgs e)
        {
            HideTracksOverlayAnim.Begin();
            HideTracksOverlayAnim.Completed += (s, args) =>
            {
                TracksOverlay.Visibility = Visibility.Collapsed;
            };
        }

        private async void TracksButton_Click(object sender, RoutedEventArgs e)
        {
             if (TracksOverlay.Visibility == Visibility.Visible)
             {
                 HideTracksOverlayAnim.Begin();
                 await Task.Delay(200);
                 TracksOverlay.Visibility = Visibility.Collapsed;
             }
             else
             {
                 // Close others
                 CloseVolumeOverlay();
                 CloseSpeedOverlay();
                 
                 TracksOverlay.Visibility = Visibility.Visible;
                 ShowTracksOverlayAnim.Begin();
                 
                 if (!_isPopulatingTracks && (AudioListView.Items.Count == 0 || _currentSubtitleTracks.Count == 0))
                 {
                     await LoadTracksAsync();
                 }
                 else if (!_isPopulatingTracks)
                 {
                     // Just refresh selection state without full reload
                     await RefreshTrackSelection();
                 }
             }
        }

        private async void SyncButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mpvPlayer == null) return;
            
            HideTracksOverlayAnim.Begin();
            await Task.Delay(200);
            TracksOverlay.Visibility = Visibility.Collapsed;

            UnifiedDelayOverlay.Visibility = Visibility.Visible;
            
            var aDelay = await _mpvPlayer.GetPropertyAsync("audio-delay");
            var sDelay = await _mpvPlayer.GetPropertyAsync("sub-delay");

            if (double.TryParse(aDelay, NumberStyles.Any, CultureInfo.InvariantCulture, out double ad)) _audioDelayMs = ad * 1000;
            if (double.TryParse(sDelay, NumberStyles.Any, CultureInfo.InvariantCulture, out double sd)) _subDelayMs = sd * 1000;

            UpdateDelayUI();
        }

        private void CloseDelayOverlay_Click(object sender, RoutedEventArgs e)
        {
            UnifiedDelayOverlay.Visibility = Visibility.Collapsed;
        }

        private void DelayTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                _isAudioDelayMode = tag == "Audio";
                UpdateDelayUI();
            }
        }

        private void UpdateDelayUI()
        {
             DelayValueText.Text = _isAudioDelayMode ? $"{_audioDelayMs} ms" : $"{_subDelayMs} ms";
             if (_isAudioDelayMode)
             {
                 DelayTabAudio.Opacity = 1.0;
                 DelayTabSub.Opacity = 0.5;
             }
             else
             {
                 DelayTabAudio.Opacity = 0.5;
                 DelayTabSub.Opacity = 1.0;
             }
        }

        private async void DelayMinus_Click(object sender, RoutedEventArgs e)
        {
            await AdjustDelay(-50);
        }

        private async void DelayPlus_Click(object sender, RoutedEventArgs e)
        {
            await AdjustDelay(50);
        }

        private async Task AdjustDelay(double deltaMs)
        {
            if (_mpvPlayer == null) return;

            if (_isAudioDelayMode)
            {
                _audioDelayMs += deltaMs;
                await _mpvPlayer.SetPropertyAsync("audio-delay", (_audioDelayMs / 1000.0).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                _subDelayMs += deltaMs;
                await _mpvPlayer.SetPropertyAsync("sub-delay", (_subDelayMs / 1000.0).ToString(CultureInfo.InvariantCulture));
            }
            UpdateDelayUI();
        }

        private async void AddExternalSub_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var window = MainWindow.Current;
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);

            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.FileTypeFilter.Add(".srt");
            picker.FileTypeFilter.Add(".vtt");
            picker.FileTypeFilter.Add(".ass");
            picker.FileTypeFilter.Add(".ssa");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await _mpvPlayer.ExecuteCommandAsync("sub-add", file.Path);
                ShowOsd("Altyazı Eklendi");
            }
        }

        private void SubtitleLangListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SubtitleLangListView.SelectedItem is SubtitleLanguageViewModel lang && !lang.IsLoadingItem)
            {
                bool oldState = _isPopulatingTracks;
                _isPopulatingTracks = true; // Guard the cascade selection
                
                try
                {
                    var filtered = _currentSubtitleTracks.Where(t => 
                        t.Lang.Equals(lang.Code, StringComparison.OrdinalIgnoreCase) || 
                        (lang.Code == "und" && string.IsNullOrEmpty(t.Lang))
                    ).ToList();
                    SubtitleListView.ItemsSource = filtered;
                    
                    // Restore active subtitle track selection
                    var selectedTrack = filtered.FirstOrDefault(t => t.IsSelected);
                    if (selectedTrack != null)
                    {
                        SubtitleListView.SelectedItem = selectedTrack;
                        SubtitleListView.ScrollIntoView(selectedTrack);
                    }
                }
                finally
                {
                    _isPopulatingTracks = oldState;
                }
            }
        }



        private void UpdateLanguageList(bool isLoading)
        {
            bool oldState = _isPopulatingTracks;
            _isPopulatingTracks = true; // Prevent triggering MPV commands during UI rebuild

            try
            {
                // Save current selection to restore it after refresh
                var currentSelection = SubtitleLangListView.SelectedItem as SubtitleLanguageViewModel;

                var grouped = _currentSubtitleTracks
                    .GroupBy(t => t.Lang)
                    .Select(g => new SubtitleLanguageViewModel 
                    { 
                        Code = g.Key, 
                        Name = ModernIPTVPlayer.Helpers.LanguageHelpers.GetDisplayName(g.Key),
                        Count = g.Count(),
                        IsLoadingItem = false
                    })
                    .OrderBy(l => l.Name)
                    .ToList();

                if (isLoading)
                {
                    grouped.Add(new SubtitleLanguageViewModel { Name = "Loading...", IsLoadingItem = true });
                }

                SubtitleLangListView.ItemsSource = grouped;
                
                // 1. Try to restore previous user selection
                if (currentSelection != null)
                {
                    var restore = grouped.FirstOrDefault(l => l.Code == currentSelection.Code);
                    if (restore != null)
                    {
                        SubtitleLangListView.SelectedItem = restore;
                        return;
                    }
                }

                // 2. If nothing selected or restore failed, find the language of the active track
                var activeSub = _currentSubtitleTracks.FirstOrDefault(t => t.IsSelected);
                if (activeSub != null)
                {
                    var targetLang = grouped.FirstOrDefault(l => l.Code == activeSub.Lang);
                    if (targetLang != null)
                    {
                        SubtitleLangListView.SelectedItem = targetLang;
                    }
                }
                else if (SubtitleLangListView.SelectedItem == null && grouped.Count > 0)
                {
                    // Fallback to first language only if absolutely nothing is selected
                    SubtitleLangListView.SelectedIndex = 0;
                }
            }
            finally
            {
                _isPopulatingTracks = oldState;
            }
        }

        private async Task FetchAddonSubtitles()
        {
            try
            {
                // 1. Identify Content
                string imdbId = null;
                string type = "movie";
                string extra = "";
                
                // Use _navArgs for all metadata since _item is not available in PlayerPage
                if (_navArgs != null && !string.IsNullOrEmpty(_navArgs.Id))
                {
                    imdbId = _navArgs.Id;
                    // Guess type based on season presence
                    type = _navArgs.Season > 0 ? "series" : "movie";
                }

                if (string.IsNullOrEmpty(imdbId))
                {
                    System.Diagnostics.Debug.WriteLine("[FetchAddonSubtitles] No ID found. Skipping addon subtitles.");
                    DispatcherQueue.TryEnqueue(() => UpdateLanguageList(isLoading: false));
                    return;
                }

                // Format ID for series: tt1234567:1:5
                string queryId = imdbId;
                if (type == "series" || type == "episode")
                {
                    int s = 1, e = 1;
                    if (_navArgs != null && _navArgs.Season > 0) 
                    { 
                        s = _navArgs.Season; 
                        e = _navArgs.Episode; 
                    }
                    
                    queryId = $"{imdbId}:{s}:{e}";
                    type = "series"; // Ensure type is series for subtitle query
                }

                // ID looks like "tt1234567" or "tt1234567:1:5"
                string currentFetchKey = queryId;

                // 2. Check Cache
                if (_lastAddonFetchId == currentFetchKey && _cachedAddonSubtitles.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[FetchAddonSubtitles] Using CACHED subtitles for {currentFetchKey}");
                    DispatcherQueue.TryEnqueue(() => 
                    {
                        InjectAddonSubsIntoList(_cachedAddonSubtitles);
                        UpdateLanguageList(isLoading: false);
                    });
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[FetchAddonSubtitles] Cache miss or new content. Querying for: {type} / {queryId}");

                // 3. Get Addons
                var addons = StremioAddonManager.Instance.GetAddonsWithManifests()
                    .Where(a => a.Manifest != null && 
                                a.Manifest.Resources != null &&
                                a.Manifest.Resources.Any(r => r.ToString().Contains("subtitles")))
                    .ToList();

                if (!addons.Any())
                {
                    System.Diagnostics.Debug.WriteLine("[FetchAddonSubtitles] No subtitle addons found.");
                    DispatcherQueue.TryEnqueue(() => UpdateLanguageList(isLoading: false));
                    return;
                }

                // 4. Fetch Concurrently (Capturing Addon Names)
                var fetchTasks = addons.Select(async a => 
                {
                    var subs = await StremioService.Instance.GetSubtitlesAsync(a.BaseUrl, type, queryId, extra);
                    return subs.Select(s => new SubtitleTrackViewModel
                    {
                        Id = -1,
                        Text = $"Addon: {s.Lang ?? "Unknown"} ({s.Id ?? "Ext"})",
                        Lang = s.Lang ?? "und",
                        IsAddon = true,
                        Url = s.Url,
                        AddonName = a.Manifest?.Name?.ToUpper() ?? "ADDON"
                    }).ToList();
                });

                var results = await Task.WhenAll(fetchTasks);
                var allNewAddonSubs = results.SelectMany(x => x).ToList();

                System.Diagnostics.Debug.WriteLine($"[FetchAddonSubtitles] Found {allNewAddonSubs.Count} subtitles.");

                _cachedAddonSubtitles = allNewAddonSubs;
                _lastAddonFetchId = currentFetchKey;

                // 2. Update UI
                DispatcherQueue.TryEnqueue(() => 
                {
                    InjectAddonSubsIntoList(allNewAddonSubs);
                    UpdateLanguageList(isLoading: false);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FetchAddonSubtitles] Error: {ex.Message}");
                DispatcherQueue.TryEnqueue(() => UpdateLanguageList(isLoading: false));
            }
        }

        /// <summary>
        /// Called after player init (Handoff or Fresh Start). 
        /// Waits for MPV to stabilize, loads tracks, fetches addon subtitles, 
        /// and directly applies the saved subtitle via MPV command.
        /// </summary>
        private async Task AutoFetchAndRestoreAddonSubtitleAsync()
        {
            try
            {
                if (_navArgs == null || _mpvPlayer == null) return;

                // 1. Wait for MPV to fully load the video and have tracks available
                Debug.WriteLine("[AutoSubRestore] Waiting for player to stabilize...");
                await Task.Delay(3000);
                if (_mpvPlayer == null || !_isPageLoaded) return;

                // 2. Load the track list (populates _currentSubtitleTracks with embedded tracks)
                Debug.WriteLine("[AutoSubRestore] Loading tracks...");
                await LoadTracksAsync();
                if (_mpvPlayer == null || !_isPageLoaded) return;

                // 3. FetchAddonSubtitles is already triggered by LoadTracksAsync (line 2154: _ = FetchAddonSubtitles())
                //    But it's fire-and-forget inside LoadTracksAsync, so we need to wait for it.
                //    Call it again (cache will be used if it already ran).
                Debug.WriteLine("[AutoSubRestore] Fetching addon subtitles...");
                await FetchAddonSubtitles();
                if (_mpvPlayer == null || !_isPageLoaded) return;

                // 4. InjectAddonSubsIntoList already handles finding the saved URL and calling sub-add.
                //    It was called by FetchAddonSubtitles -> InjectAddonSubsIntoList.
                //    So by this point, the subtitle should already be applied.
                Debug.WriteLine("[AutoSubRestore] Complete.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSubRestore] Error: {ex.Message}");
            }
        }

        private void InjectAddonSubsIntoList(List<SubtitleTrackViewModel> addonSubs)
        {
            var existingUrls = _currentSubtitleTracks.Select(t => t.Url).ToHashSet();
            int count = 0;
            foreach (var sub in addonSubs)
            {
                if (!string.IsNullOrEmpty(sub.Url) && !existingUrls.Contains(sub.Url))
                {
                    _currentSubtitleTracks.Add(sub);
                    count++;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[InjectAddonSubs] Added {count} subtitles to active list.");

            // Attempt to restore last used subtitle if it was an addon sub
            try
            {
                var history = HistoryManager.Instance.GetProgress(_navArgs.Id ?? _navArgs.Title);
                if (history != null && !string.IsNullOrEmpty(history.SubtitleTrackUrl))
                {
                    var match = _currentSubtitleTracks.FirstOrDefault(t => t.Url == history.SubtitleTrackUrl);
                    if (match != null)
                    {
                        // Match found in the newly fetched/existing list
                        // 1. Mark as selected in model
                        foreach (var t in _currentSubtitleTracks) t.IsSelected = (t == match);
                        match.IsSelected = true;

                        // 2. Explicitly apply to MPV (UI binding might not fire if panel is closed)
                        if (_mpvPlayer != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[InjectAddonSubs] Restoring addon subtitle: {match.Text} ({match.Url})");
                            _ = _mpvPlayer.ExecuteCommandAsync("sub-add", match.Url);
                            ShowOsd($"Altyazı Yüklendi: {match.Text}");
                        }

                        // 3. Sync UI Selection (if visible/initialized)
                        DispatcherQueue.TryEnqueue(() => 
                        {
                            SubtitleListView.SelectedItem = match;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InjectAddonSubs] Restore Error: {ex.Message}");
            }
        }

        private async Task RefreshTrackSelection()
        {
            if (_mpvPlayer == null) return;
            
            bool safeState = _isPopulatingTracks;
            _isPopulatingTracks = true;
            try
            {
                string sCount = await _mpvPlayer.GetPropertyAsync("track-list/count");
                if (int.TryParse(sCount, out int trackCount) && trackCount > 0)
                {
                     for (int i = 0; i < trackCount; i++)
                     {
                         string type = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/type");
                         if (type == "audio")
                         {
                            string sId = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/id");
                            int.TryParse(sId, out int id);
                            bool selected = await _mpvPlayer.GetPropertyBoolAsync($"track-list/{i}/selected");
                            
                            if (selected && AudioListView.ItemsSource is IEnumerable<TrackItem> items)
                            {
                                var item = items.FirstOrDefault(t => t.Id == id);
                                if (item != null && AudioListView.SelectedItem != item)
                                {
                                    // Manually update item property to reflect selection visually if needed
                                    foreach(var t in items) t.IsSelected = (t == item);
                                    AudioListView.SelectedItem = item;
                                }
                            }
                         }
                         else if (type == "sub")
                         {
                            string sId = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/id");
                            int.TryParse(sId, out int id);
                            bool selected = await _mpvPlayer.GetPropertyBoolAsync($"track-list/{i}/selected");

                            var sub = _currentSubtitleTracks.FirstOrDefault(s => s.Id == id);
                            if (sub != null) 
                            {
                                sub.IsSelected = selected;
                            }
                         }
                     }
                }
                
                // Update active sub in UI
                 DispatcherQueue.TryEnqueue(() => 
                 {
                    UpdateLanguageList(isLoading: false);
                 });
            }
            finally
            {
                _isPopulatingTracks = safeState;
            }
        }

        private async void SubtitleListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isPopulatingTracks || _mpvPlayer == null) return;
            if (SubtitleListView.SelectedItem is SubtitleTrackViewModel track)
            {
                // Update internal IsSelected state
                foreach (var t in _currentSubtitleTracks) t.IsSelected = (t == track);

                if (track.IsAddon)
                {
                    await _mpvPlayer.ExecuteCommandAsync("sub-add", track.Url);
                    ShowOsd($"Altyazı Yüklendi: {track.Text}");
                    
                    // SAVE PREFERENCE
                    double duration = _mpvPlayer.Duration.TotalSeconds; // Get duration from TimeSpan property
                    HistoryManager.Instance.UpdateProgress(
                       _navArgs.Id ?? _navArgs.Title, // Use NavArgs ID or Title
                       _navArgs.Title, 
                       _streamUrl, 
                       duration, 
                       duration,
                       subUrl: track.Url);
                }
                else
                {
                    await _mpvPlayer.SetPropertyAsync("sid", track.Id.ToString());
                    ShowOsd($"Altyazı: {track.Text}");
                    
                    // SAVE PREFERENCE (Clear URL if embedded)
                    double duration = _mpvPlayer.Duration.TotalSeconds;
                    HistoryManager.Instance.UpdateProgress(
                       _navArgs.Id ?? _navArgs.Title, 
                       _navArgs.Title, 
                       _streamUrl, 
                       duration, 
                       duration,
                       sid: track.Id.ToString(),
                       subUrl: null);
                }
            }
        }












        private async Task<(bool Success, string Url, string ErrorMsg)> CheckStreamUrlAsync(string url)
        {
            try
            {
                // Basic cleanup: some servers dislike explicit :80
                var finalUrl = url.Replace(":80/", "/");

                using var request = new HttpRequestMessage(HttpMethod.Get, finalUrl);
                // Use a small range to avoid triggering "Download" limits if possible, 
                // but some servers hate Range. Let's try standard request headers only first.
                
                var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                HttpResponseMessage response;

                try 
                {
                    // Use ResponseHeadersRead to avoid downloading the whole file
                    response = await HttpHelper.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                }
                catch (Exception ex)
                {
                    return (false, finalUrl, $"Sunucuya bağlanılamadı: {ex.Message}");
                }

                var contentType = response.Content.Headers.ContentType?.MediaType;
                
                // 1. Check for Server Errors
                if (!response.IsSuccessStatusCode)
                {
                     // Specifically handle 458 or 403
                     if ((int)response.StatusCode == 458 || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                         return (false, finalUrl, "Erişim Reddedildi veya Bağlantı Sınırı Aşıldı (458/403).");
                     
                     if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                         return (false, finalUrl, "Dosya Bulunamadı (404).");

                     return (false, finalUrl, $"Sunucu Hatası: {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                // 2. Check for "Webpage instead of Video" (The main issue)
                if (contentType != null && contentType.StartsWith("text/") && !contentType.Contains("mpegurl") && !contentType.Contains("xml"))
                {
                     return (false, finalUrl, "Yayın kaynağı geçerli bir video dosyası değil (Web Sayfası döndü). Link kırık veya süresi dolmuş olabilir.");
                }
                
                // Success
                return (true, finalUrl, string.Empty);
            }
            catch (Exception ex)
            {
                // If checking fails entirely, we might still let MPV try as a last resort, 
                // but usually it's better to fail gracefully.
                return (false, url, $"Bilinmeyen Hata: {ex.Message}");
            }
        }
        private void MultiViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mpvPlayer != null)
            {
                // 1. HANDOFF: Assign to global static
                App.HandoffPlayer = _mpvPlayer;
                
                // 2. DETACH: Remove from visual tree immediately
                PlayerContainer.Children.Remove(_mpvPlayer);
                
                // 3. NULLIFY: Prevent local Dispose in OnNavigatedFrom
                _mpvPlayer = null;
                
                // 4. NAVIGATE
                Frame.Navigate(typeof(MultiPlayerPage), _navArgs);
            }
        }
        private void SetupProfessionalAnimations()
        {
            // 1. Ghosting Trails for Rewind/FF
            SetupHighFidelityGhosting(RewindButton, RewindIconVisual, RewindGhost1, RewindGhost2, -15f);
            SetupHighFidelityGhosting(FastForwardButton, FastForwardIconVisual, FFGhost1, FFGhost2, 15f);

            // 2. Anticipation Pulse for Play/Pause
            SetupAnticipationPulse(PlayPauseButton, PlayPauseIcon);
            
            // 3. Alive System: Segmented Waves for Volume
            SetupSegmentedWaveEffect(VolumeButton, VolumeWave1, VolumeWave2);
            
            // 4. Alive System: Magnetic Glow
            SetupMagneticGlow(PlayPauseButton, PlayPauseGlow);

            // 5. Magnetic Hover for Main Buttons
            var mainButtons = new Button[] { VolumeButton, MultiViewButton, TracksButton, AspectRatioButton, InfoButton };
            foreach (var btn in mainButtons)
            {
                if (btn != null) SetupAnticipationPulse(btn, (FrameworkElement)btn.Content);
            }

            // 6. Global Organic Breathing
            ApplyOrganicBreathing(PlayPauseIcon);
            ApplyOrganicBreathing(VolumeIcon);
        }

        private void SetupHighFidelityGhosting(Button btn, FrameworkElement mainIcon, FrameworkElement ghost1, FrameworkElement ghost2, float offset)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            var mainVisual = ElementCompositionPreview.GetElementVisual(mainIcon);
            var g1Visual = ElementCompositionPreview.GetElementVisual(ghost1);
            var g2Visual = ElementCompositionPreview.GetElementVisual(ghost2);

            mainIcon.SizeChanged += (s, e) => {
                var center = new Vector3((float)mainIcon.ActualWidth / 2, (float)mainIcon.ActualHeight / 2, 0);
                mainVisual.CenterPoint = center;
                g1Visual.CenterPoint = center;
                g2Visual.CenterPoint = center;
            };

            btn.PointerEntered += (s, e) => {
                // Main Icon: Slight Overshoot
                var mainPop = compositor.CreateVector3KeyFrameAnimation();
                mainPop.InsertKeyFrame(0.5f, new Vector3(1.2f, 1.2f, 1f));
                mainPop.InsertKeyFrame(1f, new Vector3(1.1f, 1.1f, 1f));
                mainPop.Duration = TimeSpan.FromMilliseconds(400);
                mainVisual.StartAnimation("Scale", mainPop);

                // Ghost 1: Staggered Trail
                var trail1 = compositor.CreateVector3KeyFrameAnimation();
                trail1.InsertKeyFrame(0.5f, new Vector3(offset, 0, 0));
                trail1.InsertKeyFrame(1f, new Vector3(0, 0, 0));
                trail1.Duration = TimeSpan.FromMilliseconds(500);
                g1Visual.StartAnimation("Offset", trail1);
                
                var opac1 = compositor.CreateScalarKeyFrameAnimation();
                opac1.InsertKeyFrame(0.3f, 0.5f);
                opac1.InsertKeyFrame(1f, 0f);
                opac1.Duration = TimeSpan.FromMilliseconds(500);
                g1Visual.StartAnimation("Opacity", opac1);

                // Ghost 2: Deeper Stagger
                var trail2 = compositor.CreateVector3KeyFrameAnimation();
                trail2.InsertKeyFrame(0.7f, new Vector3(offset * 2, 0, 0));
                trail2.InsertKeyFrame(1f, new Vector3(0, 0, 0));
                trail2.Duration = TimeSpan.FromMilliseconds(700);
                g2Visual.StartAnimation("Offset", trail2);

                var opac2 = compositor.CreateScalarKeyFrameAnimation();
                opac2.InsertKeyFrame(0.3f, 0.3f);
                opac2.InsertKeyFrame(1f, 0f);
                opac2.Duration = TimeSpan.FromMilliseconds(700);
                g2Visual.StartAnimation("Opacity", opac2);
            };

            btn.PointerExited += (s, e) => {
                var reset = compositor.CreateSpringVector3Animation();
                reset.FinalValue = new Vector3(1f, 1f, 1f);
                reset.DampingRatio = 0.6f;
                mainVisual.StartAnimation("Scale", reset);
            };
        }

        private void SetupAnticipationPulse(Button btn, FrameworkElement content)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            var visual = ElementCompositionPreview.GetElementVisual(content);

            content.SizeChanged += (s, e) => {
                visual.CenterPoint = new Vector3((float)content.ActualWidth / 2, (float)content.ActualHeight / 2, 0);
            };

            btn.PointerEntered += (s, e) => {
                var pulse = compositor.CreateVector3KeyFrameAnimation();
                // Anticipation: Squash down first
                pulse.InsertKeyFrame(0.2f, new Vector3(0.85f, 0.85f, 1f));
                // Stretch up
                pulse.InsertKeyFrame(0.6f, new Vector3(1.25f, 1.25f, 1f));
                // Settle
                pulse.InsertKeyFrame(1f, new Vector3(1.15f, 1.15f, 1f));
                pulse.Duration = TimeSpan.FromMilliseconds(500);
                visual.StartAnimation("Scale", pulse);
            };

            btn.PointerExited += (s, e) => {
                var reset = compositor.CreateSpringVector3Animation();
                reset.FinalValue = new Vector3(1f, 1f, 1f);
                reset.DampingRatio = 0.5f;
                reset.Period = TimeSpan.FromMilliseconds(40);
                visual.StartAnimation("Scale", reset);
            };
        }

        private void SetupSegmentedWaveEffect(Button btn, FrameworkElement wave1, FrameworkElement wave2)
        {
            if (btn == null || wave1 == null || wave2 == null) return;
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            var w1Visual = ElementCompositionPreview.GetElementVisual(wave1);
            var w2Visual = ElementCompositionPreview.GetElementVisual(wave2);

            wave1.SizeChanged += (s, e) => w1Visual.CenterPoint = new Vector3((float)wave1.ActualWidth / 2, (float)wave1.ActualHeight / 2, 0);
            wave2.SizeChanged += (s, e) => w2Visual.CenterPoint = new Vector3((float)wave2.ActualWidth / 2, (float)wave2.ActualHeight / 2, 0);

            btn.PointerEntered += (s, e) => {
                // Wave 1: Rapid outward burst
                var burst1 = compositor.CreateVector3KeyFrameAnimation();
                burst1.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
                burst1.InsertKeyFrame(1f, new Vector3(2.5f, 2.5f, 1f));
                burst1.Duration = TimeSpan.FromMilliseconds(800);
                w1Visual.StartAnimation("Scale", burst1);

                var opac1 = compositor.CreateScalarKeyFrameAnimation();
                opac1.InsertKeyFrame(0f, 0.6f);
                opac1.InsertKeyFrame(1f, 0f);
                opac1.Duration = TimeSpan.FromMilliseconds(800);
                w1Visual.StartAnimation("Opacity", opac1);

                // Wave 2: Slower secondary ripple
                var burst2 = compositor.CreateVector3KeyFrameAnimation();
                burst2.InsertKeyFrame(0.2f, new Vector3(1f, 1f, 1f));
                burst2.InsertKeyFrame(1f, new Vector3(2f, 2f, 1f));
                burst2.Duration = TimeSpan.FromMilliseconds(1200);
                w2Visual.StartAnimation("Scale", burst2);

                var opac2 = compositor.CreateScalarKeyFrameAnimation();
                opac2.InsertKeyFrame(0.2f, 0.4f);
                opac2.InsertKeyFrame(1f, 0f);
                opac2.Duration = TimeSpan.FromMilliseconds(1200);
                w2Visual.StartAnimation("Opacity", opac2);
            };
        }

        private void SetupMagneticGlow(Button btn, FrameworkElement glow)
        {
            if (btn == null || glow == null) return;
            var compositor = _compositor;
            var visual = ElementCompositionPreview.GetElementVisual(glow);

            btn.PointerEntered += (s, e) => {
                var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(1f, 0.15f);
                fadeIn.Duration = TimeSpan.FromMilliseconds(300);
                visual.StartAnimation("Opacity", fadeIn);
            };

            btn.PointerExited += (s, e) => {
                var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromMilliseconds(400);
                visual.StartAnimation("Opacity", fadeOut);
            };

            btn.PointerMoved += (s, e) => {
                var pos = e.GetCurrentPoint(btn).Position;
                var centerX = btn.ActualWidth / 2;
                var centerY = btn.ActualHeight / 2;
                var offX = (float)(pos.X - centerX) * 0.5f;
                var offY = (float)(pos.Y - centerY) * 0.5f;
                
                visual.Offset = new Vector3(offX, offY, 0);
            };
        }

        private void ApplyOrganicBreathing(FrameworkElement element)
        {
            if (element == null) return;
            var compositor = _compositor;
            var visual = ElementCompositionPreview.GetElementVisual(element);
            
            element.SizeChanged += (s, e) => {
                visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
            };

            var breath = compositor.CreateVector3KeyFrameAnimation();
            breath.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
            breath.InsertKeyFrame(0.5f, new Vector3(1.03f, 1.03f, 1f));
            breath.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
            breath.Duration = TimeSpan.FromSeconds(3 + new Random().NextDouble() * 2); // Randomized period
            breath.IterationBehavior = AnimationIterationBehavior.Forever;
            
            visual.StartAnimation("Scale", breath);
        }
        // ==========================================
        // PiP IMPLEMENTATION
        // ==========================================

        // ==========================================
        // PiP IMPLEMENTATION (Single Window - Round 15)
        // ==========================================

        private void PipButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPiPMode)
            {
                EnterPiPAsync();
            }
            else
            {
                ExitPiP();
            }
        }

        private async void EnterPiPAsync()
        {
            if (_mpvPlayer == null || _isPiPMode) return;
            Debug.WriteLine("[PiP] Entering Single-Window PiP...");
            
            try 
            {
                // 1. Save Current Window State
                var appWindow = MainWindow.Current.AppWindow;
                _savedWindowBounds = appWindow.Position.X != 0 || appWindow.Position.Y != 0 || appWindow.Size.Width != 0 ? 
                                     new Windows.Graphics.RectInt32(appWindow.Position.X, appWindow.Position.Y, appWindow.Size.Width, appWindow.Size.Height) : 
                                     new Windows.Graphics.RectInt32(100, 100, 1280, 720); // Fallback
                
                _savedIsFullScreen = _isFullScreen;
                
                if (appWindow.Presenter is OverlappedPresenter op)
                {
                    _savedPresenterState = op.State;
                }
                else
                {
                    _savedPresenterState = OverlappedPresenterState.Restored;
                }

                _isPiPMode = true;

                // 2. Hide UI Elements (Clean View)
                ControlsBorder.Visibility = Visibility.Collapsed;
                BackButton.Visibility = Visibility.Collapsed;
                VideoTitleText.Visibility = Visibility.Collapsed;
                FullScreenButton.Visibility = Visibility.Collapsed;
                InfoPillsStack.Visibility = Visibility.Collapsed;
                OsdOverlay.Visibility = Visibility.Collapsed;
                
                _statsTimer.Stop();
                _cursorTimer.Stop();

                // Show PiP Controls Overlay
                PipOverlay.Visibility = Visibility.Visible;
                PipControls.Opacity = 0; // Hidden until hover

                // Sync Icons
                bool isPaused = await _mpvPlayer.GetPropertyBoolAsync("pause");
                PipPlayPauseIcon.Glyph = isPaused ? "\uF5B0" : "\uF8AE";
                
                string muteState = await _mpvPlayer.GetPropertyAsync("mute");
                PipMuteIcon.Glyph = (muteState == "yes") ? "\uE767" : "\uE74F";

                // 3. Prepare for Transition
                // Ensure we are in Overlapped mode (not Fullscreen) so we can resize
                if (_isFullScreen)
                {
                    MainWindow.Current.SetFullScreen(false);
                    _isFullScreen = false;
                    UpdateFullScreenUI();
                }
                // Also ensure we aren't Maximized, otherwise MoveAndResize might be ignored or wonky
                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    if (presenter.State == OverlappedPresenterState.Maximized)
                    {
                        presenter.Restore();
                    }
                    
                    // Make Always on Top
                    presenter.IsAlwaysOnTop = true;
                }

                // 4. Calculate Target Bounds (Bottom Right)
                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                int targetW = displayArea.WorkArea.Width / 4;
                if (targetW < 320) targetW = 320; // Min width
                int targetH = (int)(targetW * 9.0 / 16.0);
                
                int targetX = displayArea.WorkArea.X + displayArea.WorkArea.Width - targetW - 20;
                int targetY = displayArea.WorkArea.Y + displayArea.WorkArea.Height - targetH - 20;

                // 5. Animate
                // [PERF] Suspend Resize to just stretch the texture during animation
                _mpvPlayer.SuspendResize();

                await Task.Run(async () => {
                    const int steps = 25; // Faster than 40, since we are moving the heavy window
                    
                    int startX = appWindow.Position.X;
                    int startY = appWindow.Position.Y;
                    int startW = appWindow.Size.Width;
                    int startH = appWindow.Size.Height;

                    for (int i = 0; i <= steps; i++)
                    {
                        float t = (float)i / steps;
                        float ease = 1 - MathF.Pow(1 - t, 4); // EaseOutQuartic
                        
                        int curX = (int)(startX + (targetX - startX) * ease);
                        int curY = (int)(startY + (targetY - startY) * ease);
                        int curW = (int)(startW + (targetW - startW) * ease);
                        int curH = (int)(startH + (targetH - startH) * ease);
                        
                        this.DispatcherQueue.TryEnqueue(() => {
                            if (_isPiPMode) // Guard
                                appWindow.MoveAndResize(new Windows.Graphics.RectInt32(curX, curY, curW, curH));
                        });
                        
                        await Task.Delay(10);
                    }
                });

                // 6. Finish
                _mpvPlayer.ResumeResize();

                // Swap TitleBar to allow native dragging anywhere in PiP
                MainWindow.Current.SetTitleBar(PipDragRegion);

                // Hide System Caption Buttons (Min/Max/Close) by disabling system title bar
                if (appWindow.Presenter is OverlappedPresenter pipPresenter)
                {
                    pipPresenter.SetBorderAndTitleBar(true, false);
                }

                Debug.WriteLine("[PiP] Single-Window Transition Complete");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PiP] Enter Error: {ex.Message}");
                ExitPiP();
            }
        }

        private async void ExitPiP()
        {
            if (!_isPiPMode) return;
            Debug.WriteLine("[PiP] Exiting Single-Window PiP...");

            try
            {
                var appWindow = MainWindow.Current.AppWindow;

                // 1. Animate Back to Saved Bounds
                _mpvPlayer.ResumeResize(); // Resume early so it sharpen as it grows

                int targetX = _savedWindowBounds.X;
                int targetY = _savedWindowBounds.Y;
                int targetW = _savedWindowBounds.Width;
                int targetH = _savedWindowBounds.Height;
                
                await Task.Run(async () => {
                    const int steps = 20;
                    
                    int startX = appWindow.Position.X;
                    int startY = appWindow.Position.Y;
                    int startW = appWindow.Size.Width;
                    int startH = appWindow.Size.Height;

                    for (int i = 0; i <= steps; i++)
                    {
                        float t = (float)i / steps;
                        float ease = 1 - MathF.Pow(1 - t, 4);
                        
                        int curX = (int)(startX + (targetX - startX) * ease);
                        int curY = (int)(startY + (targetY - startY) * ease);
                        int curW = (int)(startW + (targetW - startW) * ease);
                        int curH = (int)(startH + (targetH - startH) * ease);
                        
                        this.DispatcherQueue.TryEnqueue(() => {
                            // Only restore if we are still meant to be restoring
                            // (Race condition guard not strictly needed but good practice)
                            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(curX, curY, curW, curH));
                        });
                        
                        await Task.Delay(10);
                    }
                });

                // 2. Restore Window State
                _isPiPMode = false;

                // Restore Main TitleBar
                MainWindow.Current.SetTitleBar(MainWindow.Current.TitleBarElement);

                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    // Restore System Caption Buttons
                    presenter.SetBorderAndTitleBar(true, true);

                    presenter.IsAlwaysOnTop = false;
                    
                    if (_savedPresenterState == OverlappedPresenterState.Maximized)
                    {
                        presenter.Maximize();
                    }
                    else if (_savedPresenterState == OverlappedPresenterState.Minimized)
                    {
                        presenter.Restore(); // Should happen automatically but forcing it
                    }
                }

                if (_savedIsFullScreen)
                {
                    MainWindow.Current.SetFullScreen(true);
                    _isFullScreen = true;
                    UpdateFullScreenUI();
                }
                else
                {
                    _isFullScreen = false;
                    UpdateFullScreenUI();
                }

                // 3. Show UI
                ControlsBorder.Visibility = Visibility.Visible;
                BackButton.Visibility = Visibility.Visible;
                VideoTitleText.Visibility = Visibility.Visible;
                FullScreenButton.Visibility = Visibility.Visible;
                
                if (InfoPillsStack != null) InfoPillsStack.Visibility = Visibility.Visible;
                
                _statsTimer.Start();
                StartCursorTimer();

                // Hide PiP Controls Overlay
                PipOverlay.Visibility = Visibility.Collapsed;
                
                Debug.WriteLine("[PiP] Restored Normal View");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PiP] Exit Error: {ex.Message}");
                // Force state reset
                _isPiPMode = false;
                ControlsBorder.Visibility = Visibility.Visible;
                MainWindow.Current.Activate();
            }
        }



        private async void TogglePiPAsync()
        {
            PipButton_Click(null, null);
        }



        private void PipOverlay_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            PipControls.Opacity = 1;
            // Window.Current.CoreWindow.PointerCursor = new Windows.UI.Core.CoreCursor(Windows.UI.Core.CoreCursorType.Arrow, 0);
        }

        private void PipOverlay_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            PipControls.Opacity = 0;
            // Optionally hide cursor again if desired, but user might be moving mouse out to other window
        }
    }
}
